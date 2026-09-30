using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Captures the Error-level lines <see cref="SimLog"/> emits while it is installed as the test's log factory, so a test
/// can assert on what the simulation logs. Other levels are dropped.
/// </summary>
internal sealed class ErrorLogCapture : ILoggerProvider, ILogger
{
    private readonly List<string> _errors = [];

    public IReadOnlyList<string> Errors => _errors;

    /// <summary>Installs a fresh capture as the SimLog factory for the current test and returns it.</summary>
    public static ErrorLogCapture Install()
    {
        var capture = new ErrorLogCapture();
        SimLog.InitializeForTest(LoggerFactory.Create(builder => builder.AddProvider(capture).SetMinimumLevel(LogLevel.Error)));
        return capture;
    }

    public ILogger CreateLogger(string categoryName) => this;

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (IsEnabled(logLevel))
        {
            _errors.Add(formatter(state, exception));
        }
    }

    public void Dispose() { }
}

/// <summary>
/// The fail-loud check for the sidecars' <c>exitCapacity</c> entries. At runtime an entry that does not resolve on the
/// live map only logs an Error and is dropped (<see cref="ExitCapacityResolver"/>), so this test resolves every entry in
/// every shipped sidecar against the committed layout of the airport it names and fails on any that is unresolved or
/// ambiguous.
/// </summary>
public class ExitCapacitySidecarTests(ITestOutputHelper output)
{
    [Fact]
    public void EveryExitCapacityEntry_ResolvesToExactlyOneSegmentOnItsAirportLayout()
    {
        TestVnasData.EnsureInitialized();
        AirportSidecarLoadResult loaded = AirportSidecarLoader.LoadAll(Path.Combine(AppContext.BaseDirectory, "Data", "ARTCCs"));
        Assert.DoesNotContain(loaded.Warnings, w => w.Contains("exitCapacity"));
        List<AirportSidecar> withRules = [.. loaded.Airports.Where(a => a.ExitCapacity.Count > 0)];
        Assert.NotEmpty(withRules);

        var data = new TestAirportGroundData();
        List<string> problems = [];
        foreach (AirportSidecar airport in withRules)
        {
            AirportGroundLayout? layout = data.GetLayout(airport.AirportId);
            if (layout is null)
            {
                problems.Add($"{airport.AirportId}: no committed layout under TestData to resolve its exitCapacity entries against");
                continue;
            }

            var capture = ErrorLogCapture.Install();
            IReadOnlyList<ExitCapacitySegment> segments = ExitCapacityResolver.Resolve(layout, airport.ExitCapacity);
            output.WriteLine($"{airport.AirportId}: {segments.Count}/{airport.ExitCapacity.Count} exitCapacity entries resolved");
            problems.AddRange(capture.Errors);
            if (segments.Count != airport.ExitCapacity.Count)
            {
                problems.Add($"{airport.AirportId}: {segments.Count} of {airport.ExitCapacity.Count} exitCapacity entries resolved");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}
