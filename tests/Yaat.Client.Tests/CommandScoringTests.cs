using Xunit;
using Yaat.SpeechSandbox;

namespace Yaat.Client.Tests;

/// <summary>
/// The command-level metric of <c>--atc-ouroboros</c>: each gold clause of a trial is recognised,
/// wrong (wrong arguments or wrong verb) or rejected, and hypothesis clauses with no gold partner
/// are inserted (counted as wrong). Clauses are <c>,</c>-separated parallel siblings, so matching
/// is order-insensitive.
/// </summary>
public sealed class CommandScoringTests
{
    private sealed record Example(
        string Gold,
        string? Hypothesis,
        bool RawTextFallback,
        int Recognised,
        int WrongArgs,
        int WrongVerb,
        int Inserted,
        int Rejected
    );

    private static readonly Example[] Examples =
    [
        new("CM 2000, FH 270", "CM 2000, FH 270", false, 2, 0, 0, 0, 0),
        new("CM 2000, FH 270", "FH 270, CM 2000", false, 2, 0, 0, 0, 0),
        new("CM 2000, FH 270", "cm  2000,fh 270", false, 2, 0, 0, 0, 0),
        new("CM 2000, FH 270", "CM 2000", false, 1, 0, 0, 0, 1),
        new("CM 2000, FH 270", "FH 270", false, 1, 0, 0, 0, 1),
        new("CM 2000", "CM 3000", false, 0, 1, 0, 0, 0),
        new("CM 2000", "DM 2000", false, 0, 0, 1, 0, 0),
        new("CM 2000", "CM 2000, FH 270", false, 1, 0, 0, 1, 0),
        new("CM 2000, FH 270", "CM 3000, TL 270", false, 0, 1, 1, 0, 0),
        new("CM 2000", "DM 3000, CM 4000", false, 0, 1, 0, 1, 0),
        new("CM 2000", null, false, 0, 0, 0, 0, 1),
        new("CM 2000, FH 270", "", false, 0, 0, 0, 0, 2),
        new("CM 2000, FH 270", "   ", false, 0, 0, 0, 0, 2),
        new("CM 2000, FH 270", "CLIMB AND MAINTAIN TWO THOUSAND", true, 0, 0, 0, 0, 2),
    ];

    public static TheoryData<string, string?, bool, int, int, int, int, int> WorkedExamples
    {
        get
        {
            var data = new TheoryData<string, string?, bool, int, int, int, int, int>();
            foreach (Example e in Examples)
            {
                data.Add(e.Gold, e.Hypothesis, e.RawTextFallback, e.Recognised, e.WrongArgs, e.WrongVerb, e.Inserted, e.Rejected);
            }
            return data;
        }
    }

    public static TheoryData<string, string?, bool> Inputs
    {
        get
        {
            var data = new TheoryData<string, string?, bool>();
            foreach (Example e in Examples)
            {
                data.Add(e.Gold, e.Hypothesis, e.RawTextFallback);
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(WorkedExamples))]
    public void Scores_Each_Gold_Clause(
        string gold,
        string? hypothesis,
        bool rawTextFallback,
        int recognised,
        int wrongArgs,
        int wrongVerb,
        int inserted,
        int rejected
    )
    {
        ClauseScore score = CommandScoring.Score(gold, hypothesis, rawTextFallback);

        Assert.Equal(
            (recognised, wrongArgs, wrongVerb, inserted, rejected),
            (score.Recognised, score.WrongArgs, score.WrongVerb, score.Inserted, score.Rejected)
        );
        Assert.Equal(wrongArgs + wrongVerb + inserted, score.Wrong);
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public void Every_Gold_Clause_Is_Recognised_Wrong_Or_Rejected(string gold, string? hypothesis, bool rawTextFallback)
    {
        ClauseScore score = CommandScoring.Score(gold, hypothesis, rawTextFallback);

        int goldClauses = gold.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Length;
        Assert.Equal(goldClauses, score.Gold);
        Assert.Equal(score.Gold, score.Recognised + score.WrongArgs + score.WrongVerb + score.Rejected);
    }
}
