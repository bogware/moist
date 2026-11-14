using Microsoft.Extensions.Logging;

namespace Moist.Core.Diagnostics;

/// <summary>
/// Appends every log line to the session log file, so what the app logs is on disk and not
/// just in a console nobody is watching (the GUI has none). Deliberately simple: synchronous,
/// lock-serialised appends — each line is durable the moment it's written, which is what you
/// want from the log you read after a crash. Best-effort: a logging failure never takes the
/// app down. Pair it with the console provider; it doesn't replace it.
/// </summary>
public sealed class FileLoggerProvider(string path, LogLevel minimum) : ILoggerProvider
{
    private readonly object _gate = new();
    // A nested class can't read the outer primary-constructor parameter, so keep it in a field.
    private readonly LogLevel _minimum = minimum;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose() { }

    private void Append(string line)
    {
        lock (_gate)
        {
            try { File.AppendAllText(path, line + Environment.NewLine); }
            catch { /* disk full / locked / gone — never throw from a logger */ }
        }
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= owner._minimum;

        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {Abbrev(level)}: {category} {formatter(state, exception)}";
            if (exception is not null) line += Environment.NewLine + exception;
            owner.Append(line);
        }

        // Same abbreviations the console formatter uses, so the two logs read alike.
        private static string Abbrev(LogLevel level) => level switch
        {
            LogLevel.Trace => "trce", LogLevel.Debug => "dbug", LogLevel.Information => "info",
            LogLevel.Warning => "warn", LogLevel.Error => "fail", LogLevel.Critical => "crit", _ => "none",
        };
    }
}
