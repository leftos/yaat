namespace Yaat.Sim.Data.Airport.V2;

/// <summary>
/// Per-category maximum admissible heading change at a junction node.
/// </summary>
public static class CategoryLimits
{
    /// <summary>
    /// Maximum heading change (degrees) that is physically executable for the given category.
    /// Jets have the widest turn radius, so tighter limit. Helicopters can pivot in place.
    /// </summary>
    public static double MaxHeadingChangeDeg(AircraftCategory category) =>
        category switch
        {
            AircraftCategory.Jet => 135.0,
            AircraftCategory.Turboprop => 145.0,
            AircraftCategory.Piston => 155.0,
            AircraftCategory.Helicopter => 175.0,
            _ => 135.0,
        };
}

/// <summary>
/// Determines whether a candidate edge can be appended to a partial route given the current
/// arrival bearing and the aircraft category. Reverse arcs are hard-rejected per §Decisions §3.
/// </summary>
public static class GeometricAdmissibility
{
    /// <summary>
    /// Returns true when the candidate edge is admissible from the current route head.
    /// </summary>
    public static bool IsAdmissible(PartialRoute current, IGroundEdge candidate, GroundNode nextNode, AircraftCategory category)
    {
        if (current.LastEdge is null)
        {
            return true;
        }

        GroundNode headNode = ResolveNode(candidate, current.HeadNodeId);
        if (headNode is null)
        {
            return false;
        }

        // Hard-reject reverse-arc traversal per §Decisions §3.
        if (candidate is GroundArc arc && IsReverseTraversal(arc, headNode))
        {
            return false;
        }

        double departureBearing = GetDepartureBearing(candidate, headNode, nextNode);
        double delta = RouteCostFunction.HeadingDelta(current.ArrivalBearing, departureBearing);
        return delta <= CategoryLimits.MaxHeadingChangeDeg(category);
    }

    /// <summary>
    /// Returns the bearing the aircraft will be travelling immediately after entering
    /// <paramref name="edge"/> from <paramref name="fromNode"/> toward <paramref name="toNode"/>.
    /// For arcs: tangent at <paramref name="fromNode"/> in the traversal direction.
    /// For straight edges: bearing from <paramref name="fromNode"/> to <paramref name="toNode"/>.
    /// </summary>
    public static double GetDepartureBearing(IGroundEdge edge, GroundNode fromNode, GroundNode toNode)
    {
        if (edge is GroundArc arc)
        {
            return arc.TangentBearingAt(fromNode, fromNode, toNode);
        }

        return GeoMath.BearingTo(fromNode.Position, toNode.Position);
    }

    /// <summary>
    /// Returns the bearing the aircraft will be arriving with at <paramref name="toNode"/>
    /// after traversing <paramref name="edge"/> from <paramref name="fromNode"/>.
    /// For arcs: tangent at <paramref name="toNode"/> continuing in the traversal direction.
    /// For straight edges: same as departure bearing.
    /// </summary>
    public static double GetArrivalBearing(IGroundEdge edge, GroundNode fromNode, GroundNode toNode)
    {
        if (edge is GroundArc arc)
        {
            return arc.TangentBearingAt(toNode, fromNode, toNode);
        }

        return GeoMath.BearingTo(fromNode.Position, toNode.Position);
    }

    /// <summary>
    /// Returns true when <paramref name="arc"/> is being traversed against its natural direction.
    /// Natural direction: <c>Nodes[0]</c> → <c>Nodes[1]</c>. Reverse: <c>Nodes[1]</c> → <c>Nodes[0]</c>.
    /// </summary>
    public static bool IsReverseTraversal(GroundArc arc, GroundNode fromNode) => arc.Nodes[0].Id != fromNode.Id;

    private static GroundNode ResolveNode(IGroundEdge edge, int nodeId)
    {
        foreach (var n in edge.Nodes)
        {
            if (n.Id == nodeId)
            {
                return n;
            }
        }

        return edge.Nodes[0];
    }
}
