namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Thin adapter that implements <see cref="ITaxiPathfinder"/> by delegating to
/// the existing static <see cref="TaxiPathfinder"/> methods. No behavior change.
/// </summary>
public sealed class TaxiPathfinderV1Adapter : ITaxiPathfinder
{
    /// <inheritdoc/>
    public TaxiRoute? ResolveExplicitPath(
        AirportGroundLayout layout,
        int fromNodeId,
        List<string> taxiwayNames,
        out string? failReason,
        ExplicitPathOptions options
    )
    {
        return TaxiPathfinder.ResolveExplicitPath(layout, fromNodeId, taxiwayNames, out failReason, options);
    }

    /// <inheritdoc/>
    public TaxiRoute? FindRoute(AirportGroundLayout layout, int fromNodeId, int toNodeId)
    {
        return TaxiPathfinder.FindRoute(layout, fromNodeId, toNodeId);
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
        return TaxiPathfinder.FindRoutes(layout, fromNodeId, toNodeId, preference, maxRoutes, authorizedTaxiways);
    }

    /// <inheritdoc/>
    public GroundNode FindFullLengthLineupHoldShort(
        AirportGroundLayout layout,
        GroundNode startNode,
        string runwayId,
        List<GroundNode> holdShortNodes
    )
    {
        return TaxiPathfinder.FindFullLengthLineupHoldShort(layout, startNode, runwayId, holdShortNodes);
    }
}
