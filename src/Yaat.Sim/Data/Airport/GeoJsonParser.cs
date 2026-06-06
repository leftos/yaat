using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Phases;

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

    private const double GateGroupAnchorConnectMaxNm = 0.24;

    private static readonly JsonDocumentOptions LenientJsonOptions = new() { AllowTrailingCommas = true };

    /// <summary>Strips leading zeros from JSON number literals (e.g. 03 → 3) that are invalid per RFC 8259.</summary>
    private static readonly Regex LeadingZeroRegex = new(@"(?<=[:,\[]\s*)0+(\d)", RegexOptions.Compiled);

    private static string SanitizeJson(string json) => LeadingZeroRegex.Replace(json, "$1");

    public static AirportGroundLayout Parse(string airportId, string geoJson, string? runwayAirportCode)
    {
        return Parse(airportId, geoJson, runwayAirportCode, FilletMode.Standard);
    }

    public static AirportGroundLayout Parse(string airportId, string geoJson, string? runwayAirportCode, bool applyFillets)
    {
        return Parse(airportId, geoJson, runwayAirportCode, applyFillets ? FilletMode.Standard : FilletMode.None);
    }

    public static AirportGroundLayout Parse(string airportId, string geoJson, string? runwayAirportCode, FilletMode filletMode)
    {
        string sanitized = SanitizeJson(geoJson);
        using var doc = JsonDocument.Parse(sanitized, LenientJsonOptions);
        var features = doc.RootElement.GetProperty("features");
        var classified = ClassifyFeatures(airportId, features.EnumerateArray());
        return BuildLayout(
            airportId,
            classified.Parkings,
            classified.Helipads,
            classified.Spots,
            classified.Taxiways,
            classified.Runways,
            runwayAirportCode,
            filletMode
        );
    }

    /// <summary>
    /// Parse from multiple GeoJSON files (separate parking, taxiways, spots, runways).
    /// Features are merged and classified directly — no re-serialization.
    /// </summary>
    public static AirportGroundLayout ParseMultiple(string airportId, IEnumerable<string> geoJsonFiles, string? runwayAirportCode)
    {
        return ParseMultiple(airportId, geoJsonFiles, runwayAirportCode, FilletMode.Standard);
    }

    public static AirportGroundLayout ParseMultiple(
        string airportId,
        IEnumerable<string> geoJsonFiles,
        string? runwayAirportCode,
        FilletMode filletMode
    )
    {
        var merged = geoJsonFiles.ToList();

        if (merged.Count == 1)
        {
            return Parse(airportId, merged[0], runwayAirportCode, filletMode);
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

        var classified = ClassifyFeatures(airportId, allFeatures);
        return BuildLayout(
            airportId,
            classified.Parkings,
            classified.Helipads,
            classified.Spots,
            classified.Taxiways,
            classified.Runways,
            runwayAirportCode,
            filletMode
        );
    }

    private static (
        List<ParkingFeature> Parkings,
        List<ParkingFeature> Helipads,
        List<SpotFeature> Spots,
        List<TaxiwayFeature> Taxiways,
        List<RunwayFeature> Runways
    ) ClassifyFeatures(string airportId, IEnumerable<JsonElement> features)
    {
        var parkings = new List<ParkingFeature>();
        var helipads = new List<ParkingFeature>();
        var spots = new List<SpotFeature>();
        var taxiways = new List<TaxiwayFeature>();
        var runways = new List<RunwayFeature>();

        int skipped = 0;
        foreach (var feature in features)
        {
            var props = feature.GetProperty("properties");
            string type = props.GetProperty("type").GetString() ?? "";
            var geom = feature.GetProperty("geometry");

            try
            {
                switch (type)
                {
                    case "parking":
                        parkings.Add(ParseParking(props, geom));
                        break;
                    case "helipad":
                        helipads.Add(ParseParking(props, geom));
                        break;
                    case "spot":
                        spots.Add(ParseSpot(props, geom));
                        break;
                    case "taxiway":
                        taxiways.Add(ParseTaxiway(props, geom));
                        break;
                    case "runway":
                        runways.Add(ParseRunway(props, geom));
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

        return (parkings, helipads, spots, taxiways, runways);
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
        FilletMode filletMode
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
                Position = new LatLon(spot.Lat, spot.Lon),
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
                    TurnoffByEnd = rwy.TurnoffByEnd,
                    PatternAltitudeAglFt = rwy.PatternAltitudeAglFt,
                    PatternSizeNm = rwy.PatternSizeNm,
                    NoTurnoffByEnd = rwy.NoTurnoffByEnd,
                }
            );
        }

        // Step 6: Create parking nodes and connect to nearest taxiway
        var parkingNodes = new List<GroundNode>();
        foreach (var pkg in parkings)
        {
            int id = nextNodeId++;
            var node = new GroundNode
            {
                Id = id,
                Position = new LatLon(pkg.Lat, pkg.Lon),
                Type = GroundNodeType.Parking,
                Name = pkg.Name,
                TrueHeading = new TrueHeading(pkg.Heading),
                Origin = "GeoJson:parking",
            };
            layout.Nodes[id] = node;
            parkingNodes.Add(node);

            ConnectParkingToTaxiway(node, layout);
        }

        // Step 6b: Create helipad nodes and connect to nearest taxiway (larger radius)
        foreach (var hp in helipads)
        {
            int id = nextNodeId++;
            var node = new GroundNode
            {
                Id = id,
                Position = new LatLon(hp.Lat, hp.Lon),
                Type = GroundNodeType.Helipad,
                Name = hp.Name,
                TrueHeading = new TrueHeading(hp.Heading),
                Origin = "GeoJson:helipad",
            };
            layout.Nodes[id] = node;

            ConnectToNearestTaxiway(node, layout, HelipadConnectMaxNm);
        }

        ConnectGateGroupsToAnchors(parkingNodes, layout);

        // Step 7: Wire up adjacency lists
        layout.RebuildAdjacencyLists();

        // Step 8: Generate fillet arcs at intersections
        if (filletMode != FilletMode.None)
        {
            FilletGeneratorFactory.Create(filletMode).Apply(layout);
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

    private static void ConnectGateGroupsToAnchors(IReadOnlyList<GroundNode> parkingNodes, AirportGroundLayout layout)
    {
        foreach (var gate in parkingNodes)
        {
            if (!TryExtractGatePrefix(gate.Name, out string prefix))
            {
                continue;
            }

            var anchor = FindConnectedGateAnchor(prefix, gate, parkingNodes, layout);
            if (anchor is null)
            {
                continue;
            }

            if (HasDirectEdge(layout, gate.Id, anchor.Id))
            {
                continue;
            }

            layout.Edges.Add(
                new GroundEdge
                {
                    Nodes = [gate, anchor],
                    TaxiwayName = "RAMP",
                    DistanceNm = GeoMath.DistanceNm(gate.Position, anchor.Position),
                    Origin = "GeoJson:gate-group-connector",
                }
            );
        }
    }

    private static bool TryExtractGatePrefix(string? name, out string prefix)
    {
        prefix = "";
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        int digitIndex = -1;
        for (int i = 0; i < name.Length; i++)
        {
            if (char.IsDigit(name[i]))
            {
                digitIndex = i;
                break;
            }

            if (!char.IsLetter(name[i]))
            {
                return false;
            }
        }

        if (digitIndex <= 0)
        {
            return false;
        }

        prefix = name[..digitIndex];
        return true;
    }

    private static GroundNode? FindConnectedGateAnchor(
        string prefix,
        GroundNode gate,
        IReadOnlyList<GroundNode> parkingNodes,
        AirportGroundLayout layout
    )
    {
        GroundNode? best = null;
        double bestDistanceNm = double.MaxValue;

        foreach (var candidate in parkingNodes)
        {
            if (!string.Equals(candidate.Name, prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!layout.Edges.Any(e => e.HasNode(candidate.Id)))
            {
                continue;
            }

            double distanceNm = GeoMath.DistanceNm(gate.Position, candidate.Position);
            if (distanceNm > GateGroupAnchorConnectMaxNm || distanceNm >= bestDistanceNm)
            {
                continue;
            }

            best = candidate;
            bestDistanceNm = distanceNm;
        }

        return best;
    }

    private static bool HasDirectEdge(AirportGroundLayout layout, int fromNodeId, int toNodeId)
    {
        return layout.Edges.Any(e => e.HasNode(fromNodeId) && e.HasNode(toNodeId));
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
        var target = FindNearestConnectorTarget(node, layout);

        if (target is null || target.Value.DistanceNm > maxDistNm)
        {
            return;
        }

        // Attach to the perpendicular-nearest taxiway EDGE's nearest existing vertex — no edge
        // splitting. Selecting the edge by perpendicular distance is what steers a gate onto the
        // closest taxiway line (e.g. MIA's south D-gates onto the concourse alley rather than the
        // Euclidean-nearest node on taxiway N). Connecting to a real vertex keeps every geojson
        // node ID stable and never mints a coincident split node or a zero-distance RAMP edge.
        var edge = target.Value.Edge;
        var nearer = target.Value.AlongNm <= edge.DistanceNm - target.Value.AlongNm ? edge.Nodes[0] : edge.Nodes[1];
        var farther = ReferenceEquals(nearer, edge.Nodes[0]) ? edge.Nodes[1] : edge.Nodes[0];

        // The nearer vertex is normally the right attachment, but if it sits within a fillet no-op
        // of the parking node (gate essentially on the vertex) the connector would be zero-distance;
        // fall back to the far endpoint, which is non-degenerate on any real taxiway edge.
        var endpoint = GeoMath.DistanceNm(node.Position, nearer.Position) < GeometricAdmissibility.NoOpEdgeThresholdNm ? farther : nearer;
        double connectorNm = GeoMath.DistanceNm(node.Position, endpoint.Position);
        if (connectorNm < GeometricAdmissibility.NoOpEdgeThresholdNm)
        {
            Log.LogDebug(
                "Skipping zero-distance parking connector for {Name} (node {Id}): coincident with taxiway vertex {VertexId}",
                node.Name,
                node.Id,
                endpoint.Id
            );
            return;
        }

        layout.Edges.Add(
            new GroundEdge
            {
                Nodes = [node, endpoint],
                TaxiwayName = "RAMP",
                DistanceNm = connectorNm,
                Origin = "GeoJson:parking-connector",
            }
        );
        // Node adjacency lists are wired up in Step 7 — don't add here to avoid duplicates.
    }

    private static ConnectorTarget? FindNearestConnectorTarget(GroundNode node, AirportGroundLayout layout)
    {
        ConnectorTarget? best = null;
        foreach (var edge in layout.Edges)
        {
            if (!CanConnectToEdge(edge))
            {
                continue;
            }

            var (footLat, footLon, alongNm, _) = GeoMath.FootOfPerpendicular(
                node.Position.Lat,
                node.Position.Lon,
                edge.Nodes[0].Position.Lat,
                edge.Nodes[0].Position.Lon,
                edge.Nodes[1].Position.Lat,
                edge.Nodes[1].Position.Lon
            );
            double distanceNm = GeoMath.DistanceNm(node.Position.Lat, node.Position.Lon, footLat, footLon);
            if (best is null || distanceNm < best.Value.DistanceNm)
            {
                best = new ConnectorTarget(edge, distanceNm, alongNm);
            }
        }

        return best;
    }

    private static bool CanConnectToEdge(GroundEdge edge)
    {
        if (edge.IsRamp || edge.IsRunwayCenterline || edge.IsRunwayCrossingLink)
        {
            return false;
        }

        return edge.Nodes.All(static n => n.Type is not GroundNodeType.Parking and not GroundNodeType.Helipad);
    }

    private readonly record struct ConnectorTarget(GroundEdge Edge, double DistanceNm, double AlongNm);

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

        var turnoff = ParseTurnoff(name, props);

        double? patternAltAgl = ReadOptionalDouble(props, "patternAltitude");
        double? patternSize = ReadOptionalDouble(props, "patternSize");

        var noTurnoff = ParseNoTurnoff(name, props);

        return new RunwayFeature(name, coords, turnoff, patternAltAgl, patternSize, noTurnoff);
    }

    /// <summary>
    /// Parse the "turnoff" property as a side relative to the first-named end's heading and produce a per-end map.
    /// The second end gets the flipped side so the same physical side of the runway resolves for either landing direction.
    /// </summary>
    private static IReadOnlyDictionary<string, ExitSide> ParseTurnoff(string runwayName, JsonElement props)
    {
        var empty = new Dictionary<string, ExitSide>(StringComparer.OrdinalIgnoreCase);
        if (!props.TryGetProperty("turnoff", out var t) || (t.ValueKind != JsonValueKind.String))
        {
            return empty;
        }

        ExitSide? end1Side = t.GetString()?.ToLowerInvariant() switch
        {
            "left" => ExitSide.Left,
            "right" => ExitSide.Right,
            _ => null,
        };
        if (end1Side is null)
        {
            return empty;
        }

        string[] ends = runwayName.Split('-', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (ends.Length != 2)
        {
            return empty;
        }

        var result = new Dictionary<string, ExitSide>(StringComparer.OrdinalIgnoreCase) { [ends[0]] = end1Side.Value };
        result[ends[1]] = (end1Side.Value == ExitSide.Left) ? ExitSide.Right : ExitSide.Left;
        return result;
    }

    private static double? ReadOptionalDouble(JsonElement props, string fieldName)
    {
        if (!props.TryGetProperty(fieldName, out var v))
        {
            return null;
        }
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), out double d) => d,
            _ => null,
        };
    }

    /// <summary>
    /// Parse noTurnoff: a 2-element array where index 0 corresponds to the first end-designator
    /// in the runway name (e.g. "10L" in "10L - 28R") and index 1 to the second.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseNoTurnoff(string runwayName, JsonElement props)
    {
        var empty = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (!props.TryGetProperty("noTurnoff", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return empty;
        }

        string[] ends = runwayName.Split('-', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (ends.Length != 2)
        {
            return empty;
        }

        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        int idx = 0;
        foreach (var subArr in arr.EnumerateArray())
        {
            if ((idx >= ends.Length) || (subArr.ValueKind != JsonValueKind.Array))
            {
                idx++;
                continue;
            }
            var names = new List<string>();
            foreach (var item in subArr.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    string? s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        names.Add(s);
                    }
                }
            }
            if (names.Count > 0)
            {
                result[ends[idx]] = names;
            }
            idx++;
        }
        return result;
    }

    // Internal feature DTOs — accessible to graph builder and crossing detector
    internal sealed record ParkingFeature(string Name, double Lat, double Lon, int Heading);

    internal sealed record SpotFeature(string Name, double Lat, double Lon);

    internal sealed record TaxiwayFeature(string Name, List<(double Lat, double Lon)> Coords);

    internal sealed record RunwayFeature(
        string Name,
        List<(double Lat, double Lon)> Coords,
        IReadOnlyDictionary<string, ExitSide>? TurnoffByEnd = null,
        double? PatternAltitudeAglFt = null,
        double? PatternSizeNm = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? NoTurnoffByEnd = null
    )
    {
        public IReadOnlyDictionary<string, ExitSide> TurnoffByEnd { get; init; } =
            TurnoffByEnd ?? new Dictionary<string, ExitSide>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, IReadOnlyList<string>> NoTurnoffByEnd { get; init; } =
            NoTurnoffByEnd ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
    }
}
