using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Data.Airport;

/// <summary>
/// A runway's hold-short bars and the band they mark: the bar nodes of each runway, which side of its centreline a point lies
/// on, how far from the centreline it lies, and whether it is nearer the centreline than the bars on its side. Shared by the
/// follower's hold-line oracle and the turn-about footprint check.
/// </summary>
public static class RunwayHoldBars
{
    private static readonly ILogger Log = SimLog.CreateLogger("RunwayHoldBars");

    /// <summary>
    /// Each layout's runway hold-short bar nodes by runway (<see cref="RunwayBarIndex"/>), built on first use and held with the
    /// layout — a new layout instance starts a new index — so a tick's hold-line checks never scan every node of the layout.
    /// </summary>
    private static readonly ConditionalWeakTable<AirportGroundLayout, RunwayBarIndex> BarIndexes = [];

    /// <summary>
    /// A layout's runway hold-short bar nodes with a runway, and those of each runway asked for so far with the side of its
    /// centreline each lies on, by the runway's airport and identifier.
    /// </summary>
    private sealed class RunwayBarIndex(GroundNode[] bars)
    {
        private readonly ConcurrentDictionary<(string AirportId, RunwayIdentifier Id), RunwayBarSides> _byRunway = new();

        public static RunwayBarIndex For(AirportGroundLayout layout) =>
            new([.. layout.Nodes.Values.Where(n => (n.Type == GroundNodeType.RunwayHoldShort) && (n.RunwayId is not null))]);

        public RunwayBarSides Of(RunwayInfo runway) =>
            _byRunway.TryGetValue((runway.AirportId, runway.Id), out RunwayBarSides? known)
                ? known
                : _byRunway.GetOrAdd((runway.AirportId, runway.Id), Filter(runway));

        private RunwayBarSides Filter(RunwayInfo runway)
        {
            GroundNode[] own = [.. bars.Where(n => (n.RunwayId is { } barRunway) && barRunway.Overlaps(runway.Id))];
            return new RunwayBarSides(own, [.. own.Select(bar => IsRightOfCentreline(runway, bar.Position))]);
        }
    }

    /// <summary>
    /// A runway's bar nodes and, for each, whether it lies right of the centreline (end 1 to end 2); and the marking of each bar
    /// asked for so far (<see cref="BarMarking"/>), by node id.
    /// </summary>
    /// <param name="Bars">The bar nodes.</param>
    /// <param name="IsRight">For each bar, in order, true right of the centreline.</param>
    private sealed record RunwayBarSides(GroundNode[] Bars, bool[] IsRight)
    {
        public ConcurrentDictionary<int, BarMarking> Markings { get; } = new();
    }

    /// <summary>The holding position marking painted at a bar (<see cref="DepthPastMarkingFt"/>).</summary>
    /// <param name="TowardRunwayDeg">The taxiway axis at the bar pointing toward the runway (deg true).</param>
    /// <param name="HalfWidthFt">Half the painted length: half the taxiway width (<see cref="HoldShortStandoff.TaxiwayWidthProxyFt"/>).</param>
    /// <param name="BesideLineFt">The distance (ft) from the centreline a part is judged against when the gear is off the stub (S_eff).</param>
    /// <param name="StubCutFt">
    /// How far back from the bar (ft, along the taxiway axis, away from the runway) the stub the bar marks ends (<see cref="StubCutOf"/>).
    /// </param>
    /// <param name="OffEnd">True when the bar's foot on the centreline extended falls beyond either runway end.</param>
    private sealed record BarMarking(double TowardRunwayDeg, double HalfWidthFt, double BesideLineFt, double StubCutFt, bool OffEnd);

    /// <summary>The runway hold-short bar nodes of <paramref name="runway"/>.</summary>
    /// <param name="layout">The airport's ground layout.</param>
    /// <param name="runway">The runway.</param>
    /// <returns>Its bar nodes, either side of the centreline.</returns>
    public static GroundNode[] RunwayBars(AirportGroundLayout layout, RunwayInfo runway) => SidesOf(layout, runway).Bars;

    private static RunwayBarSides SidesOf(AirportGroundLayout layout, RunwayInfo runway) =>
        BarIndexes.GetValue(layout, RunwayBarIndex.For).Of(runway);

    /// <summary>The runway hold-short bar nodes of <paramref name="runway"/> on <paramref name="point"/>'s side of its centreline.</summary>
    /// <param name="layout">The airport's ground layout.</param>
    /// <param name="runway">The runway.</param>
    /// <param name="point">The point.</param>
    /// <returns>Its bar nodes on that side, in the layout's order.</returns>
    public static IEnumerable<GroundNode> SameSideBars(AirportGroundLayout layout, RunwayInfo runway, LatLon point)
    {
        RunwayBarSides sides = SidesOf(layout, runway);
        bool right = IsRightOfCentreline(runway, point);
        for (int i = 0; i < sides.Bars.Length; i++)
        {
            if (sides.IsRight[i] == right)
            {
                yield return sides.Bars[i];
            }
        }
    }

    /// <summary>The nearest of <paramref name="runway"/>'s bars on <paramref name="point"/>'s side of its centreline.</summary>
    /// <param name="layout">The airport's ground layout.</param>
    /// <param name="runway">The runway.</param>
    /// <param name="point">The point.</param>
    /// <returns>The bar, the first in the layout's order on a tie; null when that side has no bar.</returns>
    public static GroundNode? NearestSameSideBar(AirportGroundLayout layout, RunwayInfo runway, LatLon point)
    {
        GroundNode? nearest = null;
        double nearestNm = double.MaxValue;
        foreach (GroundNode bar in SameSideBars(layout, runway, point))
        {
            double distanceNm = GeoMath.DistanceNm(bar.Position, point);
            if (distanceNm < nearestNm)
            {
                nearest = bar;
                nearestNm = distanceNm;
            }
        }

        return nearest;
    }

    /// <summary>How far (ft) <paramref name="point"/> lies from <paramref name="runway"/>'s centreline, between its ends.</summary>
    /// <param name="runway">The runway.</param>
    /// <param name="point">The point.</param>
    /// <returns>The distance (ft).</returns>
    public static double CentrelineFt(RunwayInfo runway, LatLon point) =>
        GeoMath.DistanceToSegmentFt(point, new LatLon(runway.Lat1, runway.Lon1), new LatLon(runway.Lat2, runway.Lon2));

    /// <summary>Which side of <paramref name="runway"/>'s centreline, end 1 to end 2, <paramref name="point"/> lies on: true to the right.</summary>
    /// <param name="runway">The runway.</param>
    /// <param name="point">The point.</param>
    /// <returns>True right of the centreline.</returns>
    public static bool IsRightOfCentreline(RunwayInfo runway, LatLon point)
    {
        var end1 = new LatLon(runway.Lat1, runway.Lon1);
        double alongDeg = GeoMath.BearingTo(end1, new LatLon(runway.Lat2, runway.Lon2));
        return GeoMath.SignedBearingDifference(alongDeg, GeoMath.BearingTo(end1, point)) > 0.0;
    }

    /// <summary>
    /// Whether <paramref name="point"/>, a part of an aircraft whose main gear is at <paramref name="gearPosition"/>, is past the
    /// holding position marking (<see cref="DepthPastMarkingFt"/>) of the nearest of <paramref name="runway"/>'s bars on the
    /// point's side (pavement aside); false when that side has no bar.
    /// </summary>
    /// <param name="layout">The airport's ground layout.</param>
    /// <param name="runway">The runway.</param>
    /// <param name="point">The point.</param>
    /// <param name="gearPosition">The main-gear position of the aircraft the point belongs to.</param>
    /// <returns>True inside the bars' line.</returns>
    public static bool IsInsideBarLine(AirportGroundLayout layout, RunwayInfo runway, LatLon point, LatLon gearPosition) =>
        (NearestSameSideBar(layout, runway, point) is { } nearest) && (DepthPastMarkingFt(layout, runway, nearest, point, gearPosition) > 0.0);

    /// <summary>
    /// How far (ft) <paramref name="point"/>, a part of an aircraft whose main gear is at <paramref name="gearPosition"/>, lies
    /// past the holding position marking painted at <paramref name="bar"/> toward <paramref name="runway"/> (negative short of it;
    /// AIM 2-3-5.a, 4-3-21.b). The marking is a segment through the bar node, square to the taxiway axis there
    /// (<see cref="TaxiwayAxisBearingDeg(RunwayInfo, GroundNode)"/>), half the taxiway width W
    /// (<see cref="HoldShortStandoff.TaxiwayWidthProxyFt"/>) each side of the node.
    /// <para>
    /// The paint governs the aircraft whose main gear is on the stub it marks (AIM 2-3-5.a.1, 7110.65 3-7-4 NOTE): within W/2 of
    /// the taxiway axis and, along it, between the runway edge and the stub's cut (<see cref="StubCutOf"/>). Every part of that
    /// aircraft is measured past the painted line extended, along the taxiway axis, but never less than the safety floor: how
    /// far the part lies inside the runway half-width plus <see cref="RunwayOccupancy.LateralSlackFt"/>.
    /// </para>
    /// <para>
    /// With the gear off the stub — on a parallel taxiway whose wingtip reaches over the stub's paint, or on the runway — every
    /// part is measured against the runway's standoff S (<see cref="GroundRunway.HoldShortStandoffFt"/>, else
    /// <see cref="GroundRunway.HoldShortDistanceFt"/>), never farther out than the bar itself, as a line parallel to the
    /// centreline; against the bar's own distance from the centreline segment when <paramref name="layout"/> has no ground
    /// runway of that name. At a bar whose foot on the centreline extended falls beyond either runway end, a part over the
    /// bar's paint (within W/2 of the bar along its line) is measured past the painted line extended as well, and never reads
    /// short of that standoff line.
    /// </para>
    /// </summary>
    /// <param name="layout">The airport's ground layout.</param>
    /// <param name="runway">The runway.</param>
    /// <param name="bar">The bar whose marking is judged.</param>
    /// <param name="point">The point.</param>
    /// <param name="gearPosition">The main-gear position of the aircraft the point belongs to; the point itself for a bare point.</param>
    /// <returns>The depth past the marking (ft).</returns>
    public static double DepthPastMarkingFt(AirportGroundLayout layout, RunwayInfo runway, GroundNode bar, LatLon point, LatLon gearPosition)
    {
        BarMarking marking = MarkingAt(layout, runway, bar);
        double pointFt = CentrelineFt(runway, point);
        if (IsGearOnTheStub(runway, marking, bar, gearPosition))
        {
            double floorFt = (runway.WidthFt / 2.0) + RunwayOccupancy.LateralSlackFt - pointFt;
            return Math.Max(LineFrame(marking.TowardRunwayDeg, bar, point).PastFt, floorFt);
        }

        (double offsetFt, double pastFt) = LineFrame(marking.TowardRunwayDeg, bar, point);
        double besideFt = marking.BesideLineFt - pointFt;
        return (marking.OffEnd && (Math.Abs(offsetFt) <= marking.HalfWidthFt)) ? Math.Max(pastFt, besideFt) : besideFt;
    }

    /// <summary>
    /// Whether <paramref name="gearPosition"/> lies on the stub <paramref name="bar"/>'s marking governs: within the paint's
    /// half-width of the taxiway axis and, along the axis, between the runway edge and the stub's cut — its foot on the axis
    /// outside the runway half-width and on the bar's side of the centreline.
    /// </summary>
    private static bool IsGearOnTheStub(RunwayInfo runway, BarMarking marking, GroundNode bar, LatLon gearPosition)
    {
        (double offsetFt, double pastFt) = LineFrame(marking.TowardRunwayDeg, bar, gearPosition);
        if ((Math.Abs(offsetFt) > marking.HalfWidthFt) || (-pastFt > marking.StubCutFt))
        {
            return false;
        }

        double towardDeg = (pastFt >= 0.0) ? marking.TowardRunwayDeg : marking.TowardRunwayDeg + 180.0;
        LatLon foot = GeoMath.ProjectPoint(bar.Position, new TrueHeading(towardDeg), Math.Abs(pastFt) / GeoMath.FeetPerNm);
        return (CentrelineFt(runway, foot) > runway.WidthFt / 2.0)
            && (IsRightOfCentreline(runway, foot) == IsRightOfCentreline(runway, bar.Position));
    }

    /// <summary>
    /// Where <paramref name="point"/> lies against the marking at <paramref name="bar"/> square to <paramref name="towardRunwayDeg"/>:
    /// its offset along the line from the node, and its distance past the line along the taxiway axis toward the runway (both ft).
    /// </summary>
    private static (double OffsetFt, double PastFt) LineFrame(double towardRunwayDeg, GroundNode bar, LatLon point)
    {
        double distanceFt = GeoMath.DistanceNm(bar.Position, point) * GeoMath.FeetPerNm;
        double angleRad = GeoMath.SignedBearingDifference(towardRunwayDeg, GeoMath.BearingTo(bar.Position, point)) * Math.PI / 180.0;
        return (distanceFt * Math.Sin(angleRad), distanceFt * Math.Cos(angleRad));
    }

    /// <summary>
    /// How far back from <paramref name="bar"/> (ft, along the taxiway axis away from <paramref name="runway"/>) the stub its
    /// marking governs ends (<see cref="StubCutOf"/>).
    /// </summary>
    /// <param name="layout">The airport's ground layout.</param>
    /// <param name="runway">The runway.</param>
    /// <param name="bar">The bar.</param>
    /// <returns>The cut (ft back from the bar).</returns>
    internal static double StubCutFt(AirportGroundLayout layout, RunwayInfo runway, GroundNode bar) => MarkingAt(layout, runway, bar).StubCutFt;

    /// <summary>
    /// The distance (ft) from <paramref name="runway"/>'s centreline that a part of an aircraft whose gear is off the stub of
    /// <paramref name="bar"/> is judged against (<see cref="DepthPastMarkingFt"/>): the runway's standoff S
    /// (<see cref="GroundRunway.HoldShortStandoffFt"/>, else <see cref="GroundRunway.HoldShortDistanceFt"/>), never farther out
    /// than the bar itself; the bar's own distance when <paramref name="layout"/> has no ground runway of that name. A part
    /// over an off-end bar's paint is judged past the painted line instead, never less than this line.
    /// </summary>
    /// <param name="layout">The airport's ground layout.</param>
    /// <param name="runway">The runway.</param>
    /// <param name="bar">The bar.</param>
    /// <returns>The beside line (ft from the centreline).</returns>
    internal static double BesideLineFt(AirportGroundLayout layout, RunwayInfo runway, GroundNode bar) => MarkingAt(layout, runway, bar).BesideLineFt;

    private static BarMarking MarkingAt(AirportGroundLayout layout, RunwayInfo runway, GroundNode bar) =>
        SidesOf(layout, runway).Markings.GetOrAdd(bar.Id, static (_, args) => MarkingOf(args.layout, args.runway, args.bar), (layout, runway, bar));

    private static BarMarking MarkingOf(AirportGroundLayout layout, RunwayInfo runway, GroundNode bar)
    {
        GroundRunway? groundRunway = layout.Runways.FirstOrDefault(r => r.Id.Overlaps(runway.Id));
        double barFt = CentrelineFt(runway, bar.Position);
        bool beyondAnEnd = GeoMath
            .FootOfPerpendicular(bar.Position, new LatLon(runway.Lat1, runway.Lon1), new LatLon(runway.Lat2, runway.Lon2))
            .Clamped;
        double besideFt = (groundRunway?.HoldShortStandoffFt ?? groundRunway?.HoldShortDistanceFt) is { } standoffFt
            ? Math.Min(standoffFt, barFt)
            : barFt;
        double towardRunwayDeg = new TrueHeading(TaxiwayAxisBearingDeg(runway, bar) + 180.0).Degrees;
        double halfWidthFt = HoldShortStandoff.TaxiwayWidthProxyFt(runway.WidthFt) / 2.0;
        return new BarMarking(towardRunwayDeg, halfWidthFt, besideFt, StubCutOf(bar, towardRunwayDeg, halfWidthFt), beyondAnEnd);
    }

    /// <summary>
    /// How far back from <paramref name="bar"/> (ft, along the taxiway axis away from the runway) the stub it marks ends:
    /// walking back along the bar's taxiway (<see cref="BarTaxiwayName"/>, edges of exactly that name, fillet arcs of that one
    /// name included), the first node past the bar where a straight edge of another taxiway meets it — a joining taxiway; a
    /// node whose only other-named edges are fillet arcs is stepped over. The cut is that node's distance back less the
    /// joining taxiway's half-width (<paramref name="halfWidthFt"/>: the layout carries no taxiway width, so the same proxy
    /// the paint uses); with nothing joining, the node where the taxiway's chain ends. Zero when the bar has no named taxiway
    /// or no edge of it leading back.
    /// </summary>
    /// <param name="bar">The bar node.</param>
    /// <param name="towardRunwayDeg">The taxiway axis at the bar pointing toward the runway (deg true).</param>
    /// <param name="halfWidthFt">The taxiway half-width proxy (ft).</param>
    /// <returns>The cut (ft back from the bar).</returns>
    private static double StubCutOf(GroundNode bar, double towardRunwayDeg, double halfWidthFt)
    {
        if (BarTaxiwayName(bar) is not { } taxiway)
        {
            return 0.0;
        }

        bool IsTaxiway(IGroundEdge edge) => string.Equals(edge.TaxiwayName, taxiway, StringComparison.Ordinal);
        double BackFt(GroundNode node) => -LineFrame(towardRunwayDeg, bar, node.Position).PastFt;

        IGroundEdge? edge = bar.Edges.Where(e => IsTaxiway(e) && !e.IsRunwayCenterline).MaxBy(e => BackFt(e.OtherNode(bar)));
        if ((edge is null) || (BackFt(edge.OtherNode(bar)) <= 0.0))
        {
            return 0.0;
        }

        (double cutFt, string? joiner) = WalkBackFt(bar, edge, IsTaxiway, BackFt, halfWidthFt);
        Log.LogDebug("Bar #{BarId} on {Taxiway}: stub cut {CutFt:F1} ft back, {Joiner}", bar.Id, taxiway, cutFt, joiner ?? "chain end");
        return cutFt;
    }

    /// <summary>
    /// Where the stub <paramref name="bar"/> marks ends, walked back from the bar along its taxiway from <paramref name="first"/>:
    /// the joining straight taxiway's node less its half-width, the chain's end when nothing joins, or the node reached when the
    /// walk closes a loop.
    /// </summary>
    /// <param name="bar">The bar node.</param>
    /// <param name="first">The bar's first edge back along its taxiway.</param>
    /// <param name="isTaxiway">Whether an edge is of the bar's taxiway.</param>
    /// <param name="backFt">How far a node lies back from the bar along its axis (ft).</param>
    /// <param name="halfWidthFt">The joining taxiway's half-width proxy (ft).</param>
    /// <returns>The cut (ft back from the bar) and the joining taxiway's name, or null for a chain end.</returns>
    private static (double CutFt, string? Joiner) WalkBackFt(
        GroundNode bar,
        IGroundEdge first,
        Func<IGroundEdge, bool> isTaxiway,
        Func<GroundNode, double> backFt,
        double halfWidthFt
    )
    {
        IGroundEdge edge = first;
        GroundNode at = bar;
        var visited = new HashSet<int> { bar.Id };
        while (true)
        {
            GroundNode next = edge.OtherNode(at);
            if (!visited.Add(next.Id))
            {
                return (backFt(at), null);
            }

            IGroundEdge[] others = [.. next.Edges.Where(e => !ReferenceEquals(e, edge))];
            IGroundEdge? joiner = others.FirstOrDefault(e => (e is GroundEdge) && !isTaxiway(e));
            if (joiner is not null)
            {
                return (backFt(next) - halfWidthFt, joiner.TaxiwayName);
            }

            IGroundEdge[] onward = [.. others.Where(isTaxiway)];
            if (onward.Length == 0)
            {
                return (backFt(next), null);
            }

            double inboundDeg = GeoMath.BearingTo(at.Position, next.Position);
            edge = onward.MinBy(e => GeoMath.AbsBearingDifference(inboundDeg, BearingAtBar(next, e)))!;
            at = next;
        }
    }

    /// <summary>
    /// The taxiway a runway bar sits on: the name of its straight edge leading off the runway — not along a runway centreline,
    /// toward a node with no runway-centreline edge — else of any straight edge not along a centreline, the ordinal-first name
    /// when several qualify, so the answer does not depend on edge order. A fillet arc's joined name ("C - G") never names a
    /// bar's taxiway. Null when no straight edge is named.
    /// </summary>
    /// <param name="bar">The bar node.</param>
    /// <returns>The taxiway name, or null.</returns>
    public static string? BarTaxiwayName(GroundNode bar)
    {
        List<GroundEdge> offRunway = NamedStraightOffCentreline(bar);
        List<GroundEdge> leadingOff = [.. offRunway.Where(e => !e.OtherNode(bar).Edges.Any(other => other.IsRunwayCenterline))];
        return ((leadingOff.Count > 0) ? leadingOff : offRunway).Select(e => e.TaxiwayName).Order(StringComparer.Ordinal).FirstOrDefault();
    }

    /// <summary>
    /// The straight edges at <paramref name="bar"/> of the taxiway it sits on (<see cref="BarTaxiwayName"/>), none along a
    /// runway centreline; empty when no straight edge is named.
    /// </summary>
    /// <param name="bar">The bar node.</param>
    /// <returns>The bar's taxiway edges.</returns>
    public static List<GroundEdge> BarTaxiwayEdges(GroundNode bar)
    {
        string? taxiway = BarTaxiwayName(bar);
        return [.. NamedStraightOffCentreline(bar).Where(e => string.Equals(e.TaxiwayName, taxiway, StringComparison.Ordinal))];
    }

    /// <summary>
    /// Every named straight edge at <paramref name="bar"/> not along a runway centreline, whatever its taxiway: a bar where two
    /// taxiways meet carries both names.
    /// </summary>
    /// <param name="bar">The bar node.</param>
    /// <returns>The bar's named straight off-centreline edges.</returns>
    internal static List<GroundEdge> NamedStraightOffCentreline(GroundNode bar) =>
        [.. bar.Edges.OfType<GroundEdge>().Where(e => !e.IsRunwayCenterline && !string.IsNullOrEmpty(e.TaxiwayName))];

    /// <summary>
    /// The taxiway axis at <paramref name="bar"/>, pointing away from <paramref name="runway"/>, over the straight edges of the
    /// bar's own taxiway (<see cref="BarTaxiwayEdges"/>), the same edges the follower squares its hold line to.
    /// </summary>
    /// <param name="runway">The runway the bar protects.</param>
    /// <param name="bar">The bar node.</param>
    /// <returns>The bearing (deg true).</returns>
    internal static double TaxiwayAxisBearingDeg(RunwayInfo runway, GroundNode bar) => TaxiwayAxisBearingDeg(runway, bar, [.. BarTaxiwayEdges(bar)]);

    /// <summary>
    /// The taxiway axis at <paramref name="bar"/> over <paramref name="edges"/>, pointing away from <paramref name="runway"/>. The
    /// edge whose far node lies nearest the centreline leads toward the runway; the edge that continues it straightest through the
    /// bar leads away. Two straight edges give the bearing from the near neighbour to the far one; an arc among them gives the mean
    /// of the two edges' bearings at the bar (an arc's tangent there). One edge gives its own bearing at the bar, oriented away
    /// from the runway; none, the bearing straight out from the centreline to the bar.
    /// </summary>
    /// <param name="runway">The runway the bar protects.</param>
    /// <param name="bar">The bar node.</param>
    /// <param name="edges">Edges at the bar carrying the taxiway.</param>
    /// <returns>The bearing (deg true).</returns>
    internal static double TaxiwayAxisBearingDeg(RunwayInfo runway, GroundNode bar, IReadOnlyList<IGroundEdge> edges)
    {
        if (edges.Count == 0)
        {
            LatLon foot = GeoMath.FootOfPerpendicular(bar.Position, new LatLon(runway.Lat1, runway.Lon1), new LatLon(runway.Lat2, runway.Lon2)).Foot;
            return GeoMath.BearingTo(foot, bar.Position);
        }

        IGroundEdge near = edges.MinBy(edge => CentrelineFt(runway, edge.OtherNode(bar).Position))!;
        double fromNearDeg = new TrueHeading(BearingAtBar(bar, near) + 180.0).Degrees;
        if (edges.Count == 1)
        {
            bool nearIsInward = CentrelineFt(runway, near.OtherNode(bar).Position) < CentrelineFt(runway, bar.Position);
            return nearIsInward ? fromNearDeg : BearingAtBar(bar, near);
        }

        IGroundEdge far = edges
            .Where(edge => !ReferenceEquals(edge, near))
            .MinBy(edge => GeoMath.AbsBearingDifference(fromNearDeg, BearingAtBar(bar, edge)))!;
        if ((near is GroundEdge) && (far is GroundEdge))
        {
            return GeoMath.BearingTo(near.OtherNode(bar).Position, far.OtherNode(bar).Position);
        }

        double halfTurnDeg = GeoMath.SignedBearingDifference(fromNearDeg, BearingAtBar(bar, far)) / 2.0;
        return new TrueHeading(fromNearDeg + halfTurnDeg).Degrees;
    }

    /// <summary>
    /// The bearing (deg true) of <paramref name="edge"/> leaving <paramref name="bar"/>: an arc's tangent there, else toward its other node.
    /// </summary>
    private static double BearingAtBar(GroundNode bar, IGroundEdge edge) =>
        (edge is GroundArc arc) ? arc.TangentBearingAt(bar, bar) : GeoMath.BearingTo(bar.Position, edge.OtherNode(bar).Position);
}
