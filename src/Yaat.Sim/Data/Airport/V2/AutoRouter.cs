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
    public static (TaxiRoute? Route, PathfindingFailure? Failure) Run(SearchContext ctx)
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
            var emptyRoute = RouteMaterialiser.Materialise([], ctx);
            return (emptyRoute, null);
        }

        var result = RunAstar(ctx, startNode, destinationNode);
        return result;
    }

    private static (TaxiRoute? Route, PathfindingFailure? Failure) RunAstar(SearchContext ctx, GroundNode startNode, GroundNode destinationNode)
    {
        // Priority queue: (PartialRoute, fScore). .NET 6+ PriorityQueue<TElement, TPriority>.
        var openSet = new PriorityQueue<PartialRoute, double>();

        // Global best-g-score per node. When a node is re-encountered with a g-score >= the
        // recorded best, the duplicate is skipped. This reduces exponential state explosion
        // from the per-path visited set without sacrificing optimality (A* with consistent
        // heuristic finds the optimal path first).
        var bestGScore = new Dictionary<int, double>();

        int expansions = 0;
        PartialRoute? deepestViable = null;

        var startRoute = PartialRoute.StartAt(ctx.StartNodeId);
        double startHeuristic = RouteCostFunction.Heuristic(startNode, destinationNode);
        bestGScore[ctx.StartNodeId] = 0.0;
        openSet.Enqueue(startRoute, startHeuristic);

        ctx.DiagnosticLog?.Invoke($"[v2:auto] start node={ctx.StartNodeId}  dest node={destinationNode.Id}  h0={startHeuristic:F3}");

        while (openSet.Count > 0)
        {
            var current = openSet.Dequeue();
            expansions++;

            if (expansions > MaxExpansions)
            {
                ctx.DiagnosticLog?.Invoke(
                    $"[v2:auto] FAIL reason=SearchExhausted  expansions={expansions}  deepest_depth={deepestViable?.Depth ?? 0}"
                );

                return (
                    null,
                    new PathfindingFailure(
                        FailureKind.SearchExhausted,
                        $"Route search exceeded {MaxExpansions} expansions near node {current.HeadNodeId} — possible layout data gap.",
                        null,
                        null,
                        null
                    )
                );
            }

            // Skip stale queue entries: a cheaper path to this node was already expanded.
            if (bestGScore.TryGetValue(current.HeadNodeId, out double recordedBest) && (current.AccumulatedCost > recordedBest + 1e-9))
            {
                continue;
            }

            ctx.DiagnosticLog?.Invoke(
                $"[v2:auto] pop f={current.AccumulatedCost + RouteCostFunction.Heuristic(ctx.Layout.Nodes[current.HeadNodeId], destinationNode):F3}  node={current.HeadNodeId}  depth={current.Depth}  cost={current.AccumulatedCost:F3}"
            );

            // Destination check.
            if (IsAtDestination(current.HeadNodeId, destinationNode, ctx))
            {
                ctx.DiagnosticLog?.Invoke(
                    $"[v2:auto] SUCCESS edges={current.Depth}  total_cost={current.AccumulatedCost:F3}  expansions={expansions}"
                );

                var edges = current.MaterialiseEdges();
                var route = RouteMaterialiser.Materialise(edges, ctx);
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

                // Skip if we already have a cheaper or equal path to nextNode.
                if (bestGScore.TryGetValue(nextNode.Id, out double existingBest) && (newGScore >= existingBest - 1e-9))
                {
                    rejected++;
                    continue;
                }

                admitted++;
                bestGScore[nextNode.Id] = newGScore;

                double arrivalBearing = GeometricAdmissibility.GetArrivalBearing(edge, headNode, nextNode);
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
