using Xunit;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests;

/// <summary>
/// The special-purpose-code latch: <see cref="AircraftTransponder.SpcStartedAt"/> stamps the sim time a special code
/// first appears, restarts when the squawk changes to another special code, and clears on a normal code. The ERAM
/// Field-E blink runs for a fixed interval from it.
/// </summary>
public class TransponderSpcTests
{
    [Theory]
    [InlineData(1276u)]
    [InlineData(7400u)]
    [InlineData(7500u)]
    [InlineData(7600u)]
    [InlineData(7700u)]
    [InlineData(7777u)]
    public void Tick_SpecialCode_StampsStartTime(uint code)
    {
        var xpdr = new AircraftTransponder { Code = code };

        xpdr.Tick(nowSeconds: 42);

        Assert.Equal(42, xpdr.SpcStartedAt);
    }

    [Fact]
    public void Tick_SameSpecialCode_KeepsFirstStamp()
    {
        var xpdr = new AircraftTransponder { Code = 7700 };
        xpdr.Tick(nowSeconds: 10);

        xpdr.Code = 7700;
        xpdr.Tick(nowSeconds: 50);

        Assert.Equal(10, xpdr.SpcStartedAt);
    }

    [Fact]
    public void Tick_NormalCode_NoStamp()
    {
        var xpdr = new AircraftTransponder { Code = 4571 };

        xpdr.Tick(nowSeconds: 10);

        Assert.Null(xpdr.SpcStartedAt);
    }

    [Fact]
    public void Tick_ChangeToAnotherSpecialCode_RestartsStamp()
    {
        var xpdr = new AircraftTransponder { Code = 7700 };
        xpdr.Tick(nowSeconds: 10);

        xpdr.Code = 7600;
        xpdr.Tick(nowSeconds: 50);

        Assert.Equal(50, xpdr.SpcStartedAt);
    }

    [Fact]
    public void Tick_ChangeToNormalCode_ClearsStamp()
    {
        var xpdr = new AircraftTransponder { Code = 7700 };
        xpdr.Tick(nowSeconds: 10);

        xpdr.Code = 4571;
        xpdr.Tick(nowSeconds: 20);

        Assert.Null(xpdr.SpcStartedAt);
    }

    [Fact]
    public void Snapshot_RoundTripsSpcStartedAt()
    {
        var xpdr = new AircraftTransponder { Code = 7500 };
        xpdr.Tick(nowSeconds: 33);

        AircraftTransponderDto dto = xpdr.ToSnapshot();
        var restored = AircraftTransponder.FromSnapshot(dto);
        restored.Tick(nowSeconds: 60);

        Assert.Equal(33, dto.SpcStartedAt);
        Assert.Equal(33, restored.SpcStartedAt);
    }

    [Theory]
    [InlineData(7700u, true)]
    [InlineData(1276u, true)]
    [InlineData(1200u, false)]
    [InlineData(4571u, false)]
    public void IsSpecialPurposeCode_MatchesEramSpcSet(uint code, bool expected) =>
        Assert.Equal(expected, AircraftTransponder.IsSpecialPurposeCode(code));
}
