using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Simulation.Snapshots;

/// <summary>
/// <see cref="TowerListTracker"/> ⇄ <see cref="TowerListSnapshotDto"/>. Only the dwell entries cross: the tower list
/// airports come from the ARTCC and are rebuilt by <see cref="SimulationEngine.InitializeFromArtcc"/> at every load,
/// so a restore that runs after that init replaces what the lists hold and never the lists themselves. Without this
/// the entries would be re-stamped at the restore second and the P-lists' <c>DropZoneEntryTime</c> order would change
/// under a rewind.
/// </summary>
public static class TowerListSnapshotMapper
{
    private static readonly ILogger Log = SimLog.CreateLogger("TowerListSnapshotMapper");

    /// <summary>The non-empty lists, in the tracker's own list order. An empty list carries nothing and is left out.</summary>
    public static TowerListSnapshotDto Capture(TowerListTracker tracker)
    {
        var lists = new List<TowerListEntriesDto>();

        foreach (var key in tracker.GetLists())
        {
            var entries = tracker.GetEntries(key);
            if (entries.Count == 0)
            {
                continue;
            }

            lists.Add(
                new TowerListEntriesDto
                {
                    FacilityId = key.FacilityId,
                    ListId = key.ListId,
                    Entries = [.. entries.Select(e => new TowerListEntryDto { Callsign = e.Callsign, EnteredAtSeconds = e.EnteredAtSeconds })],
                }
            );
        }

        return new TowerListSnapshotDto { Lists = lists };
    }

    /// <summary>
    /// Replaces the tracker's dwell entries. A null section is a pre-feature snapshot: its lists were empty. Two
    /// kinds of list are dropped instead of restored: one written before the facility id existed, which cannot be
    /// attributed to a facility (two facilities may declare the same list id), and one whose (facility, list) the
    /// restoring room does not configure — a different ARTCC, or an airport position its navigation database could
    /// not resolve. Both are reported in a single warning per restore: a client scrub runs this once per seek, so a
    /// line per list would be a stream.
    /// </summary>
    public static void Restore(TowerListTracker tracker, TowerListSnapshotDto? dto)
    {
        tracker.ClearSession();

        if (dto is null)
        {
            return;
        }

        var dropped = new List<string>();

        foreach (var list in dto.Lists)
        {
            if (list.FacilityId is not { } facilityId)
            {
                dropped.Add($"{list.ListId} (no facility id)");
                continue;
            }

            if (!tracker.RestoreEntries(new TowerListKey(facilityId, list.ListId), list.Entries.Select(e => (e.Callsign, e.EnteredAtSeconds))))
            {
                dropped.Add($"{facilityId}/{list.ListId} (facility/list not configured)");
            }
        }

        if (dropped.Count > 0)
        {
            Log.LogWarning("{Count} tower list(s) in the snapshot could not be restored: {Lists}", dropped.Count, string.Join(", ", dropped));
        }
    }
}

/// <summary>
/// The tower lists' dwell entries: which aircraft each STARS P-list holds and the elapsed second it came into the
/// list airport's range at. The airports themselves are deliberately out — a load re-derives them from the ARTCC
/// before the restore runs. Absent in pre-feature snapshots, which restore empty lists.
/// </summary>
public sealed class TowerListSnapshotDto
{
    public required List<TowerListEntriesDto> Lists { get; init; }
}

/// <summary>One list's entries, oldest first — the order the P-list renders.</summary>
public sealed class TowerListEntriesDto
{
    /// <summary>
    /// The facility that declares the list. Absent in snapshots written before tower lists were keyed per facility;
    /// such a list cannot be attributed to one (FAT and NCT both declare <c>P1</c>) and is dropped on restore.
    /// </summary>
    public string? FacilityId { get; init; }

    public required string ListId { get; init; }
    public required List<TowerListEntryDto> Entries { get; init; }
}

public sealed class TowerListEntryDto
{
    public required string Callsign { get; init; }

    /// <summary>The scenario's elapsed second at which the aircraft came into range. The P-list's sort key.</summary>
    public required double EnteredAtSeconds { get; init; }
}
