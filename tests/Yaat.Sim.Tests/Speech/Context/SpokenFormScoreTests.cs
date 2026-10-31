using Xunit;
using Yaat.Sim.Speech.Context;

namespace Yaat.Sim.Tests.Speech.Context;

public class SpokenFormScoreTests
{
    private static readonly DecoderSettings Settings = DecoderSettings.Default;

    [Fact]
    public void Exact_IsOne() => Assert.Equal(1.0, SpokenFormScore.WordScore("southwest", "southwest", MatchMode.Fuzzy, Settings));

    [Fact]
    public void KizerKaiser_AboveEntityAccept()
    {
        double score = SpokenFormScore.WordScore("kizer", "kaiser", MatchMode.Fuzzy, Settings);
        Assert.True(score >= Settings.EntityAcceptName, $"score {score} below EntityAcceptName {Settings.EntityAcceptName}");
    }

    [Fact]
    public void SkywestSouthwest_BelowCallsignAccept()
    {
        double score = SpokenFormScore.WordScore("skywest", "southwest", MatchMode.Fuzzy, Settings);
        Assert.True(score < Settings.CallsignAccept, $"score {score} not below CallsignAccept {Settings.CallsignAccept}");
    }

    [Fact]
    public void DigitMismatch_Zero() => Assert.Equal(0.0, SpokenFormScore.WordScore("3", "0", MatchMode.Fuzzy, Settings));

    [Fact]
    public void ShortWord_NoFuzzy() => Assert.Equal(0.0, SpokenFormScore.WordScore("sky", "ski", MatchMode.Fuzzy, Settings));

    [Fact]
    public void ExactOnly_RejectsNearMiss() => Assert.Equal(0.0, SpokenFormScore.WordScore("kizer", "kaiser", MatchMode.ExactOnly, Settings));

    [Fact]
    public void NatoTokensDiffer_Zero() => Assert.Equal(0.0, SpokenFormScore.WordScore("bravo", "charlie", MatchMode.Fuzzy, Settings));

    [Fact]
    public void Span_OneWrongDigit_ScoresCodeAtThreeQuartersTimesPrior()
    {
        IReadOnlyList<SpokenToken> heard = SpokenTokenizer.Tokenize("southwest two four nine five");
        IReadOnlyList<SpokenToken> form = SpokenTokenizer.Tokenize("southwest two four nine six");

        // Name part exact (1), code part 3 of 4 aligned tokens match (0.75), product with the prior.
        Assert.Equal(0.75 * 0.9, SpokenFormScore.ScoreSpan(heard, form, 0.9, MatchMode.Fuzzy, Settings), 6);
    }

    [Fact]
    public void NatoNearMiss_LeemaAlignsWithLima()
    {
        double score = Score("november 8 2 9 8 leema", "november 8 2 9 8 lima");
        Assert.True(score >= Settings.CallsignAccept, $"score {score} below CallsignAccept {Settings.CallsignAccept}");
    }

    [Fact]
    public void Discriminator_LeftNotRight() => Assert.Equal(0.0, Score("ils runway 2 8 left approach", "ils runway 2 8 right approach"));

    [Fact]
    public void LiteralWord_NotFuzzed() => Assert.Equal(0.0, Score("say", "sahey"));

    [Fact]
    public void Facility_NotFuzzed() => Assert.Equal(0.0, Score("san francisco tower", "san francisco vor"));

    [Fact]
    public void Kizer_StillMatches()
    {
        double score = Score("kizer", "kaiser");
        Assert.True(score >= Settings.EntityAcceptName, $"score {score} below EntityAcceptName {Settings.EntityAcceptName}");
    }

    private static double Score(string heard, string form) =>
        SpokenFormScore.ScoreSpan(SpokenTokenizer.Tokenize(heard), SpokenTokenizer.Tokenize(form), 1.0, MatchMode.Fuzzy, Settings);
}
