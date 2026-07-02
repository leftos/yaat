using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

public class CfrParserTests
{
    [Fact]
    public void Cfr_NoArg_NoTimeNoClear()
    {
        var cmd = CommandParser.Parse("CFR");
        var cfr = Assert.IsType<CfrDepartureCommand>(cmd.Value);
        Assert.Null(cfr.Hhmm);
        Assert.False(cfr.Clear);
    }

    [Fact]
    public void Cfr_Hhmm_Parsed()
    {
        var cmd = CommandParser.Parse("CFR 1830");
        var cfr = Assert.IsType<CfrDepartureCommand>(cmd.Value);
        Assert.Equal(1830, cfr.Hhmm);
        Assert.False(cfr.Clear);
    }

    [Fact]
    public void Cfr_LeadingZeroTime_Parsed()
    {
        var cmd = CommandParser.Parse("CFR 0001");
        var cfr = Assert.IsType<CfrDepartureCommand>(cmd.Value);
        Assert.Equal(1, cfr.Hhmm);
    }

    [Fact]
    public void Cfr_Off_Clears()
    {
        var cmd = CommandParser.Parse("CFR OFF");
        var cfr = Assert.IsType<CfrDepartureCommand>(cmd.Value);
        Assert.Null(cfr.Hhmm);
        Assert.True(cfr.Clear);
    }

    [Fact]
    public void Cfr_Cancel_Clears()
    {
        var cmd = CommandParser.Parse("CFR CANCEL");
        var cfr = Assert.IsType<CfrDepartureCommand>(cmd.Value);
        Assert.True(cfr.Clear);
    }

    [Fact]
    public void Apreq_Alias_ParsesToCfr()
    {
        var cmd = CommandParser.Parse("APREQ 0530");
        var cfr = Assert.IsType<CfrDepartureCommand>(cmd.Value);
        Assert.Equal(530, cfr.Hhmm);
    }

    [Fact]
    public void Cfr_InvalidTime_Fails()
    {
        Assert.False(CommandParser.Parse("CFR 2599").IsSuccess);
        Assert.False(CommandParser.Parse("CFR ABCD").IsSuccess);
        Assert.False(CommandParser.Parse("CFR 1860").IsSuccess);
    }
}
