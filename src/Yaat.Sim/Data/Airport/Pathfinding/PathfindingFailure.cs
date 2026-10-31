namespace Yaat.Sim.Data.Airport.Pathfinding;

/// <summary>
/// Classifies the reason a pathfinding call produced no route.
/// </summary>
public enum FailureKind
{
    StartNodeUnreachable,
    TaxiwayNotConnected,
    TransitionInfeasible,
    TransitionAmbiguous,
    DestinationUnreachable,
    SearchExhausted,

    /// <summary>
    /// Two named taxiways meet neither directly nor across a runway as a crossing, and every connector that would join them
    /// runs along a runway, turns at one, or needs a lettered taxiway the clearance left out: the clearance is refused as issued.
    /// </summary>
    TaxiwaysDoNotConnect,

    /// <summary>
    /// Two named taxiways meet, but nowhere the route can reach ahead of the aircraft: every junction lies behind it or beyond
    /// where its walk along the first taxiway ends, and no legal connector bridges them. Refused as issued, like
    /// <see cref="TaxiwaysDoNotConnect"/>.
    /// </summary>
    NoRouteAhead,

    /// <summary>
    /// Two named taxiways meet across a runway as a crossing, but the turn from the runway onto the second exceeds the
    /// aircraft's turn limit (KCOS A2 onto C2, a 150° reversal onto a high-speed exit, for a B738). Refused as issued, like
    /// <see cref="TaxiwaysDoNotConnect"/>.
    /// </summary>
    CannotMakeTurn,

    /// <summary>
    /// A bare clearance crosses a runway onto a taxiway with no straight-ahead point where the whole aircraft clears the
    /// runway's holding position (tail and nose both <see cref="Phases.Ground.RunwayTailClearance.MarginFt"/> past the
    /// far hold line, measured square to it) before a T, a runway centreline or another runway's bar. Refused as issued, like
    /// <see cref="TaxiwaysDoNotConnect"/>.
    /// </summary>
    NoRoomToClearRunway,

    /// <summary>
    /// Two named taxiways meet across a runway as a crossing, but the line of the first meets the line of the second past the
    /// second's hold bar, so the aircraft cannot cross straight over and land on the second short of it. Whatever the aircraft,
    /// refused as issued, like <see cref="TaxiwaysDoNotConnect"/>.
    /// </summary>
    NoRoomToCrossStraight,
}

/// <summary>
/// Structured failure returned when the pathfinder exhausts the search.
/// </summary>
public sealed record PathfindingFailure(
    FailureKind Kind,
    string HumanMessage,
    string? InfeasibleTaxiway,
    string? InfeasibleTransition,
    string? SuggestedAlternative
)
{
    /// <summary>The first of the two named taxiways a pair refusal (<see cref="RefusesPair"/>) names; null otherwise.</summary>
    public string? UnconnectedFrom { get; init; }

    /// <summary>The second of the two named taxiways a pair refusal (<see cref="RefusesPair"/>) names; null otherwise.</summary>
    public string? UnconnectedTo { get; init; }

    /// <summary>
    /// For <see cref="FailureKind.NoRouteAhead"/>: every junction of the pair lies directly behind the aircraft, so reaching one
    /// would take a turn-about. False for every other failure.
    /// </summary>
    public bool IntersectionOnlyBehind { get; init; }

    /// <summary>
    /// For <see cref="FailureKind.NoRoomToClearRunway"/>: the runway crossed, as the controller reads it ("17R/35L"). Null for
    /// every other failure.
    /// </summary>
    public string? UnclearedRunway { get; init; }

    /// <summary>
    /// Whether this failure refuses the clearance as issued for its pair of named taxiways
    /// (<see cref="FailureKind.TaxiwaysDoNotConnect"/>, <see cref="FailureKind.NoRouteAhead"/>,
    /// <see cref="FailureKind.CannotMakeTurn"/>, <see cref="FailureKind.NoRoomToClearRunway"/>,
    /// <see cref="FailureKind.NoRoomToCrossStraight"/>): no recovery may rewrite it into a route the clearance never named, and
    /// the aircraft stays put.
    /// </summary>
    public bool RefusesPair =>
        Kind
            is FailureKind.TaxiwaysDoNotConnect
                or FailureKind.NoRouteAhead
                or FailureKind.CannotMakeTurn
                or FailureKind.NoRoomToClearRunway
                or FailureKind.NoRoomToCrossStraight;
}

/// <summary>
/// Result of resolving a base taxiway letter to a numbered variant.
/// </summary>
public sealed record VariantResolutionResult(bool IsUnambiguous, string? ResolvedVariant, IReadOnlyList<string> Candidates);
