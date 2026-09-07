namespace Yaat.Sim.Simulation.Snapshots;

/// <summary>
/// Engine-level state outside the aircraft list and the scenario: consolidation overrides, conflict alerts, the
/// beacon code pool, the per-connection position selections, the attended CRC positions, and the flight strips
/// and vTDLS session.
/// </summary>
public sealed class ServerSnapshotDto
{
    public Dictionary<string, ConsolidationOverrideDto>? ConsolidationOverrides { get; init; }
    public List<ActiveConflictDto>? ActiveConflicts { get; init; }
    public List<EramActiveConflictDto>? EramConflicts { get; init; }
    public BeaconCodePoolDto? BeaconCodePool { get; init; }

    /// <summary>Connection id → the position it selected with a bare <c>AS</c>. Absent in pre-feature snapshots (restores empty).</summary>
    public Dictionary<string, TrackOwnerDto>? PositionSelections { get; init; }

    /// <summary>The vNAS position ids a CRC client was working. Absent in pre-feature snapshots (restores empty).</summary>
    public List<string>? AttendedPositionIds { get; init; }

    /// <summary>The flight strips, their bays and the printer queues. Absent in pre-feature snapshots (restores empty).</summary>
    public FlightStripSnapshotDto? Strips { get; init; }

    /// <summary>The vTDLS session: items, dumped lockout, active ops configs, pending auto-WILCOs. Absent in pre-feature snapshots (restores empty).</summary>
    public TdlsSnapshotDto? Tdls { get; init; }
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

public sealed class EramActiveConflictDto
{
    public required string Id { get; init; }
    public required string CallsignA { get; init; }
    public required string CallsignB { get; init; }
    public string? OwnerFacilityA { get; init; }
    public string? OwnerFacilityB { get; init; }
}

public sealed class BeaconCodePoolDto
{
    public Dictionary<uint, string>? AssignedCodes { get; init; }

    /// <summary>Sequential-fallback cursor (used when no banks are configured). 0 ⇒ restore to default 0001.</summary>
    public uint NextCandidate { get; init; }

    /// <summary>Per-bank draw cursors, keyed by the deterministic bank key (Start*10000+End).</summary>
    public Dictionary<int, uint>? BankCursors { get; init; }
}
