using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Airport;

/// <summary>What a controller-issued <c>HS &lt;target&gt;</c> resolves to against a live taxi route.</summary>
internal enum ExplicitHoldShortOutcome
{
    /// <summary>An existing hold-short protects the target: re-arm it (and revoke any clearance).</summary>
    ReArm,

    /// <summary>No hold-short protects the target yet: insert one at <see cref="ExplicitHoldShortPlan.NodeId"/>.</summary>
    Add,

    /// <summary>The target is already protected in a way that must not be disturbed (destination runway).</summary>
    NoOp,

    /// <summary>The aircraft is already on or past the target runway; the hold-short cannot be honoured.</summary>
    AlreadyEntered,

    /// <summary>The target appears nowhere on the remaining route.</summary>
    NotOnRoute,
}

/// <summary>The mutation <see cref="HoldShortAnnotator.PlanExplicitHoldShort"/> would perform.</summary>
internal readonly record struct ExplicitHoldShortPlan
{
    public required ExplicitHoldShortOutcome Outcome { get; init; }

    /// <summary>The hold-short to re-arm. Set only for <see cref="ExplicitHoldShortOutcome.ReArm"/>.</summary>
    public HoldShortPoint? Existing { get; init; }

    /// <summary>Node to hang a new hold-short on. Set only for <see cref="ExplicitHoldShortOutcome.Add"/>.</summary>
    public int NodeId { get; init; }

    /// <summary>Name for the new hold-short. Set only for <see cref="ExplicitHoldShortOutcome.Add"/>.</summary>
    public string? TargetName { get; init; }
}

/// <summary>
/// Post-processes a resolved taxi route to insert hold-short points at runway
/// crossings, explicit controller-specified holds, and destination runway holds.
/// </summary>
public static class HoldShortAnnotator
{
    private static readonly ILogger Log = SimLog.CreateLogger("HoldShortAnnotator");

    /// <summary>The airport and node ids <see cref="FirstLegLeadsTowardRunway"/> has already warned about.</summary>
    private static readonly ConcurrentDictionary<(string AirportId, int NodeId), byte> UnmeasuredStartBarWarned = new();

    /// <summary>
    /// True when the route leaves <paramref name="startNodeId"/> — a hold-short bar the aircraft is
    /// parked on — passes over <paramref name="startRwyId"/>, and reaches that runway's paired bar on
    /// the far side. In other words, the aircraft is standing at the entry side of a crossing it is
    /// about to make, so that bar is the one a hold-short binds to and the far bar is its exit pair.
    ///
    /// Requiring the runway to actually be traversed in between is what separates a real pair from an
    /// unrelated later crossing of the same runway (an aircraft that just vacated a runway onto a
    /// single-sided exit bar must not be paired with a bar it reaches minutes later).
    ///
    /// The scan deliberately does NOT stop at a taxiway change: when a crossing point doubles as a
    /// taxiway junction, its two bars carry different taxiway names (SFO 10R/28L — near bar on Foxtrot,
    /// far bar on Charlie). Stopping there was issue #316, where the far bar became the hold-short and
    /// the aircraft taxied over an occupied runway to reach it.
    /// </summary>
    internal static bool RouteCrossesRunwayAfterStart(
        AirportGroundLayout layout,
        IReadOnlyList<TaxiRouteSegment> segments,
        int startNodeId,
        RunwayIdentifier startRwyId
    )
    {
        bool traversedRunway = false;
        foreach (TaxiRouteSegment seg in segments)
        {
            if (seg.ToNodeId == startNodeId)
            {
                continue;
            }

            traversedRunway = traversedRunway || SegmentRunsAlongRunway(seg, startRwyId);

            if (!layout.Nodes.TryGetValue(seg.ToNodeId, out GroundNode? node))
            {
                continue;
            }

            if (node.Type == GroundNodeType.RunwayHoldShort && node.RunwayId is { } rwyId && rwyId.Equals(startRwyId))
            {
                return traversedRunway;
            }

            traversedRunway = traversedRunway || NodeLiesOnRunway(node, startRwyId);
        }

        return false;
    }

    /// <summary>True when this segment runs along <paramref name="runwayId"/>'s centerline.</summary>
    private static bool SegmentRunsAlongRunway(TaxiRouteSegment segment, RunwayIdentifier runwayId) =>
        segment.Edge.Edge.IsRunwayCenterline && (segment.Edge.Edge.MatchesRunway(runwayId.End1) || segment.Edge.Edge.MatchesRunway(runwayId.End2));

    /// <summary>
    /// True when this node sits on <paramref name="runwayId"/>'s centerline. A taxiway that crosses a
    /// runway is split at the centerline, so the crossing point is a node incident to a runway edge —
    /// which is how a one-point crossing is recognised, since no route segment runs along the runway.
    /// </summary>
    private static bool NodeLiesOnRunway(GroundNode node, RunwayIdentifier runwayId) =>
        node.Edges.Exists(edge => edge.IsRunwayCenterline && (edge.MatchesRunway(runwayId.End1) || edge.MatchesRunway(runwayId.End2)));

    /// <summary>
    /// Whether the aircraft has not yet passed <paramref name="bar"/>, the route's start node, along the route's first leg
    /// (from the bar toward <paramref name="firstLegNext"/>): the part of its body furthest along that leg (nose, tail or a
    /// wingtip) lies no more than <see cref="Simulation.RunwayOccupancy.HoldLineFoulToleranceFt"/> past the bar in that
    /// direction. An aircraft at the line, or still short of it, has the bar ahead; one already past it along the route has
    /// not, and neither has one facing the bar and cleared back the other way, whose tail is the part furthest along the route.
    /// This is geometry only: whether the bar holds is <see cref="StartBarHolds"/>, which never holds a route leading away from
    /// the runway, so an aircraft leaving the runway is not held at its start bar.
    /// </summary>
    /// <param name="bar">The runway hold-short node the route starts at.</param>
    /// <param name="firstLegNext">The node the route's first segment leads to from the bar.</param>
    /// <param name="pose">The aircraft's pose.</param>
    /// <returns>True when the bar is still ahead of every part of the aircraft.</returns>
    internal static bool StartBarIsAhead(GroundNode bar, GroundNode firstLegNext, StartPose pose) =>
        pose.PartPositions().All(part => StartBarIsAhead(bar, firstLegNext, part));

    /// <summary>
    /// Whether <paramref name="bar"/>, the route's start node, holds the aircraft — as a hold-short on a taxi route, and as a
    /// runway entry on a follower's way back: the route's first leg leads toward the bar's runway
    /// (<see cref="FirstLegLeadsTowardRunway"/>) and the aircraft has not yet passed the bar along it
    /// (<see cref="StartBarIsAhead(GroundNode, GroundNode, StartPose)"/>). The holding position marking stops traffic not
    /// cleared onto the runway; for traffic leaving the runway it marks the point where it is clear (AIM 2-3-5.a.1, 4-3-21.a),
    /// so a route leading away from the runway is never held there.
    /// </summary>
    /// <param name="layout">The airport's ground layout.</param>
    /// <param name="bar">The runway hold-short node the route starts at.</param>
    /// <param name="firstLegNext">The node the route's first segment leads to from the bar.</param>
    /// <param name="pose">The aircraft's pose.</param>
    /// <returns>True when the start bar is a hold for the route.</returns>
    internal static bool StartBarHolds(AirportGroundLayout layout, GroundNode bar, GroundNode firstLegNext, StartPose pose) =>
        FirstLegLeadsTowardRunway(layout, bar, firstLegNext) && StartBarIsAhead(bar, firstLegNext, pose);

    /// <summary>
    /// Whether the route's first leg, from <paramref name="bar"/> to <paramref name="firstLegNext"/>, leads toward the bar's
    /// runway: its far node lies no farther from the runway centreline than the bar. True when the layout has no runway of the
    /// bar's name to measure against, which keeps the bar a hold.
    /// </summary>
    /// <param name="layout">The airport's ground layout.</param>
    /// <param name="bar">The runway hold-short node the route starts at.</param>
    /// <param name="firstLegNext">The node the route's first segment leads to from the bar.</param>
    /// <returns>True when the first leg leads toward the runway.</returns>
    internal static bool FirstLegLeadsTowardRunway(AirportGroundLayout layout, GroundNode bar, GroundNode firstLegNext)
    {
        GroundRunway? runway = (bar.RunwayId is { } runwayId) ? layout.Runways.FirstOrDefault(r => r.Id.Overlaps(runwayId)) : null;
        if ((runway is null) || (runway.Coordinates.Count < 2))
        {
            if (UnmeasuredStartBarWarned.TryAdd((layout.AirportId, bar.Id), 0))
            {
                Log.LogWarning(
                    "[HoldShortAnnotator] Start bar {NodeId} has no ground runway {Runway} to measure its first leg against — holding there",
                    bar.Id,
                    bar.RunwayId
                );
            }

            return true;
        }

        var end1 = new LatLon(runway.Coordinates[0].Lat, runway.Coordinates[0].Lon);
        var end2 = new LatLon(runway.Coordinates[^1].Lat, runway.Coordinates[^1].Lon);
        return GeoMath.DistanceToSegmentFt(firstLegNext.Position, end1, end2) <= GeoMath.DistanceToSegmentFt(bar.Position, end1, end2);
    }

    /// <summary>
    /// Whether <paramref name="point"/> lies no more than <see cref="Simulation.RunwayOccupancy.HoldLineFoulToleranceFt"/> past
    /// <paramref name="bar"/> along the route's first leg (from the bar toward <paramref name="firstLegNext"/>).
    /// </summary>
    /// <param name="bar">The runway hold-short node the route starts at.</param>
    /// <param name="firstLegNext">The node the route's first segment leads to from the bar.</param>
    /// <param name="point">A point of the aircraft's body.</param>
    /// <returns>True when the bar is still ahead of the point.</returns>
    private static bool StartBarIsAhead(GroundNode bar, GroundNode firstLegNext, LatLon point)
    {
        double distanceFt = GeoMath.DistanceNm(bar.Position, point) * GeoMath.FeetPerNm;
        double legDeg = GeoMath.BearingTo(bar.Position, firstLegNext.Position);
        double angleRad = GeoMath.SignedBearingDifference(legDeg, GeoMath.BearingTo(bar.Position, point)) * Math.PI / 180.0;
        return distanceFt * Math.Cos(angleRad) <= Simulation.RunwayOccupancy.HoldLineFoulToleranceFt;
    }

    /// <summary>
    /// Scans the segment list for runway hold-short nodes and inserts implicit
    /// hold-short points at each runway crossing entry. Exit-side nodes are
    /// recognised by entry/exit pairing and skipped. A route that starts at a runway bar, leading toward its runway, that the
    /// aircraft has not yet passed along it (<see cref="StartBarHolds"/>, judged from <paramref name="startPose"/>) holds there too.
    /// </summary>
    /// <param name="layout">The airport's ground layout.</param>
    /// <param name="segments">The route's segments.</param>
    /// <param name="holdShorts">The route's hold-short points, added to.</param>
    /// <param name="startPose">
    /// The aircraft's pose when the route starts at the aircraft; null when it starts elsewhere (an extension past another
    /// route's end), where the start bar is never a hold.
    /// </param>
    internal static void AddImplicitRunwayHoldShorts(
        AirportGroundLayout layout,
        List<TaxiRouteSegment> segments,
        List<HoldShortPoint> holdShorts,
        StartPose? startPose
    )
    {
        // Entry/exit pairing by encounter order: the first HS node for a
        // runway is the entry side (add hold-short); the second distinct HS
        // node for that runway is the exit side (skip and reset tracking).
        // Revisiting the same node (backtrack) doesn't count as a new encounter.
        var enteredRunways = new Dictionary<RunwayIdentifier, int>();
        var seenHsNodes = new HashSet<(RunwayIdentifier, int)>();
        AnnotateStartBar(layout, segments, holdShorts, startPose, enteredRunways, seenHsNodes);
        AnnotateRouteBars(layout, segments, holdShorts, enteredRunways, seenHsNodes);
    }

    /// <summary>
    /// The start-node half of <see cref="AddImplicitRunwayHoldShorts"/>: pre-seeds the pairing when the route starts at a bar
    /// and goes on to cross its runway, and holds at that bar when the aircraft has not yet passed it along the route.
    /// </summary>
    private static void AnnotateStartBar(
        AirportGroundLayout layout,
        List<TaxiRouteSegment> segments,
        List<HoldShortPoint> holdShorts,
        StartPose? startPose,
        Dictionary<RunwayIdentifier, int> enteredRunways,
        HashSet<(RunwayIdentifier, int)> seenHsNodes
    )
    {
        // Pre-seed entry tracking from the starting node. If the route begins
        // at a RunwayHoldShort and the aircraft is mid-crossing (e.g., re-routed
        // from a destination hold-short), the next HS for the same runway is
        // the exit side of the crossing and must be skipped.
        //
        // BUT: a route can also begin at a RunwayHoldShort when the aircraft
        // has just vacated the runway via a single-sided exit taxiway (e.g.,
        // exited 28R onto H, where node 499 is the H/28R hold-short line).
        // In that case the aircraft is on the taxiway side of the line, NOT
        // mid-crossing. Pre-seeding there is wrong because it flips the next
        // encountered HS for the same runway from "entry" to "exit" — and the
        // next encountered HS may be at a totally different crossing (e.g.,
        // the B crossing of 28R, reached after taxiing H → C → B), not the
        // pair of the starting HS at all.
        //
        // RouteCrossesRunwayAfterStart separates the two.
        if (segments.Count > 0)
        {
            int startNodeId = segments[0].FromNodeId;
            if (
                layout.Nodes.TryGetValue(startNodeId, out GroundNode? startNode)
                && startNode.Type == GroundNodeType.RunwayHoldShort
                && startNode.RunwayId is { } startRwyId
            )
            {
                if (RouteCrossesRunwayAfterStart(layout, segments, startNodeId, startRwyId))
                {
                    enteredRunways[startRwyId] = startNodeId;
                    seenHsNodes.Add((startRwyId, startNodeId));
                    Log.LogDebug(
                        "[HoldShortAnnotator] Starting node {NodeId} is HS for {Runway} — pre-seeded as entry (paired crossing ahead)",
                        startNodeId,
                        startRwyId
                    );
                }
                else
                {
                    Log.LogDebug(
                        "[HoldShortAnnotator] Starting node {NodeId} is HS for {Runway} — NOT pre-seeding (exit-only, route never crosses it)",
                        startNodeId,
                        startRwyId
                    );
                }

                if (
                    (startPose is { } pose)
                    && layout.Nodes.TryGetValue(segments[0].ToNodeId, out GroundNode? firstLegNext)
                    && StartBarHolds(layout, startNode, firstLegNext, pose)
                    && !HoldShortExists(holdShorts, startNodeId)
                )
                {
                    seenHsNodes.Add((startRwyId, startNodeId));
                    holdShorts.Add(
                        new HoldShortPoint
                        {
                            NodeId = startNodeId,
                            Reason = HoldShortReason.RunwayCrossing,
                            TargetName = startRwyId.ToString(),
                        }
                    );
                    Log.LogDebug(
                        "[HoldShortAnnotator] Starting node {NodeId} is HS for {Runway}, still ahead of the aircraft — adding hold-short",
                        startNodeId,
                        startRwyId
                    );
                }
            }
        }
    }

    /// <summary>
    /// The segment half of <see cref="AddImplicitRunwayHoldShorts"/>: each bar a segment ends at, entry side held, exit side skipped.
    /// </summary>
    private static void AnnotateRouteBars(
        AirportGroundLayout layout,
        List<TaxiRouteSegment> segments,
        List<HoldShortPoint> holdShorts,
        Dictionary<RunwayIdentifier, int> enteredRunways,
        HashSet<(RunwayIdentifier, int)> seenHsNodes
    )
    {
        foreach (TaxiRouteSegment seg in segments)
        {
            if (
                !layout.Nodes.TryGetValue(seg.ToNodeId, out GroundNode? node)
                || node.Type != GroundNodeType.RunwayHoldShort
                || node.RunwayId is not { } rwyId
            )
            {
                continue;
            }

            // Skip if we've already processed this exact HS node for this runway
            if (!seenHsNodes.Add((rwyId, node.Id)))
            {
                Log.LogDebug("[HoldShortAnnotator] Skipping duplicate HS node {NodeId} for {Runway}", node.Id, rwyId);
                continue;
            }

            if (enteredRunways.Remove(rwyId))
            {
                // Exit-side HS: paired with the previous entry, skip
                Log.LogDebug("[HoldShortAnnotator] Exit-side HS node {NodeId} for {Runway} — paired with entry, skipping", node.Id, rwyId);
                continue;
            }

            // Entry-side: track for pairing and add hold-short
            enteredRunways[rwyId] = node.Id;
            Log.LogDebug("[HoldShortAnnotator] Entry-side HS node {NodeId} for {Runway} — adding hold-short", node.Id, rwyId);

            if (!HoldShortExists(holdShorts, node.Id))
            {
                holdShorts.Add(
                    new HoldShortPoint
                    {
                        NodeId = node.Id,
                        Reason = HoldShortReason.RunwayCrossing,
                        TargetName = rwyId.ToString(),
                    }
                );
            }
        }
    }

    /// <summary>
    /// Whether a hold-short target name matches a controller-supplied argument. Accepts both runway
    /// designators (parsed via <see cref="RunwayIdentifier"/>, so <c>28R</c> matches <c>28R/10L</c>)
    /// and taxiway/intersection names (case-insensitive equality, so <c>B</c> matches <c>B</c>).
    /// </summary>
    internal static bool TargetMatches(string? targetName, string arg)
    {
        if (targetName is null)
        {
            return false;
        }

        // A spot name keeps its $ sigil on both sides, so it only ever matches by literal equality —
        // never as runway "17" via the designator parse.
        if (HoldShortTarget.IsSpotTargetName(targetName) || HoldShortTarget.IsSpotTargetName(arg))
        {
            return string.Equals(targetName, arg, StringComparison.OrdinalIgnoreCase);
        }

        return RunwayIdentifier.Parse(targetName).Contains(arg) || string.Equals(targetName, arg, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Works out what <c>HS &lt;target&gt;</c> would do to <paramref name="route"/> without touching it,
    /// so a multi-target command can validate every target before mutating any. Apply the result with
    /// <see cref="ApplyExplicitHoldShort"/>. A located target (<c>C@J</c>) only considers points and
    /// nodes on the location taxiway, so the crossing the controller named binds even when an earlier
    /// crossing of the same target lies ahead on the route (issue #358).
    /// </summary>
    internal static ExplicitHoldShortPlan PlanExplicitHoldShort(AirportGroundLayout? layout, TaxiRoute route, HoldShortTarget target)
    {
        // The route's HoldShortPoints already carry exactly one entry per runway it crosses, on the
        // entry side — AddImplicitRunwayHoldShorts pairs the bars and drops the far side. So an
        // existing point is the authoritative near-side bar; never re-derive it from the segments.
        var candidates = route
            .HoldShortPoints.Where(h => TargetMatches(h.TargetName, target.MatchKey) && NodeOnLocationTaxiway(layout, h.NodeId, target.OnTaxiway))
            .ToList();
        if (candidates.Count > 0)
        {
            // A bar can only be behind the aircraft if it was cleared — the taxi gate physically
            // stops it otherwise. Testing IsCleared first is what makes this safe against
            // TaxiingPhase.BuildResumePhases, which bumps CurrentSegmentIndex past the bar the
            // aircraft is *stopped at*.
            var ahead = candidates.Where(h => !IsPassed(route, h)).OrderBy(h => SegmentIndexOf(route, h)).ToList();
            if (ahead.Count == 0)
            {
                return new ExplicitHoldShortPlan { Outcome = ExplicitHoldShortOutcome.AlreadyEntered };
            }

            HoldShortPoint nearest = ahead[0];

            // A destination-runway hold already stops the aircraft short of that runway, and its
            // reason gates the LUAW/CTO departure flow. Re-arming it as a plain ExplicitHoldShort
            // would strip that gate, so treat "HS <destination runway>" as a no-op.
            if (nearest.Reason == HoldShortReason.DestinationRunway)
            {
                return new ExplicitHoldShortPlan { Outcome = ExplicitHoldShortOutcome.NoOp };
            }

            return new ExplicitHoldShortPlan { Outcome = ExplicitHoldShortOutcome.ReArm, Existing = nearest };
        }

        if (layout is null)
        {
            return new ExplicitHoldShortPlan { Outcome = ExplicitHoldShortOutcome.NotOnRoute };
        }

        // No point for this target yet: walk the remaining route for the first node to hang one on.
        // At a given node a runway bar is tried before the node's taxiway edges. A located target
        // skips nodes off its location taxiway.
        for (int i = Math.Max(0, route.CurrentSegmentIndex); i < route.Segments.Count; i++)
        {
            if (!layout.Nodes.TryGetValue(route.Segments[i].ToNodeId, out GroundNode? node))
            {
                continue;
            }

            if (target.OnTaxiway is { } onTaxiway && !node.Edges.Any(e => e.MatchesTaxiway(onTaxiway)))
            {
                continue;
            }

            // A spot target binds the named Spot node itself (issue #394) — never a bar or a taxiway edge.
            if (target.IsSpot)
            {
                if (Pathfinding.RouteMaterialiser.IsSpotNode(node, target.Target))
                {
                    return new ExplicitHoldShortPlan
                    {
                        Outcome = ExplicitHoldShortOutcome.Add,
                        NodeId = node.Id,
                        TargetName = target.MatchKey,
                    };
                }

                continue;
            }

            if (node.Type == GroundNodeType.RunwayHoldShort && node.RunwayId is { } nodeRwyId && nodeRwyId.Contains(target.Target))
            {
                return new ExplicitHoldShortPlan
                {
                    Outcome = ExplicitHoldShortOutcome.Add,
                    NodeId = node.Id,
                    TargetName = nodeRwyId.ToString(),
                };
            }

            // A taxiway target never binds a bar where the route leaves a runway (KCOS A2's 35L bar at A): its hold falls through
            // to the next route node on the target taxiway, past the bar, so the tail can clear the runway's hold line (AIM 4-3-21.b).
            if (BindsPastLeavingBar(layout, route, i, target.Target))
            {
                continue;
            }

            foreach (IGroundEdge edge in node.Edges)
            {
                if (edge.MatchesTaxiway(target.Target))
                {
                    return new ExplicitHoldShortPlan
                    {
                        Outcome = ExplicitHoldShortOutcome.Add,
                        NodeId = node.Id,
                        TargetName = target.Target,
                    };
                }
            }
        }

        return new ExplicitHoldShortPlan { Outcome = ExplicitHoldShortOutcome.NotOnRoute };
    }

    /// <summary>
    /// Whether <paramref name="arriving"/> ends on a runway hold-short bar the route reaches from that runway's side
    /// (<see cref="LeavesRunwayAtBar"/>): a bar where the route leaves the runway, never a hold of its own runway, so a
    /// taxiway hold short binds past it (KCOS A2's 17R/35L bar, seated where A2's fillet onto A begins; AIM 4-3-21.b).
    /// </summary>
    public static bool IsBarLeavingRunway(AirportGroundLayout layout, TaxiRouteSegment arriving) =>
        (arriving.Edge.ToNode.Type == GroundNodeType.RunwayHoldShort) && LeavesRunwayAtBar(layout, arriving);

    /// <summary>
    /// Whether a hold short of <paramref name="taxiway"/> binds past the bar that <paramref name="route"/>'s segment
    /// <paramref name="barSegmentIndex"/> ends on, rather than on it: the segment ends on a bar where the route leaves its runway
    /// (<see cref="IsBarLeavingRunway"/>), a segment follows it, no runway crossing or destination-runway hold sits on the bar,
    /// the next segment ends neither on a runway hold-short bar nor on a runway centreline, and it is not a straight
    /// <see cref="GroundEdge"/> of <paramref name="taxiway"/> (a T with no fillet, whose bar sits on the taxiway itself).
    /// </summary>
    public static bool BindsPastLeavingBar(AirportGroundLayout layout, TaxiRoute route, int barSegmentIndex, string taxiway)
    {
        if ((barSegmentIndex < 0) || (barSegmentIndex + 1 >= route.Segments.Count))
        {
            return false;
        }

        TaxiRouteSegment arriving = route.Segments[barSegmentIndex];
        if (!IsBarLeavingRunway(layout, arriving))
        {
            return false;
        }

        if (
            route.HoldShortPoints.Any(h =>
                (h.NodeId == arriving.ToNodeId) && (h.Reason is HoldShortReason.RunwayCrossing or HoldShortReason.DestinationRunway)
            )
        )
        {
            return false;
        }

        TaxiRouteSegment next = route.Segments[barSegmentIndex + 1];
        GroundNode nextEnd = next.Edge.ToNode;
        if ((nextEnd.Type == GroundNodeType.RunwayHoldShort) || AirportGroundLayout.HasRunwayCenterlineEdge(nextEnd))
        {
            return false;
        }

        return !((next.Edge.Edge is GroundEdge) && next.Edge.Edge.MatchesTaxiway(taxiway));
    }

    /// <summary>
    /// Whether <paramref name="arriving"/> reaches its runway bar from the runway's side: its start node lies nearer that
    /// runway's centreline than the bar does. False when the bar's runway is not in the layout.
    /// </summary>
    private static bool LeavesRunwayAtBar(AirportGroundLayout layout, TaxiRouteSegment arriving)
    {
        GroundNode bar = arriving.Edge.ToNode;
        if ((bar.RunwayId is not { } runwayId) || (layout.Runways.FirstOrDefault(r => r.Id.Equals(runwayId)) is not { } runway))
        {
            return false;
        }

        return RunwayCentrelineFt(runway, arriving.Edge.FromNode.Position) < RunwayCentrelineFt(runway, bar.Position);
    }

    /// <summary>The distance (ft) from <paramref name="point"/> to <paramref name="runway"/>'s centreline polyline.</summary>
    private static double RunwayCentrelineFt(GroundRunway runway, LatLon point)
    {
        double best = double.PositiveInfinity;
        for (int i = 0; i + 1 < runway.Coordinates.Count; i++)
        {
            (double lat1, double lon1) = runway.Coordinates[i];
            (double lat2, double lon2) = runway.Coordinates[i + 1];
            best = Math.Min(best, GeoMath.DistanceToSegmentFt(point, new LatLon(lat1, lon1), new LatLon(lat2, lon2)));
        }

        return best;
    }

    /// <summary>
    /// True when the located constraint is satisfied: no location, or the node has an edge on the
    /// location taxiway. Without a layout the constraint cannot be checked and fails closed — a
    /// located target must never silently bind the wrong crossing. The one home for the incidence
    /// test: <c>HS C@J</c> binding a route point and <c>ATXI 28L@J</c> picking a bar ask the same
    /// question of a node.
    /// </summary>
    internal static bool NodeOnLocationTaxiway(AirportGroundLayout? layout, int nodeId, string? onTaxiway)
    {
        if (onTaxiway is null)
        {
            return true;
        }

        return layout is not null && layout.Nodes.TryGetValue(nodeId, out GroundNode? node) && node.Edges.Any(e => e.MatchesTaxiway(onTaxiway));
    }

    /// <summary>
    /// Commits a <see cref="PlanExplicitHoldShort"/> result. A re-arm revokes the existing clearance
    /// whatever set it — AutoCross, an earlier CROSS, or the implicit first-crossing clearance — because
    /// the hold-short is the controller's most recent instruction for that runway.
    /// </summary>
    internal static void ApplyExplicitHoldShort(TaxiRoute route, ExplicitHoldShortPlan plan, HoldShortTarget target)
    {
        switch (plan.Outcome)
        {
            case ExplicitHoldShortOutcome.ReArm when plan.Existing is { } existing:
                existing.Reason = HoldShortReason.ExplicitHoldShort;
                existing.IsCleared = false;
                existing.ClearedByAutoCross = false;
                Log.LogDebug("[HoldShortAnnotator] Explicit HS {Target}: re-armed hold-short at node {NodeId}", target, existing.NodeId);
                break;

            case ExplicitHoldShortOutcome.Add:
                route.HoldShortPoints.Add(
                    new HoldShortPoint
                    {
                        NodeId = plan.NodeId,
                        Reason = HoldShortReason.ExplicitHoldShort,
                        TargetName = plan.TargetName,
                    }
                );
                Log.LogDebug("[HoldShortAnnotator] Explicit HS {Target}: added hold-short at node {NodeId}", target, plan.NodeId);
                break;

            case ExplicitHoldShortOutcome.NoOp:
            case ExplicitHoldShortOutcome.AlreadyEntered:
            case ExplicitHoldShortOutcome.NotOnRoute:
            default:
                break;
        }
    }

    private static int SegmentIndexOf(TaxiRoute route, HoldShortPoint holdShort)
    {
        for (int i = 0; i < route.Segments.Count; i++)
        {
            if (route.Segments[i].ToNodeId == holdShort.NodeId)
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    private static bool IsPassed(TaxiRoute route, HoldShortPoint holdShort) =>
        holdShort.IsCleared && SegmentIndexOf(route, holdShort) < route.CurrentSegmentIndex;

    /// <summary>
    /// Appends a hold-short point at the last segment node, marking it as
    /// the destination runway hold position.
    /// </summary>
    internal static void AddDestinationHoldShort(List<TaxiRouteSegment> segments, List<HoldShortPoint> holdShorts, string runwayId)
    {
        if (segments.Count == 0)
        {
            return;
        }

        int lastNodeId = segments[^1].ToNodeId;

        // Remove any crossing hold-short at this node — the aircraft is taxiing TO
        // this runway, not crossing it. Without this, the same node gets both a
        // RunwayCrossing and DestinationRunway hold-short.
        holdShorts.RemoveAll(h => h.NodeId == lastNodeId && h.Reason == HoldShortReason.RunwayCrossing);

        holdShorts.Add(
            new HoldShortPoint
            {
                NodeId = lastNodeId,
                Reason = HoldShortReason.DestinationRunway,
                TargetName = runwayId,
            }
        );
    }

    /// <summary>
    /// Computes hold-short stop positions for all hold-short points in the route.
    /// Runway hold-shorts are offset back from the node by half the aircraft length so the
    /// aircraft's nose stops AT the hold-short line (the aircraft position is its center). A taxi
    /// spot (<c>HS $17</c>) is a painted point with nothing to clear beyond it, so it takes the same
    /// nose-at-the-mark setback — the taxiway setback would put a widebody back in the junction
    /// behind the spot. Taxiway hold-shorts are offset back from the intersection node along the
    /// approach edge by <paramref name="aircraftLengthFt"/> + buffer.
    ///
    /// <para>The taxiway setback is what AIM 2-3-5.b.3 asks of the pilot: told to hold short of a taxiway,
    /// "the pilot MUST STOP the aircraft at a point which provides adequate clearance from an aircraft on
    /// the intersecting taxiway" — a whole fuselage back from the junction is that point, since the aircraft's
    /// position is its centre. The 30 ft on top is a judgement call: no FAA document gives a figure for it.</para>
    ///
    /// <para>That setback is then held to a wingtip-clearance floor (<see cref="ApplyWingtipClearanceFloor"/>): the nose
    /// stays at least <see cref="WingtipClearanceFloorFt"/> from the crossed taxiway's centreline, so an aircraft taxiing
    /// on it clears the holder's nose.</para>
    ///
    /// <para>No taxiway stop is ever placed behind the aircraft (<paramref name="aircraftPosition"/>, measured along the route):
    /// a stop the setback would put there is placed where the aircraft stands instead.</para>
    /// </summary>
    /// <param name="layout">The airport's ground layout.</param>
    /// <param name="route">The route whose hold shorts are placed.</param>
    /// <param name="aircraftLengthFt">The aircraft's fuselage length (ft).</param>
    /// <param name="aircraftPosition">
    /// Where the aircraft stands on <paramref name="route"/>; null for a route it is not on yet (one stored behind a runway
    /// exit), which is measured from the start of its current segment.
    /// </param>
    /// <param name="nameTailRunway">
    /// Names the runway a tail-over warning is about; <see cref="CombinedName"/> names it by both ends.
    /// </param>
    public static void ComputeHoldShortPositions(
        AirportGroundLayout layout,
        TaxiRoute route,
        double aircraftLengthFt,
        LatLon? aircraftPosition,
        Func<RunwayIdentifier, string> nameTailRunway
    )
    {
        var path = RoutePolyline.Build(route);
        var placing = new PlacingRoute(layout, route, path, aircraftLengthFt, path.AircraftAlongFt(route.CurrentSegmentIndex, aircraftPosition));
        double runwayHalfLengthNm = (aircraftLengthFt / 2.0) / GeoMath.FeetPerNm;

        foreach (HoldShortPoint hs in route.HoldShortPoints)
        {
            // An unable bar's position is the stop the taxi phase moved it to, not the painted line: the
            // aircraft was already inside its braking distance when the bar was armed, so recomputing the
            // setback here would put the bar back somewhere nobody is going to stop.
            if (hs.Unable || !layout.Nodes.TryGetValue(hs.NodeId, out GroundNode? hsNode))
            {
                continue;
            }

            GroundNode stop = StopsWithTheNoseAtTheNode(hs, hsNode)
                ? VirtualNode.OffsetBefore(layout, route, hs.NodeId, runwayHalfLengthNm, HoldShortStopKind.Runway)
                : PlaceTaxiwayStop(placing, hs, nameTailRunway);
            hs.Latitude = stop.Position.Lat;
            hs.Longitude = stop.Position.Lon;
        }
    }

    /// <summary>
    /// Whether <paramref name="hs"/> stops with the nose at its node (half the aircraft length back): runway hold-shorts,
    /// destination holds, and spot targets. Keyed on the target, never on the node type alone: a taxiway bar can bind a
    /// spot-typed node painted at the junction (SFO spot 32 on B at T), and that bar still needs the taxiway setback.
    /// </summary>
    private static bool StopsWithTheNoseAtTheNode(HoldShortPoint hs, GroundNode hsNode) =>
        (hs.Reason is HoldShortReason.RunwayCrossing or HoldShortReason.DestinationRunway)
        || (hsNode.Type is GroundNodeType.RunwayHoldShort)
        || HoldShortTarget.IsSpotTargetName(hs.TargetName);

    /// <summary>
    /// A taxiway hold-short's stop: offset back from the intersection along the approach edge, the length + 30 ft setback.
    /// When a runway bar the route crossed lies within that setback, the stop is the nose-at-node one (½ length back),
    /// clamped at the bar, so the aircraft never reverses onto the runway it just crossed (issue #172 W1). A gap from that bar
    /// shorter than the fuselage (L) keeps that stop: the tail stays over the bar, so the overhung bar node is tagged and the
    /// controller warned at issuance (W2/W3). A gap of L or more, and any stop with no crossed bar, goes through the wingtip
    /// floor (<see cref="ApplyWingtipClearanceFloor"/>), whose walk-back stops with the tail
    /// <see cref="Phases.Ground.RunwayTailClearance.MarginFt"/> past the crossed bar; from L to L + 10 ft the nose may sit a
    /// few feet past the hold-short node (AIM 4-3-21.b). Either way the stop is never behind the aircraft
    /// (<see cref="NoFurtherBackThanTheAircraft"/>).
    /// </summary>
    private static GroundNode PlaceTaxiwayStop(PlacingRoute placing, HoldShortPoint hs, Func<RunwayIdentifier, string> nameTailRunway)
    {
        double taxiwayOffsetNm = (placing.LengthFt + TaxiwayHoldShortBufferFt) / GeoMath.FeetPerNm;
        (int RunwayNodeId, double GapNm)? crossedRunway = FindCrossedRunwayHoldShort(placing.Layout, placing.Route, hs.NodeId, taxiwayOffsetNm);
        bool justPastRunway = crossedRunway is not null;
        double twyOffsetNm = justPastRunway ? (placing.LengthFt / 2.0) / GeoMath.FeetPerNm : taxiwayOffsetNm;
        HoldShortStopKind stopKind = justPastRunway ? HoldShortStopKind.TaxiwayJustPastRunway : HoldShortStopKind.Taxiway;
        var placed = new PlacedStop(
            VirtualNode.OffsetBefore(placing.Layout, placing.Route, hs.NodeId, twyOffsetNm, stopKind),
            (crossedRunway is { } crossing ? Math.Min(twyOffsetNm, crossing.GapNm) : twyOffsetNm) * GeoMath.FeetPerNm
        );
        bool tailClearsRunway = (crossedRunway is not { } gap) || ((gap.GapNm * GeoMath.FeetPerNm) >= placing.LengthFt);
        if (tailClearsRunway)
        {
            placed = ApplyWingtipClearanceFloor(placing, hs, placed);
        }

        GroundNode twyVn = NoFurtherBackThanTheAircraft(placing, hs, placed);
        if (crossedRunway is { } cr)
        {
            AddTailOverRunwayWarning(placing, hs, cr, nameTailRunway);
        }

        double? pastCrossedBarFt = crossedRunway is { } bar ? (bar.GapNm * GeoMath.FeetPerNm) - placed.BackFt : null;
        Log.LogDebug(
            "[HoldShortAnnotator] Taxiway HS at node {NodeId} for {Target}: offset {OffsetFt:F0}ft ({Lat:F6}, {Lon:F6}) "
                + "justPastRunway={JustPast} tailClearsRunway={TailClears} centrePastCrossedBar={PastBarFt:F0}ft tailOverRunwayNode={TailOver}",
            hs.NodeId,
            hs.TargetName,
            twyOffsetNm * GeoMath.FeetPerNm,
            twyVn.Position.Lat,
            twyVn.Position.Lon,
            justPastRunway,
            tailClearsRunway,
            pastCrossedBarFt,
            hs.TailOverRunwayNodeId
        );
        return twyVn;
    }

    /// <summary>
    /// When the gap from the runway hold-short the route just crossed to <paramref name="hs"/>'s node is shorter than the
    /// fuselage, tags that runway node as overhung and warns that holding short leaves the tail over the runway, named by
    /// <paramref name="nameTailRunway"/>.
    /// </summary>
    private static void AddTailOverRunwayWarning(
        PlacingRoute placing,
        HoldShortPoint hs,
        (int RunwayNodeId, double GapNm) crossed,
        Func<RunwayIdentifier, string> nameTailRunway
    )
    {
        if ((crossed.GapNm * GeoMath.FeetPerNm) >= placing.LengthFt)
        {
            return;
        }

        hs.TailOverRunwayNodeId = crossed.RunwayNodeId;
        string rwy =
            placing.Layout.Nodes.TryGetValue(crossed.RunwayNodeId, out GroundNode? rwyNode) && rwyNode.RunwayId is { } rid
                ? nameTailRunway(rid)
                : "the runway";
        string warning =
            $"holding short of {HoldShortTarget.Describe(hs.TargetName ?? "")} leaves the tail over RWY {rwy} — unable to clear the runway";
        if (!placing.Route.Warnings.Contains(warning))
        {
            placing.Route.Warnings.Add(warning);
        }
    }

    /// <summary>
    /// <paramref name="stop"/>'s node, or the point on the route where the aircraft stands when the stop lies behind it (by
    /// more than <see cref="BehindTheAircraftToleranceFt"/>) while <paramref name="hs"/>'s node is still ahead: a hold short
    /// is never placed behind the aircraft, so one it has already passed the setback for holds it where it is. How far along
    /// the route the stop lies is carried from its placement (<see cref="PlacedStop.BackFt"/>), never found again by
    /// projecting the point onto the route, which a route that doubles back on itself passes twice. A stop the setback
    /// projected back past the route's start counts as at the start.
    /// </summary>
    private static GroundNode NoFurtherBackThanTheAircraft(PlacingRoute placing, HoldShortPoint hs, PlacedStop stop)
    {
        RoutePolyline path = placing.Path;
        int barIndex = path.IndexOfFirstArrival(hs.NodeId);
        if ((barIndex < 0) || (placing.AircraftAlongFt >= path.AlongFt(barIndex)))
        {
            return stop.Node;
        }

        double stopFt = Math.Max(0.0, path.AlongFt(barIndex) - stop.BackFt);
        if (stopFt >= placing.AircraftAlongFt - BehindTheAircraftToleranceFt)
        {
            return stop.Node;
        }

        Log.LogDebug(
            "[HoldShortAnnotator] Taxiway HS for {Target} at node {NodeId}: the stop {BehindFt:F0}ft behind the aircraft is placed where it stands",
            hs.TargetName,
            hs.NodeId,
            placing.AircraftAlongFt - stopFt
        );
        LatLon here = path.PositionAt(placing.AircraftAlongFt);
        return VirtualNode.Create(here.Lat, here.Lon);
    }

    /// <summary>How far (ft, along the route) behind the aircraft a placed stop may lie and still count as where it stands.</summary>
    private const double BehindTheAircraftToleranceFt = 1.0;

    /// <summary>
    /// A taxiway hold-short's stop and how far back from the bar's node (ft, along the route) it was placed: the setback the
    /// stop was offset by, or the bar's distance less the wingtip floor's centre.
    /// </summary>
    private readonly record struct PlacedStop(GroundNode Node, double BackFt);

    /// <summary>
    /// The route a hold-short placement works on: its layout, the route and its polyline, the aircraft's fuselage length
    /// (ft), and how far along the route (ft) the aircraft stands.
    /// </summary>
    private sealed record PlacingRoute(AirportGroundLayout Layout, TaxiRoute Route, RoutePolyline Path, double LengthFt, double AircraftAlongFt);

    /// <summary>A runway named by both ends, as a tail-over warning names one with no active end (<c>1R/19L</c>).</summary>
    public static string CombinedName(RunwayIdentifier runway) => runway.ToDisplayString();

    /// <summary>The margin (ft) a taxiway hold-short keeps beyond the aircraft's length. A judgement call: no FAA document gives a figure.</summary>
    public const double TaxiwayHoldShortBufferFt = 30.0;

    /// <summary>How far past the bar's node (ft, along the route) the search for the crossed centreline runs.</summary>
    private const double CentrelineMeetSearchAheadFt = 500.0;

    /// <summary>Only centreline edges of the crossed taxiway within this distance (ft) of the bar's node count.</summary>
    private const double CentrelineSearchRadiusFt = 2000.0;

    /// <summary>The step (ft) the nose walks back along the route while looking for the setback.</summary>
    private const double NoseWalkStepFt = 1.0;

    /// <summary>
    /// The nose-to-centreline clearance floor (ft) for a taxiway hold-short: half the largest wingspan the
    /// airport's Aircraft Design Group admits, plus 25 ft. The ADG is read from the widest runway at the airport,
    /// in the same width buckets as <see cref="RunwayCrossingDetector.HoldShortDistanceForWidth"/>, with the span
    /// ceilings of AC 150/5300-13B Table 1-2. It is a whole-airport worst case: the layout carries no taxiway width
    /// or design group, so every taxiway hold-short at the airport gets the floor of its largest aircraft. The
    /// 25 ft margin is a judgement call; no FAA document gives a figure. AIM 2-3-5.b.3 asks only that the pilot stop
    /// "at a point which provides adequate clearance from an aircraft on the intersecting taxiway".
    /// </summary>
    /// <param name="widestRunwayWidthFt">Width (ft) of the airport's widest runway.</param>
    /// <returns>The floor (ft) from the holder's nose to the crossed taxiway's centreline.</returns>
    public static double WingtipClearanceFloorFt(double widestRunwayWidthFt) =>
        (AirplaneDesignGroups.MaxWingspanFt(AirplaneDesignGroups.FromRunwayWidth(widestRunwayWidthFt)) / 2.0) + 25.0;

    /// <summary>
    /// Holds a taxiway hold-short's nose at least <c>max(L/2 + 30, floor)</c> from the crossed taxiway's centreline,
    /// measured perpendicular to the centreline edges (never to the junction node). The stop is found by walking the
    /// nose back along the route from where the route meets the centreline; the centre sits half a length behind it.
    /// No taxiway intersection behind caps the walk (<see cref="WalkBackLimitCentreFt"/>): it stops only where the aircraft
    /// stands or at a runway bar the route crossed, and those two limits bound the length setback too. Otherwise the stop is
    /// never forward of <paramref name="lengthSetbackStop"/>. When the clamp leaves the nose short of the wingtip floor
    /// itself, the route carries a wingtip-clearance warning. With no target, runway or centreline to measure against, the
    /// stop is <paramref name="lengthSetbackStop"/> held no further back than the walk-back limit (<see cref="HeldToWalkBackLimit"/>).
    /// </summary>
    /// <param name="placing">The route the stop is placed on.</param>
    /// <param name="hs">The taxiway hold short being placed.</param>
    /// <param name="lengthSetbackStop">The stop to fall back to.</param>
    /// <returns>
    /// <paramref name="lengthSetbackStop"/>, the stop farther back along the route that the floor asks for, or the stop
    /// farther on that the walk-back limit holds it to.
    /// </returns>
    private static PlacedStop ApplyWingtipClearanceFloor(PlacingRoute placing, HoldShortPoint hs, PlacedStop lengthSetbackStop)
    {
        (AirportGroundLayout layout, TaxiRoute route, RoutePolyline path, double lengthFt, _) = placing;
        int barIndex = path.IndexOfFirstArrival(hs.NodeId);
        if ((hs.TargetName is not { } target) || (layout.Runways.Count == 0) || (barIndex < 0))
        {
            return HeldToWalkBackLimit(placing, barIndex, lengthSetbackStop);
        }

        (List<(LatLon A, LatLon B)> centreline, string clearanceFrom) = FloorCentreline(
            layout,
            route,
            barIndex,
            target,
            path.Vertex(barIndex).Position
        );
        if (centreline.Count == 0)
        {
            RemovePlacementWarnings(route, target);
            string reason =
                clearanceFrom.Length == 0
                    ? $"it continues straight ahead and every other branch there lies within {StraightAheadToleranceDeg:F0}° of straight "
                        + "ahead or behind"
                    : $"no straight centreline of {clearanceFrom} within {CentrelineSearchRadiusFt:F0}ft";
            Log.LogDebug(
                "[HoldShortAnnotator] No centreline to clear for {Target} at bar node {NodeId} ({Reason}); keeping the length setback, held to the walk-back limit",
                target,
                hs.NodeId,
                reason
            );
            return HeldToWalkBackLimit(placing, barIndex, lengthSetbackStop);
        }

        double halfFt = lengthFt / 2.0;
        double floorFt = WingtipClearanceFloorFt(layout.Runways.Max(r => r.WidthFt));
        double setbackFt = Math.Max(halfFt + TaxiwayHoldShortBufferFt, floorFt);
        double barFt = path.AlongFt(barIndex);
        double lengthSetbackCentreFt = barFt - (lengthFt + TaxiwayHoldShortBufferFt);
        (double limitCentreFt, bool limitBoundsSetback) = WalkBackLimitCentreFt(placing, barIndex, halfFt);
        double meetFt = CentrelineMeetFt(path, centreline, barFt);
        double floorCentreFt = NoseWalkBackCentreFt(path, centreline, new NoseWalk(meetFt, setbackFt, halfFt, limitCentreFt));

        // The walk-back limit bounds the length setback too: a runway bar behind keeps the tail clear of the runway's hold
        // line, and the aircraft's own position keeps the stop from landing behind it. Either way the nose stops inside the
        // taxiway setback with the wingtip warning rather than leave the aircraft on the runway or send it backwards.
        double setbackCentreFt = Math.Min(floorCentreFt, lengthSetbackCentreFt);
        double centreFt = limitBoundsSetback ? Math.Max(limitCentreFt, setbackCentreFt) : setbackCentreFt;
        LatLon floorCentre = path.PositionAt(centreFt);
        PlacedStop stop =
            Math.Abs(centreFt - lengthSetbackCentreFt) > StopMatchToleranceFt
                ? new PlacedStop(VirtualNode.Create(floorCentre.Lat, floorCentre.Lon), barFt - centreFt)
                : lengthSetbackStop;
        double noseFt = DistanceToCentrelineFt(path.PositionAt(centreFt + halfFt), centreline);
        var placement = new FloorPlacement
        {
            Path = path,
            BarIndex = barIndex,
            Target = target,
            HalfFt = halfFt,
            FloorFt = floorFt,
        };
        ReplaceWingtipWarning(route, target, clearanceFrom, noseFt, floorFt);
        AddShallowMergeSetbackAdvisory(route, placement, lengthSetbackCentreFt, centreFt, noseFt);

        Log.LogDebug(
            "[HoldShortAnnotator] Wingtip floor for {Target} at node {NodeId} against {ClearanceFrom}: setback {SetbackFt:F0}ft "
                + "(floor {FloorFt:F0}), nose {NoseFt:F0}ft, walk-back limit {LimitFt:F0}ft, centre moved back {MovedFt:F0}ft",
            target,
            hs.NodeId,
            clearanceFrom,
            setbackFt,
            floorFt,
            noseFt,
            limitCentreFt,
            lengthSetbackCentreFt - centreFt
        );
        return stop;
    }

    /// <summary>
    /// Keeps at most one wingtip-clearance warning per crossed taxiway on the route, current with the last placement:
    /// any earlier one is dropped (its distance may be stale), and a new one is added when the nose stops short of the
    /// wingtip floor. The floor alone decides the warning — the <c>L/2 + 30</c> part of the setback is a placement
    /// margin, not wingtip clearance, so a nose that meets the floor is clear of a crosser's wingtip.
    /// </summary>
    private static void ReplaceWingtipWarning(TaxiRoute route, string target, string clearanceFrom, double noseFt, double floorFt)
    {
        RemovePlacementWarnings(route, target);
        if (noseFt < floorFt - 0.5)
        {
            route.Warnings.Add($"{WingtipWarningPrefix(target)}{clearanceFrom} not assured ({noseFt:F0} ft)");
        }
    }

    /// <summary>
    /// Drops the route's placement warnings for the bar on <paramref name="target"/>: the wingtip-clearance warning and the
    /// shallow-merge setback advisory, whichever branches each named.
    /// </summary>
    private static void RemovePlacementWarnings(TaxiRoute route, string target)
    {
        string wingtipPrefix = WingtipWarningPrefix(target);
        string setbackPrefix = SetbackAdvisoryPrefix(target);
        string mergeSuffix = MergesIntoSuffix(target);
        route.Warnings.RemoveAll(w =>
            w.StartsWith(wingtipPrefix, StringComparison.Ordinal)
            || (
                w.StartsWith(setbackPrefix, StringComparison.Ordinal)
                && w.Contains(SetbackAdvisoryMarker, StringComparison.Ordinal)
                && w.EndsWith(mergeSuffix, StringComparison.Ordinal)
            )
        );
    }

    /// <summary>"merges into X)" closes the shallow-merge setback advisory; the opening parenthesis names the arriving taxiway.</summary>
    private static string MergesIntoSuffix(string target) => $"merges into {target})";

    /// <summary>The setback advisory's opening — <c>holding short of TWY T </c> — after which the nose's distance back follows.</summary>
    private static string SetbackAdvisoryPrefix(string target) => $"holding short of TWY {target} ";

    /// <summary>Separates the setback advisory's distance from the arriving taxiway that follows it.</summary>
    private const string SetbackAdvisoryMarker = " ft back (";

    /// <summary>
    /// At a shallow merge with no intersection capping the walk-back, the stop stays where the wingtip floor against the
    /// target's own centreline is met, which can lie hundreds of feet back (the arriving taxiway diverges from the target at a
    /// few degrees). When that stop is further back than the plain <c>length + 30</c> setback and meets the floor, the route
    /// tells the instructor so, since nothing else would explain the early stop: <c>holding short of TWY W 203 ft back (W5
    /// merges into W)</c>, the nose's distance back of the merge node. A stop short of the floor carries the wingtip warning
    /// instead, and the readback stays plain.
    /// </summary>
    private static void AddShallowMergeSetbackAdvisory(
        TaxiRoute route,
        FloorPlacement p,
        double lengthSetbackCentreFt,
        double centreFt,
        double noseFt
    )
    {
        bool floorMet = noseFt >= p.FloorFt - 0.5;
        bool behindLengthSetback = centreFt < lengthSetbackCentreFt - 0.5;
        if (!floorMet || !behindLengthSetback || !IsShallowMerge(route, p.BarIndex, p.Target))
        {
            return;
        }

        string arriving = route.Segments[p.BarIndex - 1].TaxiwayName;
        double noseBackFt = p.Path.AlongFt(p.BarIndex) - (centreFt + p.HalfFt);
        route.Warnings.Add($"{SetbackAdvisoryPrefix(p.Target)}{noseBackFt:F0}{SetbackAdvisoryMarker}{arriving} {MergesIntoSuffix(p.Target)}");
    }

    private static string WingtipWarningPrefix(string target) => $"holding short of TWY {target} — wingtip clearance from ";

    /// <summary>The inputs of a wingtip-floor placement the shallow-merge setback advisory reads; every distance in feet.</summary>
    private readonly record struct FloorPlacement
    {
        public required RoutePolyline Path { get; init; }
        public required int BarIndex { get; init; }
        public required string Target { get; init; }
        public required double HalfFt { get; init; }
        public required double FloorFt { get; init; }
    }

    /// <summary>How far (ft) a branch's centreline is followed from the bar's node when the floor is held against it.</summary>
    private const double BranchWalkFt = 500.0;

    /// <summary>
    /// The centreline of one branch leaving <paramref name="node"/> by <paramref name="first"/>: that edge and the straight
    /// edges of the same taxiway that carry it on, for <see cref="BranchWalkFt"/>. Built from the branch's own edges rather
    /// than by name, so a branch named like the taxiway the aircraft arrives on never measures against the aircraft's own line.
    /// </summary>
    private static List<(LatLon A, LatLon B)> BranchCentreline(GroundEdge first, GroundNode node)
    {
        var pieces = new List<(LatLon A, LatLon B)>();
        GroundEdge? edge = first;
        GroundNode from = node;
        double walkedFt = 0.0;
        while ((edge is not null) && (walkedFt < BranchWalkFt))
        {
            var points = new List<LatLon> { edge.Nodes[0].Position };
            points.AddRange(edge.IntermediatePoints.Select(p => new LatLon(p.Lat, p.Lon)));
            points.Add(edge.Nodes[1].Position);
            for (int i = 0; i + 1 < points.Count; i++)
            {
                pieces.Add((points[i], points[i + 1]));
            }

            walkedFt += edge.DistanceNm * GeoMath.FeetPerNm;
            GroundEdge current = edge;
            from = current.OtherNode(from);
            edge = from.Edges.OfType<GroundEdge>().FirstOrDefault(e => !ReferenceEquals(e, current) && e.MatchesTaxiway(current.TaxiwayName));
        }

        return pieces;
    }

    /// <summary>A branch leaving the bar's node within this many degrees of the arriving heading continues straight ahead.</summary>
    private const double StraightAheadToleranceDeg = 20.0;

    /// <summary>
    /// The centreline the wingtip floor is measured against, and the taxiway name(s) a warning names. Normally the
    /// crossed taxiway's own. When the target continues the route's taxiway straight ahead at the bar's node, an aircraft
    /// on it is ahead, not crossing (AIM 2-3-5.b.3 speaks of "an aircraft on the intersecting taxiway"), so the floor is
    /// held against the node's other branches instead, those not straight ahead or straight behind; with none, the
    /// centreline is empty and the plain length setback stands. A shallow merge (<see cref="IsShallowMerge(TaxiRoute, int, string)"/>)
    /// keeps the target's own centreline: its leg behind runs alongside the arriving taxiway.
    /// </summary>
    private static (List<(LatLon A, LatLon B)> Centreline, string ClearanceFrom) FloorCentreline(
        AirportGroundLayout layout,
        TaxiRoute route,
        int barIndex,
        string target,
        LatLon anchor
    )
    {
        if (BranchesBesideStraightAhead(route, barIndex, target) is not { } branches)
        {
            return (CrossedCentreline(layout, target, anchor), target);
        }

        GroundNode node = route.Segments[barIndex - 1].Edge.ToNode;
        var centreline = new List<(LatLon A, LatLon B)>();
        foreach (GroundEdge branch in branches)
        {
            centreline.AddRange(BranchCentreline(branch, node));
        }

        string names = string.Join(
            "/",
            branches.Select(b => b.TaxiwayName).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
        );
        return (centreline, names);
    }

    /// <summary>
    /// A target-named edge straight behind that fans off the arriving line by less than this (deg) is the same line drawn
    /// under two names (FLL A/A1, MIA Q2/M2, IAH WA), not a merge.
    /// </summary>
    private const double CoincidentFanDeg = 1.0;

    /// <summary>
    /// When <paramref name="target"/> leaves the bar's node straight ahead (within <see cref="StraightAheadToleranceDeg"/>
    /// of the arriving heading), the node's other straight, non-ramp taxiway edges that are neither straight
    /// ahead nor straight behind; null when the target does not continue straight ahead there, or when the node is a
    /// shallow merge (<see cref="IsShallowMerge(TaxiRoute, int, string)"/>), whose floor is held against the target's own centreline.
    /// </summary>
    private static List<GroundEdge>? BranchesBesideStraightAhead(TaxiRoute route, int barIndex, string target)
    {
        if ((ArrivingBranches(route, barIndex) is not { } branches) || !ContinuesStraightAhead(branches, target) || IsShallowMerge(branches, target))
        {
            return null;
        }

        return [.. branches.Where(b => (b.OffDeg > StraightAheadToleranceDeg) && (b.OffDeg < 180.0 - StraightAheadToleranceDeg)).Select(b => b.Edge)];
    }

    /// <summary>
    /// True when the route arrives at the bar's node on a taxiway that merges into <paramref name="target"/> at a shallow
    /// angle: the target continues straight ahead, and another target edge lies within <see cref="StraightAheadToleranceDeg"/>
    /// of straight behind but off the arriving line by at least <see cref="CoincidentFanDeg"/> (OAK D into C at 5.7°).
    /// An aircraft on the target's leg behind then passes alongside the holder's nose.
    /// </summary>
    private static bool IsShallowMerge(TaxiRoute route, int barIndex, string target) =>
        (ArrivingBranches(route, barIndex) is { } branches) && ContinuesStraightAhead(branches, target) && IsShallowMerge(branches, target);

    private static bool IsShallowMerge(List<(GroundEdge Edge, double OffDeg)> branches, string target) =>
        branches.Any(b => b.Edge.MatchesTaxiway(target) && (b.OffDeg >= 180.0 - StraightAheadToleranceDeg) && (b.OffDeg <= 180.0 - CoincidentFanDeg));

    private static bool ContinuesStraightAhead(List<(GroundEdge Edge, double OffDeg)> branches, string target) =>
        branches.Any(b => b.Edge.MatchesTaxiway(target) && (b.OffDeg <= StraightAheadToleranceDeg));

    /// <summary>
    /// The bar's node's straight, non-ramp, non-runway edges with each one's angle off the arriving heading; null when the
    /// bar is not reached by a route segment.
    /// </summary>
    private static List<(GroundEdge Edge, double OffDeg)>? ArrivingBranches(TaxiRoute route, int barIndex)
    {
        if ((barIndex < 1) || (barIndex > route.Segments.Count))
        {
            return null;
        }

        DirectionalEdge arriving = route.Segments[barIndex - 1].Edge;
        GroundNode node = arriving.ToNode;
        double arrivingDeg = arriving.ArrivalBearing;
        return
        [
            .. node
                .Edges.OfType<GroundEdge>()
                .Where(e => !e.IsRamp && !e.IsRunwayCenterline)
                .Select(e => (Edge: e, OffDeg: GeoMath.AbsBearingDifference(arrivingDeg, LeavingBearingDeg(e, node)))),
        ];
    }

    /// <summary>The bearing a straight edge leaves <paramref name="node"/> on: toward its first shape point, else its far node.</summary>
    private static double LeavingBearingDeg(GroundEdge edge, GroundNode node)
    {
        bool fromStart = edge.Nodes[0].Id == node.Id;
        LatLon next =
            edge.IntermediatePoints.Count == 0 ? edge.OtherNode(node).Position
            : fromStart ? new LatLon(edge.IntermediatePoints[0].Lat, edge.IntermediatePoints[0].Lon)
            : new LatLon(edge.IntermediatePoints[^1].Lat, edge.IntermediatePoints[^1].Lon);
        return GeoMath.BearingTo(node.Position, next);
    }

    /// <summary>How far (ft) the placed centre may sit from the length setback's and still stop at that setback's own node.</summary>
    private const double StopMatchToleranceFt = 0.01;

    /// <summary>The nose walk-back's inputs, all along-route distances or setbacks in feet.</summary>
    private readonly record struct NoseWalk(double MeetFt, double SetbackFt, double HalfFt, double LimitCentreFt);

    /// <summary>The straight centreline pieces of <paramref name="taxiway"/> within <see cref="CentrelineSearchRadiusFt"/> of <paramref name="anchor"/>.</summary>
    private static List<(LatLon A, LatLon B)> CrossedCentreline(AirportGroundLayout layout, string taxiway, LatLon anchor) =>
        CentrelinePieces(layout.Edges.Where(e => string.Equals(e.TaxiwayName, taxiway, StringComparison.OrdinalIgnoreCase)), anchor);

    /// <summary>
    /// How far (ft) <paramref name="point"/> is from the centreline of <paramref name="edges"/>, measured the way a taxiway
    /// hold-short's setback is: perpendicular to each straight piece between an edge's nodes and shape points, counting only
    /// the pieces within <see cref="CentrelineSearchRadiusFt"/> of <paramref name="anchor"/>. Infinity when none is.
    /// </summary>
    public static double DistanceToCentrelineFt(LatLon point, IEnumerable<GroundEdge> edges, LatLon anchor) =>
        DistanceToCentrelineFt(point, CentrelinePieces(edges, anchor));

    /// <summary>The straight pieces of <paramref name="edges"/> within <see cref="CentrelineSearchRadiusFt"/> of <paramref name="anchor"/>.</summary>
    private static List<(LatLon A, LatLon B)> CentrelinePieces(IEnumerable<GroundEdge> edges, LatLon anchor)
    {
        var pieces = new List<(LatLon A, LatLon B)>();
        foreach (GroundEdge edge in edges)
        {
            var points = new List<LatLon> { edge.Nodes[0].Position };
            points.AddRange(edge.IntermediatePoints.Select(p => new LatLon(p.Lat, p.Lon)));
            points.Add(edge.Nodes[1].Position);
            for (int i = 0; i + 1 < points.Count; i++)
            {
                if (GeoMath.DistanceToSegmentFt(anchor, points[i], points[i + 1]) <= CentrelineSearchRadiusFt)
                {
                    pieces.Add((points[i], points[i + 1]));
                }
            }
        }

        return pieces;
    }

    private static double DistanceToCentrelineFt(LatLon point, List<(LatLon A, LatLon B)> centreline)
    {
        double best = double.PositiveInfinity;
        foreach ((LatLon a, LatLon b) in centreline)
        {
            best = Math.Min(best, GeoMath.DistanceToSegmentFt(point, a, b));
        }

        return best;
    }

    /// <summary>
    /// Where (ft along the route) the route meets the crossed centreline: the closest approach within
    /// <see cref="CentrelineMeetSearchAheadFt"/> past the bar's node, which is the crossing itself when the route
    /// carries on over the taxiway.
    /// </summary>
    private static double CentrelineMeetFt(RoutePolyline path, List<(LatLon A, LatLon B)> centreline, double barFt)
    {
        double endFt = Math.Min(path.LengthFt, barFt + CentrelineMeetSearchAheadFt);
        double bestFt = barFt;
        double bestDistFt = double.PositiveInfinity;
        for (double alongFt = barFt; alongFt <= endFt; alongFt += NoseWalkStepFt)
        {
            double distFt = DistanceToCentrelineFt(path.PositionAt(alongFt), centreline);
            if (distFt < bestDistFt)
            {
                bestDistFt = distFt;
                bestFt = alongFt;
            }

            if (distFt < NoseWalkStepFt)
            {
                break;
            }
        }

        return bestFt;
    }

    /// <summary>
    /// Walks the nose back from the meeting point until it is <c>SetbackFt</c> from the centreline and returns the
    /// centre (half a length behind the nose), or <c>LimitCentreFt</c> when the walk reaches the limit first.
    /// </summary>
    private static double NoseWalkBackCentreFt(RoutePolyline path, List<(LatLon A, LatLon B)> centreline, NoseWalk walk)
    {
        for (double noseFt = walk.MeetFt; (noseFt - walk.HalfFt) > walk.LimitCentreFt; noseFt -= NoseWalkStepFt)
        {
            if (DistanceToCentrelineFt(path.PositionAt(noseFt), centreline) >= walk.SetbackFt)
            {
                return noseFt - walk.HalfFt;
            }
        }

        return walk.LimitCentreFt;
    }

    /// <summary>
    /// How far back along the route (ft, the centre's position) the wingtip-floor walk-back may go: a taxiway hold short has
    /// no intersection backstop, so the stop goes back past any taxiway junction behind it. It never goes behind where the
    /// aircraft stands along the route (<see cref="PlacingRoute.AircraftAlongFt"/>, at most the bar's own node), nor back
    /// over a runway hold-short bar the route crossed: the tail stays clear of that bar by the crossing rule's margin
    /// (<see cref="Phases.Ground.RunwayTailClearance.MarginFt"/>), never back over the hold line (AIM 4-3-21.b). When
    /// the aircraft already stands past where that margin puts the centre, the limit is where it stands.
    /// <c>BoundsSetback</c> is true when the limit also bounds the length setback: a runway bar set it, or the aircraft did
    /// while the bar's node still lies ahead of it (an aircraft at or past the node has no stop ahead to hold back).
    /// </summary>
    private static (double LimitCentreFt, bool BoundsSetback) WalkBackLimitCentreFt(PlacingRoute placing, int barIndex, double halfFt)
    {
        RoutePolyline path = placing.Path;
        double barFt = path.AlongFt(barIndex);
        bool barAhead = placing.AircraftAlongFt < barFt;
        double aircraftFt = Math.Min(placing.AircraftAlongFt, barFt);
        for (int i = barIndex - 1; i >= 0; i--)
        {
            if (placing.Layout.Nodes.TryGetValue(path.Vertex(i).Id, out GroundNode? node) && (node.Type == GroundNodeType.RunwayHoldShort))
            {
                double tailClearFt = path.AlongFt(i) + halfFt + Phases.Ground.RunwayTailClearance.MarginFt;
                return (tailClearFt > aircraftFt) ? (tailClearFt, true) : (aircraftFt, barAhead);
            }
        }

        return (aircraftFt, barAhead);
    }

    /// <summary>
    /// <paramref name="fallback"/>, or the stop at the walk-back limit when that limit bounds the setback and lies farther on
    /// (<see cref="WalkBackLimitCentreFt"/>): the wingtip floor's limit-bound placement for a stop it has no centreline to
    /// measure against, so a stop past a crossed runway bar still keeps the tail
    /// <see cref="Phases.Ground.RunwayTailClearance.MarginFt"/> past that bar (AIM 4-3-21.b).
    /// </summary>
    private static PlacedStop HeldToWalkBackLimit(PlacingRoute placing, int barIndex, PlacedStop fallback)
    {
        if (barIndex < 0)
        {
            return fallback;
        }

        RoutePolyline path = placing.Path;
        double barFt = path.AlongFt(barIndex);
        (double limitCentreFt, bool limitBoundsSetback) = WalkBackLimitCentreFt(placing, barIndex, placing.LengthFt / 2.0);
        if (!limitBoundsSetback || (limitCentreFt <= barFt - fallback.BackFt + StopMatchToleranceFt))
        {
            return fallback;
        }

        LatLon centre = path.PositionAt(limitCentreFt);
        return new PlacedStop(VirtualNode.Create(centre.Lat, centre.Lon), barFt - limitCentreFt);
    }

    /// <summary>A taxi route as a polyline of node positions, with the along-route distance (ft) of each node.</summary>
    private sealed class RoutePolyline
    {
        private readonly List<GroundNode> _vertices = [];
        private readonly List<double> _alongFt = [];

        public double LengthFt => _alongFt.Count == 0 ? 0.0 : _alongFt[^1];

        public static RoutePolyline Build(TaxiRoute route)
        {
            var path = new RoutePolyline();
            foreach (TaxiRouteSegment seg in route.Segments)
            {
                if (path._vertices.Count == 0)
                {
                    path._vertices.Add(seg.Edge.FromNode);
                    path._alongFt.Add(0.0);
                }

                double legFt = GeoMath.DistanceNm(path._vertices[^1].Position, seg.Edge.ToNode.Position) * GeoMath.FeetPerNm;
                path._vertices.Add(seg.Edge.ToNode);
                path._alongFt.Add(path._alongFt[^1] + legFt);
            }

            return path;
        }

        public GroundNode Vertex(int index) => _vertices[index];

        public double AlongFt(int index) => _alongFt[index];

        /// <summary>
        /// How far along the route (ft) the aircraft at <paramref name="position"/> stands: its projection onto the segment at
        /// <paramref name="currentSegmentIndex"/>, clamped to that segment's two ends; the segment's start when the position is
        /// unknown. The route's end once every segment is flown, and zero on an empty polyline.
        /// </summary>
        public double AircraftAlongFt(int currentSegmentIndex, LatLon? position)
        {
            if (_vertices.Count < 2)
            {
                return 0.0;
            }

            if (currentSegmentIndex >= _vertices.Count - 1)
            {
                return _alongFt[^1];
            }

            int leg = Math.Max(0, currentSegmentIndex) + 1;
            return position is { } at ? OntoLeg(leg, at).AlongFt : _alongFt[leg - 1];
        }

        /// <summary>
        /// <paramref name="point"/> projected onto the leg ending at vertex <paramref name="leg"/>, clamped to the leg's ends:
        /// the foot's distance along the route (ft) and the point's distance from the foot (ft).
        /// </summary>
        private (double AlongFt, double OffFt) OntoLeg(int leg, LatLon point)
        {
            LatLon from = _vertices[leg - 1].Position;
            double legBearing = GeoMath.BearingTo(from, _vertices[leg].Position);
            double toPointFt = GeoMath.DistanceNm(from, point) * GeoMath.FeetPerNm;
            double offAngleRad = GeoMath.AbsBearingDifference(legBearing, GeoMath.BearingTo(from, point)) * Math.PI / 180.0;
            double intoLegFt = Math.Clamp(toPointFt * Math.Cos(offAngleRad), 0.0, _alongFt[leg] - _alongFt[leg - 1]);
            (double lat, double lon) = GeoMath.ProjectPointRaw(from.Lat, from.Lon, legBearing, intoLegFt / GeoMath.FeetPerNm);
            double offFt = GeoMath.DistanceNm(new LatLon(lat, lon), point) * GeoMath.FeetPerNm;
            return (_alongFt[leg - 1] + intoLegFt, offFt);
        }

        /// <summary>The first vertex after the start that is <paramref name="nodeId"/>, as <see cref="VirtualNode.OffsetBefore"/> finds it; -1 when none is.</summary>
        public int IndexOfFirstArrival(int nodeId)
        {
            for (int i = 1; i < _vertices.Count; i++)
            {
                if (_vertices[i].Id == nodeId)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>The point <paramref name="alongFt"/> along the route, clamped to the route's two ends.</summary>
        public LatLon PositionAt(double alongFt)
        {
            if (_vertices.Count == 0)
            {
                throw new InvalidOperationException("PositionAt on an empty route polyline");
            }

            for (int i = 1; i < _vertices.Count; i++)
            {
                if (alongFt <= _alongFt[i])
                {
                    double intoLegFt = Math.Max(0.0, alongFt - _alongFt[i - 1]);
                    double bearing = GeoMath.BearingTo(_vertices[i - 1].Position, _vertices[i].Position);
                    (double lat, double lon) = GeoMath.ProjectPointRaw(
                        _vertices[i - 1].Position.Lat,
                        _vertices[i - 1].Position.Lon,
                        bearing,
                        intoLegFt / GeoMath.FeetPerNm
                    );
                    return new LatLon(lat, lon);
                }
            }

            return _vertices[^1].Position;
        }
    }

    /// <summary>
    /// Walks the route backward from <paramref name="nodeId"/> up to <paramref name="withinNm"/> and
    /// returns the first <see cref="GroundNodeType.RunwayHoldShort"/> node encountered (the runway the
    /// route just crossed) with the along-route gap to it, or null if none lies within range. Used to
    /// cap a taxiway hold-short's setback so it never lands behind the runway, and to detect the
    /// tail-over-runway state when the gap is shorter than a fuselage.
    /// </summary>
    private static (int RunwayNodeId, double GapNm)? FindCrossedRunwayHoldShort(
        AirportGroundLayout layout,
        TaxiRoute route,
        int nodeId,
        double withinNm
    )
    {
        double accumulated = 0;
        int currentId = nodeId;
        for (int guard = 0; guard <= route.Segments.Count; guard++)
        {
            int approachId = -1;
            foreach (TaxiRouteSegment seg in route.Segments)
            {
                if (seg.ToNodeId == currentId)
                {
                    approachId = seg.FromNodeId;
                    break;
                }
            }

            if (
                approachId < 0
                || !layout.Nodes.TryGetValue(approachId, out GroundNode? approachNode)
                || !layout.Nodes.TryGetValue(currentId, out GroundNode? curNode)
            )
            {
                break;
            }

            accumulated += GeoMath.DistanceNm(curNode.Position, approachNode.Position);
            if (accumulated > withinNm)
            {
                break;
            }

            if (approachNode.Type == GroundNodeType.RunwayHoldShort)
            {
                return (approachId, accumulated);
            }

            currentId = approachId;
        }

        return null;
    }

    internal static bool HoldShortExists(List<HoldShortPoint> holdShorts, int nodeId)
    {
        foreach (HoldShortPoint hs in holdShorts)
        {
            if (hs.NodeId == nodeId)
            {
                return true;
            }
        }

        return false;
    }
}
