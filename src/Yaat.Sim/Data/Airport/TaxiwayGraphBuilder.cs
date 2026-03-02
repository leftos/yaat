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
                    Latitude = lat,
                    Longitude = lon,
                    Type = GroundNodeType.TaxiwayIntersection,
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
                if ((e.FromNodeId == fromId && e.ToNodeId == toId) || (e.FromNodeId == toId && e.ToNodeId == fromId))
                {
                    if (string.Equals(e.TaxiwayName, tw.Name, StringComparison.OrdinalIgnoreCase))
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

            double dist = GeoMath.DistanceNm(fromNode.Latitude, fromNode.Longitude, toNode.Latitude, toNode.Longitude);

            var edge = new GroundEdge
            {
                FromNodeId = fromId,
                ToNodeId = toId,
                TaxiwayName = tw.Name,
                DistanceNm = dist,
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
                var (lat, lon) = SegmentIntersection(tw1.Coords[a], tw1.Coords[a + 1], tw2.Coords[b], tw2.Coords[b + 1]);

                if (double.IsNaN(lat))
                {
                    continue;
                }

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
                    Latitude = lat,
                    Longitude = lon,
                    Type = GroundNodeType.TaxiwayIntersection,
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
            tw.Coords.Insert(insertAt, (node.Latitude, node.Longitude));
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

    /// <summary>
    /// Compute intersection of two line segments.
    /// Returns (NaN, NaN) if no intersection.
    /// </summary>
    private static (double Lat, double Lon) SegmentIntersection(
        (double Lat, double Lon) a1,
        (double Lat, double Lon) a2,
        (double Lat, double Lon) b1,
        (double Lat, double Lon) b2
    )
    {
        double d1Lat = a2.Lat - a1.Lat;
        double d1Lon = a2.Lon - a1.Lon;
        double d2Lat = b2.Lat - b1.Lat;
        double d2Lon = b2.Lon - b1.Lon;

        double cross = d1Lat * d2Lon - d1Lon * d2Lat;
        if (Math.Abs(cross) < 1e-12)
        {
            return (double.NaN, double.NaN);
        }

        double diffLat = b1.Lat - a1.Lat;
        double diffLon = b1.Lon - a1.Lon;

        double t = (diffLat * d2Lon - diffLon * d2Lat) / cross;
        double u = (diffLat * d1Lon - diffLon * d1Lat) / cross;

        if (t < 0 || t > 1 || u < 0 || u > 1)
        {
            return (double.NaN, double.NaN);
        }

        double lat = a1.Lat + t * d1Lat;
        double lon = a1.Lon + t * d1Lon;

        return (lat, lon);
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
