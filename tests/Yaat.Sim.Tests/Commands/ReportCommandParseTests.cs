using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// Parse + canonical round-trip for the <c>REPORT</c> verb (GitHub issue #211). Pins the
/// argument disambiguation: a bare leg keyword is a pattern-leg report; a number plus FINAL
/// (either order) is an n-mile-final report; any other short token is an at-fix report; an OFF /
/// CANCEL / STOP keyword cancels (scoped to a leg when one is named).
/// </summary>
public class ReportCommandParseTests
{
    public ReportCommandParseTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static ReportCommand Parse(string input)
    {
        var result = CommandParser.ParseCompound(input);
        Assert.True(result.IsSuccess, result.Reason);
        var block = Assert.Single(result.Value!.Blocks);
        return Assert.IsType<ReportCommand>(Assert.Single(block.Commands));
    }

    [Theory]
    [InlineData("REPORT BASE", ReportTrigger.Base)]
    [InlineData("REPORT FINAL", ReportTrigger.Final)]
    [InlineData("REPORT CROSSWIND", ReportTrigger.Crosswind)]
    [InlineData("REPORT XW", ReportTrigger.Crosswind)]
    [InlineData("REPORT DOWNWIND", ReportTrigger.Downwind)]
    [InlineData("REPORT DW", ReportTrigger.Downwind)]
    public void Parse_PatternLeg(string input, ReportTrigger expected)
    {
        var cmd = Parse(input);
        Assert.Equal(expected, cmd.Trigger);
        Assert.Null(cmd.DistanceNm);
        Assert.Null(cmd.FixName);
    }

    [Theory]
    [InlineData("REPORT 5 FINAL", 5)]
    [InlineData("REPORT FINAL 5", 5)]
    [InlineData("REPORT 10 FINAL", 10)]
    public void Parse_MileFinal(string input, int miles)
    {
        var cmd = Parse(input);
        Assert.Equal(ReportTrigger.MileFinal, cmd.Trigger);
        Assert.Equal(miles, cmd.DistanceNm);
    }

    [Theory]
    [InlineData("REPORT MENLO", "MENLO")]
    [InlineData("REPORT SUNOL", "SUNOL")]
    public void Parse_AtFix(string input, string fix)
    {
        var cmd = Parse(input);
        Assert.Equal(ReportTrigger.AtFix, cmd.Trigger);
        Assert.Equal(fix, cmd.FixName);
    }

    [Theory]
    [InlineData("REPORT OFF")]
    [InlineData("REPORT CANCEL")]
    [InlineData("REPORT STOP")]
    [InlineData("REPORT NONE")]
    public void Parse_CancelAll(string input)
    {
        var cmd = Parse(input);
        Assert.Equal(ReportTrigger.Cancel, cmd.Trigger);
        Assert.Null(cmd.CancelTarget);
    }

    [Theory]
    [InlineData("REPORT OFF BASE", ReportTrigger.Base)]
    [InlineData("REPORT BASE OFF", ReportTrigger.Base)]
    [InlineData("REPORT OFF FINAL", ReportTrigger.Final)]
    [InlineData("REPORT OFF CROSSWIND", ReportTrigger.Crosswind)]
    [InlineData("REPORT DOWNWIND OFF", ReportTrigger.Downwind)]
    public void Parse_CancelSpecificLeg(string input, ReportTrigger leg)
    {
        var cmd = Parse(input);
        Assert.Equal(ReportTrigger.Cancel, cmd.Trigger);
        Assert.Equal(leg, cmd.CancelTarget);
    }

    [Fact]
    public void Parse_BareReport_Fails()
    {
        var result = CommandParser.ParseCompound("REPORT");
        Assert.False(result.IsSuccess);
    }

    [Theory]
    [InlineData("REPORT BASE", "REPORT BASE")]
    [InlineData("REPORT FINAL", "REPORT FINAL")]
    [InlineData("REPORT XW", "REPORT CROSSWIND")]
    [InlineData("REPORT FINAL 5", "REPORT 5 FINAL")]
    [InlineData("REPORT 5 FINAL", "REPORT 5 FINAL")]
    [InlineData("REPORT MENLO", "REPORT MENLO")]
    [InlineData("REPORT OFF", "REPORT OFF")]
    [InlineData("REPORT BASE OFF", "REPORT OFF BASE")]
    public void CanonicalRoundTrip(string input, string canonical)
    {
        Assert.Equal(canonical, CommandDescriber.DescribeCommand(Parse(input)));
    }
}
