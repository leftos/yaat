// TODO(v2): replace with cleanroom rewrite per docs/plans/taxi-pathfinder-v2.md

namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Placeholder for the v2 pathfinder. All methods delegate to
/// <see cref="TaxiPathfinderV1Adapter"/> so the comparison harness runs cleanly
/// while the cleanroom rewrite is in progress.
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
        return _v1.FindRoute(layout, fromNodeId, toNodeId);
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
        return _v1.FindRoutes(layout, fromNodeId, toNodeId, preference, maxRoutes, authorizedTaxiways);
    }

    /// <inheritdoc/>
    public GroundNode FindFullLengthLineupHoldShort(
        AirportGroundLayout layout,
        GroundNode startNode,
        string runwayId,
        List<GroundNode> holdShortNodes
    )
    {
        return _v1.FindFullLengthLineupHoldShort(layout, startNode, runwayId, holdShortNodes);
    }
}
