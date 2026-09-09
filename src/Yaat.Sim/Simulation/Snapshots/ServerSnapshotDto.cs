using Yaat.Sim.Simulation.Eram;

namespace Yaat.Sim.Simulation.Snapshots;

/// <summary>
/// Engine-level state outside the aircraft list and the scenario: consolidation overrides, conflict alerts, the
/// beacon code pool, the per-connection position selections, the attended CRC positions, the flight strips
/// and vTDLS session, the tower lists' dwell entries and the ERAM CRR groups.
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

    /// <summary>Each STARS P-list's aircraft with the second they came into range at. Absent in pre-feature snapshots (restores empty).</summary>
    public TowerListSnapshotDto? TowerLists { get; init; }

    /// <summary>
    /// The ERAM Continuous Range Readout groups, in label order so two passes of the same run capture the same bytes.
    /// Absent when the session holds none, which is also what a pre-feature snapshot looks like (restores empty).
    /// </summary>
    public List<EramCrrGroupSnapshotDto>? CrrGroups { get; init; }
}

/// <summary>One CRR group: its label, colour and location. Membership is the aircraft's, so none of it is here.</summary>
public sealed class EramCrrGroupSnapshotDto
{
    public required string Label { get; init; }
    public required EramCrrColor Color { get; init; }
    public required double Latitude { get; init; }
    public required double Longitude { get; init; }
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
