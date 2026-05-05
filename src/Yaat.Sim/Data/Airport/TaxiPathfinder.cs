using Yaat.Sim.Phases;

namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Route preference for A* pathfinding. Each strategy uses a different cost function.
/// When null, all three strategies are evaluated and results are merged.
/// </summary>
public enum RoutePreference
{
    /// <summary>Minimize taxiway transitions (fewest differently-named taxiways).</summary>
    FewestTurns,

    /// <summary>Minimize total distance in nautical miles.</summary>
    Shortest,

    /// <summary>Minimize estimated travel time (accounts for arc speed limits).</summary>
    Fastest,
}

/// <summary>
/// Optional routing hints and diagnostics for <see cref="TaxiPathfinder.ResolveExplicitPath"/>.
/// </summary>
public sealed class ExplicitPathOptions
{
    public List<string>? ExplicitHoldShorts { get; init; }
    public string? DestinationRunway { get; init; }
    public string? AirportId { get; init; }
    public GroundNode? DestinationHintNode { get; init; }
    public Action<string>? DiagnosticLog { get; init; }

    /// <summary>
    /// When true, <see cref="TaxiPathfinder.ResolveExplicitPath"/> runs the
    /// SelectBestStopNode look-ahead at each inter-taxiway transition. The
    /// look-ahead recursively resolves the remaining authorized taxiway
    /// sequence from each candidate stop and picks the one with the lowest
    /// total distance, defeating "first-match" stops that produce hairpin
    /// U-turns at V-shaped taxiway junctions. SelectBestStopNode disables
    /// this on its own recursive calls to bound recursion depth — external
    /// callers should leave it at the default.
    /// </summary>
    internal bool EnableLookahead { get; init; } = true;
}

/// <summary>
/// Optional parameters for <see cref="TaxiPathfinder.WalkTaxiway"/>.
/// </summary>
public sealed class WalkOptions
{
    public string? NextTaxiwayName { get; init; }
    public bool AllowRampFallback { get; init; } = true;
    public bool AllowCurrentTaxiwayWalk { get; init; } = true;
    public GroundNode? DestinationHint { get; init; }
    public string? StopAtRunwayId { get; init; }

    /// <summary>
    /// Nodes at which the walk should stop. Populated by
    /// <see cref="TaxiPathfinder.ResolveExplicitPath"/> from
    /// <see cref="TaxiPathfinder.SelectBestStopNode"/> — the walk exits at the
    /// first node in this set it reaches (whether a ramp branch-off or a
    /// look-ahead-chosen inter-taxiway transition).
    /// </summary>
    public HashSet<int>? StopAtNodeIds { get; init; }

    public Action<string>? DiagnosticLog { get; init; }
}

/// <summary>
/// Pathfinding on the airport ground layout graph.
/// Supports explicit path validation (user specifies taxiways) and A* auto-routing.
/// </summary>
public static class TaxiPathfinder
{
    /// <summary>Max distance (nm) allowed when bridging taxiways via runway crossing (~3000ft).
    /// Covers hold-short approach + runway width + exit to next taxiway.</summary>
    private const double MaxRunwayBridgeNm = 3000.0 / GeoMath.FeetPerNm;

    /// <summary>
    /// Heavy cost penalty added when A* traverses a runway centerline edge.
    /// Prevents backtaxi/through-taxi on runways unless no taxiway-only path exists.
    /// Applied uniformly across all strategies.
    /// </summary>
    internal const double RunwayEdgePenaltyCost = 50.0;

    /// <summary>
    /// Large cost added per taxiway transition in the FewestTurns strategy.
    /// Must dwarf any realistic distance to ensure transition count dominates.
    /// </summary>
    private const double FewestTurnsPenalty = 10.0;

    /// <summary>
    /// Small tiebreaker weight on distance in the FewestTurns strategy.
    /// Breaks ties between routes with the same number of transitions.
    /// </summary>
    private const double FewestTurnsDistanceWeight = 0.001;

    /// <summary>Assumed max taxi speed (kts) for straight edges in the Fastest strategy.</summary>
    private const double FastestStraightSpeedKts = 30.0;

    /// <summary>
    /// Returns true if the token is a node reference (e.g., "#42").
    /// </summary>
    public static bool IsNodeReference(string token) => token.Length > 1 && token[0] == '#' && int.TryParse(token.AsSpan(1), out _);

    /// <summary>
    /// Parses the numeric node ID from a node reference token (e.g., "#42" → 42).
    /// </summary>
    public static int ParseNodeId(string token) => int.Parse(token.AsSpan(1));

    /// <summary>
    /// Validate and resolve an explicit taxiway path (e.g., "S T U W W1").
    /// Supports #nodeId tokens for exact node references (A* between them).
    /// Returns the route along the named taxiways, with implicit hold-short at
    /// runway crossings and explicit hold-short points.
    /// When a destination runway is set and the last user-specified taxiway doesn't
    /// reach it, automatically extends via a numbered variant (e.g., W → W1) if one
    /// connects to the runway's hold-short node.
    /// </summary>
    public static TaxiRoute? ResolveExplicitPath(
        AirportGroundLayout layout,
        int fromNodeId,
        List<string> taxiwayNames,
        out string? failReason,
        ExplicitPathOptions options
    )
    {
        var explicitHoldShorts = options.ExplicitHoldShorts;
        var destinationRunway = options.DestinationRunway;
        var airportId = options.AirportId;
        var destinationHintNode = options.DestinationHintNode;
        var diagnosticLog = options.DiagnosticLog;
        failReason = null;

        if (taxiwayNames.Count == 0)
        {
            return null;
        }

        var segments = new List<TaxiRouteSegment>();
        var holdShorts = new List<HoldShortPoint>();
        var warnings = new List<string>();
        int currentNodeId = fromNodeId;
        int segmentCountBeforeLastTw = 0;

        // Set of taxiway names the controller explicitly named in the
        // instruction. Used to bias the parking-extension and bridge A*
        // searches against entering letter-only taxiways the controller did
        // not authorize (e.g. avoid Y on a TAXI E A @B10 when stays-on-A is
        // available). Node references (@123) are excluded because they're
        // routing waypoints, not taxiway names.
        //
        // Also include the start node's currently-on taxiways: if the aircraft
        // is parked on G when given TAXI D, the natural bridge from G to D
        // walks G — penalizing G would force a worse D-entry point.
        var authorizedTaxiways = new HashSet<string>(taxiwayNames.Where(n => !IsNodeReference(n)), StringComparer.OrdinalIgnoreCase);
        if (layout.Nodes.TryGetValue(fromNodeId, out var startNodeForAuth))
        {
            foreach (var startEdge in startNodeForAuth.Edges)
            {
                if (startEdge is GroundArc startArc)
                {
                    foreach (var twy in startArc.TaxiwayNames)
                    {
                        authorizedTaxiways.Add(twy);
                    }
                }
                else
                {
                    authorizedTaxiways.Add(startEdge.TaxiwayName);
                }
            }
        }

        // Resolve destination hint for direction guidance when no next taxiway is available.
        // Use destinationRunway first; fall back to the first explicit hold-short (HS keyword)
        // so that "TAXI Y H B M1 HS 01L" steers the same direction as "TAXI Y H B M1 1L".
        string? effectiveDestRunway = destinationRunway ?? explicitHoldShorts?.FirstOrDefault();
        GroundNode? destinationHint = null;
        if (effectiveDestRunway is not null)
        {
            var holdShortNodes = layout.GetRunwayHoldShortNodes(effectiveDestRunway);
            diagnosticLog?.Invoke($"[Pathfinder] effectiveDestRunway={effectiveDestRunway}, holdShortNodes.Count={holdShortNodes.Count}");
            if (holdShortNodes.Count == 1)
            {
                destinationHint = holdShortNodes[0];
            }
            else if (holdShortNodes.Count > 1 && layout.Nodes.TryGetValue(fromNodeId, out var startNode))
            {
                double bestDist = double.MaxValue;
                foreach (var hsn in holdShortNodes)
                {
                    double dist = GeoMath.DistanceNm(hsn.Position, startNode.Position);
                    diagnosticLog?.Invoke(
                        $"[Pathfinder]   holdShort candidate id={hsn.Id} lat={hsn.Position.Lat:F6} lon={hsn.Position.Lon:F6} dist={dist:F4}nm edges=[{string.Join(",", hsn.Edges.Select(e => e.TaxiwayName))}]"
                    );
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        destinationHint = hsn;
                    }
                }
            }

            diagnosticLog?.Invoke(
                $"[Pathfinder] destinationHint={destinationHint?.Id.ToString() ?? "null"} lat={destinationHint?.Position.Lat:F6} lon={destinationHint?.Position.Lon:F6} edges=[{string.Join(",", destinationHint?.Edges.Select(e => e.TaxiwayName) ?? [])}]"
            );
        }

        // Fall back to the caller-provided destination node when no runway-based hint is available.
        // This steers WalkTaxiway toward a parking/spot destination instead of picking an arbitrary direction.
        destinationHint ??= destinationHintNode;

        for (int twIdx = 0; twIdx < taxiwayNames.Count; twIdx++)
        {
            string twName = taxiwayNames[twIdx];
            string? nextTwName = twIdx + 1 < taxiwayNames.Count ? taxiwayNames[twIdx + 1] : null;
            int segCountBefore = segments.Count;
            segmentCountBeforeLastTw = segCountBefore;

            // Node reference: A* from current position to the specified node
            if (IsNodeReference(twName))
            {
                int targetNodeId = ParseNodeId(twName);
                if (!layout.Nodes.ContainsKey(targetNodeId))
                {
                    failReason = $"Node #{targetNodeId} does not exist";
                    return null;
                }

                if (currentNodeId != targetNodeId)
                {
                    var subRoute = FindRoute(layout, currentNodeId, targetNodeId);
                    if (subRoute is null)
                    {
                        failReason = $"No route from node {currentNodeId} to #{targetNodeId}";
                        return null;
                    }

                    segments.AddRange(subRoute.Segments);
                    currentNodeId = targetNodeId;
                }

                continue;
            }

            // For the first taxiway: allow current-taxiway walk and RAMP fallback
            // (parking→taxiway). Between explicitly listed taxiways: only BFS
            // (short hop) and runway centerline bridging are allowed.
            bool isFirstTw = twIdx == 0;
            var passedHint = destinationHint;
            var passedStopId = nextTwName is null ? effectiveDestRunway : null;

            // Look-ahead: pick the best stop node on this taxiway before walking it.
            // For inter-taxiway transitions (nextTw set), candidates are nodes on X that
            // have an edge matching nextTw — score by walk-cost-on-X + simulated cost of
            // the remaining authorized taxiway walk from the candidate. This defeats
            // first-match transition picks at V-shape junctions (e.g. FLL T4 east-vs-west
            // leg for a B-bound walk).
            //
            // For the last taxiway (no nextTw), candidates are nodes on X reachable to a
            // parking/spot destination via non-walk-path edges. Only fires when a parking
            // destination node was passed — runway destinations are already handled
            // correctly by WalkTaxiway's stopAtRunwayId, and running the look-ahead would
            // append a spurious A* extension past the proper hold-short.
            //
            // Disabled when caller passes EnableLookahead=false (used by SelectBestStopNode's
            // own recursion to bound depth).
            HashSet<int>? passedStopNodeIds = null;
            GroundNode? passedWalkHint = passedHint;
            BestStopResult bestStopResult = default;
            bool runLookahead =
                !IsNodeReference(twName)
                && options.EnableLookahead
                && destinationHint is not null
                && (nextTwName is not null || destinationHintNode is not null);
            if (runLookahead && destinationHint is not null)
            {
                bestStopResult = SelectBestStopNode(
                    layout,
                    currentNodeId,
                    twName,
                    nextTwName,
                    destinationHint.Id,
                    authorizedTaxiways,
                    diagnosticLog,
                    taxiwayNames,
                    twIdx,
                    destinationRunway,
                    airportId,
                    explicitHoldShorts
                );
                if (bestStopResult.BestNodeId is not null)
                {
                    passedStopNodeIds = [bestStopResult.BestNodeId.Value];
                    if (layout.Nodes.TryGetValue(bestStopResult.BestNodeId.Value, out var bestStopNode))
                    {
                        passedWalkHint = bestStopNode;
                    }

                    diagnosticLog?.Invoke(
                        $"[Pathfinder] Walk[{twIdx}] {twName} best stop node: {bestStopResult.BestNodeId.Value} (next={nextTwName ?? "<dest>"})"
                    );
                }
            }

            if (layout.Nodes.TryGetValue(currentNodeId, out var curNode))
            {
                diagnosticLog?.Invoke(
                    $"[Pathfinder] Walk[{twIdx}] taxiway={twName} nextTw={nextTwName ?? "null"} from node={currentNodeId} lat={curNode.Position.Lat:F6} lon={curNode.Position.Lon:F6} "
                        + $"edges=[{string.Join(",", curNode.Edges.Select(e => e.TaxiwayName))}] "
                        + $"hint={(passedWalkHint is null ? "null" : passedWalkHint.Id.ToString())} stopAtRunwayId={passedStopId ?? "null"}"
                );
            }

            // When SelectBestStopNode already computed a Shortest-A* bridge (start is off X),
            // use those segments verbatim instead of calling WalkTaxiway — WalkTaxiway's
            // BridgeToTaxiway is direction-agnostic and picks the nearest X-entry, which
            // can force the D-walk through a wrong-way junction arc even when the chosen
            // stop implies a different entry.
            int endNodeId;
            bool found;
            if (bestStopResult.BridgeRoute is { Segments: { Count: > 0 } bridgeSegs })
            {
                segments.AddRange(bridgeSegs);
                endNodeId = bridgeSegs[^1].ToNodeId;
                found = true;
                diagnosticLog?.Invoke(
                    $"[Pathfinder] Walk[{twIdx}] {twName}: used cached bridge from {currentNodeId} to {endNodeId} ({bridgeSegs.Count} segments)"
                );
            }
            else
            {
                found = WalkTaxiway(
                    layout,
                    currentNodeId,
                    twName,
                    segments,
                    out endNodeId,
                    new WalkOptions
                    {
                        NextTaxiwayName = nextTwName,
                        AllowRampFallback = isFirstTw,
                        AllowCurrentTaxiwayWalk = isFirstTw,
                        DestinationHint = passedWalkHint,
                        StopAtRunwayId = passedStopId,
                        StopAtNodeIds = passedStopNodeIds,
                        DiagnosticLog = diagnosticLog,
                    }
                );
            }

            int addedSegments = segments.Count - segCountBefore;
            diagnosticLog?.Invoke($"[Pathfinder] Walk[{twIdx}] {twName} done: found={found} addedSegments={addedSegments} endNode={endNodeId}");

            if (!found)
            {
                if (isFirstTw)
                {
                    failReason = $"Cannot reach taxiway {twName} from current position";
                }

                return null;
            }

            // If this is the last taxiway and SelectBestStopNode cached a Shortest-A*
            // extension to the final destination, append it now. ResolveParkingRoute
            // would otherwise recompute this via FindRoute (FewestTurns) — which has
            // an inadmissible heuristic that can pick a longer path. Reusing the
            // cached Shortest extension avoids the heuristic bug for parking routes.
            if (nextTwName is null && bestStopResult.ExtensionRoute is { Segments: { Count: > 0 } extSegs })
            {
                segments.AddRange(extSegs);
                endNodeId = extSegs[^1].ToNodeId;
                diagnosticLog?.Invoke(
                    $"[Pathfinder] Walk[{twIdx}] {twName}: appended cached Shortest extension ({extSegs.Count} segments, ends at {endNodeId})"
                );
            }

            // Check if WalkTaxiway had to bridge via a different taxiway
            if (segments.Count > segCountBefore)
            {
                var firstNewSeg = segments[segCountBefore];
                if (!string.Equals(firstNewSeg.TaxiwayName, twName, StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add($"Taxiing via {firstNewSeg.TaxiwayName} to reach {twName}");
                }
            }

            currentNodeId = endNodeId;
        }

        // Auto-infer numbered taxiway variant for destination runway
        if (destinationRunway is not null && taxiwayNames.Count > 0 && !IsNodeReference(taxiwayNames[^1]))
        {
            _ = TaxiVariantResolver.TryInferVariant(
                layout,
                segments,
                taxiwayNames[^1],
                segmentCountBeforeLastTw,
                destinationRunway,
                airportId,
                ref currentNodeId,
                out failReason
            );

            if (failReason is not null)
            {
                return null;
            }
        }

        // Add implicit hold-short at runway crossings
        HoldShortAnnotator.AddImplicitRunwayHoldShorts(layout, segments, holdShorts);

        // Add explicit hold-short points
        if (explicitHoldShorts is not null)
        {
            foreach (string hsRunway in explicitHoldShorts)
            {
                HoldShortAnnotator.AddExplicitHoldShort(layout, segments, holdShorts, hsRunway);
            }
        }

        // Add destination runway hold-short
        if (destinationRunway is not null)
        {
            HoldShortAnnotator.AddDestinationHoldShort(layout, segments, holdShorts, destinationRunway);
        }

        return new TaxiRoute
        {
            Segments = segments,
            HoldShortPoints = holdShorts,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// Find the best route between two nodes. Uses the FewestTurns strategy
    /// (the most natural taxi instruction a controller would give).
    /// </summary>
    public static TaxiRoute? FindRoute(AirportGroundLayout layout, int fromNodeId, int toNodeId)
    {
        var routes = FindRoutes(layout, fromNodeId, toNodeId, RoutePreference.FewestTurns, 1);
        return routes.Count > 0 ? routes[0] : null;
    }

    /// <summary>
    /// Find up to <paramref name="maxRoutes"/> distinct routes between two nodes.
    /// When <paramref name="preference"/> is null, all three strategies (fewest turns,
    /// shortest, fastest) are evaluated via Yen's K-shortest; otherwise only the specified
    /// strategy runs. Each candidate route is scored by every strategy on a 0.0–1.0 scale
    /// (1.0 = best possible for that metric). A route's final score is the max across
    /// strategies. Results are deduplicated by taxiway sequence and ranked by score descending.
    /// <para>
    /// <paramref name="aircraftType"/> is used by the Fastest strategy to compute arc
    /// speed limits from the aircraft's ground turn rate. When null, defaults to Jet.
    /// </para>
    /// </summary>
    public static List<TaxiRoute> FindRoutes(
        AirportGroundLayout layout,
        int fromNodeId,
        int toNodeId,
        RoutePreference? preference = null,
        int maxRoutes = 4,
        string? aircraftType = null,
        IReadOnlySet<string>? authorizedTaxiways = null
    )
    {
        var strategies = preference is not null
            ? [preference.Value]
            : new[] { RoutePreference.FewestTurns, RoutePreference.Shortest, RoutePreference.Fastest };

        var category = aircraftType is not null ? AircraftCategorization.Categorize(aircraftType) : AircraftCategory.Jet;
        double turnRateDegSec = CategoryPerformance.GroundTurnRate(category);
        double taxiSpeedKts = CategoryPerformance.TaxiSpeed(category);

        // Phase 1: Collect candidates from each strategy via Yen's K-shortest.
        // Each strategy produces up to maxRoutes candidates.
        var allCandidates = new List<TaxiRoute>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var strategy in strategies)
        {
            Func<IGroundEdge, IGroundEdge?, double> costFn = strategy switch
            {
                RoutePreference.Shortest => (edge, _) => CostShortestBiased(edge, authorizedTaxiways),
                RoutePreference.FewestTurns => (edge, prev) => CostFewestTurns(edge, prev),
                RoutePreference.Fastest => (edge, _) => CostFastest(edge, turnRateDegSec, taxiSpeedKts),
                _ => (edge, _) => CostShortestBiased(edge, authorizedTaxiways),
            };

            // Heuristic must lower-bound the cost-to-goal in the same units as costFn.
            // Shortest costs distance-nm, so straight-line distance-nm is admissible.
            // FewestTurns costs distance × 0.001 + transition penalties (which only
            // add to cost), so distance × 0.001 lower-bounds the distance term and
            // ignores penalties. Fastest costs time = distance / speed, so distance /
            // taxiSpeed (the supremum speed) lower-bounds true time.
            Func<double, double> heuristicFn = strategy switch
            {
                RoutePreference.Shortest => distNm => distNm,
                RoutePreference.FewestTurns => distNm => distNm * FewestTurnsDistanceWeight,
                RoutePreference.Fastest => distNm => distNm / Math.Max(taxiSpeedKts, 1.0),
                _ => distNm => distNm,
            };

            var strategyRoutes = YenKShortest(layout, fromNodeId, toNodeId, costFn, heuristicFn, maxRoutes);
            foreach (var route in strategyRoutes)
            {
                var key = BuildTaxiwayKey(route);
                if (seenKeys.Add(key))
                {
                    allCandidates.Add(route);
                }
            }
        }

        if (allCandidates.Count == 0)
        {
            return [];
        }

        // Phase 2: Score every candidate by every strategy (normalized 0.0–1.0).
        // Find best raw value per metric across all candidates.
        double bestDistance = double.MaxValue;
        int fewestTransitions = int.MaxValue;
        double bestTime = double.MaxValue;

        foreach (var route in allCandidates)
        {
            double dist = route.TotalDistanceNm;
            int trans = CountTaxiwayTransitions(route);
            double time = EstimateTime(route, turnRateDegSec, taxiSpeedKts);

            if (dist < bestDistance)
            {
                bestDistance = dist;
            }

            if (trans < fewestTransitions)
            {
                fewestTransitions = trans;
            }

            if (time < bestTime)
            {
                bestTime = time;
            }
        }

        // Phase 3: Assign final score = average normalized score across strategies.
        // Each score is 0.0–1.0 where 1.0 = best in category. Averaging ensures a
        // route must be good across all metrics, not just dominant in one.
        var scored = new List<(TaxiRoute Route, double Score)>();
        foreach (var route in allCandidates)
        {
            double distScore = bestDistance / Math.Max(route.TotalDistanceNm, 1e-9);
            double transScore = (fewestTransitions + 1.0) / (CountTaxiwayTransitions(route) + 1.0);
            double timeScore = bestTime / Math.Max(EstimateTime(route, turnRateDegSec, taxiSpeedKts), 1e-9);

            double finalScore = (distScore + transScore + timeScore) / 3.0;
            scored.Add((route, finalScore));
        }

        // Phase 4: Sort by score descending, take top N.
        scored.Sort((a, b) => b.Score.CompareTo(a.Score));

        var results = new List<TaxiRoute>();
        for (int i = 0; i < Math.Min(scored.Count, maxRoutes); i++)
        {
            results.Add(scored[i].Route);
        }

        return results;
    }

    /// <summary>
    /// Estimate travel time (hours) for a route given aircraft performance.
    /// Straight edges use taxi speed; arcs use min(taxiSpeed, arcSafeSpeed).
    /// </summary>
    private static double EstimateTime(TaxiRoute route, double turnRateDegSec, double taxiSpeedKts)
    {
        double totalTime = 0;
        foreach (var seg in route.Segments)
        {
            double speed = Math.Min(taxiSpeedKts, seg.Edge.Edge.MaxSafeSpeedKts(turnRateDegSec));
            speed = Math.Max(speed, 1.0);
            totalTime += seg.Edge.DistanceNm / speed;
        }

        return totalTime;
    }

    /// <summary>
    /// Yen's K-shortest paths using a pluggable A* cost function and heuristic.
    /// Returns up to <paramref name="maxRoutes"/> routes, deduplicated by taxiway sequence.
    /// </summary>
    private static List<TaxiRoute> YenKShortest(
        AirportGroundLayout layout,
        int fromNodeId,
        int toNodeId,
        Func<IGroundEdge, IGroundEdge?, double> costFn,
        Func<double, double> heuristicFn,
        int maxRoutes
    )
    {
        var first = FindRouteInternal(layout, fromNodeId, toNodeId, null, null, costFn, heuristicFn);
        if (first is null)
        {
            return [];
        }

        var results = new List<TaxiRoute> { first };
        var candidates = new List<TaxiRoute>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal) { BuildTaxiwayKey(first) };

        for (int k = 1; k < maxRoutes; k++)
        {
            var prevRoute = results[k - 1];
            var prevSegments = prevRoute.Segments;

            for (int i = 0; i < prevSegments.Count; i++)
            {
                int spurNodeId = i == 0 ? prevSegments[0].FromNodeId : prevSegments[i - 1].ToNodeId;

                var blockedEdges = new HashSet<(int, int)>();
                foreach (var result in results)
                {
                    if (result.Segments.Count <= i)
                    {
                        continue;
                    }

                    bool rootMatches = true;
                    for (int j = 0; j < i; j++)
                    {
                        if (result.Segments[j].FromNodeId != prevSegments[j].FromNodeId || result.Segments[j].ToNodeId != prevSegments[j].ToNodeId)
                        {
                            rootMatches = false;
                            break;
                        }
                    }

                    if (rootMatches)
                    {
                        var seg = result.Segments[i];
                        blockedEdges.Add((seg.FromNodeId, seg.ToNodeId));
                        blockedEdges.Add((seg.ToNodeId, seg.FromNodeId));
                    }
                }

                var blockedNodes = new HashSet<int>();
                for (int j = 0; j < i; j++)
                {
                    blockedNodes.Add(prevSegments[j].FromNodeId);
                }

                var spurPath = FindRouteInternal(layout, spurNodeId, toNodeId, blockedEdges, blockedNodes, costFn, heuristicFn);
                if (spurPath is null)
                {
                    continue;
                }

                var combinedSegments = new List<TaxiRouteSegment>();
                for (int j = 0; j < i; j++)
                {
                    combinedSegments.Add(prevSegments[j]);
                }

                combinedSegments.AddRange(spurPath.Segments);

                var holdShorts = new List<HoldShortPoint>();
                HoldShortAnnotator.AddImplicitRunwayHoldShorts(layout, combinedSegments, holdShorts);

                candidates.Add(new TaxiRoute { Segments = combinedSegments, HoldShortPoints = holdShorts });
            }

            candidates.Sort((a, b) => RouteCost(a, costFn).CompareTo(RouteCost(b, costFn)));

            TaxiRoute? nextRoute = null;
            foreach (var candidate in candidates)
            {
                var key = BuildTaxiwayKey(candidate);
                if (seenKeys.Add(key))
                {
                    nextRoute = candidate;
                    break;
                }
            }

            if (nextRoute is null)
            {
                break;
            }

            results.Add(nextRoute);
            candidates.Remove(nextRoute);
        }

        return results;
    }

    private static double CostShortest(IGroundEdge edge)
    {
        double cost = edge.DistanceNm;
        if (edge.IsRunwayCenterline)
        {
            cost += RunwayEdgePenaltyCost;
        }

        return cost;
    }

    /// <summary>
    /// Multiplier applied to per-edge distance when an edge is on a letter-only
    /// taxiway not in the controller's authorized list. Letter-only names (A,
    /// Y, F, M) denote full named taxiways that controllers explicitly issue
    /// in instructions; if a route candidate enters one that wasn't requested,
    /// it is almost certainly a parallel detour the controller would have
    /// named had they intended it. Numbered taxiways (any name containing a
    /// digit — A1, M1, AY1) are treated as ramp/connector links the controller
    /// expects the pilot to use without naming. Multiplier &gt; 1 biases A* away
    /// from unauthorized letter-only paths without forbidding them — if the
    /// only physical route to the goal goes through one, A* will still pick it.
    /// </summary>
    private const double UnauthorizedLetterOnlyTaxiwayMultiplier = 5.0;

    /// <summary>
    /// Names that look letter-only but are graph categories rather than real
    /// taxiways and must be exempt from the bias (otherwise the parking-
    /// extension A* would refuse to enter the ramp it must reach).
    /// </summary>
    private static readonly HashSet<string> LetterOnlyTaxiwayBiasExemptions = new(StringComparer.OrdinalIgnoreCase) { "RAMP", "SPOT" };

    private static bool IsLetterOnlyTaxiway(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }
        if (LetterOnlyTaxiwayBiasExemptions.Contains(name))
        {
            return false;
        }
        for (int i = 0; i < name.Length; i++)
        {
            if (char.IsDigit(name[i]))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// CostShortest plus a multiplicative bias that discourages entering
    /// letter-only taxiways the controller didn't authorize. See
    /// <see cref="UnauthorizedLetterOnlyTaxiwayMultiplier"/> for the rationale.
    /// When <paramref name="authorizedTaxiways"/> is null the bias is disabled
    /// (cost matches <see cref="CostShortest"/>).
    ///
    /// <para>
    /// For multi-named transition arcs (e.g. "C - RAMP", "F - E"), the bias
    /// checks each of the arc's declared taxiway names: if any one is in the
    /// authorized list, or if any one is not a letter-only taxiway (i.e.
    /// numbered or exempt like RAMP/SPOT), the edge is not penalized. The
    /// arc is the legitimate transition between those taxiways and shouldn't
    /// be discouraged just because its formatted name string contains a
    /// separator.
    /// </para>
    /// </summary>
    private static double CostShortestBiased(IGroundEdge edge, IReadOnlySet<string>? authorizedTaxiways)
    {
        double cost = CostShortest(edge);
        if (authorizedTaxiways is null)
        {
            return cost;
        }

        string[] names = edge is GroundArc arc ? arc.TaxiwayNames : [edge.TaxiwayName];

        bool unauthorized = true;
        foreach (var name in names)
        {
            if (authorizedTaxiways.Contains(name) || !IsLetterOnlyTaxiway(name))
            {
                unauthorized = false;
                break;
            }
        }

        if (unauthorized)
        {
            cost *= UnauthorizedLetterOnlyTaxiwayMultiplier;
        }
        return cost;
    }

    private static double CostFewestTurns(IGroundEdge edge, IGroundEdge? prevEdge)
    {
        double cost = edge.DistanceNm * FewestTurnsDistanceWeight;
        if (edge.IsRunwayCenterline)
        {
            cost += RunwayEdgePenaltyCost;
        }

        if (prevEdge is not null && !edge.SharesTaxiway(prevEdge))
        {
            cost += FewestTurnsPenalty;
        }

        // Junction arcs (2 names) always represent a taxiway transition
        if (edge is GroundArc { TaxiwayNames.Length: > 1 })
        {
            cost += FewestTurnsPenalty;
        }

        return cost;
    }

    private static double CostFastest(IGroundEdge edge, double turnRateDegSec, double taxiSpeedKts)
    {
        if (edge.IsRunwayCenterline)
        {
            return edge.DistanceNm + RunwayEdgePenaltyCost;
        }

        double speedKts = Math.Min(taxiSpeedKts, edge.MaxSafeSpeedKts(turnRateDegSec));
        speedKts = Math.Max(speedKts, 1.0); // avoid division by zero
        double timeHours = edge.DistanceNm / speedKts;
        return timeHours;
    }

    /// <summary>
    /// Total cost of a route under the given cost function.
    /// Used by Yen's K-shortest to sort candidates by the active strategy's metric.
    /// </summary>
    private static double RouteCost(TaxiRoute route, Func<IGroundEdge, IGroundEdge?, double> costFn)
    {
        double total = 0;
        IGroundEdge? prev = null;
        foreach (var seg in route.Segments)
        {
            total += costFn(seg.Edge.Edge, prev);
            prev = seg.Edge.Edge;
        }

        return total;
    }

    /// <summary>
    /// Count distinct taxiway transitions in a route (excluding RWY and RAMP segments).
    /// </summary>
    private static int CountTaxiwayTransitions(TaxiRoute route)
    {
        int transitions = 0;
        string? prev = null;
        foreach (var seg in route.Segments)
        {
            if (
                seg.TaxiwayName.StartsWith("RWY", StringComparison.OrdinalIgnoreCase)
                || string.Equals(seg.TaxiwayName, "RAMP", StringComparison.OrdinalIgnoreCase)
            )
            {
                continue;
            }

            if (prev is not null && !string.Equals(prev, seg.TaxiwayName, StringComparison.OrdinalIgnoreCase))
            {
                transitions++;
            }

            prev = seg.TaxiwayName;
        }

        return transitions;
    }

    /// <summary>
    /// Build a deduplication key from the ordered unique taxiway names in a route,
    /// excluding RWY* and RAMP segments.
    /// </summary>
    internal static string BuildTaxiwayKey(TaxiRoute route)
    {
        var names = new List<string>();
        foreach (var seg in route.Segments)
        {
            var name = seg.TaxiwayName;
            if (name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "RAMP", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (names.Count == 0 || !string.Equals(names[^1], name, StringComparison.OrdinalIgnoreCase))
            {
                names.Add(name.ToUpperInvariant());
            }
        }

        return string.Join("|", names);
    }

    /// <summary>
    /// Checks if a stored runway ID (e.g., "12/30") contains the target
    /// designator (case-insensitive).
    /// </summary>
    internal static bool RunwayIdMatches(RunwayIdentifier storedRunwayId, string targetRunway)
    {
        return storedRunwayId.Contains(targetRunway);
    }

    /// <summary>
    /// Returns true if the node with <paramref name="nodeId"/> has any edge
    /// on <paramref name="taxiwayName"/>.
    /// </summary>
    internal static bool NodeHasEdgeTo(AirportGroundLayout layout, int nodeId, string taxiwayName)
    {
        if (!layout.Nodes.TryGetValue(nodeId, out var node))
        {
            return false;
        }

        foreach (var edge in node.Edges)
        {
            if (edge.MatchesTaxiway(taxiwayName))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true when <paramref name="edge"/> is part of the walk path on
    /// <paramref name="taxiwayName"/> — a straight edge named X or a same-taxiway arc
    /// whose only name is X. Multi-name arcs (e.g. "D - RAMP") are NOT walk-path
    /// edges; they are junction transitions from X to another taxiway, and the walk
    /// only takes them when turning off X.
    /// </summary>
    private static bool IsWalkPathEdgeOn(IGroundEdge edge, string taxiwayName) =>
        edge switch
        {
            GroundEdge ge => ge.MatchesTaxiway(taxiwayName),
            GroundArc arc => arc.TaxiwayNames.Length == 1 && arc.MatchesTaxiway(taxiwayName),
            _ => false,
        };

    /// <summary>
    /// Enumerate candidate stop nodes for a walk on <paramref name="taxiwayName"/>.
    /// For inter-taxiway transitions (<paramref name="nextTaxiwayName"/> is not null):
    /// nodes on X that have any edge matching Y. For the last taxiway
    /// (<paramref name="nextTaxiwayName"/> is null): BFS from
    /// <paramref name="finalDestinationNodeId"/> over edges that are not walk-path edges
    /// on X, collecting every node reached that has a walk-path edge on X. This finds
    /// every reasonable exit from X toward the destination, including via multi-name
    /// junction arcs the walk wouldn't take on its own.
    /// </summary>
    private static List<int> EnumerateTransitionCandidates(
        AirportGroundLayout layout,
        string taxiwayName,
        string? nextTaxiwayName,
        int finalDestinationNodeId
    )
    {
        var candidates = new List<int>();

        if (nextTaxiwayName is not null)
        {
            foreach (var node in layout.Nodes.Values)
            {
                bool onX = false;
                bool hasY = false;
                foreach (var edge in node.Edges)
                {
                    if (IsWalkPathEdgeOn(edge, taxiwayName))
                    {
                        onX = true;
                    }

                    if (edge.MatchesTaxiway(nextTaxiwayName))
                    {
                        hasY = true;
                    }
                }

                if (onX && hasY)
                {
                    candidates.Add(node.Id);
                }
            }

            return candidates;
        }

        if (!layout.Nodes.TryGetValue(finalDestinationNodeId, out var destinationNode))
        {
            return candidates;
        }

        // If the destination itself sits on the walk path (rare — e.g. destination is
        // a taxiway-intersection node already on X), it IS the stop. Skip the BFS.
        foreach (var edge in destinationNode.Edges)
        {
            if (IsWalkPathEdgeOn(edge, taxiwayName))
            {
                candidates.Add(finalDestinationNodeId);
                return candidates;
            }
        }

        var visited = new HashSet<int> { finalDestinationNodeId };
        var queue = new Queue<int>();
        queue.Enqueue(finalDestinationNodeId);
        var candidateSet = new HashSet<int>();

        while (queue.Count > 0)
        {
            int nodeId = queue.Dequeue();
            if (!layout.Nodes.TryGetValue(nodeId, out var node))
            {
                continue;
            }

            bool onWalkPath = false;
            foreach (var edge in node.Edges)
            {
                if (IsWalkPathEdgeOn(edge, taxiwayName))
                {
                    onWalkPath = true;
                    break;
                }
            }

            if (nodeId != finalDestinationNodeId && onWalkPath)
            {
                candidateSet.Add(nodeId);
                // Do not expand through walk-path nodes — they're on X, so the walk
                // reaches them directly; further BFS through them would re-enter X.
                continue;
            }

            foreach (var edge in node.Edges)
            {
                if (IsWalkPathEdgeOn(edge, taxiwayName))
                {
                    continue;
                }

                int otherId = edge.OtherNodeId(nodeId);
                if (visited.Add(otherId))
                {
                    queue.Enqueue(otherId);
                }
            }
        }

        candidates.AddRange(candidateSet);
        return candidates;
    }

    /// <summary>
    /// Shortest path in nautical miles from <paramref name="startNodeId"/> to every
    /// node reachable via edges matching <paramref name="taxiwayName"/>. Used by
    /// <see cref="SelectBestStopNode"/> to score candidates by the actual cost of
    /// walking the named taxiway to them, rather than straight-line distance which
    /// ignores topology. Unlike <see cref="IsWalkPathEdgeOn"/>, this includes
    /// multi-name junction arcs — the walker does traverse those when transitioning
    /// onto or off the named taxiway (see <c>WalkTaxiway</c>'s edge collection,
    /// which falls back to arcs when no straight option exists).
    /// </summary>
    private static Dictionary<int, double> WalkPathDistancesFrom(AirportGroundLayout layout, int startNodeId, string taxiwayName)
    {
        var distances = new Dictionary<int, double> { [startNodeId] = 0 };
        if (!layout.Nodes.ContainsKey(startNodeId))
        {
            return distances;
        }

        var pq = new PriorityQueue<int, double>();
        pq.Enqueue(startNodeId, 0);

        while (pq.TryDequeue(out int u, out double d))
        {
            if (d > distances[u])
            {
                continue;
            }

            if (!layout.Nodes.TryGetValue(u, out var node))
            {
                continue;
            }

            foreach (var edge in node.Edges)
            {
                if (!edge.MatchesTaxiway(taxiwayName))
                {
                    continue;
                }

                int v = edge.OtherNodeId(u);
                double nd = d + edge.DistanceNm;
                if (!distances.TryGetValue(v, out double cur) || nd < cur)
                {
                    distances[v] = nd;
                    pq.Enqueue(v, nd);
                }
            }
        }

        return distances;
    }

    /// <summary>
    /// Dijkstra over walk-path edges on <paramref name="taxiwayName"/> starting from
    /// <paramref name="startNodeId"/>. Returns parent pointers for path reconstruction
    /// to any reachable node on X. Used by <see cref="SelectBestStopNode"/> after
    /// picking the best candidate to materialize the actual walk-on-X segments.
    /// </summary>
    private static Dictionary<int, (int Parent, IGroundEdge Edge)> WalkPathParentsFrom(
        AirportGroundLayout layout,
        int startNodeId,
        string taxiwayName
    )
    {
        var parents = new Dictionary<int, (int Parent, IGroundEdge Edge)>();
        var distances = new Dictionary<int, double> { [startNodeId] = 0 };
        if (!layout.Nodes.ContainsKey(startNodeId))
        {
            return parents;
        }

        var pq = new PriorityQueue<int, double>();
        pq.Enqueue(startNodeId, 0);

        while (pq.TryDequeue(out int u, out double d))
        {
            if (d > distances[u])
            {
                continue;
            }

            if (!layout.Nodes.TryGetValue(u, out var node))
            {
                continue;
            }

            foreach (var edge in node.Edges)
            {
                if (!edge.MatchesTaxiway(taxiwayName))
                {
                    continue;
                }

                int v = edge.OtherNodeId(u);
                double nd = d + edge.DistanceNm;
                if (!distances.TryGetValue(v, out double cur) || nd < cur)
                {
                    distances[v] = nd;
                    parents[v] = (u, edge);
                    pq.Enqueue(v, nd);
                }
            }
        }

        return parents;
    }

    /// <summary>
    /// Reconstructs the walk-on-X path from <paramref name="startNodeId"/> to
    /// <paramref name="endNodeId"/> using parents from <see cref="WalkPathParentsFrom"/>.
    /// Returns segments tagged with <paramref name="taxiwayName"/>. Returns an empty
    /// list when start equals end; null when end is unreachable from start.
    /// </summary>
    private static List<TaxiRouteSegment>? ReconstructWalkOnXPath(
        AirportGroundLayout layout,
        int startNodeId,
        int endNodeId,
        string taxiwayName,
        Dictionary<int, (int Parent, IGroundEdge Edge)> parents
    )
    {
        if (startNodeId == endNodeId)
        {
            return [];
        }

        if (!parents.ContainsKey(endNodeId))
        {
            return null;
        }

        var nodeChain = new List<int>();
        var edgeChain = new List<IGroundEdge>();
        int cur = endNodeId;
        while (cur != startNodeId)
        {
            if (!parents.TryGetValue(cur, out var entry))
            {
                return null;
            }
            nodeChain.Add(cur);
            edgeChain.Add(entry.Edge);
            cur = entry.Parent;
        }
        nodeChain.Add(startNodeId);
        nodeChain.Reverse();
        edgeChain.Reverse();

        var segments = new List<TaxiRouteSegment>(edgeChain.Count);
        for (int i = 0; i < edgeChain.Count; i++)
        {
            if (!layout.Nodes.TryGetValue(nodeChain[i], out var fromNode) || !layout.Nodes.TryGetValue(nodeChain[i + 1], out var toNode))
            {
                return null;
            }
            segments.Add(new TaxiRouteSegment { TaxiwayName = taxiwayName, Edge = edgeChain[i].Directed(fromNode, toNode) });
        }

        return segments;
    }

    /// <summary>
    /// Result of <see cref="SelectBestStopNode"/>: the chosen stop node for the
    /// walk on a taxiway, plus the Shortest-A* routes already computed during
    /// scoring. <see cref="BridgeRoute"/> is the start→stop path when the walker
    /// begins off the target taxiway (null when already on X). <see cref="ExtensionRoute"/>
    /// is the stop→finalDestination path (null when the stop IS the destination).
    /// Callers should reuse these cached routes instead of re-running A* — the
    /// second run would use <c>FindRoute</c>'s FewestTurns strategy whose heuristic
    /// is inadmissible at the 0.001 distance weight, and can pick a longer path
    /// than Shortest-A* chose here.
    /// </summary>
    private readonly record struct BestStopResult(int? BestNodeId, TaxiRoute? BridgeRoute, TaxiRoute? ExtensionRoute);

    /// <summary>
    /// Pick the single best stop node for the walk on <paramref name="taxiwayName"/>.
    /// Enumerates transition candidates (see <see cref="EnumerateTransitionCandidates"/>),
    /// scores each by walk-cost-on-X-to-candidate + simulated-remaining-walk cost,
    /// and returns the candidate with minimum total cost. This defeats "first-match"
    /// transition bugs where the walk stopped at the first taxiway transition without
    /// checking whether it pointed toward the destination.
    /// <para>
    /// For inter-taxiway transitions, the extension is computed by recursively calling
    /// <see cref="ResolveExplicitPath"/> with the remaining authorized taxiway sequence
    /// (<paramref name="taxiwayNames"/> from <paramref name="twIdx"/>+1 onward) and
    /// <c>EnableLookahead=false</c>. This restricts the extension to legitimate
    /// authorized-sequence paths, preventing it from finding indirect cross-runway
    /// bridges through unauthorized taxiways (e.g. SFO TAXI A E succeeding via T41E/C/D).
    /// </para>
    /// <para>
    /// For the last taxiway (<paramref name="nextTaxiwayName"/> is null) the extension
    /// uses a Shortest A* to a parking/spot destination — there are no remaining named
    /// taxiways, so the unconstrained Shortest path is the right cost.
    /// </para>
    /// </summary>
    private static BestStopResult SelectBestStopNode(
        AirportGroundLayout layout,
        int walkStartNodeId,
        string taxiwayName,
        string? nextTaxiwayName,
        int finalDestinationNodeId,
        IReadOnlySet<string> authorizedTaxiways,
        Action<string>? diagnosticLog,
        List<string> taxiwayNames,
        int twIdx,
        string? destinationRunway,
        string? airportId,
        List<string>? explicitHoldShorts
    )
    {
        var candidates = EnumerateTransitionCandidates(layout, taxiwayName, nextTaxiwayName, finalDestinationNodeId);
        if (candidates.Count == 0)
        {
            diagnosticLog?.Invoke(
                $"[Pathfinder] SelectBestStopNode({taxiwayName}, next={nextTaxiwayName ?? "<dest>"}, dest={finalDestinationNodeId}): no candidates"
            );
            return default;
        }

        // When the destination is a runway and the walker starts off X, bridge onto X
        // using the same legitimate strategies WalkTaxiway uses (BFS short-hop,
        // current-taxiway walk, runway centerline, RAMP-with-runway-cross-check).
        // This matches the behavior the SFO TAXI A E (from gate) test depends on:
        // before this look-ahead existed, runway-destination commands fell through to
        // WalkTaxiway whose BridgeToTaxiway would reject long indirect bridges
        // through unauthorized taxiways. Without the same restriction here, the
        // look-ahead would silently succeed where WalkTaxiway fails.
        //
        // For parking destinations we keep the per-candidate FindRoutes bridge below
        // — OAK TAXI G @SIG1 is a parking destination whose legitimate bridge legitimately
        // goes via a letter-only connector (C → RAMP) that BridgeToTaxiway's BFS depth
        // limit can't reach but FindRoutes' soft-bias does. Pre-bridging with
        // BridgeToTaxiway would reject those parking bridges.
        TaxiRoute? bridgeRoute = null;
        double bridgeBaseCost = 0;
        int effectiveWalkStartId = walkStartNodeId;
        if (!layout.Nodes.TryGetValue(walkStartNodeId, out var walkStartNode))
        {
            return default;
        }

        bool walkStartOnX = walkStartNode.Edges.Any(e => e.MatchesTaxiway(taxiwayName));
        // Use BridgeToTaxiway-style pre-bridging for runway-destination commands
        // (final destination is a runway hold-short — set via DestinationRunway or
        // ExplicitHoldShorts). This matches the behavior runway-destination commands
        // had before this look-ahead existed: ResolveExplicitPath fell through to
        // WalkTaxiway whose BridgeToTaxiway uses limited-hop BFS, current-taxiway
        // walk, runway centerline, and RAMP-with-runway-cross-check — strategies
        // conservative enough to reject illegitimate long indirect bridges (e.g.
        // SFO TAXI A E from a gate where reaching A would require T41E/C/D).
        // Parking-destination commands keep the per-candidate FindRoutes bridge
        // below — OAK TAXI G @SIG1 is a parking destination whose legitimate bridge
        // legitimately goes via a letter-only connector (C → RAMP) that
        // BridgeToTaxiway's BFS-3-hop limit can't reach.
        bool finalDestIsHoldShort =
            layout.Nodes.TryGetValue(finalDestinationNodeId, out var finalDestNode) && finalDestNode.Type == GroundNodeType.RunwayHoldShort;
        bool useBridgeToTaxiway = finalDestIsHoldShort;
        if (!walkStartOnX && useBridgeToTaxiway)
        {
            var bridgeSegs = new List<TaxiRouteSegment>();
            int bridgedTo = BridgeToTaxiway(
                layout,
                walkStartNodeId,
                taxiwayName,
                bridgeSegs,
                walkStartNode,
                allowCurrentTaxiwayWalk: true,
                allowRampFallback: true,
                diagnosticLog
            );
            diagnosticLog?.Invoke(
                $"[Pathfinder] SelectBestStopNode({taxiwayName}): off-X runway-dest bridge {walkStartNodeId}→{bridgedTo} segs={bridgeSegs.Count}"
            );
            if (bridgedTo == -1)
            {
                diagnosticLog?.Invoke($"[Pathfinder] SelectBestStopNode({taxiwayName}): no legitimate runway-dest bridge — returning no candidate");
                return default;
            }

            effectiveWalkStartId = bridgedTo;
            bridgeBaseCost = bridgeSegs.Sum(s => s.Edge.Edge.DistanceNm);
            bridgeRoute = new TaxiRoute
            {
                Segments = bridgeSegs,
                HoldShortPoints = [],
                Warnings = [],
            };
        }

        // Real walk distance (Dijkstra over walk-path edges on X) from the effective
        // start (post-bridge if we bridged) to each reachable node. Candidates not in
        // this map are unreachable by walking X. For the parking-destination path the
        // effective start is still the original off-X start; candidates not on X get
        // a per-candidate Shortest A* bridge below.
        var walkDistances = WalkPathDistancesFrom(layout, effectiveWalkStartId, taxiwayName);

        // For inter-taxiway transitions we score each candidate by simulating the
        // remaining authorized taxiway walk from it. Pre-build the remaining list so
        // the recursion runs only on the post-transition sequence.
        List<string>? remainingTaxiways = null;
        if (nextTaxiwayName is not null)
        {
            remainingTaxiways = new List<string>(taxiwayNames.Count - twIdx - 1);
            for (int i = twIdx + 1; i < taxiwayNames.Count; i++)
            {
                remainingTaxiways.Add(taxiwayNames[i]);
            }
        }

        int? bestCandidate = null;
        double bestCost = double.MaxValue;
        TaxiRoute? bestBridge = null;
        TaxiRoute? bestExtension = null;
        var scores = new List<string>();

        foreach (int candidateId in candidates)
        {
            // Walk-on-X distance from the effective start (post-bridge if we used the
            // runway-destination BridgeToTaxiway path; original start otherwise).
            // For the parking-destination path, candidates not on X get a per-candidate
            // Shortest A* bridge that lets soft-bias find legitimate connector hops
            // (e.g. OAK E → C → RAMP → G when G isn't directly reachable from E).
            TaxiRoute? perCandidateBridge = null;
            double walkCost;
            if (walkDistances.TryGetValue(candidateId, out double onXCost))
            {
                walkCost = bridgeBaseCost + onXCost;
            }
            else if (!useBridgeToTaxiway)
            {
                var bridgeCandidates = FindRoutes(
                    layout,
                    walkStartNodeId,
                    candidateId,
                    RoutePreference.Shortest,
                    1,
                    authorizedTaxiways: authorizedTaxiways
                );
                if (bridgeCandidates.Count == 0)
                {
                    scores.Add($"{candidateId}=unreachable");
                    continue;
                }

                perCandidateBridge = bridgeCandidates[0];
                walkCost = perCandidateBridge.TotalDistanceNm;
            }
            else
            {
                scores.Add($"{candidateId}=unreachable_post_bridge");
                continue;
            }

            if (candidateId == finalDestinationNodeId)
            {
                scores.Add($"{candidateId}=walk:{walkCost:F4}+ext:0.0000=TOTAL:{walkCost:F4}");
                if (walkCost < bestCost)
                {
                    bestCost = walkCost;
                    bestCandidate = candidateId;
                    bestBridge = bridgeRoute ?? perCandidateBridge;
                    bestExtension = null;
                }

                continue;
            }

            double extensionDistance;
            TaxiRoute? extension = null;
            if (remainingTaxiways is not null)
            {
                // Inter-taxiway transition: recursively resolve the remaining authorized
                // taxiway sequence from this candidate. EnableLookahead=false bounds the
                // recursion to one level — the inner call walks the remainder using the
                // basic walker, which is sufficient because look-ahead at THIS level
                // already considers the downstream geometry.
                var subRoute = ResolveExplicitPath(
                    layout,
                    candidateId,
                    remainingTaxiways,
                    out _,
                    new ExplicitPathOptions
                    {
                        DestinationRunway = destinationRunway,
                        ExplicitHoldShorts = explicitHoldShorts,
                        AirportId = airportId,
                        DestinationHintNode = layout.Nodes.TryGetValue(finalDestinationNodeId, out var destNode) ? destNode : null,
                        EnableLookahead = false,
                    }
                );
                if (subRoute is null)
                {
                    scores.Add($"{candidateId}=no_route");
                    continue;
                }

                extensionDistance = subRoute.TotalDistanceNm;
                extension = subRoute;
            }
            else
            {
                // Last taxiway with parking/spot destination off X — use Shortest A* to
                // the destination. No remaining authorized taxiways, so an unconstrained
                // Shortest path is the right cost.
                var extensionCandidates = FindRoutes(
                    layout,
                    candidateId,
                    finalDestinationNodeId,
                    RoutePreference.Shortest,
                    1,
                    authorizedTaxiways: authorizedTaxiways
                );
                if (extensionCandidates.Count == 0)
                {
                    scores.Add($"{candidateId}=no_route");
                    continue;
                }

                extension = extensionCandidates[0];
                extensionDistance = extension.TotalDistanceNm;
            }

            double totalCost = walkCost + extensionDistance;
            scores.Add($"{candidateId}=walk:{walkCost:F4}+ext:{extensionDistance:F4}=TOTAL:{totalCost:F4}");
            if (totalCost < bestCost)
            {
                bestCost = totalCost;
                bestCandidate = candidateId;
                bestBridge = bridgeRoute ?? perCandidateBridge;
                // Cache the parking-destination extension for ResolveExplicitPath to
                // reuse verbatim. Inter-taxiway extensions go back through the outer
                // walk loop, so we don't cache them — caching would skip the bridge
                // segments emitted by the outer walker between candidate and the next
                // taxiway, producing incomplete routes.
                bestExtension = remainingTaxiways is null ? extension : null;
            }
        }

        diagnosticLog?.Invoke(
            $"[Pathfinder] SelectBestStopNode({taxiwayName}, next={nextTaxiwayName ?? "<dest>"}, dest={finalDestinationNodeId}): "
                + $"scores=[{string.Join(" ; ", scores)}] best={bestCandidate?.ToString() ?? "null"} bestCost={bestCost:F4}nm"
        );

        // When we used a runway-dest pre-bridge, the cached bridgeRoute only has segments
        // from walkStart to the BFS-landing on X. Append the walk-on-X segments from the
        // BFS-landing to the chosen candidate so the cached BridgeRoute the outer walker
        // replays actually ends at the chosen stop. Without this, the outer walker would
        // restart from the BFS-landing and never reach the chosen candidate, leaving the
        // next taxiway's walk stranded at the wrong entry point.
        if (bridgeRoute is not null && bestCandidate is not null && bestCandidate.Value != effectiveWalkStartId)
        {
            var parents = WalkPathParentsFrom(layout, effectiveWalkStartId, taxiwayName);
            var walkOnXSegs = ReconstructWalkOnXPath(layout, effectiveWalkStartId, bestCandidate.Value, taxiwayName, parents);
            if (walkOnXSegs is null)
            {
                diagnosticLog?.Invoke(
                    $"[Pathfinder] SelectBestStopNode({taxiwayName}): could not reconstruct walk-on-X path from {effectiveWalkStartId} to {bestCandidate.Value}"
                );
                return default;
            }

            var fullSegments = new List<TaxiRouteSegment>(bridgeRoute.Segments.Count + walkOnXSegs.Count);
            fullSegments.AddRange(bridgeRoute.Segments);
            fullSegments.AddRange(walkOnXSegs);
            bestBridge = new TaxiRoute
            {
                Segments = fullSegments,
                HoldShortPoints = [],
                Warnings = [],
            };
        }

        return new BestStopResult(bestCandidate, bestBridge, bestExtension);
    }

    /// <summary>
    /// Walk along <paramref name="taxiwayName"/> from <paramref name="startNodeId"/>,
    /// appending segments. Stops when the taxiway ends or the next taxiway in the
    /// path is reachable. Uses BFS then straight-line fallback to reach the taxiway
    /// if not directly connected.
    /// </summary>
    internal static bool WalkTaxiway(
        AirportGroundLayout layout,
        int startNodeId,
        string taxiwayName,
        List<TaxiRouteSegment> segments,
        out int endNodeId,
        WalkOptions opts
    )
    {
        var nextTaxiwayName = opts.NextTaxiwayName;
        bool allowRampFallback = opts.AllowRampFallback;
        bool allowCurrentTaxiwayWalk = opts.AllowCurrentTaxiwayWalk;
        var destinationHint = opts.DestinationHint;
        var stopAtRunwayId = opts.StopAtRunwayId;
        var stopAtNodeIds = opts.StopAtNodeIds;
        var diagnosticLog = opts.DiagnosticLog;
        endNodeId = startNodeId;

        // Record where this walk's segments begin in the shared list so the
        // post-walk arc-shortcut pass only rewrites THIS taxiway's segments.
        int walkStartIdx = segments.Count;

        if (!layout.Nodes.TryGetValue(startNodeId, out var currentNode))
        {
            return false;
        }

        // Find all edges on this taxiway from the current node.
        // Prefer straight edges over junction arcs — arcs are for transitions between taxiways,
        // not for continuing along the same taxiway.
        var straightCandidates = new List<IGroundEdge>();
        var arcCandidates = new List<IGroundEdge>();
        foreach (var edge in currentNode.Edges)
        {
            if (!edge.MatchesTaxiway(taxiwayName))
            {
                continue;
            }

            if (edge is GroundArc)
            {
                arcCandidates.Add(edge);
            }
            else
            {
                straightCandidates.Add(edge);
            }
        }

        var candidateEdges = straightCandidates.Count > 0 ? straightCandidates : arcCandidates;

        diagnosticLog?.Invoke(
            $"[WalkTaxiway] {taxiwayName}: startNode={startNodeId} candidateEdges={candidateEdges.Count} nextTw={nextTaxiwayName ?? "null"} stopAtRunwayId={stopAtRunwayId ?? "null"}"
        );

        // When stopAtRunwayId is set, compute effectiveHint from the nearest hold-short that is
        // directly on this taxiway (not a variant). This steers PickBestStartEdge/PickBestWalkEdge
        // in the correct direction when the walk has two choices.
        // If no hold-short is found directly on this taxiway, use null — the passed destinationHint
        // was computed relative to the original aircraft position and is unreliable for later taxiways.
        // Example: SFO M1 — node 882 (1L hold-short) is on M1 → walk steers south and stops there.
        // Example: OAK W — runway 30 hold-shorts are on W1/W2, not W → null → candidates[0] (south).
        GroundNode? effectiveHint = destinationHint;
        if (stopAtRunwayId is not null && layout.Nodes.TryGetValue(startNodeId, out var startHintRef))
        {
            double nearestDist = double.MaxValue;
            GroundNode? taxiwayHoldShort = null;
            foreach (var node in layout.Nodes.Values)
            {
                if (node.Type != GroundNodeType.RunwayHoldShort || node.RunwayId is not { } rId || !rId.Contains(stopAtRunwayId))
                {
                    continue;
                }

                if (!node.Edges.Any(e => e.MatchesTaxiway(taxiwayName)))
                {
                    continue;
                }

                double dist = GeoMath.DistanceNm(node.Position, startHintRef.Position);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    taxiwayHoldShort = node;
                }
            }
            effectiveHint = taxiwayHoldShort;
            if (effectiveHint is not null)
            {
                diagnosticLog?.Invoke(
                    $"[WalkTaxiway] {taxiwayName}: computed effectiveHint from hold-short node={effectiveHint.Id} lat={effectiveHint.Position.Lat:F6} RunwayId={effectiveHint.RunwayId}"
                );
            }
            else if (destinationHint is not null)
            {
                diagnosticLog?.Invoke(
                    $"[WalkTaxiway] {taxiwayName}: no hold-short on taxiway for stopAtRunwayId={stopAtRunwayId} — effectiveHint cleared (passed hint was node={destinationHint.Id})"
                );
            }
        }

        IGroundEdge? startEdge = candidateEdges.Count switch
        {
            0 => null,
            1 => candidateEdges[0],
            _ => PickBestStartEdge(layout, startNodeId, candidateEdges, nextTaxiwayName, effectiveHint),
        };

        if (startEdge is not null)
        {
            int firstDest = startEdge.OtherNodeId(startNodeId);
            diagnosticLog?.Invoke($"[WalkTaxiway] {taxiwayName}: startEdge → node={firstDest}");
        }

        if (startEdge is null)
        {
            diagnosticLog?.Invoke($"[WalkTaxiway] {taxiwayName}: no direct edge from node={startNodeId}, trying bridge strategies");

            int bridgedTo = BridgeToTaxiway(
                layout,
                startNodeId,
                taxiwayName,
                segments,
                currentNode,
                allowCurrentTaxiwayWalk,
                allowRampFallback,
                diagnosticLog
            );
            if (bridgedTo == -1)
            {
                return false;
            }

            startNodeId = bridgedTo;
        }

        // If the start node already connects to the next taxiway, this taxiway name
        // is just a directional hint at a multi-way junction (e.g., "T" in "TE T U W"
        // at the TE/T/U intersection). Skip the walk — the aircraft is already where
        // it needs to be to transition to the next taxiway.
        // When SelectBestStopNode has chosen a specific stop (stopAtNodeIds populated),
        // defer to that choice: the start node connecting to nextTaxiwayName is
        // irrelevant if look-ahead scored a different node as better.
        if (
            nextTaxiwayName is not null
            && (stopAtNodeIds is null || stopAtNodeIds.Contains(startNodeId))
            && NodeHasEdgeTo(layout, startNodeId, nextTaxiwayName)
        )
        {
            diagnosticLog?.Invoke($"[WalkTaxiway] {taxiwayName}: startNode={startNodeId} already connects to {nextTaxiwayName} — skipping walk");
            endNodeId = startNodeId;
            return segments.Count > 0;
        }

        // Walk along the taxiway to the end
        int currentId = startNodeId;
        var visited = new HashSet<int> { currentId };
        diagnosticLog?.Invoke($"[WalkTaxiway] {taxiwayName}: starting walk from node={startNodeId}");

        while (true)
        {
            if (!layout.Nodes.TryGetValue(currentId, out var node))
            {
                break;
            }

            // Collect all unvisited edges on this taxiway.
            // Prefer straight edges over junction arcs to stay on collinear segments
            // rather than detouring through arcs at intersections.
            var straightCands = new List<(IGroundEdge Edge, int NodeId)>();
            var arcCands = new List<(IGroundEdge Edge, int NodeId)>();
            foreach (var edge in node.Edges)
            {
                int otherId = edge.OtherNodeId(currentId);
                if (visited.Contains(otherId) || !edge.MatchesTaxiway(taxiwayName))
                {
                    continue;
                }

                if (edge is GroundArc)
                {
                    arcCands.Add((edge, otherId));
                }
                else
                {
                    straightCands.Add((edge, otherId));
                }
            }

            var candidates = straightCands.Count > 0 ? straightCands : arcCands;

            IGroundEdge? nextEdge;
            int nextNodeId;
            if (candidates.Count == 0)
            {
                nextEdge = null;
                nextNodeId = -1;
            }
            else if (candidates.Count == 1)
            {
                (nextEdge, nextNodeId) = candidates[0];
            }
            else
            {
                // Multiple directions — prefer the one leading toward the next taxiway
                (nextEdge, nextNodeId) = PickBestWalkEdge(layout, candidates, nextTaxiwayName, effectiveHint);
            }

            if (nextEdge is null)
            {
                diagnosticLog?.Invoke($"[WalkTaxiway] {taxiwayName}: dead end at node={currentId}");
                break;
            }

            if (!layout.Nodes.TryGetValue(nextNodeId, out var nextNodeInfo))
            {
                break;
            }

            string nextNodeType =
                nextNodeInfo.Type == GroundNodeType.RunwayHoldShort ? $"RunwayHoldShort({nextNodeInfo.RunwayId})" : nextNodeInfo.Type.ToString();
            diagnosticLog?.Invoke(
                $"[WalkTaxiway] {taxiwayName}: step {currentId}→{nextNodeId} ({nextNodeType}) lat={nextNodeInfo.Position.Lat:F6} edges=[{string.Join(",", nextNodeInfo.Edges.Select(e => e.TaxiwayName))}]"
            );

            segments.Add(new TaxiRouteSegment { TaxiwayName = taxiwayName, Edge = nextEdge.Directed(node, nextNodeInfo) });

            visited.Add(nextNodeId);
            currentId = nextNodeId;

            // Stop at any node chosen upstream by SelectBestStopNode — either the
            // destination itself (if on this taxiway), a ramp branch-off for a
            // parking/spot destination, or the best inter-taxiway transition node.
            if (stopAtNodeIds is not null && stopAtNodeIds.Contains(currentId))
            {
                diagnosticLog?.Invoke($"[WalkTaxiway] {taxiwayName}: stopping at chosen node={currentId}");
                break;
            }

            // Stop early if this node connects to the next taxiway in the path.
            // Suppressed when SelectBestStopNode chose a specific stop — otherwise we'd
            // stop at the first Y-connecting node and ignore the look-ahead pick.
            if (stopAtNodeIds is null && nextTaxiwayName is not null && NodeHasEdgeTo(layout, currentId, nextTaxiwayName))
            {
                diagnosticLog?.Invoke($"[WalkTaxiway] {taxiwayName}: stopping at node={currentId} — connects to {nextTaxiwayName}");
                break;
            }

            // Stop at the first runway hold-short matching the destination runway.
            // Without this, WalkTaxiway walks past the correct hold-short to the taxiway dead-end,
            // potentially crossing unrelated runways.
            if (
                stopAtRunwayId is not null
                && layout.Nodes.TryGetValue(currentId, out var arrNode)
                && arrNode.Type == GroundNodeType.RunwayHoldShort
                && arrNode.RunwayId is { } arrRwyId
                && arrRwyId.Contains(stopAtRunwayId)
            )
            {
                diagnosticLog?.Invoke(
                    $"[WalkTaxiway] {taxiwayName}: stopping at runway hold-short node={currentId} RunwayId={arrRwyId} matches stopAtRunwayId={stopAtRunwayId}"
                );
                break;
            }
        }

        endNodeId = currentId;

        // Post-walk: check for same-taxiway fillet arcs that would shortcut
        // the straight-walk through the corner apex. E.g. SFO A1's 2186↔2185
        // fillet spans the 2186→507→2185 straight pair — the main walk's
        // "prefer straights over arcs" rule correctly keeps same-taxiway
        // arcs out of transition picks but needs this post-pass to put
        // them back in as through-turn shortcuts.
        ApplySameTaxiwayArcShortcuts(layout, segments, walkStartIdx, startNodeId, taxiwayName, diagnosticLog);

        return segments.Count > 0;
    }

    /// <summary>
    /// Replace straight-through spans with same-taxiway fillet arcs when the
    /// arc connects two walk nodes non-adjacently. Iterates until no further
    /// shortcut applies, so chained shortcuts on the same walk all fire.
    /// </summary>
    /// <remarks>
    /// A same-taxiway arc is a <see cref="GroundArc"/> with a single entry in
    /// <see cref="GroundArc.TaxiwayNames"/> (vs. two-entry junction arcs
    /// connecting different taxiways). <see cref="FilletArcGenerator"/>
    /// creates these at bends WITHIN a single taxiway (e.g. SFO A1's apex
    /// near node 507); they represent the physical pavement curve an
    /// aircraft would naturally follow. The main walk prefers straights to
    /// keep arcs out of junction picks — this pass re-introduces them as
    /// shortcuts when the straight walk visits both endpoints with at least
    /// one intermediate node skipped.
    /// </remarks>
    private static void ApplySameTaxiwayArcShortcuts(
        AirportGroundLayout layout,
        List<TaxiRouteSegment> segments,
        int walkStartIdx,
        int startNodeId,
        string taxiwayName,
        Action<string>? diagnosticLog
    )
    {
        bool changed;
        do
        {
            changed = false;

            // Build the ordered node sequence for THIS walk's segments.
            // nodeSequence[0] = startNodeId; nodeSequence[i>0] = segments[walkStartIdx+i-1].ToNodeId.
            int walkLen = segments.Count - walkStartIdx;
            if (walkLen < 2)
            {
                return; // Need at least two segments for a shortcut (three nodes).
            }

            var nodeSequence = new List<int>(walkLen + 1) { startNodeId };
            for (int i = walkStartIdx; i < segments.Count; i++)
            {
                nodeSequence.Add(segments[i].ToNodeId);
            }

            var nodePositionMap = new Dictionary<int, int>(nodeSequence.Count);
            for (int i = 0; i < nodeSequence.Count; i++)
            {
                nodePositionMap[nodeSequence[i]] = i;
            }

            foreach (var arc in layout.Arcs)
            {
                if (arc.TaxiwayNames.Length != 1)
                {
                    continue; // Skip junction arcs — they connect different taxiways by design.
                }
                if (!arc.MatchesTaxiway(taxiwayName))
                {
                    continue;
                }

                int ep0 = arc.Nodes[0].Id;
                int ep1 = arc.Nodes[1].Id;
                if (!nodePositionMap.TryGetValue(ep0, out int pos0) || !nodePositionMap.TryGetValue(ep1, out int pos1))
                {
                    continue; // Arc endpoint not visited by this walk.
                }

                int fromPos = Math.Min(pos0, pos1);
                int toPos = Math.Max(pos0, pos1);
                if (toPos - fromPos < 2)
                {
                    continue; // Adjacent — no intermediate nodes to skip.
                }

                var fromNode = nodeSequence[fromPos] == arc.Nodes[0].Id ? arc.Nodes[0] : arc.Nodes[1];
                var toNode = nodeSequence[toPos] == arc.Nodes[0].Id ? arc.Nodes[0] : arc.Nodes[1];

                // segments[walkStartIdx + i - 1] goes nodeSequence[i-1] → nodeSequence[i].
                // Spanning segments for fromPos..toPos = segments[walkStartIdx + fromPos] .. [walkStartIdx + toPos - 1].
                int segFromIdx = walkStartIdx + fromPos;
                int spanCount = toPos - fromPos;

                diagnosticLog?.Invoke(
                    $"[WalkTaxiway] {taxiwayName}: same-taxiway arc shortcut — collapsing {spanCount} straight segments through intermediate node(s) "
                        + $"[{string.Join(",", nodeSequence.Skip(fromPos + 1).Take(spanCount - 1))}] into arc {fromNode.Id}↔{toNode.Id}"
                );

                segments.RemoveRange(segFromIdx, spanCount);
                segments.Insert(segFromIdx, new TaxiRouteSegment { TaxiwayName = taxiwayName, Edge = arc.Directed(fromNode, toNode) });

                changed = true;
                break; // Restart scan with updated segment list.
            }
        } while (changed);
    }

    /// <summary>
    /// Try multiple strategies to bridge from <paramref name="startNodeId"/> to the
    /// target taxiway when no direct edge exists. Returns the bridged-to node ID,
    /// or -1 if all strategies fail.
    /// </summary>
    private static int BridgeToTaxiway(
        AirportGroundLayout layout,
        int startNodeId,
        string taxiwayName,
        List<TaxiRouteSegment> segments,
        GroundNode currentNode,
        bool allowCurrentTaxiwayWalk,
        bool allowRampFallback,
        Action<string>? diagnosticLog
    )
    {
        var bridgeCandidates = new List<(int EndId, List<TaxiRouteSegment> Segs, string Strategy)>();

        // Strategy 1: BFS (short hop, max 3 hops)
        var bfsSegs = new List<TaxiRouteSegment>();
        (int bfsId, _) = BfsToTaxiway(layout, startNodeId, taxiwayName, bfsSegs);
        if (bfsId != -1)
        {
            diagnosticLog?.Invoke($"[WalkTaxiway] {taxiwayName}: BFS candidate → node={bfsId} segs={bfsSegs.Count}");
            bridgeCandidates.Add((bfsId, bfsSegs, "BFS"));
        }

        // Strategy 2: Walk the current taxiway to reach the target
        if (allowCurrentTaxiwayWalk)
        {
            var walkSegs = new List<TaxiRouteSegment>();
            (int walkId, _) = WalkCurrentTaxiwayToTarget(layout, startNodeId, taxiwayName, walkSegs, diagnosticLog);
            if (walkId != -1)
            {
                diagnosticLog?.Invoke($"[WalkTaxiway] {taxiwayName}: WalkCurrentTaxiway candidate → node={walkId} segs={walkSegs.Count}");
                bridgeCandidates.Add((walkId, walkSegs, "WalkCurrent"));
            }
            else
            {
                diagnosticLog?.Invoke($"[WalkTaxiway] {taxiwayName}: WalkCurrentTaxiway returned -1");
            }
        }

        if (bridgeCandidates.Count > 0)
        {
            var best = bridgeCandidates.MinBy(c => ScoreBridgePath(c.Segs));
            diagnosticLog?.Invoke($"[WalkTaxiway] {taxiwayName}: picked {best.Strategy} → node={best.EndId} (score={ScoreBridgePath(best.Segs):F4})");
            segments.AddRange(best.Segs);
            return best.EndId;
        }

        // Strategy 3: Bridge via runway centerline edges (e.g., D→F across a runway)
        (int rwyEndId, _) = BridgeViaRunwayEdges(layout, startNodeId, taxiwayName, segments);
        if (rwyEndId != -1)
        {
            return rwyEndId;
        }

        if (!allowRampFallback)
        {
            return -1;
        }

        // Strategy 4: Straight-line RAMP fallback for disconnected parking/ramp areas
        (int nearestId, double nearestDist, _) = FindNearestNodeOnTaxiway(layout, currentNode, taxiwayName);
        if (nearestId == -1)
        {
            return -1;
        }

        if (!layout.Nodes.TryGetValue(nearestId, out var rampTarget))
        {
            return -1;
        }

        if (RampCrossesRunway(layout, currentNode, rampTarget))
        {
            diagnosticLog?.Invoke($"[WalkTaxiway] RAMP {startNodeId}→{nearestId} rejected: crosses runway");
            return -1;
        }

        var rampEdge = new GroundEdge
        {
            Nodes = [currentNode, rampTarget],
            TaxiwayName = "RAMP",
            DistanceNm = nearestDist,
            Origin = "Pathfinder:virtual-ramp-edge",
        };
        segments.Add(new TaxiRouteSegment { TaxiwayName = "RAMP", Edge = rampEdge.Directed(currentNode, rampTarget) });
        return nearestId;
    }

    /// <summary>
    /// When the starting node has multiple edges on the same taxiway, pick the
    /// direction that leads toward the next taxiway. Falls back to first edge.
    /// </summary>
    private static IGroundEdge PickBestStartEdge(
        AirportGroundLayout layout,
        int startNodeId,
        List<IGroundEdge> candidates,
        string? nextTaxiwayName,
        GroundNode? destinationHint = null
    )
    {
        if (candidates.Count <= 1)
        {
            return candidates[0];
        }

        // When no next taxiway, use destination hint (e.g., hold-short node for destination runway)
        if (nextTaxiwayName is null)
        {
            if (destinationHint is null)
            {
                return candidates[0];
            }

            IGroundEdge bestHint = candidates[0];
            double bestHintDist = double.MaxValue;
            foreach (var edge in candidates)
            {
                int destId = edge.OtherNodeId(startNodeId);
                if (layout.Nodes.TryGetValue(destId, out var destNode))
                {
                    double dist = GeoMath.DistanceNm(destNode.Position, destinationHint.Position);
                    if (dist < bestHintDist)
                    {
                        bestHintDist = dist;
                        bestHint = edge;
                    }
                }
            }

            return bestHint;
        }

        // Find the nearest node that has an edge on the next taxiway (our goal)
        var goalNode = FindNearestNodeOnTaxiway(layout, layout.Nodes[startNodeId], nextTaxiwayName);

        if (goalNode.NodeId == -1)
        {
            return candidates[0];
        }

        var goal = layout.Nodes[goalNode.NodeId];

        // Score each candidate: prefer the one whose destination is closer to the goal
        IGroundEdge best = candidates[0];
        double bestDist = double.MaxValue;

        foreach (var edge in candidates)
        {
            int destId = edge.OtherNodeId(startNodeId);
            if (layout.Nodes.TryGetValue(destId, out var destNode))
            {
                double dist = GeoMath.DistanceNm(destNode.Position, goal.Position);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = edge;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// During the walk loop, when multiple unvisited edges branch on the same taxiway,
    /// prefer the one leading toward the next taxiway.
    /// </summary>
    private static (IGroundEdge Edge, int NodeId) PickBestWalkEdge(
        AirportGroundLayout layout,
        List<(IGroundEdge Edge, int NodeId)> candidates,
        string? nextTaxiwayName,
        GroundNode? destinationHint = null
    )
    {
        if (nextTaxiwayName is null)
        {
            if (destinationHint is null)
            {
                return candidates[0];
            }

            // Pick the candidate closest to the destination hint
            (IGroundEdge Edge, int NodeId) bestHint = candidates[0];
            double bestHintDist = double.MaxValue;
            foreach (var (edge, nodeId) in candidates)
            {
                if (layout.Nodes.TryGetValue(nodeId, out var node))
                {
                    double dist = GeoMath.DistanceNm(node.Position, destinationHint.Position);
                    if (dist < bestHintDist)
                    {
                        bestHintDist = dist;
                        bestHint = (edge, nodeId);
                    }
                }
            }

            return bestHint;
        }

        // Check if any candidate directly connects to the next taxiway
        foreach (var (edge, nodeId) in candidates)
        {
            if (NodeHasEdgeTo(layout, nodeId, nextTaxiwayName))
            {
                return (edge, nodeId);
            }
        }

        // Otherwise pick the candidate whose node has an edge on nextTaxiway nearest
        // (simple heuristic: check if the next taxiway's nearest node is closer)
        (IGroundEdge Edge, int NodeId) best = candidates[0];
        double bestDist = double.MaxValue;

        foreach (var (edge, nodeId) in candidates)
        {
            if (layout.Nodes.TryGetValue(nodeId, out var node))
            {
                // Find how close this candidate's destination is to any node on the next taxiway
                double minDist = MinDistToTaxiway(layout, node, nextTaxiwayName);
                if (minDist < bestDist)
                {
                    bestDist = minDist;
                    best = (edge, nodeId);
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Returns the minimum distance from a node to any node on the named taxiway.
    /// </summary>
    private static double MinDistToTaxiway(AirportGroundLayout layout, GroundNode fromNode, string taxiwayName)
    {
        double minDist = double.MaxValue;
        foreach (var node in layout.GetNodesOnTaxiway(taxiwayName))
        {
            if (node.Id == fromNode.Id)
            {
                continue;
            }

            double dist = GeoMath.DistanceNm(fromNode.Position, node.Position);
            if (dist < minDist)
            {
                minDist = dist;
            }
        }

        return minDist;
    }

    /// <summary>
    /// A* pathfinding with a pluggable cost function and heuristic.
    /// <paramref name="costFn"/> receives (currentEdge, previousEdge) and returns the cost.
    /// previousEdge is null for the first edge from the start node.
    /// <paramref name="heuristicFn"/> takes straight-line distance (nm) between two
    /// nodes and returns an admissible (never-overestimate) lower bound on remaining
    /// cost in the same units as <paramref name="costFn"/>. Pass distance-as-nm for
    /// the Shortest strategy; scale down for FewestTurns / Fastest whose costs are
    /// in different units. An inadmissible heuristic breaks A*'s optimality
    /// guarantee — it terminates on the first goal pop, which can be a longer
    /// detour than the actual minimum-cost path.
    /// </summary>
    private static TaxiRoute? FindRouteInternal(
        AirportGroundLayout layout,
        int fromNodeId,
        int toNodeId,
        HashSet<(int, int)>? blockedEdges,
        HashSet<int>? blockedNodes,
        Func<IGroundEdge, IGroundEdge?, double> costFn,
        Func<double, double> heuristicFn
    )
    {
        if (!layout.Nodes.TryGetValue(fromNodeId, out var startNode) || !layout.Nodes.TryGetValue(toNodeId, out var endNode))
        {
            return null;
        }

        var openSet = new PriorityQueue<int, double>();
        var cameFrom = new Dictionary<int, (int NodeId, IGroundEdge Edge)>();
        var gScore = new Dictionary<int, double> { [fromNodeId] = 0 };
        var closedSet = new HashSet<int>();

        double heuristic = heuristicFn(GeoMath.DistanceNm(startNode.Position, endNode.Position));
        openSet.Enqueue(fromNodeId, heuristic);

        while (openSet.Count > 0)
        {
            int current = openSet.Dequeue();
            if (current == toNodeId)
            {
                return ReconstructRoute(layout, cameFrom, toNodeId);
            }

            // Skip stale priority queue entries — this node was already expanded
            if (!closedSet.Add(current))
            {
                continue;
            }

            if (!layout.Nodes.TryGetValue(current, out var currentNode))
            {
                continue;
            }

            double currentG = gScore.GetValueOrDefault(current, double.MaxValue);

            foreach (var edge in currentNode.Edges)
            {
                int neighbor = edge.OtherNodeId(current);

                if (blockedNodes is not null && blockedNodes.Contains(neighbor))
                {
                    continue;
                }

                if (blockedEdges is not null && (blockedEdges.Contains((current, neighbor)) || blockedEdges.Contains((neighbor, current))))
                {
                    continue;
                }

                IGroundEdge? prevEdge = cameFrom.TryGetValue(current, out var prevEntry) ? prevEntry.Edge : null;
                double tentativeG = currentG + costFn(edge, prevEdge);

                if (tentativeG >= gScore.GetValueOrDefault(neighbor, double.MaxValue))
                {
                    continue;
                }

                cameFrom[neighbor] = (current, edge);
                gScore[neighbor] = tentativeG;

                if (!layout.Nodes.TryGetValue(neighbor, out var neighborNode))
                {
                    continue;
                }

                double h = heuristicFn(GeoMath.DistanceNm(neighborNode.Position, endNode.Position));
                openSet.Enqueue(neighbor, tentativeG + h);
            }
        }

        return null;
    }

    /// <summary>
    /// Dijkstra from startNodeId, following only RWY* edges (runway centerline),
    /// to reach the nearest node that has an edge on the target taxiway.
    /// This bridges taxiways that cross the same runway (e.g., D→F at OAK via
    /// runway 15/33) without allowing traversal via other named taxiways.
    /// Limited to <see cref="MaxRunwayBridgeNm"/> to prevent long runway walks.
    /// </summary>
    private static (int FoundId, IGroundEdge? FoundEdge) BridgeViaRunwayEdges(
        AirportGroundLayout layout,
        int startNodeId,
        string taxiwayName,
        List<TaxiRouteSegment> segments
    )
    {
        var openSet = new PriorityQueue<int, double>();
        var cameFrom = new Dictionary<int, (int NodeId, IGroundEdge Edge)>();
        var gScore = new Dictionary<int, double> { [startNodeId] = 0 };
        openSet.Enqueue(startNodeId, 0);

        int foundId = -1;

        while (openSet.Count > 0)
        {
            int current = openSet.Dequeue();

            if (current != startNodeId && NodeHasEdgeTo(layout, current, taxiwayName))
            {
                foundId = current;
                break;
            }

            if (!layout.Nodes.TryGetValue(current, out var currentNode))
            {
                continue;
            }

            double currentG = gScore.GetValueOrDefault(current, double.MaxValue);

            foreach (var edge in currentNode.Edges)
            {
                // Only follow runway centerline edges
                if (!edge.IsRunwayCenterline)
                {
                    continue;
                }

                int neighbor = edge.OtherNodeId(current);
                double tentativeG = currentG + edge.DistanceNm;

                if (tentativeG > MaxRunwayBridgeNm)
                {
                    continue;
                }

                if (tentativeG >= gScore.GetValueOrDefault(neighbor, double.MaxValue))
                {
                    continue;
                }

                cameFrom[neighbor] = (current, edge);
                gScore[neighbor] = tentativeG;
                openSet.Enqueue(neighbor, tentativeG);
            }
        }

        if (foundId == -1)
        {
            return (-1, null);
        }

        // Reconstruct path
        var pathSegments = new List<TaxiRouteSegment>();
        int traceId = foundId;
        while (cameFrom.TryGetValue(traceId, out var prev))
        {
            if (layout.Nodes.TryGetValue(prev.NodeId, out var bridgeFromNode) && layout.Nodes.TryGetValue(traceId, out var bridgeToNode))
            {
                pathSegments.Add(
                    new TaxiRouteSegment { TaxiwayName = prev.Edge.TaxiwayName, Edge = prev.Edge.Directed(bridgeFromNode, bridgeToNode) }
                );
            }

            traceId = prev.NodeId;
        }

        pathSegments.Reverse();
        segments.AddRange(pathSegments);

        IGroundEdge? foundEdge = null;
        if (layout.Nodes.TryGetValue(foundId, out var endNode))
        {
            foreach (var edge in endNode.Edges)
            {
                if (edge.MatchesTaxiway(taxiwayName))
                {
                    foundEdge = edge;
                    break;
                }
            }
        }

        return (foundId, foundEdge);
    }

    /// <summary>
    /// Walk along whatever taxiway the aircraft is currently on to reach the
    /// target taxiway. For each non-target, non-runway taxiway on the start node,
    /// attempts a directed walk toward the target. Picks the shortest successful
    /// walk. This lets users omit the current taxiway from instructions (e.g.,
    /// aircraft on W5 told "TAXI W V T" doesn't need to say "TAXI W5 W V T").
    /// </summary>
    private static (int FoundId, IGroundEdge? FoundEdge) WalkCurrentTaxiwayToTarget(
        AirportGroundLayout layout,
        int startNodeId,
        string targetTaxiwayName,
        List<TaxiRouteSegment> segments,
        Action<string>? diagnosticLog = null
    )
    {
        if (!layout.Nodes.TryGetValue(startNodeId, out var startNode))
        {
            return (-1, null);
        }

        // Collect distinct taxiway names on the start node (excluding target and runways)
        var candidateNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var edge in startNode.Edges)
        {
            if (!edge.MatchesTaxiway(targetTaxiwayName) && !edge.IsRunwayCenterline && !edge.IsRamp)
            {
                candidateNames.Add(edge.TaxiwayName);
            }
        }

        diagnosticLog?.Invoke($"[WalkCurrent] start={startNodeId} target={targetTaxiwayName} candidateTaxiways=[{string.Join(",", candidateNames)}]");

        if (candidateNames.Count == 0)
        {
            return (-1, null);
        }

        // Try walking each candidate taxiway toward the target; keep the shortest
        List<TaxiRouteSegment>? bestSegments = null;
        int bestEndId = -1;
        IGroundEdge? bestEdge = null;

        foreach (string twName in candidateNames)
        {
            var trialSegments = new List<TaxiRouteSegment>();
            (int endId, IGroundEdge? edge) = WalkTaxiwayToward(layout, startNodeId, twName, targetTaxiwayName, trialSegments, diagnosticLog);
            diagnosticLog?.Invoke($"[WalkCurrent] tried walk via {twName}: endId={endId} segs={trialSegments.Count}");

            if (endId != -1 && (bestSegments is null || trialSegments.Count < bestSegments.Count))
            {
                bestSegments = trialSegments;
                bestEndId = endId;
                bestEdge = edge;
            }
        }

        if (bestSegments is null)
        {
            return (-1, null);
        }

        segments.AddRange(bestSegments);
        return (bestEndId, bestEdge);
    }

    /// <summary>
    /// Walk along <paramref name="walkTaxiwayName"/> from the start node until
    /// reaching a node that connects to <paramref name="targetTaxiwayName"/>.
    /// At forks, prefers the branch closer to the nearest target-taxiway node.
    /// Returns (-1, null) if the walk dead-ends without reaching the target.
    /// </summary>
    private static (int FoundId, IGroundEdge? FoundEdge) WalkTaxiwayToward(
        AirportGroundLayout layout,
        int startNodeId,
        string walkTaxiwayName,
        string targetTaxiwayName,
        List<TaxiRouteSegment> trialSegments,
        Action<string>? diagnosticLog = null
    )
    {
        // BFS over the walkTaxiwayName-only sub-graph to find the shortest hop count
        // path to a node that has an edge on targetTaxiwayName. A real BFS handles
        // multi-fork topologies correctly — a greedy walk biased by geographic distance
        // can pick the wrong fork at a junction (leading into a dead-end spur) and
        // fail to discover an A-connected node down the other branch.
        var visited = new HashSet<int> { startNodeId };
        var cameFrom = new Dictionary<int, (int ParentId, IGroundEdge Edge)>();
        var queue = new Queue<int>();
        queue.Enqueue(startNodeId);

        int foundId = -1;
        IGroundEdge? foundTargetEdge = null;

        while (queue.Count > 0)
        {
            int currentId = queue.Dequeue();
            if (!layout.Nodes.TryGetValue(currentId, out var node))
            {
                continue;
            }

            // Already standing on target? Only true for the start node — handled by caller.
            if (currentId != startNodeId && NodeHasEdgeTo(layout, currentId, targetTaxiwayName))
            {
                foundId = currentId;
                foreach (var e in node.Edges)
                {
                    if (e.MatchesTaxiway(targetTaxiwayName))
                    {
                        foundTargetEdge = e;
                        break;
                    }
                }
                break;
            }

            foreach (var edge in node.Edges)
            {
                if (!edge.MatchesTaxiway(walkTaxiwayName))
                {
                    continue;
                }

                int otherId = edge.OtherNodeId(currentId);
                if (!visited.Add(otherId))
                {
                    continue;
                }

                cameFrom[otherId] = (currentId, edge);
                queue.Enqueue(otherId);
            }
        }

        if (foundId == -1)
        {
            diagnosticLog?.Invoke($"[WalkToward] BFS via {walkTaxiwayName} from {startNodeId} → no node connects to {targetTaxiwayName}");
            return (-1, null);
        }

        // Reconstruct the path from start to foundId.
        var path = new List<int>();
        int trace = foundId;
        while (trace != startNodeId)
        {
            path.Add(trace);
            trace = cameFrom[trace].ParentId;
        }
        path.Reverse();

        int prevId = startNodeId;
        foreach (int id in path)
        {
            var (_, walkEdge) = cameFrom[id];
            if (layout.Nodes.TryGetValue(prevId, out var fromNode) && layout.Nodes.TryGetValue(id, out var toNode))
            {
                trialSegments.Add(new TaxiRouteSegment { TaxiwayName = walkTaxiwayName, Edge = walkEdge.Directed(fromNode, toNode) });
            }
            prevId = id;
        }

        diagnosticLog?.Invoke($"[WalkToward] BFS via {walkTaxiwayName} from {startNodeId} → {foundId} ({path.Count} hops)");
        return (foundId, foundTargetEdge);
    }

    /// <summary>
    /// BFS from startNodeId (max 3 hops) to find the nearest graph-connected
    /// Scores a bridge path by taxiway transitions (primary) and total distance (tiebreaker).
    /// Lower score = better. Prefers paths that stay on the same taxiway over multi-taxiway hops.
    /// </summary>
    private static double ScoreBridgePath(List<TaxiRouteSegment> segs)
    {
        double dist = 0;
        int transitions = 0;
        for (int i = 0; i < segs.Count; i++)
        {
            dist += segs[i].Edge.DistanceNm;
            if ((i > 0) && !string.Equals(segs[i].TaxiwayName, segs[i - 1].TaxiwayName, StringComparison.OrdinalIgnoreCase))
            {
                transitions++;
            }
        }

        return (transitions * FewestTurnsPenalty) + dist;
    }

    /// <summary>
    /// BFS from startNodeId (max 3 hops) to find the nearest graph-connected
    /// node that has an edge on the target taxiway. Adds connecting segments.
    /// Returns (-1, null) if no path found.
    /// </summary>
    private static (int FoundId, IGroundEdge? FoundEdge) BfsToTaxiway(
        AirportGroundLayout layout,
        int startNodeId,
        string taxiwayName,
        List<TaxiRouteSegment> segments
    )
    {
        const int maxHops = 3;
        var visited = new HashSet<int> { startNodeId };
        var queue = new Queue<(int NodeId, int Depth)>();
        var cameFrom = new Dictionary<int, (int ParentId, IGroundEdge Edge)>();
        queue.Enqueue((startNodeId, 0));

        int foundId = -1;
        IGroundEdge? foundEdge = null;

        while (queue.Count > 0 && foundId == -1)
        {
            var (nodeId, depth) = queue.Dequeue();
            if (!layout.Nodes.TryGetValue(nodeId, out var node))
            {
                continue;
            }

            foreach (var edge in node.Edges)
            {
                int neighborId = edge.OtherNodeId(nodeId);

                if (!visited.Add(neighborId))
                {
                    continue;
                }

                cameFrom[neighborId] = (nodeId, edge);

                if (layout.Nodes.TryGetValue(neighborId, out var neighborNode))
                {
                    foreach (var nEdge in neighborNode.Edges)
                    {
                        if (nEdge.MatchesTaxiway(taxiwayName))
                        {
                            foundId = neighborId;
                            foundEdge = nEdge;
                            break;
                        }
                    }
                }

                if (foundId != -1)
                {
                    break;
                }

                if (depth + 1 < maxHops)
                {
                    queue.Enqueue((neighborId, depth + 1));
                }
            }
        }

        if (foundId == -1)
        {
            return (-1, null);
        }

        // Reconstruct path and add connecting segments
        var pathNodes = new List<int>();
        int traceId = foundId;
        while (traceId != startNodeId)
        {
            pathNodes.Add(traceId);
            traceId = cameFrom[traceId].ParentId;
        }

        pathNodes.Reverse();

        int prevId = startNodeId;
        foreach (int id in pathNodes)
        {
            var (_, connectEdge) = cameFrom[id];
            if (layout.Nodes.TryGetValue(prevId, out var bfsFromNode) && layout.Nodes.TryGetValue(id, out var bfsToNode))
            {
                segments.Add(new TaxiRouteSegment { TaxiwayName = connectEdge.TaxiwayName, Edge = connectEdge.Directed(bfsFromNode, bfsToNode) });
            }

            prevId = id;
        }

        return (foundId, foundEdge);
    }

    /// <summary>
    /// Straight-line search: find the geographically nearest node that has
    /// an edge on the target taxiway. Used for parking/ramp areas where
    /// graph connectivity to the taxiway may be missing.
    /// </summary>
    private static (int NodeId, double DistNm, IGroundEdge? Edge) FindNearestNodeOnTaxiway(
        AirportGroundLayout layout,
        GroundNode fromNode,
        string taxiwayName
    )
    {
        int nearestId = -1;
        double nearestDist = double.MaxValue;
        IGroundEdge? nearestEdge = null;

        foreach (var node in layout.Nodes.Values)
        {
            if (node.Id == fromNode.Id)
            {
                continue;
            }

            foreach (var edge in node.Edges)
            {
                if (!edge.MatchesTaxiway(taxiwayName))
                {
                    continue;
                }

                double dist = GeoMath.DistanceNm(fromNode.Position, node.Position);

                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearestId = node.Id;
                    nearestEdge = edge;
                }

                break;
            }
        }

        return (nearestId, nearestDist, nearestEdge);
    }

    /// <summary>
    /// Check whether a straight-line RAMP segment between two nodes would cross
    /// any runway. Samples interior points along the line and tests each against
    /// every runway rectangle in the layout.
    /// </summary>
    private static bool RampCrossesRunway(AirportGroundLayout layout, GroundNode fromNode, GroundNode toNode)
    {
        if (layout.Runways.Count == 0)
        {
            return false;
        }

        var rects = new RunwayRectangle[layout.Runways.Count];
        for (int i = 0; i < layout.Runways.Count; i++)
        {
            rects[i] = RunwayCrossingDetector.BuildRunwayRectangle(layout.Runways[i]);
        }

        // Sample 9 interior points (fractions 0.1..0.9), excluding endpoints
        for (int step = 1; step <= 9; step++)
        {
            double frac = step / 10.0;
            double lat = fromNode.Position.Lat + ((toNode.Position.Lat - fromNode.Position.Lat) * frac);
            double lon = fromNode.Position.Lon + ((toNode.Position.Lon - fromNode.Position.Lon) * frac);

            foreach (ref readonly var rect in rects.AsSpan())
            {
                if (RunwayCrossingDetector.IsOnRunway(lat, lon, rect))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// For a junction arc with multiple taxiway names, pick the name that matches the
    /// adjacent segment to keep the route summary clean. Falls back to TaxiwayNames[0].
    /// Note: during reconstruction segments are built in reverse, so the "adjacent" segment
    /// is the last one added (which is the next segment in forward order).
    /// </summary>
    private static string ResolveArcSegmentName(IGroundEdge edge, List<TaxiRouteSegment> segments)
    {
        if (edge is not GroundArc { TaxiwayNames.Length: > 1 } arc)
        {
            return edge.TaxiwayName;
        }

        // segments are in reverse order — last added = next segment in forward direction
        if (segments.Count > 0)
        {
            string nextName = segments[^1].TaxiwayName;
            if (arc.MatchesTaxiway(nextName))
            {
                return nextName;
            }
        }

        return arc.TaxiwayNames[0];
    }

    private static TaxiRoute ReconstructRoute(AirportGroundLayout layout, Dictionary<int, (int NodeId, IGroundEdge Edge)> cameFrom, int endNodeId)
    {
        var segments = new List<TaxiRouteSegment>();
        int current = endNodeId;

        while (cameFrom.TryGetValue(current, out var prev))
        {
            if (layout.Nodes.TryGetValue(prev.NodeId, out var reconFromNode) && layout.Nodes.TryGetValue(current, out var reconToNode))
            {
                // For junction arcs connecting two taxiways, use the name that's
                // consistent with the adjacent segment to avoid spurious transitions.
                string segName = ResolveArcSegmentName(prev.Edge, segments);
                segments.Add(new TaxiRouteSegment { TaxiwayName = segName, Edge = prev.Edge.Directed(reconFromNode, reconToNode) });
            }

            current = prev.NodeId;
        }

        segments.Reverse();

        var holdShorts = new List<HoldShortPoint>();
        HoldShortAnnotator.AddImplicitRunwayHoldShorts(layout, segments, holdShorts);

        return new TaxiRoute { Segments = segments, HoldShortPoints = holdShorts };
    }
}
