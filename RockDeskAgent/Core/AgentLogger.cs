using Microsoft.Extensions.Logging;
using RockDeskAgent.Config;

namespace RockDeskAgent;

/// <summary>Logger simples que grava em arquivo + console.</summary>
public static class AgentLogger
{
    private static readonly object _lock = new();
    private static ILoggerFactory? _factory;

    public static void Init()
    {
        Directory.CreateDirectory(AgentConfig.ConfigDir);
        _factory = LoggerFactory.Create(b =>
        {
            b.AddSimpleConsole(o => { o.TimestampFormat = "[HH:mm:ss] "; });
            b.AddProvider(new FileLoggerProvider(AgentConfig.LogFile));
            b.SetMinimumLevel(LogLevel.Information);
        });
    }

    public static ILogger<T> Get<T>() =>
        (_factory ?? LoggerFactory.Create(_ => { })).CreateLogger<T>();

    /// <summary>Para classes static que não podem ser type-args de ILogger&lt;T&gt;.</summary>
    public static ILogger GetNamed(string category) =>
        (_factory ?? LoggerFactory.Create(_ => { })).CreateLogger(category);
}

// ── Provedor de log em arquivo ─────────────────────────────────────────────
public class FileLoggerProvider(string path) : ILoggerProvider
{
    public ILogger CreateLogger(string name) => new FileLogger(path, name);
    public void Dispose() { }
}

public class FileLogger(string path, string name) : ILogger
{
    private static readonly object _lock = new();
    public IDisposable? BeginScope<T>(T state) => null;
    public bool IsEnabled(LogLevel l) => l >= LogLevel.Information;

    public void Log<T>(LogLevel level, EventId id, T state, Exception? ex,
                        Func<T, Exception?, string> fmt)
    {
        if (!IsEnabled(level)) return;
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level.ToString()[..4].ToUpper()}] [{name.Split('.').Last()}] {fmt(state, ex)}{(ex != null ? "\n" + ex : "")}";
        lock (_lock)
        {
            try { File.AppendAllText(path, line + Environment.NewLine); }
            catch { /* não pode falhar o serviço por problema de log */ }
        }
    }
}
