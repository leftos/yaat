namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Route preference for A* pathfinding. Each strategy uses a different cost function.
/// When null, all three strategies are evaluated and results are merged.
/// </summary>
public enum RoutePreference
{
    /// <summary>Minimize taxiway transitions (fewest differently-named taxiways).</summary>
    FewestTurns,

    /// <summary>Minimize total distance in nautical miles.</summary>
    Shortest,

    /// <summary>Minimize estimated travel time (accounts for arc speed limits).</summary>
    Fastest,
}

/// <summary>
/// Optional routing hints and diagnostics for <see cref="TaxiPathfinderV2.ResolveExplicitPath"/>.
/// </summary>
public sealed class ExplicitPathOptions
{
    public List<string>? ExplicitHoldShorts { get; init; }
    public string? DestinationRunway { get; init; }
    public string? AirportId { get; init; }
    public GroundNode? DestinationHintNode { get; init; }
    public Action<string>? DiagnosticLog { get; init; }
}
