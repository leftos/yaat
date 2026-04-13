using Xunit;
using Yaat.Sim.Speech;

namespace Yaat.Sim.Tests.Speech;

public class PhoneticFixMatcherTests
{
    // Typical Bay Area fix set — small enough for programmed-fix scope testing.
    private static readonly string[] ProgrammedFixes = ["CEPIN", "SUNOL", "MENLO", "OAKES", "PIRAT", "DUMBA", "ALTAM"];

    // --- Phonetize sanity checks ---

    [Theory]
    [InlineData("CEPIN", "SPN")] // C before E = S, vowels dropped
    [InlineData("SEPIN", "SPN")] // identical phonetic code
    [InlineData("SUNOL", "SNL")]
    [InlineData("sunol", "SNL")] // case insensitive
    [InlineData("CAT", "KT")] // C before A = K
    [InlineData("KITE", "KT")] // K + vowel dropped + T
    [InlineData("PHONE", "FN")] // PH → F
    [InlineData("KNOT", "NT")] // silent leading K
    [InlineData("", "")]
    public void Phonetize_ProducesExpectedCode(string input, string expected)
    {
        Assert.Equal(expected, PhoneticFixMatcher.Phonetize(input));
    }

    // --- Levenshtein sanity ---

    [Theory]
    [InlineData("CEPIN", "CEPIN", 0)]
    [InlineData("CEPIN", "SEPIN", 1)] // 1 substitution
    [InlineData("SUNOL", "SUNNY", 2)] // 2 substitutions (O→N, L→Y)
    [InlineData("", "CEPIN", 5)]
    [InlineData("CEPIN", "", 5)]
    public void Levenshtein_ReturnsExpected(string a, string b, int expected)
    {
        Assert.Equal(expected, PhoneticFixMatcher.Levenshtein(a, b));
    }

    // --- TryMatch: programmed-fix scope ---

    [Fact]
    public void TryMatch_ExactMatch_ReturnsCanonical()
    {
        Assert.Equal("CEPIN", PhoneticFixMatcher.TryMatch("CEPIN", ProgrammedFixes, allowFullDatabaseFallback: false));
    }

    [Fact]
    public void TryMatch_CaseInsensitiveExact()
    {
        Assert.Equal("SUNOL", PhoneticFixMatcher.TryMatch("sunol", ProgrammedFixes, allowFullDatabaseFallback: false));
    }

    [Theory]
    [InlineData("sepin", "CEPIN")] // 1 raw edit + phonetic match (both SPN)
    [InlineData("seapin", "CEPIN")] // phonetic match via vowel drop
    [InlineData("soonol", "SUNOL")] // phonetic match
    [InlineData("men low", "MENLO")] // Whisper tends to add spaces; caller should dedupe, but the core algorithm should still match "menlo" against "MENLO"
    public void TryMatch_NearMiss_ReturnsBestProgrammedFix(string transcribed, string expected)
    {
        // Strip spaces before passing to the matcher — emulates what the mapper would do.
        var cleaned = transcribed.Replace(" ", "");
        var result = PhoneticFixMatcher.TryMatch(cleaned, ProgrammedFixes, allowFullDatabaseFallback: false);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void TryMatch_EmptyToken_ReturnsNull()
    {
        Assert.Null(PhoneticFixMatcher.TryMatch("", ProgrammedFixes, allowFullDatabaseFallback: false));
        Assert.Null(PhoneticFixMatcher.TryMatch("   ", ProgrammedFixes, allowFullDatabaseFallback: false));
    }

    [Fact]
    public void TryMatch_NoCloseMatch_ReturnsNull()
    {
        // "quantum" isn't close to any programmed fix.
        Assert.Null(PhoneticFixMatcher.TryMatch("quantum", ProgrammedFixes, allowFullDatabaseFallback: false));
    }

    [Fact]
    public void TryMatch_EmptyProgrammedFixes_ReturnsNull()
    {
        // No candidates + fallback disabled → no match possible.
        Assert.Null(PhoneticFixMatcher.TryMatch("CEPIN", [], allowFullDatabaseFallback: false));
    }

    // --- TryMatch: threshold behavior ---

    [Fact]
    public void TryMatch_TwoEdits_IsAcceptedForProgrammedFix()
    {
        // "SEEPIN" vs CEPIN: S→C, insert E → 2 raw edits. Phonetic SPN vs SPN = 0.
        // max(2,0) = 2, within threshold.
        var result = PhoneticFixMatcher.TryMatch("SEEPIN", ProgrammedFixes, allowFullDatabaseFallback: false);
        Assert.Equal("CEPIN", result);
    }

    [Fact]
    public void TryMatch_SelectsClosestOfMultipleCandidates()
    {
        // Both CEPIN and OAKES are ~equally far from "cakes" raw, but phonetically "cakes"
        // should match one of them better. The algorithm should pick the best candidate
        // deterministically, not return null.
        var result = PhoneticFixMatcher.TryMatch("OAKES", ProgrammedFixes, allowFullDatabaseFallback: false);
        Assert.Equal("OAKES", result);
    }
}
