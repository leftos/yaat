using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// An airport first named mid-session whose layout is still loading is deferred: the engine records the second it
/// deferred it and, live, the second it admitted the landed layout at the opening of a second. A replay reads only the
/// recorded seconds — never the source's answer for a deferred airport — so it reproduces the live run exactly, from a
/// snapshot taken while the airport was deferred as well as from t=0.
/// </summary>
public class LayoutAdmissionTests
{
    private const string Callsign = "N1";

    private const string ScenarioJson = """
        {
          "id": "layout-admission",
          "name": "Layout admission",
          "primaryAirportId": "OAK",
          "aircraft": [
            {
              "id": "a1",
              "aircraftId": "N1",
              "aircraftType": "C172",
              "transponderMode": "C",
              "startingConditions": {
                "type": "Coordinates",
                "coordinates": { "lat": 37.80, "lon": -122.30 },
                "altitude": 3500,
                "heading": 90,
                "speed": 100
              },
              "flightplan": {
                "rules": "VFR",
                "departure": "KOAK",
                "destination": "KOAK",
                "cruiseAltitude": 3500,
                "cruiseSpeed": 100,
                "route": "",
                "remarks": "",
                "aircraftType": "C172"
              }
            }
          ]
        }
        """;

    public LayoutAdmissionTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void RuntimeAirport_DeferredAtN_AdmittedOnceAtTheFirstSecondItIsLoaded()
    {
        var data = new PartlyLoadingGroundData("SMF");
        SimulationEngine engine = Load(data);
        Tick(engine, 2);

        NameSmf(engine);

        RecordedLayoutDeferred deferred = Assert.Single(engine.Scenario!.ActionLog.OfType<RecordedLayoutDeferred>());
        Assert.Equal(2, deferred.ElapsedSeconds);
        Assert.Equal("SMF", deferred.AirportId);
        Assert.Equal(["SMF"], engine.Scenario.DeferredLayoutAirports);

        // While deferred, the source is read once per opened second (the admission check) and by nothing else.
        int reads = data.ReadCount("SMF");
        Tick(engine, 3);
        Assert.Equal(reads + 3, data.ReadCount("SMF"));
        Assert.Empty(engine.Scenario.ActionLog.OfType<RecordedLayoutAdmitted>());
        Assert.Null(engine.ResolveAirportLayout("KSMF"));

        data.Release("SMF");
        Tick(engine, 1);

        RecordedLayoutAdmitted admitted = Assert.Single(engine.Scenario.ActionLog.OfType<RecordedLayoutAdmitted>());
        Assert.Equal(6, admitted.ElapsedSeconds);
        Assert.Equal("SMF", admitted.AirportId);
        Assert.Empty(engine.Scenario.DeferredLayoutAirports);
        Assert.NotNull(engine.ResolveAirportLayout("KSMF"));

        Tick(engine, 3);
        Assert.Single(engine.Scenario.ActionLog.OfType<RecordedLayoutAdmitted>());
        Assert.Single(engine.Scenario.ActionLog.OfType<RecordedLayoutDeferred>());
    }

    [Fact]
    public void HotPath_ReadsTheSourceZeroTimesWhileDeferred()
    {
        var data = new PartlyLoadingGroundData("SMF");
        SimulationEngine engine = Load(data);
        Tick(engine, 1);
        NameSmf(engine);

        // In a phase with no layout of its own, every sub-tick's phase context resolves the aircraft's layout from its plan,
        // which names the deferred SMF: that resolution must answer from the deferred set, not the source.
        AircraftState aircraft = engine.World.FindAircraft(Callsign)!;
        aircraft.Ground.Layout = null;
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new VfrHoldPhase { OrbitDirection = TurnDirection.Left });
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, null));
        Assert.Null(engine.ResolveGroundLayout(aircraft));

        int reads = data.ReadCount("SMF");
        Tick(engine, 4);

        Assert.Equal(reads + 4, data.ReadCount("SMF"));
        Assert.False(aircraft.Phases.IsComplete);
    }

    [Fact]
    public void ReplayDriver_ReproducesTheDeferredAndAdmittedSeconds_ByteForByte()
    {
        var data = new PartlyLoadingGroundData("SMF");
        SimulationEngine live = Load(data);
        var liveSnapshots = new Dictionary<int, string>();
        for (int t = 1; t <= 10; t++)
        {
            if (t == 6)
            {
                data.Release("SMF");
            }

            live.TickOneSecond();
            if (t == 2)
            {
                NameSmf(live);
            }

            liveSnapshots[t] = SnapshotJson(live);
        }

        List<RecordedAction> actions = [.. live.Scenario!.ActionLog];
        Assert.Single(actions.OfType<RecordedLayoutDeferred>());
        Assert.Single(actions.OfType<RecordedLayoutAdmitted>());

        // The replay's source has SMF loaded throughout, as a room's full pin does on a rewind.
        SimulationEngine replay = Load(new PartlyLoadingGroundData());
        replay.ArmReplay(actions);
        for (int t = 1; t <= 10; t++)
        {
            replay.ReplayOneSecond();
            Assert.Equal(liveSnapshots[t], SnapshotJson(replay));
        }
    }

    [Fact]
    public void SnapshotWhileDeferred_RestoresTheSetAndStaysLoadingUntilTheAdmissionRecord()
    {
        var data = new PartlyLoadingGroundData("SMF");
        SimulationEngine live = Load(data);
        Assert.DoesNotContain(nameof(ScenarioSnapshotDto.DeferredLayoutAirportIds), SnapshotJson(live), StringComparison.Ordinal);
        Tick(live, 2);
        NameSmf(live);
        Tick(live, 2);
        StateSnapshotDto atFour = live.CaptureSnapshot();
        Tick(live, 1);
        data.Release("SMF");
        Tick(live, 3);
        Assert.Equal(6, Assert.Single(live.Scenario!.ActionLog.OfType<RecordedLayoutAdmitted>()).ElapsedSeconds);
        Assert.Equal(["SMF"], atFour.Scenario.DeferredLayoutAirportIds);

        SimulationEngine restored = Load(new PartlyLoadingGroundData());
        restored.RestoreFromSnapshot(atFour);
        Assert.Equal(["SMF"], restored.Scenario!.DeferredLayoutAirports);
        Assert.Null(restored.ResolveAirportLayout("SMF"));
        Assert.Equal("OAK", NavigationDatabase.NormalizeAirport(restored.World.FindAircraft(Callsign)!.Ground.Layout!.AirportId));

        restored.ArmReplay([.. live.Scenario.ActionLog]);
        restored.ReplayOneSecond();
        Assert.Equal(["SMF"], restored.Scenario.DeferredLayoutAirports);
        Assert.Null(restored.ResolveAirportLayout("SMF"));

        restored.ReplayOneSecond();
        Assert.Empty(restored.Scenario.DeferredLayoutAirports);
        Assert.NotNull(restored.ResolveAirportLayout("SMF"));

        // An aircraft the snapshot binds to the deferred SMF comes back without the SMF layout the source now holds: the
        // deferred set is restored before any aircraft re-attaches.
        JsonNode node = JsonSerializer.SerializeToNode(atFour)!;
        node["Aircraft"]![0]!["Ground"]!["LayoutAirportId"] = "SMF";
        StateSnapshotDto boundToSmf = node.Deserialize<StateSnapshotDto>()!;
        SimulationEngine reattached = Load(new PartlyLoadingGroundData());
        reattached.RestoreFromSnapshot(boundToSmf);
        Assert.NotEqual("SMF", NavigationDatabase.NormalizeAirport(reattached.World.FindAircraft(Callsign)!.Ground.Layout!.AirportId));
    }

    [Fact]
    public void LoadedOnFirstRead_IsAdmittedWithNothingRecorded()
    {
        SimulationEngine engine = Load(new PartlyLoadingGroundData());
        Tick(engine, 2);

        NameSmf(engine);
        Tick(engine, 2);

        Assert.Equal("SMF", NavigationDatabase.NormalizeAirport(engine.World.FindAircraft(Callsign)!.Ground.Layout!.AirportId));
        Assert.Empty(engine.Scenario!.DeferredLayoutAirports);
        Assert.Empty(engine.Scenario.ActionLog.OfType<RecordedLayoutDeferred>());
        Assert.Empty(engine.Scenario.ActionLog.OfType<RecordedLayoutAdmitted>());
    }

    [Fact]
    public void ReplayOfAnUnrecordedRuntimeAirport_AnswersLoadingWithOneFidelityWarning()
    {
        List<RecordedAction> actions = LiveLogNamingSmfTwiceUnrecorded();
        var log = new WarningLogCapture();
        var replay = new SimulationEngine(new PartlyLoadingGroundData("SMF"), PushPlanStore.CreateShipped(), log);
        replay.LoadScenario(ScenarioJson, 42, MagneticDeclination.EvaluationDateUtc);
        replay.ArmReplay(actions);
        for (int t = 1; t <= 5; t++)
        {
            replay.ReplayOneSecond();
        }

        // Read inside the replay only: a read here, after the replay step, runs as the engine's own (test) kind.
        Assert.Empty(replay.Scenario!.DeferredLayoutAirports);
        Assert.Equal("OAK", NavigationDatabase.NormalizeAirport(replay.World.FindAircraft(Callsign)!.Ground.Layout!.AirportId));
        string warning = Assert.Single(log.Warnings, w => w.Contains("replay-fidelity", StringComparison.Ordinal));
        Assert.Contains("SMF", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplayOfAnUnrecordedRuntimeAirport_StaysLoadingWhenTheSourceLandsMidReplay()
    {
        List<RecordedAction> actions = LiveLogNamingSmfTwiceUnrecorded();
        var log = new WarningLogCapture();
        var data = new PartlyLoadingGroundData("SMF");
        var replay = new SimulationEngine(data, PushPlanStore.CreateShipped(), log);
        replay.LoadScenario(ScenarioJson, 42, MagneticDeclination.EvaluationDateUtc);
        replay.ArmReplay(actions);
        replay.ReplayOneSecond();
        replay.ReplayOneSecond();

        // The fetch lands between the two recorded reads; the tape recorded no admission, so the second read still loads.
        data.Release("SMF");
        int reads = data.ReadCount("SMF");
        replay.ReplayOneSecond();
        replay.ReplayOneSecond();

        Assert.Equal(reads, data.ReadCount("SMF"));
        Assert.Equal("OAK", NavigationDatabase.NormalizeAirport(replay.World.FindAircraft(Callsign)!.Ground.Layout!.AirportId));
        Assert.Single(log.Warnings, w => w.Contains("replay-fidelity", StringComparison.Ordinal));
    }

    [Fact]
    public void CommandOriginatedDeferred_AppliesAtOpenSecondOfItsStamp()
    {
        SimulationEngine live = Load(new PartlyLoadingGroundData("SMF"));
        Tick(live, 3);
        NameSmf(live);
        Tick(live, 1);
        Assert.Equal(3, Assert.Single(live.Scenario!.ActionLog.OfType<RecordedLayoutDeferred>()).ElapsedSeconds);

        SimulationEngine replay = Load(new PartlyLoadingGroundData());
        replay.ArmReplay([.. live.Scenario.ActionLog]);
        replay.ReplayOneSecond();
        replay.ReplayOneSecond();
        Assert.Empty(replay.Scenario!.DeferredLayoutAirports);

        // The first sub-tick of second 3 opens it: the deferral is in force before the command stamped 3 applies.
        replay.ReplayOneSubTick();
        AircraftState aircraft = replay.World.FindAircraft(Callsign)!;
        Assert.Equal(["SMF"], replay.Scenario.DeferredLayoutAirports);
        Assert.True(NavigationDatabase.AirportIdsMatch("OAK", aircraft.FlightPlan.Destination));

        for (int sub = 1; sub < SimulationEngine.PhysicsSubTickRate; sub++)
        {
            replay.ReplayOneSubTick();
        }

        Assert.True(NavigationDatabase.AirportIdsMatch("SMF", aircraft.FlightPlan.Destination));
        Assert.Equal("OAK", NavigationDatabase.NormalizeAirport(aircraft.Ground.Layout!.AirportId));
    }

    [Fact]
    public void DeferredAtZero_AppliesBeforeFirstUse_OnReplayDriverRange()
    {
        var data = new PartlyLoadingGroundData("SMF");
        SimulationEngine live = Load(data);
        NameSmf(live);
        Assert.Equal(0, Assert.Single(live.Scenario!.ActionLog.OfType<RecordedLayoutDeferred>()).ElapsedSeconds);
        Tick(live, 1);
        data.Release("SMF");
        Tick(live, 2);
        Assert.Equal(2, Assert.Single(live.Scenario.ActionLog.OfType<RecordedLayoutAdmitted>()).ElapsedSeconds);

        SimulationEngine replay = Load(new PartlyLoadingGroundData());
        replay.ReplayFromStartTo(1, [.. live.Scenario.ActionLog]);

        // The command at t=0 read SMF through a deferral already in force, so it kept its OAK layout as it did live.
        Assert.Equal(["SMF"], replay.Scenario!.DeferredLayoutAirports);
        Assert.Equal("OAK", NavigationDatabase.NormalizeAirport(replay.World.FindAircraft(Callsign)!.Ground.Layout!.AirportId));
    }

    private static SimulationEngine Load(PartlyLoadingGroundData data)
    {
        var engine = new SimulationEngine(data, PushPlanStore.CreateShipped());
        engine.LoadScenario(ScenarioJson, 42, MagneticDeclination.EvaluationDateUtc);
        Assert.NotNull(engine.World.FindAircraft(Callsign));
        return engine;
    }

    // A live log on a source with SMF loaded throughout: APT SMF at t=2 and again at t=3, and no layout record.
    private static List<RecordedAction> LiveLogNamingSmfTwiceUnrecorded()
    {
        SimulationEngine live = Load(new PartlyLoadingGroundData());
        Tick(live, 2);
        NameSmf(live);
        Tick(live, 1);
        NameSmf(live);
        Tick(live, 2);
        Assert.Equal(2, live.Scenario!.ActionLog.OfType<RecordedCommand>().Count());
        Assert.Empty(live.Scenario.ActionLog.OfType<RecordedLayoutDeferred>());
        return [.. live.Scenario.ActionLog];
    }

    private static void NameSmf(SimulationEngine engine)
    {
        CommandResult result = engine.SendCommand(Callsign, "APT SMF");
        Assert.True(result.Success, result.Message);
    }

    private static void Tick(SimulationEngine engine, int seconds)
    {
        for (int i = 0; i < seconds; i++)
        {
            engine.TickOneSecond();
        }
    }

    private static string SnapshotJson(SimulationEngine engine) => JsonSerializer.Serialize(engine.CaptureSnapshot());
}
