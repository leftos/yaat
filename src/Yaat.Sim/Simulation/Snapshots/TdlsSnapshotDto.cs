using Yaat.Sim.Simulation.Tdls;

namespace Yaat.Sim.Simulation.Snapshots;

/// <summary>
/// The engine's vTDLS session state: the items, the dumped lockout, the active ops configs, the pending
/// auto-WILCOs and the id counter. The per-facility <c>TdlsConfig</c> map is deliberately out — a load
/// re-derives it from the ARTCC before the restore runs. Absent in pre-feature snapshots, which restore an
/// empty session.
/// </summary>
public sealed class TdlsSnapshotDto
{
    public required List<TdlsItemSnapshotDto> Items { get; init; }
    public required List<TdlsDumpedSnapshotDto> Dumped { get; init; }
    public required int NextItemId { get; init; }

    /// <summary>Active operational configuration id per facility. Additive — older snapshots restore as empty, which resolves to each facility's first config.</summary>
    public List<TdlsActiveOpConfigSnapshotDto> ActiveOpConfigs { get; init; } = [];

    /// <summary>Sent items awaiting their auto-WILCO, so a restored run acknowledges at the sim second the live one did.</summary>
    public List<TdlsScheduledWilcoDto> ScheduledWilco { get; init; } = [];
}

public sealed class TdlsActiveOpConfigSnapshotDto
{
    public required string FacilityId { get; init; }
    public required string OpConfigId { get; init; }
}

public sealed class TdlsItemSnapshotDto
{
    public required string Id { get; init; }
    public required string AircraftId { get; init; }
    public string? Cid { get; init; }
    public required string FacilityId { get; init; }

    /// <summary>The <see cref="TdlsItemStatus"/> ordinal.</summary>
    public required int Status { get; init; }
    public required int Sequence { get; init; }
    public required DateTime CreatedUtc { get; init; }
    public DateTime? SentUtc { get; init; }
    public DateTime? WilcoUtc { get; init; }
    public required DateTime ExpiresUtc { get; init; }
    public TdlsClearance? SentPayload { get; init; }
}

public sealed class TdlsDumpedSnapshotDto
{
    public required string FacilityId { get; init; }
    public required string Callsign { get; init; }
}

public sealed class TdlsScheduledWilcoDto
{
    public required string ItemId { get; init; }
    public required DateTime DueUtc { get; init; }
}
