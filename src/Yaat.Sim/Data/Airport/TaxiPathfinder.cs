namespace Yaat.Sim.Data.Airport;

using Yaat.Sim.Data.Airport.Pathfinding;

/// <summary>
/// Taxi pathfinder. Auto-route methods (<see cref="FindRoute"/>, <see cref="FindRoutes"/>) are
/// implemented with the A* <see cref="AutoRouter"/>. <see cref="ResolveExplicitPath"/> is implemented
/// with <see cref="SegmentExpander"/>. All methods are stateless; production code calls them directly.
/// </summary>
public static class TaxiPathfinder
{
    /// <summary>
    /// How far ahead a bare <c>TAXI &lt;rwy&gt;</c> (no taxiways named) may carry an aircraft to reach that
    /// runway's hold-short bar: a straight run along the taxiway it already occupies. Anything farther, or
    /// anything needing a turn onto another taxiway, is a route the controller has to give.
    /// </summary>
    public const double AdjacentRunwayHoldShortMaxFt = 600.0;

    /// <summary>
    /// Within this distance of a bar the aircraft is treated as standing at it — a scenario spawn or a
    /// creep-to-stop lands a few dozen feet short of, or just past, the painted line, and a bare
    /// <c>TAXI &lt;rwy&gt;</c> there means "hold where you are", not "back up to the bar".
    /// </summary>
    public const double AtRunwayHoldShortRadiusFt = 100.0;

    /// <summary>Slack beyond the runway's paved half-width before a position counts as on the runway.</summary>
    private const double RunwayPavementMarginFt = 25.0;

    /// <summary>
    /// Resolve a controller-specified taxi route from a sequence of taxiway names.
    /// Handles runway crossings, explicit hold-shorts, and variant resolution
    /// (e.g., W → W1 auto-extension when the destination runway is set).
    /// Returns null when the path cannot be resolved; <paramref name="failure"/> then carries the
    /// structured reason (kind, offending taxiway, human message) so a command handler can react to
    /// <em>why</em> — e.g. drop a gate-adjacent lead-out lane the graph cannot reach — instead of
    /// pattern-matching the message.
    /// </summary>
    public static TaxiRoute? ResolveExplicitPathDetailed(
        AirportGroundLayout layout,
        int fromNodeId,
        List<string> taxiwayNames,
        out PathfindingFailure? failure,
        ExplicitPathOptions options,
        AircraftCategory category
    )
    {
        // Route the resolved destination hint node through the channel that matches its node
        // TYPE. A spot ($) must resolve via FindSpotNodeByName (the destinationSpot channel), not
        // FindParkingByName (the destinationParking channel) — passing a spot's name as parking
        // resolves to null, leaving ctx.Destination.TargetNodeId null and defeating the
        // destination-aware terminus routing in SegmentExpander (the route then walks to the
        // taxiway terminus and U-turns back to the spot).
        var hint = options.DestinationHintNode;
        string? destParking = null;
        string? destSpot = null;
        int? destNodeId = null;
        switch (hint?.Type)
        {
            case GroundNodeType.Spot:
                destSpot = hint.Name;
                break;
            case GroundNodeType.Parking or GroundNodeType.Helipad:
                destParking = hint.Name;
                break;
            case not null:
                destNodeId = hint.Id;
                break;
        }

        var ctx = SearchContext.Compile(
            layout,
            fromNodeId,
            waypointSequence: taxiwayNames,
            destinationRunway: options.DestinationRunway,
            destinationParking: destParking,
            destinationSpot: destSpot,
            destinationNodeId: destNodeId,
            explicitHoldShorts: options.ExplicitHoldShorts,
            category: category,
            preference: null,
            diagnosticLog: options.DiagnosticLog,
            waypointTurnHints: options.PathTurnHints,
            startHeadingTrue: options.StartHeadingTrue
        );

        (var route, failure) = SegmentExpander.Run(ctx);
        return failure is null ? route : null;
    }

    /// <summary>
    /// Message-only form of <see cref="ResolveExplicitPathDetailed"/> for callers that just echo the
    /// failure; <paramref name="failReason"/> is the failure's human-readable message, or null on success.
    /// </summary>
    public static TaxiRoute? ResolveExplicitPath(
        AirportGroundLayout layout,
        int fromNodeId,
        List<string> taxiwayNames,
        out string? failReason,
        ExplicitPathOptions options,
        AircraftCategory category
    )
    {
        var route = ResolveExplicitPathDetailed(layout, fromNodeId, taxiwayNames, out PathfindingFailure? failure, options, category);
        failReason = failure?.HumanMessage;
        return route;
    }

    /// <summary>
    /// Find the single best route between two nodes using the FewestTurns strategy.
    /// Returns null when no route exists in the graph.
    /// </summary>
    public static TaxiRoute? FindRoute(AirportGroundLayout layout, int fromNodeId, int toNodeId, AircraftCategory category)
    {
        var ctx = SearchContext.Compile(
            layout,
            fromNodeId,
            waypointSequence: [],
            destinationRunway: null,
            destinationParking: null,
            destinationSpot: null,
            destinationNodeId: toNodeId,
            explicitHoldShorts: null,
            category: category,
            preference: RoutePreference.FewestTurns,
            diagnosticLog: null,
            waypointTurnHints: null,
            startHeadingTrue: null
        );

        var (route, _) = RunWithAvoidance(ctx);
        return route;
    }

    /// <summary>
    /// Find an auto-route from <paramref name="startNode"/> toward <paramref name="runwayId"/>,
    /// materialized as a runway destination so the route ends at the first destination hold-short
    /// encountered rather than crossing the runway to a far-side target node.
    /// </summary>
    public static TaxiRoute? FindRunwayRoute(AirportGroundLayout layout, GroundNode startNode, string runwayId, AircraftCategory category)
    {
        var holdShortNodes = layout.GetRunwayHoldShortNodes(runwayId);
        if (holdShortNodes.Count == 0)
        {
            return null;
        }

        var runwayContext = CompileRunwayDestinationContext(layout, startNode, runwayId, category);

        var reference = RouteMaterialiser.ResolveRunwayThreshold(layout.AirportId, runwayId) ?? startNode.Position;
        var candidates = holdShortNodes.OrderBy(n => GeoMath.DistanceNm(reference, n.Position)).ToList();
        TaxiRoute? fallbackRoute = null;

        foreach (var targetHs in candidates)
        {
            var routeToTarget = FindRoute(layout, startNode.Id, targetHs.Id, category);
            if (routeToTarget is null)
            {
                continue;
            }

            var route = RouteMaterialiser.Materialise(routeToTarget.Segments.Select(static s => s.Edge).ToList(), runwayContext, []);
            fallbackRoute ??= route;
            if ((EndsAtDestinationRunwayHoldShort(route, layout, runwayId)) && (!TraversesDestinationRunwaySurface(route, runwayId)))
            {
                return route;
            }
        }

        return fallbackRoute;
    }

    private static bool EndsAtDestinationRunwayHoldShort(TaxiRoute route, AirportGroundLayout layout, string runwayId)
    {
        if (route.Segments.Count == 0)
        {
            return false;
        }

        int finalNodeId = route.Segments[^1].ToNodeId;
        return (layout.Nodes.TryGetValue(finalNodeId, out var node))
            && (node.Type == GroundNodeType.RunwayHoldShort)
            && (node.RunwayId is { } nodeRunwayId)
            && (nodeRunwayId.Contains(runwayId))
            && (route.HoldShortPoints.Any(hs => (hs.NodeId == finalNodeId) && (hs.Reason == HoldShortReason.DestinationRunway)));
    }

    private static bool TraversesDestinationRunwaySurface(TaxiRoute route, string runwayId)
    {
        return route.Segments.Any(segment => (segment.Edge.Edge.IsRunwayCenterline) && (segment.Edge.Edge.MatchesRunway(runwayId)));
    }

    /// <summary>
    /// Route for a bare <c>TAXI &lt;rwy&gt;</c>: the aircraft is expected to already be at that runway, so the only
    /// acceptable destination is the runway's hold-short bar it is standing at (within
    /// <see cref="AtRunwayHoldShortRadiusFt"/> — a bar behind the aircraft counts only while it is still clear of
    /// the runway pavement), or one a short, turn-free run ahead on the taxiway it occupies — at most
    /// <see cref="AdjacentRunwayHoldShortMaxFt"/>, a single taxiway name, never across any runway, never behind
    /// the aircraft. Candidates are ordered by distance from the <em>aircraft</em>, unlike
    /// <see cref="FindRunwayRoute"/> (TAXIAUTO), which orders by distance from the threshold to prefer full length:
    /// here the clearance names no route, so the bar in front of the aircraft is the only one it can mean.
    /// Returns null when no bar qualifies; the caller rejects the command instead of guessing a route across the
    /// airport. When the aircraft is already at the bar the route has no segments and a single
    /// <see cref="HoldShortReason.DestinationRunway"/> point on that bar.
    /// </summary>
    public static TaxiRoute? FindAdjacentRunwayRoute(
        AirportGroundLayout layout,
        GroundNode startNode,
        (LatLon Position, TrueHeading Heading) aircraft,
        string runwayId,
        AircraftCategory category
    )
    {
        var (position, heading) = aircraft;
        var holdShortNodes = layout.GetRunwayHoldShortNodes(runwayId);
        double atBarNm = AtRunwayHoldShortRadiusFt / GeoMath.FeetPerNm;
        var atBar = holdShortNodes
            .Where(n => GeoMath.DistanceNm(position, n.Position) <= atBarNm)
            .Where(n => IsAhead(position, heading, n) || !IsOnRunwayPavement(layout, position, runwayId))
            .MinBy(n => GeoMath.DistanceNm(position, n.Position));
        if (atBar is not null)
        {
            var route = new TaxiRoute
            {
                Segments = [],
                HoldShortPoints =
                [
                    new HoldShortPoint
                    {
                        NodeId = atBar.Id,
                        Reason = HoldShortReason.DestinationRunway,
                        TargetName = runwayId,
                    },
                ],
                CurrentSegmentIndex = 0,
            };
            if (!IsAhead(position, heading, atBar))
            {
                route.Warnings.Add(
                    $"already past the {RunwayIdentifier.ToDisplayDesignator(runwayId)} hold-short line — holding in place, clear of the runway"
                );
            }

            return route;
        }

        var runwayContext = CompileRunwayDestinationContext(layout, startNode, runwayId, category);

        // The start node is the bar itself (the heading-aligned endpoint of the edge the aircraft is on)
        // while the aircraft is still short of it: route from the node behind the aircraft so the
        // navigator drives it up to the bar instead of a degenerate bar-to-bar route.
        if (holdShortNodes.Any(n => n.Id == startNode.Id))
        {
            var behind = startNode
                .Edges.Where(e => !e.IsRunwayCenterline)
                .Select(e => e.OtherNode(startNode))
                .Where(n => !IsAhead(position, heading, n))
                .MinBy(n => GeoMath.DistanceNm(position, n.Position));
            return behind is null ? null : TryAdjacentRoute(layout, runwayContext, behind.Id, startNode, runwayId, category);
        }

        double maxNm = AdjacentRunwayHoldShortMaxFt / GeoMath.FeetPerNm;
        var candidates = holdShortNodes
            .Where(n => GeoMath.DistanceNm(startNode.Position, n.Position) <= maxNm)
            .Where(n => IsAhead(startNode.Position, heading, n))
            .OrderBy(n => GeoMath.DistanceNm(startNode.Position, n.Position));

        foreach (var bar in candidates)
        {
            var route = TryAdjacentRoute(layout, runwayContext, startNode.Id, bar, runwayId, category);
            if (route is not null)
            {
                return route;
            }
        }

        return null;
    }

    private static TaxiRoute? TryAdjacentRoute(
        AirportGroundLayout layout,
        SearchContext runwayContext,
        int fromNodeId,
        GroundNode bar,
        string runwayId,
        AircraftCategory category
    )
    {
        var routeToBar = FindRoute(layout, fromNodeId, bar.Id, category);
        if (routeToBar is null)
        {
            return null;
        }

        var route = RouteMaterialiser.Materialise(routeToBar.Segments.Select(static s => s.Edge).ToList(), runwayContext, []);
        return IsAdjacentRunwayApproach(route, layout, runwayId) ? route : null;
    }

    private static bool IsAhead(LatLon from, TrueHeading heading, GroundNode node) =>
        GeoMath.AbsBearingDifference(GeoMath.BearingTo(from, node.Position), heading.Degrees) < 90.0;

    /// <summary>
    /// True when <paramref name="position"/> lies on the runway's paved surface (half its width plus a small
    /// margin from the centerline). A hold-short bar sits well outside this band, so an aircraft that crept
    /// past the painted line is still "at the bar" only while this is false.
    /// </summary>
    private static bool IsOnRunwayPavement(AirportGroundLayout layout, LatLon position, string runwayId)
    {
        var runway = layout.FindRunway(runwayId);
        if (runway is null || runway.Coordinates.Count < 2)
        {
            return false;
        }

        var start = runway.Coordinates[0];
        var end = runway.Coordinates[^1];
        var centerline = new TrueHeading(GeoMath.BearingTo(start.Lat, start.Lon, end.Lat, end.Lon));
        double crossTrackFt =
            Math.Abs(GeoMath.SignedCrossTrackDistanceNm(position.Lat, position.Lon, start.Lat, start.Lon, centerline)) * GeoMath.FeetPerNm;
        return crossTrackFt <= (runway.WidthFt / 2.0) + RunwayPavementMarginFt;
    }

    /// <summary>
    /// A short run to the bar is only "already at the runway" when it stays on one taxiway, is short, ends at the
    /// destination bar, and crosses nothing — a route carrying any other hold-short reaches the bar from the far
    /// side of another runway, which needs a crossing clearance the bare command cannot carry.
    /// </summary>
    private static bool IsAdjacentRunwayApproach(TaxiRoute route, AirportGroundLayout layout, string runwayId)
    {
        if (
            (route.Segments.Count == 0)
            || !EndsAtDestinationRunwayHoldShort(route, layout, runwayId)
            || TraversesDestinationRunwaySurface(route, runwayId)
        )
        {
            return false;
        }

        string taxiway = route.Segments[0].TaxiwayName;
        return route.Segments.All(s => string.Equals(s.TaxiwayName, taxiway, StringComparison.OrdinalIgnoreCase))
            && route.HoldShortPoints.All(h => h.Reason == HoldShortReason.DestinationRunway)
            && (route.TotalDistanceNm * GeoMath.FeetPerNm <= AdjacentRunwayHoldShortMaxFt);
    }

    private static SearchContext CompileRunwayDestinationContext(
        AirportGroundLayout layout,
        GroundNode startNode,
        string runwayId,
        AircraftCategory category
    )
    {
        return SearchContext.Compile(
            layout,
            startNode.Id,
            waypointSequence: [],
            destinationRunway: runwayId,
            destinationParking: null,
            destinationSpot: null,
            destinationNodeId: null,
            explicitHoldShorts: null,
            category: category,
            preference: RoutePreference.FewestTurns,
            diagnosticLog: null,
            waypointTurnHints: null,
            startHeadingTrue: null
        );
    }

    /// <summary>
    /// Find up to <paramref name="maxRoutes"/> distinct routes between two nodes.
    /// When <paramref name="preference"/> is null, all three strategies
    /// (FewestTurns, Shortest, Fastest) are evaluated and results merged.
    /// Pass null for <paramref name="authorizedTaxiways"/> to allow all taxiways.
    /// </summary>
    /// <remarks>
    /// The pathfinder is intentionally <b>per-preference</b>, not a Yen-style k-shortest generator. With no
    /// preference it runs one search for each of <see cref="RoutePreference.FewestTurns"/>
    /// / <see cref="RoutePreference.Shortest"/> / <see cref="RoutePreference.Fastest"/> and returns the
    /// deduplicated results — at most 3 routes, regardless of <paramref name="maxRoutes"/>. Three distinct
    /// strategies are more useful to a controller than a set of near-identical Yen detours, so callers
    /// should request ≤3 (see <c>GroundViewModel.FindRoutesToNode</c>).
    /// </remarks>
    public static List<TaxiRoute> FindRoutes(
        AirportGroundLayout layout,
        int fromNodeId,
        int toNodeId,
        RoutePreference? preference,
        int maxRoutes,
        IReadOnlySet<string>? authorizedTaxiways,
        AircraftCategory category
    )
    {
        if (preference is not null)
        {
            var ctx = BuildNodeContext(layout, fromNodeId, toNodeId, preference.Value, authorizedTaxiways, category);
            var (route, _) = RunWithAvoidance(ctx);
            return route is not null ? [route] : [];
        }

        // No preference — run all three strategies and return unique routes capped at maxRoutes.
        var preferences = new[] { RoutePreference.FewestTurns, RoutePreference.Shortest, RoutePreference.Fastest };
        var results = new List<TaxiRoute>(preferences.Length);

        foreach (var pref in preferences)
        {
            if (results.Count >= maxRoutes)
            {
                break;
            }

            var ctx = BuildNodeContext(layout, fromNodeId, toNodeId, pref, authorizedTaxiways, category);
            var (route, _) = RunWithAvoidance(ctx);

            if (route is null)
            {
                continue;
            }

            if (!IsDuplicateRoute(route, results))
            {
                results.Add(route);
            }
        }

        return results;
    }

    /// <summary>
    /// Pick the canonical full-length lineup hold-short for a runway designator —
    /// the hold-short geographically closest to the runway threshold.
    /// Falls back to the hold-short nearest <paramref name="startNode"/> when
    /// the runway is unknown to <see cref="NavigationDatabase"/>.
    /// </summary>
    public static GroundNode FindFullLengthLineupHoldShort(
        AirportGroundLayout layout,
        GroundNode startNode,
        string runwayId,
        List<GroundNode> holdShortNodes
    )
    {
        return RouteMaterialiser.FindFullLengthLineupHoldShort(layout, startNode, runwayId, holdShortNodes);
    }

    /// <summary>
    /// Runs the A* auto-router with two-pass hard-gate semantics. Pass 1 hard-excludes avoided taxiways
    /// (<see cref="AvoidTaxiwayMode.HardExclude"/>) and one-way wrong-way moves
    /// (<see cref="OneWayMode.HardExclude"/>). Only if pass 1 finds no route does pass 2 relax those hard
    /// gates — avoided taxiways become a heavy soft penalty, one-way wrong-way moves become permitted but
    /// warned — so a destination reachable only through an avoided taxiway or against a one-way still
    /// resolves while deviating minimally. With neither hard gate active this is a single, unchanged search.
    /// </summary>
    private static (TaxiRoute? Route, PathfindingFailure? Failure) RunWithAvoidance(SearchContext ctx)
    {
        bool hardAvoid = ctx.AvoidMode == AvoidTaxiwayMode.HardExclude;
        bool hardOneWay = ctx.OneWayMode == OneWayMode.HardExclude;
        if (!hardAvoid && !hardOneWay)
        {
            return AutoRouter.Run(ctx);
        }

        var pass1 = AutoRouter.Run(ctx);
        if (pass1.Route is not null)
        {
            return pass1;
        }

        ctx.DiagnosticLog?.Invoke("[avoid/one-way] pass 1 (hard-exclude) found no route; retrying with gates relaxed");
        var relaxed = ctx;
        if (hardAvoid)
        {
            relaxed = relaxed with { AvoidMode = AvoidTaxiwayMode.SoftPenalty };
        }

        if (hardOneWay)
        {
            relaxed = relaxed with { OneWayMode = OneWayMode.Warn };
        }

        return AutoRouter.Run(relaxed);
    }

    private static SearchContext BuildNodeContext(
        AirportGroundLayout layout,
        int fromNodeId,
        int toNodeId,
        RoutePreference preference,
        IReadOnlySet<string>? authorizedTaxiways,
        AircraftCategory category
    )
    {
        var ctx = SearchContext.Compile(
            layout,
            fromNodeId,
            waypointSequence: [],
            destinationRunway: null,
            destinationParking: null,
            destinationSpot: null,
            destinationNodeId: toNodeId,
            explicitHoldShorts: null,
            category: category,
            preference: preference,
            diagnosticLog: null,
            waypointTurnHints: null,
            startHeadingTrue: null
        );

        // authorizedTaxiways is not part of SearchContext.Compile's signature; use with-expression
        // to override the null from Compile (which returns null for auto-route anyway, but honour
        // the caller's explicit set when provided).
        if (authorizedTaxiways is not null)
        {
            return ctx with { AuthorizedTaxiways = authorizedTaxiways };
        }

        return ctx;
    }

    /// <summary>
    /// Returns true when <paramref name="candidate"/> has the same segment node sequence
    /// as any route already in <paramref name="existing"/>.
    /// </summary>
    private static bool IsDuplicateRoute(TaxiRoute candidate, List<TaxiRoute> existing)
    {
        foreach (var other in existing)
        {
            if (SegmentsIdentical(candidate, other))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SegmentsIdentical(TaxiRoute a, TaxiRoute b)
    {
        if (a.Segments.Count != b.Segments.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Segments.Count; i++)
        {
            if ((a.Segments[i].FromNodeId != b.Segments[i].FromNodeId) || (a.Segments[i].ToNodeId != b.Segments[i].ToNodeId))
            {
                return false;
            }
        }

        return true;
    }
}
