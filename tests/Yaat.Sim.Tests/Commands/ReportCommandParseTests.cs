using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// Parse + canonical round-trip for the <c>REPORT</c> verb (GitHub issue #211). Pins the
/// argument disambiguation: a bare leg keyword is a pattern-leg report; a number plus FINAL
/// (either order) is an n-mile-final report; any other fix-shaped token is an at-fix report; an
/// OFF / CANCEL / STOP keyword cancels everything, or only the leg, the n-mile-final report or the
/// fix report the form names.
/// </summary>
public class ReportCommandParseTests
{
    public ReportCommandParseTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static ReportCommand Parse(string input)
    {
        ParseResult<CompoundCommand> result = CommandParser.ParseCompound(input);
        Assert.True(result.IsSuccess, result.Reason);
        ParsedBlock block = Assert.Single(result.Value!.Blocks);
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
        ReportCommand cmd = Parse(input);
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
        ReportCommand cmd = Parse(input);
        Assert.Equal(ReportTrigger.MileFinal, cmd.Trigger);
        Assert.Equal(miles, cmd.DistanceNm);
    }

    [Theory]
    [InlineData("REPORT MENLO", "MENLO")]
    [InlineData("REPORT SUNOL", "SUNOL")]
    public void Parse_AtFix(string input, string fix)
    {
        ReportCommand cmd = Parse(input);
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
        ReportCommand cmd = Parse(input);
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
        ReportCommand cmd = Parse(input);
        Assert.Equal(ReportTrigger.Cancel, cmd.Trigger);
        Assert.Equal(leg, cmd.CancelTarget);
    }

    [Theory]
    [InlineData("REPORT OFF 5 FINAL", 5)]
    [InlineData("REPORT OFF FINAL 5", 5)]
    [InlineData("REPORT FINAL 5 OFF", 5)]
    [InlineData("REPORT 3 FINAL CANCEL", 3)]
    [InlineData("REPORT OFF FINAL FINAL 5", 5)]
    public void Parse_CancelMileFinal(string input, int miles)
    {
        ReportCommand cmd = Parse(input);
        Assert.Equal(ReportTrigger.Cancel, cmd.Trigger);
        Assert.Equal(ReportTrigger.MileFinal, cmd.CancelTarget);
        Assert.Equal(miles, cmd.DistanceNm);
    }

    [Fact]
    public void Parse_CancelFinalLeg_WithoutDistance()
    {
        ReportCommand cmd = Parse("REPORT OFF FINAL");
        Assert.Equal(ReportTrigger.Cancel, cmd.Trigger);
        Assert.Equal(ReportTrigger.Final, cmd.CancelTarget);
        Assert.Null(cmd.DistanceNm);
    }

    [Theory]
    [InlineData("REPORT OFF SUNOL", "SUNOL")]
    [InlineData("REPORT SUNOL OFF", "SUNOL")]
    [InlineData("REPORT OFF OAK30NUM", "OAK30NUM")]
    public void Parse_CancelFix(string input, string fix)
    {
        ReportCommand cmd = Parse(input);
        Assert.Equal(ReportTrigger.Cancel, cmd.Trigger);
        Assert.Equal(ReportTrigger.AtFix, cmd.CancelTarget);
        Assert.Equal(fix, cmd.FixName);
    }

    [Theory]
    [InlineData("REPORT OFF 5")]
    [InlineData("REPORT OFF SUNOL MENLO")]
    [InlineData("REPORT OFF ALL")]
    [InlineData("REPORT ALL OFF")]
    public void Parse_CancelAll_OtherForms(string input)
    {
        ReportCommand cmd = Parse(input);
        Assert.Equal(ReportTrigger.Cancel, cmd.Trigger);
        Assert.Null(cmd.CancelTarget);
    }

    [Theory]
    [InlineData("REPORT OAK30NUM", "OAK30NUM")]
    [InlineData("report oak30num", "OAK30NUM")]
    public void Parse_AtFix_LongName(string input, string fix)
    {
        ReportCommand cmd = Parse(input);
        Assert.Equal(ReportTrigger.AtFix, cmd.Trigger);
        Assert.Equal(fix, cmd.FixName);
    }

    [Theory]
    [InlineData("SUNOL", true)]
    [InlineData("OAK30NUM", true)]
    [InlineData("AB", true)]
    [InlineData("sunol", true)]
    [InlineData("A", false)]
    [InlineData("5", false)]
    [InlineData("5ABC", false)]
    [InlineData("OAK-30", false)]
    [InlineData("OFF", false)]
    [InlineData("STOP", false)]
    [InlineData("NONE", false)]
    [InlineData("CANCEL", false)]
    [InlineData("BASE", false)]
    [InlineData("FINAL", false)]
    [InlineData("DW", false)]
    [InlineData("XW", false)]
    [InlineData("MIDFIELD", false)]
    [InlineData("CROSSWIND", false)]
    [InlineData("DOWNWIND", false)]
    [InlineData("ALL", false)]
    [InlineData("off", false)]
    [InlineData("base", false)]
    [InlineData("dw", false)]
    [InlineData("all", false)]
    [InlineData("ÉCLAT", false)]
    [InlineData("", false)]
    public void IsReportFixName(string token, bool expected) => Assert.Equal(expected, CommandParser.IsReportFixName(token));

    [Fact]
    public void Parse_CancelMileFinal_ZeroDistance_Fails()
    {
        ParseResult<CompoundCommand> result = CommandParser.ParseCompound("REPORT OFF 0 FINAL");
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void DescribeNatural_Cancels_NameTheReport()
    {
        Assert.Equal("Cancel 5-mile final report", CommandDescriber.DescribeNatural(Parse("REPORT FINAL 5 OFF")));
        Assert.Equal("Cancel report at SUNOL", CommandDescriber.DescribeNatural(Parse("REPORT SUNOL OFF")));
    }

    [Fact]
    public void Parse_Midfield()
    {
        ReportCommand cmd = Parse("REPORT MIDFIELD");
        Assert.Equal(ReportTrigger.Midfield, cmd.Trigger);
        Assert.Null(cmd.DistanceNm);
        Assert.Null(cmd.FixName);
    }

    [Theory]
    [InlineData("REPORT OFF MIDFIELD")]
    [InlineData("REPORT MIDFIELD OFF")]
    public void Parse_CancelMidfield(string input)
    {
        ReportCommand cmd = Parse(input);
        Assert.Equal(ReportTrigger.Cancel, cmd.Trigger);
        Assert.Equal(ReportTrigger.Midfield, cmd.CancelTarget);
    }

    [Theory]
    [InlineData("REPORT MIDFIELD", "REPORT MIDFIELD")]
    [InlineData("REPORT MIDFIELD OFF", "REPORT OFF MIDFIELD")]
    public void CanonicalRoundTrip_Midfield(string input, string canonical) =>
        Assert.Equal(canonical, CommandDescriber.DescribeCommand(Parse(input)));

    [Fact]
    public void Parse_BareReport_Fails()
    {
        ParseResult<CompoundCommand> result = CommandParser.ParseCompound("REPORT");
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
    [InlineData("REPORT FINAL 5 OFF", "REPORT OFF 5 FINAL")]
    [InlineData("REPORT SUNOL OFF", "REPORT OFF SUNOL")]
    [InlineData("REPORT OAK30NUM", "REPORT OAK30NUM")]
    public void CanonicalRoundTrip(string input, string canonical) => Assert.Equal(canonical, CommandDescriber.DescribeCommand(Parse(input)));
}
