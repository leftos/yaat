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

        var edges = new List<DirectionalEdge>();
        var head = PartialRoute.StartAt(ctx.StartNodeId);

        // Walk each consecutive waypoint pair.
        for (int i = 0; i < resolvedWaypoints.Count; i++)
        {
            var current = resolvedWaypoints[i];

            if (i + 1 < resolvedWaypoints.Count)
            {
                // Segment from current taxiway/node to the next.
                var next = resolvedWaypoints[i + 1];
                var (segEdges, newHead, failure) = ExpandSegment(head, current, next, ctx);
                if (failure is not null)
                {
                    return (null, failure);
                }

                edges.AddRange(segEdges!);

                // Reset VisitedNodeIds to just the current head node before entering the next segment.
                // This allows routes that intentionally revisit a taxiway (e.g. A E B B3 A B1) to
                // use nodes already visited in a prior segment. Cycle prevention within a single
                // segment's local search is preserved because each local search starts from the
                // freshly-reset head.
                head = newHead! with
                {
                    VisitedNodeIds = ImmutableHashSet<int>.Empty.Add(newHead!.HeadNodeId),
                };
            }
            else
            {
                // Last waypoint: walk to natural terminus of the taxiway (or the node itself for node-refs).
                var (segEdges, newHead, failure) = ExpandLastWaypoint(head, current, ctx);
                if (failure is not null)
                {
                    return (null, failure);
                }

                edges.AddRange(segEdges!);
                head = newHead!;
            }
        }

        // Variant resolution: if destination is a runway and the last named taxiway has
        // numbered variants that reach it, extend automatically.
        if (ctx.Destination.Kind == DestinationKind.Runway && ctx.Destination.RunwayId is not null)
        {
            var lastWaypoint = resolvedWaypoints[^1];
            if (!lastWaypoint.IsNodeRef)
            {
                var (varEdges, varFailure) = TryVariantExtension(head, lastWaypoint.Name, ctx.Destination.RunwayId, ctx);
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

        // Build context adjusted for EndOfLastTaxiway so materialiser truncates correctly.
        var materCtx = ctx.Destination.Kind == DestinationKind.EndOfLastTaxiway ? ctx : ctx;
        var route = RouteMaterialiser.Materialise(edges, materCtx);
        return (route, null);
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

        // Named taxiway → named taxiway: find junction nodes and pick best.
        return RouteNamedToNamed(head, current.Name, next.Name, ctx);
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

        // Named taxiway at end: walk to natural terminus.
        return WalkToNaturalTerminus(head, waypoint.Name, ctx);
    }

    // -----------------------------------------------------------------------
    // Named taxiway to named taxiway
    // -----------------------------------------------------------------------

    private static (List<DirectionalEdge>? Edges, PartialRoute? Head, PathfindingFailure? Failure) RouteNamedToNamed(
        PartialRoute head,
        string fromTaxiway,
        string toTaxiway,
        SearchContext ctx
    )
    {
        // Find all junction candidates: nodes on fromTaxiway with at least one edge onto toTaxiway.
        var junctionCandidates = FindJunctionCandidates(ctx.Layout, fromTaxiway, toTaxiway);

        ctx.DiagnosticLog?.Invoke($"[v2:segment] twy={fromTaxiway}→{toTaxiway} head={head.HeadNodeId} junctions={junctionCandidates.Count}");

        if (junctionCandidates.Count == 0)
        {
            // No direct junction: attempt a detour.
            return TryDetour(head, fromTaxiway, toTaxiway, ctx);
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

            if (cost < bestCost)
            {
                bestCost = cost;
                bestEdges = segEdges;
                bestHead = segHead;
            }
        }

        if (bestEdges is null)
        {
            // All junction candidates failed: attempt detour.
            return TryDetour(head, fromTaxiway, toTaxiway, ctx);
        }

        ctx.DiagnosticLog?.Invoke(
            $"[v2:committed] twy={fromTaxiway}→{toTaxiway} via={bestHead!.HeadNodeId} edges={bestEdges.Count} cost={bestCost:F3}"
        );

        return (bestEdges, bestHead, null);
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
            return ([], startHead, 0.0);
        }

        if (!ctx.Layout.Nodes.TryGetValue(junctionNodeId, out var destNode))
        {
            return (null, null, double.MaxValue);
        }

        var openSet = new PriorityQueue<PartialRoute, double>();
        var bestGScore = new Dictionary<int, double>();
        int expansions = 0;

        double h0 = ctx.Layout.Nodes.TryGetValue(startHead.HeadNodeId, out var sn) ? RouteCostFunction.Heuristic(sn, destNode) : 0.0;

        openSet.Enqueue(startHead, startHead.AccumulatedCost + h0);
        bestGScore[startHead.HeadNodeId] = startHead.AccumulatedCost;

        while (openSet.Count > 0)
        {
            var current = openSet.Dequeue();
            expansions++;

            if (expansions > MaxLocalExpansions)
            {
                break;
            }

            if (bestGScore.TryGetValue(current.HeadNodeId, out double recorded) && (current.AccumulatedCost > recorded + 1e-9))
            {
                continue;
            }

            if (current.HeadNodeId == junctionNodeId)
            {
                // Found the junction: extract edges accumulated since the start.
                var edges = ExtractEdgesSince(current, startHead.HeadNodeId, startHead.Depth);
                return (edges, current, current.AccumulatedCost - startHead.AccumulatedCost);
            }

            if (!ctx.Layout.Nodes.TryGetValue(current.HeadNodeId, out var headNode))
            {
                continue;
            }

            foreach (var edge in headNode.Edges)
            {
                var nextNode = edge.OtherNode(headNode);

                if (current.VisitedNodeIds.Contains(nextNode.Id))
                {
                    continue;
                }

                // Constrain to edges on the current taxiway, OR edges that connect to the junction node directly.
                if (!edge.MatchesTaxiway(taxiwayName) && nextNode.Id != junctionNodeId)
                {
                    continue;
                }

                if (!GeometricAdmissibility.IsAdmissible(current, edge, nextNode, ctx.Category))
                {
                    ctx.DiagnosticLog?.Invoke($"[v2:junction] {taxiwayName}  node={nextNode.Id}  headingDelta=REJECTED");
                    continue;
                }

                double incrementalCost = RouteCostFunction.IncrementalCost(current, edge, nextNode, ctx);

                // Apply direction-reversal penalty for SegmentExpander local searches (§Decisions §7).
                // When the edge bearing is more than 90° away from the overall segment direction
                // (head → destination), treat it as a temporary reversal.
                incrementalCost += ComputeDirectionReversalPenalty(current, edge, headNode, nextNode, destNode);

                double newGScore = current.AccumulatedCost + incrementalCost;

                if (bestGScore.TryGetValue(nextNode.Id, out double existing) && (newGScore >= existing - 1e-9))
                {
                    continue;
                }

                bestGScore[nextNode.Id] = newGScore;

                double arrival = GeometricAdmissibility.GetArrivalBearing(edge, headNode, nextNode);
                string twyName = RouteCostFunction.ResolveTaxiwayName(edge, current.HeadNodeId);

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
            }
        }

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
        SearchContext ctx
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

            // Find the best admissible forward step on this taxiway.
            IGroundEdge? bestEdge = null;
            GroundNode? bestNext = null;
            double bestCost = double.MaxValue;

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
                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestEdge = edge;
                    bestNext = nextNode;
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

            // No extension possible — route ends at natural terminus.
            return (null, null);
        }

        // Determine distinct variant names.
        var distinctVariants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, name) in variants)
        {
            distinctVariants.Add(name);
        }

        if (distinctVariants.Count > 1)
        {
            // Multiple variants serving the same runway — ambiguous.
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
    /// </summary>
    internal static bool IsNumberedVariant(string candidate, string baseName)
    {
        if (candidate.Length <= baseName.Length)
        {
            return false;
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

        // Build a detour context that allows only numbered connectors and RAMP edges.
        // We try to route from head to the nearest node on toTaxiway using a permissive AutoRouter.
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
    /// Build a detour SearchContext that permits only numbered taxiways and RAMP edges.
    /// </summary>
    private static SearchContext BuildDetourContext(SearchContext ctx, int fromNodeId, int toNodeId)
    {
        return ctx with
        {
            StartNodeId = fromNodeId,
            Destination = new DestinationDescriptor(toNodeId, null, null, null, DestinationKind.Node),
            WaypointSequence = [],
            AuthorizedTaxiways = null,
        };
    }

    /// <summary>
    /// Run a bounded AutoRouter detour search. Passes the prior segment's <paramref name="priorHead"/>
    /// as the AutoRouter start so admissibility fires on the detour's first edge — without this,
    /// the detour can pick a first edge that U-turns against the aircraft's existing heading.
    /// </summary>
    private static (TaxiRoute? Route, PathfindingFailure? Failure) RunBoundedDetour(SearchContext ctx, PartialRoute priorHead)
    {
        return AutoRouter.Run(ctx, startOverride: priorHead);
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
