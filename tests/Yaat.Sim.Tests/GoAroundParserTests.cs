using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Tests;

/// <summary>
/// Tests for all GA (go-around) command parsing forms:
/// GA, GA MRT, GA MLT, GA {heading}, GA RH, GA {heading} {altitude}, GA RH {altitude}.
/// </summary>
public class GoAroundParserTests
{
    [Fact]
    public void GA_NoArgs_ParsesSuccessfully()
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse("GA");
        Assert.True(result.IsSuccess, result.Reason);
        GoAroundCommand ga = Assert.IsType<GoAroundCommand>(result.Value);
        Assert.Null(ga.AssignedMagneticHeading);
        Assert.Null(ga.TargetAltitude);
        Assert.Null(ga.TrafficPattern);
    }

    [Fact]
    public void GA_MRT_ParsesPatternRight()
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse("GA MRT");
        Assert.True(result.IsSuccess, result.Reason);
        GoAroundCommand ga = Assert.IsType<GoAroundCommand>(result.Value);
        Assert.Null(ga.AssignedMagneticHeading);
        Assert.Null(ga.TargetAltitude);
        Assert.Equal(PatternDirection.Right, ga.TrafficPattern);
    }

    [Fact]
    public void GA_MLT_ParsesPatternLeft()
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse("GA MLT");
        Assert.True(result.IsSuccess, result.Reason);
        GoAroundCommand ga = Assert.IsType<GoAroundCommand>(result.Value);
        Assert.Null(ga.AssignedMagneticHeading);
        Assert.Null(ga.TargetAltitude);
        Assert.Equal(PatternDirection.Left, ga.TrafficPattern);
    }

    [Fact]
    public void GA_HeadingOnly_ParsesSuccessfully()
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse("GA 315");
        Assert.True(result.IsSuccess, result.Reason);
        GoAroundCommand ga = Assert.IsType<GoAroundCommand>(result.Value);
        Assert.NotNull(ga.AssignedMagneticHeading);
        Assert.Equal(315.0, ga.AssignedMagneticHeading.Value.Degrees);
        Assert.Null(ga.TargetAltitude);
        Assert.Null(ga.TrafficPattern);
    }

    [Fact]
    public void GA_RH_HeadingOnly_ParsesSuccessfully()
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse("GA RH");
        Assert.True(result.IsSuccess, result.Reason);
        GoAroundCommand ga = Assert.IsType<GoAroundCommand>(result.Value);
        Assert.Null(ga.AssignedMagneticHeading);
        Assert.Null(ga.TargetAltitude);
        Assert.Null(ga.TrafficPattern);
    }

    /// <summary>Only an explicit RH records the runway-heading instruction; a bare GA and a numbered heading do not.</summary>
    [Theory]
    [InlineData("GA RH", true)]
    [InlineData("GA RH 50", true)]
    [InlineData("GA", false)]
    [InlineData("GA 270", false)]
    [InlineData("GA MLT", false)]
    public void GA_RunwayHeading_IsRecordedOnlyForRh(string text, bool expected)
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse(text);
        Assert.True(result.IsSuccess, result.Reason);
        GoAroundCommand ga = Assert.IsType<GoAroundCommand>(result.Value);
        Assert.Equal(expected, ga.RunwayHeading);
    }

    /// <summary>The canonical text names only what the go-around carries: no trailing space when there is no altitude.</summary>
    [Theory]
    [InlineData("GA", "GA")]
    [InlineData("GA RH", "GA RH")]
    [InlineData("GA 270", "GA 270")]
    [InlineData("GA RH 50", "GA RH 5000")]
    [InlineData("GA 270 30", "GA 270 3000")]
    [InlineData("GA MLT", "GA MLT")]
    public void GA_CanonicalText_HasNoTrailingSpace(string text, string expected)
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse(text);
        Assert.True(result.IsSuccess, result.Reason);
        Assert.Equal(expected, CommandDescriber.DescribeCommand(result.Value!));
    }

    [Fact]
    public void GA_HeadingAndAltitude_ParsesSuccessfully()
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse("GA 270 50");
        Assert.True(result.IsSuccess, result.Reason);
        GoAroundCommand ga = Assert.IsType<GoAroundCommand>(result.Value);
        Assert.NotNull(ga.AssignedMagneticHeading);
        Assert.Equal(270.0, ga.AssignedMagneticHeading.Value.Degrees);
        Assert.Equal(5000, ga.TargetAltitude);
        Assert.Null(ga.TrafficPattern);
    }

    [Fact]
    public void GA_RH_AndAltitude_ParsesSuccessfully()
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse("GA RH 50");
        Assert.True(result.IsSuccess, result.Reason);
        GoAroundCommand ga = Assert.IsType<GoAroundCommand>(result.Value);
        Assert.Null(ga.AssignedMagneticHeading);
        Assert.Equal(5000, ga.TargetAltitude);
        Assert.Null(ga.TrafficPattern);
    }

    [Fact]
    public void GA_InvalidHeading_Fails()
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse("GA 999");
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void GA_TooManyArgs_Fails()
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse("GA foo bar baz");
        Assert.False(result.IsSuccess);
    }
}
