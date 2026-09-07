using System.Collections.Concurrent;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Simulation.Tdls;

/// <summary>
/// The vTDLS state of one run: the DCL (Pending) and PDC (Sent / Wilco) lists plus a "Dumped" lockout that keeps
/// entries from being re-created after the controller has explicitly removed them. Engine-owned
/// (<see cref="SimulationEngine.Tdls"/>) — a fresh engine starts empty and the snapshot's server section carries
/// the session state, so every run kind holds the same items at the same second.
/// <para>
/// Mutations funnel through the host's <c>TdlsMutations</c> under the single <see cref="Gate"/> lock.
/// Per-facility configuration (<see cref="Configs"/>) is the exception: it is loaded from the ARTCC at scenario
/// load and is NOT snapshotted — <see cref="ClearSession"/> is what a restore clears, and it leaves the configs
/// the load just derived in place.
/// </para>
/// </summary>
public sealed class TdlsState
{
    public object Gate { get; } = new();

    /// <summary>All TDLS items, keyed by item id (<c>"TDLS_{n}"</c>). DCL = Status==Pending; PDC = Status==Sent|Wilco.</summary>
    public ConcurrentDictionary<string, TdlsItemRecord> Items { get; } = new();

    /// <summary>Per-facility TDLS configuration (keyed by FacilityId) — what SIDs/transitions/values the FE has defined.</summary>
    public ConcurrentDictionary<string, TdlsConfig> Configs { get; } = new();

    /// <summary>
    /// Explicitly selected operational configuration id per facility (keyed by FacilityId). Shared
    /// room state rather than a per-controller preference: the active config decides which SIDs and
    /// which per-transition defaults a PDC is built from, so every vTDLS window in the room has to
    /// agree. Read through <see cref="ResolveActiveOpConfigId"/>, which fills in the default.
    /// </summary>
    public ConcurrentDictionary<string, string> ActiveOpConfigIds { get; } = new(StringComparer.Ordinal);

    /// <summary>(facility, callsign) pairs that have been dumped this session; auto-generation must not re-create them.</summary>
    public HashSet<DumpedKey> Dumped { get; } = [];

    /// <summary>
    /// Sent items awaiting auto-WILCO. Keyed by item id, value is the session-clock time at which the
    /// scheduler should mark the item as Wilco. Snapshotted with the items, so a restored run acknowledges
    /// at the same sim second the live one did.
    /// </summary>
    public Dictionary<string, DateTime> ScheduledWilcoAt { get; } = new();

    public int NextItemId { get; set; } = 1;

    /// <summary>Walks the facility tree of the loaded ARTCC and registers a <see cref="TdlsConfig"/> entry for every facility node with a non-null <c>tdlsConfiguration</c>. Idempotent.</summary>
    public void InitializeFromArtcc(FacilityConfig artccRoot)
    {
        lock (Gate)
        {
            foreach (var facility in Walk(artccRoot))
            {
                if (facility.TdlsConfiguration is not null)
                {
                    Configs[facility.Id] = facility.TdlsConfiguration;
                }
            }
        }
    }

    /// <summary>
    /// The operational configuration in force at a facility: the explicit selection when it still
    /// names a real config, otherwise the facility's first. Null when the facility has ops configs
    /// disabled or defines none — callers then fall back to the facility-level SID list.
    /// </summary>
    public string? ResolveActiveOpConfigId(string facilityId)
    {
        if (!Configs.TryGetValue(facilityId, out var config) || !config.DclOpConfigsEnabled || (config.OpConfigs.Count == 0))
        {
            return null;
        }
        if (
            ActiveOpConfigIds.TryGetValue(facilityId, out var selected)
            && config.OpConfigs.Any(c => string.Equals(c.Id, selected, StringComparison.Ordinal))
        )
        {
            return selected;
        }
        return config.OpConfigs[0].Id;
    }

    /// <summary>The SID list a clearance at this facility must be built from, honouring the active ops config.</summary>
    public List<TdlsSidConfig> ResolveSids(string facilityId) =>
        Configs.TryGetValue(facilityId, out var config) ? config.ResolveSids(ResolveActiveOpConfigId(facilityId)) : [];

    /// <summary>
    /// Clears the session state — items, dumped lockout, active ops configs, the scheduled WILCOs and the id
    /// counter — and leaves <see cref="Configs"/> alone. This is what a snapshot restore replaces: the configs
    /// were re-derived from the ARTCC by the load that ran just before it.
    /// </summary>
    public void ClearSession()
    {
        lock (Gate)
        {
            Items.Clear();
            ActiveOpConfigIds.Clear();
            Dumped.Clear();
            ScheduledWilcoAt.Clear();
            NextItemId = 1;
        }
    }

    /// <summary>Clears the session state and the facility configs. Called when a scenario unloads.</summary>
    public void Reset()
    {
        lock (Gate)
        {
            ClearSession();
            Configs.Clear();
        }
    }

    private static IEnumerable<FacilityConfig> Walk(FacilityConfig facility)
    {
        yield return facility;
        foreach (var child in facility.ChildFacilities)
        {
            foreach (var descendant in Walk(child))
            {
                yield return descendant;
            }
        }
    }
}

/// <summary>Where a TDLS item sits in the Pending → Sent → Wilco lifecycle. The server projects it onto the CRC wire <c>TdlsStatus</c>, which shares these ordinals.</summary>
public enum TdlsItemStatus
{
    Pending = 0,
    Sent = 1,
    Wilco = 2,
}

/// <summary>One TDLS list entry. Items live in <see cref="TdlsState.Items"/> across the Pending → Sent → Wilco lifecycle and are removed on Dump, TTL expiry, or activation-on-departure.</summary>
public sealed record TdlsItemRecord(
    string Id,
    string AircraftId,
    string? Cid,
    string FacilityId,
    TdlsItemStatus Status,
    int Sequence,
    DateTime CreatedUtc,
    DateTime? SentUtc,
    DateTime? WilcoUtc,
    DateTime ExpiresUtc,
    TdlsClearance? SentPayload
);

/// <summary>Key for the per-session "dumped" lockout. Comparison is case-insensitive on both fields to match how callsigns/facilities are looked up elsewhere.</summary>
public readonly record struct DumpedKey(string FacilityId, string Callsign)
{
    public bool Equals(DumpedKey other) =>
        string.Equals(FacilityId, other.FacilityId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Callsign, other.Callsign, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => HashCode.Combine(FacilityId.ToUpperInvariant(), Callsign.ToUpperInvariant());
}
