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

    /// <summary>Maximum hops the junction walker takes while searching for a segment that crosses the ideal hold-short distance.</summary>
    private const int HoldShortWalkMaxHops = 8;

    /// <summary>Backoff distance (ft) from the farthest reachable node when the walker dead-ends before reaching the ideal hold-short distance.</summary>
    private const double HoldShortFallbackBufferFt = 25.0;

    internal static double DetectRunwayCrossings(
        GeoJsonParser.RunwayFeature rwy,
        AirportGroundLayout layout,
        CoordinateIndex coordIndex,
        ref int nextNodeId,
        string? runwayAirportCode
    )
    {
        var combinedId = RunwayIdentifier.Parse(rwy.Name);

        // Look up runway width from navdata; fall back to default
        double widthFt = DefaultRunwayWidthFt;
        if (runwayAirportCode is not null)
        {
            var navDb = NavigationDatabase.Instance;
            var rwyInfo = navDb.GetRunway(runwayAirportCode, combinedId.End1) ?? navDb.GetRunway(runwayAirportCode, combinedId.End2);
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
            if (IsOnRunway(node.Position, rect))
            {
                onRunwayNodes.Add(nodeId);
            }
        }

        // Snapshot edges — we mutate during iteration
        var edgeSnapshot = new List<GroundEdge>(layout.Edges);
        var processed = new HashSet<(int, int)>();

        foreach (var edge in edgeSnapshot)
        {
            if (edge.IsRunwayCenterline)
            {
                continue;
            }

            bool fromOn = onRunwayNodes.Contains(edge.Nodes[0].Id);
            bool toOn = onRunwayNodes.Contains(edge.Nodes[1].Id);

            // Only process boundary edges (one on, one off)
            if (fromOn == toOn)
            {
                continue;
            }

            int onId = fromOn ? edge.Nodes[0].Id : edge.Nodes[1].Id;
            int offId = fromOn ? edge.Nodes[1].Id : edge.Nodes[0].Id;

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
        ConnectOnRunwayNodes(layout, rect, coordIndex, ref nextNodeId);

        return widthFt;
    }

    internal static RunwayRectangle BuildRunwayRectangle(GeoJsonParser.RunwayFeature rwy, double widthFt, RunwayIdentifier combinedId)
    {
        double bearing = GeoMath.BearingTo(rwy.Coords[0].Lat, rwy.Coords[0].Lon, rwy.Coords[^1].Lat, rwy.Coords[^1].Lon);
        double lengthNm = GeoMath.DistanceNm(rwy.Coords[0].Lat, rwy.Coords[0].Lon, rwy.Coords[^1].Lat, rwy.Coords[^1].Lon);
        double halfWidthNm = (widthFt / 2.0) / GeoMath.FeetPerNm;
        double holdShortNm = HoldShortDistanceForWidth(widthFt) / GeoMath.FeetPerNm;

        return new RunwayRectangle
        {
            RefLat = rwy.Coords[0].Lat,
            RefLon = rwy.Coords[0].Lon,
            TrueHeading = new TrueHeading(bearing),
            LengthNm = lengthNm,
            HalfWidthNm = halfWidthNm,
            HoldShortNm = holdShortNm,
            CombinedId = combinedId,
        };
    }

    internal static RunwayRectangle BuildRunwayRectangle(GroundRunway rwy)
    {
        var coords = rwy.Coordinates;
        double bearing = GeoMath.BearingTo(coords[0].Lat, coords[0].Lon, coords[^1].Lat, coords[^1].Lon);
        double lengthNm = GeoMath.DistanceNm(coords[0].Lat, coords[0].Lon, coords[^1].Lat, coords[^1].Lon);
        double halfWidthNm = (rwy.WidthFt / 2.0) / GeoMath.FeetPerNm;
        double holdShortNm = HoldShortDistanceForWidth(rwy.WidthFt) / GeoMath.FeetPerNm;

        return new RunwayRectangle
        {
            RefLat = coords[0].Lat,
            RefLon = coords[0].Lon,
            TrueHeading = new TrueHeading(bearing),
            LengthNm = lengthNm,
            HalfWidthNm = halfWidthNm,
            HoldShortNm = holdShortNm,
            CombinedId = RunwayIdentifier.Parse(rwy.Name),
        };
    }

    internal static bool IsOnRunway(double lat, double lon, in RunwayRectangle rect)
    {
        double crossTrack = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(lat, lon, rect.RefLat, rect.RefLon, rect.TrueHeading));
        double alongTrack = GeoMath.AlongTrackDistanceNm(lat, lon, rect.RefLat, rect.RefLon, rect.TrueHeading);

        return crossTrack <= rect.HalfWidthNm + RunwayTolerance && alongTrack >= -RunwayTolerance && alongTrack <= rect.LengthNm + RunwayTolerance;
    }

    internal static bool IsOnRunway(LatLon position, in RunwayRectangle rect) => IsOnRunway(position.Lat, position.Lon, rect);

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
        double crossOff = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(offNode.Position, new LatLon(rect.RefLat, rect.RefLon), rect.TrueHeading));
        double crossOn = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(onNode.Position, new LatLon(rect.RefLat, rect.RefLon), rect.TrueHeading));

        double distOffToIdeal = Math.Abs(crossOff - rect.HoldShortNm) * GeoMath.FeetPerNm;

        // Don't reuse junction nodes (connected to multiple taxiways) as hold-short —
        // aircraft holding short would block other taxiways. Only reuse simple
        // intermediate nodes that serve a single taxiway.
        bool isJunction = HasMultipleTaxiwayConnections(offNode.Id, layout);

        if (distOffToIdeal <= HoldShortReuseFt && offNode.Type != GroundNodeType.RunwayHoldShort && !isJunction)
        {
            // Existing node is close enough — upgrade it to hold-short in place
            offNode.Type = GroundNodeType.RunwayHoldShort;
            offNode.RunwayId = rect.CombinedId;

            Log.LogDebug("Reused node {NodeId} as hold-short for {Runway} on {Taxiway}", offNode.Id, rect.CombinedId, edge.TaxiwayName);
            return;
        }

        if (crossOff < rect.HoldShortNm)
        {
            // Off-node is still inside the ideal hold-short band. Walk outward
            // along the same taxiway to find a segment that straddles the ideal
            // distance, then interpolate on that segment. Only activates for
            // wide runways where the ideal exceeds typical boundary-edge length.
            FindHoldShortInsertionPoint(layout, coordIndex, onNode, offNode, edge, rect, ref nextNodeId);
            return;
        }

        // Interpolate a new HS node at the correct cross-track distance within
        // the current boundary edge (off-node is beyond the ideal hold-short distance).
        double denom = crossOff - crossOn;
        if (Math.Abs(denom) < 1e-9)
        {
            return;
        }

        double fraction = (rect.HoldShortNm - crossOn) / denom;
        fraction = Math.Clamp(fraction, 0.01, 0.99);

        double hsLat = onNode.Position.Lat + fraction * (offNode.Position.Lat - onNode.Position.Lat);
        double hsLon = onNode.Position.Lon + fraction * (offNode.Position.Lon - onNode.Position.Lon);

        int hsId = nextNodeId++;
        var hsNode = new GroundNode
        {
            Id = hsId,
            Latitude = hsLat,
            Longitude = hsLon,
            Type = GroundNodeType.RunwayHoldShort,
            RunwayId = rect.CombinedId,
            Origin = $"RunwayCrossing:hold-short@{rect.CombinedId}",
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
    private static void ConnectOnRunwayNodes(AirportGroundLayout layout, in RunwayRectangle rect, CoordinateIndex coordIndex, ref int nextNodeId)
    {
        string rwyEdgeName = $"RWY{rect.CombinedId}";

        // Classify all nodes as on/off runway for walk lookups
        var onRunwaySet = new HashSet<int>();
        foreach (var (nid, n) in layout.Nodes)
        {
            if (IsOnRunway(n.Position, rect))
            {
                onRunwaySet.Add(nid);
            }
        }

        // For each HS node, walk from the on-runway neighbor toward the
        // centerline to find the best representative node. Then project that
        // node onto the actual runway centerline and create a new node there.
        // This ensures runway edges follow the straight centerline exactly.
        var centerlineNodes = new List<(int Id, double AlongTrack)>();
        var seen = new HashSet<int>();

        // Track which on-runway nodes we projected so we can connect them
        // to their centerline projection with a short perpendicular edge.

        foreach (var (nodeId, node) in layout.Nodes.ToList())
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

            if (bestId == -1 || !seen.Add(bestId))
            {
                continue;
            }

            var bestNode = layout.Nodes[bestId];
            double alongTrack = GeoMath.AlongTrackDistanceNm(bestNode.Position, new LatLon(rect.RefLat, rect.RefLon), rect.TrueHeading);
            double crossTrack = GeoMath.SignedCrossTrackDistanceNm(bestNode.Position, new LatLon(rect.RefLat, rect.RefLon), rect.TrueHeading);
            double crossTrackFt = Math.Abs(crossTrack) * GeoMath.FeetPerNm;

            if (crossTrackFt < 5.0)
            {
                centerlineNodes.Add((bestId, alongTrack));
            }
            else
            {
                // Create a new node on the actual runway centerline and connect
                // the on-runway node to it. The link uses the runway name with a
                // :link suffix so it's not treated as a centerline edge or a taxiway edge.
                var (clLat, clLon) = GeoMath.ProjectPointRaw(rect.RefLat, rect.RefLon, rect.TrueHeading.Degrees, alongTrack);
                int clId = nextNodeId++;
                var clNode = new GroundNode
                {
                    Id = clId,
                    Latitude = clLat,
                    Longitude = clLon,
                    Type = GroundNodeType.TaxiwayIntersection,
                    Origin = $"RunwayCrossing:centerline-projection@{rect.CombinedId} from #{bestId}",
                };
                layout.Nodes[clId] = clNode;
                coordIndex.Add(clLat, clLon, clId);

                double perpDist = GeoMath.DistanceNm(bestNode.Position, new LatLon(clLat, clLon));
                layout.Edges.Add(
                    new GroundEdge
                    {
                        Nodes = [bestNode, clNode],
                        TaxiwayName = $"RWY{rect.CombinedId}:link",
                        DistanceNm = perpDist,
                        Origin = $"RunwayCrossing:centerline-link@{rect.CombinedId} #{bestId}↔#{clId}",
                    }
                );

                centerlineNodes.Add((clId, alongTrack));

                Log.LogDebug(
                    "Projected #{OnId} onto centerline as #{ClId} ({CrossFt:F0}ft off-center, {AlongFt:F0}ft along)",
                    bestId,
                    clId,
                    crossTrackFt,
                    alongTrack * GeoMath.FeetPerNm
                );
            }
        }

        if (centerlineNodes.Count < 2)
        {
            return;
        }

        centerlineNodes.Sort((a, b) => a.AlongTrack.CompareTo(b.AlongTrack));

        for (int i = 0; i < centerlineNodes.Count - 1; i++)
        {
            int fromId = centerlineNodes[i].Id;
            int toId = centerlineNodes[i + 1].Id;

            var from = layout.Nodes[fromId];
            var to = layout.Nodes[toId];
            double dist = GeoMath.DistanceNm(from.Position, to.Position);

            layout.Edges.Add(
                new GroundEdge
                {
                    Nodes = [from, to],
                    TaxiwayName = rwyEdgeName,
                    DistanceNm = dist,
                    Origin = $"RunwayCrossing:rwy-edge@{rect.CombinedId}",
                }
            );

            Log.LogDebug("Runway centerline edge: {From}->{To} on {Runway} ({DistFt:F0}ft)", fromId, toId, rect.CombinedId, dist * GeoMath.FeetPerNm);
        }
    }

    /// <summary>
    /// Hold-short distance from runway centerline (ft) per FAA AC 150/5300-13B
    /// Table 3-2. Runway width is used as a proxy for Aircraft Design Group (ADG),
    /// which determines the published hold position setback. Values approximate
    /// the CAT I/II/III ILS rows of the table and do not apply the small
    /// elevation correction (+1 ft per 100 ft MSL) since most US airports sit
    /// below a few hundred feet.
    /// </summary>
    internal static double HoldShortDistanceForWidth(double runwayWidthFt)
    {
        if (runwayWidthFt < 75.0)
        {
            return 125.0; // ADG I/II small GA
        }

        if (runwayWidthFt < 100.0)
        {
            return 150.0; // ADG II/III regional
        }

        if (runwayWidthFt < 150.0)
        {
            return 200.0; // ADG III narrow-body
        }

        if (runwayWidthFt < 200.0)
        {
            return 250.0; // ADG IV/V
        }

        return 280.0; // ADG V/VI wide body / CAT III
    }

    /// <summary>
    /// Starting from an HS node, walks along same-taxiway edges (through
    /// shape-point nodes, which may be off-runway) toward the runway centerline.
    /// Returns the node with the smallest cross-track distance to the centerline.
    /// </summary>
    private static int FindCenterlineNode(int hsNodeId, AirportGroundLayout layout, in RunwayRectangle rect, HashSet<int> onRunwaySet)
    {
        // Find which taxiway this hold-short is on
        string? hsTaxiway = null;
        foreach (var edge in layout.Edges)
        {
            if (edge.HasNode(hsNodeId) && !edge.IsRunwayCenterline)
            {
                hsTaxiway = edge.TaxiwayName;
                break;
            }
        }

        if (hsTaxiway is null)
        {
            Log.LogDebug("  FindCenterline(HS#{Hs}): no taxiway edge found", hsNodeId);
            return -1;
        }

        // BFS walk along same-taxiway edges toward the centerline.
        // Walk through all nodes (including off-runway shape-points) but
        // only consider on-runway nodes as centerline candidates.
        int bestId = -1;
        double bestCrossTrack = double.MaxValue;
        var visited = new HashSet<int> { hsNodeId };

        var queue = new Queue<int>();
        queue.Enqueue(hsNodeId);

        while (queue.Count > 0)
        {
            int current = queue.Dequeue();

            foreach (var edge in layout.Edges)
            {
                if (edge.IsRunwayCenterline)
                {
                    continue;
                }

                if (!edge.HasNode(current))
                {
                    continue;
                }

                // Follow same-taxiway edges to stay on this taxiway's path
                if (edge.TaxiwayName != hsTaxiway)
                {
                    continue;
                }

                int nextId = edge.OtherNodeId(current);

                if (!visited.Add(nextId))
                {
                    continue;
                }

                if (!layout.Nodes.TryGetValue(nextId, out var nextNode))
                {
                    continue;
                }

                double ct = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(nextNode.Position, new LatLon(rect.RefLat, rect.RefLon), rect.TrueHeading));

                Log.LogDebug(
                    "  FindCenterline(HS#{Hs}): walked to #{Next} via {Tw} crossTrack={Ct:F0}ft onRunway={On} (best: #{Best} at {BestCt:F0}ft)",
                    hsNodeId,
                    nextId,
                    edge.TaxiwayName,
                    ct * GeoMath.FeetPerNm,
                    onRunwaySet.Contains(nextId),
                    bestId,
                    bestCrossTrack * GeoMath.FeetPerNm
                );

                // Only on-runway nodes are candidates for the centerline representative
                if (onRunwaySet.Contains(nextId) && (ct < bestCrossTrack))
                {
                    bestCrossTrack = ct;
                    bestId = nextId;
                }

                // Keep walking — the next node might be closer to centerline
                // Stop when we've crossed the centerline and are moving away
                // (cross-track increasing past the best), or hit a non-taxiway node
                if ((bestId != -1) && (ct > bestCrossTrack * 2))
                {
                    continue; // Don't enqueue — too far past centerline
                }

                queue.Enqueue(nextId);
            }
        }

        return bestId;
    }

    /// <summary>
    /// Walks outward from the off-node along the same taxiway until a segment
    /// straddles the ideal hold-short distance, then interpolates and inserts
    /// a hold-short node there. Used when the boundary edge's off-node is
    /// still inside the FAA hold-short band — common at wide CAT III runways
    /// where the ideal distance is 250-280 ft from centerline.
    /// </summary>
    private static void FindHoldShortInsertionPoint(
        AirportGroundLayout layout,
        CoordinateIndex coordIndex,
        GroundNode startOnNode,
        GroundNode startOffNode,
        GroundEdge boundaryEdge,
        in RunwayRectangle rect,
        ref int nextNodeId
    )
    {
        string taxiwayName = boundaryEdge.TaxiwayName;
        var prevNode = startOnNode;
        var currentNode = startOffNode;
        var lastEdge = boundaryEdge;
        var visited = new HashSet<int> { startOnNode.Id, startOffNode.Id };

        for (int hop = 0; hop < HoldShortWalkMaxHops; hop++)
        {
            double currentCrossNm = Math.Abs(
                GeoMath.SignedCrossTrackDistanceNm(currentNode.Position, new LatLon(rect.RefLat, rect.RefLon), rect.TrueHeading)
            );

            if (currentCrossNm >= rect.HoldShortNm)
            {
                // lastEdge straddles the ideal band. Interpolate on it.
                InterpolateAndInsert(layout, coordIndex, prevNode, currentNode, lastEdge, rect, ref nextNodeId);
                return;
            }

            // Pick the next edge: same taxiway, non-RWY, not visited, prefer straight continuation.
            double incomingBearing = GeoMath.BearingTo(prevNode.Position, currentNode.Position);

            GroundEdge? bestEdge = null;
            GroundNode? bestFar = null;
            double bestTurnDelta = double.MaxValue;
            double bestFarCross = -1.0;

            foreach (var candidate in layout.Edges)
            {
                if (candidate.IsRunwayCenterline)
                {
                    continue;
                }

                if (!candidate.MatchesTaxiway(taxiwayName))
                {
                    continue;
                }

                if (!candidate.HasNode(currentNode.Id))
                {
                    continue;
                }

                int farId = candidate.OtherNodeId(currentNode.Id);
                if (visited.Contains(farId))
                {
                    continue;
                }

                if (!layout.Nodes.TryGetValue(farId, out var farNode))
                {
                    continue;
                }

                double outBearing = GeoMath.BearingTo(currentNode.Position, farNode.Position);
                double turnDelta = Math.Abs(NormalizeBearingDelta(outBearing - incomingBearing));
                double farCross = Math.Abs(
                    GeoMath.SignedCrossTrackDistanceNm(farNode.Position, new LatLon(rect.RefLat, rect.RefLon), rect.TrueHeading)
                );

                // Prefer straightest continuation; break ties by larger cross-track (more outward).
                if (turnDelta < bestTurnDelta - 0.5 || (Math.Abs(turnDelta - bestTurnDelta) <= 0.5 && farCross > bestFarCross))
                {
                    bestTurnDelta = turnDelta;
                    bestFarCross = farCross;
                    bestEdge = candidate;
                    bestFar = farNode;
                }
            }

            if (bestEdge is null || bestFar is null)
            {
                // Dead end: no same-taxiway continuation.
                DeadEndFallback(layout, coordIndex, prevNode, currentNode, lastEdge, rect, taxiwayName, ref nextNodeId);
                return;
            }

            prevNode = currentNode;
            currentNode = bestFar;
            lastEdge = bestEdge;
            visited.Add(bestFar.Id);
        }

        // Exhausted hop budget — fall back at the last reached node.
        DeadEndFallback(layout, coordIndex, prevNode, currentNode, lastEdge, rect, taxiwayName, ref nextNodeId);
    }

    /// <summary>
    /// Interpolates a hold-short node on <paramref name="edge"/> between
    /// <paramref name="nearNode"/> and <paramref name="farNode"/> at the ideal
    /// cross-track distance. If the ideal lies within <see cref="HoldShortReuseFt"/>
    /// of either endpoint, upgrades that endpoint in place instead of creating a
    /// near-coincident HS that would collapse under later fillet coincident-node merge.
    /// </summary>
    private static void InterpolateAndInsert(
        AirportGroundLayout layout,
        CoordinateIndex coordIndex,
        GroundNode nearNode,
        GroundNode farNode,
        GroundEdge edge,
        in RunwayRectangle rect,
        ref int nextNodeId
    )
    {
        double crossNearFt =
            Math.Abs(GeoMath.SignedCrossTrackDistanceNm(nearNode.Position, new LatLon(rect.RefLat, rect.RefLon), rect.TrueHeading))
            * GeoMath.FeetPerNm;
        double crossFarFt =
            Math.Abs(GeoMath.SignedCrossTrackDistanceNm(farNode.Position, new LatLon(rect.RefLat, rect.RefLon), rect.TrueHeading))
            * GeoMath.FeetPerNm;
        double idealFt = rect.HoldShortNm * GeoMath.FeetPerNm;

        // Prefer upgrading the nearest eligible endpoint when it sits within
        // reuse tolerance of ideal. This avoids the "1 ft interpolation inside
        // a fillet tangent stub" case where fillet coincident-node merge would
        // collapse the split edge anyway.
        double distNearToIdeal = Math.Abs(crossNearFt - idealFt);
        double distFarToIdeal = Math.Abs(crossFarFt - idealFt);

        if (distNearToIdeal <= HoldShortReuseFt && TryUpgradeToHoldShort(nearNode, rect, layout))
        {
            return;
        }

        if (distFarToIdeal <= HoldShortReuseFt && TryUpgradeToHoldShort(farNode, rect, layout))
        {
            return;
        }

        double denom = crossFarFt - crossNearFt;
        if (Math.Abs(denom) < 1e-9)
        {
            return;
        }

        double fraction = Math.Clamp((idealFt - crossNearFt) / denom, 0.01, 0.99);
        double hsLat = nearNode.Position.Lat + fraction * (farNode.Position.Lat - nearNode.Position.Lat);
        double hsLon = nearNode.Position.Lon + fraction * (farNode.Position.Lon - nearNode.Position.Lon);

        int hsId = nextNodeId++;
        var hsNode = new GroundNode
        {
            Id = hsId,
            Latitude = hsLat,
            Longitude = hsLon,
            Type = GroundNodeType.RunwayHoldShort,
            RunwayId = rect.CombinedId,
            Origin = $"RunwayCrossing:hold-short@{rect.CombinedId}",
        };
        layout.Nodes[hsId] = hsNode;
        coordIndex.Add(hsLat, hsLon, hsId);

        SplitEdgeAtOneNode(layout, edge, hsNode);

        Log.LogDebug(
            "Runway crossing (walker): {Taxiway} boundary at {Runway} — hold-short node {NodeId} at ({Lat:F6}, {Lon:F6})",
            edge.TaxiwayName,
            rect.CombinedId,
            hsId,
            hsLat,
            hsLon
        );
    }

    /// <summary>
    /// Upgrades an existing non-junction, non-HS node to a RunwayHoldShort for
    /// the given runway. Returns false if the node is already HS or is a junction
    /// between multiple taxiways (upgrading would block other taxi paths).
    /// </summary>
    private static bool TryUpgradeToHoldShort(GroundNode node, in RunwayRectangle rect, AirportGroundLayout layout)
    {
        if (node.Type == GroundNodeType.RunwayHoldShort)
        {
            return true; // already HS for this or another runway — nothing to do
        }

        if (HasMultipleTaxiwayConnections(node.Id, layout))
        {
            return false;
        }

        node.Type = GroundNodeType.RunwayHoldShort;
        node.RunwayId = rect.CombinedId;
        Log.LogDebug("Upgraded node {NodeId} to hold-short for {Runway}", node.Id, rect.CombinedId);
        return true;
    }

    /// <summary>
    /// Dead-end fallback: the walker couldn't reach the ideal hold-short distance.
    /// Prefer upgrading the farthest reached node (currentNode) in place. If that
    /// node is a junction, try upgrading prevNode. Only split and interpolate as a
    /// last resort.
    /// </summary>
    private static void DeadEndFallback(
        AirportGroundLayout layout,
        CoordinateIndex coordIndex,
        GroundNode prevNode,
        GroundNode currentNode,
        GroundEdge lastEdge,
        in RunwayRectangle rect,
        string taxiwayName,
        ref int nextNodeId
    )
    {
        double idealFt = rect.HoldShortNm * GeoMath.FeetPerNm;
        double currentCrossFt =
            Math.Abs(GeoMath.SignedCrossTrackDistanceNm(currentNode.Position, new LatLon(rect.RefLat, rect.RefLon), rect.TrueHeading))
            * GeoMath.FeetPerNm;

        // Best outcome: upgrade the farthest reached node (it's closest to ideal)
        if (TryUpgradeToHoldShort(currentNode, rect, layout))
        {
            Log.LogDebug(
                "Hold-short dead-end: upgraded {Node} for {Runway} on {Taxiway} (ideal={Ideal:F0}ft, actual={Actual:F0}ft)",
                currentNode.Id,
                rect.CombinedId,
                taxiwayName,
                idealFt,
                currentCrossFt
            );
            return;
        }

        // Second choice: upgrade prevNode if it's within reuse tolerance of ideal
        double prevCrossFt =
            Math.Abs(GeoMath.SignedCrossTrackDistanceNm(prevNode.Position, new LatLon(rect.RefLat, rect.RefLon), rect.TrueHeading))
            * GeoMath.FeetPerNm;
        if (Math.Abs(prevCrossFt - idealFt) <= HoldShortReuseFt && TryUpgradeToHoldShort(prevNode, rect, layout))
        {
            Log.LogDebug(
                "Hold-short dead-end: upgraded {Node} (prev) for {Runway} on {Taxiway} (ideal={Ideal:F0}ft, actual={Actual:F0}ft)",
                prevNode.Id,
                rect.CombinedId,
                taxiwayName,
                idealFt,
                prevCrossFt
            );
            return;
        }

        // Last resort: interpolate 25 ft before the dead-end on the last edge.
        // But skip if either endpoint is a junction — splitting a junction-to-junction
        // edge creates near-coincident nodes that get collapsed by fillet merge.
        bool nearIsJunction = HasMultipleTaxiwayConnections(prevNode.Id, layout);
        bool farIsJunction = HasMultipleTaxiwayConnections(currentNode.Id, layout);
        double edgeLengthFt = GeoMath.DistanceNm(prevNode.Position, currentNode.Position) * GeoMath.FeetPerNm;
        if (nearIsJunction && farIsJunction)
        {
            Log.LogWarning(
                "Hold-short dead-end on {Taxiway} for {Runway}: both endpoints are junctions and edge is too short ({Len:F0}ft), skipping",
                taxiwayName,
                rect.CombinedId,
                edgeLengthFt
            );
            return;
        }

        double backoffFraction = Math.Clamp(1.0 - (HoldShortFallbackBufferFt / edgeLengthFt), 0.01, 0.99);
        double hsLat = prevNode.Position.Lat + backoffFraction * (currentNode.Position.Lat - prevNode.Position.Lat);
        double hsLon = prevNode.Position.Lon + backoffFraction * (currentNode.Position.Lon - prevNode.Position.Lon);

        int hsId = nextNodeId++;
        var hsNode = new GroundNode
        {
            Id = hsId,
            Latitude = hsLat,
            Longitude = hsLon,
            Type = GroundNodeType.RunwayHoldShort,
            RunwayId = rect.CombinedId,
            Origin = $"RunwayCrossing:hold-short@{rect.CombinedId}",
        };
        layout.Nodes[hsId] = hsNode;
        coordIndex.Add(hsLat, hsLon, hsId);

        SplitEdgeAtOneNode(layout, lastEdge, hsNode);

        Log.LogWarning(
            "Hold-short junction clamp on {Taxiway} for {Runway}: ideal={Ideal:F0}ft reached={Reached:F0}ft, placing at dead-end backoff",
            taxiwayName,
            rect.CombinedId,
            idealFt,
            currentCrossFt
        );
    }

    /// <summary>Normalizes a bearing delta in degrees to the range (-180, 180].</summary>
    private static double NormalizeBearingDelta(double deltaDeg)
    {
        double d = deltaDeg % 360.0;
        if (d > 180.0)
        {
            d -= 360.0;
        }
        else if (d <= -180.0)
        {
            d += 360.0;
        }

        return d;
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
            if (!edge.HasNode(nodeId))
            {
                continue;
            }

            if (edge.IsRunwayCenterline)
            {
                continue;
            }

            if (firstTaxiway is null)
            {
                firstTaxiway = edge.TaxiwayName;
            }
            else if (!edge.MatchesTaxiway(firstTaxiway))
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
    internal static void SplitEdgeAtOneNode(AirportGroundLayout layout, GroundEdge edge, GroundNode midNode)
    {
        layout.Edges.Remove(edge);

        var nodeA = edge.Nodes[0];
        var nodeB = edge.Nodes[1];

        var edgeA = new GroundEdge
        {
            Nodes = [nodeA, midNode],
            TaxiwayName = edge.TaxiwayName,
            DistanceNm = GeoMath.DistanceNm(nodeA.Position, midNode.Position),
            Origin = $"RunwayCrossing:split-edge({edge.Origin ?? edge.TaxiwayName})",
        };

        var edgeB = new GroundEdge
        {
            Nodes = [midNode, nodeB],
            TaxiwayName = edge.TaxiwayName,
            DistanceNm = GeoMath.DistanceNm(midNode.Position, nodeB.Position),
            Origin = $"RunwayCrossing:split-edge({edge.Origin ?? edge.TaxiwayName})",
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
    public required TrueHeading TrueHeading { get; init; }
    public required double LengthNm { get; init; }
    public required double HalfWidthNm { get; init; }
    public required double HoldShortNm { get; init; }
    public required RunwayIdentifier CombinedId { get; init; }
}
