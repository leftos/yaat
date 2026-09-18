using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Airport.Pathfinding;

/// <summary>
/// Explicit-mode driver for the pathfinder. Walks <see cref="SearchContext.WaypointSequence"/>
/// segment by segment, resolves each taxiway-to-taxiway junction via a bounded local best-first
/// search, handles variant resolution at the final segment, and appends a parking/spot extension
/// via AutoRouter when needed.
/// </summary>
public static class SegmentExpander
{
    private static readonly ILogger Log = SimLog.CreateLogger("SegmentExpander");

    /// <summary>
    /// Maximum nodes expanded in a single per-segment local search.
    /// Taxiways have fewer than 200 nodes at any real airport; this is a safety ceiling.
    /// </summary>
    private const int MaxLocalExpansions = 500;

    /// <summary>
    /// Maximum nodes expanded in the local auto-route detour (numbered + RAMP bridging).
    /// </summary>
    private const int MaxDetourExpansions = 5_000;

    /// <summary>
    /// Maximum BFS hops when bridging a start node (e.g. a parking/RAMP spot) onto the
    /// first named taxiway. Matches v1's <c>BfsToTaxiway</c> bound — parking spots connect
    /// to their taxiway within one or two RAMP/connector hops. A runway hold-short passed through
    /// on the way does NOT consume a hop (see <see cref="CollectBridgeCandidates"/>): a hold-short is
    /// a degree-2 pass-through inserted at graph-build time, and counting it silently broke the
    /// bridge when a hold-short moved onto the path between the start and the taxiway-access node.
    /// </summary>
    private const int MaxBridgeHops = 3;

    /// <summary>
    /// Reach of the second, deeper bridge search, run only when every access node within
    /// <see cref="MaxBridgeHops"/> is a dead end (no admissible onward edge on the taxiway from the bridged
    /// arrival bearing). SFO gate B4 → M3: the only M3 node within three hops is a corner-arc entry whose
    /// sole M3 edge is a U-turn, while the M3/RAMP junction sits four hops down the straight gate lead-out.
    /// Gates with a usable shallow entry never reach this pass, so their routes are unchanged (issue #396).
    /// </summary>
    private const int MaxBridgeHopsDeep = 6;

    /// <summary>
    /// Longest stub between a runway hold-short bar and the cleared taxiway it hangs off for that bar to
    /// count as the cleared taxiway's own access to the runway. Shares issue #393's rule that a short run
    /// to the bar counts as being at the runway (<see cref="TaxiPathfinder.AdjacentRunwayHoldShortMaxFt"/>).
    /// </summary>
    private const double RunwayConnectorStubMaxFt = TaxiPathfinder.AdjacentRunwayHoldShortMaxFt;

    /// <summary>
    /// Run the segment expander on the given <paramref name="ctx"/>.
    /// Returns either a materialised <see cref="TaxiRoute"/> or a structured
    /// <see cref="PathfindingFailure"/>. Exactly one of the two return values is non-null.
    /// </summary>
    /// <summary>
    /// Resolve an explicit named-taxiway clearance. When an implicit connector bridges an adjacent pair
    /// of cleared taxiways (e.g. <c>LF</c> between <c>L</c> and <c>F</c>), also resolve the variant with
    /// the connector threaded into the sequence and keep whichever full route is shorter — so the painted
    /// connector is used when it beats crossing at the taxiways' shared apex (and a blocked apex never
    /// strands the route), while a far-away aircraft still crosses directly. The original sequence is
    /// always one of the candidates, so a connector is never forced when the direct crossing is cheaper.
    /// </summary>
    public static (TaxiRoute? Route, PathfindingFailure? Failure) Run(SearchContext ctx)
    {
        List<IReadOnlyList<string>> variants = BuildConnectorVariants(ctx);
        if (variants.Count == 0)
        {
            (TaxiRoute? Route, PathfindingFailure? Failure) only = ResolveExplicit(ctx);
            return only.Route is not null ? only : TryRunwayConnectorFallback(ctx, only);
        }

        (TaxiRoute? Route, PathfindingFailure? Failure) primary = ResolveExplicit(ctx);
        TaxiRoute? best = primary.Route;
        foreach (IReadOnlyList<string> variant in variants)
        {
            (TaxiRoute? route, PathfindingFailure? _) = ResolveExplicit(ctx with { WaypointSequence = variant });
            if (route is not null && (best is null || IsBetterRoute(route, best)))
            {
                best = route;
            }
        }

        return best is not null ? (best, null) : TryRunwayConnectorFallback(ctx, primary);
    }

    /// <summary>
    /// Last chance for a runway destination the cleared taxiways reach but carry no hold-short bar for: the
    /// bar sits on a short numbered stub off the final taxiway (SFO taxiway B runs past 01L/19R's south end,
    /// and the bars are on the M1 and A2 stubs). Controllers clear "…Bravo, runway 1L" and pilots take the
    /// stub, so the clearance is re-resolved with the connector appended as a final waypoint rather than
    /// rejected. Only a numbered connector within <see cref="RunwayConnectorStubMaxFt"/> of the taxiway
    /// qualifies (see <see cref="FindRunwayConnectorsOffTaxiway"/>, issue #393's rule that a short run to the
    /// bar counts as being at the runway); with no candidate the original failure is returned unchanged, so a
    /// runway the taxiway genuinely does not serve reports exactly what it reports today.
    /// </summary>
    private static (TaxiRoute? Route, PathfindingFailure? Failure) TryRunwayConnectorFallback(
        SearchContext ctx,
        (TaxiRoute? Route, PathfindingFailure? Failure) primary
    )
    {
        if (!IsRunwayConnectorFallbackApplicable(ctx, primary.Failure))
        {
            return primary;
        }

        string lastTaxiway = ctx.WaypointSequence[^1];
        string runwayId = ctx.Destination.RunwayId!;
        foreach (RunwayConnectorCandidate candidate in FindRunwayConnectorsOffTaxiway(ctx.Layout, lastTaxiway, runwayId, ctx.WaypointSequence))
        {
            if (ResolveThroughConnector(ctx, candidate, lastTaxiway, runwayId) is { } route)
            {
                return (route, null);
            }
        }

        return primary;
    }

    /// <summary>
    /// True when the failed resolution is the one this fallback owns: a runway destination the final named
    /// taxiway could not reach, that taxiway being a plain name (not a node-ref or a runway taxied along).
    /// </summary>
    private static bool IsRunwayConnectorFallbackApplicable(SearchContext ctx, PathfindingFailure? failure)
    {
        if ((ctx.Destination.Kind != DestinationKind.Runway) || (ctx.Destination.RunwayId is null) || (ctx.WaypointSequence.Count == 0))
        {
            return false;
        }

        if (failure is not { Kind: FailureKind.DestinationUnreachable })
        {
            return false;
        }

        string lastTaxiway = ctx.WaypointSequence[^1];
        return string.Equals(failure.InfeasibleTaxiway, lastTaxiway, StringComparison.OrdinalIgnoreCase)
            && IsPlainTaxiwayToken(ctx.Layout, lastTaxiway);
    }

    /// <summary>True when a waypoint token names a taxiway outright — not a <c>#id</c> node-ref, not a runway.</summary>
    private static bool IsPlainTaxiwayToken(AirportGroundLayout layout, string token) =>
        !token.StartsWith('#') && !IsRunwayWaypoint(token) && !layout.TryGetRunwayCenterlineName(token, out _);

    /// <summary>
    /// Re-resolve the clearance with <paramref name="candidate"/> appended as the final waypoint, accepting
    /// the route only when it now stops at the very bar the stub led to — appending a taxiway authorises the
    /// whole taxiway, and a long one (SFO M1 runs the length of the A ramp) could otherwise carry the route the
    /// long way to a different bar for the same runway. The notification goes on the route's own warnings, and
    /// the advisory list is copied — a <c>with</c> copy shares it, so a candidate that fails to resolve would
    /// otherwise leak its advisories onto the next one.
    /// </summary>
    private static TaxiRoute? ResolveThroughConnector(SearchContext ctx, RunwayConnectorCandidate candidate, string lastTaxiway, string runwayId)
    {
        (TaxiRoute? route, PathfindingFailure? _) = ResolveExplicit(
            ctx with
            {
                WaypointSequence = [.. ctx.WaypointSequence, candidate.Connector],
                ResolutionAdvisories = [.. ctx.ResolutionAdvisories],
            }
        );
        if (route is null || !route.HoldShortPoints.Any(hs => IsDestinationHoldFor(hs, runwayId) && (hs.NodeId == candidate.BarNodeId)))
        {
            return null;
        }

        string runwayDisplay = RunwayIdentifier.ToDisplayDesignator(runwayId);
        route.Warnings.Insert(0, $"via {candidate.Connector} — {lastTaxiway} reaches {runwayDisplay} through {candidate.Connector}");
        string trace = $"[connector] {lastTaxiway} has no {runwayDisplay} bar; threading {candidate.Connector} (stub {candidate.StubFt:F0} ft)";
        ctx.DiagnosticLog?.Invoke(trace);
        Log.LogDebug(
            "{Taxiway} has no runway {Runway} bar; threading {Connector} (stub {StubFt:F0} ft)",
            lastTaxiway,
            runwayDisplay,
            candidate.Connector,
            candidate.StubFt
        );
        return route;
    }

    /// <summary>
    /// Prefer the route that honors the clearance with fewer mandatory blind-detour connector
    /// insertions; distance breaks ties. A curated-connector variant threads the real connector as a
    /// named token, so it resolves with no insertion and beats a shorter route that had to blind-detour
    /// between two cleared taxiways sharing no junction (e.g. SFO <c>TAXI A B1</c> routing via the real
    /// connector Q instead of a shorter bypass that drops A and B1).
    /// </summary>
    internal static bool IsBetterRoute(TaxiRoute candidate, TaxiRoute incumbent)
    {
        if (candidate.MandatoryConnectorCount != incumbent.MandatoryConnectorCount)
        {
            return candidate.MandatoryConnectorCount < incumbent.MandatoryConnectorCount;
        }

        return candidate.TotalDistanceNm < incumbent.TotalDistanceNm;
    }

    /// <summary>
    /// Sequence variants with each applicable implicit connector threaded between its adjacent cleared
    /// pair. Empty when no implicit connector applies (the common case — caller resolves the sequence
    /// directly with no extra work).
    /// </summary>
    private static List<IReadOnlyList<string>> BuildConnectorVariants(SearchContext ctx)
    {
        IReadOnlyList<string> seq = ctx.WaypointSequence;
        if (seq.Count < 2 || ctx.ImplicitConnectors.Count == 0)
        {
            return [];
        }

        var inserted = new List<string>(seq.Count + 1);
        bool any = false;
        for (int i = 0; i < seq.Count; i++)
        {
            inserted.Add(seq[i]);
            if (i + 1 >= seq.Count)
            {
                continue;
            }

            string? connector = ctx.GetImplicitConnectorName(seq[i], seq[i + 1]);
            if (
                connector is not null
                && !connector.Equals(seq[i], StringComparison.OrdinalIgnoreCase)
                && !connector.Equals(seq[i + 1], StringComparison.OrdinalIgnoreCase)
            )
            {
                inserted.Add(connector);
                any = true;
            }
        }

        return any ? [inserted] : [];
    }

    private static (TaxiRoute? Route, PathfindingFailure? Failure) ResolveExplicit(SearchContext ctx)
    {
        if (ctx.WaypointSequence.Count == 0)
        {
            return (
                null,
                new PathfindingFailure(FailureKind.TaxiwayNotConnected, "SegmentExpander requires a non-empty waypoint sequence.", null, null, null)
            );
        }

        if (!ctx.Layout.Nodes.TryGetValue(ctx.StartNodeId, out _))
        {
            return (
                null,
                new PathfindingFailure(FailureKind.StartNodeUnreachable, $"Start node {ctx.StartNodeId} not found in layout.", null, null, null)
            );
        }

        // Resolve node-reference tokens (#NNNN) in the waypoint sequence.
        List<WaypointToken> resolvedWaypoints = ResolveWaypoints(ctx);

        // Reject a clearance naming a taxiway that is absent from the layout. Otherwise the
        // per-segment walk finds no matching edge, silently yields an empty/partial route, and the
        // command would succeed against a route that goes nowhere. (Node-ref tokens are validated
        // when routed.) Mirrors v1's "Cannot reach taxiway X" rejection.
        foreach (WaypointToken wp in resolvedWaypoints)
        {
            if (!wp.IsNodeRef && (ctx.Layout.GetNodesOnTaxiway(wp.Name).Count == 0))
            {
                return (
                    null,
                    new PathfindingFailure(FailureKind.TaxiwayNotConnected, $"Cannot find taxiway {wp.Name} in layout.", wp.Name, null, null)
                );
            }
        }

        var edges = new List<DirectionalEdge>();
        var head = PartialRoute.StartAt(ctx.StartNodeId);

        // Bridge onto the first named taxiway when the start node is off it (e.g. parked
        // on a RAMP spot whose only edges are RAMP). Mirrors v1's BfsToTaxiway. Without
        // this the first per-segment local search finds no on-taxiway edge from the start
        // and the route degrades to a long, often-failing detour. The bridge is
        // direction-aware: when several taxiway-access nodes are reachable, it picks the one
        // whose admissible on-taxiway continuation heads toward where the route must go (the
        // junction with the next cleared taxiway, or the destination). A direction-blind
        // pick can enter the taxiway through a corner arc that commits the head to the wrong
        // branch, after which the correct branch fails the U-turn admissibility check.
        if (!resolvedWaypoints[0].IsNodeRef)
        {
            LatLon? bridgeBias = ResolveBridgeBias(resolvedWaypoints, head, ctx);
            (List<DirectionalEdge>? bridgeEdges, PartialRoute? bridgeHead) = BridgeStartToTaxiway(head, resolvedWaypoints[0].Name, bridgeBias, ctx);
            if (bridgeEdges.Count > 0)
            {
                edges.AddRange(bridgeEdges);
                head = bridgeHead with { VisitedNodeIds = VisitedNodeSet.Single(bridgeHead.HeadNodeId) };
            }
        }
        else if (head.HeadNodeId != resolvedWaypoints[0].ResolvedNodeId)
        {
            // The node-ref counterpart of the bridge above. ExpandSegment dispatches on the NEXT token,
            // so without this the first node-ref is never a routing target and the walk jumps straight to
            // the second — reaching it by whatever path the A* likes rather than the one that was drawn.
            (List<DirectionalEdge>? leadEdges, PartialRoute? leadHead, PathfindingFailure? leadFailure) = RouteToSpecificNode(
                head,
                resolvedWaypoints[0].ResolvedNodeId,
                ctx
            );
            if (leadFailure is not null)
            {
                return (null, leadFailure);
            }

            edges.AddRange(leadEdges!);
            head = leadHead! with { VisitedNodeIds = VisitedNodeSet.Single(leadHead!.HeadNodeId) };
        }

        // Walk the waypoint sequence segment by segment, with recursive look-ahead at each
        // taxiway-to-taxiway junction (see ResolveSequence). Look-ahead defeats first-match
        // junction picks that would strand the route on the wrong leg of a V-shaped taxiway.
        // Mandatory-connector insertions (two cleared taxiways with no direct junction) are
        // collected so the materialiser can notify the controller instead of warning.
        var insertions = new List<ConnectorInsertion>();
        (List<DirectionalEdge>? seqEdges, PartialRoute? seqHead, PathfindingFailure? seqFailure) = ResolveSequence(
            head,
            resolvedWaypoints,
            0,
            enableLookahead: true,
            insertions,
            ctx
        );
        if (seqFailure is not null)
        {
            return (null, seqFailure);
        }

        edges.AddRange(seqEdges!);
        head = seqHead!;

        // Variant resolution: if destination is a runway and the last named taxiway has
        // numbered variants that reach it, extend automatically. Skipped when the walked
        // route already reaches a hold-short for the destination runway — the named taxiway
        // serves the runway directly (e.g. B1 has its own 10L hold-short), so the materialiser
        // truncates there and a variant extension would only back-track to it.
        if (
            ctx.Destination.Kind == DestinationKind.Runway
            && ctx.Destination.RunwayId is { } destRunway
            && !RouteReachesRunwayHoldShort(edges, destRunway, ctx)
        )
        {
            WaypointToken lastWaypoint = resolvedWaypoints[^1];
            if (!lastWaypoint.IsNodeRef)
            {
                (List<DirectionalEdge>? varEdges, PathfindingFailure? varFailure) = TryVariantExtension(head, lastWaypoint.Name, destRunway, ctx);
                if (varFailure is not null)
                {
                    return (null, varFailure);
                }

                if (varEdges is not null)
                {
                    edges.AddRange(varEdges);
                }
            }
        }

        // Last-resort A* fallback for a runway destination the explicit walk could not reach.
        // The greedy WalkToNaturalTerminus can dead-end on a hold-area hairpin one turn short of
        // the lineup bar (e.g. MIA TAXI P S to rwy 9: taxiway S crosses rwy 12 then hairpins to the
        // rwy-9 hold-short, and the U-turn back to it from the dead-end spur is geometrically
        // inadmissible). A flat A* explores all branches and reaches the runway hold-short across
        // any intermediate crossings. It is HARD-constrained to the cleared taxiways — every
        // letter-only taxiway the controller did not name is excluded (numbered connectors and RAMP
        // stay free) — so the route may not detour onto an unnamed taxiway and must cross every
        // runway the cleared taxiways cross (e.g. B crosses 28R then 28L) to reach the destination.
        // This only runs when the explicit route does NOT already reach the destination runway, so
        // it never changes a route that already does. An explicit HS on a crossed runway is a hold/
        // authorization marker, not a routing terminus, so the route still continues to the runway.
        if (
            ctx.Destination.Kind == DestinationKind.Runway
            && ctx.Destination.RunwayId is { } fallbackRunway
            && !RouteReachesRunwayHoldShort(edges, fallbackRunway, ctx)
        )
        {
            IReadOnlySet<string> unauthorized = UnnamedLetterTaxiways(ctx.Layout, ctx.AuthorizedTaxiways);
            SearchContext autoCtx = ctx with
            {
                WaypointSequence = [],
                AvoidedTaxiways = unauthorized,
                AvoidMode = unauthorized.Count > 0 ? AvoidTaxiwayMode.HardExclude : AvoidTaxiwayMode.Off,
            };
            (TaxiRoute? autoRoute, PathfindingFailure? autoFailure) = AutoRouter.Run(autoCtx);
            if (autoRoute is not null)
            {
                var autoEdges = autoRoute.Segments.Select(s => s.Edge).ToList();
                if (RouteReachesRunwayHoldShort(autoEdges, fallbackRunway, ctx))
                {
                    ctx.DiagnosticLog?.Invoke(
                        $"[fallback] explicit walk short of rwy {fallbackRunway}; constrained A* re-route reached it ({autoEdges.Count} edges)"
                    );
                    edges = autoEdges;
                    insertions.Clear();
                }
                else
                {
                    ctx.DiagnosticLog?.Invoke(
                        $"[fallback] constrained A* route did not reach a rwy {fallbackRunway} hold-short ({autoEdges.Count} edges)"
                    );
                }
            }
            else
            {
                ctx.DiagnosticLog?.Invoke($"[fallback] constrained A* failed: {autoFailure?.HumanMessage ?? "no route"}");
            }
        }

        // Parking/spot extension: after the named taxiway walk, auto-route to destination.
        if (ctx.Destination.Kind is DestinationKind.Parking or DestinationKind.Spot or DestinationKind.Helipad)
        {
            if (ctx.Destination.TargetNodeId is { } destId && head.HeadNodeId != destId)
            {
                (List<DirectionalEdge>? extEdges, PathfindingFailure? extFailure) = ExtendToDestination(head, destId, ctx);
                if (extFailure is not null)
                {
                    return (null, extFailure);
                }

                if (extEdges is not null)
                {
                    edges.AddRange(extEdges);
                }
            }
        }

        TaxiRoute route = RouteMaterialiser.Materialise(edges, ctx, insertions);

        // Honor the clearance: every named taxiway the controller specified must be REACHED by the
        // resolved route — either traversed (an edge labeled for it) or at least touched (the route
        // passes through a node that lies on it). Touching without traversing is legitimate and
        // common: when two cleared taxiways meet at the same junction, the route turns from one onto
        // the next through that node without ever walking a labeled edge of either, and when a more
        // direct connector reaches the junction a named taxiway serves, the named taxiway is still
        // touched there. The check only fails when a named taxiway is never reached at all — the
        // aircraft could not get to it from its start without leaving the movement area (e.g. a gate
        // from which taxiway A lies across active runways), so the resolver bypassed it entirely.
        // Clearing via a taxiway the aircraft cannot reach is worse than rejecting. (Node-ref tokens
        // are validated when routed.)
        foreach (WaypointToken wp in resolvedWaypoints)
        {
            if (wp.IsNodeRef || RouteReachesTaxiway(route, wp.Name))
            {
                continue;
            }

            return (
                null,
                new PathfindingFailure(
                    FailureKind.TaxiwayNotConnected,
                    $"Cannot taxi via {wp.Name} from the aircraft's position — it is unreachable without crossing a "
                        + $"runway or leaving the movement area.",
                    wp.Name,
                    null,
                    null
                )
            );
        }

        return (route, null);
    }

    /// <summary>
    /// True if the materialised route reaches <paramref name="taxiwayName"/>: it traverses an edge
    /// labeled for the taxiway, or it passes through a node incident to the taxiway (the route turned
    /// off at a junction the taxiway serves without walking a labeled edge). Used to honor an explicit
    /// clearance without rejecting the normal case where consecutive cleared taxiways share a junction
    /// node. Operates on the final route — not the pre-materialise edge walk, which can over-run the
    /// destination hold-short and reach the taxiway only on the far side of a runway it never crosses.
    /// </summary>
    private static bool RouteReachesTaxiway(TaxiRoute route, string taxiwayName)
    {
        foreach (TaxiRouteSegment seg in route.Segments)
        {
            DirectionalEdge e = seg.Edge;
            if (e.Edge.MatchesTaxiway(taxiwayName) || NodeIncidentToTaxiway(e.FromNode, taxiwayName) || NodeIncidentToTaxiway(e.ToNode, taxiwayName))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when stepping from <paramref name="current"/> to <paramref name="nextNodeId"/> is a blocked
    /// turn — either the corner-arc 2-node move, or the sharp pivot turn-triple keyed on where we arrived
    /// from. Hard for explicit named-taxiway paths too (a blocked turn has no painted line).
    /// </summary>
    private static bool IsBlockedTurnEdge(SearchContext ctx, PartialRoute current, int nextNodeId) =>
        ctx.IsBlockedArcMove(current.HeadNodeId, nextNodeId)
        || (current.Previous is not null && ctx.IsBlockedTurn(current.Previous.HeadNodeId, current.HeadNodeId, nextNodeId));

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

    // -----------------------------------------------------------------------
    // Sequence resolution (with recursive junction look-ahead)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Resolve waypoint tokens from <paramref name="startIndex"/> onward, walking each
    /// consecutive pair via <see cref="ExpandSegment"/> and the final token via
    /// <see cref="ExpandLastWaypoint"/>. Returns the accumulated directed edges, the final
    /// head, or a structured failure (exactly one of edges/head vs failure is non-null).
    ///
    /// <para><paramref name="enableLookahead"/> controls junction selection. When true (the
    /// top-level resolution) <see cref="RouteNamedToNamed"/> scores each junction candidate by
    /// the cost of resolving the <em>remaining</em> sequence from it — a bounded recursive
    /// look-ahead that defeats first-match picks producing hairpin U-turns at V-shaped
    /// taxiway junctions (mirrors v1's SelectBestStopNode). When false (inside a look-ahead
    /// probe) it falls back to first-match selection and suppresses the whole-airport detour
    /// fallback, which both bounds recursion to a single level and treats a continuation that
    /// needs a detour as a strong negative signal against the candidate that led there.</para>
    /// </summary>
    private static (List<DirectionalEdge>? Edges, PartialRoute? Head, PathfindingFailure? Failure) ResolveSequence(
        PartialRoute head,
        IReadOnlyList<WaypointToken> tokens,
        int startIndex,
        bool enableLookahead,
        List<ConnectorInsertion> insertions,
        SearchContext ctx
    )
    {
        var edges = new List<DirectionalEdge>();
        PartialRoute current = head;

        for (int i = startIndex; i < tokens.Count; i++)
        {
            WaypointToken token = tokens[i];

            if (i + 1 < tokens.Count)
            {
                WaypointToken next = tokens[i + 1];
                WaypointToken? lookahead = i + 2 < tokens.Count ? tokens[i + 2] : null;
                (List<DirectionalEdge>? segEdges, PartialRoute? newHead, PathfindingFailure? failure) = ExpandSegment(
                    current,
                    token,
                    next,
                    lookahead,
                    tokens,
                    i,
                    enableLookahead,
                    insertions,
                    ctx
                );
                if (failure is not null)
                {
                    return (null, null, failure);
                }

                edges.AddRange(segEdges!);

                // Reset VisitedNodeIds to just the current head node before entering the next segment.
                // This allows routes that intentionally revisit a taxiway (e.g. A E B B3 A B1) to
                // use nodes already visited in a prior segment. Cycle prevention within a single
                // segment's local search is preserved because each local search starts from the
                // freshly-reset head.
                current = newHead! with
                {
                    VisitedNodeIds = VisitedNodeSet.Single(newHead!.HeadNodeId),
                };
            }
            else
            {
                // Last waypoint: walk to natural terminus of the taxiway (or the node itself for
                // node-refs). The named taxiway it was reached FROM (when any) lets a bare final
                // taxiway with no downstream constraint stop at that junction instead of walking off
                // in an arbitrary direction. A preceding RUNWAY token does not count: stopping at the
                // runway/taxiway "junction" ends the route on the runway surface with no crossing
                // bar (MIA TAXI M1 RWY08R/26L L1 stopped on the 08R centerline), so the final
                // taxiway is walked clear of the runway as before.
                string? precedingTaxiway = (i > 0 && !tokens[i - 1].IsNodeRef && !tokens[i - 1].IsRunway) ? tokens[i - 1].Name : null;
                (List<DirectionalEdge>? segEdges, PartialRoute? newHead, PathfindingFailure? failure) = ExpandLastWaypoint(
                    current,
                    token,
                    precedingTaxiway,
                    ctx
                );
                if (failure is not null)
                {
                    return (null, null, failure);
                }

                edges.AddRange(segEdges!);
                current = newHead!;
            }
        }

        return (edges, current, null);
    }

    /// <summary>
    /// Penalty assigned to a junction candidate whose remaining-sequence look-ahead probe
    /// cannot resolve cleanly (a tail segment would need the whole-airport detour). Large
    /// enough to dominate any real route distance so a clean continuation always wins, but
    /// finite so that when <em>every</em> candidate's tail is unresolvable the selection
    /// still falls back to the cheapest cost-to-reach.
    /// </summary>
    private const double TailUnresolvablePenaltyNm = 1000.0;

    /// <summary>
    /// True when there is at least one waypoint after the segment's toTaxiway (token
    /// <paramref name="fromIndex"/>+1), i.e. the route continues past the junction and the
    /// look-ahead probe has something meaningful to discriminate on. For the final
    /// transition (toTaxiway is the last token) the probe would only measure a terminus walk,
    /// so the cheaper geometric heuristic is used instead.
    /// </summary>
    private static bool HasMeaningfulTail(IReadOnlyList<WaypointToken> tokens, int fromIndex) => fromIndex + 2 < tokens.Count;

    /// <summary>
    /// Cost of resolving the remaining sequence (tokens from <paramref name="startIndex"/>)
    /// starting at <paramref name="headAtJunction"/>, used to score a junction candidate.
    /// Resolves first-match with the detour fallback suppressed; returns
    /// <see cref="TailUnresolvablePenaltyNm"/> when the tail cannot be resolved cleanly.
    /// </summary>
    private static double ProbeTailCost(PartialRoute headAtJunction, IReadOnlyList<WaypointToken> tokens, int startIndex, SearchContext ctx)
    {
        // Mirror the main loop's reset of VisitedNodeIds before the next segment so the probe
        // cost matches what the real resolution would produce from this junction.
        PartialRoute probeStart = headAtJunction with
        {
            VisitedNodeIds = VisitedNodeSet.Single(headAtJunction.HeadNodeId),
        };
        // Probes never reach the detour (it is suppressed when enableLookahead is false), so no
        // connector insertions are recorded — pass a throwaway list.
        (List<DirectionalEdge>? _, PartialRoute? tailHead, PathfindingFailure? failure) = ResolveSequence(
            probeStart,
            tokens,
            startIndex,
            enableLookahead: false,
            [],
            ctx
        );
        if (failure is not null || tailHead is null)
        {
            return TailUnresolvablePenaltyNm;
        }

        double tailCost = Math.Max(0.0, tailHead.AccumulatedCost - probeStart.AccumulatedCost);

        // Resolving the tail tokens is not the same as reaching the destination. When the destination
        // is a concrete node and the tail terminated short of it — the destination-aware terminus walk
        // in ExpandLastWaypoint fell through to the natural terminus because the onward turn onto the
        // destination was inadmissible — the tail is deceptively cheap. Charge the true remaining reach
        // cost so a junction that strands the aircraft where the destination is unreachable without a
        // long detour cannot beat a junction from which the destination is genuinely reachable. (OAK
        // "TAXI C D @NEW1": the wrong-way-onto-C junction's tail dead-ends at the C/D junction, where
        // turning onto D toward NEW1 is a ~185° U-turn; without this it out-scores the correct junction.)
        if (
            ctx.Destination.Kind is DestinationKind.Parking or DestinationKind.Spot or DestinationKind.Helipad or DestinationKind.Node
            && ctx.Destination.TargetNodeId is { } destId
            && tailHead.HeadNodeId != destId
        )
        {
            tailCost += ProbeDestinationReachCost(tailHead, destId, ctx);
        }

        return tailCost;
    }

    /// <summary>
    /// Additional cost to actually reach a concrete-node destination from a tail probe's terminus,
    /// used by <see cref="ProbeTailCost"/> so a junction whose tail dead-ends short of the destination
    /// does not score deceptively cheap. Runs a bounded A* from the tail terminus — inheriting its
    /// arrival bearing via <paramref name="tailHead"/> so admissibility fires on the first reach edge —
    /// to the destination node. The detour is priced with the search's own accumulated cost — turns,
    /// transitions, crossings and the centerline multiplier, not bare distance — because it competes
    /// against other candidates' tails that carry every one of those terms: priced by distance alone, a
    /// wrong-way junction whose way out is a loop across a runway (OAK "TAXI G C D @NEW1": back down C,
    /// E, across 28R, G) scored 0.2 nm under the straight G→D junction. Bounded to
    /// <see cref="MaxDetourExpansions"/> expansions; a destination unreachable within that bound returns
    /// <see cref="TailUnresolvablePenaltyNm"/> — either way the dead-ending junction is penalised out of
    /// contention.
    /// </summary>
    private static double ProbeDestinationReachCost(PartialRoute tailHead, int destinationNodeId, SearchContext ctx)
    {
        SearchContext reachCtx = ctx with
        {
            StartNodeId = tailHead.HeadNodeId,
            Destination = new DestinationDescriptor(
                destinationNodeId,
                null,
                ctx.Destination.ParkingName,
                ctx.Destination.SpotName,
                ctx.Destination.Kind
            ),
            WaypointSequence = [],
            AuthorizedTaxiways = null,
        };

        (TaxiRoute? route, PathfindingFailure? failure, double reachCost) = AutoRouter.RunWithCost(reachCtx, tailHead, MaxDetourExpansions);
        if (failure is not null || route is null)
        {
            return TailUnresolvablePenaltyNm;
        }

        return reachCost;
    }

    // -----------------------------------------------------------------------
    // Waypoint resolution
    // -----------------------------------------------------------------------

    private sealed record WaypointToken(string Name, bool IsNodeRef, int ResolvedNodeId, TurnDirection? TurnHint, bool IsRunway);

    private static List<WaypointToken> ResolveWaypoints(SearchContext ctx)
    {
        IReadOnlyList<TurnDirection?>? hints = ctx.WaypointTurnHints;
        var result = new List<WaypointToken>(ctx.WaypointSequence.Count);
        for (int i = 0; i < ctx.WaypointSequence.Count; i++)
        {
            string token = ctx.WaypointSequence[i];
            TurnDirection? hint = (hints is not null && i < hints.Count) ? hints[i] : null;
            if (token.StartsWith('#') && int.TryParse(token.AsSpan(1), out int nodeId))
            {
                result.Add(new WaypointToken(token, IsNodeRef: true, ResolvedNodeId: nodeId, TurnHint: hint, IsRunway: false));
            }
            else if (ctx.Layout.TryGetRunwayCenterlineName(token, out string? centerlineName))
            {
                // A runway named in the path is taxied ALONG: rewrite to the canonical centerline edge
                // name (e.g. "28R" → "RWY28R/10L") so the name-keyed walk routes over the runway surface.
                // A turn glyph on a runway token has no meaning — travel direction is fixed by the adjacent
                // waypoints' junctions — so the hint is dropped to avoid a spurious unhonored-turn advisory.
                result.Add(new WaypointToken(centerlineName, IsNodeRef: false, ResolvedNodeId: -1, TurnHint: null, IsRunway: true));
            }
            else
            {
                // A token already in canonical centerline form ("RWY08R/26L") routes along the runway
                // by name without the rewrite above, so classify it as a runway too.
                result.Add(new WaypointToken(token, IsNodeRef: false, ResolvedNodeId: -1, TurnHint: hint, IsRunway: IsRunwayWaypoint(token)));
            }
        }

        return result;
    }

    /// <summary>True when a waypoint name is the canonical runway-centerline form (e.g. <c>"RWY28R/10L"</c>).</summary>
    private static bool IsRunwayWaypoint(string name) => name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase);

    /// <summary>Display form of a canonical runway waypoint name for messages — strips the <c>RWY</c> prefix.</summary>
    private static string RunwayWaypointDisplay(string name) => IsRunwayWaypoint(name) ? name[3..] : name;

    // -----------------------------------------------------------------------
    // Segment expansion (T_i → T_{i+1})
    // -----------------------------------------------------------------------

    private static (List<DirectionalEdge>? Edges, PartialRoute? Head, PathfindingFailure? Failure) ExpandSegment(
        PartialRoute head,
        WaypointToken current,
        WaypointToken next,
        WaypointToken? lookahead,
        IReadOnlyList<WaypointToken> tokens,
        int index,
        bool enableLookahead,
        List<ConnectorInsertion> insertions,
        SearchContext ctx
    )
    {
        if (next.IsNodeRef)
        {
            // Next is a node-ref: route from current taxiway terminus toward that node.
            return RouteToNodeRef(head, next.ResolvedNodeId, ctx);
        }

        if (current.IsNodeRef)
        {
            // Current is a node-ref, next is a named taxiway: walk onto the next taxiway from current node.
            return RouteFromNodeRefToTaxiway(head, current.ResolvedNodeId, next.Name, ctx);
        }

        // Named taxiway → named taxiway: find junction nodes and pick best. Lookahead only
        // applies when it's a named taxiway (not a node-ref) — for node-ref lookaheads, the
        // junction picker has no anchor to align against.
        string? lookaheadName = lookahead is { IsNodeRef: false } la ? la.Name : null;
        return RouteNamedToNamed(head, current.Name, next.Name, lookaheadName, tokens, index, enableLookahead, insertions, ctx);
    }

    private static (List<DirectionalEdge>? Edges, PartialRoute? Head, PathfindingFailure? Failure) ExpandLastWaypoint(
        PartialRoute head,
        WaypointToken waypoint,
        string? precedingTaxiway,
        SearchContext ctx
    )
    {
        if (waypoint.IsNodeRef)
        {
            // The sequence ends with a node-ref: route to that specific node.
            return RouteToSpecificNode(head, waypoint.ResolvedNodeId, ctx);
        }

        // Destination-aware terminus: when the clearance ends on a named taxiway that leads to a
        // known parking/spot/helipad node, route to that node along the taxiway instead of walking
        // to the natural terminus. The greedy terminus walk is direction-blind — it picks the
        // geometrically-admissible continuation, which can be the wrong way along the taxiway
        // (post-landing arrival direction) or past the destination, after which ExtendToDestination
        // U-turns back. LocalSearchToJunction routes straight to the destination in the correct
        // direction and stops there (its junction-edge exception allows the final one-hop off the
        // taxiway onto the spot). A destination more than one hop off the taxiway makes the search
        // fail, so it falls through to the terminus walk + ExtendToDestination as before.
        if (
            ctx.Destination.Kind is DestinationKind.Parking or DestinationKind.Spot or DestinationKind.Helipad
            && ctx.Destination.TargetNodeId is { } destId
            && head.HeadNodeId != destId
        )
        {
            (List<DirectionalEdge>? toDestEdges, PartialRoute? toDestHead, double _) = LocalSearchToJunction(head, waypoint.Name, destId, ctx);
            if (toDestEdges is not null)
            {
                return (toDestEdges, toDestHead, null);
            }

            // The destination is more than one hop off the named taxiway (e.g. via a connector or a
            // RAMP spur). Pick the on-taxiway stop node from which the destination is reachable at
            // lowest total (walk + extension) cost instead of committing to the greedy terminus —
            // mirrors V1's SelectBestStopNode. The greedy terminus is direction-blind and its single
            // from-terminus extension is often inadmissible (a U-turn at a dead-end) or wrong-way.
            (List<DirectionalEdge>? stopEdges, PartialRoute? stopHead, double _) = SelectBestParkingStop(head, waypoint.Name, destId, ctx);
            if (stopEdges is not null)
            {
                return (stopEdges, stopHead, null);
            }
        }

        // Crossed-runway anchor (issue #172 W6): a Node destination sitting ON the final taxiway is the
        // far-side hold-short of a crossed runway (TAXI <twy> CROSS <rwy>). Route straight to it along the
        // taxiway and stop — the same direction-correct walk a parking destination uses — so the route
        // heads toward and across the runway and terminates just past the far bars, instead of the
        // direction-blind terminus walk picking the wrong way (e.g. back across a parallel runway behind).
        if (ctx.Destination.Kind == DestinationKind.Node && ctx.Destination.TargetNodeId is { } crossDestId && head.HeadNodeId != crossDestId)
        {
            (List<DirectionalEdge>? toCrossEdges, PartialRoute? toCrossHead, double _) = LocalSearchToJunction(head, waypoint.Name, crossDestId, ctx);
            if (toCrossEdges is not null)
            {
                return (toCrossEdges, toCrossHead, null);
            }
            // Node not reachable along this taxiway (the crossing is on an earlier leg): fall through to
            // the natural-terminus walk, where the next named taxiway already anchors direction.
        }

        // Named taxiway at end: walk to natural terminus, biased toward the destination at the
        // first (momentum-free) step. The greedy walk is otherwise direction-blind and picks an
        // arbitrary admissible direction — wrong when the aircraft starts mid-taxiway facing away
        // from the destination (post-landing arrival heading) or when the destination-runway
        // hold-short sits the opposite way along the named taxiway. The bias only breaks ties on
        // the first step; once moving, admissibility constrains direction as before.
        LatLon? bias = ResolveTerminusBias(head, waypoint.Name, ctx);

        // Single-taxiway clearance with a turn hint (e.g. "TAXI >A"): there is no junction transition
        // to bias, so steer the first (momentum-free) step toward the hinted turn from the aircraft's
        // current heading. Only when no destination bias already gives a direction and this is the
        // first taxiway (no preceding one).
        if (bias is null && precedingTaxiway is null && waypoint.TurnHint is { } firstHint && ctx.StartHeadingTrue is { } startHeading)
        {
            bias = ResolveTurnHintBias(head, waypoint.Name, startHeading, firstHint, ctx);
            if (bias is null)
            {
                // No edge on this taxiway departs the hinted way from the aircraft's heading — the turn
                // can't be honored, so advise the controller (the terminus walk still picks a direction).
                ctx.ResolutionAdvisories.Add(TurnHintAdvisory(waypoint.Name, firstHint));
            }
        }

        // No downstream constraint and reached by transitioning from a different taxiway: stop at
        // that intersection rather than committing to a direction along the final taxiway. "TAXI G B"
        // leaves the aircraft at the pure G/B intersection so the controller can then turn it either
        // way on B with a follow-up taxi; walking B here is direction-blind and picks a wrong way.
        // A destination (handled above), a hold-short on the taxiway (a non-null bias), or a >/<
        // turn hint on the final taxiway gives a direction, so those keep walking.
        if (precedingTaxiway is not null && bias is null && waypoint.TurnHint is null && ctx.Destination.Kind == DestinationKind.EndOfLastTaxiway)
        {
            (List<DirectionalEdge>? Edges, PartialRoute? Head, PathfindingFailure? Failure)? terminate = TerminateAtTransitionJunction(
                head,
                precedingTaxiway,
                waypoint.Name,
                ctx
            );
            if (terminate is not null)
            {
                return terminate.Value;
            }
        }

        return WalkToNaturalTerminus(head, waypoint.Name, ctx, bias);
    }

    /// <summary>
    /// Stop the route at the pure intersection of <paramref name="precedingTaxiway"/> and
    /// <paramref name="finalTaxiway"/> instead of walking the final taxiway. Used for a bare final
    /// taxiway with no downstream constraint (e.g. <c>TAXI G B</c>): the aircraft arrives at the
    /// junction and holds, ready to be turned either way on the final taxiway by a follow-up taxi.
    /// Routes from the current head along the preceding taxiway to the canonical (pre-fillet, lowest
    /// id) crossing node. Applies whether the final taxiway extends one way or both ways from the
    /// junction — the clearance gave no onward direction or destination, so committing the aircraft
    /// down the taxiway (OAK <c>TAXI C D</c> walking the full 0.9 nm of D to the 15/33 boundary) is
    /// never what the controller meant. An advisory tells the controller where the aircraft holds.
    /// Returns null — caller falls back to the natural-terminus walk — when the intersection is
    /// unknown or unreachable on the preceding taxiway.
    /// </summary>
    private static (List<DirectionalEdge>? Edges, PartialRoute? Head, PathfindingFailure? Failure)? TerminateAtTransitionJunction(
        PartialRoute head,
        string precedingTaxiway,
        string finalTaxiway,
        SearchContext ctx
    )
    {
        List<DirectionalEdge> edges = [];
        PartialRoute? junctionHead = head;

        GroundNode? intersection = ctx.Layout.FindIntersectionNode(precedingTaxiway, finalTaxiway);
        if (intersection is not null && head.HeadNodeId != intersection.Id)
        {
            (List<DirectionalEdge>? searchEdges, junctionHead, _) = LocalSearchToJunction(head, precedingTaxiway, intersection.Id, ctx);
            if (searchEdges is null || junctionHead is null)
            {
                // The canonical intersection is behind or otherwise unreachable from the junction the
                // preceding segment committed to. The head already sits at (or one fillet node short
                // of) a junction with the final taxiway — that pick is the stop.
                bool headOnFinal =
                    ctx.Layout.Nodes.TryGetValue(head.HeadNodeId, out GroundNode? headNode) && NodeIncidentToTaxiway(headNode, finalTaxiway);
                if (!headOnFinal)
                {
                    return null;
                }

                junctionHead = head;
                edges = [];
            }
            else
            {
                edges = searchEdges;
            }
        }
        else if (intersection is null)
        {
            bool headOnFinal =
                ctx.Layout.Nodes.TryGetValue(head.HeadNodeId, out GroundNode? headNode) && NodeIncidentToTaxiway(headNode, finalTaxiway);
            if (!headOnFinal)
            {
                return null;
            }
        }

        ctx.ResolutionAdvisories.Add(
            $"holding at the {precedingTaxiway}/{finalTaxiway} intersection — route ends at {finalTaxiway}, no destination given"
        );
        ctx.DiagnosticLog?.Invoke($"[terminus-junction] {precedingTaxiway}/{finalTaxiway} stop={junctionHead.HeadNodeId} edges={edges.Count}");
        return (edges, junctionHead, null);
    }

    /// <summary>
    /// Maximum candidate stop nodes evaluated by <see cref="SelectBestParkingStop"/>. The turn-off
    /// onto a parking/spot spur is geometrically near the destination, so the nearest few taxiway
    /// nodes cover the real stop points without one extension search per taxiway node.
    /// </summary>
    private const int MaxParkingStopCandidates = 10;

    /// <summary>
    /// Pick the on-taxiway stop node from which a parking/spot/helipad destination is reachable at
    /// lowest total (walk + extension) cost. Mirrors v1's <c>SelectBestStopNode</c>: the greedy
    /// terminus walk commits to one direction and a single extension from the dead-end terminus is
    /// often inadmissible (a U-turn) or wrong-way, so instead try the taxiway nodes nearest the
    /// destination as stop points — route to each on-taxiway (<see cref="LocalSearchToJunction"/>)
    /// and extend from there (<see cref="ExtendToDestination"/>) — and return the walk to the best
    /// stop node. The caller's <see cref="ExtendToDestination"/> then completes the route from that
    /// node. Returns (null, null) when no candidate reaches the destination — the caller falls back
    /// to the greedy terminus walk.
    /// </summary>
    private static (List<DirectionalEdge>? Edges, PartialRoute? Head, double Cost) SelectBestParkingStop(
        PartialRoute head,
        string taxiwayName,
        int destId,
        SearchContext ctx,
        int maxStopCandidates = MaxParkingStopCandidates
    )
    {
        if (!ctx.Layout.Nodes.TryGetValue(destId, out GroundNode? destNode))
        {
            return (null, null, double.MaxValue);
        }

        List<GroundNode> onTaxiway = ctx.Layout.GetNodesOnTaxiway(taxiwayName);

        // Nearest-by-straight-line stop candidates (original behaviour).
        IEnumerable<GroundNode> nearest = onTaxiway.OrderBy(n => GeoMath.DistanceNm(n.Position, destNode.Position)).Take(maxStopCandidates);

        // Ramp-connector stop candidates: the junction where the cleared taxiway meets each numbered
        // connector or RAMP. A parking/spot gate hangs off a RAMP reached via such a connector, and that
        // junction can sit FARTHER from the gate in straight-line terms than dead-end taxiway nodes
        // physically nearer it (the connector loops away before curving back to the ramp) — so the
        // nearest-by-distance pool above can miss the only real join point. Seeding ONE junction per
        // distinct connector name (the node nearest the gate) guarantees the true join point is evaluated
        // without a nearer connector (e.g. RAMP, which meets the taxiway at many nodes) crowding it out —
        // so the walk+extend reach-cost picker below can select it (e.g. SFO B/T9 for "TAXI B @F1"),
        // mirroring the issue-#235 reach-probe for the no-junction case.
        IEnumerable<GroundNode> connectorJunctions = onTaxiway
            .SelectMany(n => RampConnectorNames(n, taxiwayName).Select(name => (Name: name, Node: n)))
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(x => GeoMath.DistanceNm(x.Node.Position, destNode.Position)).First().Node);

        IEnumerable<GroundNode> candidates = nearest.Concat(connectorJunctions).DistinctBy(n => n.Id);

        List<DirectionalEdge>? bestWalk = null;
        PartialRoute? bestStopHead = null;
        double bestTotal = double.MaxValue;

        foreach (GroundNode? cand in candidates)
        {
            (List<DirectionalEdge>? walkEdges, PartialRoute? candHead, double walkCost) = LocalSearchToJunction(head, taxiwayName, cand.Id, ctx);
            if (walkEdges is null || candHead is null)
            {
                continue;
            }

            (List<DirectionalEdge>? extEdges, PathfindingFailure? extFailure) = ExtendToDestination(candHead, destId, ctx);
            if (extFailure is not null || extEdges is null)
            {
                continue;
            }

            double extCost = 0.0;
            foreach (DirectionalEdge e in extEdges)
            {
                extCost += e.DistanceNm;
            }

            double total = walkCost + extCost;
            if (total < bestTotal)
            {
                bestTotal = total;
                bestWalk = walkEdges;
                bestStopHead = candHead;
            }
        }

        if (bestWalk is null)
        {
            return (null, null, double.MaxValue);
        }

        ctx.DiagnosticLog?.Invoke($"[beststop] twy={taxiwayName} dest={destId} stop={bestStopHead!.HeadNodeId} total={bestTotal:F3}");
        return (bestWalk, bestStopHead, bestTotal);
    }

    /// <summary>
    /// The distinct numbered-connector / RAMP taxiway names that branch off <paramref name="clearedTaxiway"/>
    /// at <paramref name="node"/> — the ramp connectors reachable by turning off the cleared taxiway here
    /// (e.g. <c>T9</c> at the B/T9 junction). A numbered/RAMP name is non-letter-only
    /// (<see cref="SearchContext.IsLetterOnlyTaxiway"/> is false); the cleared taxiway itself and
    /// runway-crossing edges (<c>RWY…</c>, not ramp connectors) are excluded. Arc edges carry several
    /// names joined with <c>" - "</c>; each qualifying member counts.
    /// </summary>
    private static IEnumerable<string> RampConnectorNames(GroundNode node, string clearedTaxiway)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (IGroundEdge edge in node.Edges)
        {
            foreach (string name in edge.TaxiwayName.Split(" - ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (
                    !name.Equals(clearedTaxiway, StringComparison.OrdinalIgnoreCase)
                    && !IsRunwayWaypoint(name)
                    && !SearchContext.IsLetterOnlyTaxiway(name)
                )
                {
                    names.Add(name);
                }
            }
        }

        return names;
    }

    /// <summary>
    /// Number of on-taxiway stop candidates <see cref="ProbeParkingReachCost"/> evaluates per junction
    /// candidate. Fewer than <see cref="MaxParkingStopCandidates"/>: the probe only needs a comparative
    /// reach cost to separate "reachable this direction" from "only via a loop", and it runs once per
    /// junction candidate, so it is kept cheap. The committed junction is later re-resolved by
    /// <see cref="ExpandLastWaypoint"/> with the full candidate set for the actual edges.
    /// </summary>
    private const int ProbeStopCandidateCap = 4;

    /// <summary>
    /// Realized cost of resolving a parking/spot/helipad destination from <paramref name="segHead"/>
    /// along <paramref name="toTaxiway"/> — mirrors <see cref="ExpandLastWaypoint"/>'s parking-branch
    /// order (direct one-hop-off via <see cref="LocalSearchToJunction"/>, else best on-taxiway stop via
    /// <see cref="SelectBestParkingStop"/>). Used to score a FINAL-transition junction candidate by
    /// whether the destination is admissibly and cheaply reachable from it. Returns
    /// <see cref="TailUnresolvablePenaltyNm"/> when the destination is not reachable staying on the
    /// taxiway from this junction's arrival bearing, so a junction that would strand the terminus
    /// (forcing the whole-layout loop) loses to one from which the destination is reachable.
    /// </summary>
    private static double ProbeParkingReachCost(PartialRoute segHead, string toTaxiway, int destId, SearchContext ctx)
    {
        if (segHead.HeadNodeId == destId)
        {
            return 0.0;
        }

        // Mirror ResolveSequence's per-segment VisitedNodeIds reset (the real terminus walk starts from
        // a reset head, so the probe must too, or it under-counts reachability).
        PartialRoute probeHead = segHead with
        {
            VisitedNodeIds = VisitedNodeSet.Single(segHead.HeadNodeId),
        };

        (List<DirectionalEdge>? directEdges, PartialRoute? _, double directCost) = LocalSearchToJunction(probeHead, toTaxiway, destId, ctx);
        if (directEdges is not null)
        {
            return directCost;
        }

        (List<DirectionalEdge>? stopEdges, PartialRoute? _, double stopCost) = SelectBestParkingStop(
            probeHead,
            toTaxiway,
            destId,
            ctx,
            ProbeStopCandidateCap
        );
        return stopEdges is not null ? stopCost : TailUnresolvablePenaltyNm;
    }

    /// <summary>
    /// Direction penalty for a bridge candidate with no admissible on-taxiway continuation from
    /// the bridged arrival bearing. Large enough that any candidate WITH a dest-ward continuation
    /// always wins, but finite so a dead-end candidate stays selectable when it is the only option.
    /// </summary>
    private const double BridgeNoOnwardPenaltyNm = 100.0;

    /// <summary>
    /// Bridge the search head from a start node that is not yet on <paramref name="taxiwayName"/>
    /// (typically a parking/RAMP spot, or a runway hold-short on a crossing taxiway) onto a node
    /// carrying an edge on that taxiway, via a bounded BFS. Mirrors v1's <c>BfsToTaxiway</c> but is
    /// direction-aware: among the taxiway-access nodes reachable within <see cref="MaxBridgeHops"/>
    /// hops, it picks the one whose admissible on-taxiway continuation gets nearest
    /// <paramref name="bias"/> (the next-junction or destination position). A direction-blind
    /// nearest pick can enter the taxiway through a corner arc that commits the head to the wrong
    /// branch, after which the correct branch fails the U-turn admissibility check and the route
    /// detours the long way round. Returns empty edges and the unchanged head when the start is
    /// already on the taxiway, or when no taxiway node is reachable — the caller then proceeds and
    /// the per-segment detour remains the fallback.
    /// </summary>
    private static (List<DirectionalEdge> Edges, PartialRoute Head) BridgeStartToTaxiway(
        PartialRoute head,
        string taxiwayName,
        LatLon? bias,
        SearchContext ctx
    )
    {
        if (!ctx.Layout.Nodes.TryGetValue(head.HeadNodeId, out GroundNode? startNode))
        {
            return ([], head);
        }

        foreach (IGroundEdge edge in startNode.Edges)
        {
            if (edge.MatchesTaxiway(taxiwayName))
            {
                return ([], head);
            }
        }

        (List<DirectionalEdge>? Edges, PartialRoute? Head, double Score, bool HasOnward) best = PickBridgeCandidate(
            head,
            taxiwayName,
            bias,
            ctx,
            MaxBridgeHops
        );

        // Every access node within the shallow reach commits the head to a U-turn onto the taxiway (the
        // shallow pick still carries BridgeNoOnwardPenaltyNm). The real entry may simply lie a hop or two
        // further along the gate's lead-out lane, so look deeper before settling for the dead end.
        if (best.Edges is not null && !best.HasOnward)
        {
            (List<DirectionalEdge>? Edges, PartialRoute? Head, double Score, bool HasOnward) deeper = PickBridgeCandidate(
                head,
                taxiwayName,
                bias,
                ctx,
                MaxBridgeHopsDeep
            );
            if (deeper.Edges is not null && deeper.HasOnward)
            {
                ctx.DiagnosticLog?.Invoke(
                    $"[bridge] deepened to {MaxBridgeHopsDeep} hops: shallow pick {best.Head!.HeadNodeId} has no "
                        + $"admissible continuation on {taxiwayName}"
                );
                best = deeper;
            }
        }

        if (best.Edges is null || best.Head is null)
        {
            return ([], head);
        }

        ctx.DiagnosticLog?.Invoke(
            $"[bridge] start={head.HeadNodeId} → {best.Head.HeadNodeId} onto {taxiwayName} ({best.Edges.Count} edges, score={best.Score:F3})"
        );
        return (best.Edges, best.Head);
    }

    /// <summary>
    /// Collect the access nodes within <paramref name="maxHops"/> of the head and pick the best one.
    /// <c>Edges</c> is null when no access node is reachable within the cap; <c>HasOnward</c> reports whether
    /// the chosen candidate has an admissible onward edge on the taxiway from its bridged arrival bearing.
    /// </summary>
    private static (List<DirectionalEdge>? Edges, PartialRoute? Head, double Score, bool HasOnward) PickBridgeCandidate(
        PartialRoute head,
        string taxiwayName,
        LatLon? bias,
        SearchContext ctx,
        int maxHops
    )
    {
        (List<int>? candidates, Dictionary<int, (int ParentId, IGroundEdge Edge)>? cameFrom) = CollectBridgeCandidates(
            head.HeadNodeId,
            taxiwayName,
            ctx,
            maxHops
        );

        List<DirectionalEdge>? bestEdges = null;
        PartialRoute? bestHead = null;
        double bestScore = double.MaxValue;
        double bestCost = double.MaxValue;
        bool bestHasOnward = false;

        foreach (int candidateId in candidates)
        {
            (List<DirectionalEdge>? candEdges, PartialRoute? candHead) = BuildBridgePath(head, candidateId, cameFrom, ctx);
            if (candEdges.Count == 0)
            {
                continue;
            }

            double cost = candHead.AccumulatedCost - head.AccumulatedCost;
            // Score each candidate as f = g + h: the cost of the bridge path itself (g, which already
            // carries distance, the turn budget, and the reverse-arc penalty) plus how well its
            // continuation heads toward where the route must go (h, the bias-proximity probe). Using h
            // alone let a candidate win purely because its taxiway entry sat closest to the bias, even
            // when reaching it meant threading a doubling-back ramp cross-connector (a spot whose only
            // link is a tight fillet arc pointing the wrong way); folding g back in keeps the smooth,
            // shorter straight bridge ahead of the zigzag. With no bias (no destination / next-junction
            // signal) fall back to the nearest access node — the original nearest-first behaviour.
            double score = bias is { } b ? cost + ScoreBridgeCandidate(candHead, taxiwayName, b, ctx) : cost;

            if ((score < bestScore - 1e-9) || ((Math.Abs(score - bestScore) <= 1e-9) && (cost < bestCost)))
            {
                bestScore = score;
                bestCost = cost;
                bestEdges = candEdges;
                bestHead = candHead;
                bestHasOnward = AdmissibleOnwardNeighbors(candHead, taxiwayName, ctx).Any();
            }
        }

        return (bestEdges, bestHead, bestScore, bestHasOnward);
    }

    /// <summary>
    /// BFS from <paramref name="startId"/> collecting every node within <see cref="MaxBridgeHops"/>
    /// hops that carries an edge on <paramref name="taxiwayName"/>. Unlike a stop-at-first BFS, it
    /// keeps exploring past a taxiway-access node so access nodes reachable only THROUGH another one
    /// (e.g. the next junction node further along the connecting taxiway) are also discovered —
    /// these are exactly the alternate-direction entries a direction-blind nearest pick would miss.
    /// Returns the candidate node ids and the BFS-tree parent map for path reconstruction.
    /// </summary>
    private static (List<int> Candidates, Dictionary<int, (int ParentId, IGroundEdge Edge)> CameFrom) CollectBridgeCandidates(
        int startId,
        string taxiwayName,
        SearchContext ctx,
        int maxHops
    )
    {
        var visited = new HashSet<int> { startId };
        var queue = new Queue<(int NodeId, int Depth)>();
        var cameFrom = new Dictionary<int, (int ParentId, IGroundEdge Edge)>();
        var candidates = new List<int>();
        queue.Enqueue((startId, 0));

        while (queue.Count > 0)
        {
            (int nodeId, int depth) = queue.Dequeue();
            if (!ctx.Layout.Nodes.TryGetValue(nodeId, out GroundNode? node))
            {
                continue;
            }

            foreach (IGroundEdge edge in node.Edges)
            {
                // The bridge may not hop over runway pavement the route isn't allowed on.
                if (ctx.IsForbiddenCenterlineEdge(edge))
                {
                    continue;
                }

                GroundNode neighbor = edge.OtherNode(node);
                if (!visited.Add(neighbor.Id))
                {
                    continue;
                }

                cameFrom[neighbor.Id] = (nodeId, edge);

                if (neighbor.Edges.Any(e => e.MatchesTaxiway(taxiwayName)))
                {
                    candidates.Add(neighbor.Id);
                }

                // A runway hold-short is a degree-2 pass-through the graph-build inserts on the
                // taxiway; it doesn't consume a hop, so the taxiway-access node stays reachable no
                // matter where the hold-short bar sits. (Non-hold-short nodes cost a hop as before,
                // preserving the legacy reach for bridges that traverse no hold-short.)
                int nextDepth = neighbor.Type == GroundNodeType.RunwayHoldShort ? depth : depth + 1;
                if (nextDepth < maxHops)
                {
                    queue.Enqueue((neighbor.Id, nextDepth));
                }
            }
        }

        return (candidates, cameFrom);
    }

    /// <summary>
    /// Reconstruct the directed bridge edges and the resulting <see cref="PartialRoute"/> head for
    /// the path from <paramref name="head"/> to <paramref name="targetId"/> recorded in
    /// <paramref name="cameFrom"/>. Returns empty edges when the chain cannot be walked.
    /// </summary>
    private static (List<DirectionalEdge> Edges, PartialRoute Head) BuildBridgePath(
        PartialRoute head,
        int targetId,
        Dictionary<int, (int ParentId, IGroundEdge Edge)> cameFrom,
        SearchContext ctx
    )
    {
        var pathNodes = new List<int>();
        int trace = targetId;
        while (trace != head.HeadNodeId)
        {
            pathNodes.Add(trace);
            if (!cameFrom.TryGetValue(trace, out (int ParentId, IGroundEdge Edge) step))
            {
                return ([], head);
            }

            trace = step.ParentId;
        }

        pathNodes.Reverse();

        var bridgeEdges = new List<DirectionalEdge>(pathNodes.Count);
        PartialRoute current = head;
        foreach (int id in pathNodes)
        {
            (int _, IGroundEdge? edge) = cameFrom[id];
            if (
                !ctx.Layout.Nodes.TryGetValue(current.HeadNodeId, out GroundNode? fromNode)
                || !ctx.Layout.Nodes.TryGetValue(id, out GroundNode? toNode)
            )
            {
                break;
            }

            double arrival = GeometricAdmissibility.GetArrivalBearing(edge, fromNode, toNode);
            double cost = RouteCostFunction.IncrementalCost(current, edge, toNode, ctx);
            string twyName = RouteCostFunction.ResolveTaxiwayName(edge, current.HeadNodeId);

            bridgeEdges.Add(edge.Directed(fromNode, toNode));
            current = current with
            {
                HeadNodeId = id,
                ArrivalBearing = arrival,
                LastEdge = edge,
                LastTaxiwayName = twyName,
                Previous = current,
                Depth = current.Depth + 1,
                AccumulatedCost = current.AccumulatedCost + cost,
                VisitedNodeIds = current.VisitedNodeIds.Add(id),
            };
        }

        return (bridgeEdges, current);
    }

    /// <summary>
    /// Score a bridge candidate by the distance from <paramref name="bias"/> of the nearest node
    /// reachable by one admissible on-taxiway step from the bridged head — i.e. how well the
    /// candidate's available continuation heads toward where the route must go. A candidate whose
    /// only taxiway edges are inadmissible from the bridged arrival bearing (a corner-arc entry that
    /// would force an immediate U-turn) gets its own distance plus
    /// <see cref="BridgeNoOnwardPenaltyNm"/>, so any candidate with a real dest-ward continuation
    /// wins. Lower is better.
    /// </summary>
    private static double ScoreBridgeCandidate(PartialRoute candHead, string taxiwayName, LatLon bias, SearchContext ctx)
    {
        if (!ctx.Layout.Nodes.TryGetValue(candHead.HeadNodeId, out GroundNode? node))
        {
            return double.MaxValue;
        }

        double best = double.MaxValue;
        foreach (GroundNode neighbor in AdmissibleOnwardNeighbors(candHead, taxiwayName, ctx))
        {
            double d = GeoMath.DistanceNm(neighbor.Position, bias);
            if (d < best)
            {
                best = d;
            }
        }

        if (best == double.MaxValue)
        {
            return GeoMath.DistanceNm(node.Position, bias) + BridgeNoOnwardPenaltyNm;
        }

        return best;
    }

    /// <summary>
    /// Nodes reachable from the bridged head by one admissible, not-yet-visited step along
    /// <paramref name="taxiwayName"/>. Empty when the candidate's only taxiway edges would force an immediate
    /// U-turn from the bridged arrival bearing — the dead-end entry both the score penalty and the deeper
    /// bridge pass key on.
    /// </summary>
    private static IEnumerable<GroundNode> AdmissibleOnwardNeighbors(PartialRoute candHead, string taxiwayName, SearchContext ctx)
    {
        if (!ctx.Layout.Nodes.TryGetValue(candHead.HeadNodeId, out GroundNode? node))
        {
            yield break;
        }

        foreach (IGroundEdge edge in node.Edges)
        {
            if (!edge.MatchesTaxiway(taxiwayName))
            {
                continue;
            }

            GroundNode neighbor = edge.OtherNode(node);
            if (candHead.VisitedNodeIds.Contains(neighbor.Id))
            {
                continue;
            }

            if (GeometricAdmissibility.IsAdmissible(candHead, edge, neighbor, ctx.Category))
            {
                yield return neighbor;
            }
        }
    }

    /// <summary>
    /// The position the bridge should head toward when entering the first cleared taxiway: the
    /// junction with the next cleared taxiway when the route continues, otherwise the destination
    /// (parking/spot/node target, or the nearest destination-runway hold-short on the taxiway).
    /// Null when there is no directional signal — the bridge then keeps its nearest-access pick.
    /// </summary>
    private static LatLon? ResolveBridgeBias(IReadOnlyList<WaypointToken> tokens, PartialRoute head, SearchContext ctx)
    {
        WaypointToken first = tokens[0];

        // Route continues past the first taxiway: head toward the junction with the next token.
        if (tokens.Count >= 2)
        {
            WaypointToken next = tokens[1];
            if (next.IsNodeRef)
            {
                if (ctx.Layout.Nodes.TryGetValue(next.ResolvedNodeId, out GroundNode? nextNode))
                {
                    return nextNode.Position;
                }
            }
            else
            {
                List<GroundNode> junctions = FindJunctionCandidates(ctx.Layout, first.Name, next.Name);
                if (junctions.Count > 0)
                {
                    double sumLat = 0;
                    double sumLon = 0;
                    foreach (GroundNode j in junctions)
                    {
                        sumLat += j.Position.Lat;
                        sumLon += j.Position.Lon;
                    }

                    return new LatLon(sumLat / junctions.Count, sumLon / junctions.Count);
                }
            }
        }

        // Single cleared taxiway: head toward the destination node when known.
        if (ctx.Destination.TargetNodeId is { } destId && ctx.Layout.Nodes.TryGetValue(destId, out GroundNode? destNode))
        {
            return destNode.Position;
        }

        // Runway destination has no single node — reuse the terminus bias (nearest
        // destination-runway hold-short on the first taxiway).
        return ResolveTerminusBias(head, first.Name, ctx);
    }

    // -----------------------------------------------------------------------
    // Named taxiway to named taxiway
    // -----------------------------------------------------------------------

    private static (List<DirectionalEdge>? Edges, PartialRoute? Head, PathfindingFailure? Failure) RouteNamedToNamed(
        PartialRoute head,
        string fromTaxiway,
        string toTaxiway,
        string? lookaheadTaxiway,
        IReadOnlyList<WaypointToken> tokens,
        int index,
        bool enableLookahead,
        List<ConnectorInsertion> insertions,
        SearchContext ctx
    )
    {
        // Find all junction candidates: nodes on fromTaxiway with at least one edge onto toTaxiway.
        List<GroundNode> junctionCandidates = FindJunctionCandidates(ctx.Layout, fromTaxiway, toTaxiway);

        // When the route continues past toTaxiway and look-ahead is enabled, score each junction
        // by the cost of resolving the remaining sequence from it (recursive probe). Otherwise
        // fall back to the cheap geometric look-ahead anchor.
        bool useTailProbe = enableLookahead && HasMeaningfulTail(tokens, index);

        // Lookahead anchor: centroid of junctions between toTaxiway and the next-next taxiway.
        // Used to bias junction selection so that the chosen junction's toTaxiway-side points
        // toward where the route will continue, avoiding wrong-side picks that force a U-turn
        // immediately after the transition.
        (double Lat, double Lon)? lookaheadAnchor = null;
        if (lookaheadTaxiway is not null)
        {
            List<GroundNode> anchors = FindJunctionCandidates(ctx.Layout, toTaxiway, lookaheadTaxiway);
            if (anchors.Count > 0)
            {
                double sumLat = 0;
                double sumLon = 0;
                foreach (GroundNode a in anchors)
                {
                    sumLat += a.Position.Lat;
                    sumLon += a.Position.Lon;
                }

                lookaheadAnchor = (sumLat / anchors.Count, sumLon / anchors.Count);
            }
        }

        // Final named transition into a runway destination: there is no next named taxiway to
        // anchor toward, but the destination runway's own hold-short on toTaxiway is the de-facto
        // next waypoint. Anchor junction selection toward it so the chosen junction's toTaxiway
        // side leads to the runway — otherwise the cheapest (nearest-along-fromTaxiway) junction
        // can commit the following terminus walk to the wrong end of toTaxiway, after which the
        // correct direction fails the U-turn admissibility check and the route detours the long
        // way around to the same runway (OAK "TAXI D J C 33": C-from-the-near-junction only
        // continues toward A, forcing a loop via B/28L-10R/P back to 33).
        if (
            lookaheadAnchor is null
            && lookaheadTaxiway is null
            && ctx.Destination.Kind == DestinationKind.Runway
            && ctx.Destination.RunwayId is { } destRunwayId
        )
        {
            lookaheadAnchor = ResolveRunwayHoldShortAnchorOnTaxiway(toTaxiway, destRunwayId, ctx);
        }

        // Final named transition into a parking/spot/helipad destination: the destination node hangs
        // off toTaxiway (e.g. OAK "TAXI G D @NEW1" — NEW1 is on a ramp stub off D's north end). Anchor
        // junction selection toward it for the same reason as the runway case above — otherwise the
        // cheapest junction can land the aircraft on toTaxiway facing away from the destination, the
        // destination-aware terminus search then can't reach it without an inadmissible U-turn, and the
        // route walks to the far terminus and detours the long way back.
        if (
            lookaheadAnchor is null
            && lookaheadTaxiway is null
            && ctx.Destination.Kind is DestinationKind.Parking or DestinationKind.Spot or DestinationKind.Helipad
            && ctx.Destination.TargetNodeId is { } destNodeId
            && ctx.Layout.Nodes.TryGetValue(destNodeId, out GroundNode? destNode)
        )
        {
            lookaheadAnchor = (destNode.Position.Lat, destNode.Position.Lon);
        }

        ctx.DiagnosticLog?.Invoke(
            $"[segment] twy={fromTaxiway}→{toTaxiway} head={head.HeadNodeId} junctions={junctionCandidates.Count} "
                + $"lookahead={lookaheadTaxiway ?? "(none)"}"
        );

        if (junctionCandidates.Count == 0)
        {
            // No direct junction between the two cleared taxiways. Inside a look-ahead probe the
            // detour is suppressed (a continuation needing one is a negative signal). At the top
            // level we detour and — because zero junction candidates verifies there is genuinely
            // no edge or arc joining fromTaxiway and toTaxiway — record the inserted connector so
            // the controller is notified of the mandatory insertion rather than warned about it.
            if (!enableLookahead)
            {
                return (null, null, DetourSuppressedFailure(fromTaxiway, toTaxiway, head));
            }

            (List<DirectionalEdge>? Edges, PartialRoute? Head, PathfindingFailure? Failure) detour = TryDetour(head, fromTaxiway, toTaxiway, ctx);
            if (detour.Failure is null && detour.Edges is not null)
            {
                RecordConnectorInsertion(insertions, fromTaxiway, toTaxiway, detour.Edges, tokens);
            }

            return detour;
        }

        // For each junction candidate, run a bounded local search from the current head.
        (List<DirectionalEdge>? bestEdges, PartialRoute? bestHead, double bestCost) = (null, null, double.MaxValue);
        GroundNode? bestJunction = null;
        List<DirectionalEdge>? bestSegEdges = null;
        double bestArrivalBearing = 0.0;

        foreach (GroundNode junctionNode in junctionCandidates)
        {
            (List<DirectionalEdge>? segEdges, PartialRoute? segHead, double cost) = LocalSearchToJunction(head, fromTaxiway, junctionNode.Id, ctx);
            if (segEdges is null)
            {
                continue;
            }

            // Look-ahead: prefer the junction whose continuation through the rest of the
            // sequence is cheapest (recursive probe), defeating first-match picks that strand
            // the route on the wrong leg of a V-shaped taxiway. When there is no meaningful tail
            // (final transition) into a parking/spot/helipad destination, probe the realized cost of
            // reaching that destination from this junction — the destination is the de-facto next
            // waypoint (mirrors the runway anchor above), so a junction from which it is only
            // reachable via a whole-layout loop loses to one from which it is admissibly reachable.
            // Otherwise (inside a probe, or a runway/node/end-of-taxiway destination) use the cheap
            // geometric heuristic.
            double continuationCost;
            if (useTailProbe)
            {
                continuationCost = ProbeTailCost(segHead!, tokens, index + 1, ctx);
            }
            else if (
                enableLookahead
                && !HasMeaningfulTail(tokens, index)
                && ctx.Destination.Kind is DestinationKind.Parking or DestinationKind.Spot or DestinationKind.Helipad
                && ctx.Destination.TargetNodeId is { } probeDestId
            )
            {
                continuationCost = ProbeParkingReachCost(segHead!, toTaxiway, probeDestId, ctx);
            }
            else
            {
                continuationCost = ComputeLookaheadPenalty(junctionNode, toTaxiway, lookaheadAnchor);
            }

            // Turn-direction hints (issue #172 W7): prefer the junction that realises the controller's
            // >/< turn. Both penalties are additive and finite (< the tail-unresolvable penalty), so a
            // hint only re-ranks otherwise-feasible candidates and never strands the route — best-effort.
            double hintCost =
                TurnHintOntoTaxiwayPenalty(junctionNode, toTaxiway, segHead!.ArrivalBearing, tokens, index)
                + FirstTaxiwayTurnHintPenalty(segEdges, tokens, index, ctx);
            double totalCost = cost + continuationCost + hintCost;

            ctx.DiagnosticLog?.Invoke(
                $"[junction-pick] {fromTaxiway}→{toTaxiway} candidate={junctionNode.Id} cost={cost:F3} "
                    + $"continuation+={continuationCost:F3} hint+={hintCost:F3} total={totalCost:F3}"
            );

            if (totalCost < bestCost)
            {
                bestCost = totalCost;
                bestEdges = segEdges;
                bestHead = segHead;
                bestJunction = junctionNode;
                bestSegEdges = segEdges;
                bestArrivalBearing = segHead!.ArrivalBearing;
            }
        }

        // Record an advisory when the committed junction couldn't honor a turn hint (the hinted-direction
        // edge wasn't available there). Only at the top level — probes resolve with enableLookahead off, so
        // their speculative picks never reach the controller. The penalty helpers return 0 when there is no
        // hint, so a non-zero result is exactly "hinted but unhonored".
        if (enableLookahead && bestEdges is not null && bestJunction is not null && bestSegEdges is not null)
        {
            if (
                TurnHintOntoTaxiwayPenalty(bestJunction, toTaxiway, bestArrivalBearing, tokens, index) > 0
                && tokens[index + 1].TurnHint is { } ontoHint
            )
            {
                ctx.ResolutionAdvisories.Add(TurnHintAdvisory(toTaxiway, ontoHint));
            }

            if (FirstTaxiwayTurnHintPenalty(bestSegEdges, tokens, index, ctx) > 0 && tokens[0].TurnHint is { } firstHint)
            {
                ctx.ResolutionAdvisories.Add(TurnHintAdvisory(tokens[0].Name, firstHint));
            }
        }

        if (bestEdges is null)
        {
            // All junction candidates failed: attempt detour (suppressed inside a probe).
            return enableLookahead
                ? TryDetour(head, fromTaxiway, toTaxiway, ctx)
                : (null, null, DetourSuppressedFailure(fromTaxiway, toTaxiway, head));
        }

        ctx.DiagnosticLog?.Invoke(
            $"[committed] twy={fromTaxiway}→{toTaxiway} via={bestHead!.HeadNodeId} " + $"edges={bestEdges.Count} cost={bestCost:F3}"
        );

        return (bestEdges, bestHead, null);
    }

    /// <summary>
    /// Penalise junction candidates whose toTaxiway-side does not point toward
    /// <paramref name="lookaheadAnchor"/>. A junction is "well-aligned" if it has at least
    /// one toTaxiway-edge whose other endpoint is geographically closer to the anchor than
    /// the junction itself — meaning continuing along that edge moves the aircraft toward
    /// the next-next taxiway. Junctions with no such edge would force a U-turn on
    /// toTaxiway after the transition.
    /// </summary>
    private const double LookaheadMisalignedPenaltyNm = 10.0;

    private static double ComputeLookaheadPenalty(GroundNode junction, string toTaxiway, (double Lat, double Lon)? anchor)
    {
        if (anchor is not { } a)
        {
            return 0.0;
        }

        double junctionToAnchorNm = GeoMath.DistanceNm(junction.Position.Lat, junction.Position.Lon, a.Lat, a.Lon);

        foreach (IGroundEdge edge in junction.Edges)
        {
            if (!edge.MatchesTaxiway(toTaxiway))
            {
                continue;
            }

            GroundNode neighbor = edge.OtherNode(junction);
            double neighborToAnchorNm = GeoMath.DistanceNm(neighbor.Position.Lat, neighbor.Position.Lon, a.Lat, a.Lon);
            if (neighborToAnchorNm < junctionToAnchorNm)
            {
                // At least one toTaxiway-edge moves us closer to the anchor — junction OK.
                return 0.0;
            }
        }

        // Every toTaxiway-edge moves away from the anchor — picking this junction would
        // require a U-turn on toTaxiway to reach the next-next taxiway.
        return LookaheadMisalignedPenaltyNm;
    }

    // -----------------------------------------------------------------------
    // Turn-direction hints (issue #172 W7)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Penalty (nm) added to a junction candidate whose turn onto the hinted taxiway does not match the
    /// controller's <c>&gt;</c>/<c>&lt;</c> hint. Large enough to dominate any real taxi-distance or
    /// look-ahead difference so the hinted turn wins whenever it is feasible, but well below
    /// <see cref="TailUnresolvablePenaltyNm"/> so an infeasible hint never strands the route (best-effort).
    /// </summary>
    private const double TurnHintMismatchPenaltyNm = 50.0;

    /// <summary>Minimum signed turn (deg) for an edge to count as a left/right turn rather than straight-through.</summary>
    private const double TurnHintDeadbandDeg = 10.0;

    /// <summary>Signed turn from <paramref name="fromBearing"/> to <paramref name="toBearing"/> in (-180, 180];
    /// positive is right (clockwise).</summary>
    private static double SignedTurnDeg(double fromBearing, double toBearing)
    {
        double d = (toBearing - fromBearing) % 360.0;
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

    private static bool TurnMatchesHint(double signedTurnDeg, TurnDirection hint) =>
        hint == TurnDirection.Right ? signedTurnDeg > TurnHintDeadbandDeg : signedTurnDeg < -TurnHintDeadbandDeg;

    /// <summary>
    /// Controller-facing advisory when a turn hint couldn't be honored: the route turned the other way
    /// because the hinted direction wasn't reachable at the committed junction. Surfaced in the TAXI echo.
    /// </summary>
    private static string TurnHintAdvisory(string taxiway, TurnDirection requested)
    {
        string requestedWord = requested == TurnDirection.Right ? "right" : "left";
        string otherWord = requested == TurnDirection.Right ? "left" : "right";
        return $"Unable {requestedWord} turn onto {taxiway} — taxiing {otherWord} instead";
    }

    /// <summary>
    /// Penalise a junction candidate when the hint on the taxiway being entered (token
    /// <paramref name="fromIndex"/>+1) cannot be realised there: zero when at least one of the
    /// junction's onward edges on that taxiway turns the hinted way from the arrival bearing, else
    /// <see cref="TurnHintMismatchPenaltyNm"/>. No hint on the entered taxiway ⇒ zero.
    /// </summary>
    private static double TurnHintOntoTaxiwayPenalty(
        GroundNode junction,
        string toTaxiway,
        double arrivalBearing,
        IReadOnlyList<WaypointToken> tokens,
        int fromIndex
    )
    {
        if (fromIndex + 1 >= tokens.Count || tokens[fromIndex + 1].TurnHint is not { } hint)
        {
            return 0.0;
        }

        foreach (IGroundEdge edge in junction.Edges)
        {
            if (!edge.MatchesTaxiway(toTaxiway))
            {
                continue;
            }

            GroundNode neighbor = edge.OtherNode(junction);
            double departure = edge.Directed(junction, neighbor).DepartureBearing;
            if (TurnMatchesHint(SignedTurnDeg(arrivalBearing, departure), hint))
            {
                return 0.0;
            }
        }

        return TurnHintMismatchPenaltyNm;
    }

    /// <summary>
    /// Penalise a first-taxiway junction candidate (token 0) when the initial direction along that
    /// taxiway — the departure bearing of the candidate's first edge — does not match the hint on
    /// token 0 relative to the aircraft's current heading (<see cref="SearchContext.StartHeadingTrue"/>).
    /// This is how "right onto A" picks which way along A the route starts. Zero unless this is the
    /// first segment, token 0 carries a hint, and the start heading is known.
    /// </summary>
    private static double FirstTaxiwayTurnHintPenalty(
        IReadOnlyList<DirectionalEdge> segEdges,
        IReadOnlyList<WaypointToken> tokens,
        int fromIndex,
        SearchContext ctx
    )
    {
        if (
            fromIndex != 0
            || tokens.Count == 0
            || tokens[0].TurnHint is not { } hint
            || ctx.StartHeadingTrue is not { } startHeading
            || segEdges.Count == 0
        )
        {
            return 0.0;
        }

        return TurnMatchesHint(SignedTurnDeg(startHeading, segEdges[0].DepartureBearing), hint) ? 0.0 : TurnHintMismatchPenaltyNm;
    }

    /// <summary>
    /// First-step bias for a single-taxiway clearance with a turn hint (e.g. <c>TAXI &gt;A</c>): the
    /// position of the neighbour reached by the taxiway edge whose departure from the start is the
    /// hinted turn relative to <paramref name="startHeadingTrue"/>. Null when no edge matches — the
    /// terminus walk then keeps its admissibility-only direction.
    /// </summary>
    private static LatLon? ResolveTurnHintBias(PartialRoute head, string taxiwayName, double startHeadingTrue, TurnDirection hint, SearchContext ctx)
    {
        if (!ctx.Layout.Nodes.TryGetValue(head.HeadNodeId, out GroundNode? node))
        {
            return null;
        }

        foreach (IGroundEdge edge in node.Edges)
        {
            if (!edge.MatchesTaxiway(taxiwayName))
            {
                continue;
            }

            GroundNode neighbor = edge.OtherNode(node);
            double departure = edge.Directed(node, neighbor).DepartureBearing;
            if (TurnMatchesHint(SignedTurnDeg(startHeadingTrue, departure), hint))
            {
                return neighbor.Position;
            }
        }

        return null;
    }

    /// <summary>
    /// Centroid of the destination runway's hold-short nodes that lie on <paramref name="taxiway"/>,
    /// used as the look-ahead anchor for the final named transition when the route ends at a runway
    /// (there is no next named taxiway). Steers junction selection toward the junction whose
    /// taxiway side leads to the runway's own hold-short, so the following terminus walk heads the
    /// right way along the taxiway instead of committing to the opposite end and detouring back.
    /// Null when no hold-short for the runway sits on the taxiway (the runway is reached via a numbered
    /// variant, which <see cref="TryVariantExtension"/> handles later, or via a connector stub off the
    /// taxiway, which <see cref="TryRunwayConnectorFallback"/> handles once the whole walk has failed) — in
    /// which case junction selection keeps its prior cost-only behaviour.
    /// </summary>
    private static (double Lat, double Lon)? ResolveRunwayHoldShortAnchorOnTaxiway(string taxiway, string runwayId, SearchContext ctx)
    {
        double sumLat = 0;
        double sumLon = 0;
        int count = 0;
        foreach (GroundNode node in ctx.Layout.Nodes.Values)
        {
            if (!IsRunwayHoldShort(node.Id, runwayId, ctx))
            {
                continue;
            }

            bool onTaxiway = false;
            foreach (IGroundEdge edge in node.Edges)
            {
                if (edge.MatchesTaxiway(taxiway))
                {
                    onTaxiway = true;
                    break;
                }
            }

            if (!onTaxiway)
            {
                continue;
            }

            sumLat += node.Position.Lat;
            sumLon += node.Position.Lon;
            count++;
        }

        return count > 0 ? (sumLat / count, sumLon / count) : null;
    }

    /// <summary>
    /// Find all nodes on <paramref name="fromTaxiway"/> that have at least one edge belonging
    /// to <paramref name="toTaxiway"/>. These are the junction candidates between the two taxiways.
    /// </summary>
    private static List<GroundNode> FindJunctionCandidates(AirportGroundLayout layout, string fromTaxiway, string toTaxiway)
    {
        var result = new List<GroundNode>();
        List<GroundNode> onFromTaxiway = layout.GetNodesOnTaxiway(fromTaxiway);

        foreach (GroundNode node in onFromTaxiway)
        {
            foreach (IGroundEdge edge in node.Edges)
            {
                if (edge.MatchesTaxiway(toTaxiway))
                {
                    result.Add(node);
                    break;
                }
            }
        }

        return result;
    }

    // -----------------------------------------------------------------------
    // Local best-first search within a single taxiway to a target junction node
    // -----------------------------------------------------------------------

    /// <summary>
    /// Run a bounded best-first search from <paramref name="head"/> to <paramref name="junctionNodeId"/>,
    /// constrained to edges on <paramref name="taxiwayName"/> (plus junction arcs leaving toward the next taxiway).
    /// Returns the edge list, the updated partial route head, and the total cost.
    /// Returns null edges when the junction cannot be reached.
    /// </summary>
    private static (List<DirectionalEdge>? Edges, PartialRoute? Head, double Cost) LocalSearchToJunction(
        PartialRoute startHead,
        string taxiwayName,
        int junctionNodeId,
        SearchContext ctx
    )
    {
        // Trivial: head is already the junction.
        if (startHead.HeadNodeId == junctionNodeId)
        {
            ctx.DiagnosticLog?.Invoke($"[local] twy={taxiwayName} start={startHead.HeadNodeId} dest={junctionNodeId} TRIVIAL (start==dest)");
            return ([], startHead, 0.0);
        }

        if (!ctx.Layout.Nodes.TryGetValue(junctionNodeId, out GroundNode? destNode))
        {
            ctx.DiagnosticLog?.Invoke($"[local] twy={taxiwayName} dest={junctionNodeId} NOT IN LAYOUT");
            return (null, null, double.MaxValue);
        }

        ctx.DiagnosticLog?.Invoke(
            $"[local] BEGIN twy={taxiwayName} start={startHead.HeadNodeId} arr={startHead.ArrivalBearing:F1} "
                + $"hasPrior={startHead.LastEdge is not null} dest={junctionNodeId}"
        );

        var openSet = new PriorityQueue<PartialRoute, double>();

        // Keyed by (node, arrival-bearing-bucket): onward admissibility depends on arrival bearing,
        // so node-id-only pruning would let a cheaper dead-end arrival suppress the only admissible
        // arrival (see GeometricAdmissibility.PruningStateKey).
        var bestGScore = new Dictionary<(int Node, int Bucket, string Taxiway), double>();
        int expansions = 0;
        PartialRoute? deepest = null;
        int admittedTotal = 0;
        int rejectedTotal = 0;

        double h0 = ctx.Layout.Nodes.TryGetValue(startHead.HeadNodeId, out GroundNode? sn) ? RouteCostFunction.Heuristic(sn, destNode) : 0.0;

        openSet.Enqueue(startHead, startHead.AccumulatedCost + h0);
        bestGScore[GeometricAdmissibility.PruningStateKey(startHead.HeadNodeId, startHead.ArrivalBearing, startHead.LastTaxiwayName)] =
            startHead.AccumulatedCost;

        while (openSet.Count > 0)
        {
            PartialRoute current = openSet.Dequeue();
            expansions++;

            if (expansions > MaxLocalExpansions)
            {
                ctx.DiagnosticLog?.Invoke(
                    $"[local] EXHAUSTED twy={taxiwayName} dest={junctionNodeId} expansions={expansions} "
                        + $"deepest={deepest?.HeadNodeId ?? -1} depth={deepest?.Depth ?? 0} admitted={admittedTotal} rejected={rejectedTotal}"
                );
                break;
            }

            if (
                bestGScore.TryGetValue(
                    GeometricAdmissibility.PruningStateKey(current.HeadNodeId, current.ArrivalBearing, current.LastTaxiwayName),
                    out double recorded
                ) && (current.AccumulatedCost > recorded + 1e-9)
            )
            {
                continue;
            }

            if (deepest is null || current.Depth > deepest.Depth)
            {
                deepest = current;
            }

            if (current.HeadNodeId == junctionNodeId)
            {
                // Found the junction: extract edges accumulated since the start.
                List<DirectionalEdge> edges = ExtractEdgesSince(current, startHead.Depth);
                ctx.DiagnosticLog?.Invoke(
                    $"[local] SUCCESS twy={taxiwayName} dest={junctionNodeId} expansions={expansions} "
                        + $"edges={edges.Count} cost={current.AccumulatedCost - startHead.AccumulatedCost:F3} "
                        + $"admitted={admittedTotal} rejected={rejectedTotal}"
                );
                return (edges, current, current.AccumulatedCost - startHead.AccumulatedCost);
            }

            if (!ctx.Layout.Nodes.TryGetValue(current.HeadNodeId, out GroundNode? headNode))
            {
                continue;
            }

            int admittedHere = 0;
            int rejectedHere = 0;

            foreach (IGroundEdge edge in headNode.Edges)
            {
                GroundNode nextNode = edge.OtherNode(headNode);

                if (current.VisitedNodeIds.Contains(nextNode.Id))
                {
                    ctx.DiagnosticLog?.Invoke($"[local-edge] {current.HeadNodeId}->{nextNode.Id} twy={edge.TaxiwayName} REJECT visited");
                    rejectedHere++;
                    continue;
                }

                // Constrain to edges on the current taxiway, OR edges that connect to the junction node directly.
                bool matchesTwy = edge.MatchesTaxiway(taxiwayName);
                bool isJunctionEdge = nextNode.Id == junctionNodeId;
                if (!matchesTwy && !isJunctionEdge)
                {
                    ctx.DiagnosticLog?.Invoke(
                        $"[local-edge] {current.HeadNodeId}->{nextNode.Id} twy={edge.TaxiwayName} REJECT off-taxiway (need {taxiwayName})"
                    );
                    rejectedHere++;
                    continue;
                }

                // Blocked-turn exclusion — hard for explicit named-taxiway paths too (no painted line at
                // the apex). Forces an explicit L→F clearance onto the connector instead of the sharp corner.
                if (IsBlockedTurnEdge(ctx, current, nextNode.Id))
                {
                    ctx.DiagnosticLog?.Invoke($"[local-edge] {current.HeadNodeId}->{nextNode.Id} twy={edge.TaxiwayName} REJECT blocked-turn");
                    rejectedHere++;
                    continue;
                }

                if (!GeometricAdmissibility.IsAdmissible(current, edge, nextNode, ctx.Category))
                {
                    double dep = GeometricAdmissibility.GetDepartureBearing(edge, headNode, nextNode);
                    double delta = RouteCostFunction.HeadingDelta(current.ArrivalBearing, dep);
                    ctx.DiagnosticLog?.Invoke(
                        $"[local-edge] {current.HeadNodeId}->{nextNode.Id} twy={edge.TaxiwayName} REJECT admis "
                            + $"arr={current.ArrivalBearing:F1} dep={dep:F1} delta={delta:F1} "
                            + $"limit={CategoryLimits.MaxHeadingChangeDeg(ctx.Category):F0}"
                    );
                    rejectedHere++;
                    continue;
                }

                double incrementalCost = RouteCostFunction.IncrementalCost(current, edge, nextNode, ctx);

                // Apply direction-reversal penalty for SegmentExpander local searches (§Decisions §7).
                // When the edge bearing is more than 90° away from the overall segment direction
                // (head → destination), treat it as a temporary reversal.
                incrementalCost += ComputeDirectionReversalPenalty(edge, headNode, nextNode, destNode);

                // req ①: penalise leaving the walked taxiway onto a membership taxiway-junction arc
                // ("X - Y", both taxiways) as a CONTINUATION — a single-name continuation must win.
                // The legitimate turn onto the next taxiway (nextNode == junction) and runway-crossing
                // arcs (IsRunwayJunction) are not penalised. Soft: the arc stays usable when it is the
                // only continuation, so a resolvable clearance never fails.
                //
                // Exemption (issue #236 follow-up): a membership arc whose OTHER name is the taxiway we
                // just arrived on is the INTENDED smooth turn from that cleared taxiway onto this one —
                // a lane change / corner (e.g. the "[A,F1]" arc when transitioning A→F1). Flying that
                // fillet arc is the whole point of the corner; penalising it forces a square pivot
                // through the junction node instead. This is not the req ① turn-OFF case (that walks a
                // single taxiway X and diverts onto an "X - Y" arc when arriving on X, so the arc's
                // other name never equals the incoming taxiway), so req ① stays intact.
                if (matchesTwy && !isJunctionEdge && (edge is GroundArc { IsMembershipTaxiwayJunctionArc: true } membershipArc))
                {
                    bool isIntendedTransitionArc =
                        !string.IsNullOrEmpty(current.LastTaxiwayName)
                        && !string.Equals(current.LastTaxiwayName, taxiwayName, StringComparison.OrdinalIgnoreCase)
                        && membershipArc.TaxiwayNames.Any(name => name.Equals(current.LastTaxiwayName, StringComparison.OrdinalIgnoreCase));

                    if (!isIntendedTransitionArc)
                    {
                        incrementalCost += RouteCostFunction.MembershipJunctionArcContinuationCostNm;
                    }
                }

                double newGScore = current.AccumulatedCost + incrementalCost;

                // Zero-distance no-op edges (phase-d-shorten between co-located nodes) carry
                // bogus inherited bearings — propagate the current arrival bearing through
                // them so the next admissibility check (and the closed-set key below) sees the
                // real heading.
                double arrival = GeometricAdmissibility.IsNoOpEdge(edge)
                    ? current.ArrivalBearing
                    : GeometricAdmissibility.GetArrivalBearing(edge, headNode, nextNode);

                string twyName = RouteCostFunction.ResolveTaxiwayName(edge, current.HeadNodeId);
                (int Node, int Bucket, string Taxiway) nextKey = GeometricAdmissibility.PruningStateKey(nextNode.Id, arrival, twyName);
                if (bestGScore.TryGetValue(nextKey, out double existing) && (newGScore >= existing - 1e-9))
                {
                    ctx.DiagnosticLog?.Invoke(
                        $"[local-edge] {current.HeadNodeId}->{nextNode.Id} twy={edge.TaxiwayName} REJECT g-score "
                            + $"new={newGScore:F3} existing={existing:F3}"
                    );
                    rejectedHere++;
                    continue;
                }

                bestGScore[nextKey] = newGScore;

                ctx.DiagnosticLog?.Invoke(
                    $"[local-edge] {current.HeadNodeId}->{nextNode.Id} twy={edge.TaxiwayName} ADMIT "
                        + $"arr={current.ArrivalBearing:F1} arr'={arrival:F1} g={newGScore:F3} h={RouteCostFunction.Heuristic(nextNode, destNode):F3}"
                );

                PartialRoute extended = current with
                {
                    HeadNodeId = nextNode.Id,
                    ArrivalBearing = arrival,
                    LastEdge = edge,
                    LastTaxiwayName = twyName,
                    Previous = current,
                    Depth = current.Depth + 1,
                    AccumulatedCost = newGScore,
                    VisitedNodeIds = current.VisitedNodeIds.Add(nextNode.Id),
                };

                double h = RouteCostFunction.Heuristic(nextNode, destNode);
                double fScore = newGScore + h - (extended.Depth * 1e-9);
                openSet.Enqueue(extended, fScore);
                admittedHere++;
            }

            admittedTotal += admittedHere;
            rejectedTotal += rejectedHere;

            ctx.DiagnosticLog?.Invoke(
                $"[local-pop] node={current.HeadNodeId} depth={current.Depth} arr={current.ArrivalBearing:F1} "
                    + $"admit={admittedHere} reject={rejectedHere}"
            );
        }

        ctx.DiagnosticLog?.Invoke(
            $"[local] FAIL twy={taxiwayName} dest={junctionNodeId} expansions={expansions} openSet=empty "
                + $"deepest={deepest?.HeadNodeId ?? -1} depth={deepest?.Depth ?? 0} admitted={admittedTotal} rejected={rejectedTotal}"
        );

        return (null, null, double.MaxValue);
    }

    /// <summary>
    /// Compute the direction-reversal penalty for a candidate edge in a local segment search.
    /// Fires when the edge departs more than 90° away from the start-of-segment → junction-node direction.
    /// </summary>
    private static double ComputeDirectionReversalPenalty(IGroundEdge edge, GroundNode headNode, GroundNode nextNode, GroundNode destNode)
    {
        double departureBearing = GeometricAdmissibility.GetDepartureBearing(edge, headNode, nextNode);
        double segmentBearing = GeoMath.BearingTo(headNode.Position, destNode.Position);
        double delta = RouteCostFunction.HeadingDelta(departureBearing, segmentBearing);
        return delta > 90.0 ? RouteCostFunction.DirectionReversalCostNm : 0.0;
    }

    /// <summary>
    /// Extract the directed edges from <paramref name="route"/> that were added after the
    /// node at <paramref name="startDepth"/>.
    /// </summary>
    private static List<DirectionalEdge> ExtractEdgesSince(PartialRoute route, int startDepth)
    {
        int edgeCount = route.Depth - startDepth;
        if (edgeCount <= 0)
        {
            return [];
        }

        var result = new DirectionalEdge[edgeCount];
        PartialRoute current = route;

        for (int i = edgeCount - 1; i >= 0; i--)
        {
            int prevNodeId = current.Previous!.HeadNodeId;
            GroundNode prevNode = FindNodeInChain(current, prevNodeId);
            GroundNode headNode = FindNodeInChain(current, current.HeadNodeId);
            result[i] = current.LastEdge!.Directed(prevNode, headNode);
            current = current.Previous;
        }

        return [.. result];
    }

    private static GroundNode FindNodeInChain(PartialRoute route, int nodeId)
    {
        PartialRoute? cursor = route;
        while (cursor is not null)
        {
            if (cursor.LastEdge is not null)
            {
                foreach (GroundNode n in cursor.LastEdge.Nodes)
                {
                    if (n.Id == nodeId)
                    {
                        return n;
                    }
                }
            }

            cursor = cursor.Previous!;
        }

        throw new InvalidOperationException($"Node {nodeId} not found in partial route chain.");
    }

    // -----------------------------------------------------------------------
    // Walk to natural terminus (last waypoint, no destination)
    // -----------------------------------------------------------------------

    private static (List<DirectionalEdge>? Edges, PartialRoute? Head, PathfindingFailure? Failure) WalkToNaturalTerminus(
        PartialRoute head,
        string taxiwayName,
        SearchContext ctx,
        LatLon? biasToward
    )
    {
        // Walk forward along the taxiway from current head, staying on the named taxiway,
        // following the geometrically admissible continuation until no more edges remain.
        var edges = new List<DirectionalEdge>();
        PartialRoute current = head;
        bool madeProgress = true;

        while (madeProgress)
        {
            madeProgress = false;

            if (!ctx.Layout.Nodes.TryGetValue(current.HeadNodeId, out GroundNode? headNode))
            {
                break;
            }

            // On the first (momentum-free) step, break ties toward the destination so the walk
            // heads the right way along the taxiway; afterwards admissibility fixes the direction.
            bool firstStep = (edges.Count == 0) && (biasToward is not null);

            // Find the best admissible forward step on this taxiway.
            IGroundEdge? bestEdge = null;
            GroundNode? bestNext = null;
            double bestCost = double.MaxValue;
            double bestBiasDist = double.MaxValue;
            // Sentinel true so the first single-name candidate always displaces it.
            bool bestIsJunctionArc = true;

            foreach (IGroundEdge edge in headNode.Edges)
            {
                if (!edge.MatchesTaxiway(taxiwayName))
                {
                    continue;
                }

                GroundNode nextNode = edge.OtherNode(headNode);
                if (current.VisitedNodeIds.Contains(nextNode.Id))
                {
                    continue;
                }

                if (IsBlockedTurnEdge(ctx, current, nextNode.Id))
                {
                    continue;
                }

                if (!GeometricAdmissibility.IsAdmissible(current, edge, nextNode, ctx.Category))
                {
                    continue;
                }

                double cost = RouteCostFunction.IncrementalCost(current, edge, nextNode, ctx);

                // req ①: when extending the same named taxiway, an exact single-name edge ranks
                // strictly above a junction arc that matches taxiwayName only by membership (e.g.
                // an "A - Q1" arc when walking A). A multi-name junction arc is a turn OFF the
                // taxiway onto a crossing one, not a continuation of it — the collapsed junctions
                // expose several such membership matches at one node. Single-name wins regardless
                // of cost; cost only breaks ties within the same tier. Runway-crossing arcs
                // (IsRunwayJunction, e.g. "H - RWY...") DO continue the taxiway across a runway and
                // are not treated as junction arcs.
                bool isJunctionArc = edge is GroundArc { IsMembershipTaxiwayJunctionArc: true };
                if (!isJunctionArc && (edge is GroundArc { TaxiwayNames.Length: >= 2 } runwayJunctionArc))
                {
                    // A runway-junction membership arc continues the walked taxiway across the runway
                    // only when it stays roughly on-direction. An arc that sweeps >90° between its
                    // entry and exit tangents is the corner ONTO the crossing runway (or a reverse
                    // corner off it), not a continuation — when the exit corner and reverse corner
                    // share one fused tangent node, both arcs depart tangent to the taxiway and the
                    // reverse one can be the cheapest step, dead-ending the greedy walk mid-crossing.
                    // Demote such arcs below single-name edges; they stay usable when nothing else
                    // continues the taxiway.
                    double departure = runwayJunctionArc.TangentBearingAt(headNode, headNode);
                    double arrivalTangent = runwayJunctionArc.TangentBearingAt(nextNode, headNode);
                    isJunctionArc = GeoMath.AbsBearingDifference(departure, arrivalTangent) > 90.0;
                }

                double biasDist = firstStep ? GeoMath.DistanceNm(nextNode.Position, biasToward!.Value) : 0.0;
                bool sameTier = bestIsJunctionArc == isJunctionArc;
                bool tieBetter = firstStep ? (biasDist < bestBiasDist) : (cost < bestCost);
                bool better = (bestEdge is null) || (bestIsJunctionArc && !isJunctionArc) || (sameTier && tieBetter);

                if (better)
                {
                    bestCost = cost;
                    bestBiasDist = biasDist;
                    bestEdge = edge;
                    bestNext = nextNode;
                    bestIsJunctionArc = isJunctionArc;
                }
            }

            if (bestEdge is null)
            {
                break;
            }

            double arrival = GeometricAdmissibility.GetArrivalBearing(bestEdge, headNode, bestNext!);
            string twyName = RouteCostFunction.ResolveTaxiwayName(bestEdge, current.HeadNodeId);

            edges.Add(bestEdge.Directed(headNode, bestNext!));
            current = current with
            {
                HeadNodeId = bestNext!.Id,
                ArrivalBearing = arrival,
                LastEdge = bestEdge,
                LastTaxiwayName = twyName,
                Previous = current,
                Depth = current.Depth + 1,
                AccumulatedCost = current.AccumulatedCost + bestCost,
                VisitedNodeIds = current.VisitedNodeIds.Add(bestNext!.Id),
            };
            madeProgress = true;
        }

        ctx.DiagnosticLog?.Invoke($"[terminus] twy={taxiwayName} terminus={current.HeadNodeId} edges={edges.Count}");

        return (edges, current, null);
    }

    /// <summary>
    /// The position the final-taxiway terminus walk should head toward at its first step, for a
    /// runway destination: the nearest hold-short for the destination runway that lies on the named
    /// taxiway (so a "TAXI B 28R" walk heads toward B's own 28R hold-short instead of away from it,
    /// avoiding a wrong-direction walk that then detours via another taxiway). A located taxiway
    /// hold-short whose ON-taxiway is the final taxiway ("TAXI C D J HS C@J") biases toward the
    /// J∩C crossing the same way — without it the walk never commits down J at all (issue #358).
    /// Null when there is no directional preference — the walk keeps its prior admissibility-only
    /// behaviour. Parking/spot destinations are handled by the on-taxiway LocalSearchToJunction in
    /// ExpandLastWaypoint, not here: biasing the terminus walk toward an off-taxiway parking node is
    /// unreliable (it can pick a terminus from which the parking extension cannot complete).
    /// </summary>
    private static LatLon? ResolveTerminusBias(PartialRoute head, string taxiwayName, SearchContext ctx)
    {
        if (!ctx.Layout.Nodes.TryGetValue(head.HeadNodeId, out GroundNode? headNode))
        {
            return null;
        }

        // Runway designators that pull the terminus walk in a definite direction: the destination
        // runway (taxiing TO it) and any runway the controller named as an explicit hold-short. Both
        // put a hold-short node on the final taxiway the walk should head toward — without this a
        // "TAXI B K HS 10R" walk picks an arbitrary direction along K and heads away from 10R.
        var targets = new List<string>();
        if (ctx.Destination.Kind == DestinationKind.Runway && ctx.Destination.RunwayId is { } runwayId)
        {
            targets.Add(runwayId);
        }

        LatLon? taxiwayCrossingBias = null;
        double taxiwayCrossingDistNm = double.MaxValue;
        foreach (HoldShortTarget holdShort in ctx.ExplicitHoldShorts)
        {
            // A spot target is a node the route passes through, not a bar or crossing to aim the walk at.
            if (holdShort.IsSpot)
            {
                continue;
            }

            // A located target only steers the taxiway it holds ON.
            if (holdShort.OnTaxiway is { } onTaxiway && !onTaxiway.Equals(taxiwayName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            targets.Add(holdShort.Target);

            // A located TAXIWAY target has no runway bar to aim at — bias toward the crossing of
            // the target taxiway on this taxiway instead, so the walk heads for the C∩J junction.
            if (holdShort.OnTaxiway is not null && !ctx.Layout.TryGetRunwayCenterlineName(holdShort.Target, out _))
            {
                GroundNode? crossing = ctx.Layout.FindIntersectionNode(taxiwayName, holdShort.Target, headNode.Position);
                if (crossing is not null)
                {
                    double crossingDistNm = GeoMath.DistanceNm(crossing.Position, headNode.Position);
                    if (crossingDistNm < taxiwayCrossingDistNm)
                    {
                        taxiwayCrossingDistNm = crossingDistNm;
                        taxiwayCrossingBias = crossing.Position;
                    }
                }
            }
        }

        if (targets.Count == 0)
        {
            return null;
        }

        GroundNode? best = null;
        double bestDistNm = double.MaxValue;
        foreach (GroundNode node in ctx.Layout.Nodes.Values)
        {
            bool isTarget = false;
            foreach (string target in targets)
            {
                if (IsRunwayHoldShort(node.Id, target, ctx))
                {
                    isTarget = true;
                    break;
                }
            }

            if (!isTarget)
            {
                continue;
            }

            bool onTaxiway = false;
            foreach (IGroundEdge edge in node.Edges)
            {
                if (edge.MatchesTaxiway(taxiwayName))
                {
                    onTaxiway = true;
                    break;
                }
            }

            if (!onTaxiway)
            {
                continue;
            }

            double distNm = GeoMath.DistanceNm(node.Position, headNode.Position);
            if (distNm < bestDistNm)
            {
                bestDistNm = distNm;
                best = node;
            }
        }

        if (best is not null && (taxiwayCrossingBias is null || bestDistNm <= taxiwayCrossingDistNm))
        {
            return best.Position;
        }

        return taxiwayCrossingBias;
    }

    // -----------------------------------------------------------------------
    // Node-ref waypoint handling
    // -----------------------------------------------------------------------

    private static (List<DirectionalEdge>? Edges, PartialRoute? Head, PathfindingFailure? Failure) RouteToNodeRef(
        PartialRoute head,
        int targetNodeId,
        SearchContext ctx
    ) =>
        // First: walk to terminus of current taxiway near the target node, then route to target.
        RouteToSpecificNode(head, targetNodeId, ctx);

    private static (List<DirectionalEdge>? Edges, PartialRoute? Head, PathfindingFailure? Failure) RouteFromNodeRefToTaxiway(
        PartialRoute head,
        int nodeRefId,
        string nextTaxiway,
        SearchContext ctx
    )
    {
        // Head is already at the node-ref (resolved in previous step).
        // Find junction candidates from that node onto the next taxiway.
        if (!ctx.Layout.Nodes.TryGetValue(nodeRefId, out GroundNode? nodeRefNode))
        {
            return (
                null,
                null,
                new PathfindingFailure(FailureKind.StartNodeUnreachable, $"Node-reference node {nodeRefId} not found in layout.", null, null, null)
            );
        }

        // Treat the node-ref as a single-node "taxiway" and find the best junction to nextTaxiway.
        List<GroundNode> junctionCandidates = FindJunctionCandidates(nodeRefNode, nextTaxiway);
        if (junctionCandidates.Count == 0)
        {
            return TryDetour(head, $"#{nodeRefId}", nextTaxiway, ctx);
        }

        (List<DirectionalEdge>? bestEdges, PartialRoute? bestHead, double bestCost) = (null, null, double.MaxValue);

        foreach (GroundNode junctionNode in junctionCandidates)
        {
            (List<DirectionalEdge>? segEdges, PartialRoute? segHead, double cost) = LocalSearchToJunction(head, nextTaxiway, junctionNode.Id, ctx);
            if (segEdges is null)
            {
                continue;
            }

            if (cost < bestCost)
            {
                bestCost = cost;
                bestEdges = segEdges;
                bestHead = segHead;
            }
        }

        if (bestEdges is null)
        {
            return TryDetour(head, $"#{nodeRefId}", nextTaxiway, ctx);
        }

        return (bestEdges, bestHead, null);
    }

    /// <summary>
    /// Find junction candidates from a specific node (rather than all nodes on a taxiway).
    /// </summary>
    private static List<GroundNode> FindJunctionCandidates(GroundNode fromNode, string toTaxiway)
    {
        var result = new List<GroundNode>();
        foreach (IGroundEdge edge in fromNode.Edges)
        {
            if (edge.MatchesTaxiway(toTaxiway))
            {
                result.Add(fromNode);
                return result;
            }
        }

        return result;
    }

    private static (List<DirectionalEdge>? Edges, PartialRoute? Head, PathfindingFailure? Failure) RouteToSpecificNode(
        PartialRoute head,
        int targetNodeId,
        SearchContext ctx
    )
    {
        if (head.HeadNodeId == targetNodeId)
        {
            return ([], head, null);
        }

        if (!ctx.Layout.Nodes.TryGetValue(targetNodeId, out GroundNode? destNode))
        {
            return (
                null,
                null,
                new PathfindingFailure(
                    FailureKind.DestinationUnreachable,
                    $"Node-reference #{targetNodeId} not found in layout.",
                    null,
                    $"#{targetNodeId}",
                    null
                )
            );
        }

        // A node-ref sequence from the ground draw tool lists every node along the drawn route, so
        // consecutive targets are one edge apart. Take that edge directly: the A* below would have to
        // rediscover it, and when it can't (the drawn turn is inadmissible from here) it silently
        // substitutes a long way round instead of failing.
        (List<DirectionalEdge>? stepEdges, PartialRoute? stepHead) = TryStepToAdjacentNode(head, destNode, ctx);
        if (stepEdges is not null)
        {
            return (stepEdges, stepHead, null);
        }

        // Use AutoRouter from current head to the target node.
        SearchContext detourCtx = ctx with
        {
            StartNodeId = head.HeadNodeId,
            Destination = new DestinationDescriptor(targetNodeId, null, null, null, DestinationKind.Node),
            WaypointSequence = [],
            AuthorizedTaxiways = null,
        };

        (TaxiRoute? route, PathfindingFailure? failure) = AutoRouter.Run(detourCtx, startOverride: head);
        if (failure is not null || route is null)
        {
            return (
                null,
                null,
                new PathfindingFailure(
                    FailureKind.DestinationUnreachable,
                    $"Cannot route to node-reference #{targetNodeId} from node {head.HeadNodeId}.",
                    null,
                    $"#{targetNodeId}",
                    null
                )
            );
        }

        PartialRoute newHead = BuildHeadFromRoute(head, route);
        return ([.. route.Segments.Select(s => s.Edge)], newHead, null);
    }

    /// <summary>
    /// Advance the head one graph edge to <paramref name="target"/> when the two nodes are directly
    /// connected and that step is legal — the same blocked-turn and heading-delta gates the searches
    /// apply, so an inadmissible drawn turn still falls through to the A*. Parallel edges between the
    /// pair (the fillet generator emits duplicate corner arcs) are broken by incremental cost.
    /// Returns <c>(null, null)</c> when no such step exists.
    /// </summary>
    private static (List<DirectionalEdge>? Edges, PartialRoute? Head) TryStepToAdjacentNode(PartialRoute head, GroundNode target, SearchContext ctx)
    {
        if (!ctx.Layout.Nodes.TryGetValue(head.HeadNodeId, out GroundNode? headNode) || IsBlockedTurnEdge(ctx, head, target.Id))
        {
            return (null, null);
        }

        IGroundEdge? best = null;
        GroundNode? bestNext = null;
        double bestCost = double.MaxValue;

        foreach (IGroundEdge edge in headNode.Edges)
        {
            GroundNode nextNode = edge.OtherNode(headNode);
            if ((nextNode.Id != target.Id) || !GeometricAdmissibility.IsAdmissible(head, edge, nextNode, ctx.Category))
            {
                continue;
            }

            double cost = RouteCostFunction.IncrementalCost(head, edge, nextNode, ctx);
            if (cost < bestCost)
            {
                bestCost = cost;
                best = edge;
                bestNext = nextNode;
            }
        }

        if ((best is null) || (bestNext is null))
        {
            return (null, null);
        }

        double arrival = GeometricAdmissibility.IsNoOpEdge(best)
            ? head.ArrivalBearing
            : GeometricAdmissibility.GetArrivalBearing(best, headNode, bestNext);

        PartialRoute extended = head with
        {
            HeadNodeId = bestNext.Id,
            ArrivalBearing = arrival,
            LastEdge = best,
            LastTaxiwayName = RouteCostFunction.ResolveTaxiwayName(best, head.HeadNodeId),
            Previous = head,
            Depth = head.Depth + 1,
            AccumulatedCost = head.AccumulatedCost + bestCost,
            VisitedNodeIds = head.VisitedNodeIds.Add(bestNext.Id),
        };

        ctx.DiagnosticLog?.Invoke($"[node-ref] {head.HeadNodeId}->{bestNext.Id} twy={best.TaxiwayName} direct edge (cost={bestCost:F3})");
        return ([best.Directed(headNode, bestNext)], extended);
    }

    // -----------------------------------------------------------------------
    // Variant resolution at the final taxiway segment
    // -----------------------------------------------------------------------

    /// <summary>
    /// True when any node touched by <paramref name="edges"/> is a hold-short for
    /// <paramref name="runwayId"/> (reciprocal-tolerant via <c>Contains</c>) — i.e. the
    /// walked route already reaches the destination runway and needs no variant extension.
    /// </summary>
    /// <summary>
    /// Every letter-only taxiway name in the layout that the controller did NOT name (i.e. not in
    /// <paramref name="authorized"/>). Numbered connectors (<c>B1</c>) and <c>RAMP</c> are never
    /// letter-only, so they are always free. Used to HARD-constrain the runway-destination fallback
    /// A* to the cleared taxiways so it cannot detour onto an unnamed taxiway.
    /// </summary>
    private static IReadOnlySet<string> UnnamedLetterTaxiways(AirportGroundLayout layout, IReadOnlySet<string>? authorized)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (GroundNode node in layout.Nodes.Values)
        {
            foreach (IGroundEdge edge in node.Edges)
            {
                if (edge is GroundArc arc)
                {
                    foreach (string name in arc.TaxiwayNames)
                    {
                        AddIfUnnamedLetter(result, name, authorized);
                    }
                }
                else
                {
                    AddIfUnnamedLetter(result, edge.TaxiwayName, authorized);
                }
            }
        }

        return result;
    }

    private static void AddIfUnnamedLetter(HashSet<string> set, string name, IReadOnlySet<string>? authorized)
    {
        if (SearchContext.IsLetterOnlyTaxiway(name) && !(authorized?.Contains(name) ?? false))
        {
            set.Add(name);
        }
    }

    private static bool RouteReachesRunwayHoldShort(IReadOnlyList<DirectionalEdge> edges, string runwayId, SearchContext ctx)
    {
        foreach (DirectionalEdge edge in edges)
        {
            if (IsRunwayHoldShort(edge.ToNodeId, runwayId, ctx) || IsRunwayHoldShort(edge.FromNodeId, runwayId, ctx))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsRunwayHoldShort(int nodeId, string runwayId, SearchContext ctx) =>
        ctx.Layout.Nodes.TryGetValue(nodeId, out GroundNode? node)
        && node.Type == GroundNodeType.RunwayHoldShort
        && node.RunwayId is { } rwy
        && rwy.Contains(runwayId);

    private static (List<DirectionalEdge>? Edges, PathfindingFailure? Failure) TryVariantExtension(
        PartialRoute head,
        string lastTaxiwayName,
        string destinationRunway,
        SearchContext ctx
    )
    {
        // Check if we already reached a hold-short for the destination runway.
        if (ctx.Layout.Nodes.TryGetValue(head.HeadNodeId, out GroundNode? currentNode))
        {
            if (
                currentNode.Type == GroundNodeType.RunwayHoldShort
                && currentNode.RunwayId is { } currentRwyId
                && currentRwyId.Contains(destinationRunway)
            )
            {
                ctx.DiagnosticLog?.Invoke($"[variant] already at hold-short for {destinationRunway}");
                return (null, null);
            }
        }

        // Find numbered variants of lastTaxiwayName that have a hold-short for the destination runway.
        List<(GroundNode HsNode, string Name)> variants = FindVariantHoldShorts(ctx.Layout, lastTaxiwayName, destinationRunway);

        ctx.DiagnosticLog?.Invoke($"[variant] lastTwy={lastTaxiwayName} destRwy={destinationRunway} variants={variants.Count}");

        if (variants.Count == 0)
        {
            // No variants: check for same-name hold-shorts first.
            List<GroundNode> sameNameHs = FindSameNameHoldShorts(ctx.Layout, lastTaxiwayName, destinationRunway);
            if (sameNameHs.Count > 0)
            {
                return ExtendToNearestHoldShort(head, sameNameHs, ctx);
            }

            // No numbered variant and no same-name hold-short reaches the destination runway from this
            // taxiway. TryVariantExtension is only entered when the walk itself did NOT reach a hold-short
            // for the runway, so fail rather than returning a route that stops at the taxiway terminus short
            // of the runway (which would let the command succeed against a route that never gets there).
            // Run treats this failure as its cue to try a connector stub off the taxiway
            // (TryRunwayConnectorFallback) before the failure reaches the controller.
            return (
                null,
                new PathfindingFailure(
                    FailureKind.DestinationUnreachable,
                    $"Taxiway {lastTaxiwayName} does not reach runway "
                        + $"{RunwayIdentifier.ToDisplayDesignator(destinationRunway)} — specify a connecting taxiway.",
                    lastTaxiwayName,
                    $"{lastTaxiwayName} → {destinationRunway}",
                    null
                )
            );
        }

        // Determine distinct variant names.
        var distinctVariants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((GroundNode _, string? name) in variants)
        {
            distinctVariants.Add(name);
        }

        if (distinctVariants.Count > 1)
        {
            // Multiple numbered variants of the base taxiway serve the destination runway (e.g.
            // W1..W7 off W to rwy 30). Auto-pick the one whose hold-short is nearest the requested
            // runway's threshold — the full-length lineup connector. Only fall back to a TransitionAmbiguous failure
            // when the threshold is unavailable (no navdata), so the controller is never asked to
            // disambiguate a resolvable clearance and we never silently guess without a reference.
            LatLon? threshold = RouteMaterialiser.ResolveRunwayThreshold(ctx.Layout.AirportId, destinationRunway);
            if (threshold is { } thresholdPos)
            {
                string bestVariant = variants[0].Name;
                double bestDist = double.MaxValue;
                foreach ((GroundNode? hsNode, string? name) in variants)
                {
                    double dist = GeoMath.DistanceNm(hsNode.Position, thresholdPos);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestVariant = name;
                    }
                }

                var bestHsNodes = variants.Where(v => v.Name.Equals(bestVariant, StringComparison.OrdinalIgnoreCase)).Select(v => v.HsNode).ToList();
                ctx.DiagnosticLog?.Invoke(
                    $"[variant] auto-picked {bestVariant} (nearest {destinationRunway} threshold) from {distinctVariants.Count} variants"
                );
                return ExtendToVariant(head, lastTaxiwayName, bestVariant, bestHsNodes, ctx);
            }

            var candidateList = distinctVariants.OrderBy(s => s).ToList();
            return (
                null,
                new PathfindingFailure(
                    FailureKind.TransitionAmbiguous,
                    $"Runway {RunwayIdentifier.ToDisplayDesignator(destinationRunway)} is served by both "
                        + $"{string.Join(" and ", candidateList)} from {lastTaxiwayName} — specify {string.Join(" or ", candidateList)}",
                    lastTaxiwayName,
                    $"{lastTaxiwayName} → {destinationRunway}",
                    candidateList[0]
                )
            );
        }

        // Exactly one variant: auto-extend onto it.
        string chosenVariant = distinctVariants.First();
        var variantHsNodes = variants.Where(v => v.Name.Equals(chosenVariant, StringComparison.OrdinalIgnoreCase)).Select(v => v.HsNode).ToList();

        return ExtendToVariant(head, lastTaxiwayName, chosenVariant, variantHsNodes, ctx);
    }

    private static List<(GroundNode HsNode, string Name)> FindVariantHoldShorts(AirportGroundLayout layout, string baseName, string runwayId)
    {
        var result = new List<(GroundNode, string)>();
        foreach (GroundNode node in layout.Nodes.Values)
        {
            if (node.Type != GroundNodeType.RunwayHoldShort || node.RunwayId is null)
            {
                continue;
            }

            if (node.RunwayId is not { } nodeRwyId || !nodeRwyId.Contains(runwayId))
            {
                continue;
            }

            foreach (IGroundEdge edge in node.Edges)
            {
                string edgeName = edge.TaxiwayName;
                if (IsNumberedVariant(edgeName, baseName))
                {
                    result.Add((node, edgeName));
                    break;
                }
            }
        }

        return result;
    }

    private static List<GroundNode> FindSameNameHoldShorts(AirportGroundLayout layout, string taxiwayName, string runwayId)
    {
        var result = new List<GroundNode>();
        foreach (GroundNode node in layout.Nodes.Values)
        {
            if (node.Type != GroundNodeType.RunwayHoldShort || node.RunwayId is null)
            {
                continue;
            }

            if (node.RunwayId is not { } sameNameRwyId || !sameNameRwyId.Contains(runwayId))
            {
                continue;
            }

            foreach (IGroundEdge edge in node.Edges)
            {
                if (edge.TaxiwayName.Equals(taxiwayName, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(node);
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// True when <paramref name="hs"/> is the destination-runway hold-short for <paramref name="runwayId"/>
    /// itself — the reason alone is not enough, because the hold-short node set is a substring match on the
    /// runway id (`1L` also matches `11L`), so the accepted route must be checked against the designator.
    /// </summary>
    private static bool IsDestinationHoldFor(HoldShortPoint hs, string runwayId) =>
        (hs.Reason == HoldShortReason.DestinationRunway)
        && (hs.TargetName is { Length: > 0 } target)
        && RunwayIdentifier.Parse(target).Contains(RunwayIdentifier.NormalizeDesignator(runwayId));

    /// <summary>
    /// Numbered connectors that carry <paramref name="lastTaxiway"/> to a hold-short bar for
    /// <paramref name="runwayId"/> when the taxiway itself has no bar for that runway — SFO's B, which runs
    /// past 01L/19R's south end while the bars sit on the M1 and A2 stubs. A candidate is a straight,
    /// non-runway edge at a bar whose name is neither <paramref name="lastTaxiway"/> nor already in
    /// <paramref name="alreadyNamed"/>, and never a letter-only name (an unnamed letter taxiway is a
    /// deviation the controller has to name; numbered stubs are free, see
    /// <see cref="SearchContext.IsLetterOnlyTaxiway"/>). The connector must then reach a node on
    /// <paramref name="lastTaxiway"/> within <see cref="RunwayConnectorStubMaxFt"/> without crossing another
    /// bar, a runway surface, or a junction onto a third taxiway — issue #393's rule that a short run to the
    /// bar counts as being at the runway. Ordered shortest stub first, name breaking ties.
    /// </summary>
    public static List<RunwayConnectorCandidate> FindRunwayConnectorsOffTaxiway(
        AirportGroundLayout layout,
        string lastTaxiway,
        string runwayId,
        IReadOnlyCollection<string> alreadyNamed
    )
    {
        var shortest = new Dictionary<string, RunwayConnectorCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (GroundNode? bar in layout.GetRunwayHoldShortNodes(runwayId).OrderBy(n => n.Id))
        {
            foreach (string connector in ConnectorNamesAtBar(bar, lastTaxiway, alreadyNamed))
            {
                if (WalkConnectorStub(bar, connector, lastTaxiway) is not { } reach)
                {
                    continue;
                }

                if (!shortest.TryGetValue(connector, out RunwayConnectorCandidate incumbent) || (reach.StubFt < incumbent.StubFt))
                {
                    shortest[connector] = new RunwayConnectorCandidate(connector, reach.NodeId, bar.Id, reach.StubFt);
                }
            }
        }

        return [.. shortest.Values.OrderBy(c => c.StubFt).ThenBy(c => c.Connector, StringComparer.Ordinal)];
    }

    /// <summary>
    /// A numbered stub that carries the last cleared taxiway to a hold-short bar for the destination runway.
    /// </summary>
    /// <param name="Connector">The stub's taxiway name (SFO <c>M1</c>).</param>
    /// <param name="JunctionNodeId">The node where the stub meets the cleared taxiway.</param>
    /// <param name="BarNodeId">The hold-short bar the stub leads to — the node the re-resolved route must end at.</param>
    /// <param name="StubFt">Walked length from the bar to the junction.</param>
    public readonly record struct RunwayConnectorCandidate(string Connector, int JunctionNodeId, int BarNodeId, double StubFt);

    /// <summary>Distinct connector names named by the straight, non-runway edges meeting a hold-short bar.</summary>
    private static List<string> ConnectorNamesAtBar(GroundNode bar, string lastTaxiway, IReadOnlyCollection<string> alreadyNamed)
    {
        var names = new List<string>();
        foreach (IGroundEdge edge in bar.Edges)
        {
            if ((edge is not GroundEdge straight) || straight.IsRunwayCenterline || straight.IsRunwayCrossingLink)
            {
                continue;
            }

            string name = straight.TaxiwayName;
            if (IsUsableConnectorName(name, lastTaxiway, alreadyNamed) && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>
    /// True when <paramref name="name"/> is a numbered connector the clearance has not already named. The digit
    /// requirement is structural: it excludes letter-only taxiways (a deviation the controller must name) and
    /// <c>RAMP</c> (nonmovement area, 7110.65 §3-7-2 NOTE 2) even on a layout that wires a ramp edge to a bar.
    /// </summary>
    private static bool IsUsableConnectorName(string name, string lastTaxiway, IReadOnlyCollection<string> alreadyNamed)
    {
        if (name.Equals(lastTaxiway, StringComparison.OrdinalIgnoreCase) || !name.Any(char.IsDigit))
        {
            return false;
        }

        foreach (string named in alreadyNamed)
        {
            if (named.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Dijkstra along <paramref name="connector"/> from <paramref name="bar"/> to the first node on
    /// <paramref name="lastTaxiway"/> (a fillet corner arc counts — the B/M1 tangent-cut node is where
    /// the taxiway and the stub meet). Null when no such node lies within
    /// <see cref="RunwayConnectorStubMaxFt"/> of the bar along the connector.
    /// </summary>
    private static (int NodeId, double StubFt)? WalkConnectorStub(GroundNode bar, string connector, string lastTaxiway)
    {
        var queue = new PriorityQueue<GroundNode, (double Ft, int NodeId)>();
        queue.Enqueue(bar, (0.0, bar.Id));
        var settled = new HashSet<int>();

        while (queue.TryDequeue(out GroundNode? node, out (double Ft, int NodeId) key))
        {
            if (!settled.Add(node.Id))
            {
                continue;
            }

            if ((node.Id != bar.Id) && NodeIncidentToTaxiway(node, lastTaxiway))
            {
                return (node.Id, key.Ft);
            }

            if (CanExpandConnectorNode(node, bar, connector, lastTaxiway))
            {
                RelaxConnectorEdges(node, key.Ft, connector, settled, queue);
            }
        }

        return null;
    }

    private static void RelaxConnectorEdges(
        GroundNode node,
        double reachedFt,
        string connector,
        HashSet<int> settled,
        PriorityQueue<GroundNode, (double Ft, int NodeId)> queue
    )
    {
        foreach (IGroundEdge edge in node.Edges)
        {
            if (edge.IsRunwayCenterline || IsRunwayCrossingLinkEdge(edge) || !edge.MatchesTaxiway(connector))
            {
                continue;
            }

            GroundNode next = edge.OtherNode(node);
            double ft = reachedFt + (edge.DistanceNm * GeoMath.FeetPerNm);
            if ((ft <= RunwayConnectorStubMaxFt) && !settled.Contains(next.Id))
            {
                queue.Enqueue(next, (ft, next.Id));
            }
        }
    }

    /// <summary>
    /// A stub walk never continues through a second hold-short bar or a node where a third taxiway joins —
    /// both mean the connector has left its short lead-in and is a route of its own. Fillet arcs are ignored
    /// for the third-taxiway test: a corner arc carries the joined names of the taxiways it bridges.
    /// </summary>
    private static bool CanExpandConnectorNode(GroundNode node, GroundNode bar, string connector, string lastTaxiway)
    {
        if ((node.Id != bar.Id) && (node.Type == GroundNodeType.RunwayHoldShort))
        {
            return false;
        }

        foreach (IGroundEdge edge in node.Edges)
        {
            if (IsThirdTaxiwayStraight(edge, connector, lastTaxiway))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// True for any edge — straight or fillet arc — whose name carries a runway-crossing <c>:link</c>, the
    /// runway-surface edges the stub walk must never step onto (a junction arc joining a connector to a link
    /// still matches the connector's name, so <see cref="IGroundEdge.MatchesTaxiway"/> alone does not exclude it).
    /// </summary>
    private static bool IsRunwayCrossingLinkEdge(IGroundEdge edge) => edge.TaxiwayName.Contains(":link", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="edge"/> is a straight edge of a taxiway that is neither the connector nor the
    /// cleared taxiway — the junction onto a third taxiway that ends a stub walk.
    /// </summary>
    private static bool IsThirdTaxiwayStraight(IGroundEdge edge, string connector, string lastTaxiway)
    {
        if ((edge is not GroundEdge straight) || straight.IsRunwayCenterline || straight.IsRunwayCrossingLink)
        {
            return false;
        }

        return !straight.MatchesTaxiway(connector) && !straight.MatchesTaxiway(lastTaxiway);
    }

    /// <summary>
    /// Returns true if <paramref name="candidate"/> is a numbered variant of
    /// <paramref name="baseName"/> (e.g., "W1" is a variant of "W"; "WA" is not).
    /// Numbered variants only branch off a letter-only base: "B10" is a variant of
    /// "B", not of "B1" — "B1" and "B10" are siblings under base "B". A digit-bearing
    /// base ("B1") is itself a leaf connector and has no further numbered variants, so
    /// it never matches (prevents the "B10 is a variant of B1" false positive that made
    /// a B1→runway hold-short look ambiguous against B10/B11).
    /// </summary>
    internal static bool IsNumberedVariant(string candidate, string baseName)
    {
        if (candidate.Length <= baseName.Length)
        {
            return false;
        }

        foreach (char c in baseName)
        {
            if (char.IsAsciiDigit(c))
            {
                return false;
            }
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

    private static (List<DirectionalEdge>? Edges, PathfindingFailure? Failure) ExtendToVariant(
        PartialRoute head,
        string baseTaxiway,
        string variantName,
        List<GroundNode> variantHsNodes,
        SearchContext ctx
    )
    {
        // Find the closest variant hold-short.
        GroundNode? targetHs = null;
        double bestDist = double.MaxValue;

        if (ctx.Layout.Nodes.TryGetValue(head.HeadNodeId, out GroundNode? headNode))
        {
            foreach (GroundNode hs in variantHsNodes)
            {
                double dist = GeoMath.DistanceNm(headNode.Position, hs.Position);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    targetHs = hs;
                }
            }
        }
        else
        {
            targetHs = variantHsNodes[0];
        }

        if (targetHs is null)
        {
            return (null, null);
        }

        ctx.DiagnosticLog?.Invoke($"[variant] extending {baseTaxiway}→{variantName} to hold-short #{targetHs.Id}");

        // Route from head to the variant hold-short via local search.
        (List<DirectionalEdge>? segEdges, PartialRoute? _, double _) = LocalSearchToJunction(head, variantName, targetHs.Id, ctx);
        if (segEdges is not null)
        {
            return (segEdges, null);
        }

        // Local search failed — fall back to AutoRouter.
        return ExtendToNearestHoldShort(head, [targetHs], ctx);
    }

    private static (List<DirectionalEdge>? Edges, PathfindingFailure? Failure) ExtendToNearestHoldShort(
        PartialRoute head,
        List<GroundNode> holdShortNodes,
        SearchContext ctx
    )
    {
        GroundNode? best = null;
        double bestDist = double.MaxValue;

        if (ctx.Layout.Nodes.TryGetValue(head.HeadNodeId, out GroundNode? headNode))
        {
            foreach (GroundNode hs in holdShortNodes)
            {
                double dist = GeoMath.DistanceNm(headNode.Position, hs.Position);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = hs;
                }
            }
        }
        else
        {
            best = holdShortNodes[0];
        }

        if (best is null)
        {
            return (null, null);
        }

        SearchContext detourCtx = ctx with
        {
            StartNodeId = head.HeadNodeId,
            Destination = new DestinationDescriptor(best.Id, null, null, null, DestinationKind.Node),
            WaypointSequence = [],
            AuthorizedTaxiways = null,
        };

        (TaxiRoute? route, PathfindingFailure? _) = AutoRouter.Run(detourCtx, startOverride: head);
        if (route is null)
        {
            return (null, null);
        }

        return ([.. route.Segments.Select(s => s.Edge)], null);
    }

    // -----------------------------------------------------------------------
    // Detour (§Decisions §1): when junction search fails, try numbered+RAMP bridge
    // -----------------------------------------------------------------------

    /// <summary>
    /// Record a mandatory-connector insertion: <paramref name="fromTaxiway"/> and
    /// <paramref name="toTaxiway"/> are consecutive cleared taxiways with no direct junction, so
    /// the detour bridged them via one or more connectors. The connector names are the distinct
    /// single-name (non-junction-arc) taxiways the detour traversed other than from/to and other
    /// than any taxiway already named in the clearance (<paramref name="tokens"/>) — only a
    /// taxiway the controller did not name counts as an inserted connector worth flagging. When
    /// the bridge uses no such taxiway, nothing is recorded.
    /// </summary>
    private static void RecordConnectorInsertion(
        List<ConnectorInsertion> insertions,
        string fromTaxiway,
        string toTaxiway,
        IReadOnlyList<DirectionalEdge> detourEdges,
        IReadOnlyList<WaypointToken> tokens
    )
    {
        var cleared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (WaypointToken token in tokens)
        {
            if (!token.IsNodeRef)
            {
                cleared.Add(token.Name);
            }
        }

        var connectors = new List<string>();
        foreach (DirectionalEdge edge in detourEdges)
        {
            if (edge.Edge is GroundArc { TaxiwayNames.Length: >= 2 })
            {
                continue;
            }

            string name = edge.TaxiwayName;
            if (cleared.Contains(name) || connectors.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            connectors.Add(name);
        }

        if (connectors.Count > 0)
        {
            insertions.Add(new ConnectorInsertion(fromTaxiway, toTaxiway, connectors));
        }
    }

    /// <summary>
    /// Failure returned in place of a detour when resolving a segment inside a look-ahead
    /// probe. Signals the probe that this continuation would need the whole-airport detour —
    /// a strong negative signal against the candidate junction that led here.
    /// </summary>
    private static PathfindingFailure DetourSuppressedFailure(string fromTaxiway, string toTaxiway, PartialRoute head) =>
        new(
            FailureKind.TransitionInfeasible,
            $"[probe] {fromTaxiway} → {toTaxiway} would require a detour from node {head.HeadNodeId}",
            fromTaxiway,
            $"{fromTaxiway} → {toTaxiway}",
            null
        );

    private static (List<DirectionalEdge>? Edges, PartialRoute? Head, PathfindingFailure? Failure) TryDetour(
        PartialRoute head,
        string fromTaxiway,
        string toTaxiway,
        SearchContext ctx
    )
    {
        ctx.DiagnosticLog?.Invoke($"[detour] attempting detour {fromTaxiway}→{toTaxiway} from head={head.HeadNodeId}");

        // A runway can only be entered or left where a taxiway physically crosses it — there is no
        // "connect via a connector taxiway" detour onto a runway surface. When either side of the
        // transition is a runway and no direct junction exists, fail cleanly rather than fabricating a
        // connector route onto the runway.
        if (IsRunwayWaypoint(fromTaxiway) || IsRunwayWaypoint(toTaxiway))
        {
            string taxiway = IsRunwayWaypoint(fromTaxiway) ? toTaxiway : fromTaxiway;
            string runway = IsRunwayWaypoint(fromTaxiway) ? fromTaxiway : toTaxiway;
            string subject = IsRunwayWaypoint(taxiway) ? $"Runway {RunwayWaypointDisplay(taxiway)}" : $"Taxiway {taxiway}";
            return (
                null,
                null,
                new PathfindingFailure(
                    FailureKind.TaxiwayNotConnected,
                    $"{subject} does not intersect runway {RunwayWaypointDisplay(runway)}.",
                    taxiway,
                    $"{fromTaxiway} → {toTaxiway}",
                    null
                )
            );
        }

        // Find entry nodes onto toTaxiway — any node on that taxiway.
        List<GroundNode> toTaxiwayNodes = ctx.Layout.GetNodesOnTaxiway(toTaxiway);
        if (toTaxiwayNodes.Count == 0)
        {
            return (
                null,
                null,
                new PathfindingFailure(
                    FailureKind.TaxiwayNotConnected,
                    $"Cannot find taxiway {toTaxiway} in layout",
                    toTaxiway,
                    $"{fromTaxiway} → {toTaxiway}",
                    null
                )
            );
        }

        (GroundNode? bestEntry, TaxiRoute? bestRoute, double bestScore) = PickDetourEntry(head, fromTaxiway, toTaxiway, ctx);

        if (bestRoute is null)
        {
            return (
                null,
                null,
                new PathfindingFailure(
                    FailureKind.TransitionInfeasible,
                    $"No valid path from {fromTaxiway} to {toTaxiway} — transition infeasible from node {head.HeadNodeId}",
                    fromTaxiway,
                    $"{fromTaxiway} → {toTaxiway}",
                    null
                )
            );
        }

        ctx.DiagnosticLog?.Invoke(
            $"[detour] found detour {fromTaxiway}→{toTaxiway} via #{bestEntry!.Id} segs={bestRoute.Segments.Count} score={bestScore:F3}"
        );

        PartialRoute newHead = BuildHeadFromRoute(head, bestRoute);
        return ([.. bestRoute.Segments.Select(s => s.Edge)], newHead, null);
    }

    /// <summary>
    /// Pick the node on <paramref name="toTaxiway"/> the bridge should land on, and the bridge route to it.
    /// One bounded <see cref="AutoRouter"/> run per candidate node (each inheriting the authorized set, so
    /// numbered connectors and RAMP beat unnamed letter taxiways), ranked by two terms:
    /// <see cref="BridgePavementCost"/> plus <see cref="ReversalAgainstPoseCost"/>.
    ///
    /// <para>The pavement cost carries the bridge's soft taxiway policy — without its unauthorized-letter
    /// charge SFO's Y→A bridge takes taxiway H, which the controller never cleared, over the AY3 connector.
    /// The reversal charge carries the aircraft's pose, which the bridge's raw length is blind to: an E75L
    /// resting on SFO's taxilane Y facing 208° takes the 261 ft hop back through AY2 <em>behind</em> its
    /// nose over the 1,383 ft bridge through AY3 ahead of it, and <c>TAXI Y A A1 1R</c> then begins with a
    /// 180° about-face in a ramp alley. Charged for the turn-around, the connector ahead wins even though
    /// its bridge is five times as long.</para>
    ///
    /// <para>With no pose to rank by — a route resolved from a bare node, with neither a prior edge nor a
    /// start heading — neither term carries directional information, so the order is the shortest
    /// bridge.</para>
    ///
    /// <para>Ranking may reorder the bridges but must not cost the clearance a taxiway the shortest bridge
    /// reached: when the bridge starts <em>off</em> <paramref name="fromTaxiway"/> (it is how the route gets
    /// onto it), the ranked candidate does not reach it and the shortest-bridge candidate does, the shortest
    /// bridge is kept — otherwise SFO gate G3's <c>TAXI A Q B F 28L HS 1L</c>, which bridges A→Q straight
    /// from the stand, would arrive on Q having never touched A and <see cref="ResolveExplicit"/>'s
    /// honor-named-taxiway check would refuse the whole clearance. It is deliberately only a do-no-harm
    /// guard and not "always prefer a candidate that reaches <paramref name="fromTaxiway"/>": when no bridge
    /// reaches the taxiway at all, the clearance is one the resolver is supposed to fail (gate B20S's
    /// <c>TAXI M5 …</c> across the ramp, issue #396, whose free-space cut keys on exactly that failure), and
    /// hunting down a far-side bridge that touches M5 would take the failure away.</para>
    /// </summary>
    private static (GroundNode? Entry, TaxiRoute? Route, double Score) PickDetourEntry(
        PartialRoute head,
        string fromTaxiway,
        string toTaxiway,
        SearchContext ctx
    )
    {
        bool rankByPose = (head.LastEdge is not null) || (ctx.StartHeadingTrue is not null);
        bool guardFromTaxiway = rankByPose && !fromTaxiway.StartsWith('#') && !HeadHasReachedTaxiway(head, fromTaxiway, ctx);

        (GroundNode? Entry, TaxiRoute? Route, double Score) best = (null, null, double.MaxValue);
        (GroundNode? Entry, TaxiRoute? Route, double Score) shortest = (null, null, double.MaxValue);

        foreach (GroundNode entryNode in ctx.Layout.GetNodesOnTaxiway(toTaxiway))
        {
            if (entryNode.Id == head.HeadNodeId)
            {
                continue;
            }

            (TaxiRoute? route, PathfindingFailure? _) = RunBoundedDetour(BuildDetourContext(ctx, head.HeadNodeId, entryNode.Id), head);
            if (route is null)
            {
                continue;
            }

            double score = rankByPose ? BridgePavementCost(route, ctx) + ReversalAgainstPoseCost(head, route, ctx) : route.TotalDistanceNm;

            if (score < best.Score)
            {
                best = (entryNode, route, score);
            }

            if (guardFromTaxiway && (route.TotalDistanceNm < shortest.Score))
            {
                shortest = (entryNode, route, route.TotalDistanceNm);
            }
        }

        if (
            guardFromTaxiway
            && (best.Route is not null)
            && (shortest.Route is not null)
            && !RouteReachesTaxiway(best.Route, fromTaxiway)
            && RouteReachesTaxiway(shortest.Route, fromTaxiway)
        )
        {
            ctx.DiagnosticLog?.Invoke($"[detour] cost pick drops {fromTaxiway}; keeping the shortest bridge via #{shortest.Entry!.Id}");
            return shortest;
        }

        return best;
    }

    /// <summary>
    /// What a candidate bridge's pavement costs, in the cost function's own terms: its length, a runway's
    /// ten times over (<see cref="RouteCostFunction.RunwayCenterlineDistanceMultiplier"/>), the
    /// <see cref="RouteCostFunction.UnauthorizedTaxiwayFirstUseCostNm"/> charge for each unnamed letter
    /// taxiway it threads (first use only, as <see cref="RouteCostFunction.IncrementalCost"/> charges it).
    ///
    /// <para>The unauthorized charge is the term that has to be here: it is the bridge's whole soft policy —
    /// a numbered connector or RAMP is free, an unnamed letter taxiway is dearer but usable — and without it
    /// SFO's Y→A bridge takes taxiway H, which the controller never cleared, over the AY3 connector.</para>
    ///
    /// <para>The turn budget, the per-transition charge and the per-crossing charge are deliberately left
    /// out. All three count pieces of pavement a bridge threads, which across candidate landings is junction
    /// geometry rather than route quality — a bridge through a ramp threads more corners and more hold-short
    /// bars than one cutting across a runway — and together they outweigh thousands of feet of taxiing: with
    /// them, OAK's <c>TAXI F 33 D C B RWY 28R</c> prices a 4,015 ft bridge down D and along runway 15/33 at
    /// 1.01 nm against the correct 2,810 ft ramp bridge's 1.26 nm, and the route that follows can no longer
    /// make the turn onto D. Nothing is lost by leaving them out: every bar on the resolved route is
    /// annotated as a hold-short the controller still has to clear, and driving <em>along</em> a runway is
    /// still ten times its length.</para>
    /// </summary>
    private static double BridgePavementCost(TaxiRoute route, SearchContext ctx)
    {
        double cost = 0.0;
        var chargedTaxiways = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (TaxiRouteSegment seg in route.Segments)
        {
            IGroundEdge edge = seg.Edge.Edge;
            cost += edge.DistanceNm * (edge.IsRunwayCenterline ? RouteCostFunction.RunwayCenterlineDistanceMultiplier : 1.0);

            string name = RouteCostFunction.ResolveTaxiwayName(edge, seg.FromNodeId);
            if (
                (ctx.AuthorizedTaxiways is { } authorized)
                && SearchContext.IsLetterOnlyTaxiway(name)
                && !authorized.Contains(name)
                && chargedTaxiways.Add(name)
            )
            {
                cost += RouteCostFunction.UnauthorizedTaxiwayFirstUseCostNm;
            }
        }

        return cost;
    }

    /// <summary>
    /// <see cref="RouteCostFunction.DirectionReversalCostNm"/> when <paramref name="route"/>'s first edge
    /// is an about-face against the pose the aircraft holds at <paramref name="head"/> — the prior
    /// segment's arrival bearing, or <see cref="SearchContext.StartHeadingTrue"/> when the bridge starts
    /// where the aircraft stands — and 0 otherwise. A bridge's first edge is the search's first edge, where
    /// the turn-budget term is skipped (there is no prior edge inside the search) and only the soft
    /// first-hop bias resists, so without this charge a connector 180° behind the nose is priced as if the
    /// about-face were free. It is applied in the ranking rather than inside the detour A* for the reason
    /// <see cref="RouteCostFunction.IncrementalCost"/> gives — a per-edge reversal term breaks the
    /// heuristic's admissibility — and it is the same constant, so a bridge that must reverse stays usable
    /// when every candidate reverses.
    ///
    /// <para>"About-face" is the pathfinder's own line between a turn and a reversal:
    /// <see cref="CategoryLimits.MaxHeadingChangeDeg"/>, the heading change past which
    /// <see cref="GeometricAdmissibility"/> refuses an edge as undrivable (135° for a jet). Anything
    /// inside it is a turn the aircraft can drive and the clearance may well call for — a 90°-off first
    /// edge is how a route leaves a junction onto a crossing taxiway (SFO's <c>TAXI B H F C …</c> from the
    /// B/H junction), and charging those a reversal re-ranks ordinary bridges.</para>
    /// </summary>
    private static double ReversalAgainstPoseCost(PartialRoute head, TaxiRoute route, SearchContext ctx)
    {
        if (route.Segments.Count == 0)
        {
            return 0.0;
        }

        double? poseBearing = head.LastEdge is not null ? head.ArrivalBearing : ctx.StartHeadingTrue;
        if (poseBearing is not { } pose)
        {
            return 0.0;
        }

        double turnDeg = RouteCostFunction.HeadingDelta(pose, route.Segments[0].Edge.DepartureBearing);
        return (turnDeg > CategoryLimits.MaxHeadingChangeDeg(ctx.Category)) ? RouteCostFunction.DirectionReversalCostNm : 0.0;
    }

    /// <summary>
    /// Build a detour SearchContext for bridging two cleared taxiways that have no direct junction.
    /// Inherits the original <see cref="SearchContext.AuthorizedTaxiways"/> so the cost function still
    /// prefers numbered connectors and RAMP edges over unnamed letter taxiways (the soft policy:
    /// an unauthorized letter taxiway is penalized but usable as a last resort, then surfaced in the
    /// connector notification — never silently free, never hard-failing a resolvable clearance).
    /// </summary>
    private static SearchContext BuildDetourContext(SearchContext ctx, int fromNodeId, int toNodeId)
    {
        return ctx with
        {
            StartNodeId = fromNodeId,
            Destination = new DestinationDescriptor(toNodeId, null, null, null, DestinationKind.Node),
            WaypointSequence = [],
        };
    }

    /// <summary>
    /// Run a bounded AutoRouter detour search. Passes the prior segment's <paramref name="priorHead"/>
    /// as the AutoRouter start so admissibility fires on the detour's first edge — without this,
    /// the detour can pick a first edge that U-turns against the aircraft's existing heading.
    /// </summary>
    private static (TaxiRoute? Route, PathfindingFailure? Failure) RunBoundedDetour(SearchContext ctx, PartialRoute priorHead) =>
        AutoRouter.Run(ctx, startOverride: priorHead, maxExpansions: MaxDetourExpansions);

    /// <summary>
    /// True when the walk has already reached <paramref name="taxiwayName"/>: the head stands on a node
    /// incident to it, or an edge already walked is labeled for it. A bridge from such a head is a
    /// transition off a taxiway the route has honored; one from anywhere else is how the route gets onto
    /// the taxiway in the first place and must still touch it (see the honor check in
    /// <see cref="ResolveExplicit"/>).
    /// </summary>
    private static bool HeadHasReachedTaxiway(PartialRoute head, string taxiwayName, SearchContext ctx)
    {
        if (ctx.Layout.Nodes.TryGetValue(head.HeadNodeId, out GroundNode? node) && NodeIncidentToTaxiway(node, taxiwayName))
        {
            return true;
        }

        for (PartialRoute? cursor = head; cursor?.LastEdge is not null; cursor = cursor.Previous)
        {
            if (cursor.LastEdge.MatchesTaxiway(taxiwayName))
            {
                return true;
            }
        }

        return false;
    }

    // -----------------------------------------------------------------------
    // Parking / spot extension
    // -----------------------------------------------------------------------

    private static (List<DirectionalEdge>? Edges, PathfindingFailure? Failure) ExtendToDestination(
        PartialRoute head,
        int destinationNodeId,
        SearchContext ctx
    )
    {
        ctx.DiagnosticLog?.Invoke($"[extend] extending to destination #{destinationNodeId} from head={head.HeadNodeId}");

        SearchContext extCtx = ctx with
        {
            StartNodeId = head.HeadNodeId,
            Destination = new DestinationDescriptor(
                destinationNodeId,
                null,
                ctx.Destination.ParkingName,
                ctx.Destination.SpotName,
                ctx.Destination.Kind
            ),
            WaypointSequence = [],
            AuthorizedTaxiways = null,
        };

        // Prefer an extension confined to the cleared taxiways + numbered connectors + RAMP (the
        // "impliable" set): a controller clearing "TAXI B @F1" expects the gate reached by staying on B
        // and turning onto the ramp connector, NOT by threading uncleared letter taxiways (e.g. B Q A T9
        // RAMP). Hard-exclude every letter taxiway the controller did not name — numbered connectors and
        // RAMP stay free, so the join stays on the cleared taxiway until a numbered ramp connector
        // branches off. Fall back to an unconstrained search only when the confined one finds no route,
        // so a gate genuinely reachable only across an uncleared taxiway still resolves. Mirrors the
        // runway-destination fallback's hard-constraint (see ResolveExplicit's last-resort A*).
        IReadOnlySet<string> unauthorized = UnnamedLetterTaxiways(ctx.Layout, ctx.AuthorizedTaxiways);
        if (unauthorized.Count > 0)
        {
            SearchContext confinedCtx = extCtx with { AvoidedTaxiways = unauthorized, AvoidMode = AvoidTaxiwayMode.HardExclude };
            (TaxiRoute? confinedRoute, PathfindingFailure? _) = AutoRouter.Run(confinedCtx, startOverride: head);
            if (confinedRoute is not null)
            {
                return ([.. confinedRoute.Segments.Select(s => s.Edge)], null);
            }

            ctx.DiagnosticLog?.Invoke("[extend] confined (cleared+numbered+RAMP) extension found no route; retrying unconstrained");
        }

        (TaxiRoute? route, PathfindingFailure? failure) = AutoRouter.Run(extCtx, startOverride: head);
        if (failure is not null || route is null)
        {
            return (
                null,
                new PathfindingFailure(
                    FailureKind.DestinationUnreachable,
                    $"Cannot reach destination from end of taxi path (node {head.HeadNodeId})",
                    null,
                    null,
                    null
                )
            );
        }

        return ([.. route.Segments.Select(s => s.Edge)], null);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Build a <see cref="PartialRoute"/> head that reflects the state after traversing
    /// <paramref name="route"/> from <paramref name="startHead"/>.
    /// </summary>
    private static PartialRoute BuildHeadFromRoute(PartialRoute startHead, TaxiRoute route)
    {
        if (route.Segments.Count == 0)
        {
            return startHead;
        }

        PartialRoute current = startHead;
        foreach (TaxiRouteSegment seg in route.Segments)
        {
            current = current with
            {
                HeadNodeId = seg.ToNodeId,
                ArrivalBearing = seg.Edge.ArrivalBearing,
                LastEdge = seg.Edge.Edge,
                LastTaxiwayName = seg.TaxiwayName,
                Previous = current,
                Depth = current.Depth + 1,
                AccumulatedCost = current.AccumulatedCost + seg.Edge.DistanceNm,
                VisitedNodeIds = current.VisitedNodeIds.Add(seg.ToNodeId),
            };
        }

        return current;
    }
}
