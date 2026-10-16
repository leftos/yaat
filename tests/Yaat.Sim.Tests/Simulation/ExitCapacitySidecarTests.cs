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
/// ambiguous: every one-runway entry must resolve to exactly one segment, and every taxiway of every crossovers entry
/// to exactly one segment shared by both of the entry's runways.
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
            output.WriteLine($"{airport.AirportId}: {segments.Count}/{airport.ExitCapacity.Count} exitCapacity rules resolved");
            problems.AddRange(capture.Errors);
            foreach (ExitCapacityRule rule in airport.ExitCapacity)
            {
                problems.AddRange(RuleProblems(airport.AirportId, rule, segments));
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// A malformed exitCapacity entry (its runway and taxiway fields given as <paramref name="form"/>, with valid limits)
    /// is skipped with one warning naming what is wrong, and adds no rule.
    /// </summary>
    [Theory]
    [InlineData(
        """
            "runway": "28R", "taxiway": "T", "runways": ["28R", "28L"], "crossovers": ["K"]
            """,
        "not both"
    )]
    [InlineData(
        """
            "runways": ["28R"], "crossovers": ["K"]
            """,
        "runways must name exactly two distinct runway ends"
    )]
    [InlineData(
        """
            "runways": ["28R", "28R"], "crossovers": ["K"]
            """,
        "runways must name exactly two distinct runway ends"
    )]
    [InlineData(
        """
            "runways": ["28R", "28r"], "crossovers": ["K"]
            """,
        "runways must name exactly two distinct runway ends"
    )]
    [InlineData(
        """
            "runways": ["28R", "28L"], "crossovers": []
            """,
        "crossovers must list at least one taxiway"
    )]
    [InlineData(
        """
            "runways": ["28R", "28L"], "crossovers": ["K", " "]
            """,
        "crossovers must not list a blank taxiway"
    )]
    [InlineData(
        """
            "runways": ["28R", "28L"], "crossovers": ["K", null]
            """,
        "crossovers must not list a blank taxiway"
    )]
    [InlineData(
        """
            "runways": ["28R", "28L"], "crossovers": ["K", "k"]
            """,
        "crossovers must not list a taxiway twice"
    )]
    [InlineData(
        """
            "runways": ["28R", "28L"], "crossovers": null
            """,
        "runways and crossovers must be lists, not null"
    )]
    [InlineData(
        """
            "runways": null, "crossovers": ["K"]
            """,
        "runways and crossovers must be lists, not null"
    )]
    public void MalformedExitCapacityEntry_IsSkippedWithAWarning(string form, string expectedProblem)
    {
        AirportSidecarLoadResult loaded = LoadSidecar(
            $$"""
            {
              "airportId": "SFO",
              "exitCapacity": [ { {{form}}, "maxAircraft": 2, "maxAircraftAboveCwt": 1, "cwtThreshold": "G" } ]
            }
            """
        );

        string warning = Assert.Single(loaded.Warnings);
        output.WriteLine(warning);
        Assert.Contains("exitCapacity[0]", warning, StringComparison.Ordinal);
        Assert.Contains(expectedProblem, warning, StringComparison.Ordinal);
        Assert.All(loaded.Airports, a => Assert.Empty(a.ExitCapacity));
    }

    /// <summary>
    /// What is wrong with how <paramref name="rule"/> resolved: a one-runway rule must own exactly one segment; a crossover
    /// rule (one taxiway of a crossovers entry) must own exactly one segment, and that segment must govern both runways.
    /// </summary>
    private static IEnumerable<string> RuleProblems(string airportId, ExitCapacityRule rule, IReadOnlyList<ExitCapacitySegment> segments)
    {
        string label = rule.IsCrossover
            ? $"crossover {rule.Runway}/{rule.CrossoverRunway} taxiway {rule.Taxiway}"
            : $"runway {rule.Runway} taxiway {rule.Taxiway}";
        List<ExitCapacitySegment> owned = [.. segments.Where(s => ReferenceEquals(s.Rule, rule))];
        if (owned.Count != 1)
        {
            yield return $"{airportId}: exitCapacity {label} resolved to {owned.Count} segments, expected one";
            yield break;
        }

        if ((rule.CrossoverRunway is { } other) && !(owned[0].AppliesToRunway(rule.Runway) && owned[0].AppliesToRunway(other)))
        {
            yield return $"{airportId}: exitCapacity {label} resolved to a segment not shared by {rule.Runway} and {other}";
        }
    }

    /// <summary>Loads <paramref name="json"/> as the only sidecar file of a throwaway ARTCCs tree.</summary>
    internal static AirportSidecarLoadResult LoadSidecar(string json)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "exit-capacity-sidecar-" + Guid.NewGuid());
        string categoryDir = Path.Combine(tempDir, "ZTEST", "Airports");
        Directory.CreateDirectory(categoryDir);
        try
        {
            File.WriteAllText(Path.Combine(categoryDir, "test.json"), json);
            return AirportSidecarLoader.LoadAll(tempDir);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
