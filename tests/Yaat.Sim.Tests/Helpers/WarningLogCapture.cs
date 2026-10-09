using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// Captures the Warning-level and higher lines <see cref="SimLog"/> emits while it is installed as the test's log factory,
/// so a test can assert what was warned about. Lower levels are dropped.
/// </summary>
internal sealed class WarningLogCapture : ILoggerProvider, ILogger
{
    /// <summary>The words of the navigator's warning when a restored playback does not belong to the segment set up.</summary>
    internal const string PlaybackDropped = "restored playback dropped";

    private readonly List<(string Message, Exception? Exception)> _entries = [];

    /// <summary>The captured lines, in the order they were logged.</summary>
    public IReadOnlyList<string> Warnings => [.. Entries.Select(entry => entry.Message)];

    /// <summary>The captured lines with the exception each was logged with, in the order they were logged.</summary>
    public IReadOnlyList<(string Message, Exception? Exception)> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>Installs a fresh capture as the SimLog factory for the current async flow and returns it.</summary>
    public static WarningLogCapture Install()
    {
        var capture = new WarningLogCapture();
        SimLog.InitializeForTest(LoggerFactory.Create(builder => builder.AddProvider(capture).SetMinimumLevel(LogLevel.Warning)));
        return capture;
    }

    ILogger ILoggerProvider.CreateLogger(string categoryName) => this;

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    void ILogger.Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (IsEnabled(logLevel))
        {
            (string Message, Exception? Exception) entry = (formatter(state, exception), exception);
            lock (_entries)
            {
                _entries.Add(entry);
            }
        }
    }

    public void Dispose() { }
}
