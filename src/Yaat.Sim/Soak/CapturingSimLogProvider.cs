using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Soak;

/// <summary>
/// Bounded ring-buffer <see cref="ILoggerProvider"/> for the soak harness. Records every entry at or above
/// <paramref name="minimumLevel"/> from any category, keeps only the newest <paramref name="capacity"/> records
/// (counting evictions in <see cref="DroppedCount"/>), and hands them out through <see cref="Drain"/> once per tick.
/// Thread-safe: the airport-layout refresh and other pool work log concurrently with the tick thread.
/// Register it on the same <see cref="ILoggerFactory"/> that <see cref="SimLog.Initialize"/> receives so Yaat.Sim
/// loggers and the host's injected loggers are both tapped.
/// </summary>
public sealed class CapturingSimLogProvider(LogLevel minimumLevel, int capacity) : ILoggerProvider
{
    private readonly int _capacity =
        capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Ring capacity must be positive");
    private readonly LogLevel _minimumLevel = minimumLevel;
    private readonly Lock _lock = new();
    private readonly Queue<CapturedLogRecord> _records = new();
    private int _dropped;

    /// <summary>Records evicted because the ring was full, since the provider was created.</summary>
    public int DroppedCount
    {
        get
        {
            lock (_lock)
            {
                return _dropped;
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    /// <summary>Returns the captured records in arrival order and clears the buffer.</summary>
    public IReadOnlyList<CapturedLogRecord> Drain()
    {
        lock (_lock)
        {
            var drained = _records.ToArray();
            _records.Clear();
            return drained;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _records.Clear();
        }
    }

    private bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= _minimumLevel;

    private void Append(CapturedLogRecord record)
    {
        lock (_lock)
        {
            if (_records.Count >= _capacity)
            {
                _records.Dequeue();
                _dropped++;
            }

            _records.Enqueue(record);
        }
    }

    private sealed class CapturingLogger(CapturingSimLogProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => owner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!owner.IsEnabled(logLevel))
            {
                return;
            }

            owner.Append(new CapturedLogRecord(logLevel, category, eventId, formatter(state, exception), exception?.ToString(), DateTime.UtcNow));
        }
    }
}
