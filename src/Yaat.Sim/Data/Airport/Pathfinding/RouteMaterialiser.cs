using Yaat.Sim.Commands;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Data.Airport.Pathfinding;

/// <summary>
/// Phase 3: converts a flat list of directed edges plus search context into a <see cref="TaxiRoute"/>.
/// Annotates hold-short points, truncates trailing pavement, sets destination strings, and emits warnings.
/// </summary>
public static class RouteMaterialiser
{
    /// <summary>
    /// The controller's note for a movement-area taxiway the route drives that the clearance did not name:
    /// <c>taxiing via T — not in the route issued</c>.
    /// </summary>
    public static string NotInRouteIssuedWarning(string taxiway) => $"taxiing via {taxiway} — not in the route issued";

    /// <summary>
    /// The controller's note for a one-way lane the resolver implied into a gate or spot clearance as the connector the
    /// one-way rules leave the aircraft (<see cref="SegmentExpander"/>'s one-way lane extension): <c>M1 not in clearance</c>.
    /// The readback names the lane, since the aircraft drives it.
    /// </summary>
    public static string NotInClearanceWarning(string taxiway) => $"{taxiway} not in clearance";

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
        List<TaxiRouteSegment> segments = BuildSegments(edges);

        // Step 2: Annotate hold-short points.
        List<HoldShortPoint> holdShorts = AnnotateHoldShorts(segments, ctx);

        // Step 3: Truncate to one segment past the last required stop.
        (int TruncateAt, int HoldShortNodeId)? runwaySurfaceEntry = FindDestinationRunwaySurfaceEntry(segments, holdShorts, ctx);
        int truncateAt = runwaySurfaceEntry?.TruncateAt ?? FindTruncationIndex(segments, holdShorts, ctx);
        if (runwaySurfaceEntry is { TruncateAt: < 0 } entry)
        {
            segments = [];
            holdShorts = [.. holdShorts.Where(hs => hs.NodeId == entry.HoldShortNodeId)];
        }
        else if (truncateAt >= 0 && truncateAt < segments.Count - 1)
        {
            segments = [.. segments.Take(truncateAt + 1)];

            // Keep the route's own start node: a hold-short the aircraft is already parked at is never
            // any segment's ToNodeId, so filtering on ToNodeId alone would silently drop it.
            int startNodeId = segments.Count > 0 ? segments[0].FromNodeId : -1;
            holdShorts =
            [
                .. holdShorts.Where(hs =>
                    (hs.NodeId == startNodeId) || segments.Any(s => s.ToNodeId == hs.NodeId) || (hs.NodeId == runwaySurfaceEntry?.HoldShortNodeId)
                ),
            ];
        }

        // Step 4: Warnings for unauthorized letter taxiways traversed, plus informative
        // notifications for mandatory connector insertions.
        List<string> warnings = BuildWarnings(segments, ctx, insertions);

        // An HS target that bound nothing — no runway bar, no adjacent-taxiway point — was silently
        // dropped; tell the controller instead of letting them believe the aircraft will hold. A
        // plain RunwayCrossing point does NOT satisfy the check: a located runway target whose
        // location taxiway doesn't match the crossing's node (HS 28R@E when the route crosses 28R
        // on J) leaves the crossing un-promoted, and AutoCross may then clear it — the one case
        // where a name match must still warn. DestinationRunway points still satisfy it (the
        // explicit-HS-of-own-destination no-op).
        foreach (HoldShortTarget target in ctx.ExplicitHoldShorts)
        {
            if (FindBoundHoldShort(holdShorts, target) is null)
            {
                string display = target.ToCanonical();
                if (target.IsSpot)
                {
                    warnings.Add($"HS {display} not applied — the route never reaches {target.ToNatural()}");
                }
                else
                {
                    warnings.Add(
                        ctx.Layout.TryGetRunwayCenterlineName(target.Target, out _)
                            ? $"HS {display} not applied — no runway {target.Target} hold line on the route"
                            : $"HS {display} not applied — the route never reaches {display}"
                    );
                }
            }
        }

        // A bare taxiway HS target that is ALSO a cleared path element can bind at the crossing the
        // route STARTS from — the aircraft holds immediately and never taxis (the issue #358 report:
        // "TAXI C D J HS C" from an aircraft already short of C). The binding is kept — the located
        // form is the disambiguator — but the instructor is told which crossing bound and how to
        // pick a later one.
        if (segments.Count > 0)
        {
            foreach (HoldShortTarget target in ctx.ExplicitHoldShorts)
            {
                if (
                    target.IsSpot
                    || target.OnTaxiway is not null
                    || ctx.Layout.TryGetRunwayCenterlineName(target.Target, out _)
                    || !ctx.WaypointSequence.Contains(target.Target, StringComparer.OrdinalIgnoreCase)
                )
                {
                    continue;
                }

                bool boundAtStart = holdShorts.Exists(h =>
                    (h.Reason == HoldShortReason.ExplicitHoldShort)
                    && string.Equals(h.TargetName, target.Target, StringComparison.OrdinalIgnoreCase)
                    && ((h.NodeId == segments[0].FromNodeId) || (h.NodeId == segments[0].ToNodeId))
                );
                if (boundAtStart)
                {
                    warnings.Add(
                        $"HS {target.Target} holds at the first {target.Target} crossing, at the start of the route — "
                            + $"use {target.Target}@<taxiway> to hold at a later crossing"
                    );
                }
            }
        }

        return new TaxiRoute
        {
            Segments = segments,
            HoldShortPoints = holdShorts,
            Warnings = warnings,
            MandatoryConnectorCount = insertions.Count,
            ClearOfRunwayEnd = segments.Count > 0 ? ctx.ClearOfRunwayEnds.At(segments[^1].ToNodeId) : null,
            CurrentSegmentIndex = 0,
            DestinationParking = ctx.Destination.ParkingName,
            DestinationSpot = ctx.Destination.SpotName,
        };
    }

    /// <summary>
    /// The hold-short point that satisfies <paramref name="target"/>: an
    /// <see cref="HoldShortReason.ExplicitHoldShort"/> or <see cref="HoldShortReason.DestinationRunway"/>
    /// point named for the target (runway ids by <see cref="RunwayIdentifier.Contains"/>, so a combined
    /// "28R/10L" bar satisfies "28R"). A plain <see cref="HoldShortReason.RunwayCrossing"/> never
    /// satisfies it — a located runway target whose location taxiway did not match leaves the crossing
    /// un-promoted, which must still read as unbound. Null when nothing on the route binds the target.
    /// Shared by the "HS not applied" warning and <c>GroundCommandHandler</c>'s as-cleared gate so the
    /// two never disagree on what "bound" means.
    /// </summary>
    public static HoldShortPoint? FindBoundHoldShort(IReadOnlyList<HoldShortPoint> holdShorts, HoldShortTarget target)
    {
        foreach (HoldShortPoint point in holdShorts)
        {
            if (
                point.TargetName is { } name
                && (point.Reason != HoldShortReason.RunwayCrossing)
                && (
                    string.Equals(name, target.MatchKey, StringComparison.OrdinalIgnoreCase)
                    || (!target.IsSpot && RunwayIdentifier.Parse(name).Contains(target.Target))
                )
            )
            {
                return point;
            }
        }

        return null;
    }

    private static List<TaxiRouteSegment> BuildSegments(IReadOnlyList<DirectionalEdge> edges)
    {
        var segments = new List<TaxiRouteSegment>(edges.Count);
        foreach (DirectionalEdge edge in edges)
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
        var taxiwayHoldShortTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Runways the controller cleared the aircraft to taxi ALONG (named as path waypoints). Entering
        // such a runway is authorized, so no hold-short is placed at its boundary — the route flows
        // straight onto and along it. A DIFFERENT runway merely crossed is not in this set and still
        // pairs/annotates below. The destination runway (handled first) keeps its own terminus stop.
        var clearedRunways = new List<string>();
        foreach (string token in ctx.WaypointSequence)
        {
            if (ctx.Layout.TryGetRunwayCenterlineName(token, out _))
            {
                clearedRunways.Add(token);
            }
        }

        // Entry/exit pairing by encounter order (ported from HoldShortAnnotator): the first
        // RunwayHoldShort node for a runway is the entry side (annotate); the second distinct
        // node for that runway is the exit side of the same crossing (skip). The destination
        // runway is exempt — it is the route terminus, always annotated. When the route begins
        // ON a bar (e.g. re-routed from a runway hold-short), that bar is the entry side.
        var enteredRunways = new Dictionary<RunwayIdentifier, int>();
        AnnotateStartCrossing(segments, ctx, clearedRunways, holdShorts, seen, enteredRunways);

        foreach (TaxiRouteSegment seg in segments)
        {
            int nodeId = seg.ToNodeId;
            if (!seen.Add(nodeId))
            {
                continue;
            }

            if (!ctx.Layout.Nodes.TryGetValue(nodeId, out GroundNode? node))
            {
                continue;
            }

            if (node.Type == GroundNodeType.RunwayHoldShort && node.RunwayId is { } runwayId)
            {
                // Precedence: DestinationRunway (taxiing TO this runway — hold for departure, never
                // auto-cross) > ExplicitHoldShort (controller named it in the HS list) > RunwayCrossing.
                // Reciprocal matching: a node's RunwayId is the combined "28R/10L"; clearances name a
                // single end ("28R"), so match via RunwayIdentifier.Contains, not literal string equality.
                if (ctx.Destination.Kind == DestinationKind.Runway && ctx.Destination.RunwayId is { } destRwy && runwayId.Contains(destRwy))
                {
                    holdShorts.Add(
                        new HoldShortPoint
                        {
                            NodeId = nodeId,
                            Reason = HoldShortReason.DestinationRunway,
                            TargetName = destRwy,
                        }
                    );
                    continue;
                }

                // Cleared to taxi along this runway: no hold-short at its boundary (taxi straight
                // on) — unless the controller ALSO named it in the HS list. An explicit HS overrides
                // the straight-on entry: the aircraft holds at the entry-side bar until cleared, then
                // proceeds onto/along the runway (the exit side pairs away below like any crossing).
                if (clearedRunways.Exists(d => runwayId.Contains(d)) && !MatchesExplicitHoldShort(node, runwayId, ctx.ExplicitHoldShorts))
                {
                    continue;
                }

                // Non-destination runway: pair the crossing's entry and exit sides. The second
                // distinct hold-short node for a runway is the exit side — drop it so the aircraft
                // doesn't stop on the far side of a runway it is cleared through.
                if (enteredRunways.Remove(runwayId))
                {
                    continue;
                }

                enteredRunways[runwayId] = nodeId;

                holdShorts.Add(
                    new HoldShortPoint
                    {
                        NodeId = nodeId,
                        Reason = MatchesExplicitHoldShort(node, runwayId, ctx.ExplicitHoldShorts)
                            ? HoldShortReason.ExplicitHoldShort
                            : HoldShortReason.RunwayCrossing,
                        TargetName = runwayId.ToString(),
                    }
                );

                continue;
            }

            // Check explicit hold-shorts that target a taxiway name (non-runway). Annotate each
            // target at most once, at the first route node adjacent to it. A hold-short of a
            // taxiway the route runs ALONG (e.g. "TAXI G B HS B") is adjacent to every node on
            // that taxiway; without this per-target guard the summary echoes "HS B" once per node
            // ("G B HS B HS B HS B …"). Mirrors the per-runway enteredRunways guard above.
            // A located target ("HS C@J") additionally requires the node to sit ON the location
            // taxiway, so only the J-side crossing of C binds — not an earlier crossing the route
            // happens to pass first (issue #358). A spot target ("HS $17") binds the named Spot node
            // itself — the route passes through it — and never matches by taxiway (issue #394).
            foreach (HoldShortTarget holdShortTarget in ctx.ExplicitHoldShorts)
            {
                if (taxiwayHoldShortTargets.Contains(holdShortTarget.ToCanonical()))
                {
                    continue;
                }

                if (holdShortTarget.IsSpot)
                {
                    if (IsSpotNode(node, holdShortTarget.Target))
                    {
                        holdShorts.Add(
                            new HoldShortPoint
                            {
                                NodeId = nodeId,
                                Reason = HoldShortReason.ExplicitHoldShort,
                                TargetName = holdShortTarget.MatchKey,
                            }
                        );
                        taxiwayHoldShortTargets.Add(holdShortTarget.ToCanonical());
                    }

                    continue;
                }

                if (holdShortTarget.OnTaxiway is { } onTaxiway && !NodeIncidentToTaxiway(node, onTaxiway))
                {
                    continue;
                }

                foreach (IGroundEdge edge in node.Edges)
                {
                    if (edge.MatchesTaxiway(holdShortTarget.Target))
                    {
                        holdShorts.Add(
                            new HoldShortPoint
                            {
                                NodeId = nodeId,
                                Reason = HoldShortReason.ExplicitHoldShort,
                                TargetName = holdShortTarget.Target,
                            }
                        );
                        taxiwayHoldShortTargets.Add(holdShortTarget.ToCanonical());
                        break;
                    }
                }
            }
        }

        return holdShorts;
    }

    /// <summary>True when <paramref name="node"/> is the taxi spot named <paramref name="spotName"/>.</summary>
    internal static bool IsSpotNode(GroundNode node, string spotName) =>
        node.Type == GroundNodeType.Spot && string.Equals(node.Name, spotName, StringComparison.OrdinalIgnoreCase);

    private static (int TruncateAt, int HoldShortNodeId)? FindDestinationRunwaySurfaceEntry(
        List<TaxiRouteSegment> segments,
        List<HoldShortPoint> holdShorts,
        SearchContext ctx
    )
    {
        if ((ctx.Destination.Kind != DestinationKind.Runway) || (ctx.Destination.RunwayId is not { } runwayId))
        {
            return null;
        }

        for (int i = 0; i < segments.Count; i++)
        {
            if (SegmentEndsAtDestinationHoldShort(segments[i], runwayId, ctx))
            {
                return null;
            }

            if (!SegmentTraversesRunwaySurface(segments[i], runwayId))
            {
                continue;
            }

            int holdShortNodeId = segments[i].FromNodeId;
            if (!holdShorts.Any(hs => (hs.NodeId == holdShortNodeId) && (hs.Reason == HoldShortReason.DestinationRunway)))
            {
                holdShorts.Add(
                    new HoldShortPoint
                    {
                        NodeId = holdShortNodeId,
                        Reason = HoldShortReason.DestinationRunway,
                        TargetName = runwayId,
                    }
                );
            }

            return (i - 1, holdShortNodeId);
        }

        return null;
    }

    private static bool SegmentEndsAtDestinationHoldShort(TaxiRouteSegment segment, string runwayId, SearchContext ctx)
    {
        return (ctx.Layout.Nodes.TryGetValue(segment.ToNodeId, out GroundNode? node))
            && (node.Type == GroundNodeType.RunwayHoldShort)
            && (node.RunwayId is { } nodeRunwayId)
            && (nodeRunwayId.Contains(runwayId));
    }

    private static bool SegmentTraversesRunwaySurface(TaxiRouteSegment segment, string runwayId) =>
        (segment.Edge.Edge.IsRunwayCenterline) && (segment.Edge.Edge.MatchesRunway(runwayId));

    /// <summary>
    /// When the route begins ON a runway hold-short bar and goes on to cross that runway, annotate
    /// the start bar as the crossing's entry side and seed it so the far-side bar is dropped as its
    /// exit pair. The aircraft is already standing at the holding position, so that bar — not the
    /// one across the runway — is where "hold short of X" applies; binding the far bar instead would
    /// send the aircraft over the runway to reach its own hold-short (issue #316).
    ///
    /// A route that begins at a single-sided exit bar (just vacated the runway and never crosses it
    /// again) is not a crossing: its next hold-short is a genuine new entry. Its start bar is still the route's hold for
    /// that runway while the route's first leg leads toward the runway and the aircraft has not yet passed the bar along it
    /// (<see cref="HoldShortAnnotator.StartBarHolds"/>, from <see cref="SearchContext.StartPose"/>); a route leading away
    /// from the runway is leaving it, and the bar marks where it is clear, not a hold.
    /// </summary>
    private static void AnnotateStartCrossing(
        List<TaxiRouteSegment> segments,
        SearchContext ctx,
        List<string> clearedRunways,
        List<HoldShortPoint> holdShorts,
        HashSet<int> seen,
        Dictionary<RunwayIdentifier, int> enteredRunways
    )
    {
        if (segments.Count == 0)
        {
            return;
        }

        int startNodeId = segments[0].FromNodeId;
        if (
            !ctx.Layout.Nodes.TryGetValue(startNodeId, out GroundNode? startNode)
            || startNode.Type != GroundNodeType.RunwayHoldShort
            || startNode.RunwayId is not { } startRwyId
        )
        {
            return;
        }

        bool crossesAfterStart = HoldShortAnnotator.RouteCrossesRunwayAfterStart(ctx.Layout, segments, startNodeId, startRwyId);
        bool barAhead = (ctx.StartPose is { } pose) && HoldShortAnnotator.StartBarHolds(ctx.Layout, startNode, segments[0].Edge.ToNode, pose);
        if (!crossesAfterStart && !barAhead)
        {
            return;
        }

        // Same precedence as the main loop, which owns both of these cases: the destination runway's
        // bar is the route terminus, and a runway the aircraft is cleared to taxi ALONG gets no bar.
        if (ctx.Destination.Kind == DestinationKind.Runway && ctx.Destination.RunwayId is { } destRwy && startRwyId.Contains(destRwy))
        {
            return;
        }

        if (
            clearedRunways.Exists(designator => startRwyId.Contains(designator))
            && !MatchesExplicitHoldShort(startNode, startRwyId, ctx.ExplicitHoldShorts)
        )
        {
            return;
        }

        // Only a bar the route goes on to cross pairs with the far bar; a start bar the aircraft is leaving the runway by
        // pairs with nothing, so a later bar of the same runway is a new entry.
        seen.Add(startNodeId);
        if (crossesAfterStart)
        {
            enteredRunways[startRwyId] = startNodeId;
        }

        holdShorts.Add(
            new HoldShortPoint
            {
                NodeId = startNodeId,
                Reason = MatchesExplicitHoldShort(startNode, startRwyId, ctx.ExplicitHoldShorts)
                    ? HoldShortReason.ExplicitHoldShort
                    : HoldShortReason.RunwayCrossing,
                TargetName = startRwyId.ToString(),
            }
        );
    }

    /// <summary>
    /// True when any controller-named explicit hold-short target matches this runway bar,
    /// reciprocal-aware (a "28R" hold matches a node whose RunwayId is "28R/10L"). A located
    /// target ("28R@J") additionally requires the bar node to sit on the location taxiway, so
    /// only the crossing the controller named binds when the route meets the runway more than once.
    /// </summary>
    private static bool MatchesExplicitHoldShort(GroundNode node, RunwayIdentifier runwayId, IReadOnlySet<HoldShortTarget> explicitHoldShorts)
    {
        foreach (HoldShortTarget target in explicitHoldShorts)
        {
            // A spot target ($9) is never a runway hold-short. Without this guard its bare number
            // (the $ sigil is stripped at parse) collides with a runway whose end is that number
            // (IAH spot "9" vs runway 9/27), mislabeling the crossing ExplicitHoldShort. Mirrors the
            // IsSpot guards in FindBoundHoldShort and HoldShortAnnotator.TargetMatches.
            if (target.IsSpot)
            {
                continue;
            }

            if (!runwayId.Contains(target.Target))
            {
                continue;
            }

            if (target.OnTaxiway is { } onTaxiway && !NodeIncidentToTaxiway(node, onTaxiway))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static int FindTruncationIndex(List<TaxiRouteSegment> segments, List<HoldShortPoint> holdShorts, SearchContext ctx)
    {
        // A bare taxi the resolver ended past a crossed runway's far bar is whole: beyond a hold short the route passed before
        // the crossing, its only hold short is of the runway crossed (or a CROSS anchor at the far bar), and cutting at that
        // one would leave the aircraft's tail over the runway.
        if ((segments.Count > 0) && (ctx.ClearOfRunwayEnds.At(segments[^1].ToNodeId) is not null))
        {
            return segments.Count - 1;
        }

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

        // Runway destination: the route terminus is the lineup hold-short on the destination runway —
        // the FIRST hold-short of that runway the route reaches. A departure taxis up to its runway and
        // holds; it never crosses its own departure runway, so the near-side hold-short (first encountered)
        // is the lineup. Taking the last match instead would extend the route across the runway to the
        // far-side hold-short of the same crossing (both physical sides share the combined "28R/10L"
        // RunwayId). Match reciprocal-aware via Contains.
        int runwayDestHoldIdx = -1;
        if (ctx.Destination.RunwayId is { } runwayId && ctx.Destination.Kind == DestinationKind.Runway)
        {
            for (int i = 0; i < segments.Count; i++)
            {
                if (!ctx.Layout.Nodes.TryGetValue(segments[i].ToNodeId, out GroundNode? node))
                {
                    continue;
                }

                if (node.Type == GroundNodeType.RunwayHoldShort && node.RunwayId is { } nodeRunwayId && nodeRunwayId.Contains(runwayId))
                {
                    runwayDestHoldIdx = i;
                    break;
                }
            }
        }

        // Explicit hold-shorts. A hold-short is the route terminus only when no cleared taxiway
        // lies beyond it. "TAXI G C HS 28R" holds short of 28R while crossing from G onto C: the
        // hold-short is a stop point mid-route, not the end, so the route must cross and stop just
        // onto the last cleared taxiway C. Truncating at the hold-short would strand the aircraft
        // on the near side, dropping the crossing and all of C. When the route reaches the last
        // cleared taxiway AFTER the hold-short (crossed en route), stop AT the first segment onto
        // that taxiway — directly, with no trailing buffer, so the aircraft settles just past the
        // junction. Otherwise (the hold-short is on/after the last cleared taxiway) it is the
        // terminus — stop one segment past it like any other required stop.
        int lastClearedEntryIdx = FindLastClearedTaxiwayEntry(segments, ctx);
        int crossHoldTruncateAt = -1;
        foreach (HoldShortPoint hs in holdShorts)
        {
            if (hs.Reason != HoldShortReason.ExplicitHoldShort)
            {
                continue;
            }

            for (int i = 0; i < segments.Count; i++)
            {
                if (segments[i].ToNodeId != hs.NodeId)
                {
                    continue;
                }

                if (lastClearedEntryIdx > i)
                {
                    crossHoldTruncateAt = Math.Max(crossHoldTruncateAt, lastClearedEntryIdx);
                }
                else
                {
                    // No distinct named cleared taxiway lies beyond the hold-short. If the hold-short is a
                    // runway the cleared taxiway CROSSES (a paired far-side hold-short of the same runway
                    // lies further along the route), it is an en-route restriction, not the terminus: extend
                    // through the crossing and stop at the far-side bar so a later CROSS leaves the aircraft
                    // just clear. Truncating one segment past the near bar would strand it on the runway.
                    int farExitIdx = FindPairedFarSideRunwayHoldShortIndex(segments, ctx, hs.NodeId, i);
                    if (farExitIdx >= 0)
                    {
                        crossHoldTruncateAt = Math.Max(crossHoldTruncateAt, farExitIdx);
                    }
                    else
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

        // A runway destination ends EXACTLY at its lineup hold-short — the true terminus. No "+1" buffer
        // (that would step onto the runway), and en-route truncations never apply: an explicit hold-short
        // crossed on the way (crossHold, which would otherwise stop the route on the last cleared taxiway)
        // is moot here because the destination runway lies beyond that taxiway. Departing onto or across
        // the runway is clearance-gated by LineUp / CrossingRunway phases, never baked into the taxi route.
        if (runwayDestHoldIdx >= 0)
        {
            return Math.Min(runwayDestHoldIdx, segments.Count - 1);
        }

        // One past the last required stop; a crossed-hold stop is exact (no trailing buffer).
        int normalTruncate = lastRequiredIdx < 0 ? -1 : lastRequiredIdx + 1;
        int result = Math.Max(normalTruncate, crossHoldTruncateAt);
        if (result < 0)
        {
            return segments.Count - 1;
        }

        // A trailing #node pin is where an explicit path ends: a hold short before it is en route, so a cut that falls short
        // of the pin runs on to it.
        return Math.Min(Math.Max(result, TrailingNodePinIndex(segments, ctx)), segments.Count - 1);
    }

    /// <summary>
    /// Index of the last segment ending at the trailing <c>#node</c> pin of the waypoint sequence, or -1 when the sequence
    /// does not end in a pin or the route never reaches it.
    /// </summary>
    private static int TrailingNodePinIndex(List<TaxiRouteSegment> segments, SearchContext ctx)
    {
        if ((ctx.WaypointSequence.Count == 0) || !NodeRefToken.IsNodeReference(ctx.WaypointSequence[^1]))
        {
            return -1;
        }

        int pinNodeId = NodeRefToken.ParseNodeId(ctx.WaypointSequence[^1]);
        return segments.FindLastIndex(segment => segment.ToNodeId == pinNodeId);
    }

    /// <summary>
    /// Index of the first segment that lies purely on the last cleared taxiway (the final named
    /// waypoint), or -1 when there is no named last waypoint or it is never reached on a single-name
    /// segment. "Purely" excludes the membership junction arc onto the taxiway (e.g. a "C - G" corner
    /// arc) so the cross-and-hold stop lands on the taxiway proper — leaving the aircraft reporting
    /// the cleared taxiway as its current one rather than a transitional corner. Used by the
    /// explicit-hold-short truncation to stop one segment onto the taxiway the route crossed to reach.
    /// </summary>
    private static int FindLastClearedTaxiwayEntry(List<TaxiRouteSegment> segments, SearchContext ctx)
    {
        if (ctx.WaypointSequence.Count == 0)
        {
            return -1;
        }

        string last = ctx.WaypointSequence[^1];
        if (last.StartsWith('#'))
        {
            return -1;
        }

        for (int i = 0; i < segments.Count; i++)
        {
            if (segments[i].TaxiwayName.Equals(last, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        // Fallback: the route reached the last cleared taxiway only at its junction. With no onward
        // direction cleared, the aircraft stops AT the intersection (a terminus-junction), so no
        // segment lies purely on the taxiway. Recognize the last segment whose ToNode is incident to
        // the taxiway (the junction stop) as the entry — mirroring the node-incidence rule in
        // SegmentExpander.RouteReachesTaxiway — so the explicit-hold-short cross extension keeps the
        // crossing and stops at the junction, rather than truncating at the near-side hold-short and
        // dropping the crossed taxiway entirely. Which junction candidate the resolver commits to
        // (walk one segment onto the taxiway vs. stop at the corner) otherwise silently flips whether
        // the crossing survives truncation.
        int junctionEntryIdx = -1;
        for (int i = 0; i < segments.Count; i++)
        {
            if (NodeIncidentToTaxiway(segments[i].Edge.ToNode, last))
            {
                junctionEntryIdx = i;
            }
        }

        return junctionEntryIdx;
    }

    /// <summary>True if any edge incident to <paramref name="node"/> belongs to <paramref name="taxiwayName"/>.</summary>
    private static bool NodeIncidentToTaxiway(GroundNode node, string taxiwayName)
    {
        foreach (IGroundEdge edge in node.Edges)
        {
            if (edge.MatchesTaxiway(taxiwayName))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Index of the segment whose <c>ToNodeId</c> is the far-side (exit) hold-short of the SAME runway as
    /// the near-side runway hold-short at <paramref name="nearNodeId"/> (= <c>segments[nearIdx].ToNodeId</c>).
    /// Returns -1 when the near node is not a runway hold-short, or no later same-runway
    /// <see cref="GroundNodeType.RunwayHoldShort"/> node exists — the taxiway dead-ends at the runway rather
    /// than crossing it. Matched value-equal on the combined "28R/10L" id (both bars of one crossing share
    /// it), mirroring <c>TaxiingPhase.FindRunwayCrossingExitNode</c>. Used by the explicit-hold-short
    /// truncation so a hold-short of a runway the cleared taxiway crosses and continues past extends the
    /// route through to the exit bar instead of stopping one segment past the near bar, on the runway.
    /// </summary>
    private static int FindPairedFarSideRunwayHoldShortIndex(List<TaxiRouteSegment> segments, SearchContext ctx, int nearNodeId, int nearIdx)
    {
        if (
            !ctx.Layout.Nodes.TryGetValue(nearNodeId, out GroundNode? nearNode)
            || nearNode.Type != GroundNodeType.RunwayHoldShort
            || nearNode.RunwayId is not { } nearRwy
        )
        {
            return -1;
        }

        for (int j = nearIdx + 1; j < segments.Count; j++)
        {
            int toId = segments[j].ToNodeId;
            if (toId == nearNodeId)
            {
                continue;
            }

            if (
                ctx.Layout.Nodes.TryGetValue(toId, out GroundNode? node)
                && node.Type == GroundNodeType.RunwayHoldShort
                && node.RunwayId is { } rwy
                && rwy.Equals(nearRwy)
            )
            {
                return j;
            }
        }

        return -1;
    }

    private static List<string> BuildWarnings(List<TaxiRouteSegment> segments, SearchContext ctx, IReadOnlyList<ConnectorInsertion> insertions)
    {
        var warnings = new List<string>();

        // Turn-direction hints (issue #172 W7) the resolver couldn't honor — advise the controller that
        // the aircraft turned the other way. De-duplicated in case a hint was evaluated more than once.
        foreach (string advisory in ctx.ResolutionAdvisories.Distinct())
        {
            warnings.Add(advisory);
        }

        // A bare crossing's end past the far bar, reported only by the route that ends there: every probe records its own.
        if ((segments.Count > 0) && (ctx.ClearOfRunwayEnds.At(segments[^1].ToNodeId) is { } clearOfRunway))
        {
            warnings.Add(clearOfRunway.Advisory);
        }

        // Mandatory connectors the resolver had to bridge between cleared taxiways with no direct
        // junction. Notify the controller of each insertion, and suppress the generic
        // "unauthorized" warning for those connector taxiways — they were not a deviation.
        var mandatoryConnectors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ConnectorInsertion insertion in insertions)
        {
            warnings.Add(
                $"{insertion.FromTaxiway} and {insertion.ToTaxiway} do not connect directly — taxi via {string.Join(", ", insertion.Connectors)}"
            );
            foreach (string connector in insertion.Connectors)
            {
                mandatoryConnectors.Add(connector);
            }
        }

        AddOneWayWarnings(segments, ctx, warnings);

        if (ctx.AuthorizedTaxiways is null)
        {
            return warnings;
        }

        var warned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int bridgeEnd = OccupiedBridgeEnd(segments, ctx);
        (int Start, string? Taxiway) protrusion = ClearOfRunwayProtrusion(segments, ctx);
        for (int i = 0; i < segments.Count; i++)
        {
            TaxiRouteSegment seg = segments[i];

            // Junction arcs ("X - Y") are transitions between taxiways, not a traversal of one —
            // never an "unauthorized taxiway" deviation.
            if (seg.Edge.Edge is GroundArc { TaxiwayNames.Length: >= 2 })
            {
                continue;
            }

            string name = seg.TaxiwayName;
            bool inProtrusion = (i >= protrusion.Start) && name.Equals(protrusion.Taxiway, StringComparison.OrdinalIgnoreCase);
            if (inProtrusion || ((i < bridgeEnd) && name.Equals(ctx.OccupiedTaxiway, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // RAMP (apron / parking access) is never an unauthorized deviation — it is excluded by
            // IsLetterOnlyTaxiway, so the parking-bridge and arrival RAMP legs are not flagged here.
            if (
                SearchContext.IsLetterOnlyTaxiway(name)
                && !ctx.AuthorizedTaxiways.Contains(name)
                && !mandatoryConnectors.Contains(name)
                && warned.Add(name)
            )
            {
                warnings.Add(NotInRouteIssuedWarning(name));
            }
        }

        return warnings;
    }

    /// <summary>
    /// Where a bare taxi ended past a crossed runway protrudes into a taxiway the clearance never named (AIM 4-3-21.b): the
    /// index after the route's last segment on the final cleared taxiway, and that taxiway; (segment count, null) when the route
    /// ends no such way. The protrusion is where the clearance left the aircraft, not a deviation from it.
    /// </summary>
    private static (int Start, string? Taxiway) ClearOfRunwayProtrusion(List<TaxiRouteSegment> segments, SearchContext ctx)
    {
        if (
            (segments.Count == 0)
            || (ctx.WaypointSequence.Count == 0)
            || (ctx.ClearOfRunwayEnds.At(segments[^1].ToNodeId)?.CarriedInto is not { } taxiway)
        )
        {
            return (segments.Count, null);
        }

        string final = ctx.WaypointSequence[^1];
        int lastOnFinal = segments.FindLastIndex(s => s.TaxiwayName.Equals(final, StringComparison.OrdinalIgnoreCase));
        return (lastOnFinal + 1, taxiway);
    }

    /// <summary>
    /// How many leading segments bridge from the start onto the first cleared taxiway: the index of the first segment
    /// on it, or 0 when the aircraft occupies no taxiway, the path is empty, or the route never reaches it. Along those
    /// segments the taxiway the aircraft occupies (<see cref="SearchContext.OccupiedTaxiway"/>) is not a deviation from
    /// the route issued (THY9WC on B, cleared <c>TAXI A F</c>: B → B1 → A).
    /// </summary>
    private static int OccupiedBridgeEnd(List<TaxiRouteSegment> segments, SearchContext ctx)
    {
        if ((ctx.OccupiedTaxiway is null) || (ctx.WaypointSequence.Count == 0))
        {
            return 0;
        }

        string firstCleared = ctx.WaypointSequence[0];
        return Math.Max(segments.FindIndex(s => s.Edge.Edge.MatchesTaxiway(firstCleared)), 0);
    }

    /// <summary>
    /// In one-way <see cref="OneWayMode.Warn"/> mode (explicit clearances and the auto fallback), flag any
    /// materialised segment that traverses a one-way span against its allowed direction. Auto routes
    /// (<see cref="OneWayMode.HardExclude"/>) never reach here with a wrong-way segment — they are gated
    /// in <see cref="AutoRouter"/> — so no warning is needed for them.
    /// </summary>
    private static void AddOneWayWarnings(List<TaxiRouteSegment> segments, SearchContext ctx, List<string> warnings)
    {
        if (ctx.OneWayMode != OneWayMode.Warn || ctx.ForbiddenOneWayMoves.Count == 0)
        {
            return;
        }

        var warned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (TaxiRouteSegment seg in segments)
        {
            if (ctx.ForbiddenOneWayMoves.Contains((seg.FromNodeId, seg.ToNodeId)) && warned.Add(seg.TaxiwayName))
            {
                warnings.Add($"Taxiing {seg.TaxiwayName} against one-way direction");
            }
        }
    }

    /// <summary>
    /// Pick the full-length lineup hold-short on <paramref name="runwayId"/>: the first bar of
    /// <see cref="OrderFullLengthLineupBars"/>, with <paramref name="startNode"/> giving the side of the runway
    /// the aircraft is on and <paramref name="category"/> and <paramref name="wakeClass"/> the taxi routes the shadow
    /// test searches (a bar with no ground route ranks with the shadowed bars). When the runway is unknown to nav-data,
    /// falls back to the hold-short nearest <paramref name="startNode"/>.
    /// </summary>
    public static GroundNode FindFullLengthLineupHoldShort(
        AirportGroundLayout layout,
        GroundNode startNode,
        string runwayId,
        List<GroundNode> holdShortNodes,
        AircraftCategory category,
        WakeTurbulenceData.WakeClass wakeClass
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

        Func<GroundNode, bool>? ShadowTest() =>
            TaxiPathfinder.LineupBarShadowTest(layout, startNode.Id, runwayId, category, wakeClass, unreachableIsShadowed: true);
        if (OrderFullLengthLineupBars(layout.AirportId, runwayId, startNode.Position, holdShortNodes, ShadowTest) is [GroundNode first, ..])
        {
            return first;
        }

        GroundNode best = holdShortNodes[0];
        double bestDist = GeoMath.DistanceNm(startNode.Position, best.Position);
        for (int i = 1; i < holdShortNodes.Count; i++)
        {
            double dist = GeoMath.DistanceNm(startNode.Position, holdShortNodes[i].Position);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = holdShortNodes[i];
            }
        }

        return best;
    }

    /// <summary>How far along the runway past the bar nearest the threshold a bar still counts as full length.</summary>
    private const double FullLengthBandFt = 200.0;

    /// <summary>How far beyond the runway's edge a start must be before the side it is on counts.</summary>
    private const double SideMarginFt = 50.0;

    /// <summary>A candidate bar measured against the runway axis: feet along it from the threshold, and its side.</summary>
    private readonly record struct MeasuredBar(GroundNode Bar, double AlongFt, int Side);

    /// <summary>
    /// Orders <paramref name="bars"/>, hold-short bars of <paramref name="runwayId"/>, by how well each serves as the
    /// full-length lineup point for a departure from that end, best first.
    ///
    /// Each bar is measured along the runway centreline from the requested threshold toward the far end (negative
    /// before the threshold) and by its side of the centreline. The bars within 200 ft along-track of the smallest
    /// form the full-length band. The start's side is the side of the centreline <paramref name="start"/> is on; there
    /// is none when the start is null, within half the runway width plus 50 ft of the centreline, or before the
    /// threshold or past the far end along the runway.
    ///
    /// With no start side the order is along-track, then node id, except that, when the start is before the threshold
    /// or within the centreline band, a bar the shadow test reports (one the start's taxi route meets another of the
    /// runway's bars before) goes after every bar it does not. A null start or one past the far end gets no shadow
    /// test. With a start side, the band's start-side bars come first; when the band has none, the start-side bar with
    /// the smallest along-track stands in as the departure point, then the band follows; every other bar comes last;
    /// each group by along-track, then node id. When no bar lies on the start's side, the order is straight-line
    /// distance from the threshold, then node id.
    /// </summary>
    /// <param name="airportId">The airport whose nav data holds the runway.</param>
    /// <param name="runwayId">The departure end, e.g. <c>28R</c>.</param>
    /// <param name="start">Where the aircraft is, or null for no side preference.</param>
    /// <param name="bars">The candidate bars.</param>
    /// <param name="shadowTest">
    /// Builds the test of whether a bar is shadowed, called only when the start has no side and is before the threshold
    /// or within the centreline band, so a caller does no lookup or route search the order does not use. It or the test
    /// it builds is null when the start is not on the ground graph.
    /// </param>
    /// <returns>The bars best first, or null when nav data cannot resolve the runway's threshold and far end.</returns>
    public static List<GroundNode>? OrderFullLengthLineupBars(
        string airportId,
        string runwayId,
        LatLon? start,
        IReadOnlyList<GroundNode> bars,
        Func<Func<GroundNode, bool>?>? shadowTest
    )
    {
        RunwayInfo? runway = NavigationDatabase.InstanceOrNull?.GetRunway(airportId, runwayId);
        if (runway is null)
        {
            return null;
        }

        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        var farEnd = new LatLon(runway.EndLatitude, runway.EndLongitude);
        var heading = new TrueHeading(GeoMath.BearingTo(threshold, farEnd));
        List<MeasuredBar> measured =
        [
            .. bars.Select(bar => new MeasuredBar(bar, AlongFt(bar.Position, threshold, heading), SideOf(bar.Position, threshold, heading))),
        ];
        int startSide = StartSide(start, threshold, heading, runway.PavementLengthFt, runway.WidthFt);
        if ((startSide != 0) && (!measured.Any(m => m.Side == startSide)))
        {
            return [.. bars.OrderBy(bar => GeoMath.DistanceNm(threshold, bar.Position)).ThenBy(bar => bar.Id)];
        }

        Func<GroundNode, bool>? isShadowed =
            (startSide == 0) && (start is { } position) && (AlongFt(position, threshold, heading) <= runway.PavementLengthFt)
                ? shadowTest?.Invoke()
                : null;
        return OrderBySideAndBand(measured, startSide, isShadowed);
    }

    /// <summary>
    /// The ranked order of <paramref name="measured"/> once the start side is known (0: none); see
    /// <see cref="OrderFullLengthLineupBars"/>.
    /// </summary>
    private static List<GroundNode> OrderBySideAndBand(List<MeasuredBar> measured, int startSide, Func<GroundNode, bool>? isShadowed)
    {
        List<MeasuredBar> sorted = [.. measured.OrderBy(m => m.AlongFt).ThenBy(m => m.Bar.Id)];
        if (startSide == 0)
        {
            return isShadowed is null ? [.. sorted.Select(m => m.Bar)] : [.. sorted.Select(m => m.Bar).OrderBy(isShadowed)];
        }

        double bandEdgeFt = sorted[0].AlongFt + FullLengthBandFt;
        List<MeasuredBar> head = [.. sorted.Where(m => (m.Side == startSide) && (m.AlongFt <= bandEdgeFt))];
        if (head.Count == 0)
        {
            head.Add(sorted.First(m => m.Side == startSide));
        }

        IEnumerable<MeasuredBar> band = sorted.Where(m => (m.AlongFt <= bandEdgeFt) && (!head.Contains(m)));
        IEnumerable<MeasuredBar> rest = sorted.Where(m => (m.AlongFt > bandEdgeFt) && (!head.Contains(m)));
        return [.. head.Concat(band).Concat(rest).Select(m => m.Bar)];
    }

    /// <summary>The side of the runway <paramref name="start"/> is on (see <see cref="OrderFullLengthLineupBars"/>), or 0 for none.</summary>
    private static int StartSide(LatLon? start, LatLon threshold, TrueHeading heading, double lengthFt, double widthFt)
    {
        if (start is not { } position)
        {
            return 0;
        }

        double alongFt = AlongFt(position, threshold, heading);
        if ((alongFt < 0) || (alongFt > lengthFt))
        {
            return 0;
        }

        double crossFt = GeoMath.SignedCrossTrackDistanceNm(position, threshold, heading) * GeoMath.FeetPerNm;
        return Math.Abs(crossFt) <= ((widthFt / 2) + SideMarginFt) ? 0 : Math.Sign(crossFt);
    }

    private static double AlongFt(LatLon position, LatLon threshold, TrueHeading heading) =>
        GeoMath.AlongTrackDistanceNm(position, threshold, heading) * GeoMath.FeetPerNm;

    private static int SideOf(LatLon position, LatLon threshold, TrueHeading heading) =>
        Math.Sign(GeoMath.SignedCrossTrackDistanceNm(position, threshold, heading));

    /// <summary>
    /// The geographic threshold of <paramref name="runwayId"/>'s requested designator (e.g. the 28R
    /// end, not the reciprocal 10L end), from <see cref="NavigationDatabase"/>. Null when nav-data is
    /// uninitialized or the runway is unknown to it.
    /// </summary>
    internal static LatLon? ResolveRunwayThreshold(string airportId, string runwayId)
    {
        RunwayInfo? runway = NavigationDatabase.InstanceOrNull?.GetRunway(airportId, runwayId);
        return runway is null ? null : new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
    }
}
