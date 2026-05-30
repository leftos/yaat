using System.Diagnostics;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// Result of a side-by-side comparison between two <see cref="ITaxiPathfinder"/>
/// implementations on the same input.
/// </summary>
public sealed record ComparisonResult(
    bool BothSucceeded,
    bool BothFailed,
    bool SameRoute,
    int V1SegmentCount,
    int V2SegmentCount,
    double V1TotalDistanceFt,
    double V2TotalDistanceFt,
    int V1UTurnCount,
    int V2UTurnCount,
    long V1ElapsedMs,
    long V2ElapsedMs,
    string? V1FailReason,
    string? V2FailReason
);

/// <summary>
/// Compares two <see cref="ITaxiPathfinder"/> implementations on the same routing
/// input and produces a structured <see cref="ComparisonResult"/>. Used by the
/// PathfinderGrid tests to verify harness wiring and to measure diff once v2 is real.
/// </summary>
public static class PathfinderComparison
{
    /// <summary>
    /// Heading change threshold above which a corner is counted as a U-turn.
    /// </summary>
    private const double UTurnThresholdDeg = 135.0;

    /// <summary>
    /// Feet per nautical mile conversion factor.
    /// </summary>
    private const double FeetPerNm = 6076.115;

    /// <summary>
    /// Calls <see cref="ITaxiPathfinder.FindRoute"/> on both implementations,
    /// records timing and route metrics, and returns a <see cref="ComparisonResult"/>.
    /// The route is considered the same when both return null, or when both return
    /// routes with identical taxiway sequences (segment-by-segment taxiway name match).
    /// </summary>
    public static ComparisonResult Compare(
        ITaxiPathfinder v1,
        ITaxiPathfinder v2,
        AirportGroundLayout layout,
        int fromNodeId,
        int toNodeId,
        AircraftCategory category = AircraftCategory.Jet
    )
    {
        var sw1 = Stopwatch.StartNew();
        var r1 = v1.FindRoute(layout, fromNodeId, toNodeId, category);
        sw1.Stop();

        var sw2 = Stopwatch.StartNew();
        var r2 = v2.FindRoute(layout, fromNodeId, toNodeId, category);
        sw2.Stop();

        return BuildResult(r1, r2, sw1.ElapsedMilliseconds, sw2.ElapsedMilliseconds, null, null);
    }

    /// <summary>
    /// Calls <see cref="ITaxiPathfinder.ResolveExplicitPath"/> on both implementations for the same
    /// named waypoint sequence, records timing and route metrics, and returns a
    /// <see cref="ComparisonResult"/>. The explicit-path analogue of <see cref="Compare"/>; the key
    /// metric is <c>V2UTurnCount &lt;= V1UTurnCount</c> — V2 must be no zig-zaggier than V1 on the same
    /// instructed taxi route over the same graph. A fresh copy of <paramref name="taxiwayNames"/> is
    /// passed to each implementation so neither can observe the other's mutations.
    /// </summary>
    public static ComparisonResult CompareExplicit(
        ITaxiPathfinder v1,
        ITaxiPathfinder v2,
        AirportGroundLayout layout,
        int fromNodeId,
        List<string> taxiwayNames,
        ExplicitPathOptions options,
        AircraftCategory category = AircraftCategory.Jet
    )
    {
        var sw1 = Stopwatch.StartNew();
        var r1 = v1.ResolveExplicitPath(layout, fromNodeId, [.. taxiwayNames], out string? f1, options, category);
        sw1.Stop();

        var sw2 = Stopwatch.StartNew();
        var r2 = v2.ResolveExplicitPath(layout, fromNodeId, [.. taxiwayNames], out string? f2, options, category);
        sw2.Stop();

        return BuildResult(r1, r2, sw1.ElapsedMilliseconds, sw2.ElapsedMilliseconds, f1, f2);
    }

    private static ComparisonResult BuildResult(TaxiRoute? r1, TaxiRoute? r2, long v1ElapsedMs, long v2ElapsedMs, string? v1Fail, string? v2Fail)
    {
        bool v1Ok = r1 is not null;
        bool v2Ok = r2 is not null;

        return new ComparisonResult(
            BothSucceeded: v1Ok && v2Ok,
            BothFailed: (!v1Ok) && (!v2Ok),
            SameRoute: RoutesMatch(r1, r2),
            V1SegmentCount: r1?.Segments.Count ?? 0,
            V2SegmentCount: r2?.Segments.Count ?? 0,
            V1TotalDistanceFt: (r1?.TotalDistanceNm ?? 0.0) * FeetPerNm,
            V2TotalDistanceFt: (r2?.TotalDistanceNm ?? 0.0) * FeetPerNm,
            V1UTurnCount: CountUTurns(r1),
            V2UTurnCount: CountUTurns(r2),
            V1ElapsedMs: v1ElapsedMs,
            V2ElapsedMs: v2ElapsedMs,
            V1FailReason: r1 is null ? (v1Fail ?? "No route found") : null,
            V2FailReason: r2 is null ? (v2Fail ?? "No route found") : null
        );
    }

    /// <summary>
    /// Produces a human-readable summary of a <see cref="ComparisonResult"/> for
    /// inclusion in test output.
    /// </summary>
    public static string FormatReport(ComparisonResult r)
    {
        if (r.BothFailed)
        {
            return "Both implementations returned no route.";
        }

        if (!r.BothSucceeded)
        {
            string winner = r.V1FailReason is null ? "V1" : "V2";
            string loser = r.V1FailReason is null ? "V2" : "V1";
            return $"Route disagreement: {winner} succeeded, {loser} failed ({(r.V1FailReason ?? r.V2FailReason)})";
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Both succeeded. Route match: {r.SameRoute}");
        sb.AppendLine($"  Segments  — V1: {r.V1SegmentCount, 4}  V2: {r.V2SegmentCount, 4}  delta: {r.V2SegmentCount - r.V1SegmentCount:+0;-0;0}");
        sb.AppendLine(
            $"  Dist (ft) — V1: {r.V1TotalDistanceFt, 8:F0}  V2: {r.V2TotalDistanceFt, 8:F0}  delta: {r.V2TotalDistanceFt - r.V1TotalDistanceFt:+0;-0;0}"
        );
        sb.AppendLine($"  U-turns   — V1: {r.V1UTurnCount, 4}  V2: {r.V2UTurnCount, 4}  delta: {r.V2UTurnCount - r.V1UTurnCount:+0;-0;0}");
        sb.AppendLine($"  Time (ms) — V1: {r.V1ElapsedMs, 4}  V2: {r.V2ElapsedMs, 4}");
        return sb.ToString().TrimEnd();
    }

    private static bool RoutesMatch(TaxiRoute? r1, TaxiRoute? r2)
    {
        if ((r1 is null) != (r2 is null))
        {
            return false;
        }
        if (r1 is null)
        {
            return true;
        }

        if (r1.Segments.Count != r2!.Segments.Count)
        {
            return false;
        }

        for (int i = 0; i < r1.Segments.Count; i++)
        {
            if (!string.Equals(r1.Segments[i].TaxiwayName, r2.Segments[i].TaxiwayName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    private static int CountUTurns(TaxiRoute? route)
    {
        if (route is null || route.Segments.Count < 2)
        {
            return 0;
        }

        int count = 0;
        for (int i = 1; i < route.Segments.Count; i++)
        {
            double prevBearing = route.Segments[i - 1].Edge.ArrivalBearing;
            double currBearing = route.Segments[i].Edge.DepartureBearing;
            double delta = Math.Abs(NormalizeAngle(currBearing - prevBearing));
            if (delta > UTurnThresholdDeg)
            {
                count++;
            }
        }
        return count;
    }

    private static double NormalizeAngle(double deg)
    {
        while (deg > 180.0)
        {
            deg -= 360.0;
        }
        while (deg < -180.0)
        {
            deg += 360.0;
        }
        return deg;
    }
}
