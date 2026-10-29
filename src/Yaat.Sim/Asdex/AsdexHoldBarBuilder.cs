using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Asdex;

/// <summary>
/// One ASDE-X Safety Logic hold bar: a straight line at the edge of the runway it protects, parallel to that runway, which a
/// surface display draws while the bar is Active.
/// </summary>
/// <param name="Id">Deterministic from the layout: <c>{protected runway}|{surface crossed}|{side}</c>, plus an ordinal when that repeats.</param>
/// <param name="ProtectedRunwayId">The safety-logic runway id the bar protects (e.g. "28R-10L").</param>
/// <param name="Points">The bar's two end points.</param>
/// <param name="AlongRunwayFt">Where the bar's centre lies along the protected runway, feet from its navdata end 1 toward end 2.</param>
public sealed record AsdexHoldBar(string Id, string ProtectedRunwayId, IReadOnlyList<LatLon> Points, double AlongRunwayFt);

/// <summary>A hold bar with the status a surface display last got for it: Active (drawn) or Suppressed (hidden).</summary>
public sealed record AsdexHoldBarState(AsdexHoldBar Bar, bool IsActive);

/// <summary>
/// One safety-logic runway as the hold-bar builder and status rule see it: its configuration id, the navdata runway that id
/// names, and the footprint ring the configuration carries (empty when it carries none).
/// </summary>
public sealed record AsdexHoldBarRunway(string Id, RunwayInfo Runway, IReadOnlyList<LatLon> Area);

/// <summary>
/// Builds an airport's fixed set of ASDE-X hold bars from its safety-logic runways and ground layout. Each runway gets one bar
/// per hold-short node of the layout that protects it, where the node's taxiway centerline meets the runway edge, and a bar
/// across every other safety-logic runway at each of its edges that runway's pavement runs over: two where it crosses, one on
/// its own side where it only meets the protected runway. A bar lies parallel to the runway it protects at half that runway's
/// width from its centerline. The set depends only on its inputs, so the same layout gives the same bars and ids on every run.
/// </summary>
public static class AsdexHoldBarBuilder
{
    private static readonly ILogger Log = SimLog.CreateLogger("AsdexHoldBarBuilder");

    /// <summary>The length of a taxiway bar where its taxiway meets the runway square on; a slanted taxiway's is 1 / sin(angle) longer.</summary>
    public const double TaxiwayBarSquareLengthFt = 190;

    /// <summary>The smallest taxiway-to-runway angle a taxiway bar's length is stretched for; a shallower taxiway gets this angle's length.</summary>
    public const double MinTaxiwayAngleDeg = 30;

    /// <summary>The widest gap between two taxiway bars on one side of a runway that still joins them into one bar.</summary>
    public const double MergeGapFt = 60;

    /// <summary>The two sides of a runway's centerline: -1 left, +1 right, facing end 2.</summary>
    private static readonly double[] Sides = [-1, 1];

    /// <summary>How many graph edges the walk from a hold-short node toward its runway follows before it gives up.</summary>
    private const int MaxWalkEdges = 8;

    /// <summary>
    /// The safety-logic runways of <paramref name="facilityId"/> that navdata resolves, in configuration order. A runway id is
    /// either the vNAS pair form ("28R-10L") or one end ("28R"); one navdata lacks is left out with a debug log.
    /// </summary>
    public static IReadOnlyList<AsdexHoldBarRunway> ResolveRunways(
        string facilityId,
        IReadOnlyList<AsdexRunwayConfig> runways,
        NavigationDatabase navDb
    )
    {
        IReadOnlyList<RunwayInfo> navRunways = AsdexSafetyLogicSeed.FacilityRunways(facilityId, navDb);
        var resolved = new List<AsdexHoldBarRunway>(runways.Count);
        foreach (AsdexRunwayConfig config in runways)
        {
            if (ResolveRunway(facilityId, config, navRunways) is { } runway)
            {
                resolved.Add(runway);
            }
        }

        return resolved;
    }

    /// <summary>
    /// The safety-logic runway <paramref name="config"/> names among <paramref name="facilityId"/>'s navdata runways
    /// (<see cref="AsdexSafetyLogicSeed.FacilityRunways"/>), or null with a debug log when navdata lacks it. Every entry naming
    /// one navdata runway gets the same <see cref="RunwayInfo"/> instance from one <paramref name="navRunways"/> list.
    /// </summary>
    internal static AsdexHoldBarRunway? ResolveRunway(string facilityId, AsdexRunwayConfig config, IReadOnlyList<RunwayInfo> navRunways)
    {
        RunwayInfo? nav = navRunways.FirstOrDefault(runway => NamesRunway(config.Id, runway));
        if (nav is null)
        {
            Log.LogDebug(
                "ASDE-X runway {RunwayId} at {FacilityId} gets no hold bars and no runway users: navdata does not have it",
                config.Id,
                facilityId
            );
            return null;
        }

        return new AsdexHoldBarRunway(config.Id, nav, config.AreaPoints);
    }

    /// <summary>The hold bars of <paramref name="runways"/> over <paramref name="layout"/>, grouped by protected runway in input order.</summary>
    public static IReadOnlyList<AsdexHoldBar> Build(IReadOnlyList<AsdexHoldBarRunway> runways, AirportGroundLayout layout)
    {
        var bars = new List<AsdexHoldBar>();
        foreach (AsdexHoldBarRunway protectedRunway in runways)
        {
            var axis = new AsdexRunwayAxis(protectedRunway.Runway);
            bool lowIsEnd1 = DesignatorNumber(protectedRunway.Runway.Id.End1) <= DesignatorNumber(protectedRunway.Runway.Id.End2);
            var taxiwayDrafts = new List<Draft>();
            foreach (GroundNode node in RunwayHoldBars.RunwayBars(layout, protectedRunway.Runway))
            {
                if (TaxiwayBar(node, protectedRunway.Runway, axis) is { } draft)
                {
                    taxiwayDrafts.Add(draft);
                }
            }

            List<Draft> drafts = Merge(taxiwayDrafts, lowIsEnd1);
            foreach (AsdexHoldBarRunway crossed in runways.Where(other => !ReferenceEquals(other, protectedRunway)))
            {
                drafts.AddRange(RunwayBars(protectedRunway, axis, crossed));
            }

            bars.AddRange(Name(protectedRunway, axis, drafts, lowIsEnd1));
        }

        return bars;
    }

    /// <summary>A bar before it is named: the surface it crosses, its centre and half-length in the protected runway's axis.</summary>
    internal sealed record Draft(string Surface, double Along, double Lateral, double HalfLengthFt)
    {
        public double Start => Along - HalfLengthFt;

        public double End => Along + HalfLengthFt;
    }

    /// <summary>
    /// Joins the taxiway bars on one side of the runway whose spans overlap or lie within <see cref="MergeGapFt"/> of each
    /// other into one bar spanning them all, named after the first taxiway along the runway from its lower-numbered end, so a
    /// wide taxiway throat with a hold-short node per centerline gets one bar. The spans are walked from that end by where
    /// each starts, so a long span from a shallow taxiway joins every bar it overlaps however far along its centre lies.
    /// </summary>
    internal static List<Draft> Merge(List<Draft> drafts, bool lowIsEnd1)
    {
        var merged = new List<Draft>();
        foreach (IGrouping<bool, Draft> side in drafts.GroupBy(draft => draft.Lateral > 0))
        {
            Draft? open = null;
            foreach (Draft draft in side.OrderBy(draft => StartFromLowEnd(draft, lowIsEnd1)))
            {
                if ((open is not null) && ((StartFromLowEnd(draft, lowIsEnd1) - EndFromLowEnd(open, lowIsEnd1)) <= MergeGapFt))
                {
                    double start = Math.Min(open.Start, draft.Start);
                    double end = Math.Max(open.End, draft.End);
                    open = open with { Along = (start + end) / 2, HalfLengthFt = (end - start) / 2 };
                    continue;
                }

                if (open is not null)
                {
                    merged.Add(open);
                }

                open = draft;
            }

            if (open is not null)
            {
                merged.Add(open);
            }
        }

        return merged;
    }

    /// <summary>Where the draft's span starts, in feet from the protected runway's lower-numbered end toward the other.</summary>
    private static double StartFromLowEnd(Draft draft, bool lowIsEnd1) => lowIsEnd1 ? draft.Start : -draft.End;

    /// <summary>Where the draft's span ends, in feet from the protected runway's lower-numbered end toward the other.</summary>
    private static double EndFromLowEnd(Draft draft, bool lowIsEnd1) => lowIsEnd1 ? draft.End : -draft.Start;

    private static IEnumerable<AsdexHoldBar> Name(AsdexHoldBarRunway protectedRunway, AsdexRunwayAxis axis, List<Draft> drafts, bool lowIsEnd1)
    {
        foreach (IGrouping<string, Draft> group in drafts.GroupBy(draft => $"{protectedRunway.Id}|{draft.Surface}|{Side(draft, lowIsEnd1)}"))
        {
            List<Draft> ordered = [.. group.OrderBy(draft => lowIsEnd1 ? draft.Along : -draft.Along)];
            for (int i = 0; i < ordered.Count; i++)
            {
                Draft draft = ordered[i];
                string id = ordered.Count == 1 ? group.Key : $"{group.Key}|{i + 1}";
                var a = axis.ToLatLon(draft.Along - draft.HalfLengthFt, draft.Lateral);
                var b = axis.ToLatLon(draft.Along + draft.HalfLengthFt, draft.Lateral);
                yield return new AsdexHoldBar(id, protectedRunway.Id, [a, b], draft.Along);
            }
        }
    }

    /// <summary>"R" or "L": the side of the protected runway's centerline, facing along it from its lower-numbered end.</summary>
    private static string Side(Draft draft, bool lowIsEnd1) => (draft.Lateral > 0) == lowIsEnd1 ? "R" : "L";

    private static int DesignatorNumber(string designator)
    {
        int digits = 0;
        foreach (char c in designator.TakeWhile(char.IsDigit))
        {
            digits = (digits * 10) + (c - '0');
        }

        return digits;
    }

    /// <summary>
    /// The bar where <paramref name="hold"/>'s taxiway centerline, followed through the graph toward the runway, comes within
    /// half the runway's width of its centerline; null when the walk never gets there.
    /// </summary>
    private static Draft? TaxiwayBar(GroundNode hold, RunwayInfo runway, AsdexRunwayAxis axis)
    {
        double halfWidth = runway.WidthFt / 2;
        double side = Math.Sign(axis.Lateral(hold.Position));
        if ((side == 0) || (side * axis.Lateral(hold.Position) <= halfWidth))
        {
            return null;
        }

        GroundNode current = hold;
        IGroundEdge? cameBy = null;
        for (int step = 0; step < MaxWalkEdges; step++)
        {
            (IGroundEdge Edge, List<LatLon> Path)? next = NextEdgeTowardRunway(current, cameBy, axis, side);
            if (next is not { } edge)
            {
                break;
            }

            for (int i = 1; i < edge.Path.Count; i++)
            {
                double from = side * axis.Lateral(edge.Path[i - 1]);
                double to = side * axis.Lateral(edge.Path[i]);
                if ((from > halfWidth) && (to <= halfWidth))
                {
                    return EdgeCrossing(axis, edge.Path[i - 1], edge.Path[i], (from - halfWidth) / (from - to), TaxiwayName(hold));
                }
            }

            cameBy = edge.Edge;
            current = edge.Edge.OtherNode(current);
        }

        Log.LogDebug("No hold bar for hold-short node {NodeId}: its taxiway never reaches runway {Runway}'s edge", hold.Id, runway.Id);
        return null;
    }

    /// <summary>
    /// The bar centred where the segment <paramref name="a"/>–<paramref name="b"/> crosses the runway edge at fraction
    /// <paramref name="t"/>.
    /// </summary>
    private static Draft EdgeCrossing(AsdexRunwayAxis axis, LatLon a, LatLon b, double t, string taxiway)
    {
        double alongA = axis.Along(a);
        double alongB = axis.Along(b);
        double latA = axis.Lateral(a);
        double latB = axis.Lateral(b);
        double dAlong = alongB - alongA;
        double dLat = latB - latA;
        double angleDeg = Math.Atan2(Math.Abs(dLat), Math.Abs(dAlong)) * 180.0 / Math.PI;
        double sin = Math.Sin(Math.Max(angleDeg, MinTaxiwayAngleDeg) * Math.PI / 180.0);
        return new Draft(taxiway, alongA + (t * dAlong), latA + (t * dLat), TaxiwayBarSquareLengthFt / sin / 2);
    }

    /// <summary>
    /// The edge out of <paramref name="node"/> (not <paramref name="cameBy"/>, not a runway centerline) whose far end lies
    /// nearest the runway centerline on <paramref name="side"/>, provided it gets nearer than <paramref name="node"/>; with
    /// the path along it from <paramref name="node"/>.
    /// </summary>
    private static (IGroundEdge Edge, List<LatLon> Path)? NextEdgeTowardRunway(
        GroundNode node,
        IGroundEdge? cameBy,
        AsdexRunwayAxis axis,
        double side
    )
    {
        double best = side * axis.Lateral(node.Position);
        (IGroundEdge, List<LatLon>)? chosen = null;
        foreach (IGroundEdge edge in node.Edges)
        {
            if (ReferenceEquals(edge, cameBy) || edge.IsRunwayCenterline)
            {
                continue;
            }

            double far = side * axis.Lateral(edge.OtherNode(node).Position);
            if (far < best)
            {
                best = far;
                chosen = (edge, PathFrom(edge, node));
            }
        }

        return chosen;
    }

    /// <summary>The edge's points from <paramref name="from"/> to its other node, its intermediate points included.</summary>
    private static List<LatLon> PathFrom(IGroundEdge edge, GroundNode from)
    {
        List<LatLon> path = [edge.Nodes[0].Position];
        if (edge is GroundEdge straight)
        {
            path.AddRange(straight.IntermediatePoints.Select(point => new LatLon(point.Lat, point.Lon)));
        }

        path.Add(edge.Nodes[1].Position);
        if (edge.Nodes[0].Id != from.Id)
        {
            path.Reverse();
        }

        return path;
    }

    /// <summary>The hold-short node's taxiway: its first edge that is a taxiway, else the node's own name.</summary>
    private static string TaxiwayName(GroundNode hold) =>
        hold.Edges.FirstOrDefault(edge =>
            !edge.IsRunwayCenterline && !edge.IsRamp && !edge.TaxiwayName.Contains(":link", StringComparison.OrdinalIgnoreCase)
        )?.TaxiwayName
        ?? hold.Name
        ?? "TWY";

    /// <summary>
    /// The bars across <paramref name="crossed"/> that protect <paramref name="protectedRunway"/> (whose axis is
    /// <paramref name="axis"/>): one at each edge of the protected runway that the crossed runway's pavement runs past, centred
    /// on the crossed centerline at half the protected runway's width from its own, where that point lies along the protected
    /// runway's pavement. A full crossing gets two; a runway whose end lies at or inside the protected runway's pavement gets
    /// one, on its own side, and none over ground it does not pave. Each is as long as the crossed runway is wide at that angle.
    /// </summary>
    private static IEnumerable<Draft> RunwayBars(AsdexHoldBarRunway protectedRunway, AsdexRunwayAxis axis, AsdexHoldBarRunway crossed)
    {
        var crossedAxis = new AsdexRunwayAxis(crossed.Runway);
        (double crossedStart, double crossedEnd) = PavementExtent(crossed, crossedAxis);
        var from = crossedAxis.ToLatLon(crossedStart, 0);
        var to = crossedAxis.ToLatLon(crossedEnd, 0);
        double a1 = axis.Along(from);
        double l1 = axis.Lateral(from);
        double a2 = axis.Along(to);
        double l2 = axis.Lateral(to);
        if (l1 == l2)
        {
            yield break;
        }

        (double start, double end) = PavementExtent(protectedRunway, axis);
        double sin = Math.Abs(l2 - l1) / Math.Sqrt(((a2 - a1) * (a2 - a1)) + ((l2 - l1) * (l2 - l1)));
        double halfWidth = protectedRunway.Runway.WidthFt / 2;
        foreach (double side in Sides)
        {
            // The crossed pavement must run past this edge and start at or inside it.
            bool runsPastEdge = (Math.Max(side * l1, side * l2) > halfWidth) && (Math.Min(side * l1, side * l2) <= halfWidth);
            double lateral = side * halfWidth;
            double along = a1 + ((lateral - l1) / (l2 - l1) * (a2 - a1));
            if (runsPastEdge && (along >= start) && (along <= end))
            {
                yield return new Draft(crossed.Id, along, lateral, crossed.Runway.WidthFt / sin / 2);
            }
        }
    }

    /// <summary>
    /// The runway's pavement along its own axis, feet from end 1: threshold to threshold, stretched to every point of the
    /// footprint ring its configuration carries.
    /// </summary>
    private static (double Start, double End) PavementExtent(AsdexHoldBarRunway runway, AsdexRunwayAxis axis)
    {
        double start = 0;
        double end = axis.LengthFt;
        foreach (LatLon point in runway.Area)
        {
            double along = axis.Along(point);
            start = Math.Min(start, along);
            end = Math.Max(end, along);
        }

        return (start, end);
    }

    /// <summary>True when every end the id names ("28R-10L", or "28R" alone) is an end of <paramref name="runway"/>.</summary>
    private static bool NamesRunway(string runwayId, RunwayInfo runway)
    {
        string[] ends = runwayId.Split('-');
        return (ends.Length is 1 or 2)
            && ends.All(end =>
                (RunwayIdentifier.NormalizeDesignator(end) is var normalized) && ((normalized == runway.Id.End1) || (normalized == runway.Id.End2))
            );
    }
}
