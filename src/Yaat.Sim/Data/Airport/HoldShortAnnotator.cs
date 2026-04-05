using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Post-processes a resolved taxi route to insert hold-short points at runway
/// crossings, explicit controller-specified holds, and destination runway holds.
/// </summary>
internal static class HoldShortAnnotator
{
    private static readonly ILogger Log = SimLog.CreateLogger("HoldShortAnnotator");

    /// <summary>
    /// Scans the segment list for runway hold-short nodes and inserts implicit
    /// hold-short points at each runway crossing entry. Exit-side nodes are
    /// recognised by entry/exit pairing and skipped.
    /// </summary>
    internal static void AddImplicitRunwayHoldShorts(AirportGroundLayout layout, List<TaxiRouteSegment> segments, List<HoldShortPoint> holdShorts)
    {
        // Entry/exit pairing by encounter order: the first HS node for a
        // runway is the entry side (add hold-short); the second distinct HS
        // node for that runway is the exit side (skip and reset tracking).
        // Revisiting the same node (backtrack) doesn't count as a new encounter.
        var enteredRunways = new Dictionary<RunwayIdentifier, int>();
        var seenHsNodes = new HashSet<(RunwayIdentifier, int)>();

        // Pre-seed entry tracking from the starting node. If the route begins
        // at a RunwayHoldShort (e.g., re-routed from a destination hold-short),
        // the aircraft is already at the entry side of that runway crossing.
        // The next HS for the same runway is the exit side and must be skipped.
        if (segments.Count > 0)
        {
            int startNodeId = segments[0].FromNodeId;
            if (
                layout.Nodes.TryGetValue(startNodeId, out var startNode)
                && startNode.Type == GroundNodeType.RunwayHoldShort
                && startNode.RunwayId is { } startRwyId
            )
            {
                enteredRunways[startRwyId] = startNodeId;
                seenHsNodes.Add((startRwyId, startNodeId));
                Log.LogDebug("[HoldShortAnnotator] Starting node {NodeId} is HS for {Runway} — pre-seeded as entry", startNodeId, startRwyId);
            }
        }

        foreach (var seg in segments)
        {
            if (
                !layout.Nodes.TryGetValue(seg.ToNodeId, out var node)
                || node.Type != GroundNodeType.RunwayHoldShort
                || node.RunwayId is not { } rwyId
            )
            {
                continue;
            }

            // Skip if we've already processed this exact HS node for this runway
            if (!seenHsNodes.Add((rwyId, node.Id)))
            {
                Log.LogDebug("[HoldShortAnnotator] Skipping duplicate HS node {NodeId} for {Runway}", node.Id, rwyId);
                continue;
            }

            if (enteredRunways.Remove(rwyId))
            {
                // Exit-side HS: paired with the previous entry, skip
                Log.LogDebug("[HoldShortAnnotator] Exit-side HS node {NodeId} for {Runway} — paired with entry, skipping", node.Id, rwyId);
                continue;
            }

            // Entry-side: track for pairing and add hold-short
            enteredRunways[rwyId] = node.Id;
            Log.LogDebug("[HoldShortAnnotator] Entry-side HS node {NodeId} for {Runway} — adding hold-short", node.Id, rwyId);

            if (!HoldShortExists(holdShorts, node.Id))
            {
                holdShorts.Add(
                    new HoldShortPoint
                    {
                        NodeId = node.Id,
                        Reason = HoldShortReason.RunwayCrossing,
                        TargetName = rwyId.ToString(),
                    }
                );
            }
        }
    }

    /// <summary>
    /// Finds the hold-short point for <paramref name="target"/> along the route.
    /// Checks runway hold-short nodes first, then falls back to taxiway intersection
    /// detection (first node with an adjacent edge on the target taxiway).
    /// </summary>
    internal static void AddExplicitHoldShort(
        AirportGroundLayout layout,
        List<TaxiRouteSegment> segments,
        List<HoldShortPoint> holdShorts,
        string target
    )
    {
        // First pass: check for runway hold-short nodes matching the target
        bool foundRunway = false;
        foreach (var seg in segments)
        {
            if (!layout.Nodes.TryGetValue(seg.ToNodeId, out var node))
            {
                continue;
            }

            if (node.Type != GroundNodeType.RunwayHoldShort || node.RunwayId is not { } nodeRwyId)
            {
                continue;
            }

            if (!nodeRwyId.Contains(target))
            {
                continue;
            }

            foundRunway = true;
            if (!HoldShortExists(holdShorts, node.Id))
            {
                holdShorts.Add(
                    new HoldShortPoint
                    {
                        NodeId = node.Id,
                        Reason = HoldShortReason.ExplicitHoldShort,
                        TargetName = target,
                    }
                );
            }
        }

        if (foundRunway)
        {
            return;
        }

        // Second pass: taxiway intersection — find the first node with an
        // adjacent edge on the target taxiway. The hold-short is placed at
        // this intersection node; the actual stop position is offset back
        // along the approach edge by ComputeHoldShortPositions later.
        foreach (var seg in segments)
        {
            if (!layout.Nodes.TryGetValue(seg.ToNodeId, out var node))
            {
                continue;
            }

            foreach (var edge in node.Edges)
            {
                if (string.Equals(edge.TaxiwayName, target, StringComparison.OrdinalIgnoreCase))
                {
                    if (!HoldShortExists(holdShorts, seg.ToNodeId))
                    {
                        holdShorts.Add(
                            new HoldShortPoint
                            {
                                NodeId = seg.ToNodeId,
                                Reason = HoldShortReason.ExplicitHoldShort,
                                TargetName = target.ToUpperInvariant(),
                            }
                        );
                    }
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Appends a hold-short point at the last segment node, marking it as
    /// the destination runway hold position.
    /// </summary>
    internal static void AddDestinationHoldShort(
        AirportGroundLayout layout,
        List<TaxiRouteSegment> segments,
        List<HoldShortPoint> holdShorts,
        string runwayId
    )
    {
        if (segments.Count == 0)
        {
            return;
        }

        int lastNodeId = segments[^1].ToNodeId;

        // Remove any crossing hold-short at this node — the aircraft is taxiing TO
        // this runway, not crossing it. Without this, the same node gets both a
        // RunwayCrossing and DestinationRunway hold-short.
        holdShorts.RemoveAll(h => h.NodeId == lastNodeId && h.Reason == HoldShortReason.RunwayCrossing);

        holdShorts.Add(
            new HoldShortPoint
            {
                NodeId = lastNodeId,
                Reason = HoldShortReason.DestinationRunway,
                TargetName = runwayId,
            }
        );
    }

    /// <summary>
    /// Computes hold-short stop positions for all hold-short points in the route.
    /// Runway hold-shorts (RunwayCrossing, DestinationRunway) use the node position directly.
    /// Taxiway hold-shorts (ExplicitHoldShort targeting a taxiway) are offset back from
    /// the intersection node along the approach edge by <paramref name="aircraftLengthFt"/> + buffer.
    /// </summary>
    internal static void ComputeHoldShortPositions(AirportGroundLayout layout, TaxiRoute route, double aircraftLengthFt)
    {
        const double bufferFt = 30.0;
        const double ftPerNm = 6076.12;
        double offsetNm = (aircraftLengthFt + bufferFt) / ftPerNm;

        foreach (var hs in route.HoldShortPoints)
        {
            if (!layout.Nodes.TryGetValue(hs.NodeId, out var hsNode))
            {
                continue;
            }

            // Runway hold-shorts and destination holds: use node position directly.
            // RunwayHoldShort nodes are already placed at the hold line in the GeoJSON.
            if (hs.Reason is HoldShortReason.RunwayCrossing or HoldShortReason.DestinationRunway)
            {
                hs.Latitude = hsNode.Latitude;
                hs.Longitude = hsNode.Longitude;
                continue;
            }

            // ExplicitHoldShort: check if it's a runway HS node (first pass matched a runway)
            if (hsNode.Type == GroundNodeType.RunwayHoldShort)
            {
                hs.Latitude = hsNode.Latitude;
                hs.Longitude = hsNode.Longitude;
                continue;
            }

            // Taxiway hold-short: offset back from intersection along approach edge.
            // Find the segment that arrives at this node to determine approach direction.
            int approachFromNodeId = -1;
            foreach (var seg in route.Segments)
            {
                if (seg.ToNodeId == hs.NodeId)
                {
                    approachFromNodeId = seg.FromNodeId;
                    break;
                }
            }

            if (approachFromNodeId < 0 || !layout.Nodes.TryGetValue(approachFromNodeId, out var approachNode))
            {
                // Can't determine approach direction — fall back to node position
                hs.Latitude = hsNode.Latitude;
                hs.Longitude = hsNode.Longitude;
                continue;
            }

            // Bearing from intersection back toward approach node
            double backBearing = GeoMath.BearingTo(hsNode.Latitude, hsNode.Longitude, approachNode.Latitude, approachNode.Longitude);

            // Clamp offset to 90% of edge length so the aircraft doesn't end up
            // at or past the approach node (which would confuse segment navigation).
            double edgeLenNm = GeoMath.DistanceNm(approachNode.Latitude, approachNode.Longitude, hsNode.Latitude, hsNode.Longitude);
            double clampedOffsetNm = Math.Min(offsetNm, edgeLenNm * 0.9);

            var (lat, lon) = GeoMath.ProjectPointRaw(hsNode.Latitude, hsNode.Longitude, backBearing, clampedOffsetNm);
            hs.Latitude = lat;
            hs.Longitude = lon;

            Log.LogDebug(
                "[HoldShortAnnotator] Taxiway HS at node {NodeId} for {Target}: offset {OffsetFt:F0}ft back from intersection ({Lat:F6}, {Lon:F6})",
                hs.NodeId,
                hs.TargetName,
                clampedOffsetNm * ftPerNm,
                lat,
                lon
            );
        }
    }

    /// <summary>
    /// Estimates aircraft fuselage length (ft) from CWT code when FAA ACD data is unavailable.
    /// </summary>
    internal static double CwtFallbackLengthFt(string? aircraftType)
    {
        var cwt = WakeTurbulenceData.GetCwt(aircraftType ?? "");
        return cwt switch
        {
            "A" => 250.0, // Super (A388)
            "B" => 220.0, // Upper Heavy (B744, B77W)
            "C" => 200.0, // Lower Heavy (B763, A332, B788)
            "D" => 155.0, // B757
            "E" => 130.0, // Large Low (DC85, IL76)
            "F" => 110.0, // Upper Medium (B738, A320)
            "G" => 80.0, // Lower Medium (CRJ7, E170)
            "H" => 60.0, // Upper Small (C208, PC12)
            "I" => 40.0, // Small (C172, PA28)
            _ => 80.0, // Unknown — assume medium
        };
    }

    internal static bool HoldShortExists(List<HoldShortPoint> holdShorts, int nodeId)
    {
        foreach (var hs in holdShorts)
        {
            if (hs.NodeId == nodeId)
            {
                return true;
            }
        }

        return false;
    }
}
