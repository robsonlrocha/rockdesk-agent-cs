using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RockDeskAgent;
using RockDeskAgent.Config;
using RockDeskAgent.Remote;
using RockDeskAgent.Services;

AgentLogger.Init();
var logger = AgentLogger.Get<Program>();

var cmd = args.Length > 0 ? args[0].ToLower() : "";

switch (cmd)
{
    // ── Chamado pelo SCM ───────────────────────────────────────────────
    case "service":
        Host.CreateDefaultBuilder()
            .UseWindowsService(o => o.ServiceName = AgentConfig.SvcName)
            .ConfigureServices(s => s.AddHostedService<AgentService>())
            .Build().Run();
        break;

    // ── Remote worker (subprocess na sessão do usuário) ───────────────
    // args: remoteWorker <token> <relayUrl> <queueId>
    case "remoteworker":
        if (args.Length >= 4)
        {
            var cfg = AgentConfig.Load();
            var worker = new RemoteWorker(cfg, args[1], args[2], int.Parse(args[3]));
            await worker.RunAsync();
        }
        break;

    // ── Instalar serviço ──────────────────────────────────────────────
    case "install":
        InstallService();
        break;

    // ── Remover serviço ───────────────────────────────────────────────
    case "remove":
    case "uninstall":
        RemoveService();
        break;

    // ── Bandeja (lançado pelo serviço na sessão do usuário) ──────────
    case "tray":
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new RockDeskAgent.Tray.TrayApp());
        break;

    // ── Wizard de registro ────────────────────────────────────────────
    case "setup":
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new RockDeskAgent.Setup.SetupForm());
        break;

    // ── Debug console ─────────────────────────────────────────────────
    case "debug":
        logger.LogInformation("Modo debug — Ctrl+C para parar.");
        using (var cts = new CancellationTokenSource())
        {
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            var svc = new AgentService();
            await svc.StartAsync(cts.Token);
            try { await Task.Delay(Timeout.Infinite, cts.Token); } catch { }
            await svc.StopAsync(CancellationToken.None);
        }
        break;

    // ── Duplo-clique / sem args ───────────────────────────────────────
    default:
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var cfgD = AgentConfig.Load();
        if (cfgD.IsRegistered)
            Application.Run(new RockDeskAgent.Tray.TrayApp());
        else
            Application.Run(new RockDeskAgent.Setup.SetupForm());
        break;
}

// ── Helpers de instalação ─────────────────────────────────────────────────
void InstallService()
{
    var src     = Environment.ProcessPath
                  ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
    var destExe = Path.Combine(AgentConfig.ConfigDir, "RockDeskAgentCS.exe");

    Console.WriteLine($"Copiando exe para {destExe}...");
    Directory.CreateDirectory(AgentConfig.ConfigDir);
    if (!string.Equals(src, destExe, StringComparison.OrdinalIgnoreCase))
        File.Copy(src, destExe, overwrite: true);

    Console.WriteLine($"Instalando serviço '{AgentConfig.SvcName}'...");
    RunSc($"stop {AgentConfig.SvcName}");
    RunSc($"delete {AgentConfig.SvcName}");
    Thread.Sleep(1500);

    var r = RunSc($"create {AgentConfig.SvcName} " +
                  $"binPath= \"\\\"{destExe}\\\" service\" " +
                  $"DisplayName= \"{AgentConfig.SvcDisplay}\" " +
                  $"start= auto obj= LocalSystem");

    if (r == 0)
    {
        RunSc($"description {AgentConfig.SvcName} \"Agente de suporte remoto RockDesk (C#)\"");
        RunSc($"start {AgentConfig.SvcName}");
        Console.WriteLine($"OK — serviço instalado em:\n  {destExe}");
        logger.LogInformation("Serviço instalado: {P}", destExe);
    }
    else Console.WriteLine($"Erro ao instalar serviço (código {r}).");
}

void RemoveService()
{
    RunSc($"stop {AgentConfig.SvcName}");
    Thread.Sleep(2000);
    RunSc($"delete {AgentConfig.SvcName}");
    Console.WriteLine("Serviço removido.");
}

int RunSc(string args_)
{
    var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
    {
        FileName = "sc.exe", Arguments = args_,
        RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
    });
    p?.WaitForExit(10_000);
    return p?.ExitCode ?? -1;
}
