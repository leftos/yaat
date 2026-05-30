namespace Yaat.Sim.Data.Airport;

using Yaat.Sim.Data.Airport.V2;

/// <summary>
/// v2 pathfinder implementation. Auto-route methods (<see cref="FindRoute"/>, <see cref="FindRoutes"/>)
/// are implemented with the A* <see cref="AutoRouter"/>. <see cref="ResolveExplicitPath"/> is
/// implemented with <see cref="SegmentExpander"/>.
/// </summary>
public sealed class TaxiPathfinderV2 : ITaxiPathfinder
{
    /// <inheritdoc/>
    public TaxiRoute? ResolveExplicitPath(
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
            diagnosticLog: options.DiagnosticLog
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

    /// <inheritdoc/>
    public TaxiRoute? FindRoute(AirportGroundLayout layout, int fromNodeId, int toNodeId, AircraftCategory category)
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
            diagnosticLog: null
        );

        var (route, _) = AutoRouter.Run(ctx);
        return route;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// V2 is intentionally <b>per-preference</b>, not a Yen-style k-shortest generator (V1 was the
    /// latter). With no preference it runs one search for each of <see cref="RoutePreference.FewestTurns"/>
    /// / <see cref="RoutePreference.Shortest"/> / <see cref="RoutePreference.Fastest"/> and returns the
    /// deduplicated results — at most 3 routes, regardless of <paramref name="maxRoutes"/>. Three distinct
    /// strategies are more useful to a controller than a set of near-identical Yen detours, so callers
    /// should request ≤3 (see <c>GroundViewModel.FindRoutesToNode</c>). Decision recorded in
    /// <c>docs/plans/pathfinderv2/codex-review.md</c>.
    /// </remarks>
    public List<TaxiRoute> FindRoutes(
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
            var (route, _) = AutoRouter.Run(ctx);
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
            var (route, _) = AutoRouter.Run(ctx);

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

    /// <inheritdoc/>
    public GroundNode FindFullLengthLineupHoldShort(
        AirportGroundLayout layout,
        GroundNode startNode,
        string runwayId,
        List<GroundNode> holdShortNodes
    )
    {
        return RouteMaterialiser.FindFullLengthLineupHoldShort(layout, startNode, runwayId, holdShortNodes);
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
            diagnosticLog: null
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
