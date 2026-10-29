using System.Collections.Immutable;
using Xunit;
using Yaat.Sim.Asdex;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Asdex;

/// <summary>
/// The safety-logic configuration and the standing alerts are kept per ASDE-X facility: a push replaces the pushing
/// facility's configuration only, two facilities' alerts on runways of the same name stand side by side, every alert
/// diff names its facility, and a seeded configuration alone (no display push) drives the detector.
/// </summary>
public class AsdexPerFacilityTests
{
    private const string TwoAircraftAtSfo = """
        {
          "name": "SFO ASDE-X seed", "artccId": "ZOA", "primaryAirportId": "SFO",
          "aircraft": [
            {
              "id": "a1", "aircraftId": "UAL1", "aircraftType": "B738", "transponderMode": "C",
              "startingConditions": { "type": "Coordinates", "coordinates": { "lat": 37.9, "lon": -122.6 }, "altitude": 5000, "heading": 280, "speed": 250 },
              "flightplan": { "rules": "IFR", "departure": "KSFO", "destination": "KLAX", "cruiseAltitude": 35000, "cruiseSpeed": 450, "route": "", "remarks": "", "aircraftType": "B738" }
            },
            {
              "id": "a2", "aircraftId": "UAL2", "aircraftType": "B738", "transponderMode": "C",
              "startingConditions": { "type": "Coordinates", "coordinates": { "lat": 37.95, "lon": -122.6 }, "altitude": 6000, "heading": 280, "speed": 250 },
              "flightplan": { "rules": "IFR", "departure": "KSFO", "destination": "KLAX", "cruiseAltitude": 35000, "cruiseSpeed": 450, "route": "", "remarks": "", "aircraftType": "B738" }
            }
          ]
        }
        """;

    /// <summary>A box around a point on OAK's field, the footprint both facilities' pushes carry in the collision test.</summary>
    private static readonly IReadOnlyList<LatLon> SharedArea =
    [
        new(37.7208, -122.2215),
        new(37.7218, -122.2215),
        new(37.7218, -122.2201),
        new(37.7208, -122.2201),
    ];

    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public AsdexPerFacilityTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static ImmutableSortedDictionary<string, AsdexSafetyLogicConfig> Seed() =>
        AsdexSafetyLogicSeed.Build(ZoaArtccConfig.Full, NavigationDatabase.Instance, ActiveRunways.Empty);

    private static AsdexSafetyLogicConfig Closed28R() =>
        new(
            [new AsdexRunwayConfig("28R", SharedArea, IsClosed: true)],
            "WEST-PLAN",
            [],
            RunwayConfigurationChosen: false,
            AsdexSafetyLogicConfig.NoSeconds,
            AsdexSafetyLogicConfig.NoSeconds
        );

    [Fact]
    public void APushUnderSfo_LeavesOaksEntryAlone()
    {
        if (_zoa is null)
        {
            return;
        }

        SimulationEngine engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);
        engine.Scenario!.AsdexSafetyLogicConfigs = Seed();
        AsdexSafetyLogicConfig oakBefore = engine.Scenario.AsdexSafetyLogicConfigs["OAK"];

        AsdexSafetyLogicConfig pushed = Closed28R();
        engine.ApplyRecordedAsdexSafetyLogic(new RecordedAsdexSafetyLogicChange(0, "SFO", pushed));

        Assert.Equal(pushed with { RunwayConfigurationChosen = true }, engine.Scenario.AsdexSafetyLogicConfigs["SFO"]);
        Assert.Same(oakBefore, engine.Scenario.AsdexSafetyLogicConfigs["OAK"]);
    }

    /// <summary>
    /// SFO and OAK both configure a closed "28R" over the same ground, and the one aircraft stands on it: the two alerts
    /// share an id, and keeping them per facility is what lets both stand, each diff naming its own facility.
    /// </summary>
    [Fact]
    public void TwoFacilitiesWithA28ROccupiedAtOnce_KeepTwoAlerts_AndEachDiffNamesItsFacility()
    {
        if (_zoa is null)
        {
            return;
        }

        SimulationEngine engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);
        var spine = new SpineCapturingHost(engine);
        engine.ApplyRecordedAsdexSafetyLogic(new RecordedAsdexSafetyLogicChange(0, "OAK", Closed28R()));
        engine.ApplyRecordedAsdexSafetyLogic(new RecordedAsdexSafetyLogicChange(0, "SFO", Closed28R()));
        AircraftState ac = engine.World.GetSnapshot()[0];
        ac.Position = new LatLon(37.7213, -122.2208);
        ac.TrueHeading = new TrueHeading(293);
        ac.IsOnGround = true;
        ac.Altitude = 9;

        engine.TickAsdexAlerts(spine);

        AsdexSafetyAlert oak = Assert.Single(engine.Scenario!.ActiveAsdexAlerts["OAK"].Values);
        AsdexSafetyAlert sfo = Assert.Single(engine.Scenario.ActiveAsdexAlerts["SFO"].Values);
        Assert.Equal(oak.Id, sfo.Id);
        Assert.Equal(["OAK", "SFO"], spine.AsdexAlertFacilities);
        Assert.All(spine.AsdexAlertChanges, change => Assert.Equal(oak.Id, Assert.Single(change.NewAlerts).Id));
    }

    /// <summary>
    /// The scenario of the server's <c>SfoScenarioLoad_RunsTheDetector_WithoutADisplayPush</c>: two aligned aircraft on
    /// the surface of SFO 28R, with only the seeded configuration in the scenario, stand as an occupied-runway alert at SFO.
    /// </summary>
    [Fact]
    public void ASeededConfigAlone_RaisesTheOccupiedRunwayAlertOnSfo28R()
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        engine.LoadScenario(TwoAircraftAtSfo, 7, MagneticDeclination.EvaluationDateUtc, scenario => scenario.ArtccConfig = ZoaArtccConfig.Full);
        engine.Scenario!.AsdexSafetyLogicConfigs = Seed();
        RunwayInfo r28R =
            NavigationDatabase.Instance.GetRunway("SFO", "28R")
            ?? NavigationDatabase.Instance.GetRunway("KSFO", "28R")
            ?? throw new InvalidOperationException("SFO 28R is not in navdata");
        var threshold = new LatLon(r28R.ThresholdLatitude, r28R.ThresholdLongitude);
        List<AircraftState> aircraft = engine.World.GetSnapshot();
        Assert.Equal(2, aircraft.Count);
        for (int i = 0; i < aircraft.Count; i++)
        {
            aircraft[i].Position = GeoMath.ProjectPoint(threshold, r28R.TrueHeading, 0.2 + (0.4 * i));
            aircraft[i].Altitude = r28R.ElevationFt;
            aircraft[i].IsOnGround = true;
            aircraft[i].IndicatedAirspeed = 0;
            aircraft[i].TrueHeading = r28R.TrueHeading;
        }

        var spine = new SpineCapturingHost(engine);
        engine.TickAsdexAlerts(spine);

        AsdexSafetyAlert alert = Assert.Single(engine.Scenario.ActiveAsdexAlerts["SFO"].Values);
        Assert.Equal(AsdexAlertKind.OccupiedRunway, alert.Kind);
        Assert.Equal(["28R-10L"], alert.RunwayIds);
        Assert.Equal(["SFO"], spine.AsdexAlertFacilities);
    }
}
