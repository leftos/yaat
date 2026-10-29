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

    /// <summary>A closed "28R" whose footprint is a box around <paramref name="centre"/>.</summary>
    private static AsdexSafetyLogicConfig Closed28R(LatLon centre)
    {
        const double Half = 0.002;
        return new(
            [
                new AsdexRunwayConfig(
                    "28R",
                    [
                        new(centre.Lat - Half, centre.Lon - Half),
                        new(centre.Lat + Half, centre.Lon - Half),
                        new(centre.Lat + Half, centre.Lon + Half),
                        new(centre.Lat - Half, centre.Lon + Half),
                    ],
                    IsClosed: true
                ),
            ],
            "WEST-PLAN",
            [],
            RunwayConfigurationChosen: false
        );
    }

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
    /// SFO and OAK both configure a closed "28R", each its own airport's, with a different aircraft rolling down each: each
    /// facility raises the closed-runway alert for its own aircraft only (a runway user is judged against the facility's own
    /// navdata runway), both alerts stand at once under their own facility, and each diff names its facility. The two alerts'
    /// ids differ by callsign; two facilities holding one alert id at once are covered by <see cref="AsdexSnapshotSchema36Tests"/>.
    /// </summary>
    [Fact]
    public void TwoFacilitiesWithAClosed28RInUseAtOnce_KeepTwoAlerts_AndEachDiffNamesItsFacility()
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        engine.LoadScenario(TwoAircraftAtSfo, 7, MagneticDeclination.EvaluationDateUtc, scenario => scenario.ArtccConfig = ZoaArtccConfig.Full);
        var spine = new SpineCapturingHost(engine);
        List<AircraftState> aircraft = engine.World.GetSnapshot();
        engine.ApplyRecordedAsdexSafetyLogic(new RecordedAsdexSafetyLogicChange(0, "OAK", Closed28R(RollDown28R(aircraft[0], "OAK"))));
        engine.ApplyRecordedAsdexSafetyLogic(new RecordedAsdexSafetyLogicChange(0, "SFO", Closed28R(RollDown28R(aircraft[1], "SFO"))));

        engine.TickAsdexAlerts(spine);

        AsdexSafetyAlert oak = Assert.Single(engine.Scenario!.ActiveAsdexAlerts["OAK"].Values);
        AsdexSafetyAlert sfo = Assert.Single(engine.Scenario.ActiveAsdexAlerts["SFO"].Values);
        Assert.Equal($"ClosedRunway|28R|{aircraft[0].Callsign}", oak.Id);
        Assert.Equal($"ClosedRunway|28R|{aircraft[1].Callsign}", sfo.Id);
        Assert.Equal(["OAK", "SFO"], spine.AsdexAlertFacilities);
        Assert.Equal([oak.Id, sfo.Id], spine.AsdexAlertChanges.Select(change => Assert.Single(change.NewAlerts).Id));
    }

    /// <summary>Puts <paramref name="ac"/> rolling at 80 kt 1,500 ft down <paramref name="airport"/>'s 28R, answering where.</summary>
    private static LatLon RollDown28R(AircraftState ac, string airport)
    {
        RunwayInfo runway =
            NavigationDatabase.Instance.GetRunway(airport, "28R")
            ?? NavigationDatabase.Instance.GetRunway("K" + airport, "28R")
            ?? throw new InvalidOperationException($"{airport} 28R is not in navdata");
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
        return ac.Position;
    }

    /// <summary>
    /// The scenario of the server's <c>SfoScenarioLoad_RunsTheDetector_WithoutADisplayPush</c>: one aircraft rolling
    /// down SFO 28R and one stopped on it, with only the seeded configuration in the scenario, stand as an occupied-runway alert at SFO.
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
            // The first rolls at 80 kt, a runway user; the second is stopped on the runway, occupying it.
            aircraft[i].IndicatedAirspeed = i == 0 ? 80 : 0;
            aircraft[i].TrueHeading = r28R.TrueHeading;
            aircraft[i].TrueTrack = r28R.TrueHeading;
        }

        var spine = new SpineCapturingHost(engine);
        engine.TickAsdexAlerts(spine);

        AsdexSafetyAlert alert = Assert.Single(engine.Scenario.ActiveAsdexAlerts["SFO"].Values);
        Assert.Equal(AsdexAlertKind.OccupiedRunway, alert.Kind);
        Assert.Equal(["28R-10L"], alert.RunwayIds);
        Assert.Equal(["SFO"], spine.AsdexAlertFacilities);
    }
}
