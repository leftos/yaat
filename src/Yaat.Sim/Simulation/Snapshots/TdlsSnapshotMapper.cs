using Yaat.Sim.Simulation.Tdls;

namespace Yaat.Sim.Simulation.Snapshots;

/// <summary>
/// <see cref="TdlsState"/> ⇄ <see cref="TdlsSnapshotDto"/>. Restore replaces the session state and leaves
/// <see cref="TdlsState.Configs"/> alone — the scenario load that runs before a restore has just re-derived the
/// facility configuration from the ARTCC, and the snapshot never carried it.
/// </summary>
public static class TdlsSnapshotMapper
{
    public static TdlsSnapshotDto Capture(TdlsState tdls)
    {
        lock (tdls.Gate)
        {
            var items = tdls
                .Items.Values.Select(i => new TdlsItemSnapshotDto
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

            var dumped = tdls.Dumped.Select(k => new TdlsDumpedSnapshotDto { FacilityId = k.FacilityId, Callsign = k.Callsign }).ToList();

            var activeOpConfigs = tdls
                .ActiveOpConfigIds.Select(kv => new TdlsActiveOpConfigSnapshotDto { FacilityId = kv.Key, OpConfigId = kv.Value })
                .ToList();

            var scheduledWilco = tdls.ScheduledWilcoAt.Select(kv => new TdlsScheduledWilcoDto { ItemId = kv.Key, DueUtc = kv.Value }).ToList();

            return new TdlsSnapshotDto
            {
                Items = items,
                Dumped = dumped,
                NextItemId = tdls.NextItemId,
                ActiveOpConfigs = activeOpConfigs,
                ScheduledWilco = scheduledWilco,
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

            foreach (var active in dto.ActiveOpConfigs)
            {
                tdls.ActiveOpConfigIds[active.FacilityId] = active.OpConfigId;
            }

            foreach (var item in dto.Items)
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

            foreach (var d in dto.Dumped)
            {
                tdls.Dumped.Add(new DumpedKey(d.FacilityId, d.Callsign));
            }

            foreach (var wilco in dto.ScheduledWilco)
            {
                tdls.ScheduledWilcoAt[wilco.ItemId] = wilco.DueUtc;
            }

            tdls.NextItemId = dto.NextItemId;
        }
    }
}
