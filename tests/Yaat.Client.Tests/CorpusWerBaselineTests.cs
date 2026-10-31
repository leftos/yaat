using System.Text.Json;
using Xunit;
using Yaat.SpeechSandbox;

namespace Yaat.Client.Tests;

/// <summary>
/// The <c>--corpus-wer</c> baseline gate: a run regresses only when an arm's overall WER rises by more
/// than <see cref="CorpusWerBaselineComparer.RegressionThresholdPoints"/>, a run measured differently
/// is skipped, and the results document survives a JSON round trip.
/// </summary>
public sealed class CorpusWerBaselineTests
{
    private static readonly CorpusWerBaseline Baseline = Run(1117, Arm("default", 0.200), Arm("none", 0.250));

    [Fact]
    public void A_Rise_Within_The_Threshold_Is_Not_A_Regression()
    {
        CorpusWerComparison comparison = CorpusWerBaselineComparer.Compare(Baseline, Run(1117, Arm("default", 0.209), Arm("none", 0.250)));

        Assert.False(comparison.Skipped);
        Assert.False(comparison.HasRegression);
    }

    [Fact]
    public void A_Rise_Past_The_Threshold_Is_A_Regression()
    {
        CorpusWerComparison comparison = CorpusWerBaselineComparer.Compare(Baseline, Run(1117, Arm("default", 0.211), Arm("none", 0.250)));

        Assert.True(comparison.HasRegression);
        Assert.Contains(
            comparison.Lines,
            line => line.StartsWith("default", StringComparison.Ordinal) && line.Contains("REGRESSION", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void A_Run_With_Another_Seed_Is_Skipped()
    {
        CorpusWerComparison comparison = CorpusWerBaselineComparer.Compare(Baseline, Run(1118, Arm("default", 0.900), Arm("none", 0.900)));

        Assert.True(comparison.Skipped);
        Assert.False(comparison.HasRegression);
        Assert.Contains("Seed", comparison.SkipReason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Run_Over_Another_Corpus_Fingerprint_Is_Skipped()
    {
        CorpusWerBaseline other = Run(1117, Arm("default", 0.900), Arm("none", 0.900)) with { Sha256 = "other" };

        CorpusWerComparison comparison = CorpusWerBaselineComparer.Compare(Baseline, other);

        Assert.True(comparison.Skipped);
        Assert.False(comparison.HasRegression);
        Assert.Contains("Sha256", comparison.SkipReason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Run_Over_Another_Drawn_Subset_Is_Skipped()
    {
        CorpusWerBaseline other = Run(1117, Arm("default", 0.900), Arm("none", 0.900)) with { SubsetFingerprint = "other" };

        CorpusWerComparison comparison = CorpusWerBaselineComparer.Compare(Baseline, other);

        Assert.True(comparison.Skipped);
        Assert.False(comparison.HasRegression);
        Assert.Contains("SubsetFingerprint", comparison.SkipReason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_Arm_Missing_From_The_Run_Is_Named_And_Not_A_Regression()
    {
        CorpusWerComparison comparison = CorpusWerBaselineComparer.Compare(Baseline, Run(1117, Arm("default", 0.200)));

        Assert.False(comparison.HasRegression);
        Assert.Contains(comparison.Lines, line => line.StartsWith("none", StringComparison.Ordinal));
    }

    [Fact]
    public void The_Results_Document_Survives_A_Json_Round_Trip()
    {
        var original = new CorpusWerBaseline(
            "uwb-atcc",
            "rev",
            "abc123",
            1117,
            400,
            "subset-fp",
            "model",
            [
                new CorpusWerArm(
                    "default",
                    [new CorpusWerRoleTotals("all", 4, 40, 1, 2, 3, 0.15), new CorpusWerRoleTotals("pilot", 2, 20, 1, 0, 0, 0.05)],
                    12.5,
                    3.25,
                    1,
                    new CorpusWerTiming("auto", 2.5, 4, 1, 120, 240, 260, 1_000_000, 2_000_000, -1, -1, null),
                    null,
                    null
                ),
            ],
            null
        );

        string json = JsonSerializer.Serialize(original, CorpusWerJsonContext.Default.CorpusWerBaseline);
        CorpusWerBaseline? back = JsonSerializer.Deserialize(json, CorpusWerJsonContext.Default.CorpusWerBaseline);

        Assert.NotNull(back);
        Assert.Equal(original.Corpus, back.Corpus);
        Assert.Equal(original.Revision, back.Revision);
        Assert.Equal(original.Sha256, back.Sha256);
        Assert.Equal(original.Seed, back.Seed);
        Assert.Equal(original.Subset, back.Subset);
        Assert.Equal(original.SubsetFingerprint, back.SubsetFingerprint);
        Assert.Equal(original.SttModel, back.SttModel);
        CorpusWerArm arm = Assert.Single(back.Arms);
        Assert.Equal(original.Arms[0].Prompt, arm.Prompt);
        Assert.Equal(original.Arms[0].AudioSeconds, arm.AudioSeconds);
        Assert.Equal(original.Arms[0].WallSeconds, arm.WallSeconds);
        Assert.Equal(original.Arms[0].Roles, arm.Roles);
        Assert.Equal(original.Arms[0].NoTranscript, arm.NoTranscript);
        Assert.Equal(original.Arms[0].Timing, arm.Timing);
        Assert.NotNull(arm.Timing);
    }

    [Fact]
    public void An_Arm_Without_A_No_Transcript_Count_Reads_As_Zero()
    {
        const string json = """
            {"corpus":"uwb-atcc","revision":"rev","sha256":"sha","seed":1117,"subset":400,"subsetFingerprint":"fp","sttModel":"model",
             "arms":[{"prompt":"default","roles":[],"audioSeconds":30,"wallSeconds":6}]}
            """;

        CorpusWerBaseline? back = JsonSerializer.Deserialize(json, CorpusWerJsonContext.Default.CorpusWerBaseline);

        Assert.Equal(0, Assert.Single(Assert.IsType<CorpusWerBaseline>(back).Arms).NoTranscript);
    }

    [Fact]
    public void An_Arm_Without_A_Timing_Reads_Back_Without_One()
    {
        const string json = """
            {"corpus":"uwb-atcc","revision":"rev","sha256":"sha","seed":1117,"subset":400,"subsetFingerprint":"fp","sttModel":"model",
             "arms":[{"prompt":"default","roles":[],"audioSeconds":30,"wallSeconds":6,"noTranscript":0}]}
            """;

        CorpusWerBaseline? back = JsonSerializer.Deserialize(json, CorpusWerJsonContext.Default.CorpusWerBaseline);

        Assert.Null(Assert.Single(Assert.IsType<CorpusWerBaseline>(back).Arms).Timing);
    }

    [Fact]
    public void A_Committed_Baseline_Drops_Every_Arm_Timing()
    {
        CorpusWerBaseline run = Run(1117, Arm("default", 0.200) with { Timing = Timing(120) }, Arm("none", 0.250) with { Timing = Timing(240) });

        CorpusWerBaseline baseline = CorpusWerBaselineComparer.ForBaseline(run);

        Assert.All(baseline.Arms, arm => Assert.Null(arm.Timing));
        Assert.Equal(run.Arms[0].Roles, baseline.Arms[0].Roles);
        Assert.Equal(run.Arms[1].Roles, baseline.Arms[1].Roles);
    }

    private static CorpusWerArm Arm(string prompt, double wer) =>
        new(prompt, [new CorpusWerRoleTotals("all", 10, 100, 0, 0, 0, wer)], 30, 6, 0, null, null, null);

    private static CorpusWerTiming Timing(double p50Ms) => new("cpu", 1.5, 4, 1, p50Ms, p50Ms + 10, p50Ms + 20, 1000, 2000, -1, -1, null);

    private static CorpusWerBaseline Run(int seed, params CorpusWerArm[] arms) =>
        new("uwb-atcc", "rev", "sha", seed, 400, "subset-fp", "model", arms, null);
}
