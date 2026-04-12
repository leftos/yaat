using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Yaat.Sim;

public static class SimLog
{
    // AsyncLocal so each test's async context gets its own factory. A plain static field
    // races between parallel tests: test A sets factory F_A, test B sets F_B, and when A's
    // still-running engine ticks try to log they resolve via whichever F_X is current —
    // which may belong to a completed test whose xUnit output helper has been disposed.
    private static readonly AsyncLocal<ILoggerFactory?> _factory = new();

    public static void Initialize(ILoggerFactory factory) => _factory.Value = factory;

    public static ILogger CreateLogger<T>() => new DeferredLogger(typeof(T).Name);

    public static ILogger CreateLogger(string category) => new DeferredLogger(category);

    /// <summary>
    /// Logger that defers to whatever <see cref="ILoggerFactory"/> is current at call time.
    /// Static fields using <c>SimLog.CreateLogger</c> are initialized before tests call
    /// <see cref="Initialize"/>. A deferred logger ensures those fields pick up the
    /// test-configured factory when it's set, rather than capturing a null factory at
    /// class-load time. Resolves the factory via AsyncLocal so parallel tests do not
    /// cross-contaminate each other's output helpers.
    /// </summary>
    private sealed class DeferredLogger(string category) : ILogger
    {
        private ILogger Resolve() =>
            _factory.Value is { } current ? current.CreateLogger(category) : NullLoggerFactory.Instance.CreateLogger(category);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => Resolve().BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => Resolve().IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Resolve().Log(logLevel, eventId, state, exception, formatter);
    }
}
