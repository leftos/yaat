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
        // at a RunwayHoldShort and the aircraft is mid-crossing (e.g., re-routed
        // from a destination hold-short), the next HS for the same runway is
        // the exit side of the crossing and must be skipped.
        //
        // BUT: a route can also begin at a RunwayHoldShort when the aircraft
        // has just vacated the runway via a single-sided exit taxiway (e.g.,
        // exited 28R onto H, where node 499 is the H/28R hold-short line).
        // In that case the aircraft is on the taxiway side of the line, NOT
        // mid-crossing. Pre-seeding there is wrong because it flips the next
        // encountered HS for the same runway from "entry" to "exit" — and the
        // next encountered HS may be at a totally different crossing (e.g.,
        // the B crossing of 28R, reached after taxiing H → C → B), not the
        // pair of the starting HS at all.
        //
        // Distinguish the two cases by walking forward along segments[0]'s
        // taxiway looking for a paired HS on the same taxiway. Runway
        // crossings place HSes on both sides of the runway along one named
        // taxiway (e.g., B: HS@188 → interior → HS@186, both labeled "B").
        // If we find a paired HS on the same taxiway, the starting HS is the
        // entry side of a crossing — pre-seed. If we don't, the aircraft is
        // leaving the runway and the starting HS is standalone — skip.
        if (segments.Count > 0)
        {
            int startNodeId = segments[0].FromNodeId;
            if (
                layout.Nodes.TryGetValue(startNodeId, out var startNode)
                && startNode.Type == GroundNodeType.RunwayHoldShort
                && startNode.RunwayId is { } startRwyId
            )
            {
                string startTaxiway = segments[0].TaxiwayName;
                bool hasPairedExit = false;
                foreach (var seg in segments)
                {
                    if (seg.TaxiwayName != startTaxiway)
                    {
                        break;
                    }
                    if (seg.ToNodeId == startNodeId)
                    {
                        continue;
                    }
                    if (
                        layout.Nodes.TryGetValue(seg.ToNodeId, out var segToNode)
                        && segToNode.Type == GroundNodeType.RunwayHoldShort
                        && segToNode.RunwayId is { } segRwyId
                        && segRwyId.Equals(startRwyId)
                    )
                    {
                        hasPairedExit = true;
                        break;
                    }
                }

                if (hasPairedExit)
                {
                    enteredRunways[startRwyId] = startNodeId;
                    seenHsNodes.Add((startRwyId, startNodeId));
                    Log.LogDebug(
                        "[HoldShortAnnotator] Starting node {NodeId} is HS for {Runway} — pre-seeded as entry (paired crossing on {Taxiway})",
                        startNodeId,
                        startRwyId,
                        startTaxiway
                    );
                }
                else
                {
                    Log.LogDebug(
                        "[HoldShortAnnotator] Starting node {NodeId} is HS for {Runway} — NOT pre-seeding (exit-only, no paired HS on {Taxiway})",
                        startNodeId,
                        startRwyId,
                        startTaxiway
                    );
                }
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
        // Runway pass: an explicit HS for a runway must always hold on the entry side
        // and must not be silently cleared by auto-cross. The implicit pass
        // (AddImplicitRunwayHoldShorts) has already chosen the correct entry-side node
        // and added it as RunwayCrossing — promote that entry to ExplicitHoldShort so
        // the auto-cross loop in GroundCommandHandler.TryTaxi leaves it uncleared.
        // Otherwise, walk segments and add the FIRST matching node only — never the
        // exit-side, which would shadow the explicit hold on the wrong side of the runway.
        foreach (var existing in holdShorts)
        {
            if (existing.Reason != HoldShortReason.RunwayCrossing || existing.TargetName is null)
            {
                continue;
            }

            if (!RunwayIdentifier.Parse(existing.TargetName).Contains(target))
            {
                continue;
            }

            existing.Reason = HoldShortReason.ExplicitHoldShort;
            Log.LogDebug(
                "[HoldShortAnnotator] Explicit HS {Target}: upgraded existing crossing at node {NodeId} to ExplicitHoldShort",
                target,
                existing.NodeId
            );
            return;
        }

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
            break;
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
                if (edge.MatchesTaxiway(target))
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
    /// Runway hold-shorts are offset back from the node by half the aircraft length so the
    /// aircraft's nose stops AT the hold-short line (the aircraft position is its center).
    /// Taxiway hold-shorts are offset back from the intersection node along the approach edge
    /// by <paramref name="aircraftLengthFt"/> + buffer.
    /// </summary>
    internal static void ComputeHoldShortPositions(AirportGroundLayout layout, TaxiRoute route, double aircraftLengthFt)
    {
        const double bufferFt = 30.0;
        double taxiwayOffsetNm = (aircraftLengthFt + bufferFt) / GeoMath.FeetPerNm;
        double runwayHalfLengthNm = (aircraftLengthFt / 2.0) / GeoMath.FeetPerNm;

        foreach (var hs in route.HoldShortPoints)
        {
            if (!layout.Nodes.TryGetValue(hs.NodeId, out var hsNode))
            {
                continue;
            }

            // Runway hold-shorts and destination holds: offset back from node by half the
            // aircraft length so the aircraft center (position) stops with its nose at the node.
            if ((hs.Reason is HoldShortReason.RunwayCrossing or HoldShortReason.DestinationRunway) || (hsNode.Type == GroundNodeType.RunwayHoldShort))
            {
                var vn = VirtualNode.OffsetBefore(layout, route, hs.NodeId, runwayHalfLengthNm);
                hs.Latitude = vn.Position.Lat;
                hs.Longitude = vn.Position.Lon;
                continue;
            }

            // Taxiway hold-short: offset back from intersection along approach edge.
            var twyVn = VirtualNode.OffsetBefore(layout, route, hs.NodeId, taxiwayOffsetNm);
            hs.Latitude = twyVn.Position.Lat;
            hs.Longitude = twyVn.Position.Lon;

            Log.LogDebug(
                "[HoldShortAnnotator] Taxiway HS at node {NodeId} for {Target}: offset {OffsetFt:F0}ft back from intersection ({Lat:F6}, {Lon:F6})",
                hs.NodeId,
                hs.TargetName,
                taxiwayOffsetNm * GeoMath.FeetPerNm,
                twyVn.Position.Lat,
                twyVn.Position.Lon
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
