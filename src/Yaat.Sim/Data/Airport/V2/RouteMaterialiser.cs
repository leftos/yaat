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
                // Precedence: DestinationRunway (taxiing TO this runway — hold for departure, never
                // auto-cross) > ExplicitHoldShort (controller named it in the HS list) > RunwayCrossing.
                // Reciprocal matching: a node's RunwayId is the combined "28R/10L"; clearances name a
                // single end ("28R"), so match via RunwayIdentifier.Contains, not literal string equality.
                HoldShortReason reason;
                string targetName = runwayId.ToString();

                if (ctx.Destination.Kind == DestinationKind.Runway && ctx.Destination.RunwayId is { } destRwy && runwayId.Contains(destRwy))
                {
                    reason = HoldShortReason.DestinationRunway;
                    targetName = destRwy;
                }
                else if (MatchesExplicitHoldShort(runwayId, ctx.ExplicitHoldShorts))
                {
                    reason = HoldShortReason.ExplicitHoldShort;
                }
                else
                {
                    reason = HoldShortReason.RunwayCrossing;
                }

                holdShorts.Add(
                    new HoldShortPoint
                    {
                        NodeId = nodeId,
                        Reason = reason,
                        TargetName = targetName,
                    }
                );

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

    /// <summary>
    /// True when any controller-named explicit hold-short designator matches this runway,
    /// reciprocal-aware (a "28R" hold matches a node whose RunwayId is "28R/10L").
    /// </summary>
    private static bool MatchesExplicitHoldShort(RunwayIdentifier runwayId, IReadOnlySet<string> explicitHoldShorts)
    {
        foreach (string designator in explicitHoldShorts)
        {
            if (runwayId.Contains(designator))
            {
                return true;
            }
        }

        return false;
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
    /// geographically nearest to the requested designator's threshold (the departure end you line
    /// up at for a full-length takeoff). The threshold is resolved authoritatively from
    /// <see cref="NavigationDatabase"/> so the correct end is chosen for the named designator
    /// (the 28R end, not the reciprocal 10L end). When the runway is unknown to nav-data, falls
    /// back to the hold-short nearest <paramref name="startNode"/>.
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

        LatLon reference = ResolveRunwayThreshold(layout.AirportId, runwayId) ?? startNode.Position;

        GroundNode best = holdShortNodes[0];
        double bestDist = GeoMath.DistanceNm(reference, best.Position);
        for (int i = 1; i < holdShortNodes.Count; i++)
        {
            double dist = GeoMath.DistanceNm(reference, holdShortNodes[i].Position);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = holdShortNodes[i];
            }
        }

        return best;
    }

    /// <summary>
    /// The geographic threshold of <paramref name="runwayId"/>'s requested designator (e.g. the 28R
    /// end, not the reciprocal 10L end), from <see cref="NavigationDatabase"/>. Null when nav-data is
    /// uninitialized or the runway is unknown to it.
    /// </summary>
    internal static LatLon? ResolveRunwayThreshold(string airportId, string runwayId)
    {
        var runway = NavigationDatabase.InstanceOrNull?.GetRunway(airportId, runwayId);
        return runway is null ? null : new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
    }
}
