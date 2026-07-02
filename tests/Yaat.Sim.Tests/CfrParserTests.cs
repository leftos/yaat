using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

public class CfrParserTests
{
    [Fact]
    public void Cfr_NoArg_ImmediateSet()
    {
        var cmd = CommandParser.Parse("CFR");
        var cfr = Assert.IsType<CfrDepartureCommand>(cmd.Value);
        Assert.Null(cfr.Hhmm);
        Assert.Equal(CfrAction.Set, cfr.Action);
    }

    [Fact]
    public void Cfr_Hhmm_Parsed()
    {
        var cmd = CommandParser.Parse("CFR 1830");
        var cfr = Assert.IsType<CfrDepartureCommand>(cmd.Value);
        Assert.Equal(1830, cfr.Hhmm);
        Assert.Equal(CfrAction.Set, cfr.Action);
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
        Assert.Equal(CfrAction.Clear, cfr.Action);
    }

    [Fact]
    public void Cfr_Cancel_Clears()
    {
        var cmd = CommandParser.Parse("CFR CANCEL");
        var cfr = Assert.IsType<CfrDepartureCommand>(cmd.Value);
        Assert.Equal(CfrAction.Clear, cfr.Action);
    }

    [Fact]
    public void Cfr_Check_Queries()
    {
        var cmd = CommandParser.Parse("CFR CHECK");
        var cfr = Assert.IsType<CfrDepartureCommand>(cmd.Value);
        Assert.Equal(CfrAction.Check, cfr.Action);
    }

    [Fact]
    public void Cfr_Status_AliasesCheck()
    {
        var cmd = CommandParser.Parse("CFR STATUS");
        var cfr = Assert.IsType<CfrDepartureCommand>(cmd.Value);
        Assert.Equal(CfrAction.Check, cfr.Action);
    }

    [Fact]
    public void Apreq_IsNotAnAlias()
    {
        // APREQ (Approval Request) is broader than a departure release, so it is deliberately not a CFR alias.
        Assert.False(CommandParser.Parse("APREQ 0530").IsSuccess);
    }

    [Fact]
    public void Cfr_InvalidTime_Fails()
    {
        Assert.False(CommandParser.Parse("CFR 2599").IsSuccess);
        Assert.False(CommandParser.Parse("CFR ABCD").IsSuccess);
        Assert.False(CommandParser.Parse("CFR 1860").IsSuccess);
    }
}
