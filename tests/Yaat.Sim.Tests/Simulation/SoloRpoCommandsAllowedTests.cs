using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The <see cref="SimScenarioState.SoloRpoCommandsAllowed"/> development flag: solo training refuses the RPO-only
/// commands (FOLLOWF, CVAF, RFISF, RTISF, CLANDF) unless it is set, and the flag rides the scenario snapshot. Each
/// command runs through <see cref="SimulationEngine.SendCommand"/>, so the engine's dispatch-context wiring is under
/// test along with the gate. Real navdata: an IFR B738 six miles out on OAK 30 with a lead three miles ahead of it.
/// </summary>
public class SoloRpoCommandsAllowedTests(ITestOutputHelper output)
{
    private const string Subject = "GATE1";
    private const string Lead = "LEAD1";

    public static TheoryData<string, string?> RpoOnlyCommands =>
        new()
        {
            { "FOLLOWF LEAD1", null },
            { "CVAF 30", null },
            { "RFISF", null },
            { "RTISF LEAD1", null },
            // CLANDF needs an aircraft established on an approach; the RPO clears it first.
            { "CLANDF", "CVAF 30" },
        };

    public static TheoryData<string, string?, string> RpoOnlyRefusals =>
        new()
        {
            { "FOLLOWF LEAD1", null, "FOLLOWF is RPO-only; use RTIS/RTISF in solo training" },
            { "CVAF 30", null, "CVAF is RPO-only; use RFIS/RTIS in solo training" },
            { "RFISF", null, "RFISF is RPO-only; use RFIS <clock> <miles> in solo training" },
            { "RTISF LEAD1", null, "RTISF is RPO-only; use RTIS <clock> <miles> <direction> <type> <altitude> in solo training" },
            { "CLANDF", "CVAF 30", "CLANDF is RPO-only; clear the aircraft to land with CLAND in solo training" },
        };

    [Theory]
    [MemberData(nameof(RpoOnlyCommands))]
    public void Solo_WithFlag_AcceptsRpoOnlyCommand(string command, string? rpoPrep)
    {
        SimulationEngine? engine = BuildEngineOnFinal(rpoPrep, soloRpoCommandsAllowed: true);
        if (engine is null)
        {
            return;
        }

        CommandResult result = engine.SendCommand(Subject, command);

        Assert.True(result.Success, $"'{command}' must be accepted in solo with the flag set, got: {result.Message}");
    }

    [Theory]
    [MemberData(nameof(RpoOnlyRefusals))]
    public void Solo_WithoutFlag_RefusesRpoOnlyCommand(string command, string? rpoPrep, string refusal)
    {
        SimulationEngine? engine = BuildEngineOnFinal(rpoPrep, soloRpoCommandsAllowed: false);
        if (engine is null)
        {
            return;
        }

        CommandResult result = engine.SendCommand(Subject, command);

        Assert.False(result.Success, $"'{command}' must be refused in solo without the flag");
        Assert.Contains(refusal, result.Message);
    }

    [Fact]
    public void Flag_SurvivesSnapshotRestore()
    {
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        engine.Scenario!.SoloRpoCommandsAllowed = true;
        StateSnapshotDto snapshot = engine.CaptureSnapshot();
        Assert.True(snapshot.Scenario.SoloRpoCommandsAllowed);

        SimulationEngine restored = BuildEngine()!;
        restored.RestoreFromSnapshot(snapshot);

        Assert.True(restored.Scenario!.SoloRpoCommandsAllowed);
    }

    [Fact]
    public void SnapshotJsonWithoutTheField_RestoresFalse()
    {
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        engine.Scenario!.SoloRpoCommandsAllowed = true;
        JsonObject root = JsonSerializer.SerializeToNode(engine.CaptureSnapshot(), RecordingJsonOptions.Default)!.AsObject();
        JsonObject scenario = root[KeyOf(root, "Scenario")]!.AsObject();
        Assert.True(scenario.Remove(KeyOf(scenario, "SoloRpoCommandsAllowed")));
        StateSnapshotDto stripped = root.Deserialize<StateSnapshotDto>(RecordingJsonOptions.Default)!;

        SimulationEngine restored = BuildEngine()!;
        restored.Scenario!.SoloRpoCommandsAllowed = true;
        restored.RestoreFromSnapshot(stripped);

        Assert.False(restored.Scenario.SoloRpoCommandsAllowed);
    }

    private static string KeyOf(JsonObject obj, string name) =>
        obj.Select(p => p.Key).Single(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(new TestAirportGroundData())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-solo-rpo-commands",
                ScenarioName = "Solo RPO Commands",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
            },
        };
    }

    /// <summary>
    /// The subject and its lead on OAK 30 final. <paramref name="rpoPrep"/> runs as the RPO before solo is switched on,
    /// then the scenario enters solo with the flag as given.
    /// </summary>
    private SimulationEngine? BuildEngineOnFinal(string? rpoPrep, bool soloRpoCommandsAllowed)
    {
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            return null;
        }

        engine.World.AddAircraft(OnFinal(Lead, finalDistanceNm: 3.0, altitude: 1100));
        engine.World.AddAircraft(OnFinal(Subject, finalDistanceNm: 6.0, altitude: 2000));
        if (rpoPrep is not null)
        {
            CommandResult prep = engine.SendCommand(Subject, rpoPrep);
            Assert.True(prep.Success, $"RPO prep '{rpoPrep}' failed: {prep.Message}");
        }

        engine.Scenario!.SoloTrainingMode = true;
        engine.Scenario.SoloRpoCommandsAllowed = soloRpoCommandsAllowed;
        return engine;
    }

    private static AircraftState OnFinal(string callsign, double finalDistanceNm, double altitude)
    {
        RunwayInfo? rwy = NavigationDatabase.Instance.GetRunway("OAK", "30");
        Assert.NotNull(rwy);
        double reciprocal = (rwy.TrueHeading.Degrees + 180) % 360;
        (double lat, double lon) = GeoMath.ProjectPointRaw(rwy.ThresholdLatitude, rwy.ThresholdLongitude, reciprocal, finalDistanceNm);
        return new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = new LatLon(lat, lon),
            TrueHeading = rwy.TrueHeading,
            TrueTrack = rwy.TrueHeading,
            Altitude = altitude,
            IndicatedAirspeed = 180,
            IsOnGround = false,
            HasMadeInitialContact = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KSFO",
                Destination = "OAK",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr((int)altitude),
            },
        };
    }
}
