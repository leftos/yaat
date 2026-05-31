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
);

/// <summary>
/// Result of resolving a base taxiway letter to a numbered variant.
/// </summary>
public sealed record VariantResolutionResult(bool IsUnambiguous, string? ResolvedVariant, IReadOnlyList<string> Candidates);
