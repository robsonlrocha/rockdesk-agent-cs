using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RockDeskAgent;
using RockDeskAgent.Config;
using RockDeskAgent.Services;

// ── Inicialização do logger ────────────────────────────────────────────────
AgentLogger.Init();
var logger = AgentLogger.Get<Program>();

var cmd = args.Length > 0 ? args[0].ToLower() : "";

// ── Dispatcher de modo de execução ────────────────────────────────────────
switch (cmd)
{
    case "service":
        RunAsWindowsService();
        break;

    case "install":
        InstallService();
        break;

    case "remove":
    case "uninstall":
        RemoveService();
        break;

    case "setup":
        RunSetup();
        break;

    case "debug":
        await RunDebugAsync();
        break;

    default:
        var cfgD = AgentConfig.Load();
        if (cfgD.IsRegistered)
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
        .ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Information))
        .Build()
        .Run();
}

void InstallService()
{
    var exe = Environment.ProcessPath
              ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
    Console.WriteLine($"Instalando serviço '{AgentConfig.SvcName}'...");

    RunSc($"stop {AgentConfig.SvcName}");
    RunSc($"delete {AgentConfig.SvcName}");
    Thread.Sleep(1500);

    var r = RunSc($"create {AgentConfig.SvcName} " +
                  $"binPath= \"\\\"{exe}\\\" service\" " +
                  $"DisplayName= \"{AgentConfig.SvcDisplay}\" " +
                  $"start= auto obj= LocalSystem");

    if (r == 0)
    {
        RunSc($"description {AgentConfig.SvcName} \"Agente de monitoramento e suporte remoto RockDesk (C#)\"");
        RunSc($"start {AgentConfig.SvcName}");
        Console.WriteLine("Serviço instalado e iniciado.");
        logger.LogInformation("Serviço instalado com sucesso.");
    }
    else
    {
        Console.WriteLine($"Falha ao instalar serviço (código {r}).");
    }
}

void RemoveService()
{
    Console.WriteLine($"Removendo serviço '{AgentConfig.SvcName}'...");
    RunSc($"stop {AgentConfig.SvcName}");
    Thread.Sleep(2000);
    RunSc($"delete {AgentConfig.SvcName}");
    Console.WriteLine("Serviço removido.");
}

int RunSc(string arguments)
{
    var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
    {
        FileName               = "sc.exe",
        Arguments              = arguments,
        RedirectStandardOutput = true,
        UseShellExecute        = false,
        CreateNoWindow         = true
    });
    p?.WaitForExit(10_000);
    return p?.ExitCode ?? -1;
}

void RunSetup()
{
    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);
    Application.Run(new RockDeskAgent.Setup.SetupForm());
}

void RunTray()
{
    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);
    Application.Run(new RockDeskAgent.Tray.TrayApp());
}

async Task RunDebugAsync()
{
    logger.LogInformation("Modo debug — Ctrl+C para parar.");
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    var host = Host.CreateDefaultBuilder()
        .ConfigureServices(s => s.AddHostedService<AgentService>())
        .Build();

    await host.RunAsync(cts.Token);
}
