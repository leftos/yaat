using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Phases;

public enum ExitSide
{
    Left,
    Right,
}

public sealed class ExitPreference
{
    public ExitSide? Side { get; init; }
    public string? Taxiway { get; init; }
}

/// <summary>
/// Whether a late <c>EL</c>/<c>ER</c>/<c>EXIT</c> can still change the exit an aircraft is committed to.
/// <paramref name="UnableReason"/> is the controller-facing refusal text, non-null exactly when
/// <paramref name="Allowed"/> is false. Produced by <c>RunwayExitPhase.EvaluateRetarget</c>.
/// </summary>
public readonly record struct ExitRetargetVerdict(bool Allowed, string? UnableReason);

/// <summary>
/// Fully resolved exit: hold-short node, branch point on the centerline,
/// ordered path of intermediate nodes, taxiway name, and turn-off speed.
/// Produced by LandingPhase's continuous evaluation; consumed by RunwayExitPhase.
/// </summary>
public sealed class ResolvedExitInfo
{
    public required GroundNode HoldShortNode { get; init; }
    public required string TaxiwayName { get; init; }
    public required double TurnOffSpeed { get; init; }
    public required List<GroundNode> Path { get; init; }
    public required GroundNode BranchPointNode { get; init; }

    /// <summary>
    /// The braking rate (kts/sec) the reachability filter allowed when it chose this exit — the class-based default
    /// rate, the firm rate, the firm-braking fallback's rate, or the max-effort rate under <c>EXP</c>. Null when the
    /// straight-line search chose it (no braking filter) or when it was restored from a snapshot that did not carry it.
    /// </summary>
    public required double? SelectionDecelRate { get; init; }
}
