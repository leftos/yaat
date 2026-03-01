using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Parses GeoJSON FeatureCollections (parking, taxiway, spot, runway features)
/// into an AirportGroundLayout with a connected graph.
///
/// Expects features with properties.type = "parking" | "taxiway" | "spot" | "runway".
/// Coordinates are [lon, lat] per GeoJSON spec.
/// </summary>
public static class GeoJsonParser
{
    /// <summary>Snap tolerance in degrees (~10 feet ≈ 0.00003°).</summary>
    private const double SnapToleranceDeg = 0.00003;

    /// <summary>Max distance to connect a parking spot to a taxiway (nm).</summary>
    private const double ParkingConnectMaxNm = 0.15;

    public static AirportGroundLayout Parse(
        string airportId, string geoJson, ILogger? logger = null)
    {
        var doc = JsonDocument.Parse(geoJson);
        var root = doc.RootElement;

        var features = root.GetProperty("features");

        var parkingFeatures = new List<ParkingFeature>();
        var spotFeatures = new List<SpotFeature>();
        var taxiwayFeatures = new List<TaxiwayFeature>();
        var runwayFeatures = new List<RunwayFeature>();

        foreach (var feature in features.EnumerateArray())
        {
            var props = feature.GetProperty("properties");
            string type = props.GetProperty("type").GetString() ?? "";
            var geom = feature.GetProperty("geometry");

            switch (type)
            {
                case "parking":
                    parkingFeatures.Add(ParseParking(props, geom));
                    break;
                case "spot":
                    spotFeatures.Add(ParseSpot(props, geom));
                    break;
                case "taxiway":
                    taxiwayFeatures.Add(ParseTaxiway(props, geom));
                    break;
                case "runway":
                    runwayFeatures.Add(ParseRunway(props, geom));
                    break;
                default:
                    logger?.LogWarning("Unknown GeoJSON feature type: {Type}", type);
                    break;
            }
        }

        return BuildLayout(
            airportId, parkingFeatures, spotFeatures,
            taxiwayFeatures, runwayFeatures, logger);
    }

    /// <summary>
    /// Parse from multiple GeoJSON files (separate parking, taxiways, spots, runways).
    /// </summary>
    public static AirportGroundLayout ParseMultiple(
        string airportId, IEnumerable<string> geoJsonFiles, ILogger? logger = null)
    {
        var merged = new List<string>();
        foreach (string json in geoJsonFiles)
        {
            merged.Add(json);
        }

        if (merged.Count == 1)
        {
            return Parse(airportId, merged[0], logger);
        }

        var allFeatures = new List<JsonElement>();
        foreach (string json in merged)
        {
            var doc = JsonDocument.Parse(json);
            var features = doc.RootElement.GetProperty("features");
            foreach (var f in features.EnumerateArray())
            {
                allFeatures.Add(f.Clone());
            }
        }

        // Rebuild as single FeatureCollection
        string combined = BuildCombinedJson(allFeatures);
        return Parse(airportId, combined, logger);
    }

    private static AirportGroundLayout BuildLayout(
        string airportId,
        List<ParkingFeature> parkings,
        List<SpotFeature> spots,
        List<TaxiwayFeature> taxiways,
        List<RunwayFeature> runways,
        ILogger? logger)
    {
        var layout = new AirportGroundLayout { AirportId = airportId };
        int nextNodeId = 0;

        // Spatial index for fast coordinate snapping
        var coordIndex = new CoordinateIndex(SnapToleranceDeg);

        // Step 1: Create spot nodes (named intersection / hold-short points)
        foreach (var spot in spots)
        {
            int id = nextNodeId++;
            var node = new GroundNode
            {
                Id = id,
                Latitude = spot.Lat,
                Longitude = spot.Lon,
                Type = GroundNodeType.Spot,
                Name = spot.Name,
            };
            layout.Nodes[id] = node;
            coordIndex.Add(spot.Lat, spot.Lon, id);
        }

        // Step 2: Process taxiway LineStrings — insert nodes at each vertex,
        // snap to existing nodes, detect intersections
        var taxiwaySegments = new List<ProcessedTaxiway>();
        foreach (var tw in taxiways)
        {
            var processed = ProcessTaxiway(
                tw, layout, coordIndex, ref nextNodeId);
            taxiwaySegments.Add(processed);
        }

        // Step 3: Detect intersections between taxiway segments
        DetectIntersections(taxiwaySegments, layout, coordIndex, ref nextNodeId);

        // Step 4: Build edges from processed taxiway vertex chains
        foreach (var tw in taxiwaySegments)
        {
            BuildEdgesFromTaxiway(tw, layout);
        }

        // Step 5: Process runway LineStrings, detect taxiway-runway crossings
        foreach (var rwy in runways)
        {
            DetectRunwayCrossings(rwy, layout, coordIndex, ref nextNodeId, logger);
        }

        // Step 6: Create parking nodes and connect to nearest taxiway
        foreach (var pkg in parkings)
        {
            int id = nextNodeId++;
            var node = new GroundNode
            {
                Id = id,
                Latitude = pkg.Lat,
                Longitude = pkg.Lon,
                Type = GroundNodeType.Parking,
                Name = pkg.Name,
                Heading = pkg.Heading,
            };
            layout.Nodes[id] = node;

            ConnectParkingToTaxiway(node, layout);
        }

        // Step 7: Wire up adjacency lists
        foreach (var edge in layout.Edges)
        {
            if (layout.Nodes.TryGetValue(edge.FromNodeId, out var fromNode))
            {
                fromNode.Edges.Add(edge);
            }

            if (layout.Nodes.TryGetValue(edge.ToNodeId, out var toNode))
            {
                toNode.Edges.Add(edge);
            }
        }

        logger?.LogInformation(
            "Parsed airport {Id}: {NodeCount} nodes, {EdgeCount} edges, " +
            "{ParkingCount} parking spots",
            airportId, layout.Nodes.Count, layout.Edges.Count, parkings.Count);

        return layout;
    }

    private static ProcessedTaxiway ProcessTaxiway(
        TaxiwayFeature tw,
        AirportGroundLayout layout,
        CoordinateIndex coordIndex,
        ref int nextNodeId)
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

    private static void DetectIntersections(
        List<ProcessedTaxiway> taxiways,
        AirportGroundLayout layout,
        CoordinateIndex coordIndex,
        ref int nextNodeId)
    {
        for (int i = 0; i < taxiways.Count; i++)
        {
            for (int j = i + 1; j < taxiways.Count; j++)
            {
                FindLineStringIntersections(
                    taxiways[i], taxiways[j],
                    layout, coordIndex, ref nextNodeId);
            }
        }
    }

    private static void FindLineStringIntersections(
        ProcessedTaxiway tw1, ProcessedTaxiway tw2,
        AirportGroundLayout layout, CoordinateIndex coordIndex,
        ref int nextNodeId)
    {
        // Check each segment pair
        for (int a = 0; a < tw1.Coords.Count - 1; a++)
        {
            for (int b = 0; b < tw2.Coords.Count - 1; b++)
            {
                var (lat, lon) = SegmentIntersection(
                    tw1.Coords[a], tw1.Coords[a + 1],
                    tw2.Coords[b], tw2.Coords[b + 1]);

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

    private static void EnsureNodeInChain(
        ProcessedTaxiway tw, int nodeId, int afterSegIndex,
        AirportGroundLayout layout)
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

    private static void InsertNodeInChain(
        ProcessedTaxiway tw, int nodeId, int afterSegIndex)
    {
        int insertAt = afterSegIndex + 1;
        if (insertAt > tw.NodeIds.Count)
        {
            insertAt = tw.NodeIds.Count;
        }

        tw.NodeIds.Insert(insertAt, nodeId);
    }

    private static void BuildEdgesFromTaxiway(
        ProcessedTaxiway tw, AirportGroundLayout layout)
    {
        for (int i = 0; i < tw.NodeIds.Count - 1; i++)
        {
            int fromId = tw.NodeIds[i];
            int toId = tw.NodeIds[i + 1];

            if (fromId == toId)
            {
                continue;
            }

            if (!layout.Nodes.TryGetValue(fromId, out var fromNode)
                || !layout.Nodes.TryGetValue(toId, out var toNode))
            {
                continue;
            }

            // Check for duplicate edge
            bool exists = false;
            foreach (var e in layout.Edges)
            {
                if ((e.FromNodeId == fromId && e.ToNodeId == toId)
                    || (e.FromNodeId == toId && e.ToNodeId == fromId))
                {
                    if (string.Equals(e.TaxiwayName, tw.Name,
                        StringComparison.OrdinalIgnoreCase))
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

            double dist = GeoMath.DistanceNm(
                fromNode.Latitude, fromNode.Longitude,
                toNode.Latitude, toNode.Longitude);

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

    private static void DetectRunwayCrossings(
        RunwayFeature rwy, AirportGroundLayout layout,
        CoordinateIndex coordIndex, ref int nextNodeId,
        ILogger? logger)
    {
        string[] nameParts = rwy.Name.Split(" - ");
        string rwyId1 = nameParts[0].Trim();
        string rwyId2 = nameParts.Length > 1 ? nameParts[1].Trim() : rwyId1;

        // For each taxiway edge, check if it crosses this runway LineString
        var edgesToCheck = new List<GroundEdge>(layout.Edges);
        foreach (var edge in edgesToCheck)
        {
            if (!layout.Nodes.TryGetValue(edge.FromNodeId, out var fromNode)
                || !layout.Nodes.TryGetValue(edge.ToNodeId, out var toNode))
            {
                continue;
            }

            // Skip edges that are themselves on a runway
            if (edge.TaxiwayName.StartsWith("RWY", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Check each runway segment against this edge
            for (int i = 0; i < rwy.Coords.Count - 1; i++)
            {
                var (intLat, intLon) = SegmentIntersection(
                    (fromNode.Latitude, fromNode.Longitude),
                    (toNode.Latitude, toNode.Longitude),
                    rwy.Coords[i], rwy.Coords[i + 1]);

                if (double.IsNaN(intLat))
                {
                    continue;
                }

                // Check if there's already a node near the intersection
                int? existing = coordIndex.FindNearest(intLat, intLon);
                if (existing is not null && layout.Nodes.TryGetValue(existing.Value, out var existingNode))
                {
                    // Mark it as a hold-short node if it isn't already
                    if (existingNode.Type != GroundNodeType.RunwayHoldShort)
                    {
                        // Create a new node at this position with RunwayHoldShort type
                        int id = nextNodeId++;
                        var hsNode = new GroundNode
                        {
                            Id = id,
                            Latitude = intLat,
                            Longitude = intLon,
                            Type = GroundNodeType.RunwayHoldShort,
                            RunwayId = $"{rwyId1}/{rwyId2}",
                            Name = existingNode.Name,
                        };
                        layout.Nodes[id] = hsNode;
                        coordIndex.Add(intLat, intLon, id);
                    }

                    continue;
                }

                // Create hold-short node at intersection
                {
                    int id = nextNodeId++;
                    var hsNode = new GroundNode
                    {
                        Id = id,
                        Latitude = intLat,
                        Longitude = intLon,
                        Type = GroundNodeType.RunwayHoldShort,
                        RunwayId = $"{rwyId1}/{rwyId2}",
                    };
                    layout.Nodes[id] = hsNode;
                    coordIndex.Add(intLat, intLon, id);

                    logger?.LogDebug(
                        "Runway crossing detected: {Taxiway} crosses {Runway} at ({Lat}, {Lon})",
                        edge.TaxiwayName, rwy.Name, intLat, intLon);
                }
            }
        }
    }

    private static void ConnectParkingToTaxiway(
        GroundNode parking, AirportGroundLayout layout)
    {
        GroundNode? nearest = null;
        double nearestDist = double.MaxValue;

        foreach (var node in layout.Nodes.Values)
        {
            if (node.Id == parking.Id || node.Type == GroundNodeType.Parking)
            {
                continue;
            }

            double dist = GeoMath.DistanceNm(
                parking.Latitude, parking.Longitude,
                node.Latitude, node.Longitude);

            if (dist < nearestDist)
            {
                nearestDist = dist;
                nearest = node;
            }
        }

        if (nearest is null || nearestDist > ParkingConnectMaxNm)
        {
            return;
        }

        var edge = new GroundEdge
        {
            FromNodeId = parking.Id,
            ToNodeId = nearest.Id,
            TaxiwayName = "RAMP",
            DistanceNm = nearestDist,
        };

        layout.Edges.Add(edge);
    }

    /// <summary>
    /// Compute intersection of two line segments.
    /// Returns (NaN, NaN) if no intersection.
    /// </summary>
    private static (double Lat, double Lon) SegmentIntersection(
        (double Lat, double Lon) a1, (double Lat, double Lon) a2,
        (double Lat, double Lon) b1, (double Lat, double Lon) b2)
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

    private static ParkingFeature ParseParking(JsonElement props, JsonElement geom)
    {
        var coords = geom.GetProperty("coordinates");
        double lon = coords[0].GetDouble();
        double lat = coords[1].GetDouble();
        string name = props.GetProperty("name").GetString() ?? "";
        int heading = props.TryGetProperty("heading", out var h) ? h.GetInt32() : 0;
        return new ParkingFeature(name, lat, lon, heading);
    }

    private static SpotFeature ParseSpot(JsonElement props, JsonElement geom)
    {
        var coords = geom.GetProperty("coordinates");
        double lon = coords[0].GetDouble();
        double lat = coords[1].GetDouble();
        string name = props.GetProperty("name").GetString() ?? "";
        return new SpotFeature(name, lat, lon);
    }

    private static TaxiwayFeature ParseTaxiway(JsonElement props, JsonElement geom)
    {
        string name = props.GetProperty("name").GetString() ?? "";
        var coordsArray = geom.GetProperty("coordinates");
        var coords = new List<(double Lat, double Lon)>();
        foreach (var coord in coordsArray.EnumerateArray())
        {
            double lon = coord[0].GetDouble();
            double lat = coord[1].GetDouble();
            coords.Add((lat, lon));
        }

        return new TaxiwayFeature(name, coords);
    }

    private static RunwayFeature ParseRunway(JsonElement props, JsonElement geom)
    {
        string name = props.GetProperty("name").GetString() ?? "";
        var coordsArray = geom.GetProperty("coordinates");
        var coords = new List<(double Lat, double Lon)>();
        foreach (var coord in coordsArray.EnumerateArray())
        {
            double lon = coord[0].GetDouble();
            double lat = coord[1].GetDouble();
            coords.Add((lat, lon));
        }

        return new RunwayFeature(name, coords);
    }

    private static string BuildCombinedJson(List<JsonElement> features)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();
        writer.WriteString("type", "FeatureCollection");
        writer.WritePropertyName("features");
        writer.WriteStartArray();
        foreach (var f in features)
        {
            f.WriteTo(writer);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    // Internal feature DTOs
    private sealed record ParkingFeature(
        string Name, double Lat, double Lon, int Heading);

    private sealed record SpotFeature(string Name, double Lat, double Lon);

    private sealed record TaxiwayFeature(
        string Name, List<(double Lat, double Lon)> Coords);

    private sealed record RunwayFeature(
        string Name, List<(double Lat, double Lon)> Coords);

    private sealed class ProcessedTaxiway(
        string name,
        List<int> nodeIds,
        List<(double Lat, double Lon)> coords)
    {
        public string Name { get; } = name;
        public List<int> NodeIds { get; } = nodeIds;
        public List<(double Lat, double Lon)> Coords { get; } = coords;
    }

    /// <summary>
    /// Simple spatial index for fast coordinate snapping within a tolerance.
    /// Uses a grid-based bucketing approach.
    /// </summary>
    private sealed class CoordinateIndex
    {
        private readonly double _tolerance;
        private readonly Dictionary<(int LatBucket, int LonBucket), List<(double Lat, double Lon, int NodeId)>> _grid = [];

        public CoordinateIndex(double tolerance)
        {
            _tolerance = tolerance;
        }

        public void Add(double lat, double lon, int nodeId)
        {
            var key = BucketKey(lat, lon);
            if (!_grid.TryGetValue(key, out var list))
            {
                list = [];
                _grid[key] = list;
            }

            list.Add((lat, lon, nodeId));
        }

        public int? FindNearest(double lat, double lon)
        {
            var key = BucketKey(lat, lon);

            // Check this bucket and neighbors
            for (int dlat = -1; dlat <= 1; dlat++)
            {
                for (int dlon = -1; dlon <= 1; dlon++)
                {
                    var neighborKey = (key.LatBucket + dlat, key.LonBucket + dlon);
                    if (!_grid.TryGetValue(neighborKey, out var list))
                    {
                        continue;
                    }

                    foreach (var (nLat, nLon, nodeId) in list)
                    {
                        if (Math.Abs(lat - nLat) <= _tolerance
                            && Math.Abs(lon - nLon) <= _tolerance)
                        {
                            return nodeId;
                        }
                    }
                }
            }

            return null;
        }

        private (int LatBucket, int LonBucket) BucketKey(double lat, double lon)
        {
            return (
                (int)Math.Floor(lat / _tolerance),
                (int)Math.Floor(lon / _tolerance));
        }
    }
}
