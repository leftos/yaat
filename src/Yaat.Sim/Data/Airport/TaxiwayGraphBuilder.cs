namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Builds the taxiway graph from processed taxiway LineStrings.
/// Handles intersection detection, node chain management, and edge construction.
/// </summary>
internal static class TaxiwayGraphBuilder
{
    internal static ProcessedTaxiway ProcessTaxiway(
        GeoJsonParser.TaxiwayFeature tw,
        AirportGroundLayout layout,
        CoordinateIndex coordIndex,
        ref int nextNodeId
    )
    {
        var nodeIds = new List<int>();

        for (int i = 0; i < tw.Coords.Count; i++)
        {
            var (lat, lon) = tw.Coords[i];

            int? existing = coordIndex.FindNearest(lat, lon);
            if (existing is not null)
            {
                nodeIds.Add(existing.Value);
            }
            else
            {
                int id = nextNodeId++;
                var node = new GroundNode
                {
                    Id = id,
                    Position = new LatLon(lat, lon),
                    Type = GroundNodeType.TaxiwayIntersection,
                    Origin = "TaxiwayGraphBuilder:intermediate",
                };
                layout.Nodes[id] = node;
                coordIndex.Add(lat, lon, id);
                nodeIds.Add(id);
            }
        }

        return new ProcessedTaxiway(tw.Name, nodeIds, tw.Coords);
    }

    internal static void DetectIntersections(
        List<ProcessedTaxiway> taxiways,
        AirportGroundLayout layout,
        CoordinateIndex coordIndex,
        ref int nextNodeId
    )
    {
        for (int i = 0; i < taxiways.Count; i++)
        {
            for (int j = i + 1; j < taxiways.Count; j++)
            {
                FindLineStringIntersections(taxiways[i], taxiways[j], layout, coordIndex, ref nextNodeId);
            }
        }
    }

    internal static void BuildEdgesFromTaxiway(ProcessedTaxiway tw, AirportGroundLayout layout)
    {
        for (int i = 0; i < tw.NodeIds.Count - 1; i++)
        {
            int fromId = tw.NodeIds[i];
            int toId = tw.NodeIds[i + 1];

            if (fromId == toId)
            {
                continue;
            }

            if (!layout.Nodes.TryGetValue(fromId, out var fromNode) || !layout.Nodes.TryGetValue(toId, out var toNode))
            {
                continue;
            }

            // Check for duplicate edge
            bool exists = false;
            foreach (var e in layout.Edges)
            {
                if (e.HasNode(fromId) && e.HasNode(toId))
                {
                    if (e.MatchesTaxiway(tw.Name))
                    {
                        exists = true;
                        break;
                    }
                }
            }

            if (exists)
            {
                continue;
            }

            double dist = GeoMath.DistanceNm(fromNode.Position, toNode.Position);

            var edge = new GroundEdge
            {
                Nodes = [fromNode, toNode],
                TaxiwayName = tw.Name,
                DistanceNm = dist,
                Origin = "TaxiwayGraphBuilder:edge",
            };

            layout.Edges.Add(edge);
        }
    }

    private static void FindLineStringIntersections(
        ProcessedTaxiway tw1,
        ProcessedTaxiway tw2,
        AirportGroundLayout layout,
        CoordinateIndex coordIndex,
        ref int nextNodeId
    )
    {
        // Check each segment pair
        for (int a = 0; a < tw1.Coords.Count - 1; a++)
        {
            for (int b = 0; b < tw2.Coords.Count - 1; b++)
            {
                var result = GeoMath.SegmentsIntersect(
                    tw1.Coords[a].Lat,
                    tw1.Coords[a].Lon,
                    tw1.Coords[a + 1].Lat,
                    tw1.Coords[a + 1].Lon,
                    tw2.Coords[b].Lat,
                    tw2.Coords[b].Lon,
                    tw2.Coords[b + 1].Lat,
                    tw2.Coords[b + 1].Lon
                );

                if (result is null)
                {
                    continue;
                }

                double lat = result.Value.Lat;
                double lon = result.Value.Lon;

                // Check if there's already a node at this location
                int? existing = coordIndex.FindNearest(lat, lon);
                if (existing is not null)
                {
                    // Ensure both taxiways have this node in their chains
                    EnsureNodeInChain(tw1, existing.Value, a, layout);
                    EnsureNodeInChain(tw2, existing.Value, b, layout);
                    continue;
                }

                int id = nextNodeId++;
                var node = new GroundNode
                {
                    Id = id,
                    Position = new LatLon(lat, lon),
                    Type = GroundNodeType.TaxiwayIntersection,
                    Origin = $"TaxiwayGraphBuilder:intersection({tw1.Name}/{tw2.Name})",
                };
                layout.Nodes[id] = node;
                coordIndex.Add(lat, lon, id);

                InsertNodeInChain(tw1, id, a);
                InsertNodeInChain(tw2, id, b);
            }
        }
    }

    private static void EnsureNodeInChain(ProcessedTaxiway tw, int nodeId, int afterSegIndex, AirportGroundLayout layout)
    {
        if (tw.NodeIds.Contains(nodeId))
        {
            return;
        }

        int insertAt = afterSegIndex + 1;
        if (insertAt > tw.NodeIds.Count)
        {
            insertAt = tw.NodeIds.Count;
        }

        tw.NodeIds.Insert(insertAt, nodeId);

        if (layout.Nodes.TryGetValue(nodeId, out var node))
        {
            tw.Coords.Insert(insertAt, (node.Position.Lat, node.Position.Lon));
        }
    }

    private static void InsertNodeInChain(ProcessedTaxiway tw, int nodeId, int afterSegIndex)
    {
        int insertAt = afterSegIndex + 1;
        if (insertAt > tw.NodeIds.Count)
        {
            insertAt = tw.NodeIds.Count;
        }

        tw.NodeIds.Insert(insertAt, nodeId);
    }
}

/// <summary>
/// Represents a taxiway LineString after initial node processing.
/// Mutable node chain — intersection detection inserts nodes in-place.
/// </summary>
internal sealed class ProcessedTaxiway(string name, List<int> nodeIds, List<(double Lat, double Lon)> coords)
{
    public string Name { get; } = name;
    public List<int> NodeIds { get; } = nodeIds;
    public List<(double Lat, double Lon)> Coords { get; } = coords;
}
