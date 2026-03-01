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
    /// </summary>
    public static TaxiRoute? ResolveExplicitPath(
        AirportGroundLayout layout,
        int fromNodeId,
        List<string> taxiwayNames,
        List<string>? explicitHoldShorts = null,
        string? destinationRunway = null)
    {
        if (taxiwayNames.Count == 0)
        {
            return null;
        }

        var segments = new List<TaxiRouteSegment>();
        var holdShorts = new List<HoldShortPoint>();
        int currentNodeId = fromNodeId;

        foreach (string twName in taxiwayNames)
        {
            bool found = WalkTaxiway(
                layout, currentNodeId, twName,
                segments, out int endNodeId);

            if (!found)
            {
                return null;
            }

            currentNodeId = endNodeId;
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
                // Don't add duplicate
                bool exists = false;
                foreach (var hs in holdShorts)
                {
                    if (hs.NodeId == node.Id)
                    {
                        exists = true;
                        break;
                    }
                }

                if (!exists)
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

            bool exists = false;
            foreach (var hs in holdShorts)
            {
                if (hs.NodeId == node.Id)
                {
                    exists = true;
                    break;
                }
            }

            if (!exists)
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
