using MartinCostello.Logging.XUnit;
using Microsoft.Extensions.Logging;
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
            Latitude = 37.72,
            Longitude = -122.22,
            TrueHeading = new TrueHeading(090),
            Altitude = 2000,
            IndicatedAirspeed = 120,
            IsOnGround = false,
            Departure = "OAK",
            CruiseAltitude = 3000,
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

        var parseResult = CommandParser.ParseCompound(command, ac.Route);
        Assert.True(parseResult.IsSuccess, $"Parse failed: {parseResult.Reason}");

        var result = CommandDispatcher.DispatchCompound(parseResult.Value!, ac, null, new Random(0), false);

        output.WriteLine($"{command}: Success={result.Success} Message={result.Message}");

        Assert.True(result.Success, $"{command} should succeed without phases but got: {result.Message}");
        Assert.Contains(expectedMessage, result.Message);
        Assert.NotNull(ac.Phases);
        Assert.IsType<MakeTurnPhase>(ac.Phases.CurrentPhase);
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

        var parseResult = CommandParser.ParseCompound("R360", ac.Route);
        Assert.True(parseResult.IsSuccess, $"Parse failed: {parseResult.Reason}");

        var result = CommandDispatcher.DispatchCompound(parseResult.Value!, ac, null, new Random(0), false);

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
        var recording = RecordingLoader.Load("TestData/r360-no-phases-recording.zip");
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
}
