using Microsoft.Extensions.Logging;

namespace Client.Diagnostics.Logging;

/// <summary>
/// Sends log entries to the <see cref="RollingLogWriter"/> (MISS-05). From the configured minimum level (default Information); framework
/// categories (Microsoft.*, System.*) from Warning only, so the file holds what the application did, not the framework's chatter.
/// </summary>
[ProviderAlias("File")]
public sealed class FileLoggerProvider(RollingLogWriter writer, DiagnosticsConfiguration options) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new FileLogger(writer, categoryName, Minimum(categoryName, options.MinimumLevel));

    internal static LogLevel Minimum(string category, LogLevel configured)
        => category.StartsWith("Microsoft.", StringComparison.Ordinal) || category.StartsWith("System.", StringComparison.Ordinal)
            ? (LogLevel)Math.Max((int)configured, (int)LogLevel.Warning)
            : configured;

    public void Dispose()
    {
        // the writer is a singleton of its own: the crash reporter still uses it while the host shuts down
    }

    private sealed class FileLogger(RollingLogWriter writer, string category, LogLevel minimum) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= minimum;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            writer.Write(Short(logLevel), category, formatter(state, exception), exception);
        }

        private static string Short(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            _ => "CRT",
        };
    }
}
