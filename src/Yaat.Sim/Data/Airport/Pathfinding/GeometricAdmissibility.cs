namespace Yaat.Sim.Data.Airport.Pathfinding;

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
/// arrival bearing and the aircraft category. The traversal direction of an arc carries no cost
/// or gate of its own: a fillet is a symmetric curve, its end tangents are direction-aware
/// (<see cref="GetDepartureBearing"/>), and the navigator plays a reversed arc exactly.
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
    /// Defence-in-depth: the fillet generator removes zero-distance pairs at the source (guarded by
    /// <c>FilletCornerSpanGuardTests.EdgeSplit_NoZeroDistanceEdges</c>), so this admissibility skip
    /// should never fire in practice — it keeps the pathfinder robust against any stray zero-distance edge.
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
    /// Tightest fillet radius (ft) any aircraft can steer: the smallest category nose-wheel turn radius
    /// (<see cref="CategoryPerformance.NoseWheelTurnRadiusFt"/>). A <see cref="GroundArc"/> whose
    /// <see cref="GroundArc.MinRadiusOfCurvatureFt"/> is below it is fillet-generator noise at a cramped
    /// junction (OAK RWY 15/33 → D at the F junction: 6 ft), not pavement anything can track, and is
    /// inadmissible for every category — the route goes through the junction nodes instead, which the
    /// navigator rounds at the nose-wheel radius.
    /// </summary>
    public static readonly double MinSteerableArcRadiusFt = Enum.GetValues<AircraftCategory>().Min(CategoryPerformance.NoseWheelTurnRadiusFt);

    /// <summary>
    /// Closed-set key for state-aware A* pruning: node id, arrival-bearing bucket (see
    /// <see cref="PruningBearingBucketDeg"/>) and the taxiway the state arrived on. Two arrivals at the same
    /// node with sufficiently different bearings are distinct states (onward admissibility differs), and so
    /// are two arrivals on different taxiways: <see cref="RouteCostFunction.TaxiwayTransitionCostNm"/> is
    /// charged on the edge <em>after</em> the arrival, so the cheaper of two same-bearing arrivals on different
    /// taxiways is not the cheaper way onward. OAK A/B junction (node 805): the A-to-B fillet arrives at B's
    /// bearing a hair cheaper than straight-B traffic, then pays the A-to-B transition on the next edge; keyed
    /// without the taxiway it pruned the straight-B state and the auto route detoured around the east end of
    /// 10L/28R.
    /// </summary>
    public static (int Node, int Bucket, string Taxiway) PruningStateKey(int nodeId, double arrivalBearing, string lastTaxiwayName)
    {
        double normalized = arrivalBearing % 360.0;
        if (normalized < 0.0)
        {
            normalized += 360.0;
        }

        return (nodeId, (int)(normalized / PruningBearingBucketDeg), lastTaxiwayName);
    }

    /// <summary>
    /// Returns true when the candidate edge is admissible from the current route head.
    /// Per §Decisions §3: hard-reject any junction where the resulting heading change exceeds
    /// the category limit — for an arc, the change onto its entry tangent in the traversal
    /// direction, so a corner fillet is admitted from either end and only an arc whose tangent
    /// turns back through the limit is excluded.
    /// Zero-distance no-op edges (see <see cref="IsNoOpEdge"/>) are admitted unconditionally
    /// because the aircraft doesn't physically move along them.
    /// </summary>
    public static bool IsAdmissible(PartialRoute current, IGroundEdge candidate, GroundNode nextNode, AircraftCategory category)
    {
        if (IsNoOpEdge(candidate))
        {
            return true;
        }

        if (candidate is GroundArc { MinRadiusOfCurvatureFt: var tightestRadiusFt } && (tightestRadiusFt < MinSteerableArcRadiusFt))
        {
            return false;
        }

        if (current.LastEdge is null)
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
