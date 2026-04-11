using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;

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
    private static readonly ILogger Log = SimLog.CreateLogger("GeoJsonParser");

    /// <summary>Snap tolerance in degrees (~10 feet ≈ 0.00003°).</summary>
    private const double SnapToleranceDeg = 0.00003;

    /// <summary>Max distance to connect a parking spot to a taxiway (nm).</summary>
    private const double ParkingConnectMaxNm = 0.15;

    private static readonly JsonDocumentOptions LenientJsonOptions = new() { AllowTrailingCommas = true };

    /// <summary>Strips leading zeros from JSON number literals (e.g. 03 → 3) that are invalid per RFC 8259.</summary>
    private static readonly Regex LeadingZeroRegex = new(@"(?<=[:,\[]\s*)0+(\d)", RegexOptions.Compiled);

    private static string SanitizeJson(string json) => LeadingZeroRegex.Replace(json, "$1");

    public static AirportGroundLayout Parse(string airportId, string geoJson, string? runwayAirportCode)
    {
        return Parse(airportId, geoJson, runwayAirportCode, applyFillets: true);
    }

    public static AirportGroundLayout Parse(string airportId, string geoJson, string? runwayAirportCode, bool applyFillets)
    {
        string sanitized = SanitizeJson(geoJson);
        var doc = JsonDocument.Parse(sanitized, LenientJsonOptions);
        var root = doc.RootElement;

        var features = root.GetProperty("features");

        var parkingFeatures = new List<ParkingFeature>();
        var helipadFeatures = new List<ParkingFeature>();
        var spotFeatures = new List<SpotFeature>();
        var taxiwayFeatures = new List<TaxiwayFeature>();
        var runwayFeatures = new List<RunwayFeature>();

        int skipped = 0;
        foreach (var feature in features.EnumerateArray())
        {
            var props = feature.GetProperty("properties");
            string type = props.GetProperty("type").GetString() ?? "";
            var geom = feature.GetProperty("geometry");

            try
            {
                switch (type)
                {
                    case "parking":
                        parkingFeatures.Add(ParseParking(props, geom));
                        break;
                    case "helipad":
                        helipadFeatures.Add(ParseParking(props, geom));
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
                        Log.LogWarning("Unknown GeoJSON feature type: {Type}", type);
                        break;
                }
            }
            catch (InvalidOperationException ex)
            {
                string name = props.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?";
                Log.LogWarning("Skipping malformed {Type} feature '{Name}' in {Airport}: {Message}", type, name, airportId, ex.Message);
                skipped++;
            }
        }

        if (skipped > 0)
        {
            Log.LogWarning("Skipped {Count} malformed feature(s) in {Airport}", skipped, airportId);
        }

        return BuildLayout(
            airportId,
            parkingFeatures,
            helipadFeatures,
            spotFeatures,
            taxiwayFeatures,
            runwayFeatures,
            runwayAirportCode,
            applyFillets
        );
    }

    /// <summary>
    /// Parse from multiple GeoJSON files (separate parking, taxiways, spots, runways).
    /// </summary>
    public static AirportGroundLayout ParseMultiple(string airportId, IEnumerable<string> geoJsonFiles, string? runwayAirportCode)
    {
        var merged = geoJsonFiles.ToList();

        if (merged.Count == 1)
        {
            return Parse(airportId, merged[0], runwayAirportCode);
        }

        var allFeatures = new List<JsonElement>();
        foreach (string json in merged)
        {
            string sanitized = SanitizeJson(json);
            using var doc = JsonDocument.Parse(sanitized, LenientJsonOptions);
            var features = doc.RootElement.GetProperty("features");
            foreach (var f in features.EnumerateArray())
            {
                allFeatures.Add(f.Clone());
            }
        }

        // Rebuild as single FeatureCollection
        string combined = BuildCombinedJson(allFeatures);
        return Parse(airportId, combined, runwayAirportCode);
    }

    /// <summary>Max distance to connect a helipad to a taxiway (nm). Larger than parking since helipads may be further from taxiways.</summary>
    private const double HelipadConnectMaxNm = 0.3;

    private static AirportGroundLayout BuildLayout(
        string airportId,
        List<ParkingFeature> parkings,
        List<ParkingFeature> helipads,
        List<SpotFeature> spots,
        List<TaxiwayFeature> taxiways,
        List<RunwayFeature> runways,
        string? runwayAirportCode,
        bool applyFillets
    )
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
                Origin = "GeoJson:spot",
            };
            layout.Nodes[id] = node;
            coordIndex.Add(spot.Lat, spot.Lon, id);
        }

        // Step 2: Process taxiway LineStrings — insert nodes at each vertex,
        // snap to existing nodes, detect intersections
        var taxiwaySegments = new List<ProcessedTaxiway>();
        foreach (var tw in taxiways)
        {
            var processed = TaxiwayGraphBuilder.ProcessTaxiway(tw, layout, coordIndex, ref nextNodeId);
            taxiwaySegments.Add(processed);
        }

        // Step 3: Detect intersections between taxiway segments
        TaxiwayGraphBuilder.DetectIntersections(taxiwaySegments, layout, coordIndex, ref nextNodeId);

        // Step 4: Build edges from processed taxiway vertex chains
        foreach (var tw in taxiwaySegments)
        {
            TaxiwayGraphBuilder.BuildEdgesFromTaxiway(tw, layout);
        }

        // Step 4b: Remove overlapping edges (same two nodes, different taxiway names).
        // When two taxiways share an identical segment, keep the one that continues
        // through both endpoints; remove the one that terminates.
        RemoveOverlappingEdges(layout);

        // Step 5: Process runway LineStrings, detect taxiway-runway crossings
        foreach (var rwy in runways)
        {
            double rwyWidthFt = RunwayCrossingDetector.DetectRunwayCrossings(rwy, layout, coordIndex, ref nextNodeId, runwayAirportCode);

            layout.Runways.Add(
                new GroundRunway
                {
                    Name = rwy.Name,
                    Coordinates = new List<(double Lat, double Lon)>(rwy.Coords),
                    WidthFt = rwyWidthFt,
                }
            );
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
                TrueHeading = new TrueHeading(pkg.Heading),
                Origin = "GeoJson:parking",
            };
            layout.Nodes[id] = node;

            ConnectParkingToTaxiway(node, layout);
        }

        // Step 6b: Create helipad nodes and connect to nearest taxiway (larger radius)
        foreach (var hp in helipads)
        {
            int id = nextNodeId++;
            var node = new GroundNode
            {
                Id = id,
                Latitude = hp.Lat,
                Longitude = hp.Lon,
                Type = GroundNodeType.Helipad,
                Name = hp.Name,
                TrueHeading = new TrueHeading(hp.Heading),
                Origin = "GeoJson:helipad",
            };
            layout.Nodes[id] = node;

            ConnectToNearestTaxiway(node, layout, HelipadConnectMaxNm);
        }

        // Step 7: Wire up adjacency lists
        layout.RebuildAdjacencyLists();

        // Step 8: Generate fillet arcs at intersections
        if (applyFillets)
        {
            FilletArcGenerator.Apply(layout);
        }

        Log.LogInformation(
            "Parsed airport {Id}: {NodeCount} nodes, {EdgeCount} edges, {ArcCount} arcs, " + "{ParkingCount} parking, {HelipadCount} helipads",
            airportId,
            layout.Nodes.Count,
            layout.Edges.Count,
            layout.Arcs.Count,
            parkings.Count,
            helipads.Count
        );

        return layout;
    }

    private static void ConnectParkingToTaxiway(GroundNode parking, AirportGroundLayout layout)
    {
        ConnectToNearestTaxiway(parking, layout, ParkingConnectMaxNm);
    }

    /// <summary>
    /// Remove overlapping edges: when two taxiways share an identical segment (same
    /// two nodes, different names), keep the taxiway that continues through both
    /// endpoints and remove the one that terminates at one end.
    /// </summary>
    private static void RemoveOverlappingEdges(AirportGroundLayout layout)
    {
        // Group edges by node pair (order-independent)
        var byNodePair = new Dictionary<(int, int), List<GroundEdge>>();
        foreach (var edge in layout.Edges)
        {
            int a = Math.Min(edge.Nodes[0].Id, edge.Nodes[1].Id);
            int b = Math.Max(edge.Nodes[0].Id, edge.Nodes[1].Id);
            var key = (a, b);
            if (!byNodePair.TryGetValue(key, out var list))
            {
                list = [];
                byNodePair[key] = list;
            }
            list.Add(edge);
        }

        var toRemove = new HashSet<GroundEdge>();
        foreach (var (pair, edges) in byNodePair)
        {
            if (edges.Count < 2)
            {
                continue;
            }

            // For each pair of overlapping edges with different names, decide which to keep
            for (int i = 0; i < edges.Count; i++)
            {
                for (int j = i + 1; j < edges.Count; j++)
                {
                    var edgeA = edges[i];
                    var edgeB = edges[j];
                    if (edgeA.TaxiwayName == edgeB.TaxiwayName)
                    {
                        continue;
                    }
                    if (toRemove.Contains(edgeA) || toRemove.Contains(edgeB))
                    {
                        continue;
                    }

                    // Count how many other edges each taxiway has at each endpoint
                    int contA0 = CountOtherEdgesForTaxiway(layout, edgeA.Nodes[0].Id, edgeA.TaxiwayName, pair);
                    int contA1 = CountOtherEdgesForTaxiway(layout, edgeA.Nodes[1].Id, edgeA.TaxiwayName, pair);
                    int contB0 = CountOtherEdgesForTaxiway(layout, edgeB.Nodes[0].Id, edgeB.TaxiwayName, pair);
                    int contB1 = CountOtherEdgesForTaxiway(layout, edgeB.Nodes[1].Id, edgeB.TaxiwayName, pair);

                    // Taxiway "continues" at an endpoint if it has other edges there
                    int scoreA = (contA0 > 0 ? 1 : 0) + (contA1 > 0 ? 1 : 0);
                    int scoreB = (contB0 > 0 ? 1 : 0) + (contB1 > 0 ? 1 : 0);

                    if (scoreA > scoreB)
                    {
                        toRemove.Add(edgeB);
                    }
                    else if (scoreB > scoreA)
                    {
                        toRemove.Add(edgeA);
                    }
                    // If tied, keep both — can't determine which owns the segment
                }
            }
        }

        if (toRemove.Count > 0)
        {
            layout.Edges.RemoveAll(e => toRemove.Contains(e));
        }
    }

    private static int CountOtherEdgesForTaxiway(AirportGroundLayout layout, int nodeId, string taxiwayName, (int, int) excludePair)
    {
        int count = 0;
        foreach (var edge in layout.Edges)
        {
            if (edge.TaxiwayName != taxiwayName)
            {
                continue;
            }
            if (!edge.HasNode(nodeId))
            {
                continue;
            }
            int a = Math.Min(edge.Nodes[0].Id, edge.Nodes[1].Id);
            int b = Math.Max(edge.Nodes[0].Id, edge.Nodes[1].Id);
            if ((a, b) == excludePair)
            {
                continue;
            }
            count++;
        }
        return count;
    }

    private static void ConnectToNearestTaxiway(GroundNode node, AirportGroundLayout layout, double maxDistNm)
    {
        GroundNode? nearest = null;
        double nearestDist = double.MaxValue;

        foreach (var candidate in layout.Nodes.Values)
        {
            if (candidate.Id == node.Id || candidate.Type is GroundNodeType.Parking or GroundNodeType.Helipad)
            {
                continue;
            }

            double dist = GeoMath.DistanceNm(node.Latitude, node.Longitude, candidate.Latitude, candidate.Longitude);

            if (dist < nearestDist)
            {
                nearestDist = dist;
                nearest = candidate;
            }
        }

        if (nearest is null || nearestDist > maxDistNm)
        {
            return;
        }

        var edge = new GroundEdge
        {
            Nodes = [node, nearest],
            TaxiwayName = "RAMP",
            DistanceNm = nearestDist,
            Origin = "GeoJson:taxiway-edge",
        };

        layout.Edges.Add(edge);
        // Node adjacency lists are wired up in Step 7 — don't add here to avoid duplicates.
    }

    private static ParkingFeature ParseParking(JsonElement props, JsonElement geom)
    {
        var coords = geom.GetProperty("coordinates");
        double lon = coords[0].GetDouble();
        double lat = coords[1].GetDouble();
        string name = props.GetProperty("name").GetString() ?? "";
        int heading = 0;
        if (props.TryGetProperty("heading", out var h))
        {
            if (h.ValueKind == JsonValueKind.String)
            {
                int.TryParse(h.GetString(), out heading);
            }
            else if (h.ValueKind == JsonValueKind.Number)
            {
                heading = h.GetInt32();
            }
        }
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

    private static (string Name, List<(double Lat, double Lon)> Coords) ParseLineString(JsonElement props, JsonElement geom)
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

        return (name, coords);
    }

    private static TaxiwayFeature ParseTaxiway(JsonElement props, JsonElement geom)
    {
        var (name, coords) = ParseLineString(props, geom);
        return new TaxiwayFeature(name, coords);
    }

    private static RunwayFeature ParseRunway(JsonElement props, JsonElement geom)
    {
        var (name, coords) = ParseLineString(props, geom);
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

    // Internal feature DTOs — accessible to graph builder and crossing detector
    internal sealed record ParkingFeature(string Name, double Lat, double Lon, int Heading);

    internal sealed record SpotFeature(string Name, double Lat, double Lon);

    internal sealed record TaxiwayFeature(string Name, List<(double Lat, double Lon)> Coords);

    internal sealed record RunwayFeature(string Name, List<(double Lat, double Lon)> Coords);
}
