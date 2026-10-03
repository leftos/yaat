using Microsoft.Extensions.Logging;

namespace Yaat.Client.Logging;

/// <summary>
/// A logger provider that keeps the last <see cref="Capacity"/> entries logged at <see cref="LogLevel.Error"/> or above, each
/// numbered with a sequence that only grows, so a caller can ask for the errors logged since a point it noted. Safe to log to
/// and read from on any thread.
/// </summary>
public sealed class RecentErrorLog : ILoggerProvider
{
    /// <summary>How many entries the ring keeps; past it the oldest is dropped.</summary>
    public const int Capacity = 200;

    private readonly Lock _lock = new();
    private readonly Queue<RecentErrorEntry> _entries = new(Capacity);
    private long _lastSequence;

    /// <summary>The sequence number of the newest entry ever kept, or 0 when none has been.</summary>
    public long LastSequence
    {
        get
        {
            lock (_lock)
            {
                return _lastSequence;
            }
        }
    }

    /// <summary>
    /// The entries still in the ring whose sequence is above <paramref name="sequence"/>, oldest first, at most
    /// <paramref name="max"/>.
    /// </summary>
    public IReadOnlyList<RecentErrorEntry> Since(long sequence, int max)
    {
        lock (_lock)
        {
            return [.. _entries.Where(entry => entry.Sequence > sequence).Take(max)];
        }
    }

    public ILogger CreateLogger(string categoryName) => new RecentErrorLogger(this, categoryName);

    public void Dispose() { }

    private void Add(LogLevel level, string category, string message, Exception? exception)
    {
        lock (_lock)
        {
            if (_entries.Count == Capacity)
            {
                _entries.Dequeue();
            }

            _lastSequence++;
            _entries.Enqueue(new RecentErrorEntry(_lastSequence, level, category, message, Describe(exception)));
        }
    }

    // One line, no stack trace: the type and the message, its line breaks folded into spaces.
    private static string? Describe(Exception? exception) =>
        (exception is null) ? null : OneLine($"{exception.GetType().FullName}: {exception.Message}");

    private static string OneLine(string text) => text.ReplaceLineEndings(" ");

    private sealed class RecentErrorLogger(RecentErrorLog owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => (logLevel >= LogLevel.Error) && (logLevel != LogLevel.None);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            owner.Add(logLevel, category, OneLine(formatter(state, exception)), exception);
        }
    }
}

/// <summary>One error the client logged, as <see cref="RecentErrorLog"/> keeps it.</summary>
/// <param name="Sequence">The entry's place in the log's sequence, above every entry kept before it.</param>
/// <param name="Level">The level it was logged at: <see cref="LogLevel.Error"/> or <see cref="LogLevel.Critical"/>.</param>
/// <param name="Category">The logger category, e.g. the class that logged it.</param>
/// <param name="Message">The formatted log message.</param>
/// <param name="Exception">The exception's type and message on one line, or null when none was logged.</param>
public sealed record RecentErrorEntry(long Sequence, LogLevel Level, string Category, string Message, string? Exception);
