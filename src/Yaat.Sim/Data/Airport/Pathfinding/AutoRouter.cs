namespace Yaat.Sim.Data.Airport.Pathfinding;

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
        var (route, failure, _) = RunWithCost(ctx, startOverride, maxExpansions);
        return (route, failure);
    }

    /// <summary>
    /// <see cref="Run"/> plus the search's own accumulated cost of the returned route beyond
    /// <paramref name="startOverride"/> — every <see cref="RouteCostFunction.IncrementalCost"/> term
    /// (distance, turns, transitions, crossings, centerline multiplier), not just its length — so a caller
    /// scoring this route against other cost-function tails compares like with like. 0 when no route.
    /// </summary>
    public static (TaxiRoute? Route, PathfindingFailure? Failure, double Cost) RunWithCost(
        SearchContext ctx,
        PartialRoute? startOverride,
        int maxExpansions
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
                ),
                0.0
            );
        }

        if (!ctx.Layout.Nodes.TryGetValue(ctx.StartNodeId, out var startNode))
        {
            return (
                null,
                new PathfindingFailure(FailureKind.StartNodeUnreachable, $"Start node {ctx.StartNodeId} not found in layout.", null, null, null),
                0.0
            );
        }

        GroundNode? destinationNode = ResolveDestinationNode(ctx);

        // For runway destinations, find the full-length lineup hold-short.
        if (ctx.Destination.Kind == DestinationKind.Runway)
        {
            if (ctx.Destination.RunwayId is null)
            {
                return (
                    null,
                    new PathfindingFailure(FailureKind.DestinationUnreachable, "Runway destination has no RunwayId.", null, null, null),
                    0.0
                );
            }

            var holdShortNodes = ctx.Layout.GetRunwayHoldShortNodes(ctx.Destination.RunwayId);
            if (holdShortNodes.Count == 0)
            {
                return (
                    null,
                    new PathfindingFailure(
                        FailureKind.DestinationUnreachable,
                        $"No hold-short nodes found for runway {RunwayIdentifier.ToDisplayDesignator(ctx.Destination.RunwayId ?? "")}.",
                        null,
                        null,
                        ctx.Destination.RunwayId
                    ),
                    0.0
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
                ),
                0.0
            );
        }

        // Trivial case: start is already at the destination.
        if (ctx.StartNodeId == destinationNode.Id)
        {
            ctx.DiagnosticLog?.Invoke($"[auto] trivial route — start == destination node {ctx.StartNodeId}");
            var emptyRoute = RouteMaterialiser.Materialise([], ctx, []);
            return (emptyRoute, null, 0.0);
        }

        // A returned path may use an uncleared runway as a same-side shortcut (on at one exit, off
        // at the next) — not a crossing. That can only be judged on the whole path, and judging it
        // inside the A* would poison the (node, bearing-bucket) closed set with path-dependent dead
        // ends. So: run, validate, and on a violation re-run with that run's centerline edges banned
        // outright, so the next attempt finds the legal path instead of inheriting poisoned states.
        HashSet<(int, int)>? bannedMoves = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var result = RunAstar(ctx, startNode, destinationNode, startOverride, maxExpansions, bannedMoves);
            if (result.Route is null || !ctx.HasSameSideCenterlineRun(result.Route.Segments.Select(s => s.Edge).ToList()))
            {
                return result;
            }

            bannedMoves ??= [];
            int bannedBefore = bannedMoves.Count;
            foreach (var seg in result.Route.Segments)
            {
                if (seg.Edge.Edge.IsRunwayCenterline)
                {
                    bannedMoves.Add((seg.FromNodeId, seg.ToNodeId));
                    bannedMoves.Add((seg.ToNodeId, seg.FromNodeId));
                }
            }

            ctx.DiagnosticLog?.Invoke(
                $"[auto] path travels along an uncleared runway same-side; retrying with {bannedMoves.Count} banned centerline moves"
            );
            if (bannedMoves.Count == bannedBefore)
            {
                // Nothing new to ban — the violation cannot be excised; fail rather than loop.
                break;
            }
        }

        return (
            null,
            new PathfindingFailure(
                FailureKind.DestinationUnreachable,
                "No route to the destination without taxiing along a runway not in the clearance.",
                null,
                null,
                null
            ),
            0.0
        );
    }

    private static (TaxiRoute? Route, PathfindingFailure? Failure, double Cost) RunAstar(
        SearchContext ctx,
        GroundNode startNode,
        GroundNode destinationNode,
        PartialRoute? startOverride,
        int maxExpansions,
        HashSet<(int From, int To)>? bannedMoves
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
        var bestGScore = new Dictionary<(int Node, int Bucket, string Taxiway), double>();

        int expansions = 0;
        PartialRoute? deepestViable = null;

        // When startOverride is provided, inherit its LastEdge + ArrivalBearing so the first
        // expansion goes through GeometricAdmissibility against the prior heading. Otherwise
        // the search starts cold (admissibility skips the first edge).
        var startRoute = startOverride ?? PartialRoute.StartAt(ctx.StartNodeId);
        double startHeuristic = RouteCostFunction.Heuristic(startNode, destinationNode);
        bestGScore[GeometricAdmissibility.PruningStateKey(startRoute.HeadNodeId, startRoute.ArrivalBearing, startRoute.LastTaxiwayName)] =
            startRoute.AccumulatedCost;
        openSet.Enqueue(startRoute, startRoute.AccumulatedCost + startHeuristic);

        ctx.DiagnosticLog?.Invoke(
            $"[auto] start node={startRoute.HeadNodeId}  dest node={destinationNode.Id}  h0={startHeuristic:F3}  arrival={startRoute.ArrivalBearing:F1}  hasPrior={startRoute.LastEdge is not null}"
        );

        while (openSet.Count > 0)
        {
            var current = openSet.Dequeue();
            expansions++;

            if (expansions > maxExpansions)
            {
                ctx.DiagnosticLog?.Invoke($"[auto] FAIL reason=SearchExhausted  expansions={expansions}  deepest_depth={deepestViable?.Depth ?? 0}");

                return (
                    null,
                    new PathfindingFailure(
                        FailureKind.SearchExhausted,
                        $"Route search exceeded {maxExpansions} expansions near node {current.HeadNodeId} — possible layout data gap.",
                        null,
                        null,
                        null
                    ),
                    0.0
                );
            }

            // Skip stale queue entries: a cheaper path to this (node, bearing-bucket) state was already expanded.
            if (
                bestGScore.TryGetValue(
                    GeometricAdmissibility.PruningStateKey(current.HeadNodeId, current.ArrivalBearing, current.LastTaxiwayName),
                    out double recordedBest
                ) && (current.AccumulatedCost > recordedBest + 1e-9)
            )
            {
                continue;
            }

            ctx.DiagnosticLog?.Invoke(
                $"[auto] pop f={current.AccumulatedCost + RouteCostFunction.Heuristic(ctx.Layout.Nodes[current.HeadNodeId], destinationNode):F3}  node={current.HeadNodeId}  depth={current.Depth}  cost={current.AccumulatedCost:F3}"
            );

            // Destination check.
            if (IsAtDestination(current.HeadNodeId, destinationNode, ctx))
            {
                int baseDepth = startOverride?.Depth ?? 0;
                int newEdgeCount = current.Depth - baseDepth;
                ctx.DiagnosticLog?.Invoke($"[auto] SUCCESS edges={newEdgeCount}  total_cost={current.AccumulatedCost:F3}  expansions={expansions}");

                var edges = current.MaterialiseEdges(baseDepth);
                var route = RouteMaterialiser.Materialise(edges, ctx, []);
                return (route, null, current.AccumulatedCost - startRoute.AccumulatedCost);
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

                // Pass-1 hard exclusion of ARTCC-avoided taxiways (auto routes only — AvoidMode is
                // Off for explicit/named-taxiway searches). When this pass finds no route,
                // TaxiPathfinder re-runs with AvoidMode flipped to SoftPenalty so a destination only
                // reachable through an avoided taxiway still resolves.
                if (
                    ctx.AvoidMode == AvoidTaxiwayMode.HardExclude
                    && ctx.AvoidedTaxiways.Contains(RouteCostFunction.ResolveTaxiwayName(edge, current.HeadNodeId))
                )
                {
                    rejected++;
                    continue;
                }

                // One-way hard exclusion (auto routes only — OneWayMode is Warn for explicit paths, which
                // are allowed the wrong way but flagged by RouteMaterialiser). When this pass finds no
                // route, TaxiPathfinder re-runs with OneWayMode relaxed to Warn so a destination reachable
                // only against a one-way still resolves.
                if (ctx.IsForbiddenMove(current.HeadNodeId, nextNode.Id))
                {
                    rejected++;
                    continue;
                }

                // Along-runway pavement is capped at a crossing's worth unless the controller named
                // the runway in the path or the aircraft started on it — a taxi route may CROSS a
                // runway stitched through centerline nodes, but never invents a back-taxi (OAK
                // "TAXI C D @GA1" back-taxied all of 10L to satisfy the destination). No soft
                // fallback: an unreachable destination fails rather than routing over a runway.
                if (ctx.IsForbiddenCenterlineMove(current, edge))
                {
                    rejected++;
                    continue;
                }

                // Centerline moves banned by a prior same-side-shortcut retry (see Run).
                if (bannedMoves is not null && bannedMoves.Contains((current.HeadNodeId, nextNode.Id)))
                {
                    rejected++;
                    continue;
                }

                // Blocked-turn exclusion (hard for AUTO and explicit alike). The corner arc is a 2-node
                // move; the sharp straight pivot through a surviving apex is a turn-triple keyed on where
                // we arrived from, so it never over-blocks straight-through or other-arm traffic.
                if (
                    ctx.IsBlockedArcMove(current.HeadNodeId, nextNode.Id)
                    || (current.Previous is not null && ctx.IsBlockedTurn(current.Previous.HeadNodeId, current.HeadNodeId, nextNode.Id))
                )
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

                // Skip if we already have a cheaper or equal path to this (node, bearing-bucket, taxiway) state.
                string taxiwayName = RouteCostFunction.ResolveTaxiwayName(edge, current.HeadNodeId);
                var nextKey = GeometricAdmissibility.PruningStateKey(nextNode.Id, arrivalBearing, taxiwayName);
                if (bestGScore.TryGetValue(nextKey, out double existingBest) && (newGScore >= existingBest - 1e-9))
                {
                    rejected++;
                    continue;
                }

                admitted++;
                bestGScore[nextKey] = newGScore;

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

                // Encode depth as a tiny fractional tie-breaker. Subtracting (Depth * 1e-9) lowers
                // the priority value of deeper routes, and .NET's PriorityQueue is a min-queue, so
                // among equal f-scores the DEEPER route dequeues first. This is the standard A*
                // tie-break — preferring nodes closer to the goal (higher g) cuts expansions — and
                // it keeps the queue deterministic. (Tie-break only; A* still returns an
                // optimal-cost route, since ties are between equal-cost frontiers.)
                double priority = fScore - (extended.Depth * 1e-9);

                openSet.Enqueue(extended, priority);
            }

            ctx.DiagnosticLog?.Invoke($"[auto] EXPAND admitted={admitted} rejected={rejected}");
        }

        ctx.DiagnosticLog?.Invoke($"[auto] FAIL reason=DestinationUnreachable  expansions={expansions}");

        return (
            null,
            new PathfindingFailure(
                FailureKind.DestinationUnreachable,
                $"No route found from node {ctx.StartNodeId} to destination (node {destinationNode.Id}) — graph may be disconnected.",
                null,
                null,
                null
            ),
            0.0
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
