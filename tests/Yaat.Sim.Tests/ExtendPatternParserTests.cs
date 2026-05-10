using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Tests;

/// <summary>
/// Tests for EXT / EXTEND parsing: bare, full leg names, short leg names,
/// case insensitivity, alias parity, and rejection of non-leg arguments.
/// </summary>
public class ExtendPatternParserTests
{
    [Fact]
    public void Bare_EXT_ParsesWithNullLeg()
    {
        var result = CommandParser.Parse("EXT");
        Assert.True(result.IsSuccess, result.Reason);
        var ext = Assert.IsType<ExtendPatternCommand>(result.Value);
        Assert.Null(ext.Leg);
    }

    [Fact]
    public void Bare_EXTEND_AliasParsesWithNullLeg()
    {
        var result = CommandParser.Parse("EXTEND");
        Assert.True(result.IsSuccess, result.Reason);
        var ext = Assert.IsType<ExtendPatternCommand>(result.Value);
        Assert.Null(ext.Leg);
    }

    [Theory]
    [InlineData("EXT UPWIND", PatternEntryLeg.Upwind)]
    [InlineData("EXT UW", PatternEntryLeg.Upwind)]
    [InlineData("EXT CROSSWIND", PatternEntryLeg.Crosswind)]
    [InlineData("EXT CW", PatternEntryLeg.Crosswind)]
    [InlineData("EXT DOWNWIND", PatternEntryLeg.Downwind)]
    [InlineData("EXT DW", PatternEntryLeg.Downwind)]
    public void EXT_LegArgument_ParsesToCorrectLeg(string input, PatternEntryLeg expected)
    {
        var result = CommandParser.Parse(input);
        Assert.True(result.IsSuccess, result.Reason);
        var ext = Assert.IsType<ExtendPatternCommand>(result.Value);
        Assert.Equal(expected, ext.Leg);
    }

    [Theory]
    [InlineData("EXTEND UPWIND", PatternEntryLeg.Upwind)]
    [InlineData("EXTEND CW", PatternEntryLeg.Crosswind)]
    [InlineData("EXTEND DW", PatternEntryLeg.Downwind)]
    public void EXTEND_AliasWithLeg_ParsesIdenticallyToEXT(string input, PatternEntryLeg expected)
    {
        var result = CommandParser.Parse(input);
        Assert.True(result.IsSuccess, result.Reason);
        var ext = Assert.IsType<ExtendPatternCommand>(result.Value);
        Assert.Equal(expected, ext.Leg);
    }

    [Theory]
    [InlineData("EXT upwind", PatternEntryLeg.Upwind)]
    [InlineData("EXT Cw", PatternEntryLeg.Crosswind)]
    [InlineData("EXT dW", PatternEntryLeg.Downwind)]
    public void EXT_LegArgument_IsCaseInsensitive(string input, PatternEntryLeg expected)
    {
        var result = CommandParser.Parse(input);
        Assert.True(result.IsSuccess, result.Reason);
        var ext = Assert.IsType<ExtendPatternCommand>(result.Value);
        Assert.Equal(expected, ext.Leg);
    }

    [Theory]
    [InlineData("EXT BASE")]
    [InlineData("EXT FINAL")]
    [InlineData("EXT FN")]
    [InlineData("EXT XW")]
    [InlineData("EXT 5")]
    [InlineData("EXT BOGUS")]
    public void EXT_UnsupportedArgument_FailsWithHelpfulMessage(string input)
    {
        var result = CommandParser.Parse(input);
        Assert.False(result.IsSuccess);
        Assert.Contains("UPWIND", result.Reason!, StringComparison.OrdinalIgnoreCase);
    }
}
