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
/// it is on, and its remaining assigned route — the route only when the lead is not itself a follower (a follow current or
/// queued in its phases), since a follower's assigned route is stale. The follower joins at the merge point: the node of that
/// path its shortest taxi path reaches first, or — standing behind the lead on the edge the lead is on — that edge's start,
/// with no path to it.
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
    /// <see cref="FollowRoutePlan.WaitForLead"/> while the lead pushes back, is parked or is off every taxiway and every fillet
    /// arc of its route (<see cref="LocateLead"/>), and while it rounds an arc the follower may not drive with no path behind it
    /// (<see cref="BuildLeadPath"/>);
    /// <see cref="FollowRoutePlan.FollowerAhead"/> when the follower is ahead of the lead on the edge it is on or on its
    /// route ahead of that edge; <see cref="FollowRoutePlan.NoPath"/> when no taxi path reaches the lead's path; otherwise
    /// <see cref="FollowRoutePlan.Joinable"/>.
    /// </returns>
    public static FollowRoutePlan Plan(AirportGroundLayout layout, AircraftState follower, AircraftState lead) =>
        Plan(layout, follower, lead, requireAhead: true);

    /// <summary>
    /// Plans the follow again for a follower already driving a follow route: as
    /// <see cref="Plan(AirportGroundLayout, AircraftState, AircraftState)"/>, except that the route starts at its trail's end
    /// without the check that the node lies ahead (<see cref="StartNode"/>): a follower re-planning just past a node has that
    /// node a little behind it, and still drives on from it. A re-plan never turns the follower about: its route never starts
    /// back along the edge it came in on (<see cref="BackMove"/>), and a lead path that would take it back there joins nothing
    /// (<see cref="FollowRoutePlan.NoPath"/>).
    /// </summary>
    public static FollowRoutePlan Replan(AirportGroundLayout layout, AircraftState follower, AircraftState lead) =>
        Plan(layout, follower, lead, requireAhead: false);

    private static FollowRoutePlan Plan(AirportGroundLayout layout, AircraftState follower, AircraftState lead, bool requireAhead)
    {
        if (LocateLead(layout, lead) is not { } located)
        {
            Log.LogDebug("[FollowPlan] {Follower}: {Lead} is not on a taxiway yet; wait for it", follower.Callsign, lead.Callsign);
            return new FollowRoutePlan.WaitForLead();
        }

        (DirectionalEdge current, IReadOnlyList<TaxiRouteSegment> routeAhead) = located;
        GroundEdge? followerEdge = EdgeUnder(layout, follower);
        if (IsAheadOfLead(followerEdge, follower, lead, current, routeAhead))
        {
            Log.LogDebug("[FollowPlan] {Follower}: ahead of {Lead} on its path", follower.Callsign, lead.Callsign);
            return new FollowRoutePlan.FollowerAhead();
        }

        if (BuildLeadPath(layout, lead, follower, current, routeAhead) is not { } path)
        {
            Log.LogDebug(
                "[FollowPlan] {Follower}: {Lead} is rounding an arc the follower cannot drive and has no path behind it yet; wait for it",
                follower.Callsign,
                lead.Callsign
            );
            return new FollowRoutePlan.WaitForLead();
        }

        (int From, int To)? back = requireAhead ? null : BackMove(layout, follower);
        if ((followerEdge is not null) && Joins(followerEdge, current.FromNodeId, current.ToNodeId))
        {
            return PlanOnLeadEdge(follower, lead, current, path, back);
        }

        return PlanToMerge(layout, follower, lead, path, new PlanStart(requireAhead, back));
    }

    /// <summary>How a plan starts: whether its start node must lie ahead (a first plan), and the move back it never starts with.</summary>
    private readonly record struct PlanStart(bool RequireAhead, (int From, int To)? Back);

    /// <summary>
    /// The plan for a follower behind the lead on the edge the lead is on: it joins at that edge's start, with no route to it.
    /// None (<see cref="FollowRoutePlan.NoPath"/>) when the lead drives that edge back the way the follower came
    /// (<paramref name="back"/>): following would turn it about.
    /// </summary>
    private static FollowRoutePlan PlanOnLeadEdge(
        AircraftState follower,
        AircraftState lead,
        DirectionalEdge current,
        LeadPath path,
        (int From, int To)? back
    )
    {
        if (IsMove(current, back))
        {
            Log.LogDebug(
                "[FollowPlan] {Follower}: {Lead} drives its edge back toward node {Node}, behind the follower; no join without turning about",
                follower.Callsign,
                lead.Callsign,
                current.ToNodeId
            );
            return new FollowRoutePlan.NoPath();
        }

        Log.LogDebug(
            "[FollowPlan] {Follower}: behind {Lead} on the edge it is on; joins at node {Merge}",
            follower.Callsign,
            lead.Callsign,
            current.FromNodeId
        );
        int sharedIndex = path.AheadIndex - 1;
        return new FollowRoutePlan.Joinable(current.FromNodeId, NoSegments(), EdgeInto(path, sharedIndex), [.. path.Edges.Skip(sharedIndex)], false);
    }

    /// <summary>
    /// The plan for a follower off the lead's edge: its route from <see cref="StartOf"/> to the cheapest node of the lead's path.
    /// On a re-plan the route never starts back along the segment that leads the follower into its start, or with none, back
    /// along its newest trail edge; and a merge at the start whose lead path leaves back that way joins nothing.
    /// </summary>
    private static FollowRoutePlan PlanToMerge(AirportGroundLayout layout, AircraftState follower, AircraftState lead, LeadPath path, PlanStart how)
    {
        if (StartOf(layout, follower, how.RequireAhead) is not { } routeStart)
        {
            return new FollowRoutePlan.NoPath();
        }

        GroundNode start = routeStart.Node;
        (int From, int To)? forbidden = ForbiddenFirstMove(routeStart, how);
        HashSet<int> goals = [.. path.Edges.Select(e => e.FromNodeId), path.Edges[^1].ToNodeId];
        if (TaxiClass.Of(follower).FindRoute(layout, start.Id, goals, forbidden) is not { } toMerge)
        {
            Log.LogDebug("[FollowPlan] {Follower}: no taxi route from node {Start} to {Lead}'s path", follower.Callsign, start.Id, lead.Callsign);
            return new FollowRoutePlan.NoPath();
        }

        int mergeIndex = LastNodeIndex(path.Edges, toMerge.GoalNodeId);
        if (LeadPathLeavesBack(toMerge.Route, path, mergeIndex, forbidden))
        {
            Log.LogDebug(
                "[FollowPlan] {Follower}: {Lead}'s path leaves node {Merge} back the way the follower came; no join without turning about",
                follower.Callsign,
                lead.Callsign,
                start.Id
            );
            return new FollowRoutePlan.NoPath();
        }

        bool mergeAhead = mergeIndex >= path.AheadIndex;
        TaxiRoute pathToMerge = routeStart.LeadIn is { } leadIn
            ? new TaxiRoute { Segments = [leadIn, .. toMerge.Route.Segments], HoldShortPoints = toMerge.Route.HoldShortPoints }
            : toMerge.Route;
        Log.LogDebug(
            "[FollowPlan] {Follower}: joins {Lead}'s path at node {Merge} (ahead of the lead: {MergeAhead}), {Segs} segments to the merge",
            follower.Callsign,
            lead.Callsign,
            toMerge.GoalNodeId,
            mergeAhead,
            pathToMerge.Segments.Count
        );
        return new FollowRoutePlan.Joinable(
            toMerge.GoalNodeId,
            pathToMerge,
            EdgeInto(path, mergeIndex),
            [.. path.Edges.Skip(mergeIndex)],
            mergeAhead
        );
    }

    /// <summary>
    /// The move a re-plan's route never starts with: back along the segment that leads the follower into its route's start, or
    /// with none, the plan's move back (<see cref="PlanStart.Back"/>); null on a first plan.
    /// </summary>
    private static (int From, int To)? ForbiddenFirstMove(RouteStart routeStart, PlanStart how) =>
        ((how.Back is not null) && (routeStart.LeadIn is { } into)) ? (into.ToNodeId, into.FromNodeId) : how.Back;

    /// <summary>
    /// Whether a merge where the follower's route starts (<paramref name="toMerge"/> empty) has the lead's path leave it by
    /// <paramref name="forbidden"/>, back the way the follower came: no join without turning about.
    /// </summary>
    private static bool LeadPathLeavesBack(TaxiRoute toMerge, LeadPath path, int mergeIndex, (int From, int To)? forbidden) =>
        (toMerge.Segments.Count == 0) && (mergeIndex < path.Edges.Count) && IsMove(path.Edges[mergeIndex], forbidden);

    /// <summary>The lead path's edge into node <paramref name="nodeIndex"/> (<see cref="LeadPath"/>); null at the path's start.</summary>
    private static DirectionalEdge? EdgeInto(LeadPath path, int nodeIndex) => nodeIndex > 0 ? path.Edges[nodeIndex - 1] : null;

    /// <summary>
    /// The move back along <paramref name="follower"/>'s newest trail edge: from the end it drives toward
    /// (<see cref="TrailEndNode"/>) to the end it came from. A re-plan never starts its route with it, which would turn the
    /// follower about. Null with no trail edge the layout holds.
    /// </summary>
    private static (int From, int To)? BackMove(AirportGroundLayout layout, AircraftState follower)
    {
        IReadOnlyList<TaxiTrailEdge> trail = follower.Ground.TaxiEdgeTrail.Edges;
        if ((trail.Count == 0) || (trail[^1].Resolve(layout) is not { } newest))
        {
            return null;
        }

        GroundNode toward = DrivenTowardOf(newest, follower);
        return (toward.Id, newest.OtherNode(toward).Id);
    }

    /// <summary>The end of <paramref name="newest"/>, the aircraft's newest trail edge, it drives toward (<see cref="TrailEndNode"/>).</summary>
    private static GroundNode DrivenTowardOf(GroundEdge newest, AircraftState aircraft)
    {
        IReadOnlyList<TaxiTrailEdge> trail = aircraft.Ground.TaxiEdgeTrail.Edges;
        return TrailEndNode(newest, trail.Count > 1 ? trail[^2] : null, aircraft);
    }

    /// <summary>Whether <paramref name="edge"/> drives <paramref name="move"/>: from its first node to its second.</summary>
    private static bool IsMove(DirectionalEdge edge, (int From, int To)? move) =>
        (move is { } m) && (edge.FromNodeId == m.From) && (edge.ToNodeId == m.To);

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
    public static double? FollowerToMergeFt(AirportGroundLayout layout, FollowRoutePlan.Joinable plan, AircraftState follower) =>
        FollowerToMergeFt(layout, plan.PathToMerge, plan.LeadPathFromMerge, follower);

    /// <summary>
    /// The follower's along-path distance to the merge node, feet, read from the plan's parts: <paramref name="pathToMerge"/>
    /// and <paramref name="leadPathFromMerge"/> as <see cref="FollowerToMergeFt(AirportGroundLayout, FollowRoutePlan.Joinable, AircraftState)"/>
    /// reads them from a plan.
    /// </summary>
    public static double? FollowerToMergeFt(
        AirportGroundLayout layout,
        TaxiRoute pathToMerge,
        IReadOnlyList<DirectionalEdge> leadPathFromMerge,
        AircraftState follower
    )
    {
        if (LocateOnPath(layout, leadPathFromMerge, follower) is { } onPath)
        {
            return -PathOffsetFt(leadPathFromMerge, onPath);
        }

        return (Math.Max(0, pathToMerge.CurrentSegmentIndex) >= pathToMerge.Segments.Count)
            ? null
            : pathToMerge.RemainingDistanceFt(follower.Position, pathToMerge.Segments.Count);
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
    /// The node <paramref name="aircraft"/>'s follow (or clearing) route starts at, read from its own <see cref="TaxiEdgeTrail"/>
    /// (ramp connectors included), so an aircraft on a fillet arc, or beside another taxiway's edge, plans on along the edges it
    /// drove rather than from whichever straight edge it stands nearest (<see cref="TrailCandidate"/>). With
    /// <paramref name="requireAhead"/> (a follow's first plan) that node counts only when it lies ahead of the aircraft, within
    /// 90° of its heading; a re-plan of a follow already driving a route takes it as it is. Otherwise — or farther off the
    /// newest edge, with an empty trail, or a newest edge the layout does not hold — a taxi's start
    /// (<see cref="AirportGroundLayout.FindTaxiStartNode"/>). Null when the layout has no node.
    /// </summary>
    internal static GroundNode? StartNode(AirportGroundLayout layout, AircraftState aircraft, bool requireAhead) =>
        StartOf(layout, aircraft, requireAhead)?.Node;

    /// <summary>Where a follow (or clearing) route starts: its start node, and the segment that leads the aircraft into it, if any.</summary>
    /// <param name="Node">The node the route's search starts at.</param>
    /// <param name="LeadIn">
    /// The route's first segment, into <paramref name="Node"/>: the fillet arc the aircraft stands partway round, or the straight
    /// edge it stands mid-way along (<see cref="LeadInSegment"/>); null when it starts at the node.
    /// </param>
    internal readonly record struct RouteStart(GroundNode Node, TaxiRouteSegment? LeadIn);

    /// <summary>
    /// Where <paramref name="aircraft"/>'s follow (or clearing) route starts. Partway round a fillet arc that joins its newest
    /// trail edge (<see cref="TaxiEdgeLocator.FilletArcRounding"/>), it starts on that arc: the arc, pointed away from the trail edge,
    /// is the first segment and its far node the start, so the navigator plays the arc on from the aircraft's own point on it.
    /// Otherwise the start is <see cref="StartNode"/>'s trail rule, led in by <see cref="LeadInSegment"/>. With
    /// <paramref name="requireAhead"/> the arc's far node, like the trail's end node, counts only when it lies ahead of the
    /// aircraft. Null when the layout has no node.
    /// </summary>
    internal static RouteStart? StartOf(AirportGroundLayout layout, AircraftState aircraft, bool requireAhead)
    {
        if (TrailArcStart(layout, aircraft, requireAhead) is { } onArc)
        {
            return onArc;
        }

        GroundNode? start = TrailStartNode(layout, aircraft, requireAhead) ?? layout.FindTaxiStartNode(aircraft.Position, aircraft.TrueHeading);
        return (start is null) ? null : new RouteStart(start, LeadInSegment(start, aircraft));
    }

    /// <summary>
    /// The start on the fillet arc <paramref name="aircraft"/> stands partway round, when that arc meets its newest trail edge
    /// (<see cref="TaxiEdgeLocator.FilletArcRounding"/>): the arc's far node (the end the trail edge does not share), with the arc
    /// pointed into it as the lead-in. The arc counts only when it leaves the trail edge at the end the aircraft drives toward
    /// (<see cref="TrailEndNode"/>), never at the node it came from, and when the aircraft stands within
    /// <see cref="TaxiEdgeLocator.OnFilletArcMaxOffsetFt"/> of its curve. An aircraft driving a route
    /// (<see cref="FollowingPhase.DrivenRouteOf"/>) is on the arc only while that route's current segment is the arc into its far
    /// node: where a fillet leaves a straight edge tangent to it, the two lie within a foot of each other, and only the route
    /// says which one the aircraft drives. Null off such an arc, or — with
    /// <paramref name="requireAhead"/> — when the far node lies behind the aircraft.
    /// </summary>
    private static RouteStart? TrailArcStart(AirportGroundLayout layout, AircraftState aircraft, bool requireAhead)
    {
        if (
            (aircraft.Ground.TaxiEdgeTrail.Newest is not { } newestEnds)
            || (newestEnds.Resolve(layout) is not { } newest)
            || (TaxiEdgeLocator.FilletArcRounding(layout, aircraft.Position, (newestEnds.NodeA, newestEnds.NodeB)) is not { } arc)
        )
        {
            return null;
        }

        GroundNode shared = newest.HasNode(arc.Nodes[0].Id) ? arc.Nodes[0] : arc.Nodes[1];
        GroundNode far = arc.OtherNode(shared);
        double offArcFt = OffArcFt(arc, aircraft.Position);
        if (ArcRejection(aircraft, arc, shared, DrivenTowardOf(newest, aircraft), offArcFt) is { } rejection)
        {
            Log.LogDebug("[FollowPlan] {Callsign}: {Rejection}; starting from its trail", aircraft.Callsign, rejection);
            return null;
        }

        if (requireAhead && !LiesAhead(aircraft, far))
        {
            Log.LogDebug(
                "[FollowPlan] {Callsign}: the far node {Node} of the fillet it is on lies behind it; starting from the taxi start pick",
                aircraft.Callsign,
                far.Id
            );
            return null;
        }

        Log.LogDebug(
            "[FollowPlan] {Callsign}: starts partway round the fillet {Shared}-{Far}, {Off:F1} ft off its curve",
            aircraft.Callsign,
            shared.Id,
            far.Id,
            offArcFt
        );
        return new RouteStart(far, new TaxiRouteSegment { Edge = arc.Directed(shared, far), TaxiwayName = arc.TaxiwayName });
    }

    /// <summary>
    /// Why the aircraft is not on <paramref name="arc"/> for <see cref="TrailArcStart"/>, or null when it is: the arc leaves its
    /// newest trail edge at <paramref name="shared"/>, not at <paramref name="drivenToward"/>, the end it drives toward; the route
    /// it drives (<see cref="FollowingPhase.DrivenRouteOf"/>) is on another segment than the arc into its far node; or it stands
    /// <paramref name="offArcFt"/> off the arc's curve, more than <see cref="TaxiEdgeLocator.OnFilletArcMaxOffsetFt"/>.
    /// </summary>
    private static string? ArcRejection(AircraftState aircraft, GroundArc arc, GroundNode shared, GroundNode drivenToward, double offArcFt)
    {
        GroundNode far = arc.OtherNode(shared);
        if (shared.Id != drivenToward.Id)
        {
            return $"the fillet {shared.Id}-{far.Id} leaves its newest trail edge at node {shared.Id}, behind it";
        }

        if (
            (FollowingPhase.DrivenRouteOf(aircraft) is { IsComplete: false, CurrentSegment: { } current })
            && !(ReferenceEquals(current.Edge.Edge, arc) && (current.ToNodeId == far.Id))
        )
        {
            return $"drives {current.FromNodeId}->{current.ToNodeId}, not the fillet {shared.Id}-{far.Id} beside it";
        }

        return (offArcFt > TaxiEdgeLocator.OnFilletArcMaxOffsetFt) ? $"{offArcFt:F1} ft off the fillet {shared.Id}-{far.Id}, not on it" : null;
    }

    /// <summary>How far (ft) <paramref name="position"/> stands off <paramref name="arc"/>'s curve; infinite past either of its ends.</summary>
    private static double OffArcFt(GroundArc arc, LatLon position) => TaxiEdgeLocator.InsideArcDistanceFt(arc, position) ?? double.PositiveInfinity;

    /// <summary>A route from where an aircraft stands onto a set of goal nodes, and the goal it reached.</summary>
    /// <param name="Segments">The lead-in segment into the start node, if any, then the auto route from there to the goal.</param>
    /// <param name="GoalNodeId">The goal node the route ends at.</param>
    internal readonly record struct RouteFromHere(List<TaxiRouteSegment> Segments, int GoalNodeId);

    /// <summary>
    /// The auto route from where <paramref name="aircraft"/> stands (<see cref="StartOf"/>, without the ahead check, as a
    /// re-plan starts) to the cheapest of <paramref name="goals"/>, led in by its lead-in segment. It never drives back along the
    /// move it arrived on: never back along its lead-in, or with none, along its newest trail edge (<see cref="BackMove"/>), or
    /// with no trail, along the edge it stands on where that edge's far end lies behind it (<see cref="BackAlongEdgeUnder"/>).
    /// Anything else the graph offers, a loop round to a goal behind it included, is left to the router. Null with no start node
    /// or no route.
    /// </summary>
    internal static RouteFromHere? RouteOnto(AirportGroundLayout layout, AircraftState aircraft, IReadOnlySet<int> goals)
    {
        if (StartOf(layout, aircraft, requireAhead: false) is not { } routeStart)
        {
            return null;
        }

        (int From, int To)? forbidden = routeStart.LeadIn is { } into
            ? (into.ToNodeId, into.FromNodeId)
            : (BackMove(layout, aircraft) ?? BackAlongEdgeUnder(layout, aircraft, routeStart.Node));
        if (TaxiClass.Of(aircraft).FindRoute(layout, routeStart.Node.Id, goals, forbidden) is not { } found)
        {
            return null;
        }

        List<TaxiRouteSegment> segments = routeStart.LeadIn is { } leadIn ? [leadIn, .. found.Route.Segments] : [.. found.Route.Segments];
        return new RouteFromHere(segments, found.GoalNodeId);
    }

    /// <summary>
    /// The move from <paramref name="start"/> back along the straight edge <paramref name="aircraft"/> stands on, toward the end
    /// behind it; null when it stands on no edge that ends at <paramref name="start"/>, or when that move does not leave back
    /// across the heading (<see cref="LiesAhead"/>), which forbids nothing the aircraft is doing.
    /// </summary>
    private static (int From, int To)? BackAlongEdgeUnder(AirportGroundLayout layout, AircraftState aircraft, GroundNode start) =>
        ((EdgeUnder(layout, aircraft) is { } edge) && edge.Nodes.Any(n => n.Id == start.Id) && !LiesAhead(aircraft, edge.OtherNode(start)))
            ? (start.Id, edge.OtherNode(start).Id)
            : null;

    /// <summary>Whether <paramref name="node"/> lies ahead of <paramref name="aircraft"/>: its bearing within 90° of the heading.</summary>
    internal static bool LiesAhead(AircraftState aircraft, GroundNode node) =>
        Math.Abs(GeoMath.SignedBearingDifference(aircraft.TrueHeading.Degrees, GeoMath.BearingTo(aircraft.Position, node.Position))) <= 90.0;

    /// <summary>
    /// Whether <paramref name="edge"/> leaves its from-node within 90° of <paramref name="aircraft"/>'s heading: a route starting on
    /// it does not turn the aircraft back across its heading.
    /// </summary>
    internal static bool DepartsAhead(AircraftState aircraft, DirectionalEdge edge) =>
        Math.Abs(GeoMath.SignedBearingDifference(aircraft.TrueHeading.Degrees, edge.DepartureBearing)) <= 90.0;

    /// <summary>The trail's start node for <see cref="StartNode"/>; null when the trail gives none.</summary>
    private static GroundNode? TrailStartNode(AirportGroundLayout layout, AircraftState aircraft, bool requireAhead)
    {
        IReadOnlyList<TaxiTrailEdge> trail = aircraft.Ground.TaxiEdgeTrail.Edges;
        if ((trail.Count == 0) || (trail[^1].Resolve(layout) is not { } newest))
        {
            return null;
        }

        if (TrailCandidate(newest, trail.Count > 1 ? trail[^2] : null, aircraft) is not { } end)
        {
            Log.LogDebug(
                "[FollowPlan] {Callsign}: off its newest trail edge {A}-{B}; starting from the taxi start pick",
                aircraft.Callsign,
                newest.Nodes[0].Id,
                newest.Nodes[1].Id
            );
            return null;
        }

        double towardDeg = GeoMath.BearingTo(aircraft.Position, end.Position);
        if (requireAhead && (Math.Abs(GeoMath.SignedBearingDifference(aircraft.TrueHeading.Degrees, towardDeg)) > 90.0))
        {
            Log.LogDebug(
                "[FollowPlan] {Callsign}: trail start node {Node} lies behind it; starting from the taxi start pick",
                aircraft.Callsign,
                end.Id
            );
            return null;
        }

        return end;
    }

    /// <summary>
    /// The node the trail says <paramref name="aircraft"/> drives toward, before the ahead check: within
    /// <see cref="TaxiEdgeLocator.OnTaxiwayMaxOffsetFt"/> of <paramref name="newest"/>, the end of it the aircraft drives toward
    /// (<see cref="TrailEndNode"/>). Null farther off.
    /// </summary>
    private static GroundNode? TrailCandidate(GroundEdge newest, TaxiTrailEdge? previous, AircraftState aircraft)
    {
        double offEdgeFt = GeoMath.DistanceToSegmentFt(aircraft.Position, newest.Nodes[0].Position, newest.Nodes[1].Position);
        return (offEdgeFt <= TaxiEdgeLocator.OnTaxiwayMaxOffsetFt) ? TrailEndNode(newest, previous, aircraft) : null;
    }

    /// <summary>
    /// The end of <paramref name="newest"/> the aircraft drives toward: the one it does not share with
    /// <paramref name="previous"/>, the edge recorded before it, which the aircraft came from. With no previous edge, or one
    /// that does not meet it, the end the edge runs toward within 90° of the aircraft's heading.
    /// </summary>
    private static GroundNode TrailEndNode(GroundEdge newest, TaxiTrailEdge? previous, AircraftState aircraft)
    {
        GroundNode a = newest.Nodes[0];
        GroundNode b = newest.Nodes[1];
        if (previous is { } prev)
        {
            if ((a.Id == prev.NodeA) || (a.Id == prev.NodeB))
            {
                return b;
            }

            if ((b.Id == prev.NodeA) || (b.Id == prev.NodeB))
            {
                return a;
            }
        }

        double alongDeg = GeoMath.BearingTo(a.Position, b.Position);
        return (Math.Abs(GeoMath.SignedBearingDifference(aircraft.TrueHeading.Degrees, alongDeg)) <= 90.0) ? b : a;
    }

    /// <summary>
    /// The edge the follower stands mid-way along into <paramref name="start"/>, the node its route to the merge starts at,
    /// as a segment pointed into that node: the follow route's first segment, so the follower drives on from where it stands
    /// rather than treating the stretch back to its route's first node as a centreline it is off. Any straight edge counts,
    /// a runway centreline too (a follower released from a hold on a runway it is staged on). Null when the follower is at
    /// <paramref name="start"/>, faces away from it, or stands on no edge into it: within
    /// <see cref="TaxiEdgeLocator.OnTaxiwayMaxOffsetFt"/> of the edge and projecting between its ends.
    /// </summary>
    private static TaxiRouteSegment? LeadInSegment(GroundNode start, AircraftState follower)
    {
        if ((GeoMath.DistanceNm(follower.Position, start.Position) * GeoMath.FeetPerNm) <= AirportGroundLayout.AtNodeToleranceFt)
        {
            return null;
        }

        GroundEdge? nearest = null;
        double nearestOffFt = TaxiEdgeLocator.OnTaxiwayMaxOffsetFt;
        foreach (GroundEdge edge in start.Edges.OfType<GroundEdge>())
        {
            GroundNode behind = edge.OtherNode(start);
            (double alongFt, double offFt) = ProjectOntoEdge(edge, behind, follower.Position);
            bool between = (alongFt > 0.0) && (alongFt < (edge.DistanceNm * GeoMath.FeetPerNm));
            if (between && (offFt <= nearestOffFt))
            {
                nearest = edge;
                nearestOffFt = offFt;
            }
        }

        if (nearest is null)
        {
            return null;
        }

        double towardStartDeg = GeoMath.BearingTo(follower.Position, start.Position);
        if (Math.Abs(GeoMath.SignedBearingDifference(follower.TrueHeading.Degrees, towardStartDeg)) > 90.0)
        {
            return null;
        }

        return new TaxiRouteSegment { Edge = nearest.Directed(nearest.OtherNode(start), start), TaxiwayName = nearest.TaxiwayName ?? "" };
    }

    /// <summary>
    /// How far along <paramref name="edge"/>, driven from <paramref name="from"/>, <paramref name="position"/> projects, feet:
    /// onto the piece of the edge's polyline nearest it, clamped to that piece.
    /// </summary>
    private static double AlongEdgeFt(GroundEdge edge, GroundNode from, LatLon position) => ProjectOntoEdge(edge, from, position).AlongFt;

    /// <summary>
    /// Where <paramref name="position"/> projects onto <paramref name="edge"/> driven from <paramref name="from"/>: how far
    /// along it, feet, onto the piece of the edge's polyline nearest it, clamped to that piece, and how far off that piece.
    /// </summary>
    private static (double AlongFt, double OffFt) ProjectOntoEdge(GroundEdge edge, GroundNode from, LatLon position)
    {
        List<LatLon> points = EdgeGeometry.PointsFrom(edge, from);
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

        return (bestAlongFt, bestOffFt);
    }

    /// <summary>
    /// The lead's path as directed edges, oldest first, and the index of the node ahead of the lead on the edge it is on
    /// (node <c>i</c> is where edge <c>i</c> starts; the last edge's end is node <c>Edges.Count</c>). <c>Edges.Count + 1</c> when
    /// the path ends behind the lead, cut where it rounds a fillet arc the follower may not drive (<see cref="BuildLeadPath"/>):
    /// no node of the path lies ahead of it.
    /// </summary>
    private sealed record LeadPath(List<DirectionalEdge> Edges, int AheadIndex);

    /// <summary>The aircraft class a graph search runs for: its performance category and wake class.</summary>
    internal readonly record struct TaxiClass(AircraftCategory Category, WakeTurbulenceData.WakeClass Wake)
    {
        public static TaxiClass Of(AircraftState aircraft)
        {
            AircraftCategory category = AircraftCategorization.Categorize(aircraft.AircraftType);
            return new TaxiClass(category, WakeTurbulenceData.WakeClassForType(aircraft.AircraftType, category));
        }

        /// <summary>The auto route from <paramref name="fromNodeId"/> to the cheapest of <paramref name="goals"/>; null when none.</summary>
        public GoalRoute? FindRoute(AirportGroundLayout layout, int fromNodeId, IReadOnlySet<int> goals, (int From, int To)? forbiddenFirstMove) =>
            TaxiPathfinder.FindRouteToNearestGoal(layout, fromNodeId, goals, Category, Wake, forbiddenFirstMove);
    }

    /// <summary>
    /// The edge the lead taxis on, pointed the way it is going, and its route ahead of that edge (<see cref="OnRoute"/>); null
    /// while it pushes back or is parked, and while it is off every taxiway and every fillet arc of its route.
    /// </summary>
    private static (DirectionalEdge Current, IReadOnlyList<TaxiRouteSegment> RouteAhead)? LocateLead(
        AirportGroundLayout layout,
        AircraftState lead
    ) => lead.Phases?.CurrentPhase is PushbackPhase or AtParkingPhase ? null : OnRoute(EdgeUnder(layout, lead), lead);

    private static GroundEdge? EdgeUnder(AirportGroundLayout layout, AircraftState aircraft) =>
        TaxiEdgeLocator.EdgeUnder(
            layout,
            aircraft.Position,
            aircraft.Ground.TaxiEdgeTrail.Newest is { } newest ? (newest.NodeA, newest.NodeB) : null
        );

    /// <summary>
    /// The route the lead drives, from its current segment on: a follower's own follow route (or clearing route) while its follow
    /// is its current phase (<see cref="FollowingPhase.DrivenRouteOf"/>), none while a hold or a crossing stands in front of the
    /// follow — never a follower's assigned route, which predates its <c>FOLLOWG</c> — else its assigned route. None with no
    /// route left.
    /// </summary>
    internal static IReadOnlyList<TaxiRouteSegment> RemainingRoute(AircraftState lead)
    {
        TaxiRoute? route = IsFollower(lead)
            ? (lead.Phases?.CurrentPhase is FollowingPhase ? FollowingPhase.DrivenRouteOf(lead) : null)
            : lead.Ground.AssignedTaxiRoute;
        return route is { IsComplete: false } ? route.Segments[route.CurrentSegmentIndex..] : [];
    }

    /// <summary>
    /// Whether the route <paramref name="lead"/> drives now (<see cref="RemainingRoute"/>) is still the one a plan's lead path from
    /// its merge, <paramref name="leadPathFromMerge"/>, was built from: no route left, or a route whose segments, from the first
    /// one on the path, lie along the path in order (the path may hold bridging edges between them, and the square way round a
    /// fillet arc too tight for the follower in place of the arc, <see cref="IndexOnPath"/>) and that ends where the path ends —
    /// or runs on past the path's end where the planner cut it for <paramref name="follower"/> (<see cref="PathCutBefore"/>). With
    /// no lead path from the merge — the merge is where the lead's path ends — a route that ends at <paramref name="mergeNode"/>.
    /// A route whose first segment is a fillet arc the follower may not drive out of the path's end (or the merge, with no path
    /// from it) matches too: the lead is on that arc and the planner cut its path there (<see cref="IsCutArcFrom"/>). False
    /// once the lead is re-routed — a new <c>TAXI</c>, or a lead that is itself a follower planning a new follow route — so
    /// the follower plans again that tick.
    /// </summary>
    public static bool LeadRouteMatchesPath(
        AirportGroundLayout layout,
        AircraftState follower,
        AircraftState lead,
        int mergeNode,
        IReadOnlyList<DirectionalEdge> leadPathFromMerge
    )
    {
        IReadOnlyList<TaxiRouteSegment> route = RemainingRoute(lead);
        if (route.Count == 0)
        {
            return true;
        }

        DirectionalEdge? arrivedOn = leadPathFromMerge.Count == 0 ? null : leadPathFromMerge[^1];
        if (IsCutArcFrom(layout, follower, arrivedOn, arrivedOn?.ToNodeId ?? mergeNode, route[0]))
        {
            return true;
        }

        if (leadPathFromMerge.Count == 0)
        {
            return route[^1].ToNodeId == mergeNode;
        }

        return RouteRunsAlongPath(layout, follower, route, leadPathFromMerge);
    }

    /// <summary>
    /// Whether <paramref name="route"/>'s segments, from the first one on <paramref name="path"/>, lie along it in order
    /// (<see cref="IndexOnPath"/>) and end where it ends, or run on past a cut (<see cref="PathCutBefore"/>).
    /// </summary>
    private static bool RouteRunsAlongPath(
        AirportGroundLayout layout,
        AircraftState follower,
        IReadOnlyList<TaxiRouteSegment> route,
        IReadOnlyList<DirectionalEdge> path
    )
    {
        int onPath = 0;
        bool found = false;
        foreach (TaxiRouteSegment segment in route)
        {
            int at = IndexOnPath(path, segment, onPath);
            if (at >= 0)
            {
                found = true;
                onPath = at + 1;
            }
            else if (found)
            {
                return PathCutBefore(layout, follower, path, onPath, segment);
            }
        }

        return found && (route[^1].ToNodeId == path[^1].ToNodeId);
    }

    /// <summary>
    /// Whether <paramref name="segment"/> is a fillet arc out of node <paramref name="nodeId"/> that <paramref name="follower"/>
    /// may not drive (<see cref="IsRefusedArc"/>), not even at its tight-turn floor (<see cref="TightArcDrivable"/>), and that has
    /// no acceptable square way round from <paramref name="arrivedOn"/>, the path edge into that node (null when not known)
    /// (<see cref="SquareWayRound"/>): the arc <see cref="TryAppendUsable"/> cuts the path at. The lead's first remaining
    /// segment is that arc, out of the lead path's end, when the lead is on it and the planner cut the path there
    /// (<see cref="BuildLeadPath"/>).
    /// </summary>
    private static bool IsCutArcFrom(
        AirportGroundLayout layout,
        AircraftState follower,
        DirectionalEdge? arrivedOn,
        int nodeId,
        TaxiRouteSegment segment
    )
    {
        var followerClass = TaxiClass.Of(follower);
        return (segment.FromNodeId == nodeId)
            && (segment.Edge.Edge is GroundArc arc)
            && IsRefusedArc(layout, followerClass, segment.Edge)
            && !TightArcDrivable(layout, followerClass, segment.Edge)
            && (SquareWayRound(layout, arrivedOn, segment.Edge, arc, followerClass) is null);
    }

    /// <summary>
    /// Whether the planner cut <paramref name="path"/> short of <paramref name="next"/>, the lead route's first segment not on it,
    /// <paramref name="onPath"/> the path edges the route has run along up to it. A fillet arc out of the path's last node is a
    /// cut when <paramref name="follower"/> may not drive it, not even at its tight-turn floor (<see cref="IsCutArcFrom"/>; the
    /// arc had no acceptable square way round, <see cref="TryAppendUsable"/>), wherever the route's earlier segments end on the
    /// path: the path may end in a bridge to the arc. A segment that does not start at the path's last node is a cut once the
    /// route has run along the whole path (the gap to it bridged by no path the follower may drive, <see cref="TryBridge"/>). A
    /// straight edge out of the path's last node, or an arc the follower may drive, is a route run on past the path's end — a
    /// re-route — never a cut.
    /// </summary>
    private static bool PathCutBefore(
        AirportGroundLayout layout,
        AircraftState follower,
        IReadOnlyList<DirectionalEdge> path,
        int onPath,
        TaxiRouteSegment next
    )
    {
        bool startsAtEnd = next.FromNodeId == path[^1].ToNodeId;
        if (startsAtEnd && (next.Edge.Edge is GroundArc))
        {
            return IsCutArcFrom(layout, follower, path[^1], path[^1].ToNodeId, next);
        }

        return !startsAtEnd && (onPath == path.Count);
    }

    /// <summary>
    /// The index of <paramref name="segment"/>'s directed edge on <paramref name="path"/> from <paramref name="from"/> on; -1 when
    /// absent. A fillet arc the path does not hold matches the square way round it the planner put there in its place
    /// (<see cref="TryAppendUsable"/>): a path edge leaving the arc's start, then the first later one reaching its end, whose
    /// index it is — when that run is no longer than the planner accepts for a square way round the arc
    /// (<see cref="SquareWayStubFactor"/> times <see cref="ArcStubLengthFt"/>), so a route cut short over an arc never matches
    /// a longer way round still on the path.
    /// </summary>
    private static int IndexOnPath(IReadOnlyList<DirectionalEdge> path, TaxiRouteSegment segment, int from)
    {
        int exact = FirstIndex(path, from, edge => (edge.FromNodeId == segment.FromNodeId) && (edge.ToNodeId == segment.ToNodeId));
        if ((exact >= 0) || (segment.Edge.Edge is not GroundArc arc))
        {
            return exact;
        }

        int leaves = FirstIndex(path, from, edge => edge.FromNodeId == segment.FromNodeId);
        int reaches = leaves < 0 ? -1 : FirstIndex(path, leaves, edge => edge.ToNodeId == segment.ToNodeId);
        return (reaches >= 0) && (SpanFt(path, leaves, reaches) <= (SquareWayStubFactor * ArcStubLengthFt(arc))) ? reaches : -1;
    }

    /// <summary>
    /// The summed length (ft) of <paramref name="path"/>'s edges <paramref name="first"/> to <paramref name="last"/>, both
    /// included.
    /// </summary>
    private static double SpanFt(IReadOnlyList<DirectionalEdge> path, int first, int last)
    {
        double nm = 0.0;
        for (int i = first; i <= last; i++)
        {
            nm += path[i].DistanceNm;
        }

        return nm * GeoMath.FeetPerNm;
    }

    /// <summary>
    /// The index of the first item of <paramref name="items"/> from <paramref name="from"/> on that <paramref name="match"/>
    /// accepts; -1 when none.
    /// </summary>
    private static int FirstIndex<T>(IReadOnlyList<T> items, int from, Func<T, bool> match)
    {
        for (int i = from; i < items.Count; i++)
        {
            if (match(items[i]))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Whether <paramref name="lead"/> is itself a <c>FOLLOWG</c> follower: a <see cref="FollowingPhase"/> is its current phase or
    /// one still queued — behind the hold at a bar it stopped at, or the crossing a CROSS put in front of the follow. <c>FOLLOWG</c>
    /// leaves the assigned route in place, so a follower's assigned route predates its follow and is stale; a <c>TAXI</c> clears
    /// the follow, so a later assigned route is never read as stale.
    /// </summary>
    private static bool IsFollower(AircraftState lead) =>
        (lead.Phases is { } phases) && phases.Phases.Skip(Math.Max(phases.CurrentIndex, 0)).Any(static p => p is FollowingPhase);

    /// <summary>
    /// The edge the lead is on, pointed the way it is going, and the lead's route ahead of that edge. On its route, the first
    /// remaining segment over the edge points it, and only the segments after that one lie ahead: the lead has passed those
    /// before it even while its segment index still names one — held on its first segment through an entry-alignment turn, or
    /// sampled onto the next edge before it reaches the node. When no remaining segment runs over <paramref name="edge"/> — the
    /// straight edge under the lead, null off every taxiway — the lead is on the first remaining fillet arc whose curve it lies
    /// beside (<see cref="BesideArc"/>): rounding an arc, the straight edge nearest it is a stub the arc cuts past. That arc,
    /// as the route drives it, is the edge it is on, and only the segments after it lie ahead. Off its route, the edge points
    /// toward the end nearer its heading (<see cref="OrientByHeading"/>) and every remaining segment lies ahead; null with no
    /// edge under the lead and no route arc beside it.
    /// </summary>
    private static (DirectionalEdge Current, IReadOnlyList<TaxiRouteSegment> RouteAhead)? OnRoute(GroundEdge? edge, AircraftState lead)
    {
        IReadOnlyList<TaxiRouteSegment> remaining = RemainingRoute(lead);
        if (edge is not null)
        {
            int onEdge = FirstIndex(remaining, 0, segment => Joins(edge, segment.FromNodeId, segment.ToNodeId));
            if (onEdge >= 0)
            {
                return (Directed(edge, remaining[onEdge].Edge.FromNode, remaining[onEdge].Edge.ToNode), [.. remaining.Skip(onEdge + 1)]);
            }
        }

        int onArc = FirstIndex(remaining, 0, segment => BesideArc(segment, lead.Position));
        if (onArc >= 0)
        {
            return (remaining[onArc].Edge, [.. remaining.Skip(onArc + 1)]);
        }

        return edge is null ? null : (OrientByHeading(edge, lead), remaining);
    }

    /// <summary>
    /// Whether <paramref name="segment"/> is a fillet arc whose curve passes within <see cref="TaxiEdgeLocator.OnTaxiwayMaxOffsetFt"/>
    /// of <paramref name="position"/> at a point strictly between its ends (<see cref="TaxiEdgeLocator.InsideArcDistanceFt"/>).
    /// </summary>
    private static bool BesideArc(TaxiRouteSegment segment, LatLon position) =>
        (segment.Edge.Edge is GroundArc arc)
        && (TaxiEdgeLocator.InsideArcDistanceFt(arc, position) is { } offFt)
        && (offFt <= TaxiEdgeLocator.OnTaxiwayMaxOffsetFt);

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

    /// <summary>
    /// The lead's path: its trail behind <paramref name="current"/>, the edge it is on, then its route ahead. The follower drives
    /// all of it, so every stretch the planner searches or takes over is held to the follower's taxi class, not the lead's. A
    /// fillet arc the lead is on that the follower may not drive goes through <see cref="TryAppendUsable"/> as an arc on its
    /// route ahead does: the square way round in its place, the node ahead of the lead the arc's far end, or the arc at the
    /// follower's tight-turn floor; with neither, the path ends with the trail, the lead past its end, and the route ahead is
    /// dropped. Null when that leaves no path: no trail behind the lead.
    /// </summary>
    private static LeadPath? BuildLeadPath(
        AirportGroundLayout layout,
        AircraftState lead,
        AircraftState follower,
        DirectionalEdge current,
        IReadOnlyList<TaxiRouteSegment> routeAhead
    )
    {
        var followerClass = TaxiClass.Of(follower);
        List<DirectionalEdge> edges = TrailBehind(layout, lead, current, followerClass);
        if (!TryAppendUsable(layout, edges, current, followerClass, RestOfRouteDropped))
        {
            return edges.Count == 0 ? null : new LeadPath(edges, edges.Count + 1);
        }

        int aheadIndex = edges.Count;
        AppendRoute(layout, edges, routeAhead, followerClass);
        return new LeadPath(edges, aheadIndex);
    }

    /// <summary>
    /// The lead's trail behind <paramref name="current"/>, oldest first, walked back from the newest edge. Each trail edge is
    /// pointed toward the edge after it; where the two do not meet, the shortest graph path the follower's class may drive fills
    /// the gap. The walk stops at the first edge no such path joins, and at the first trail edge whose step back would revisit
    /// an edge the walk already holds — where the lead reversed, as after a push out along a taxiway it then taxied back down —
    /// so the path never runs out and back over an edge. A step back that has to fill a gap takes the fillet arc where what it
    /// fills is the straight stubs either side of one the lead rounded (<see cref="CutCorner"/>).
    /// </summary>
    private static List<DirectionalEdge> TrailBehind(AirportGroundLayout layout, AircraftState lead, DirectionalEdge current, TaxiClass followerClass)
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

            if ((ReachForward(layout, edge, cursor, followerClass) is not { } step) || !HoldsNoneOf(held, step))
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
            if (CutCorner(layout, followerClass, reversed, step.Count, lead.Callsign) is { } replaced)
            {
                held.ExceptWith(replaced.Select(EdgeKey));
                held.Add(EdgeKey(reversed[^1]));
            }

            cursor = reversed[^1].FromNode;
        }

        reversed.Reverse();
        return reversed;
    }

    /// <summary>
    /// Replaces the newest run of the path behind the lead with a fillet arc where the lead rounded one: the run starts at
    /// the arc's tangent node the step back from the cursor ends at and reaches the arc's other tangent node round the
    /// junction through edges the lead never drove — the straight stubs either side of the arc its 1 Hz trail samples. A
    /// run is the newest <paramref name="stepCount"/> edges, the step back that filled a gap, plus at most the one edge the
    /// walk held before that step, the stub on the arc's far side; it never reaches further toward the lead, so a lead that
    /// drove the junction itself, whose trail has no gap there, keeps the junction's edges, and a trail that leaves the
    /// corner and comes back to it keeps its loop. A step of one edge filled no gap and is left alone. The arc must suit
    /// the follower's class (<see cref="UsableArc"/>): an arc tighter than the follower's own main-gear turn radius is
    /// refused and the run keeps the square corner through the junction. Returns the replaced run, which the caller's held
    /// set swaps for the arc so a later step back never re-uses it; null when no run is replaced.
    /// </summary>
    private static List<DirectionalEdge>? CutCorner(
        AirportGroundLayout layout,
        TaxiClass followerClass,
        List<DirectionalEdge> reversed,
        int stepCount,
        string callsign
    )
    {
        if (stepCount < 2)
        {
            return null;
        }

        GroundNode tangent = reversed[^1].FromNode;
        // The gap step's own edges and the one edge before it; a longer run would reach into what the lead drove after it.
        int longestRun = Math.Min(stepCount + 1, reversed.Count);
        for (int run = 2; run <= longestRun; run++)
        {
            GroundNode far = reversed[^run].ToNode;
            if ((UsableArc(layout, followerClass, tangent, far) is not { } arc) || (RunFt(reversed, run) <= (arc.DistanceNm * GeoMath.FeetPerNm)))
            {
                continue;
            }

            DirectionalEdge arcBack = arc.Directed(tangent, far);
            List<DirectionalEdge> replaced = reversed.GetRange(reversed.Count - run, run);
            reversed.RemoveRange(reversed.Count - run, run);
            reversed.Add(arcBack);
            Log.LogDebug(
                "[FollowPlan] {Lead}: {Run} edges node {From} to {To} are a {Arc:F0} ft fillet arc's stubs; lead path takes the arc",
                callsign,
                run,
                tangent.Id,
                far.Id,
                arc.DistanceNm * GeoMath.FeetPerNm
            );
            return replaced;
        }

        return null;
    }

    /// <summary>The summed length (ft) of the newest <paramref name="count"/> edges of <paramref name="edges"/>.</summary>
    private static double RunFt(List<DirectionalEdge> edges, int count)
    {
        double ft = 0.0;
        for (int i = edges.Count - count; i < edges.Count; i++)
        {
            ft += edges[i].DistanceNm * GeoMath.FeetPerNm;
        }

        return ft;
    }

    /// <summary>
    /// The first arc joining <paramref name="from"/> and <paramref name="to"/> that the follower's class may drive that way
    /// (<see cref="ArcDrivable"/>); null when no arc joining them is drivable.
    /// </summary>
    private static GroundArc? UsableArc(AirportGroundLayout layout, TaxiClass followerClass, GroundNode from, GroundNode to) =>
        from.Edges.OfType<GroundArc>().FirstOrDefault(arc => (arc.OtherNode(from).Id == to.Id) && ArcDrivable(layout, followerClass, arc, from, to));

    /// <summary>
    /// Whether the follower's class may drive <paramref name="arc"/> from <paramref name="from"/> to <paramref name="to"/>: its
    /// effective radius (<see cref="EffectiveArcRadiusFt"/>) not below the follower's own main-gear turn radius
    /// (<see cref="CategoryPerformance.MainGearTurnRadiusFt"/>: a jet needs 25 ft, a helicopter 10 ft), its tightest radius not
    /// below the floor no category steers under (<see cref="GeometricAdmissibility.MinSteerableArcRadiusFt"/>), and not a move
    /// one-way data or a blocked turn forbids — the one-way and blocked-turn gates <see cref="TaxiPathfinder"/>'s searches apply
    /// to the same traversal. A fillet leaves the edge it joins tangent to itself, so its entry from either tangent node is no
    /// heading change for any category.
    /// </summary>
    private static bool ArcDrivable(AirportGroundLayout layout, TaxiClass followerClass, GroundArc arc, GroundNode from, GroundNode to) =>
        ArcDrivableAbove(layout, followerClass, arc, (from.Id, to.Id), CategoryPerformance.MainGearTurnRadiusFt(followerClass.Category));

    /// <summary>
    /// Whether the follower's class may drive <paramref name="edge"/>, a fillet arc, at its tight-turn floor: as
    /// <see cref="ArcDrivable"/>, with the arc's effective radius held to the follower's
    /// <see cref="CategoryPerformance.TightTurnFloorRadiusFt"/> (a jet 15 ft) in place of its main-gear turn radius. The navigator
    /// rounds such an arc at tight-turn speed; the planner keeps it only where it has no acceptable square way round
    /// (<see cref="TryAppendUsable"/>).
    /// </summary>
    private static bool TightArcDrivable(AirportGroundLayout layout, TaxiClass followerClass, DirectionalEdge edge) =>
        (edge.Edge is GroundArc arc)
        && ArcDrivableAbove(
            layout,
            followerClass,
            arc,
            (edge.FromNodeId, edge.ToNodeId),
            CategoryPerformance.TightTurnFloorRadiusFt(followerClass.Category)
        );

    /// <summary>
    /// Whether the follower's class may drive <paramref name="arc"/> as <paramref name="move"/> with an effective radius
    /// (<see cref="EffectiveArcRadiusFt"/>) of at least <paramref name="minEffectiveRadiusFt"/>: its tightest radius not below
    /// <see cref="GeometricAdmissibility.MinSteerableArcRadiusFt"/>, and not a move one-way data or a blocked turn forbids.
    /// </summary>
    private static bool ArcDrivableAbove(
        AirportGroundLayout layout,
        TaxiClass followerClass,
        GroundArc arc,
        (int From, int To) move,
        double minEffectiveRadiusFt
    ) =>
        (arc.MinRadiusOfCurvatureFt >= GeometricAdmissibility.MinSteerableArcRadiusFt)
        && (EffectiveArcRadiusFt(arc) >= minEffectiveRadiusFt)
        && !OneWayResolver.GetForbiddenMoves(layout, followerClass.Wake).Contains(move)
        && !BlockedTurnResolver.GetBlocked(layout).ForbiddenArcMoves.Contains(move);

    /// <summary>
    /// The radius (ft) an aircraft rounding <paramref name="arc"/> turns on: the arc's length over its total heading change, in
    /// radians, from the heading it leaves its first node on (toward its first control point) to the heading it reaches its
    /// last node on (from its second control point). A Bezier fillet's tightest radius
    /// (<see cref="GroundArc.MinRadiusOfCurvatureFt"/>) can be a curvature spike far shorter than a wheelbase, which no
    /// aircraft follows; the turn it makes is spread over the arc. Infinite for an arc that does not turn.
    /// </summary>
    public static double EffectiveArcRadiusFt(GroundArc arc)
    {
        double leaveDeg = GeoMath.BearingTo(arc.Nodes[0].Position, new LatLon(arc.P1Lat, arc.P1Lon));
        double reachDeg = GeoMath.BearingTo(new LatLon(arc.P2Lat, arc.P2Lon), arc.Nodes[1].Position);
        double turnRad = Math.Abs(GeoMath.SignedBearingDifference(leaveDeg, reachDeg)) * Math.PI / 180.0;
        return turnRad < 1e-6 ? double.PositiveInfinity : arc.DistanceNm * GeoMath.FeetPerNm / turnRad;
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
    /// one-way lane the lead used the right way is not excluded — with the follower's class, which drives it. A gap route that
    /// holds a fillet arc too tight for the follower (<see cref="ArcDrivable"/>) gives way to the other end's route when that one
    /// holds none; with both holding one, the cheaper is driven the square way round each such arc, as the route ahead is
    /// (<see cref="TryAppendUsable"/>), or kept at the follower's tight-turn floor. Null when no path joins them, or an arc in the
    /// cheaper one has neither.
    /// </summary>
    private static List<DirectionalEdge>? ReachForward(AirportGroundLayout layout, GroundEdge edge, GroundNode cursor, TaxiClass followerClass)
    {
        if ((edge.Nodes[0].Id == cursor.Id) || (edge.Nodes[1].Id == cursor.Id))
        {
            return [Directed(edge, edge.OtherNode(cursor), cursor)];
        }

        List<GoalRoute> gaps = GapRoutes(layout, edge, cursor, followerClass);
        if ((gaps.FirstOrDefault(gap => !HoldsRefusedArc(layout, followerClass, gap.Route)) ?? gaps.FirstOrDefault()) is not { } chosen)
        {
            return null;
        }

        GroundNode exit = chosen.Route.Segments[0].Edge.FromNode;
        List<DirectionalEdge> forward = [Directed(edge, edge.OtherNode(exit), exit)];
        foreach (TaxiRouteSegment segment in chosen.Route.Segments)
        {
            if (!TryAppendUsable(layout, forward, segment.Edge, followerClass, "older trail dropped"))
            {
                return null;
            }
        }

        forward.Reverse();
        return forward;
    }

    /// <summary>
    /// The routes from either end of <paramref name="edge"/> to <paramref name="cursor"/>, which is not an end of it, cheaper
    /// first (the first end's on a tie); empty when neither end reaches it.
    /// </summary>
    private static List<GoalRoute> GapRoutes(AirportGroundLayout layout, GroundEdge edge, GroundNode cursor, TaxiClass taxiClass)
    {
        HashSet<int> goal = [cursor.Id];
        GoalRoute?[] routes = [taxiClass.FindRoute(layout, edge.Nodes[0].Id, goal, null), taxiClass.FindRoute(layout, edge.Nodes[1].Id, goal, null)];
        return [.. routes.OfType<GoalRoute>().OrderBy(route => route.Cost)];
    }

    /// <summary>
    /// Appends <paramref name="routeAhead"/>, the lead's route ahead of the edge it is on (<see cref="OnRoute"/>); a segment
    /// that does not start where the path ends is reached by the shortest graph path the follower's class may drive. Every edge
    /// appended passes <see cref="TryAppendUsable"/>, so a fillet arc too tight for the follower is driven the square way
    /// round, or kept at the follower's tight-turn floor; where neither is acceptable, the rest of the route is dropped.
    /// </summary>
    private static void AppendRoute(
        AirportGroundLayout layout,
        List<DirectionalEdge> edges,
        IReadOnlyList<TaxiRouteSegment> routeAhead,
        TaxiClass followerClass
    )
    {
        foreach (TaxiRouteSegment segment in routeAhead)
        {
            int end = edges[^1].ToNodeId;
            if ((end != segment.FromNodeId) && !TryBridge(layout, edges, segment.FromNodeId, followerClass))
            {
                return;
            }

            if (!TryAppendUsable(layout, edges, segment.Edge, followerClass, RestOfRouteDropped))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Appends the shortest graph path the follower's class may drive from the end of <paramref name="edges"/> to
    /// <paramref name="toNodeId"/>, each edge through <see cref="TryAppendUsable"/>, all or nothing: false, with nothing
    /// appended, when there is no such path or one of its edges is refused.
    /// </summary>
    private static bool TryBridge(AirportGroundLayout layout, List<DirectionalEdge> edges, int toNodeId, TaxiClass followerClass)
    {
        HashSet<int> goal = [toNodeId];
        if (followerClass.FindRoute(layout, edges[^1].ToNodeId, goal, null) is not { } bridge)
        {
            Log.LogDebug("[FollowPlan] no path from node {From} to route node {To}; rest of the route dropped", edges[^1].ToNodeId, toNodeId);
            return false;
        }

        List<DirectionalEdge> scratch = [edges[^1]];
        foreach (TaxiRouteSegment segment in bridge.Route.Segments)
        {
            if (!TryAppendUsable(layout, scratch, segment.Edge, followerClass, RestOfRouteDropped))
            {
                return false;
            }
        }

        edges.AddRange(scratch.Skip(1));
        return true;
    }

    /// <summary>
    /// How much longer than the arc's stub length (<see cref="ArcStubLengthFt"/>) the square way round a fillet arc too tight for
    /// the follower may be: a follower does not loop round a block away from its lead; it stops.
    /// </summary>
    private const double SquareWayStubFactor = 1.5;

    /// <summary>What the lead path's route ahead and its bridges drop when an arc on them is refused (<see cref="TryAppendUsable"/>).</summary>
    private const string RestOfRouteDropped = "rest of the lead route dropped";

    /// <summary>
    /// Appends <paramref name="edge"/>, or, when it is a fillet arc the follower may not drive (<see cref="ArcDrivable"/>), the
    /// square way round the junction (<see cref="SquareWayRound"/>); with no acceptable square way round, the arc itself when the
    /// follower may round it at its tight-turn floor (<see cref="TightArcDrivable"/>). False, with nothing appended, when it may
    /// not, logging what the caller then drops, <paramref name="dropped"/>.
    /// </summary>
    private static bool TryAppendUsable(
        AirportGroundLayout layout,
        List<DirectionalEdge> edges,
        DirectionalEdge edge,
        TaxiClass followerClass,
        string dropped
    )
    {
        if (!IsRefusedArc(layout, followerClass, edge))
        {
            edges.Add(edge);
            return true;
        }

        var arc = (GroundArc)edge.Edge;
        DirectionalEdge? arrivedOn = edges.Count > 0 ? edges[^1] : null;
        if (SquareWayRound(layout, arrivedOn, edge, arc, followerClass) is { } round)
        {
            Log.LogDebug(
                "[FollowPlan] fillet arc node {From} to {To} ({Radius:F1} ft effective) is too tight for a {Category} follower; "
                    + "lead path takes the square way round, {Edges} edges",
                edge.FromNodeId,
                edge.ToNodeId,
                EffectiveArcRadiusFt(arc),
                followerClass.Category,
                round.Count
            );
            edges.AddRange(round);
            return true;
        }

        if (TightArcDrivable(layout, followerClass, edge))
        {
            Log.LogDebug(
                "[FollowPlan] fillet arc node {From} to {To} ({Radius:F1} ft effective) is too tight for a {Category} follower and has no "
                    + "acceptable square way round; lead path keeps the arc, rounded at the {Floor:F0} ft tight-turn floor",
                edge.FromNodeId,
                edge.ToNodeId,
                EffectiveArcRadiusFt(arc),
                followerClass.Category,
                CategoryPerformance.TightTurnFloorRadiusFt(followerClass.Category)
            );
            edges.Add(edge);
            return true;
        }

        Log.LogDebug(
            "[FollowPlan] fillet arc node {From} to {To} ({Radius:F1} ft effective, {Tightest:F1} ft tightest radius) is too tight for "
                + "a {Category} follower even at its tight-turn floor and has no acceptable square way round; {Dropped}",
            edge.FromNodeId,
            edge.ToNodeId,
            EffectiveArcRadiusFt(arc),
            arc.MinRadiusOfCurvatureFt,
            followerClass.Category,
            dropped
        );
        return false;
    }

    /// <summary>
    /// The follower's own route between the two nodes of <paramref name="arc"/>, driven as <paramref name="edge"/>, that does not
    /// start over the arc — the square way round the junction — when it is acceptable: it holds no refused arc of its own, its
    /// first edge does not turn back along <paramref name="arrivedOn"/>, the edge the path reached the arc's start by (null when
    /// the path starts at the arc: a lead on the arc with no trail behind it), and it is no longer than
    /// <see cref="SquareWayStubFactor"/> times the arc's stub length. Null otherwise.
    /// </summary>
    private static List<DirectionalEdge>? SquareWayRound(
        AirportGroundLayout layout,
        DirectionalEdge? arrivedOn,
        DirectionalEdge edge,
        GroundArc arc,
        TaxiClass followerClass
    )
    {
        HashSet<int> goal = [edge.ToNodeId];
        if (
            (followerClass.FindRoute(layout, edge.FromNodeId, goal, (edge.FromNodeId, edge.ToNodeId)) is not { } round)
            || HoldsRefusedArc(layout, followerClass, round.Route)
        )
        {
            return null;
        }

        DirectionalEdge first = round.Route.Segments[0].Edge;
        bool turnsBack = (arrivedOn is not null) && (first.FromNodeId == arrivedOn.ToNodeId) && (first.ToNodeId == arrivedOn.FromNodeId);
        double lengthFt = round.Route.Segments.Sum(s => s.Edge.DistanceNm) * GeoMath.FeetPerNm;
        return turnsBack || (lengthFt > (SquareWayStubFactor * ArcStubLengthFt(arc))) ? null : [.. round.Route.Segments.Select(s => s.Edge)];
    }

    /// <summary>
    /// The stub length of <paramref name="arc"/> (ft): the lines through its two tangent nodes along its end headings — toward its
    /// control points — meet at a point P, and the stub length is the distance from one tangent node to P plus P to the other,
    /// the way round the corner the arc rounds (for a 90° fillet of radius r, 2r). The arc's own length when the lines do not
    /// meet ahead of both nodes.
    /// </summary>
    public static double ArcStubLengthFt(GroundArc arc)
    {
        LatLon a = arc.Nodes[0].Position;
        LatLon b = arc.Nodes[1].Position;
        double headingA = GeoMath.BearingTo(a, new LatLon(arc.P1Lat, arc.P1Lon)) * Math.PI / 180.0;
        double headingB = GeoMath.BearingTo(b, new LatLon(arc.P2Lat, arc.P2Lon)) * Math.PI / 180.0;
        double abFt = GeoMath.DistanceNm(a, b) * GeoMath.FeetPerNm;
        double abBearing = GeoMath.BearingTo(a, b) * Math.PI / 180.0;
        (double ux, double uy) = (Math.Sin(headingA), Math.Cos(headingA));
        (double vx, double vy) = (Math.Sin(headingB), Math.Cos(headingB));
        (double dx, double dy) = (abFt * Math.Sin(abBearing), abFt * Math.Cos(abBearing));
        // A + s·u = B + t·v, solved for the distances s from A and t from B to P.
        double det = (vx * uy) - (ux * vy);
        double s = ((vx * dy) - (dx * vy)) / det;
        double t = ((ux * dy) - (uy * dx)) / det;
        return (Math.Abs(det) < 1e-6) || (s <= 0.0) || (t <= 0.0) ? arc.DistanceNm * GeoMath.FeetPerNm : s + t;
    }

    /// <summary>Whether <paramref name="route"/> holds a fillet arc the follower may not drive (<see cref="ArcDrivable"/>).</summary>
    private static bool HoldsRefusedArc(AirportGroundLayout layout, TaxiClass followerClass, TaxiRoute route) =>
        route.Segments.Any(s => IsRefusedArc(layout, followerClass, s.Edge));

    /// <summary>
    /// Whether <paramref name="edge"/> is a fillet arc the follower may not drive that way: that arc itself is gated
    /// (<see cref="ArcDrivable"/>), not whichever arc first joins its two nodes.
    /// </summary>
    private static bool IsRefusedArc(AirportGroundLayout layout, TaxiClass followerClass, DirectionalEdge edge) =>
        (edge.Edge is GroundArc arc) && !ArcDrivable(layout, followerClass, arc, edge.FromNode, edge.ToNode);

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
