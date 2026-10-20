using System.Globalization;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// The canonical text of a <c>HOLDP</c> prints the leg it was given: an omitted leg prints no leg token, so the canonical text
/// re-parses to the command the controller typed, and an explicit leg prints invariantly ("1.5M" whatever the current culture).
/// </summary>
public class CommandDescriberHoldTests
{
    public CommandDescriberHoldTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void DescribeCommand_NoLegHold_PrintsNoLegToken_AndReparsesToTheSameCommand()
    {
        ParseResult<ParsedCommand> parsed = CommandParser.Parse("HOLDP SUNOL 184 R");
        Assert.True(parsed.IsSuccess, parsed.Reason);
        HoldingPatternCommand hold = Assert.IsType<HoldingPatternCommand>(parsed.Value);

        string canonical = CommandDescriber.DescribeCommand(hold);

        Assert.Equal("HOLDP SUNOL 184 R", canonical);
        ParseResult<ParsedCommand> reparsed = CommandParser.Parse(canonical);
        Assert.True(reparsed.IsSuccess, reparsed.Reason);
        Assert.Equal(hold, Assert.IsType<HoldingPatternCommand>(reparsed.Value));
    }

    [Fact]
    public void DescribeCommand_DecimalLeg_UnderGermanCulture_FormatsInvariantly()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            ParseResult<ParsedCommand> parsed = CommandParser.Parse("HOLDP SUNOL 090 1.5M R");
            Assert.True(parsed.IsSuccess, parsed.Reason);
            HoldingPatternCommand hold = Assert.IsType<HoldingPatternCommand>(parsed.Value);

            Assert.Equal("HOLDP SUNOL 090 1.5M R", CommandDescriber.DescribeCommand(hold));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void DescribeNatural_NoLegHold_OmitsTheLeg()
    {
        ParseResult<ParsedCommand> parsed = CommandParser.Parse("HOLDP SUNOL 184 R");
        Assert.True(parsed.IsSuccess, parsed.Reason);
        HoldingPatternCommand hold = Assert.IsType<HoldingPatternCommand>(parsed.Value);

        Assert.Equal("Hold at SUNOL, 184 inbound, right turns", CommandDescriber.DescribeNatural(hold));
    }

    [Fact]
    public void DescribeNatural_DecimalMinuteLeg_UnderGermanCulture_SeparatesNumberAndUnit()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            ParseResult<ParsedCommand> parsed = CommandParser.Parse("HOLDP SUNOL 090 1.5M R");
            Assert.True(parsed.IsSuccess, parsed.Reason);
            HoldingPatternCommand hold = Assert.IsType<HoldingPatternCommand>(parsed.Value);

            Assert.Equal("Hold at SUNOL, 090 inbound, 1.5 minute legs, right turns", CommandDescriber.DescribeNatural(hold));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
