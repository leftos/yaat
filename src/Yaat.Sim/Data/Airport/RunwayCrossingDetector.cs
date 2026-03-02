using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Detects taxiway-runway crossings and inserts hold-short nodes at runway boundaries.
/// Based on AC 150/5300-13B Table 3-2 hold-short distance standards.
/// </summary>
internal static class RunwayCrossingDetector
{
    /// <summary>Default runway width (ft) when navdata is unavailable.</summary>
    private const double DefaultRunwayWidthFt = 150.0;

    /// <summary>Tolerance (nm) for runway boundary classification (~6ft).</summary>
    private const double RunwayTolerance = 0.001;

    /// <summary>Tolerance (ft) for reusing an existing node as hold-short.</summary>
    private const double HoldShortReuseFt = 50.0;

    internal static double DetectRunwayCrossings(
        GeoJsonParser.RunwayFeature rwy,
        AirportGroundLayout layout,
        CoordinateIndex coordIndex,
        ref int nextNodeId,
        ILogger? logger,
        IRunwayLookup? runwayLookup,
        string? runwayAirportCode
    )
    {
        var combinedId = RunwayIdentifier.Parse(rwy.Name);

        // Look up runway width from navdata; fall back to default
        double widthFt = DefaultRunwayWidthFt;
        if (runwayLookup is not null && runwayAirportCode is not null)
        {
            var rwyInfo = runwayLookup.GetRunway(runwayAirportCode, combinedId.End1) ?? runwayLookup.GetRunway(runwayAirportCode, combinedId.End2);
            if (rwyInfo is not null)
            {
                widthFt = rwyInfo.WidthFt;
            }
        }

        var rect = BuildRunwayRectangle(rwy, widthFt, combinedId);

        // Classify every node as on-runway or off-runway
        var onRunwayNodes = new HashSet<int>();
        foreach (var (nodeId, node) in layout.Nodes)
        {
            if (IsOnRunway(node.Latitude, node.Longitude, rect))
            {
                onRunwayNodes.Add(nodeId);
            }
        }

        // Snapshot edges — we mutate during iteration
        var edgeSnapshot = new List<GroundEdge>(layout.Edges);
        var processed = new HashSet<(int, int)>();

        foreach (var edge in edgeSnapshot)
        {
            if (edge.TaxiwayName.StartsWith("RWY", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool fromOn = onRunwayNodes.Contains(edge.FromNodeId);
            bool toOn = onRunwayNodes.Contains(edge.ToNodeId);

            // Only process boundary edges (one on, one off)
            if (fromOn == toOn)
            {
                continue;
            }

            int onId = fromOn ? edge.FromNodeId : edge.ToNodeId;
            int offId = fromOn ? edge.ToNodeId : edge.FromNodeId;

            // Avoid processing the same boundary pair twice
            var key = (Math.Min(onId, offId), Math.Max(onId, offId));
            if (!processed.Add(key))
            {
                continue;
            }

            if (!layout.Nodes.TryGetValue(onId, out var onNode) || !layout.Nodes.TryGetValue(offId, out var offNode))
            {
                continue;
            }

            ProcessBoundaryEdge(layout, edge, onNode, offNode, rect, coordIndex, ref nextNodeId, logger);
        }

        return widthFt;
    }

    internal static RunwayRectangle BuildRunwayRectangle(GeoJsonParser.RunwayFeature rwy, double widthFt, RunwayIdentifier combinedId)
    {
        double heading = GeoMath.BearingTo(rwy.Coords[0].Lat, rwy.Coords[0].Lon, rwy.Coords[^1].Lat, rwy.Coords[^1].Lon);
        double lengthNm = GeoMath.DistanceNm(rwy.Coords[0].Lat, rwy.Coords[0].Lon, rwy.Coords[^1].Lat, rwy.Coords[^1].Lon);
        double halfWidthNm = (widthFt / 2.0) / GeoMath.FeetPerNm;
        double holdShortNm = HoldShortDistanceForWidth(widthFt) / GeoMath.FeetPerNm;

        return new RunwayRectangle
        {
            RefLat = rwy.Coords[0].Lat,
            RefLon = rwy.Coords[0].Lon,
            Heading = heading,
            LengthNm = lengthNm,
            HalfWidthNm = halfWidthNm,
            HoldShortNm = holdShortNm,
            CombinedId = combinedId,
        };
    }

    internal static bool IsOnRunway(double lat, double lon, in RunwayRectangle rect)
    {
        double crossTrack = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(lat, lon, rect.RefLat, rect.RefLon, rect.Heading));
        double alongTrack = GeoMath.AlongTrackDistanceNm(lat, lon, rect.RefLat, rect.RefLon, rect.Heading);

        return crossTrack <= rect.HalfWidthNm + RunwayTolerance && alongTrack >= -RunwayTolerance && alongTrack <= rect.LengthNm + RunwayTolerance;
    }

    private static void ProcessBoundaryEdge(
        AirportGroundLayout layout,
        GroundEdge edge,
        GroundNode onNode,
        GroundNode offNode,
        in RunwayRectangle rect,
        CoordinateIndex coordIndex,
        ref int nextNodeId,
        ILogger? logger
    )
    {
        double crossOff = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(offNode.Latitude, offNode.Longitude, rect.RefLat, rect.RefLon, rect.Heading));
        double crossOn = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(onNode.Latitude, onNode.Longitude, rect.RefLat, rect.RefLon, rect.Heading));

        double distOffToIdeal = Math.Abs(crossOff - rect.HoldShortNm) * GeoMath.FeetPerNm;

        if (distOffToIdeal <= HoldShortReuseFt && offNode.Type != GroundNodeType.RunwayHoldShort)
        {
            // Existing node is close enough — upgrade it to hold-short
            var upgraded = new GroundNode
            {
                Id = offNode.Id,
                Latitude = offNode.Latitude,
                Longitude = offNode.Longitude,
                Type = GroundNodeType.RunwayHoldShort,
                RunwayId = rect.CombinedId,
                Name = offNode.Name,
            };
            layout.Nodes[offNode.Id] = upgraded;

            logger?.LogDebug("Reused node {NodeId} as hold-short for {Runway} on {Taxiway}", offNode.Id, rect.CombinedId, edge.TaxiwayName);
            return;
        }

        // Interpolate a new HS node at the correct cross-track distance
        double denom = crossOff - crossOn;
        if (Math.Abs(denom) < 1e-9)
        {
            return;
        }

        double fraction = (rect.HoldShortNm - crossOn) / denom;
        fraction = Math.Clamp(fraction, 0.01, 0.99);

        double hsLat = onNode.Latitude + fraction * (offNode.Latitude - onNode.Latitude);
        double hsLon = onNode.Longitude + fraction * (offNode.Longitude - onNode.Longitude);

        int hsId = nextNodeId++;
        var hsNode = new GroundNode
        {
            Id = hsId,
            Latitude = hsLat,
            Longitude = hsLon,
            Type = GroundNodeType.RunwayHoldShort,
            RunwayId = rect.CombinedId,
        };
        layout.Nodes[hsId] = hsNode;
        coordIndex.Add(hsLat, hsLon, hsId);

        SplitEdgeAtOneNode(layout, edge, hsNode);

        logger?.LogDebug(
            "Runway crossing: {Taxiway} boundary at {Runway} — hold-short node {NodeId} at ({Lat:F6}, {Lon:F6})",
            edge.TaxiwayName,
            rect.CombinedId,
            hsId,
            hsLat,
            hsLon
        );
    }

    /// <summary>
    /// Determines the hold-short distance from runway centerline (ft) based on
    /// runway width as a proxy for Airplane Design Group (ADG).
    /// Per AC 150/5300-13B Table 3-2.
    /// </summary>
    private static double HoldShortDistanceForWidth(double runwayWidthFt)
    {
        return runwayWidthFt switch
        {
            < 75 => 125, // ADG I: small GA (e.g., Cessna 172, Beechcraft Baron)
            < 100 => 200, // ADG II: regional (e.g., King Air, CRJ-200)
            < 150 => 250, // ADG III: commercial (e.g., B737, A320)
            _ => 300, // ADG IV-VI: major (e.g., B777, A380)
        };
    }

    /// <summary>
    /// Splits an edge into two segments through one intermediate node.
    /// Replaces: from-to with from-mid, mid-to.
    /// </summary>
    private static void SplitEdgeAtOneNode(AirportGroundLayout layout, GroundEdge edge, GroundNode midNode)
    {
        layout.Edges.Remove(edge);

        var fromNode = layout.Nodes[edge.FromNodeId];
        var toNode = layout.Nodes[edge.ToNodeId];

        layout.Edges.Add(
            new GroundEdge
            {
                FromNodeId = edge.FromNodeId,
                ToNodeId = midNode.Id,
                TaxiwayName = edge.TaxiwayName,
                DistanceNm = GeoMath.DistanceNm(fromNode.Latitude, fromNode.Longitude, midNode.Latitude, midNode.Longitude),
            }
        );

        layout.Edges.Add(
            new GroundEdge
            {
                FromNodeId = midNode.Id,
                ToNodeId = edge.ToNodeId,
                TaxiwayName = edge.TaxiwayName,
                DistanceNm = GeoMath.DistanceNm(midNode.Latitude, midNode.Longitude, toNode.Latitude, toNode.Longitude),
            }
        );
    }
}

/// <summary>
/// Geometric representation of a runway as an oriented rectangle for node classification.
/// </summary>
internal readonly struct RunwayRectangle
{
    public required double RefLat { get; init; }
    public required double RefLon { get; init; }
    public required double Heading { get; init; }
    public required double LengthNm { get; init; }
    public required double HalfWidthNm { get; init; }
    public required double HoldShortNm { get; init; }
    public required RunwayIdentifier CombinedId { get; init; }
}
