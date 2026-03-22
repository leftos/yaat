using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

/// <summary>
/// Verifies that condition keywords (AT, LV, ATFN, etc.) after a comma are promoted
/// to new sequential blocks (treated as semicolon), so users can write
/// "cm 020, dct vpcol oak30num vpmid, at oak30num cm 014" naturally.
/// </summary>
public class CommaBeforeConditionTests
{
    private static readonly CommandScheme Scheme = CommandScheme.Default();

    private static readonly NavigationDatabase NavDb = TestNavDbFactory.WithFixNames("VPCOL", "OAK30NUM", "VPMID", "SUNOL", "BRIXX");

    public CommaBeforeConditionTests()
    {
        NavigationDatabase.SetInstance(NavDb);
    }

    [Theory]
    [InlineData("cm 020, dct vpcol oak30num vpmid, at oak30num cm 014", "CM 020, DCT VPCOL OAK30NUM VPMID; AT OAK30NUM CM 014")]
    [InlineData("cm 020, at 5000 dm 200", "CM 020; AT 5000 DM 200")]
    [InlineData("cm 020, lv 100 dm 200", "CM 020; LV 100 DM 200")]
    [InlineData("fh 090, at sunol cm 030", "FH 090; AT SUNOL CM 030")]
    public void CommaBeforeCondition_PromotedToSemicolon(string input, string expected)
    {
        var result = CommandSchemeParser.ParseCompound(input, Scheme, out var failure);

        Assert.NotNull(result);
        Assert.Null(failure);
        Assert.Equal(expected, result.CanonicalString);
    }

    [Fact]
    public void SemicolonBeforeCondition_StillWorks()
    {
        var result = CommandSchemeParser.ParseCompound("cm 020; at oak30num cm 014", Scheme, out var failure);

        Assert.NotNull(result);
        Assert.Null(failure);
        Assert.Equal("CM 020; AT OAK30NUM CM 014", result.CanonicalString);
    }

    [Fact]
    public void PlainCommaParallelCommands_Unaffected()
    {
        var result = CommandSchemeParser.ParseCompound("cm 020, fh 090", Scheme, out var failure);

        Assert.NotNull(result);
        Assert.Null(failure);
        Assert.Equal("CM 020, FH 090", result.CanonicalString);
    }
}
