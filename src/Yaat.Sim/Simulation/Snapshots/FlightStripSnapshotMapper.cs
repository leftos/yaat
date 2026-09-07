using Yaat.Sim.Simulation.Strips;

namespace Yaat.Sim.Simulation.Snapshots;

/// <summary>
/// <see cref="FlightStripState"/> ⇄ <see cref="FlightStripSnapshotDto"/>. Restore replaces, never merges: the
/// snapshot is the whole strip state at its second, so anything the target engine held is cleared first.
/// </summary>
public static class FlightStripSnapshotMapper
{
    public static FlightStripSnapshotDto Capture(FlightStripState strips)
    {
        lock (strips.Gate)
        {
            var items = strips
                .Items.Values.Select(i => new StripItemSnapshotDto
                {
                    Id = i.Id,
                    AircraftId = i.AircraftId,
                    Type = i.Type,
                    IsOffset = i.IsOffset,
                    // Aliased, not copied: every writer of FieldValues builds a new array rather than mutating in place.
                    FieldValues = i.FieldValues,
                    FacilityId = i.FacilityId,
                    BayId = i.BayId,
                    Rack = i.Rack,
                    Index = i.Index,
                })
                .ToList();

            var bayRacks = new List<StripBayRackSnapshotDto>();
            foreach (var (bayId, racks) in strips.Bays)
            {
                foreach (var (rackKey, columns) in racks)
                {
                    bayRacks.Add(
                        new StripBayRackSnapshotDto
                        {
                            BayId = bayId,
                            RackKey = rackKey,
                            Columns = columns.Select(col => col.ToList()).ToList(),
                        }
                    );
                }
            }

            return new FlightStripSnapshotDto
            {
                Items = items,
                BayRacks = bayRacks,
                DeparturePrinterQueue = strips.DeparturePrinterQueue.ToList(),
                ArrivalPrinterQueue = strips.ArrivalPrinterQueue.ToList(),
                NextBlankId = strips.NextBlankId,
            };
        }
    }

    public static void Restore(FlightStripState strips, FlightStripSnapshotDto dto)
    {
        lock (strips.Gate)
        {
            strips.Items.Clear();
            strips.Bays.Clear();
            strips.DeparturePrinterQueue.Clear();
            strips.ArrivalPrinterQueue.Clear();

            foreach (var item in dto.Items)
            {
                strips.Items[item.Id] = new StripItemRecord(
                    item.Id,
                    item.AircraftId,
                    item.Type,
                    item.IsOffset,
                    item.FieldValues,
                    item.FacilityId,
                    item.BayId,
                    item.Rack,
                    item.Index
                );
            }

            foreach (var rack in dto.BayRacks)
            {
                if (!strips.Bays.TryGetValue(rack.BayId, out var racks))
                {
                    racks = new Dictionary<string, List<string>[]>();
                    strips.Bays[rack.BayId] = racks;
                }

                racks[rack.RackKey] = rack.Columns.Select(col => col.ToList()).ToArray();
            }

            strips.DeparturePrinterQueue.AddRange(dto.DeparturePrinterQueue);
            strips.ArrivalPrinterQueue.AddRange(dto.ArrivalPrinterQueue);
            strips.NextBlankId = dto.NextBlankId;
        }
    }
}
