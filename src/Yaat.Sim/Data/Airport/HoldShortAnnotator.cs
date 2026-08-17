using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Airport;

/// <summary>What a controller-issued <c>HS &lt;target&gt;</c> resolves to against a live taxi route.</summary>
internal enum ExplicitHoldShortOutcome
{
    /// <summary>An existing hold-short protects the target: re-arm it (and revoke any clearance).</summary>
    ReArm,

    /// <summary>No hold-short protects the target yet: insert one at <see cref="ExplicitHoldShortPlan.NodeId"/>.</summary>
    Add,

    /// <summary>The target is already protected in a way that must not be disturbed (destination runway).</summary>
    NoOp,

    /// <summary>The aircraft is already on or past the target runway; the hold-short cannot be honoured.</summary>
    AlreadyEntered,

    /// <summary>The target appears nowhere on the remaining route.</summary>
    NotOnRoute,
}

/// <summary>The mutation <see cref="HoldShortAnnotator.PlanExplicitHoldShort"/> would perform.</summary>
internal readonly record struct ExplicitHoldShortPlan
{
    public required ExplicitHoldShortOutcome Outcome { get; init; }

    /// <summary>The hold-short to re-arm. Set only for <see cref="ExplicitHoldShortOutcome.ReArm"/>.</summary>
    public HoldShortPoint? Existing { get; init; }

    /// <summary>Node to hang a new hold-short on. Set only for <see cref="ExplicitHoldShortOutcome.Add"/>.</summary>
    public int NodeId { get; init; }

    /// <summary>Name for the new hold-short. Set only for <see cref="ExplicitHoldShortOutcome.Add"/>.</summary>
    public string? TargetName { get; init; }
}

/// <summary>
/// Post-processes a resolved taxi route to insert hold-short points at runway
/// crossings, explicit controller-specified holds, and destination runway holds.
/// </summary>
internal static class HoldShortAnnotator
{
    private static readonly ILogger Log = SimLog.CreateLogger("HoldShortAnnotator");

    /// <summary>
    /// True when the route leaves <paramref name="startNodeId"/> — a hold-short bar the aircraft is
    /// parked on — passes over <paramref name="startRwyId"/>, and reaches that runway's paired bar on
    /// the far side. In other words, the aircraft is standing at the entry side of a crossing it is
    /// about to make, so that bar is the one a hold-short binds to and the far bar is its exit pair.
    ///
    /// Requiring the runway to actually be traversed in between is what separates a real pair from an
    /// unrelated later crossing of the same runway (an aircraft that just vacated a runway onto a
    /// single-sided exit bar must not be paired with a bar it reaches minutes later).
    ///
    /// The scan deliberately does NOT stop at a taxiway change: when a crossing point doubles as a
    /// taxiway junction, its two bars carry different taxiway names (SFO 10R/28L — near bar on Foxtrot,
    /// far bar on Charlie). Stopping there was issue #316, where the far bar became the hold-short and
    /// the aircraft taxied over an occupied runway to reach it.
    /// </summary>
    internal static bool RouteCrossesRunwayAfterStart(
        AirportGroundLayout layout,
        IReadOnlyList<TaxiRouteSegment> segments,
        int startNodeId,
        RunwayIdentifier startRwyId
    )
    {
        bool traversedRunway = false;
        foreach (var seg in segments)
        {
            if (seg.ToNodeId == startNodeId)
            {
                continue;
            }

            traversedRunway = traversedRunway || SegmentRunsAlongRunway(seg, startRwyId);

            if (!layout.Nodes.TryGetValue(seg.ToNodeId, out var node))
            {
                continue;
            }

            if (node.Type == GroundNodeType.RunwayHoldShort && node.RunwayId is { } rwyId && rwyId.Equals(startRwyId))
            {
                return traversedRunway;
            }

            traversedRunway = traversedRunway || NodeLiesOnRunway(node, startRwyId);
        }

        return false;
    }

    /// <summary>True when this segment runs along <paramref name="runwayId"/>'s centerline.</summary>
    private static bool SegmentRunsAlongRunway(TaxiRouteSegment segment, RunwayIdentifier runwayId) =>
        segment.Edge.Edge.IsRunwayCenterline && (segment.Edge.Edge.MatchesRunway(runwayId.End1) || segment.Edge.Edge.MatchesRunway(runwayId.End2));

    /// <summary>
    /// True when this node sits on <paramref name="runwayId"/>'s centerline. A taxiway that crosses a
    /// runway is split at the centerline, so the crossing point is a node incident to a runway edge —
    /// which is how a one-point crossing is recognised, since no route segment runs along the runway.
    /// </summary>
    private static bool NodeLiesOnRunway(GroundNode node, RunwayIdentifier runwayId) =>
        node.Edges.Exists(edge => edge.IsRunwayCenterline && (edge.MatchesRunway(runwayId.End1) || edge.MatchesRunway(runwayId.End2)));

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
        // RouteCrossesRunwayAfterStart separates the two.
        if (segments.Count > 0)
        {
            int startNodeId = segments[0].FromNodeId;
            if (
                layout.Nodes.TryGetValue(startNodeId, out var startNode)
                && startNode.Type == GroundNodeType.RunwayHoldShort
                && startNode.RunwayId is { } startRwyId
            )
            {
                if (RouteCrossesRunwayAfterStart(layout, segments, startNodeId, startRwyId))
                {
                    enteredRunways[startRwyId] = startNodeId;
                    seenHsNodes.Add((startRwyId, startNodeId));
                    Log.LogDebug(
                        "[HoldShortAnnotator] Starting node {NodeId} is HS for {Runway} — pre-seeded as entry (paired crossing ahead)",
                        startNodeId,
                        startRwyId
                    );
                }
                else
                {
                    Log.LogDebug(
                        "[HoldShortAnnotator] Starting node {NodeId} is HS for {Runway} — NOT pre-seeding (exit-only, route never crosses it)",
                        startNodeId,
                        startRwyId
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
    /// Whether a hold-short target name matches a controller-supplied argument. Accepts both runway
    /// designators (parsed via <see cref="RunwayIdentifier"/>, so <c>28R</c> matches <c>28R/10L</c>)
    /// and taxiway/intersection names (case-insensitive equality, so <c>B</c> matches <c>B</c>).
    /// </summary>
    internal static bool TargetMatches(string? targetName, string arg)
    {
        if (targetName is null)
        {
            return false;
        }

        return RunwayIdentifier.Parse(targetName).Contains(arg) || string.Equals(targetName, arg, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Works out what <c>HS &lt;target&gt;</c> would do to <paramref name="route"/> without touching it,
    /// so a multi-target command can validate every target before mutating any. Apply the result with
    /// <see cref="ApplyExplicitHoldShort"/>. A located target (<c>C@J</c>) only considers points and
    /// nodes on the location taxiway, so the crossing the controller named binds even when an earlier
    /// crossing of the same target lies ahead on the route (issue #358).
    /// </summary>
    internal static ExplicitHoldShortPlan PlanExplicitHoldShort(AirportGroundLayout? layout, TaxiRoute route, HoldShortTarget target)
    {
        // The route's HoldShortPoints already carry exactly one entry per runway it crosses, on the
        // entry side — AddImplicitRunwayHoldShorts pairs the bars and drops the far side. So an
        // existing point is the authoritative near-side bar; never re-derive it from the segments.
        var candidates = route
            .HoldShortPoints.Where(h => TargetMatches(h.TargetName, target.Target) && NodeOnLocationTaxiway(layout, h.NodeId, target.OnTaxiway))
            .ToList();
        if (candidates.Count > 0)
        {
            // A bar can only be behind the aircraft if it was cleared — the taxi gate physically
            // stops it otherwise. Testing IsCleared first is what makes this safe against
            // TaxiingPhase.BuildResumePhases, which bumps CurrentSegmentIndex past the bar the
            // aircraft is *stopped at*.
            var ahead = candidates.Where(h => !IsPassed(route, h)).OrderBy(h => SegmentIndexOf(route, h)).ToList();
            if (ahead.Count == 0)
            {
                return new ExplicitHoldShortPlan { Outcome = ExplicitHoldShortOutcome.AlreadyEntered };
            }

            var nearest = ahead[0];

            // A destination-runway hold already stops the aircraft short of that runway, and its
            // reason gates the LUAW/CTO departure flow. Re-arming it as a plain ExplicitHoldShort
            // would strip that gate, so treat "HS <destination runway>" as a no-op.
            if (nearest.Reason == HoldShortReason.DestinationRunway)
            {
                return new ExplicitHoldShortPlan { Outcome = ExplicitHoldShortOutcome.NoOp };
            }

            return new ExplicitHoldShortPlan { Outcome = ExplicitHoldShortOutcome.ReArm, Existing = nearest };
        }

        if (layout is null)
        {
            return new ExplicitHoldShortPlan { Outcome = ExplicitHoldShortOutcome.NotOnRoute };
        }

        // No point for this target yet: walk the remaining route for the first node to hang one on.
        // At a given node a runway bar is tried before the node's taxiway edges. A located target
        // skips nodes off its location taxiway.
        for (int i = Math.Max(0, route.CurrentSegmentIndex); i < route.Segments.Count; i++)
        {
            if (!layout.Nodes.TryGetValue(route.Segments[i].ToNodeId, out var node))
            {
                continue;
            }

            if (target.OnTaxiway is { } onTaxiway && !node.Edges.Any(e => e.MatchesTaxiway(onTaxiway)))
            {
                continue;
            }

            if (node.Type == GroundNodeType.RunwayHoldShort && node.RunwayId is { } nodeRwyId && nodeRwyId.Contains(target.Target))
            {
                return new ExplicitHoldShortPlan
                {
                    Outcome = ExplicitHoldShortOutcome.Add,
                    NodeId = node.Id,
                    TargetName = nodeRwyId.ToString(),
                };
            }

            foreach (var edge in node.Edges)
            {
                if (edge.MatchesTaxiway(target.Target))
                {
                    return new ExplicitHoldShortPlan
                    {
                        Outcome = ExplicitHoldShortOutcome.Add,
                        NodeId = node.Id,
                        TargetName = target.Target,
                    };
                }
            }
        }

        return new ExplicitHoldShortPlan { Outcome = ExplicitHoldShortOutcome.NotOnRoute };
    }

    /// <summary>
    /// True when the located constraint is satisfied: no location, or the node has an edge on the
    /// location taxiway. Without a layout the constraint cannot be checked and fails closed — a
    /// located target must never silently bind the wrong crossing.
    /// </summary>
    private static bool NodeOnLocationTaxiway(AirportGroundLayout? layout, int nodeId, string? onTaxiway)
    {
        if (onTaxiway is null)
        {
            return true;
        }

        return layout is not null && layout.Nodes.TryGetValue(nodeId, out var node) && node.Edges.Any(e => e.MatchesTaxiway(onTaxiway));
    }

    /// <summary>
    /// Commits a <see cref="PlanExplicitHoldShort"/> result. A re-arm revokes the existing clearance
    /// whatever set it — AutoCross, an earlier CROSS, or the implicit first-crossing clearance — because
    /// the hold-short is the controller's most recent instruction for that runway.
    /// </summary>
    internal static void ApplyExplicitHoldShort(TaxiRoute route, ExplicitHoldShortPlan plan, HoldShortTarget target)
    {
        switch (plan.Outcome)
        {
            case ExplicitHoldShortOutcome.ReArm when plan.Existing is { } existing:
                existing.Reason = HoldShortReason.ExplicitHoldShort;
                existing.IsCleared = false;
                existing.ClearedByAutoCross = false;
                Log.LogDebug("[HoldShortAnnotator] Explicit HS {Target}: re-armed hold-short at node {NodeId}", target, existing.NodeId);
                break;

            case ExplicitHoldShortOutcome.Add:
                route.HoldShortPoints.Add(
                    new HoldShortPoint
                    {
                        NodeId = plan.NodeId,
                        Reason = HoldShortReason.ExplicitHoldShort,
                        TargetName = plan.TargetName,
                    }
                );
                Log.LogDebug("[HoldShortAnnotator] Explicit HS {Target}: added hold-short at node {NodeId}", target, plan.NodeId);
                break;

            case ExplicitHoldShortOutcome.NoOp:
            case ExplicitHoldShortOutcome.AlreadyEntered:
            case ExplicitHoldShortOutcome.NotOnRoute:
            default:
                break;
        }
    }

    private static int SegmentIndexOf(TaxiRoute route, HoldShortPoint holdShort)
    {
        for (int i = 0; i < route.Segments.Count; i++)
        {
            if (route.Segments[i].ToNodeId == holdShort.NodeId)
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    private static bool IsPassed(TaxiRoute route, HoldShortPoint holdShort)
    {
        return holdShort.IsCleared && SegmentIndexOf(route, holdShort) < route.CurrentSegmentIndex;
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
                var vn = VirtualNode.OffsetBefore(layout, route, hs.NodeId, runwayHalfLengthNm, stopAtRunwayHoldShort: false);
                hs.Latitude = vn.Position.Lat;
                hs.Longitude = vn.Position.Lon;
                continue;
            }

            // Taxiway hold-short: offset back from intersection along approach edge. When the
            // hold-short sits within a fuselage length past a runway the route crosses, the normal
            // aircraftLength+30 setback would place the stop behind the runway. Cap to the
            // nose-at-line setback (½ length) and clamp at the runway hold-short so the aircraft
            // holds at the taxiway line with its tail over the bars — never reversing onto the
            // runway it just crossed (issue #172 W1). When the gap is shorter than the whole
            // fuselage the aircraft also cannot fully clear the runway: tag the overhung runway
            // hold-short node and warn the controller at issuance (W2/W3).
            var crossedRunway = FindCrossedRunwayHoldShort(layout, route, hs.NodeId, taxiwayOffsetNm);
            bool justPastRunway = crossedRunway is not null;
            double twyOffsetNm = justPastRunway ? runwayHalfLengthNm : taxiwayOffsetNm;
            var twyVn = VirtualNode.OffsetBefore(layout, route, hs.NodeId, twyOffsetNm, stopAtRunwayHoldShort: justPastRunway);
            hs.Latitude = twyVn.Position.Lat;
            hs.Longitude = twyVn.Position.Lon;

            if (crossedRunway is { } cr && (cr.GapNm * GeoMath.FeetPerNm) < aircraftLengthFt)
            {
                hs.TailOverRunwayNodeId = cr.RunwayNodeId;
                string rwy =
                    layout.Nodes.TryGetValue(cr.RunwayNodeId, out var rwyNode) && rwyNode.RunwayId is { } rid ? rid.ToDisplayString() : "the runway";
                string warning =
                    $"holding short of {RunwayIdentifier.ToDisplayDesignator(hs.TargetName ?? "")} leaves the tail over RWY {rwy} — unable to clear the runway";
                if (!route.Warnings.Contains(warning))
                {
                    route.Warnings.Add(warning);
                }
            }

            Log.LogDebug(
                "[HoldShortAnnotator] Taxiway HS at node {NodeId} for {Target}: offset {OffsetFt:F0}ft ({Lat:F6}, {Lon:F6}) justPastRunway={JustPast} tailOverRunwayNode={TailOver}",
                hs.NodeId,
                hs.TargetName,
                twyOffsetNm * GeoMath.FeetPerNm,
                twyVn.Position.Lat,
                twyVn.Position.Lon,
                justPastRunway,
                hs.TailOverRunwayNodeId
            );
        }
    }

    /// <summary>
    /// Walks the route backward from <paramref name="nodeId"/> up to <paramref name="withinNm"/> and
    /// returns the first <see cref="GroundNodeType.RunwayHoldShort"/> node encountered (the runway the
    /// route just crossed) with the along-route gap to it, or null if none lies within range. Used to
    /// cap a taxiway hold-short's setback so it never lands behind the runway, and to detect the
    /// tail-over-runway state when the gap is shorter than a fuselage.
    /// </summary>
    private static (int RunwayNodeId, double GapNm)? FindCrossedRunwayHoldShort(
        AirportGroundLayout layout,
        TaxiRoute route,
        int nodeId,
        double withinNm
    )
    {
        double accumulated = 0;
        int currentId = nodeId;
        for (int guard = 0; guard <= route.Segments.Count; guard++)
        {
            int approachId = -1;
            foreach (var seg in route.Segments)
            {
                if (seg.ToNodeId == currentId)
                {
                    approachId = seg.FromNodeId;
                    break;
                }
            }

            if (
                approachId < 0
                || !layout.Nodes.TryGetValue(approachId, out var approachNode)
                || !layout.Nodes.TryGetValue(currentId, out var curNode)
            )
            {
                break;
            }

            accumulated += GeoMath.DistanceNm(curNode.Position, approachNode.Position);
            if (accumulated > withinNm)
            {
                break;
            }

            if (approachNode.Type == GroundNodeType.RunwayHoldShort)
            {
                return (approachId, accumulated);
            }

            currentId = approachId;
        }

        return null;
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
