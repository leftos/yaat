using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Asdex;

/// <summary>What a runway user is doing on its runway; the order is the alert ranking.</summary>
public enum AsdexRunwayUserRole
{
    /// <summary>
    /// Airborne inside the runway's own arrival box (before its threshold, inside the arrival range, band and ceiling) whatever its
    /// phase or vertical speed; otherwise landing or rolling out (final approach, landing, runway exit, a touch-and-go before
    /// it re-accelerates, a stop-and-go before its stop, low approach), or going around: a go-around stays a runway user until
    /// it is past the threshold.
    /// </summary>
    Arrival = 0,

    /// <summary>
    /// Rolling for takeoff (or rejecting it) at <see cref="AsdexRunwayUsers.RollingSpeedKts"/> or more, whether from a line-up
    /// or a takeoff roll, or in the initial climb; and a touch-and-go or a stop-and-go once it re-accelerates after its
    /// touchdown or its stop (7110.65 §3-8-2).
    /// </summary>
    Departure = 1,

    /// <summary>
    /// Any other aircraft: taxiing, crossing, back-taxiing, holding short on the runway after landing, on the ground below
    /// <see cref="AsdexRunwayUsers.RollingSpeedKts"/> whatever its phase (an occupant), or with no phase.
    /// </summary>
    Other = 2,
}

/// <summary>
/// One runway an aircraft uses: the runway's index in the facility's list, where along it the aircraft is (feet from end 1),
/// and which way it moves (+1 toward end 2).
/// </summary>
internal readonly record struct AsdexRunwayUse(int RunwayIndex, double AlongFt, int Direction);

/// <summary>The runway an aircraft is an arrival to, and the runway it uses from over it; either or both may be none.</summary>
internal readonly record struct AsdexRunwayUses(AsdexRunwayUse? Arrival, AsdexRunwayUse? OverRunway);

/// <summary>
/// Who uses a runway, for every ASDE-X surface function: the hold bars and every Safety Logic alert kind read this one rule,
/// so bars and alerts switch in the same tick. A runway user is an aircraft tracking along the runway (either direction)
/// that is either an arrival inside about one nautical mile of a landing threshold, or over the runway footprint at 50 kt or
/// more, on it or airborne below 200 ft above it; these are the vNAS thresholds measured on vNAS Test (yaat-server
/// <c>docs/plans/asdex-probe-sfo</c>). It stops being one when it slows below 50 kt, climbs through 200 ft above the
/// runway, or crosses the threshold already that high. Taxiing, crossing, hovering, stopped and lined-up aircraft are not
/// users: on the footprint they are occupants. The rule is stateless (one edge each way, no hysteresis), and an aircraft
/// whose alerts are individually inhibited is still a user: the inhibit gates alerts, not bars.
/// </summary>
public static class AsdexRunwayUsers
{
    /// <summary>How far before a landing threshold, along the extended centerline, an arrival is a runway user.</summary>
    public const double ArrivalRangeFt = 6000;

    /// <summary>
    /// How far either side of the extended centerline an arrival may be and still be a runway user. A judgement call: wide
    /// enough for an approach a little off the centerline; close parallels inside it go to the nearest centerline.
    /// </summary>
    public const double ArrivalLateralFt = 1000;

    /// <summary>
    /// The height above field elevation at or above which an arrival is not a runway user. A judgement call standing for the
    /// top of ASDE-X target coverage.
    /// </summary>
    public const double ArrivalCeilingFt = 1500;

    /// <summary>The ground speed at or above which an aircraft over the runway, on it or airborne low, is a runway user.</summary>
    public const double RollingSpeedKts = 50;

    /// <summary>
    /// The height above the runway at or above which an airborne aircraft over it is neither a runway user nor an occupant.
    /// </summary>
    public const double OverRunwayCeilingFt = 200;

    /// <summary>Track tolerance (deg) for an aircraft to count as aligned with a runway direction.</summary>
    public const double AlignmentToleranceDeg = 35;

    /// <summary>Below this ground speed (kt) the aircraft's heading stands for its track, which a stopped aircraft does not have.</summary>
    private const double TrackMinGroundSpeedKts = 1;

    /// <summary>How far from every runway an aircraft is left out before any rule looks at it: well past the arrival range.</summary>
    private const double ConsideredRangeFt = 3 * GeoMath.FeetPerNm;

    /// <summary>The two directions along a runway: +1 toward end 2, -1 toward end 1.</summary>
    private static readonly int[] Directions = [1, -1];

    /// <summary>
    /// The runways <paramref name="aircraft"/> uses among <paramref name="runways"/>, whose axes <paramref name="axes"/> holds
    /// in the same order: the one it is an arrival to and the one it uses from over it.
    /// </summary>
    internal static AsdexRunwayUses UsesOf(
        AircraftState aircraft,
        IReadOnlyList<AsdexHoldBarRunway> runways,
        IReadOnlyList<AsdexRunwayAxis> axes,
        double fieldElevationFt
    )
    {
        if (!NearAnyRunway(aircraft.Position, axes))
        {
            return default;
        }

        return new AsdexRunwayUses(Arrival(aircraft, runways, axes, fieldElevationFt), OverRunway(aircraft, runways, axes));
    }

    /// <summary>
    /// True when <paramref name="uses"/> makes the aircraft a user of <c>runways[runwayIndex]</c>: the runway it uses is that
    /// one or, where the configuration names one navdata runway twice, the same navdata runway. A use from over the runway
    /// also needs the aircraft inside that entry's footprint.
    /// </summary>
    internal static bool IsUserOf(AircraftState aircraft, AsdexRunwayUses uses, IReadOnlyList<AsdexHoldBarRunway> runways, int runwayIndex)
    {
        AsdexHoldBarRunway runway = runways[runwayIndex];
        if (ArrivesOn(uses, runways, runwayIndex))
        {
            return true;
        }

        return (uses.OverRunway is { } over) && ReferenceEquals(runways[over.RunwayIndex].Runway, runway.Runway) && IsOverFootprint(aircraft, runway);
    }

    /// <summary>True when the aircraft is inside the runway's footprint ring; never for a runway whose configuration carries none.</summary>
    internal static bool IsOverFootprint(AircraftState aircraft, AsdexHoldBarRunway runway) =>
        (runway.Area.Count >= 3) && GeoMath.PointInRing(aircraft.Position, runway.Area);

    /// <summary>
    /// True when the aircraft occupies the runway: inside its footprint, on the ground or airborne below
    /// <see cref="OverRunwayCeilingFt"/> above it, whatever its speed, track or role.
    /// </summary>
    internal static bool Occupies(AircraftState aircraft, AsdexHoldBarRunway runway) =>
        IsOverFootprint(aircraft, runway) && (aircraft.IsOnGround || ((aircraft.Altitude - RunwayElevationFt(runway)) < OverRunwayCeilingFt));

    /// <summary>
    /// The aircraft's role on one runway. An airborne aircraft inside that runway's arrival box (<paramref name="arrivesOnRunway"/>,
    /// from <see cref="ArrivesOn"/>) is <see cref="AsdexRunwayUserRole.Arrival"/> whatever its phase or vertical speed; inside
    /// another runway's box only, it takes the role it would have anyway. A ground aircraft below <see cref="RollingSpeedKts"/>
    /// is <see cref="AsdexRunwayUserRole.Other"/> whatever it is doing. A sim aircraft otherwise takes its role from its current
    /// phase; a live-traffic shadow, which has none, from its kinematics: airborne it is an arrival unless climbing, and on the
    /// ground an arrival while it decelerates and a departure while it accelerates (with no acceleration to read, an arrival
    /// only if it was seen landing).
    /// </summary>
    internal static AsdexRunwayUserRole Role(AircraftState aircraft, bool arrivesOnRunway)
    {
        if (arrivesOnRunway)
        {
            return AsdexRunwayUserRole.Arrival;
        }

        if (aircraft.IsOnGround && (aircraft.GroundSpeed < RollingSpeedKts))
        {
            return AsdexRunwayUserRole.Other;
        }

        return aircraft.IsShadow ? KinematicRole(aircraft) : PhaseRole(aircraft);
    }

    /// <summary>
    /// True when the arrival box <paramref name="uses"/> puts the aircraft in is on <c>runways[runwayIndex]</c>'s navdata
    /// runway (matched by <c>RunwayInfo</c> reference, so an entry naming that runway twice matches either).
    /// </summary>
    internal static bool ArrivesOn(AsdexRunwayUses uses, IReadOnlyList<AsdexHoldBarRunway> runways, int runwayIndex) =>
        (uses.Arrival is { } arrival) && ReferenceEquals(runways[arrival.RunwayIndex].Runway, runways[runwayIndex].Runway);

    /// <summary>
    /// Ranks aircraft for an alert by their <paramref name="roles"/>: arrivals, then departures, then the rest; within a role
    /// the faster over the ground, then the ordinal-first callsign, so the order never depends on the aircraft list's. The
    /// detector orders an alert's callsigns (and so its second line) by it at detect time; the server reads that stored order,
    /// never re-ranking, to find the runway end for the first line and aural name, so line 1's end and line 2's first aircraft
    /// belong to the same aircraft.
    /// </summary>
    public static Comparer<AircraftState> First(IReadOnlyDictionary<AircraftState, AsdexRunwayUserRole> roles) =>
        Comparer<AircraftState>.Create(
            (a, b) =>
            {
                int role = roles[a].CompareTo(roles[b]);
                if (role != 0)
                {
                    return role;
                }

                int speed = b.GroundSpeed.CompareTo(a.GroundSpeed);
                return (speed != 0) ? speed : StringComparer.Ordinal.Compare(a.Callsign, b.Callsign);
            }
        );

    private static AsdexRunwayUserRole PhaseRole(AircraftState aircraft) =>
        aircraft.Phases?.CurrentPhase switch
        {
            TouchAndGoPhase { IsReaccelerating: true } or StopAndGoPhase { IsReaccelerating: true } => AsdexRunwayUserRole.Departure,
            FinalApproachPhase
            or LandingPhase
            or RunwayExitPhase
            or TouchAndGoPhase
            or StopAndGoPhase
            or LowApproachPhase
            or GoAroundPhase
            or HelicopterApproachPhase
            or HelicopterLandingPhase => AsdexRunwayUserRole.Arrival,
            LineUpPhase or LinedUpAndWaitingPhase or TakeoffPhase or RejectedTakeoffPhase or InitialClimbPhase or HelicopterTakeoffPhase =>
                AsdexRunwayUserRole.Departure,
            _ => AsdexRunwayUserRole.Other,
        };

    private static AsdexRunwayUserRole KinematicRole(AircraftState aircraft)
    {
        if (!aircraft.IsOnGround)
        {
            return aircraft.VerticalSpeed <= 0 ? AsdexRunwayUserRole.Arrival : AsdexRunwayUserRole.Departure;
        }

        if ((RunwayOccupancy.GroundAccelerationKtPerSec(aircraft) is { } acceleration) && (acceleration != 0))
        {
            return acceleration < 0 ? AsdexRunwayUserRole.Arrival : AsdexRunwayUserRole.Departure;
        }

        return aircraft.LiveTraffic is { LandedOnRunway: true } ? AsdexRunwayUserRole.Arrival : AsdexRunwayUserRole.Departure;
    }

    private static bool NearAnyRunway(LatLon position, IReadOnlyList<AsdexRunwayAxis> axes)
    {
        foreach (AsdexRunwayAxis axis in axes)
        {
            double along = axis.Along(position);
            bool alongNear = (along >= -ConsideredRangeFt) && (along <= (axis.LengthFt + ConsideredRangeFt));
            if (alongNear && (Math.Abs(axis.Lateral(position)) <= ConsideredRangeFt))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The arrival the aircraft is, if any: airborne below the ceiling, lined up on a landing direction (aligned, before that
    /// end's threshold, inside the lateral band; when several ends qualify, as close parallels share the band, the one whose
    /// extended centreline the aircraft is nearest, then the nearest threshold), and inside the range of that end alone.
    /// </summary>
    private static AsdexRunwayUse? Arrival(
        AircraftState ac,
        IReadOnlyList<AsdexHoldBarRunway> runways,
        IReadOnlyList<AsdexRunwayAxis> axes,
        double fieldElevationFt
    )
    {
        if (ac.IsOnGround || ((ac.Altitude - fieldElevationFt) >= ArrivalCeilingFt))
        {
            return null;
        }

        AsdexRunwayUse? best = null;
        double bestLateralFt = double.MaxValue;
        double bestToThresholdFt = double.MaxValue;
        for (int i = 0; i < runways.Count; i++)
        {
            AsdexRunwayAxis axis = axes[i];
            double along = axis.Along(ac.Position);
            double lateralFt = Math.Abs(axis.Lateral(ac.Position));
            if (lateralFt > ArrivalLateralFt)
            {
                continue;
            }

            foreach (int direction in Directions)
            {
                // Landing toward end 2 crosses end 1's threshold (along 0); landing toward end 1 crosses end 2's (along = length).
                double toThresholdFt = direction > 0 ? -along : along - axis.LengthFt;
                bool better = (lateralFt < bestLateralFt) || ((lateralFt == bestLateralFt) && (toThresholdFt < bestToThresholdFt));
                if ((toThresholdFt > 0) && better && (OffTrackDeg(ac, axis, direction) <= AlignmentToleranceDeg))
                {
                    bestLateralFt = lateralFt;
                    bestToThresholdFt = toThresholdFt;
                    best = new AsdexRunwayUse(i, along, direction);
                }
            }
        }

        // The range applies to the runway the arrival is lined up on only, so a staggered parallel whose threshold is nearer
        // never counts while the arrival is still outside the range of its own.
        return bestToThresholdFt <= ArrivalRangeFt ? best : null;
    }

    /// <summary>
    /// The runway the aircraft is a user of from over it, if any: inside the footprint, at the rolling speed or more, on the
    /// runway or airborne low over it, and aligned. Where footprints overlap and it is aligned with more than one, the runway
    /// its track lies closest to.
    /// </summary>
    private static AsdexRunwayUse? OverRunway(AircraftState ac, IReadOnlyList<AsdexHoldBarRunway> runways, IReadOnlyList<AsdexRunwayAxis> axes)
    {
        if (ac.GroundSpeed < RollingSpeedKts)
        {
            return null;
        }

        AsdexRunwayUse? best = null;
        double bestOffDeg = double.MaxValue;
        for (int i = 0; i < runways.Count; i++)
        {
            AsdexHoldBarRunway runway = runways[i];
            if (!IsOverFootprint(ac, runway))
            {
                continue;
            }

            if (!ac.IsOnGround && ((ac.Altitude - RunwayElevationFt(runway)) >= OverRunwayCeilingFt))
            {
                continue;
            }

            foreach (int direction in Directions)
            {
                double offDeg = OffTrackDeg(ac, axes[i], direction);
                if ((offDeg <= AlignmentToleranceDeg) && (offDeg < bestOffDeg))
                {
                    bestOffDeg = offDeg;
                    best = new AsdexRunwayUse(i, axes[i].Along(ac.Position), direction);
                }
            }
        }

        return best;
    }

    /// <summary>The runway's elevation the height over it is measured from: the mean of its two ends'.</summary>
    private static double RunwayElevationFt(AsdexHoldBarRunway runway) => (runway.Runway.Elevation1Ft + runway.Runway.Elevation2Ft) / 2;

    /// <summary>
    /// How far (deg) the aircraft's true course is from the runway's true direction toward end 2 (+1) or end 1 (-1): its track,
    /// or its heading when it is too slow to have one.
    /// </summary>
    private static double OffTrackDeg(AircraftState ac, AsdexRunwayAxis axis, int direction)
    {
        double runwayBearingDeg = direction > 0 ? axis.BearingDeg : axis.BearingDeg + 180;
        double courseDeg = ac.GroundSpeed < TrackMinGroundSpeedKts ? ac.TrueHeading.Degrees : ac.TrueTrack.Degrees;
        return GeoMath.AbsBearingDifference(courseDeg, runwayBearingDeg);
    }
}
