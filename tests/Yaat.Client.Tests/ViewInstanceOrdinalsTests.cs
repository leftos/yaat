using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Tests;

// Extra Radar/Ground view windows are numbered from #2 (the docked view is the implicit #1), and the
// ordinal is what keys a window's geometry and its per-scenario view settings. Closing a window frees
// its number, so NextFree fills the lowest gap rather than always appending.
public class ViewInstanceOrdinalsTests
{
    [Fact]
    public void NextFree_NoneTaken_StartsAtTwo() => Assert.Equal(2, ViewInstanceOrdinals.NextFree([]));

    [Fact]
    public void NextFree_ContiguousTaken_AppendsAfterHighest() => Assert.Equal(4, ViewInstanceOrdinals.NextFree([2, 3]));

    [Fact]
    public void NextFree_LowestTakenIsThree_ReusesTwo() => Assert.Equal(2, ViewInstanceOrdinals.NextFree([3]));

    [Fact]
    public void NextFree_GapInTaken_FillsTheGap() => Assert.Equal(3, ViewInstanceOrdinals.NextFree([2, 4]));

    [Fact]
    public void Keys_ArePrefixPlusOrdinal()
    {
        Assert.Equal("RadarView#2", ViewInstanceOrdinals.RadarKey(2));
        Assert.Equal("GroundView#3", ViewInstanceOrdinals.GroundKey(3));
        Assert.Equal(ViewInstanceOrdinals.RadarPrefix + "10", ViewInstanceOrdinals.RadarKey(10));
        Assert.Equal(ViewInstanceOrdinals.GroundPrefix + "10", ViewInstanceOrdinals.GroundKey(10));
    }

    [Fact]
    public void Instances_ExposeKeyAndTitleForTheirOrdinal()
    {
        var radar = new RadarViewInstance
        {
            Ordinal = 2,
            AirportId = "KOAK",
            Vm = TestRadarVm(),
        };
        var ground = new GroundViewInstance
        {
            Ordinal = 3,
            AirportId = "KSFO",
            Vm = TestGroundVm(),
        };

        Assert.Equal("RadarView#2", radar.GeometryKey);
        // The title carries the base airport: several windows can share a scenario but not a target.
        Assert.Equal("Radar View #2 — KOAK", radar.Title);
        Assert.Equal("GroundView#3", ground.GeometryKey);
        Assert.Equal("Ground View #3 — KSFO", ground.Title);
    }

    private static RadarViewModel TestRadarVm() => new(new ServerConnection(), new VideoMapService(), (_, _, _) => Task.CompletedTask);

    private static GroundViewModel TestGroundVm() => new(new ServerConnection(), sendCommand: (_, _, _) => Task.CompletedTask);
}
