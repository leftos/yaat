using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Data.Faa;

namespace Yaat.Sim.Phases.Ground;

/// <summary>What <see cref="FollowRoutePlanner.Plan"/> found for a <c>FOLLOWG</c> follower and its lead.</summary>
public abstract record FollowRoutePlan
{
    private FollowRoutePlan() { }

    /// <summary>The follower can join the lead's taxi path at <paramref name="MergeNode"/>.</summary>
    /// <param name="MergeNode">
    /// The first node of the lead's path the follower's shortest taxi path reaches; for a follower behind the lead on the edge
    /// the lead is on, that edge's start in the lead's direction, behind the follower.
    /// </param>
    /// <param name="PathToMerge">
    /// The follower's route to <paramref name="MergeNode"/>; no segments when it starts there, or stands behind the lead on the
    /// edge the lead is on.
    /// </param>
    /// <param name="LeadEdgeIntoMerge">
    /// The lead's path edge into <paramref name="MergeNode"/>, in its direction of travel; null when the merge is where the
    /// lead's path starts.
    /// </param>
    /// <param name="LeadPathFromMerge">The lead's path from <paramref name="MergeNode"/> on, edge to edge, in the lead's direction of travel.</param>
    /// <param name="MergeAheadOfLead">The merge lies ahead of the lead, on the edge it is on or its remaining route.</param>
    public sealed record Joinable(
        int MergeNode,
        TaxiRoute PathToMerge,
        DirectionalEdge? LeadEdgeIntoMerge,
        IReadOnlyList<DirectionalEdge> LeadPathFromMerge,
        bool MergeAheadOfLead
    ) : FollowRoutePlan;

    /// <summary>The follower stands on an edge of the lead's remaining route, ahead of the lead.</summary>
    public sealed record FollowerAhead : FollowRoutePlan;

    /// <summary>No taxi path reaches any node of the lead's path.</summary>
    public sealed record NoPath : FollowRoutePlan;

    /// <summary>The lead is pushing back, parked, or not on a taxiway: there is no path to join yet.</summary>
    public sealed record WaitForLead : FollowRoutePlan;
}

/// <summary>Where an aircraft stands on a path of directed edges (<see cref="FollowRoutePlanner.LocateOnPath"/>).</summary>
/// <param name="EdgeIndex">The index of the path edge the aircraft is on.</param>
/// <param name="AlongEdgeFt">How far along that edge, from its start in the path's direction, the aircraft's centre stands, feet.</param>
public readonly record struct PathPosition(int EdgeIndex, double AlongEdgeFt);

/// <summary>
/// Plans how a <c>FOLLOWG</c> follower joins its lead's taxi path. The lead's path is its trail (the edges it has taxied,
/// oldest first, gaps between non-adjacent samples filled with the shortest graph path the way the lead was going), the edge
/// it is on, and its remaining assigned route — the route only when the lead is not itself following, since a follower's
/// assigned route is stale. The follower joins at the merge point: the node of that path its shortest taxi path reaches first,
/// or — standing behind the lead on the edge the lead is on — that edge's start, with no path to it.
/// </summary>
public static class FollowRoutePlanner
{
    private static readonly ILogger Log = SimLog.CreateLogger("FollowRoutePlanner");

    /// <summary>
    /// Plans the follow of <paramref name="lead"/> by <paramref name="follower"/> on <paramref name="layout"/>.
    /// </summary>
    /// <param name="layout">The airport ground layout both aircraft are on.</param>
    /// <param name="follower">The aircraft told to follow.</param>
    /// <param name="lead">The aircraft it follows.</param>
    /// <returns>
    /// <see cref="FollowRoutePlan.WaitForLead"/> while the lead pushes back, is parked or is off every taxiway;
    /// <see cref="FollowRoutePlan.FollowerAhead"/> when the follower is ahead of the lead on the edge it is on or on its
    /// route ahead of that edge; <see cref="FollowRoutePlan.NoPath"/> when no taxi path reaches the lead's path; otherwise
    /// <see cref="FollowRoutePlan.Joinable"/>.
    /// </returns>
    public static FollowRoutePlan Plan(AirportGroundLayout layout, AircraftState follower, AircraftState lead)
    {
        if (TaxiingEdge(layout, lead) is not { } leadEdge)
        {
            Log.LogDebug("[FollowPlan] {Follower}: {Lead} is not on a taxiway yet; wait for it", follower.Callsign, lead.Callsign);
            return new FollowRoutePlan.WaitForLead();
        }

        (DirectionalEdge current, IReadOnlyList<TaxiRouteSegment> routeAhead) = OnRoute(leadEdge, lead);
        GroundEdge? followerEdge = EdgeUnder(layout, follower);
        if (IsAheadOfLead(followerEdge, follower, lead, current, routeAhead))
        {
            Log.LogDebug("[FollowPlan] {Follower}: ahead of {Lead} on its path", follower.Callsign, lead.Callsign);
            return new FollowRoutePlan.FollowerAhead();
        }

        LeadPath path = BuildLeadPath(layout, lead, current, routeAhead);
        if ((followerEdge is not null) && Joins(followerEdge, current.FromNodeId, current.ToNodeId))
        {
            Log.LogDebug(
                "[FollowPlan] {Follower}: behind {Lead} on the edge it is on; joins at node {Merge}",
                follower.Callsign,
                lead.Callsign,
                current.FromNodeId
            );
            int sharedIndex = path.AheadIndex - 1;
            return new FollowRoutePlan.Joinable(
                current.FromNodeId,
                NoSegments(),
                EdgeInto(path, sharedIndex),
                [.. path.Edges.Skip(sharedIndex)],
                false
            );
        }

        if (layout.FindTaxiStartNode(follower.Position, follower.TrueHeading) is not { } start)
        {
            return new FollowRoutePlan.NoPath();
        }

        HashSet<int> goals = [.. path.Edges.Select(e => e.FromNodeId), path.Edges[^1].ToNodeId];
        if (TaxiClass.Of(follower).FindRoute(layout, start.Id, goals) is not { } toMerge)
        {
            Log.LogDebug("[FollowPlan] {Follower}: no taxi route from node {Start} to {Lead}'s path", follower.Callsign, start.Id, lead.Callsign);
            return new FollowRoutePlan.NoPath();
        }

        int mergeIndex = LastNodeIndex(path.Edges, toMerge.GoalNodeId);
        bool mergeAhead = mergeIndex >= path.AheadIndex;
        Log.LogDebug(
            "[FollowPlan] {Follower}: joins {Lead}'s path at node {Merge} (ahead of the lead: {MergeAhead}), {Segs} segments to the merge",
            follower.Callsign,
            lead.Callsign,
            toMerge.GoalNodeId,
            mergeAhead,
            toMerge.Route.Segments.Count
        );
        return new FollowRoutePlan.Joinable(
            toMerge.GoalNodeId,
            toMerge.Route,
            EdgeInto(path, mergeIndex),
            [.. path.Edges.Skip(mergeIndex)],
            mergeAhead
        );
    }

    /// <summary>The lead path's edge into node <paramref name="nodeIndex"/> (<see cref="LeadPath"/>); null at the path's start.</summary>
    private static DirectionalEdge? EdgeInto(LeadPath path, int nodeIndex) => nodeIndex > 0 ? path.Edges[nodeIndex - 1] : null;

    /// <summary>
    /// Where <paramref name="aircraft"/> stands on <paramref name="path"/>: the first path edge that is the straight taxi edge
    /// under it (<see cref="TaxiEdgeLocator.EdgeUnder"/>, hinted by its newest trail edge: the nearest straight taxi edge within
    /// <see cref="TaxiEdgeLocator.OnTaxiwayMaxOffsetFt"/>, never a fillet arc), and how far along that edge its position
    /// projects. Null when the aircraft is on no path edge the lookup picks.
    /// </summary>
    public static PathPosition? LocateOnPath(AirportGroundLayout layout, IReadOnlyList<DirectionalEdge> path, AircraftState aircraft)
    {
        if (EdgeUnder(layout, aircraft) is not { } edge)
        {
            return null;
        }

        // The first match is deliberate: where an edge repeats on the path, the earlier entry is the one a follower behind the lead reaches.
        for (int i = 0; i < path.Count; i++)
        {
            if ((path[i].Edge is GroundEdge) && Joins(edge, path[i].FromNodeId, path[i].ToNodeId))
            {
                return new PathPosition(i, AlongEdgeFt(edge, path[i].FromNode, aircraft.Position));
            }
        }

        return null;
    }

    /// <summary>
    /// How far along <paramref name="path"/> from its start <paramref name="at"/> lies, feet: the edges before its edge by
    /// their lengths, then its way along that edge.
    /// </summary>
    public static double PathOffsetFt(IReadOnlyList<DirectionalEdge> path, PathPosition at) =>
        (path.Take(at.EdgeIndex).Sum(edge => edge.DistanceNm) * GeoMath.FeetPerNm) + at.AlongEdgeFt;

    /// <summary>
    /// The follower's along-path distance to <paramref name="plan"/>'s merge node, feet. Once the follower stands on the lead's
    /// path from the merge (<see cref="LocateOnPath"/>) — as it does from the start when it joined behind the lead on the lead's
    /// edge — it is past the merge, and this is minus its <see cref="PathOffsetFt"/>. Before that, it is the rest of its route
    /// to the merge: the segment in progress straight from the follower to that segment's end node, as
    /// <see cref="TaxiingPhase.AlongRouteDistanceToHoldShortFt"/> measures it, then each later segment by its length. Null with
    /// no segment left and the follower on no path edge the lookup picks: it may be past the merge, where the straight distance
    /// back to the merge would read with the wrong sign, so the caller falls back to the straight-line gap.
    /// </summary>
    public static double? FollowerToMergeFt(AirportGroundLayout layout, FollowRoutePlan.Joinable plan, AircraftState follower)
    {
        if (LocateOnPath(layout, plan.LeadPathFromMerge, follower) is { } onPath)
        {
            return -PathOffsetFt(plan.LeadPathFromMerge, onPath);
        }

        TaxiRoute route = plan.PathToMerge;
        int first = Math.Max(0, route.CurrentSegmentIndex);
        if (first >= route.Segments.Count)
        {
            return null;
        }

        double inProgressFt = GeoMath.DistanceNm(follower.Position, route.Segments[first].Edge.ToNode.Position) * GeoMath.FeetPerNm;
        return inProgressFt + route.TotalDistanceFt - route.PrefixDistanceFt(first + 1);
    }

    /// <summary>
    /// The nose-to-tail gap from the follower to the lead along the lead's path, feet: <paramref name="followerToMergeFt"/>
    /// (<see cref="FollowerToMergeFt"/>), plus the lead's <see cref="PathOffsetFt"/> from the merge, less half of each
    /// aircraft's length (<see cref="AircraftLength.ResolveFt"/>). Null when the lead is not on the path
    /// (<paramref name="lead"/> null) or the follower's distance to the merge is unknown (<paramref name="followerToMergeFt"/>
    /// null); the caller then falls back to the straight-line nose-to-tail distance.
    /// </summary>
    public static double? AlongPathGapFt(
        double? followerToMergeFt,
        IReadOnlyList<DirectionalEdge> leadPathFromMerge,
        PathPosition? lead,
        string followerType,
        string leadType
    ) =>
        (lead is { } at) && (followerToMergeFt is { } toMergeFt)
            ? toMergeFt
                + PathOffsetFt(leadPathFromMerge, at)
                - (AircraftLength.ResolveFt(followerType) / 2.0)
                - (AircraftLength.ResolveFt(leadType) / 2.0)
            : null;

    /// <summary>
    /// Whether the lead's tail — half its length (<see cref="AircraftLength.ResolveFt"/>) behind its centre, along its path —
    /// is past the merge node, where <paramref name="leadPathFromMerge"/> starts.
    /// </summary>
    public static bool LeadTailPastMerge(IReadOnlyList<DirectionalEdge> leadPathFromMerge, PathPosition lead, string leadType) =>
        PathOffsetFt(leadPathFromMerge, lead) - (AircraftLength.ResolveFt(leadType) / 2.0) > 0.0;

    /// <summary>
    /// How far along <paramref name="edge"/>, driven from <paramref name="from"/>, <paramref name="position"/> projects, feet:
    /// onto the piece of the edge's polyline nearest it, clamped to that piece.
    /// </summary>
    private static double AlongEdgeFt(GroundEdge edge, GroundNode from, LatLon position)
    {
        List<LatLon> points = TugMovePlanner.EdgePointsFrom(edge, from);
        double walkedFt = 0.0;
        double bestOffFt = double.PositiveInfinity;
        double bestAlongFt = 0.0;
        for (int k = 1; k < points.Count; k++)
        {
            double pieceFt = GeoMath.DistanceNm(points[k - 1], points[k]) * GeoMath.FeetPerNm;
            var heading = new TrueHeading(GeoMath.BearingTo(points[k - 1], points[k]));
            double alongFt = Math.Clamp(GeoMath.AlongTrackDistanceNm(position, points[k - 1], heading) * GeoMath.FeetPerNm, 0.0, pieceFt);
            double offFt =
                GeoMath.DistanceNm(position, GeoMath.ProjectPoint(points[k - 1], heading, alongFt / GeoMath.FeetPerNm)) * GeoMath.FeetPerNm;
            if (offFt < bestOffFt)
            {
                bestOffFt = offFt;
                bestAlongFt = walkedFt + alongFt;
            }

            walkedFt += pieceFt;
        }

        return bestAlongFt;
    }

    /// <summary>
    /// The lead's path as directed edges, oldest first, and the index of the node ahead of the lead on the edge it is on
    /// (node <c>i</c> is where edge <c>i</c> starts; the last edge's end is node <c>Edges.Count</c>).
    /// </summary>
    private sealed record LeadPath(List<DirectionalEdge> Edges, int AheadIndex);

    /// <summary>The aircraft class a graph search runs for: its performance category and wake class.</summary>
    private readonly record struct TaxiClass(AircraftCategory Category, WakeTurbulenceData.WakeClass Wake)
    {
        public static TaxiClass Of(AircraftState aircraft)
        {
            AircraftCategory category = AircraftCategorization.Categorize(aircraft.AircraftType);
            return new TaxiClass(category, WakeTurbulenceData.WakeClassForType(aircraft.AircraftType, category));
        }

        /// <summary>The auto route from <paramref name="fromNodeId"/> to the cheapest of <paramref name="goals"/>; null when none.</summary>
        public GoalRoute? FindRoute(AirportGroundLayout layout, int fromNodeId, IReadOnlySet<int> goals) =>
            TaxiPathfinder.FindRouteToNearestGoal(layout, fromNodeId, goals, Category, Wake);
    }

    /// <summary>The edge the lead taxis on; null while it pushes back, is parked, or is off every taxiway.</summary>
    private static GroundEdge? TaxiingEdge(AirportGroundLayout layout, AircraftState lead) =>
        lead.Phases?.CurrentPhase is PushbackPhase or AtParkingPhase ? null : EdgeUnder(layout, lead);

    private static GroundEdge? EdgeUnder(AirportGroundLayout layout, AircraftState aircraft) =>
        TaxiEdgeLocator.EdgeUnder(
            layout,
            aircraft.Position,
            aircraft.Ground.TaxiEdgeTrail.Newest is { } newest ? (newest.NodeA, newest.NodeB) : null
        );

    /// <summary>The lead's assigned route from its current segment on; none while it is following, or with no route left.</summary>
    private static IReadOnlyList<TaxiRouteSegment> RemainingRoute(AircraftState lead) =>
        (lead.Phases?.CurrentPhase is not FollowingPhase) && (lead.Ground.AssignedTaxiRoute is { IsComplete: false } route)
            ? route.Segments[route.CurrentSegmentIndex..]
            : [];

    /// <summary>
    /// The edge the lead is on, pointed the way it is going, and the lead's route ahead of that edge. On its route, the first
    /// remaining segment over the edge points it, and only the segments after that one lie ahead: the lead has passed those
    /// before it even while its segment index still names one — held on its first segment through an entry-alignment turn, or
    /// sampled onto the next edge before it reaches the node. Off its route, the edge points toward the end nearer its heading
    /// (<see cref="OrientByHeading"/>) and every remaining segment lies ahead.
    /// </summary>
    private static (DirectionalEdge Current, IReadOnlyList<TaxiRouteSegment> RouteAhead) OnRoute(GroundEdge edge, AircraftState lead)
    {
        IReadOnlyList<TaxiRouteSegment> remaining = RemainingRoute(lead);
        for (int i = 0; i < remaining.Count; i++)
        {
            TaxiRouteSegment segment = remaining[i];
            if (Joins(edge, segment.FromNodeId, segment.ToNodeId))
            {
                return (Directed(edge, segment.Edge.FromNode, segment.Edge.ToNode), [.. remaining.Skip(i + 1)]);
            }
        }

        return (OrientByHeading(edge, lead), remaining);
    }

    /// <summary>
    /// <paramref name="edge"/> pointed toward the end nearer <paramref name="lead"/>'s heading, which a turn in progress can
    /// swing past the edge's own direction.
    /// </summary>
    private static DirectionalEdge OrientByHeading(GroundEdge edge, AircraftState lead)
    {
        GroundNode a = edge.Nodes[0];
        GroundNode b = edge.Nodes[1];
        double toA = lead.TrueHeading.AbsAngleTo(new TrueHeading(GeoMath.BearingTo(lead.Position, a.Position)));
        double toB = lead.TrueHeading.AbsAngleTo(new TrueHeading(GeoMath.BearingTo(lead.Position, b.Position)));
        return toB <= toA ? Directed(edge, a, b) : Directed(edge, b, a);
    }

    /// <summary>
    /// Whether the follower is ahead of the lead: on the edge the lead is on and nearer that edge's far end than the lead is,
    /// or on an edge of the lead's route ahead of it.
    /// </summary>
    private static bool IsAheadOfLead(
        GroundEdge? followerEdge,
        AircraftState follower,
        AircraftState lead,
        DirectionalEdge current,
        IReadOnlyList<TaxiRouteSegment> routeAhead
    )
    {
        if (followerEdge is null)
        {
            return false;
        }

        if (Joins(followerEdge, current.FromNodeId, current.ToNodeId))
        {
            LatLon far = current.ToNode.Position;
            return GeoMath.DistanceNm(follower.Position, far) < GeoMath.DistanceNm(lead.Position, far);
        }

        return routeAhead.Any(segment => Joins(followerEdge, segment.FromNodeId, segment.ToNodeId));
    }

    /// <summary>A route with no segments: the follower is on the lead's path already.</summary>
    private static TaxiRoute NoSegments() => new() { Segments = [], HoldShortPoints = [] };

    private static bool Joins(GroundEdge edge, int nodeA, int nodeB) =>
        ((edge.Nodes[0].Id == nodeA) && (edge.Nodes[1].Id == nodeB)) || ((edge.Nodes[0].Id == nodeB) && (edge.Nodes[1].Id == nodeA));

    private static LeadPath BuildLeadPath(
        AirportGroundLayout layout,
        AircraftState lead,
        DirectionalEdge current,
        IReadOnlyList<TaxiRouteSegment> routeAhead
    )
    {
        var taxiClass = TaxiClass.Of(lead);
        List<DirectionalEdge> edges = [.. TrailBehind(layout, lead, current, taxiClass), current];
        int aheadIndex = edges.Count;
        AppendRoute(layout, edges, routeAhead, taxiClass);
        return new LeadPath(edges, aheadIndex);
    }

    /// <summary>
    /// The lead's trail behind <paramref name="current"/>, oldest first, walked back from the newest edge. Each trail edge is
    /// pointed toward the edge after it; where the two do not meet, the shortest graph path between them fills the gap. The
    /// walk stops at the first edge no graph path joins, and at the first trail edge whose step back would revisit an edge the
    /// walk already holds — where the lead reversed, as after a push out along a taxiway it then taxied back down — so the
    /// path never runs out and back over an edge.
    /// </summary>
    private static List<DirectionalEdge> TrailBehind(AirportGroundLayout layout, AircraftState lead, DirectionalEdge current, TaxiClass taxiClass)
    {
        List<DirectionalEdge> reversed = [];
        HashSet<(int, int)> held = [EdgeKey(current)];
        GroundNode cursor = current.FromNode;
        IReadOnlyList<TaxiTrailEdge> trail = lead.Ground.TaxiEdgeTrail.Edges;
        for (int i = trail.Count - 1; i >= 0; i--)
        {
            if ((trail[i].Resolve(layout) is not { } edge) || ((reversed.Count == 0) && Joins(edge, current.FromNodeId, current.ToNodeId)))
            {
                continue;
            }

            if ((ReachForward(layout, edge, cursor, taxiClass) is not { } step) || !HoldsNoneOf(held, step))
            {
                Log.LogDebug(
                    "[FollowPlan] {Lead}: trail edge {A}-{B} joins no new path to node {Cursor}; older trail dropped",
                    lead.Callsign,
                    trail[i].NodeA,
                    trail[i].NodeB,
                    cursor.Id
                );
                break;
            }

            reversed.AddRange(step);
            cursor = reversed[^1].FromNode;
        }

        reversed.Reverse();
        return reversed;
    }

    /// <summary>Adds every edge of <paramref name="step"/> to <paramref name="held"/>; false at the first one it already holds.</summary>
    private static bool HoldsNoneOf(HashSet<(int, int)> held, List<DirectionalEdge> step)
    {
        foreach (DirectionalEdge edge in step)
        {
            if (!held.Add(EdgeKey(edge)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The end node ids of <paramref name="edge"/>, lower first, whichever way it points.</summary>
    private static (int, int) EdgeKey(DirectionalEdge edge) =>
        edge.FromNodeId < edge.ToNodeId ? (edge.FromNodeId, edge.ToNodeId) : (edge.ToNodeId, edge.FromNodeId);

    /// <summary>
    /// The step back from <paramref name="cursor"/> to <paramref name="edge"/>, newest first: the gap between them, if any,
    /// then <paramref name="edge"/> pointed toward it. There is no gap when the cursor is an end of the edge. The gap is
    /// searched forward, the way the lead taxied it — from each end of the edge to the cursor, keeping the cheaper — so a
    /// one-way lane the lead used the right way is not excluded. Null when no path joins them.
    /// </summary>
    private static List<DirectionalEdge>? ReachForward(AirportGroundLayout layout, GroundEdge edge, GroundNode cursor, TaxiClass taxiClass)
    {
        if ((edge.Nodes[0].Id == cursor.Id) || (edge.Nodes[1].Id == cursor.Id))
        {
            return [Directed(edge, edge.OtherNode(cursor), cursor)];
        }

        if (CheaperGap(layout, edge, cursor, taxiClass) is not { } gap)
        {
            return null;
        }

        List<DirectionalEdge> step = [];
        for (int i = gap.Route.Segments.Count - 1; i >= 0; i--)
        {
            step.Add(gap.Route.Segments[i].Edge);
        }

        GroundNode exit = gap.Route.Segments[0].Edge.FromNode;
        step.Add(Directed(edge, edge.OtherNode(exit), exit));
        return step;
    }

    /// <summary>
    /// The cheaper of the routes from either end of <paramref name="edge"/> to <paramref name="cursor"/>, which is not an end of
    /// it; null when neither end reaches it.
    /// </summary>
    private static GoalRoute? CheaperGap(AirportGroundLayout layout, GroundEdge edge, GroundNode cursor, TaxiClass taxiClass)
    {
        HashSet<int> goal = [cursor.Id];
        GoalRoute? fromA = taxiClass.FindRoute(layout, edge.Nodes[0].Id, goal);
        GoalRoute? fromB = taxiClass.FindRoute(layout, edge.Nodes[1].Id, goal);
        return (fromA is null) || ((fromB is not null) && (fromB.Cost < fromA.Cost)) ? fromB : fromA;
    }

    /// <summary>
    /// Appends <paramref name="routeAhead"/>, the lead's route ahead of the edge it is on (<see cref="OnRoute"/>); a segment
    /// that does not start where the path ends is reached by the shortest graph path.
    /// </summary>
    private static void AppendRoute(
        AirportGroundLayout layout,
        List<DirectionalEdge> edges,
        IReadOnlyList<TaxiRouteSegment> routeAhead,
        TaxiClass taxiClass
    )
    {
        foreach (TaxiRouteSegment segment in routeAhead)
        {
            int end = edges[^1].ToNodeId;
            if ((end != segment.FromNodeId) && !TryBridge(layout, edges, segment.FromNodeId, taxiClass))
            {
                return;
            }

            edges.Add(segment.Edge);
        }
    }

    /// <summary>Appends the shortest graph path from the end of <paramref name="edges"/> to <paramref name="toNodeId"/>; false when none.</summary>
    private static bool TryBridge(AirportGroundLayout layout, List<DirectionalEdge> edges, int toNodeId, TaxiClass taxiClass)
    {
        HashSet<int> goal = [toNodeId];
        if (taxiClass.FindRoute(layout, edges[^1].ToNodeId, goal) is not { } bridge)
        {
            Log.LogDebug("[FollowPlan] no path from node {From} to route node {To}; rest of the route dropped", edges[^1].ToNodeId, toNodeId);
            return false;
        }

        edges.AddRange(bridge.Route.Segments.Select(s => s.Edge));
        return true;
    }

    /// <summary>The last node index of <paramref name="nodeId"/> along <paramref name="edges"/> (node <c>i</c> starts edge <c>i</c>).</summary>
    /// <exception cref="InvalidOperationException">No edge starts or ends at <paramref name="nodeId"/>.</exception>
    private static int LastNodeIndex(List<DirectionalEdge> edges, int nodeId)
    {
        if (edges[^1].ToNodeId == nodeId)
        {
            return edges.Count;
        }

        for (int i = edges.Count - 1; i >= 0; i--)
        {
            if (edges[i].FromNodeId == nodeId)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"Merge node {nodeId} is not on the lead's path of {edges.Count} edges.");
    }

    private static DirectionalEdge Directed(IGroundEdge edge, GroundNode from, GroundNode to) =>
        new()
        {
            Edge = edge,
            FromNode = from,
            ToNode = to,
        };
}
