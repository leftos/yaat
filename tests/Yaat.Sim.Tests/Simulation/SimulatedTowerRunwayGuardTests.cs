using Xunit;
using Yaat.Sim.ControllerAi.Rules;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The silent simulated tower's runway guard (YAAT-1180) on real runways: a crosser on the departure runway, an arrival
/// inside the crossing gate's final gate (3 nm or 90 s), the wake turbulence interval behind a preceding departure on the
/// same runway (7110.65 3-9-6.f), and an arrival to a runway that crosses the departure runway (3-9-8) each keep a
/// departure waiting.
/// </summary>
[Collection("NavDbMutator")]
public class SimulatedTowerRunwayGuardTests
{
    private const string DepartureCallsign = "SWA1234";

    public SimulatedTowerRunwayGuardTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void RunwayCrossingTraffic_Waits()
    {
        RunwayInfo runway = Runway("OAK", "28R");
        AircraftState departure = Departure(runway, "B738");
        var midfield = new LatLon((runway.Lat1 + runway.Lat2) / 2, (runway.Lon1 + runway.Lon2) / 2);
        var crosser = new AircraftState
        {
            Callsign = "N152SP",
            AircraftType = "C172",
            AirportId = "OAK",
            Position = midfield,
            TrueHeading = new TrueHeading(runway.TrueHeading.Degrees + 90),
            TrueTrack = new TrueHeading(runway.TrueHeading.Degrees + 90),
            IndicatedAirspeed = 10,
            IsOnGround = true,
            Phases = new PhaseList(),
        };

        string? reason = SimulatedTowerRunwayGuard.ReasonToWait(departure, runway, [departure, crosser], null, 0);
        Assert.NotNull(reason);
        Assert.Contains("N152SP", reason, StringComparison.Ordinal);

        crosser.Position = GeoMath.ProjectPoint(midfield, crosser.TrueHeading, 0.3);
        Assert.Null(SimulatedTowerRunwayGuard.ReasonToWait(departure, runway, [departure, crosser], null, 0));
    }

    [Fact]
    public void ArrivalAtEightySeconds_Waits()
    {
        RunwayInfo runway = Runway("OAK", "28R");
        AircraftState departure = Departure(runway, "B738");
        AircraftState arrival = OnFinalSecondsOut(runway, "N152SP", 80);
        Assert.True(
            RunwayOccupancy.DistanceToLandingThresholdNm(arrival, runway, null) > RunwayCrossingGate.FinalGateNm,
            "the arrival is inside 3 nm"
        );

        string? reason = SimulatedTowerRunwayGuard.ReasonToWait(departure, runway, [departure, arrival], null, 0);
        Assert.NotNull(reason);
        Assert.Contains("N152SP", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ArrivalAtFourNmAndOverNinetySeconds_DoesNotWait()
    {
        RunwayInfo runway = Runway("OAK", "28R");
        AircraftState departure = Departure(runway, "B738");
        AircraftState arrival = OnFinal(runway, "N152SP", 4.0);
        Assert.True(
            RunwayOccupancy.SecondsToLandingThreshold(arrival, runway, null) > RunwayCrossingGate.FinalGateSeconds,
            "the arrival is inside 90 s"
        );

        Assert.Null(SimulatedTowerRunwayGuard.ReasonToWait(departure, runway, [departure, arrival], null, 0));
    }

    [Fact]
    public void HeavyAheadThenNarrowbody_WaitsTheWakeInterval()
    {
        RunwayInfo runway = Runway("OAK", "28R");
        AircraftState departure = Departure(runway, "B738");
        const double rollStart = 1000;
        AircraftState heavy = Climbout(runway, "BAW1", "B77W", rollStart);
        DepartureWakeInterval interval =
            RunwayDepartureSeparation.DepartureWakeIntervalFor(
                RunwayDepartureSeparation.CwtCategoryOf("B77W"),
                RunwayDepartureSeparation.CwtCategoryOf("B738"),
                sameRunwayOrParallelUnder700Ft: true
            ) ?? throw new InvalidOperationException("no wake interval for a B738 behind a B77W");

        string? reason = SimulatedTowerRunwayGuard.ReasonToWait(departure, runway, [heavy, departure], null, rollStart + interval.Seconds - 1);
        Assert.NotNull(reason);
        Assert.Contains("BAW1", reason, StringComparison.Ordinal);

        Assert.Null(SimulatedTowerRunwayGuard.ReasonToWait(departure, runway, [heavy, departure], null, rollStart + interval.Seconds));
    }

    [Fact]
    public void IntersectingRunwayArrivalInsideTheGate_Waits()
    {
        RunwayInfo departureRunway = Runway("SFO", "1L");
        RunwayInfo landingRunway = Runway("SFO", "28L");
        AircraftState departure = Departure(departureRunway, "B738");

        AircraftState near = OnFinal(landingRunway, "UAL1", 2.0);
        string? reason = SimulatedTowerRunwayGuard.ReasonToWait(departure, departureRunway, [departure, near], null, 0);
        Assert.NotNull(reason);
        Assert.Contains("UAL1", reason, StringComparison.Ordinal);

        AircraftState far = OnFinal(landingRunway, "UAL1", 6.0);
        Assert.Null(SimulatedTowerRunwayGuard.ReasonToWait(departure, departureRunway, [departure, far], null, 0));
    }

    private static RunwayInfo Runway(string airport, string designator) =>
        NavigationDatabase.Instance.GetRunway(airport, designator) ?? throw new InvalidOperationException($"no runway {airport} {designator}");

    /// <summary>The transferred departure at the runway's threshold.</summary>
    private static AircraftState Departure(RunwayInfo runway, string type) =>
        new()
        {
            Callsign = DepartureCallsign,
            AircraftType = type,
            AirportId = runway.AirportId,
            Position = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
            TrueHeading = runway.TrueHeading,
            IsOnGround = true,
            Phases = new PhaseList { AssignedRunway = runway },
        };

    /// <summary>A departure off <paramref name="runway"/> climbing out three miles past its far end, its roll begun at <paramref name="rollStartSeconds"/>.</summary>
    private static AircraftState Climbout(RunwayInfo runway, string callsign, string type, double rollStartSeconds)
    {
        var aircraft = new AircraftState
        {
            Callsign = callsign,
            AircraftType = type,
            AirportId = runway.AirportId,
            Position = GeoMath.ProjectPoint(new LatLon(runway.EndLatitude, runway.EndLongitude), runway.TrueHeading, 3.0),
            TrueHeading = runway.TrueHeading,
            TrueTrack = runway.TrueHeading,
            Altitude = runway.ElevationFt + 3000,
            IndicatedAirspeed = 220,
            VerticalSpeed = 2500,
            IsOnGround = false,
            Phases = new PhaseList { AssignedRunway = runway },
        };
        aircraft.Ground.TakeoffRollStartedAtSeconds = rollStartSeconds;
        return aircraft;
    }

    /// <summary>An arrival on <paramref name="runway"/>'s final that reaches its threshold in <paramref name="seconds"/> at its own ground speed.</summary>
    private static AircraftState OnFinalSecondsOut(RunwayInfo runway, string callsign, double seconds)
    {
        AircraftState probe = OnFinal(runway, callsign, 3.0);
        return OnFinal(runway, callsign, probe.GroundSpeed * seconds / 3600.0);
    }

    private static AircraftState OnFinal(RunwayInfo runway, string callsign, double distanceNm)
    {
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        return new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            AirportId = runway.AirportId,
            Position = GeoMath.ProjectPoint(threshold, runway.TrueHeading.ToReciprocal(), distanceNm),
            TrueHeading = runway.TrueHeading,
            TrueTrack = runway.TrueHeading,
            Altitude = runway.ElevationFt + (distanceNm * 318),
            IndicatedAirspeed = 140,
            VerticalSpeed = -700,
            IsOnGround = false,
            Phases = new PhaseList { AssignedRunway = runway },
        };
    }
}
