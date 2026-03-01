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
        string? airportId = null)
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

        foreach (string twName in taxiwayNames)
        {
            segmentCountBeforeLastTw = segments.Count;

            bool found = WalkTaxiway(
                layout, currentNodeId, twName,
                segments, out int endNodeId);

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
                layout, segments, taxiwayNames[^1],
                segmentCountBeforeLastTw, destinationRunway,
                runways, airportId,
                ref currentNodeId, out failReason);

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

        return new TaxiRoute
        {
            Segments = segments,
            HoldShortPoints = holdShorts,
        };
    }

    /// <summary>
    /// Find shortest route between two nodes using A*.
    /// </summary>
    public static TaxiRoute? FindRoute(
        AirportGroundLayout layout, int fromNodeId, int toNodeId)
    {
        if (!layout.Nodes.TryGetValue(fromNodeId, out var startNode)
            || !layout.Nodes.TryGetValue(toNodeId, out var endNode))
        {
            return null;
        }

        var openSet = new PriorityQueue<int, double>();
        var cameFrom = new Dictionary<int, (int NodeId, GroundEdge Edge)>();
        var gScore = new Dictionary<int, double> { [fromNodeId] = 0 };

        double heuristic = GeoMath.DistanceNm(
            startNode.Latitude, startNode.Longitude,
            endNode.Latitude, endNode.Longitude);
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
                int neighbor = edge.FromNodeId == current
                    ? edge.ToNodeId : edge.FromNodeId;

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

                double h = GeoMath.DistanceNm(
                    neighborNode.Latitude, neighborNode.Longitude,
                    endNode.Latitude, endNode.Longitude);
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
    /// Checks if any part of a stored runway ID (e.g., "12/30") matches the target
    /// runway exactly (case-insensitive). Splits on '/'.
    /// </summary>
    internal static bool RunwayIdMatches(string storedRunwayId, string targetRunway)
    {
        foreach (var part in storedRunwayId.Split('/'))
        {
            if (string.Equals(part, targetRunway, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
        out string? failReason)
    {
        failReason = null;

        // Check if route already reaches a hold-short for the destination runway
        foreach (var seg in segments)
        {
            if (layout.Nodes.TryGetValue(seg.ToNodeId, out var segNode)
                && segNode.Type == GroundNodeType.RunwayHoldShort
                && segNode.RunwayId is not null
                && RunwayIdMatches(segNode.RunwayId, destinationRunway))
            {
                return false;
            }
        }

        // Find hold-short nodes for the destination runway
        var variants = new List<(GroundNode HsNode, string VariantName)>();
        var nonVariantConnectors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in layout.Nodes.Values)
        {
            if (node.Type != GroundNodeType.RunwayHoldShort
                || node.RunwayId is null
                || node.Edges.Count == 0
                || !RunwayIdMatches(node.RunwayId, destinationRunway))
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
                layout, segments, lastTaxiwayName,
                segmentCountBeforeLastTw, variants,
                runways, airportId, destinationRunway,
                ref currentNodeId);
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
        ref int currentNodeId)
    {
        // Pick variant: if multiple distinct names, choose closest to runway threshold
        string chosenVariant = PickBestVariant(
            variants, runways, airportId, destinationRunway);

        // Find branch point: scan nodes along the last-taxiway segments
        int branchNodeId = -1;
        int branchSegmentIndex = -1;

        for (int i = segmentCountBeforeLastTw; i < segments.Count; i++)
        {
            int nodeId = i == segmentCountBeforeLastTw
                ? segments[i].FromNodeId
                : segments[i].ToNodeId;

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
        bool walked = WalkTaxiway(
            layout, branchNodeId, chosenVariant,
            segments, out int endNodeId);

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
        string destinationRunway)
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
            double dist = GeoMath.DistanceNm(
                hsNode.Latitude, hsNode.Longitude,
                rwyInfo.ThresholdLatitude, rwyInfo.ThresholdLongitude);

            if (dist < bestDist)
            {
                bestDist = dist;
                bestName = name;
            }
        }

        return bestName;
    }

    private static bool NodeHasEdgeTo(
        AirportGroundLayout layout, int nodeId, string taxiwayName)
    {
        if (!layout.Nodes.TryGetValue(nodeId, out var node))
        {
            return false;
        }

        foreach (var edge in node.Edges)
        {
            if (string.Equals(edge.TaxiwayName, taxiwayName,
                StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool WalkTaxiway(
        AirportGroundLayout layout,
        int startNodeId,
        string taxiwayName,
        List<TaxiRouteSegment> segments,
        out int endNodeId)
    {
        endNodeId = startNodeId;

        if (!layout.Nodes.TryGetValue(startNodeId, out var currentNode))
        {
            return false;
        }

        // Find any edge from the current node on the named taxiway
        var visited = new HashSet<int> { startNodeId };
        var queue = new Queue<int>();
        queue.Enqueue(startNodeId);

        // First, check if any edge directly from current node is on this taxiway
        GroundEdge? startEdge = null;
        foreach (var edge in currentNode.Edges)
        {
            if (string.Equals(edge.TaxiwayName, taxiwayName,
                StringComparison.OrdinalIgnoreCase))
            {
                startEdge = edge;
                break;
            }
        }

        if (startEdge is null)
        {
            // Need to find a path to reach this taxiway
            // For now, try immediate neighbors
            foreach (var edge in currentNode.Edges)
            {
                int neighborId = edge.FromNodeId == startNodeId
                    ? edge.ToNodeId : edge.FromNodeId;

                if (!layout.Nodes.TryGetValue(neighborId, out var neighborNode))
                {
                    continue;
                }

                foreach (var nEdge in neighborNode.Edges)
                {
                    if (string.Equals(nEdge.TaxiwayName, taxiwayName,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        // Add connecting segment
                        segments.Add(new TaxiRouteSegment
                        {
                            FromNodeId = startNodeId,
                            ToNodeId = neighborId,
                            TaxiwayName = edge.TaxiwayName,
                            Edge = edge,
                        });
                        startNodeId = neighborId;
                        startEdge = nEdge;
                        break;
                    }
                }

                if (startEdge is not null)
                {
                    break;
                }
            }

            if (startEdge is null)
            {
                return false;
            }
        }

        // Walk along the taxiway to the end
        int currentId = startNodeId;
        visited.Clear();
        visited.Add(currentId);

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
                if (!string.Equals(edge.TaxiwayName, taxiwayName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int otherId = edge.FromNodeId == currentId
                    ? edge.ToNodeId : edge.FromNodeId;

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

            segments.Add(new TaxiRouteSegment
            {
                FromNodeId = currentId,
                ToNodeId = nextNodeId,
                TaxiwayName = taxiwayName,
                Edge = nextEdge,
            });

            visited.Add(nextNodeId);
            currentId = nextNodeId;
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

    private static void AddImplicitRunwayHoldShorts(
        AirportGroundLayout layout,
        List<TaxiRouteSegment> segments,
        List<HoldShortPoint> holdShorts)
    {
        foreach (var seg in segments)
        {
            if (layout.Nodes.TryGetValue(seg.ToNodeId, out var node)
                && node.Type == GroundNodeType.RunwayHoldShort
                && node.RunwayId is not null)
            {
                if (!HoldShortExists(holdShorts, node.Id))
                {
                    holdShorts.Add(new HoldShortPoint
                    {
                        NodeId = node.Id,
                        Reason = HoldShortReason.RunwayCrossing,
                        RunwayId = node.RunwayId,
                    });
                }
            }
        }
    }

    private static void AddExplicitHoldShort(
        AirportGroundLayout layout,
        List<TaxiRouteSegment> segments,
        List<HoldShortPoint> holdShorts,
        string runwayId)
    {
        // Find nodes along the route that are hold-short for this runway
        foreach (var seg in segments)
        {
            if (!layout.Nodes.TryGetValue(seg.ToNodeId, out var node))
            {
                continue;
            }

            if (node.Type != GroundNodeType.RunwayHoldShort
                || node.RunwayId is null)
            {
                continue;
            }

            if (!node.RunwayId.Contains(runwayId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!HoldShortExists(holdShorts, node.Id))
            {
                holdShorts.Add(new HoldShortPoint
                {
                    NodeId = node.Id,
                    Reason = HoldShortReason.ExplicitHoldShort,
                    RunwayId = runwayId,
                });
            }
        }
    }

    private static void AddDestinationHoldShort(
        AirportGroundLayout layout,
        List<TaxiRouteSegment> segments,
        List<HoldShortPoint> holdShorts,
        string runwayId)
    {
        if (segments.Count == 0)
        {
            return;
        }

        int lastNodeId = segments[^1].ToNodeId;
        holdShorts.Add(new HoldShortPoint
        {
            NodeId = lastNodeId,
            Reason = HoldShortReason.DestinationRunway,
            RunwayId = runwayId,
        });
    }

    private static TaxiRoute ReconstructRoute(
        AirportGroundLayout layout,
        Dictionary<int, (int NodeId, GroundEdge Edge)> cameFrom,
        int endNodeId)
    {
        var segments = new List<TaxiRouteSegment>();
        int current = endNodeId;

        while (cameFrom.TryGetValue(current, out var prev))
        {
            segments.Add(new TaxiRouteSegment
            {
                FromNodeId = prev.NodeId,
                ToNodeId = current,
                TaxiwayName = prev.Edge.TaxiwayName,
                Edge = prev.Edge,
            });
            current = prev.NodeId;
        }

        segments.Reverse();

        var holdShorts = new List<HoldShortPoint>();
        AddImplicitRunwayHoldShorts(layout, segments, holdShorts);

        return new TaxiRoute
        {
            Segments = segments,
            HoldShortPoints = holdShorts,
        };
    }
}
