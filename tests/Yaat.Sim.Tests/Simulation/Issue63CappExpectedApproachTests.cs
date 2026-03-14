using System.Text.Json;
using MartinCostello.Logging.XUnit;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for GitHub issue #63: bare CAPP should auto-resolve from ExpectedApproach.
///
/// Recording: S3-NCTC-2 Area C Sequencing — USC28 has expectedApproach: "I28R",
/// no destinationRunway, spawnDelay=0. A bare CAPP should resolve to ILS 28R.
/// </summary>
public class Issue63CappExpectedApproachTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue63-capp-expected-approach-recording.json";

    private static SessionRecording? LoadRecording()
    {
        if (!File.Exists(RecordingPath))
        {
            return null;
        }

        var json = File.ReadAllText(RecordingPath);
        return JsonSerializer.Deserialize<SessionRecording>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    private SimulationEngine? BuildEngine()
    {
        var fixes = TestVnasData.FixDatabase;
        if (fixes is null)
        {
            return null;
        }

        var procedures = TestVnasData.ProcedureDatabase;
        var approaches = TestVnasData.ApproachDatabase;
        if (procedures is null || approaches is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        var loggerFactory = LoggerFactory.Create(builder => builder.AddXUnit(output).SetMinimumLevel(LogLevel.Debug));
        SimLog.Initialize(loggerFactory);

        return new SimulationEngine(fixes, fixes, groundData, approaches, procedures);
    }

    /// <summary>
    /// USC28 has expectedApproach "I28R" and no destinationRunway.
    /// A bare CAPP (force, to bypass intercept angle at t=5) should resolve to I28R,
    /// set ActiveApproach, and assign DestinationRunway from the approach procedure.
    /// </summary>
    [Fact]
    public void BareCapp_WithExpectedApproach_ResolvesToI28R()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Recording or NavData not available, skipping");
            return;
        }

        engine.Replay(recording, 5);

        var aircraft = engine.FindAircraft("USC28");
        Assert.NotNull(aircraft);

        output.WriteLine(
            $"Before: ExpectedApproach={aircraft.ExpectedApproach} DestinationRunway={aircraft.DestinationRunway} Phases={aircraft.Phases is not null}"
        );

        Assert.Equal("I28R", aircraft.ExpectedApproach);
        Assert.Null(aircraft.DestinationRunway);

        var result = engine.SendCommand("USC28", "CAPP");

        output.WriteLine(
            $"After:  Success={result.Success} ActiveApproach={aircraft.Phases?.ActiveApproach?.ApproachId} DestinationRunway={aircraft.DestinationRunway}"
        );

        Assert.True(result.Success, $"Bare CAPP should succeed with ExpectedApproach set. Got: {result.Message}");
        Assert.NotNull(aircraft.Phases?.ActiveApproach);
        Assert.Equal("I28R", aircraft.Phases.ActiveApproach.ApproachId);
        Assert.Equal("28R", aircraft.DestinationRunway);
    }

    /// <summary>
    /// USC28 with explicit CAPP I28R should succeed without prior runway assignment,
    /// and should set DestinationRunway from the approach procedure.
    /// </summary>
    [Fact]
    public void ExplicitCapp_SetsDestinationRunway()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Recording or NavData not available, skipping");
            return;
        }

        engine.Replay(recording, 5);

        var aircraft = engine.FindAircraft("USC28");
        Assert.NotNull(aircraft);
        Assert.Null(aircraft.DestinationRunway);

        var result = engine.SendCommand("USC28", "CAPP I28R");

        output.WriteLine($"CAPP I28R: Success={result.Success} DestinationRunway={aircraft.DestinationRunway}");

        Assert.True(result.Success, $"Explicit CAPP I28R should succeed. Got: {result.Message}");
        Assert.Equal("28R", aircraft.DestinationRunway);
    }

    /// <summary>
    /// EAPP should set ExpectedApproach but should NOT set DestinationRunway
    /// (it's an expectation, not a clearance).
    /// </summary>
    [Fact]
    public void Eapp_SetsExpectedApproach_DoesNotSetDestinationRunway()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Recording or NavData not available, skipping");
            return;
        }

        engine.Replay(recording, 5);

        var aircraft = engine.FindAircraft("USC28");
        Assert.NotNull(aircraft);

        // Clear the scenario-set ExpectedApproach to test EAPP independently
        aircraft.ExpectedApproach = null;
        Assert.Null(aircraft.DestinationRunway);

        var result = engine.SendCommand("USC28", "EAPP I30");

        output.WriteLine(
            $"EAPP I30: Success={result.Success} ExpectedApproach={aircraft.ExpectedApproach} DestinationRunway={aircraft.DestinationRunway}"
        );

        Assert.True(result.Success, $"EAPP should succeed. Got: {result.Message}");
        Assert.Equal("I30", aircraft.ExpectedApproach);
        Assert.Null(aircraft.DestinationRunway);
    }
}
