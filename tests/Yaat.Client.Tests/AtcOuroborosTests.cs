using Xunit;
using Yaat.SpeechSandbox;

namespace Yaat.Client.Tests;

/// <summary>
/// The model-free half of the speech sandbox's <c>--atc-ouroboros</c> mode: the synthetic
/// controller-phraseology templates verify through the production rule mapper, cases are sampled
/// deterministically across templates, unverifiable cases follow the gap policy, and verdicts
/// aggregate and diff against a baseline into the right exit code.
/// </summary>
public sealed class AtcOuroborosTests
{
    /// <summary>
    /// Templates that render labels the rule mapper cannot currently produce. Each entry names the
    /// rule gap in a comment; a template leaves this set the moment its rule is fixed (the test fails
    /// until it does). Every template currently verifies, so the set is empty.
    /// </summary>
    private static readonly HashSet<string> KnownGaps = [];

    private const int RendersPerTemplate = 8;

    public AtcOuroborosTests()
    {
        Yaat.Sim.Testing.TestVnasData.EnsureInitialized();
    }

    [Fact]
    public async Task Every_Template_Verifies_Through_The_Rule_Mapper_Or_Is_A_Known_Gap()
    {
        var rng = new Random(20260928);
        var failures = new List<TemplateGap>();
        foreach (SynthTemplate template in SynthTemplates.All)
        {
            for (int i = 0; i < RendersPerTemplate; i++)
            {
                TemplateGap? gap = await SynthTemplates.VerifyAsync(SynthTemplates.Render(template, i, rng));
                if (gap is not null)
                {
                    failures.Add(gap);
                }
            }
        }

        string detail = string.Join(
            Environment.NewLine,
            failures.Select(g =>
                $"{g.Template}: \"{g.Transcript}\" expected {g.ExpectedCanonical}, got {g.GotCanonical ?? "(null)"} ({g.FailureReason})"
            )
        );
        HashSet<string> failedTemplates = [.. failures.Select(g => g.Template)];
        Assert.True(failedTemplates.SetEquals(KnownGaps), $"Unverified templates differ from KnownGaps:{Environment.NewLine}{detail}");
    }

    [Fact]
    public void Template_Keys_Are_Unique_And_Every_Family_Has_Three_Variants()
    {
        Assert.Equal(SynthTemplates.All.Count, SynthTemplates.All.Select(t => t.Key).Distinct().Count());
        Assert.All(
            SynthTemplates.All.GroupBy(t => t.Family),
            family => Assert.True(family.Count() >= 3, $"{family.Key} has {family.Count()} templates")
        );
    }

    [Fact]
    public void Sampling_Gives_Every_Template_Its_Floor_Share_Deterministically()
    {
        IReadOnlyList<SynthTemplate> templates = SynthTemplates.All;
        List<SynthTemplate> first = SynthTemplates.SampleTemplates(templates, 200, 42);
        List<SynthTemplate> second = SynthTemplates.SampleTemplates(templates, 200, 42);

        Assert.Equal(200, first.Count);
        int floor = 200 / templates.Count;
        Assert.All(templates, t => Assert.True(first.Count(p => p.Key == t.Key) >= floor, $"{t.Key} got fewer than {floor} cases"));
        Assert.Equal(first.Select(t => t.Key), second.Select(t => t.Key));
    }

    [Fact]
    public void Sampling_Below_One_Round_Draws_Distinct_Templates()
    {
        List<SynthTemplate> picked = SynthTemplates.SampleTemplates(SynthTemplates.All, 15, 7);

        Assert.Equal(15, picked.Count);
        Assert.Equal(15, picked.Select(t => t.Key).Distinct().Count());
    }

    [Fact]
    public async Task Same_Seed_Plans_The_Same_Cases()
    {
        SynthPlan first = await SynthTemplates.PlanAsync(SynthTemplates.All, 60, 99);
        SynthPlan second = await SynthTemplates.PlanAsync(SynthTemplates.All, 60, 99);

        Assert.Equal(Describe(first), Describe(second));
        Assert.Equal(60, first.Cases.Count);
    }

    [Fact]
    public async Task An_Unverifiable_Template_Is_Reported_And_Never_Sampled()
    {
        SynthTemplate[] templates = [Good, Mislabeled];

        SynthPlan plan = await SynthTemplates.PlanAsync(templates, 6, 1);

        Assert.Equal(["good"], plan.VerifiedTemplates.Select(t => t.Key));
        TemplateGap gap = Assert.Single(plan.Gaps);
        Assert.Equal("mislabeled", gap.Template);
        Assert.Equal("canonical mismatch", gap.FailureReason);
        Assert.StartsWith("TR ", gap.ExpectedCanonical);
        Assert.StartsWith("FH ", gap.GotCanonical);
        Assert.StartsWith("GAP mislabeled: \"", gap.Describe());
        Assert.Equal(6, plan.Cases.Count);
        Assert.All(plan.Cases, c => Assert.Equal("good", c.Template.Key));
    }

    [Fact]
    public async Task Distribution_Covers_Only_Verified_Templates()
    {
        SynthPlan plan = await SynthTemplates.PlanAsync(SynthTemplates.All, 200, AtcOuroborosSeed);

        HashSet<string> gapTemplates = [.. plan.Gaps.Select(g => g.Template)];
        Assert.True(gapTemplates.SetEquals(KnownGaps), string.Join(", ", gapTemplates));
        Assert.Equal(SynthTemplates.All.Count - KnownGaps.Count, plan.VerifiedTemplates.Count);
        Assert.Equal(200, plan.Cases.Count);
        Assert.DoesNotContain(plan.Cases, c => KnownGaps.Contains(c.Template.Key));
        int floor = 200 / plan.VerifiedTemplates.Count;
        Assert.All(
            plan.VerifiedTemplates,
            t => Assert.True(plan.Cases.Count(c => c.Template.Key == t.Key) >= floor, $"{t.Key} got fewer than {floor} cases")
        );
    }

    [Fact]
    public void Aggregation_Counts_Verdicts_Per_Family_Per_Template_And_Overall()
    {
        // Clause counts are set independently of the verdicts: aggregation pools whatever each case carries.
        EvalCaseResult[] verdicts =
        [
            Verdict("fh", EvalVerdict.Pass, 0.0) with
            {
                Clauses = new ClauseScore(3, 3, 0, 0, 0, 0),
                CallsignsMatched = 3,
                CallsignsExpected = 3,
            },
            Verdict("fh", EvalVerdict.Fail, 0.5) with
            {
                Clauses = new ClauseScore(3, 2, 0, 0, 0, 1),
                CallsignsMatched = 2,
                CallsignsExpected = 3,
            },
            Verdict("tl", EvalVerdict.Flaky, null) with
            {
                Clauses = new ClauseScore(3, 3, 0, 0, 0, 0),
            },
            Verdict("cm", EvalVerdict.Pass, 0.1) with
            {
                Clauses = new ClauseScore(3, 2, 0, 1, 0, 0),
            },
            Verdict("cm", EvalVerdict.Pass, 0.3) with
            {
                Clauses = new ClauseScore(3, 2, 0, 0, 0, 1),
            },
        ];
        var families = new Dictionary<string, string>
        {
            ["fh"] = "HeadingRules",
            ["tl"] = "HeadingRules",
            ["cm"] = "AltitudeSpeedRules",
        };

        AtcAggregate aggregate = AtcOuroborosAnalysis.Aggregate(verdicts, families);

        // Worst recognition first — the reverse of the pass-rate order.
        Assert.Equal(["AltitudeSpeedRules", "HeadingRules"], aggregate.Families.Select(f => f.Family));
        FamilyResult altitude = aggregate.Families[0];
        Assert.Equal((2, 2, 0, 0), (altitude.Cases, altitude.Pass, altitude.Flaky, altitude.Fail));
        Assert.Equal(1.0, altitude.PassRate, 9);
        Assert.Equal(0.2, altitude.MeanWer!.Value, 9);
        Assert.Equal(
            (6, 4, 1, 1),
            (altitude.Commands.GoldClauses, altitude.Commands.Recognised, altitude.Commands.Wrong, altitude.Commands.Rejected)
        );
        Assert.Equal(4.0 / 6, altitude.Commands.RecognitionRate, 9);
        Assert.Equal(1.0 / 6, altitude.Commands.ErrorRate, 9);
        Assert.Equal(1.0 / 6, altitude.Commands.RejectionRate, 9);
        Assert.Null(altitude.Commands.CallsignAccuracy);
        FamilyResult heading = aggregate.Families[1];
        Assert.Equal((3, 1, 1, 1), (heading.Cases, heading.Pass, heading.Flaky, heading.Fail));
        Assert.Equal(1.0 / 3, heading.PassRate, 9);
        Assert.Equal(0.25, heading.MeanWer!.Value, 9);
        Assert.Equal((9, 8, 0, 1), (heading.Commands.GoldClauses, heading.Commands.Recognised, heading.Commands.Wrong, heading.Commands.Rejected));
        Assert.Equal(8.0 / 9, heading.Commands.RecognitionRate, 9);
        Assert.Equal(0.0, heading.Commands.ErrorRate, 9);
        Assert.Equal(5.0 / 6, heading.Commands.CallsignAccuracy!.Value, 9);

        Assert.Equal(["cm", "fh", "tl"], aggregate.Templates.Select(t => t.Template));
        TemplateResult fh = aggregate.Templates[1];
        Assert.Equal(("HeadingRules", 2, 1, 0, 1), (fh.Family, fh.Cases, fh.Pass, fh.Flaky, fh.Fail));
        Assert.Equal(0.5, fh.PassRate, 9);
        Assert.Equal((6, 5, 0, 1), (fh.Commands.GoldClauses, fh.Commands.Recognised, fh.Commands.Wrong, fh.Commands.Rejected));
        Assert.Equal(5.0 / 6, fh.Commands.RecognitionRate, 9);

        TotalsResult totals = aggregate.Totals;
        Assert.Equal((5, 3, 1, 1), (totals.Cases, totals.Pass, totals.Flaky, totals.Fail));
        Assert.Equal(0.6, totals.PassRate, 9);
        Assert.Equal(0.225, totals.MeanWer!.Value, 9);
        Assert.Equal((15, 12, 1, 2), (totals.Commands.GoldClauses, totals.Commands.Recognised, totals.Commands.Wrong, totals.Commands.Rejected));
        Assert.Equal(0.8, totals.Commands.RecognitionRate, 9);
        Assert.Equal(1.0 / 15, totals.Commands.ErrorRate, 9);
        Assert.Equal(2.0 / 15, totals.Commands.RejectionRate, 9);
        Assert.Equal(5.0 / 6, totals.Commands.CallsignAccuracy!.Value, 9);
    }

    [Fact]
    public void A_Case_Sums_Its_Trials_With_No_Mapping_And_Raw_Text_Scored_As_Rejections()
    {
        EvalTrial[] trials =
        [
            new("climb and maintain two thousand fly heading two seven zero", "CM 2000, FH 270", "CM 2000, FH 270", RawTextFallback: false),
            new("", null, null, RawTextFallback: false),
            new("climb and maintain two thousand", null, "climb and maintain 2000", RawTextFallback: true),
        ];

        ClauseScore score = EvalRunner.ScoreTrials("CM 2000, FH 270", trials);

        Assert.Equal(new ClauseScore(6, 2, 0, 0, 0, 4), score);
    }

    [Theory]
    // A null callsign is a trial that extracted none, such as an empty transcript: a miss when one is expected.
    [InlineData("N346G", new[] { "N346G", null, "N364G" }, 1, 3)]
    [InlineData("N346G", new[] { "n346g", "N346G", "N346G" }, 3, 3)]
    // No expected callsign: the case's trials are excluded from callsign accuracy.
    [InlineData(null, new[] { "N346G", null }, 0, 0)]
    public void Callsign_Tally_Counts_Every_Trial_Against_The_Expected_Callsign(
        string? expectedCallsign,
        string?[] trialCallsigns,
        int matched,
        int expected
    )
    {
        (int Matched, int Expected) tally = EvalRunner.TallyCallsigns(trialCallsigns, expectedCallsign);

        Assert.Equal((matched, expected), tally);
    }

    [Fact]
    public void Aggregation_With_No_Wer_Reports_Null_Mean()
    {
        AtcAggregate aggregate = AtcOuroborosAnalysis.Aggregate([Verdict("x", EvalVerdict.Fail, null)], new Dictionary<string, string>());

        Assert.Equal("unknown", aggregate.Families[0].Family);
        Assert.Equal("unknown", aggregate.Templates[0].Family);
        Assert.Null(aggregate.Families[0].MeanWer);
        Assert.Equal(0, aggregate.Totals.PassRate);
    }

    [Theory]
    // Moves are in units of each rate's tolerance: beyond 1 is a change, inside it is not.
    [InlineData(2.0, 0.0, DiffKind.Improved)]
    [InlineData(-2.0, 0.0, DiffKind.Regressed)]
    [InlineData(0.0, 0.0, DiffKind.Unchanged)]
    [InlineData(2.0, 0.5, DiffKind.Improved)]
    [InlineData(2.0, 2.0, DiffKind.Regressed)]
    [InlineData(0.0, -2.0, DiffKind.Unchanged)]
    public void Diff_Classifies_Family_Movement_Beyond_The_Rate_Tolerances(double recognitionMove, double errorMove, DiffKind expected)
    {
        AtcOuroborosResults baseline = Results([Family("HeadingRules", BaseRecognition, BaseError)], [], []);
        AtcOuroborosResults current = Results(
            [
                Family(
                    "HeadingRules",
                    BaseRecognition + (recognitionMove * AtcOuroborosAnalysis.RecognitionTolerance),
                    BaseError + (errorMove * AtcOuroborosAnalysis.ErrorTolerance)
                ),
            ],
            [],
            []
        );

        BaselineDiffResult diff = AtcOuroborosAnalysis.Compare(baseline, current);

        Assert.Equal(expected, Assert.Single(diff.Families).Kind);
        int expectedExit = expected == DiffKind.Regressed ? AtcOuroborosAnalysis.ExitRegression : AtcOuroborosAnalysis.ExitNoRegression;
        Assert.Equal(expectedExit, AtcOuroborosAnalysis.ExitCodeFor(diff));
    }

    [Fact]
    public void Diff_Compares_Rates_Whatever_The_Case_Counts()
    {
        AtcOuroborosResults baseline = Results([FamilyWithPasses("HeadingRules", 0.9, 0.0, 10, 10)], [], []);
        double fallen = 0.9 - (2 * AtcOuroborosAnalysis.RecognitionTolerance);

        BaselineDiffResult same = AtcOuroborosAnalysis.Compare(baseline, Results([FamilyWithPasses("HeadingRules", 0.9, 0.0, 5, 5)], [], []));
        BaselineDiffResult beyond = AtcOuroborosAnalysis.Compare(baseline, Results([FamilyWithPasses("HeadingRules", fallen, 0.0, 5, 5)], [], []));

        Assert.Equal(DiffKind.Unchanged, Assert.Single(same.Families).Kind);
        Assert.Equal(DiffKind.Regressed, Assert.Single(beyond.Families).Kind);
    }

    [Fact]
    public void A_Template_Swing_Inside_Its_Family_Does_Not_Regress()
    {
        AtcOuroborosResults baseline = Results(
            [Family("HeadingRules", 0.8, 0.05)],
            [Template("fh", "HeadingRules", 1.0, 0.0), Template("tl", "HeadingRules", 0.6, 0.1)],
            []
        );
        AtcOuroborosResults current = Results(
            [Family("HeadingRules", 0.8, 0.05)],
            [Template("fh", "HeadingRules", 0.6, 0.1), Template("tl", "HeadingRules", 1.0, 0.0)],
            []
        );

        BaselineDiffResult diff = AtcOuroborosAnalysis.Compare(baseline, current);

        Assert.Equal(DiffKind.Unchanged, Assert.Single(diff.Families).Kind);
        Assert.False(diff.HasRegression);
        Assert.Equal(AtcOuroborosAnalysis.ExitNoRegression, AtcOuroborosAnalysis.ExitCodeFor(diff));
    }

    [Fact]
    public void An_Error_Rise_Beyond_Its_Tolerance_Regresses_With_Recognition_Unchanged()
    {
        double risen = BaseError + (2 * AtcOuroborosAnalysis.ErrorTolerance);
        AtcOuroborosResults baseline = Results([Family("HeadingRules", BaseRecognition, BaseError)], [], []);
        AtcOuroborosResults current = Results([Family("HeadingRules", BaseRecognition, risen)], [], []);

        BaselineDiffResult diff = AtcOuroborosAnalysis.Compare(baseline, current);

        FamilyDiff heading = Assert.Single(diff.Families);
        Assert.Equal(DiffKind.Regressed, heading.Kind);
        Assert.Equal((BaseRecognition, BaseRecognition), (heading.BaselineRecognitionRate!.Value, heading.CurrentRecognitionRate!.Value));
        Assert.Equal(BaseError, heading.BaselineErrorRate!.Value, 9);
        Assert.Equal(risen, heading.CurrentErrorRate!.Value, 9);
        Assert.Equal(DiffKind.Regressed, diff.Totals);
        Assert.Equal(AtcOuroborosAnalysis.ExitRegression, AtcOuroborosAnalysis.ExitCodeFor(diff));
    }

    [Fact]
    public void A_Recognition_Fall_Within_Its_Tolerance_Does_Not_Regress()
    {
        double fallen = BaseRecognition - (0.5 * AtcOuroborosAnalysis.RecognitionTolerance);
        AtcOuroborosResults baseline = Results([Family("HeadingRules", BaseRecognition, BaseError)], [], []);
        AtcOuroborosResults current = Results([Family("HeadingRules", fallen, BaseError)], [], []);

        BaselineDiffResult diff = AtcOuroborosAnalysis.Compare(baseline, current);

        Assert.Equal(DiffKind.Unchanged, Assert.Single(diff.Families).Kind);
        Assert.Equal(DiffKind.Unchanged, diff.Totals);
        Assert.False(diff.HasRegression);
    }

    [Fact]
    public void A_Pass_Rate_Drop_With_Rates_Unchanged_Does_Not_Regress()
    {
        AtcOuroborosResults baseline = Results([FamilyWithPasses("HeadingRules", BaseRecognition, BaseError, 4, 4)], [], []);
        AtcOuroborosResults current = Results([FamilyWithPasses("HeadingRules", BaseRecognition, BaseError, 1, 4)], [], []);

        BaselineDiffResult diff = AtcOuroborosAnalysis.Compare(baseline, current);

        Assert.Equal(DiffKind.Unchanged, Assert.Single(diff.Families).Kind);
        Assert.Equal(DiffKind.Unchanged, diff.Totals);
        Assert.Equal(AtcOuroborosAnalysis.ExitNoRegression, AtcOuroborosAnalysis.ExitCodeFor(diff));
    }

    [Fact]
    public void An_Insertion_Only_Change_Raises_The_Error_Rate()
    {
        var families = new Dictionary<string, string> { ["cm"] = "AltitudeSpeedRules" };
        AtcOuroborosResults exact = Aggregated(AtcOuroborosAnalysis.Aggregate([Heard("CM 2000", "CM 2000")], families));
        AtcOuroborosResults inserted = Aggregated(AtcOuroborosAnalysis.Aggregate([Heard("CM 2000", "CM 2000, FH 270")], families));

        BaselineDiffResult diff = AtcOuroborosAnalysis.Compare(exact, inserted);

        FamilyResult before = Assert.Single(exact.Families);
        FamilyResult after = Assert.Single(inserted.Families);
        Assert.Equal((1.0, 0.0), (before.Commands.RecognitionRate, before.Commands.ErrorRate));
        Assert.Equal((1.0, 1.0, 0.0), (after.Commands.RecognitionRate, after.Commands.ErrorRate, after.Commands.RejectionRate));
        Assert.Equal(DiffKind.Regressed, Assert.Single(diff.Families).Kind);
        Assert.Equal(AtcOuroborosAnalysis.ExitRegression, AtcOuroborosAnalysis.ExitCodeFor(diff));
    }

    [Fact]
    public void Diff_Reports_New_And_Gone_Families_Without_Regression()
    {
        AtcOuroborosResults baseline = Results([Family("HoldRules", 1.0, 0.0), Family("HeadingRules", 1.0, 0.0)], [], []);
        AtcOuroborosResults current = Results([Family("HeadingRules", 1.0, 0.0), Family("Compound", 1.0, 0.0)], [], []);

        BaselineDiffResult diff = AtcOuroborosAnalysis.Compare(baseline, current);

        Assert.Equal(DiffKind.New, diff.Families.Single(f => f.Family == "Compound").Kind);
        Assert.Equal(DiffKind.Gone, diff.Families.Single(f => f.Family == "HoldRules").Kind);
        Assert.Equal(DiffKind.Unchanged, diff.Families.Single(f => f.Family == "HeadingRules").Kind);
        Assert.False(diff.HasRegression);
        Assert.Equal(AtcOuroborosAnalysis.ExitNoRegression, AtcOuroborosAnalysis.ExitCodeFor(diff));
    }

    [Fact]
    public void Diff_Flags_A_Baseline_Template_That_Became_A_Gap()
    {
        TemplateResult[] templates = [Template("fh", "HeadingRules", 1.0, 0.0), Template("cm", "AltitudeSpeedRules", 1.0, 0.0)];
        AtcOuroborosResults baseline = Results([Family("HeadingRules", 1.0, 0.0), Family("AltitudeSpeedRules", 1.0, 0.0)], templates, []);
        AtcOuroborosResults current = Results([Family("HeadingRules", 1.0, 0.0)], [templates[0]], [Gap("cm")]);

        BaselineDiffResult diff = AtcOuroborosAnalysis.Compare(baseline, current);

        Assert.Equal(["cm"], diff.NewGaps);
        Assert.True(diff.HasRegression);
        Assert.Equal(AtcOuroborosAnalysis.ExitRegression, AtcOuroborosAnalysis.ExitCodeFor(diff));
    }

    [Fact]
    public void Diff_Does_Not_Flag_A_Gap_The_Baseline_Already_Had()
    {
        AtcOuroborosResults baseline = Results([Family("HeadingRules", 1.0, 0.0)], [Template("fh", "HeadingRules", 1.0, 0.0)], [Gap("tl")]);
        AtcOuroborosResults current = Results([Family("HeadingRules", 1.0, 0.0)], [Template("fh", "HeadingRules", 1.0, 0.0)], [Gap("tl")]);

        BaselineDiffResult diff = AtcOuroborosAnalysis.Compare(baseline, current);

        Assert.Empty(diff.NewGaps);
        Assert.Equal(AtcOuroborosAnalysis.ExitNoRegression, AtcOuroborosAnalysis.ExitCodeFor(diff));
    }

    [Fact]
    public void Diff_Flags_A_Totals_Drop_As_A_Regression()
    {
        // Each 15-clause family loses 2 clauses, inside its one-case floor (3 of 15); pooled over the
        // shared families that is 4 of 30, past the totals' floor (3 of 30).
        AtcOuroborosResults baseline = Results([FamilyCounts("a", 15, 15, 0), FamilyCounts("b", 15, 15, 0)], [], []);
        AtcOuroborosResults current = Results([FamilyCounts("a", 15, 13, 0), FamilyCounts("b", 15, 13, 0)], [], []);

        BaselineDiffResult diff = AtcOuroborosAnalysis.Compare(baseline, current);

        Assert.All(diff.Families, f => Assert.Equal(DiffKind.Unchanged, f.Kind));
        Assert.Equal(DiffKind.Regressed, diff.Totals);
        Assert.Equal(AtcOuroborosAnalysis.ExitRegression, AtcOuroborosAnalysis.ExitCodeFor(diff));
    }

    [Fact]
    public void A_Poorly_Recognised_New_Family_Alone_Leaves_The_Totals_Unchanged()
    {
        AtcOuroborosResults baseline = Results([Family("a", 0.9, 0.0)], [], []);
        AtcOuroborosResults current = Results([Family("a", 0.9, 0.0), Family("b", 0.5, 0.0)], [], []);

        BaselineDiffResult diff = AtcOuroborosAnalysis.Compare(baseline, current);

        Assert.Equal(DiffKind.Unchanged, diff.Families.Single(f => f.Family == "a").Kind);
        Assert.Equal(DiffKind.New, diff.Families.Single(f => f.Family == "b").Kind);
        Assert.Equal(DiffKind.Unchanged, diff.Totals);
        Assert.False(diff.HasRegression);
        Assert.Equal(AtcOuroborosAnalysis.ExitNoRegression, AtcOuroborosAnalysis.ExitCodeFor(diff));
    }

    [Fact]
    public void A_Move_Inside_A_Small_Familys_One_Case_Floor_Is_Unchanged()
    {
        // 15 gold clauses over 3 trials: one case is 3 clauses (0.2), above both flat tolerances.
        AtcOuroborosResults baseline = Results([FamilyCounts("HoldRules", 15, 15, 0)], [], []);

        BaselineDiffResult oneClauseRejected = AtcOuroborosAnalysis.Compare(baseline, Results([FamilyCounts("HoldRules", 15, 14, 0)], [], []));
        BaselineDiffResult oneClauseWrong = AtcOuroborosAnalysis.Compare(baseline, Results([FamilyCounts("HoldRules", 15, 14, 1)], [], []));
        BaselineDiffResult twoCases = AtcOuroborosAnalysis.Compare(baseline, Results([FamilyCounts("HoldRules", 15, 9, 0)], [], []));

        Assert.Equal(DiffKind.Unchanged, Assert.Single(oneClauseRejected.Families).Kind);
        Assert.Equal(DiffKind.Unchanged, Assert.Single(oneClauseWrong.Families).Kind);
        Assert.Equal(DiffKind.Regressed, Assert.Single(twoCases.Families).Kind);
    }

    [Fact]
    public void A_One_Case_Move_In_A_Large_Family_Regresses_On_Recognition_And_On_Error()
    {
        // 102 gold clauses over 3 trials: one case is 3 clauses (3/102), above both flat tolerances.
        AtcOuroborosResults baseline = Results([FamilyCounts("GroundRules", 102, 90, 6)], [], []);

        BaselineDiffResult recognitionFell = AtcOuroborosAnalysis.Compare(baseline, Results([FamilyCounts("GroundRules", 102, 87, 6)], [], []));
        BaselineDiffResult errorRose = AtcOuroborosAnalysis.Compare(baseline, Results([FamilyCounts("GroundRules", 102, 90, 9)], [], []));
        BaselineDiffResult recognitionRose = AtcOuroborosAnalysis.Compare(baseline, Results([FamilyCounts("GroundRules", 102, 93, 6)], [], []));

        Assert.Equal(DiffKind.Regressed, Assert.Single(recognitionFell.Families).Kind);
        Assert.Equal(DiffKind.Regressed, Assert.Single(errorRose.Families).Kind);
        Assert.Equal(DiffKind.Improved, Assert.Single(recognitionRose.Families).Kind);
    }

    [Fact]
    public void No_Baseline_Exits_Clean() => Assert.Equal(AtcOuroborosAnalysis.ExitNoRegression, AtcOuroborosAnalysis.ExitCodeFor(null));

    [Fact]
    public void Baseline_Form_Drops_Only_The_Timestamp_And_Round_Trips()
    {
        AtcOuroborosResults results = Results([Family("HeadingRules", 0.75, 0.1)], [Template("fh", "HeadingRules", 0.75, 0.1)], [Gap("cm")]);

        string json = AtcOuroborosAnalysis.Serialize(AtcOuroborosAnalysis.ToBaseline(results));
        AtcOuroborosResults? back = AtcOuroborosAnalysis.Deserialize(json);

        Assert.DoesNotContain("generatedUtc", json);
        Assert.Contains("\"passRate\"", json);
        Assert.Contains("\"recognitionRate\"", json);
        Assert.Contains("\"errorRate\"", json);
        Assert.Contains("\"rejectionRate\"", json);
        Assert.Contains("\"goldClauses\"", json);
        Assert.NotNull(back);
        Assert.Equal(results.Families, back.Families);
        Assert.Equal(results.Templates, back.Templates);
        Assert.Equal(results.Gaps, back.Gaps);
        Assert.Equal(results.Totals, back.Totals);
    }

    [Fact]
    public void Failing_Case_Transcripts_Round_Trip_Through_The_Baseline_And_Are_Never_Compared()
    {
        EvalTrial heard = new("taxi via bravo charlie to gate golf alfa five", null, "TAXI B C TO GATE GOLF ALFA FIVE", RawTextFallback: true);
        EvalCaseResult[] verdicts =
        [
            Verdict("fh", EvalVerdict.Pass, 0.0),
            new(
                "synth-007-taxi-gate",
                "TAXI B C @GA5",
                ["TAXI B C TO GATE GOLF ALFA FIVE"],
                EvalVerdict.Fail,
                0.1,
                Synthetic: true,
                "taxi-gate",
                [heard],
                new ClauseScore(1, 0, 0, 0, 0, 1),
                CallsignsMatched: 0,
                CallsignsExpected: 0
            ),
            new(
                "synth-008-fh",
                "FH 270",
                ["FH 270", "TR 270"],
                EvalVerdict.Flaky,
                0.0,
                Synthetic: true,
                "fh",
                [
                    new EvalTrial("fly heading two seven zero", "FH 270", "FH 270", RawTextFallback: false),
                    new EvalTrial("right heading two seven zero", "TR 270", "TR 270", RawTextFallback: false),
                ],
                new ClauseScore(2, 1, 0, 1, 0, 0),
                CallsignsMatched: 0,
                CallsignsExpected: 0
            ),
        ];
        AtcOuroborosResults results = Results([FamilyWithPasses("GroundRules", 0.0, 0.0, 0, 1)], [], []) with
        {
            Failures = AtcOuroborosAnalysis.Failures(verdicts),
        };

        AtcOuroborosResults? back = AtcOuroborosAnalysis.Deserialize(AtcOuroborosAnalysis.Serialize(AtcOuroborosAnalysis.ToBaseline(results)));

        Assert.NotNull(back);
        Assert.Equal(2, back.Failures.Count);
        FailingCase failing = back.Failures[0];
        Assert.Equal(
            ("synth-007-taxi-gate", "taxi-gate", "TAXI B C @GA5", EvalVerdict.Fail),
            (failing.Case, failing.Template, failing.Expected, failing.Verdict)
        );
        Assert.Equal(heard, Assert.Single(failing.Trials));
        FailingCase flaky = back.Failures[1];
        Assert.Equal(("synth-008-fh", EvalVerdict.Flaky), (flaky.Case, flaky.Verdict));
        Assert.Equal(["fly heading two seven zero", "right heading two seven zero"], flaky.Trials.Select(t => t.Transcript));
        Assert.Equal("TR 270", flaky.Trials[1].MapperCanonical);

        EvalTrial heardOtherwise = new("taxi via bravo charlie to gate golf alpha five", "TAXI B C @GA5", "TAXI B C @GA5", RawTextFallback: false);
        AtcOuroborosResults differentWords = back with { Failures = [failing with { Trials = [heardOtherwise] }] };
        BaselineDiffResult diff = AtcOuroborosAnalysis.Compare(back, differentWords);

        Assert.All(diff.Families, f => Assert.Equal(DiffKind.Unchanged, f.Kind));
        Assert.Equal(DiffKind.Unchanged, diff.Totals);
        Assert.Empty(diff.NewGaps);
        Assert.Equal(AtcOuroborosAnalysis.ExitNoRegression, AtcOuroborosAnalysis.ExitCodeFor(diff));
    }

    [Fact]
    public void A_Baseline_Without_Failures_Reads_As_An_Empty_List()
    {
        const string json = """
            {
              "seed": 1,
              "cases": 4,
              "trials": 3,
              "sttModel": "stt",
              "llmModel": "llm",
              "families": [],
              "templates": [],
              "totals": { "cases": 4, "pass": 4, "flaky": 0, "fail": 0, "passRate": 1 },
              "gaps": []
            }
            """;

        AtcOuroborosResults? back = AtcOuroborosAnalysis.Deserialize(json);

        Assert.NotNull(back);
        Assert.NotNull(back.Failures);
        Assert.Empty(back.Failures);
    }

    [Fact]
    public void Serialize_WritesLfLineEndingsOnly()
    {
        AtcOuroborosResults results = Results([Family("HeadingRules", 0.75, 0.1)], [Template("fh", "HeadingRules", 0.75, 0.1)], [Gap("cm")]);

        string full = AtcOuroborosAnalysis.Serialize(results);
        string baseline = AtcOuroborosAnalysis.Serialize(AtcOuroborosAnalysis.ToBaseline(results));

        Assert.Contains("\n", full);
        Assert.DoesNotContain("\r", full);
        Assert.Contains("\n", baseline);
        Assert.DoesNotContain("\r", baseline);
    }

    [Fact]
    public void BuildReport_WritesLfLineEndingsOnly()
    {
        AtcOuroborosResults results = Results([Family("HeadingRules", 0.75, 0.1)], [Template("fh", "HeadingRules", 0.75, 0.1)], [Gap("cm")]);
        AtcOuroborosResults baseline = Results([Family("HeadingRules", 1.0, 0.0)], [Template("fh", "HeadingRules", 1.0, 0.0)], []);
        BaselineDiffResult diff = AtcOuroborosAnalysis.Compare(baseline, results);

        string withDiff = AtcOuroborosRunner.BuildReport(results, diff, "Compared with `baseline.json`.");
        string withoutDiff = AtcOuroborosRunner.BuildReport(results, null, "No baseline at `baseline.json`.");

        Assert.Contains("\n", withDiff);
        Assert.DoesNotContain("\r", withDiff);
        Assert.Contains("\n", withoutDiff);
        Assert.DoesNotContain("\r", withoutDiff);
    }

    private static readonly SynthTemplate Good = new("good", "HeadingRules", "fly heading {hdg}", "FH {hdg}");

    // Spoken as "fly heading" but labeled as a right turn — the rule mapper can never agree.
    private static readonly SynthTemplate Mislabeled = new("mislabeled", "HeadingRules", "fly heading {hdg}", "TR {hdg}");

    private static string[] Describe(SynthPlan plan) =>
        [
            .. plan.Cases.Select(c => $"{c.Index} {c.Template.Key} {c.Expectation.Transcript} {c.Expectation.Canonical} {c.Speaker} {c.Speed}"),
            .. plan.Gaps.Select(g => $"gap {g.Template} {g.Transcript}"),
        ];

    private static EvalCaseResult Verdict(string template, EvalVerdict verdict, double? wer) =>
        new(
            $"case-{template}",
            "FH 270",
            ["FH 270"],
            verdict,
            wer,
            Synthetic: true,
            template,
            [new EvalTrial("fly heading two seven zero", "FH 270", "FH 270", RawTextFallback: false)],
            new ClauseScore(1, 1, 0, 0, 0, 0),
            CallsignsMatched: 0,
            CallsignsExpected: 0
        );

    /// <summary>A one-trial case labeled <paramref name="gold"/> whose pipeline produced <paramref name="heard"/>.</summary>
    private static EvalCaseResult Heard(string gold, string heard)
    {
        EvalTrial trial = new("climb and maintain two thousand", heard, heard, RawTextFallback: false);
        return Verdict("cm", EvalVerdict.Pass, null) with
        {
            ExpectedCanonical = gold,
            Trials = [trial],
            Clauses = EvalRunner.ScoreTrials(gold, [trial]),
        };
    }

    private const int AtcOuroborosSeed = 20260928;

    private const double BaseRecognition = 0.5;
    private const double BaseError = 0.1;

    /// <summary>Gold clauses behind every synthetic family/template row, large enough that the rates round to 1e-4.</summary>
    private const int GoldClauses = 10000;

    private static FamilyResult Family(string family, double recognitionRate, double errorRate) =>
        FamilyWithPasses(family, recognitionRate, errorRate, 4, 4);

    private static FamilyResult FamilyWithPasses(string family, double recognitionRate, double errorRate, int pass, int cases) =>
        new(family, cases, pass, 0, cases - pass, (double)pass / cases, null, RatesAt(recognitionRate, errorRate));

    /// <summary>A family of <paramref name="gold"/> clauses (pooled over 3 trials) with the given counts; the rest are rejected.</summary>
    private static FamilyResult FamilyCounts(string family, int gold, int recognised, int wrong) =>
        new(family, 4, 4, 0, 0, 1.0, null, Rates(gold, recognised, wrong));

    private static TemplateResult Template(string template, string family, double recognitionRate, double errorRate) =>
        new(template, family, 4, 4, 0, 0, 1.0, null, RatesAt(recognitionRate, errorRate));

    /// <summary>Rates over <see cref="GoldClauses"/> clauses, rounded to whole clauses.</summary>
    private static CommandRates RatesAt(double recognitionRate, double errorRate) =>
        Rates(GoldClauses, (int)Math.Round(recognitionRate * GoldClauses), (int)Math.Round(errorRate * GoldClauses));

    /// <summary>Rates of <paramref name="gold"/> clauses with the given counts; the rest are rejected (none when insertions push wrong past them).</summary>
    private static CommandRates Rates(int gold, int recognised, int wrong)
    {
        int rejected = Math.Max(0, gold - recognised - wrong);
        return new CommandRates(
            gold,
            recognised,
            wrong,
            rejected,
            CommandRates.Rate(recognised, gold),
            CommandRates.Rate(wrong, gold),
            CommandRates.Rate(rejected, gold),
            null
        );
    }

    private static TemplateGap Gap(string template) => new(template, "united two thirty four", "FH 270", null, "no rule matched");

    private static AtcOuroborosResults Results(FamilyResult[] families, TemplateResult[] templates, TemplateGap[] gaps)
    {
        int cases = families.Sum(f => f.Cases);
        int pass = families.Sum(f => f.Pass);
        CommandRates pooled = Rates(
            families.Sum(f => f.Commands.GoldClauses),
            families.Sum(f => f.Commands.Recognised),
            families.Sum(f => f.Commands.Wrong)
        );
        var totals = new TotalsResult(cases, pass, 0, cases - pass, cases == 0 ? 0 : (double)pass / cases, null, pooled);
        return new AtcOuroborosResults(1, cases, 3, "stt", "llm", DateTime.UtcNow, families, templates, totals, gaps, []);
    }

    private static AtcOuroborosResults Aggregated(AtcAggregate aggregate) =>
        new(1, aggregate.Totals.Cases, 1, "stt", "llm", null, aggregate.Families, aggregate.Templates, aggregate.Totals, [], []);
}
