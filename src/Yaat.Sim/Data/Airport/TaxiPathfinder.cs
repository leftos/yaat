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
    /// Resolve a controller-specified taxi route from a sequence of taxiway names.
    /// Handles runway crossings, explicit hold-shorts, and variant resolution
    /// (e.g., W → W1 auto-extension when the destination runway is set).
    /// Returns null when the path cannot be resolved; <paramref name="failReason"/>
    /// is set to a human-readable explanation in that case.
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
            explicitHoldShortRunways: options.ExplicitHoldShorts,
            category: category,
            preference: null,
            diagnosticLog: options.DiagnosticLog,
            waypointTurnHints: options.PathTurnHints,
            startHeadingTrue: options.StartHeadingTrue
        );

        var (route, failure) = SegmentExpander.Run(ctx);

        if (failure is not null)
        {
            failReason = failure.HumanMessage;
            return null;
        }

        failReason = null;
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
            explicitHoldShortRunways: null,
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
            explicitHoldShortRunways: null,
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
