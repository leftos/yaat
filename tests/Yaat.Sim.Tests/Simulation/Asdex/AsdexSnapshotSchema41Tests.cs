using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Asdex;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.ControllerAi;

namespace Yaat.Sim.Tests.Simulation.Asdex;

/// <summary>
/// Snapshot schema 41: the taxi-onto-active-runway alert kind (stored as 2) is gone, the alert texts became vNAS's and
/// alerts carry GoAround. A schema-40 standing alert of the retired kind restores as an occupied-runway alert, every
/// stored alert's third line moves to the current text, and other kinds are left alone.
/// </summary>
public class AsdexSnapshotSchema41Tests
{
    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public AsdexSnapshotSchema41Tests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>An OAK engine with a closed 28R and one aircraft rolling at 80 kt down it, its closed-runway alert standing.</summary>
    private SimulationEngine EngineWithAStandingAlert()
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
        ac.Position = GeoMath.ProjectPoint(
            new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
            runway.TrueHeading,
            1500 / GeoMath.FeetPerNm
        );
        ac.TrueHeading = runway.TrueHeading;
        ac.TrueTrack = runway.TrueHeading;
        ac.IsOnGround = true;
        ac.Altitude = runway.ElevationFt;
        ac.IndicatedAirspeed = 80;
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
                new AsdexSafetyLogicConfig([new AsdexRunwayConfig("28R", area, IsClosed: true)], "OAKCFG", [], RunwayConfigurationChosen: false)
            )
        );
        engine.TickAsdexAlerts(new Helpers.SpineCapturingHost(engine));
        Assert.Single(engine.Scenario!.ActiveAsdexAlerts["OAK"].Keys);
        return engine;
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
    private static JsonObject LegacyAlert(string id, int kind, string runway, string[] callsigns, string text) =>
        new()
        {
            ["FacilityId"] = "OAK",
            ["Id"] = id,
            ["Kind"] = kind,
            ["RunwayIds"] = new JsonArray(runway),
            ["Callsigns"] = new JsonArray([.. callsigns.Select(callsign => JsonValue.Create(callsign))]),
            ["MessageLines"] = new JsonArray(runway, string.Join(" ", callsigns), text),
            ["PlayAuralAlert"] = true,
        };

    [Fact]
    public void V40SnapshotWithTaxiOntoActiveRunwayAlert_RestoresAsOccupiedRunway()
    {
        SimulationEngine engine = EngineWithAStandingAlert();
        JsonObject root = JsonNode.Parse(JsonSerializer.Serialize(engine.CaptureSnapshot()))!.AsObject();
        Assert.Equal(JsonValueKind.Number, root["Scenario"]!["ActiveAsdexAlerts"]![0]!["Kind"]!.GetValueKind());
        StateSnapshotDto legacy = AsSchema40(engine, LegacyAlert("TaxiOntoActiveRunway|28R|AAL1,GND3", 2, "28R", ["AAL1", "GND3"], "RWY INCURSION"));

        engine.RestoreFromSnapshot(legacy);

        AsdexSafetyAlert restored = Assert.Single(engine.Scenario!.ActiveAsdexAlerts["OAK"].Values);
        Assert.Equal("TaxiOntoActiveRunway|28R|AAL1,GND3", restored.Id);
        Assert.Equal(AsdexAlertKind.OccupiedRunway, restored.Kind);
        Assert.Equal("RWY OCCUPIED", restored.MessageLines[2]);
        Assert.False(restored.GoAround);
    }

    /// <summary>
    /// Kinds 0, 1 and 3 keep their kind and id; each runway alert's old third line becomes the current text, a
    /// taxiway-landing text is kept, and migrating the result again changes nothing.
    /// </summary>
    [Fact]
    public void V40Snapshot_RewritesOldTexts_LeavesOtherKinds_AndMigratingTwiceChangesNothing()
    {
        SimulationEngine engine = EngineWithAStandingAlert();
        StateSnapshotDto snapshot = AsSchema40(
            engine,
            LegacyAlert("ClosedRunway|28R|UAL1", 0, "28R", ["UAL1"], "CLOSED RWY"),
            LegacyAlert("OccupiedRunway|28R|AAL1,UAL2", 1, "28R", ["AAL1", "UAL2"], "OCCUPIED RWY"),
            LegacyAlert("TWYLAND|A|SKW3", 3, "A", ["SKW3"], "TAXIWAY LANDING")
        );

        SnapshotSchemaMigrator.Migrate(snapshot);

        List<AsdexSafetyAlertDto> alerts = snapshot.Scenario.ActiveAsdexAlerts!;
        Assert.Equal(SnapshotSchemaMigrator.CurrentSchemaVersion, snapshot.SchemaVersion);
        Assert.Equal(["ClosedRunway|28R|UAL1", "OccupiedRunway|28R|AAL1,UAL2", "TWYLAND|A|SKW3"], alerts.Select(a => a.Id));
        Assert.Equal([AsdexAlertKind.ClosedRunway, AsdexAlertKind.OccupiedRunway, AsdexAlertKind.TaxiwayLanding], alerts.Select(a => a.Kind));
        Assert.Equal(["RWY CLOSED", "RWY OCCUPIED", "TAXIWAY LANDING"], alerts.Select(a => a.MessageLines[2]));
        Assert.All(alerts, a => Assert.False(a.GoAround));

        // Reset the version so the second pass runs the V40→V41 step itself again rather than skipping it.
        string once = JsonSerializer.Serialize(snapshot.Scenario.ActiveAsdexAlerts);
        snapshot.SchemaVersion = 40;
        SnapshotSchemaMigrator.Migrate(snapshot);
        Assert.Equal(once, JsonSerializer.Serialize(snapshot.Scenario.ActiveAsdexAlerts));
    }
}
