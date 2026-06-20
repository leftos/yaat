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
        var cmd = CommandParser.Parse("LUAW");
        var luaw = Assert.IsType<LineUpAndWaitCommand>(cmd.Value);
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
        var cmd = CommandParser.Parse(input);
        var luaw = Assert.IsType<LineUpAndWaitCommand>(cmd.Value);
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
        var luaw = Assert.IsType<LineUpAndWaitCommand>(CommandParser.Parse(input).Value);
        Assert.True(luaw.WithoutDelay);
    }

    [Fact]
    public void Luaw_WithoutDelay_CanonicalRoundTrips()
    {
        var canonical = CommandDescriber.DescribeCommand(Assert.IsType<LineUpAndWaitCommand>(CommandParser.Parse("LUAW WD").Value));
        var reparsed = Assert.IsType<LineUpAndWaitCommand>(CommandParser.Parse(canonical).Value);
        Assert.True(reparsed.WithoutDelay);
    }

    [Fact]
    public void Luaw_TrailingJunk_Fails()
    {
        var cmd = CommandParser.Parse("LUAW JUNK");
        Assert.False(cmd.IsSuccess);
    }
}
