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

    private readonly List<string> _warnings = [];

    /// <summary>The captured lines, in the order they were logged.</summary>
    public IReadOnlyList<string> Warnings
    {
        get
        {
            lock (_warnings)
            {
                return [.. _warnings];
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

    public ILogger CreateLogger(string categoryName) => this;

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (IsEnabled(logLevel))
        {
            lock (_warnings)
            {
                _warnings.Add(formatter(state, exception));
            }
        }
    }

    public void Dispose() { }
}
