using Yaat.Sim.Phases;

namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Infers and extends taxiway variant legs (e.g., W → W1) when the explicit
/// path ends short of the destination runway hold-short node.
/// </summary>
internal static class TaxiVariantResolver
{
    /// <summary>
    /// Attempts to auto-extend the last taxiway segment to a numbered variant
    /// (e.g., W → W1) that reaches the destination runway hold-short.
    /// Returns true if segments were extended; sets <paramref name="failReason"/>
    /// when the path is ambiguous and requires user clarification.
    /// </summary>
    internal static bool TryInferVariant(
        AirportGroundLayout layout,
        List<TaxiRouteSegment> segments,
        string lastTaxiwayName,
        int segmentCountBeforeLastTw,
        string destinationRunway,
        NavigationDatabase? navDb,
        string? airportId,
        ref int currentNodeId,
        out string? failReason
    )
    {
        failReason = null;

        // Check if route already reaches a hold-short for the destination runway
        foreach (var seg in segments)
        {
            if (
                layout.Nodes.TryGetValue(seg.ToNodeId, out var segNode)
                && segNode.Type == GroundNodeType.RunwayHoldShort
                && segNode.RunwayId is { } segRwyId
                && TaxiPathfinder.RunwayIdMatches(segRwyId, destinationRunway)
            )
            {
                return false;
            }
        }

        // Find hold-short nodes for the destination runway
        var variants = new List<(GroundNode HsNode, string VariantName)>();
        var nonVariantConnectors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sameNameHoldShorts = new List<GroundNode>();

        foreach (var node in layout.Nodes.Values)
        {
            if (
                node.Type != GroundNodeType.RunwayHoldShort
                || node.RunwayId is not { } nodeRwyId
                || node.Edges.Count == 0
                || !TaxiPathfinder.RunwayIdMatches(nodeRwyId, destinationRunway)
            )
            {
                continue;
            }

            foreach (var edge in node.Edges)
            {
                string edgeName = edge.TaxiwayName;

                if (string.Equals(edgeName, lastTaxiwayName, StringComparison.OrdinalIgnoreCase))
                {
                    // The last taxiway reaches this hold-short, but the walk
                    // may have gone the wrong direction at a fork. Track it so
                    // we can A* extend if needed.
                    sameNameHoldShorts.Add(node);
                    continue;
                }

                if (IsNumberedVariant(edgeName, lastTaxiwayName))
                {
                    variants.Add((node, edgeName));
                }
                else
                {
                    nonVariantConnectors.Add(edgeName);
                }
            }
        }

        if (variants.Count > 0)
        {
            return AutoExtendVariant(
                layout,
                segments,
                lastTaxiwayName,
                segmentCountBeforeLastTw,
                variants,
                navDb,
                airportId,
                destinationRunway,
                ref currentNodeId
            );
        }

        // The walk went the wrong direction at a fork — the last taxiway does
        // reach the destination runway hold-short, but the walker missed it.
        // A* extend from the current endpoint to the nearest reachable hold-short.
        if (sameNameHoldShorts.Count > 0)
        {
            bool extended = ExtendToSameNameHoldShort(layout, segments, sameNameHoldShorts, ref currentNodeId);
            if (extended)
            {
                return true;
            }
        }

        if (nonVariantConnectors.Count > 0)
        {
            var connectors = string.Join(", ", nonVariantConnectors.Order());
            failReason = $"Taxi to runway {destinationRunway}: specify connecting taxiway ({connectors})";
            return false;
        }

        return false;
    }

    /// <summary>
    /// Returns true if <paramref name="candidate"/> is a numbered variant of
    /// <paramref name="baseName"/> (e.g., "W1" is a variant of "W", "W10" too, "WA" is not).
    /// </summary>
    internal static bool IsNumberedVariant(string candidate, string baseName)
    {
        if (candidate.Length <= baseName.Length)
        {
            return false;
        }

        if (!candidate.StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        for (int i = baseName.Length; i < candidate.Length; i++)
        {
            if (!char.IsAsciiDigit(candidate[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AutoExtendVariant(
        AirportGroundLayout layout,
        List<TaxiRouteSegment> segments,
        string lastTaxiwayName,
        int segmentCountBeforeLastTw,
        List<(GroundNode HsNode, string VariantName)> variants,
        NavigationDatabase? navDb,
        string? airportId,
        string destinationRunway,
        ref int currentNodeId
    )
    {
        // Pick variant: if multiple distinct names, choose closest to runway threshold
        string chosenVariant = PickBestVariant(variants, navDb, airportId, destinationRunway);

        // Find branch point: scan nodes along the last-taxiway segments
        int branchNodeId = -1;
        int branchSegmentIndex = -1;

        for (int i = segmentCountBeforeLastTw; i < segments.Count; i++)
        {
            int nodeId = i == segmentCountBeforeLastTw ? segments[i].FromNodeId : segments[i].ToNodeId;

            if (TaxiPathfinder.NodeHasEdgeTo(layout, nodeId, chosenVariant))
            {
                branchNodeId = nodeId;
                branchSegmentIndex = i;
                break;
            }

            // Also check ToNodeId for first segment
            if (i == segmentCountBeforeLastTw)
            {
                if (TaxiPathfinder.NodeHasEdgeTo(layout, segments[i].ToNodeId, chosenVariant))
                {
                    branchNodeId = segments[i].ToNodeId;
                    branchSegmentIndex = i + 1;
                    break;
                }
            }
        }

        // Check remaining ToNodeIds if not found yet
        if (branchNodeId == -1)
        {
            for (int i = segmentCountBeforeLastTw; i < segments.Count; i++)
            {
                if (TaxiPathfinder.NodeHasEdgeTo(layout, segments[i].ToNodeId, chosenVariant))
                {
                    branchNodeId = segments[i].ToNodeId;
                    branchSegmentIndex = i + 1;
                    break;
                }
            }
        }

        if (branchNodeId == -1)
        {
            return false;
        }

        // Truncate segments after the branch point
        if (branchSegmentIndex < segments.Count)
        {
            segments.RemoveRange(branchSegmentIndex, segments.Count - branchSegmentIndex);
        }

        // Walk the variant from the branch point
        bool walked = TaxiPathfinder.WalkTaxiway(layout, branchNodeId, chosenVariant, segments, out int endNodeId);

        if (walked)
        {
            currentNodeId = endNodeId;
        }

        return walked;
    }

    private static string PickBestVariant(
        List<(GroundNode HsNode, string VariantName)> variants,
        NavigationDatabase? navDb,
        string? airportId,
        string destinationRunway
    )
    {
        // Collect distinct variant names
        var distinctNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, name) in variants)
        {
            distinctNames.Add(name);
        }

        if (distinctNames.Count == 1)
        {
            return variants[0].VariantName;
        }

        // Multiple variants: pick closest to runway threshold
        RunwayInfo? rwyInfo = null;
        if (navDb is not null && airportId is not null)
        {
            rwyInfo = navDb.GetRunway(airportId, destinationRunway);
        }

        if (rwyInfo is null)
        {
            return variants[0].VariantName;
        }

        string bestName = variants[0].VariantName;
        double bestDist = double.MaxValue;

        foreach (var (hsNode, name) in variants)
        {
            double dist = GeoMath.DistanceNm(hsNode.Latitude, hsNode.Longitude, rwyInfo.ThresholdLatitude, rwyInfo.ThresholdLongitude);

            if (dist < bestDist)
            {
                bestDist = dist;
                bestName = name;
            }
        }

        return bestName;
    }

    /// <summary>
    /// Extends the route via A* from the current endpoint to the nearest
    /// hold-short node that the last taxiway reaches but the walk missed
    /// (wrong fork at a junction).
    /// </summary>
    private static bool ExtendToSameNameHoldShort(
        AirportGroundLayout layout,
        List<TaxiRouteSegment> segments,
        List<GroundNode> holdShortNodes,
        ref int currentNodeId
    )
    {
        // Try each hold-short, pick the one with the shortest A* route
        TaxiRoute? bestRoute = null;
        int bestHsId = -1;

        foreach (var hs in holdShortNodes)
        {
            var route = TaxiPathfinder.FindRoute(layout, currentNodeId, hs.Id);
            if (route is null)
            {
                continue;
            }

            if (bestRoute is null || route.Segments.Count < bestRoute.Segments.Count)
            {
                bestRoute = route;
                bestHsId = hs.Id;
            }
        }

        if (bestRoute is null)
        {
            return false;
        }

        segments.AddRange(bestRoute.Segments);
        currentNodeId = bestHsId;
        return true;
    }
}
