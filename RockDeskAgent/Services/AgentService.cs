using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using RockDeskAgent.Api;
using RockDeskAgent.Config;
using RockDeskAgent.Core;
using RockDeskAgent.Remote;

namespace RockDeskAgent.Services;

/// <summary>Windows Service principal — gerencia heartbeat, polling e sessões remotas.</summary>
public class AgentService : BackgroundService
{
    private static readonly ILogger Logger = AgentLogger.Get<AgentService>();

    private readonly AgentConfig _cfg;
    private readonly PortalClient _api;
    private readonly SemaphoreSlim _workerLock = new(1, 1);

    // Intervalos (segundos)
    const int HeartbeatInt    = 300;
    const int CmdCheckInt     = 30;
    const int RemoteWatchInt  = 2;
    const int DataInt         = 3600;

    public AgentService(ILogger<AgentService> log)
    {
        _cfg = AgentConfig.Load();
        _api = new PortalClient(_cfg);
    }

    // Construtor sem parâmetros para compatibilidade com modo debug
    public AgentService() : this(AgentLogger.Get<AgentService>()) { }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        Logger.LogInformation("RockDesk Agent CS v{Ver} iniciado (serviço SCM).",
                              AgentConfig.AgentVersion);

        // Habilita SendSAS via registro (backup para configs legadas)
        EnableSasGeneration();

        // Coleta inicial de hardware
        await SendFullDataAsync(ct);

        // Threads paralelas: heartbeat + remote watcher
        var tasks = new[]
        {
            HeartbeatLoopAsync(ct),
            CommandPollLoopAsync(ct),
            RemoteWatchLoopAsync(ct),
        };
        await Task.WhenAll(tasks);

        Logger.LogInformation("AgentService encerrado.");
    }

    // ── Heartbeat a cada 5 min ─────────────────────────────────────────
    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _api.HeartbeatAsync(
                    Environment.MachineName, GetPrivateIp(), "", ct);
                Logger.LogInformation("Heartbeat enviado.");
            }
            catch (Exception ex) { Logger.LogWarning("Heartbeat erro: {E}", ex.Message); }

            await Task.Delay(TimeSpan.FromSeconds(HeartbeatInt), ct);
        }
    }

    // ── Polling de comandos a cada 30s ─────────────────────────────────
    private async Task CommandPollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var r = await _api.CheckCommandsAsync(ct);
                if (r?["has_pending"]?.GetValue<bool>() == true)
                    Logger.LogInformation("Comandos pendentes detectados.");
                // TODO: processar filas de comandos (senha, usuário, screenshot, etc.)
            }
            catch (Exception ex) { Logger.LogDebug("CommandPoll erro: {E}", ex.Message); }

            await Task.Delay(TimeSpan.FromSeconds(CmdCheckInt), ct);
        }
    }

    // ── Remote session watcher a cada 2s ──────────────────────────────
    private async Task RemoteWatchLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var r = await _api.CheckRemotePendingAsync(ct);
                if (r?["has_pending"]?.GetValue<bool>() == true)
                {
                    Logger.LogInformation("Sessão remota pendente — iniciando worker.");
                    _ = LaunchRemoteWorkerAsync(ct);
                }
            }
            catch (Exception ex) { Logger.LogDebug("RemoteWatch erro: {E}", ex.Message); }

            await Task.Delay(TimeSpan.FromSeconds(RemoteWatchInt), ct);
        }
    }

    private async Task LaunchRemoteWorkerAsync(CancellationToken ct)
    {
        // Apenas 1 worker por vez
        if (!await _workerLock.WaitAsync(0)) return;
        try
        {
            var r = await _api.GetRemoteSessionQueueAsync(ct);
            var job = r?["job"];
            if (job == null) return;

            var jid   = job["id"]?.GetValue<int>()     ?? 0;
            var token = job["token"]?.GetValue<string>() ?? "";
            var relay = job["relay"]?.GetValue<string>() ?? "";
            if (jid == 0 || string.IsNullOrEmpty(token)) return;

            Logger.LogInformation("Iniciando RemoteWorker (job {Jid}).", jid);
            var worker = new RemoteWorker(_cfg, token, relay, jid);
            await worker.RunAsync(ct);

            await _api.UpdateRemoteSessionStatusAsync(jid, "ended", null, ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning("RemoteWorker erro: {E}", ex.Message);
        }
        finally
        {
            _workerLock.Release();
        }
    }

    // ── Coleta completa de hardware ────────────────────────────────────
    private async Task SendFullDataAsync(CancellationToken ct)
    {
        try
        {
            Logger.LogInformation("Coletando hardware/software...");
            var hw = HardwareInventory.Collect(_cfg);
            var r  = await _api.SendFullDataAsync(hw, ct);
            Logger.LogInformation("Hardware enviado: {Ok}", r?["success"]?.GetValue<bool>());
        }
        catch (Exception ex) { Logger.LogWarning("SendFullData erro: {E}", ex.Message); }
    }

    private static string GetPrivateIp()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(ua.Address))
                    return ua.Address.ToString();
            }
        }
        return "";
    }

    private static void EnableSasGeneration()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", true);
            key?.SetValue("SasGeneration", 1, Microsoft.Win32.RegistryValueKind.DWord);
            Logger.LogInformation("SasGeneration=1 configurado.");
        }
        catch (Exception ex) { Logger.LogWarning("SasGeneration: {E}", ex.Message); }
    }
}
