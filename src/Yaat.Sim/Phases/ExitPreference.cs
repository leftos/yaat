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

public sealed class ResolvedExitInfo
{
    public required GroundNode HoldShortNode { get; init; }
    public required string TaxiwayName { get; init; }
    public required double TurnOffSpeed { get; init; }
    public required List<GroundNode> Path { get; init; }
}
