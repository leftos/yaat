using Xunit;

namespace Yaat.Sim.Tests;

/// <summary>
/// The "has ever reported Mode C" latch (<see cref="AircraftTransponder.HasReportedModeC"/>): CRC's ERAM
/// data blocks render the recently-lost-Mode-C <c>X</c>/<c>XXX</c> forms only when the target reports no
/// altitude but Mode C was previously received. The latch is set by <see cref="AircraftTransponder.Tick"/>
/// while the transponder is in an altitude-reporting mode and never clears (issue #368).
/// </summary>
public class TransponderModeCLatchTests
{
    [Fact]
    public void Tick_ModeC_LatchesHasReportedModeC()
    {
        var xpdr = new AircraftTransponder { Mode = "C" };

        xpdr.Tick(nowSeconds: 1);

        Assert.True(xpdr.HasReportedModeC);
    }

    [Fact]
    public void Tick_Standby_DoesNotLatch()
    {
        var xpdr = new AircraftTransponder { Mode = "Standby" };

        xpdr.Tick(nowSeconds: 1);

        Assert.False(xpdr.HasReportedModeC);
    }

    [Fact]
    public void Tick_StandbyAfterModeC_KeepsLatch()
    {
        var xpdr = new AircraftTransponder { Mode = "C" };
        xpdr.Tick(nowSeconds: 1);

        xpdr.Mode = "Standby";
        xpdr.Tick(nowSeconds: 2);

        Assert.True(xpdr.HasReportedModeC);
    }

    [Fact]
    public void Snapshot_RoundTripsLatch()
    {
        var xpdr = new AircraftTransponder { Mode = "C" };
        xpdr.Tick(nowSeconds: 1);

        var restored = AircraftTransponder.FromSnapshot(xpdr.ToSnapshot());

        Assert.True(restored.HasReportedModeC);
    }
}
