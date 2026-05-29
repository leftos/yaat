namespace Yaat.Sim.Data.Airport.V2;

/// <summary>
/// Auto-mode A* driver. Runs a flat best-first search over the full layout from start to
/// destination, constrained by <see cref="SearchContext.AuthorizedTaxiways"/> (soft penalty)
/// and <see cref="GeometricAdmissibility"/> (hard gate). Returns a flat edge sequence for
/// <see cref="RouteMaterialiser"/> or a structured <see cref="PathfindingFailure"/>.
/// </summary>
public static class AutoRouter
{
    /// <summary>Maximum node-expansions before returning <see cref="FailureKind.SearchExhausted"/>.</summary>
    private const int MaxExpansions = 200_000;

    /// <summary>
    /// Run A* from <see cref="SearchContext.StartNodeId"/> to the destination described
    /// in <see cref="SearchContext.Destination"/>. Returns either the materialised route
    /// or a structured failure.
    /// </summary>
    /// <param name="startOverride">
    /// Optional pre-built starting <see cref="PartialRoute"/>. When provided, A* begins
    /// from this route's state — including its <c>LastEdge</c> and <c>ArrivalBearing</c> —
    /// so geometric admissibility fires on the first expanded edge. Used by
    /// <see cref="SegmentExpander"/>'s detour fallback to inherit the prior segment's
    /// heading. When null, A* starts cold from <see cref="SearchContext.StartNodeId"/>
    /// with no arrival-bearing constraint (the first edge is admitted unconditionally).
    /// </param>
    /// <param name="maxExpansions">
    /// Node-expansion ceiling before returning <see cref="FailureKind.SearchExhausted"/>. Defaults to
    /// the full-search cap; bounded callers (e.g. <c>SegmentExpander</c>'s detour) pass a smaller value.
    /// </param>
    public static (TaxiRoute? Route, PathfindingFailure? Failure) Run(
        SearchContext ctx,
        PartialRoute? startOverride = null,
        int maxExpansions = MaxExpansions
    )
    {
        if (ctx.Destination.Kind == DestinationKind.EndOfLastTaxiway)
        {
            return (
                null,
                new PathfindingFailure(
                    FailureKind.DestinationUnreachable,
                    "AutoRouter cannot route to EndOfLastTaxiway — use SegmentExpander for explicit paths.",
                    null,
                    null,
                    null
                )
            );
        }

        if (!ctx.Layout.Nodes.TryGetValue(ctx.StartNodeId, out var startNode))
        {
            return (
                null,
                new PathfindingFailure(FailureKind.StartNodeUnreachable, $"Start node {ctx.StartNodeId} not found in layout.", null, null, null)
            );
        }

        GroundNode? destinationNode = ResolveDestinationNode(ctx);

        // For runway destinations, find the full-length lineup hold-short.
        if (ctx.Destination.Kind == DestinationKind.Runway)
        {
            if (ctx.Destination.RunwayId is null)
            {
                return (null, new PathfindingFailure(FailureKind.DestinationUnreachable, "Runway destination has no RunwayId.", null, null, null));
            }

            var holdShortNodes = ctx.Layout.GetRunwayHoldShortNodes(ctx.Destination.RunwayId);
            if (holdShortNodes.Count == 0)
            {
                return (
                    null,
                    new PathfindingFailure(
                        FailureKind.DestinationUnreachable,
                        $"No hold-short nodes found for runway {ctx.Destination.RunwayId}.",
                        null,
                        null,
                        ctx.Destination.RunwayId
                    )
                );
            }

            destinationNode = RouteMaterialiser.FindFullLengthLineupHoldShort(ctx.Layout, startNode, ctx.Destination.RunwayId, holdShortNodes);
        }

        if (destinationNode is null)
        {
            return (
                null,
                new PathfindingFailure(
                    FailureKind.DestinationUnreachable,
                    $"Destination node could not be resolved (kind={ctx.Destination.Kind}).",
                    null,
                    null,
                    null
                )
            );
        }

        // Trivial case: start is already at the destination.
        if (ctx.StartNodeId == destinationNode.Id)
        {
            ctx.DiagnosticLog?.Invoke($"[v2:auto] trivial route — start == destination node {ctx.StartNodeId}");
            var emptyRoute = RouteMaterialiser.Materialise([], ctx, []);
            return (emptyRoute, null);
        }

        var result = RunAstar(ctx, startNode, destinationNode, startOverride, maxExpansions);
        return result;
    }

    private static (TaxiRoute? Route, PathfindingFailure? Failure) RunAstar(
        SearchContext ctx,
        GroundNode startNode,
        GroundNode destinationNode,
        PartialRoute? startOverride,
        int maxExpansions
    )
    {
        // Priority queue: (PartialRoute, fScore). .NET 6+ PriorityQueue<TElement, TPriority>.
        var openSet = new PriorityQueue<PartialRoute, double>();

        // Best-g-score per (node, arrival-bearing-bucket) state. When a state is re-encountered with
        // a g-score >= the recorded best, the duplicate is skipped. Keying by node id alone would be
        // unsound: onward-edge admissibility depends on arrival bearing, so a cheaper arrival with a
        // dead-end bearing must not suppress the only admissible (different-bearing) arrival
        // (see GeometricAdmissibility.PruningStateKey). The heuristic is bearing-independent, so
        // A* optimality is preserved within the (node, bucket) state space.
        var bestGScore = new Dictionary<(int Node, int Bucket), double>();

        int expansions = 0;
        PartialRoute? deepestViable = null;

        // When startOverride is provided, inherit its LastEdge + ArrivalBearing so the first
        // expansion goes through GeometricAdmissibility against the prior heading. Otherwise
        // the search starts cold (admissibility skips the first edge).
        var startRoute = startOverride ?? PartialRoute.StartAt(ctx.StartNodeId);
        double startHeuristic = RouteCostFunction.Heuristic(startNode, destinationNode);
        bestGScore[GeometricAdmissibility.PruningStateKey(startRoute.HeadNodeId, startRoute.ArrivalBearing)] = startRoute.AccumulatedCost;
        openSet.Enqueue(startRoute, startRoute.AccumulatedCost + startHeuristic);

        ctx.DiagnosticLog?.Invoke(
            $"[v2:auto] start node={startRoute.HeadNodeId}  dest node={destinationNode.Id}  h0={startHeuristic:F3}  arrival={startRoute.ArrivalBearing:F1}  hasPrior={startRoute.LastEdge is not null}"
        );

        while (openSet.Count > 0)
        {
            var current = openSet.Dequeue();
            expansions++;

            if (expansions > maxExpansions)
            {
                ctx.DiagnosticLog?.Invoke(
                    $"[v2:auto] FAIL reason=SearchExhausted  expansions={expansions}  deepest_depth={deepestViable?.Depth ?? 0}"
                );

                return (
                    null,
                    new PathfindingFailure(
                        FailureKind.SearchExhausted,
                        $"Route search exceeded {maxExpansions} expansions near node {current.HeadNodeId} — possible layout data gap.",
                        null,
                        null,
                        null
                    )
                );
            }

            // Skip stale queue entries: a cheaper path to this (node, bearing-bucket) state was already expanded.
            if (
                bestGScore.TryGetValue(GeometricAdmissibility.PruningStateKey(current.HeadNodeId, current.ArrivalBearing), out double recordedBest)
                && (current.AccumulatedCost > recordedBest + 1e-9)
            )
            {
                continue;
            }

            ctx.DiagnosticLog?.Invoke(
                $"[v2:auto] pop f={current.AccumulatedCost + RouteCostFunction.Heuristic(ctx.Layout.Nodes[current.HeadNodeId], destinationNode):F3}  node={current.HeadNodeId}  depth={current.Depth}  cost={current.AccumulatedCost:F3}"
            );

            // Destination check.
            if (IsAtDestination(current.HeadNodeId, destinationNode, ctx))
            {
                int baseDepth = startOverride?.Depth ?? 0;
                int newEdgeCount = current.Depth - baseDepth;
                ctx.DiagnosticLog?.Invoke(
                    $"[v2:auto] SUCCESS edges={newEdgeCount}  total_cost={current.AccumulatedCost:F3}  expansions={expansions}"
                );

                var edges = current.MaterialiseEdges(baseDepth);
                var route = RouteMaterialiser.Materialise(edges, ctx, []);
                return (route, null);
            }

            // Track deepest viable partial route for SearchExhausted diagnostics.
            if (deepestViable is null || (current.Depth > deepestViable.Depth))
            {
                deepestViable = current;
            }

            if (!ctx.Layout.Nodes.TryGetValue(current.HeadNodeId, out var headNode))
            {
                continue;
            }

            int admitted = 0;
            int rejected = 0;

            foreach (var edge in headNode.Edges)
            {
                GroundNode nextNode = edge.OtherNode(headNode);

                // Skip already-visited nodes within this path (prevents cycles in the path).
                if (current.VisitedNodeIds.Contains(nextNode.Id))
                {
                    rejected++;
                    continue;
                }

                // Geometric admissibility gate.
                if (!GeometricAdmissibility.IsAdmissible(current, edge, nextNode, ctx.Category))
                {
                    rejected++;
                    continue;
                }

                double incrementalCost = RouteCostFunction.IncrementalCost(current, edge, nextNode, ctx);
                double newGScore = current.AccumulatedCost + incrementalCost;

                // Zero-distance no-op edges carry bogus inherited bearings — propagate the
                // current arrival bearing through them so the next admissibility check (and the
                // closed-set key below) sees the real heading.
                double arrivalBearing = GeometricAdmissibility.IsNoOpEdge(edge)
                    ? current.ArrivalBearing
                    : GeometricAdmissibility.GetArrivalBearing(edge, headNode, nextNode);

                // Skip if we already have a cheaper or equal path to this (node, bearing-bucket) state.
                var nextKey = GeometricAdmissibility.PruningStateKey(nextNode.Id, arrivalBearing);
                if (bestGScore.TryGetValue(nextKey, out double existingBest) && (newGScore >= existingBest - 1e-9))
                {
                    rejected++;
                    continue;
                }

                admitted++;
                bestGScore[nextKey] = newGScore;

                string taxiwayName = RouteCostFunction.ResolveTaxiwayName(edge, current.HeadNodeId);

                var extended = current with
                {
                    HeadNodeId = nextNode.Id,
                    ArrivalBearing = arrivalBearing,
                    LastEdge = edge,
                    LastTaxiwayName = taxiwayName,
                    Previous = current,
                    Depth = current.Depth + 1,
                    AccumulatedCost = newGScore,
                    VisitedNodeIds = current.VisitedNodeIds.Add(nextNode.Id),
                };

                double heuristic = RouteCostFunction.Heuristic(nextNode, destinationNode);
                double fScore = newGScore + heuristic;

                // Encode depth as a tiny fractional tie-breaker so shallower routes
                // are preferred among equal f-scores, keeping the queue deterministic.
                double priority = fScore - (extended.Depth * 1e-9);

                openSet.Enqueue(extended, priority);
            }

            ctx.DiagnosticLog?.Invoke($"[v2:auto] EXPAND admitted={admitted} rejected={rejected}");
        }

        ctx.DiagnosticLog?.Invoke($"[v2:auto] FAIL reason=DestinationUnreachable  expansions={expansions}");

        return (
            null,
            new PathfindingFailure(
                FailureKind.DestinationUnreachable,
                $"No route found from node {ctx.StartNodeId} to destination (node {destinationNode.Id}) — graph may be disconnected.",
                null,
                null,
                null
            )
        );
    }

    /// <summary>
    /// True when <paramref name="nodeId"/> satisfies the destination for this search.
    /// For runway destinations: any <see cref="GroundNodeType.RunwayHoldShort"/> matching the runway.
    /// For all others: exact node-ID match against <paramref name="destinationNode"/>.
    /// </summary>
    private static bool IsAtDestination(int nodeId, GroundNode destinationNode, SearchContext ctx) => nodeId == destinationNode.Id;

    /// <summary>
    /// Resolve the target <see cref="GroundNode"/> from the context.
    /// Returns null for runway destinations (handled separately via hold-short lookup)
    /// and when the target node ID is not present in the layout.
    /// </summary>
    private static GroundNode? ResolveDestinationNode(SearchContext ctx)
    {
        if (ctx.Destination.TargetNodeId is { } id && ctx.Layout.Nodes.TryGetValue(id, out var node))
        {
            return node;
        }

        return null;
    }
}
