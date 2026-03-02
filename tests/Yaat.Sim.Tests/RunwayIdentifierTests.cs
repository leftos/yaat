using Xunit;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests;

public class RunwayIdentifierTests
{
    [Theory]
    [InlineData("28R", "10L")]
    [InlineData("10L", "28R")]
    [InlineData("36", "18")]
    [InlineData("9", "27")]
    [InlineData("1C", "19C")]
    public void TwoEndConstructor_StoresBothEnds(string end1, string end2)
    {
        var id = new RunwayIdentifier(end1, end2);

        Assert.Equal(end1, id.End1);
        Assert.Equal(end2, id.End2);
    }

    [Theory]
    [InlineData("10L", "28R")]
    [InlineData("28R", "10L")]
    [InlineData("36", "18")]
    [InlineData("18", "36")]
    [InlineData("9", "27")]
    [InlineData("1", "19")]
    [InlineData("10C", "28C")]
    [InlineData("12R", "30L")]
    public void SingleEndConstructor_InfersOpposite(string designator, string expectedOpposite)
    {
        var id = new RunwayIdentifier(designator);

        Assert.Equal(designator, id.End1);
        Assert.Equal(expectedOpposite, id.End2);
    }

    [Theory]
    [InlineData("28R/10L", "28R", "10L")]
    [InlineData("36 - 18", "36", "18")]
    [InlineData("28R", "28R", "10L")]
    [InlineData(" 10L / 28R ", "10L", "28R")]
    public void Parse_VariousFormats(string input, string expectedEnd1, string expectedEnd2)
    {
        var id = RunwayIdentifier.Parse(input);

        Assert.Equal(expectedEnd1, id.End1);
        Assert.Equal(expectedEnd2, id.End2);
    }

    [Fact]
    public void Equals_OrderIndependent()
    {
        var a = new RunwayIdentifier("28R", "10L");
        var b = new RunwayIdentifier("10L", "28R");

        Assert.Equal(a, b);
        Assert.True(a == b);
    }

    [Fact]
    public void Equals_DifferentRunways_NotEqual()
    {
        var a = new RunwayIdentifier("28R", "10L");
        var b = new RunwayIdentifier("28L", "10R");

        Assert.NotEqual(a, b);
        Assert.True(a != b);
    }

    [Fact]
    public void GetHashCode_OrderIndependent()
    {
        var a = new RunwayIdentifier("28R", "10L");
        var b = new RunwayIdentifier("10L", "28R");

        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Theory]
    [InlineData("28R", true)]
    [InlineData("10L", true)]
    [InlineData("28r", true)]
    [InlineData("10l", true)]
    [InlineData("28L", false)]
    [InlineData("9", false)]
    public void Contains_MatchesEitherEnd(string designator, bool expected)
    {
        var id = new RunwayIdentifier("28R", "10L");

        Assert.Equal(expected, id.Contains(designator));
    }

    [Fact]
    public void Overlaps_SharedDesignator_ReturnsTrue()
    {
        var a = new RunwayIdentifier("28R", "10L");
        var b = new RunwayIdentifier("28R", "10L");

        Assert.True(a.Overlaps(b));
    }

    [Fact]
    public void Overlaps_NoSharedDesignator_ReturnsFalse()
    {
        var a = new RunwayIdentifier("28R", "10L");
        var b = new RunwayIdentifier("28L", "10R");

        Assert.False(a.Overlaps(b));
    }

    [Theory]
    [InlineData("28R", "10L", "28R/10L")]
    [InlineData("10L", "28R", "10L/28R")]
    public void ToString_PreservesConstructionOrder(string end1, string end2, string expected)
    {
        var id = new RunwayIdentifier(end1, end2);

        Assert.Equal(expected, id.ToString());
    }

    [Fact]
    public void ToString_SameEnds_ReturnsSingle()
    {
        var id = new RunwayIdentifier("28R", "28R");

        Assert.Equal("28R", id.ToString());
    }

    [Theory]
    [InlineData("10L", "28R")]
    [InlineData("10R", "28L")]
    [InlineData("10C", "28C")]
    [InlineData("10", "28")]
    [InlineData("36", "18")]
    [InlineData("1", "19")]
    [InlineData("9", "27")]
    [InlineData("12R", "30L")]
    public void ComputeOpposite_Correct(string input, string expected)
    {
        Assert.Equal(expected, RunwayIdentifier.ComputeOpposite(input));
    }
}
