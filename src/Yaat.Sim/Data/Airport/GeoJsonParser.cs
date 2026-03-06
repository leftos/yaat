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
        string airportId,
        string geoJson,
        ILogger? logger = null,
        IRunwayLookup? runwayLookup = null,
        string? runwayAirportCode = null
    )
    {
        var doc = JsonDocument.Parse(geoJson);
        var root = doc.RootElement;

        var features = root.GetProperty("features");

        var parkingFeatures = new List<ParkingFeature>();
        var helipadFeatures = new List<ParkingFeature>();
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
                    logger?.LogWarning("Unknown GeoJSON feature type: {Type}", type);
                    break;
            }
        }

        return BuildLayout(
            airportId,
            parkingFeatures,
            helipadFeatures,
            spotFeatures,
            taxiwayFeatures,
            runwayFeatures,
            logger,
            runwayLookup,
            runwayAirportCode
        );
    }

    /// <summary>
    /// Parse from multiple GeoJSON files (separate parking, taxiways, spots, runways).
    /// </summary>
    public static AirportGroundLayout ParseMultiple(
        string airportId,
        IEnumerable<string> geoJsonFiles,
        ILogger? logger = null,
        IRunwayLookup? runwayLookup = null,
        string? runwayAirportCode = null
    )
    {
        var merged = geoJsonFiles.ToList();

        if (merged.Count == 1)
        {
            return Parse(airportId, merged[0], logger, runwayLookup, runwayAirportCode);
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
        return Parse(airportId, combined, logger, runwayLookup, runwayAirportCode);
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
        ILogger? logger,
        IRunwayLookup? runwayLookup = null,
        string? runwayAirportCode = null
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

        // Step 5: Process runway LineStrings, detect taxiway-runway crossings
        foreach (var rwy in runways)
        {
            double rwyWidthFt = RunwayCrossingDetector.DetectRunwayCrossings(
                rwy,
                layout,
                coordIndex,
                ref nextNodeId,
                logger,
                runwayLookup,
                runwayAirportCode
            );

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
                Heading = pkg.Heading,
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
                Heading = hp.Heading,
            };
            layout.Nodes[id] = node;

            ConnectToNearestTaxiway(node, layout, HelipadConnectMaxNm);
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
            "Parsed airport {Id}: {NodeCount} nodes, {EdgeCount} edges, " + "{ParkingCount} parking, {HelipadCount} helipads",
            airportId,
            layout.Nodes.Count,
            layout.Edges.Count,
            parkings.Count,
            helipads.Count
        );

        return layout;
    }

    private static void ConnectParkingToTaxiway(GroundNode parking, AirportGroundLayout layout)
    {
        ConnectToNearestTaxiway(parking, layout, ParkingConnectMaxNm);
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
            FromNodeId = node.Id,
            ToNodeId = nearest.Id,
            TaxiwayName = "RAMP",
            DistanceNm = nearestDist,
        };

        layout.Edges.Add(edge);
        node.Edges.Add(edge);
        nearest.Edges.Add(edge);
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
