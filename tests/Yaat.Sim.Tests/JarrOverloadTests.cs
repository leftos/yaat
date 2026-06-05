using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

/// <summary>
/// Parser coverage for the JARR overloads added in issue #187:
/// <c>JARR star</c> / <c>JARR star entryFix</c> / <c>JARR star runway</c> /
/// <c>JARR star entryFix runway</c>. A runway-shaped second token (27, 26R) is the runway
/// transition; a fix name is the entry fix. The STAR id may omit its version (handler resolves).
/// </summary>
public class JarrOverloadTests
{
    private static JoinStarCommand ParseJarr(string input)
    {
        using var _ = NavigationDatabase.ScopedOverride(NavigationDatabase.ForTesting());
        var result = CommandParser.Parse(input);
        Assert.True(result.IsSuccess, $"parse failed: {input}");
        return Assert.IsType<JoinStarCommand>(result.Value);
    }

    [Fact]
    public void Jarr_StarOnly()
    {
        var cmd = ParseJarr("JARR TEJAS5");
        Assert.Equal("TEJAS5", cmd.StarId);
        Assert.Null(cmd.Transition);
        Assert.Null(cmd.RunwayTransition);
    }

    [Fact]
    public void Jarr_StarEntryFix_SecondTokenIsEntryFix()
    {
        var cmd = ParseJarr("JARR TEJAS5 RIDLR");
        Assert.Equal("TEJAS5", cmd.StarId);
        Assert.Equal("RIDLR", cmd.Transition);
        Assert.Null(cmd.RunwayTransition);
    }

    [Theory]
    [InlineData("JARR TEJAS5 27", "27")]
    [InlineData("JARR DRLLR5 26R", "26R")]
    [InlineData("JARR TEJAS5 13L", "13L")]
    public void Jarr_StarRunway_RunwayShapedSecondTokenIsRunwayTransition(string input, string expectedRunway)
    {
        var cmd = ParseJarr(input);
        Assert.Null(cmd.Transition);
        Assert.Equal(expectedRunway, cmd.RunwayTransition);
    }

    [Fact]
    public void Jarr_StarEntryFixRunway_ThreeTokens()
    {
        var cmd = ParseJarr("JARR TEJAS5 RIDLR 27");
        Assert.Equal("TEJAS5", cmd.StarId);
        Assert.Equal("RIDLR", cmd.Transition);
        Assert.Equal("27", cmd.RunwayTransition);
    }

    [Fact]
    public void Jarr_VersionlessStar_KeepsStarTokenForHandlerResolution()
    {
        var cmd = ParseJarr("JARR TEJAS 27");
        Assert.Equal("TEJAS", cmd.StarId);
        Assert.Null(cmd.Transition);
        Assert.Equal("27", cmd.RunwayTransition);
    }

    [Theory]
    [InlineData("JARR TEJAS5")]
    [InlineData("JARR TEJAS5 RIDLR")]
    [InlineData("JARR TEJAS5 27")]
    [InlineData("JARR TEJAS5 RIDLR 27")]
    public void Jarr_CanonicalRoundTrips(string canonical)
    {
        var cmd = ParseJarr(canonical);
        Assert.Equal(canonical, CommandDescriber.DescribeCommand(cmd));
    }
}
