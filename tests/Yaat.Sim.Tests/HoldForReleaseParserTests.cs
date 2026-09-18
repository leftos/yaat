using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

public class HoldForReleaseParserTests
{
    [Fact]
    public void Hfr_ParsesAirport()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("HFR SJC");
        HoldForReleaseCommand hfr = Assert.IsType<HoldForReleaseCommand>(cmd.Value);
        Assert.Equal("SJC", hfr.Airport);
    }

    [Fact]
    public void Hfr_LowercaseUppercased()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("HFR pao");
        HoldForReleaseCommand hfr = Assert.IsType<HoldForReleaseCommand>(cmd.Value);
        Assert.Equal("PAO", hfr.Airport);
    }

    [Fact]
    public void Hfr_NoAirport_Fails()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("HFR");
        Assert.False(cmd.IsSuccess);
    }

    [Fact]
    public void Hfroff_ParsesAirport()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("HFROFF SJC");
        DisarmHoldForReleaseCommand off = Assert.IsType<DisarmHoldForReleaseCommand>(cmd.Value);
        Assert.Equal("SJC", off.Airport);
    }

    [Fact]
    public void Rel_AirportOnly_NoInterval()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("REL SJC");
        ReleaseDepartureCommand rel = Assert.IsType<ReleaseDepartureCommand>(cmd.Value);
        Assert.Equal("SJC", rel.Target);
        Assert.Null(rel.IntervalSeconds);
    }

    [Fact]
    public void Ctoa_AliasParsesToReleaseDeparture()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("CTOA SJC");
        ReleaseDepartureCommand rel = Assert.IsType<ReleaseDepartureCommand>(cmd.Value);
        Assert.Equal("SJC", rel.Target);
        Assert.Null(rel.IntervalSeconds);
    }

    [Fact]
    public void Rel_WithIntervalMinutes_ConvertedToSeconds()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("REL SJC 2");
        ReleaseDepartureCommand rel = Assert.IsType<ReleaseDepartureCommand>(cmd.Value);
        Assert.Equal("SJC", rel.Target);
        Assert.Equal(120, rel.IntervalSeconds);
    }

    [Fact]
    public void Rel_Callsign_ParsesAsTarget()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("REL SWA123");
        ReleaseDepartureCommand rel = Assert.IsType<ReleaseDepartureCommand>(cmd.Value);
        Assert.Equal("SWA123", rel.Target);
        Assert.Null(rel.IntervalSeconds);
    }

    [Fact]
    public void Rel_NoTarget_Fails()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("REL");
        Assert.False(cmd.IsSuccess);
    }
}
