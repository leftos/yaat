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
    /// Edges shorter than this distance are treated as topological no-ops — the fillet generator
    /// emits zero-distance "phase-d-shorten" pairs (e.g. SFO 1471↔30) at co-located node pairs
    /// with inherited-from-neighbour bearings that have no physical meaning. Admissibility
    /// skips them and downstream code must propagate the prior arrival bearing through them
    /// rather than reading the edge's stored bearing.
    ///
    /// <para>
    /// Load-bearing while the Legacy fillet generator remains the runtime default (it emits these
    /// zero-distance pairs and the V2 pathfinder router can run on either fillet graph). Fillet V2
    /// removes them at the source — guarded by <c>FilletV2…V2_EdgeSplit_NoZeroDistanceEdges</c> — so
    /// once the joint flip makes fillet V2 the only graph this guard becomes pure defence-in-depth;
    /// re-evaluate keeping vs removing it then.
    /// </para>
    /// </summary>
    public const double NoOpEdgeThresholdNm = 0.0002; // ≈ 1.2 ft

    /// <summary>
    /// True when <paramref name="edge"/> is a zero-distance no-op — see <see cref="NoOpEdgeThresholdNm"/>.
    /// </summary>
    public static bool IsNoOpEdge(IGroundEdge edge) => edge.DistanceNm < NoOpEdgeThresholdNm;

    /// <summary>
    /// Bucket width (degrees) for the A* closed-set key. Onward-edge admissibility depends on the
    /// arrival bearing, so both A* searches key the closed set by <c>(nodeId, arrival-bearing-bucket)</c>
    /// rather than node id alone — otherwise a cheaper arrival with a dead-end bearing can permanently
    /// suppress the only admissible (different-bearing) arrival, producing a false
    /// <see cref="FailureKind.DestinationUnreachable"/> or a worse route. 1° gives near-exact
    /// discrimination relative to the 135°+ category turn limits while bounding states-per-node at 360.
    /// </summary>
    public const int PruningBearingBucketDeg = 1;

    /// <summary>
    /// Closed-set key for state-aware A* pruning: node id paired with the arrival-bearing bucket
    /// (see <see cref="PruningBearingBucketDeg"/>). Two arrivals at the same node with sufficiently
    /// different bearings occupy distinct keys, so neither prunes the other.
    /// </summary>
    public static (int Node, int Bucket) PruningStateKey(int nodeId, double arrivalBearing)
    {
        double normalized = arrivalBearing % 360.0;
        if (normalized < 0.0)
        {
            normalized += 360.0;
        }

        return (nodeId, (int)(normalized / PruningBearingBucketDeg));
    }

    /// <summary>
    /// Returns true when the candidate edge is admissible from the current route head.
    /// Per §Decisions §3: hard-reject any junction where the resulting heading change exceeds
    /// the category limit. Reverse arcs whose heading change is within the limit are admitted
    /// (and penalised by the cost function's ReverseArcCostNm term). Only arcs whose heading
    /// delta exceeds the limit regardless of direction are excluded.
    /// Zero-distance no-op edges (see <see cref="IsNoOpEdge"/>) are admitted unconditionally
    /// because the aircraft doesn't physically move along them.
    /// </summary>
    public static bool IsAdmissible(PartialRoute current, IGroundEdge candidate, GroundNode nextNode, AircraftCategory category)
    {
        if (current.LastEdge is null)
        {
            return true;
        }

        if (IsNoOpEdge(candidate))
        {
            return true;
        }

        GroundNode headNode = ResolveNode(candidate, current.HeadNodeId);
        if (headNode is null)
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
