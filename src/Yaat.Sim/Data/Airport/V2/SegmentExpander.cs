using System.Collections.Immutable;

namespace Yaat.Sim.Data.Airport.V2;

/// <summary>
/// Explicit-mode driver for the v2 pathfinder. Walks <see cref="SearchContext.WaypointSequence"/>
/// segment by segment, resolves each taxiway-to-taxiway junction via a bounded local best-first
/// search, handles variant resolution at the final segment, and appends a parking/spot extension
/// via AutoRouter when needed.
/// </summary>
public static class SegmentExpander
{
    /// <summary>
    /// Maximum nodes expanded in a single per-segment local search.
    /// Taxiways have fewer than 200 nodes at any real airport; this is a safety ceiling.
    /// </summary>
    private const int MaxLocalExpansions = 500;

    /// <summary>
    /// Maximum nodes expanded in the local auto-route detour (numbered + RAMP bridging).
    /// </summary>
    private const int MaxDetourExpansions = 5_000;

    /// <summary>
    /// Maximum BFS hops when bridging a start node (e.g. a parking/RAMP spot) onto the
    /// first named taxiway. Matches v1's <c>BfsToTaxiway</c> bound — parking spots connect
    /// to their taxiway within one or two RAMP/connector hops.
    /// </summary>
    private const int MaxBridgeHops = 3;

    /// <summary>
    /// Run the segment expander on the given <paramref name="ctx"/>.
    /// Returns either a materialised <see cref="TaxiRoute"/> or a structured
    /// <see cref="PathfindingFailure"/>. Exactly one of the two return values is non-null.
    /// </summary>
    public static (TaxiRoute? Route, PathfindingFailure? Failure) Run(SearchContext ctx)
    {
        if (ctx.WaypointSequence.Count == 0)
        {
            return (
                null,
                new PathfindingFailure(FailureKind.TaxiwayNotConnected, "SegmentExpander requires a non-empty waypoint sequence.", null, null, null)
            );
        }

        if (!ctx.Layout.Nodes.TryGetValue(ctx.StartNodeId, out _))
        {
            return (
                null,
                new PathfindingFailure(FailureKind.StartNodeUnreachable, $"Start node {ctx.StartNodeId} not found in layout.", null, null, null)
            );
        }

        // Resolve node-reference tokens (#NNNN) in the waypoint sequence.
        var resolvedWaypoints = ResolveWaypoints(ctx);

        // Reject a clearance naming a taxiway that is absent from the layout. Otherwise the
        // per-segment walk finds no matching edge, silently yields an empty/partial route, and the
        // command would succeed against a route that goes nowhere. (Node-ref tokens are validated
        // when routed.) Mirrors v1's "Cannot reach taxiway X" rejection.
        foreach (var wp in resolvedWaypoints)
        {
            if (!wp.IsNodeRef && (ctx.Layout.GetNodesOnTaxiway(wp.Name).Count == 0))
            {
                return (
                    null,
                    new PathfindingFailure(FailureKind.TaxiwayNotConnected, $"Cannot find taxiway {wp.Name} in layout.", wp.Name, null, null)
                );
            }
        }

        var edges = new List<DirectionalEdge>();
        var head = PartialRoute.StartAt(ctx.StartNodeId);

        // Bridge onto the first named taxiway when the start node is off it (e.g. parked
        // on a RAMP spot whose only edges are RAMP). Mirrors v1's BfsToTaxiway. Without
        // this the first per-segment local search finds no on-taxiway edge from the start
        // and the route degrades to a long, often-failing detour. The bridge is
        // direction-aware: when several taxiway-access nodes are reachable, it picks the one
        // whose admissible on-taxiway continuation heads toward where the route must go (the
        // junction with the next cleared taxiway, or the destination). A direction-blind
        // pick can enter the taxiway through a corner arc that commits the head to the wrong
        // branch, after which the correct branch fails the U-turn admissibility check.
        if (!resolvedWaypoints[0].IsNodeRef)
        {
            var bridgeBias = ResolveBridgeBias(resolvedWaypoints, head, ctx);
            var (bridgeEdges, bridgeHead) = BridgeStartToTaxiway(head, resolvedWaypoints[0].Name, bridgeBias, ctx);
            if (bridgeEdges.Count > 0)
            {
                edges.AddRange(bridgeEdges);
                head = bridgeHead with { VisitedNodeIds = ImmutableHashSet<int>.Empty.Add(bridgeHead.HeadNodeId) };
            }
        }

        // Walk the waypoint sequence segment by segment, with recursive look-ahead at each
        // taxiway-to-taxiway junction (see ResolveSequence). Look-ahead defeats first-match
        // junction picks that would strand the route on the wrong leg of a V-shaped taxiway.
        // Mandatory-connector insertions (two cleared taxiways with no direct junction) are
        // collected so the materialiser can notify the controller instead of warning.
        var insertions = new List<ConnectorInsertion>();
        var (seqEdges, seqHead, seqFailure) = ResolveSequence(head, resolvedWaypoints, 0, enableLookahead: true, insertions, ctx);
        if (seqFailure is not null)
        {
            return (null, seqFailure);
        }

        edges.AddRange(seqEdges!);
        head = seqHead!;

        // Variant resolution: if destination is a runway and the last named taxiway has
        // numbered variants that reach it, extend automatically. Skipped when the walked
        // route already reaches a hold-short for the destination runway — the named taxiway
        // serves the runway directly (e.g. B1 has its own 10L hold-short), so the materialiser
        // truncates there and a variant extension would only back-track to it.
        if (
            ctx.Destination.Kind == DestinationKind.Runway
            && ctx.Destination.RunwayId is { } destRunway
            && !RouteReachesRunwayHoldShort(edges, destRunway, ctx)
        )
        {
            var lastWaypoint = resolvedWaypoints[^1];
            if (!lastWaypoint.IsNodeRef)
            {
                var (varEdges, varFailure) = TryVariantExtension(head, lastWaypoint.Name, destRunway, ctx);
                if (varFailure is not null)
                {
                    return (null, varFailure);
                }

                if (varEdges is not null)
                {
                    edges.AddRange(varEdges);
                }
            }
        }

        // Parking/spot extension: after the named taxiway walk, auto-route to destination.
        if (ctx.Destination.Kind is DestinationKind.Parking or DestinationKind.Spot or DestinationKind.Helipad)
        {
            if (ctx.Destination.TargetNodeId is { } destId && head.HeadNodeId != destId)
            {
                var (extEdges, extFailure) = ExtendToDestination(head, destId, ctx);
                if (extFailure is not null)
                {
                    return (null, extFailure);
                }

                if (extEdges is not null)
                {
                    edges.AddRange(extEdges);
                }
            }
        }

        var route = RouteMaterialiser.Materialise(edges, ctx, insertions);

        // Honor the clearance: every named taxiway the controller specified must be traversed by the
        // resolved route. If one is wholly absent, the aircraft could not reach it from its start
        // without leaving the movement area (e.g. a gate from which taxiway A lies across active
        // runways), so the resolver bypassed it via a connector toward a later named taxiway. Taxiing
        // somewhere the controller never cleared is worse than rejecting — fail the command. This is
        // distinct from the soft mandatory-connector policy, which inserts a connector BETWEEN named
        // taxiways while keeping every named taxiway present; the check below only fires when a named
        // taxiway appears nowhere in the route. (Node-ref tokens are validated when routed.)
        foreach (var wp in resolvedWaypoints)
        {
            if (wp.IsNodeRef || edges.Any(e => e.Edge.MatchesTaxiway(wp.Name)))
            {
                continue;
            }

            return (
                null,
                new PathfindingFailure(
                    FailureKind.TaxiwayNotConnected,
                    $"Cannot taxi via {wp.Name} from the aircraft's position — it is unreachable without crossing a runway or leaving the movement area.",
                    wp.Name,
                    null,
                    null
                )
            );
        }

        return (route, null);
    }

    // -----------------------------------------------------------------------
    // Sequence resolution (with recursive junction look-ahead)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Resolve waypoint tokens from <paramref name="startIndex"/> onward, walking each
    /// consecutive pair via <see cref="ExpandSegment"/> and the final token via
    /// <see cref="ExpandLastWaypoint"/>. Returns the accumulated directed edges, the final
    /// head, or a structured failure (exactly one of edges/head vs failure is non-null).
    ///
    /// <para><paramref name="enableLookahead"/> controls junction selection. When true (the
    /// top-level resolution) <see cref="RouteNamedToNamed"/> scores each junction candidate by
    /// the cost of resolving the <em>remaining</em> sequence from it — a bounded recursive
    /// look-ahead that defeats first-match picks producing hairpin U-turns at V-shaped
    /// taxiway junctions (mirrors v1's SelectBestStopNode). When false (inside a look-ahead
    /// probe) it falls back to first-match selection and suppresses the whole-airport detour
    /// fallback, which both bounds recursion to a single level and treats a continuation that
    /// needs a detour as a strong negative signal against the candidate that led there.</para>
    /// </summary>
    private static (List<DirectionalEdge>? Edges, PartialRoute? Head, PathfindingFailure? Failure) ResolveSequence(
        PartialRoute head,
        IReadOnlyList<WaypointToken> tokens,
        int startIndex,
        bool enableLookahead,
        List<ConnectorInsertion> insertions,
        SearchContext ctx
    )
    {
        var edges = new List<DirectionalEdge>();
        var current = head;

        for (int i = startIndex; i < tokens.Count; i++)
        {
            var token = tokens[i];

            if (i + 1 < tokens.Count)
            {
                var next = tokens[i + 1];
                var lookahead = i + 2 < tokens.Count ? tokens[i + 2] : null;
                var (segEdges, newHead, failure) = ExpandSegment(current, token, next, lookahead, tokens, i, enableLookahead, insertions, ctx);
                if (failure is not null)
                {
                    return (null, null, failure);
                }

                edges.AddRange(segEdges!);

                // Reset VisitedNodeIds to just the current head node before entering the next segment.
                // This allows routes that intentionally revisit a taxiway (e.g. A E B B3 A B1) to
                // use nodes already visited in a prior segment. Cycle prevention within a single
                // segment's local search is preserved because each local search starts from the
                // freshly-reset head.
                current = newHead! with
                {
                    VisitedNodeIds = ImmutableHashSet<int>.Empty.Add(newHead!.HeadNodeId),
                };
            }
            else
            {
                // Last waypoint: walk to natural terminus of the taxiway (or the node itself for node-refs).
                var (segEdges, newHead, failure) = ExpandLastWaypoint(current, token, ctx);
                if (failure is not null)
                {
                    return (null, null, failure);
                }

                edges.AddRange(segEdges!);
                current = newHead!;
            }
        }

        return (edges, current, null);
    }

    /// <summary>
    /// Penalty assigned to a junction candidate whose remaining-sequence look-ahead probe
    /// cannot resolve cleanly (a tail segment would need the whole-airport detour). Large
    /// enough to dominate any real route distance so a clean continuation always wins, but
    /// finite so that when <em>every</em> candidate's tail is unresolvable the selection
    /// still falls back to the cheapest cost-to-reach.
    /// </summary>
    private const double TailUnresolvablePenaltyNm = 1000.0;

    /// <summary>
    /// True when there is at least one waypoint after the segment's toTaxiway (token
    /// <paramref name="fromIndex"/>+1), i.e. the route continues past the junction and the
    /// look-ahead probe has something meaningful to discriminate on. For the final
    /// transition (toTaxiway is the last token) the probe would only measure a terminus walk,
    /// so the cheaper geometric heuristic is used instead.
    /// </summary>
    private static bool HasMeaningfulTail(IReadOnlyList<WaypointToken> tokens, int fromIndex) => fromIndex + 2 < tokens.Count;

    /// <summary>
    /// Cost of resolving the remaining sequence (tokens from <paramref name="startIndex"/>)
    /// starting at <paramref name="headAtJunction"/>, used to score a junction candidate.
    /// Resolves first-match with the detour fallback suppressed; returns
    /// <see cref="TailUnresolvablePenaltyNm"/> when the tail cannot be resolved cleanly.
    /// </summary>
    private static double ProbeTailCost(PartialRoute headAtJunction, IReadOnlyList<WaypointToken> tokens, int startIndex, SearchContext ctx)
    {
        // Mirror the main loop's reset of VisitedNodeIds before the next segment so the probe
        // cost matches what the real resolution would produce from this junction.
        var probeStart = headAtJunction with
        {
            VisitedNodeIds = ImmutableHashSet<int>.Empty.Add(headAtJunction.HeadNodeId),
        };
        // Probes never reach the detour (it is suppressed when enableLookahead is false), so no
        // connector insertions are recorded — pass a throwaway list.
        var (_, tailHead, failure) = ResolveSequence(probeStart, tokens, startIndex, enableLookahead: false, [], ctx);
        if (failure is not null || tailHead is null)
        {
            return TailUnresolvablePenaltyNm;
        }

        return Math.Max(0.0, tailHead.AccumulatedCost - probeStart.AccumulatedCost);
    }

    // -----------------------------------------------------------------------
    // Waypoint resolution
    // -----------------------------------------------------------------------

    private sealed record WaypointToken(string Name, bool IsNodeRef, int ResolvedNodeId);

    private static List<WaypointToken> ResolveWaypoints(SearchContext ctx)
    {
        var result = new List<WaypointToken>(ctx.WaypointSequence.Count);
        foreach (string token in ctx.WaypointSequence)
        {
            if (token.StartsWith('#') && int.TryParse(token.AsSpan(1), out int nodeId))
            {
                result.Add(new WaypointToken(token, IsNodeRef: true, ResolvedNodeId: nodeId));
            }
            else
            {
                result.Add(new WaypointToken(token, IsNodeRef: false, ResolvedNodeId: -1));
            }
        }

        return result;
    }

    // -----------------------------------------------------------------------
    // Segment expansion (T_i → T_{i+1})
    // -----------------------------------------------------------------------

    private static (List<DirectionalEdge>? Edges, PartialRoute? Head, PathfindingFailure? Failure) ExpandSegment(
        PartialRoute head,
        WaypointToken current,
        WaypointToken next,
        WaypointToken? lookahead,
        IReadOnlyList<WaypointToken> tokens,
        int index,
        bool enableLookahead,
        List<ConnectorInsertion> insertions,
        SearchContext ctx
    )
    {
        if (next.IsNodeRef)
        {
            // Next is a node-ref: route from current taxiway terminus toward that node.
            return RouteToNodeRef(head, current, next.ResolvedNodeId, ctx);
        }

        if (current.IsNodeRef)
        {
            // Current is a node-ref, next is a named taxiway: walk onto the next taxiway from current node.
            return RouteFromNodeRefToTaxiway(head, current.ResolvedNodeId, next.Name, ctx);
        }

        // Named taxiway → named taxiway: find junction nodes and pick best. Lookahead only
        // applies when it's a named taxiway (not a node-ref) — for node-ref lookaheads, the
        // junction picker has no anchor to align against.
        string? lookaheadName = lookahead is { IsNodeRef: false } la ? la.Name : null;
        return RouteNamedToNamed(head, current.Name, next.Name, lookaheadName, tokens, index, enableLookahead, insertions, ctx);
    }

    private static (List<DirectionalEdge>? Edges, PartialRoute? Head, PathfindingFailure? Failure) ExpandLastWaypoint(
        PartialRoute head,
        WaypointToken waypoint,
        SearchContext ctx
    )
    {
        if (waypoint.IsNodeRef)
        {
            // The sequence ends with a node-ref: route to that specific node.
            return RouteToSpecificNode(head, waypoint.ResolvedNodeId, ctx);
        }

        // Destination-aware terminus: when the clearance ends on a named taxiway that leads to a
        // known parking/spot/helipad node, route to that node along the taxiway instead of walking
        // to the natural terminus. The greedy terminus walk is direction-blind — it picks the
        // geometrically-admissible continuation, which can be the wrong way along the taxiway
        // (post-landing arrival direction) or past the destination, after which ExtendToDestination
        // U-turns back. LocalSearchToJunction routes straight to the destination in the correct
        // direction and stops there (its junction-edge exception allows the final one-hop off the
        // taxiway onto the spot). A destination more than one hop off the taxiway makes the search
        // fail, so it falls through to the terminus walk + ExtendToDestination as before.
        if (
            ctx.Destination.Kind is DestinationKind.Parking or DestinationKind.Spot or DestinationKind.Helipad
            && ctx.Destination.TargetNodeId is { } destId
            && head.HeadNodeId != destId
        )
        {
            var (toDestEdges, toDestHead, _) = LocalSearchToJunction(head, waypoint.Name, destId, ctx);
            if (toDestEdges is not null)
            {
                return (toDestEdges, toDestHead, null);
            }

            // The destination is more than one hop off the named taxiway (e.g. via a connector or a
            // RAMP spur). Pick the on-taxiway stop node from which the destination is reachable at
            // lowest total (walk + extension) cost instead of committing to the greedy terminus —
            // mirrors V1's SelectBestStopNode. The greedy terminus is direction-blind and its single
            // from-terminus extension is often inadmissible (a U-turn at a dead-end) or wrong-way.
            var (stopEdges, stopHead) = SelectBestParkingStop(head, waypoint.Name, destId, ctx);
            if (stopEdges is not null)
            {
                return (stopEdges, stopHead, null);
            }
        }

        // Named taxiway at end: walk to natural terminus, biased toward the destination at the
        // first (momentum-free) step. The greedy walk is otherwise direction-blind and picks an
        // arbitrary admissible direction — wrong when the aircraft starts mid-taxiway facing away
        // from the destination (post-landing arrival heading) or when the destination-runway
        // hold-short sits the opposite way along the named taxiway. The bias only breaks ties on
        // the first step; once moving, admissibility constrains direction as before.
        var bias = ResolveTerminusBias(head, waypoint.Name, ctx);
        return WalkToNaturalTerminus(head, waypoint.Name, ctx, bias);
    }

    /// <summary>
    /// Maximum candidate stop nodes evaluated by <see cref="SelectBestParkingStop"/>. The turn-off
    /// onto a parking/spot spur is geometrically near the destination, so the nearest few taxiway
    /// nodes cover the real stop points without one extension search per taxiway node.
    /// </summary>
    private const int MaxParkingStopCandidates = 10;

    /// <summary>
    /// Pick the on-taxiway stop node from which a parking/spot/helipad destination is reachable at
    /// lowest total (walk + extension) cost. Mirrors v1's <c>SelectBestStopNode</c>: the greedy
    /// terminus walk commits to one direction and a single extension from the dead-end terminus is
    /// often inadmissible (a U-turn) or wrong-way, so instead try the taxiway nodes nearest the
    /// destination as stop points — route to each on-taxiway (<see cref="LocalSearchToJunction"/>)
    /// and extend from there (<see cref="ExtendToDestination"/>) — and return the walk to the best
    /// stop node. The caller's <see cref="ExtendToDestination"/> then completes the route from that
    /// node. Returns (null, null) when no candidate reaches the destination — the caller falls back
    /// to the greedy terminus walk.
    /// </summary>
    private static (List<DirectionalEdge>? Edges, PartialRoute? Head) SelectBestParkingStop(
        PartialRoute head,
        string taxiwayName,
        int destId,
        SearchContext ctx
    )
    {
        if (!ctx.Layout.Nodes.TryGetValue(destId, out var destNode))
        {
            return (null, null);
        }

        var candidates = ctx
            .Layout.GetNodesOnTaxiway(taxiwayName)
            .OrderBy(n => GeoMath.DistanceNm(n.Position, destNode.Position))
            .Take(MaxParkingStopCandidates);

        List<DirectionalEdge>? bestWalk = null;
        PartialRoute? bestStopHead = null;
        double bestTotal = double.MaxValue;

        foreach (var cand in candidates)
        {
            var (walkEdges, candHead, walkCost) = LocalSearchToJunction(head, taxiwayName, cand.Id, ctx);
            if (walkEdges is null || candHead is null)
            {
                continue;
            }

            var (extEdges, extFailure) = ExtendToDestination(candHead, destId, ctx);
            if (extFailure is not null || extEdges is null)
            {
                continue;
            }

            double extCost = 0.0;
            foreach (var e in extEdges)
            {
                extCost += e.DistanceNm;
            }

            double total = walkCost + extCost;
            if (total < bestTotal)
            {
                bestTotal = total;
                bestWalk = walkEdges;
                bestStopHead = candHead;
            }
        }

        if (bestWalk is null)
        {
            return (null, null);
        }

        ctx.DiagnosticLog?.Invoke($"[v2:beststop] twy={taxiwayName} dest={destId} stop={bestStopHead!.HeadNodeId} total={bestTotal:F3}");
        return (bestWalk, bestStopHead);
    }

    /// <summary>
    /// Direction penalty for a bridge candidate with no admissible on-taxiway continuation from
    /// the bridged arrival bearing. Large enough that any candidate WITH a dest-ward continuation
    /// always wins, but finite so a dead-end candidate stays selectable when it is the only option.
    /// </summary>
    private const double BridgeNoOnwardPenaltyNm = 100.0;

    /// <summary>
    /// Bridge the search head from a start node that is not yet on <paramref name="taxiwayName"/>
    /// (typically a parking/RAMP spot, or a runway hold-short on a crossing taxiway) onto a node
    /// carrying an edge on that taxiway, via a bounded BFS. Mirrors v1's <c>BfsToTaxiway</c> but is
    /// direction-aware: among the taxiway-access nodes reachable within <see cref="MaxBridgeHops"/>
    /// hops, it picks the one whose admissible on-taxiway continuation gets nearest
    /// <paramref name="bias"/> (the next-junction or destination position). A direction-blind
    /// nearest pick can enter the taxiway through a corner arc that commits the head to the wrong
    /// branch, after which the correct branch fails the U-turn admissibility check and the route
    /// detours the long way round. Returns empty edges and the unchanged head when the start is
    /// already on the taxiway, or when no taxiway node is reachable — the caller then proceeds and
    /// the per-segment detour remains the fallback.
    /// </summary>
    private static (List<DirectionalEdge> Edges, PartialRoute Head) BridgeStartToTaxiway(
        PartialRoute head,
        string taxiwayName,
        LatLon? bias,
        SearchContext ctx
    )
    {
        if (!ctx.Layout.Nodes.TryGetValue(head.HeadNodeId, out var startNode))
        {
            return ([], head);
        }

        foreach (var edge in startNode.Edges)
        {
            if (edge.MatchesTaxiway(taxiwayName))
            {
                return ([], head);
            }
        }

        var (candidates, cameFrom) = CollectBridgeCandidates(head.HeadNodeId, taxiwayName, ctx);
        if (candidates.Count == 0)
        {
            return ([], head);
        }

        List<DirectionalEdge>? bestEdges = null;
        PartialRoute? bestHead = null;
        double bestScore = double.MaxValue;
        double bestCost = double.MaxValue;

        foreach (int candidateId in candidates)
        {
            var (candEdges, candHead) = BuildBridgePath(head, candidateId, cameFrom, ctx);
            if (candEdges.Count == 0)
            {
                continue;
            }

            double cost = candHead.AccumulatedCost - head.AccumulatedCost;
            // No bias (no destination / next-junction signal): prefer the nearest access node,
            // matching the original nearest-first behaviour.
            double score = bias is { } b ? ScoreBridgeCandidate(candHead, taxiwayName, b, ctx) : cost;

            if ((score < bestScore - 1e-9) || ((Math.Abs(score - bestScore) <= 1e-9) && (cost < bestCost)))
            {
                bestScore = score;
                bestCost = cost;
                bestEdges = candEdges;
                bestHead = candHead;
            }
        }

        if (bestEdges is null || bestHead is null)
        {
            return ([], head);
        }

        ctx.DiagnosticLog?.Invoke(
            $"[v2:bridge] start={head.HeadNodeId} → {bestHead.HeadNodeId} onto {taxiwayName} ({bestEdges.Count} edges, score={bestScore:F3})"
        );
        return (bestEdges, bestHead);
    }

    /// <summary>
    /// BFS from <paramref name="startId"/> collecting every node within <see cref="MaxBridgeHops"/>
    /// hops that carries an edge on <paramref name="taxiwayName"/>. Unlike a stop-at-first BFS, it
    /// keeps exploring past a taxiway-access node so access nodes reachable only THROUGH another one
    /// (e.g. the next junction node further along the connecting taxiway) are also discovered —
    /// these are exactly the alternate-direction entries a direction-blind nearest pick would miss.
    /// Returns the candidate node ids and the BFS-tree parent map for path reconstruction.
    /// </summary>
    private static (List<int> Candidates, Dictionary<int, (int ParentId, IGroundEdge Edge)> CameFrom) CollectBridgeCandidates(
        int startId,
        string taxiwayName,
        SearchContext ctx
    )
    {
        var visited = new HashSet<int> { startId };
        var queue = new Queue<(int NodeId, int Depth)>();
        var cameFrom = new Dictionary<int, (int ParentId, IGroundEdge Edge)>();
        var candidates = new List<int>();
        queue.Enqueue((startId, 0));

        while (queue.Count > 0)
        {
            var (nodeId, depth) = queue.Dequeue();
            if (!ctx.Layout.Nodes.TryGetValue(nodeId, out var node))
            {
                continue;
            }

            foreach (var edge in node.Edges)
            {
                var neighbor = edge.OtherNode(node);
                if (!visited.Add(neighbor.Id))
                {
                    continue;
                }

                cameFrom[neighbor.Id] = (nodeId, edge);

                if (neighbor.Edges.Any(e => e.MatchesTaxiway(taxiwayName)))
                {
                    candidates.Add(neighbor.Id);
                }

                if (depth + 1 < MaxBridgeHops)
                {
                    queue.Enqueue((neighbor.Id, depth + 1));
                }
            }
        }

        return (candidates, cameFrom);
    }

    /// <summary>
    /// Reconstruct the directed bridge edges and the resulting <see cref="PartialRoute"/> head for
    /// the path from <paramref name="head"/> to <paramref name="targetId"/> recorded in
    /// <paramref name="cameFrom"/>. Returns empty edges when the chain cannot be walked.
    /// </summary>
    private static (List<DirectionalEdge> Edges, PartialRoute Head) BuildBridgePath(
        PartialRoute head,
        int targetId,
        Dictionary<int, (int ParentId, IGroundEdge Edge)> cameFrom,
        SearchContext ctx
    )
    {
        var pathNodes = new List<int>();
        int trace = targetId;
        while (trace != head.HeadNodeId)
        {
            pathNodes.Add(trace);
            if (!cameFrom.TryGetValue(trace, out var step))
            {
                return ([], head);
            }

            trace = step.ParentId;
        }

        pathNodes.Reverse();

        var bridgeEdges = new List<DirectionalEdge>(pathNodes.Count);
        var current = head;
        foreach (int id in pathNodes)
        {
            var (_, edge) = cameFrom[id];
            if (!ctx.Layout.Nodes.TryGetValue(current.HeadNodeId, out var fromNode) || !ctx.Layout.Nodes.TryGetValue(id, out var toNode))
            {
                break;
            }

            double arrival = GeometricAdmissibility.GetArrivalBearing(edge, fromNode, toNode);
            double cost = RouteCostFunction.IncrementalCost(current, edge, toNode, ctx);
            string twyName = RouteCostFunction.ResolveTaxiwayName(edge, current.HeadNodeId);

            bridgeEdges.Add(edge.Directed(fromNode, toNode));
            current = current with
            {
                HeadNodeId = id,
                ArrivalBearing = arrival,
                LastEdge = edge,
                LastTaxiwayName = twyName,
                Previous = current,
                Depth = current.Depth + 1,
                AccumulatedCost = current.AccumulatedCost + cost,
                VisitedNodeIds = current.VisitedNodeIds.Add(id),
            };
        }

        return (bridgeEdges, current);
    }

    /// <summary>
    /// Score a bridge candidate by the distance from <paramref name="bias"/> of the nearest node
    /// reachable by one admissible on-taxiway step from the bridged head — i.e. how well the
    /// candidate's available continuation heads toward where the route must go. A candidate whose
    /// only taxiway edges are inadmissible from the bridged arrival bearing (a corner-arc entry that
    /// would force an immediate U-turn) gets its own distance plus
    /// <see cref="BridgeNoOnwardPenaltyNm"/>, so any candidate with a real dest-ward continuation
    /// wins. Lower is better.
    /// </summary>
    private static double ScoreBridgeCandidate(PartialRoute candHead, string taxiwayName, LatLon bias, SearchContext ctx)
    {
        if (!ctx.Layout.Nodes.TryGetValue(candHead.HeadNodeId, out var node))
        {
            return double.MaxValue;
        }

        double best = double.MaxValue;
        foreach (var edge in node.Edges)
        {
            if (!edge.MatchesTaxiway(taxiwayName))
            {
                continue;
            }

            var neighbor = edge.OtherNode(node);
            if (candHead.VisitedNodeIds.Contains(neighbor.Id))
            {
                continue;
            }

            if (!GeometricAdmissibility.IsAdmissible(candHead, edge, neighbor, ctx.Category))
            {
                continue;
            }

            double d = GeoMath.DistanceNm(neighbor.Position, bias);
            if (d < best)
            {
                best = d;
            }
        }

        if (best == double.MaxValue)
        {
            return GeoMath.DistanceNm(node.Position, bias) + BridgeNoOnwardPenaltyNm;
        }

        return best;
    }

    /// <summary>
    /// The position the bridge should head toward when entering the first cleared taxiway: the
    /// junction with the next cleared taxiway when the route continues, otherwise the destination
    /// (parking/spot/node target, or the nearest destination-runway hold-short on the taxiway).
    /// Null when there is no directional signal — the bridge then keeps its nearest-access pick.
    /// </summary>
    private static LatLon? ResolveBridgeBias(IReadOnlyList<WaypointToken> tokens, PartialRoute head, SearchContext ctx)
    {
        var first = tokens[0];

        // Route continues past the first taxiway: head toward the junction with the next token.
        if (tokens.Count >= 2)
        {
            var next = tokens[1];
            if (next.IsNodeRef)
            {
                if (ctx.Layout.Nodes.TryGetValue(next.ResolvedNodeId, out var nextNode))
                {
                    return nextNode.Position;
                }
            }
            else
            {
                var junctions = FindJunctionCandidates(ctx.Layout, first.Name, next.Name);
                if (junctions.Count > 0)
                {
                    double sumLat = 0;
                    double sumLon = 0;
                    foreach (var j in junctions)
                    {
                        sumLat += j.Position.Lat;
                        sumLon += j.Position.Lon;
                    }

                    return new LatLon(sumLat / junctions.Count, sumLon / junctions.Count);
                }
            }
        }

        // Single cleared taxiway: head toward the destination node when known.
        if (ctx.Destination.TargetNodeId is { } destId && ctx.Layout.Nodes.TryGetValue(destId, out var destNode))
        {
            return destNode.Position;
        }

        // Runway destination has no single node — reuse the terminus bias (nearest
        // destination-runway hold-short on the first taxiway).
        return ResolveTerminusBias(head, first.Name, ctx);
    }

    // -----------------------------------------------------------------------
    // Named taxiway to named taxiway
    // -----------------------------------------------------------------------

    private static (List<DirectionalEdge>? Edges, PartialRoute? Head, PathfindingFailure? Failure) RouteNamedToNamed(
        PartialRoute head,
        string fromTaxiway,
        string toTaxiway,
        string? lookaheadTaxiway,
        IReadOnlyList<WaypointToken> tokens,
        int index,
        bool enableLookahead,
        List<ConnectorInsertion> insertions,
        SearchContext ctx
    )
    {
        // Find all junction candidates: nodes on fromTaxiway with at least one edge onto toTaxiway.
        var junctionCandidates = FindJunctionCandidates(ctx.Layout, fromTaxiway, toTaxiway);

        // When the route continues past toTaxiway and look-ahead is enabled, score each junction
        // by the cost of resolving the remaining sequence from it (recursive probe). Otherwise
        // fall back to the cheap geometric look-ahead anchor.
        bool useTailProbe = enableLookahead && HasMeaningfulTail(tokens, index);

        // Lookahead anchor: centroid of junctions between toTaxiway and the next-next taxiway.
        // Used to bias junction selection so that the chosen junction's toTaxiway-side points
        // toward where the route will continue, avoiding wrong-side picks that force a U-turn
        // immediately after the transition.
        (double Lat, double Lon)? lookaheadAnchor = null;
        if (lookaheadTaxiway is not null)
        {
            var anchors = FindJunctionCandidates(ctx.Layout, toTaxiway, lookaheadTaxiway);
            if (anchors.Count > 0)
            {
                double sumLat = 0;
                double sumLon = 0;
                foreach (var a in anchors)
                {
                    sumLat += a.Position.Lat;
                    sumLon += a.Position.Lon;
                }

                lookaheadAnchor = (sumLat / anchors.Count, sumLon / anchors.Count);
            }
        }

        // Final named transition into a runway destination: there is no next named taxiway to
        // anchor toward, but the destination runway's own hold-short on toTaxiway is the de-facto
        // next waypoint. Anchor junction selection toward it so the chosen junction's toTaxiway
        // side leads to the runway — otherwise the cheapest (nearest-along-fromTaxiway) junction
        // can commit the following terminus walk to the wrong end of toTaxiway, after which the
        // correct direction fails the U-turn admissibility check and the route detours the long
        // way around to the same runway (OAK "TAXI D J C 33": C-from-the-near-junction only
        // continues toward A, forcing a loop via B/28L-10R/P back to 33).
        if (
            lookaheadAnchor is null
            && lookaheadTaxiway is null
            && ctx.Destination.Kind == DestinationKind.Runway
            && ctx.Destination.RunwayId is { } destRunwayId
        )
        {
            lookaheadAnchor = ResolveRunwayHoldShortAnchorOnTaxiway(toTaxiway, destRunwayId, ctx);
        }

        ctx.DiagnosticLog?.Invoke(
            $"[v2:segment] twy={fromTaxiway}→{toTaxiway} head={head.HeadNodeId} junctions={junctionCandidates.Count} lookahead={lookaheadTaxiway ?? "(none)"}"
        );

        if (junctionCandidates.Count == 0)
        {
            // No direct junction between the two cleared taxiways. Inside a look-ahead probe the
            // detour is suppressed (a continuation needing one is a negative signal). At the top
            // level we detour and — because zero junction candidates verifies there is genuinely
            // no edge or arc joining fromTaxiway and toTaxiway — record the inserted connector so
            // the controller is notified of the mandatory insertion rather than warned about it.
            if (!enableLookahead)
            {
                return (null, null, DetourSuppressedFailure(fromTaxiway, toTaxiway, head));
            }

            var detour = TryDetour(head, fromTaxiway, toTaxiway, ctx);
            if (detour.Failure is null && detour.Edges is not null)
            {
                RecordConnectorInsertion(insertions, fromTaxiway, toTaxiway, detour.Edges, tokens);
            }

            return detour;
        }

        // For each junction candidate, run a bounded local search from the current head.
        (List<DirectionalEdge>? bestEdges, PartialRoute? bestHead, double bestCost) = (null, null, double.MaxValue);

        foreach (var junctionNode in junctionCandidates)
        {
            var (segEdges, segHead, cost) = LocalSearchToJunction(head, fromTaxiway, junctionNode.Id, ctx);
            if (segEdges is null)
            {
                continue;
            }

            // Look-ahead: prefer the junction whose continuation through the rest of the
            // sequence is cheapest (recursive probe), defeating first-match picks that strand
            // the route on the wrong leg of a V-shaped taxiway. When there is no meaningful tail
            // (final transition) or we are already inside a probe, use the geometric heuristic.
            double continuationCost = useTailProbe
                ? ProbeTailCost(segHead!, tokens, index + 1, ctx)
                : ComputeLookaheadPenalty(junctionNode, toTaxiway, lookaheadAnchor);
            double totalCost = cost + continuationCost;

            ctx.DiagnosticLog?.Invoke(
                $"[v2:junction-pick] {fromTaxiway}→{toTaxiway} candidate={junctionNode.Id} cost={cost:F3} continuation+={continuationCost:F3} total={totalCost:F3}"
            );

            if (totalCost < bestCost)
            {
                bestCost = totalCost;
                bestEdges = segEdges;
                bestHead = segHead;
            }
        }

        if (bestEdges is null)
        {
            // All junction candidates failed: attempt detour (suppressed inside a probe).
            return enableLookahead
                ? TryDetour(head, fromTaxiway, toTaxiway, ctx)
                : (null, null, DetourSuppressedFailure(fromTaxiway, toTaxiway, head));
        }

        ctx.DiagnosticLog?.Invoke(
            $"[v2:committed] twy={fromTaxiway}→{toTaxiway} via={bestHead!.HeadNodeId} edges={bestEdges.Count} cost={bestCost:F3}"
        );

        return (bestEdges, bestHead, null);
    }

    /// <summary>
    /// Penalise junction candidates whose toTaxiway-side does not point toward
    /// <paramref name="lookaheadAnchor"/>. A junction is "well-aligned" if it has at least
    /// one toTaxiway-edge whose other endpoint is geographically closer to the anchor than
    /// the junction itself — meaning continuing along that edge moves the aircraft toward
    /// the next-next taxiway. Junctions with no such edge would force a U-turn on
    /// toTaxiway after the transition.
    /// </summary>
    private const double LookaheadMisalignedPenaltyNm = 10.0;

    private static double ComputeLookaheadPenalty(GroundNode junction, string toTaxiway, (double Lat, double Lon)? anchor)
    {
        if (anchor is not { } a)
        {
            return 0.0;
        }

        double junctionToAnchorNm = GeoMath.DistanceNm(junction.Position.Lat, junction.Position.Lon, a.Lat, a.Lon);

        foreach (var edge in junction.Edges)
        {
            if (!edge.MatchesTaxiway(toTaxiway))
            {
                continue;
            }

            var neighbor = edge.OtherNode(junction);
            double neighborToAnchorNm = GeoMath.DistanceNm(neighbor.Position.Lat, neighbor.Position.Lon, a.Lat, a.Lon);
            if (neighborToAnchorNm < junctionToAnchorNm)
            {
                // At least one toTaxiway-edge moves us closer to the anchor — junction OK.
                return 0.0;
            }
        }

        // Every toTaxiway-edge moves away from the anchor — picking this junction would
        // require a U-turn on toTaxiway to reach the next-next taxiway.
        return LookaheadMisalignedPenaltyNm;
    }

    /// <summary>
    /// Centroid of the destination runway's hold-short nodes that lie on <paramref name="taxiway"/>,
    /// used as the look-ahead anchor for the final named transition when the route ends at a runway
    /// (there is no next named taxiway). Steers junction selection toward the junction whose
    /// taxiway side leads to the runway's own hold-short, so the following terminus walk heads the
    /// right way along the taxiway instead of committing to the opposite end and detouring back.
    /// Null when no hold-short for the runway sits on the taxiway (the runway is reached via a
    /// numbered variant or connector, which <see cref="TryVariantExtension"/> handles later) — in
    /// which case junction selection keeps its prior cost-only behaviour.
    /// </summary>
    private static (double Lat, double Lon)? ResolveRunwayHoldShortAnchorOnTaxiway(string taxiway, string runwayId, SearchContext ctx)
    {
        double sumLat = 0;
        double sumLon = 0;
        int count = 0;
        foreach (var node in ctx.Layout.Nodes.Values)
        {
            if (!IsRunwayHoldShort(node.Id, runwayId, ctx))
            {
                continue;
            }

            bool onTaxiway = false;
            foreach (var edge in node.Edges)
            {
                if (edge.MatchesTaxiway(taxiway))
                {
                    onTaxiway = true;
                    break;
                }
            }

            if (!onTaxiway)
            {
                continue;
            }

            sumLat += node.Position.Lat;
            sumLon += node.Position.Lon;
            count++;
        }

        return count > 0 ? (sumLat / count, sumLon / count) : null;
    }

    /// <summary>
    /// Find all nodes on <paramref name="fromTaxiway"/> that have at least one edge belonging
    /// to <paramref name="toTaxiway"/>. These are the junction candidates between the two taxiways.
    /// </summary>
    private static List<GroundNode> FindJunctionCandidates(AirportGroundLayout layout, string fromTaxiway, string toTaxiway)
    {
        var result = new List<GroundNode>();
        var onFromTaxiway = layout.GetNodesOnTaxiway(fromTaxiway);

        foreach (var node in onFromTaxiway)
        {
            foreach (var edge in node.Edges)
            {
                if (edge.MatchesTaxiway(toTaxiway))
                {
                    result.Add(node);
                    break;
                }
            }
        }

        return result;
    }

    // -----------------------------------------------------------------------
    // Local best-first search within a single taxiway to a target junction node
    // -----------------------------------------------------------------------

    /// <summary>
    /// Run a bounded best-first search from <paramref name="head"/> to <paramref name="junctionNodeId"/>,
    /// constrained to edges on <paramref name="taxiwayName"/> (plus junction arcs leaving toward the next taxiway).
    /// Returns the edge list, the updated partial route head, and the total cost.
    /// Returns null edges when the junction cannot be reached.
    /// </summary>
    private static (List<DirectionalEdge>? Edges, PartialRoute? Head, double Cost) LocalSearchToJunction(
        PartialRoute startHead,
        string taxiwayName,
        int junctionNodeId,
        SearchContext ctx
    )
    {
        // Trivial: head is already the junction.
        if (startHead.HeadNodeId == junctionNodeId)
        {
            ctx.DiagnosticLog?.Invoke($"[v2:local] twy={taxiwayName} start={startHead.HeadNodeId} dest={junctionNodeId} TRIVIAL (start==dest)");
            return ([], startHead, 0.0);
        }

        if (!ctx.Layout.Nodes.TryGetValue(junctionNodeId, out var destNode))
        {
            ctx.DiagnosticLog?.Invoke($"[v2:local] twy={taxiwayName} dest={junctionNodeId} NOT IN LAYOUT");
            return (null, null, double.MaxValue);
        }

        ctx.DiagnosticLog?.Invoke(
            $"[v2:local] BEGIN twy={taxiwayName} start={startHead.HeadNodeId} arr={startHead.ArrivalBearing:F1} hasPrior={startHead.LastEdge is not null} dest={junctionNodeId}"
        );

        var openSet = new PriorityQueue<PartialRoute, double>();

        // Keyed by (node, arrival-bearing-bucket): onward admissibility depends on arrival bearing,
        // so node-id-only pruning would let a cheaper dead-end arrival suppress the only admissible
        // arrival (see GeometricAdmissibility.PruningStateKey).
        var bestGScore = new Dictionary<(int Node, int Bucket), double>();
        int expansions = 0;
        PartialRoute? deepest = null;
        int admittedTotal = 0;
        int rejectedTotal = 0;

        double h0 = ctx.Layout.Nodes.TryGetValue(startHead.HeadNodeId, out var sn) ? RouteCostFunction.Heuristic(sn, destNode) : 0.0;

        openSet.Enqueue(startHead, startHead.AccumulatedCost + h0);
        bestGScore[GeometricAdmissibility.PruningStateKey(startHead.HeadNodeId, startHead.ArrivalBearing)] = startHead.AccumulatedCost;

        while (openSet.Count > 0)
        {
            var current = openSet.Dequeue();
            expansions++;

            if (expansions > MaxLocalExpansions)
            {
                ctx.DiagnosticLog?.Invoke(
                    $"[v2:local] EXHAUSTED twy={taxiwayName} dest={junctionNodeId} expansions={expansions} deepest={deepest?.HeadNodeId ?? -1} depth={deepest?.Depth ?? 0} admitted={admittedTotal} rejected={rejectedTotal}"
                );
                break;
            }

            if (
                bestGScore.TryGetValue(GeometricAdmissibility.PruningStateKey(current.HeadNodeId, current.ArrivalBearing), out double recorded)
                && (current.AccumulatedCost > recorded + 1e-9)
            )
            {
                continue;
            }

            if (deepest is null || current.Depth > deepest.Depth)
            {
                deepest = current;
            }

            if (current.HeadNodeId == junctionNodeId)
            {
                // Found the junction: extract edges accumulated since the start.
                var edges = ExtractEdgesSince(current, startHead.HeadNodeId, startHead.Depth);
                ctx.DiagnosticLog?.Invoke(
                    $"[v2:local] SUCCESS twy={taxiwayName} dest={junctionNodeId} expansions={expansions} edges={edges.Count} cost={current.AccumulatedCost - startHead.AccumulatedCost:F3} admitted={admittedTotal} rejected={rejectedTotal}"
                );
                return (edges, current, current.AccumulatedCost - startHead.AccumulatedCost);
            }

            if (!ctx.Layout.Nodes.TryGetValue(current.HeadNodeId, out var headNode))
            {
                continue;
            }

            int admittedHere = 0;
            int rejectedHere = 0;

            foreach (var edge in headNode.Edges)
            {
                var nextNode = edge.OtherNode(headNode);

                if (current.VisitedNodeIds.Contains(nextNode.Id))
                {
                    ctx.DiagnosticLog?.Invoke($"[v2:local-edge] {current.HeadNodeId}->{nextNode.Id} twy={edge.TaxiwayName} REJECT visited");
                    rejectedHere++;
                    continue;
                }

                // Constrain to edges on the current taxiway, OR edges that connect to the junction node directly.
                bool matchesTwy = edge.MatchesTaxiway(taxiwayName);
                bool isJunctionEdge = nextNode.Id == junctionNodeId;
                if (!matchesTwy && !isJunctionEdge)
                {
                    ctx.DiagnosticLog?.Invoke(
                        $"[v2:local-edge] {current.HeadNodeId}->{nextNode.Id} twy={edge.TaxiwayName} REJECT off-taxiway (need {taxiwayName})"
                    );
                    rejectedHere++;
                    continue;
                }

                if (!GeometricAdmissibility.IsAdmissible(current, edge, nextNode, ctx.Category))
                {
                    double dep = GeometricAdmissibility.GetDepartureBearing(edge, headNode, nextNode);
                    double delta = RouteCostFunction.HeadingDelta(current.ArrivalBearing, dep);
                    ctx.DiagnosticLog?.Invoke(
                        $"[v2:local-edge] {current.HeadNodeId}->{nextNode.Id} twy={edge.TaxiwayName} REJECT admis arr={current.ArrivalBearing:F1} dep={dep:F1} delta={delta:F1} limit={CategoryLimits.MaxHeadingChangeDeg(ctx.Category):F0}"
                    );
                    rejectedHere++;
                    continue;
                }

                double incrementalCost = RouteCostFunction.IncrementalCost(current, edge, nextNode, ctx);

                // Apply direction-reversal penalty for SegmentExpander local searches (§Decisions §7).
                // When the edge bearing is more than 90° away from the overall segment direction
                // (head → destination), treat it as a temporary reversal.
                incrementalCost += ComputeDirectionReversalPenalty(current, edge, headNode, nextNode, destNode);

                // req ①: penalise leaving the walked taxiway onto a membership taxiway-junction arc
                // ("X - Y", both taxiways) as a CONTINUATION — a single-name continuation must win.
                // The legitimate turn onto the next taxiway (nextNode == junction) and runway-crossing
                // arcs (IsRunwayJunction) are not penalised. Soft: the arc stays usable when it is the
                // only continuation, so a resolvable clearance never fails.
                if (matchesTwy && !isJunctionEdge && (edge is GroundArc { IsMembershipTaxiwayJunctionArc: true }))
                {
                    incrementalCost += RouteCostFunction.MembershipJunctionArcContinuationCostNm;
                }

                double newGScore = current.AccumulatedCost + incrementalCost;

                // Zero-distance no-op edges (phase-d-shorten between co-located nodes) carry
                // bogus inherited bearings — propagate the current arrival bearing through
                // them so the next admissibility check (and the closed-set key below) sees the
                // real heading.
                double arrival = GeometricAdmissibility.IsNoOpEdge(edge)
                    ? current.ArrivalBearing
                    : GeometricAdmissibility.GetArrivalBearing(edge, headNode, nextNode);

                var nextKey = GeometricAdmissibility.PruningStateKey(nextNode.Id, arrival);
                if (bestGScore.TryGetValue(nextKey, out double existing) && (newGScore >= existing - 1e-9))
                {
                    ctx.DiagnosticLog?.Invoke(
                        $"[v2:local-edge] {current.HeadNodeId}->{nextNode.Id} twy={edge.TaxiwayName} REJECT g-score new={newGScore:F3} existing={existing:F3}"
                    );
                    rejectedHere++;
                    continue;
                }

                bestGScore[nextKey] = newGScore;

                string twyName = RouteCostFunction.ResolveTaxiwayName(edge, current.HeadNodeId);

                ctx.DiagnosticLog?.Invoke(
                    $"[v2:local-edge] {current.HeadNodeId}->{nextNode.Id} twy={edge.TaxiwayName} ADMIT arr={current.ArrivalBearing:F1} arr'={arrival:F1} g={newGScore:F3} h={RouteCostFunction.Heuristic(nextNode, destNode):F3}"
                );

                var extended = current with
                {
                    HeadNodeId = nextNode.Id,
                    ArrivalBearing = arrival,
                    LastEdge = edge,
                    LastTaxiwayName = twyName,
                    Previous = current,
                    Depth = current.Depth + 1,
                    AccumulatedCost = newGScore,
                    VisitedNodeIds = current.VisitedNodeIds.Add(nextNode.Id),
                };

                double h = RouteCostFunction.Heuristic(nextNode, destNode);
                double fScore = newGScore + h - (extended.Depth * 1e-9);
                openSet.Enqueue(extended, fScore);
                admittedHere++;
            }

            admittedTotal += admittedHere;
            rejectedTotal += rejectedHere;

            ctx.DiagnosticLog?.Invoke(
                $"[v2:local-pop] node={current.HeadNodeId} depth={current.Depth} arr={current.ArrivalBearing:F1} admit={admittedHere} reject={rejectedHere}"
            );
        }

        ctx.DiagnosticLog?.Invoke(
            $"[v2:local] FAIL twy={taxiwayName} dest={junctionNodeId} expansions={expansions} openSet=empty deepest={deepest?.HeadNodeId ?? -1} depth={deepest?.Depth ?? 0} admitted={admittedTotal} rejected={rejectedTotal}"
        );

        return (null, null, double.MaxValue);
    }

    /// <summary>
    /// Compute the direction-reversal penalty for a candidate edge in a local segment search.
    /// Fires when the edge departs more than 90° away from the start-of-segment → junction-node direction.
    /// </summary>
    private static double ComputeDirectionReversalPenalty(
        PartialRoute current,
        IGroundEdge edge,
        GroundNode headNode,
        GroundNode nextNode,
        GroundNode destNode
    )
    {
        double departureBearing = GeometricAdmissibility.GetDepartureBearing(edge, headNode, nextNode);
        double segmentBearing = GeoMath.BearingTo(headNode.Position, destNode.Position);
        double delta = RouteCostFunction.HeadingDelta(departureBearing, segmentBearing);
        return delta > 90.0 ? RouteCostFunction.DirectionReversalCostNm : 0.0;
    }

    /// <summary>
    /// Extract the directed edges from <paramref name="route"/> that were added after the
    /// node with id <paramref name="startNodeId"/> at <paramref name="startDepth"/>.
    /// </summary>
    private static List<DirectionalEdge> ExtractEdgesSince(PartialRoute route, int startNodeId, int startDepth)
    {
        int edgeCount = route.Depth - startDepth;
        if (edgeCount <= 0)
        {
            return [];
        }

        var result = new DirectionalEdge[edgeCount];
        var current = route;

        for (int i = edgeCount - 1; i >= 0; i--)
        {
            var prevNodeId = current.Previous!.HeadNodeId;
            var prevNode = FindNodeInChain(current, prevNodeId);
            var headNode = FindNodeInChain(current, current.HeadNodeId);
            result[i] = current.LastEdge!.Directed(prevNode, headNode);
            current = current.Previous;
        }

        return [.. result];
    }

    private static GroundNode FindNodeInChain(PartialRoute route, int nodeId)
    {
        var cursor = route;
        while (cursor is not null)
        {
            if (cursor.LastEdge is not null)
            {
                foreach (var n in cursor.LastEdge.Nodes)
                {
                    if (n.Id == nodeId)
                    {
                        return n;
                    }
                }
            }

            cursor = cursor.Previous!;
        }

        throw new InvalidOperationException($"Node {nodeId} not found in partial route chain.");
    }

    // -----------------------------------------------------------------------
    // Walk to natural terminus (last waypoint, no destination)
    // -----------------------------------------------------------------------

    private static (List<DirectionalEdge>? Edges, PartialRoute? Head, PathfindingFailure? Failure) WalkToNaturalTerminus(
        PartialRoute head,
        string taxiwayName,
        SearchContext ctx,
        LatLon? biasToward
    )
    {
        // Walk forward along the taxiway from current head, staying on the named taxiway,
        // following the geometrically admissible continuation until no more edges remain.
        var edges = new List<DirectionalEdge>();
        var current = head;
        bool madeProgress = true;

        while (madeProgress)
        {
            madeProgress = false;

            if (!ctx.Layout.Nodes.TryGetValue(current.HeadNodeId, out var headNode))
            {
                break;
            }

            // On the first (momentum-free) step, break ties toward the destination so the walk
            // heads the right way along the taxiway; afterwards admissibility fixes the direction.
            bool firstStep = (edges.Count == 0) && (biasToward is not null);

            // Find the best admissible forward step on this taxiway.
            IGroundEdge? bestEdge = null;
            GroundNode? bestNext = null;
            double bestCost = double.MaxValue;
            double bestBiasDist = double.MaxValue;
            // Sentinel true so the first single-name candidate always displaces it.
            bool bestIsJunctionArc = true;

            foreach (var edge in headNode.Edges)
            {
                if (!edge.MatchesTaxiway(taxiwayName))
                {
                    continue;
                }

                var nextNode = edge.OtherNode(headNode);
                if (current.VisitedNodeIds.Contains(nextNode.Id))
                {
                    continue;
                }

                if (!GeometricAdmissibility.IsAdmissible(current, edge, nextNode, ctx.Category))
                {
                    continue;
                }

                double cost = RouteCostFunction.IncrementalCost(current, edge, nextNode, ctx);

                // req ①: when extending the same named taxiway, an exact single-name edge ranks
                // strictly above a junction arc that matches taxiwayName only by membership (e.g.
                // an "A - Q1" arc when walking A). A multi-name junction arc is a turn OFF the
                // taxiway onto a crossing one, not a continuation of it — V2's collapsed junctions
                // expose several such membership matches at one node. Single-name wins regardless
                // of cost; cost only breaks ties within the same tier. Runway-crossing arcs
                // (IsRunwayJunction, e.g. "H - RWY...") DO continue the taxiway across a runway and
                // are not treated as junction arcs.
                bool isJunctionArc = edge is GroundArc { IsMembershipTaxiwayJunctionArc: true };
                double biasDist = firstStep ? GeoMath.DistanceNm(nextNode.Position, biasToward!.Value) : 0.0;
                bool sameTier = bestIsJunctionArc == isJunctionArc;
                bool tieBetter = firstStep ? (biasDist < bestBiasDist) : (cost < bestCost);
                bool better = (bestEdge is null) || (bestIsJunctionArc && !isJunctionArc) || (sameTier && tieBetter);

                if (better)
                {
                    bestCost = cost;
                    bestBiasDist = biasDist;
                    bestEdge = edge;
                    bestNext = nextNode;
                    bestIsJunctionArc = isJunctionArc;
                }
            }

            if (bestEdge is null)
            {
                break;
            }

            var arrival = GeometricAdmissibility.GetArrivalBearing(bestEdge, headNode, bestNext!);
            var twyName = RouteCostFunction.ResolveTaxiwayName(bestEdge, current.HeadNodeId);

            edges.Add(bestEdge.Directed(headNode, bestNext!));
            current = current with
            {
                HeadNodeId = bestNext!.Id,
                ArrivalBearing = arrival,
                LastEdge = bestEdge,
                LastTaxiwayName = twyName,
                Previous = current,
                Depth = current.Depth + 1,
                AccumulatedCost = current.AccumulatedCost + bestCost,
                VisitedNodeIds = current.VisitedNodeIds.Add(bestNext!.Id),
            };
            madeProgress = true;
        }

        ctx.DiagnosticLog?.Invoke($"[v2:terminus] twy={taxiwayName} terminus={current.HeadNodeId} edges={edges.Count}");

        return (edges, current, null);
    }

    /// <summary>
    /// The position the final-taxiway terminus walk should head toward at its first step, for a
    /// runway destination: the nearest hold-short for the destination runway that lies on the named
    /// taxiway (so a "TAXI B 28R" walk heads toward B's own 28R hold-short instead of away from it,
    /// avoiding a wrong-direction walk that then detours via another taxiway). Null when there is no
    /// directional preference — the walk keeps its prior admissibility-only behaviour. Parking/spot
    /// destinations are handled by the on-taxiway LocalSearchToJunction in ExpandLastWaypoint, not
    /// here: biasing the terminus walk toward an off-taxiway parking node is unreliable (it can pick
    /// a terminus from which the parking extension cannot complete).
    /// </summary>
    private static LatLon? ResolveTerminusBias(PartialRoute head, string taxiwayName, SearchContext ctx)
    {
        if (
            ctx.Destination.Kind == DestinationKind.Runway
            && ctx.Destination.RunwayId is { } runwayId
            && ctx.Layout.Nodes.TryGetValue(head.HeadNodeId, out var headNode)
        )
        {
            GroundNode? best = null;
            double bestDistNm = double.MaxValue;
            foreach (var node in ctx.Layout.Nodes.Values)
            {
                if (!IsRunwayHoldShort(node.Id, runwayId, ctx))
                {
                    continue;
                }

                bool onTaxiway = false;
                foreach (var edge in node.Edges)
                {
                    if (edge.MatchesTaxiway(taxiwayName))
                    {
                        onTaxiway = true;
                        break;
                    }
                }

                if (!onTaxiway)
                {
                    continue;
                }

                double distNm = GeoMath.DistanceNm(node.Position, headNode.Position);
                if (distNm < bestDistNm)
                {
                    bestDistNm = distNm;
                    best = node;
                }
            }

            if (best is not null)
            {
                return best.Position;
            }
        }

        return null;
    }

    // -----------------------------------------------------------------------
    // Node-ref waypoint handling
    // -----------------------------------------------------------------------

    private static (List<DirectionalEdge>? Edges, PartialRoute? Head, PathfindingFailure? Failure) RouteToNodeRef(
        PartialRoute head,
        WaypointToken currentToken,
        int targetNodeId,
        SearchContext ctx
    )
    {
        // First: walk to terminus of current taxiway near the target node, then route to target.
        return RouteToSpecificNode(head, targetNodeId, ctx);
    }

    private static (List<DirectionalEdge>? Edges, PartialRoute? Head, PathfindingFailure? Failure) RouteFromNodeRefToTaxiway(
        PartialRoute head,
        int nodeRefId,
        string nextTaxiway,
        SearchContext ctx
    )
    {
        // Head is already at the node-ref (resolved in previous step).
        // Find junction candidates from that node onto the next taxiway.
        if (!ctx.Layout.Nodes.TryGetValue(nodeRefId, out var nodeRefNode))
        {
            return (
                null,
                null,
                new PathfindingFailure(FailureKind.StartNodeUnreachable, $"Node-reference node {nodeRefId} not found in layout.", null, null, null)
            );
        }

        // Treat the node-ref as a single-node "taxiway" and find the best junction to nextTaxiway.
        var junctionCandidates = FindJunctionCandidates(ctx.Layout, nodeRefNode, nextTaxiway);
        if (junctionCandidates.Count == 0)
        {
            return TryDetour(head, $"#{nodeRefId}", nextTaxiway, ctx);
        }

        (List<DirectionalEdge>? bestEdges, PartialRoute? bestHead, double bestCost) = (null, null, double.MaxValue);

        foreach (var junctionNode in junctionCandidates)
        {
            var (segEdges, segHead, cost) = LocalSearchToJunction(head, nextTaxiway, junctionNode.Id, ctx);
            if (segEdges is null)
            {
                continue;
            }

            if (cost < bestCost)
            {
                bestCost = cost;
                bestEdges = segEdges;
                bestHead = segHead;
            }
        }

        if (bestEdges is null)
        {
            return TryDetour(head, $"#{nodeRefId}", nextTaxiway, ctx);
        }

        return (bestEdges, bestHead, null);
    }

    /// <summary>
    /// Find junction candidates from a specific node (rather than all nodes on a taxiway).
    /// </summary>
    private static List<GroundNode> FindJunctionCandidates(AirportGroundLayout layout, GroundNode fromNode, string toTaxiway)
    {
        var result = new List<GroundNode>();
        foreach (var edge in fromNode.Edges)
        {
            if (edge.MatchesTaxiway(toTaxiway))
            {
                result.Add(fromNode);
                return result;
            }
        }

        return result;
    }

    private static (List<DirectionalEdge>? Edges, PartialRoute? Head, PathfindingFailure? Failure) RouteToSpecificNode(
        PartialRoute head,
        int targetNodeId,
        SearchContext ctx
    )
    {
        if (head.HeadNodeId == targetNodeId)
        {
            return ([], head, null);
        }

        if (!ctx.Layout.Nodes.TryGetValue(targetNodeId, out var destNode))
        {
            return (
                null,
                null,
                new PathfindingFailure(
                    FailureKind.DestinationUnreachable,
                    $"Node-reference #{targetNodeId} not found in layout.",
                    null,
                    $"#{targetNodeId}",
                    null
                )
            );
        }

        // Use AutoRouter from current head to the target node.
        var detourCtx = ctx with
        {
            StartNodeId = head.HeadNodeId,
            Destination = new DestinationDescriptor(targetNodeId, null, null, null, DestinationKind.Node),
            WaypointSequence = [],
            AuthorizedTaxiways = null,
        };

        var (route, failure) = AutoRouter.Run(detourCtx, startOverride: head);
        if (failure is not null || route is null)
        {
            return (
                null,
                null,
                new PathfindingFailure(
                    FailureKind.DestinationUnreachable,
                    $"Cannot route to node-reference #{targetNodeId} from node {head.HeadNodeId}.",
                    null,
                    $"#{targetNodeId}",
                    null
                )
            );
        }

        var newHead = BuildHeadFromRoute(head, route);
        return (route.Segments.Select(s => s.Edge).ToList(), newHead, null);
    }

    // -----------------------------------------------------------------------
    // Variant resolution at the final taxiway segment
    // -----------------------------------------------------------------------

    /// <summary>
    /// True when any node touched by <paramref name="edges"/> is a hold-short for
    /// <paramref name="runwayId"/> (reciprocal-tolerant via <c>Contains</c>) — i.e. the
    /// walked route already reaches the destination runway and needs no variant extension.
    /// </summary>
    private static bool RouteReachesRunwayHoldShort(IReadOnlyList<DirectionalEdge> edges, string runwayId, SearchContext ctx)
    {
        foreach (var edge in edges)
        {
            if (IsRunwayHoldShort(edge.ToNodeId, runwayId, ctx) || IsRunwayHoldShort(edge.FromNodeId, runwayId, ctx))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsRunwayHoldShort(int nodeId, string runwayId, SearchContext ctx) =>
        ctx.Layout.Nodes.TryGetValue(nodeId, out var node)
        && node.Type == GroundNodeType.RunwayHoldShort
        && node.RunwayId is { } rwy
        && rwy.ToString().Contains(runwayId, StringComparison.OrdinalIgnoreCase);

    private static (List<DirectionalEdge>? Edges, PathfindingFailure? Failure) TryVariantExtension(
        PartialRoute head,
        string lastTaxiwayName,
        string destinationRunway,
        SearchContext ctx
    )
    {
        // Check if we already reached a hold-short for the destination runway.
        if (ctx.Layout.Nodes.TryGetValue(head.HeadNodeId, out var currentNode))
        {
            if (
                currentNode.Type == GroundNodeType.RunwayHoldShort
                && currentNode.RunwayId is { } currentRwyId
                && currentRwyId.Contains(destinationRunway)
            )
            {
                ctx.DiagnosticLog?.Invoke($"[v2:variant] already at hold-short for {destinationRunway}");
                return (null, null);
            }
        }

        // Find numbered variants of lastTaxiwayName that have a hold-short for the destination runway.
        var variants = FindVariantHoldShorts(ctx.Layout, lastTaxiwayName, destinationRunway);

        ctx.DiagnosticLog?.Invoke($"[v2:variant] lastTwy={lastTaxiwayName} destRwy={destinationRunway} variants={variants.Count}");

        if (variants.Count == 0)
        {
            // No variants: check for same-name hold-shorts first.
            var sameNameHs = FindSameNameHoldShorts(ctx.Layout, lastTaxiwayName, destinationRunway);
            if (sameNameHs.Count > 0)
            {
                return ExtendToNearestHoldShort(head, sameNameHs, ctx);
            }

            // No numbered variant and no same-name hold-short reaches the destination runway from
            // this taxiway. TryVariantExtension is only entered when the walk itself did NOT reach a
            // hold-short for the runway, so the runway is genuinely unreachable from the cleared
            // route — fail rather than returning a route that stops at the taxiway terminus short of
            // the runway (which would let the command succeed against a route that never gets there).
            return (
                null,
                new PathfindingFailure(
                    FailureKind.DestinationUnreachable,
                    $"Taxiway {lastTaxiwayName} does not reach runway {destinationRunway} — specify a connecting taxiway.",
                    lastTaxiwayName,
                    $"{lastTaxiwayName} → {destinationRunway}",
                    null
                )
            );
        }

        // Determine distinct variant names.
        var distinctVariants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, name) in variants)
        {
            distinctVariants.Add(name);
        }

        if (distinctVariants.Count > 1)
        {
            // Multiple numbered variants of the base taxiway serve the destination runway (e.g.
            // W1..W7 off W to rwy 30). Auto-pick the one whose hold-short is nearest the requested
            // runway's threshold — the full-length lineup connector — mirroring V1's
            // TaxiVariantResolver.PickBestVariant. Only fall back to a TransitionAmbiguous failure
            // when the threshold is unavailable (no navdata), so the controller is never asked to
            // disambiguate a resolvable clearance and we never silently guess without a reference.
            var threshold = RouteMaterialiser.ResolveRunwayThreshold(ctx.Layout.AirportId, destinationRunway);
            if (threshold is { } thresholdPos)
            {
                string bestVariant = variants[0].Name;
                double bestDist = double.MaxValue;
                foreach (var (hsNode, name) in variants)
                {
                    double dist = GeoMath.DistanceNm(hsNode.Position, thresholdPos);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestVariant = name;
                    }
                }

                var bestHsNodes = variants.Where(v => v.Name.Equals(bestVariant, StringComparison.OrdinalIgnoreCase)).Select(v => v.HsNode).ToList();
                ctx.DiagnosticLog?.Invoke(
                    $"[v2:variant] auto-picked {bestVariant} (nearest {destinationRunway} threshold) from {distinctVariants.Count} variants"
                );
                return ExtendToVariant(head, lastTaxiwayName, bestVariant, bestHsNodes, destinationRunway, ctx);
            }

            var candidateList = distinctVariants.OrderBy(s => s).ToList();
            return (
                null,
                new PathfindingFailure(
                    FailureKind.TransitionAmbiguous,
                    $"Runway {destinationRunway} is served by both {string.Join(" and ", candidateList)} from {lastTaxiwayName} — specify {string.Join(" or ", candidateList)}",
                    lastTaxiwayName,
                    $"{lastTaxiwayName} → {destinationRunway}",
                    candidateList[0]
                )
            );
        }

        // Exactly one variant: auto-extend onto it.
        string chosenVariant = distinctVariants.First();
        var variantHsNodes = variants.Where(v => v.Name.Equals(chosenVariant, StringComparison.OrdinalIgnoreCase)).Select(v => v.HsNode).ToList();

        return ExtendToVariant(head, lastTaxiwayName, chosenVariant, variantHsNodes, destinationRunway, ctx);
    }

    private static List<(GroundNode HsNode, string Name)> FindVariantHoldShorts(AirportGroundLayout layout, string baseName, string runwayId)
    {
        var result = new List<(GroundNode, string)>();
        foreach (var node in layout.Nodes.Values)
        {
            if (node.Type != GroundNodeType.RunwayHoldShort || node.RunwayId is null)
            {
                continue;
            }

            if (node.RunwayId is not { } nodeRwyId || !nodeRwyId.Contains(runwayId))
            {
                continue;
            }

            foreach (var edge in node.Edges)
            {
                string edgeName = edge.TaxiwayName;
                if (IsNumberedVariant(edgeName, baseName))
                {
                    result.Add((node, edgeName));
                    break;
                }
            }
        }

        return result;
    }

    private static List<GroundNode> FindSameNameHoldShorts(AirportGroundLayout layout, string taxiwayName, string runwayId)
    {
        var result = new List<GroundNode>();
        foreach (var node in layout.Nodes.Values)
        {
            if (node.Type != GroundNodeType.RunwayHoldShort || node.RunwayId is null)
            {
                continue;
            }

            if (node.RunwayId is not { } sameNameRwyId || !sameNameRwyId.Contains(runwayId))
            {
                continue;
            }

            foreach (var edge in node.Edges)
            {
                if (edge.TaxiwayName.Equals(taxiwayName, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(node);
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Returns true if <paramref name="candidate"/> is a numbered variant of
    /// <paramref name="baseName"/> (e.g., "W1" is a variant of "W"; "WA" is not).
    /// Numbered variants only branch off a letter-only base: "B10" is a variant of
    /// "B", not of "B1" — "B1" and "B10" are siblings under base "B". A digit-bearing
    /// base ("B1") is itself a leaf connector and has no further numbered variants, so
    /// it never matches (prevents the "B10 is a variant of B1" false positive that made
    /// a B1→runway hold-short look ambiguous against B10/B11).
    /// </summary>
    internal static bool IsNumberedVariant(string candidate, string baseName)
    {
        if (candidate.Length <= baseName.Length)
        {
            return false;
        }

        foreach (char c in baseName)
        {
            if (char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        if (!candidate.StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        for (int i = baseName.Length; i < candidate.Length; i++)
        {
            if (!char.IsAsciiDigit(candidate[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static (List<DirectionalEdge>? Edges, PathfindingFailure? Failure) ExtendToVariant(
        PartialRoute head,
        string baseTaxiway,
        string variantName,
        List<GroundNode> variantHsNodes,
        string destinationRunway,
        SearchContext ctx
    )
    {
        // Find the closest variant hold-short.
        GroundNode? targetHs = null;
        double bestDist = double.MaxValue;

        if (ctx.Layout.Nodes.TryGetValue(head.HeadNodeId, out var headNode))
        {
            foreach (var hs in variantHsNodes)
            {
                double dist = GeoMath.DistanceNm(headNode.Position, hs.Position);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    targetHs = hs;
                }
            }
        }
        else
        {
            targetHs = variantHsNodes[0];
        }

        if (targetHs is null)
        {
            return (null, null);
        }

        ctx.DiagnosticLog?.Invoke($"[v2:variant] extending {baseTaxiway}→{variantName} to hold-short #{targetHs.Id}");

        // Route from head to the variant hold-short via local search.
        var (segEdges, _, cost) = LocalSearchToJunction(head, variantName, targetHs.Id, ctx);
        if (segEdges is not null)
        {
            return (segEdges, null);
        }

        // Local search failed — fall back to AutoRouter.
        return ExtendToNearestHoldShort(head, [targetHs], ctx);
    }

    private static (List<DirectionalEdge>? Edges, PathfindingFailure? Failure) ExtendToNearestHoldShort(
        PartialRoute head,
        List<GroundNode> holdShortNodes,
        SearchContext ctx
    )
    {
        GroundNode? best = null;
        double bestDist = double.MaxValue;

        if (ctx.Layout.Nodes.TryGetValue(head.HeadNodeId, out var headNode))
        {
            foreach (var hs in holdShortNodes)
            {
                double dist = GeoMath.DistanceNm(headNode.Position, hs.Position);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = hs;
                }
            }
        }
        else
        {
            best = holdShortNodes[0];
        }

        if (best is null)
        {
            return (null, null);
        }

        var detourCtx = ctx with
        {
            StartNodeId = head.HeadNodeId,
            Destination = new DestinationDescriptor(best.Id, null, null, null, DestinationKind.Node),
            WaypointSequence = [],
            AuthorizedTaxiways = null,
        };

        var (route, _) = AutoRouter.Run(detourCtx, startOverride: head);
        if (route is null)
        {
            return (null, null);
        }

        return (route.Segments.Select(s => s.Edge).ToList(), null);
    }

    // -----------------------------------------------------------------------
    // Detour (§Decisions §1): when junction search fails, try numbered+RAMP bridge
    // -----------------------------------------------------------------------

    /// <summary>
    /// Record a mandatory-connector insertion: <paramref name="fromTaxiway"/> and
    /// <paramref name="toTaxiway"/> are consecutive cleared taxiways with no direct junction, so
    /// the detour bridged them via one or more connectors. The connector names are the distinct
    /// single-name (non-junction-arc) taxiways the detour traversed other than from/to and other
    /// than any taxiway already named in the clearance (<paramref name="tokens"/>) — only a
    /// taxiway the controller did not name counts as an inserted connector worth flagging. When
    /// the bridge uses no such taxiway, nothing is recorded.
    /// </summary>
    private static void RecordConnectorInsertion(
        List<ConnectorInsertion> insertions,
        string fromTaxiway,
        string toTaxiway,
        IReadOnlyList<DirectionalEdge> detourEdges,
        IReadOnlyList<WaypointToken> tokens
    )
    {
        var cleared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in tokens)
        {
            if (!token.IsNodeRef)
            {
                cleared.Add(token.Name);
            }
        }

        var connectors = new List<string>();
        foreach (var edge in detourEdges)
        {
            if (edge.Edge is GroundArc { TaxiwayNames.Length: >= 2 })
            {
                continue;
            }

            string name = edge.TaxiwayName;
            if (cleared.Contains(name) || connectors.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            connectors.Add(name);
        }

        if (connectors.Count > 0)
        {
            insertions.Add(new ConnectorInsertion(fromTaxiway, toTaxiway, connectors));
        }
    }

    /// <summary>
    /// Failure returned in place of a detour when resolving a segment inside a look-ahead
    /// probe. Signals the probe that this continuation would need the whole-airport detour —
    /// a strong negative signal against the candidate junction that led here.
    /// </summary>
    private static PathfindingFailure DetourSuppressedFailure(string fromTaxiway, string toTaxiway, PartialRoute head) =>
        new(
            FailureKind.TransitionInfeasible,
            $"[probe] {fromTaxiway} → {toTaxiway} would require a detour from node {head.HeadNodeId}",
            fromTaxiway,
            $"{fromTaxiway} → {toTaxiway}",
            null
        );

    private static (List<DirectionalEdge>? Edges, PartialRoute? Head, PathfindingFailure? Failure) TryDetour(
        PartialRoute head,
        string fromTaxiway,
        string toTaxiway,
        SearchContext ctx
    )
    {
        ctx.DiagnosticLog?.Invoke($"[v2:detour] attempting detour {fromTaxiway}→{toTaxiway} from head={head.HeadNodeId}");

        // Find entry nodes onto toTaxiway — any node on that taxiway.
        var toTaxiwayNodes = ctx.Layout.GetNodesOnTaxiway(toTaxiway);
        if (toTaxiwayNodes.Count == 0)
        {
            return (
                null,
                null,
                new PathfindingFailure(
                    FailureKind.TaxiwayNotConnected,
                    $"Cannot find taxiway {toTaxiway} in layout",
                    toTaxiway,
                    $"{fromTaxiway} → {toTaxiway}",
                    null
                )
            );
        }

        // Route from head to the nearest node on toTaxiway via a bounded AutoRouter that inherits the
        // authorized set — so numbered connectors and RAMP edges are preferred over unnamed letter taxiways.
        GroundNode? bestEntry = null;
        TaxiRoute? bestRoute = null;

        foreach (var entryNode in toTaxiwayNodes)
        {
            if (entryNode.Id == head.HeadNodeId)
            {
                continue;
            }

            var detourCtx = BuildDetourContext(ctx, head.HeadNodeId, entryNode.Id);
            var (route, _) = RunBoundedDetour(detourCtx, head);

            if (route is not null)
            {
                if (bestRoute is null || (route.TotalDistanceNm < bestRoute.TotalDistanceNm))
                {
                    bestRoute = route;
                    bestEntry = entryNode;
                }
            }
        }

        if (bestRoute is null)
        {
            return (
                null,
                null,
                new PathfindingFailure(
                    FailureKind.TransitionInfeasible,
                    $"No valid path from {fromTaxiway} to {toTaxiway} — transition infeasible from node {head.HeadNodeId}",
                    fromTaxiway,
                    $"{fromTaxiway} → {toTaxiway}",
                    null
                )
            );
        }

        ctx.DiagnosticLog?.Invoke($"[v2:detour] found detour {fromTaxiway}→{toTaxiway} via #{bestEntry!.Id} segs={bestRoute.Segments.Count}");

        var newHead = BuildHeadFromRoute(head, bestRoute);
        return (bestRoute.Segments.Select(s => s.Edge).ToList(), newHead, null);
    }

    /// <summary>
    /// Build a detour SearchContext for bridging two cleared taxiways that have no direct junction.
    /// Inherits the original <see cref="SearchContext.AuthorizedTaxiways"/> so the cost function still
    /// prefers numbered connectors and RAMP edges over unnamed letter taxiways (the soft policy:
    /// an unauthorized letter taxiway is penalized but usable as a last resort, then surfaced in the
    /// connector notification — never silently free, never hard-failing a resolvable clearance).
    /// </summary>
    private static SearchContext BuildDetourContext(SearchContext ctx, int fromNodeId, int toNodeId)
    {
        return ctx with
        {
            StartNodeId = fromNodeId,
            Destination = new DestinationDescriptor(toNodeId, null, null, null, DestinationKind.Node),
            WaypointSequence = [],
        };
    }

    /// <summary>
    /// Run a bounded AutoRouter detour search. Passes the prior segment's <paramref name="priorHead"/>
    /// as the AutoRouter start so admissibility fires on the detour's first edge — without this,
    /// the detour can pick a first edge that U-turns against the aircraft's existing heading.
    /// </summary>
    private static (TaxiRoute? Route, PathfindingFailure? Failure) RunBoundedDetour(SearchContext ctx, PartialRoute priorHead)
    {
        return AutoRouter.Run(ctx, startOverride: priorHead, maxExpansions: MaxDetourExpansions);
    }

    // -----------------------------------------------------------------------
    // Parking / spot extension
    // -----------------------------------------------------------------------

    private static (List<DirectionalEdge>? Edges, PathfindingFailure? Failure) ExtendToDestination(
        PartialRoute head,
        int destinationNodeId,
        SearchContext ctx
    )
    {
        ctx.DiagnosticLog?.Invoke($"[v2:extend] extending to destination #{destinationNodeId} from head={head.HeadNodeId}");

        var extCtx = ctx with
        {
            StartNodeId = head.HeadNodeId,
            Destination = new DestinationDescriptor(
                destinationNodeId,
                null,
                ctx.Destination.ParkingName,
                ctx.Destination.SpotName,
                ctx.Destination.Kind
            ),
            WaypointSequence = [],
            AuthorizedTaxiways = null,
        };

        var (route, failure) = AutoRouter.Run(extCtx, startOverride: head);
        if (failure is not null || route is null)
        {
            return (
                null,
                new PathfindingFailure(
                    FailureKind.DestinationUnreachable,
                    $"Cannot reach destination from end of taxi path (node {head.HeadNodeId})",
                    null,
                    null,
                    null
                )
            );
        }

        return (route.Segments.Select(s => s.Edge).ToList(), null);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Build a <see cref="PartialRoute"/> head that reflects the state after traversing
    /// <paramref name="route"/> from <paramref name="startHead"/>.
    /// </summary>
    private static PartialRoute BuildHeadFromRoute(PartialRoute startHead, TaxiRoute route)
    {
        if (route.Segments.Count == 0)
        {
            return startHead;
        }

        var current = startHead;
        foreach (var seg in route.Segments)
        {
            current = current with
            {
                HeadNodeId = seg.ToNodeId,
                ArrivalBearing = seg.Edge.ArrivalBearing,
                LastEdge = seg.Edge.Edge,
                LastTaxiwayName = seg.TaxiwayName,
                Previous = current,
                Depth = current.Depth + 1,
                AccumulatedCost = current.AccumulatedCost + seg.Edge.DistanceNm,
                VisitedNodeIds = current.VisitedNodeIds.Add(seg.ToNodeId),
            };
        }

        return current;
    }
}
