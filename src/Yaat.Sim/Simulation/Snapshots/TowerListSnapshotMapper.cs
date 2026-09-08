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
    /// <summary>The non-empty lists, in the tracker's own list order. An empty list carries nothing and is left out.</summary>
    public static TowerListSnapshotDto Capture(TowerListTracker tracker)
    {
        var lists = new List<TowerListEntriesDto>();

        foreach (var listId in tracker.GetListIds())
        {
            var entries = tracker.GetEntries(listId);
            if (entries.Count == 0)
            {
                continue;
            }

            lists.Add(
                new TowerListEntriesDto
                {
                    ListId = listId,
                    Entries = [.. entries.Select(e => new TowerListEntryDto { Callsign = e.Callsign, EnteredAtSeconds = e.EnteredAtSeconds })],
                }
            );
        }

        return new TowerListSnapshotDto { Lists = lists };
    }

    /// <summary>Replaces the tracker's dwell entries. A null section is a pre-feature snapshot: its lists were empty.</summary>
    public static void Restore(TowerListTracker tracker, TowerListSnapshotDto? dto)
    {
        tracker.ClearSession();

        if (dto is null)
        {
            return;
        }

        foreach (var list in dto.Lists)
        {
            tracker.RestoreEntries(list.ListId, list.Entries.Select(e => (e.Callsign, e.EnteredAtSeconds)));
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
    public required string ListId { get; init; }
    public required List<TowerListEntryDto> Entries { get; init; }
}

public sealed class TowerListEntryDto
{
    public required string Callsign { get; init; }

    /// <summary>The scenario's elapsed second at which the aircraft came into range. The P-list's sort key.</summary>
    public required double EnteredAtSeconds { get; init; }
}
