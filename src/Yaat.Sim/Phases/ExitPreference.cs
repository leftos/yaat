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
}
