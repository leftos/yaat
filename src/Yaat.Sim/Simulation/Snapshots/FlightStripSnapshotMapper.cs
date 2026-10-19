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
            foreach ((string? bayId, Dictionary<string, List<string>[]>? racks) in strips.Bays)
            {
                foreach ((string? rackKey, List<string>[]? columns) in racks)
                {
                    bayRacks.Add(
                        new StripBayRackSnapshotDto
                        {
                            BayId = bayId,
                            RackKey = rackKey,
                            Columns = [.. columns.Select(col => col.ToList())],
                            AutoSort = strips.AutoSortedRacks.Contains((bayId, ParseRackKey(rackKey))),
                        }
                    );
                }
            }

            return new FlightStripSnapshotDto
            {
                Items = items,
                BayRacks = bayRacks,
                DeparturePrinterQueue = [.. strips.DeparturePrinterQueue],
                ArrivalPrinterQueue = [.. strips.ArrivalPrinterQueue],
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
            strips.AutoSortedRacks.Clear();
            strips.DeparturePrinterQueue.Clear();
            strips.ArrivalPrinterQueue.Clear();

            foreach (StripItemSnapshotDto item in dto.Items)
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

            foreach (StripBayRackSnapshotDto rack in dto.BayRacks)
            {
                if (!strips.Bays.TryGetValue(rack.BayId, out Dictionary<string, List<string>[]>? racks))
                {
                    racks = [];
                    strips.Bays[rack.BayId] = racks;
                }

                racks[rack.RackKey] = [.. rack.Columns.Select(col => col.ToList())];
                if (rack.AutoSort)
                {
                    strips.AutoSortedRacks.Add((rack.BayId, ParseRackKey(rack.RackKey)));
                }
            }

            strips.DeparturePrinterQueue.AddRange(dto.DeparturePrinterQueue);
            strips.ArrivalPrinterQueue.AddRange(dto.ArrivalPrinterQueue);
            strips.NextBlankId = dto.NextBlankId;
        }
    }

    /// <summary>A rack key is its 0-based rack index written out ("0", "1", …), the form <c>EnsureRack</c> creates.</summary>
    private static int ParseRackKey(string rackKey) => int.Parse(rackKey, System.Globalization.CultureInfo.InvariantCulture);
}
