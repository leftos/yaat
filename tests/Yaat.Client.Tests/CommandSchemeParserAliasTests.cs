using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Client.Tests;

public class CommandSchemeParserAliasTests
{
    private static readonly CommandScheme Scheme = CommandScheme.Default();

    [Fact]
    public void SayForceAlias_WithComma_PreservesLiteralText()
    {
        var result = CommandSchemeParser.ParseCompound("SAYF HELLO, WORLD", Scheme);

        Assert.NotNull(result);
        Assert.Equal("SAY HELLO, WORLD", result.CanonicalString);
    }

    // --- NormalizeSeparatorAliases: direct helper coverage ---

    [Theory]
    [InlineData("H180 AND D250", "H180 , D250")]
    [InlineData("H180 THEN D250", "H180 ; D250")]
    [InlineData("H180 AND D250 THEN CTO 28R", "H180 , D250 ; CTO 28R")]
    [InlineData("h180 then d250", "h180 ; d250")]
    [InlineData("H180 And D250", "H180 , D250")]
    [InlineData("H180,D250 AND CTO 28R", "H180,D250 , CTO 28R")]
    [InlineData("H180; D250 AND CTO 28R", "H180; D250 , CTO 28R")]
    public void NormalizeSeparatorAliases_SubstitutesOutsideSay(string input, string expected)
    {
        Assert.Equal(expected, CommandSchemeParser.NormalizeSeparatorAliases(input));
    }

    [Theory]
    [InlineData("SAYF READING YOU LOUD AND CLEAR")]
    [InlineData("SAYF GOOD THEN H180")]
    [InlineData("SAY MORNING AND GOODBYE")]
    [InlineData("sayf hello and world")]
    public void NormalizeSeparatorAliases_PreservesSayBlockArguments(string input)
    {
        Assert.Equal(input, CommandSchemeParser.NormalizeSeparatorAliases(input));
    }

    [Fact]
    public void NormalizeSeparatorAliases_ThenBeforeSayStillSubstitutes()
    {
        // THEN before SAYF substitutes to `;`; the AND inside the SAYF literal stays put.
        Assert.Equal("H180 ; SAYF GOOD AND CLEAR", CommandSchemeParser.NormalizeSeparatorAliases("H180 THEN SAYF GOOD AND CLEAR"));
    }

    [Fact]
    public void NormalizeSeparatorAliases_SaySubCommandPreservesAndUntilNextComma()
    {
        // After `,`, SAYF is at sub-command-start and its literal runs only until the next `,`.
        // AND inside that literal must NOT be rewritten.
        Assert.Equal("H180 , SAYF FOO AND BAR", CommandSchemeParser.NormalizeSeparatorAliases("H180 , SAYF FOO AND BAR"));
    }

    [Fact]
    public void NormalizeSeparatorAliases_SayBlockwideKeepsCommasLiteral()
    {
        // SAYF at block start absorbs subsequent commas as literal text — AND after a literal comma
        // inside the SAY argument must still NOT be substituted.
        Assert.Equal("SAYF HELLO, WORLD AND PEACE", CommandSchemeParser.NormalizeSeparatorAliases("SAYF HELLO, WORLD AND PEACE"));
    }

    [Fact]
    public void NormalizeSeparatorAliases_BlockBoundaryAfterSayResumesSubstitution()
    {
        // SAYF block ends at `;`; the next block resumes normal AND/THEN substitution.
        Assert.Equal("SAYF HELLO WORLD; H180 , D250", CommandSchemeParser.NormalizeSeparatorAliases("SAYF HELLO WORLD; H180 AND D250"));
    }

    // --- End-to-end ParseCompound: alias inputs produce same canonical as punctuation ---

    [Fact]
    public void ParseCompound_AndAlias_ProducesSameCanonicalAsComma()
    {
        var withAnd = CommandSchemeParser.ParseCompound("H180 AND D250", Scheme);
        var withComma = CommandSchemeParser.ParseCompound("H180, D250", Scheme);

        Assert.NotNull(withAnd);
        Assert.NotNull(withComma);
        Assert.Equal(withComma.CanonicalString, withAnd.CanonicalString);
    }

    [Fact]
    public void ParseCompound_ThenAlias_ProducesSameCanonicalAsSemicolon()
    {
        var withThen = CommandSchemeParser.ParseCompound("H180 THEN D250", Scheme);
        var withSemi = CommandSchemeParser.ParseCompound("H180; D250", Scheme);

        Assert.NotNull(withThen);
        Assert.NotNull(withSemi);
        Assert.Equal(withSemi.CanonicalString, withThen.CanonicalString);
    }

    [Fact]
    public void ParseCompound_MixedAndThen_ProducesSameCanonicalAsPunctuation()
    {
        var withAliases = CommandSchemeParser.ParseCompound("H180 AND D250 THEN CTO 28R", Scheme);
        var withPunct = CommandSchemeParser.ParseCompound("H180, D250; CTO 28R", Scheme);

        Assert.NotNull(withAliases);
        Assert.NotNull(withPunct);
        Assert.Equal(withPunct.CanonicalString, withAliases.CanonicalString);
    }

    [Fact]
    public void ParseCompound_SayfWithAndInLiteral_PreservesText()
    {
        var result = CommandSchemeParser.ParseCompound("SAYF READING YOU LOUD AND CLEAR", Scheme);

        Assert.NotNull(result);
        Assert.Equal("SAY READING YOU LOUD AND CLEAR", result.CanonicalString);
    }

    [Theory]
    [InlineData("SPEEDN 80")]
    [InlineData("SPDN 80")]
    [InlineData("SLN 80")]
    public void ParseCompound_ForceSpeedAliases_ProduceCanonicalSpdn(string input)
    {
        var result = CommandSchemeParser.ParseCompound(input, Scheme);

        Assert.NotNull(result);
        Assert.Equal("SPDN 80", result.CanonicalString);
    }
}
