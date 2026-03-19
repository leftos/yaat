using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Tests;

public class HoldingEntryCalculatorTests
{
    // Right turns (standard pattern): Direct [0,110), Teardrop [110,250), Parallel [250,360)

    [Theory]
    [InlineData(0, 0, TurnDirection.Right, HoldingEntry.Direct)]
    [InlineData(90, 0, TurnDirection.Right, HoldingEntry.Direct)]
    [InlineData(109, 0, TurnDirection.Right, HoldingEntry.Direct)]
    [InlineData(110, 0, TurnDirection.Right, HoldingEntry.Teardrop)]
    [InlineData(180, 0, TurnDirection.Right, HoldingEntry.Teardrop)]
    [InlineData(249, 0, TurnDirection.Right, HoldingEntry.Teardrop)]
    [InlineData(250, 0, TurnDirection.Right, HoldingEntry.Parallel)]
    [InlineData(300, 0, TurnDirection.Right, HoldingEntry.Parallel)]
    [InlineData(359, 0, TurnDirection.Right, HoldingEntry.Parallel)]
    public void RightTurns_CorrectEntry(double heading, double inboundCourse, TurnDirection dir, HoldingEntry expected)
    {
        var result = HoldingEntryCalculator.ComputeEntry(new TrueHeading(heading), inboundCourse, dir);
        Assert.Equal(expected, result);
    }

    // Left turns (nonstandard): Parallel [0,110), Teardrop [110,250), Direct [250,360)

    [Theory]
    [InlineData(0, 0, TurnDirection.Left, HoldingEntry.Parallel)]
    [InlineData(90, 0, TurnDirection.Left, HoldingEntry.Parallel)]
    [InlineData(109, 0, TurnDirection.Left, HoldingEntry.Parallel)]
    [InlineData(110, 0, TurnDirection.Left, HoldingEntry.Teardrop)]
    [InlineData(180, 0, TurnDirection.Left, HoldingEntry.Teardrop)]
    [InlineData(249, 0, TurnDirection.Left, HoldingEntry.Teardrop)]
    [InlineData(250, 0, TurnDirection.Left, HoldingEntry.Direct)]
    [InlineData(300, 0, TurnDirection.Left, HoldingEntry.Direct)]
    [InlineData(359, 0, TurnDirection.Left, HoldingEntry.Direct)]
    public void LeftTurns_CorrectEntry(double heading, double inboundCourse, TurnDirection dir, HoldingEntry expected)
    {
        var result = HoldingEntryCalculator.ComputeEntry(new TrueHeading(heading), inboundCourse, dir);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(180, 090, TurnDirection.Right, HoldingEntry.Direct)]
    [InlineData(270, 090, TurnDirection.Right, HoldingEntry.Teardrop)]
    [InlineData(045, 090, TurnDirection.Right, HoldingEntry.Parallel)]
    public void NonZeroInboundCourse_CorrectEntry(double heading, double inboundCourse, TurnDirection dir, HoldingEntry expected)
    {
        var result = HoldingEntryCalculator.ComputeEntry(new TrueHeading(heading), inboundCourse, dir);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void WraparoundHeading_CorrectEntry()
    {
        // Heading 350, inbound 010 → theta = (350-10) = 340 → Parallel (right turns)
        var result = HoldingEntryCalculator.ComputeEntry(new TrueHeading(350), 10, TurnDirection.Right);
        Assert.Equal(HoldingEntry.Parallel, result);
    }
}
