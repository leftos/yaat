using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Asdex;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.ControllerAi;

namespace Yaat.Sim.Tests.Simulation.Asdex;

/// <summary>
/// Snapshot schema 41: the taxi-onto-active-runway alert kind (stored as 2) is gone, the alert texts became vNAS's and
/// alerts carry GoAround. Every standing alert of a schema-40 snapshot is dropped, whatever its kind, since its stored
/// lines cannot be rebuilt from the snapshot: the next tick raises each one again from live state under the same id in
/// the current text, at the cost of one extra aural.
/// </summary>
public class AsdexSnapshotSchema41Tests
{
    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public AsdexSnapshotSchema41Tests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>
    /// An OAK engine with a closed 28R and one aircraft using it, its closed-runway alert standing: rolling at 80 kt down
    /// the runway, or, with <paramref name="airborneArrival"/>, an arrival 3,000 ft before the threshold at 150 ft, inside
    /// the runway's arrival box, which the detector alerts on with GO AROUND.
    /// </summary>
    private SimulationEngine EngineWithAStandingAlert(bool airborneArrival)
    {
        if (_zoa is null)
        {
            Assert.Skip("The ZOA ARTCC config is not available");
        }

        SimulationEngine engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);
        AircraftState ac = engine.World.GetSnapshot()[0];
        RunwayInfo runway =
            NavigationDatabase.Instance.GetRunway("OAK", "28R")
            ?? NavigationDatabase.Instance.GetRunway("KOAK", "28R")
            ?? throw new InvalidOperationException("OAK 28R is not in navdata");
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        ac.Position = airborneArrival
            ? GeoMath.ProjectPoint(threshold, new TrueHeading(runway.TrueHeading.Degrees + 180), 3000 / GeoMath.FeetPerNm)
            : GeoMath.ProjectPoint(threshold, runway.TrueHeading, 1500 / GeoMath.FeetPerNm);
        ac.TrueHeading = runway.TrueHeading;
        ac.TrueTrack = runway.TrueHeading;
        ac.IsOnGround = !airborneArrival;
        ac.Altitude = airborneArrival ? runway.ElevationFt + 150 : runway.ElevationFt;
        ac.IndicatedAirspeed = airborneArrival ? 130 : 80;
        const double Half = 0.002;
        LatLon[] area =
        [
            new(ac.Position.Lat - Half, ac.Position.Lon - Half),
            new(ac.Position.Lat + Half, ac.Position.Lon - Half),
            new(ac.Position.Lat + Half, ac.Position.Lon + Half),
            new(ac.Position.Lat - Half, ac.Position.Lon + Half),
        ];
        engine.ApplyRecordedAsdexSafetyLogic(
            new RecordedAsdexSafetyLogicChange(
                0,
                "OAK",
                new AsdexSafetyLogicConfig(
                    [new AsdexRunwayConfig("28R", area, IsClosed: true)],
                    "OAKCFG",
                    [],
                    RunwayConfigurationChosen: false,
                    AsdexSafetyLogicConfig.NoSeconds,
                    AsdexSafetyLogicConfig.NoSeconds
                )
            )
        );
        engine.TickAsdexAlerts(new Helpers.SpineCapturingHost(engine));
        Assert.Single(engine.Scenario!.ActiveAsdexAlerts["OAK"].Keys);
        return engine;
    }

    /// <summary>The facility the occupied-runway fixture runs at; the ARTCC config below names its runway configuration.</summary>
    private const string Sfo = "SFO";

    /// <summary>ZOA's SFO "L28D01" runway configuration, the one that lifts the LIMITED gate on open-runway alerts.</summary>
    private const string SfoL28D01 = "01GVF4AGA8HX03NSN8QJS4WJS4";

    private const string TwoAtSfo = """
        {
          "id": "asdex-v41-restore", "name": "ASDE-X V41 restore at SFO", "artccId": "ZOA", "primaryAirportId": "SFO",
          "aircraft": [
            {
              "id": "a1", "aircraftId": "AAL1", "aircraftType": "B738", "transponderMode": "C",
              "startingConditions": {
                "type": "Coordinates", "coordinates": { "lat": 37.9, "lon": -122.6 }, "altitude": 5000, "heading": 280, "speed": 250
              },
              "flightplan": {
                "rules": "IFR", "departure": "KSFO", "destination": "KLAX", "cruiseAltitude": 35000, "cruiseSpeed": 450,
                "route": "", "remarks": "", "aircraftType": "B738"
              }
            },
            {
              "id": "a2", "aircraftId": "UAL2", "aircraftType": "B738", "transponderMode": "C",
              "startingConditions": {
                "type": "Coordinates", "coordinates": { "lat": 37.95, "lon": -122.6 }, "altitude": 6000, "heading": 280, "speed": 250
              },
              "flightplan": {
                "rules": "IFR", "departure": "KSFO", "destination": "KLAX", "cruiseAltitude": 35000, "cruiseSpeed": 450,
                "route": "", "remarks": "", "aircraftType": "B738"
              }
            }
          ]
        }
        """;

    /// <summary>
    /// An SFO engine on the L28D01 runway configuration with 28L open and the pair the occupied-runway alert is raised
    /// for: UAL2 stopped 3,600 ft down the runway, AAL1 an airborne arrival 300 ft past the threshold at 40 ft, inside its
    /// arrival box. Only a configuration the ARTCC names lifts the LIMITED gate; OAK's config names none.
    /// </summary>
    private static SimulationEngine EngineWithAnOccupiedRunwayPair()
    {
        SimulationEngine engine = AiTestFixture.Load(TwoAtSfo, ZoaArtccConfig.Full, 7, []);
        AircraftState arrival = engine.World.FindAircraft("AAL1")!;
        AircraftState occupant = engine.World.FindAircraft("UAL2")!;
        RunwayInfo runway =
            NavigationDatabase.Instance.GetRunway(Sfo, "28L")
            ?? NavigationDatabase.Instance.GetRunway("K" + Sfo, "28L")
            ?? throw new InvalidOperationException($"{Sfo} 28L is not in navdata");
        Place(arrival, runway, 300, airborne: true, heightFt: 40, speedKts: 135);
        Place(occupant, runway, 3600, airborne: false, heightFt: 0, speedKts: 0);
        arrival.Phases = new PhaseList();
        arrival.Phases.Add(new FinalApproachPhase());
        engine.ApplyRecordedAsdexSafetyLogic(
            new RecordedAsdexSafetyLogicChange(
                0,
                Sfo,
                new AsdexSafetyLogicConfig(
                    [new AsdexRunwayConfig("28L", Envelope(arrival.Position, occupant.Position), IsClosed: false)],
                    SfoL28D01,
                    [],
                    RunwayConfigurationChosen: false,
                    AsdexSafetyLogicConfig.NoSeconds,
                    AsdexSafetyLogicConfig.NoSeconds
                )
            )
        );
        return engine;
    }

    /// <summary>
    /// Puts <paramref name="aircraft"/> on <paramref name="runway"/>'s centreline <paramref name="alongFt"/> past its
    /// threshold, heading and tracking down the runway, <paramref name="heightFt"/> above it when airborne.
    /// </summary>
    private static void Place(AircraftState aircraft, RunwayInfo runway, double alongFt, bool airborne, double heightFt, double speedKts)
    {
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        aircraft.Position = GeoMath.ProjectPoint(threshold, runway.TrueHeading, alongFt / GeoMath.FeetPerNm);
        aircraft.TrueHeading = runway.TrueHeading;
        aircraft.TrueTrack = runway.TrueHeading;
        aircraft.IsOnGround = !airborne;
        aircraft.Altitude = runway.ElevationFt + (airborne ? heightFt : 0);
        aircraft.IndicatedAirspeed = speedKts;
    }

    /// <summary>A footprint ring with a margin of slack around both positions, wide enough to hold the pair.</summary>
    private static LatLon[] Envelope(LatLon first, LatLon second)
    {
        const double Margin = 0.002;
        double minLat = Math.Min(first.Lat, second.Lat) - Margin;
        double maxLat = Math.Max(first.Lat, second.Lat) + Margin;
        double minLon = Math.Min(first.Lon, second.Lon) - Margin;
        double maxLon = Math.Max(first.Lon, second.Lon) + Margin;
        return [new(minLat, minLon), new(minLat, maxLon), new(maxLat, maxLon), new(maxLat, minLon)];
    }

    /// <summary>The engine's snapshot rewritten to schema 40, its standing alerts replaced by <paramref name="alerts"/>.</summary>
    private static StateSnapshotDto AsSchema40(SimulationEngine engine, params JsonObject[] alerts)
    {
        JsonObject root = JsonNode.Parse(JsonSerializer.Serialize(engine.CaptureSnapshot()))!.AsObject();
        root["SchemaVersion"] = 40;
        root["Scenario"]!["ActiveAsdexAlerts"] = new JsonArray([.. alerts]);
        return JsonSerializer.Deserialize<StateSnapshotDto>(root.ToJsonString())!;
    }

    /// <summary>A stored alert in the pre-V41 shape: no GoAround, the old texts.</summary>
    private static JsonObject LegacyAlert(string facility, string id, int kind, string runway, string[] callsigns, string text) =>
        new()
        {
            ["FacilityId"] = facility,
            ["Id"] = id,
            ["Kind"] = kind,
            ["RunwayIds"] = new JsonArray(runway),
            ["Callsigns"] = new JsonArray([.. callsigns.Select(callsign => JsonValue.Create(callsign))]),
            ["MessageLines"] = new JsonArray(runway, string.Join(" ", callsigns), text),
            ["PlayAuralAlert"] = true,
        };

    /// <summary>
    /// A pre-V41 standing occupied-runway alert carries the old lines: the runway without its "RWY " prefix, the
    /// callsigns space-separated, the old text. Dropped on restore, the next tick — the same pair still on the runway —
    /// raises it again in the current build's text instead of leaving the old lines standing under its id.
    /// </summary>
    [Fact]
    public void V40SnapshotWithStandingOccupiedRunwayAlert_IsDropped_AndNextTickRaisesItInTheCurrentText()
    {
        SimulationEngine engine = EngineWithAnOccupiedRunwayPair();
        StateSnapshotDto snapshot = AsSchema40(engine, LegacyAlert(Sfo, "OccupiedRunway|28L|AAL1,UAL2", 1, "28L", ["AAL1", "UAL2"], "OCCUPIED RWY"));

        engine.RestoreFromSnapshot(snapshot);

        Assert.Empty(engine.Scenario!.ActiveAsdexAlerts.GetValueOrDefault(Sfo, SimScenarioState.NoAsdexAlerts));

        engine.TickAsdexAlerts(new Helpers.SpineCapturingHost(engine));

        AsdexSafetyAlert alert = Assert.Single(engine.Scenario.ActiveAsdexAlerts.GetValueOrDefault(Sfo, SimScenarioState.NoAsdexAlerts).Values);
        Assert.Equal(AsdexAlertKind.OccupiedRunway, alert.Kind);
        Assert.Equal(["RWY 28L", "AAL1, UAL2", "RWY OCCUPIED"], alert.MessageLines);
    }

    /// <summary>A standing pre-V41 alert of the retired taxi-onto-active-runway kind is dropped like every other kind.</summary>
    [Fact]
    public void V40SnapshotWithTaxiOntoActiveRunwayAlert_IsDropped()
    {
        SimulationEngine engine = EngineWithAStandingAlert(airborneArrival: false);
        StateSnapshotDto legacy = AsSchema40(
            engine,
            LegacyAlert("OAK", "TaxiOntoActiveRunway|28R|AAL1,GND3", 2, "28R", ["AAL1", "GND3"], "RWY INCURSION")
        );

        engine.RestoreFromSnapshot(legacy);

        Assert.Empty(engine.Scenario!.ActiveAsdexAlerts.GetValueOrDefault("OAK", SimScenarioState.NoAsdexAlerts));
    }

    /// <summary>
    /// Every standing alert of a schema-40 snapshot is dropped, whatever its kind, and migrating the dropped set again
    /// changes nothing.
    /// </summary>
    [Fact]
    public void V40Snapshot_DropsEveryStandingAlert_AndMigratingTwiceChangesNothing()
    {
        SimulationEngine engine = EngineWithAStandingAlert(airborneArrival: false);
        StateSnapshotDto snapshot = AsSchema40(
            engine,
            LegacyAlert("OAK", "ClosedRunway|28R|UAL1", 0, "28R", ["UAL1"], "CLOSED RWY"),
            LegacyAlert("OAK", "OccupiedRunway|28R|AAL1,UAL2", 1, "28R", ["AAL1", "UAL2"], "OCCUPIED RWY"),
            LegacyAlert("OAK", "TWYLAND|A|SKW3", 3, "A", ["SKW3"], "TAXIWAY LANDING")
        );

        SnapshotSchemaMigrator.Migrate(snapshot);

        Assert.Equal(SnapshotSchemaMigrator.CurrentSchemaVersion, snapshot.SchemaVersion);
        Assert.Empty(snapshot.Scenario.ActiveAsdexAlerts!);

        // Reset the version so the second pass runs the V40→V41 step itself again rather than skipping it.
        snapshot.SchemaVersion = 40;
        SnapshotSchemaMigrator.Migrate(snapshot);
        Assert.Empty(snapshot.Scenario.ActiveAsdexAlerts!);
    }

    /// <summary>
    /// A pre-V41 closed-runway alert for an arrival is dropped rather than restored with the plain text and GoAround
    /// false; the first tick after the restore raises it again with GO AROUND, since the detector reads the aircraft's
    /// arrival box.
    /// </summary>
    [Fact]
    public void V40SnapshotWithStandingClosedRunwayArrivalAlert_RestoresThenNextTick_ShowsGoAround()
    {
        SimulationEngine engine = EngineWithAStandingAlert(airborneArrival: true);
        StateSnapshotDto snapshot = AsSchema40(
            engine,
            LegacyAlert("OAK", $"ClosedRunway|28R|{AiTestFixture.Callsign}", 0, "28R", [AiTestFixture.Callsign], "CLOSED RWY")
        );

        engine.RestoreFromSnapshot(snapshot);

        Assert.Empty(engine.Scenario!.ActiveAsdexAlerts.GetValueOrDefault("OAK", SimScenarioState.NoAsdexAlerts));

        engine.TickAsdexAlerts(new Helpers.SpineCapturingHost(engine));

        AsdexSafetyAlert alert = Assert.Single(engine.Scenario.ActiveAsdexAlerts.GetValueOrDefault("OAK", SimScenarioState.NoAsdexAlerts).Values);
        Assert.Equal(AsdexAlertKind.ClosedRunway, alert.Kind);
        Assert.Equal(AsdexSafetyLogicDetector.ClosedRunwayGoAroundText, alert.MessageLines[2]);
        Assert.True(alert.GoAround);
    }

    /// <summary>
    /// The same drop for a pre-V41 closed-runway alert on a departure: the first tick after the restore raises it again
    /// with the plain text and no GO AROUND.
    /// </summary>
    [Fact]
    public void V40SnapshotWithStandingClosedRunwayDepartureAlert_RestoresThenNextTick_ShowsRwyClosed()
    {
        SimulationEngine engine = EngineWithAStandingAlert(airborneArrival: false);
        StateSnapshotDto snapshot = AsSchema40(
            engine,
            LegacyAlert("OAK", $"ClosedRunway|28R|{AiTestFixture.Callsign}", 0, "28R", [AiTestFixture.Callsign], "CLOSED RWY")
        );

        engine.RestoreFromSnapshot(snapshot);
        engine.TickAsdexAlerts(new Helpers.SpineCapturingHost(engine));

        AsdexSafetyAlert alert = Assert.Single(engine.Scenario!.ActiveAsdexAlerts.GetValueOrDefault("OAK", SimScenarioState.NoAsdexAlerts).Values);
        Assert.Equal(AsdexAlertKind.ClosedRunway, alert.Kind);
        Assert.Equal(AsdexSafetyLogicDetector.ClosedRunwayText, alert.MessageLines[2]);
        Assert.False(alert.GoAround);
    }
}
