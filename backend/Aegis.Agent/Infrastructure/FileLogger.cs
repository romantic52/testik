using System.Collections.Concurrent;
using System.Text;

namespace Aegis.Agent.Infrastructure;

/// <summary>
/// Daily rolling text log in &lt;DataDirectory&gt;\logs\aegis-YYYYMMDD.log, kept for 14 days.
/// Works the same in console and Windows Service mode.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const int KeepDays = 14;
    private readonly object _sync = new();
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private StreamWriter? _writer;
    private DateOnly _day;

    public FileLoggerProvider(string directory)
    {
        Directory = directory;
        System.IO.Directory.CreateDirectory(directory);
        Prune();
    }

    public string Directory { get; }

    public string CurrentFile => Path.Combine(Directory, $"aegis-{DateTime.Now:yyyyMMdd}.log");

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, name => new FileLogger(this, name));

    internal void Write(string category, LogLevel level, string message, Exception? error)
    {
        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ')
            .Append(level switch
            {
                LogLevel.Critical => "CRIT", LogLevel.Error => "ERR ", LogLevel.Warning => "WARN",
                LogLevel.Information => "INFO", LogLevel.Debug => "DBG ", _ => "TRC "
            })
            .Append(' ').Append(category).Append(": ").Append(message.ReplaceLineEndings(" "));
        if (error is not null) line.Append(" | ").Append(error.GetType().Name).Append(": ").Append(error.Message.ReplaceLineEndings(" "));

        lock (_sync)
        {
            try
            {
                var today = DateOnly.FromDateTime(DateTime.Now);
                if (_writer is null || today != _day)
                {
                    _writer?.Dispose();
                    _writer = new StreamWriter(new FileStream(CurrentFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false)) { AutoFlush = true };
                    _day = today;
                    Prune();
                }
                _writer.WriteLine(line.ToString());
            }
            catch
            {
                // Logging must never take the agent down (disk full, permissions).
            }
        }
    }

    public IReadOnlyList<string> Tail(int lines)
    {
        lines = Math.Clamp(lines, 10, 5000);
        var files = new DirectoryInfo(Directory).GetFiles("aegis-*.log").OrderByDescending(f => f.Name).Take(2).ToList();
        var result = new List<string>();
        foreach (var file in files)
        {
            try
            {
                using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var all = new List<string>();
                while (reader.ReadLine() is { } l) all.Add(l);
                result.InsertRange(0, all);
            }
            catch { }
            if (result.Count >= lines) break;
        }
        return result.Skip(Math.Max(0, result.Count - lines)).ToList();
    }

    private void Prune()
    {
        try
        {
            foreach (var file in new DirectoryInfo(Directory).GetFiles("aegis-*.log"))
                if (file.LastWriteTime < DateTime.Now.AddDays(-KeepDays)) file.Delete();
        }
        catch { }
    }

    public void Dispose()
    {
        lock (_sync) { _writer?.Dispose(); _writer = null; }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            if (logLevel < LogLevel.Warning && category.StartsWith("Microsoft.", StringComparison.Ordinal)
                && !category.StartsWith("Microsoft.Hosting.Lifetime", StringComparison.Ordinal)) return;
            provider.Write(category, logLevel, formatter(state, exception), exception);
        }
    }
}
