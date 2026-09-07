using Xunit;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Simulation.Strips;

namespace Yaat.Sim.Tests.Simulation.Snapshots;

/// <summary>
/// <see cref="FlightStripSnapshotMapper"/> round-trips the whole strip state — the strips, the bay/rack layout
/// they sit in, both printer queues and the blank-id counter — and restores by replacing, never merging.
/// </summary>
public class FlightStripSnapshotMapperTests
{
    private static FlightStripState Seeded()
    {
        var strips = new FlightStripState();
        lock (strips.Gate)
        {
            strips.Items["s1"] = new StripItemRecord("s1", "AAL100", 0, false, ["a", "b", "c", "d", "e", "f", "g", "h", "i"], "fac", "bay1", 0, 0);
            strips.Bays["bay1"] = new Dictionary<string, List<string>[]> { ["0"] = [new List<string> { "s1" }] };
            strips.DeparturePrinterQueue.Add("s1");
            strips.NextBlankId = 5;
        }
        return strips;
    }

    [Fact]
    public void RoundTrip_PreservesItemsBaysAndPrinterQueue()
    {
        var dto = FlightStripSnapshotMapper.Capture(Seeded());

        Assert.Single(dto.Items);
        Assert.Single(dto.BayRacks);
        Assert.Equal(5, dto.NextBlankId);

        var restored = new FlightStripState();
        FlightStripSnapshotMapper.Restore(restored, dto);

        lock (restored.Gate)
        {
            Assert.True(restored.Items.ContainsKey("s1"));
            var item = restored.Items["s1"];
            Assert.Equal("AAL100", item.AircraftId);
            Assert.Equal("bay1", item.BayId);
            Assert.Equal(["a", "b", "c", "d", "e", "f", "g", "h", "i"], item.FieldValues);
            Assert.Equal(5, restored.NextBlankId);
            Assert.Equal(["s1"], restored.DeparturePrinterQueue);
            Assert.Empty(restored.ArrivalPrinterQueue);
            Assert.Equal(["s1"], restored.Bays["bay1"]["0"][0]);
        }
    }

    [Fact]
    public void Restore_ReplacesWhateverTheTargetHeld()
    {
        var empty = FlightStripSnapshotMapper.Capture(new FlightStripState());

        var populated = Seeded();
        FlightStripSnapshotMapper.Restore(populated, empty);

        lock (populated.Gate)
        {
            Assert.Empty(populated.Items);
            Assert.Empty(populated.Bays);
            Assert.Empty(populated.DeparturePrinterQueue);
            Assert.Equal(1, populated.NextBlankId);
        }
    }
}
