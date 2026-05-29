namespace Yaat.Sim.Data.Airport.V2;

/// <summary>
/// Phase 3: converts a flat list of directed edges plus search context into a <see cref="TaxiRoute"/>.
/// Annotates hold-short points, truncates trailing pavement, sets destination strings, and emits warnings.
/// </summary>
public static class RouteMaterialiser
{
    /// <summary>
    /// Produce a <see cref="TaxiRoute"/> from a committed edge sequence and search context.
    /// <paramref name="insertions"/> are mandatory connectors the resolver had to bridge between
    /// cleared taxiways with no direct junction — surfaced as informative notifications rather
    /// than "unauthorized taxiway" warnings. Auto-route callers (no named clearance) pass empty.
    /// </summary>
    public static TaxiRoute Materialise(IReadOnlyList<DirectionalEdge> edges, SearchContext ctx, IReadOnlyList<ConnectorInsertion> insertions)
    {
        if (edges.Count == 0)
        {
            return new TaxiRoute
            {
                Segments = [],
                HoldShortPoints = [],
                CurrentSegmentIndex = 0,
                DestinationParking = ctx.Destination.ParkingName,
                DestinationSpot = ctx.Destination.SpotName,
            };
        }

        // Step 1: Build segment list — one per directed edge.
        var segments = BuildSegments(edges);

        // Step 2: Annotate hold-short points.
        var holdShorts = AnnotateHoldShorts(segments, ctx);

        // Step 3: Truncate to one segment past the last required stop.
        int truncateAt = FindTruncationIndex(segments, holdShorts, ctx);
        if (truncateAt >= 0 && truncateAt < segments.Count - 1)
        {
            segments = segments.Take(truncateAt + 1).ToList();
            holdShorts = holdShorts.Where(hs => segments.Any(s => s.ToNodeId == hs.NodeId)).ToList();
        }

        // Step 4: Warnings for unauthorized letter taxiways traversed, plus informative
        // notifications for mandatory connector insertions.
        var warnings = BuildWarnings(segments, ctx, insertions);

        return new TaxiRoute
        {
            Segments = segments,
            HoldShortPoints = holdShorts,
            Warnings = warnings,
            CurrentSegmentIndex = 0,
            DestinationParking = ctx.Destination.ParkingName,
            DestinationSpot = ctx.Destination.SpotName,
        };
    }

    private static List<TaxiRouteSegment> BuildSegments(IReadOnlyList<DirectionalEdge> edges)
    {
        var segments = new List<TaxiRouteSegment>(edges.Count);
        foreach (var edge in edges)
        {
            string taxiwayName = edge.TaxiwayName;
            segments.Add(new TaxiRouteSegment { Edge = edge, TaxiwayName = taxiwayName });
        }

        return segments;
    }

    private static List<HoldShortPoint> AnnotateHoldShorts(List<TaxiRouteSegment> segments, SearchContext ctx)
    {
        var holdShorts = new List<HoldShortPoint>();
        var seen = new HashSet<int>();

        foreach (var seg in segments)
        {
            int nodeId = seg.ToNodeId;
            if (!seen.Add(nodeId))
            {
                continue;
            }

            if (!ctx.Layout.Nodes.TryGetValue(nodeId, out var node))
            {
                continue;
            }

            if (node.Type == GroundNodeType.RunwayHoldShort && node.RunwayId is { } runwayId)
            {
                string runwayIdStr = runwayId.ToString();
                HoldShortReason reason = ctx.ExplicitHoldShorts.Contains(runwayIdStr)
                    ? HoldShortReason.ExplicitHoldShort
                    : HoldShortReason.RunwayCrossing;

                // Handle "28L/28R" style multi-runway hold-shorts.
                string[] parts = runwayIdStr.Split('/', StringSplitOptions.RemoveEmptyEntries);
                bool addedAny = false;
                foreach (string part in parts)
                {
                    if (!addedAny)
                    {
                        holdShorts.Add(
                            new HoldShortPoint
                            {
                                NodeId = nodeId,
                                Reason = reason,
                                TargetName = runwayIdStr,
                            }
                        );
                        addedAny = true;
                    }
                }

                continue;
            }

            // Check explicit hold-shorts that target a taxiway name (non-runway).
            foreach (string holdShortTarget in ctx.ExplicitHoldShorts)
            {
                foreach (var edge in node.Edges)
                {
                    if (edge.MatchesTaxiway(holdShortTarget))
                    {
                        holdShorts.Add(
                            new HoldShortPoint
                            {
                                NodeId = nodeId,
                                Reason = HoldShortReason.ExplicitHoldShort,
                                TargetName = holdShortTarget,
                            }
                        );
                        break;
                    }
                }
            }
        }

        return holdShorts;
    }

    private static int FindTruncationIndex(List<TaxiRouteSegment> segments, List<HoldShortPoint> holdShorts, SearchContext ctx)
    {
        // Find last required stop: destination node or last explicit hold-short.
        int lastRequiredIdx = -1;

        // Check destination node.
        if (ctx.Destination.TargetNodeId is { } destNodeId)
        {
            for (int i = 0; i < segments.Count; i++)
            {
                if (segments[i].ToNodeId == destNodeId)
                {
                    lastRequiredIdx = Math.Max(lastRequiredIdx, i);
                }
            }
        }

        // Check runway destination: stop at the first hold-short on the destination runway.
        if (ctx.Destination.RunwayId is { } runwayId && ctx.Destination.Kind == DestinationKind.Runway)
        {
            for (int i = 0; i < segments.Count; i++)
            {
                if (!ctx.Layout.Nodes.TryGetValue(segments[i].ToNodeId, out var node))
                {
                    continue;
                }

                if (
                    node.Type == GroundNodeType.RunwayHoldShort
                    && node.RunwayId is { } nodeRunwayId
                    && (nodeRunwayId.ToString() ?? string.Empty).Contains(runwayId, StringComparison.OrdinalIgnoreCase)
                )
                {
                    lastRequiredIdx = Math.Max(lastRequiredIdx, i);
                    break;
                }
            }
        }

        // Explicit hold-shorts: include one past the last one.
        foreach (var hs in holdShorts)
        {
            if (hs.Reason == HoldShortReason.ExplicitHoldShort)
            {
                for (int i = 0; i < segments.Count; i++)
                {
                    if (segments[i].ToNodeId == hs.NodeId)
                    {
                        lastRequiredIdx = Math.Max(lastRequiredIdx, i);
                    }
                }
            }
        }

        // Parking/spot: stop at the destination node.
        if (
            ctx.Destination.Kind is DestinationKind.Parking or DestinationKind.Spot or DestinationKind.Helipad
            && ctx.Destination.TargetNodeId is { } parkingNodeId
        )
        {
            for (int i = 0; i < segments.Count; i++)
            {
                if (segments[i].ToNodeId == parkingNodeId)
                {
                    lastRequiredIdx = Math.Max(lastRequiredIdx, i);
                }
            }
        }

        // One past the last required stop.
        return lastRequiredIdx < 0 ? segments.Count - 1 : Math.Min(lastRequiredIdx + 1, segments.Count - 1);
    }

    private static List<string> BuildWarnings(List<TaxiRouteSegment> segments, SearchContext ctx, IReadOnlyList<ConnectorInsertion> insertions)
    {
        var warnings = new List<string>();

        // Mandatory connectors the resolver had to bridge between cleared taxiways with no direct
        // junction. Notify the controller of each insertion, and suppress the generic
        // "unauthorized" warning for those connector taxiways — they were not a deviation.
        var mandatoryConnectors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var insertion in insertions)
        {
            warnings.Add(
                $"{insertion.FromTaxiway} and {insertion.ToTaxiway} do not connect directly — taxi via {string.Join(", ", insertion.Connectors)}"
            );
            foreach (string connector in insertion.Connectors)
            {
                mandatoryConnectors.Add(connector);
            }
        }

        if (ctx.AuthorizedTaxiways is null)
        {
            return warnings;
        }

        // Bounds of the non-RAMP traversal. RAMP segments before the first / after the last are
        // the parking bridge and parking arrival — not a deviation, so they are never flagged.
        int firstNonRamp = -1;
        int lastNonRamp = -1;
        for (int i = 0; i < segments.Count; i++)
        {
            if (!segments[i].TaxiwayName.Equals("RAMP", StringComparison.OrdinalIgnoreCase))
            {
                firstNonRamp = firstNonRamp < 0 ? i : firstNonRamp;
                lastNonRamp = i;
            }
        }

        var warned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];

            // Junction arcs ("X - Y") are transitions between taxiways, not a traversal of one —
            // never an "unauthorized taxiway" deviation.
            if (seg.Edge.Edge is GroundArc { TaxiwayNames.Length: >= 2 })
            {
                continue;
            }

            string name = seg.TaxiwayName;

            // RAMP forming the leading parking bridge or trailing parking arrival is expected.
            if (name.Equals("RAMP", StringComparison.OrdinalIgnoreCase) && (firstNonRamp < 0 || i < firstNonRamp || i > lastNonRamp))
            {
                continue;
            }

            if (
                SearchContext.IsLetterOnlyTaxiway(name)
                && !ctx.AuthorizedTaxiways.Contains(name)
                && !mandatoryConnectors.Contains(name)
                && warned.Add(name)
            )
            {
                warnings.Add($"Taxiing via {name} (not in authorized path)");
            }
        }

        return warnings;
    }

    /// <summary>
    /// Pick the full-length lineup hold-short on <paramref name="runwayId"/> — the one
    /// geographically nearest to the runway threshold (departure end).
    /// Falls back to the hold-short nearest <paramref name="startNode"/> when the
    /// runway is unknown to the layout.
    /// </summary>
    public static GroundNode FindFullLengthLineupHoldShort(
        AirportGroundLayout layout,
        GroundNode startNode,
        string runwayId,
        List<GroundNode> holdShortNodes
    )
    {
        if (holdShortNodes.Count == 0)
        {
            return startNode;
        }

        if (holdShortNodes.Count == 1)
        {
            return holdShortNodes[0];
        }

        // Find the runway's threshold position to determine which hold-short is at the full-length end.
        GroundNode? thresholdProxy = FindRunwayThresholdProxy(layout, runwayId);

        if (thresholdProxy is not null)
        {
            // Pick the hold-short nearest to the runway threshold.
            GroundNode best = holdShortNodes[0];
            double bestDist = GeoMath.DistanceNm(thresholdProxy.Position, best.Position);

            for (int i = 1; i < holdShortNodes.Count; i++)
            {
                double dist = GeoMath.DistanceNm(thresholdProxy.Position, holdShortNodes[i].Position);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = holdShortNodes[i];
                }
            }

            return best;
        }

        // Fallback: pick the hold-short nearest the start node.
        GroundNode fallback = holdShortNodes[0];
        double fallbackDist = GeoMath.DistanceNm(startNode.Position, fallback.Position);

        for (int i = 1; i < holdShortNodes.Count; i++)
        {
            double dist = GeoMath.DistanceNm(startNode.Position, holdShortNodes[i].Position);
            if (dist < fallbackDist)
            {
                fallbackDist = dist;
                fallback = holdShortNodes[i];
            }
        }

        return fallback;
    }

    private static GroundNode? FindRunwayThresholdProxy(AirportGroundLayout layout, string runwayId)
    {
        // Use the farthest runway-centerline node on the named runway as the threshold proxy.
        GroundNode? thresholdProxy = null;
        double maxDistFromCentroid = -1.0;

        double centroidLat = 0.0;
        double centroidLon = 0.0;
        int count = 0;

        foreach (var edge in layout.AllEdges)
        {
            if (edge.MatchesRunway(runwayId))
            {
                foreach (var node in edge.Nodes)
                {
                    centroidLat += node.Position.Lat;
                    centroidLon += node.Position.Lon;
                    count++;
                }
            }
        }

        if (count == 0)
        {
            return null;
        }

        var centroid = new LatLon(centroidLat / count, centroidLon / count);

        foreach (var edge in layout.AllEdges)
        {
            if (edge.MatchesRunway(runwayId))
            {
                foreach (var node in edge.Nodes)
                {
                    double d = GeoMath.DistanceNm(centroid, node.Position);
                    if (d > maxDistFromCentroid)
                    {
                        maxDistFromCentroid = d;
                        thresholdProxy = node;
                    }
                }
            }
        }

        return thresholdProxy;
    }
}
