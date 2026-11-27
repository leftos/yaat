using Xunit;
using Yaat.Sim.Authoring.Stamping;

namespace Yaat.Sim.Tests.Authoring.Stamping;

/// <summary>
/// <see cref="StarAliasing.Substitute"/> replaces whole route tokens and the STAR part of a STAR.TRANS token, never a longer
/// or prefixed token.
/// </summary>
public class StarAliasingTests
{
    private static readonly StarAlias Bdega = new("BDEGA4", "STLER3");

    public StarAliasingTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void Substitute_WholeToken_IsReplaced()
    {
        StarAliasResult result = StarAliasing.Substitute("KSFO BDEGA4 KLAX", [Bdega]);

        Assert.Equal("KSFO STLER3 KLAX", result.RouteText);
        StarAliasReplacement replacement = Assert.Single(result.Replacements);
        Assert.Equal(new StarAliasReplacement(1, "BDEGA4", "STLER3", Bdega), replacement);
    }

    [Theory]
    [InlineData("KSFO BDEGA45 KLAX")]
    [InlineData("KSFO XBDEGA4 KLAX")]
    public void Substitute_LongerOrPrefixedToken_IsUnchanged(string route)
    {
        StarAliasResult result = StarAliasing.Substitute(route, [Bdega]);

        Assert.Equal(route, result.RouteText);
        Assert.Empty(result.Replacements);
    }

    [Fact]
    public void Substitute_DottedToken_ReplacesOnlyTheStarPart()
    {
        StarAliasResult result = StarAliasing.Substitute("KSFO BDEGA4.SUNOL", [Bdega]);

        Assert.Equal("KSFO STLER3.SUNOL", result.RouteText);
        Assert.Equal("BDEGA4.SUNOL", Assert.Single(result.Replacements).OldToken);
    }

    [Fact]
    public void Substitute_LowerCaseToken_MatchesAndOutputIsUpperCase()
    {
        StarAliasResult result = StarAliasing.Substitute("ksfo bdega4 klax", [Bdega]);

        Assert.Equal("KSFO STLER3 KLAX", result.RouteText);
    }

    [Fact]
    public void Substitute_TwoAliases_ApplyInOrder()
    {
        StarAliasResult result = StarAliasing.Substitute("KSFO BDEGA4 KLAX", [Bdega, new StarAlias("STLER3", "SERFR4")]);

        Assert.Equal("KSFO SERFR4 KLAX", result.RouteText);
        StarAliasReplacement replacement = Assert.Single(result.Replacements);
        Assert.Equal(("BDEGA4", "SERFR4"), (replacement.OldToken, replacement.NewToken));
    }

    [Fact]
    public void Substitute_ChainBackToTheStart_LeavesNoReplacement()
    {
        StarAliasResult result = StarAliasing.Substitute("A", [new StarAlias("A", "B"), new StarAlias("B", "A")]);

        Assert.Equal("A", result.RouteText);
        Assert.Empty(result.Replacements);
    }

    [Theory]
    [InlineData("BDE GA4", "STLER3")]
    [InlineData("BDEGA4", "STL ER3")]
    [InlineData("BDEGA4.SUNOL", "STLER3")]
    [InlineData("BDEGA4", "STLER3.SUNOL")]
    public void Substitute_AliasWithSpaceOrDot_IsRejectedAndListed(string from, string to)
    {
        var bad = new StarAlias(from, to);

        StarAliasResult result = StarAliasing.Substitute("KSFO BDEGA4 BDEGA4.SUNOL KLAX", [bad]);

        Assert.Equal("KSFO BDEGA4 BDEGA4.SUNOL KLAX", result.RouteText);
        Assert.Empty(result.Replacements);
        Assert.Equal(bad, Assert.Single(result.RejectedAliases));
    }

    [Fact]
    public void Substitute_ToEqualsFrom_IsANoOp()
    {
        StarAliasResult result = StarAliasing.Substitute("KSFO BDEGA4 KLAX", [new StarAlias("BDEGA4", "bdega4")]);

        Assert.Equal("KSFO BDEGA4 KLAX", result.RouteText);
        Assert.Empty(result.Replacements);
    }

    [Theory]
    [InlineData("", "STLER3")]
    [InlineData("BDEGA4", " ")]
    public void Substitute_EmptyAlias_IsSkipped(string from, string to)
    {
        StarAliasResult result = StarAliasing.Substitute("KSFO BDEGA4 KLAX", [new StarAlias(from, to), Bdega]);

        Assert.Equal("KSFO STLER3 KLAX", result.RouteText);
        Assert.Single(result.Replacements);
    }
}
