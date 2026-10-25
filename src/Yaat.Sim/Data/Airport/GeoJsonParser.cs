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

    private static readonly JsonDocumentOptions LenientJsonOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    /// <summary>Strips leading zeros from JSON number literals (e.g. 03 → 3) that are invalid per RFC 8259.</summary>
    private static readonly Regex LeadingZeroRegex = new(@"(?<=[:,\[]\s*)0+(\d)", RegexOptions.Compiled);

    private static string SanitizeJson(string json) => LeadingZeroRegex.Replace(json, "$1");

    /// <summary>
    /// Look a feature property up by key: exact match first, then a case-insensitive scan, since
    /// ATCTrainer exports PascalCase keys where the vNAS maps use camelCase.
    /// </summary>
    private static bool TryGetPropertyIgnoreCase(JsonElement props, string propertyName, out JsonElement value)
    {
        if (props.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        foreach (JsonProperty property in props.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    /// <summary>Read a name-style property as text, or null when it is absent or not a string or number.</summary>
    private static string? TryReadNameText(JsonElement props, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(props, propertyName, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    /// <summary>
    /// Read a name-style property as text: a JSON string as-is, or a JSON number as its raw literal
    /// (some maps carry unquoted numeric stand names). Absent or any other kind is a malformed feature.
    /// </summary>
    private static string ReadNameText(JsonElement props, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(props, propertyName, out JsonElement value))
        {
            throw new InvalidOperationException($"Property '{propertyName}' is missing");
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            _ => throw new InvalidOperationException($"Property '{propertyName}' is {value.ValueKind}, not a string or number"),
        };
    }

    /// <summary>Parse an airport's GeoJSON map into its ground layout, with Standard fillets.</summary>
    /// <param name="airportId">The layout's airport id.</param>
    /// <param name="geoJson">The map's GeoJSON FeatureCollection.</param>
    /// <param name="runwayAirportCode">The airport code runway widths are looked up by, or null for the default width.</param>
    /// <param name="standLeadIns">
    /// The airport's sidecar stand lead-ins (<see cref="AirportSidecarCatalog.GetStandLeadIns"/>): each listed stand is
    /// connected to its named taxiway's vertex instead of the nearest taxiway edge.
    /// </param>
    /// <returns>The layout.</returns>
    public static AirportGroundLayout Parse(string airportId, string geoJson, string? runwayAirportCode, IReadOnlyList<StandLeadIn> standLeadIns) =>
        Parse(airportId, geoJson, runwayAirportCode, standLeadIns, FilletMode.Standard);

    public static AirportGroundLayout Parse(
        string airportId,
        string geoJson,
        string? runwayAirportCode,
        IReadOnlyList<StandLeadIn> standLeadIns,
        bool applyFillets
    ) => Parse(airportId, geoJson, runwayAirportCode, standLeadIns, applyFillets ? FilletMode.Standard : FilletMode.None);

    public static AirportGroundLayout Parse(
        string airportId,
        string geoJson,
        string? runwayAirportCode,
        IReadOnlyList<StandLeadIn> standLeadIns,
        FilletMode filletMode
    ) => ParseCollectingIssues(new LayoutSource(airportId, runwayAirportCode, standLeadIns), geoJson, filletMode, issues: []);

    /// <summary>
    /// Parse as <see cref="Parse(string, string, string?, IReadOnlyList{StandLeadIn})"/> does (Standard fillets) and also return the
    /// features left out of the layout: one <see cref="GroundMapIssue"/> per unknown-type feature and per malformed feature skipped.
    /// </summary>
    /// <param name="airportId">The layout's airport id.</param>
    /// <param name="geoJson">The map's GeoJSON FeatureCollection.</param>
    /// <param name="runwayAirportCode">The airport code runway widths are looked up by, or null for the default width.</param>
    /// <param name="standLeadIns">The airport's sidecar stand lead-ins (<see cref="AirportSidecarCatalog.GetStandLeadIns"/>).</param>
    /// <returns>The layout and the dropped features, in feature order.</returns>
    public static (AirportGroundLayout Layout, IReadOnlyList<GroundMapIssue> Issues) ParseWithDiagnostics(
        string airportId,
        string geoJson,
        string? runwayAirportCode,
        IReadOnlyList<StandLeadIn> standLeadIns
    )
    {
        var issues = new List<GroundMapIssue>();
        AirportGroundLayout layout = ParseCollectingIssues(
            new LayoutSource(airportId, runwayAirportCode, standLeadIns),
            geoJson,
            FilletMode.Standard,
            issues
        );
        return (layout, issues);
    }

    /// <summary>What a layout is built for: its airport id, the code its runway widths are looked up by, and its stand lead-ins.</summary>
    private sealed record LayoutSource(string AirportId, string? RunwayAirportCode, IReadOnlyList<StandLeadIn> StandLeadIns);

    private static AirportGroundLayout ParseCollectingIssues(LayoutSource source, string geoJson, FilletMode filletMode, List<GroundMapIssue> issues)
    {
        string airportId = source.AirportId;
        string sanitized = SanitizeJson(geoJson);
        using var doc = JsonDocument.Parse(sanitized, LenientJsonOptions);
        JsonElement features = doc.RootElement.GetProperty("features");
        (
            List<ParkingFeature> Parkings,
            List<ParkingFeature> Helipads,
            List<SpotFeature> Spots,
            List<TaxiwayFeature> Taxiways,
            List<RunwayFeature> Runways
        ) classified = ClassifyFeatures(airportId, features.EnumerateArray(), issues);
        return BuildLayout(source, classified.Parkings, classified.Helipads, classified.Spots, classified.Taxiways, classified.Runways, filletMode);
    }

    /// <summary>
    /// Parse from multiple GeoJSON files (separate parking, taxiways, spots, runways).
    /// Features are merged and classified directly — no re-serialization.
    /// </summary>
    public static AirportGroundLayout ParseMultiple(
        string airportId,
        IEnumerable<string> geoJsonFiles,
        string? runwayAirportCode,
        IReadOnlyList<StandLeadIn> standLeadIns
    ) => ParseMultiple(airportId, geoJsonFiles, runwayAirportCode, standLeadIns, FilletMode.Standard);

    public static AirportGroundLayout ParseMultiple(
        string airportId,
        IEnumerable<string> geoJsonFiles,
        string? runwayAirportCode,
        IReadOnlyList<StandLeadIn> standLeadIns,
        FilletMode filletMode
    )
    {
        var merged = geoJsonFiles.ToList();

        if (merged.Count == 1)
        {
            return Parse(airportId, merged[0], runwayAirportCode, standLeadIns, filletMode);
        }

        var allFeatures = new List<JsonElement>();
        foreach (string json in merged)
        {
            string sanitized = SanitizeJson(json);
            using var doc = JsonDocument.Parse(sanitized, LenientJsonOptions);
            JsonElement features = doc.RootElement.GetProperty("features");
            foreach (JsonElement f in features.EnumerateArray())
            {
                allFeatures.Add(f.Clone());
            }
        }

        (
            List<ParkingFeature> Parkings,
            List<ParkingFeature> Helipads,
            List<SpotFeature> Spots,
            List<TaxiwayFeature> Taxiways,
            List<RunwayFeature> Runways
        ) classified = ClassifyFeatures(airportId, allFeatures, issues: []);
        return BuildLayout(
            new LayoutSource(airportId, runwayAirportCode, standLeadIns),
            classified.Parkings,
            classified.Helipads,
            classified.Spots,
            classified.Taxiways,
            classified.Runways,
            filletMode
        );
    }

    private static (
        List<ParkingFeature> Parkings,
        List<ParkingFeature> Helipads,
        List<SpotFeature> Spots,
        List<TaxiwayFeature> Taxiways,
        List<RunwayFeature> Runways
    ) ClassifyFeatures(string airportId, IEnumerable<JsonElement> features, List<GroundMapIssue> issues)
    {
        var parkings = new List<ParkingFeature>();
        var helipads = new List<ParkingFeature>();
        var spots = new List<SpotFeature>();
        var taxiways = new List<TaxiwayFeature>();
        var runways = new List<RunwayFeature>();

        int skipped = 0;
        foreach ((int index, JsonElement feature) in features.Index())
        {
            JsonElement props = feature.GetProperty("properties");
            string type = TryGetPropertyIgnoreCase(props, "type", out JsonElement typeProp) ? typeProp.GetString() ?? "" : "";
            JsonElement geom = feature.GetProperty("geometry");

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
                        issues.Add(
                            new GroundMapIssue(
                                GroundMapIssueKind.UnknownFeatureType,
                                index,
                                type,
                                TryReadNameText(props, "name"),
                                type.Length == 0 ? "no feature type" : "unknown feature type"
                            )
                        );
                        break;
                }
            }
            catch (InvalidOperationException ex)
            {
                string? name = TryReadNameText(props, "name");
                Log.LogWarning("Skipping malformed {Type} feature '{Name}' in {Airport}: {Message}", type, name ?? "?", airportId, ex.Message);
                issues.Add(new GroundMapIssue(GroundMapIssueKind.MalformedFeature, index, type, name, ex.Message));
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
        LayoutSource source,
        List<ParkingFeature> parkings,
        List<ParkingFeature> helipads,
        List<SpotFeature> spots,
        List<TaxiwayFeature> taxiways,
        List<RunwayFeature> runways,
        FilletMode filletMode
    )
    {
        string airportId = source.AirportId;
        string? runwayAirportCode = source.RunwayAirportCode;
        var layout = new AirportGroundLayout { AirportId = airportId };
        int nextNodeId = 0;

        // Spatial index for fast coordinate snapping
        var coordIndex = new CoordinateIndex(SnapToleranceDeg);

        // Step 1: Create spot nodes (named intersection / hold-short points)
        foreach (SpotFeature spot in spots)
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
        foreach (TaxiwayFeature tw in taxiways)
        {
            ProcessedTaxiway processed = TaxiwayGraphBuilder.ProcessTaxiway(tw, layout, coordIndex, ref nextNodeId);
            taxiwaySegments.Add(processed);
        }

        // Step 3: Detect intersections between taxiway segments
        TaxiwayGraphBuilder.DetectIntersections(taxiwaySegments, layout, coordIndex, ref nextNodeId);

        // Step 4: Build edges from processed taxiway vertex chains
        foreach (ProcessedTaxiway tw in taxiwaySegments)
        {
            TaxiwayGraphBuilder.BuildEdgesFromTaxiway(tw, layout);
        }

        // Step 4b: Remove overlapping edges (same two nodes, different taxiway names).
        // When two taxiways share an identical segment, keep the one that continues
        // through both endpoints; remove the one that terminates.
        RemoveOverlappingEdges(layout);

        // Step 5: Process runway LineStrings, detect taxiway-runway crossings
        var fallbackBars = new List<FallbackBar>();
        foreach (RunwayFeature rwy in runways)
        {
            RunwayCrossingResult crossings = RunwayCrossingDetector.DetectRunwayCrossings(rwy, layout, coordIndex, ref nextNodeId, runwayAirportCode);
            double rwyWidthFt = crossings.WidthFt;
            fallbackBars.AddRange(crossings.FallbackBars);

            layout.Runways.Add(
                new GroundRunway
                {
                    Name = rwy.Name,
                    Coordinates = [.. rwy.Coords],
                    WidthFt = rwyWidthFt,
                    HoldShortDistanceFt = rwy.HoldShortDistanceFt,
                    TurnoffByEnd = rwy.TurnoffByEnd,
                    PatternAltitudeAglFt = rwy.PatternAltitudeAglFt,
                    PatternSizeNm = rwy.PatternSizeNm,
                    NoTurnoffByEnd = rwy.NoTurnoffByEnd,
                    ThresholdDisplacementFtByEnd = rwy.ThresholdDisplacementFtByEnd,
                }
            );
        }

        // Step 5b: with every runway's bars in place, drop the dead-end fallback bars a joining taxiway's bar makes redundant.
        RunwayCrossingDetector.DropRedundantFallbackBars(layout, fallbackBars);

        // Step 6: Create parking nodes and connect each to its sidecar lead-in's taxiway vertex, else to the nearest taxiway
        Dictionary<string, StandLeadIn> leadInsByStand = UsableStandLeadIns(airportId, parkings, source.StandLeadIns);
        var parkingNodes = new List<GroundNode>();
        foreach (ParkingFeature pkg in parkings)
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

            GroundNode? leadInVertex = leadInsByStand.TryGetValue(pkg.Name, out StandLeadIn? leadIn)
                ? ConnectStandLeadIn(airportId, node, leadIn, layout)
                : null;
            LatLon? foot = leadInVertex is null ? ConnectParkingToTaxiway(node, layout) : leadInVertex.Position;
            node.StandDeparture = StandDepartures.Classify(node, foot);
        }

        // Step 6b: Create helipad nodes and connect to nearest taxiway (larger radius)
        foreach (ParkingFeature hp in helipads)
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

        // Step 9: with every node in its final place, mark each runway bar as its runway's hold line or a short fallback bar
        RunwayCrossingDetector.MarkShortBars(layout);

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

    /// <summary>
    /// Joins <paramref name="parking"/> to the nearest taxiway (<see cref="ConnectToNearestTaxiway"/>).
    /// </summary>
    /// <returns>The foot of the perpendicular from the stand onto the taxiway edge joined, or null when no connector was added.</returns>
    private static LatLon? ConnectParkingToTaxiway(GroundNode parking, AirportGroundLayout layout)
    {
        GroundEdge? joined = ConnectToNearestTaxiway(parking, layout, ParkingConnectMaxNm);
        return joined is null ? null : GeoMath.FootOfPerpendicular(parking.Position, joined.Nodes[0].Position, joined.Nodes[1].Position).Foot;
    }

    /// <summary>
    /// The lead-ins whose stand names exactly one parking stand, by stand name (case-insensitive). An entry naming no stand or
    /// several is warned about and ignored, so those stands keep their nearest-taxiway connectors.
    /// </summary>
    private static Dictionary<string, StandLeadIn> UsableStandLeadIns(
        string airportId,
        List<ParkingFeature> parkings,
        IReadOnlyList<StandLeadIn> leadIns
    )
    {
        var byStand = new Dictionary<string, StandLeadIn>(StringComparer.OrdinalIgnoreCase);
        foreach (StandLeadIn leadIn in leadIns)
        {
            int matches = parkings.Count(p => string.Equals(p.Name, leadIn.Stand, StringComparison.OrdinalIgnoreCase));
            if (matches == 1)
            {
                byStand[leadIn.Stand] = leadIn;
                continue;
            }

            Log.LogWarning(
                "{Airport}: sidecar standLeadIns entry for stand {Stand} (taxiway {Taxiway}) {Problem}; ignored",
                airportId,
                leadIn.Stand,
                leadIn.Taxiway,
                matches == 0 ? "names no parking stand" : $"names {matches} parking stands"
            );
        }

        return byStand;
    }

    /// <summary>
    /// Joins a stand its sidecar lead-in lists to the vertex of the lead-in's taxiway nearest the lead-in's point, with a
    /// <c>RAMP</c> connector of the same origin as a nearest-taxiway one, so it is the stand's one connector. A vertex is a
    /// node with at least one edge of that taxiway a stand may connect to (<see cref="CanConnectToEdge"/>: not <c>RAMP</c>, not a
    /// runway centreline or crossing link, not touching a parking or helipad node).
    /// </summary>
    /// <returns>
    /// The vertex joined, or null (warned about) when no vertex of the taxiway lies within <see cref="ParkingConnectMaxNm"/> of
    /// the point, or the vertex sits on the stand.
    /// </returns>
    private static GroundNode? ConnectStandLeadIn(string airportId, GroundNode stand, StandLeadIn leadIn, AirportGroundLayout layout)
    {
        GroundNode? vertex = layout
            .Edges.Where(e => CanConnectToEdge(e) && string.Equals(e.TaxiwayName, leadIn.Taxiway, StringComparison.OrdinalIgnoreCase))
            .SelectMany(e => e.Nodes)
            .MinBy(n => GeoMath.DistanceNm(n.Position, leadIn.Point));
        if ((vertex is null) || (GeoMath.DistanceNm(vertex.Position, leadIn.Point) > ParkingConnectMaxNm))
        {
            Log.LogWarning(
                "{Airport}: sidecar standLeadIns entry for stand {Stand} finds no vertex of taxiway {Taxiway} within {MaxNm} nm; ignored",
                airportId,
                leadIn.Stand,
                leadIn.Taxiway,
                ParkingConnectMaxNm
            );
            return null;
        }

        double connectorNm = GeoMath.DistanceNm(stand.Position, vertex.Position);
        if (connectorNm < GeometricAdmissibility.NoOpEdgeThresholdNm)
        {
            Log.LogWarning(
                "{Airport}: sidecar standLeadIns entry for stand {Stand} joins {Taxiway} at node {VertexId}, on the stand; ignored",
                airportId,
                leadIn.Stand,
                leadIn.Taxiway,
                vertex.Id
            );
            return null;
        }

        layout.Edges.Add(
            new GroundEdge
            {
                Nodes = [stand, vertex],
                TaxiwayName = "RAMP",
                DistanceNm = connectorNm,
                Origin = "GeoJson:parking-connector",
            }
        );
        return vertex;
    }

    private static void ConnectGateGroupsToAnchors(IReadOnlyList<GroundNode> parkingNodes, AirportGroundLayout layout)
    {
        foreach (GroundNode gate in parkingNodes)
        {
            if (!TryExtractGatePrefix(gate.Name, out string prefix))
            {
                continue;
            }

            GroundNode? anchor = FindConnectedGateAnchor(prefix, gate, parkingNodes, layout);
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

        foreach (GroundNode candidate in parkingNodes)
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

    private static bool HasDirectEdge(AirportGroundLayout layout, int fromNodeId, int toNodeId) =>
        layout.Edges.Any(e => e.HasNode(fromNodeId) && e.HasNode(toNodeId));

    /// <summary>
    /// Remove overlapping edges: when two taxiways share an identical segment (same
    /// two nodes, different names), keep the taxiway that continues through both
    /// endpoints and remove the one that terminates at one end.
    /// </summary>
    private static void RemoveOverlappingEdges(AirportGroundLayout layout)
    {
        // Group edges by node pair (order-independent)
        var byNodePair = new Dictionary<(int, int), List<GroundEdge>>();
        foreach (GroundEdge edge in layout.Edges)
        {
            int a = Math.Min(edge.Nodes[0].Id, edge.Nodes[1].Id);
            int b = Math.Max(edge.Nodes[0].Id, edge.Nodes[1].Id);
            (int a, int b) key = (a, b);
            if (!byNodePair.TryGetValue(key, out List<GroundEdge>? list))
            {
                list = [];
                byNodePair[key] = list;
            }
            list.Add(edge);
        }

        var toRemove = new HashSet<GroundEdge>();
        foreach (((int, int) pair, List<GroundEdge>? edges) in byNodePair)
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
                    GroundEdge edgeA = edges[i];
                    GroundEdge edgeB = edges[j];
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
        foreach (GroundEdge edge in layout.Edges)
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

    /// <summary>
    /// Joins <paramref name="node"/> to the nearest vertex of the taxiway edge nearest it by perpendicular distance, with a
    /// <c>RAMP</c> connector.
    /// </summary>
    /// <returns>The taxiway edge the connector joined, or null when no connector was added.</returns>
    private static GroundEdge? ConnectToNearestTaxiway(GroundNode node, AirportGroundLayout layout, double maxDistNm)
    {
        ConnectorTarget? target = FindNearestConnectorTarget(node, layout);

        if (target is null || target.Value.DistanceNm > maxDistNm)
        {
            return null;
        }

        // Attach to the perpendicular-nearest taxiway EDGE's nearest existing vertex — no edge
        // splitting. Selecting the edge by perpendicular distance is what steers a gate onto the
        // closest taxiway line (e.g. MIA's south D-gates onto the concourse alley rather than the
        // Euclidean-nearest node on taxiway N). Connecting to a real vertex keeps every geojson
        // node ID stable and never mints a coincident split node or a zero-distance RAMP edge.
        GroundEdge edge = target.Value.Edge;
        GroundNode nearer = target.Value.AlongNm <= edge.DistanceNm - target.Value.AlongNm ? edge.Nodes[0] : edge.Nodes[1];
        GroundNode farther = ReferenceEquals(nearer, edge.Nodes[0]) ? edge.Nodes[1] : edge.Nodes[0];

        // The nearer vertex is normally the right attachment, but if it sits within a fillet no-op
        // of the parking node (gate essentially on the vertex) the connector would be zero-distance;
        // fall back to the far endpoint, which is non-degenerate on any real taxiway edge.
        GroundNode endpoint = GeoMath.DistanceNm(node.Position, nearer.Position) < GeometricAdmissibility.NoOpEdgeThresholdNm ? farther : nearer;
        double connectorNm = GeoMath.DistanceNm(node.Position, endpoint.Position);
        if (connectorNm < GeometricAdmissibility.NoOpEdgeThresholdNm)
        {
            Log.LogDebug(
                "Skipping zero-distance parking connector for {Name} (node {Id}): coincident with taxiway vertex {VertexId}",
                node.Name,
                node.Id,
                endpoint.Id
            );
            return null;
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
        return edge;
    }

    private static ConnectorTarget? FindNearestConnectorTarget(GroundNode node, AirportGroundLayout layout)
    {
        ConnectorTarget? best = null;
        foreach (GroundEdge edge in layout.Edges)
        {
            if (!CanConnectToEdge(edge))
            {
                continue;
            }

            (double footLat, double footLon, double alongNm, bool _) = GeoMath.FootOfPerpendicular(
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
        JsonElement coords = geom.GetProperty("coordinates");
        double lon = coords[0].GetDouble();
        double lat = coords[1].GetDouble();
        string name = ReadNameText(props, "name");
        int heading = 0;
        if (TryGetPropertyIgnoreCase(props, "heading", out JsonElement h))
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
        JsonElement coords = geom.GetProperty("coordinates");
        double lon = coords[0].GetDouble();
        double lat = coords[1].GetDouble();
        string name = ReadNameText(props, "name");
        return new SpotFeature(name, lat, lon);
    }

    private static (string Name, List<(double Lat, double Lon)> Coords) ParseLineString(JsonElement props, JsonElement geom)
    {
        string name = ReadNameText(props, "name");
        JsonElement coordsArray = geom.GetProperty("coordinates");
        var coords = new List<(double Lat, double Lon)>();
        foreach (JsonElement coord in coordsArray.EnumerateArray())
        {
            double lon = coord[0].GetDouble();
            double lat = coord[1].GetDouble();
            coords.Add((lat, lon));
        }

        return (name, coords);
    }

    private static TaxiwayFeature ParseTaxiway(JsonElement props, JsonElement geom)
    {
        (string? name, List<(double Lat, double Lon)>? coords) = ParseLineString(props, geom);
        return new TaxiwayFeature(name, coords);
    }

    private static RunwayFeature ParseRunway(JsonElement props, JsonElement geom)
    {
        (string? name, List<(double Lat, double Lon)>? coords) = ParseLineString(props, geom);

        IReadOnlyDictionary<string, ExitSide> turnoff = ParseTurnoff(name, props);

        double? patternAltAgl = ReadOptionalDouble(props, "patternAltitude");
        double? patternSize = ReadOptionalDouble(props, "patternSize");

        IReadOnlyDictionary<string, IReadOnlyList<string>> noTurnoff = ParseNoTurnoff(name, props);

        // Authoritative per-runway hold-short standoff (ft from centerline) from the vNAS map;
        // when absent RunwayCrossingDetector falls back to the width-based FAA Table 3-2 heuristic.
        double? holdShortDistance = ReadOptionalDouble(props, "holdShortDistance");

        IReadOnlyDictionary<string, double> thresholdDisplacement = ParseThresholdDisplacement(name, props);

        return new RunwayFeature(name, coords, turnoff, patternAltAgl, patternSize, noTurnoff, holdShortDistance, thresholdDisplacement);
    }

    /// <summary>
    /// Parse the "threshold" property — displaced-threshold distances in feet as "end1 - end2"
    /// (e.g. "0 - 957"), in the same end order as the runway name. Produces a per-end map keyed by
    /// the zero-pad-normalized designator. Absent, null, or unparseable values yield an empty map,
    /// which <see cref="GroundRunway.ThresholdDisplacementForEnd"/> reads as no displacement.
    /// </summary>
    private static IReadOnlyDictionary<string, double> ParseThresholdDisplacement(string runwayName, JsonElement props)
    {
        var empty = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (!TryGetPropertyIgnoreCase(props, "threshold", out JsonElement t) || (t.ValueKind != JsonValueKind.String))
        {
            return empty;
        }

        string[] values = (t.GetString() ?? "").Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        string[] ends = runwayName.Split('-', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if ((values.Length != 2) || (ends.Length != 2))
        {
            return empty;
        }

        if (!double.TryParse(values[0], out double end1Ft) || !double.TryParse(values[1], out double end2Ft))
        {
            return empty;
        }

        return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            [RunwayIdentifier.NormalizeDesignator(ends[0])] = end1Ft,
            [RunwayIdentifier.NormalizeDesignator(ends[1])] = end2Ft,
        };
    }

    /// <summary>
    /// Parse the "turnoff" property as a side relative to the first-named end's heading and produce a per-end map.
    /// The second end gets the flipped side so the same physical side of the runway resolves for either landing direction.
    /// </summary>
    private static IReadOnlyDictionary<string, ExitSide> ParseTurnoff(string runwayName, JsonElement props)
    {
        var empty = new Dictionary<string, ExitSide>(StringComparer.OrdinalIgnoreCase);
        if (!TryGetPropertyIgnoreCase(props, "turnoff", out JsonElement t) || (t.ValueKind != JsonValueKind.String))
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

        // Key by the zero-pad-normalized designator ("9" → "09") so the runtime, which always
        // carries the normalized identity, resolves the authored side at single-digit runways.
        string end1Key = RunwayIdentifier.NormalizeDesignator(ends[0]);
        string end2Key = RunwayIdentifier.NormalizeDesignator(ends[1]);
        var result = new Dictionary<string, ExitSide>(StringComparer.OrdinalIgnoreCase) { [end1Key] = end1Side.Value };
        result[end2Key] = (end1Side.Value == ExitSide.Left) ? ExitSide.Right : ExitSide.Left;
        return result;
    }

    private static double? ReadOptionalDouble(JsonElement props, string fieldName)
    {
        if (!TryGetPropertyIgnoreCase(props, fieldName, out JsonElement v))
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
        if (!TryGetPropertyIgnoreCase(props, "noTurnoff", out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
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
        foreach (JsonElement subArr in arr.EnumerateArray())
        {
            if ((idx >= ends.Length) || (subArr.ValueKind != JsonValueKind.Array))
            {
                idx++;
                continue;
            }
            var names = new List<string>();
            foreach (JsonElement item in subArr.EnumerateArray())
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
                result[RunwayIdentifier.NormalizeDesignator(ends[idx])] = names;
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
        IReadOnlyDictionary<string, IReadOnlyList<string>>? NoTurnoffByEnd = null,
        double? HoldShortDistanceFt = null,
        IReadOnlyDictionary<string, double>? ThresholdDisplacementFtByEnd = null
    )
    {
        public IReadOnlyDictionary<string, ExitSide> TurnoffByEnd { get; init; } =
            TurnoffByEnd ?? new Dictionary<string, ExitSide>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, IReadOnlyList<string>> NoTurnoffByEnd { get; init; } =
            NoTurnoffByEnd ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, double> ThresholdDisplacementFtByEnd { get; init; } =
            ThresholdDisplacementFtByEnd ?? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    }
}
