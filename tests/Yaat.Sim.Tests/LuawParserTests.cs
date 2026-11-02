using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

/// <summary>
/// Parser coverage for LUAW and its IMM/WD/ND "without delay" modifier
/// ("line up and wait, without delay", 7110.65 §3-7-2.b.10).
/// </summary>
public class LuawParserTests
{
    [Fact]
    public void BareLuaw_IsNotWithoutDelay()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("LUAW");
        LineUpAndWaitCommand luaw = Assert.IsType<LineUpAndWaitCommand>(cmd.Value);
        Assert.False(luaw.WithoutDelay);
        Assert.Equal("LUAW", CommandDescriber.DescribeCommand(luaw));
        Assert.Equal("Line up and wait", CommandDescriber.DescribeNatural(luaw));
    }

    [Theory]
    [InlineData("LUAW WD")]
    [InlineData("LUAW ND")]
    [InlineData("LUAW IMM")]
    public void Luaw_WithoutDelayAliases_SetWithoutDelay(string input)
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse(input);
        LineUpAndWaitCommand luaw = Assert.IsType<LineUpAndWaitCommand>(cmd.Value);
        Assert.True(luaw.WithoutDelay);
        Assert.Equal("LUAW WD", CommandDescriber.DescribeCommand(luaw));
        Assert.Equal("Line up and wait, without delay", CommandDescriber.DescribeNatural(luaw));
    }

    [Theory]
    [InlineData("POS WD")]
    [InlineData("LU ND")]
    [InlineData("PH IMM")]
    public void Luaw_WithoutDelay_WorksOnAllAliases(string input)
    {
        LineUpAndWaitCommand luaw = Assert.IsType<LineUpAndWaitCommand>(CommandParser.Parse(input).Value);
        Assert.True(luaw.WithoutDelay);
    }

    [Fact]
    public void Luaw_WithoutDelay_CanonicalRoundTrips()
    {
        string canonical = CommandDescriber.DescribeCommand(Assert.IsType<LineUpAndWaitCommand>(CommandParser.Parse("LUAW WD").Value));
        LineUpAndWaitCommand reparsed = Assert.IsType<LineUpAndWaitCommand>(CommandParser.Parse(canonical).Value);
        Assert.True(reparsed.WithoutDelay);
    }

    [Fact]
    public void Luaw_Runway_CarriesTheRunway()
    {
        LineUpAndWaitCommand luaw = Assert.IsType<LineUpAndWaitCommand>(CommandParser.Parse("LUAW 28L").Value);
        Assert.Equal("28L", luaw.Runway);
        Assert.False(luaw.WithoutDelay);
        Assert.Equal("LUAW 28L", CommandDescriber.DescribeCommand(luaw));
        Assert.Equal("Runway 28L, line up and wait", CommandDescriber.DescribeNatural(luaw));
    }

    [Fact]
    public void Luaw_RunwayThenWithoutDelay_CarriesBoth()
    {
        LineUpAndWaitCommand luaw = Assert.IsType<LineUpAndWaitCommand>(CommandParser.Parse("LUAW 28L WD").Value);
        Assert.Equal("28L", luaw.Runway);
        Assert.True(luaw.WithoutDelay);
        Assert.Equal("LUAW 28L WD", CommandDescriber.DescribeCommand(luaw));
        Assert.Equal("Runway 28L, line up and wait, without delay", CommandDescriber.DescribeNatural(luaw));
        Assert.Equal(luaw, CommandParser.Parse(CommandDescriber.DescribeCommand(luaw)).Value);
    }

    [Fact]
    public void Luaw_OneDigitRunway_IsTheZeroPaddedRunway()
    {
        LineUpAndWaitCommand luaw = Assert.IsType<LineUpAndWaitCommand>(CommandParser.Parse("POS 4").Value);
        Assert.Equal("04", luaw.Runway);
        Assert.Equal(luaw, CommandParser.Parse("LUAW 04").Value);
    }

    [Theory]
    [InlineData("LUAW RWY 28L")]
    [InlineData("LUAW 330")]
    [InlineData("LUAW 45")]
    [InlineData("LUAW WD 28L")]
    [InlineData("LUAW 28L 28R")]
    public void Luaw_RunwayNotLeadingOrNotARunway_IsRefused(string input)
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse(input);
        Assert.False(cmd.IsSuccess, $"{input} parsed as {cmd.Value}");
        Assert.Contains("LUAW does not understand", cmd.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LuawRunway_IsNotSplitIntoTwoCommands()
    {
        Assert.DoesNotContain(",", CommandSchemeParser.ExpandMultiCommand("LUAW 28L WD"));
        CompoundParseResult? scheme = CommandSchemeParser.ParseCompound("LUAW 28L WD", CommandScheme.Default());
        Assert.NotNull(scheme);
        Assert.Equal("LUAW 28L WD", scheme.CanonicalString);
    }

    [Fact]
    public void Luaw_TrailingJunk_Fails()
    {
        ParseResult<ParsedCommand> cmd = CommandParser.Parse("LUAW JUNK");
        Assert.False(cmd.IsSuccess);
    }
}
