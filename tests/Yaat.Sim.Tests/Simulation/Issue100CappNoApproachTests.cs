using System.Text.Json;
using MartinCostello.Logging.XUnit;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Tests for GitHub issue #100: CAPP issued on an aircraft with no resolvable approach
/// should fail cleanly, not destroy phases. CLAND with a runway arg should be rejected.
///
/// Recording: S2-OAK-5 (1) — JSX170 on final rwy 30, CAPP issued at t=727s.
/// </summary>
public class Issue100CappNoApproachTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue100-capp-no-approach-recording.json";

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
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        var loggerFactory = LoggerFactory.Create(builder => builder.AddXUnit(output).SetMinimumLevel(LogLevel.Debug));
        SimLog.Initialize(loggerFactory);

        NavigationDatabase.SetInstance(navDb);
        return new SimulationEngine(groundData);
    }

    [Fact]
    public void Capp_NoApproach_FailsAndPreservesPhases()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to just before the CAPP command at t=727
        engine.Replay(recording, 726);

        var aircraft = engine.FindAircraft("JSX170");
        Assert.NotNull(aircraft);

        // Preconditions: aircraft has phases but no resolvable approach
        Assert.NotNull(aircraft.Phases);
        Assert.Null(aircraft.Phases.ActiveApproach);
        Assert.Null(aircraft.ExpectedApproach);

        var phasesBefore = aircraft.Phases;

        // Issue CAPP — should fail because no approach is resolvable
        var result = engine.SendCommand("JSX170", "CAPP");

        aircraft = engine.FindAircraft("JSX170");
        Assert.NotNull(aircraft);

        Assert.False(result.Success, $"CAPP should fail when no approach is resolvable, but got: {result.Message}");
        Assert.NotNull(aircraft.Phases); // Phases must NOT be destroyed
    }

    [Fact]
    public void Cland30_RejectedWithClearError()
    {
        // CLAND 30 should be rejected at parse time, not treated as UnsupportedCommand
        var parseResult = CommandParser.ParseCompound("CLAND 30", aircraftRoute: null);
        Assert.False(parseResult.IsSuccess, "CLAND 30 should fail to parse");
        Assert.NotNull(parseResult.Reason);
        Assert.DoesNotContain("not yet supported", parseResult.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cland_NoArg_SucceedsOnAircraftWithRunway()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to t=989 where FDX3807 has an assigned runway
        engine.Replay(recording, 989);

        var aircraft = engine.FindAircraft("FDX3807");
        Assert.NotNull(aircraft);
        Assert.NotNull(aircraft.Phases);
        Assert.NotNull(aircraft.Phases.AssignedRunway);

        var result = engine.SendCommand("FDX3807", "CLAND");
        Assert.True(result.Success, $"CLAND should succeed on aircraft with assigned runway, but got: {result.Message}");
    }

    [Fact]
    public void Capp_WithResolvableApproach_Succeeds()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to t=989 where FDX3807 has phases and a destination runway
        engine.Replay(recording, 989);

        var aircraft = engine.FindAircraft("FDX3807");
        Assert.NotNull(aircraft);
        Assert.NotNull(aircraft.Phases);

        // FDX3807 needs an ExpectedApproach or DestinationRunway for CAPP to resolve
        // Set ExpectedApproach explicitly to ensure CAPP can resolve
        if (aircraft.ExpectedApproach is null && aircraft.DestinationRunway is null)
        {
            aircraft.ExpectedApproach = "I30";
        }

        var result = engine.SendCommand("FDX3807", "CAPP");
        Assert.True(result.Success, $"CAPP should succeed when approach is resolvable, but got: {result.Message}");
    }
}
