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
///
/// <para>
/// The flight-rules predicates carry hysteresis: while the previous situation is one of them, that situation's
/// conditions use its wider leave thresholds, and every other test uses the enter thresholds. A standalone turn on the
/// flight-rules branch keeps the previous flight-rules situation rather than sweeping through every bearing.
/// </para>
/// </summary>
public static class SituationClassifier
{
    /// <summary>Largest angle between track and the bearing to an airport that counts as closing on it (enter).</summary>
    public const double ClosingMaxTrackOffsetDeg = 60.0;

    /// <summary>While latched, an aircraft stops closing only past this angle between track and the bearing to the airport.</summary>
    public const double ClosingLeaveMaxTrackOffsetDeg = 90.0;

    /// <summary>Ground speed an aircraft must exceed to count as closing on an airport (enter).</summary>
    public const double ClosingMinGroundSpeedKts = 40.0;

    /// <summary>While latched, an aircraft stops closing at or below this ground speed.</summary>
    public const double ClosingLeaveMinGroundSpeedKts = 30.0;

    /// <summary>An IFR aircraft closing on its destination inside this range is an arrival.</summary>
    public const double IfrArrivalRadiusNm = 40.0;

    /// <summary>A non-local IFR arrival stays an arrival inside this range, whatever its track.</summary>
    public const double IfrArrivalLeaveRadiusNm = 45.0;

    /// <summary>Vertical speed at or below which an aircraft counts as descending.</summary>
    public const double DescendingMaxVerticalSpeedFpm = -500.0;

    /// <summary>How far below the aircraft its target altitude must be for the descent test.</summary>
    public const double DescendingMinAltitudeToLoseFt = 1000.0;

    /// <summary>The descent test's range: this many miles per 1,000 ft above the destination field...</summary>
    public const double DescentRangeNmPerThousandFt = 3.0;

    /// <summary>...plus this many miles.</summary>
    public const double DescentRangeBufferNm = 10.0;

    /// <summary>A latched IFR arrival keeps the descent range's geometry, widened by this many miles, without descending.</summary>
    public const double DescentRangeLeaveBufferNm = 5.0;

    /// <summary>A VFR aircraft closing on its destination inside this range is inbound (the Class C outer area, AIM 3-2-4).</summary>
    public const double VfrInboundRadiusNm = 20.0;

    /// <summary>A VFR inbound aircraft stays inbound inside this range.</summary>
    public const double VfrInboundLeaveRadiusNm = 22.5;

    /// <summary>A VFR aircraft that lifted off its destination field less than this long ago is not inbound to it.</summary>
    public const double VfrJustDepartedSeconds = 180.0;

    /// <summary>A VFR aircraft heading away from its origin inside this range is departing (no leave band).</summary>
    public const double VfrDepartingRadiusNm = 10.0;

    /// <summary>Smallest angle between track and the bearing to the origin that counts as heading away from it (enter).</summary>
    public const double VfrDepartingMinTrackOffsetDeg = 120.0;

    /// <summary>A departing VFR aircraft stops heading away only below this angle off the bearing to its origin.</summary>
    public const double VfrDepartingLeaveMinTrackOffsetDeg = 110.0;

    /// <summary>A VFR aircraft with no known origin is departing for this long after liftoff...</summary>
    public const double VfrNoOriginDepartingSeconds = 300.0;

    /// <summary>...while below this height above the ground.</summary>
    public const double VfrNoOriginDepartingMaxAglFt = 3000.0;

    private static readonly ClosingBand EnterClosing = new(ClosingMaxTrackOffsetDeg, ClosingMinGroundSpeedKts);
    private static readonly ClosingBand LeaveClosing = new(ClosingLeaveMaxTrackOffsetDeg, ClosingLeaveMinGroundSpeedKts);

    /// <summary>Returns the aircraft's situation.</summary>
    /// <param name="ac">The aircraft to classify.</param>
    /// <param name="simTimeSeconds">The current sim time, against which the liftoff time is measured.</param>
    /// <param name="previous">The situation last stored for the aircraft; <see cref="AircraftSituation.Unknown"/> when none.</param>
    /// <returns>The situation; <see cref="AircraftSituation.Unknown"/> is never returned for a live aircraft.</returns>
    public static AircraftSituation Classify(AircraftState ac, double simTimeSeconds, AircraftSituation previous)
    {
        if (ac.IsShadow)
        {
            return AircraftSituation.LiveTraffic;
        }

        AircraftSituation byPhase = ClassifyByPhase(ac, out bool onTurn);
        if (byPhase != AircraftSituation.Unknown)
        {
            return byPhase;
        }

        if (ac.IsOnGround)
        {
            return AircraftSituation.HoldingOnGround;
        }

        // A standalone turn (a 360 for spacing) sweeps every bearing; reclassifying through it would flip the menu.
        if (onTurn && IsFlightRulesSituation(previous))
        {
            return previous;
        }

        return ClassifyByFlightRules(ac, simTimeSeconds, previous);
    }

    /// <summary>
    /// The situation the current phase names: a turn or S-turns take the phase they interrupt
    /// (<see cref="ClassifyResumedPhase"/>), any other phase its own type's.
    /// </summary>
    /// <param name="ac">The aircraft to classify.</param>
    /// <param name="onTurn">Set when the current phase is a turn or S-turns.</param>
    private static AircraftSituation ClassifyByPhase(AircraftState ac, out bool onTurn)
    {
        if (ac.Phases is { CurrentPhase: MakeTurnPhase or STurnPhase } phases)
        {
            onTurn = true;
            return ClassifyResumedPhase(phases, ac.IsOnGround);
        }

        onTurn = false;
        return ClassifyPhase(ac.Phases?.CurrentPhase, ac.IsOnGround);
    }

    private static bool IsFlightRulesSituation(AircraftSituation situation) =>
        situation
            is AircraftSituation.IfrEnroute
                or AircraftSituation.IfrArrival
                or AircraftSituation.VfrFlightFollowing
                or AircraftSituation.VfrArrivalInbound
                or AircraftSituation.VfrDeparting;

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
    /// downwind or final, or the next pattern leg): the aircraft's situation is that phase's. Only consecutive turns
    /// are skipped; the first phase after them decides, so an unrelated queued phase further on never does. A turn
    /// followed by nothing, or by a phase that names no situation, names none.
    /// </summary>
    private static AircraftSituation ClassifyResumedPhase(PhaseList phases, bool isOnGround)
    {
        for (int i = phases.CurrentIndex + 1; i < phases.Phases.Count; i++)
        {
            Phase next = phases.Phases[i];
            if (next is MakeTurnPhase or STurnPhase)
            {
                continue;
            }

            return ClassifyPhase(next, isOnGround);
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

    private static AircraftSituation ClassifyByFlightRules(AircraftState ac, double simTimeSeconds, AircraftSituation previous)
    {
        NavigationDatabase? navDb = NavigationDatabase.InstanceOrNull;
        if (ac.FlightPlan.IsVfr)
        {
            return ClassifyVfr(ac, navDb, simTimeSeconds, previous);
        }

        return IsIfrArrival(ac, navDb, previous == AircraftSituation.IfrArrival) ? AircraftSituation.IfrArrival : AircraftSituation.IfrEnroute;
    }

    private static bool IsIfrArrival(AircraftState ac, NavigationDatabase? navDb, bool latched)
    {
        if ((!string.IsNullOrEmpty(ac.Procedure.ActiveStarId)) || (!string.IsNullOrEmpty(ac.Procedure.DestinationRunway)))
        {
            return true;
        }

        if ((navDb is null) || (Locate(ac, navDb, ac.FlightPlan.Destination) is not { } destination))
        {
            return false;
        }

        bool isLocal = navDb.AirportIdsMatchResolved(ac.FlightPlan.Departure, ac.FlightPlan.Destination);
        return latched ? StaysIfrArrival(ac, navDb, destination, isLocal) : EntersIfrArrival(ac, navDb, destination, isLocal);
    }

    private static bool EntersIfrArrival(AircraftState ac, NavigationDatabase navDb, AirportFix destination, bool isLocal)
    {
        if (!IsClosing(ac, destination, EnterClosing))
        {
            return false;
        }

        if ((!isLocal) && (destination.DistanceNm <= IfrArrivalRadiusNm))
        {
            return true;
        }

        return IsDescending(ac) && (destination.DistanceNm <= DescentRangeNm(ac, navDb, destination));
    }

    /// <summary>
    /// The leave test for an aircraft already an IFR arrival. A non-local arrival inside the leave radius stays one
    /// whatever its track (a downwind vector before any STAR or expected approach). Otherwise the descent clause is
    /// enter-only: the aircraft stays an arrival while closing and inside the widened descent range, so a level-off on
    /// a step-down descent does not flip it back to enroute. A local flight (origin = destination) never uses the
    /// radius test, entering or leaving.
    /// </summary>
    private static bool StaysIfrArrival(AircraftState ac, NavigationDatabase navDb, AirportFix destination, bool isLocal)
    {
        if ((!isLocal) && (destination.DistanceNm <= IfrArrivalLeaveRadiusNm))
        {
            return true;
        }

        return IsClosing(ac, destination, LeaveClosing)
            && (destination.DistanceNm <= (DescentRangeNm(ac, navDb, destination) + DescentRangeLeaveBufferNm));
    }

    private static bool IsDescending(AircraftState ac) =>
        (ac.VerticalSpeed <= DescendingMaxVerticalSpeedFpm)
        && (ac.Targets.TargetAltitude is { } target)
        && ((ac.Altitude - target) >= DescendingMinAltitudeToLoseFt);

    private static double DescentRangeNm(AircraftState ac, NavigationDatabase navDb, AirportFix destination)
    {
        double fieldElevationFt = navDb.GetAirportElevation(destination.Id) ?? 0.0;
        double heightAboveFieldFt = Math.Max(0.0, ac.Altitude - fieldElevationFt);
        return (DescentRangeNmPerThousandFt * heightAboveFieldFt / 1000.0) + DescentRangeBufferNm;
    }

    private static AircraftSituation ClassifyVfr(AircraftState ac, NavigationDatabase? navDb, double simTimeSeconds, AircraftSituation previous)
    {
        if (navDb is null)
        {
            return AircraftSituation.VfrFlightFollowing;
        }

        if (IsVfrInbound(ac, navDb, simTimeSeconds, previous == AircraftSituation.VfrArrivalInbound))
        {
            return AircraftSituation.VfrArrivalInbound;
        }

        return IsVfrDeparting(ac, navDb, simTimeSeconds, previous == AircraftSituation.VfrDeparting)
            ? AircraftSituation.VfrDeparting
            : AircraftSituation.VfrFlightFollowing;
    }

    private static bool IsVfrInbound(AircraftState ac, NavigationDatabase navDb, double simTimeSeconds, bool latched)
    {
        if (Locate(ac, navDb, ac.FlightPlan.Destination) is not { } destination)
        {
            return false;
        }

        double radiusNm = latched ? VfrInboundLeaveRadiusNm : VfrInboundRadiusNm;
        if ((destination.DistanceNm > radiusNm) || (!IsClosing(ac, destination, latched ? LeaveClosing : EnterClosing)))
        {
            return false;
        }

        bool departedThisField = navDb.AirportIdsMatchResolved(ac.FlightPlan.Departure, ac.FlightPlan.Destination);
        return !(departedThisField && (SecondsSinceLiftoff(ac, simTimeSeconds) < VfrJustDepartedSeconds));
    }

    /// <summary>
    /// Heading away from the origin inside its radius; with no origin known, soon after liftoff and still low. An
    /// aircraft with no liftoff time (spawned airborne, or restored from an older snapshot) departed long ago.
    /// </summary>
    private static bool IsVfrDeparting(AircraftState ac, NavigationDatabase navDb, double simTimeSeconds, bool latched)
    {
        if (Locate(ac, navDb, ac.FlightPlan.Departure) is { } origin)
        {
            double minOffsetDeg = latched ? VfrDepartingLeaveMinTrackOffsetDeg : VfrDepartingMinTrackOffsetDeg;
            return (origin.DistanceNm <= VfrDepartingRadiusNm)
                && (GeoMath.AbsBearingDifference(ac.TrueTrack.Degrees, origin.BearingDeg) >= minOffsetDeg);
        }

        return (SecondsSinceLiftoff(ac, simTimeSeconds) < VfrNoOriginDepartingSeconds)
            && ((ac.Altitude - FieldElevationResolver.Resolve(ac, navDb)) < VfrNoOriginDepartingMaxAglFt);
    }

    /// <summary>Seconds since liftoff, or infinity for an aircraft with no liftoff time (departed long ago).</summary>
    private static double SecondsSinceLiftoff(AircraftState ac, double simTimeSeconds) =>
        ac.Situation.AirborneAtSeconds is { } airborneAt ? simTimeSeconds - airborneAt : double.PositiveInfinity;

    private static bool IsClosing(AircraftState ac, AirportFix airport, ClosingBand band) =>
        (ac.GroundSpeed > band.MinGroundSpeedKts)
        && (GeoMath.AbsBearingDifference(ac.TrueTrack.Degrees, airport.BearingDeg) <= band.MaxTrackOffsetDeg);

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

    /// <summary>The closing test's thresholds: the largest track offset that still closes, and the ground speed to exceed.</summary>
    private readonly record struct ClosingBand(double MaxTrackOffsetDeg, double MinGroundSpeedKts);
}
