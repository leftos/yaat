namespace Yaat.Sim.Phases;

public sealed class ClearanceRequirement
{
    public required ClearanceType Type { get; init; }
    public bool IsSatisfied { get; set; }
}
