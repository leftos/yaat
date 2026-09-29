using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Situation;

/// <summary>
/// Classifies an aircraft into its <see cref="AircraftSituation"/>. Precedence: a live-traffic shadow first, then
/// the current phase's type (ground, tower and airborne families; a turn or S-turns take the phase they interrupt),
/// then, for an airborne aircraft whose phase names no situation, the flight-rules and inbound predicates. A grounded
/// aircraft with no mapped phase is holding.
/// </summary>
public static class SituationClassifier
{
    /// <summary>Largest angle between track and the bearing to an airport that still counts as closing on it.</summary>
    public const double ClosingMaxTrackOffsetDeg = 60.0;

    /// <summary>Ground speed an aircraft must exceed to count as closing on an airport.</summary>
    public const double ClosingMinGroundSpeedKts = 40.0;

    /// <summary>An IFR aircraft closing on its destination inside this range is an arrival.</summary>
    public const double IfrArrivalRadiusNm = 40.0;

    /// <summary>Vertical speed at or below which an aircraft counts as descending.</summary>
    public const double DescendingMaxVerticalSpeedFpm = -500.0;

    /// <summary>How far below the aircraft its target altitude must be for the descent test.</summary>
    public const double DescendingMinAltitudeToLoseFt = 1000.0;

    /// <summary>The descent test's range: this many miles per 1,000 ft above the destination field...</summary>
    public const double DescentRangeNmPerThousandFt = 3.0;

    /// <summary>...plus this many miles.</summary>
    public const double DescentRangeBufferNm = 10.0;

    /// <summary>A VFR aircraft closing on its destination inside this range is inbound (the Class C outer area, AIM 3-2-4).</summary>
    public const double VfrInboundRadiusNm = 20.0;

    /// <summary>A VFR aircraft heading away from its origin inside this range is departing.</summary>
    public const double VfrDepartingRadiusNm = 10.0;

    /// <summary>Smallest angle between track and the bearing to the origin that counts as heading away from it.</summary>
    public const double VfrDepartingMinTrackOffsetDeg = 120.0;

    /// <summary>Returns the aircraft's situation.</summary>
    /// <param name="ac">The aircraft to classify.</param>
    /// <returns>The situation; <see cref="AircraftSituation.Unknown"/> is never returned for a live aircraft.</returns>
    public static AircraftSituation Classify(AircraftState ac)
    {
        if (ac.IsShadow)
        {
            return AircraftSituation.LiveTraffic;
        }

        AircraftSituation byPhase = ac.Phases is { CurrentPhase: MakeTurnPhase or STurnPhase } phases
            ? ClassifyResumedPhase(phases, ac.IsOnGround)
            : ClassifyPhase(ac.Phases?.CurrentPhase, ac.IsOnGround);
        if (byPhase != AircraftSituation.Unknown)
        {
            return byPhase;
        }

        return ac.IsOnGround ? AircraftSituation.HoldingOnGround : ClassifyByFlightRules(ac);
    }

    /// <summary>
    /// The situation the phase's type alone names, or <see cref="AircraftSituation.Unknown"/> when it names none: no
    /// phase, a turn or S-turns, or a military training route (classified by the flight-rules predicates instead).
    /// </summary>
    private static AircraftSituation ClassifyPhase(Phase? phase, bool isOnGround)
    {
        AircraftSituation byPhase = ClassifyGroundPhase(phase);
        if (byPhase == AircraftSituation.Unknown)
        {
            byPhase = ClassifyTowerPhase(phase, isOnGround);
        }

        return byPhase == AircraftSituation.Unknown ? ClassifyAirbornePhase(phase) : byPhase;
    }

    /// <summary>
    /// A turn (360/270) or S-turns interrupt the phase they were given in, which follows them in the list (the resumed
    /// downwind or final, or the next pattern leg): the aircraft's situation is that phase's. A standalone turn with
    /// nothing mapped after it names none, leaving the aircraft to the flight-rules predicates.
    /// </summary>
    private static AircraftSituation ClassifyResumedPhase(PhaseList phases, bool isOnGround)
    {
        for (int i = phases.CurrentIndex + 1; i < phases.Phases.Count; i++)
        {
            AircraftSituation resumed = ClassifyPhase(phases.Phases[i], isOnGround);
            if (resumed != AircraftSituation.Unknown)
            {
                return resumed;
            }
        }

        return AircraftSituation.Unknown;
    }

    private static AircraftSituation ClassifyGroundPhase(Phase? phase) =>
        phase switch
        {
            AtParkingPhase => AircraftSituation.AtParking,
            PushbackPhase => AircraftSituation.PushingBack,
            HoldingInPositionPhase or HoldingAfterExitPhase or HoldingAfterPushbackPhase => AircraftSituation.HoldingOnGround,
            TaxiingPhase or FollowingPhase or AirTaxiPhase or CrossingRunwayPhase => AircraftSituation.Taxiing,
            HoldingShortPhase or RunwayHoldingPhase => AircraftSituation.HoldingShort,
            _ => AircraftSituation.Unknown,
        };

    private static AircraftSituation ClassifyTowerPhase(Phase? phase, bool isOnGround) =>
        phase switch
        {
            TakeoffPhase when isOnGround => AircraftSituation.LinedUp,
            LineUpPhase or LinedUpAndWaitingPhase => AircraftSituation.LinedUp,
            TakeoffPhase or HelicopterTakeoffPhase or InitialClimbPhase or DepartureProcedurePhase => AircraftSituation.Departing,
            FinalApproachPhase or HelicopterApproachPhase or HelicopterLandingPhase => AircraftSituation.Final,
            LandingPhase or RunwayExitPhase or ClearRunwayPhase or RejectedTakeoffPhase => AircraftSituation.RolloutExit,
            GoAroundPhase or LowApproachPhase => AircraftSituation.GoAround,
            _ => AircraftSituation.Unknown,
        };

    private static AircraftSituation ClassifyAirbornePhase(Phase? phase) =>
        phase switch
        {
            ApproachNavigationPhase or InterceptCoursePhase or ProcedureTurnPhase => AircraftSituation.Approach,
            HoldingPatternPhase or VfrHoldPhase or AirspaceBoundaryHoldPhase or AerialRefuelingAnchorPhase => AircraftSituation.Holding,
            UpwindPhase or CrosswindPhase or DownwindPhase or BasePhase => AircraftSituation.Pattern,
            PatternEntryPhase or MidfieldCrossingPhase or TeardropReentryPhase or VfrFollowPhase => AircraftSituation.Pattern,
            TouchAndGoPhase or StopAndGoPhase => AircraftSituation.Pattern,
            PatternExitPhase => AircraftSituation.VfrDeparting,
            _ => AircraftSituation.Unknown,
        };

    private static AircraftSituation ClassifyByFlightRules(AircraftState ac)
    {
        NavigationDatabase? navDb = NavigationDatabase.InstanceOrNull;
        if (ac.FlightPlan.IsVfr)
        {
            return ClassifyVfr(ac, navDb);
        }

        return IsIfrArrival(ac, navDb) ? AircraftSituation.IfrArrival : AircraftSituation.IfrEnroute;
    }

    private static bool IsIfrArrival(AircraftState ac, NavigationDatabase? navDb)
    {
        if ((!string.IsNullOrEmpty(ac.Procedure.ActiveStarId)) || (!string.IsNullOrEmpty(ac.Procedure.DestinationRunway)))
        {
            return true;
        }

        if ((navDb is null) || (Locate(ac, navDb, ac.FlightPlan.Destination) is not { } destination) || (!IsClosing(ac, destination)))
        {
            return false;
        }

        bool isLocal = navDb.AirportIdsMatchResolved(ac.FlightPlan.Departure, ac.FlightPlan.Destination);
        if ((!isLocal) && (destination.DistanceNm <= IfrArrivalRadiusNm))
        {
            return true;
        }

        return IsDescendingToward(ac, navDb, destination);
    }

    private static bool IsDescendingToward(AircraftState ac, NavigationDatabase navDb, AirportFix destination)
    {
        if ((ac.VerticalSpeed > DescendingMaxVerticalSpeedFpm) || (ac.Targets.TargetAltitude is not { } target))
        {
            return false;
        }

        if ((ac.Altitude - target) < DescendingMinAltitudeToLoseFt)
        {
            return false;
        }

        double fieldElevationFt = navDb.GetAirportElevation(destination.Id) ?? 0.0;
        double heightAboveFieldFt = Math.Max(0.0, ac.Altitude - fieldElevationFt);
        double rangeNm = (DescentRangeNmPerThousandFt * heightAboveFieldFt / 1000.0) + DescentRangeBufferNm;
        return destination.DistanceNm <= rangeNm;
    }

    private static AircraftSituation ClassifyVfr(AircraftState ac, NavigationDatabase? navDb)
    {
        if (navDb is null)
        {
            return AircraftSituation.VfrFlightFollowing;
        }

        if (
            (Locate(ac, navDb, ac.FlightPlan.Destination) is { } destination)
            && (destination.DistanceNm <= VfrInboundRadiusNm)
            && IsClosing(ac, destination)
        )
        {
            return AircraftSituation.VfrArrivalInbound;
        }

        if ((Locate(ac, navDb, ac.FlightPlan.Departure) is { } origin) && (origin.DistanceNm <= VfrDepartingRadiusNm) && IsHeadingAway(ac, origin))
        {
            return AircraftSituation.VfrDeparting;
        }

        return AircraftSituation.VfrFlightFollowing;
    }

    private static bool IsClosing(AircraftState ac, AirportFix airport) =>
        (ac.GroundSpeed > ClosingMinGroundSpeedKts)
        && (GeoMath.AbsBearingDifference(ac.TrueTrack.Degrees, airport.BearingDeg) <= ClosingMaxTrackOffsetDeg);

    private static bool IsHeadingAway(AircraftState ac, AirportFix airport) =>
        GeoMath.AbsBearingDifference(ac.TrueTrack.Degrees, airport.BearingDeg) >= VfrDepartingMinTrackOffsetDeg;

    private static AirportFix? Locate(AircraftState ac, NavigationDatabase navDb, string airportId)
    {
        if (navDb.GetAirportPosition(airportId) is not { } position)
        {
            return null;
        }

        double distanceNm = GeoMath.DistanceNm(ac.Position.Lat, ac.Position.Lon, position.Lat, position.Lon);
        double bearingDeg = GeoMath.BearingTo(ac.Position.Lat, ac.Position.Lon, position.Lat, position.Lon);
        return new AirportFix(airportId, distanceNm, bearingDeg);
    }

    private readonly record struct AirportFix(string Id, double DistanceNm, double BearingDeg);
}
