using Yaat.Sim.Phases;

namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Pathfinding on the airport ground layout graph.
/// Supports explicit path validation (user specifies taxiways) and A* auto-routing.
/// </summary>
public static class TaxiPathfinder
{
    /// <summary>
    /// Validate and resolve an explicit taxiway path (e.g., "S T U W W1").
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
        string? airportId = null
    )
    {
        failReason = null;

        if (taxiwayNames.Count == 0)
        {
            return null;
        }

        var segments = new List<TaxiRouteSegment>();
        var holdShorts = new List<HoldShortPoint>();
        int currentNodeId = fromNodeId;
        int segmentCountBeforeLastTw = 0;

        for (int twIdx = 0; twIdx < taxiwayNames.Count; twIdx++)
        {
            string twName = taxiwayNames[twIdx];
            string? nextTwName = twIdx + 1 < taxiwayNames.Count ? taxiwayNames[twIdx + 1] : null;
            segmentCountBeforeLastTw = segments.Count;

            bool found = WalkTaxiway(layout, currentNodeId, twName, segments, out int endNodeId, nextTwName);

            if (!found)
            {
                return null;
            }

            currentNodeId = endNodeId;
        }

        // Auto-infer numbered taxiway variant for destination runway
        if (destinationRunway is not null && taxiwayNames.Count > 0)
        {
            bool inferred = TryInferVariant(
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
        AddImplicitRunwayHoldShorts(layout, segments, holdShorts);

        // Add explicit hold-short points
        if (explicitHoldShorts is not null)
        {
            foreach (string hsRunway in explicitHoldShorts)
            {
                AddExplicitHoldShort(layout, segments, holdShorts, hsRunway);
            }
        }

        // Add destination runway hold-short
        if (destinationRunway is not null)
        {
            AddDestinationHoldShort(layout, segments, holdShorts, destinationRunway);
        }

        return new TaxiRoute { Segments = segments, HoldShortPoints = holdShorts };
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
                AddImplicitRunwayHoldShorts(layout, combinedSegments, holdShorts);

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

                double tentativeG = currentG + edge.DistanceNm;

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
    /// Returns true if <paramref name="candidate"/> is a numbered variant of
    /// <paramref name="baseName"/> (e.g., "W1" is a variant of "W", "W10" too, "WA" is not).
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

    /// <summary>
    /// Checks if a stored runway ID (e.g., "12/30") contains the target
    /// designator (case-insensitive).
    /// </summary>
    internal static bool RunwayIdMatches(RunwayIdentifier storedRunwayId, string targetRunway)
    {
        return storedRunwayId.Contains(targetRunway);
    }

    private static bool TryInferVariant(
        AirportGroundLayout layout,
        List<TaxiRouteSegment> segments,
        string lastTaxiwayName,
        int segmentCountBeforeLastTw,
        string destinationRunway,
        IRunwayLookup? runways,
        string? airportId,
        ref int currentNodeId,
        out string? failReason
    )
    {
        failReason = null;

        // Check if route already reaches a hold-short for the destination runway
        foreach (var seg in segments)
        {
            if (
                layout.Nodes.TryGetValue(seg.ToNodeId, out var segNode)
                && segNode.Type == GroundNodeType.RunwayHoldShort
                && segNode.RunwayId is { } segRwyId
                && RunwayIdMatches(segRwyId, destinationRunway)
            )
            {
                return false;
            }
        }

        // Find hold-short nodes for the destination runway
        var variants = new List<(GroundNode HsNode, string VariantName)>();
        var nonVariantConnectors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in layout.Nodes.Values)
        {
            if (
                node.Type != GroundNodeType.RunwayHoldShort
                || node.RunwayId is not { } nodeRwyId
                || node.Edges.Count == 0
                || !RunwayIdMatches(nodeRwyId, destinationRunway)
            )
            {
                continue;
            }

            foreach (var edge in node.Edges)
            {
                string edgeName = edge.TaxiwayName;

                if (string.Equals(edgeName, lastTaxiwayName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (IsNumberedVariant(edgeName, lastTaxiwayName))
                {
                    variants.Add((node, edgeName));
                }
                else
                {
                    nonVariantConnectors.Add(edgeName);
                }
            }
        }

        if (variants.Count > 0)
        {
            return AutoExtendVariant(
                layout,
                segments,
                lastTaxiwayName,
                segmentCountBeforeLastTw,
                variants,
                runways,
                airportId,
                destinationRunway,
                ref currentNodeId
            );
        }

        if (nonVariantConnectors.Count > 0)
        {
            var connectors = string.Join(", ", nonVariantConnectors.Order());
            failReason = $"Taxi to runway {destinationRunway}: specify connecting taxiway ({connectors})";
            return false;
        }

        return false;
    }

    private static bool AutoExtendVariant(
        AirportGroundLayout layout,
        List<TaxiRouteSegment> segments,
        string lastTaxiwayName,
        int segmentCountBeforeLastTw,
        List<(GroundNode HsNode, string VariantName)> variants,
        IRunwayLookup? runways,
        string? airportId,
        string destinationRunway,
        ref int currentNodeId
    )
    {
        // Pick variant: if multiple distinct names, choose closest to runway threshold
        string chosenVariant = PickBestVariant(variants, runways, airportId, destinationRunway);

        // Find branch point: scan nodes along the last-taxiway segments
        int branchNodeId = -1;
        int branchSegmentIndex = -1;

        for (int i = segmentCountBeforeLastTw; i < segments.Count; i++)
        {
            int nodeId = i == segmentCountBeforeLastTw ? segments[i].FromNodeId : segments[i].ToNodeId;

            if (NodeHasEdgeTo(layout, nodeId, chosenVariant))
            {
                branchNodeId = nodeId;
                branchSegmentIndex = i;
                break;
            }

            // Also check ToNodeId for first segment
            if (i == segmentCountBeforeLastTw)
            {
                if (NodeHasEdgeTo(layout, segments[i].ToNodeId, chosenVariant))
                {
                    branchNodeId = segments[i].ToNodeId;
                    branchSegmentIndex = i + 1;
                    break;
                }
            }
        }

        // Check remaining ToNodeIds if not found yet
        if (branchNodeId == -1)
        {
            for (int i = segmentCountBeforeLastTw; i < segments.Count; i++)
            {
                if (NodeHasEdgeTo(layout, segments[i].ToNodeId, chosenVariant))
                {
                    branchNodeId = segments[i].ToNodeId;
                    branchSegmentIndex = i + 1;
                    break;
                }
            }
        }

        if (branchNodeId == -1)
        {
            return false;
        }

        // Truncate segments after the branch point
        if (branchSegmentIndex < segments.Count)
        {
            segments.RemoveRange(branchSegmentIndex, segments.Count - branchSegmentIndex);
        }

        // Walk the variant from the branch point
        bool walked = WalkTaxiway(layout, branchNodeId, chosenVariant, segments, out int endNodeId);

        if (walked)
        {
            currentNodeId = endNodeId;
        }

        return walked;
    }

    private static string PickBestVariant(
        List<(GroundNode HsNode, string VariantName)> variants,
        IRunwayLookup? runways,
        string? airportId,
        string destinationRunway
    )
    {
        // Collect distinct variant names
        var distinctNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, name) in variants)
        {
            distinctNames.Add(name);
        }

        if (distinctNames.Count == 1)
        {
            return variants[0].VariantName;
        }

        // Multiple variants: pick closest to runway threshold
        RunwayInfo? rwyInfo = null;
        if (runways is not null && airportId is not null)
        {
            rwyInfo = runways.GetRunway(airportId, destinationRunway);
        }

        if (rwyInfo is null)
        {
            return variants[0].VariantName;
        }

        string bestName = variants[0].VariantName;
        double bestDist = double.MaxValue;

        foreach (var (hsNode, name) in variants)
        {
            double dist = GeoMath.DistanceNm(hsNode.Latitude, hsNode.Longitude, rwyInfo.ThresholdLatitude, rwyInfo.ThresholdLongitude);

            if (dist < bestDist)
            {
                bestDist = dist;
                bestName = name;
            }
        }

        return bestName;
    }

    private static bool NodeHasEdgeTo(AirportGroundLayout layout, int nodeId, string taxiwayName)
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

    private static bool WalkTaxiway(
        AirportGroundLayout layout,
        int startNodeId,
        string taxiwayName,
        List<TaxiRouteSegment> segments,
        out int endNodeId,
        string? nextTaxiwayName = null
    )
    {
        endNodeId = startNodeId;

        if (!layout.Nodes.TryGetValue(startNodeId, out var currentNode))
        {
            return false;
        }

        // First, check if any edge directly from current node is on this taxiway
        GroundEdge? startEdge = null;
        foreach (var edge in currentNode.Edges)
        {
            if (string.Equals(edge.TaxiwayName, taxiwayName, StringComparison.OrdinalIgnoreCase))
            {
                startEdge = edge;
                break;
            }
        }

        if (startEdge is null)
        {
            // Try short BFS first (handles normal taxiway-to-taxiway transitions)
            (int foundId, GroundEdge? foundEdge) = BfsToTaxiway(layout, startNodeId, taxiwayName, segments);

            if (foundId != -1)
            {
                startNodeId = foundId;
                startEdge = foundEdge;
            }
            else
            {
                // BFS failed — straight-line search for parking/ramp areas
                // where graph connectivity may be missing
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
        }

        // Walk along the taxiway to the end
        int currentId = startNodeId;
        var visited = new HashSet<int> { currentId };

        while (true)
        {
            if (!layout.Nodes.TryGetValue(currentId, out var node))
            {
                break;
            }

            GroundEdge? nextEdge = null;
            int nextNodeId = -1;

            foreach (var edge in node.Edges)
            {
                if (!string.Equals(edge.TaxiwayName, taxiwayName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int otherId = edge.FromNodeId == currentId ? edge.ToNodeId : edge.FromNodeId;

                if (visited.Contains(otherId))
                {
                    continue;
                }

                nextEdge = edge;
                nextNodeId = otherId;
                break;
            }

            if (nextEdge is null)
            {
                break;
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
                break;
            }
        }

        endNodeId = currentId;
        return segments.Count > 0;
    }

    private static bool HoldShortExists(List<HoldShortPoint> holdShorts, int nodeId)
    {
        foreach (var hs in holdShorts)
        {
            if (hs.NodeId == nodeId)
            {
                return true;
            }
        }
        return false;
    }

    private static void AddImplicitRunwayHoldShorts(AirportGroundLayout layout, List<TaxiRouteSegment> segments, List<HoldShortPoint> holdShorts)
    {
        // Entry/exit pairing by encounter order: the first HS node for a
        // runway is the entry side (add hold-short); the second is the exit
        // side (skip and reset tracking). A third HS would be a new crossing.
        var enteredRunways = new Dictionary<RunwayIdentifier, GroundNode>();

        foreach (var seg in segments)
        {
            if (
                !layout.Nodes.TryGetValue(seg.ToNodeId, out var node)
                || node.Type != GroundNodeType.RunwayHoldShort
                || node.RunwayId is not { } rwyId
            )
            {
                continue;
            }

            if (enteredRunways.Remove(rwyId))
            {
                // Exit-side HS: paired with the previous entry, skip
                continue;
            }

            // Entry-side: track for pairing and add hold-short
            enteredRunways[rwyId] = node;

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

    private static void AddExplicitHoldShort(
        AirportGroundLayout layout,
        List<TaxiRouteSegment> segments,
        List<HoldShortPoint> holdShorts,
        string runwayId
    )
    {
        // Find nodes along the route that are hold-short for this runway
        foreach (var seg in segments)
        {
            if (!layout.Nodes.TryGetValue(seg.ToNodeId, out var node))
            {
                continue;
            }

            if (node.Type != GroundNodeType.RunwayHoldShort || node.RunwayId is not { } nodeRwyId)
            {
                continue;
            }

            if (!nodeRwyId.Contains(runwayId))
            {
                continue;
            }

            if (!HoldShortExists(holdShorts, node.Id))
            {
                holdShorts.Add(
                    new HoldShortPoint
                    {
                        NodeId = node.Id,
                        Reason = HoldShortReason.ExplicitHoldShort,
                        TargetName = runwayId,
                    }
                );
            }
        }
    }

    private static void AddDestinationHoldShort(
        AirportGroundLayout layout,
        List<TaxiRouteSegment> segments,
        List<HoldShortPoint> holdShorts,
        string runwayId
    )
    {
        if (segments.Count == 0)
        {
            return;
        }

        int lastNodeId = segments[^1].ToNodeId;
        holdShorts.Add(
            new HoldShortPoint
            {
                NodeId = lastNodeId,
                Reason = HoldShortReason.DestinationRunway,
                TargetName = runwayId,
            }
        );
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
        AddImplicitRunwayHoldShorts(layout, segments, holdShorts);

        return new TaxiRoute { Segments = segments, HoldShortPoints = holdShorts };
    }
}
