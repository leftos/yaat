using Xunit;
using Yaat.Sim.Asdex;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Asdex;

/// <summary>
/// The engine's surface-alert pass honours the facility's runway configuration, from the real ZOA config and navdata:
/// on LIMITED ("limited", or an id that names none of the facility's configurations) only closed-runway alerts are
/// raised, while any named configuration raises every alert kind on every runway, its runway lists playing no part.
/// The choice is made per tick, so switching to LIMITED clears a standing occupied-runway alert on the next tick.
/// A facility with no runway configurations (OAK) can only be on LIMITED.
/// </summary>
public class AsdexLimitedConfigurationAlertTests
{
    private const string Sfo = "SFO";
    private const string Oak = "OAK";
    private const string L28D01 = "01GVF4AGA8HX03NSN8QJS4WJS4";
    private const string L28D28 = "01GVM1SKN089G82RCS84S0W8W8";

    private const string TwoAtSfo = """
        {
          "id": "asdex-limited", "name": "ASDE-X LIMITED at SFO", "artccId": "ZOA", "primaryAirportId": "SFO",
          "aircraft": [
            {
              "id": "a1", "aircraftId": "UAL1", "aircraftType": "B738", "transponderMode": "C",
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

    public AsdexLimitedConfigurationAlertTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static SimulationEngine Engine()
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        engine.LoadScenario(TwoAtSfo, 7, MagneticDeclination.EvaluationDateUtc, scenario => scenario.ArtccConfig = ZoaArtccConfig.Full);
        return engine;
    }

    /// <summary>
    /// Puts <paramref name="facility"/> on <paramref name="configurationId"/>, closing the runway that has the end
    /// <paramref name="closeRunwayEnd"/> when one is given.
    /// </summary>
    private static void SetConfiguration(SimulationEngine engine, string facility, string configurationId, string? closeRunwayEnd)
    {
        AsdexSafetyLogicConfig config = engine.Scenario!.AsdexSafetyLogicConfigs[facility];
        List<AsdexRunwayConfig> runways =
        [
            .. config.Runways.Select(r =>
                ((closeRunwayEnd is not null) && r.Id.Split('-').Contains(closeRunwayEnd)) ? r with { IsClosed = true } : r
            ),
        ];
        engine.Scenario.AsdexSafetyLogicConfigs = engine.Scenario.AsdexSafetyLogicConfigs.SetItem(
            facility,
            config with
            {
                RunwayConfigurationId = configurationId,
                Runways = runways,
            }
        );
    }

    private static RunwayInfo NavRunway(string airport, string end) =>
        NavigationDatabase.Instance.GetRunway(airport, end)
        ?? NavigationDatabase.Instance.GetRunway("K" + airport, end)
        ?? throw new InvalidOperationException($"{airport} {end} is not in navdata");

    /// <summary>
    /// The aircraft heading and tracking <paramref name="nmPastThreshold"/> down <paramref name="runway"/>, airborne low or on
    /// the surface.
    /// </summary>
    private static void Place(SimulationEngine engine, string callsign, RunwayInfo runway, double nmPastThreshold, bool airborne, double speedKts)
    {
        AircraftState ac = engine.World.FindAircraft(callsign)!;
        ac.Position = GeoMath.ProjectPoint(new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude), runway.TrueHeading, nmPastThreshold);
        ac.TrueHeading = runway.TrueHeading;
        ac.TrueTrack = runway.TrueHeading;
        ac.IsOnGround = !airborne;
        ac.Altitude = runway.ElevationFt + (airborne ? 100 : 0);
        ac.IndicatedAirspeed = speedKts;
    }

    /// <summary>An arrival low over <paramref name="airport"/>'s 28R and an aircraft stopped further down it.</summary>
    private static void ArrivalOverAStoppedAircraftOn28R(SimulationEngine engine, string airport)
    {
        Place(engine, "UAL1", NavRunway(airport, "28R"), 0.2, airborne: true, speedKts: 130);
        Place(engine, "UAL2", NavRunway(airport, "28R"), 0.6, airborne: false, speedKts: 0);
    }

    private static IReadOnlyCollection<AsdexSafetyAlert> AlertsAfterATick(SimulationEngine engine, string facility)
    {
        engine.TickAsdexAlerts(new SpineCapturingHost(engine));
        return [.. engine.Scenario!.ActiveAsdexAlerts.GetValueOrDefault(facility, SimScenarioState.NoAsdexAlerts).Values];
    }

    private static IReadOnlyCollection<AsdexSafetyAlert> SfoAlertsAfterATick(SimulationEngine engine) => AlertsAfterATick(engine, Sfo);

    [Fact]
    public void Limited_AnArrivalOverAnOccupied28R_RaisesNothing()
    {
        SimulationEngine engine = Engine();
        SetConfiguration(engine, Sfo, "limited", closeRunwayEnd: null);
        ArrivalOverAStoppedAircraftOn28R(engine, Sfo);

        Assert.Empty(SfoAlertsAfterATick(engine));
    }

    [Fact]
    public void AnUnknownConfigurationId_IsLimited_AndRaisesNothing()
    {
        SimulationEngine engine = Engine();
        SetConfiguration(engine, Sfo, "NOPE", closeRunwayEnd: null);
        ArrivalOverAStoppedAircraftOn28R(engine, Sfo);

        Assert.Empty(SfoAlertsAfterATick(engine));
    }

    /// <summary>L28D28 lists only 28L/28R, yet a departure rolling on 1R toward an aircraft stopped on 1R still alerts.</summary>
    [Fact]
    public void ANamedConfiguration_AlertsOnARunwayItDoesNotList()
    {
        SimulationEngine engine = Engine();
        SetConfiguration(engine, Sfo, L28D28, closeRunwayEnd: null);
        Place(engine, "UAL1", NavRunway(Sfo, "01R"), 0.2, airborne: false, speedKts: 60);
        Place(engine, "UAL2", NavRunway(Sfo, "01R"), 0.6, airborne: false, speedKts: 0);

        AsdexSafetyAlert alert = Assert.Single(SfoAlertsAfterATick(engine));
        Assert.Equal(AsdexAlertKind.OccupiedRunway, alert.Kind);
    }

    [Fact]
    public void Limited_AClosed28RWithAClaimant_StillRaisesTheClosedRunwayAlert()
    {
        SimulationEngine engine = Engine();
        SetConfiguration(engine, Sfo, "limited", closeRunwayEnd: "28R");
        Place(engine, "UAL1", NavRunway(Sfo, "28R"), 0.2, airborne: true, speedKts: 130);

        AsdexSafetyAlert alert = Assert.Single(SfoAlertsAfterATick(engine));
        Assert.Equal(AsdexAlertKind.ClosedRunway, alert.Kind);
    }

    [Fact]
    public void SwitchingToLimited_ClearsAStandingOccupiedRunwayAlert_OnTheNextTick()
    {
        SimulationEngine engine = Engine();
        SetConfiguration(engine, Sfo, L28D01, closeRunwayEnd: null);
        ArrivalOverAStoppedAircraftOn28R(engine, Sfo);
        Assert.Equal(AsdexAlertKind.OccupiedRunway, Assert.Single(SfoAlertsAfterATick(engine)).Kind);

        SetConfiguration(engine, Sfo, "limited", closeRunwayEnd: null);

        Assert.Empty(SfoAlertsAfterATick(engine));
    }

    [Fact]
    public void NoConfigurations_AnArrivalOverAnOccupiedRunway_RaisesNothing()
    {
        SimulationEngine engine = Engine();
        Assert.Equal("", engine.Scenario!.AsdexSafetyLogicConfigs[Oak].RunwayConfigurationId);
        ArrivalOverAStoppedAircraftOn28R(engine, Oak);

        Assert.Empty(AlertsAfterATick(engine, Oak));
    }

    [Fact]
    public void NoConfigurations_AClosedRunwayWithAClaimant_StillRaisesTheClosedRunwayAlert()
    {
        SimulationEngine engine = Engine();
        Assert.Equal("", engine.Scenario!.AsdexSafetyLogicConfigs[Oak].RunwayConfigurationId);
        SetConfiguration(engine, Oak, "", closeRunwayEnd: "28R");
        Place(engine, "UAL1", NavRunway(Oak, "28R"), 0.2, airborne: true, speedKts: 130);

        AsdexSafetyAlert alert = Assert.Single(AlertsAfterATick(engine, Oak));
        Assert.Equal(AsdexAlertKind.ClosedRunway, alert.Kind);
    }
}
