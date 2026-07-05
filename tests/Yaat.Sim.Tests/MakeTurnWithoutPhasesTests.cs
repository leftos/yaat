using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// Tests that R360/L360/R270/L270 commands work on airborne aircraft without
/// active phases. Previously these commands were only handled inside
/// TryApplyTowerCommand (phase-dependent path), so they failed with
/// "requires an active runway assignment" when no phases existed.
/// </summary>
public class MakeTurnWithoutPhasesTests(ITestOutputHelper output)
{
    private static AircraftState MakeAirborneAircraft()
    {
        return new AircraftState
        {
            Callsign = "N805FM",
            AircraftType = "C172",
            Position = new LatLon(37.72, -122.22),
            TrueHeading = new TrueHeading(090),
            Altitude = 2000,
            IndicatedAirspeed = 120,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "OAK",
                Altitude = PlannedAltitude.Vfr(3000),
                FlightRules = "VFR",
            },
        };
    }

    [Theory]
    [InlineData("R360", "Make right 360")]
    [InlineData("L360", "Make left 360")]
    [InlineData("R270", "Make right 270")]
    [InlineData("L270", "Make left 270")]
    public void MakeTurn_WithoutPhases_Succeeds(string command, string expectedMessage)
    {
        var ac = MakeAirborneAircraft();
        Assert.Null(ac.Phases);

        var parseResult = CommandParser.ParseCompound(command, ac.FlightPlan.Route);
        Assert.True(parseResult.IsSuccess, $"Parse failed: {parseResult.Reason}");

        var result = CommandDispatcher.DispatchCompound(parseResult.Value!, ac, TestDispatch.Context(new Random(0), validateDctFixes: false));

        output.WriteLine($"{command}: Success={result.Success} Message={result.Message}");

        Assert.True(result.Success, $"{command} should succeed without phases but got: {result.Message}");
        Assert.Contains(expectedMessage, result.Message);
        Assert.NotNull(ac.Phases);
        Assert.IsType<MakeTurnPhase>(ac.Phases.CurrentPhase);
    }

    [Theory]
    [InlineData("CM 004", 400)]
    [InlineData("DM 020", 2000)]
    public void MakeTurn_VerticalCommand_DoesNotClearPhase(string verticalCmd, int expectedAlt)
    {
        var ac = MakeAirborneAircraft();

        var r360Parse = CommandParser.ParseCompound("R360", ac.FlightPlan.Route);
        Assert.True(r360Parse.IsSuccess, $"R360 parse failed: {r360Parse.Reason}");
        var r360Result = CommandDispatcher.DispatchCompound(r360Parse.Value!, ac, TestDispatch.Context(new Random(0), validateDctFixes: false));
        Assert.True(r360Result.Success, $"R360 dispatch failed: {r360Result.Message}");
        Assert.IsType<MakeTurnPhase>(ac.Phases?.CurrentPhase);
        var turnPhase = ac.Phases!.CurrentPhase!;

        var parsed = CommandParser.ParseCompound(verticalCmd, ac.FlightPlan.Route);
        Assert.True(parsed.IsSuccess, $"{verticalCmd} parse failed: {parsed.Reason}");
        var result = CommandDispatcher.DispatchCompound(parsed.Value!, ac, TestDispatch.Context(new Random(0), validateDctFixes: false));

        output.WriteLine($"{verticalCmd}: Success={result.Success} Message={result.Message}");
        Assert.True(result.Success, $"{verticalCmd}: {result.Message}");
        Assert.Same(turnPhase, ac.Phases?.CurrentPhase);
        Assert.Equal(expectedAlt, ac.Targets.AssignedAltitude);
    }

    [Fact]
    public void MakeTurn_SpeedCommand_DoesNotClearPhase()
    {
        var ac = MakeAirborneAircraft();

        var r360 = CommandParser.ParseCompound("R360", ac.FlightPlan.Route);
        var r360Result = CommandDispatcher.DispatchCompound(r360.Value!, ac, TestDispatch.Context(new Random(0), validateDctFixes: false));
        Assert.True(r360Result.Success);
        var turnPhase = ac.Phases?.CurrentPhase;
        Assert.IsType<MakeTurnPhase>(turnPhase);

        var parsed = CommandParser.ParseCompound("SPD 90", ac.FlightPlan.Route);
        Assert.True(parsed.IsSuccess, $"SPD 90 parse failed: {parsed.Reason}");
        var result = CommandDispatcher.DispatchCompound(parsed.Value!, ac, TestDispatch.Context(new Random(0), validateDctFixes: false));

        output.WriteLine($"SPD 90: Success={result.Success} Message={result.Message}");
        Assert.True(result.Success, $"SPD 90: {result.Message}");
        Assert.Same(turnPhase, ac.Phases?.CurrentPhase);
        Assert.Equal(90, ac.Targets.AssignedSpeed);
    }

    [Fact]
    public void MakeTurn_WithExistingPhases_StillWorks()
    {
        var runway = TestRunwayFactory.Make(
            designator: "28R",
            airportId: "OAK",
            thresholdLat: 37.72,
            thresholdLon: -122.22,
            endLat: 37.73,
            endLon: -122.27,
            heading: 280,
            elevationFt: 9
        );

        var ac = MakeAirborneAircraft();
        ac.Phases = new PhaseList { AssignedRunway = runway };
        ac.Phases.Add(new VfrHoldPhase());
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        var parseResult = CommandParser.ParseCompound("R360", ac.FlightPlan.Route);
        Assert.True(parseResult.IsSuccess, $"Parse failed: {parseResult.Reason}");

        var result = CommandDispatcher.DispatchCompound(parseResult.Value!, ac, TestDispatch.Context(new Random(0), validateDctFixes: false));

        output.WriteLine($"R360 with phases: Success={result.Success} Message={result.Message}");
        Assert.True(result.Success, $"R360 with phases should succeed but got: {result.Message}");
    }

    /// <summary>
    /// E2E test using the recording where N805FM gets R360 rejected.
    /// Replays to the point where the command was sent (t≈771) and verifies
    /// R360 succeeds on an aircraft with no active phases.
    /// </summary>
    [Fact]
    public void Recording_N805FM_R360_Succeeds()
    {
        var recording = RecordingLoader.Load("TestData/f8e389804194.zip");
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to just before the R360 command was sent (t=771 based on server logs)
        engine.Replay(recording, 770);

        var ac = engine.FindAircraft("N805FM");
        Assert.NotNull(ac);

        output.WriteLine($"t=770: N805FM alt={ac.Altitude:F0} phases={ac.Phases?.CurrentPhase?.GetType().Name ?? "(none)"}");

        // Send R360 — this is the command that failed in the bug report
        var result = engine.SendCommand("N805FM", "R360");

        output.WriteLine($"R360 result: Success={result.Success} Message={result.Message}");

        Assert.True(result.Success, $"R360 should succeed but got: {result.Message}");
    }

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        return new SimulationEngine(groundData);
    }
}
