using System.Collections.Frozen;

namespace Yaat.Sim.Asdex;

/// <summary>
/// Which ASDE-X hold bars are Active this second, from the aircraft alone (stateless; nothing carries over between ticks). A
/// bar is Active while any runway user of the runway it protects still has it ahead. A runway user is an aircraft tracking
/// along the runway (either direction) that is either an arrival inside one nautical mile of a landing threshold, or over the
/// runway surface at 50 kt or more, on it or airborne low over it. So an arrival lights the whole runway, a departure lights
/// only the bars ahead once it rolls fast, each bar clears as the user passes it, and the rest clear together when the user
/// slows, climbs out, or crosses the threshold already high. Taxiing, crossing, hovering and lined-up aircraft are not runway
/// users. An aircraft whose alerts are individually inhibited is still one: the inhibit gates alerts, not bars.
/// </summary>
public static class AsdexHoldBarStatusEvaluator
{
    /// <summary>How far before a landing threshold, along the extended centerline, an arrival lights the runway's bars.</summary>
    public const double ArrivalRangeFt = 6000;

    /// <summary>
    /// How far either side of the extended centerline an arrival may be and still light the runway's bars. A judgement call:
    /// wide enough for an approach a little off the centerline; close parallels inside it go to the nearest centerline.
    /// </summary>
    public const double ArrivalLateralFt = 1000;

    /// <summary>
    /// The height above field elevation at or above which an arrival lights no bars. A judgement call standing for the top of
    /// ASDE-X target coverage.
    /// </summary>
    public const double ArrivalCeilingFt = 1500;

    /// <summary>The ground speed at or above which an aircraft over the runway, on it or airborne low, is a runway user.</summary>
    public const double RollingSpeedKts = 50;

    /// <summary>The height above the runway at or above which an airborne aircraft over it is no longer a runway user.</summary>
    public const double OverRunwayCeilingFt = 200;

    /// <summary>Track tolerance (deg) for an aircraft to count as aligned with a runway; the safety-logic detector's.</summary>
    private const double AlignmentToleranceDeg = 35;

    /// <summary>How far from every runway an aircraft is left out before any rule looks at it: well past the arrival range.</summary>
    private const double ConsideredRangeFt = 3 * GeoMath.FeetPerNm;

    /// <summary>The two directions along a runway: +1 toward end 2, -1 toward end 1.</summary>
    private static readonly int[] Directions = [1, -1];

    private static readonly IReadOnlySet<string> NoActiveBars = FrozenSet<string>.Empty;

    /// <summary>One runway user: the runway, where it is along it (feet from end 1), and which way it moves (+1 toward end 2).</summary>
    private readonly record struct RunwayUser(string RunwayId, double AlongFt, int Direction);

    /// <summary>
    /// The ids of the bars in <paramref name="bars"/> that are Active with <paramref name="aircraft"/> where they are.
    /// <paramref name="axes"/> holds each runway's axis, in <paramref name="runways"/>' order.
    /// </summary>
    internal static IReadOnlySet<string> ActiveBarIds(
        IReadOnlyList<AsdexHoldBarRunway> runways,
        IReadOnlyList<AsdexRunwayAxis> axes,
        IReadOnlyList<AsdexHoldBar> bars,
        IReadOnlyList<AircraftState> aircraft,
        double fieldElevationFt
    )
    {
        List<RunwayUser>? users = null;
        foreach (AircraftState ac in aircraft)
        {
            if (!NearAnyRunway(ac.Position, axes))
            {
                continue;
            }

            if (Arrival(ac, runways, axes, fieldElevationFt) is { } arrival)
            {
                (users ??= []).Add(arrival);
            }

            if (OnRunway(ac, runways, axes) is { } onRunway)
            {
                (users ??= []).Add(onRunway);
            }
        }

        if (users is null)
        {
            return NoActiveBars;
        }

        var active = new HashSet<string>(StringComparer.Ordinal);
        foreach (AsdexHoldBar bar in bars)
        {
            if (AnyUserHasAhead(users, bar))
            {
                active.Add(bar.Id);
            }
        }

        return active;
    }

    private static bool AnyUserHasAhead(List<RunwayUser> users, AsdexHoldBar bar)
    {
        foreach (RunwayUser user in users)
        {
            if ((user.RunwayId == bar.ProtectedRunwayId) && ((user.Direction * (bar.AlongRunwayFt - user.AlongFt)) > 0))
            {
                return true;
            }
        }

        return false;
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
    private static RunwayUser? Arrival(
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

        RunwayUser? best = null;
        double bestLateralFt = double.MaxValue;
        double bestToThresholdFt = double.MaxValue;
        for (int i = 0; i < axes.Count; i++)
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
                    best = new RunwayUser(runways[i].Id, along, direction);
                }
            }
        }

        // The range applies to the runway the arrival is lined up on only, so a staggered parallel whose threshold is nearer
        // never lights while the arrival is still outside the range of its own.
        return bestToThresholdFt <= ArrivalRangeFt ? best : null;
    }

    /// <summary>
    /// The runway the aircraft is a user of from over it, if any: inside the footprint, at the rolling speed or more, on the
    /// runway or airborne low over it, and aligned. Where footprints overlap and it is aligned with more than one, the runway
    /// its track lies closest to.
    /// </summary>
    private static RunwayUser? OnRunway(AircraftState ac, IReadOnlyList<AsdexHoldBarRunway> runways, IReadOnlyList<AsdexRunwayAxis> axes)
    {
        if (ac.GroundSpeed < RollingSpeedKts)
        {
            return null;
        }

        RunwayUser? best = null;
        double bestOffDeg = double.MaxValue;
        for (int i = 0; i < runways.Count; i++)
        {
            AsdexHoldBarRunway runway = runways[i];
            if ((runway.Area.Count < 3) || !GeoMath.PointInRing(ac.Position, runway.Area))
            {
                continue;
            }

            double runwayElevationFt = (runway.Runway.Elevation1Ft + runway.Runway.Elevation2Ft) / 2;
            if (!ac.IsOnGround && ((ac.Altitude - runwayElevationFt) >= OverRunwayCeilingFt))
            {
                continue;
            }

            foreach (int direction in Directions)
            {
                double offDeg = OffTrackDeg(ac, axes[i], direction);
                if ((offDeg <= AlignmentToleranceDeg) && (offDeg < bestOffDeg))
                {
                    bestOffDeg = offDeg;
                    best = new RunwayUser(runway.Id, axes[i].Along(ac.Position), direction);
                }
            }
        }

        return best;
    }

    /// <summary>How far (deg) the aircraft's true track is from the runway's direction toward end 2 (+1) or end 1 (-1).</summary>
    private static double OffTrackDeg(AircraftState ac, AsdexRunwayAxis axis, int direction)
    {
        double runwayBearingDeg = direction > 0 ? axis.BearingDeg : axis.BearingDeg + 180;
        return GeoMath.AbsBearingDifference(ac.TrueTrack.Degrees, runwayBearingDeg);
    }
}
