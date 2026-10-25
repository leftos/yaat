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
/// </summary>
public class AsdexLimitedConfigurationAlertTests
{
    private const string Sfo = "SFO";
    private const string L28D01 = "01GVF4AGA8HX03NSN8QJS4WJS4";
    private const string L28D28 = "01GVM1SKN089G82RCS84S0W8W8";

    private const string TwoAtSfo = """
        {
          "id": "asdex-limited", "name": "ASDE-X LIMITED at SFO", "artccId": "ZOA", "primaryAirportId": "SFO",
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

    /// <summary>Puts SFO on <paramref name="configurationId"/>, with 28R closed when <paramref name="close28R"/>.</summary>
    private static void SetSfoConfiguration(SimulationEngine engine, string configurationId, bool close28R)
    {
        AsdexSafetyLogicConfig sfo = engine.Scenario!.AsdexSafetyLogicConfigs[Sfo];
        List<AsdexRunwayConfig> runways = [.. sfo.Runways.Select(r => r.Id == "28R-10L" ? r with { IsClosed = close28R } : r)];
        engine.Scenario.AsdexSafetyLogicConfigs = engine.Scenario.AsdexSafetyLogicConfigs.SetItem(
            Sfo,
            sfo with
            {
                RunwayConfigurationId = configurationId,
                Runways = runways,
            }
        );
    }

    private static RunwayInfo NavRunway(string end) =>
        NavigationDatabase.Instance.GetRunway(Sfo, end)
        ?? NavigationDatabase.Instance.GetRunway("K" + Sfo, end)
        ?? throw new InvalidOperationException($"SFO {end} is not in navdata");

    /// <summary>UAL1 aligned <paramref name="nmPastThreshold"/> down <paramref name="end"/>, airborne low or on the surface.</summary>
    private static void Place(SimulationEngine engine, string callsign, string end, double nmPastThreshold, bool airborne, double speedKts)
    {
        RunwayInfo runway = NavRunway(end);
        AircraftState ac = engine.World.FindAircraft(callsign)!;
        ac.Position = GeoMath.ProjectPoint(new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude), runway.TrueHeading, nmPastThreshold);
        ac.TrueHeading = runway.TrueHeading;
        ac.IsOnGround = !airborne;
        ac.Altitude = runway.ElevationFt + (airborne ? 100 : 0);
        ac.IndicatedAirspeed = speedKts;
    }

    /// <summary>An arrival low over 28R and an aircraft stopped further down it.</summary>
    private static void ArrivalOverAStoppedAircraftOn28R(SimulationEngine engine)
    {
        Place(engine, "UAL1", "28R", 0.2, airborne: true, speedKts: 130);
        Place(engine, "UAL2", "28R", 0.6, airborne: false, speedKts: 0);
    }

    private static IReadOnlyCollection<AsdexSafetyAlert> SfoAlertsAfterATick(SimulationEngine engine)
    {
        engine.TickAsdexAlerts(new SpineCapturingHost(engine));
        return [.. engine.Scenario!.ActiveAsdexAlerts.GetValueOrDefault(Sfo, SimScenarioState.NoAsdexAlerts).Values];
    }

    [Fact]
    public void Limited_AnArrivalOverAnOccupied28R_RaisesNothing()
    {
        SimulationEngine engine = Engine();
        SetSfoConfiguration(engine, "limited", close28R: false);
        ArrivalOverAStoppedAircraftOn28R(engine);

        Assert.Empty(SfoAlertsAfterATick(engine));
    }

    [Fact]
    public void AnUnknownConfigurationId_IsLimited_AndRaisesNothing()
    {
        SimulationEngine engine = Engine();
        SetSfoConfiguration(engine, "NOPE", close28R: false);
        ArrivalOverAStoppedAircraftOn28R(engine);

        Assert.Empty(SfoAlertsAfterATick(engine));
    }

    /// <summary>L28D28 lists only 28L/28R, yet a departure rolling on 1R toward an aircraft stopped on 1R still alerts.</summary>
    [Fact]
    public void ANamedConfiguration_AlertsOnARunwayItDoesNotList()
    {
        SimulationEngine engine = Engine();
        SetSfoConfiguration(engine, L28D28, close28R: false);
        Place(engine, "UAL1", "01R", 0.2, airborne: false, speedKts: 60);
        Place(engine, "UAL2", "01R", 0.6, airborne: false, speedKts: 0);

        AsdexSafetyAlert alert = Assert.Single(SfoAlertsAfterATick(engine));
        Assert.Equal(AsdexAlertKind.OccupiedRunway, alert.Kind);
    }

    [Fact]
    public void Limited_AClosed28RWithAClaimant_StillRaisesTheClosedRunwayAlert()
    {
        SimulationEngine engine = Engine();
        SetSfoConfiguration(engine, "limited", close28R: true);
        Place(engine, "UAL1", "28R", 0.2, airborne: true, speedKts: 130);

        AsdexSafetyAlert alert = Assert.Single(SfoAlertsAfterATick(engine));
        Assert.Equal(AsdexAlertKind.ClosedRunway, alert.Kind);
    }

    [Fact]
    public void SwitchingToLimited_ClearsAStandingOccupiedRunwayAlert_OnTheNextTick()
    {
        SimulationEngine engine = Engine();
        SetSfoConfiguration(engine, L28D01, close28R: false);
        ArrivalOverAStoppedAircraftOn28R(engine);
        Assert.Equal(AsdexAlertKind.OccupiedRunway, Assert.Single(SfoAlertsAfterATick(engine)).Kind);

        SetSfoConfiguration(engine, "limited", close28R: false);

        Assert.Empty(SfoAlertsAfterATick(engine));
    }
}
