using Yaat.Sim.Data.Airport;
using Yaat.Sim.Pilot;

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
/// Whether an exit instruction (<c>EL</c>/<c>ER</c>/<c>EXIT</c>) can be accepted for the exit an aircraft is braking for or
/// committed to. <paramref name="UnableReason"/> is the controller-facing refusal text, non-null exactly when
/// <paramref name="Allowed"/> is false. Produced by <c>LandingPhase.EvaluateAndApplyNamedExitInstruction</c> (a named exit on the rollout) and
/// <c>RunwayExitPhase.EvaluateRetarget</c> (a late change once the exit route is handed to the navigator).
/// </summary>
public readonly record struct ExitInstructionVerdict(bool Allowed, string? UnableReason)
{
    /// <summary>
    /// The pilot's own words for the refusal, when it has them — the crew's "unable" for a named exit it cannot make
    /// (<see cref="PilotResponder.BuildUnableToExit"/>) or that is not ahead (<see cref="PilotResponder.BuildUnableNoExitAhead"/>),
    /// whose terminal line is what <see cref="PilotResponder.BuildUnable"/> would make of <see cref="UnableReason"/>; null when
    /// that generic form applies.
    /// </summary>
    public PilotSpeechText? PilotUnable { get; init; }
}

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
    /// The side of the runway the exit search found this exit on (its bar seen from the centerline node the walk reached it from).
    /// Null when the straight-line search chose it (no graph) or when it was restored from a snapshot that did not carry it.
    /// </summary>
    public required ExitSide? Side { get; init; }

    /// <summary>
    /// The braking rate (kts/sec) the reachability filter allowed when it chose this exit — the class-based default
    /// rate, the firm rate, the firm-braking fallback's rate, or the max-effort rate under <c>EXP</c>. Null when the
    /// straight-line search chose it (no braking filter) or when it was restored from a snapshot that did not carry it.
    /// </summary>
    public required double? SelectionDecelRate { get; init; }
}
