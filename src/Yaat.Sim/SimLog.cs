using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Yaat.Sim;

public static class SimLog
{
    private static ILoggerFactory? _factory;

    public static void Initialize(ILoggerFactory factory) => _factory = factory;

    public static ILogger CreateLogger<T>() => new DeferredLogger(typeof(T).Name);

    public static ILogger CreateLogger(string category) => new DeferredLogger(category);

    /// <summary>
    /// Logger that defers to whatever <see cref="ILoggerFactory"/> is current at call time.
    /// Static fields using <c>SimLog.CreateLogger</c> are initialized before tests call
    /// <see cref="Initialize"/>. A deferred logger ensures those fields pick up the
    /// test-configured factory when it's set, rather than capturing a null factory at
    /// class-load time.
    /// </summary>
    private sealed class DeferredLogger(string category) : ILogger
    {
        private ILogger? _resolved;
        private ILoggerFactory? _resolvedFrom;

        private ILogger Resolve()
        {
            var current = _factory;
            if ((current != _resolvedFrom) || (_resolved is null))
            {
                _resolved = current?.CreateLogger(category) ?? NullLoggerFactory.Instance.CreateLogger(category);
                _resolvedFrom = current;
            }

            return _resolved;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => Resolve().BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => Resolve().IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Resolve().Log(logLevel, eventId, state, exception, formatter);
    }
}
