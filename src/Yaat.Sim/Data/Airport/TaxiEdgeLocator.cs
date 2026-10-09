using Yaat.Sim.Phases.Ground;

namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Finds the straight edge a ground aircraft is rolling on, looked for first around the edge the aircraft was last on, and
/// across the whole layout only when nothing there is within <see cref="OnTaxiwayMaxOffsetFt"/>. <see cref="EdgeUnder"/> finds
/// a taxi edge, among the edges <see cref="AirportGroundLayout.FindNearestTaxiEdge(LatLon)"/> considers (no fillet arc, runway
/// centreline or ramp connector): the taxiway a <see cref="FollowingPhase"/> follower or its lead is on.
/// <see cref="DrivenEdgeUnder"/> counts the ramp connectors too: the edges a <see cref="TaxiEdgeTrail"/> records an aircraft
/// driving.
/// </summary>
public static class TaxiEdgeLocator
{
    /// <summary>How far (ft) off a taxiway's centreline a moving aircraft still counts as on that taxiway.</summary>
    public const double OnTaxiwayMaxOffsetFt = 50.0;

    /// <summary>
    /// How far (ft) an aircraft may stand off a fillet arc's curve and still count as on it, so a follow route starts partway
    /// round that arc. The navigator's mid-curve entry on the KOAK ramp→B fillet absorbed 1.2 to 8.4 ft offsets with about
    /// 30 ft of arc left without a teleport; the offset it absorbs scales with the arc left, about 1.3 × the arc left at a
    /// 3 ft sub-tick step (8.6 ft off with 4 ft left moved 6.7 ft in a 2.8 ft step).
    /// </summary>
    public const double OnFilletArcMaxOffsetFt = 8.0;

    /// <summary>
    /// The straight taxi edge under <paramref name="position"/>, ramp connectors excluded, or null off every taxiway.
    /// <paramref name="lastEdge"/> is the edge last found, by its end node ids (<c>Nodes[0]</c>, <c>Nodes[1]</c>), or null when
    /// there is none.
    /// </summary>
    public static GroundEdge? EdgeUnder(AirportGroundLayout layout, LatLon position, (int NodeA, int NodeB)? lastEdge) =>
        NearestAroundLast(layout, position, lastEdge, includeRamps: false) ?? NearestAnywhere(layout, position, includeRamps: false);

    /// <summary>
    /// The straight edge an aircraft at <paramref name="position"/> drives, a ramp connector included, or null off every one:
    /// what <see cref="Simulation.SimulationEngine.TickTaxiEdgeTrails"/> records, so an aircraft leaving a ramp has the ramp
    /// edge in its trail. <paramref name="lastEdge"/> is as for <see cref="EdgeUnder"/>. While the aircraft rounds a fillet arc
    /// meeting <paramref name="lastEdge"/> (<see cref="FilletArcUnder"/>, nearer than the straight edge found), it is still the
    /// last edge: the straight edges a fillet cuts past are not driven.
    /// </summary>
    public static GroundEdge? DrivenEdgeUnder(AirportGroundLayout layout, LatLon position, (int NodeA, int NodeB)? lastEdge)
    {
        GroundEdge? under = DrivenStraightUnder(layout, position, lastEdge);
        return (RoundingArc(layout, position, lastEdge, under) is not null) ? Resolve(layout, lastEdge) : under;
    }

    /// <summary>
    /// The straight edge under <paramref name="position"/>, a ramp connector included, looked for around
    /// <paramref name="lastEdge"/> first and across the whole layout when nothing there is near enough; null off every one.
    /// </summary>
    private static GroundEdge? DrivenStraightUnder(AirportGroundLayout layout, LatLon position, (int NodeA, int NodeB)? lastEdge) =>
        NearestAroundLast(layout, position, lastEdge, includeRamps: true) ?? NearestAnywhere(layout, position, includeRamps: true);

    /// <summary>
    /// The fillet arc meeting <paramref name="lastEdge"/> that an aircraft at <paramref name="position"/> is rounding: the one
    /// <see cref="FilletArcUnder"/> finds, nearer than the straight edge under the position (ramp connectors included) unless
    /// that edge is the last edge itself. Null off every such arc, or with no last edge. While it is non-null,
    /// <see cref="DrivenEdgeUnder"/> keeps the last edge.
    /// </summary>
    public static GroundArc? FilletArcRounding(AirportGroundLayout layout, LatLon position, (int NodeA, int NodeB)? lastEdge) =>
        RoundingArc(layout, position, lastEdge, DrivenStraightUnder(layout, position, lastEdge));

    /// <summary>
    /// The fillet arc ending at <paramref name="end"/> that an aircraft at <paramref name="position"/> is on, whichever way it is
    /// driving it, with the parameter of the arc's curve nearest the position: the nearest such arc whose curve passes within
    /// <see cref="OnTaxiwayMaxOffsetFt"/> of the position at a point strictly between the arc's ends, and nearer than the
    /// straight edge under the position (looked for around <paramref name="lastEdge"/>, ramp connectors included). Unlike
    /// <see cref="FilletArcRounding"/>, the straight edge found may be the last edge itself. Null when there is none.
    /// </summary>
    public static (GroundArc Arc, double T)? FilletArcEndingAt(
        AirportGroundLayout layout,
        LatLon position,
        GroundNode end,
        (int NodeA, int NodeB)? lastEdge
    )
    {
        GroundEdge? under = DrivenStraightUnder(layout, position, lastEdge);
        double underFt =
            (under is null) ? double.PositiveInfinity : GeoMath.DistanceToSegmentFt(position, under.Nodes[0].Position, under.Nodes[1].Position);
        return NearestInsideArc(end.Edges.OfType<GroundArc>(), position, underFt) is { } nearest ? (nearest.Arc, nearest.T) : null;
    }

    /// <summary><see cref="FilletArcRounding"/> with the straight edge under the position, <paramref name="under"/>, already found.</summary>
    private static GroundArc? RoundingArc(AirportGroundLayout layout, LatLon position, (int NodeA, int NodeB)? lastEdge, GroundEdge? under)
    {
        if ((under is null) || (Resolve(layout, lastEdge) is not { } last) || (under == last))
        {
            return null;
        }

        double underFt = GeoMath.DistanceToSegmentFt(position, under.Nodes[0].Position, under.Nodes[1].Position);
        return FilletArcUnder(last, position, underFt);
    }

    /// <summary>The straight edge <paramref name="ends"/> names by its end node ids; null with none, or none the layout holds.</summary>
    private static GroundEdge? Resolve(AirportGroundLayout layout, (int NodeA, int NodeB)? ends) =>
        ends is { } nodes ? StraightEdgeJoining(layout, nodes.NodeA, nodes.NodeB) : null;

    /// <summary>
    /// The straight edge of <paramref name="layout"/> joining the nodes <paramref name="nodeA"/> and <paramref name="nodeB"/>, or
    /// null when it has none.
    /// </summary>
    internal static GroundEdge? StraightEdgeJoining(AirportGroundLayout layout, int nodeA, int nodeB)
    {
        if (!layout.Nodes.TryGetValue(nodeA, out GroundNode? node))
        {
            return null;
        }

        foreach (IGroundEdge edge in node.Edges)
        {
            if ((edge is GroundEdge straight) && (straight.OtherNode(node).Id == nodeB))
            {
                return straight;
            }
        }

        return null;
    }

    /// <summary>Refinement passes <see cref="CubicBezier.ClosestT(LatLon, int)"/> runs for the on-arc test.</summary>
    private const int ArcClosestIterations = 24;

    /// <summary>
    /// The fillet arc meeting <paramref name="edge"/> at either of its end nodes that <paramref name="position"/> is on: the
    /// nearest whose curve passes within <see cref="OnTaxiwayMaxOffsetFt"/> of it, and nearer than
    /// <paramref name="nearerThanFt"/>, at a point strictly between the arc's ends. Null when there is none. While an aircraft
    /// rounds such an arc, <see cref="DrivenEdgeUnder"/> keeps the edge it came off rather than a straight edge the arc cuts
    /// past.
    /// </summary>
    private static GroundArc? FilletArcUnder(GroundEdge edge, LatLon position, double nearerThanFt) =>
        NearestInsideArc(edge.Nodes.SelectMany(node => node.Edges.OfType<GroundArc>()), position, nearerThanFt)?.Arc;

    /// <summary>
    /// Of <paramref name="arcs"/> (runway centrelines aside), the one <paramref name="position"/> is nearest, with the parameter of
    /// its curve's point nearest the position: its curve passing within <see cref="OnTaxiwayMaxOffsetFt"/> of the position, and
    /// nearer than <paramref name="nearerThanFt"/>, at a point strictly between the arc's ends. Null when there is none.
    /// </summary>
    private static (GroundArc Arc, double T)? NearestInsideArc(IEnumerable<GroundArc> arcs, LatLon position, double nearerThanFt)
    {
        (GroundArc Arc, double T)? best = null;
        double bestFt = Math.Min(nearerThanFt, OnTaxiwayMaxOffsetFt);
        foreach (GroundArc arc in arcs)
        {
            if (!arc.IsRunwayCenterline && (InsideClosestPoint(arc.ToBezier(), position) is { } closest) && (closest.OffFt < bestFt))
            {
                best = (arc, closest.T);
                bestFt = closest.OffFt;
            }
        }

        return best;
    }

    /// <summary>
    /// The distance (ft) from <paramref name="position"/> to the nearest point of <paramref name="arc"/>'s curve, or null when
    /// that point is one of the curve's ends: the position lies past the arc, not beside it.
    /// </summary>
    internal static double? InsideArcDistanceFt(GroundArc arc, LatLon position) => InsideClosestPoint(arc.ToBezier(), position)?.OffFt;

    /// <summary>The parameter of <paramref name="arc"/>'s curve at its point nearest <paramref name="position"/>, its ends included.</summary>
    internal static double ClosestT(GroundArc arc, LatLon position) => arc.ToBezier().ClosestT(position, ArcClosestIterations);

    /// <summary>How near either end of a curve (in its parameter) a closest point counts as that end rather than partway round.</summary>
    private const double CurveEndToleranceT = 1e-3;

    /// <summary>
    /// The point of <paramref name="curve"/> nearest <paramref name="position"/>: its parameter and its distance (ft) from the
    /// position. Null when that point is one of the curve's ends (within <see cref="CurveEndToleranceT"/>): the position lies
    /// past the curve, not beside it.
    /// </summary>
    internal static (double T, double OffFt)? InsideClosestPoint(CubicBezier curve, LatLon position)
    {
        double t = curve.ClosestT(position, ArcClosestIterations);
        if ((t <= CurveEndToleranceT) || (t >= (1.0 - CurveEndToleranceT)))
        {
            return null;
        }

        (double lat, double lon) = curve.Evaluate(t);
        return (t, GeoMath.DistanceNm(position, new LatLon(lat, lon)) * GeoMath.FeetPerNm);
    }

    /// <summary>
    /// The nearest straight edge within <see cref="OnTaxiwayMaxOffsetFt"/> of <paramref name="position"/> among
    /// <paramref name="lastEdge"/> and the edges meeting it at its two end nodes, measured the way
    /// <see cref="AirportGroundLayout.FindNearestTaxiEdge(LatLon)"/> measures them: never a runway centreline, and a ramp
    /// connector only with <paramref name="includeRamps"/>. Null with no last edge, or none of them that close.
    /// </summary>
    private static GroundEdge? NearestAroundLast(AirportGroundLayout layout, LatLon position, (int NodeA, int NodeB)? lastEdge, bool includeRamps)
    {
        if (
            (lastEdge is not { } ends)
            || !layout.Nodes.TryGetValue(ends.NodeA, out GroundNode? nodeA)
            || !layout.Nodes.TryGetValue(ends.NodeB, out GroundNode? nodeB)
        )
        {
            return null;
        }

        GroundEdge? best = null;
        double bestFt = OnTaxiwayMaxOffsetFt;
        foreach (IGroundEdge edge in nodeA.Edges.Concat(nodeB.Edges))
        {
            if ((edge is not GroundEdge straight) || edge.IsRunwayCenterline || (edge.IsRamp && !includeRamps))
            {
                continue;
            }

            double distFt = GeoMath.DistanceToSegmentFt(position, straight.Nodes[0].Position, straight.Nodes[1].Position);
            if ((distFt < bestFt) || ((best is null) && (distFt <= bestFt)))
            {
                best = straight;
                bestFt = distFt;
            }
        }

        return best;
    }

    /// <summary>
    /// The nearest straight edge of the whole layout within <see cref="OnTaxiwayMaxOffsetFt"/> of <paramref name="position"/>:
    /// the nearest taxi edge (<see cref="AirportGroundLayout.FindNearestTaxiEdge(LatLon)"/>), or with
    /// <paramref name="includeRamps"/> a ramp connector nearer still. Null when none is that close.
    /// </summary>
    private static GroundEdge? NearestAnywhere(AirportGroundLayout layout, LatLon position, bool includeRamps)
    {
        GroundEdge? best = null;
        double bestFt = OnTaxiwayMaxOffsetFt;
        if ((layout.FindNearestTaxiEdge(position) is { } nearest) && ((nearest.DistNm * GeoMath.FeetPerNm) <= bestFt))
        {
            best = nearest.Edge;
            bestFt = nearest.DistNm * GeoMath.FeetPerNm;
        }

        return includeRamps ? NearerRamp(layout, position, best, bestFt) : best;
    }

    /// <summary>
    /// The ramp connector nearer <paramref name="position"/> than <paramref name="best"/> at <paramref name="bestFt"/> — or with
    /// no <paramref name="best"/>, the nearest within <paramref name="bestFt"/> — else <paramref name="best"/>.
    /// </summary>
    private static GroundEdge? NearerRamp(AirportGroundLayout layout, LatLon position, GroundEdge? best, double bestFt)
    {
        foreach (GroundEdge edge in layout.Edges)
        {
            if (!edge.IsRamp || edge.IsRunwayCenterline)
            {
                continue;
            }

            double distFt = GeoMath.DistanceToSegmentFt(position, edge.Nodes[0].Position, edge.Nodes[1].Position);
            if ((distFt < bestFt) || ((best is null) && (distFt <= bestFt)))
            {
                best = edge;
                bestFt = distFt;
            }
        }

        return best;
    }
}
