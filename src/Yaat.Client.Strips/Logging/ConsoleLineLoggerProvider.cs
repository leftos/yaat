using Microsoft.Extensions.Logging;

namespace Yaat.Client.Logging;

/// <summary>
/// Minimal <see cref="ILoggerProvider"/> that writes a single text line per
/// log entry to <see cref="Console.Out"/> (warnings/errors to
/// <see cref="Console.Error"/>). Used by <see cref="AppLog.InitializeForBrowser"/>
/// — the .NET runtime under <c>browser-wasm</c> forwards console writes to
/// the browser DevTools console, which is where we want logs in the WASM
/// vStrips client. Format mirrors the file logger's: timestamp, short level
/// tag, category, then message + optional exception detail.
/// </summary>
internal sealed class ConsoleLineLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new ConsoleLineLogger(categoryName);

    public void Dispose() { }

    private sealed class ConsoleLineLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }
            var message = formatter(state, exception);
            var tag = logLevel switch
            {
                LogLevel.Trace => "TRC",
                LogLevel.Debug => "DBG",
                LogLevel.Information => "INF",
                LogLevel.Warning => "WRN",
                LogLevel.Error => "ERR",
                LogLevel.Critical => "CRT",
                _ => "???",
            };
            var line = $"[{DateTime.UtcNow:HH:mm:ss.fff} {tag} {category}] {message}";
            if (exception is not null)
            {
                line = line + Environment.NewLine + exception;
            }
            if (logLevel >= LogLevel.Warning)
            {
                Console.Error.WriteLine(line);
            }
            else
            {
                Console.WriteLine(line);
            }
        }
    }
}
