using Yaat.Sim.Phases;

namespace Yaat.Sim.Data.Airport;

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
    /// Heavy cost penalty (nm-equivalent) added when A* traverses a runway
    /// centerline edge. Prevents backtaxi/through-taxi on runways unless no
    /// taxiway-only path exists.
    /// </summary>
    internal const double RunwayEdgePenaltyNm = 50.0;

    /// <summary>
    /// Cost penalty (nm-equivalent) added when A* transitions from one taxiway
    /// to another. Biases toward routes with fewer taxiway changes.
    /// </summary>
    internal const double TaxiwayTransitionPenaltyNm = 0.15;

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
    /// Supports !nodeId tokens for exact node references (A* between them).
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
        List<string>? explicitHoldShorts = null,
        string? destinationRunway = null,
        IRunwayLookup? runways = null,
        string? airportId = null,
        Action<string>? diagnosticLog = null
    )
    {
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
                    double dist = GeoMath.DistanceNm(hsn.Latitude, hsn.Longitude, startNode.Latitude, startNode.Longitude);
                    diagnosticLog?.Invoke(
                        $"[Pathfinder]   holdShort candidate id={hsn.Id} lat={hsn.Latitude:F6} lon={hsn.Longitude:F6} dist={dist:F4}nm edges=[{string.Join(",", hsn.Edges.Select(e => e.TaxiwayName))}]"
                    );
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        destinationHint = hsn;
                    }
                }
            }

            diagnosticLog?.Invoke(
                $"[Pathfinder] destinationHint={destinationHint?.Id.ToString() ?? "null"} lat={destinationHint?.Latitude:F6} lon={destinationHint?.Longitude:F6} edges=[{string.Join(",", destinationHint?.Edges.Select(e => e.TaxiwayName) ?? [])}]"
            );
        }

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
                    failReason = $"Node !{targetNodeId} does not exist";
                    return null;
                }

                if (currentNodeId != targetNodeId)
                {
                    var subRoute = FindRoute(layout, currentNodeId, targetNodeId);
                    if (subRoute is null)
                    {
                        failReason = $"No route from node {currentNodeId} to !{targetNodeId}";
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

            if (layout.Nodes.TryGetValue(currentNodeId, out var curNode))
            {
                diagnosticLog?.Invoke(
                    $"[Pathfinder] Walk[{twIdx}] taxiway={twName} nextTw={nextTwName ?? "null"} from node={currentNodeId} lat={curNode.Latitude:F6} lon={curNode.Longitude:F6} "
                        + $"edges=[{string.Join(",", curNode.Edges.Select(e => e.TaxiwayName))}] "
                        + $"hint={(passedHint is null ? "null" : passedHint.Id.ToString())} stopAtRunwayId={passedStopId ?? "null"}"
                );
            }

            bool found = WalkTaxiway(
                layout,
                currentNodeId,
                twName,
                segments,
                out int endNodeId,
                nextTwName,
                allowRampFallback: isFirstTw,
                allowCurrentTaxiwayWalk: isFirstTw,
                destinationHint: passedHint,
                stopAtRunwayId: passedStopId,
                diagnosticLog: diagnosticLog
            );

            int addedSegments = segments.Count - segCountBefore;
            diagnosticLog?.Invoke($"[Pathfinder] Walk[{twIdx}] {twName} done: found={found} addedSegments={addedSegments} endNode={endNodeId}");

            if (!found)
            {
                return null;
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
            bool inferred = TaxiVariantResolver.TryInferVariant(
                layout,
                segments,
                taxiwayNames[^1],
                segmentCountBeforeLastTw,
                destinationRunway,
                runways,
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
    /// Find shortest route between two nodes using A*.
    /// </summary>
    public static TaxiRoute? FindRoute(AirportGroundLayout layout, int fromNodeId, int toNodeId)
    {
        return FindRouteInternal(layout, fromNodeId, toNodeId, null, null);
    }

    /// <summary>
    /// Find up to <paramref name="maxRoutes"/> distinct routes between two nodes
    /// using Yen's K-shortest paths algorithm. Routes are deduplicated by taxiway
    /// sequence (two routes with the same taxiway key keep only the shorter one).
    /// </summary>
    public static List<TaxiRoute> FindRoutes(AirportGroundLayout layout, int fromNodeId, int toNodeId, int maxRoutes = 4)
    {
        var first = FindRouteInternal(layout, fromNodeId, toNodeId, null, null);
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

                // Build blocked edges: for each existing result, if root matches,
                // block the edge at the spur index
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

                // Block nodes in root path except spur node
                var blockedNodes = new HashSet<int>();
                for (int j = 0; j < i; j++)
                {
                    blockedNodes.Add(prevSegments[j].FromNodeId);
                }

                var spurPath = FindRouteInternal(layout, spurNodeId, toNodeId, blockedEdges, blockedNodes);
                if (spurPath is null)
                {
                    continue;
                }

                // Combine root + spur
                var combinedSegments = new List<TaxiRouteSegment>();
                for (int j = 0; j < i; j++)
                {
                    combinedSegments.Add(prevSegments[j]);
                }

                combinedSegments.AddRange(spurPath.Segments);

                var holdShorts = new List<HoldShortPoint>();
                HoldShortAnnotator.AddImplicitRunwayHoldShorts(layout, combinedSegments, holdShorts);

                var combined = new TaxiRoute { Segments = combinedSegments, HoldShortPoints = holdShorts };
                candidates.Add(combined);
            }

            // Pick shortest candidate with a unique taxiway key
            candidates.Sort((a, b) => a.TotalDistanceNm.CompareTo(b.TotalDistanceNm));

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
            if (string.Equals(edge.TaxiwayName, taxiwayName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
        string? nextTaxiwayName = null,
        bool allowRampFallback = true,
        bool allowCurrentTaxiwayWalk = true,
        GroundNode? destinationHint = null,
        string? stopAtRunwayId = null,
        Action<string>? diagnosticLog = null
    )
    {
        endNodeId = startNodeId;

        if (!layout.Nodes.TryGetValue(startNodeId, out var currentNode))
        {
            return false;
        }

        // Find all edges on this taxiway from the current node
        var candidateEdges = new List<GroundEdge>();
        foreach (var edge in currentNode.Edges)
        {
            if (string.Equals(edge.TaxiwayName, taxiwayName, StringComparison.OrdinalIgnoreCase))
            {
                candidateEdges.Add(edge);
            }
        }

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

                if (!node.Edges.Any(e => string.Equals(e.TaxiwayName, taxiwayName, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                double dist = GeoMath.DistanceNm(node.Latitude, node.Longitude, startHintRef.Latitude, startHintRef.Longitude);
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
                    $"[WalkTaxiway] {taxiwayName}: computed effectiveHint from hold-short node={effectiveHint.Id} lat={effectiveHint.Latitude:F6} RunwayId={effectiveHint.RunwayId}"
                );
            }
            else if (destinationHint is not null)
            {
                diagnosticLog?.Invoke(
                    $"[WalkTaxiway] {taxiwayName}: no hold-short on taxiway for stopAtRunwayId={stopAtRunwayId} — effectiveHint cleared (passed hint was node={destinationHint.Id})"
                );
            }
        }

        GroundEdge? startEdge = candidateEdges.Count switch
        {
            0 => null,
            1 => candidateEdges[0],
            _ => PickBestStartEdge(layout, startNodeId, candidateEdges, nextTaxiwayName, effectiveHint),
        };

        if (startEdge is not null)
        {
            int firstDest = startEdge.FromNodeId == startNodeId ? startEdge.ToNodeId : startEdge.FromNodeId;
            diagnosticLog?.Invoke($"[WalkTaxiway] {taxiwayName}: startEdge → node={firstDest}");
        }

        if (startEdge is null)
        {
            diagnosticLog?.Invoke($"[WalkTaxiway] {taxiwayName}: no direct edge from node={startNodeId}, trying BFS");
            // Try short BFS first (handles normal taxiway-to-taxiway transitions)
            (int foundId, GroundEdge? foundEdge) = BfsToTaxiway(layout, startNodeId, taxiwayName, segments);

            if (foundId != -1)
            {
                diagnosticLog?.Invoke($"[WalkTaxiway] {taxiwayName}: BFS found node={foundId}");
                startNodeId = foundId;
                startEdge = foundEdge;
            }
            else
            {
                int variantEndId = -1;
                GroundEdge? variantEdge = null;

                if (allowCurrentTaxiwayWalk)
                {
                    // Walk whatever taxiway the aircraft is currently on to reach
                    // the target. Handles cases like W5→W, B→D, etc. without the
                    // user having to include the current taxiway in instructions.
                    (variantEndId, variantEdge) = WalkCurrentTaxiwayToTarget(layout, startNodeId, taxiwayName, segments);
                    diagnosticLog?.Invoke($"[WalkTaxiway] {taxiwayName}: WalkCurrentTaxiway → node={variantEndId}");
                }

                if (variantEndId != -1)
                {
                    startNodeId = variantEndId;
                    startEdge = variantEdge;
                }
                else
                {
                    // Try bridging via runway centerline edges only. This
                    // handles cases like D→F where taxiways cross the same
                    // runway but aren't directly connected in the graph.
                    (int rwyEndId, GroundEdge? rwyEdge) = BridgeViaRunwayEdges(layout, startNodeId, taxiwayName, segments);

                    if (rwyEndId != -1)
                    {
                        startNodeId = rwyEndId;
                        startEdge = rwyEdge;
                    }
                    else if (allowRampFallback)
                    {
                        // Graph is disconnected — straight-line fallback for
                        // parking/ramp areas where connectivity may be missing
                        (int nearestId, double nearestDist, GroundEdge? nearestEdge) = FindNearestNodeOnTaxiway(layout, currentNode, taxiwayName);

                        if (nearestId == -1)
                        {
                            return false;
                        }

                        segments.Add(
                            new TaxiRouteSegment
                            {
                                FromNodeId = startNodeId,
                                ToNodeId = nearestId,
                                TaxiwayName = "RAMP",
                                Edge = new GroundEdge
                                {
                                    FromNodeId = startNodeId,
                                    ToNodeId = nearestId,
                                    TaxiwayName = "RAMP",
                                    DistanceNm = nearestDist,
                                },
                            }
                        );

                        startNodeId = nearestId;
                        startEdge = nearestEdge;
                    }
                    else
                    {
                        return false;
                    }
                }
            }
        }

        // If the start node already connects to the next taxiway, this taxiway name
        // is just a directional hint at a multi-way junction (e.g., "T" in "TE T U W"
        // at the TE/T/U intersection). Skip the walk — the aircraft is already where
        // it needs to be to transition to the next taxiway.
        if (nextTaxiwayName is not null && NodeHasEdgeTo(layout, startNodeId, nextTaxiwayName))
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

            // Collect all unvisited edges on this taxiway
            var candidates = new List<(GroundEdge Edge, int NodeId)>();
            foreach (var edge in node.Edges)
            {
                if (!string.Equals(edge.TaxiwayName, taxiwayName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int otherId = edge.FromNodeId == currentId ? edge.ToNodeId : edge.FromNodeId;
                if (!visited.Contains(otherId))
                {
                    candidates.Add((edge, otherId));
                }
            }

            GroundEdge? nextEdge;
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

            if (layout.Nodes.TryGetValue(nextNodeId, out var nextNodeInfo))
            {
                string nodeType =
                    nextNodeInfo.Type == GroundNodeType.RunwayHoldShort ? $"RunwayHoldShort({nextNodeInfo.RunwayId})" : nextNodeInfo.Type.ToString();
                diagnosticLog?.Invoke(
                    $"[WalkTaxiway] {taxiwayName}: step {currentId}→{nextNodeId} ({nodeType}) lat={nextNodeInfo.Latitude:F6} edges=[{string.Join(",", nextNodeInfo.Edges.Select(e => e.TaxiwayName))}]"
                );
            }

            segments.Add(
                new TaxiRouteSegment
                {
                    FromNodeId = currentId,
                    ToNodeId = nextNodeId,
                    TaxiwayName = taxiwayName,
                    Edge = nextEdge,
                }
            );

            visited.Add(nextNodeId);
            currentId = nextNodeId;

            // Stop early if this node connects to the next taxiway in the path
            if (nextTaxiwayName is not null && NodeHasEdgeTo(layout, currentId, nextTaxiwayName))
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
        return segments.Count > 0;
    }

    /// <summary>
    /// When the starting node has multiple edges on the same taxiway, pick the
    /// direction that leads toward the next taxiway. Falls back to first edge.
    /// </summary>
    private static GroundEdge PickBestStartEdge(
        AirportGroundLayout layout,
        int startNodeId,
        List<GroundEdge> candidates,
        string? nextTaxiwayName,
        GroundNode? destinationHint = null
    )
    {
        if (candidates.Count == 0)
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

            GroundEdge bestHint = candidates[0];
            double bestHintDist = double.MaxValue;
            foreach (var edge in candidates)
            {
                int destId = edge.FromNodeId == startNodeId ? edge.ToNodeId : edge.FromNodeId;
                if (layout.Nodes.TryGetValue(destId, out var destNode))
                {
                    double dist = GeoMath.DistanceNm(destNode.Latitude, destNode.Longitude, destinationHint.Latitude, destinationHint.Longitude);
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
        GroundEdge best = candidates[0];
        double bestDist = double.MaxValue;

        foreach (var edge in candidates)
        {
            int destId = edge.FromNodeId == startNodeId ? edge.ToNodeId : edge.FromNodeId;
            if (layout.Nodes.TryGetValue(destId, out var destNode))
            {
                double dist = GeoMath.DistanceNm(destNode.Latitude, destNode.Longitude, goal.Latitude, goal.Longitude);
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
    private static (GroundEdge Edge, int NodeId) PickBestWalkEdge(
        AirportGroundLayout layout,
        List<(GroundEdge Edge, int NodeId)> candidates,
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
            (GroundEdge Edge, int NodeId) bestHint = candidates[0];
            double bestHintDist = double.MaxValue;
            foreach (var (edge, nodeId) in candidates)
            {
                if (layout.Nodes.TryGetValue(nodeId, out var node))
                {
                    double dist = GeoMath.DistanceNm(node.Latitude, node.Longitude, destinationHint.Latitude, destinationHint.Longitude);
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
        (GroundEdge Edge, int NodeId) best = candidates[0];
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
        foreach (var node in layout.Nodes.Values)
        {
            if (node.Id == fromNode.Id)
            {
                continue;
            }

            foreach (var edge in node.Edges)
            {
                if (string.Equals(edge.TaxiwayName, taxiwayName, StringComparison.OrdinalIgnoreCase))
                {
                    double dist = GeoMath.DistanceNm(fromNode.Latitude, fromNode.Longitude, node.Latitude, node.Longitude);
                    if (dist < minDist)
                    {
                        minDist = dist;
                    }

                    break;
                }
            }
        }

        return minDist;
    }

    private static TaxiRoute? FindRouteInternal(
        AirportGroundLayout layout,
        int fromNodeId,
        int toNodeId,
        HashSet<(int, int)>? blockedEdges,
        HashSet<int>? blockedNodes
    )
    {
        if (!layout.Nodes.TryGetValue(fromNodeId, out var startNode) || !layout.Nodes.TryGetValue(toNodeId, out var endNode))
        {
            return null;
        }

        var openSet = new PriorityQueue<int, double>();
        var cameFrom = new Dictionary<int, (int NodeId, GroundEdge Edge)>();
        var gScore = new Dictionary<int, double> { [fromNodeId] = 0 };

        double heuristic = GeoMath.DistanceNm(startNode.Latitude, startNode.Longitude, endNode.Latitude, endNode.Longitude);
        openSet.Enqueue(fromNodeId, heuristic);

        while (openSet.Count > 0)
        {
            int current = openSet.Dequeue();
            if (current == toNodeId)
            {
                return ReconstructRoute(layout, cameFrom, toNodeId);
            }

            if (!layout.Nodes.TryGetValue(current, out var currentNode))
            {
                continue;
            }

            double currentG = gScore.GetValueOrDefault(current, double.MaxValue);

            foreach (var edge in currentNode.Edges)
            {
                int neighbor = edge.FromNodeId == current ? edge.ToNodeId : edge.FromNodeId;

                if (blockedNodes is not null && blockedNodes.Contains(neighbor))
                {
                    continue;
                }

                if (blockedEdges is not null && (blockedEdges.Contains((current, neighbor)) || blockedEdges.Contains((neighbor, current))))
                {
                    continue;
                }

                double penalty = 0;
                if (edge.TaxiwayName.StartsWith("RWY", StringComparison.OrdinalIgnoreCase))
                {
                    penalty += RunwayEdgePenaltyNm;
                }

                if (
                    cameFrom.TryGetValue(current, out var prev)
                    && !string.Equals(prev.Edge.TaxiwayName, edge.TaxiwayName, StringComparison.OrdinalIgnoreCase)
                )
                {
                    penalty += TaxiwayTransitionPenaltyNm;
                }

                double tentativeG = currentG + edge.DistanceNm + penalty;

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

                double h = GeoMath.DistanceNm(neighborNode.Latitude, neighborNode.Longitude, endNode.Latitude, endNode.Longitude);
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
    private static (int FoundId, GroundEdge? FoundEdge) BridgeViaRunwayEdges(
        AirportGroundLayout layout,
        int startNodeId,
        string taxiwayName,
        List<TaxiRouteSegment> segments
    )
    {
        var openSet = new PriorityQueue<int, double>();
        var cameFrom = new Dictionary<int, (int NodeId, GroundEdge Edge)>();
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
                if (!edge.TaxiwayName.StartsWith("RWY", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int neighbor = edge.FromNodeId == current ? edge.ToNodeId : edge.FromNodeId;
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
            pathSegments.Add(
                new TaxiRouteSegment
                {
                    FromNodeId = prev.NodeId,
                    ToNodeId = traceId,
                    TaxiwayName = prev.Edge.TaxiwayName,
                    Edge = prev.Edge,
                }
            );
            traceId = prev.NodeId;
        }

        pathSegments.Reverse();
        segments.AddRange(pathSegments);

        GroundEdge? foundEdge = null;
        if (layout.Nodes.TryGetValue(foundId, out var endNode))
        {
            foreach (var edge in endNode.Edges)
            {
                if (string.Equals(edge.TaxiwayName, taxiwayName, StringComparison.OrdinalIgnoreCase))
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
    private static (int FoundId, GroundEdge? FoundEdge) WalkCurrentTaxiwayToTarget(
        AirportGroundLayout layout,
        int startNodeId,
        string targetTaxiwayName,
        List<TaxiRouteSegment> segments
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
            if (
                !string.Equals(edge.TaxiwayName, targetTaxiwayName, StringComparison.OrdinalIgnoreCase)
                && !edge.TaxiwayName.StartsWith("RWY", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(edge.TaxiwayName, "RAMP", StringComparison.OrdinalIgnoreCase)
            )
            {
                candidateNames.Add(edge.TaxiwayName);
            }
        }

        if (candidateNames.Count == 0)
        {
            return (-1, null);
        }

        // Try walking each candidate taxiway toward the target; keep the shortest
        List<TaxiRouteSegment>? bestSegments = null;
        int bestEndId = -1;
        GroundEdge? bestEdge = null;

        foreach (string twName in candidateNames)
        {
            var trialSegments = new List<TaxiRouteSegment>();
            (int endId, GroundEdge? edge) = WalkTaxiwayToward(layout, startNodeId, twName, targetTaxiwayName, trialSegments);

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
    private static (int FoundId, GroundEdge? FoundEdge) WalkTaxiwayToward(
        AirportGroundLayout layout,
        int startNodeId,
        string walkTaxiwayName,
        string targetTaxiwayName,
        List<TaxiRouteSegment> trialSegments
    )
    {
        int currentId = startNodeId;
        var visited = new HashSet<int> { currentId };

        while (true)
        {
            if (!layout.Nodes.TryGetValue(currentId, out var node))
            {
                break;
            }

            // Collect unvisited edges on the walk taxiway
            var candidates = new List<(GroundEdge Edge, int NodeId)>();
            foreach (var edge in node.Edges)
            {
                if (!string.Equals(edge.TaxiwayName, walkTaxiwayName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int otherId = edge.FromNodeId == currentId ? edge.ToNodeId : edge.FromNodeId;
                if (!visited.Contains(otherId))
                {
                    candidates.Add((edge, otherId));
                }
            }

            if (candidates.Count == 0)
            {
                break;
            }

            // Pick the candidate closest to any node on the target taxiway
            GroundEdge nextEdge;
            int nextNodeId;
            if (candidates.Count == 1)
            {
                (nextEdge, nextNodeId) = candidates[0];
            }
            else
            {
                (nextEdge, nextNodeId) = PickBestWalkEdge(layout, candidates, targetTaxiwayName);
            }

            trialSegments.Add(
                new TaxiRouteSegment
                {
                    FromNodeId = currentId,
                    ToNodeId = nextNodeId,
                    TaxiwayName = walkTaxiwayName,
                    Edge = nextEdge,
                }
            );

            visited.Add(nextNodeId);
            currentId = nextNodeId;

            if (NodeHasEdgeTo(layout, currentId, targetTaxiwayName))
            {
                GroundEdge? targetEdge = null;
                if (layout.Nodes.TryGetValue(currentId, out var targetNode))
                {
                    foreach (var edge in targetNode.Edges)
                    {
                        if (string.Equals(edge.TaxiwayName, targetTaxiwayName, StringComparison.OrdinalIgnoreCase))
                        {
                            targetEdge = edge;
                            break;
                        }
                    }
                }

                return (currentId, targetEdge);
            }
        }

        return (-1, null);
    }

    /// <summary>
    /// BFS from startNodeId (max 3 hops) to find the nearest graph-connected
    /// node that has an edge on the target taxiway. Adds connecting segments.
    /// Returns (-1, null) if no path found.
    /// </summary>
    private static (int FoundId, GroundEdge? FoundEdge) BfsToTaxiway(
        AirportGroundLayout layout,
        int startNodeId,
        string taxiwayName,
        List<TaxiRouteSegment> segments
    )
    {
        const int maxHops = 3;
        var visited = new HashSet<int> { startNodeId };
        var queue = new Queue<(int NodeId, int Depth)>();
        var cameFrom = new Dictionary<int, (int ParentId, GroundEdge Edge)>();
        queue.Enqueue((startNodeId, 0));

        int foundId = -1;
        GroundEdge? foundEdge = null;

        while (queue.Count > 0 && foundId == -1)
        {
            var (nodeId, depth) = queue.Dequeue();
            if (!layout.Nodes.TryGetValue(nodeId, out var node))
            {
                continue;
            }

            foreach (var edge in node.Edges)
            {
                int neighborId = edge.FromNodeId == nodeId ? edge.ToNodeId : edge.FromNodeId;

                if (!visited.Add(neighborId))
                {
                    continue;
                }

                cameFrom[neighborId] = (nodeId, edge);

                if (layout.Nodes.TryGetValue(neighborId, out var neighborNode))
                {
                    foreach (var nEdge in neighborNode.Edges)
                    {
                        if (string.Equals(nEdge.TaxiwayName, taxiwayName, StringComparison.OrdinalIgnoreCase))
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
            segments.Add(
                new TaxiRouteSegment
                {
                    FromNodeId = prevId,
                    ToNodeId = id,
                    TaxiwayName = connectEdge.TaxiwayName,
                    Edge = connectEdge,
                }
            );
            prevId = id;
        }

        return (foundId, foundEdge);
    }

    /// <summary>
    /// Straight-line search: find the geographically nearest node that has
    /// an edge on the target taxiway. Used for parking/ramp areas where
    /// graph connectivity to the taxiway may be missing.
    /// </summary>
    private static (int NodeId, double DistNm, GroundEdge? Edge) FindNearestNodeOnTaxiway(
        AirportGroundLayout layout,
        GroundNode fromNode,
        string taxiwayName
    )
    {
        int nearestId = -1;
        double nearestDist = double.MaxValue;
        GroundEdge? nearestEdge = null;

        foreach (var node in layout.Nodes.Values)
        {
            if (node.Id == fromNode.Id)
            {
                continue;
            }

            foreach (var edge in node.Edges)
            {
                if (!string.Equals(edge.TaxiwayName, taxiwayName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                double dist = GeoMath.DistanceNm(fromNode.Latitude, fromNode.Longitude, node.Latitude, node.Longitude);

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

    private static TaxiRoute ReconstructRoute(AirportGroundLayout layout, Dictionary<int, (int NodeId, GroundEdge Edge)> cameFrom, int endNodeId)
    {
        var segments = new List<TaxiRouteSegment>();
        int current = endNodeId;

        while (cameFrom.TryGetValue(current, out var prev))
        {
            segments.Add(
                new TaxiRouteSegment
                {
                    FromNodeId = prev.NodeId,
                    ToNodeId = current,
                    TaxiwayName = prev.Edge.TaxiwayName,
                    Edge = prev.Edge,
                }
            );
            current = prev.NodeId;
        }

        segments.Reverse();

        var holdShorts = new List<HoldShortPoint>();
        HoldShortAnnotator.AddImplicitRunwayHoldShorts(layout, segments, holdShorts);

        return new TaxiRoute { Segments = segments, HoldShortPoints = holdShorts };
    }
}
