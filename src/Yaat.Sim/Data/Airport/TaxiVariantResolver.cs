using Microsoft.Extensions.Logging;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Infers and extends taxiway variant legs (e.g., W → W1) when the explicit
/// path ends short of the destination runway hold-short node.
/// </summary>
internal static class TaxiVariantResolver
{
    private static readonly ILogger Log = SimLog.CreateLogger("TaxiVariantResolver");

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
        string? airportId,
        ref int currentNodeId,
        out string? failReason
    )
    {
        failReason = null;
        Log.LogDebug(
            "[Variant] TryInferVariant: lastTw={LastTw} destRwy={DestRwy} segments={SegCount} segCountBeforeLastTw={Before}",
            lastTaxiwayName,
            destinationRunway,
            segments.Count,
            segmentCountBeforeLastTw
        );

        // Check if route already reaches a hold-short for the destination runway
        foreach (var seg in segments)
        {
            if (
                layout.Nodes.TryGetValue(seg.ToNodeId, out var segNode)
                && segNode.Type == GroundNodeType.RunwayHoldShort
                && segNode.RunwayId is { } segRwyId
                && segRwyId.Contains(destinationRunway)
            )
            {
                Log.LogDebug("[Variant] Route already reaches hold-short #{NodeId} for {Rwy}", seg.ToNodeId, destinationRunway);
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
                || !nodeRwyId.Contains(destinationRunway)
            )
            {
                continue;
            }

            foreach (var edge in node.Edges)
            {
                string edgeName = edge.TaxiwayName;

                if (string.Equals(edgeName, lastTaxiwayName, StringComparison.OrdinalIgnoreCase))
                {
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

        Log.LogDebug(
            "[Variant] Found {VarCount} variants, {SameCount} same-name hold-shorts, {NonVarCount} non-variant connectors",
            variants.Count,
            sameNameHoldShorts.Count,
            nonVariantConnectors.Count
        );
        foreach (var (hsNode, varName) in variants)
        {
            Log.LogDebug("[Variant]   variant: #{NodeId} via {Name}", hsNode.Id, varName);
        }

        if (variants.Count > 0)
        {
            bool result = AutoExtendVariant(layout, segments, segmentCountBeforeLastTw, variants, airportId, destinationRunway, ref currentNodeId);
            Log.LogDebug("[Variant] AutoExtendVariant returned {Result}", result);
            return result;
        }

        // The walk went the wrong direction at a fork — the last taxiway does
        // reach the destination runway hold-short, but the walker missed it.
        // A* extend from the current endpoint to the nearest reachable hold-short.
        if (sameNameHoldShorts.Count > 0)
        {
            bool extended = ExtendToSameNameHoldShort(layout, segments, sameNameHoldShorts, ref currentNodeId);
            Log.LogDebug("[Variant] ExtendToSameNameHoldShort returned {Result}", extended);
            if (extended)
            {
                return true;
            }
        }

        if (nonVariantConnectors.Count > 0)
        {
            var connectors = string.Join(", ", nonVariantConnectors.Order());
            failReason = $"Taxi to runway {destinationRunway}: specify connecting taxiway ({connectors})";
            Log.LogDebug("[Variant] No variant found, non-variant connectors: {Connectors}", connectors);
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
        int segmentCountBeforeLastTw,
        List<(GroundNode HsNode, string VariantName)> variants,
        string? airportId,
        string destinationRunway,
        ref int currentNodeId
    )
    {
        // Pick variant: if multiple distinct names, choose closest to runway threshold
        string chosenVariant = PickBestVariant(variants, airportId, destinationRunway);
        Log.LogDebug(
            "[Variant] AutoExtend: chosen variant={Variant}, scanning segments [{From}..{To}]",
            chosenVariant,
            segmentCountBeforeLastTw,
            segments.Count - 1
        );

        // Find branch point: scan nodes along the last-taxiway segments
        int branchNodeId = -1;
        int branchSegmentIndex = -1;

        for (int i = segmentCountBeforeLastTw; i < segments.Count; i++)
        {
            // First iteration probes the segment's FromNodeId (the entry into the
            // last taxiway); branchSegmentIndex = i means segments[i..] are removed
            // and the variant walk starts from FromNodeId. Subsequent iterations
            // probe ToNodeId — to keep the segment ending at the branch we must use
            // i + 1 so segments[0..i] survive and segments[i+1..] are removed.
            bool isFirst = i == segmentCountBeforeLastTw;
            int nodeId = isFirst ? segments[i].FromNodeId : segments[i].ToNodeId;

            if (layout.NodeHasEdgeTo(nodeId, chosenVariant))
            {
                branchNodeId = nodeId;
                branchSegmentIndex = isFirst ? i : i + 1;
                break;
            }

            // Also check ToNodeId for first segment
            if (isFirst)
            {
                if (layout.NodeHasEdgeTo(segments[i].ToNodeId, chosenVariant))
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
                if (layout.NodeHasEdgeTo(segments[i].ToNodeId, chosenVariant))
                {
                    branchNodeId = segments[i].ToNodeId;
                    branchSegmentIndex = i + 1;
                    break;
                }
            }
        }

        if (branchNodeId == -1)
        {
            // The walk went the wrong direction at a fork — the variant junction
            // isn't along the walked route. Fall back to A* from the start of the
            // last taxiway to the nearest variant hold-short.
            Log.LogDebug("[Variant] AutoExtend: no branch point for {Variant} along route, trying A* fallback", chosenVariant);
            int lastTwStartId = segments.Count > segmentCountBeforeLastTw ? segments[segmentCountBeforeLastTw].FromNodeId : currentNodeId;
            return ExtendViaAStar(layout, segments, segmentCountBeforeLastTw, variants, chosenVariant, lastTwStartId, ref currentNodeId);
        }

        Log.LogDebug(
            "[Variant] AutoExtend: branch at #{NodeId} segIdx={SegIdx}, truncating {Count} segments after branch",
            branchNodeId,
            branchSegmentIndex,
            segments.Count - branchSegmentIndex
        );

        // Truncate segments after the branch point
        if (branchSegmentIndex < segments.Count)
        {
            segments.RemoveRange(branchSegmentIndex, segments.Count - branchSegmentIndex);
        }

        // Walk the variant from the branch point. StopAtRunwayId is mandatory:
        // without it the walk runs past the destination runway hold-short and onto
        // the runway centerline (TaxiwayIntersection end of the variant), stranding
        // the aircraft when LineUpPhase later tries to plan from on-centerline.
        bool walked = TaxiPathfinder.WalkTaxiway(
            layout,
            branchNodeId,
            chosenVariant,
            segments,
            out int endNodeId,
            new WalkOptions { StopAtRunwayId = destinationRunway }
        );
        Log.LogDebug(
            "[Variant] AutoExtend: walked {Variant} from #{Branch} → #{End}, success={Walked}",
            chosenVariant,
            branchNodeId,
            endNodeId,
            walked
        );

        if (walked)
        {
            currentNodeId = endNodeId;
        }

        return walked;
    }

    /// <summary>
    /// Fallback when the walk went the wrong direction and no branch point was found.
    /// Uses A* from the last-taxiway start node to the nearest variant hold-short,
    /// replacing the walked segments entirely.
    /// </summary>
    private static bool ExtendViaAStar(
        AirportGroundLayout layout,
        List<TaxiRouteSegment> segments,
        int segmentCountBeforeLastTw,
        List<(GroundNode HsNode, string VariantName)> variants,
        string chosenVariant,
        int fromNodeId,
        ref int currentNodeId
    )
    {
        // Find the nearest hold-short for the chosen variant
        GroundNode? targetHs = null;
        double bestDist = double.MaxValue;
        foreach (var (hsNode, varName) in variants)
        {
            if (!string.Equals(varName, chosenVariant, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!layout.Nodes.TryGetValue(fromNodeId, out var fromNode))
            {
                continue;
            }

            double dist = GeoMath.DistanceNm(fromNode.Position, hsNode.Position);
            if (dist < bestDist)
            {
                bestDist = dist;
                targetHs = hsNode;
            }
        }

        if (targetHs is null)
        {
            Log.LogDebug("[Variant] ExtendViaAStar: no hold-short found for {Variant}", chosenVariant);
            return false;
        }

        var astarRoute = TaxiPathfinderRouter.Current.FindRoute(layout, fromNodeId, targetHs.Id);
        if (astarRoute is null)
        {
            Log.LogDebug("[Variant] ExtendViaAStar: A* from #{From} to #{To} failed", fromNodeId, targetHs.Id);
            return false;
        }

        // Replace the last-taxiway segments with the A* route
        if (segmentCountBeforeLastTw < segments.Count)
        {
            segments.RemoveRange(segmentCountBeforeLastTw, segments.Count - segmentCountBeforeLastTw);
        }

        segments.AddRange(astarRoute.Segments);
        currentNodeId = astarRoute.Segments[^1].ToNodeId;
        Log.LogDebug(
            "[Variant] ExtendViaAStar: A* from #{From} to #{To} added {Count} segments, endNode=#{End}",
            fromNodeId,
            targetHs.Id,
            astarRoute.Segments.Count,
            currentNodeId
        );
        return true;
    }

    private static string PickBestVariant(List<(GroundNode HsNode, string VariantName)> variants, string? airportId, string destinationRunway)
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
        if (airportId is not null)
        {
            rwyInfo = NavigationDatabase.Instance.GetRunway(airportId, destinationRunway);
        }

        if (rwyInfo is null)
        {
            return variants[0].VariantName;
        }

        string bestName = variants[0].VariantName;
        double bestDist = double.MaxValue;

        foreach (var (hsNode, name) in variants)
        {
            double dist = GeoMath.DistanceNm(hsNode.Position, new LatLon(rwyInfo.ThresholdLatitude, rwyInfo.ThresholdLongitude));

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
            var route = TaxiPathfinderRouter.Current.FindRoute(layout, currentNodeId, hs.Id);
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
