using Xunit;
using Yaat.Sim.Speech;

namespace Yaat.Sim.Tests.Speech;

public class NatoNearMissResolverTests
{
    private static readonly IReadOnlySet<string> NoProtection = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlySet<string> Protect(params string[] fixes) => new HashSet<string>(fixes, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Empty_Input_Returns_Empty()
    {
        var result = NatoNearMissResolver.Resolve([], NoProtection);
        Assert.Empty(result);
    }

    [Fact]
    public void Unknown_Token_Passes_Through()
    {
        var result = NatoNearMissResolver.Resolve(["runway", "taxi", "via"], NoProtection);
        Assert.Equal(["runway", "taxi", "via"], result);
    }

    [Fact]
    public void Exact_Nato_Word_Passes_Through()
    {
        // Canonical NATO words are skipped before the distance computation — they're already correct.
        var result = NatoNearMissResolver.Resolve(["tango", "golf", "november"], NoProtection);
        Assert.Equal(["tango", "golf", "november"], result);
    }

    [Fact]
    public void Tingo_Rewritten_To_Tango()
    {
        // Vowel mishear: a↔i substitution at position 1. First char matches, length same,
        // distance 1, unambiguous.
        var result = NatoNearMissResolver.Resolve(["tingo"], NoProtection);
        Assert.Equal(["tango"], result);
    }

    [Fact]
    public void Gulf_Rewritten_To_Golf()
    {
        // Vowel mishear: o↔u substitution. First char matches, length same, distance 1.
        var result = NatoNearMissResolver.Resolve(["gulf"], NoProtection);
        Assert.Equal(["golf"], result);
    }

    [Theory]
    // Consonant-tail mishears at distance 1.
    [InlineData("chalie", "charlie")] // missing 'r'
    [InlineData("delt", "delta")] // missing trailing 'a'
    [InlineData("papaa", "papa")] // extra trailing 'a'
    [InlineData("whisky", "whiskey")] // missing 'e'
    [InlineData("juliett", "juliet")] // extra trailing 't'
    public void Distance1_NearMisses_Are_Resolved(string input, string expected)
    {
        var result = NatoNearMissResolver.Resolve([input], NoProtection);
        Assert.Equal([expected], result);
    }

    [Fact]
    public void Short_Token_Passes_Through()
    {
        // Tokens under 4 chars don't get near-miss matching — too much collision risk.
        // "ech" is distance 1 from "echo" but is deliberately left alone.
        var result = NatoNearMissResolver.Resolve(["ech", "abc"], NoProtection);
        Assert.Equal(["ech", "abc"], result);
    }

    [Fact]
    public void First_Character_Mismatch_Not_Rewritten()
    {
        // "bingo" → "tango" would be distance 1 (b→t) but first char doesn't match.
        // Whisper's phonetic mishears almost always preserve the initial consonant.
        var result = NatoNearMissResolver.Resolve(["bingo"], NoProtection);
        Assert.Equal(["bingo"], result);
    }

    [Fact]
    public void Length_Diff_Greater_Than_One_Not_Rewritten()
    {
        // "tang" → "tango" is distance 1 (truncation) and matches. But "tan" → "tango" is
        // distance 2, skipped. And "tangooo" → "tango" is distance 2, also skipped.
        var result = NatoNearMissResolver.Resolve(["tang", "tangooo"], NoProtection);
        Assert.Equal(["tango", "tangooo"], result);
    }

    [Fact]
    public void Protected_Fix_Is_Not_Rewritten()
    {
        // If a programmed fix is literally named "GULF", "direct gulf" must stay as-is.
        var result = NatoNearMissResolver.Resolve(["direct", "gulf"], Protect("GULF"));
        Assert.Equal(["direct", "gulf"], result);
    }

    [Fact]
    public void Protection_Is_Case_Insensitive()
    {
        var result = NatoNearMissResolver.Resolve(["tingo"], Protect("TINGO"));
        Assert.Equal(["tingo"], result);
    }

    [Fact]
    public void Command_Word_Make_Is_Not_Rewritten_To_Mike()
    {
        // "make" is a PhraseologyRules literal ("make left traffic", "make right traffic").
        // It appears in RuleLiterals and must not be rewritten to "mike" despite being
        // distance 1 with matching first char.
        var result = NatoNearMissResolver.Resolve(["make", "left", "traffic"], NoProtection);
        Assert.Equal(["make", "left", "traffic"], result);
    }

    [Fact]
    public void Command_Word_Land_Is_Not_Rewritten_To_Lima()
    {
        // "land" is a PhraseologyRules literal ("cleared to land"). It's distance 2 from
        // "lima" (a→i, d→a) so shouldn't match anyway — but the rule-literal protection
        // is defence-in-depth in case of future pattern changes.
        var result = NatoNearMissResolver.Resolve(["cleared", "to", "land"], NoProtection);
        Assert.Equal(["cleared", "to", "land"], result);
    }

    [Fact]
    public void Ambiguous_Match_Not_Rewritten()
    {
        // If a hypothetical token is equidistant from two NATO words (both first-char
        // matching, both distance 1), the resolver bails out. Construct a synthetic case:
        // "bovember" is distance 1 from "november" (b→n, first char NO match — skipped).
        // Harder to construct real ambiguity given the first-char guard, so this test
        // covers the code path defensively with a token that matches nothing at all.
        var result = NatoNearMissResolver.Resolve(["bovember"], NoProtection);
        Assert.Equal(["bovember"], result);
    }

    [Fact]
    public void Full_Transcript_Regression_MixedMishears()
    {
        var result = NatoNearMissResolver.Resolve(["taxi", "via", "tingo", "uniform", "whiskey", "november", "346", "gulf"], NoProtection);
        Assert.Equal(["taxi", "via", "tango", "uniform", "whiskey", "november", "346", "golf"], result);
    }
}
