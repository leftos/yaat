using Xunit;
using Yaat.SpeechSandbox;

namespace Yaat.Client.Tests;

/// <summary>
/// The word error rate scorer the speech sandbox's <c>--eval</c> mode reports: the counts must break
/// down into substitutions, deletions and insertions, and a corpus total must be pooled over
/// reference words rather than averaged over utterances.
/// </summary>
public sealed class WerScorerTests
{
    [Theory]
    [InlineData("a b c", "a b c", 0, 0, 0, 3, 0.0)]
    [InlineData("a b c", "a x c", 1, 0, 0, 3, 1.0 / 3.0)]
    [InlineData("a b c", "a c", 0, 1, 0, 3, 1.0 / 3.0)]
    [InlineData("a b c", "a b x c", 0, 0, 1, 3, 1.0 / 3.0)]
    [InlineData("a b c", null, 0, 3, 0, 3, 1.0)]
    [InlineData("a b c", "", 0, 3, 0, 3, 1.0)]
    [InlineData("", "", 0, 0, 0, 0, 0.0)]
    [InlineData("", "a", 0, 0, 1, 0, 1.0)]
    [InlineData("A B", "a  b", 0, 0, 0, 2, 0.0)]
    [InlineData("a b c d", "b c d e", 0, 1, 1, 4, 0.5)]
    public void Score_Counts_Substitutions_Deletions_And_Insertions(
        string reference,
        string? hypothesis,
        int substitutions,
        int deletions,
        int insertions,
        int referenceWords,
        double rate
    )
    {
        WerCounts counts = WerScorer.Score(reference, hypothesis);

        Assert.Equal(substitutions, counts.Substitutions);
        Assert.Equal(deletions, counts.Deletions);
        Assert.Equal(insertions, counts.Insertions);
        Assert.Equal(referenceWords, counts.ReferenceWords);
        Assert.Equal(substitutions + deletions + insertions, counts.Errors);
        Assert.Equal(rate, counts.Rate, 12);
    }

    [Fact]
    public void Tally_Pools_Rate_Over_Reference_Words()
    {
        var tally = new WerTally();
        tally.Add(WerScorer.Score("a", "x"));
        tally.Add(WerScorer.Score("a b c d e f g h i j", "a b c d e f g h i j"));

        Assert.Equal(2, tally.Segments);
        Assert.Equal(11, tally.Total.ReferenceWords);
        Assert.Equal(1, tally.Total.Errors);
        Assert.Equal(1.0 / 11.0, tally.Rate, 12);
    }
}
