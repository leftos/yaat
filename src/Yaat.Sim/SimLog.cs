using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Yaat.Sim;

public static class SimLog
{
    // AsyncLocal: per-context factory so parallel tests do not cross-contaminate each
    // other's xunit output helpers. Test A sets F_A in its async context, test B sets
    // F_B in its own; resolution sees the right one as long as ExecutionContext flows.
    //
    // Static fallback: ASP.NET Core dispatches requests onto thread-pool workers whose
    // ExecutionContext may not carry the AsyncLocal value set during host startup. The
    // static field is the unconditional fallback for production servers — set once at
    // startup, visible from every thread regardless of context flow.
    private static ILoggerFactory? _staticFactory;
    private static readonly AsyncLocal<ILoggerFactory?> _scopedFactory = new();

    public static void Initialize(ILoggerFactory factory)
    {
        _staticFactory = factory;
        _scopedFactory.Value = factory;
    }

    /// <summary>
    /// Sets only the AsyncLocal scoped factory — does NOT touch the process-wide static.
    /// Use this from tests to wire xunit output without leaking the test's
    /// ITestOutputHelper into the static fallback (which would survive past the
    /// test's lifetime and cause NREs in unrelated parallel tests when the helper
    /// is disposed). Production startup calls <see cref="Initialize"/> instead.
    /// </summary>
    public static void InitializeForTest(ILoggerFactory factory)
    {
        _scopedFactory.Value = factory;
    }

    public static ILogger CreateLogger<T>() => new DeferredLogger(typeof(T).Name);

    public static ILogger CreateLogger(string category) => new DeferredLogger(category);

    /// <summary>
    /// Logger that defers to whatever <see cref="ILoggerFactory"/> is current at call time.
    /// Static fields using <c>SimLog.CreateLogger</c> are initialized before tests call
    /// <see cref="Initialize"/>. A deferred logger ensures those fields pick up the
    /// configured factory when it's set, rather than capturing a null factory at
    /// class-load time. Resolves AsyncLocal first (test scoping), then the process-wide
    /// static (production), then falls back to <see cref="NullLoggerFactory"/>.
    /// </summary>
    private sealed class DeferredLogger(string category) : ILogger
    {
        private ILogger Resolve()
        {
            var factory = _scopedFactory.Value ?? _staticFactory ?? NullLoggerFactory.Instance;
            return factory.CreateLogger(category);
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => Resolve().BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => Resolve().IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Resolve().Log(logLevel, eventId, state, exception, formatter);
    }
}
