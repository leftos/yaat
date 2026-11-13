using Yaat.Sim.Simulation.Tdls;

namespace Yaat.Sim.Simulation.Snapshots;

/// <summary>
/// <see cref="TdlsState"/> ⇄ <see cref="TdlsSnapshotDto"/>. Restore replaces the session state and leaves
/// <see cref="TdlsState.Configs"/> alone — the scenario load that runs before a restore has just re-derived the
/// facility configuration from the ARTCC, and the snapshot never carried it. Capture writes every collection ordered by
/// key, all ordinal: the items and the scheduled wilcos by item id, the active ops configs by facility, the dumped and the
/// expired keys by facility then call sign. A dictionary's or a set's enumeration order follows its insertion and removal
/// history (the expiry sweep adds in the items' enumeration order; a removed wilco's slot goes to the next one scheduled),
/// which a restore cannot rebuild, so only an ordinal order makes a capture of the restored state equal the first.
/// </summary>
public static class TdlsSnapshotMapper
{
    public static TdlsSnapshotDto Capture(TdlsState tdls)
    {
        lock (tdls.Gate)
        {
            var items = tdls
                .Items.Values.OrderBy(i => i.Id, StringComparer.Ordinal)
                .Select(i => new TdlsItemSnapshotDto
                {
                    Id = i.Id,
                    AircraftId = i.AircraftId,
                    Cid = i.Cid,
                    FacilityId = i.FacilityId,
                    Status = (int)i.Status,
                    Sequence = i.Sequence,
                    CreatedUtc = i.CreatedUtc,
                    SentUtc = i.SentUtc,
                    WilcoUtc = i.WilcoUtc,
                    ExpiresUtc = i.ExpiresUtc,
                    SentPayload = i.SentPayload,
                })
                .ToList();

            List<TdlsDumpedSnapshotDto> dumped = OrderedKeys(tdls.Dumped);

            List<TdlsDumpedSnapshotDto> expired = OrderedKeys(tdls.Expired);

            var activeOpConfigs = tdls
                .ActiveOpConfigIds.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new TdlsActiveOpConfigSnapshotDto { FacilityId = kv.Key, OpConfigId = kv.Value })
                .ToList();

            var scheduledWilco = tdls
                .ScheduledWilcoAt.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new TdlsScheduledWilcoDto { ItemId = kv.Key, DueUtc = kv.Value })
                .ToList();

            return new TdlsSnapshotDto
            {
                Items = items,
                Dumped = dumped,
                NextItemId = tdls.NextItemId,
                ActiveOpConfigs = activeOpConfigs,
                ScheduledWilco = scheduledWilco,
                Expired = expired,
            };
        }
    }

    public static void Restore(TdlsState tdls, TdlsSnapshotDto dto)
    {
        lock (tdls.Gate)
        {
            // Configs are NOT restored from the snapshot — they're rehydrated by
            // InitializeFromArtcc when the ARTCC config reloads during scenario load.
            //
            // The active ops config IS restored: it decides which SIDs and which per-transition
            // defaults a PDC is built from, so a replay that dropped it would rebuild a different
            // clearance than the one the controller actually sent.
            tdls.ClearSession();

            foreach (TdlsActiveOpConfigSnapshotDto active in dto.ActiveOpConfigs)
            {
                tdls.ActiveOpConfigIds[active.FacilityId] = active.OpConfigId;
            }

            foreach (TdlsItemSnapshotDto item in dto.Items)
            {
                tdls.Items[item.Id] = new TdlsItemRecord(
                    Id: item.Id,
                    AircraftId: item.AircraftId,
                    Cid: item.Cid,
                    FacilityId: item.FacilityId,
                    Status: (TdlsItemStatus)item.Status,
                    Sequence: item.Sequence,
                    CreatedUtc: item.CreatedUtc,
                    SentUtc: item.SentUtc,
                    WilcoUtc: item.WilcoUtc,
                    ExpiresUtc: item.ExpiresUtc,
                    SentPayload: item.SentPayload
                );
            }

            foreach (TdlsDumpedSnapshotDto d in dto.Dumped)
            {
                tdls.Dumped.Add(new DumpedKey(d.FacilityId, d.Callsign));
            }

            foreach (TdlsDumpedSnapshotDto e in dto.Expired)
            {
                tdls.Expired.Add(new DumpedKey(e.FacilityId, e.Callsign));
            }

            foreach (TdlsScheduledWilcoDto wilco in dto.ScheduledWilco)
            {
                tdls.ScheduledWilcoAt[wilco.ItemId] = wilco.DueUtc;
            }

            tdls.NextItemId = dto.NextItemId;
        }
    }

    /// <summary>A dump or expiry set ordered by facility, then call sign, both ordinal.</summary>
    private static List<TdlsDumpedSnapshotDto> OrderedKeys(HashSet<DumpedKey> keys) =>
        [
            .. keys.OrderBy(k => k.FacilityId, StringComparer.Ordinal)
                .ThenBy(k => k.Callsign, StringComparer.Ordinal)
                .Select(k => new TdlsDumpedSnapshotDto { FacilityId = k.FacilityId, Callsign = k.Callsign }),
        ];
}
