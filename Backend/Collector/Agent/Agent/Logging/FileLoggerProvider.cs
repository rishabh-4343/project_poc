using System.Collections.Concurrent;
using System.Text;

namespace Auditor.Agent.Logging;

// Appends to <directory>/agent-yyyyMMdd.log (UTC day) and deletes files older than the retention window.
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly int _retentionDays;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private DateOnly _lastPruned;

    public FileLoggerProvider(string directory, int retentionDays)
    {
        _directory = directory;
        _retentionDays = Math.Max(1, retentionDays);
        Directory.CreateDirectory(_directory);
    }

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, c => new FileLogger(this, c));

    public void Dispose()
    {
    }

    private void Write(string line)
    {
        var now = DateTimeOffset.UtcNow;
        var path = Path.Combine(_directory, $"agent-{now:yyyyMMdd}.log");

        lock (_gate)
        {
            try
            {
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
                Prune(DateOnly.FromDateTime(now.UtcDateTime));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never take the agent down.
            }
        }
    }

    private void Prune(DateOnly today)
    {
        if (_lastPruned == today)
        {
            return;
        }

        _lastPruned = today;
        var cutoff = DateTime.UtcNow.AddDays(-_retentionDays);
        foreach (var file in Directory.EnumerateFiles(_directory, "agent-*.log"))
        {
            if (File.GetLastWriteTimeUtc(file) < cutoff)
            {
                File.Delete(file);
            }
        }
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var line = $"{DateTimeOffset.UtcNow:O} [{logLevel,-11}] {category}: {formatter(state, exception)}";
            owner.Write(exception is null ? line : $"{line}{Environment.NewLine}{exception}");
        }
    }
}
