using Yaat.Sim.Phases.Ground;

namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Finds the straight taxi edge a ground aircraft is rolling on: the edges
/// <see cref="AirportGroundLayout.FindNearestTaxiEdge(LatLon)"/> considers (no fillet arc, runway centreline or ramp connector),
/// looked for first around the edge the aircraft was last on, and across the whole layout only when nothing there is within
/// <see cref="OnTaxiwayMaxOffsetFt"/>. Shared by <see cref="FollowingPhase"/> (the taxiway a follower is on) and
/// <see cref="TaxiEdgeTrail"/> (the edges an aircraft has driven).
/// </summary>
public static class TaxiEdgeLocator
{
    /// <summary>How far (ft) off a taxiway's centreline a moving aircraft still counts as on that taxiway.</summary>
    public const double OnTaxiwayMaxOffsetFt = 50.0;

    /// <summary>
    /// The straight taxi edge under <paramref name="position"/>, or null off every taxiway. <paramref name="lastEdge"/> is the
    /// edge last found, by its end node ids (<c>Nodes[0]</c>, <c>Nodes[1]</c>), or null when there is none.
    /// </summary>
    public static GroundEdge? EdgeUnder(AirportGroundLayout layout, LatLon position, (int NodeA, int NodeB)? lastEdge)
    {
        GroundEdge? onEdge = NearestTaxiEdgeAroundLast(layout, position, lastEdge);
        if (
            (onEdge is null)
            && (layout.FindNearestTaxiEdge(position) is { } nearest)
            && ((nearest.DistNm * GeoMath.FeetPerNm) <= OnTaxiwayMaxOffsetFt)
        )
        {
            onEdge = nearest.Edge;
        }

        return onEdge;
    }

    /// <summary>
    /// The nearest straight taxi edge within <see cref="OnTaxiwayMaxOffsetFt"/> of <paramref name="position"/> among
    /// <paramref name="lastEdge"/> and the edges meeting it at its two end nodes, measured the way
    /// <see cref="AirportGroundLayout.FindNearestTaxiEdge(LatLon)"/> measures them. Null with no last edge, or none of them
    /// that close.
    /// </summary>
    private static GroundEdge? NearestTaxiEdgeAroundLast(AirportGroundLayout layout, LatLon position, (int NodeA, int NodeB)? lastEdge)
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
            if ((edge is not GroundEdge straight) || edge.IsRunwayCenterline || edge.IsRamp)
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
}
