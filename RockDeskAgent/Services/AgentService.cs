using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using RockDeskAgent.Api;
using RockDeskAgent.Config;
using RockDeskAgent.Core;

namespace RockDeskAgent.Services;

// P/Invoke para SendSAS — usado pelo sas-watcher do serviço SCM
internal static class SasDll
{
    [DllImport("sas.dll", EntryPoint = "SendSAS")]
    public static extern void SendSAS([MarshalAs(UnmanagedType.Bool)] bool fKeyboardInitiated);
}

/// <summary>Serviço Windows principal: heartbeat, polling, remote session.</summary>
public class AgentService : BackgroundService
{
    private static readonly ILogger Logger = AgentLogger.Get<AgentService>();

    private readonly AgentConfig _cfg;
    private readonly PortalClient _api;
    private readonly SemaphoreSlim _workerLock = new(1, 1);

    const int HeartbeatSec   = 300;
    const int CmdCheckSec    = 30;
    const int RemoteWatchSec = 2;
    const int DataIntervalSec= 3600;

    public AgentService() : this(null) { }
    public AgentService(ILogger<AgentService>? _)
    {
        _cfg = AgentConfig.Load();
        _api = new PortalClient(_cfg);
    }

    // Arquivo de trigger para SendSAS cross-session (mesmo mecanismo do Python)
    private static readonly string SasTrigger =
        Path.Combine(AgentConfig.ConfigDir, "sas.trigger");

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        Logger.LogInformation("RockDesk Agent CS v{V} iniciado (SCM service).",
            AgentConfig.AgentVersion);
        EnableSasGeneration();

        // Coleta inicial de hardware em background
        _ = Task.Run(() => SendFullDataAsync(ct), ct);

        // Lança tray icon na sessão do usuário após 5s (como Python)
        _ = Task.Run(async () =>
        {
            await Task.Delay(5_000, ct);
            var trayExe = Path.Combine(AgentConfig.ConfigDir, "RockDeskAgentCS.exe");
            if (!File.Exists(trayExe))
                trayExe = Environment.ProcessPath ?? trayExe;
            SessionHelper.LaunchInUserSession($"\"{trayExe}\" tray");
        }, ct);

        await Task.WhenAll(
            HeartbeatLoopAsync(ct),
            RemoteWatchLoopAsync(ct),
            CommandPollLoopAsync(ct),
            SasWatcherAsync(ct)
        );
    }

    // ── SAS watcher: detecta arquivo trigger e chama SendSAS ─────────
    // O subprocess remoteWorker cria sas.trigger; o serviço (SCM) chama SendSAS
    private async Task SasWatcherAsync(CancellationToken ct)
    {
        Logger.LogInformation("SAS watcher iniciado (monitorando {F}).", SasTrigger);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(SasTrigger))
                {
                    try { File.Delete(SasTrigger); } catch { }
                    try
                    {
                        SasDll.SendSAS(true); // TRUE = keyboard-initiated
                        Logger.LogInformation("SendSAS(TRUE) executado pelo serviço SCM.");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning("SendSAS falhou: {E}", ex.Message);
                    }
                }
            }
            catch { }
            await Task.Delay(500, ct);
        }
    }

    // ── Heartbeat ────────────────────────────────────────────────────
    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        await Task.Delay(5_000, ct);  // aguarda 5s antes do primeiro heartbeat
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _api.HeartbeatAsync(Environment.MachineName, GetPrivateIp(), "", ct);
                Logger.LogInformation("Heartbeat enviado.");
            }
            catch (Exception ex) { Logger.LogDebug("Heartbeat: {E}", ex.Message); }
            await Task.Delay(TimeSpan.FromSeconds(HeartbeatSec), ct);
        }
    }

    // ── Remote session watcher — 2 s ─────────────────────────────────
    private async Task RemoteWatchLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var r = await _api.CheckRemotePendingAsync(ct);
                if (r?["has_pending"]?.GetValue<bool>() == true)
                    _ = LaunchRemoteWorkerAsync(ct);
            }
            catch (Exception ex) { Logger.LogDebug("RemoteWatch: {E}", ex.Message); }
            await Task.Delay(TimeSpan.FromSeconds(RemoteWatchSec), ct);
        }
    }

    private async Task LaunchRemoteWorkerAsync(CancellationToken ct)
    {
        if (!await _workerLock.WaitAsync(0)) return; // apenas 1 worker por vez
        try
        {
            var r = await _api.GetRemoteSessionQueueAsync(ct);
            var job = r?["job"];
            if (job == null) { return; }

            var jid   = job["id"]?.GetValue<int>()      ?? 0;
            var token = job["token"]?.GetValue<string>() ?? "";
            var relay = job["relay"]?.GetValue<string>() ?? "";
            if (jid == 0 || string.IsNullOrEmpty(token)) return;

            // Exe instalado em ProgramData
            var exe = Path.Combine(AgentConfig.ConfigDir, "RockDeskAgentCS.exe");
            if (!File.Exists(exe))
            {
                exe = Environment.ProcessPath ?? exe;
                Logger.LogWarning("Exe não encontrado em {Path}, usando {Fallback}",
                    Path.Combine(AgentConfig.ConfigDir, "RockDeskAgentCS.exe"), exe);
            }

            var cmd = $"\"{exe}\" remoteWorker \"{token}\" \"{relay}\" \"{jid}\"";
            Logger.LogInformation("Lançando remote worker (job {J}) na sessão do usuário.", jid);

            // Lança como subprocess na sessão interativa do usuário (como o Python faz)
            bool launched = SessionHelper.LaunchInUserSession(cmd);
            if (!launched)
            {
                Logger.LogWarning("Falha ao lançar na sessão do usuário — sem sessão ativa?");
                await _api.UpdateRemoteSessionStatusAsync(jid, "error",
                    "LaunchInUserSession falhou — sessão não encontrada", ct);
            }
            // O worker se encerra sozinho e atualiza o status via API
        }
        catch (Exception ex)
        {
            Logger.LogWarning("LaunchRemoteWorker: {E}", ex.Message);
        }
        finally
        {
            // Aguarda 30s antes de liberar o lock (tempo para o worker conectar)
            _ = Task.Run(async () => {
                await Task.Delay(30_000);
                _workerLock.Release();
            });
        }
    }

    // ── Command poll ─────────────────────────────────────────────────
    private async Task CommandPollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var r = await _api.CheckCommandsAsync(ct);
                // TODO: processar filas de uninstall, passwd, etc.
            }
            catch (Exception ex) { Logger.LogDebug("CommandPoll: {E}", ex.Message); }
            await Task.Delay(TimeSpan.FromSeconds(CmdCheckSec), ct);
        }
    }

    // ── Hardware/software ────────────────────────────────────────────
    internal async Task SendFullDataAsync(CancellationToken ct)
    {
        try
        {
            Logger.LogInformation("Iniciando coleta de hardware...");
            var hw = HardwareInventory.Collect(_cfg);
            var r  = await _api.SendFullDataAsync(hw, ct);
            var ok = r?["success"]?.GetValue<bool>() ?? false;
            Logger.LogInformation("Hardware enviado: success={Ok}", ok);
        }
        catch (Exception ex) { Logger.LogWarning("SendFullData: {E}", ex.Message); }
    }

    // ── Helpers ──────────────────────────────────────────────────────
    private static string GetPrivateIp()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(ua.Address))
                    return ua.Address.ToString();
        }
        return "";
    }

    private static void EnableSasGeneration()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", true);
            k?.SetValue("SasGeneration", 1, Microsoft.Win32.RegistryValueKind.DWord);
        }
        catch { }
    }
}
