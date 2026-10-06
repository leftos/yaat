namespace Yaat.Sim.Data.Airport.Pathfinding;

/// <summary>The goal node a goal-set search reached (<see cref="AutoRouter.RunToGoals"/>) and the route to it.</summary>
/// <param name="GoalNodeId">The goal node reached: the end of the route's last segment, or the start when it has none.</param>
/// <param name="Route">The route from the start node to <paramref name="GoalNodeId"/>; no segments when the start is that goal.</param>
/// <param name="Cost">
/// The search's own cost of the route — every <see cref="RouteCostFunction.IncrementalCost"/> term, not just its length; 0 when
/// the start is the goal.
/// </param>
public sealed record GoalRoute(int GoalNodeId, TaxiRoute Route, double Cost);

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
        (TaxiRoute? route, PathfindingFailure? failure, double _) = RunWithCost(ctx, startOverride, maxExpansions);
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

        if (!ctx.Layout.Nodes.TryGetValue(ctx.StartNodeId, out GroundNode? startNode))
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

            List<GroundNode> holdShortNodes = ctx.Layout.GetRunwayHoldShortNodes(ctx.Destination.RunwayId);
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
            TaxiRoute emptyRoute = RouteMaterialiser.Materialise([], ctx, []);
            return (emptyRoute, null, 0.0);
        }

        GoalSearch search = SearchToGoals(ctx, startNode, [destinationNode], startOverride, maxExpansions);
        return (search.Route, search.Failure, search.Cost);
    }

    /// <summary>
    /// A* from <see cref="SearchContext.StartNodeId"/> to whichever of <paramref name="goals"/> is cheapest to reach, in one
    /// pass, under the edge filters and cost model of <paramref name="ctx"/>; the route is materialised against
    /// <paramref name="ctx"/> re-targeted at the goal it reached. One pass only: the caller runs the avoidance passes
    /// (<see cref="TaxiPathfinder.FindRouteToNearestGoal"/>).
    /// </summary>
    /// <param name="ctx">The search context; a node destination, which the search re-targets at the goal it reaches.</param>
    /// <param name="goals">The goal nodes, at least one.</param>
    /// <returns>The goal reached and the route to it — no segments when the start is a goal — or the failure.</returns>
    /// <exception cref="InvalidOperationException">The materialised route does not end at the goal the search reached.</exception>
    public static (GoalRoute? Route, PathfindingFailure? Failure) RunToGoals(SearchContext ctx, IReadOnlyList<GroundNode> goals)
    {
        if (!ctx.Layout.Nodes.TryGetValue(ctx.StartNodeId, out GroundNode? startNode))
        {
            return (
                null,
                new PathfindingFailure(FailureKind.StartNodeUnreachable, $"Start node {ctx.StartNodeId} not found in layout.", null, null, null)
            );
        }

        if (goals.Any(goal => goal.Id == startNode.Id))
        {
            TaxiRoute here = RouteMaterialiser.Materialise([], MaterialiseContext(ctx, startNode.Id), []);
            return (new GoalRoute(startNode.Id, here, 0.0), null);
        }

        GoalSearch search = SearchToGoals(ctx, startNode, goals, null, MaxExpansions);
        if (search.Route is null)
        {
            return (null, search.Failure);
        }

        int reached = search.Route.Segments.Count > 0 ? search.Route.Segments[^1].ToNodeId : startNode.Id;
        if (reached != search.GoalNodeId)
        {
            throw new InvalidOperationException(
                $"Goal-set route from node {ctx.StartNodeId} ends at node {reached}, not at goal node {search.GoalNodeId} the search reached."
            );
        }

        return (new GoalRoute(reached, search.Route, search.Cost), null);
    }

    /// <summary>
    /// One search's outcome: the materialised route and the goal its unmaterialised path reached (null with no route), or the
    /// failure; and the search cost of the route beyond the start override (0 with no route).
    /// </summary>
    private sealed record GoalSearch(TaxiRoute? Route, int? GoalNodeId, PathfindingFailure? Failure, double Cost);

    /// <summary>
    /// The context a path reaching <paramref name="goalNodeId"/> materialises against: <paramref name="ctx"/> re-targeted at
    /// that goal when its destination is a node — a goal-set search reaches one of several; a single-goal node search already
    /// targets it. Any other destination keeps its descriptor: a runway route truncates at its first lineup hold-short, which a
    /// node target would override.
    /// </summary>
    private static SearchContext MaterialiseContext(SearchContext ctx, int goalNodeId) =>
        ctx.Destination.Kind == DestinationKind.Node ? ctx with { Destination = ctx.Destination with { TargetNodeId = goalNodeId } } : ctx;

    /// <summary>
    /// A* from <paramref name="startNode"/> to the cheapest of <paramref name="goals"/>, materialised against
    /// <paramref name="ctx"/> re-targeted at the goal reached (<see cref="MaterialiseContext"/>), with its search cost beyond
    /// <paramref name="startOverride"/>. The same-side runway check runs on the materialised route, which
    /// <see cref="RouteMaterialiser.Materialise"/> may have truncated.
    /// </summary>
    private static GoalSearch SearchToGoals(
        SearchContext ctx,
        GroundNode startNode,
        IReadOnlyList<GroundNode> goals,
        PartialRoute? startOverride,
        int maxExpansions
    )
    {
        // A returned path may use an uncleared runway as a same-side shortcut (on at one exit, off
        // at the next) — not a crossing. That can only be judged on the whole path, and judging it
        // inside the A* would poison the (node, bearing-bucket) closed set with path-dependent dead
        // ends. So: run, validate, and on a violation re-run with that run's centerline edges banned
        // outright, so the next attempt finds the legal path instead of inheriting poisoned states.
        HashSet<(int, int)>? bannedMoves = null;
        var goalSet = new GoalSet(goals);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            (List<DirectionalEdge>? Edges, PathfindingFailure? Failure, double Cost) result = RunAstar(
                ctx,
                startNode,
                goalSet,
                startOverride,
                maxExpansions,
                bannedMoves
            );
            if (result.Edges is null)
            {
                return new GoalSearch(null, null, result.Failure, 0.0);
            }

            int goalNodeId = result.Edges.Count > 0 ? result.Edges[^1].ToNodeId : startNode.Id;
            TaxiRoute route = RouteMaterialiser.Materialise(result.Edges, MaterialiseContext(ctx, goalNodeId), []);
            if (!ctx.HasSameSideCenterlineRun([.. route.Segments.Select(s => s.Edge)]))
            {
                return new GoalSearch(route, goalNodeId, null, result.Cost);
            }

            bannedMoves ??= [];
            int bannedBefore = bannedMoves.Count;
            foreach (TaxiRouteSegment seg in route.Segments)
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

        return new GoalSearch(
            null,
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

    private static (List<DirectionalEdge>? Edges, PathfindingFailure? Failure, double Cost) RunAstar(
        SearchContext ctx,
        GroundNode startNode,
        GoalSet goals,
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
        PartialRoute startRoute = startOverride ?? PartialRoute.StartAt(ctx.StartNodeId);
        double startHeuristic = goals.Heuristic(startNode);
        bestGScore[GeometricAdmissibility.PruningStateKey(startRoute.HeadNodeId, startRoute.ArrivalBearing, startRoute.LastTaxiwayName)] =
            startRoute.AccumulatedCost;
        openSet.Enqueue(startRoute, startRoute.AccumulatedCost + startHeuristic);

        if (ctx.DiagnosticLog is { } startLog)
        {
            int node = startRoute.HeadNodeId;
            int dest = goals.Nodes[0].Id;
            int goalCount = goals.Nodes.Count;
            double arrival = startRoute.ArrivalBearing;
            bool hasPrior = startRoute.LastEdge is not null;
            startLog(
                $"[auto] start node={node}  dest node={dest} (of {goalCount})  h0={startHeuristic:F3}  arrival={arrival:F1}  hasPrior={hasPrior}"
            );
        }

        while (openSet.Count > 0)
        {
            PartialRoute current = openSet.Dequeue();
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

            if (ctx.DiagnosticLog is { } popLog)
            {
                double f = current.AccumulatedCost + goals.Heuristic(ctx.Layout.Nodes[current.HeadNodeId]);
                popLog($"[auto] pop f={f:F3}  node={current.HeadNodeId}  depth={current.Depth}  cost={current.AccumulatedCost:F3}");
            }

            // Destination check.
            if (goals.Contains(current.HeadNodeId))
            {
                int baseDepth = startOverride?.Depth ?? 0;
                int newEdgeCount = current.Depth - baseDepth;
                ctx.DiagnosticLog?.Invoke($"[auto] SUCCESS edges={newEdgeCount}  total_cost={current.AccumulatedCost:F3}  expansions={expansions}");

                return (current.MaterialiseEdges(baseDepth), null, current.AccumulatedCost - startRoute.AccumulatedCost);
            }

            // Track deepest viable partial route for SearchExhausted diagnostics.
            if (deepestViable is null || (current.Depth > deepestViable.Depth))
            {
                deepestViable = current;
            }

            if (!ctx.Layout.Nodes.TryGetValue(current.HeadNodeId, out GroundNode? headNode))
            {
                continue;
            }

            int admitted = 0;
            int rejected = 0;

            foreach (IGroundEdge edge in headNode.Edges)
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
                (int Node, int Bucket, string Taxiway) nextKey = GeometricAdmissibility.PruningStateKey(nextNode.Id, arrivalBearing, taxiwayName);
                if (bestGScore.TryGetValue(nextKey, out double existingBest) && (newGScore >= existingBest - 1e-9))
                {
                    rejected++;
                    continue;
                }

                admitted++;
                bestGScore[nextKey] = newGScore;

                PartialRoute extended = current with
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

                double heuristic = goals.Heuristic(nextNode);
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
                goals.Nodes.Count == 1
                    ? $"No route found from node {ctx.StartNodeId} to destination (node {goals.Nodes[0].Id}) — graph may be disconnected."
                    : $"No route found from node {ctx.StartNodeId} to any of {goals.Nodes.Count} goal nodes — graph may be disconnected.",
                null,
                null,
                null
            ),
            0.0
        );
    }

    /// <summary>
    /// The goal nodes of one search. Every goal is a node — a runway destination is resolved to its full-length line-up
    /// hold-short before the search starts (<see cref="RouteMaterialiser.FindFullLengthLineupHoldShort"/>).
    /// </summary>
    private sealed class GoalSet(IReadOnlyList<GroundNode> nodes)
    {
        private readonly HashSet<int> _ids = [.. nodes.Select(node => node.Id)];

        /// <summary>Heuristics already computed, by node id; only with more than one goal, where each costs a pass over them.</summary>
        private readonly Dictionary<int, double>? _heuristics = nodes.Count > 1 ? [] : null;

        public IReadOnlyList<GroundNode> Nodes => nodes;

        /// <summary>True when <paramref name="nodeId"/> satisfies the destination: an exact node-id match against a goal.</summary>
        public bool Contains(int nodeId) => _ids.Contains(nodeId);

        /// <summary>
        /// The A* heuristic toward the goal set: the least <see cref="RouteCostFunction.Heuristic"/> to any goal. It never
        /// exceeds the heuristic to the optimal goal, which never exceeds the cost to reach it, so it stays admissible.
        /// </summary>
        public double Heuristic(GroundNode node)
        {
            if (_heuristics is null)
            {
                return RouteCostFunction.Heuristic(node, nodes[0]);
            }

            if (!_heuristics.TryGetValue(node.Id, out double best))
            {
                best = nodes.Min(goal => RouteCostFunction.Heuristic(node, goal));
                _heuristics[node.Id] = best;
            }

            return best;
        }
    }

    /// <summary>
    /// Resolve the target <see cref="GroundNode"/> from the context.
    /// Returns null for runway destinations (handled separately via hold-short lookup)
    /// and when the target node ID is not present in the layout.
    /// </summary>
    private static GroundNode? ResolveDestinationNode(SearchContext ctx)
    {
        if (ctx.Destination.TargetNodeId is { } id && ctx.Layout.Nodes.TryGetValue(id, out GroundNode? node))
        {
            return node;
        }

        return null;
    }
}
