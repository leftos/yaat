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
        var result = CommandParser.Parse("GA");
        Assert.True(result.IsSuccess, result.Reason);
        var ga = Assert.IsType<GoAroundCommand>(result.Value);
        Assert.Null(ga.AssignedMagneticHeading);
        Assert.Null(ga.TargetAltitude);
        Assert.Null(ga.TrafficPattern);
    }

    [Fact]
    public void GA_MRT_ParsesPatternRight()
    {
        var result = CommandParser.Parse("GA MRT");
        Assert.True(result.IsSuccess, result.Reason);
        var ga = Assert.IsType<GoAroundCommand>(result.Value);
        Assert.Null(ga.AssignedMagneticHeading);
        Assert.Null(ga.TargetAltitude);
        Assert.Equal(PatternDirection.Right, ga.TrafficPattern);
    }

    [Fact]
    public void GA_MLT_ParsesPatternLeft()
    {
        var result = CommandParser.Parse("GA MLT");
        Assert.True(result.IsSuccess, result.Reason);
        var ga = Assert.IsType<GoAroundCommand>(result.Value);
        Assert.Null(ga.AssignedMagneticHeading);
        Assert.Null(ga.TargetAltitude);
        Assert.Equal(PatternDirection.Left, ga.TrafficPattern);
    }

    [Fact]
    public void GA_HeadingOnly_ParsesSuccessfully()
    {
        var result = CommandParser.Parse("GA 315");
        Assert.True(result.IsSuccess, result.Reason);
        var ga = Assert.IsType<GoAroundCommand>(result.Value);
        Assert.NotNull(ga.AssignedMagneticHeading);
        Assert.Equal(315.0, ga.AssignedMagneticHeading.Value.Degrees);
        Assert.Null(ga.TargetAltitude);
        Assert.Null(ga.TrafficPattern);
    }

    [Fact]
    public void GA_RH_HeadingOnly_ParsesSuccessfully()
    {
        var result = CommandParser.Parse("GA RH");
        Assert.True(result.IsSuccess, result.Reason);
        var ga = Assert.IsType<GoAroundCommand>(result.Value);
        Assert.Null(ga.AssignedMagneticHeading);
        Assert.Null(ga.TargetAltitude);
        Assert.Null(ga.TrafficPattern);
    }

    [Fact]
    public void GA_HeadingAndAltitude_ParsesSuccessfully()
    {
        var result = CommandParser.Parse("GA 270 50");
        Assert.True(result.IsSuccess, result.Reason);
        var ga = Assert.IsType<GoAroundCommand>(result.Value);
        Assert.NotNull(ga.AssignedMagneticHeading);
        Assert.Equal(270.0, ga.AssignedMagneticHeading.Value.Degrees);
        Assert.Equal(5000, ga.TargetAltitude);
        Assert.Null(ga.TrafficPattern);
    }

    [Fact]
    public void GA_RH_AndAltitude_ParsesSuccessfully()
    {
        var result = CommandParser.Parse("GA RH 50");
        Assert.True(result.IsSuccess, result.Reason);
        var ga = Assert.IsType<GoAroundCommand>(result.Value);
        Assert.Null(ga.AssignedMagneticHeading);
        Assert.Equal(5000, ga.TargetAltitude);
        Assert.Null(ga.TrafficPattern);
    }

    [Fact]
    public void GA_InvalidHeading_Fails()
    {
        var result = CommandParser.Parse("GA 999");
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void GA_TooManyArgs_Fails()
    {
        var result = CommandParser.Parse("GA foo bar baz");
        Assert.False(result.IsSuccess);
    }
}
