using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Detects taxiway-runway crossings and inserts hold-short nodes at runway boundaries.
/// Based on AC 150/5300-13B Table 3-2 hold-short distance standards.
/// </summary>
internal static class RunwayCrossingDetector
{
    private static readonly ILogger Log = SimLog.CreateLogger("RunwayCrossingDetector");

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

            ProcessBoundaryEdge(layout, edge, onNode, offNode, rect, coordIndex, ref nextNodeId);
        }

        // Connect on-runway nodes with centerline edges so that taxiways
        // crossing the same runway are linked (e.g., D and F at OAK both cross
        // 15/33 but have no GeoJSON edges between them).
        ConnectOnRunwayNodes(layout, rect);

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
        ref int nextNodeId
    )
    {
        double crossOff = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(offNode.Latitude, offNode.Longitude, rect.RefLat, rect.RefLon, rect.Heading));
        double crossOn = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(onNode.Latitude, onNode.Longitude, rect.RefLat, rect.RefLon, rect.Heading));

        double distOffToIdeal = Math.Abs(crossOff - rect.HoldShortNm) * GeoMath.FeetPerNm;

        // Don't reuse junction nodes (connected to multiple taxiways) as hold-short —
        // aircraft holding short would block other taxiways. Only reuse simple
        // intermediate nodes that serve a single taxiway.
        bool isJunction = HasMultipleTaxiwayConnections(offNode.Id, layout);

        if (distOffToIdeal <= HoldShortReuseFt && offNode.Type != GroundNodeType.RunwayHoldShort && !isJunction)
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

            Log.LogDebug("Reused node {NodeId} as hold-short for {Runway} on {Taxiway}", offNode.Id, rect.CombinedId, edge.TaxiwayName);
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

        Log.LogDebug(
            "Runway crossing: {Taxiway} boundary at {Runway} — hold-short node {NodeId} at ({Lat:F6}, {Lon:F6})",
            edge.TaxiwayName,
            rect.CombinedId,
            hsId,
            hsLat,
            hsLon
        );
    }

    /// <summary>
    /// After HS node insertion, connect the on-runway side of each HS node pair
    /// with RWY centerline edges. For each HS node, identifies the neighbor that's
    /// closer to the runway centerline (the on-runway dead-end), sorts them by
    /// along-track position, and links consecutive ones.
    /// </summary>
    private static void ConnectOnRunwayNodes(AirportGroundLayout layout, in RunwayRectangle rect)
    {
        string rwyEdgeName = $"RWY{rect.CombinedId}";

        // Classify all nodes as on/off runway for walk lookups
        var onRunwaySet = new HashSet<int>();
        foreach (var (nid, n) in layout.Nodes)
        {
            if (IsOnRunway(n.Latitude, n.Longitude, rect))
            {
                onRunwaySet.Add(nid);
            }
        }

        // For each HS node, walk from the on-runway neighbor toward the
        // centerline to find the best representative node for RWY edges.
        var onRunwayNodes = new List<(int Id, double AlongTrack)>();
        var seen = new HashSet<int>();

        foreach (var (nodeId, node) in layout.Nodes)
        {
            if (node.Type != GroundNodeType.RunwayHoldShort)
            {
                continue;
            }

            if (node.RunwayId is not { } rId || !rId.Equals(rect.CombinedId))
            {
                continue;
            }

            int bestId = FindCenterlineNode(nodeId, layout, rect, onRunwaySet);

            if (bestId != -1 && seen.Add(bestId))
            {
                var bestNode = layout.Nodes[bestId];
                double at = GeoMath.AlongTrackDistanceNm(bestNode.Latitude, bestNode.Longitude, rect.RefLat, rect.RefLon, rect.Heading);
                onRunwayNodes.Add((bestId, at));
            }
        }

        if (onRunwayNodes.Count < 2)
        {
            return;
        }

        onRunwayNodes.Sort((a, b) => a.AlongTrack.CompareTo(b.AlongTrack));

        for (int i = 0; i < onRunwayNodes.Count - 1; i++)
        {
            int fromId = onRunwayNodes[i].Id;
            int toId = onRunwayNodes[i + 1].Id;

            // Skip if already connected by any edge
            bool alreadyConnected = false;
            foreach (var edge in layout.Edges)
            {
                if ((edge.FromNodeId == fromId && edge.ToNodeId == toId) || (edge.FromNodeId == toId && edge.ToNodeId == fromId))
                {
                    alreadyConnected = true;
                    break;
                }
            }

            if (alreadyConnected)
            {
                continue;
            }

            var from = layout.Nodes[fromId];
            var to = layout.Nodes[toId];
            double dist = GeoMath.DistanceNm(from.Latitude, from.Longitude, to.Latitude, to.Longitude);

            var rwyEdge = new GroundEdge
            {
                FromNodeId = fromId,
                ToNodeId = toId,
                TaxiwayName = rwyEdgeName,
                DistanceNm = dist,
            };

            layout.Edges.Add(rwyEdge);
            // Node adjacency lists are wired up in GeoJsonParser Step 7.

            Log.LogDebug("Runway centerline edge: {From}->{To} on {Runway} ({DistFt:F0}ft)", fromId, toId, rect.CombinedId, dist * GeoMath.FeetPerNm);
        }
    }

    /// <summary>
    /// Buffer distance (ft) from the runway edge to the hold-short node.
    /// Real-world FAA distances (AC 150/5300-13B Table 3-2) place hold-shorts
    /// 125-300ft from centerline, but that pushes nodes far from the runway and
    /// close to nearby taxiway junctions. For simulation purposes a tighter
    /// buffer from the runway edge produces better-looking stop positions.
    /// </summary>
    private const double HoldShortBufferFromEdgeFt = 75.0;

    /// <summary>
    /// Determines the hold-short distance from runway centerline (ft).
    /// Uses the runway half-width plus a fixed buffer from the runway edge.
    /// </summary>
    private static double HoldShortDistanceForWidth(double runwayWidthFt)
    {
        return (runwayWidthFt / 2.0) + HoldShortBufferFromEdgeFt;
    }

    /// <summary>
    /// Starting from an HS node, finds its on-runway neighbor then walks along
    /// on-runway nodes (via non-RWY edges) to find the node closest to the
    /// runway centerline. This avoids using an intermediate on-runway node
    /// that's off-centerline when a centerline node exists one or more hops away.
    /// </summary>
    private static int FindCenterlineNode(int hsNodeId, AirportGroundLayout layout, in RunwayRectangle rect, HashSet<int> onRunwaySet)
    {
        // Step 1: find the immediate on-runway neighbor of the HS node
        int startId = -1;
        double startCrossTrack = double.MaxValue;

        foreach (var edge in layout.Edges)
        {
            int neighborId;
            if (edge.FromNodeId == hsNodeId)
            {
                neighborId = edge.ToNodeId;
            }
            else if (edge.ToNodeId == hsNodeId)
            {
                neighborId = edge.FromNodeId;
            }
            else
            {
                continue;
            }

            if (!layout.Nodes.TryGetValue(neighborId, out var neighbor))
            {
                continue;
            }

            double ct = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(neighbor.Latitude, neighbor.Longitude, rect.RefLat, rect.RefLon, rect.Heading));
            if (ct < startCrossTrack)
            {
                startCrossTrack = ct;
                startId = neighborId;
            }
        }

        if (startId == -1)
        {
            return -1;
        }

        // Step 2: walk along on-runway neighbors (non-RWY edges) toward centerline
        int bestId = startId;
        double bestCrossTrack = startCrossTrack;
        var visited = new HashSet<int> { hsNodeId, startId };

        var queue = new Queue<int>();
        queue.Enqueue(startId);

        while (queue.Count > 0)
        {
            int current = queue.Dequeue();

            foreach (var edge in layout.Edges)
            {
                if (edge.TaxiwayName.StartsWith("RWY", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int nextId;
                if (edge.FromNodeId == current)
                {
                    nextId = edge.ToNodeId;
                }
                else if (edge.ToNodeId == current)
                {
                    nextId = edge.FromNodeId;
                }
                else
                {
                    continue;
                }

                if (!visited.Add(nextId) || !onRunwaySet.Contains(nextId))
                {
                    continue;
                }

                var nextNode = layout.Nodes[nextId];
                double ct = Math.Abs(
                    GeoMath.SignedCrossTrackDistanceNm(nextNode.Latitude, nextNode.Longitude, rect.RefLat, rect.RefLon, rect.Heading)
                );

                if (ct < bestCrossTrack)
                {
                    bestCrossTrack = ct;
                    bestId = nextId;
                }

                queue.Enqueue(nextId);
            }
        }

        return bestId;
    }

    /// <summary>
    /// Returns true if the node has edges connecting to more than one distinct
    /// non-runway taxiway, making it a junction that shouldn't be reused as hold-short.
    /// Checks layout.Edges directly because node adjacency lists (GroundNode.Edges)
    /// are not populated until after crossing detection completes.
    /// </summary>
    private static bool HasMultipleTaxiwayConnections(int nodeId, AirportGroundLayout layout)
    {
        string? firstTaxiway = null;
        foreach (var edge in layout.Edges)
        {
            if (edge.FromNodeId != nodeId && edge.ToNodeId != nodeId)
            {
                continue;
            }

            if (edge.TaxiwayName.StartsWith("RWY", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (firstTaxiway is null)
            {
                firstTaxiway = edge.TaxiwayName;
            }
            else if (!string.Equals(edge.TaxiwayName, firstTaxiway, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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

        var edgeA = new GroundEdge
        {
            FromNodeId = edge.FromNodeId,
            ToNodeId = midNode.Id,
            TaxiwayName = edge.TaxiwayName,
            DistanceNm = GeoMath.DistanceNm(fromNode.Latitude, fromNode.Longitude, midNode.Latitude, midNode.Longitude),
        };

        var edgeB = new GroundEdge
        {
            FromNodeId = midNode.Id,
            ToNodeId = edge.ToNodeId,
            TaxiwayName = edge.TaxiwayName,
            DistanceNm = GeoMath.DistanceNm(midNode.Latitude, midNode.Longitude, toNode.Latitude, toNode.Longitude),
        };

        layout.Edges.Add(edgeA);
        layout.Edges.Add(edgeB);
        // Node adjacency lists are wired up in GeoJsonParser Step 7.
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
