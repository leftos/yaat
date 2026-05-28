namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Public contract for taxi pathfinding on an airport ground layout graph.
/// Covers the entry points called from production code. Internal helpers
/// (WalkTaxiway, BridgeToTaxiway, SelectBestStopNode, etc.) are implementation
/// details of the concrete class and are not part of this contract.
/// </summary>
public interface ITaxiPathfinder
{
    /// <summary>
    /// Resolve a controller-specified taxi route from a sequence of taxiway names.
    /// Handles runway crossings, explicit hold-shorts, and variant resolution
    /// (e.g., W → W1 auto-extension when the destination runway is set).
    /// Returns null when the path cannot be resolved; <paramref name="failReason"/>
    /// is set to a human-readable explanation in that case.
    /// </summary>
    TaxiRoute? ResolveExplicitPath(
        AirportGroundLayout layout,
        int fromNodeId,
        List<string> taxiwayNames,
        out string? failReason,
        ExplicitPathOptions options,
        AircraftCategory category
    );

    /// <summary>
    /// Find the single best route between two nodes using the FewestTurns strategy.
    /// Returns null when no route exists in the graph.
    /// </summary>
    TaxiRoute? FindRoute(AirportGroundLayout layout, int fromNodeId, int toNodeId, AircraftCategory category);

    /// <summary>
    /// Find up to <paramref name="maxRoutes"/> distinct routes between two nodes.
    /// When <paramref name="preference"/> is null, all three strategies
    /// (FewestTurns, Shortest, Fastest) are evaluated and results merged.
    /// Pass null for <paramref name="authorizedTaxiways"/> to allow all taxiways.
    /// </summary>
    List<TaxiRoute> FindRoutes(
        AirportGroundLayout layout,
        int fromNodeId,
        int toNodeId,
        RoutePreference? preference,
        int maxRoutes,
        IReadOnlySet<string>? authorizedTaxiways,
        AircraftCategory category
    );

    /// <summary>
    /// Pick the canonical full-length lineup hold-short for a runway designator —
    /// the hold-short geographically closest to the runway threshold.
    /// Falls back to the hold-short nearest <paramref name="startNode"/> when
    /// the runway is unknown to <see cref="NavigationDatabase"/>.
    /// </summary>
    GroundNode FindFullLengthLineupHoldShort(AirportGroundLayout layout, GroundNode startNode, string runwayId, List<GroundNode> holdShortNodes);
}
