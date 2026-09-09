using System.Text.Json;
using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Soak;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Actions;

/// <summary>
/// The <c>ADD</c> and <c>TAXIALL</c> arms. An ADD is derived on every run kind — the generator and the CID draw from
/// the shared RNG and the beacon pool exactly as live did, so both advance in lockstep — and the snapshot the live run
/// baked onto the record is the authority when the derivation disagrees with it. A TAXIALL taxis every aircraft at
/// parking through the dispatcher's TAXI arm.
/// </summary>
public class AddAndTaxiAllArmTests
{
    private const string Add = "ADD V S P @NEW1";

    /// <summary>Two aircraft at KOAK parking positions half a mile and more from the 28R hold shorts.</summary>
    private const string ParkedPairAtOak = """
        {
          "id": "taxiall-pair",
          "name": "Two parked at OAK",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "aircraft": [
            {
              "id": "a1",
              "aircraftId": "N152SP",
              "aircraftType": "C172",
              "transponderMode": "C",
              "startingConditions": { "type": "Parking", "parking": "SIG1" },
              "flightplan": { "rules": "VFR", "departure": "KOAK", "destination": "KOAK", "cruiseAltitude": 1500, "cruiseSpeed": 100, "route": "", "remarks": "", "aircraftType": "C172" }
            },
            {
              "id": "a2",
              "aircraftId": "N456TS",
              "aircraftType": "C172",
              "transponderMode": "C",
              "startingConditions": { "type": "Parking", "parking": "NEW7" },
              "flightplan": { "rules": "VFR", "departure": "KOAK", "destination": "KOAK", "cruiseAltitude": 1500, "cruiseSpeed": 100, "route": "", "remarks": "", "aircraftType": "C172" }
            }
          ]
        }
        """;

    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public AddAndTaxiAllArmTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private SimulationEngine? Engine() => _zoa is null ? null : AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);

    private static ActionInput Fresh(string command) => new("", command, "conn-1", "XX", Baked: null);

    private static RecordedCommand Recorded(string command) => new(0, "", command, "XX", "conn-1");

    private static string Json(AircraftSnapshotDto dto) => JsonSerializer.Serialize(dto);

    [Fact]
    public void Issue_SpawnsTheAircraft_AndBakesItsSnapshotOntoTheRecord()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var outcome = engine.Actions.Issue(Fresh(Add));

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        Assert.Equal(new ActionTrace(RecordedCommandKind.AddAircraft, ActionScope.Global), outcome.Trace);
        var spawned = Assert.Single(engine.World.GetSnapshot(), ac => ac.Callsign != AiTestFixture.Callsign);
        Assert.IsType<AtParkingPhase>(spawned.Phases?.CurrentPhase);
        Assert.Contains(spawned.Callsign, outcome.Result.Message);
        var record = Assert.IsType<RecordedCommand>(Assert.Single(engine.Scenario!.ActionLog));
        Assert.NotNull(record.SpawnedAircraft);
        Assert.Equal(Json(spawned.ToSnapshot()), Json(record.SpawnedAircraft));
    }

    [Fact]
    public void Apply_DerivesTheSameAircraft_AndAdvancesTheSharedRngAndBeaconPool_WithoutABakedSnapshot()
    {
        if (Engine() is not { } live || Engine() is not { } replay)
        {
            return;
        }

        var record = live.Actions.Issue(Fresh(Add)).ToRecord!;
        var callsign = record.SpawnedAircraft!.Callsign;

        var outcome = replay.Actions.Apply(record with { SpawnedAircraft = null });

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        var replayed = replay.FindAircraft(callsign);
        Assert.NotNull(replayed);
        Assert.Equal(Json(live.FindAircraft(callsign)!.ToSnapshot()), Json(replayed.ToSnapshot()));
        Assert.Equal(live.World.Rng.GetState(), replay.World.Rng.GetState());
        Assert.Equal(live.BeaconCodePool.NextCandidate, replay.BeaconCodePool.NextCandidate);
        Assert.Empty(replay.Scenario!.ActionLog);
    }

    [Fact]
    public void Apply_TheBakedSnapshotWins_WhenTheDerivationDisagrees_AndReservesItsBeacon()
    {
        using var tap = new CapturingSimLogProvider(LogLevel.Warning, 100);
        using var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(tap));
        SimLog.InitializeForTest(factory);

        if (Engine() is not { } live || Engine() is not { } replay)
        {
            return;
        }

        var record = live.Actions.Issue(Fresh(Add)).ToRecord!;
        var derived = record.SpawnedAircraft!;
        // What the live session would have banked had its world differed: another callsign and beacon code.
        var recordedAircraft = AircraftState.FromSnapshot(derived, live.World.GroundLayout);
        recordedAircraft.Callsign = "REC1";
        recordedAircraft.Transponder.AssignCode(4321, null, null);
        var recorded = recordedAircraft.ToSnapshot();

        var outcome = replay.Actions.Apply(record with { SpawnedAircraft = recorded });

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        Assert.Equal(Json(recorded), Json(replay.FindAircraft("REC1")!.ToSnapshot()));
        Assert.Null(replay.FindAircraft(derived.Callsign));
        Assert.True(replay.BeaconCodePool.IsAssigned(4321));
        Assert.False(replay.BeaconCodePool.IsAssigned(derived.Transponder.AssignedCode));
        // The derivation still ran, so the shared RNG stands where live's did.
        Assert.Equal(live.World.Rng.GetState(), replay.World.Rng.GetState());
        var warning = Assert.Single(tap.Drain(), r => r.Message.Contains("replay-fidelity", StringComparison.Ordinal));
        Assert.Contains("REC1", warning.Message);
    }

    [Fact]
    public void Apply_TheBakedSnapshotWins_WhenTheDerivationProducesNothing()
    {
        using var tap = new CapturingSimLogProvider(LogLevel.Warning, 100);
        using var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(tap));
        SimLog.InitializeForTest(factory);

        if (Engine() is not { } live || Engine() is not { } replay)
        {
            return;
        }

        var record = live.Actions.Issue(Fresh(Add)).ToRecord!;
        var recorded = record.SpawnedAircraft!;

        // The same record against a layout where its parking no longer resolves: the generator refuses, the recording still holds the aircraft.
        var outcome = replay.Actions.Apply(record with { Command = "ADD V S P @NOSUCHSPOT" });

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        var spawned = Assert.Single(replay.World.GetSnapshot(), ac => ac.Callsign != AiTestFixture.Callsign);
        Assert.Equal(Json(recorded), Json(spawned.ToSnapshot()));
        var warning = Assert.Single(tap.Drain(), r => r.Message.Contains("replay-fidelity", StringComparison.Ordinal));
        Assert.Contains("derived no aircraft", warning.Message);
    }

    /// <summary>
    /// A TAXIALL to a runway routes every parked aircraft to it. 7110.65 3-7-2 wants a specific route in a taxi
    /// clearance, so the bulk verb auto-routes each aircraft (TAXIAUTO) rather than issuing the route-less TAXI
    /// whose adjacent-only rule (issue #393) only ever moved the aircraft already standing at the bar.
    /// </summary>
    [Fact]
    public void Apply_TaxiAllToARunway_AutoRoutesEveryParkedAircraft()
    {
        if (_zoa is null)
        {
            return;
        }

        var engine = AiTestFixture.Load(ParkedPairAtOak, _zoa, 7, []);
        var layout = new TestAirportGroundData().GetLayout("OAK");
        Assert.NotNull(layout);
        AddParkedAtTheBar(engine, layout, "N789AB", "28R");

        var outcome = engine.Actions.Apply(Recorded("TAXIALL 28R"));

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        Assert.Equal("TAXIALL: 3 aircraft taxied", outcome.Result.Message);
        // The two parked across the field are routed to the runway; the one already at the bar keeps the
        // route-less "you are there" resolution it always had — both end at a 28R destination hold short.
        foreach (var callsign in new[] { "N152SP", "N456TS" })
        {
            var route = AssertTaxiingToRunway(engine, callsign, "28R");
            Assert.True(route.Segments.Count > 0, $"{callsign} was cleared to 28R with no route");
        }

        Assert.Empty(AssertTaxiingToRunway(engine, "N789AB", "28R").Segments);
    }

    /// <summary>The aircraft's taxi route, asserting it is taxiing and its route ends at a <paramref name="runway"/> hold short.</summary>
    private static TaxiRoute AssertTaxiingToRunway(SimulationEngine engine, string callsign, string runway)
    {
        var aircraft = engine.FindAircraft(callsign);
        Assert.NotNull(aircraft);
        Assert.IsType<TaxiingPhase>(aircraft.Phases?.CurrentPhase);
        var route = aircraft.Ground.AssignedTaxiRoute;
        Assert.True(route is not null, $"{callsign} got no taxi route");
        Assert.Contains(
            route!.HoldShortPoints,
            h =>
                (h.Reason == HoldShortReason.DestinationRunway) && (h.TargetName is not null) && RunwayIdentifier.Parse(h.TargetName).Contains(runway)
        );
        return route;
    }

    /// <summary>
    /// Adds a parked aircraft at the taxiway node next to a <paramref name="runway"/> hold short, on the side away
    /// from the pavement — an aircraft standing AT the bar. The auto-route from there snaps to the bar itself, so
    /// its route is the same route-less "you are already there" resolution the adjacent-only TAXI produced.
    /// </summary>
    private static void AddParkedAtTheBar(SimulationEngine engine, AirportGroundLayout layout, string callsign, string runway)
    {
        var pavement = layout.FindRunway(runway);
        Assert.NotNull(pavement);
        var centerline = pavement.Coordinates.Select(c => new LatLon(c.Lat, c.Lon)).ToList();
        double ToCenterlineNm(LatLon p) => centerline.Min(c => GeoMath.DistanceNm(p, c));

        var bar = layout.GetRunwayHoldShortNodes(runway).First();
        var behind = bar.Edges.Where(e => !e.IsRunwayCenterline).Select(e => e.OtherNode(bar)).MaxBy(n => ToCenterlineNm(n.Position));
        Assert.NotNull(behind);

        var aircraft = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "C172",
            Position = behind.Position,
            TrueHeading = new TrueHeading(280),
            Altitude = 6,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "KOAK" },
        };
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new AtParkingPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));
        engine.World.AddAircraft(aircraft);
    }

    [Fact]
    public void Apply_TaxiAll_TaxisEveryAircraftAtParking()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var parked = engine.FindAircraft(AiTestFixture.Callsign)!;
        Assert.IsType<AtParkingPhase>(parked.Phases?.CurrentPhase);

        // A parking destination: the empty-path TAXI a TAXIALL issues routes to a parking from anywhere, while a bare
        // runway destination is adjacent-only (issue #393) and SIG1 is not adjacent to any OAK runway.
        var outcome = engine.Actions.Apply(Recorded("TAXIALL @NEW1"));

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        Assert.Equal("TAXIALL: 1 aircraft taxied", outcome.Result.Message);
        Assert.Equal(new ActionTrace(RecordedCommandKind.TaxiAll, ActionScope.Global), outcome.Trace);
        Assert.IsType<TaxiingPhase>(parked.Phases?.CurrentPhase);
        Assert.Empty(engine.Scenario!.ActionLog);
    }
}
