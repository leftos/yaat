using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases;

public sealed class ClearanceRequirement
{
    public required ClearanceType Type { get; init; }
    public bool IsSatisfied { get; set; }

    public ClearanceRequirementDto ToSnapshot() => new() { Type = (int)Type, IsSatisfied = IsSatisfied };

    public static ClearanceRequirement FromSnapshot(ClearanceRequirementDto dto) =>
        new() { Type = (ClearanceType)dto.Type, IsSatisfied = dto.IsSatisfied };
}
