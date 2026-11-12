using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// Traffic standing on a taxi path: the aircraft at rest on the ground along a path of points, nearest first
/// (<see cref="StandingOnPathAhead"/>).
/// </summary>
internal static class TaxiPathTraffic
{
    private static readonly ILogger Log = SimLog.CreateLogger("TaxiPathTraffic");

    /// <summary>Distance (ft) from the path within which another aircraft counts as on it.</summary>
    private const double PathLateralFt = 40.0;

    /// <summary>An aircraft on a path (<see cref="StandingOnPathAhead"/>) and where along it (ft from its first point).</summary>
    internal readonly record struct StandingTraffic(AircraftState Aircraft, double AlongFt);

    /// <summary>
    /// The aircraft other than <paramref name="self"/> at rest on the ground (at or below
    /// <see cref="GroundConflictDetector.HeldStationarySpeedKts"/>) that stand on <paramref name="path"/> short of its last point:
    /// within <see cref="PathLateralFt"/> of a leg and ahead of its first point (<see cref="AircraftOnPath"/>). One standing past
    /// the last point, though within reach of it, is not counted, nor one moving through. Nearest along the path first.
    /// </summary>
    internal static List<StandingTraffic> StandingOnPathAhead(AircraftState self, IReadOnlyList<AircraftState> all, List<LatLon> path)
    {
        if (path.Count < 2)
        {
            return [];
        }

        LatLon end = path[^1];
        double pathFt = 0.0;
        double farthestFromEndFt = 0.0;
        for (int i = 1; i < path.Count; i++)
        {
            pathFt += GeoMath.DistanceNm(path[i - 1], path[i]) * GeoMath.FeetPerNm;
            farthestFromEndFt = Math.Max(farthestFromEndFt, GeoMath.DistanceNm(end, path[i - 1]) * GeoMath.FeetPerNm);
        }

        List<StandingTraffic> standing =
        [
            .. AircraftOnPath(self, all, path, (end, farthestFromEndFt + PathLateralFt))
                .Where(other => (other.AlongFt < pathFt) && (other.Aircraft.GroundSpeed <= GroundConflictDetector.HeldStationarySpeedKts))
                .OrderBy(other => other.AlongFt),
        ];
        if ((standing.Count > 0) && Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug(
                "[TaxiPathTraffic] {Callsign}: {Other} stands {Along:F0} ft along the path",
                self.Callsign,
                standing[0].Aircraft.Callsign,
                standing[0].AlongFt
            );
        }

        return standing;
    }

    /// <summary>
    /// The aircraft other than <paramref name="self"/> on <paramref name="path"/> (<see cref="OnPathOf"/>), each first ruled out
    /// by one distance check against <paramref name="reach"/>, the path's end and the farthest any part of the path lies from it.
    /// </summary>
    private static List<StandingTraffic> AircraftOnPath(
        AircraftState self,
        IReadOnlyList<AircraftState> all,
        List<LatLon> path,
        (LatLon End, double Ft) reach
    )
    {
        List<StandingTraffic> onPath = [];
        foreach (AircraftState other in all)
        {
            if (
                !ReferenceEquals(other, self)
                && other.IsOnGround
                && (GeoMath.DistanceNm(reach.End, other.Position) * GeoMath.FeetPerNm <= reach.Ft)
                && (OnPathOf(other, path) is { } found)
            )
            {
                onPath.Add(found);
            }
        }

        return onPath;
    }

    /// <summary>
    /// <paramref name="other"/> on <paramref name="path"/>: ahead of its first point and within <see cref="PathLateralFt"/> of a leg
    /// (<see cref="ProjectOntoPath"/>). Null otherwise.
    /// </summary>
    private static StandingTraffic? OnPathOf(AircraftState other, List<LatLon> path) =>
        ProjectOntoPath(path, other.Position) is { } alongFt and > 0.0 ? new StandingTraffic(other, alongFt) : null;

    /// <summary>
    /// Where <paramref name="point"/> lies along <paramref name="path"/> (ft from its start), taking the leg it is nearest; null when
    /// it is more than <see cref="PathLateralFt"/> from every leg.
    /// </summary>
    private static double? ProjectOntoPath(List<LatLon> path, LatLon point)
    {
        (double AlongFt, double LateralFt)? best = null;
        double legStartFt = 0.0;
        for (int i = 1; i < path.Count; i++)
        {
            double legFt = GeoMath.DistanceNm(path[i - 1], path[i]) * GeoMath.FeetPerNm;
            double legDeg = GeoMath.BearingTo(path[i - 1], path[i]);
            double toPointFt = GeoMath.DistanceNm(path[i - 1], point) * GeoMath.FeetPerNm;
            double offRad = GeoMath.SignedBearingDifference(legDeg, GeoMath.BearingTo(path[i - 1], point)) * Math.PI / 180.0;
            double x = toPointFt * Math.Cos(offRad);
            double y = toPointFt * Math.Sin(offRad);
            double alongFt = Math.Clamp(x, 0.0, legFt);
            double lateralFt = Math.Sqrt(((x - alongFt) * (x - alongFt)) + (y * y));
            if ((lateralFt <= PathLateralFt) && ((best is null) || (lateralFt < best.Value.LateralFt)))
            {
                best = (legStartFt + alongFt, lateralFt);
            }

            legStartFt += legFt;
        }

        return best?.AlongFt;
    }
}
