namespace Yaat.Sim.Data.Airport;

using Yaat.Sim.Data.Airport.V2;

/// <summary>
/// v2 pathfinder implementation. Auto-route methods (<see cref="FindRoute"/>, <see cref="FindRoutes"/>)
/// are implemented with the A* <see cref="AutoRouter"/>. <see cref="ResolveExplicitPath"/> still
/// delegates to <see cref="TaxiPathfinderV1Adapter"/> — SegmentExpander is step 6.
/// </summary>
public sealed class TaxiPathfinderV2 : ITaxiPathfinder
{
    private static readonly TaxiPathfinderV1Adapter _v1 = new();

    /// <inheritdoc/>
    public TaxiRoute? ResolveExplicitPath(
        AirportGroundLayout layout,
        int fromNodeId,
        List<string> taxiwayNames,
        out string? failReason,
        ExplicitPathOptions options
    )
    {
        return _v1.ResolveExplicitPath(layout, fromNodeId, taxiwayNames, out failReason, options);
    }

    /// <inheritdoc/>
    public TaxiRoute? FindRoute(AirportGroundLayout layout, int fromNodeId, int toNodeId)
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
            category: AircraftCategory.Jet,
            preference: RoutePreference.FewestTurns,
            diagnosticLog: null
        );

        var (route, _) = AutoRouter.Run(ctx);
        return route;
    }

    /// <inheritdoc/>
    public List<TaxiRoute> FindRoutes(
        AirportGroundLayout layout,
        int fromNodeId,
        int toNodeId,
        RoutePreference? preference,
        int maxRoutes,
        IReadOnlySet<string>? authorizedTaxiways
    )
    {
        if (preference is not null)
        {
            var ctx = BuildNodeContext(layout, fromNodeId, toNodeId, preference.Value, authorizedTaxiways);
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

            var ctx = BuildNodeContext(layout, fromNodeId, toNodeId, pref, authorizedTaxiways);
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
        IReadOnlySet<string>? authorizedTaxiways
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
            category: AircraftCategory.Jet,
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
