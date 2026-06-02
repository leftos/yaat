using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

public class HoldForReleaseParserTests
{
    [Fact]
    public void Hfr_ParsesAirport()
    {
        var cmd = CommandParser.Parse("HFR SJC");
        var hfr = Assert.IsType<HoldForReleaseCommand>(cmd.Value);
        Assert.Equal("SJC", hfr.Airport);
    }

    [Fact]
    public void Hfr_LowercaseUppercased()
    {
        var cmd = CommandParser.Parse("HFR pao");
        var hfr = Assert.IsType<HoldForReleaseCommand>(cmd.Value);
        Assert.Equal("PAO", hfr.Airport);
    }

    [Fact]
    public void Hfr_NoAirport_Fails()
    {
        var cmd = CommandParser.Parse("HFR");
        Assert.False(cmd.IsSuccess);
    }

    [Fact]
    public void Hfroff_ParsesAirport()
    {
        var cmd = CommandParser.Parse("HFROFF SJC");
        var off = Assert.IsType<DisarmHoldForReleaseCommand>(cmd.Value);
        Assert.Equal("SJC", off.Airport);
    }

    [Fact]
    public void Rel_AirportOnly_NoInterval()
    {
        var cmd = CommandParser.Parse("REL SJC");
        var rel = Assert.IsType<ReleaseDepartureCommand>(cmd.Value);
        Assert.Equal("SJC", rel.Target);
        Assert.Null(rel.IntervalSeconds);
    }

    [Fact]
    public void Ctoa_AliasParsesToReleaseDeparture()
    {
        var cmd = CommandParser.Parse("CTOA SJC");
        var rel = Assert.IsType<ReleaseDepartureCommand>(cmd.Value);
        Assert.Equal("SJC", rel.Target);
        Assert.Null(rel.IntervalSeconds);
    }

    [Fact]
    public void Rel_WithIntervalMinutes_ConvertedToSeconds()
    {
        var cmd = CommandParser.Parse("REL SJC 2");
        var rel = Assert.IsType<ReleaseDepartureCommand>(cmd.Value);
        Assert.Equal("SJC", rel.Target);
        Assert.Equal(120, rel.IntervalSeconds);
    }

    [Fact]
    public void Rel_Callsign_ParsesAsTarget()
    {
        var cmd = CommandParser.Parse("REL SWA123");
        var rel = Assert.IsType<ReleaseDepartureCommand>(cmd.Value);
        Assert.Equal("SWA123", rel.Target);
        Assert.Null(rel.IntervalSeconds);
    }

    [Fact]
    public void Rel_NoTarget_Fails()
    {
        var cmd = CommandParser.Parse("REL");
        Assert.False(cmd.IsSuccess);
    }
}
