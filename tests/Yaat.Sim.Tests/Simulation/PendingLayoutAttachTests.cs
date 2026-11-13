using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A flight-plan amend or a destination change that names an airport whose layout is still loading succeeds at once and
/// leaves the aircraft's layout as it is; the aircraft waits on that airport, and at the second the airport is admitted
/// it re-runs the pick that waited and attaches the result. The wait is snapshotted, so a restore taken while loading
/// comes back detached and attaches at the admission.
/// </summary>
public class PendingLayoutAttachTests
{
    private const string ScenarioJson = """
        {
          "id": "pending-layout-attach",
          "name": "Pending layout attach",
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
            },
            {
              "id": "a2",
              "aircraftId": "N2",
              "aircraftType": "C172",
              "transponderMode": "C",
              "startingConditions": {
                "type": "Coordinates",
                "coordinates": { "lat": 37.82, "lon": -122.28 },
                "altitude": 4500,
                "heading": 90,
                "speed": 100
              },
              "flightplan": {
                "rules": "VFR",
                "departure": "KOAK",
                "destination": "KOAK",
                "cruiseAltitude": 4500,
                "cruiseSpeed": 100,
                "route": "",
                "remarks": "",
                "aircraftType": "C172"
              }
            }
          ]
        }
        """;

    public PendingLayoutAttachTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void SnapshotTakenWhileLoading_RestoresDetached_UntilTheAdmissionRecord()
    {
        var data = new PartlyLoadingGroundData("SMF");
        SimulationEngine live = Load(data);
        Tick(live, 2);
        AircraftState n1 = live.World.FindAircraft("N1")!;
        n1.Ground.Layout = null;
        n1.Ground.LayoutAirportId = null;
        AmendTo(live, "N1", "SMF");
        Assert.Null(n1.Ground.Layout);
        Assert.Equal(["SMF"], n1.Ground.PendingLayoutAirportIds);

        Tick(live, 1);
        StateSnapshotDto atThree = live.CaptureSnapshot();
        Tick(live, 1);
        data.Release("SMF");
        Tick(live, 1);
        Assert.Equal(5, Assert.Single(live.Scenario!.ActionLog.OfType<RecordedLayoutAdmitted>()).ElapsedSeconds);
        Assert.Equal("SMF", AirportOf(n1));

        // The restore's source holds SMF already, as a room's full pin does: the aircraft still comes back detached, not
        // on the primary airport's layout.
        SimulationEngine restored = Load(new PartlyLoadingGroundData());
        restored.RestoreFromSnapshot(atThree);
        AircraftState back = restored.World.FindAircraft("N1")!;
        Assert.Null(back.Ground.Layout);
        Assert.Equal(["SMF"], back.Ground.PendingLayoutAirportIds);

        restored.ArmReplay([.. live.Scenario.ActionLog]);
        restored.ReplayOneSecond();
        Assert.Null(back.Ground.Layout);

        restored.ReplayOneSecond();
        Assert.NotNull(back.Ground.Layout);
        Assert.Same(restored.ResolveAirportLayout("SMF"), back.Ground.Layout);
        Assert.Null(back.Ground.PendingLayoutAirportIds);
    }

    [Fact]
    public void TwoAircraftAtOneAirport_AttachTheSameInstance_InOneSecond_OneRecord()
    {
        var data = new PartlyLoadingGroundData("SMF");
        SimulationEngine engine = Load(data);
        Tick(engine, 1);
        AmendTo(engine, "N1", "SMF");
        AmendTo(engine, "N2", "SMF");
        Tick(engine, 2);

        AircraftState n1 = engine.World.FindAircraft("N1")!;
        AircraftState n2 = engine.World.FindAircraft("N2")!;
        Assert.Equal("OAK", AirportOf(n1));
        Assert.Equal("OAK", AirportOf(n2));

        data.Release("SMF");
        Tick(engine, 1);

        AirportGroundLayout? smf = engine.ResolveAirportLayout("SMF");
        Assert.NotNull(smf);
        Assert.Same(smf, n1.Ground.Layout);
        Assert.Same(smf, n2.Ground.Layout);
        Assert.Equal(4, Assert.Single(engine.Scenario!.ActionLog.OfType<RecordedLayoutAdmitted>()).ElapsedSeconds);
        Assert.Null(n1.Ground.PendingLayoutAirportIds);
        Assert.Null(n2.Ground.PendingLayoutAirportIds);
    }

    [Fact]
    public void AmendTwiceWhileLoading_AttachesTheLatestPick()
    {
        var data = new PartlyLoadingGroundData("SMF", "SJC", "HWD");
        SimulationEngine engine = Load(data);
        Tick(engine, 1);
        AircraftState n1 = engine.World.FindAircraft("N1")!;
        AmendTo(engine, "N1", "SMF");
        AmendTo(engine, "N1", "SJC");
        Assert.Equal(["SJC"], n1.Ground.PendingLayoutAirportIds);

        // The first pick's airport lands: the aircraft no longer waits on it.
        data.Release("SMF");
        Tick(engine, 1);
        Assert.Equal("OAK", AirportOf(n1));
        Assert.Equal(["SJC"], n1.Ground.PendingLayoutAirportIds);

        data.Release("SJC");
        Tick(engine, 1);
        Assert.Equal("SJC", AirportOf(n1));
        Assert.Null(n1.Ground.PendingLayoutAirportIds);

        // An amend back to a loaded airport while another is loading attaches at once and drops the wait.
        AmendTo(engine, "N1", "HWD");
        Assert.Equal(["HWD"], n1.Ground.PendingLayoutAirportIds);
        AmendTo(engine, "N1", "OAK");
        Assert.Equal("OAK", AirportOf(n1));
        Assert.Null(n1.Ground.PendingLayoutAirportIds);
        data.Release("HWD");
        Tick(engine, 1);
        Assert.Equal("OAK", AirportOf(n1));
    }

    [Fact]
    public void ChangeDestinationToALoadingAirport_Succeeds_AndAttachesAtAdmission()
    {
        var data = new PartlyLoadingGroundData("SMF");
        SimulationEngine engine = Load(data);
        Tick(engine, 2);
        AircraftState n1 = engine.World.FindAircraft("N1")!;

        CommandResult result = engine.SendCommand("N1", "APT SMF");

        Assert.True(result.Success, result.Message);
        Assert.Equal("OAK", AirportOf(n1));
        Assert.Equal(["SMF"], n1.Ground.PendingLayoutAirportIds);
        Assert.True(n1.Ground.PendingLayoutForDestination);

        // Departing OAK and near it, the amend's nearer-of pick would answer OAK; the destination change's pick is SMF.
        data.Release("SMF");
        Tick(engine, 1);
        Assert.Equal("SMF", AirportOf(n1));
        Assert.Null(n1.Ground.PendingLayoutAirportIds);
        Assert.False(n1.Ground.PendingLayoutForDestination);
    }

    [Fact]
    public void ReRunPickReadingANewLoadingAirport_DefersItInTheSameSecond_LiveAndReplayAlike()
    {
        // N1's plan names LVK, which has no map; once LVK lands the re-run pick falls through to the aircraft's context
        // airport, SJC, which is still loading and read for the first time inside the admission's attach pass.
        var data = new PartlyLoadingGroundData("LVK", "SJC");
        SimulationEngine live = Load(data);
        live.World.FindAircraft("N1")!.AirportId = "SJC";
        var liveSnapshots = new Dictionary<int, string>();
        for (int t = 1; t <= 6; t++)
        {
            if (t == 4)
            {
                data.Release("LVK");
            }

            live.TickOneSecond();
            if (t == 1)
            {
                // Applied through the body a replay runs, then recorded, as the server applies an amend.
                var amend = new RecordedAmendFlightPlan(live.Scenario!.ElapsedSeconds, "N1", Amendment("LVK"), null);
                CommandResult amended = live.Actions.ApplyRecorded(amend);
                Assert.True(amended.Success, amended.Message);
                live.RecordAction(amend);
            }

            liveSnapshots[t] = SnapshotJson(live);
        }

        List<RecordedAction> actions = [.. live.Scenario!.ActionLog];
        int admittedLvk = actions.FindIndex(a => a is RecordedLayoutAdmitted { AirportId: "LVK", ElapsedSeconds: 4 });
        int deferredSjc = actions.FindIndex(a => a is RecordedLayoutDeferred { AirportId: "SJC", ElapsedSeconds: 4 });
        Assert.True((admittedLvk >= 0) && (deferredSjc > admittedLvk), string.Join(", ", actions));
        Assert.Equal(["SJC"], live.World.FindAircraft("N1")!.Ground.PendingLayoutAirportIds);

        // The replay's source has every airport loaded, as a room's full pin does on a rewind.
        SimulationEngine replay = Load(new PartlyLoadingGroundData());
        replay.World.FindAircraft("N1")!.AirportId = "SJC";
        replay.ArmReplay(actions);
        for (int t = 1; t <= 6; t++)
        {
            replay.ReplayOneSecond();
            Assert.Equal(liveSnapshots[t], SnapshotJson(replay));
        }
    }

    [Fact]
    public void OnTheGroundAtALoadingAirport_WaitsOnlyOnTheAirportItsPickRead()
    {
        var data = new PartlyLoadingGroundData("SMF", "SJC");
        SimulationEngine engine = Load(data);
        Tick(engine, 1);
        AmendTo(engine, "N2", "SJC");
        Assert.Contains("SJC", engine.Scenario!.DeferredLayoutAirports);

        // On the ground at SMF, the pick reads only SMF, never the deferred SJC departure the amend files.
        AircraftState n1 = engine.World.FindAircraft("N1")!;
        n1.IsOnGround = true;
        n1.AirportId = "SMF";
        CommandResult result = engine.AmendFlightPlan("N1", new FlightPlanAmendment(ClearBeaconCode: false, Departure: "SJC"));
        Assert.True(result.Success, result.Message);
        Assert.Equal(["SMF"], n1.Ground.PendingLayoutAirportIds);

        data.Release("SMF");
        Tick(engine, 1);
        Assert.Equal("SMF", AirportOf(n1));
        Assert.Null(n1.Ground.PendingLayoutAirportIds);
    }

    [Fact]
    public void IntentRoundTrips_AndIsOmittedWhenEmpty()
    {
        var empty = new AircraftGroundOps();
        string emptyJson = JsonSerializer.Serialize(empty.ToSnapshot());
        Assert.DoesNotContain("PendingLayout", emptyJson, StringComparison.Ordinal);

        var waiting = new AircraftGroundOps { PendingLayoutAirportIds = ["SJC", "SMF"], PendingLayoutForDestination = true };
        string json = JsonSerializer.Serialize(waiting.ToSnapshot());
        Assert.Contains("PendingLayoutAirportIds", json, StringComparison.Ordinal);

        var back = AircraftGroundOps.FromSnapshot(JsonSerializer.Deserialize<AircraftGroundOpsDto>(json)!, null);
        Assert.Equal(["SJC", "SMF"], back.PendingLayoutAirportIds);
        Assert.True(back.PendingLayoutForDestination);
        Assert.Equal(json, JsonSerializer.Serialize(back.ToSnapshot()));
    }

    private static SimulationEngine Load(PartlyLoadingGroundData data)
    {
        var engine = new SimulationEngine(data, PushPlanStore.CreateShipped());
        engine.LoadScenario(ScenarioJson, 42, MagneticDeclination.EvaluationDateUtc);
        Assert.NotNull(engine.World.FindAircraft("N1"));
        Assert.NotNull(engine.World.FindAircraft("N2"));
        return engine;
    }

    private static void AmendTo(SimulationEngine engine, string callsign, string airport)
    {
        CommandResult result = engine.AmendFlightPlan(callsign, Amendment(airport));
        Assert.True(result.Success, result.Message);
    }

    private static FlightPlanAmendment Amendment(string airport) => new(ClearBeaconCode: false, Departure: airport, Destination: airport);

    private static string? AirportOf(AircraftState aircraft) =>
        aircraft.Ground.Layout is { } layout ? NavigationDatabase.NormalizeAirport(layout.AirportId) : null;

    private static string SnapshotJson(SimulationEngine engine) => JsonSerializer.Serialize(engine.CaptureSnapshot());

    private static void Tick(SimulationEngine engine, int seconds)
    {
        for (int i = 0; i < seconds; i++)
        {
            engine.TickOneSecond();
        }
    }
}
