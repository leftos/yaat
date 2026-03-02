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
