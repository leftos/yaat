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
/// Optional routing hints and diagnostics for <see cref="TaxiPathfinder.ResolveExplicitPath"/>.
/// </summary>
public sealed class ExplicitPathOptions
{
    public List<string>? ExplicitHoldShorts { get; init; }
    public string? DestinationRunway { get; init; }
    public string? AirportId { get; init; }
    public GroundNode? DestinationHintNode { get; init; }
    public Action<string>? DiagnosticLog { get; init; }

    /// <summary>
    /// Optional per-taxiway turn-direction hints (issue #172 W7), index-aligned with the
    /// <c>taxiwayNames</c> sequence: entry <c>i</c> is the turn the aircraft should make onto
    /// taxiway <c>i</c> (null = no hint). When null no token carries a hint. The hint only biases
    /// junction selection toward the matching turn; it never overrides an otherwise-required route.
    /// </summary>
    public IReadOnlyList<TurnDirection?>? PathTurnHints { get; init; }

    /// <summary>
    /// The aircraft's current true heading in degrees, used as the turn reference for a hint on the
    /// <em>first</em> taxiway ("right onto A" = the direction along A that is a right turn from here).
    /// Null disables first-taxiway start-direction biasing (mid-route hints use the route's own
    /// arrival bearing and do not need it).
    /// </summary>
    public double? StartHeadingTrue { get; init; }
}
