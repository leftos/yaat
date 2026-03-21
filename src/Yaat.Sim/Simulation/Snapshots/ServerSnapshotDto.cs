namespace Yaat.Sim.Simulation.Snapshots;

/// <summary>
/// Server-side state not owned by SimulationEngine but needed for full restore.
/// Consolidation overrides, conflict alerts, and beacon code pool.
/// </summary>
public sealed class ServerSnapshotDto
{
    public Dictionary<string, ConsolidationOverrideDto>? ConsolidationOverrides { get; init; }
    public List<ActiveConflictDto>? ActiveConflicts { get; init; }
    public BeaconCodePoolDto? BeaconCodePool { get; init; }
}

public sealed class ConsolidationOverrideDto
{
    public required string ReceivingTcpId { get; init; }
    public required bool IsBasic { get; init; }
}

public sealed class ActiveConflictDto
{
    public required string Id { get; init; }
    public required string CallsignA { get; init; }
    public required string CallsignB { get; init; }
    public required bool IsAcknowledged { get; init; }
}

public sealed class BeaconCodePoolDto
{
    public Dictionary<uint, string>? AssignedCodes { get; init; }
}
