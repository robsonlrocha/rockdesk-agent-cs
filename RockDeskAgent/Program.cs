using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RockDeskAgent;
using RockDeskAgent.Config;
using RockDeskAgent.Services;
using System.ServiceProcess;

// ── Inicialização do logger ────────────────────────────────────────────────
AgentLogger.Init();
var logger = AgentLogger.Get<Program>();

var cmd = args.Length > 0 ? args[0].ToLower() : "";

// ── Dispatcher de modo de execução ────────────────────────────────────────
switch (cmd)
{
    // Chamado pelo SCM (Windows Service Control Manager)
    case "service":
        RunAsWindowsService();
        break;

    // Instala o serviço Windows
    case "install":
        InstallService();
        break;

    // Remove o serviço Windows
    case "remove":
    case "uninstall":
        RemoveService();
        break;

    // Modo setup: exibe wizard de registro
    case "setup":
        RunSetup();
        break;

    // Modo debug: roda em console sem ser serviço
    case "debug":
        await RunDebugAsync();
        break;

    // Duplo clique: se registrado → bandeja; senão → setup
    default:
        var cfg = AgentConfig.Load();
        if (cfg.IsRegistered)
            RunTray();
        else
            RunSetup();
        break;
}

// ── Implementações ─────────────────────────────────────────────────────────

void RunAsWindowsService()
{
    logger.LogInformation("Iniciando como Windows Service.");
    Host.CreateDefaultBuilder()
        .UseWindowsService(o => o.ServiceName = AgentConfig.SvcName)
        .ConfigureServices(s => s.AddHostedService<AgentService>())
        .Build()
        .Run();
}

void InstallService()
{
    var exe = Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
    logger.LogInformation("Instalando serviço '{Svc}'...", AgentConfig.SvcName);

    // Remove se já existir
    RunSc($"stop {AgentConfig.SvcName}");
    RunSc($"delete {AgentConfig.SvcName}");
    System.Threading.Thread.Sleep(1000);

    // Cria serviço
    var r = RunSc($"create {AgentConfig.SvcName} " +
                  $"binPath= \"\\\"{exe}\\\" service\" " +
                  $"DisplayName= \"{AgentConfig.SvcDisplay}\" " +
                  $"start= auto " +
                  $"obj= LocalSystem");

    if (r == 0)
    {
        RunSc($"description {AgentConfig.SvcName} \"Agente de monitoramento e suporte remoto RockDesk\"");
        RunSc($"start {AgentConfig.SvcName}");
        Console.WriteLine("Serviço instalado e iniciado com sucesso.");
        logger.LogInformation("Serviço instalado.");
    }
    else
    {
        Console.WriteLine($"Falha ao instalar serviço (código {r}).");
    }
}

void RemoveService()
{
    logger.LogInformation("Removendo serviço '{Svc}'.", AgentConfig.SvcName);
    RunSc($"stop {AgentConfig.SvcName}");
    System.Threading.Thread.Sleep(2000);
    RunSc($"delete {AgentConfig.SvcName}");
    Console.WriteLine("Serviço removido.");
}

int RunSc(string args)
{
    var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
    {
        FileName = "sc.exe", Arguments = args,
        RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
    });
    p?.WaitForExit(10_000);
    return p?.ExitCode ?? -1;
}

void RunSetup()
{
    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);
    Application.Run(new Setup.SetupForm());
}

void RunTray()
{
    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);
    Application.Run(new Tray.TrayApp());
}

async Task RunDebugAsync()
{
    logger.LogInformation("Modo debug — Ctrl+C para parar.");
    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    var svc = new AgentService();
    await svc.StartAsync(cts.Token);
    await Task.Delay(Timeout.Infinite, cts.Token).ContinueWith(_ => { });
    await svc.StopAsync(CancellationToken.None);
}
