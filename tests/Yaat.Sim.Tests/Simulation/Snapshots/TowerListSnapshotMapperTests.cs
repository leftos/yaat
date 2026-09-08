using System.Text.Json;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Snapshots;

/// <summary>
/// <see cref="TowerListSnapshotMapper"/> round-trips the tower lists' dwell entries — each list's callsigns with the
/// elapsed second they came into range at — and restores by replacing. The airports themselves are not in the
/// snapshot: they come from the ARTCC, which the load re-derives before a restore runs, so
/// <see cref="TowerListTracker.ClearSession"/> is what a restore clears and the configured lists survive it.
/// </summary>
public class TowerListSnapshotMapperTests
{
    private const string OakList = "P8";

    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public TowerListSnapshotMapperTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static AircraftState Aircraft(string callsign, double lat, double lon) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = new LatLon(lat, lon),
            Altitude = 3000,
            Transponder = new AircraftTransponder { Code = 1200, Mode = "ModeC" },
        };

    private static readonly AircraftState OverOak = Aircraft("AAL100", 37.7213, -122.2208);
    private static readonly AircraftState NorthOfOak = Aircraft("UAL200", 37.9000, -122.2500);
    private static readonly AircraftState OverSfo = Aircraft("SWA300", 37.6188, -122.3750);

    /// <summary>A tracker over the real ZOA tree with nothing in it yet.</summary>
    private TowerListTracker? Fresh()
    {
        if (_zoa is null)
        {
            return null;
        }

        var tracker = new TowerListTracker();
        tracker.Initialize(_zoa);
        return tracker;
    }

    /// <summary>Three aircraft that come into range at three different seconds, so every list's entries carry distinct dwell seconds.</summary>
    private TowerListTracker? Seeded()
    {
        if (Fresh() is not { } tracker)
        {
            return null;
        }

        tracker.Update([OverOak], 10);
        tracker.Update([OverOak, OverSfo], 20);
        tracker.Update([OverOak, OverSfo, NorthOfOak], 35);
        return tracker;
    }

    private static List<(string ListId, string Callsign, double EnteredAtSeconds)> Flatten(TowerListTracker tracker) =>
        [.. tracker.GetListIds().SelectMany(id => tracker.GetEntries(id).Select(e => (ListId: id, e.Callsign, e.EnteredAtSeconds)))];

    [Fact]
    public void ACapturedSnapshot_RestoresEveryCallsignWithItsDwellSecond()
    {
        if (Seeded() is not { } seeded || Fresh() is not { } target)
        {
            return;
        }

        var live = Flatten(seeded);
        Assert.Contains(live, e => (e.ListId == OakList) && (e.Callsign == "AAL100") && (e.EnteredAtSeconds == 10));
        Assert.Contains(live, e => (e.ListId == OakList) && (e.Callsign == "SWA300") && (e.EnteredAtSeconds == 20));
        Assert.Contains(live, e => (e.ListId == OakList) && (e.Callsign == "UAL200") && (e.EnteredAtSeconds == 35));

        var dto = TowerListSnapshotMapper.Capture(seeded);
        var serialized = JsonSerializer.Deserialize<TowerListSnapshotDto>(JsonSerializer.Serialize(dto))!;

        TowerListSnapshotMapper.Restore(target, serialized);

        Assert.Equal(live, Flatten(target));
    }

    /// <summary>
    /// A list's order is its dwell seconds, not the order the entries were written in: a snapshot restored from a
    /// section that lists them newest-first still reads back oldest-first, which is what the P-list renders.
    /// </summary>
    [Fact]
    public void EntriesRestoredOutOfOrder_ReadBackOldestFirst()
    {
        if (Fresh() is not { } tracker)
        {
            return;
        }

        TowerListSnapshotMapper.Restore(
            tracker,
            new TowerListSnapshotDto
            {
                Lists =
                [
                    new TowerListEntriesDto
                    {
                        ListId = OakList,
                        Entries =
                        [
                            new TowerListEntryDto { Callsign = "UAL200", EnteredAtSeconds = 35 },
                            new TowerListEntryDto { Callsign = "AAL100", EnteredAtSeconds = 10 },
                            new TowerListEntryDto { Callsign = "SWA300", EnteredAtSeconds = 20 },
                        ],
                    },
                ],
            }
        );

        Assert.Equal([("AAL100", 10.0), ("SWA300", 20.0), ("UAL200", 35.0)], tracker.GetEntries(OakList));
    }

    /// <summary>A pre-feature snapshot has no tower-list section; the restore reads that as "the lists were empty".</summary>
    [Fact]
    public void ARestoreOfANullSection_LeavesTheTrackerEmpty()
    {
        if (Seeded() is not { } tracker)
        {
            return;
        }

        TowerListSnapshotMapper.Restore(tracker, null);

        Assert.Empty(Flatten(tracker));
    }

    /// <summary>
    /// The airports are the ARTCC's and are never in a snapshot, so clearing the session leaves every configured list
    /// in place and the next update repopulates it.
    /// </summary>
    [Fact]
    public void ClearSession_DropsTheEntriesAndKeepsTheAirports()
    {
        if (Seeded() is not { } tracker)
        {
            return;
        }

        var lists = tracker.GetListIds();
        Assert.NotEmpty(lists);

        tracker.ClearSession();

        Assert.Equal(lists, tracker.GetListIds());
        Assert.Empty(Flatten(tracker));

        Assert.True(tracker.Update([OverOak], 100));
        Assert.Equal([("AAL100", 100.0)], tracker.GetEntries(OakList));
    }
}
