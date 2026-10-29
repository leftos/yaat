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
/// Snapshot schema 36: the safety-logic configuration and the standing alerts are kept per facility. A two-facility
/// picture round-trips through the recording serializer, and a schema-35 snapshot's single configuration and its alerts
/// are filed under the primary airport's facility — or dropped when the snapshot names no primary airport.
/// </summary>
public class AsdexSnapshotSchema36Tests
{
    private static readonly IReadOnlyList<LatLon> SharedArea =
    [
        new(37.7208, -122.2215),
        new(37.7218, -122.2215),
        new(37.7218, -122.2201),
        new(37.7208, -122.2201),
    ];

    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public AsdexSnapshotSchema36Tests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static AsdexSafetyLogicConfig Closed(string runwayId, string configurationId, IReadOnlyList<LatLon> area) =>
        new(
            [new AsdexRunwayConfig(runwayId, area, IsClosed: true)],
            configurationId,
            [$"{configurationId}_TWR"],
            RunwayConfigurationChosen: false,
            AsdexSafetyLogicConfig.NoSeconds,
            AsdexSafetyLogicConfig.NoSeconds
        );

    /// <summary>
    /// An engine with OAK and SFO each configuring a closed 28R and both alerts standing: OAK's raised by the one aircraft
    /// rolling at 80 kt down OAK's real 28R, SFO's the same alert filed under SFO.
    /// </summary>
    private SimulationEngine? TwoFacilityEngine()
    {
        if (_zoa is null)
        {
            return null;
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
        LatLon[] oakArea =
        [
            new(ac.Position.Lat - Half, ac.Position.Lon - Half),
            new(ac.Position.Lat + Half, ac.Position.Lon - Half),
            new(ac.Position.Lat + Half, ac.Position.Lon + Half),
            new(ac.Position.Lat - Half, ac.Position.Lon + Half),
        ];
        engine.ApplyRecordedAsdexSafetyLogic(new RecordedAsdexSafetyLogicChange(0, "OAK", Closed("28R", "OAKCFG", oakArea)));
        engine.ApplyRecordedAsdexSafetyLogic(new RecordedAsdexSafetyLogicChange(0, "SFO", Closed("28R", "SFOCFG", SharedArea)));
        engine.TickAsdexAlerts(new Helpers.SpineCapturingHost(engine));
        // Set directly: no aircraft can use OAK's and SFO's 28R at once, and these tests cover the snapshot, not detection.
        engine.Scenario!.ActiveAsdexAlerts = engine.Scenario.ActiveAsdexAlerts.SetItem("SFO", engine.Scenario.ActiveAsdexAlerts["OAK"]);
        Assert.Equal(["OAK", "SFO"], engine.Scenario!.ActiveAsdexAlerts.Keys);
        return engine;
    }

    [Fact]
    public void ATwoFacilityConfigAndAlertMap_RoundTripsThroughASnapshot()
    {
        if (TwoFacilityEngine() is not { } engine)
        {
            return;
        }

        string oakAlert = Assert.Single(engine.Scenario!.ActiveAsdexAlerts["OAK"].Keys);
        StateSnapshotDto reread = JsonSerializer.Deserialize<StateSnapshotDto>(JsonSerializer.Serialize(engine.CaptureSnapshot()))!;

        engine.Scenario.AsdexSafetyLogicConfigs = AsdexSafetyLogicSeed.Empty;
        engine.Scenario.ActiveAsdexAlerts = engine.Scenario.ActiveAsdexAlerts.Clear();
        engine.RestoreFromSnapshot(reread);

        Assert.Equal(["OAK", "SFO"], engine.Scenario!.AsdexSafetyLogicConfigs.Keys);
        Assert.Equal("OAKCFG", engine.Scenario.AsdexSafetyLogicConfigs["OAK"].RunwayConfigurationId);
        Assert.Equal("SFOCFG", engine.Scenario.AsdexSafetyLogicConfigs["SFO"].RunwayConfigurationId);
        Assert.Equal(SharedArea, Assert.Single(engine.Scenario.AsdexSafetyLogicConfigs["SFO"].Runways).AreaPoints);
        Assert.Equal(["OAK", "SFO"], engine.Scenario.ActiveAsdexAlerts.Keys);
        Assert.Equal([oakAlert], engine.Scenario.ActiveAsdexAlerts["OAK"].Keys);
        Assert.Equal([oakAlert], engine.Scenario.ActiveAsdexAlerts["SFO"].Keys);
    }

    /// <summary>
    /// Rewrites a current snapshot into the schema-35 shape: one scenario-wide configuration (OAK's), its alerts without a
    /// facility, and <paramref name="primaryAirportId"/> as the primary airport.
    /// </summary>
    private static StateSnapshotDto AsSchema35(StateSnapshotDto current, string? primaryAirportId)
    {
        JsonObject root = JsonNode.Parse(JsonSerializer.Serialize(current))!.AsObject();
        root["SchemaVersion"] = 35;
        JsonObject scenario = root["Scenario"]!.AsObject();
        scenario["AsdexSafetyLogicConfig"] = scenario["AsdexSafetyLogicConfigs"]!["OAK"]!.DeepClone();
        scenario.Remove("AsdexSafetyLogicConfigs");
        var legacyAlerts = new JsonArray();
        foreach (JsonNode? alert in scenario["ActiveAsdexAlerts"]!.AsArray())
        {
            if ((string?)alert!["FacilityId"] == "OAK")
            {
                JsonObject legacy = alert.DeepClone().AsObject();
                legacy.Remove("FacilityId");
                legacyAlerts.Add(legacy);
            }
        }

        scenario["ActiveAsdexAlerts"] = legacyAlerts;
        scenario["PrimaryAirportId"] = primaryAirportId;
        return JsonSerializer.Deserialize<StateSnapshotDto>(root.ToJsonString())!;
    }

    [Fact]
    public void ASchema35Snapshot_FilesItsConfigAndAlertsUnderThePrimaryAirportsFacility()
    {
        if (TwoFacilityEngine() is not { } engine)
        {
            return;
        }

        string alertId = Assert.Single(engine.Scenario!.ActiveAsdexAlerts["OAK"].Keys);
        StateSnapshotDto legacy = AsSchema35(engine.CaptureSnapshot(), "KSFO");

        engine.RestoreFromSnapshot(legacy);

        AsdexSafetyLogicConfig migrated = Assert.Single(engine.Scenario!.AsdexSafetyLogicConfigs, entry => entry.Key == "SFO").Value;
        Assert.Single(engine.Scenario.AsdexSafetyLogicConfigs);
        Assert.Equal("OAKCFG", migrated.RunwayConfigurationId);
        Assert.Equal(["OAKCFG_TWR"], migrated.InhibitedArrivalAlertPositionIds);
        Assert.Equal(["SFO"], engine.Scenario.ActiveAsdexAlerts.Keys);
        Assert.Equal([alertId], engine.Scenario.ActiveAsdexAlerts["SFO"].Keys);
        Assert.Null(legacy.Scenario.LegacyAsdexSafetyLogicConfig);
    }

    [Fact]
    public void ASchema35SnapshotWithoutAPrimaryAirport_DropsItsConfigAndAlerts()
    {
        if (TwoFacilityEngine() is not { } engine)
        {
            return;
        }

        engine.RestoreFromSnapshot(AsSchema35(engine.CaptureSnapshot(), primaryAirportId: null));

        Assert.Empty(engine.Scenario!.AsdexSafetyLogicConfigs);
        Assert.Empty(engine.Scenario.ActiveAsdexAlerts);
    }
}
