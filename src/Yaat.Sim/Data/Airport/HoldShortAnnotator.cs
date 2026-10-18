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
public static class HoldShortAnnotator
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
        foreach (TaxiRouteSegment seg in segments)
        {
            if (seg.ToNodeId == startNodeId)
            {
                continue;
            }

            traversedRunway = traversedRunway || SegmentRunsAlongRunway(seg, startRwyId);

            if (!layout.Nodes.TryGetValue(seg.ToNodeId, out GroundNode? node))
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
                layout.Nodes.TryGetValue(startNodeId, out GroundNode? startNode)
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

        foreach (TaxiRouteSegment seg in segments)
        {
            if (
                !layout.Nodes.TryGetValue(seg.ToNodeId, out GroundNode? node)
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

        // A spot name keeps its $ sigil on both sides, so it only ever matches by literal equality —
        // never as runway "17" via the designator parse.
        if (HoldShortTarget.IsSpotTargetName(targetName) || HoldShortTarget.IsSpotTargetName(arg))
        {
            return string.Equals(targetName, arg, StringComparison.OrdinalIgnoreCase);
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
            .HoldShortPoints.Where(h => TargetMatches(h.TargetName, target.MatchKey) && NodeOnLocationTaxiway(layout, h.NodeId, target.OnTaxiway))
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

            HoldShortPoint nearest = ahead[0];

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
            if (!layout.Nodes.TryGetValue(route.Segments[i].ToNodeId, out GroundNode? node))
            {
                continue;
            }

            if (target.OnTaxiway is { } onTaxiway && !node.Edges.Any(e => e.MatchesTaxiway(onTaxiway)))
            {
                continue;
            }

            // A spot target binds the named Spot node itself (issue #394) — never a bar or a taxiway edge.
            if (target.IsSpot)
            {
                if (Pathfinding.RouteMaterialiser.IsSpotNode(node, target.Target))
                {
                    return new ExplicitHoldShortPlan
                    {
                        Outcome = ExplicitHoldShortOutcome.Add,
                        NodeId = node.Id,
                        TargetName = target.MatchKey,
                    };
                }

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

            foreach (IGroundEdge edge in node.Edges)
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
    /// located target must never silently bind the wrong crossing. The one home for the incidence
    /// test: <c>HS C@J</c> binding a route point and <c>ATXI 28L@J</c> picking a bar ask the same
    /// question of a node.
    /// </summary>
    internal static bool NodeOnLocationTaxiway(AirportGroundLayout? layout, int nodeId, string? onTaxiway)
    {
        if (onTaxiway is null)
        {
            return true;
        }

        return layout is not null && layout.Nodes.TryGetValue(nodeId, out GroundNode? node) && node.Edges.Any(e => e.MatchesTaxiway(onTaxiway));
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
                // A new HS is a new instruction: whether the aircraft can make a merge hold is judged afresh.
                existing.MergeHoldDeclined = false;
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

    private static bool IsPassed(TaxiRoute route, HoldShortPoint holdShort) =>
        holdShort.IsCleared && SegmentIndexOf(route, holdShort) < route.CurrentSegmentIndex;

    /// <summary>
    /// Appends a hold-short point at the last segment node, marking it as
    /// the destination runway hold position.
    /// </summary>
    internal static void AddDestinationHoldShort(List<TaxiRouteSegment> segments, List<HoldShortPoint> holdShorts, string runwayId)
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
    /// aircraft's nose stops AT the hold-short line (the aircraft position is its center). A taxi
    /// spot (<c>HS $17</c>) is a painted point with nothing to clear beyond it, so it takes the same
    /// nose-at-the-mark setback — the taxiway setback would put a widebody back in the junction
    /// behind the spot. Taxiway hold-shorts are offset back from the intersection node along the
    /// approach edge by <paramref name="aircraftLengthFt"/> + buffer.
    ///
    /// <para>The taxiway setback is what AIM 2-3-5.b.3 asks of the pilot: told to hold short of a taxiway,
    /// "the pilot MUST STOP the aircraft at a point which provides adequate clearance from an aircraft on
    /// the intersecting taxiway" — a whole fuselage back from the junction is that point, since the aircraft's
    /// position is its centre. The 30 ft on top is a judgement call: no FAA document gives a figure for it.</para>
    ///
    /// <para>That setback is then held to a wingtip-clearance floor (<see cref="ApplyWingtipClearanceFloor"/>): the nose
    /// stays at least <see cref="WingtipClearanceFloorFt"/> from the crossed taxiway's centreline, so an aircraft taxiing
    /// on it clears the holder's nose.</para>
    /// </summary>
    public static void ComputeHoldShortPositions(AirportGroundLayout layout, TaxiRoute route, double aircraftLengthFt)
    {
        double taxiwayOffsetNm = (aircraftLengthFt + TaxiwayHoldShortBufferFt) / GeoMath.FeetPerNm;
        double runwayHalfLengthNm = (aircraftLengthFt / 2.0) / GeoMath.FeetPerNm;

        foreach (HoldShortPoint hs in route.HoldShortPoints)
        {
            // An unable bar's position is the stop the taxi phase moved it to, not the painted line: the
            // aircraft was already inside its braking distance when the bar was armed, so recomputing the
            // setback here would put the bar back somewhere nobody is going to stop.
            if (hs.Unable || !layout.Nodes.TryGetValue(hs.NodeId, out GroundNode? hsNode))
            {
                continue;
            }

            hs.MergeHold = null;

            // Runway hold-shorts, destination holds, and spot targets: offset back from node by half the
            // aircraft length so the aircraft center (position) stops with its nose at the node. Keyed on
            // the target, never on the node type alone: a taxiway bar can bind a spot-typed node painted
            // at the junction (SFO spot 32 on B at T), and that bar still needs the taxiway setback.
            if (
                (hs.Reason is HoldShortReason.RunwayCrossing or HoldShortReason.DestinationRunway)
                || (hsNode.Type is GroundNodeType.RunwayHoldShort)
                || HoldShortTarget.IsSpotTargetName(hs.TargetName)
            )
            {
                GroundNode vn = VirtualNode.OffsetBefore(layout, route, hs.NodeId, runwayHalfLengthNm, HoldShortStopKind.Runway);
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
            (int RunwayNodeId, double GapNm)? crossedRunway = FindCrossedRunwayHoldShort(layout, route, hs.NodeId, taxiwayOffsetNm);
            bool justPastRunway = crossedRunway is not null;
            double twyOffsetNm = justPastRunway ? runwayHalfLengthNm : taxiwayOffsetNm;
            HoldShortStopKind stopKind = justPastRunway ? HoldShortStopKind.TaxiwayJustPastRunway : HoldShortStopKind.Taxiway;
            GroundNode twyVn = VirtualNode.OffsetBefore(layout, route, hs.NodeId, twyOffsetNm, stopKind);
            if (!justPastRunway)
            {
                twyVn = ApplyWingtipClearanceFloor(layout, route, hs, aircraftLengthFt, twyVn);
            }

            hs.Latitude = twyVn.Position.Lat;
            hs.Longitude = twyVn.Position.Lon;

            if (crossedRunway is { } cr && (cr.GapNm * GeoMath.FeetPerNm) < aircraftLengthFt)
            {
                hs.TailOverRunwayNodeId = cr.RunwayNodeId;
                string rwy =
                    layout.Nodes.TryGetValue(cr.RunwayNodeId, out GroundNode? rwyNode) && rwyNode.RunwayId is { } rid
                        ? rid.ToDisplayString()
                        : "the runway";
                string warning =
                    $"holding short of {HoldShortTarget.Describe(hs.TargetName ?? "")} leaves the tail over RWY {rwy} — unable to clear the runway";
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

    /// <summary>The margin (ft) a taxiway hold-short keeps beyond the aircraft's length. A judgement call: no FAA document gives a figure.</summary>
    private const double TaxiwayHoldShortBufferFt = 30.0;

    /// <summary>How far past the bar's node (ft, along the route) the search for the crossed centreline runs.</summary>
    private const double CentrelineMeetSearchAheadFt = 500.0;

    /// <summary>Only centreline edges of the crossed taxiway within this distance (ft) of the bar's node count.</summary>
    private const double CentrelineSearchRadiusFt = 2000.0;

    /// <summary>The step (ft) the nose walks back along the route while looking for the setback.</summary>
    private const double NoseWalkStepFt = 1.0;

    /// <summary>
    /// The nose-to-centreline clearance floor (ft) for a taxiway hold-short: half the largest wingspan the
    /// airport's Aircraft Design Group admits, plus 25 ft. The ADG is read from the widest runway at the airport,
    /// in the same width buckets as <see cref="RunwayCrossingDetector.HoldShortDistanceForWidth"/>, with the span
    /// ceilings of AC 150/5300-13B Table 1-2. It is a whole-airport worst case: the layout carries no taxiway width
    /// or design group, so every taxiway hold-short at the airport gets the floor of its largest aircraft. The
    /// 25 ft margin is a judgement call; no FAA document gives a figure. AIM 2-3-5.b.3 asks only that the pilot stop
    /// "at a point which provides adequate clearance from an aircraft on the intersecting taxiway".
    /// </summary>
    /// <param name="widestRunwayWidthFt">Width (ft) of the airport's widest runway.</param>
    /// <returns>The floor (ft) from the holder's nose to the crossed taxiway's centreline.</returns>
    public static double WingtipClearanceFloorFt(double widestRunwayWidthFt) =>
        (AirplaneDesignGroups.MaxWingspanFt(AirplaneDesignGroups.FromRunwayWidth(widestRunwayWidthFt)) / 2.0) + 25.0;

    /// <summary>
    /// Holds a taxiway hold-short's nose at least <c>max(L/2 + 30, floor)</c> from the crossed taxiway's centreline,
    /// measured perpendicular to the centreline edges (never to the junction node). The stop is found by walking the
    /// nose back along the route from where the route meets the centreline; the centre sits half a length behind it.
    /// The walk stops at the previous junction on the route (the tail clears it) or the route's start. The stop is
    /// never forward of <paramref name="lengthSetbackStop"/>, the length + 30 ft setback from the bar's node. When
    /// the clamp leaves the nose short of the wingtip floor itself, the route carries a wingtip-clearance warning, unless the
    /// bar is a shallow merge whose capping intersection the aircraft can hold short of instead (<see cref="PlaceMergeHold"/>).
    /// </summary>
    /// <returns>
    /// <paramref name="lengthSetbackStop"/>, the stop farther back along the route that the floor asks for, or the shallow-merge
    /// stop behind the capping intersection's near edge.
    /// </returns>
    private static GroundNode ApplyWingtipClearanceFloor(
        AirportGroundLayout layout,
        TaxiRoute route,
        HoldShortPoint hs,
        double lengthFt,
        GroundNode lengthSetbackStop
    )
    {
        var path = RoutePolyline.Build(route);
        int barIndex = path.IndexOfFirstArrival(hs.NodeId);
        if ((hs.TargetName is not { } target) || (layout.Runways.Count == 0) || (barIndex < 0))
        {
            return lengthSetbackStop;
        }

        (List<(LatLon A, LatLon B)> centreline, string clearanceFrom) = FloorCentreline(
            layout,
            route,
            barIndex,
            target,
            path.Vertex(barIndex).Position
        );
        if (centreline.Count == 0)
        {
            RemovePlacementWarnings(route, target);
            string reason =
                clearanceFrom.Length == 0
                    ? $"it continues straight ahead and every other branch there lies within {StraightAheadToleranceDeg:F0}° of straight "
                        + "ahead or behind"
                    : $"no straight centreline of {clearanceFrom} within {CentrelineSearchRadiusFt:F0}ft";
            Log.LogDebug(
                "[HoldShortAnnotator] No centreline to clear for {Target} at bar node {NodeId} ({Reason}); keeping the length setback",
                target,
                hs.NodeId,
                reason
            );
            return lengthSetbackStop;
        }

        double halfFt = lengthFt / 2.0;
        double floorFt = WingtipClearanceFloorFt(layout.Runways.Max(r => r.WidthFt));
        double setbackFt = Math.Max(halfFt + TaxiwayHoldShortBufferFt, floorFt);
        double barFt = path.AlongFt(barIndex);
        double lengthSetbackCentreFt = barFt - (lengthFt + TaxiwayHoldShortBufferFt);
        double? junctionFt = PreviousJunctionFt(layout, path, barIndex, target);
        double limitCentreFt = junctionFt is { } j ? j + halfFt : 0.0;
        double meetFt = CentrelineMeetFt(path, centreline, barFt);
        double floorCentreFt = NoseWalkBackCentreFt(path, centreline, new NoseWalk(meetFt, setbackFt, halfFt, limitCentreFt));

        double centreFt = Math.Min(floorCentreFt, lengthSetbackCentreFt);
        LatLon floorCentre = path.PositionAt(centreFt);
        GroundNode stop = centreFt < lengthSetbackCentreFt ? VirtualNode.Create(floorCentre.Lat, floorCentre.Lon) : lengthSetbackStop;
        double noseFt = DistanceToCentrelineFt(path.PositionAt(centreFt + halfFt), centreline);
        var placement = new FloorPlacement
        {
            Path = path,
            BarIndex = barIndex,
            Target = target,
            HalfFt = halfFt,
            FloorFt = floorFt,
            Centreline = centreline,
            CappedCentreFt = centreFt,
        };
        if ((noseFt < floorFt - 0.5) && (PlaceMergeHold(layout, route, hs, placement) is { } mergeStop))
        {
            return mergeStop;
        }

        ReplaceWingtipWarning(route, target, clearanceFrom, noseFt, floorFt);
        AddShallowMergeSetbackAdvisory(route, placement, lengthSetbackCentreFt, noseFt);

        Log.LogDebug(
            "[HoldShortAnnotator] Wingtip floor for {Target} at node {NodeId} against {ClearanceFrom}: setback {SetbackFt:F0}ft "
                + "(floor {FloorFt:F0}), nose {NoseFt:F0}ft, junctionAt {JunctionFt}, centre moved back {MovedFt:F0}ft",
            target,
            hs.NodeId,
            clearanceFrom,
            setbackFt,
            floorFt,
            noseFt,
            junctionFt,
            lengthSetbackCentreFt - centreFt
        );
        return stop;
    }

    /// <summary>
    /// Keeps at most one wingtip-clearance warning per crossed taxiway on the route, current with the last placement:
    /// any earlier one is dropped (its distance may be stale), and a new one is added when the nose stops short of the
    /// wingtip floor. The floor alone decides the warning — the <c>L/2 + 30</c> part of the setback is a placement
    /// margin, not wingtip clearance, so a nose that meets the floor is clear of a crosser's wingtip.
    /// </summary>
    private static void ReplaceWingtipWarning(TaxiRoute route, string target, string clearanceFrom, double noseFt, double floorFt)
    {
        RemovePlacementWarnings(route, target);
        if (noseFt < floorFt - 0.5)
        {
            route.Warnings.Add($"{WingtipWarningPrefix(target)}{clearanceFrom} not assured ({noseFt:F0} ft)");
        }
    }

    /// <summary>
    /// Drops the route's placement warnings for the bar on <paramref name="target"/>: the wingtip-clearance warning, the
    /// shallow-merge advisory and the setback advisory, whichever branches or intersection each named.
    /// </summary>
    private static void RemovePlacementWarnings(TaxiRoute route, string target)
    {
        string wingtipPrefix = WingtipWarningPrefix(target);
        string mergePrefix = MergeAdvisoryPrefix(target);
        string setbackPrefix = SetbackAdvisoryPrefix(target);
        string mergeSuffix = MergesIntoSuffix(target);
        route.Warnings.RemoveAll(w =>
            w.StartsWith(wingtipPrefix, StringComparison.Ordinal)
            || w.StartsWith(mergePrefix, StringComparison.Ordinal)
            || (
                w.StartsWith(setbackPrefix, StringComparison.Ordinal)
                && w.Contains(SetbackAdvisoryMarker, StringComparison.Ordinal)
                && w.EndsWith(mergeSuffix, StringComparison.Ordinal)
            )
        );
    }

    /// <summary>"merges into X)" closes both shallow-merge advisories; the opening parenthesis names the arriving taxiway.</summary>
    private static string MergesIntoSuffix(string target) => $"merges into {target})";

    /// <summary>The setback advisory's opening — <c>holding short of TWY T </c> — after which the nose's distance back follows.</summary>
    private static string SetbackAdvisoryPrefix(string target) => $"holding short of TWY {target} ";

    /// <summary>Separates the setback advisory's distance from the arriving taxiway that follows it.</summary>
    private const string SetbackAdvisoryMarker = " ft back (";

    /// <summary>
    /// At a shallow merge with no intersection capping the walk-back, the stop stays where the wingtip floor against the
    /// target's own centreline is met, which can lie hundreds of feet back (the arriving taxiway diverges from the target at a
    /// few degrees). When that stop is further back than the plain <c>length + 30</c> setback and meets the floor, the route
    /// tells the instructor so, since nothing else would explain the early stop: <c>holding short of TWY W 203 ft back (W5
    /// merges into W)</c>, the nose's distance back of the merge node. A stop short of the floor carries the wingtip warning
    /// instead, and the readback stays plain.
    /// </summary>
    private static void AddShallowMergeSetbackAdvisory(TaxiRoute route, FloorPlacement p, double lengthSetbackCentreFt, double noseFt)
    {
        bool floorMet = noseFt >= p.FloorFt - 0.5;
        bool behindLengthSetback = p.CappedCentreFt < lengthSetbackCentreFt - 0.5;
        if (!floorMet || !behindLengthSetback || !IsShallowMerge(route, p.BarIndex, p.Target))
        {
            return;
        }

        string arriving = route.Segments[p.BarIndex - 1].TaxiwayName;
        double noseBackFt = p.Path.AlongFt(p.BarIndex) - (p.CappedCentreFt + p.HalfFt);
        route.Warnings.Add($"{SetbackAdvisoryPrefix(p.Target)}{noseBackFt:F0}{SetbackAdvisoryMarker}{arriving} {MergesIntoSuffix(p.Target)}");
    }

    private static string WingtipWarningPrefix(string target) => $"holding short of TWY {target} — wingtip clearance from ";

    private static string MergeAdvisoryPrefix(string target) => $"holding short of TWY {target} before TWY ";

    /// <summary>The inputs of a wingtip-floor placement the shallow-merge hold reuses; every distance along the route in feet.</summary>
    private readonly record struct FloorPlacement
    {
        public required RoutePolyline Path { get; init; }
        public required int BarIndex { get; init; }
        public required string Target { get; init; }
        public required double HalfFt { get; init; }
        public required double FloorFt { get; init; }
        public required List<(LatLon A, LatLon B)> Centreline { get; init; }

        /// <summary>The centre of the capped stop the merge hold would replace.</summary>
        public required double CappedCentreFt { get; init; }
    }

    /// <summary>
    /// At a shallow merge whose wingtip-floor walk-back an earlier junction caps short of the floor, the stop that holds short
    /// of that junction's whole intersection instead: centre half a length behind the intersection's near edge (its
    /// farthest-back route node), with no further setback and no floor held against the intersection's taxiway, which is not
    /// a hold-short target. Sets <see cref="HoldShortPoint.MergeHold"/> and the instructor advisory. Null, leaving the
    /// capped stop, when the bar is no shallow merge, the merge hold was declined at arming, the route has no such
    /// junction, the stop is not behind the capped stop (a long aircraft whose length setback already stands behind the
    /// intersection is never moved forward toward the target), or the stop lies behind the segment the aircraft is on.
    /// </summary>
    private static GroundNode? PlaceMergeHold(AirportGroundLayout layout, TaxiRoute route, HoldShortPoint hs, FloorPlacement p)
    {
        if (hs.MergeHoldDeclined || !IsShallowMerge(route, p.BarIndex, p.Target))
        {
            return null;
        }

        string arriving = route.Segments[p.BarIndex - 1].TaxiwayName;
        if (
            (PreviousJunctionIndex(layout, p.Path, p.BarIndex, p.Target) is not { } junctionIndex)
            || (CappingIntersection(layout, p.Path, junctionIndex, [arriving, p.Target]) is not { } intersection)
        )
        {
            return null;
        }

        double noseAtEdgeFt = p.Path.AlongFt(intersection.NearEdgeIndex);
        double centreFt = noseAtEdgeFt - p.HalfFt;
        int onIndex = Math.Clamp(route.CurrentSegmentIndex, 0, p.BarIndex);
        if ((centreFt >= p.CappedCentreFt) || (centreFt < p.Path.AlongFt(onIndex)))
        {
            return null;
        }

        hs.MergeHold = new MergeHold(arriving, intersection.Taxiway);
        double noseFt = DistanceToCentrelineFt(p.Path.PositionAt(noseAtEdgeFt), p.Centreline);
        RemovePlacementWarnings(route, p.Target);
        string advisory = $"{MergeAdvisoryPrefix(p.Target)}{intersection.Taxiway} ({arriving} merges into {p.Target})";
        route.Warnings.Add(noseFt < p.FloorFt - 0.5 ? $"{advisory} — wingtip clearance from {p.Target} not assured ({noseFt:F0} ft)" : advisory);

        LatLon centre = p.Path.PositionAt(centreFt);
        Log.LogDebug(
            "[HoldShortAnnotator] Shallow merge into {Target} at node {NodeId}: holding on {Arriving} before {Before}, nose at node {EdgeNodeId} "
                + "{NoseFt:F0}ft from {Target} (floor {FloorFt:F0})",
            p.Target,
            hs.NodeId,
            arriving,
            intersection.Taxiway,
            p.Path.Vertex(intersection.NearEdgeIndex).Id,
            noseFt,
            p.Target,
            p.FloorFt
        );
        return VirtualNode.Create(centre.Lat, centre.Lon);
    }

    /// <summary>The intersection that capped a walk-back: the route index of its near edge and its taxiway name.</summary>
    private readonly record struct CappedIntersection(int NearEdgeIndex, string Taxiway);

    /// <summary>
    /// The whole intersection the junction at route index <paramref name="junctionIndex"/> belongs to: the taxiways that
    /// make it a junction (every name on its edges other than <paramref name="ownNames"/>, ramp and runway names), and the
    /// run of route nodes from it back that touch one of those taxiways (its centre and the fillet tangents either side).
    /// The run stops at the first node that touches none, so a following junction of other taxiways (a ramp fillet pair)
    /// never joins it. The intersection is named by one taxiway, never a joined list a pilot could not say: of the crossing
    /// taxiways with a straight edge at any node of that run (in practice its centre, where the crossing taxiway's own
    /// line passes through), the first in ordinal order; with none, the first of the fillet names in ordinal order.
    /// </summary>
    private static CappedIntersection? CappingIntersection(AirportGroundLayout layout, RoutePolyline path, int junctionIndex, string[] ownNames)
    {
        if (!layout.Nodes.TryGetValue(path.Vertex(junctionIndex).Id, out GroundNode? junction))
        {
            return null;
        }

        var crossing = new SortedSet<string>(
            junction.Edges.SelectMany(EdgeNames).Where(n => IsCrossingName(n, ownNames)),
            StringComparer.OrdinalIgnoreCase
        );
        if (crossing.Count == 0)
        {
            return null;
        }

        int nearIndex = junctionIndex;
        var throughCentre = new SortedSet<string>(StringComparer.Ordinal);
        for (int i = junctionIndex; i >= 0; i--)
        {
            if (!layout.Nodes.TryGetValue(path.Vertex(i).Id, out GroundNode? node) || !node.Edges.Any(e => crossing.Any(e.MatchesTaxiway)))
            {
                break;
            }

            nearIndex = i;
            throughCentre.UnionWith(node.Edges.OfType<GroundEdge>().Select(e => e.TaxiwayName).Where(crossing.Contains));
        }

        IEnumerable<string> candidates = throughCentre.Count > 0 ? throughCentre : crossing.Order(StringComparer.Ordinal);
        string name = candidates.First();
        return new CappedIntersection(nearIndex, name);
    }

    private static IEnumerable<string> EdgeNames(IGroundEdge edge) =>
        edge switch
        {
            GroundArc arc => arc.TaxiwayNames,
            _ => [edge.TaxiwayName],
        };

    private static bool IsCrossingName(string name, string[] ownNames) =>
        !ownNames.Contains(name, StringComparer.OrdinalIgnoreCase)
        && !string.Equals(name, "RAMP", StringComparison.OrdinalIgnoreCase)
        && !name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase);

    /// <summary>How far (ft) a branch's centreline is followed from the bar's node when the floor is held against it.</summary>
    private const double BranchWalkFt = 500.0;

    /// <summary>
    /// The centreline of one branch leaving <paramref name="node"/> by <paramref name="first"/>: that edge and the straight
    /// edges of the same taxiway that carry it on, for <see cref="BranchWalkFt"/>. Built from the branch's own edges rather
    /// than by name, so a branch named like the taxiway the aircraft arrives on never measures against the aircraft's own line.
    /// </summary>
    private static List<(LatLon A, LatLon B)> BranchCentreline(GroundEdge first, GroundNode node)
    {
        var pieces = new List<(LatLon A, LatLon B)>();
        GroundEdge? edge = first;
        GroundNode from = node;
        double walkedFt = 0.0;
        while ((edge is not null) && (walkedFt < BranchWalkFt))
        {
            var points = new List<LatLon> { edge.Nodes[0].Position };
            points.AddRange(edge.IntermediatePoints.Select(p => new LatLon(p.Lat, p.Lon)));
            points.Add(edge.Nodes[1].Position);
            for (int i = 0; i + 1 < points.Count; i++)
            {
                pieces.Add((points[i], points[i + 1]));
            }

            walkedFt += edge.DistanceNm * GeoMath.FeetPerNm;
            GroundEdge current = edge;
            from = current.OtherNode(from);
            edge = from.Edges.OfType<GroundEdge>().FirstOrDefault(e => !ReferenceEquals(e, current) && e.MatchesTaxiway(current.TaxiwayName));
        }

        return pieces;
    }

    /// <summary>A branch leaving the bar's node within this many degrees of the arriving heading continues straight ahead.</summary>
    private const double StraightAheadToleranceDeg = 20.0;

    /// <summary>
    /// The centreline the wingtip floor is measured against, and the taxiway name(s) a warning names. Normally the
    /// crossed taxiway's own. When the target continues the route's taxiway straight ahead at the bar's node, an aircraft
    /// on it is ahead, not crossing (AIM 2-3-5.b.3 speaks of "an aircraft on the intersecting taxiway"), so the floor is
    /// held against the node's other branches instead, those not straight ahead or straight behind; with none, the
    /// centreline is empty and the plain length setback stands. A shallow merge (<see cref="IsShallowMerge(TaxiRoute, int, string)"/>)
    /// keeps the target's own centreline: its leg behind runs alongside the arriving taxiway.
    /// </summary>
    private static (List<(LatLon A, LatLon B)> Centreline, string ClearanceFrom) FloorCentreline(
        AirportGroundLayout layout,
        TaxiRoute route,
        int barIndex,
        string target,
        LatLon anchor
    )
    {
        if (BranchesBesideStraightAhead(route, barIndex, target) is not { } branches)
        {
            return (CrossedCentreline(layout, target, anchor), target);
        }

        GroundNode node = route.Segments[barIndex - 1].Edge.ToNode;
        var centreline = new List<(LatLon A, LatLon B)>();
        foreach (GroundEdge branch in branches)
        {
            centreline.AddRange(BranchCentreline(branch, node));
        }

        string names = string.Join(
            "/",
            branches.Select(b => b.TaxiwayName).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
        );
        return (centreline, names);
    }

    /// <summary>
    /// A target-named edge straight behind that fans off the arriving line by less than this (deg) is the same line drawn
    /// under two names (FLL A/A1, MIA Q2/M2, IAH WA), not a merge.
    /// </summary>
    private const double CoincidentFanDeg = 1.0;

    /// <summary>
    /// When <paramref name="target"/> leaves the bar's node straight ahead (within <see cref="StraightAheadToleranceDeg"/>
    /// of the arriving heading), the node's other straight, non-ramp taxiway edges that are neither straight
    /// ahead nor straight behind; null when the target does not continue straight ahead there, or when the node is a
    /// shallow merge (<see cref="IsShallowMerge(TaxiRoute, int, string)"/>), whose floor is held against the target's own centreline.
    /// </summary>
    private static List<GroundEdge>? BranchesBesideStraightAhead(TaxiRoute route, int barIndex, string target)
    {
        if ((ArrivingBranches(route, barIndex) is not { } branches) || !ContinuesStraightAhead(branches, target) || IsShallowMerge(branches, target))
        {
            return null;
        }

        return [.. branches.Where(b => (b.OffDeg > StraightAheadToleranceDeg) && (b.OffDeg < 180.0 - StraightAheadToleranceDeg)).Select(b => b.Edge)];
    }

    /// <summary>
    /// True when the route arrives at the bar's node on a taxiway that merges into <paramref name="target"/> at a shallow
    /// angle: the target continues straight ahead, and another target edge lies within <see cref="StraightAheadToleranceDeg"/>
    /// of straight behind but off the arriving line by at least <see cref="CoincidentFanDeg"/> (OAK D into C at 5.7°).
    /// An aircraft on the target's leg behind then passes alongside the holder's nose.
    /// </summary>
    private static bool IsShallowMerge(TaxiRoute route, int barIndex, string target) =>
        (ArrivingBranches(route, barIndex) is { } branches) && ContinuesStraightAhead(branches, target) && IsShallowMerge(branches, target);

    private static bool IsShallowMerge(List<(GroundEdge Edge, double OffDeg)> branches, string target) =>
        branches.Any(b => b.Edge.MatchesTaxiway(target) && (b.OffDeg >= 180.0 - StraightAheadToleranceDeg) && (b.OffDeg <= 180.0 - CoincidentFanDeg));

    private static bool ContinuesStraightAhead(List<(GroundEdge Edge, double OffDeg)> branches, string target) =>
        branches.Any(b => b.Edge.MatchesTaxiway(target) && (b.OffDeg <= StraightAheadToleranceDeg));

    /// <summary>
    /// The bar's node's straight, non-ramp, non-runway edges with each one's angle off the arriving heading; null when the
    /// bar is not reached by a route segment.
    /// </summary>
    private static List<(GroundEdge Edge, double OffDeg)>? ArrivingBranches(TaxiRoute route, int barIndex)
    {
        if ((barIndex < 1) || (barIndex > route.Segments.Count))
        {
            return null;
        }

        DirectionalEdge arriving = route.Segments[barIndex - 1].Edge;
        GroundNode node = arriving.ToNode;
        double arrivingDeg = arriving.ArrivalBearing;
        return
        [
            .. node
                .Edges.OfType<GroundEdge>()
                .Where(e => !e.IsRamp && !e.IsRunwayCenterline)
                .Select(e => (Edge: e, OffDeg: GeoMath.AbsBearingDifference(arrivingDeg, LeavingBearingDeg(e, node)))),
        ];
    }

    /// <summary>The bearing a straight edge leaves <paramref name="node"/> on: toward its first shape point, else its far node.</summary>
    private static double LeavingBearingDeg(GroundEdge edge, GroundNode node)
    {
        bool fromStart = edge.Nodes[0].Id == node.Id;
        LatLon next =
            edge.IntermediatePoints.Count == 0 ? edge.OtherNode(node).Position
            : fromStart ? new LatLon(edge.IntermediatePoints[0].Lat, edge.IntermediatePoints[0].Lon)
            : new LatLon(edge.IntermediatePoints[^1].Lat, edge.IntermediatePoints[^1].Lon);
        return GeoMath.BearingTo(node.Position, next);
    }

    /// <summary>The nose walk-back's inputs, all along-route distances or setbacks in feet.</summary>
    private readonly record struct NoseWalk(double MeetFt, double SetbackFt, double HalfFt, double LimitCentreFt);

    /// <summary>The straight centreline pieces of <paramref name="taxiway"/> within <see cref="CentrelineSearchRadiusFt"/> of <paramref name="anchor"/>.</summary>
    private static List<(LatLon A, LatLon B)> CrossedCentreline(AirportGroundLayout layout, string taxiway, LatLon anchor)
    {
        var pieces = new List<(LatLon A, LatLon B)>();
        foreach (GroundEdge edge in layout.Edges)
        {
            if (!string.Equals(edge.TaxiwayName, taxiway, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var points = new List<LatLon> { edge.Nodes[0].Position };
            points.AddRange(edge.IntermediatePoints.Select(p => new LatLon(p.Lat, p.Lon)));
            points.Add(edge.Nodes[1].Position);
            for (int i = 0; i + 1 < points.Count; i++)
            {
                if (GeoMath.DistanceToSegmentFt(anchor, points[i], points[i + 1]) <= CentrelineSearchRadiusFt)
                {
                    pieces.Add((points[i], points[i + 1]));
                }
            }
        }

        return pieces;
    }

    private static double DistanceToCentrelineFt(LatLon point, List<(LatLon A, LatLon B)> centreline)
    {
        double best = double.PositiveInfinity;
        foreach ((LatLon a, LatLon b) in centreline)
        {
            best = Math.Min(best, GeoMath.DistanceToSegmentFt(point, a, b));
        }

        return best;
    }

    /// <summary>
    /// Where (ft along the route) the route meets the crossed centreline: the closest approach within
    /// <see cref="CentrelineMeetSearchAheadFt"/> past the bar's node, which is the crossing itself when the route
    /// carries on over the taxiway.
    /// </summary>
    private static double CentrelineMeetFt(RoutePolyline path, List<(LatLon A, LatLon B)> centreline, double barFt)
    {
        double endFt = Math.Min(path.LengthFt, barFt + CentrelineMeetSearchAheadFt);
        double bestFt = barFt;
        double bestDistFt = double.PositiveInfinity;
        for (double alongFt = barFt; alongFt <= endFt; alongFt += NoseWalkStepFt)
        {
            double distFt = DistanceToCentrelineFt(path.PositionAt(alongFt), centreline);
            if (distFt < bestDistFt)
            {
                bestDistFt = distFt;
                bestFt = alongFt;
            }

            if (distFt < NoseWalkStepFt)
            {
                break;
            }
        }

        return bestFt;
    }

    /// <summary>
    /// Walks the nose back from the meeting point until it is <c>SetbackFt</c> from the centreline and returns the
    /// centre (half a length behind the nose), or <c>LimitCentreFt</c> when the walk reaches the limit first.
    /// </summary>
    private static double NoseWalkBackCentreFt(RoutePolyline path, List<(LatLon A, LatLon B)> centreline, NoseWalk walk)
    {
        for (double noseFt = walk.MeetFt; (noseFt - walk.HalfFt) > walk.LimitCentreFt; noseFt -= NoseWalkStepFt)
        {
            if (DistanceToCentrelineFt(path.PositionAt(noseFt), centreline) >= walk.SetbackFt)
            {
                return noseFt - walk.HalfFt;
            }
        }

        return walk.LimitCentreFt;
    }

    /// <summary>
    /// The along-route position (ft) of the last junction before the bar's node, or null when the route has none.
    /// A junction is a runway hold-short node (never back onto a runway) or a node with more than two edges that
    /// are neither ramp connectors nor part of the crossed taxiway (a fillet arc onto it included).
    /// </summary>
    private static double? PreviousJunctionFt(AirportGroundLayout layout, RoutePolyline path, int barIndex, string target) =>
        PreviousJunctionIndex(layout, path, barIndex, target) is { } i ? path.AlongFt(i) : null;

    /// <summary>The route index of the junction <see cref="PreviousJunctionFt"/> finds, or null when the route has none.</summary>
    private static int? PreviousJunctionIndex(AirportGroundLayout layout, RoutePolyline path, int barIndex, string target)
    {
        for (int i = barIndex - 1; i >= 0; i--)
        {
            if (!layout.Nodes.TryGetValue(path.Vertex(i).Id, out GroundNode? node))
            {
                continue;
            }

            bool isJunction = (node.Type == GroundNodeType.RunwayHoldShort) || (node.Edges.Count(e => !e.IsRamp && !e.MatchesTaxiway(target)) > 2);
            if (isJunction)
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>A taxi route as a polyline of node positions, with the along-route distance (ft) of each node.</summary>
    private sealed class RoutePolyline
    {
        private readonly List<GroundNode> _vertices = [];
        private readonly List<double> _alongFt = [];

        public double LengthFt => _alongFt.Count == 0 ? 0.0 : _alongFt[^1];

        public static RoutePolyline Build(TaxiRoute route)
        {
            var path = new RoutePolyline();
            foreach (TaxiRouteSegment seg in route.Segments)
            {
                if (path._vertices.Count == 0)
                {
                    path._vertices.Add(seg.Edge.FromNode);
                    path._alongFt.Add(0.0);
                }

                double legFt = GeoMath.DistanceNm(path._vertices[^1].Position, seg.Edge.ToNode.Position) * GeoMath.FeetPerNm;
                path._vertices.Add(seg.Edge.ToNode);
                path._alongFt.Add(path._alongFt[^1] + legFt);
            }

            return path;
        }

        public GroundNode Vertex(int index) => _vertices[index];

        public double AlongFt(int index) => _alongFt[index];

        /// <summary>The first vertex after the start that is <paramref name="nodeId"/>, as <see cref="VirtualNode.OffsetBefore"/> finds it; -1 when none is.</summary>
        public int IndexOfFirstArrival(int nodeId)
        {
            for (int i = 1; i < _vertices.Count; i++)
            {
                if (_vertices[i].Id == nodeId)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>The point <paramref name="alongFt"/> along the route, clamped to the route's two ends.</summary>
        public LatLon PositionAt(double alongFt)
        {
            if (_vertices.Count == 0)
            {
                throw new InvalidOperationException("PositionAt on an empty route polyline");
            }

            for (int i = 1; i < _vertices.Count; i++)
            {
                if (alongFt <= _alongFt[i])
                {
                    double intoLegFt = Math.Max(0.0, alongFt - _alongFt[i - 1]);
                    double bearing = GeoMath.BearingTo(_vertices[i - 1].Position, _vertices[i].Position);
                    (double lat, double lon) = GeoMath.ProjectPointRaw(
                        _vertices[i - 1].Position.Lat,
                        _vertices[i - 1].Position.Lon,
                        bearing,
                        intoLegFt / GeoMath.FeetPerNm
                    );
                    return new LatLon(lat, lon);
                }
            }

            return _vertices[^1].Position;
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
            foreach (TaxiRouteSegment seg in route.Segments)
            {
                if (seg.ToNodeId == currentId)
                {
                    approachId = seg.FromNodeId;
                    break;
                }
            }

            if (
                approachId < 0
                || !layout.Nodes.TryGetValue(approachId, out GroundNode? approachNode)
                || !layout.Nodes.TryGetValue(currentId, out GroundNode? curNode)
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

    internal static bool HoldShortExists(List<HoldShortPoint> holdShorts, int nodeId)
    {
        foreach (HoldShortPoint hs in holdShorts)
        {
            if (hs.NodeId == nodeId)
            {
                return true;
            }
        }

        return false;
    }
}
