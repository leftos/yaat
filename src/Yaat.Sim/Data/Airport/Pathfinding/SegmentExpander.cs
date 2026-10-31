using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Phases.Ground;

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

    /// <summary>
    /// Resolve the clearance with the start bridged onto the first taxiway by <see cref="BridgeStartToTaxiway"/>. Only when
    /// that fails — the bridge reached no node of the first taxiway, and the clearance did not resolve without it — is the
    /// clearance re-resolved behind an auto-routed start leg (<see cref="TryAutoRoutedStartLeg"/>), so every route that
    /// resolves through the bridge resolves exactly as before. When the leg does not help, the bridged failure is reported.
    /// </summary>
    private static (TaxiRoute? Route, PathfindingFailure? Failure) ResolveExplicit(SearchContext ctx)
    {
        (TaxiRoute? Route, PathfindingFailure? Failure) bridged = ResolveExplicitFrom(ctx, null, out string? unbridgedTaxiway);
        if ((bridged.Route is not null) || (unbridgedTaxiway is null) || (TryAutoRoutedStartLeg(ctx, unbridgedTaxiway) is not { } leg))
        {
            return bridged;
        }

        (TaxiRoute? Route, PathfindingFailure? Failure) viaLeg = ResolveExplicitFrom(ctx, leg, out _);
        ctx.DiagnosticLog?.Invoke(
            $"[startleg] re-resolved behind a {leg.Edges.Count}-edge start leg onto {unbridgedTaxiway}: "
                + (viaLeg.Route is not null ? "resolved" : $"failed ({viaLeg.Failure?.HumanMessage})")
        );
        return viaLeg.Route is not null ? viaLeg : bridged;
    }

    /// <summary>
    /// Resolve the clearance from <see cref="SearchContext.StartNodeId"/>, opening with <paramref name="startLeg"/> when one
    /// is given and with the hop-limited <see cref="BridgeStartToTaxiway"/> otherwise. <paramref name="unbridgedTaxiway"/>
    /// names the first taxiway when the bridge was needed and found no node of it; null otherwise.
    /// </summary>
    private static (TaxiRoute? Route, PathfindingFailure? Failure) ResolveExplicitFrom(
        SearchContext ctx,
        StartLeg? startLeg,
        out string? unbridgedTaxiway
    )
    {
        unbridgedTaxiway = null;
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
        if (startLeg is not null)
        {
            edges.AddRange(startLeg.Edges);
            head = startLeg.Head with { VisitedNodeIds = VisitedNodeSet.Single(startLeg.Head.HeadNodeId) };
        }
        else if (!resolvedWaypoints[0].IsNodeRef)
        {
            LatLon? bridgeBias = ResolveBridgeBias(resolvedWaypoints, head, ctx);
            (List<DirectionalEdge>? bridgeEdges, PartialRoute? bridgeHead) = BridgeStartToTaxiway(head, resolvedWaypoints[0].Name, bridgeBias, ctx);
            if (bridgeEdges.Count > 0)
            {
                edges.AddRange(bridgeEdges);
                head = bridgeHead with { VisitedNodeIds = VisitedNodeSet.Single(bridgeHead.HeadNodeId) };
            }
            else if (!resolvedWaypoints[0].IsRunway && !StartIsOnTaxiway(ctx, resolvedWaypoints[0].Name))
            {
                unbridgedTaxiway = resolvedWaypoints[0].Name;
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
        // From an off-taxiway head (SFO: holding short of C on ramp taxilane T421) the extension is
        // only deferred: taken at once it would route the whole clearance from the start node to a
        // variant's bar, skipping the taxiway's own full-length bar. The constrained fallback below
        // is preferred and runs first; the deferred edges apply only when it leaves the route short
        // of the bar (OAK: B has no 28L bar, only the R stub, so the extension stands there). The
        // step's failure is not gated: it is Run's cue to try a connector stub, so it is returned
        // whatever the head.
        List<DirectionalEdge>? deferredVariantEdges = null;
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
                    if (WalkReachedTaxiway(ctx, head, edges, lastWaypoint.Name))
                    {
                        edges.AddRange(varEdges);
                    }
                    else
                    {
                        Log.LogDebug("[variant] deferred: head #{HeadNodeId} is not on {Taxiway}", head.HeadNodeId, lastWaypoint.Name);
                        ctx.DiagnosticLog?.Invoke($"[variant] deferred: head #{head.HeadNodeId} not on {lastWaypoint.Name}");
                        deferredVariantEdges = varEdges;
                    }
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

        // The fallback block above only substitutes its route when that route reaches a bar, so the deferred
        // extension never mixes with a fallback route: it applies exactly when the route is still
        // short of the bar, as the off-taxiway head's last resort.
        if (
            (deferredVariantEdges is not null)
            && ctx.Destination.RunwayId is { } deferredRunway
            && !RouteReachesRunwayHoldShort(edges, deferredRunway, ctx)
        )
        {
            edges.AddRange(deferredVariantEdges);
            ctx.DiagnosticLog?.Invoke("[variant] deferred extension applied");
        }

        // Parking/spot extension: after the named taxiway walk, auto-route to destination.
        string? impliedOneWayLane = null;
        if (ctx.Destination.Kind is DestinationKind.Parking or DestinationKind.Spot or DestinationKind.Helipad)
        {
            if (ctx.Destination.TargetNodeId is { } destId && head.HeadNodeId != destId)
            {
                (List<DirectionalEdge>? extEdges, string? lane, PathfindingFailure? extFailure) = ExtendToDestination(
                    head,
                    destId,
                    ctx,
                    UnclearedMovementAreaTaxiways(ctx)
                );
                if (extFailure is not null)
                {
                    return (null, extFailure);
                }

                if (extEdges is not null)
                {
                    edges.AddRange(extEdges);
                }

                impliedOneWayLane = lane;
            }
        }

        TaxiRoute route = RouteMaterialiser.Materialise(edges, ctx, insertions);
        foreach (string lane in new[] { startLeg?.ImpliedLane, impliedOneWayLane }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            route.Warnings.Add(RouteMaterialiser.NotInClearanceWarning(lane));
            route.ImpliedLanes.Add(lane);
        }

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
            return RouteFromNodeRefToTaxiway(head, current.ResolvedNodeId, next.Name, index, ctx);
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

        // A bare final taxiway reached across a runway (an offset or declared crossing) ends past the far bar with the whole
        // aircraft clear of the runway, never on the centreline join.
        if (EndClearOfCrossedRunway(head, waypoint, precedingTaxiway, ctx) is { } clearOfRunway)
        {
            return clearOfRunway;
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

        return WalkToNaturalTerminus(head, waypoint.Name, ctx, bias, waypoint.TurnHint);
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

        ctx.ResolutionAdvisories.Add(NoDestinationTerminusAdvisory(finalTaxiway));
        ctx.DiagnosticLog?.Invoke($"[terminus-junction] {precedingTaxiway}/{finalTaxiway} stop={junctionHead.HeadNodeId} edges={edges.Count}");
        return (edges, junctionHead, null);
    }

    /// <summary>
    /// The controller's advisory for a clearance with no destination that ends where it meets its last taxiway
    /// <paramref name="taxiway"/>: <c>holding short of B: route ends at B, no destination given</c>.
    /// </summary>
    public static string NoDestinationTerminusAdvisory(string taxiway) =>
        $"holding short of {taxiway}: route ends at {taxiway}, no destination given";

    /// <summary>
    /// The last taxiway of a clearance with no destination when <paramref name="route"/> stops where it meets that taxiway
    /// without driving it: the transition-junction stop (<c>TAXI C B</c>), or a bridge from the occupied taxiway onto it that
    /// the walk along it does not leave (<c>TAXI B</c> given on C). Null for any other route: one with a destination, one
    /// whose last taxiway is a node reference or a runway, or one that drives its last taxiway.
    /// </summary>
    public static string? NoDestinationTerminusTaxiway(TaxiRoute route, SearchContext ctx)
    {
        if ((ctx.Destination.Kind != DestinationKind.EndOfLastTaxiway) || (ctx.WaypointSequence.Count == 0) || (route.Segments.Count == 0))
        {
            return null;
        }

        string final = ctx.WaypointSequence[^1];
        if (!IsPlainTaxiwayToken(ctx.Layout, final))
        {
            return null;
        }

        TaxiRouteSegment last = route.Segments[^1];
        bool endsOnFinal = last.Edge.ToNode.Edges.Any(e => e.MatchesTaxiway(final));
        return (endsOnFinal && !last.Edge.Edge.MatchesTaxiway(final)) ? final : null;
    }

    /// <summary>
    /// Largest turn, in degrees, the walk past a crossed runway's far bar takes as straight ahead where it has a choice (the
    /// final taxiway forks, or the walk carries into another taxiway); a sharper one is a direction the clearance never gave. A
    /// sim convention; no FAA figure.
    /// </summary>
    private const double StraightAheadMaxTurnDeg = 30.0;

    /// <summary>
    /// Sharpest bend, in degrees, the walk follows where the final taxiway simply continues (its one continuation): the bend
    /// limit the pathfinder uses elsewhere, <see cref="ComputeDirectionReversalPenalty"/>'s threshold. A sim convention; no FAA
    /// figure.
    /// </summary>
    private const double BendMaxTurnDeg = 90.0;

    /// <summary>
    /// Feet beyond the crossed runway's outward walk bound (<see cref="AirportGroundLayout.OutwardWalkBoundFt(in RunwayRectangle)"/>,
    /// its farthest hold-line bar from the centerline) the walk still looks for the far bar, measured from the centerline.
    /// </summary>
    private const double FarBarSearchMarginFt = 50.0;

    /// <summary>Most edges the walk past a crossed runway's far bar takes, a bound against a malformed layout.</summary>
    private const int MaxClearOfRunwayEdges = 64;

    /// <summary>Most refinements <see cref="CutPast"/> makes to its closed-form cut point.</summary>
    private const int MaxCutRefinements = 3;

    /// <summary>Feet past the margin the refined cut point aims the tail at, so rounding never leaves it just short.</summary>
    private const double CutRefinementSlackFt = 0.001;

    /// <summary>
    /// The walk from a crossing's far join straight ahead along the final taxiway: its edges and head, how far past the far
    /// hold line the nearer of the aircraft's tail and nose stands at the end, the taxiway the end protrudes into (null when
    /// none), and whether the whole aircraft clears.
    /// </summary>
    private sealed record ClearOfRunwayWalk(List<DirectionalEdge> Edges, PartialRoute Head, double ClearOfLineFt, string? CarriedInto, bool Clears);

    /// <summary>
    /// What the walk past a crossed runway's far bar measures against: the runway crossed, the aircraft's length, the far
    /// hold line (through the far bar, across the final taxiway's axis there, <paramref name="AxisBearingDeg"/>) and the
    /// search.
    /// </summary>
    private sealed record ClearOfRunwayProbe(RunwayIdentifier Crossed, double LengthFt, LatLon FarBar, double AxisBearingDeg, SearchContext Ctx)
    {
        /// <summary>How far, in feet, <paramref name="position"/> lies past the far hold line, measured perpendicular to it.</summary>
        public double PastLineFt(LatLon position) => SegmentExpander.PastLineFt(FarBar, AxisBearingDeg, position);

        /// <summary>
        /// How far, in feet, the nearer of the tail and nose of an aircraft whose centre stands at <paramref name="position"/>,
        /// nosed <paramref name="headingDeg"/>, lies past the far hold line (<see cref="FuselagePastLineFt"/>).
        /// </summary>
        public double ClearOfLineFt(LatLon position, double headingDeg) => FuselagePastLineFt(FarBar, AxisBearingDeg, position, headingDeg, LengthFt);
    }

    /// <summary>
    /// How far, in feet, the nearer of the tail and the nose of an aircraft <paramref name="lengthFt"/> long, its centre at
    /// <paramref name="centre"/> and nosed <paramref name="headingDeg"/>, lies past the hold line through
    /// <paramref name="farBar"/> across the axis <paramref name="axisBearingDeg"/>, measured square to the line. The fuselage
    /// is a straight segment, so its two ends bound every point of it: the whole aircraft is past the line by at least this.
    /// </summary>
    public static double FuselagePastLineFt(LatLon farBar, double axisBearingDeg, LatLon centre, double headingDeg, double lengthFt)
    {
        double tailFt = PastLineFt(farBar, axisBearingDeg, ProjectAlong(centre, headingDeg + 180.0, lengthFt / 2.0));
        double noseFt = PastLineFt(farBar, axisBearingDeg, ProjectAlong(centre, headingDeg, lengthFt / 2.0));
        return Math.Min(tailFt, noseFt);
    }

    /// <summary>
    /// How far, in feet, <paramref name="position"/> lies past the hold line through <paramref name="farBar"/> across the axis
    /// <paramref name="axisBearingDeg"/>, measured perpendicular to the line (negative short of it).
    /// </summary>
    private static double PastLineFt(LatLon farBar, double axisBearingDeg, LatLon position)
    {
        double distanceFt = GeoMath.DistanceNm(farBar, position) * GeoMath.FeetPerNm;
        if (distanceFt <= 0.0)
        {
            return 0.0;
        }

        double offAxisRad = (GeoMath.BearingTo(farBar, position) - axisBearingDeg) * Math.PI / 180.0;
        return distanceFt * Math.Cos(offAxisRad);
    }

    /// <summary><paramref name="from"/> moved <paramref name="alongFt"/> feet on <paramref name="bearingDeg"/>, as the walk's cuts project.</summary>
    internal static LatLon ProjectAlong(LatLon from, double bearingDeg, double alongFt) =>
        GeoMath.ProjectPointRaw(from, bearingDeg, alongFt / GeoMath.FeetPerNm);

    /// <summary>
    /// End a bare final taxiway reached across a runway (an offset crossing, or a pair the sidecar declares) with the whole
    /// aircraft clear of the runway: straight ahead along the final taxiway from its join, to the first point where the tail
    /// and the nose (half the aircraft's length behind and ahead of its centre, along its heading there) both stand
    /// <see cref="RunwayTailClearance.MarginFt"/> past the far hold line, measured perpendicular to that line, carried
    /// straight into a taxiway reached first (AIM 4-3-21.b, 2-3-5.a.1). On a straight taxiway that is half the length plus the
    /// margin along it; past a bend the fuselage is measured where it actually lies. Refused
    /// (<see cref="FailureKind.NoRoomToClearRunway"/>) when a T, a runway centreline or another runway's bar comes first, or
    /// lies too close ahead of the end; null, leaving the junction stop, when the clearance is not bare
    /// (<see cref="BareCrossingRunway"/>) or the final taxiway carries no bar of the runway near its join.
    /// </summary>
    private static (List<DirectionalEdge>? Edges, PartialRoute? Head, PathfindingFailure? Failure)? EndClearOfCrossedRunway(
        PartialRoute head,
        WaypointToken waypoint,
        string? precedingTaxiway,
        SearchContext ctx
    )
    {
        if ((precedingTaxiway is null) || (BareCrossingRunway(head, waypoint, precedingTaxiway, ctx) is not { } crossed))
        {
            return null;
        }

        string final = waypoint.Name;
        string runway = crossed.ToString();
        double lengthFt =
            ctx.AircraftLengthFt
            ?? throw new InvalidOperationException($"a bare taxi {precedingTaxiway} {final} across {runway} was resolved with no aircraft length");
        if (WalkClearOfRunway(head, final, crossed, lengthFt, ctx) is not { } walk)
        {
            ctx.DiagnosticLog?.Invoke($"[clear-of-runway] {precedingTaxiway}→{final} across {runway}: no {runway} bar near the join on {final}");
            return null;
        }

        string trace =
            $"{precedingTaxiway}→{final} across {runway}: end #{walk.Head.HeadNodeId}, tail and nose {walk.ClearOfLineFt:F1}ft past "
            + $"the far line (need {RunwayTailClearance.MarginFt:F0}ft) into {walk.CarriedInto ?? "-"}";
        if (!walk.Clears)
        {
            ctx.DiagnosticLog?.Invoke($"[clear-of-runway] refused {trace}");
            return (null, null, NoRoomToClearRunwayFailure(precedingTaxiway, final, runway));
        }

        string advisory = ClearOfRunwayAdvisory(final, runway, walk.CarriedInto);
        ctx.ClearOfRunwayEnds.Record(walk.Head.HeadNodeId, new ClearOfRunwayEnd(runway, precedingTaxiway, walk.CarriedInto, advisory));
        ctx.DiagnosticLog?.Invoke($"[clear-of-runway] {trace}");
        return (walk.Edges, walk.Head, null);
    }

    /// <summary>
    /// The runway a bare clearance ends across: the final taxiway <paramref name="waypoint"/> is reached across a runway from
    /// <paramref name="precedingTaxiway"/>, the head stands at that crossing's far join, the waypoint carries no turn hint, and
    /// the clearance gives nothing beyond the crossing (<see cref="IsBareCrossingClearance"/>). Null otherwise.
    /// </summary>
    private static RunwayIdentifier? BareCrossingRunway(PartialRoute head, WaypointToken waypoint, string precedingTaxiway, SearchContext ctx)
    {
        if (waypoint.TurnHint is not null)
        {
            return null;
        }

        OffsetRunwayCrossing? crossing = CachedOffsetRunwayCrossing(ctx, precedingTaxiway, waypoint.Name);
        if ((crossing is null) || ((head.HeadNodeId != crossing.Landing.Id) && (head.HeadNodeId != crossing.To.Via.Id)))
        {
            return null;
        }

        var crossed = RunwayIdentifier.Parse(RunwayWaypointDisplay(crossing.RunwayCenterline));
        return IsBareCrossingClearance(head, crossed, precedingTaxiway, waypoint.Name, ctx) ? crossed : null;
    }

    /// <summary>The refusal of a bare crossing of <paramref name="runway"/> onto <paramref name="finalTaxiway"/> with no room to clear it.</summary>
    private static PathfindingFailure NoRoomToClearRunwayFailure(string precedingTaxiway, string finalTaxiway, string runway) =>
        new(
            FailureKind.NoRoomToClearRunway,
            NoRoomToClearRunwayReason(finalTaxiway, runway),
            finalTaxiway,
            $"{precedingTaxiway} → {finalTaxiway}",
            null
        )
        {
            UnconnectedFrom = precedingTaxiway,
            UnconnectedTo = finalTaxiway,
            UnclearedRunway = runway,
        };

    /// <summary>
    /// The advisory a bare taxi across <paramref name="runway"/> carries: it ends on <paramref name="finalTaxiway"/> clear of
    /// the runway, nosed into <paramref name="carriedInto"/> when the end protrudes into it. The TAXI handler prefixes how the
    /// crossing's near bar stands once auto-cross and the CROSS keywords have applied.
    /// </summary>
    private static string ClearOfRunwayAdvisory(string finalTaxiway, string runway, string? carriedInto)
    {
        string nose = carriedInto is null ? "" : $", nose into {carriedInto}";
        return $"route ends on {finalTaxiway} clear of {runway}{nose}, no destination given";
    }

    /// <summary>
    /// Whether the clearance gives nothing beyond a crossing of <paramref name="crossed"/>: no destination but the CROSS
    /// anchor at one of its bars, and every hold short either of the runway crossed (unlocated, or located on
    /// <paramref name="precedingTaxiway"/>) or one the route has already passed (<see cref="HoldsShortBeforeCrossing"/>).
    /// </summary>
    private static bool IsBareCrossingClearance(
        PartialRoute head,
        RunwayIdentifier crossed,
        string precedingTaxiway,
        string finalTaxiway,
        SearchContext ctx
    )
    {
        bool bareDestination =
            (ctx.Destination.Kind == DestinationKind.EndOfLastTaxiway)
            || (
                (ctx.Destination.Kind == DestinationKind.Node)
                && (ctx.Destination.TargetNodeId is { } targetId)
                && ctx.Layout.Nodes.TryGetValue(targetId, out GroundNode? target)
                && IsBarOf(target, crossed)
            );
        if (!bareDestination)
        {
            return false;
        }

        return ctx.ExplicitHoldShorts.All(hs =>
            IsHoldShortOfCrossing(hs, crossed, precedingTaxiway) || HoldsShortBeforeCrossing(hs, head, precedingTaxiway, finalTaxiway, ctx)
        );
    }

    /// <summary>
    /// Whether <paramref name="hs"/> holds short of the runway crossed, unlocated or located on <paramref name="precedingTaxiway"/>.
    /// </summary>
    private static bool IsHoldShortOfCrossing(HoldShortTarget hs, RunwayIdentifier crossed, string precedingTaxiway) =>
        !hs.IsSpot
        && ((hs.OnTaxiway is null) || hs.OnTaxiway.Equals(precedingTaxiway, StringComparison.OrdinalIgnoreCase))
        && crossed.Contains(hs.Target);

    /// <summary>
    /// Whether <paramref name="hs"/> binds on the route walked before the crossing (a runway bar or a taxiway the head has
    /// already passed) and nowhere it would steer the end: never a spot, never of or on <paramref name="finalTaxiway"/> or of
    /// <paramref name="precedingTaxiway"/>, and never one <see cref="ResolveTerminusBias"/> would aim the final walk at.
    /// </summary>
    private static bool HoldsShortBeforeCrossing(
        HoldShortTarget hs,
        PartialRoute head,
        string precedingTaxiway,
        string finalTaxiway,
        SearchContext ctx
    )
    {
        bool namesTheEnd =
            hs.IsSpot
            || hs.Target.Equals(finalTaxiway, StringComparison.OrdinalIgnoreCase)
            || hs.Target.Equals(precedingTaxiway, StringComparison.OrdinalIgnoreCase)
            || (hs.OnTaxiway?.Equals(finalTaxiway, StringComparison.OrdinalIgnoreCase) == true);
        return !namesTheEnd
            && (ResolveTerminusBias(head, finalTaxiway, ctx with { ExplicitHoldShorts = new HashSet<HoldShortTarget> { hs } }) is null)
            && BindsOnWalkedRoute(hs, head, ctx);
    }

    /// <summary>
    /// Whether <paramref name="hs"/> binds at a node <paramref name="head"/> has already walked: a bar of its runway, or a node
    /// on its taxiway, on its <see cref="HoldShortTarget.OnTaxiway"/> when located.
    /// </summary>
    private static bool BindsOnWalkedRoute(HoldShortTarget hs, PartialRoute head, SearchContext ctx)
    {
        bool isRunway = ctx.Layout.TryGetRunwayCenterlineName(hs.Target, out _);
        for (PartialRoute? walked = head; walked is not null; walked = walked.Previous)
        {
            int id = walked.HeadNodeId;
            if (!ctx.Layout.Nodes.TryGetValue(id, out GroundNode? node) || ((hs.OnTaxiway is { } on) && !node.Edges.Any(e => e.MatchesTaxiway(on))))
            {
                continue;
            }

            if (isRunway ? IsRunwayHoldShort(id, hs.Target, ctx) : node.Edges.Any(e => e.MatchesTaxiway(hs.Target)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsBarOf(GroundNode node, RunwayIdentifier runway) =>
        (node.Type == GroundNodeType.RunwayHoldShort) && (node.RunwayId is { } id) && id.Contains(runway.End1);

    /// <summary>
    /// A bar of a runway other than <paramref name="crossed"/>, or a node on another runway's centreline: the walk from the
    /// crossing never carries beyond it.
    /// </summary>
    private static bool IsOtherRunway(GroundNode node, RunwayIdentifier crossed) =>
        ((node.Type == GroundNodeType.RunwayHoldShort) && !IsBarOf(node, crossed))
        || node.Edges.Any(e =>
            e.IsRunwayCenterline && !RunwayIdentifier.Parse(RunwayWaypointDisplay(RouteCostFunction.ResolveTaxiwayName(e, node.Id))).Overlaps(crossed)
        );

    /// <summary>
    /// Walk straight ahead from the crossing's far join along <paramref name="finalTaxiway"/> to its bar of the runway
    /// crossed (<see cref="FindFarBar"/>), then on past it until the aircraft stands clear of the runway
    /// (<see cref="WalkPastFarBar"/>), its hold line across the taxiway's axis as the walk arrives at the bar. Null when the
    /// final taxiway carries no such bar near its join.
    /// </summary>
    private static ClearOfRunwayWalk? WalkClearOfRunway(
        PartialRoute head,
        string finalTaxiway,
        RunwayIdentifier crossed,
        double lengthFt,
        SearchContext ctx
    )
    {
        if (FindFarBar(head, finalTaxiway, crossed, ctx) is not { } toBar)
        {
            return null;
        }

        GroundNode farBar = ctx.Layout.Nodes[toBar.Head.HeadNodeId];
        var probe = new ClearOfRunwayProbe(crossed, lengthFt, farBar.Position, toBar.Head.ArrivalBearing, ctx);
        ctx.DiagnosticLog?.Invoke($"[clear-of-runway] {finalTaxiway}'s far bar #{farBar.Id}, hold line across {probe.AxisBearingDeg:F1}°");
        return WalkPastFarBar(toBar.Edges, toBar.Head, finalTaxiway, probe);
    }

    /// <summary>
    /// The walk from the crossing's far join to <paramref name="finalTaxiway"/>'s bar of <paramref name="crossed"/>: along the
    /// final taxiway only, no farther from the crossed runway's centerline than its outward walk bound plus
    /// <see cref="FarBarSearchMarginFt"/>, before the taxiway's first junction and before another runway. A taxiway meeting the
    /// runway at a shallow angle reaches its bar far along from the join, so the search is bounded across the runway, not along
    /// the taxiway. A fillet arc onto another taxiway is not a junction: a bar can sit on the arc's tangent node. Null when no
    /// such bar lies there.
    /// </summary>
    private static (List<DirectionalEdge> Edges, PartialRoute Head)? FindFarBar(
        PartialRoute head,
        string finalTaxiway,
        RunwayIdentifier crossed,
        SearchContext ctx
    )
    {
        if (IsBarOf(ctx.Layout.Nodes[head.HeadNodeId], crossed))
        {
            return ([], head);
        }

        if (WalkToBar(head, finalTaxiway, crossed, ctx.Layout) is not { } edges)
        {
            return null;
        }

        PartialRoute current = head;
        foreach (DirectionalEdge step in edges)
        {
            current = Advance(current, step.FromNode, step.Edge, step.ToNode, ctx);
        }

        return (edges, current);
    }

    /// <summary>
    /// The edges from <paramref name="head"/> along <paramref name="taxiway"/> to its bar of <paramref name="crossed"/>, as
    /// <see cref="FindFarBar"/> bounds the walk; null when no such bar lies there.
    /// </summary>
    private static List<DirectionalEdge>? WalkToBar(PartialRoute head, string taxiway, RunwayIdentifier crossed, AirportGroundLayout layout)
    {
        if (layout.FindRunway(crossed.End1) is not { } runway)
        {
            return null;
        }

        RunwayRectangle rect = RunwayCrossingDetector.BuildRunwayRectangle(runway);
        double searchBoundFt = layout.OutwardWalkBoundFt(rect) + FarBarSearchMarginFt;
        GroundNode node = layout.Nodes[head.HeadNodeId];
        IGroundEdge? edge = StraightestEdgeOnto(node, taxiway, head.ArrivalBearing);
        var edges = new List<DirectionalEdge>();
        PartialRoute current = head;
        while ((edge is not null) && (edges.Count < MaxClearOfRunwayEdges))
        {
            GroundNode next = edge.OtherNode(node);
            DirectionalEdge step = edge.Directed(node, next);
            edges.Add(step);
            current = Advance(current, step, taxiway);
            if (IsBarOf(next, crossed))
            {
                return edges;
            }

            if (
                (AirportGroundLayout.CrossTrackFromCenterlineFt(rect, next) > searchBoundFt)
                || IsOtherRunway(next, crossed)
                || next.Edges.Any(e => (e is not GroundArc) && !e.IsRunwayCenterline && !e.MatchesTaxiway(taxiway))
            )
            {
                return null;
            }

            edge = StraightAheadEdge(next, current, taxiway);
            node = next;
        }

        return null;
    }

    /// <summary>
    /// Walk on from the far bar at <paramref name="atBar"/>'s head, straight ahead along <paramref name="finalTaxiway"/> and
    /// carried straight into whatever taxiway it meets first, to the first point where the aircraft's tail and nose both stand
    /// <see cref="RunwayTailClearance.MarginFt"/> past the far hold line (<see cref="ClearOfRunwayProbe.ClearOfLineFt"/>):
    /// a straight edge is cut there by a virtual node, an arc taken whole, and the walk carries on past an end that falls
    /// short. Stopped short (not clear) by a T, a dead end, a runway centreline or another runway's bar (<see cref="EndAtNode"/>).
    /// </summary>
    private static ClearOfRunwayWalk WalkPastFarBar(List<DirectionalEdge> edges, PartialRoute atBar, string finalTaxiway, ClearOfRunwayProbe probe)
    {
        GroundNode node = probe.Ctx.Layout.Nodes[atBar.HeadNodeId];
        PartialRoute current = atBar;
        IGroundEdge? edge = StraightAheadEdge(node, current, finalTaxiway);
        string? carriedInto = CarriedInto(edge, finalTaxiway);
        double clearFt = probe.ClearOfLineFt(node.Position, current.ArrivalBearing);
        while ((edge is not null) && (edges.Count < MaxClearOfRunwayEdges))
        {
            string onTaxiway = carriedInto ?? finalTaxiway;
            if (CutPast(node, edge, probe) is { } cut)
            {
                double cutClearFt = probe.ClearOfLineFt(cut.End.Position, cut.BearingDeg);
                if (cutClearFt >= RunwayTailClearance.MarginFt)
                {
                    bool roomAhead = !OtherRunwayAhead(current, edge, cut.AlongFt, onTaxiway, probe);
                    edges.Add(cut.Edge.Directed(node, cut.End));
                    current = Advance(current, node, cut.Edge, cut.End, probe.Ctx);
                    return new ClearOfRunwayWalk(edges, current, cutClearFt, carriedInto, roomAhead);
                }
            }

            GroundNode next = edge.OtherNode(node);
            edges.Add(edge.Directed(node, next));
            current = Advance(current, node, edge, next, probe.Ctx);
            clearFt = probe.ClearOfLineFt(next.Position, current.ArrivalBearing);
            if (StopsWalkAt(next, probe.Crossed, clearFt >= RunwayTailClearance.MarginFt))
            {
                return EndAtNode(new ClearOfRunwayWalk(edges, current, clearFt, carriedInto, false), next, probe);
            }

            edge = StraightAheadEdge(next, current, onTaxiway);
            carriedInto ??= CarriedInto(edge, finalTaxiway);
            node = next;
        }

        return new ClearOfRunwayWalk(edges, current, clearFt, carriedInto, false);
    }

    /// <summary>
    /// The walk <paramref name="walk"/> ended on the layout node <paramref name="end"/>, judged: clear only where
    /// <see cref="EndClearsAt"/> allows it, with no other runway within half the aircraft's length straight ahead.
    /// </summary>
    private static ClearOfRunwayWalk EndAtNode(ClearOfRunwayWalk walk, GroundNode end, ClearOfRunwayProbe probe)
    {
        if (!EndClearsAt(end, probe.Crossed, walk.ClearOfLineFt))
        {
            return walk;
        }

        string taxiway = walk.CarriedInto ?? walk.Head.LastTaxiwayName;
        IGroundEdge? ahead = StraightAheadEdge(end, walk.Head, taxiway);
        bool roomAhead = (ahead is null) || !OtherRunwayAhead(walk.Head, ahead, 0.0, taxiway, probe);
        return walk with { Clears = roomAhead };
    }

    /// <summary>
    /// Whether a bare crossing of <paramref name="crossed"/> may end at the layout node <paramref name="end"/> with the nearer
    /// of the aircraft's tail and nose <paramref name="clearOfLineFt"/> past the far hold line: never where
    /// <see cref="RefusesClearOfRunwayEndAt"/> refuses the node, and only once that distance reaches
    /// <see cref="RunwayTailClearance.MarginFt"/>.
    /// </summary>
    public static bool EndClearsAt(GroundNode end, RunwayIdentifier crossed, double clearOfLineFt) =>
        !RefusesClearOfRunwayEndAt(end, crossed) && (clearOfLineFt >= RunwayTailClearance.MarginFt);

    /// <summary>
    /// Whether a bare crossing of <paramref name="crossed"/> can never end at <paramref name="end"/>: a node on a runway
    /// centreline, or another runway's bar, where the aircraft would stand nose at or past that runway's holding position and
    /// auto-cross could clear the hold short there.
    /// </summary>
    public static bool RefusesClearOfRunwayEndAt(GroundNode end, RunwayIdentifier crossed) =>
        AirportGroundLayout.HasRunwayCenterlineEdge(end) || IsOtherRunway(end, crossed);

    /// <summary>
    /// Whether another runway's bar or any runway centreline lies within half the aircraft's length straight ahead of a route
    /// end <paramref name="startOffsetFt"/> along <paramref name="edge"/> from the head of <paramref name="atFrom"/>, walking
    /// <paramref name="taxiway"/>: the nose would be past that bar.
    /// </summary>
    private static bool OtherRunwayAhead(PartialRoute atFrom, IGroundEdge edge, double startOffsetFt, string taxiway, ClearOfRunwayProbe probe)
    {
        double aheadFt = probe.LengthFt / 2.0;
        double walkedFt = -startOffsetFt;
        GroundNode node = probe.Ctx.Layout.Nodes[atFrom.HeadNodeId];
        PartialRoute current = atFrom;
        IGroundEdge? next = edge;
        for (int steps = 0; (next is not null) && (steps < MaxClearOfRunwayEdges); steps++)
        {
            GroundNode other = next.OtherNode(node);
            walkedFt += next.DistanceNm * GeoMath.FeetPerNm;
            if (walkedFt >= aheadFt)
            {
                return false;
            }

            if (IsOtherRunway(other, probe.Crossed) || AirportGroundLayout.HasRunwayCenterlineEdge(other))
            {
                return true;
            }

            current = Advance(current, node, next, other, probe.Ctx);
            next = StraightAheadEdge(other, current, taxiway);
            node = other;
        }

        return false;
    }

    /// <summary>
    /// Whether the walk past the far bar ends at <paramref name="node"/>: once it lies <paramref name="clearDistanceReached"/>,
    /// or at another runway's bar or any runway centreline, which it never carries beyond.
    /// </summary>
    private static bool StopsWalkAt(GroundNode node, RunwayIdentifier crossed, bool clearDistanceReached) =>
        clearDistanceReached || IsOtherRunway(node, crossed) || AirportGroundLayout.HasRunwayCenterlineEdge(node);

    /// <summary>The taxiway <paramref name="edge"/> carries the walk into off <paramref name="finalTaxiway"/>, or null when it stays on it.</summary>
    private static string? CarriedInto(IGroundEdge? edge, string finalTaxiway) =>
        (edge is GroundEdge { IsRunwayCenterline: false } onward) && !onward.MatchesTaxiway(finalTaxiway) ? onward.TaxiwayName : null;

    /// <summary>
    /// <paramref name="edge"/> from <paramref name="from"/> cut by a virtual node at the first point where the whole fuselage of
    /// an aircraft centred there, nosed along the edge, stands <see cref="RunwayTailClearance.MarginFt"/> past the far
    /// hold line (<see cref="ClearOfRunwayProbe.ClearOfLineFt"/>), how far along the edge that lies and the edge's bearing,
    /// when it is a straight edge with no shape points that runs toward the line's far side past that point; null otherwise
    /// (an arc is taken whole), as <see cref="CrossingRunwayPhase"/>'s tail-clearance leg cuts. While the edge runs away from
    /// the runway the tail is the fuselage's nearer end, so the cut is where the tail reaches the margin; it moves past the line
    /// at the cosine of the edge's angle off the taxiway's axis at the bar, so the cut point is closed-form.
    /// </summary>
    private static (GroundEdge Edge, GroundNode End, double AlongFt, double BearingDeg)? CutPast(
        GroundNode from,
        IGroundEdge edge,
        ClearOfRunwayProbe probe
    )
    {
        if (edge is not GroundEdge { IntermediatePoints.Count: 0 })
        {
            return null;
        }

        GroundNode to = edge.OtherNode(from);
        double bearing = GeoMath.BearingTo(from.Position, to.Position);
        double towardLine = Math.Cos((bearing - probe.AxisBearingDeg) * Math.PI / 180.0);
        if (towardLine <= 0.0)
        {
            return null;
        }

        double alongFt = (probe.LengthFt / 2.0) + ((RunwayTailClearance.MarginFt - probe.PastLineFt(from.Position)) / towardLine);
        double edgeFt = GeoMath.DistanceNm(from.Position, to.Position) * GeoMath.FeetPerNm;
        if ((alongFt <= 0.0) || (alongFt >= edgeFt))
        {
            return null;
        }

        // The projection is flat and the distances great-circle, so the closed form lands a few hundredths of a foot off. At
        // most MaxCutRefinements steps move the cut until the tail lies within ±CutRefinementSlackFt/2 of
        // MarginFt + CutRefinementSlackFt; the walk still checks the end it gets, so a cut that falls short is never taken.
        double targetFt = RunwayTailClearance.MarginFt + CutRefinementSlackFt;
        LatLon endPos = ProjectAlong(from.Position, bearing, alongFt);
        for (int step = 0; step < MaxCutRefinements; step++)
        {
            double offFt = targetFt - probe.ClearOfLineFt(endPos, bearing);
            if (Math.Abs(offFt) <= (CutRefinementSlackFt / 2.0))
            {
                break;
            }

            alongFt += offFt / towardLine;
            endPos = ProjectAlong(from.Position, bearing, alongFt);
        }

        if ((alongFt <= 0.0) || (alongFt >= edgeFt))
        {
            return null;
        }

        GroundNode end = VirtualNode.Create(endPos.Lat, endPos.Lon);
        return (VirtualNode.CreateEdge(from, end, edge.TaxiwayName), end, alongFt, bearing);
    }

    /// <summary>
    /// The straight, non-centreline edge of <paramref name="taxiway"/> leaving <paramref name="join"/> with the least turn off
    /// <paramref name="arrivalBearing"/>.
    /// </summary>
    private static IGroundEdge? StraightestEdgeOnto(GroundNode join, string taxiway, double arrivalBearing)
    {
        IGroundEdge? best = null;
        double bestTurn = double.MaxValue;
        foreach (IGroundEdge edge in join.Edges)
        {
            if ((edge is not GroundEdge) || edge.IsRunwayCenterline || !edge.MatchesTaxiway(taxiway))
            {
                continue;
            }

            double turn = RouteCostFunction.HeadingDelta(arrivalBearing, GeoMath.BearingTo(join.Position, edge.OtherNode(join).Position));
            if (turn < bestTurn)
            {
                best = edge;
                bestTurn = turn;
            }
        }

        return best;
    }

    /// <summary>
    /// The edge leaving <paramref name="node"/> straight ahead of the way <paramref name="current"/> arrives, an arc judged by
    /// its net turn. Where <paramref name="preferredTaxiway"/> simply continues (one onward edge of it), that edge at any bend
    /// up to <see cref="BendMaxTurnDeg"/>; otherwise (a fork, or a carry into another taxiway) the edge within
    /// <see cref="StraightAheadMaxTurnDeg"/>, one of <paramref name="preferredTaxiway"/> first, then the least turn. Never a
    /// runway centreline, the edge arrived on, or a node already walked.
    /// </summary>
    private static IGroundEdge? StraightAheadEdge(GroundNode node, PartialRoute current, string preferredTaxiway)
    {
        var onward = new List<(IGroundEdge Edge, double Turn, bool Preferred)>();
        foreach (IGroundEdge edge in node.Edges)
        {
            GroundNode other = edge.OtherNode(node);
            if (edge.IsRunwayCenterline || ReferenceEquals(edge, current.LastEdge) || current.VisitedNodeIds.Contains(other.Id))
            {
                continue;
            }

            double turn = RouteCostFunction.HeadingDelta(current.ArrivalBearing, HintJudgedBearing(edge.Directed(node, other)));
            onward.Add((edge, turn, edge.MatchesTaxiway(preferredTaxiway)));
        }

        if (onward.Count(o => o.Preferred) == 1)
        {
            (IGroundEdge bend, double bendTurn, _) = onward.First(o => o.Preferred);
            return bendTurn <= BendMaxTurnDeg ? bend : null;
        }

        return onward
            .Where(o => o.Turn <= StraightAheadMaxTurnDeg)
            .OrderByDescending(o => o.Preferred)
            .ThenBy(o => o.Turn)
            .Select(o => o.Edge)
            .FirstOrDefault();
    }

    /// <summary><paramref name="current"/> extended by <paramref name="edge"/> from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static PartialRoute Advance(PartialRoute current, GroundNode from, IGroundEdge edge, GroundNode to, SearchContext ctx) =>
        current with
        {
            HeadNodeId = to.Id,
            ArrivalBearing = GeometricAdmissibility.GetArrivalBearing(edge, from, to),
            LastEdge = edge,
            LastTaxiwayName = RouteCostFunction.ResolveTaxiwayName(edge, current.HeadNodeId),
            Previous = current,
            Depth = current.Depth + 1,
            AccumulatedCost = current.AccumulatedCost + RouteCostFunction.IncrementalCost(current, edge, to, ctx),
            VisitedNodeIds = current.VisitedNodeIds.Add(to.Id),
        };

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
        IReadOnlySet<string> uncleared = UnclearedMovementAreaTaxiways(ctx);

        foreach (GroundNode? cand in candidates)
        {
            (List<DirectionalEdge>? walkEdges, PartialRoute? candHead, double walkCost) = LocalSearchToJunction(head, taxiwayName, cand.Id, ctx);
            if (walkEdges is null || candHead is null)
            {
                continue;
            }

            (List<DirectionalEdge>? extEdges, string? _, PathfindingFailure? extFailure) = ExtendToDestination(candHead, destId, ctx, uncleared);
            if (extFailure is not null || extEdges is null)
            {
                continue;
            }

            // A stop reached only through the junction node (a junction-edge arc, or no edge at all) never drives
            // the named taxiway. That is fine when the stand hangs straight off the junction (SFO "TAXI B K A @F10":
            // K/A junction, then RAMP to F10), but not when the extension then runs down another lane instead
            // (issue #454, SFO "TAXI B K A T5A @D1" taxiing T5B): the clearance named the taxiway, so drive it.
            bool drivesNamed = walkEdges.Any(e => (e.Edge is not GroundArc) && e.Edge.MatchesTaxiway(taxiwayName));
            if (!drivesNamed && !ExtensionStaysOnClearedOrApron(extEdges, ctx))
            {
                ctx.DiagnosticLog?.Invoke(
                    $"[beststop] twy={taxiwayName} stop={cand.Id} skipped: the walk drives no straight {taxiwayName} edge "
                        + "and the extension leaves the cleared taxiways and apron"
                );
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
    /// Every edge of <paramref name="extEdges"/> is apron or cleared pavement: each name it carries is <c>RAMP</c>,
    /// blank, the destination stand's own name (its lead-in), or a taxiway of the clearance. A taxiway or ramp lane
    /// the clearance did not name disqualifies the extension.
    /// </summary>
    private static bool ExtensionStaysOnClearedOrApron(List<DirectionalEdge> extEdges, SearchContext ctx) =>
        extEdges.All(e =>
            EdgeNames(e.Edge)
                .All(name =>
                    IsApronName(name, ctx.Destination.ParkingName, ctx.Destination.SpotName)
                    || ctx.WaypointSequence.Contains(name, StringComparer.OrdinalIgnoreCase)
                )
        );

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
    /// Bridge the search head from a start node that is not yet on <paramref name="taxiwayName"/> (typically a parking/RAMP
    /// spot, or a runway hold-short on a crossing taxiway) onto a node carrying an edge on that taxiway. A BFS collects the
    /// taxiway-access nodes within <see cref="MaxBridgeHops"/> hops and keeps those whose bridge path is admissible: the
    /// first hop within the category limit of the start heading (<see cref="FirstHopTurnsBack"/>; a parking or helipad
    /// start is exempt, its heading pointing at the terminal or pad), and every later hop by the pathfinder's junction rule
    /// (<see cref="IsAdmissibleBridgeHop"/>). Among them it picks the lowest bridge cost plus how near its admissible
    /// on-taxiway continuation gets to <paramref name="bias"/> (the next-junction or destination position).
    /// <see cref="DeepenBridgePick"/> re-runs the search at <see cref="MaxBridgeHopsDeep"/> hops when the shallow pick is a
    /// dead end on the taxiway or every shallow candidate is rejected. When no admissible bridge exists within either reach,
    /// <see cref="PickLenientBridge"/> retries with the first hop allowed to turn back through the start heading. Returns
    /// empty edges and the unchanged head when the start is already on the taxiway, or when no bridge qualifies — the
    /// caller then proceeds and the per-segment detour and the start leg remain the fallbacks.
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

        BridgePick strict = DeepenBridgePick(
            head,
            taxiwayName,
            bias,
            ctx,
            PickBridgeCandidate(head, taxiwayName, bias, ctx, new BridgeReach(MaxBridgeHops, false))
        );
        BridgePick best = strict.AllRejected ? PickLenientBridge(head, taxiwayName, bias, ctx) : strict;

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
    /// Longest auto-routed start leg (<see cref="TryAutoRoutedStartLeg"/>), in feet of path. A sanity bound with no FAA
    /// figure behind it: the leg runs over non-movement ramp, where no clearance is needed, so nothing regulatory limits its
    /// length, but a clearance must not be met by a trek across the airport. SFO's B2 push point on M5 out to A over M2 is
    /// about 3,000 ft.
    /// </summary>
    private const double MaxStartLegFt = 4_000.0;

    /// <summary>
    /// An auto-routed start leg onto the first cleared taxiway: its edges, the search head at its end, and the one-way lane
    /// it implies (<see cref="RouteMaterialiser.NotInClearanceWarning"/>), if any.
    /// </summary>
    private sealed record StartLeg(List<DirectionalEdge> Edges, PartialRoute Head, string? ImpliedLane);

    /// <summary>A start leg to one node of the first taxiway, its path length, and whether the taxiway continues admissibly from its end.</summary>
    private sealed record StartLegCandidate(StartLeg Leg, double LengthFt, bool HasOnward);

    private static bool StartIsOnTaxiway(SearchContext ctx, string taxiwayName) =>
        ctx.Layout.Nodes.TryGetValue(ctx.StartNodeId, out GroundNode? start) && start.Edges.Any(e => e.MatchesTaxiway(taxiwayName));

    /// <summary>
    /// Whether the walk ever reached <paramref name="taxiwayName"/>: it traversed an edge of it, or the head stands on a node
    /// carrying one (a start already on the taxiway, with nothing walked). It decides where the variant step's extension
    /// applies: the numbered variant (<c>B</c> → <c>B1</c>) and the same-name <c>ExtendToNearestHoldShort</c> fallback both
    /// extend from the walked terminus, and a head that never reached the taxiway (SFO: holding short of <c>C</c> on ramp
    /// taxilane <c>T421</c>, four hops past the bridge) must not take one ahead of the taxiway's own full-length bar — its
    /// extension is deferred until the constrained fallback has had its turn. The step's <c>DestinationUnreachable</c> is a
    /// separate matter — <c>Run</c>'s connector ladder (<c>TryRunwayConnectorFallback</c>) depends on it — so it is returned
    /// whatever the head.
    /// The walked-edges check is cheap insurance over the head-node check, not a separate rule.
    /// </summary>
    private static bool WalkReachedTaxiway(SearchContext ctx, PartialRoute head, List<DirectionalEdge> walkedEdges, string taxiwayName) =>
        (walkedEdges.Any(e => e.Edge.MatchesTaxiway(taxiwayName)))
        || (ctx.Layout.Nodes.TryGetValue(head.HeadNodeId, out GroundNode? headNode) && NodeIncidentToTaxiway(headNode, taxiwayName));

    /// <summary>
    /// The start leg when <see cref="BridgeStartToTaxiway"/> reaches no node of <paramref name="taxiway"/> (SFO: pushed off
    /// B2 onto M5, <c>TAXI A</c>). Movement on a nonmovement area is the pilot's responsibility, not ATC's (7110.65 §3-7-2
    /// NOTE 2), so the leg is auto-routed, one-way lanes hard-excluded, to the nearest node carrying a straight edge of
    /// <paramref name="taxiway"/>, over non-movement pavement (ramp taxilanes, RAMP, unnamed edges) and
    /// <paramref name="taxiway"/> alone. When no such leg exists it may imply one movement-area taxiway, under
    /// <see cref="TryExtendViaOneWayLane"/>'s rule: one of the airport's one-way lanes the clearance does not name, tried
    /// with every other movement-area taxiway still excluded. A leg reaching a runway holding position or runway pavement,
    /// or longer than <see cref="MaxStartLegFt"/>, is refused. Nearest is by path length, a node the taxiway continues
    /// admissibly from ranking first. Null when no leg qualifies.
    /// </summary>
    private static StartLeg? TryAutoRoutedStartLeg(SearchContext ctx, string taxiway)
    {
        if (!ctx.Layout.Nodes.TryGetValue(ctx.StartNodeId, out GroundNode? start))
        {
            return null;
        }

        List<(GroundNode Node, double StraightFt)> targets = StartLegTargets(ctx.Layout, start, taxiway);
        if (targets.Count == 0)
        {
            ctx.DiagnosticLog?.Invoke($"[startleg] no node of {taxiway} within {MaxStartLegFt:F0} ft of #{start.Id}");
            return null;
        }

        var classification = MovementAreaClassification.For(ctx.Layout);
        var onlyFirst = new HashSet<string>([taxiway], StringComparer.OrdinalIgnoreCase);
        var blocked = ctx
            .Layout.Nodes.Values.SelectMany(n => n.Edges)
            .SelectMany(EdgeNames)
            .Where(name => IsUnclearedMovementAreaName(name, onlyFirst, classification))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        SearchContext legCtx = ctx with
        {
            WaypointSequence = [],
            AuthorizedTaxiways = null,
            ExplicitHoldShorts = new HashSet<HoldShortTarget>(),
            OneWayMode = ctx.ForbiddenOneWayMoves.Count > 0 ? OneWayMode.HardExclude : OneWayMode.Off,
        };

        StartLegCandidate? best = NearestStartLeg(legCtx, targets, taxiway, blocked, null);
        if (best is null)
        {
            var cleared = new HashSet<string>(ctx.WaypointSequence, StringComparer.OrdinalIgnoreCase);
            IReadOnlySet<string> lanes = OneWayResolver.GetOneWayLaneTaxiways(ctx.Layout);
            foreach (string lane in blocked.Where(n => lanes.Contains(n) && !cleared.Contains(n)).Order(StringComparer.OrdinalIgnoreCase))
            {
                var others = new HashSet<string>(
                    blocked.Where(n => !n.Equals(lane, StringComparison.OrdinalIgnoreCase)),
                    StringComparer.OrdinalIgnoreCase
                );
                if ((NearestStartLeg(legCtx, targets, taxiway, others, lane) is { } viaLane) && ((best is null) || IsNearerStartLeg(viaLane, best)))
                {
                    best = viaLane;
                }
            }
        }

        if (best is null)
        {
            ctx.DiagnosticLog?.Invoke($"[startleg] no leg from #{start.Id} onto {taxiway}");
            return null;
        }

        string legShape = $"{best.LengthFt:F0} ft, {best.Leg.Edges.Count} edges, lane {best.Leg.ImpliedLane ?? "none"}";
        ctx.DiagnosticLog?.Invoke($"[startleg] #{start.Id} → #{best.Leg.Head.HeadNodeId} onto {taxiway}: {legShape}");
        return best?.Leg;
    }

    /// <summary>
    /// The nodes carrying a straight edge of <paramref name="taxiway"/> within <see cref="MaxStartLegFt"/> in a straight
    /// line of <paramref name="start"/>, nearest first.
    /// </summary>
    private static List<(GroundNode Node, double StraightFt)> StartLegTargets(AirportGroundLayout layout, GroundNode start, string taxiway) =>
        [
            .. layout
                .GetNodesOnTaxiway(taxiway)
                .Where(n => (n.Id != start.Id) && n.Edges.Any(e => (e is not GroundArc) && e.MatchesTaxiway(taxiway)))
                .Select(n => (Node: n, StraightFt: GeoMath.DistanceNm(start.Position, n.Position) * GeoMath.FeetPerNm))
                .Where(t => t.StraightFt <= MaxStartLegFt)
                .OrderBy(t => t.StraightFt)
                .ThenBy(t => t.Node.Id),
        ];

    /// <summary>
    /// The nearest start leg to any of <paramref name="targets"/> with the taxiways in <paramref name="avoided"/>
    /// hard-excluded. A path is never shorter than the straight line, so the targets stop being tried once the best leg,
    /// with an onward continuation, is no longer than the next target's straight-line distance.
    /// </summary>
    private static StartLegCandidate? NearestStartLeg(
        SearchContext legCtx,
        List<(GroundNode Node, double StraightFt)> targets,
        string taxiway,
        IReadOnlySet<string> avoided,
        string? impliedLane
    )
    {
        SearchContext avoidCtx = legCtx with
        {
            AvoidedTaxiways = avoided,
            AvoidMode = avoided.Count > 0 ? AvoidTaxiwayMode.HardExclude : AvoidTaxiwayMode.Off,
        };
        StartLegCandidate? best = null;
        foreach ((GroundNode target, double straightFt) in targets)
        {
            if ((best is { HasOnward: true }) && (best.LengthFt <= straightFt))
            {
                break;
            }

            if ((StartLegTo(avoidCtx, target, taxiway, impliedLane) is { } candidate) && ((best is null) || IsNearerStartLeg(candidate, best)))
            {
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// The auto-routed leg from the start to <paramref name="target"/>; null when there is none, it reaches a runway holding
    /// position or runway pavement, or it is longer than <see cref="MaxStartLegFt"/>.
    /// </summary>
    private static StartLegCandidate? StartLegTo(SearchContext avoidCtx, GroundNode target, string taxiway, string? impliedLane)
    {
        SearchContext targetCtx = avoidCtx with { Destination = new DestinationDescriptor(target.Id, null, null, null, DestinationKind.Node) };
        (TaxiRoute? route, PathfindingFailure? _) = AutoRouter.Run(targetCtx, startOverride: null, maxExpansions: MaxDetourExpansions);
        if ((route is null) || (route.Segments.Count == 0) || (route.Segments[^1].ToNodeId != target.Id) || ReachesRunway(route, avoidCtx.Layout))
        {
            return null;
        }

        double lengthFt = route.Segments.Sum(s => s.Edge.DistanceNm) * GeoMath.FeetPerNm;
        if (lengthFt > MaxStartLegFt)
        {
            return null;
        }

        PartialRoute head = BuildHeadFromRoute(PartialRoute.StartAt(avoidCtx.StartNodeId), route);
        bool hasOnward = AdmissibleOnwardNeighbors(head, taxiway, avoidCtx).Any();
        return new StartLegCandidate(new StartLeg([.. route.Segments.Select(s => s.Edge)], head, impliedLane), lengthFt, hasOnward);
    }

    /// <summary>
    /// True when <paramref name="candidate"/> beats <paramref name="incumbent"/>: an onward continuation first, then the shorter path.
    /// </summary>
    private static bool IsNearerStartLeg(StartLegCandidate candidate, StartLegCandidate incumbent) =>
        candidate.HasOnward != incumbent.HasOnward ? candidate.HasOnward : candidate.LengthFt < incumbent.LengthFt - 1e-6;

    /// <summary>
    /// With no admissible strict bridge within either reach, re-run the search letting the first hop turn back through the
    /// start heading (a start facing away from its route, held after a pushback or rolling out on a runway, turns about),
    /// every later hop still admissible. Its pick, which may be empty, replaces the empty strict one.
    /// </summary>
    private static BridgePick PickLenientBridge(PartialRoute head, string taxiwayName, LatLon? bias, SearchContext ctx)
    {
        BridgePick lenient = DeepenBridgePick(
            head,
            taxiwayName,
            bias,
            ctx,
            PickBridgeCandidate(head, taxiwayName, bias, ctx, new BridgeReach(MaxBridgeHops, true))
        );
        if (lenient.Head is { } lenientHead)
        {
            ctx.DiagnosticLog?.Invoke(
                $"[bridge] lenient pass: start={head.HeadNodeId} → {lenientHead.HeadNodeId} onto {taxiwayName}: no strict bridge is admissible"
            );
        }

        return lenient;
    }

    /// <summary>
    /// A bridge pick: its edges and head (null when no candidate qualified), its score, and whether the taxiway continues
    /// admissibly from its end. <c>AllRejected</c> says access nodes were reachable but every one's bridge path was
    /// rejected: a hop turning through more than the category limit or onto an arc too tight to steer, or a chain that could
    /// not be walked. <c>Reach</c> is the reach it was searched with.
    /// </summary>
    private readonly record struct BridgePick(
        List<DirectionalEdge>? Edges,
        PartialRoute? Head,
        double Score,
        bool HasOnward,
        bool AllRejected,
        BridgeReach Reach
    );

    /// <summary>
    /// How far a bridge search looks, in BFS hops, and whether it may keep a candidate whose first hop turns through more than
    /// the category limit off the start heading (<see cref="FirstHopTurnsBack"/>).
    /// </summary>
    private readonly record struct BridgeReach(int MaxHops, bool AllowsFirstHopTurnBack);

    /// <summary>
    /// Re-run the bridge search at <see cref="MaxBridgeHopsDeep"/> when the shallow pick is unusable, keeping the deeper
    /// pick when it is better. Two cases look deeper. Every shallow access node is a dead end (the shallow pick still
    /// carries <see cref="BridgeNoOnwardPenaltyNm"/>): the real entry may lie a hop or two further along a gate's lead-out
    /// lane, and the deeper pick wins only with a continuation. Every shallow access node's bridge path is rejected (SFO
    /// spot 10, whose only A entry within the shallow reach is the B5–T9 fillet behind the nose): any admissible deeper pick
    /// wins, a dead end included, and with none the shallow pick stands.
    /// </summary>
    private static BridgePick DeepenBridgePick(PartialRoute head, string taxiwayName, LatLon? bias, SearchContext ctx, BridgePick shallow)
    {
        bool deadEnd = (shallow.Edges is not null) && !shallow.HasOnward;
        if (!deadEnd && !shallow.AllRejected)
        {
            return shallow;
        }

        BridgePick deeper = PickBridgeCandidate(head, taxiwayName, bias, ctx, shallow.Reach with { MaxHops = MaxBridgeHopsDeep });
        if ((deeper.Edges is null) || (deadEnd && !deeper.HasOnward))
        {
            return shallow;
        }

        string reason = deadEnd
            ? $"shallow pick {shallow.Head!.HeadNodeId} has no admissible continuation on {taxiwayName}"
            : $"every shallow bridge onto {taxiwayName} is inadmissible";
        string pass = shallow.Reach.AllowsFirstHopTurnBack ? "lenient" : "strict";
        ctx.DiagnosticLog?.Invoke($"[bridge] {pass} pass: deepened to {MaxBridgeHopsDeep} hops: {reason}");
        return deeper;
    }

    /// <summary>
    /// Collect the access nodes within <paramref name="reach"/> of the head and pick the best one whose bridge path is
    /// admissible (<see cref="IsAdmissibleBridgeHop"/>; its first hop by <see cref="FirstHopTurnsBack"/> unless the reach
    /// allows a turn back). <c>Edges</c> is null when no admissible access node is reachable
    /// within the cap, and <c>AllRejected</c> says access nodes were reachable but none admissibly; <c>HasOnward</c> reports
    /// whether the chosen candidate has an admissible onward edge on the taxiway from its bridged arrival bearing.
    /// </summary>
    private static BridgePick PickBridgeCandidate(PartialRoute head, string taxiwayName, LatLon? bias, SearchContext ctx, BridgeReach reach)
    {
        (List<int>? candidates, Dictionary<int, (int ParentId, IGroundEdge Edge)>? cameFrom) = CollectBridgeCandidates(
            head.HeadNodeId,
            taxiwayName,
            ctx,
            reach.MaxHops
        );

        List<DirectionalEdge>? bestEdges = null;
        PartialRoute? bestHead = null;
        double bestScore = double.MaxValue;
        double bestCost = double.MaxValue;
        bool bestHasOnward = false;
        int rejected = 0;

        foreach (int candidateId in candidates)
        {
            (List<DirectionalEdge>? candEdges, PartialRoute? candHead) = BuildBridgePath(head, candidateId, cameFrom, reach, ctx);
            if (IsRejectedBridge(head, candEdges, reach, ctx))
            {
                rejected++;
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

        return new BridgePick(bestEdges, bestHead, bestScore, bestHasOnward, (bestEdges is null) && (rejected > 0), reach);
    }

    /// <summary>
    /// Whether a bridge candidate is unusable: <see cref="BuildBridgePath"/> found no admissible path to it, or its first hop
    /// turns back through the start heading (<see cref="FirstHopTurnsBack"/>) on a reach that does not allow that, which
    /// logs a <c>[bridge] rejected</c> line naming the hop.
    /// </summary>
    private static bool IsRejectedBridge(PartialRoute head, List<DirectionalEdge> candEdges, BridgeReach reach, SearchContext ctx)
    {
        if (candEdges.Count == 0)
        {
            return true;
        }

        if (reach.AllowsFirstHopTurnBack || !FirstHopTurnsBack(candEdges[0], ctx))
        {
            return false;
        }

        DirectionalEdge first = candEdges[0];
        ctx.DiagnosticLog?.Invoke(
            $"[bridge] rejected {head.HeadNodeId} → {candEdges[^1].ToNodeId}: first hop {first.FromNodeId} → {first.ToNodeId} turns back"
        );
        return true;
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
    /// <paramref name="cameFrom"/>. Returns empty edges when the chain cannot be walked or a hop on it is inadmissible
    /// (<see cref="IsAdmissibleBridgeHop"/>); the rejection line names the pass by <paramref name="reach"/>.
    /// </summary>
    private static (List<DirectionalEdge> Edges, PartialRoute Head) BuildBridgePath(
        PartialRoute head,
        int targetId,
        Dictionary<int, (int ParentId, IGroundEdge Edge)> cameFrom,
        BridgeReach reach,
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

            if (!IsAdmissibleBridgeHop(current, edge, toNode, ctx))
            {
                string pass = reach.AllowsFirstHopTurnBack ? "lenient" : "strict";
                ctx.DiagnosticLog?.Invoke(
                    $"[bridge] {pass} pass: rejected {head.HeadNodeId} → {targetId}: inadmissible turn onto {fromNode.Id} → {toNode.Id}"
                );
                return ([], head);
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
    /// Whether the bridge may take <paramref name="edge"/> from <paramref name="fromNode"/> to <paramref name="toNode"/>
    /// after its first hop: the pathfinder's own junction rule (<see cref="GeometricAdmissibility.IsAdmissible"/>). The first
    /// hop, which has no arrival bearing, is judged against the start heading by <see cref="FirstHopTurnsBack"/>.
    /// </summary>
    private static bool IsAdmissibleBridgeHop(PartialRoute current, IGroundEdge edge, GroundNode toNode, SearchContext ctx) =>
        (current.LastEdge is null) || GeometricAdmissibility.IsAdmissible(current, edge, toNode, ctx.Category);

    /// <summary>
    /// Whether a bridge's <paramref name="first"/> edge turns off <see cref="SearchContext.StartHeadingTrue"/> through more
    /// than the category limit, or is an arc too tight to steer (<see cref="GeometricAdmissibility.AdmitsTurn"/>). False with
    /// no start heading, and from a parking or helipad node, whose heading points at the terminal or pad rather than along
    /// the way out.
    /// </summary>
    private static bool FirstHopTurnsBack(DirectionalEdge first, SearchContext ctx) =>
        (ctx.StartHeadingTrue is { } startHeading)
        && (first.FromNode.Type is not (GroundNodeType.Parking or GroundNodeType.Helipad))
        && !GeometricAdmissibility.AdmitsTurn(startHeading, first.Edge, first.DepartureBearing, ctx.Category);

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
            // The two taxiways may meet only across a runway, one joining it a short way along the
            // centreline from the other and from its far side (OAK D/F across 15/33): the clearance
            // then means "cross the runway onto toTaxiway", which beats any connector detour.
            if (TryRouteAcrossOffsetRunway(head, fromTaxiway, toTaxiway, ctx) is { } acrossRunway)
            {
                return acrossRunway;
            }

            // No direct junction between the two cleared taxiways. Inside a look-ahead probe the
            // detour is suppressed (a continuation needing one is a negative signal). At the top
            // level we detour and — because zero junction candidates verifies there is genuinely
            // no edge or arc joining fromTaxiway and toTaxiway — record the inserted connector so
            // the controller is notified of the mandatory insertion rather than warned about it.
            if (!enableLookahead)
            {
                return (null, null, DetourSuppressedFailure(fromTaxiway, toTaxiway, head));
            }

            (List<DirectionalEdge>? Edges, PartialRoute? Head, PathfindingFailure? Failure) detour = TryDetour(
                head,
                fromTaxiway,
                toTaxiway,
                index,
                ctx
            );
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
                ? TryDetour(head, fromTaxiway, toTaxiway, index, ctx)
                : (null, null, DetourSuppressedFailure(fromTaxiway, toTaxiway, head));
        }

        ctx.DiagnosticLog?.Invoke(
            $"[committed] twy={fromTaxiway}→{toTaxiway} via={bestHead!.HeadNodeId} " + $"edges={bestEdges.Count} cost={bestCost:F3}"
        );

        return (bestEdges, bestHead, null);
    }

    /// <summary>
    /// Longest distance along a runway's centreline between the node where one cleared taxiway joins the runway and the
    /// node where the next cleared taxiway leaves it on the far side, for the pair to read as a crossing of that runway
    /// (OAK D/F across 15/33: 100 ft). A sim convention of about twice a runway's width; no FAA figure exists. A longer
    /// offset is taxiing along the runway, which the controller has to clear by naming the runway in the route.
    /// <see cref="TaxiRouteFormatter"/> reads it too: a runway leg longer than this is never read as a crossing.
    /// </summary>
    internal const double MaxOffsetRunwayCrossingFt = 300.0;

    /// <summary>
    /// A runway that two taxiways with no junction of their own both join, from opposite sides, at centreline nodes
    /// <see cref="OffsetFt"/> apart along it, and the leg straight over it (<see cref="StraightOver"/>).
    /// </summary>
    private sealed record OffsetRunwayCrossing(string RunwayCenterline, RunwayJoin From, RunwayJoin To, double OffsetFt)
    {
        /// <summary>The straight-over geometry, measured once for the winning pair (<see cref="BuildStraightOver"/>).</summary>
        public StraightOver StraightOver { get; init; } = StraightOver.Refused("not measured");

        /// <summary>The node the crossing lands on: the straight-over leg's, or toTaxiway's own join when no leg was built.</summary>
        public GroundNode Landing => StraightOver.Leg?.Landing ?? To.Via;
    }

    /// <summary>
    /// Where a taxiway joins a runway's centreline: <see cref="Join"/> is the centreline node and <see cref="Via"/> the
    /// taxiway's own node there. For a direct join they are one node; for a stitched join the taxiway ends on the runway
    /// pavement and <see cref="Link"/>, a <c>RWY…:link</c> edge, leads from <see cref="Via"/> to <see cref="Join"/> (CMH E4
    /// and F3 over 10L/28R).
    /// </summary>
    private sealed record RunwayJoin(GroundNode Via, GroundNode Join, IGroundEdge? Link)
    {
        public static RunwayJoin Direct(GroundNode join) => new(join, join, null);
    }

    /// <summary>
    /// <see cref="FindOffsetRunwayCrossing"/>'s answer per search and (fromTaxiway, toTaxiway) pair, null answers included:
    /// every look-ahead probe of a zero-junction transition asks it again.
    /// </summary>
    private static readonly ConditionalWeakTable<
        SearchContext,
        Dictionary<(string From, string To), OffsetRunwayCrossing?>
    > OffsetRunwayCrossingCache = [];

    /// <summary>
    /// Resolves <paramref name="fromTaxiway"/> → <paramref name="toTaxiway"/> across a runway when the two taxiways meet
    /// only across it (<see cref="FindOffsetRunwayCrossing"/>), straight over it (<see cref="RouteStraightOver"/>) when that leg
    /// can be built. Otherwise pinned to the join pair that check found: a local search along fromTaxiway to its join, then along the runway's centreline to toTaxiway's join. A stitched join adds its
    /// <c>RWY…:link</c> hop between the taxiway's own node and the centreline, so the route ends on toTaxiway's node, where
    /// the next transition's search starts on toTaxiway. Neither leg can detour, and neither depends on look-ahead, so a
    /// probe resolves the crossing exactly as the top level does. The runway never enters the clearance's waypoint sequence,
    /// so the route materialiser does not read it as a runway cleared to taxi along: the crossing keeps its hold short at
    /// fromTaxiway's bar. Null when there is no such runway, a leg fails, or the centreline run exceeds
    /// <see cref="MaxOffsetRunwayCrossingFt"/>, leaving the caller's no-junction handling unchanged.
    /// </summary>
    private static (List<DirectionalEdge>? Edges, PartialRoute? Head, PathfindingFailure? Failure)? TryRouteAcrossOffsetRunway(
        PartialRoute head,
        string fromTaxiway,
        string toTaxiway,
        SearchContext ctx
    )
    {
        if (IsRunwayWaypoint(fromTaxiway) || IsRunwayWaypoint(toTaxiway))
        {
            return null;
        }

        OffsetRunwayCrossing? crossing = CachedOffsetRunwayCrossing(ctx, fromTaxiway, toTaxiway);
        if (crossing is null)
        {
            return null;
        }

        string runway = crossing.RunwayCenterline;
        RunwayJoin from = crossing.From;
        RunwayJoin to = crossing.To;
        ctx.DiagnosticLog?.Invoke(
            $"[runway-crossing] {fromTaxiway}→{toTaxiway} across {runway} #{from.Join.Id}→#{to.Join.Id} offset={crossing.OffsetFt:F0}ft"
        );

        if (RouteStraightOver(head, crossing, (fromTaxiway, toTaxiway), ctx) is { } straightOver)
        {
            return straightOver;
        }

        (List<DirectionalEdge>? ontoEdges, PartialRoute? ontoHead, _) = LocalSearchToJunction(head, fromTaxiway, from.Via.Id, ctx);
        if ((ontoEdges is null) || (ontoHead is null))
        {
            ctx.DiagnosticLog?.Invoke($"[runway-crossing] {fromTaxiway}→{runway} failed: no route along {fromTaxiway} to #{from.Via.Id}");
            return null;
        }

        (List<DirectionalEdge> linkOnto, PartialRoute atJoin) = AcrossLink(ontoHead, from.Via, from.Join, from.Link, ctx);
        PartialRoute onRunway = atJoin with { VisitedNodeIds = VisitedNodeSet.Single(atJoin.HeadNodeId) };
        (List<DirectionalEdge>? offEdges, PartialRoute? offHead, _) = LocalSearchToJunction(onRunway, runway, to.Join.Id, ctx);
        if ((offEdges is null) || (offHead is null))
        {
            ctx.DiagnosticLog?.Invoke($"[runway-crossing] {runway}→{toTaxiway} failed: no route along {runway} to #{to.Join.Id}");
            return null;
        }

        double centerlineFt = CenterlineLengthFt(offEdges.Select(e => e.Edge));
        if (centerlineFt > MaxOffsetRunwayCrossingFt)
        {
            ctx.DiagnosticLog?.Invoke($"[runway-crossing] {runway}→{toTaxiway} failed: centreline run {centerlineFt:F0}ft");
            return null;
        }

        (List<DirectionalEdge> linkOff, PartialRoute offTaxiwayHead) = AcrossLink(offHead, to.Join, to.Via, to.Link, ctx);

        if (TurnOntoBeyondLimit(crossing, (ontoHead.ArrivalBearing, offHead.ArrivalBearing), (fromTaxiway, toTaxiway), ctx) is { } turnDeg)
        {
            ctx.DiagnosticLog?.Invoke(
                $"[runway-crossing] {runway}→{toTaxiway} refused: the {turnDeg:F0}° turn onto {toTaxiway} at #{to.Via.Id} "
                    + $"exceeds the {ctx.Category} limit"
            );
            return (null, null, CannotMakeTurnFailure(fromTaxiway, toTaxiway));
        }

        return ([.. ontoEdges, .. linkOnto, .. offEdges, .. linkOff], offTaxiwayHead, null);
    }

    /// <summary>
    /// The crossing as its straight-over leg (<see cref="StraightOverLeg"/>): along fromTaxiway to the leg's entry node, then the
    /// leg's virtual edges onto the real node toTaxiway's route goes on from, no centreline and no link walked. A
    /// <see cref="StraightOverKind.Continue"/> corner past the category's limit is refused
    /// (<see cref="FailureKind.CannotMakeTurn"/>), and lines meeting past the far bar are refused for every category
    /// (<see cref="FailureKind.NoRoomToCrossStraight"/>). Null, leaving today's route, when no leg was built, the head already
    /// stands past the entry, the search to the entry fails, or a chord's corner is past the limit.
    /// </summary>
    private static (List<DirectionalEdge>? Edges, PartialRoute? Head, PathfindingFailure? Failure)? RouteStraightOver(
        PartialRoute head,
        OffsetRunwayCrossing crossing,
        (string From, string To) taxiways,
        SearchContext ctx
    )
    {
        string pair = $"{taxiways.From}→{taxiways.To}";
        if (crossing.StraightOver.NoRoom)
        {
            ctx.DiagnosticLog?.Invoke($"[runway-crossing] {pair} refused: {crossing.StraightOver.NotBuilt}");
            return (null, null, NoRoomToCrossStraightFailure(taxiways.From, taxiways.To));
        }

        if (crossing.StraightOver.Leg is not { } leg)
        {
            ctx.DiagnosticLog?.Invoke($"[runway-crossing] {pair} straight-over not built: {crossing.StraightOver.NotBuilt}");
            return null;
        }

        if (leg.PastEntryNodeIds.Contains(head.HeadNodeId))
        {
            ctx.DiagnosticLog?.Invoke($"[runway-crossing] {pair} straight-over not built: head #{head.HeadNodeId} is past the entry #{leg.Entry.Id}");
            return null;
        }

        (List<DirectionalEdge>? ontoEdges, PartialRoute? ontoHead, _) = LocalSearchToJunction(head, taxiways.From, leg.Entry.Id, ctx);
        if ((ontoEdges is null) || (ontoHead is null))
        {
            ctx.DiagnosticLog?.Invoke($"[runway-crossing] {pair} straight-over not built: no route along {taxiways.From} to #{leg.Entry.Id}");
            return null;
        }

        string corners = string.Join(" then ", leg.CornersDeg.Select(deg => FormattableString.Invariant($"{deg:F1}°")));
        ctx.DiagnosticLog?.Invoke($"[runway-crossing] {pair} straight-over {leg.Kind} #{leg.Entry.Id}→#{leg.Landing.Id} corners {corners}");
        if (leg.MaxCornerDeg > CategoryLimits.MaxHeadingChangeDeg(ctx.Category))
        {
            return CornerBeyondLimit(leg, pair, taxiways, ctx);
        }

        return ([.. ontoEdges, .. leg.Edges], LandedOn(leg, ontoHead), null);
    }

    /// <summary>What a leg's corner past the category's limit comes to: a refusal for a leg with one turn, today's route for a chord.</summary>
    private static (List<DirectionalEdge>? Edges, PartialRoute? Head, PathfindingFailure? Failure)? CornerBeyondLimit(
        StraightOverLeg leg,
        string pair,
        (string From, string To) taxiways,
        SearchContext ctx
    )
    {
        if (leg.Kind == StraightOverKind.Chord)
        {
            ctx.DiagnosticLog?.Invoke($"[runway-crossing] {pair} straight-over not built: the chord's corner exceeds the {ctx.Category} limit");
            return null;
        }

        ctx.DiagnosticLog?.Invoke(
            $"[runway-crossing] {pair} refused: the {leg.MaxCornerDeg:F0}° turn onto {taxiways.To} exceeds the {ctx.Category} limit"
        );
        return (null, null, CannotMakeTurnFailure(taxiways.From, taxiways.To));
    }

    /// <summary>
    /// <paramref name="atEntry"/> extended over <paramref name="leg"/>, arriving on toTaxiway's line, with the toTaxiway nodes the
    /// leg passes marked visited: the walk on from the landing never turns back onto them.
    /// </summary>
    private static PartialRoute LandedOn(StraightOverLeg leg, PartialRoute atEntry)
    {
        PartialRoute current = atEntry;
        foreach (DirectionalEdge edge in leg.Edges)
        {
            current = Advance(current, edge, edge.TaxiwayName);
        }

        VisitedNodeSet visited = current.VisitedNodeIds;
        foreach (int skipped in leg.SkippedNodeIds)
        {
            visited = visited.Add(skipped);
        }

        return current with
        {
            ArrivalBearing = leg.LandingBearingDeg,
            VisitedNodeIds = visited,
        };
    }

    /// <summary>The refusal of a crossing whose turn onto <paramref name="toTaxiway"/> is past the aircraft category's limit.</summary>
    private static PathfindingFailure CannotMakeTurnFailure(string fromTaxiway, string toTaxiway) =>
        new(FailureKind.CannotMakeTurn, CannotMakeTurnReason(fromTaxiway, toTaxiway), toTaxiway, $"{fromTaxiway} → {toTaxiway}", null)
        {
            UnconnectedFrom = fromTaxiway,
            UnconnectedTo = toTaxiway,
        };

    /// <summary>The refusal of a crossing whose lines meet past <paramref name="toTaxiway"/>'s bar.</summary>
    private static PathfindingFailure NoRoomToCrossStraightFailure(string fromTaxiway, string toTaxiway) =>
        new(FailureKind.NoRoomToCrossStraight, NoRoomToCrossStraightReason(toTaxiway), toTaxiway, $"{fromTaxiway} → {toTaxiway}", null)
        {
            UnconnectedFrom = fromTaxiway,
            UnconnectedTo = toTaxiway,
        };

    /// <summary>
    /// The hop along a stitched join's <c>RWY…:link</c> edge <paramref name="link"/> from <paramref name="from"/> to
    /// <paramref name="to"/>, and the head after it; no edge and <paramref name="head"/> itself for a direct join.
    /// </summary>
    private static (List<DirectionalEdge> Edges, PartialRoute Head) AcrossLink(
        PartialRoute head,
        GroundNode from,
        GroundNode to,
        IGroundEdge? link,
        SearchContext ctx
    ) => link is null ? ([], head) : ([link.Directed(from, to)], Advance(head, from, link, to, ctx));

    /// <summary>
    /// The crossing's turn onto the second taxiway when it exceeds the category's limit
    /// (<see cref="CategoryLimits.MaxHeadingChangeDeg"/>), else null; the turn measured is logged either way. A direct pair
    /// turns from the bearing the centreline run arrives on (<paramref name="arrivals"/>' <c>OnRunway</c>). A stitched pair
    /// drives straight from the first taxiway onto the second, the centreline run only standing in for that, so it turns from
    /// the first taxiway's heading at its own end (<c>OnFromTaxiway</c>) onto the second's at its own (CMH E4 onto F3, 3°).
    /// </summary>
    private static double? TurnOntoBeyondLimit(
        OffsetRunwayCrossing crossing,
        (double OnFromTaxiway, double OnRunway) arrivals,
        (string From, string To) taxiways,
        SearchContext ctx
    )
    {
        bool stitched = (crossing.From.Link is not null) || (crossing.To.Link is not null);
        double arrivalBearing = stitched ? arrivals.OnFromTaxiway : arrivals.OnRunway;
        if (TurnOnto(arrivalBearing, crossing.To.Via, taxiways.To) is not { } turnDeg)
        {
            return null;
        }

        ctx.DiagnosticLog?.Invoke(
            FormattableString.Invariant(
                $"[runway-crossing] {taxiways.From}→{taxiways.To} turn {turnDeg:F1}° onto {taxiways.To} at #{crossing.To.Via.Id}"
            )
        );
        return turnDeg > CategoryLimits.MaxHeadingChangeDeg(ctx.Category) ? turnDeg : null;
    }

    /// <summary>
    /// The turn, in degrees, from <paramref name="arrivalBearing"/> onto the straightest edge of <paramref name="toTaxiway"/>
    /// leaving <paramref name="at"/> (KCOS A2→C2: 150° off 17R/35L's 180° onto C2's 030°, past a B738's
    /// <see cref="CategoryLimits.MaxHeadingChangeDeg"/>); null when no straight edge of the taxiway leaves the node.
    /// </summary>
    private static double? TurnOnto(double arrivalBearing, GroundNode at, string toTaxiway)
    {
        double? gentlest = null;
        foreach (IGroundEdge edge in at.Edges)
        {
            if ((edge is not GroundEdge) || edge.IsRunwayCenterline || !edge.MatchesTaxiway(toTaxiway))
            {
                continue;
            }

            double turn = RouteCostFunction.HeadingDelta(arrivalBearing, GeoMath.BearingTo(at.Position, edge.OtherNode(at).Position));
            gentlest = (gentlest is { } best) && (best <= turn) ? best : turn;
        }

        return gentlest;
    }

    /// <summary>
    /// Feet of runway centreline among <paramref name="edges"/>; fillet arcs and taxiway edges do not count. The one measure
    /// <see cref="MaxOffsetRunwayCrossingFt"/> bounds, here and in <see cref="TaxiRouteFormatter"/>.
    /// </summary>
    public static double CenterlineLengthFt(IEnumerable<IGroundEdge> edges)
    {
        double nm = 0.0;
        foreach (IGroundEdge edge in edges)
        {
            if (edge.IsRunwayCenterline)
            {
                nm += edge.DistanceNm;
            }
        }

        return nm * GeoMath.FeetPerNm;
    }

    /// <summary>The straight-over geometry of the crossing from <paramref name="fromTaxiway"/> onto <paramref name="toTaxiway"/>, null when the pair is no crossing.</summary>
    internal static StraightOver? FindStraightOver(
        AirportGroundLayout layout,
        string fromTaxiway,
        string toTaxiway,
        IReadOnlyList<CrossingPair> declaredPairs
    ) => FindOffsetRunwayCrossing(layout, fromTaxiway, toTaxiway, declaredPairs)?.StraightOver;

    private static OffsetRunwayCrossing? CachedOffsetRunwayCrossing(SearchContext ctx, string fromTaxiway, string toTaxiway)
    {
        Dictionary<(string From, string To), OffsetRunwayCrossing?> answers = OffsetRunwayCrossingCache.GetOrCreateValue(ctx);
        if (!answers.TryGetValue((fromTaxiway, toTaxiway), out OffsetRunwayCrossing? crossing))
        {
            crossing = FindOffsetRunwayCrossing(ctx.Layout, fromTaxiway, toTaxiway, ctx.CrossingPairs);
            answers[(fromTaxiway, toTaxiway)] = crossing;
        }

        return crossing;
    }

    /// <summary>
    /// The runway <paramref name="fromTaxiway"/> and <paramref name="toTaxiway"/> meet across: both join its centreline,
    /// from opposite sides, at nodes at most <see cref="MaxOffsetRunwayCrossingFt"/> apart. The closest such pair wins.
    /// Null when the two join no runway that way; a same-side pair is taxiing along the runway, not crossing it, and a
    /// pair where either join already has a taxiway continuing straight across the runway has a square crossing to use.
    /// A pair the airport's sidecar declares a crossing of that runway (<paramref name="declaredPairs"/>) is exempt from the
    /// oblique-join rule only (<see cref="MeetsRunwayObliquely"/>); every other rule still holds.
    /// </summary>
    private static OffsetRunwayCrossing? FindOffsetRunwayCrossing(
        AirportGroundLayout layout,
        string fromTaxiway,
        string toTaxiway,
        IReadOnlyList<CrossingPair> declaredPairs
    )
    {
        OffsetRunwayCrossing? best = null;
        foreach (string runway in RunwayCenterlinesJoinedBy(layout, fromTaxiway))
        {
            bool declared = declaredPairs.Any(p => p.Matches(fromTaxiway, toTaxiway, runway));
            List<RunwayJoin> toJoins =
            [
                .. FindJunctionCandidates(layout, runway, toTaxiway).Select(RunwayJoin.Direct),
                .. StitchedRunwayJoins(layout, toTaxiway, runway),
            ];
            List<RunwayJoin> fromJoins =
            [
                .. FindJunctionCandidates(layout, fromTaxiway, runway).Select(RunwayJoin.Direct),
                .. StitchedRunwayJoins(layout, fromTaxiway, runway),
            ];
            foreach (RunwayJoin fromJoin in fromJoins)
            {
                OffsetRunwayCrossing? pair = ClosestOppositeSideJoin(fromJoin, fromTaxiway, toJoins, toTaxiway, (runway, declared));
                if ((pair is not null) && ((best is null) || (pair.OffsetFt < best.OffsetFt)))
                {
                    best = pair;
                }
            }
        }

        return best is null ? null : best with { StraightOver = BuildStraightOver(layout, best, fromTaxiway, toTaxiway) };
    }

    /// <summary>
    /// The straight-over geometry of <paramref name="crossing"/>, from the line each taxiway is flown on at its own join and the
    /// walk from there to its bar of the runway; refused, leaving today's route, when a line or a bar is missing from the layout.
    /// </summary>
    private static StraightOver BuildStraightOver(AirportGroundLayout layout, OffsetRunwayCrossing crossing, string fromTaxiway, string toTaxiway)
    {
        var crossed = RunwayIdentifier.Parse(RunwayWaypointDisplay(crossing.RunwayCenterline));
        if (layout.FindRunway(crossed.End1) is not { } runway)
        {
            return StraightOver.Refused($"no runway {crossed} in the layout");
        }

        double towardFrom = GeoMath.BearingTo(crossing.To.Via.Position, crossing.From.Via.Position);
        if (LineToBar(crossing.From.Via, fromTaxiway, towardFrom, crossed, layout) is not { } from)
        {
            return StraightOver.Refused($"no straight line to a {crossed} bar on {fromTaxiway}");
        }

        if (LineToBar(crossing.To.Via, toTaxiway, from.InboundBearingDeg, crossed, layout) is not { } to)
        {
            return StraightOver.Refused($"no straight line to a {crossed} bar on {toTaxiway}");
        }

        return StraightOverGeometry.Build(from, to, RunwayCrossingDetector.BuildRunwayRectangle(runway));
    }

    /// <summary>
    /// <paramref name="taxiway"/>'s line at its join <paramref name="via"/>, read off its straight edge leaving there nearest
    /// <paramref name="headingDeg"/>, with the walk from there to its bar of <paramref name="crossed"/>; null without either.
    /// </summary>
    private static TaxiwayLine? LineToBar(GroundNode via, string taxiway, double headingDeg, RunwayIdentifier crossed, AirportGroundLayout layout)
    {
        if (StraightestEdgeOnto(via, taxiway, headingDeg) is not GroundEdge { IntermediatePoints.Count: 0 } edge)
        {
            return null;
        }

        double outwardDeg = GeoMath.BearingTo(via.Position, edge.OtherNode(via).Position);
        PartialRoute atVia = PartialRoute.StartAt(via.Id) with { ArrivalBearing = outwardDeg };
        return WalkToBar(atVia, taxiway, crossed, layout) is { Count: > 0 } walk ? new TaxiwayLine(taxiway, via, outwardDeg, walk) : null;
    }

    /// <summary>
    /// The runway centreline names a node on <paramref name="taxiway"/> joins, in first-seen order: directly, by a straight
    /// centreline edge of its own, or stitched, by a <c>RWY…:link</c> edge whose far node is on that runway's centreline
    /// (<see cref="StitchedRunwayJoins"/>).
    /// </summary>
    private static List<string> RunwayCenterlinesJoinedBy(AirportGroundLayout layout, string taxiway)
    {
        var runways = new List<string>();
        foreach (GroundNode node in layout.GetNodesOnTaxiway(taxiway))
        {
            foreach (IGroundEdge edge in node.Edges)
            {
                string? runway = edge switch
                {
                    GroundEdge { IsRunwayCenterline: true } => edge.TaxiwayName,
                    GroundEdge { IsRunwayCrossingLink: true } when LinkedCenterlineNode(node, edge) is not null =>
                        TaxiRouteFormatter.RunwayCenterlineName(edge.TaxiwayName),
                    _ => null,
                };
                if ((runway is not null) && !runways.Contains(runway, StringComparer.OrdinalIgnoreCase))
                {
                    runways.Add(runway);
                }
            }
        }

        return runways;
    }

    /// <summary>
    /// The stitched joins of <paramref name="taxiway"/> onto <paramref name="runway"/>: each node on the taxiway with a
    /// <c>RWY…:link</c> edge of that runway whose far node lies on its centreline, paired with that centreline node.
    /// </summary>
    private static List<RunwayJoin> StitchedRunwayJoins(AirportGroundLayout layout, string taxiway, string runway)
    {
        var joins = new List<RunwayJoin>();
        foreach (GroundNode node in layout.GetNodesOnTaxiway(taxiway))
        {
            foreach (IGroundEdge edge in node.Edges)
            {
                if (
                    (edge is GroundEdge { IsRunwayCrossingLink: true })
                    && string.Equals(TaxiRouteFormatter.RunwayCenterlineName(edge.TaxiwayName), runway, StringComparison.OrdinalIgnoreCase)
                    && (LinkedCenterlineNode(node, edge) is { } centreline)
                )
                {
                    joins.Add(new RunwayJoin(node, centreline, edge));
                }
            }
        }

        return joins;
    }

    /// <summary>
    /// The far node of the <c>RWY…:link</c> edge <paramref name="link"/> from <paramref name="node"/> when it lies on the
    /// link's runway centreline; null otherwise.
    /// </summary>
    private static GroundNode? LinkedCenterlineNode(GroundNode node, IGroundEdge link)
    {
        GroundNode far = link.OtherNode(node);
        string runway = TaxiRouteFormatter.RunwayCenterlineName(link.TaxiwayName);
        return far.Edges.Any(e => (e is GroundEdge { IsRunwayCenterline: true }) && e.MatchesTaxiway(runway)) ? far : null;
    }

    /// <summary>
    /// The closest join of <paramref name="toJoins"/> that pairs with <paramref name="fromJoin"/> as a crossing of the runway.
    /// Distance is measured between the two centreline nodes; side and obliqueness on each taxiway's own
    /// edges at its own node, never on a stitched join's link.
    /// </summary>
    private static OffsetRunwayCrossing? ClosestOppositeSideJoin(
        RunwayJoin fromJoin,
        string fromTaxiway,
        List<RunwayJoin> toJoins,
        string toTaxiway,
        (string Centerline, bool DeclaredPair) runway
    )
    {
        if (RunwayBearingAt(fromJoin.Join, runway.Centerline) is not { } runwayBearing)
        {
            return null;
        }

        int fromSide = SideOfRunway(fromJoin.Via, fromTaxiway, runwayBearing);
        if (
            (fromSide == 0)
            || HasStraightTaxiwayToward(fromJoin, -fromSide, runwayBearing)
            || (!runway.DeclaredPair && MeetsRunwayObliquely(fromJoin.Via, fromTaxiway, runwayBearing))
        )
        {
            return null;
        }

        OffsetRunwayCrossing? best = null;
        foreach (RunwayJoin toJoin in toJoins)
        {
            double offsetFt = GeoMath.DistanceNm(fromJoin.Join.Position, toJoin.Join.Position) * GeoMath.FeetPerNm;
            if ((toJoin.Join.Id == fromJoin.Join.Id) || (offsetFt > MaxOffsetRunwayCrossingFt) || ((best is not null) && (offsetFt >= best.OffsetFt)))
            {
                continue;
            }

            int toSide = SideOfRunway(toJoin.Via, toTaxiway, runwayBearing);
            if (
                (toSide == -fromSide)
                && !HasStraightTaxiwayToward(toJoin, fromSide, runwayBearing)
                && (runway.DeclaredPair || !MeetsRunwayObliquely(toJoin.Via, toTaxiway, runwayBearing))
            )
            {
                best = new OffsetRunwayCrossing(runway.Centerline, fromJoin, toJoin, offsetFt);
            }
        }

        return best;
    }

    /// <summary>
    /// The bearing of <paramref name="runway"/>'s centreline at <paramref name="join"/>, or null when no straight
    /// centreline edge of it touches the node.
    /// </summary>
    private static double? RunwayBearingAt(GroundNode join, string runway)
    {
        foreach (IGroundEdge edge in join.Edges)
        {
            if ((edge is GroundEdge { IsRunwayCenterline: true }) && edge.MatchesTaxiway(runway))
            {
                return GeoMath.BearingTo(join.Position, edge.OtherNode(join).Position);
            }
        }

        return null;
    }

    /// <summary>Sine of 5°: a taxiway edge within 5° of the runway line runs along it and is on neither side.</summary>
    private static readonly double AlongRunwayLineSin = Math.Sin(5 * Math.PI / 180);

    /// <summary>
    /// Which side of the line through <paramref name="join"/> on <paramref name="runwayBearing"/> the edges of
    /// <paramref name="taxiway"/> leave it toward: +1 right, -1 left, 0 when they leave toward both sides (the taxiway
    /// runs through the runway) or run along the line.
    /// </summary>
    private static int SideOfRunway(GroundNode join, string taxiway, double runwayBearing)
    {
        int side = 0;
        foreach (IGroundEdge edge in join.Edges)
        {
            if (!string.Equals(edge.TaxiwayName, taxiway, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int edgeSide = EdgeSide(join, edge, runwayBearing);
            if ((edgeSide == 0) || ((side != 0) && (edgeSide != side)))
            {
                return 0;
            }

            side = edgeSide;
        }

        return side;
    }

    /// <summary>
    /// True when a straight taxiway edge, any taxiway but a runway centreline, leaves <paramref name="join"/>'s centreline node
    /// toward <paramref name="side"/> of the runway line: a taxiway continues square across the runway from this join, so the
    /// offset pair is not the way across (SFO C3/S2 over 28R, where S1 crosses straight from C3's join). A stitched join's own
    /// link is the join itself, never another way across.
    /// </summary>
    private static bool HasStraightTaxiwayToward(RunwayJoin join, int side, double runwayBearing)
    {
        GroundNode node = join.Join;
        foreach (IGroundEdge edge in node.Edges)
        {
            if (
                !ReferenceEquals(edge, join.Link)
                && (edge is GroundEdge { IsRunwayCenterline: false })
                && (EdgeSide(node, edge, runwayBearing) == side)
            )
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Sine of 60°: a taxiway meeting the runway less than 60° off its line is not nearly perpendicular to it.</summary>
    private static readonly double NearlyPerpendicularSin = Math.Sin(60 * Math.PI / 180);

    /// <summary>
    /// The most a route may bend at a runway centreline node and still be crossing the runway rather than turning onto it: a
    /// sim convention between a taxiway's own bend at a crossing (SFO H over 1L/19R at #384, 25°) and a high-speed exit turning
    /// onto the taxiway across the runway (KCOS C3 onto A3 at #14, 64°).
    /// </summary>
    internal const double MaxStraightCrossingTurnDeg = 45.0;

    /// <summary>
    /// Whether a route through <paramref name="node"/>, a runway centreline node, in along <paramref name="arrival"/> and out along
    /// <paramref name="departure"/>, crosses the runway straight: both are straight edges, they lie on opposite sides of the
    /// runway's line (neither along it, <see cref="EdgeSide"/>), and the route bends at most
    /// <see cref="MaxStraightCrossingTurnDeg"/> between them (OAK C over the 33 end at #354, 52° off the line, bends 0.3°). Anything
    /// else — a turn onto or along the runway, a high-speed exit in and another taxiway out (KCOS C3 onto A3), an arc, or a
    /// same-side exit — is a turn at the runway.
    /// </summary>
    internal static bool IsStraightRunwayCrossing(GroundNode node, IGroundEdge arrival, IGroundEdge departure)
    {
        if (
            (arrival is not GroundEdge)
            || (departure is not GroundEdge)
            || node.Edges.FirstOrDefault(e => e.IsRunwayCenterline) is not { } centreline
        )
        {
            return false;
        }

        double runwayBearing = GeoMath.BearingTo(node.Position, centreline.OtherNode(node).Position);
        int inSide = EdgeSide(node, arrival, runwayBearing);
        int outSide = EdgeSide(node, departure, runwayBearing);
        if ((inSide == 0) || (outSide == 0) || (inSide == outSide))
        {
            return false;
        }

        double headingIn = (GeoMath.BearingTo(node.Position, arrival.OtherNode(node).Position) + 180.0) % 360.0;
        double headingOut = GeoMath.BearingTo(node.Position, departure.OtherNode(node).Position);
        return RouteCostFunction.HeadingDelta(headingIn, headingOut) <= MaxStraightCrossingTurnDeg;
    }

    /// <summary>
    /// True when a straight edge of <paramref name="taxiway"/> leaves <paramref name="join"/> less than 60° off the runway
    /// line: a high-speed exit, whose use as one side of an offset pair is a reversal onto or off the runway, never a
    /// crossing (7110.65 §3-7-2.c NOTE: avoid crossing points not nearly perpendicular, "reverse high speed taxiways";
    /// COS A2/C2 over 17R/35L, C2 30° off). The 60° is a sim convention; no FAA figure exists.
    /// </summary>
    private static bool MeetsRunwayObliquely(GroundNode join, string taxiway, double runwayBearing)
    {
        foreach (IGroundEdge edge in join.Edges)
        {
            if ((edge is GroundEdge) && edge.MatchesTaxiway(taxiway) && (Math.Abs(OffLineSin(join, edge, runwayBearing)) < NearlyPerpendicularSin))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The side of the runway line through <paramref name="join"/> that <paramref name="edge"/> leaves toward: +1 right,
    /// -1 left, 0 along the line.
    /// </summary>
    private static int EdgeSide(GroundNode join, IGroundEdge edge, double runwayBearing)
    {
        double offLineSin = OffLineSin(join, edge, runwayBearing);
        return Math.Abs(offLineSin) < AlongRunwayLineSin ? 0 : Math.Sign(offLineSin);
    }

    private static double OffLineSin(GroundNode join, IGroundEdge edge, double runwayBearing) =>
        Math.Sin((GeoMath.BearingTo(join.Position, edge.OtherNode(join).Position) - runwayBearing) * Math.PI / 180.0);

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

    /// <summary>
    /// The bearing a turn hint judges an onward edge by. A straight edge is judged by its bearing. A fillet
    /// <see cref="GroundArc"/> departs tangent to the taxiway it leaves, so its departure bearing reads as
    /// straight ahead however far it turns; it is judged by its arrival bearing instead, which credits it with
    /// its net turn onto the taxiway it joins.
    /// </summary>
    private static double HintJudgedBearing(DirectionalEdge edge) => edge.Edge is GroundArc ? edge.ArrivalBearing : edge.DepartureBearing;

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
            double onward = HintJudgedBearing(edge.Directed(junction, neighbor));
            if (TurnMatchesHint(SignedTurnDeg(arrivalBearing, onward), hint))
            {
                return 0.0;
            }
        }

        return TurnHintMismatchPenaltyNm;
    }

    /// <summary>
    /// Penalise a first-taxiway junction candidate (token 0) when the initial direction along that
    /// taxiway — the <see cref="HintJudgedBearing"/> of the candidate's first edge — does not match the hint on
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

        return TurnMatchesHint(SignedTurnDeg(startHeading, HintJudgedBearing(segEdges[0])), hint) ? 0.0 : TurnHintMismatchPenaltyNm;
    }

    /// <summary>
    /// First-step bias for a single-taxiway clearance with a turn hint (e.g. <c>TAXI &gt;A</c>): the
    /// position of the neighbour reached by the taxiway edge whose <see cref="HintJudgedBearing"/> is the
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
            double onward = HintJudgedBearing(edge.Directed(node, neighbor));
            if (TurnMatchesHint(SignedTurnDeg(startHeadingTrue, onward), hint))
            {
                return neighbor.Position;
            }
        }

        return null;
    }

    /// <summary>
    /// Centroid of the destination runway's hold-short nodes that lie on a straight edge of <paramref name="taxiway"/>,
    /// used as the look-ahead anchor for the final named transition when the route ends at a runway
    /// (there is no next named taxiway). Steers junction selection toward the junction whose
    /// taxiway side leads to the runway's own hold-short, so the following terminus walk heads the
    /// right way along the taxiway instead of committing to the opposite end and detouring back.
    /// Null when no hold-short for the runway sits on the taxiway (the runway is reached via a numbered
    /// variant, which <see cref="TryVariantExtension"/> handles later, or via a connector stub off the
    /// taxiway, which <see cref="TryRunwayConnectorFallback"/> handles once the whole walk has failed) — in
    /// which case junction selection keeps its prior cost-only behaviour.
    /// </summary>
    internal static (double Lat, double Lon)? ResolveRunwayHoldShortAnchorOnTaxiway(string taxiway, string runwayId, SearchContext ctx)
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

            // A straight edge of the taxiway at the bar, whichever of its straight taxiways that is: a bar on a fillet arc's
            // tangent alone is the arc's other taxiway's bar, not this one's.
            if (
                !RunwayHoldBars
                    .NamedStraightOffCentreline(node)
                    .Exists(e => string.Equals(e.TaxiwayName, taxiway, StringComparison.OrdinalIgnoreCase))
            )
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

    /// <summary>
    /// A bare walk's first step follows the aircraft's heading only when that step departs within this many degrees of it.
    /// </summary>
    private const double FirstStepHeadingAheadDeg = 90.0;

    /// <summary>
    /// A bare walk's first step follows the aircraft's heading only when every other step in its tier departs at least this
    /// many degrees further off the heading; a closer call is left to the cost ranking.
    /// </summary>
    private const double FirstStepHeadingMarginDeg = 45.0;

    /// <summary>One admissible step of a walk along a named taxiway, with its incremental cost and ranking tier.</summary>
    private readonly record struct TerminusStep(IGroundEdge Edge, GroundNode Next, double Cost, bool IsJunctionArc);

    private static (List<DirectionalEdge>? Edges, PartialRoute? Head, PathfindingFailure? Failure) WalkToNaturalTerminus(
        PartialRoute head,
        string taxiwayName,
        SearchContext ctx,
        LatLon? biasToward,
        TurnDirection? turnHint
    )
    {
        // Walk forward along the taxiway from current head, staying on the named taxiway,
        // following the geometrically admissible continuation until no more edges remain.
        var edges = new List<DirectionalEdge>();
        PartialRoute current = head;

        while (ctx.Layout.Nodes.TryGetValue(current.HeadNodeId, out GroundNode? headNode))
        {
            List<TerminusStep> candidates = TerminusCandidates(current, headNode, taxiwayName, ctx);

            // On the first (momentum-free) step, break ties toward the destination so the walk
            // heads the right way along the taxiway; afterwards admissibility fixes the direction.
            LatLon? firstStepBias = (edges.Count == 0) ? biasToward : null;
            if (CostRankedTerminusStep(candidates, firstStepBias) is not { } best)
            {
                break;
            }

            if (
                (FirstStepHeading(current, biasToward, turnHint, ctx) is { } headingTrue)
                && (HeadingLedTerminusStep(candidates, best.IsJunctionArc, headNode, headingTrue) is { } headingLed)
            )
            {
                ctx.DiagnosticLog?.Invoke(
                    $"[terminus] twy={taxiwayName} first step {headNode.Id}->{headingLed.Next.Id} follows heading {headingTrue:F0} "
                        + $"(cost ranking chose {headNode.Id}->{best.Next.Id})"
                );
                best = headingLed;
            }

            double arrival = GeometricAdmissibility.GetArrivalBearing(best.Edge, headNode, best.Next);
            string twyName = RouteCostFunction.ResolveTaxiwayName(best.Edge, current.HeadNodeId);

            edges.Add(best.Edge.Directed(headNode, best.Next));
            current = current with
            {
                HeadNodeId = best.Next.Id,
                ArrivalBearing = arrival,
                LastEdge = best.Edge,
                LastTaxiwayName = twyName,
                Previous = current,
                Depth = current.Depth + 1,
                AccumulatedCost = current.AccumulatedCost + best.Cost,
                VisitedNodeIds = current.VisitedNodeIds.Add(best.Next.Id),
            };
        }

        ctx.DiagnosticLog?.Invoke($"[terminus] twy={taxiwayName} terminus={current.HeadNodeId} edges={edges.Count}");

        return (edges, current, null);
    }

    /// <summary>Every admissible, unvisited, unblocked step from <paramref name="headNode"/> along <paramref name="taxiwayName"/>.</summary>
    private static List<TerminusStep> TerminusCandidates(PartialRoute current, GroundNode headNode, string taxiwayName, SearchContext ctx)
    {
        var candidates = new List<TerminusStep>();
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
            candidates.Add(new TerminusStep(edge, nextNode, cost, IsTerminusJunctionArc(edge, headNode, nextNode)));
        }

        return candidates;
    }

    /// <summary>
    /// Whether a step ranks in the junction-arc tier, below every single-name edge, when walking a named taxiway.
    /// </summary>
    private static bool IsTerminusJunctionArc(IGroundEdge edge, GroundNode headNode, GroundNode nextNode)
    {
        // req ①: when extending the same named taxiway, an exact single-name edge ranks
        // strictly above a junction arc that matches taxiwayName only by membership (e.g.
        // an "A - Q1" arc when walking A). A multi-name junction arc is a turn OFF the
        // taxiway onto a crossing one, not a continuation of it — the collapsed junctions
        // expose several such membership matches at one node. Single-name wins regardless
        // of cost; cost only breaks ties within the same tier. Runway-crossing arcs
        // (IsRunwayJunction, e.g. "H - RWY...") DO continue the taxiway across a runway and
        // are not treated as junction arcs.
        if (edge is GroundArc { IsMembershipTaxiwayJunctionArc: true })
        {
            return true;
        }

        if (edge is not GroundArc { TaxiwayNames.Length: >= 2 } runwayJunctionArc)
        {
            return false;
        }

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
        return GeoMath.AbsBearingDifference(departure, arrivalTangent) > 90.0;
    }

    /// <summary>
    /// The step the walk takes by cost: a single-name edge beats a junction arc, then within a tier the cheaper step wins,
    /// or the one nearer <paramref name="firstStepBias"/> when it is set. Null when there is no candidate.
    /// </summary>
    private static TerminusStep? CostRankedTerminusStep(List<TerminusStep> candidates, LatLon? firstStepBias)
    {
        TerminusStep? best = null;
        double bestRank = double.MaxValue;
        foreach (TerminusStep candidate in candidates)
        {
            double rank = (firstStepBias is { } bias) ? GeoMath.DistanceNm(candidate.Next.Position, bias) : candidate.Cost;
            bool better =
                (best is not { } held)
                || (held.IsJunctionArc && !candidate.IsJunctionArc)
                || ((held.IsJunctionArc == candidate.IsJunctionArc) && (rank < bestRank));
            if (better)
            {
                best = candidate;
                bestRank = rank;
            }
        }

        return best;
    }

    /// <summary>
    /// The aircraft's heading when the walk is about to take a bare taxi's first step: no edge taken yet, no destination
    /// to bias toward, a known heading and no explicit turn hint on the walked taxiway. Null otherwise. The rule holds under
    /// every <see cref="RoutePreference"/>, <see cref="RoutePreference.Shortest"/> included, unlike the first-hop cost bias.
    /// </summary>
    private static double? FirstStepHeading(PartialRoute current, LatLon? biasToward, TurnDirection? turnHint, SearchContext ctx) =>
        ((current.LastEdge is null) && (biasToward is null) && (turnHint is null)) ? ctx.StartHeadingTrue : null;

    /// <summary>
    /// The step in the winning tier that the heading clearly favours: within <see cref="FirstStepHeadingAheadDeg"/> of
    /// <paramref name="headingTrue"/>, with every other step in the tier at least <see cref="FirstStepHeadingMarginDeg"/>
    /// further off. Null when no step is clearly favoured, which leaves the cost ranking's choice.
    /// </summary>
    private static TerminusStep? HeadingLedTerminusStep(
        List<TerminusStep> candidates,
        bool tierIsJunctionArc,
        GroundNode headNode,
        double headingTrue
    )
    {
        List<(TerminusStep Step, double OffHeadingDeg)> tier =
        [
            .. candidates
                .Where(c => (c.IsJunctionArc == tierIsJunctionArc) && !GeometricAdmissibility.IsNoOpEdge(c.Edge))
                .Select(c =>
                    (
                        Step: c,
                        OffHeadingDeg: GeoMath.AbsBearingDifference(GeometricAdmissibility.GetDepartureBearing(c.Edge, headNode, c.Next), headingTrue)
                    )
                )
                .OrderBy(t => t.OffHeadingDeg),
        ];
        if ((tier.Count == 0) || (tier[0].OffHeadingDeg > FirstStepHeadingAheadDeg))
        {
            return null;
        }

        bool clearlyAhead = (tier.Count == 1) || (tier[1].OffHeadingDeg >= tier[0].OffHeadingDeg + FirstStepHeadingMarginDeg);
        return clearlyAhead ? tier[0].Step : null;
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
        int transitionIndex,
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
            return TryDetour(head, $"#{nodeRefId}", nextTaxiway, transitionIndex, ctx);
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
            return TryDetour(head, $"#{nodeRefId}", nextTaxiway, transitionIndex, ctx);
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
            // W1..W7 off W to rwy 30). Auto-pick the one whose hold-short is the full-length lineup bar on the
            // side the route has reached when it picks (RouteMaterialiser.OrderFullLengthLineupBars). Only fall back to a
            // TransitionAmbiguous failure when the runway is unavailable (no navdata), so the controller is never asked to
            // disambiguate a resolvable clearance and we never silently guess without a reference.
            LatLon? start = ctx.Layout.Nodes.TryGetValue(head.HeadNodeId, out GroundNode? headNode) ? headNode.Position : null;
            List<GroundNode>? ordered = RouteMaterialiser.OrderFullLengthLineupBars(
                ctx.Layout.AirportId,
                destinationRunway,
                start,
                [.. variants.Select(v => v.HsNode)],
                () =>
                    TaxiPathfinder.LineupBarShadowTest(
                        ctx.Layout,
                        head.HeadNodeId,
                        destinationRunway,
                        ctx.Category,
                        ctx.WakeClass,
                        unreachableIsShadowed: true
                    )
            );
            if (ordered is [GroundNode bestHs, ..])
            {
                string bestVariant = variants.First(v => v.HsNode.Id == bestHs.Id).Name;
                var bestHsNodes = variants.Where(v => v.Name.Equals(bestVariant, StringComparison.OrdinalIgnoreCase)).Select(v => v.HsNode).ToList();
                ctx.DiagnosticLog?.Invoke(
                    $"[variant] auto-picked {bestVariant} (full-length {destinationRunway} lineup bar) from {distinctVariants.Count} variants"
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
            // A junction arc joining a connector to a link still matches the connector's name, so the link test reads the
            // name of any edge, straight or arc: the stub walk never steps onto the runway surface.
            if (edge.IsRunwayCenterline || GroundEdge.IsRunwayCrossingLinkName(edge.TaxiwayName) || !edge.MatchesTaxiway(connector))
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
    /// taxiway the controller did not name counts as an inserted connector worth flagging. A runway
    /// centreline is never a connector: the notice never reads "taxi via" a runway. When the bridge
    /// uses no such taxiway, nothing is recorded.
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
            if ((edge.Edge is GroundArc { TaxiwayNames.Length: >= 2 }) || edge.Edge.IsRunwayCenterline)
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

    /// <summary>
    /// The refusal of a clearance naming <paramref name="fromTaxiway"/> then <paramref name="toTaxiway"/>, two taxiways that
    /// meet nowhere the aircraft may join them (<see cref="FailureKind.TaxiwaysDoNotConnect"/>).
    /// </summary>
    public static string TaxiwaysDoNotConnectReason(string fromTaxiway, string toTaxiway) =>
        $"Unable, {fromTaxiway} and {toTaxiway} do not connect, request taxi instructions";

    /// <summary>
    /// The refusal of a clearance naming <paramref name="fromTaxiway"/> then <paramref name="toTaxiway"/>, two taxiways that meet
    /// only where the route cannot reach ahead (<see cref="FailureKind.NoRouteAhead"/>); with
    /// <paramref name="intersectionOnlyBehind"/>, every junction lies directly behind the aircraft.
    /// </summary>
    public static string NoRouteAheadReason(string fromTaxiway, string toTaxiway, bool intersectionOnlyBehind) =>
        $"Unable, no route from {fromTaxiway} onto {toTaxiway} ahead, request taxi instructions"
        + (intersectionOnlyBehind ? " (intersection only directly behind, would require turn-about)" : "");

    /// <summary>
    /// The refusal of a clearance naming <paramref name="fromTaxiway"/> then <paramref name="toTaxiway"/>, a declared runway
    /// crossing whose turn from the runway onto <paramref name="toTaxiway"/> exceeds the aircraft's turn limit
    /// (<see cref="FailureKind.CannotMakeTurn"/>).
    /// </summary>
    public static string CannotMakeTurnReason(string fromTaxiway, string toTaxiway) =>
        $"Unable, can't make the turn from {fromTaxiway} onto {toTaxiway}, request taxi instructions";

    /// <summary>
    /// The refusal of a bare clearance that crosses <paramref name="runway"/> onto <paramref name="taxiway"/>, which has no
    /// straight-ahead point where the aircraft's tail and nose both stand <see cref="RunwayTailClearance.MarginFt"/> past
    /// its far hold line, measured square to it, before a T, a runway centreline or another runway's bar
    /// (<see cref="FailureKind.NoRoomToClearRunway"/>).
    /// </summary>
    public static string NoRoomToClearRunwayReason(string taxiway, string runway) =>
        $"Unable, no room on {taxiway} to clear {runway}, request taxi instructions";

    /// <summary>
    /// The refusal of a clearance across a runway onto <paramref name="toTaxiway"/> when the first taxiway's line meets
    /// <paramref name="toTaxiway"/>'s past its hold bar (<see cref="FailureKind.NoRoomToCrossStraight"/>).
    /// </summary>
    public static string NoRoomToCrossStraightReason(string toTaxiway) =>
        $"Unable, no room to cross straight onto {toTaxiway}, request taxi instructions";

    /// <summary>
    /// Bridge <paramref name="fromTaxiway"/>, the clearance's <paramref name="transitionIndex"/>th token, to
    /// <paramref name="toTaxiway"/> through the best connector <see cref="PickDetourEntry"/> keeps. When it keeps none, the
    /// transition fails: with the pair refusal (<see cref="PairRefusal"/>: <see cref="FailureKind.TaxiwaysDoNotConnect"/>, or
    /// <see cref="FailureKind.NoRouteAhead"/> when the pair's junctions exist) when two named taxiways had bridges and every
    /// one was dropped (it ran along a runway, turned at one, or needed a lettered taxiway the clearance left out), with
    /// <see cref="FailureKind.TransitionInfeasible"/> when no bridge existed at all or the transition starts at a node reference.
    /// </summary>
    private static (List<DirectionalEdge>? Edges, PartialRoute? Head, PathfindingFailure? Failure) TryDetour(
        PartialRoute head,
        string fromTaxiway,
        string toTaxiway,
        int transitionIndex,
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

        (GroundNode? bestEntry, TaxiRoute? bestRoute, double bestScore, int droppedBridges) = PickDetourEntry(
            head,
            fromTaxiway,
            toTaxiway,
            transitionIndex,
            ctx
        );

        if ((bestRoute is null) && !fromTaxiway.StartsWith('#') && (droppedBridges > 0))
        {
            ctx.DiagnosticLog?.Invoke($"[detour] {fromTaxiway}→{toTaxiway}: all {droppedBridges} bridges dropped; refused");
            return (null, null, PairRefusal(head, fromTaxiway, toTaxiway, ctx));
        }

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
    /// The refusal of a named pair whose every bridge was dropped: <see cref="FailureKind.TaxiwaysDoNotConnect"/> when the two
    /// taxiways share no node of the layout, <see cref="FailureKind.NoRouteAhead"/> when they do (the junctions exist, but the
    /// walk reached none ahead), noting whether every junction lies directly behind the pose the aircraft holds at
    /// <paramref name="head"/> — the prior segment's arrival bearing, or <see cref="SearchContext.StartHeadingTrue"/> — so
    /// reaching one would take a turn-about. With no pose, no junction counts as behind.
    /// </summary>
    private static PathfindingFailure PairRefusal(PartialRoute head, string fromTaxiway, string toTaxiway, SearchContext ctx)
    {
        List<GroundNode> junctions = [.. ctx.Layout.GetNodesOnTaxiway(fromTaxiway).Where(n => NodeIncidentToTaxiway(n, toTaxiway))];
        if (junctions.Count == 0)
        {
            return new PathfindingFailure(
                FailureKind.TaxiwaysDoNotConnect,
                TaxiwaysDoNotConnectReason(fromTaxiway, toTaxiway),
                fromTaxiway,
                $"{fromTaxiway} → {toTaxiway}",
                null
            )
            {
                UnconnectedFrom = fromTaxiway,
                UnconnectedTo = toTaxiway,
            };
        }

        double? pose = head.LastEdge is not null ? head.ArrivalBearing : ctx.StartHeadingTrue;
        bool onlyBehind =
            (pose is { } heading)
            && ctx.Layout.Nodes.TryGetValue(head.HeadNodeId, out GroundNode? at)
            && junctions.All(j => RouteCostFunction.HeadingDelta(heading, GeoMath.BearingTo(at.Position, j.Position)) > 90.0);
        ctx.DiagnosticLog?.Invoke(
            $"[detour] {fromTaxiway}→{toTaxiway}: {junctions.Count} junction(s) exist but none was reached ahead; only behind={onlyBehind}"
        );
        return new PathfindingFailure(
            FailureKind.NoRouteAhead,
            NoRouteAheadReason(fromTaxiway, toTaxiway, onlyBehind),
            fromTaxiway,
            $"{fromTaxiway} → {toTaxiway}",
            null
        )
        {
            UnconnectedFrom = fromTaxiway,
            UnconnectedTo = toTaxiway,
            IntersectionOnlyBehind = onlyBehind,
        };
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
    ///
    /// <para>A bridge never runs along a runway, however short the run, and between two named taxiways it uses no lettered
    /// taxiway the clearance left out (<see cref="BridgeDropReason"/>): a candidate that does is dropped before ranking, and
    /// <c>DroppedBridges</c> counts them. Two taxiways that meet only across a runway are a crossing, resolved before the
    /// detour (<see cref="TryRouteAcrossOffsetRunway"/>), never a bridge.</para>
    /// </summary>
    private static (GroundNode? Entry, TaxiRoute? Route, double Score, int DroppedBridges) PickDetourEntry(
        PartialRoute head,
        string fromTaxiway,
        string toTaxiway,
        int transitionIndex,
        SearchContext ctx
    )
    {
        BridgeRules rules = BridgeRulesFor(head, fromTaxiway, toTaxiway, transitionIndex, ctx);
        (GroundNode? Entry, TaxiRoute? Route, double Score, int DroppedBridges) legal = RankBridges(
            head,
            toTaxiway,
            ctx,
            rules,
            ctx with
            {
                Bridge = rules,
            }
        );
        if ((legal.Route is not null) || fromTaxiway.StartsWith('#'))
        {
            return legal;
        }

        // No legal bridge: today's unrestricted search classifies the refusal. Bridges it finds are the ones the post-hoc filter
        // drops (logged, counted: the pair does not connect); none at all is a transition with no bridge. A bridge that survives
        // the filter here is one the restricted search did not find, so the two judges disagree: it is refused, counted as
        // dropped, and the disagreement is warned about naming both verdicts.
        ctx.DiagnosticLog?.Invoke(
            $"[detour] {fromTaxiway}→{toTaxiway}: no legal bridge from any entry node; classifying with the unrestricted search"
        );
        (GroundNode? Entry, TaxiRoute? Route, double Score, int DroppedBridges) unrestricted = RankBridges(head, toTaxiway, ctx, rules, ctx);
        if (unrestricted is not { Route: { } bridge, Entry: { } entry })
        {
            return unrestricted;
        }

        (string? search, string? filter) = JudgeBridge(head, bridge, rules, ctx);
        Log.LogWarning(
            "[detour] {From}→{To} from #{Head}: the unrestricted search found a bridge via #{Entry} the restricted search did not; "
                + "refused — search: {Search}; filter: {Filter}",
            fromTaxiway,
            toTaxiway,
            head.HeadNodeId,
            entry.Id,
            search ?? "legal",
            filter ?? "legal"
        );
        ctx.DiagnosticLog?.Invoke(
            $"[detour] {fromTaxiway}→{toTaxiway}: refuses the bridge via #{entry.Id} only the unrestricted search found; "
                + $"search={search ?? "legal"}; filter={filter ?? "legal"}"
        );
        return (null, null, double.MaxValue, unrestricted.DroppedBridges + 1);
    }

    /// <summary>
    /// The rules the bridge from <paramref name="head"/> obeys
    /// (<see cref="SearchContext.BridgeMoveRefusal(BridgeRules, BridgeProgress, GroundNode, IGroundEdge, GroundNode)"/>, in the search and
    /// the post-hoc filter alike): the runway the aircraft stands on, usable only to leave it, when the bridge starts where the
    /// aircraft stands; whether the bridge starts on <paramref name="fromTaxiway"/>; and the letter-only taxiways the connector
    /// may not use — every one the clearance left out (<see cref="CountsAsNamed"/>), ramp taxilanes excepted — for a named pair
    /// whose transition is not the implied lead's. The transition is the clearance's <paramref name="transitionIndex"/>th, and
    /// only the first (index 0) may lean on where the aircraft starts: a later transition off the same taxiway as the lead is
    /// no first transition.
    /// </summary>
    internal static BridgeRules BridgeRulesFor(PartialRoute head, string fromTaxiway, string toTaxiway, int transitionIndex, SearchContext ctx)
    {
        string? startRunway = null;
        if ((head.HeadNodeId == ctx.StartNodeId) && ctx.Layout.Nodes.TryGetValue(ctx.StartNodeId, out GroundNode? start))
        {
            startRunway = start.Edges.FirstOrDefault(e => e.IsRunwayCenterline) is { } centreline
                ? RouteCostFunction.ResolveTaxiwayName(centreline, start.Id)
                : null;
        }

        bool headOnFromTaxiway = HeadHasReachedTaxiway(head, fromTaxiway, ctx);
        bool firstTransition = transitionIndex == 0;
        bool judgeLetters = !fromTaxiway.StartsWith('#') && !(firstTransition && IsImpliedLeadTransition(fromTaxiway, ctx));
        if (!judgeLetters)
        {
            return new BridgeRules(startRunway, fromTaxiway, headOnFromTaxiway, NoExcludedTaxiways);
        }

        var classification = MovementAreaClassification.For(ctx.Layout);
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in ctx.Layout.AllTaxiwayNames)
        {
            if (
                SearchContext.IsLetterOnlyTaxiway(name)
                && !classification.IsRampTaxilane(name)
                && !CountsAsNamed(name, fromTaxiway, toTaxiway, firstTransition, ctx)
            )
            {
                excluded.Add(name);
            }
        }

        return new BridgeRules(startRunway, fromTaxiway, headOnFromTaxiway, excluded);
    }

    private static readonly IReadOnlySet<string> NoExcludedTaxiways = new HashSet<string>();

    /// <summary>
    /// One bounded search per node of <paramref name="toTaxiway"/> on <paramref name="template"/>, each surviving bridge ranked
    /// as <see cref="PickDetourEntry"/> describes; <c>DroppedBridges</c> counts the ones <see cref="BridgeDropReason"/> refused.
    /// With a diagnostic log, every bridge found is also judged by the search predicate (<see cref="JudgeBridge"/>) and logged as
    /// <c>[detour] judged …</c>.
    /// </summary>
    private static (GroundNode? Entry, TaxiRoute? Route, double Score, int DroppedBridges) RankBridges(
        PartialRoute head,
        string toTaxiway,
        SearchContext ctx,
        BridgeRules rules,
        SearchContext template
    )
    {
        string fromTaxiway = rules.FromTaxiway;
        bool rankByPose = (head.LastEdge is not null) || (ctx.StartHeadingTrue is not null);
        bool guardFromTaxiway = rankByPose && !fromTaxiway.StartsWith('#') && !rules.HeadOnFromTaxiway;

        (GroundNode? Entry, TaxiRoute? Route, double Score) best = (null, null, double.MaxValue);
        (GroundNode? Entry, TaxiRoute? Route, double Score) shortest = (null, null, double.MaxValue);
        int droppedBridges = 0;

        foreach (GroundNode entryNode in ctx.Layout.GetNodesOnTaxiway(toTaxiway))
        {
            if (entryNode.Id == head.HeadNodeId)
            {
                continue;
            }

            (TaxiRoute? route, PathfindingFailure? _) = RunBoundedDetour(BuildDetourContext(template, head.HeadNodeId, entryNode.Id), head);
            if (route is null)
            {
                continue;
            }

            string? filter = BridgeDropReason(head, route.Segments.Select(s => s.Edge), rules, ctx);
            if (ctx.DiagnosticLog is { } log)
            {
                string? search = SearchVerdict(head, route, rules, ctx);
                log($"[detour] judged {fromTaxiway}→{toTaxiway} bridge via #{entryNode.Id}: search={search ?? "legal"}; filter={filter ?? "legal"}");
            }

            if (filter is { } dropReason)
            {
                ctx.DiagnosticLog?.Invoke($"[detour] drops {fromTaxiway}→{toTaxiway} bridge via #{entryNode.Id}: {dropReason}");
                droppedBridges++;
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
            return (shortest.Entry, shortest.Route, shortest.Score, droppedBridges);
        }

        return (best.Entry, best.Route, best.Score, droppedBridges);
    }

    /// <summary>
    /// Both judgements of one bridge from <paramref name="head"/>: the search predicate's
    /// (<see cref="SearchContext.BridgeMoveRefusal(PartialRoute, IGroundEdge, GroundNode)"/>, replayed move by move from the
    /// head) and the post-hoc filter's (<see cref="BridgeDropReason"/>); null is legal. Both call the one judgement
    /// (<see cref="SearchContext.BridgeMoveRefusal(BridgeRules, BridgeProgress, GroundNode, IGroundEdge, GroundNode)"/>) and so
    /// agree on every bridge, which the agreement tests assert.
    /// </summary>
    internal static (string? Search, string? Filter) JudgeBridge(PartialRoute head, TaxiRoute route, BridgeRules rules, SearchContext ctx) =>
        (SearchVerdict(head, route, rules, ctx), BridgeDropReason(head, route.Segments.Select(s => s.Edge), rules, ctx));

    /// <summary>
    /// The search predicate's judgement of the bridge <paramref name="route"/> from <paramref name="head"/>, replayed move by move
    /// with the progress the search carries (<see cref="SearchContext.BridgeProgressAtStart"/>,
    /// <see cref="SearchContext.BridgeProgressAfter"/>), or null when it allows every move.
    /// </summary>
    private static string? SearchVerdict(PartialRoute head, TaxiRoute route, BridgeRules rules, SearchContext ctx)
    {
        SearchContext search = ctx with { Bridge = rules };
        PartialRoute current = head with { BridgeProgress = search.BridgeProgressAtStart(head) };
        foreach (TaxiRouteSegment seg in route.Segments)
        {
            if (search.BridgeMoveRefusal(current, seg.Edge.Edge, seg.Edge.ToNode) is { } refusal)
            {
                return refusal;
            }

            BridgeProgress? after = search.BridgeProgressAfter(current, seg.Edge.FromNode, seg.Edge.Edge);
            current = Advance(current, seg.Edge, seg.TaxiwayName) with { BridgeProgress = after };
        }

        return null;
    }

    /// <summary>
    /// Why the bridge <paramref name="edges"/> from <paramref name="head"/> may not be driven under <paramref name="rules"/>, or
    /// null when it may: the post-hoc filter, the one judgement
    /// (<see cref="SearchContext.BridgeMoveRefusal(BridgeRules, BridgeProgress, GroundNode, IGroundEdge, GroundNode)"/>) applied
    /// to each move in turn with the progress the moves before it made (<see cref="BridgeProgress.After"/>), the first move judged
    /// against the head's own last edge as the search judges it.
    /// </summary>
    private static string? BridgeDropReason(PartialRoute head, IEnumerable<DirectionalEdge> edges, BridgeRules rules, SearchContext ctx)
    {
        var progress = BridgeProgress.AtStart(head.LastEdge, rules);
        foreach (DirectionalEdge move in edges)
        {
            if (ctx.BridgeMoveRefusal(rules, progress, move.FromNode, move.Edge, move.ToNode) is { } refusal)
            {
                return refusal;
            }

            progress = progress.After(move.FromNode, move.Edge, rules);
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="fromTaxiway"/> is the occupied taxiway the handler put in front of the clearance
    /// (<see cref="SearchContext.ImpliedLeadTaxiway"/>): the bridge off it is the start leg, which the letter rule never judges.
    /// </summary>
    private static bool IsImpliedLeadTransition(string fromTaxiway, SearchContext ctx) =>
        (ctx.ImpliedLeadTaxiway is { } lead) && fromTaxiway.Equals(lead, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the clearance names <paramref name="name"/> for the transition, explicitly or by implication: an end of the
    /// transition, a token of the clearance before <paramref name="toTaxiway"/> (taxiways are flown in clearance order, so a
    /// later one is no licence for an earlier connector: KCOS <c>TAXI C2 A2 A</c> may not bridge C2 to A2 along A), the pair's
    /// curated connector, or — on the clearance's first transition only — a taxiway the aircraft is on. The aircraft is on the
    /// taxiway the caller found it occupying (<see cref="SearchContext.OccupiedTaxiway"/>) and on every taxiway an edge at the
    /// route's start node carries (<see cref="StartIsOnTaxiway"/>, the resolver's own sense of where the taxi starts): an
    /// aircraft pushed off a gate whose stored current taxiway is stale still starts on the taxiway it was pushed onto.
    /// </summary>
    private static bool CountsAsNamed(string name, string fromTaxiway, string toTaxiway, bool firstTransition, SearchContext ctx)
    {
        if (name.Equals(fromTaxiway, StringComparison.OrdinalIgnoreCase) || name.Equals(toTaxiway, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (firstTransition)
        {
            return name.Equals(ctx.OccupiedTaxiway, StringComparison.OrdinalIgnoreCase)
                || StartIsOnTaxiway(ctx, name)
                || NamedBefore(name, fromTaxiway, toTaxiway, ctx);
        }

        // Past the first transition only the clearance as issued counts: the occupied taxiway the handler prepended as the
        // implied lead names itself once, at the front, and that mention is the handler's, not the controller's.
        bool impliedLead = (ctx.ImpliedLeadTaxiway is { } lead) && name.Equals(lead, StringComparison.OrdinalIgnoreCase);
        return impliedLead
            ? ClearedBefore(name, fromTaxiway, toTaxiway, [.. ctx.WaypointSequence.Skip(1)])
            : NamedBefore(name, fromTaxiway, toTaxiway, ctx);
    }

    /// <summary>
    /// Whether <paramref name="name"/> is the pair's curated connector (<see cref="SearchContext.GetImplicitConnectorName"/>) or a
    /// token of the clearance before the transition (<see cref="ClearedBefore"/>).
    /// </summary>
    private static bool NamedBefore(string name, string fromTaxiway, string toTaxiway, SearchContext ctx) =>
        name.Equals(ctx.GetImplicitConnectorName(fromTaxiway, toTaxiway), StringComparison.OrdinalIgnoreCase)
        || ClearedBefore(name, fromTaxiway, toTaxiway, ctx.WaypointSequence);

    /// <summary>
    /// Whether <paramref name="sequence"/> names <paramref name="name"/> before the transition from <paramref name="fromTaxiway"/>
    /// to <paramref name="toTaxiway"/>: among the tokens up to the pair's own mention (the first mention of
    /// <paramref name="toTaxiway"/> right after one of <paramref name="fromTaxiway"/>), or, when the pair is nowhere adjacent in
    /// it, up to the first mention of <paramref name="toTaxiway"/>. A later mention of <paramref name="toTaxiway"/> licenses
    /// nothing named between the two.
    /// </summary>
    internal static bool ClearedBefore(string name, string fromTaxiway, string toTaxiway, IReadOnlyList<string> sequence)
    {
        int first = -1;
        int adjacent = -1;
        for (int i = 0; i < sequence.Count; i++)
        {
            if (!sequence[i].Equals(toTaxiway, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (first < 0)
            {
                first = i;
            }

            if ((i > 0) && sequence[i - 1].Equals(fromTaxiway, StringComparison.OrdinalIgnoreCase))
            {
                adjacent = i;
                break;
            }
        }

        int end = adjacent >= 0 ? adjacent : (first >= 0 ? first : sequence.Count);
        return sequence.Take(end).Contains(name, StringComparer.OrdinalIgnoreCase);
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
    /// Build a detour SearchContext for bridging two cleared taxiways that have no direct junction, from
    /// <paramref name="template"/>: the outer context, with <see cref="SearchContext.Bridge"/> set for the restricted
    /// search and null for the classification pass (<see cref="PickDetourEntry"/>). Inherits the original
    /// <see cref="SearchContext.AuthorizedTaxiways"/> so the cost function still prefers numbered connectors and RAMP
    /// edges over the letter taxiways the rules leave usable.
    /// </summary>
    private static SearchContext BuildDetourContext(SearchContext template, int fromNodeId, int toNodeId)
    {
        return template with
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

    private static (List<DirectionalEdge>? Edges, string? ImpliedOneWayLane, PathfindingFailure? Failure) ExtendToDestination(
        PartialRoute head,
        int destinationNodeId,
        SearchContext ctx,
        IReadOnlySet<string> uncleared
    )
    {
        ctx.DiagnosticLog?.Invoke($"[extend] extending to destination #{destinationNodeId} from head={head.HeadNodeId}");

        // The extension is the pathfinder's own choice, not the controller's, so one-way lanes bind it hard even when the
        // named legs before it run in warn mode.
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
            OneWayMode = ctx.ForbiddenOneWayMoves.Count > 0 ? OneWayMode.HardExclude : OneWayMode.Off,
        };

        // The extension stays on the cleared taxiways, the ramp taxilanes and RAMP: a controller clearing "TAXI B @F1"
        // expects the gate reached by staying on B and turning onto the ramp lane, never by threading a movement-area
        // taxiway the clearance did not name (B Q A T9 RAMP). Every such taxiway is hard-excluded; when the stand is
        // reachable only across one, the extension fails, and the TAXI handler holds the aircraft short of that taxiway
        // instead (issue #461). The one exception is the destination's own short lead-in taxiway (see
        // TryExtendViaImpliedLeadIn), tried only once the confined search has failed. The caller computes the uncleared
        // set once per search, not once per candidate stop.
        SearchContext confinedCtx =
            uncleared.Count > 0 ? extCtx with { AvoidedTaxiways = uncleared, AvoidMode = AvoidTaxiwayMode.HardExclude } : extCtx;
        (TaxiRoute? route, PathfindingFailure? failure) = AutoRouter.Run(confinedCtx, startOverride: head);
        if (((failure is not null) || (route is null)) && (TryExtendViaImpliedLeadIn(head, destinationNodeId, extCtx, uncleared) is { } leadIn))
        {
            route = leadIn;
            failure = null;
        }

        string? impliedLane = null;
        if (((failure is not null) || (route is null)) && (TryExtendViaOneWayLane(head, extCtx, uncleared) is { } viaLane))
        {
            (route, impliedLane) = viaLane;
            failure = null;
        }

        if (failure is not null || route is null)
        {
            ctx.DiagnosticLog?.Invoke("[extend] confined (cleared + ramp taxilanes + RAMP) extension found no route");
            return (
                null,
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

        return ([.. route.Segments.Select(s => s.Edge)], impliedLane, null);
    }

    /// <summary>
    /// The confined extension admitting one uncleared movement-area taxiway X that is one of the airport's one-way lanes
    /// (<see cref="OneWayResolver.GetOneWayLaneTaxiways"/>) as the connector from the last cleared taxiway into the ramp:
    /// SFO <c>TAXI T A @B2</c> enters the Terminal 1 ramp on M1, because M2 is one-way outbound. It runs only after the
    /// confined search, with this aircraft's one-way lanes hard-excluded, and the implied lead-in have both failed, so X
    /// is the lane the one-way rules leave the aircraft. Each X is tried with every other uncleared taxiway still
    /// excluded, and a route reaching a runway holding position or runway pavement is refused: the lane is implied into
    /// the ramp, never across a runway (SFO M1 continues past the ramp to the 01L holding position). A lane has no length
    /// cap: the one-way rules, not the distance, decide that it is the way in. The best route by
    /// <see cref="IsBetterRoute"/> wins; null when no lane qualifies.
    /// </summary>
    private static (TaxiRoute Route, string Lane)? TryExtendViaOneWayLane(PartialRoute head, SearchContext extCtx, IReadOnlySet<string> uncleared)
    {
        IReadOnlySet<string> lanes = OneWayResolver.GetOneWayLaneTaxiways(extCtx.Layout);
        (TaxiRoute Route, string Lane)? best = null;
        foreach (string lane in uncleared.Where(lanes.Contains).Order(StringComparer.OrdinalIgnoreCase))
        {
            var others = new HashSet<string>(
                uncleared.Where(n => !n.Equals(lane, StringComparison.OrdinalIgnoreCase)),
                StringComparer.OrdinalIgnoreCase
            );
            SearchContext laneCtx = extCtx with { AvoidedTaxiways = others, AvoidMode = AvoidTaxiwayMode.HardExclude };
            (TaxiRoute? route, PathfindingFailure? _) = AutoRouter.Run(laneCtx, startOverride: head);
            bool reachesRunway = (route is not null) && ReachesRunway(route, extCtx.Layout);
            string laneRoute = route is null ? "no route" : route.FormatTaxiwaySequence();
            extCtx.DiagnosticLog?.Invoke($"[extend] one-way lane {lane}: {laneRoute}{(reachesRunway ? " (refused: reaches a runway)" : "")}");
            if ((route is null) || reachesRunway)
            {
                continue;
            }

            if ((best is null) || IsBetterRoute(route, best.Value.Route))
            {
                best = (route, lane);
            }
        }

        return best;
    }

    /// <summary>
    /// The graph route from <paramref name="ctx"/>'s start to its destination node that stays inside the ramp, for a TAXI
    /// naming no taxiway from nonmovement pavement: over ramp taxilanes, RAMP and unnamed pavement, every movement-area
    /// taxiway and this aircraft's one-way moves hard-excluded, and failing that with one of the airport's one-way lanes
    /// implied under <see cref="TryExtendViaOneWayLane"/>'s rule. A route reaching a runway holding position or runway
    /// pavement is refused. The implied lane, if any, is recorded on the route (<see cref="TaxiRoute.ImpliedLanes"/>, with
    /// its <see cref="RouteMaterialiser.NotInClearanceWarning"/>). Null when no such route exists.
    /// </summary>
    public static TaxiRoute? FindRampConfinedRoute(SearchContext ctx)
    {
        IReadOnlySet<string> uncleared = UnclearedMovementAreaTaxiways(ctx);
        SearchContext rampCtx = ctx with
        {
            WaypointSequence = [],
            AuthorizedTaxiways = null,
            OneWayMode = ctx.ForbiddenOneWayMoves.Count > 0 ? OneWayMode.HardExclude : OneWayMode.Off,
        };
        var excluded = new HashSet<string>(uncleared, StringComparer.OrdinalIgnoreCase);
        excluded.UnionWith(ctx.AvoidedTaxiways);
        SearchContext confinedCtx =
            excluded.Count > 0 ? rampCtx with { AvoidedTaxiways = excluded, AvoidMode = AvoidTaxiwayMode.HardExclude } : rampCtx;

        (TaxiRoute? route, PathfindingFailure? _) = AutoRouter.Run(confinedCtx);
        if ((route is not null) && !ReachesRunway(route, ctx.Layout))
        {
            return route;
        }

        if (TryExtendViaOneWayLane(PartialRoute.StartAt(ctx.StartNodeId), rampCtx, uncleared) is not { } viaLane)
        {
            ctx.DiagnosticLog?.Invoke($"[ramp] no route from #{ctx.StartNodeId} to #{ctx.Destination.TargetNodeId} inside the ramp");
            return null;
        }

        viaLane.Route.Warnings.Add(RouteMaterialiser.NotInClearanceWarning(viaLane.Lane));
        viaLane.Route.ImpliedLanes.Add(viaLane.Lane);
        return viaLane.Route;
    }

    /// <summary>True when <paramref name="route"/> arrives at a runway holding position or drives runway pavement.</summary>
    private static bool ReachesRunway(TaxiRoute route, AirportGroundLayout layout) =>
        route.Segments.Any(s =>
            s.Edge.Edge.IsRunwayCenterline
            || (layout.Nodes.TryGetValue(s.ToNodeId, out GroundNode? node) && (node.Type == GroundNodeType.RunwayHoldShort))
        );

    /// <summary>
    /// The longest run an uncleared movement-area taxiway may be driven as a gate's or spot's implied lead-in, in feet
    /// (the implied-lead-in rule): a short run onto the stand's own taxiway is implied; a longer one needs the taxiway named.
    /// </summary>
    public const double MaxImpliedLeadInFt = 1000.0;

    /// <summary>
    /// The confined extension admitting one uncleared movement-area taxiway X as the destination's implied lead-in: OAK
    /// <c>TAXI G @SIG1</c> drives 635 ft of D and then only apron to SIG1. X is any uncleared taxiway touching the apron
    /// around the destination (the nodes reached from it along RAMP and its own lane); each is tried with every other
    /// uncleared taxiway still excluded, and a route counts only when <see cref="ImpliedDestinationLeadIn"/> names X on
    /// it. A route reaching a runway holding position or runway pavement is refused, as for the one-way lane: a lead-in is
    /// never implied across a runway's holding position (SFO spot 35 sits 7 ft past the 01L bar on M1). The best route by
    /// <see cref="IsBetterRoute"/> wins. Null for a destination that is no gate or spot, or when no X qualifies.
    /// </summary>
    private static TaxiRoute? TryExtendViaImpliedLeadIn(
        PartialRoute head,
        int destinationNodeId,
        SearchContext extCtx,
        IReadOnlySet<string> uncleared
    )
    {
        string? parkingName = extCtx.Destination.ParkingName;
        string? spotName = extCtx.Destination.SpotName;
        if (((parkingName is null) && (spotName is null)) || !extCtx.Layout.Nodes.TryGetValue(destinationNodeId, out GroundNode? destination))
        {
            return null;
        }

        TaxiRoute? best = null;
        foreach (string leadIn in LeadInCandidates(destination, parkingName, spotName, uncleared))
        {
            var others = new HashSet<string>(
                uncleared.Where(n => !n.Equals(leadIn, StringComparison.OrdinalIgnoreCase)),
                StringComparer.OrdinalIgnoreCase
            );
            SearchContext leadInCtx = extCtx with { AvoidedTaxiways = others, AvoidMode = AvoidTaxiwayMode.HardExclude };
            (TaxiRoute? route, PathfindingFailure? _) = AutoRouter.Run(leadInCtx, startOverride: head);
            if ((route is null) || ReachesRunway(route, extCtx.Layout))
            {
                extCtx.DiagnosticLog?.Invoke($"[extend] lead-in {leadIn}: {(route is null ? "no route" : "refused: reaches a runway")}");
                continue;
            }

            string? implied = ImpliedDestinationLeadIn([.. route.Segments.Select(s => s.Edge.Edge)], parkingName, spotName, uncleared.Contains);
            double routeFt = route.TotalDistanceNm * GeoMath.FeetPerNm;
            extCtx.DiagnosticLog?.Invoke(
                $"[extend] lead-in {leadIn}: route {route.FormatTaxiwaySequence()} ({routeFt:F0} ft), implied={implied ?? "no"}"
            );
            if (string.Equals(implied, leadIn, StringComparison.OrdinalIgnoreCase) && ((best is null) || IsBetterRoute(route, best)))
            {
                best = route;
            }
        }

        return best;
    }

    /// <summary>
    /// The uncleared taxiways meeting the apron around <paramref name="destination"/>: every node reached from it along
    /// RAMP and the destination's own lane, and the <paramref name="uncleared"/> names on the edges at those nodes.
    /// </summary>
    private static SortedSet<string> LeadInCandidates(GroundNode destination, string? parkingName, string? spotName, IReadOnlySet<string> uncleared)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<int> { destination.Id };
        var frontier = new Queue<GroundNode>([destination]);
        while (frontier.TryDequeue(out GroundNode? node))
        {
            foreach (IGroundEdge edge in node.Edges)
            {
                IReadOnlyList<string> edgeNames = EdgeNames(edge);
                names.UnionWith(edgeNames.Where(uncleared.Contains));
                bool apron = edgeNames.All(n => IsApronName(n, parkingName, spotName));
                GroundNode next = edge.OtherNode(node);
                if (apron && seen.Add(next.Id))
                {
                    frontier.Enqueue(next);
                }
            }
        }

        return names;
    }

    /// <summary>
    /// The uncleared movement-area taxiway X that <paramref name="edges"/> drive as the destination's implied lead-in,
    /// or null. X is the one <paramref name="isUncleared"/> name on the last edge that is not
    /// apron (RAMP, the destination stand's or spot's own lane, a free-space leg), so after X the route drives only apron to
    /// the gate or spot; and the edges driven on X, straight and arc, total at most <see cref="MaxImpliedLeadInFt"/>.
    /// </summary>
    public static string? ImpliedDestinationLeadIn(
        IReadOnlyList<IGroundEdge> edges,
        string? parkingName,
        string? spotName,
        Func<string, bool> isUncleared
    )
    {
        if ((parkingName is null) && (spotName is null))
        {
            return null;
        }

        int last = edges.Count - 1;
        while ((last >= 0) && (VirtualNode.IsVirtualEdge(edges[last]) || EdgeNames(edges[last]).All(n => IsApronName(n, parkingName, spotName))))
        {
            last--;
        }

        if (last < 0)
        {
            return null;
        }

        List<string> unclearedNames = [.. EdgeNames(edges[last]).Where(isUncleared)];
        if (unclearedNames.Count != 1)
        {
            return null;
        }

        string leadIn = unclearedNames[0];
        double runFt = edges.Where(e => EdgeNames(e).Contains(leadIn, StringComparer.OrdinalIgnoreCase)).Sum(e => e.DistanceNm) * GeoMath.FeetPerNm;
        return runFt <= MaxImpliedLeadInFt ? leadIn : null;
    }

    /// <summary>Every name an edge carries: an arc's taxiway names, or a straight edge's name split on <c>" - "</c>.</summary>
    public static IReadOnlyList<string> EdgeNames(IGroundEdge edge) =>
        edge is GroundArc arc
            ? arc.TaxiwayNames
            : edge.TaxiwayName.Split(" - ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static bool IsApronName(string name, string? parkingName, string? spotName) =>
        name.Equals("RAMP", StringComparison.OrdinalIgnoreCase)
        || name.Equals(parkingName, StringComparison.OrdinalIgnoreCase)
        || name.Equals(spotName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Every movement-area taxiway in the layout the clearance did not name: not in the waypoint sequence, not RAMP, not
    /// a ramp taxilane (<see cref="MovementAreaClassification.IsRampTaxilane"/>), not the destination stand's or spot's
    /// own lead-in. Runway pavement is left to the centreline rules, so a runway name is never listed.
    /// </summary>
    private static IReadOnlySet<string> UnclearedMovementAreaTaxiways(SearchContext ctx)
    {
        var classification = MovementAreaClassification.For(ctx.Layout);
        var cleared = new HashSet<string>(ctx.WaypointSequence, StringComparer.OrdinalIgnoreCase);
        cleared.UnionWith(new[] { ctx.Destination.ParkingName, ctx.Destination.SpotName }.OfType<string>());
        return ctx
            .Layout.Nodes.Values.SelectMany(n => n.Edges)
            .SelectMany(EdgeNames)
            .Where(name => IsUnclearedMovementAreaName(name, cleared, classification))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A movement-area taxiway name that <paramref name="cleared"/> leaves out: never blank, RAMP, a ramp taxilane
    /// (<see cref="MovementAreaClassification.IsMovementArea"/>) or runway pavement (a <c>RWY</c> name or a
    /// <c>RWY…:link</c> stub). The one rule the TAXI extension, its readback warning and the missing-taxiway fallback share.
    /// </summary>
    public static bool IsUnclearedMovementAreaName(string name, IReadOnlySet<string> cleared, MovementAreaClassification classification) =>
        (name.Length > 0) && !cleared.Contains(name) && !IsRunwayWaypoint(name) && !name.Contains(':') && classification.IsMovementArea(name);

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary><paramref name="current"/> extended over <paramref name="edge"/>, driven as <paramref name="taxiwayName"/>.</summary>
    private static PartialRoute Advance(PartialRoute current, DirectionalEdge edge, string taxiwayName) =>
        current with
        {
            HeadNodeId = edge.ToNodeId,
            ArrivalBearing = edge.ArrivalBearing,
            LastEdge = edge.Edge,
            LastTaxiwayName = taxiwayName,
            Previous = current,
            Depth = current.Depth + 1,
            AccumulatedCost = current.AccumulatedCost + edge.DistanceNm,
            VisitedNodeIds = current.VisitedNodeIds.Add(edge.ToNodeId),
        };

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
            current = Advance(current, seg.Edge, seg.TaxiwayName);
        }

        return current;
    }
}
