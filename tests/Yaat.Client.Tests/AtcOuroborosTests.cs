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
    /// rule gap; a template leaves this set the moment its rule is fixed (the test fails until it does).
    /// </summary>
    private static readonly HashSet<string> KnownGaps =
    [
        // "cleared into bravo airspace": NATO collapse turns "bravo" into "B" before matching, so the
        // CLBRV literal never matches and "cleared into {route}" wins (CMTR B).
        "clbrv",
        // "cleared visual approach runway {rwy}": maps to nothing under a populated scenario context
        // (the same phrase maps to CVA with an empty context in PhraseologyMapperTests).
        "cva",
        // "follow / give way to {callsign}": {callsign} captures one token, a spoken telephony is several.
        "follow",
        "giveway",
        // "follow {callsign} on ground": same single-token capture; the bare FOLLOW rule wins with the
        // first word of the telephony (FOLLOW united / FOLLOW N).
        "followg",
        // "make left three sixty" / "make right two seventy": digit normalization turns the literals
        // into 360 / 270 before the rule's "three sixty" / "two seventy" tokens can match.
        "l360",
        "r270",
    ];

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
        EvalCaseResult[] verdicts =
        [
            Verdict("fh", EvalVerdict.Pass, 0.0),
            Verdict("fh", EvalVerdict.Fail, 0.5),
            Verdict("tl", EvalVerdict.Flaky, null),
            Verdict("cm", EvalVerdict.Pass, 0.1),
            Verdict("cm", EvalVerdict.Pass, 0.3),
        ];
        var families = new Dictionary<string, string>
        {
            ["fh"] = "HeadingRules",
            ["tl"] = "HeadingRules",
            ["cm"] = "AltitudeSpeedRules",
        };

        AtcAggregate aggregate = AtcOuroborosAnalysis.Aggregate(verdicts, families);

        Assert.Equal(["HeadingRules", "AltitudeSpeedRules"], aggregate.Families.Select(f => f.Family));
        FamilyResult heading = aggregate.Families[0];
        Assert.Equal((3, 1, 1, 1), (heading.Cases, heading.Pass, heading.Flaky, heading.Fail));
        Assert.Equal(1.0 / 3, heading.PassRate, 9);
        Assert.Equal(0.25, heading.MeanWer!.Value, 9);
        FamilyResult altitude = aggregate.Families[1];
        Assert.Equal((2, 2, 0, 0), (altitude.Cases, altitude.Pass, altitude.Flaky, altitude.Fail));
        Assert.Equal(1.0, altitude.PassRate, 9);
        Assert.Equal(0.2, altitude.MeanWer!.Value, 9);

        Assert.Equal(["tl", "fh", "cm"], aggregate.Templates.Select(t => t.Template));
        TemplateResult fh = aggregate.Templates[1];
        Assert.Equal(("HeadingRules", 2, 1, 0, 1), (fh.Family, fh.Cases, fh.Pass, fh.Flaky, fh.Fail));
        Assert.Equal(0.5, fh.PassRate, 9);

        TotalsResult totals = aggregate.Totals;
        Assert.Equal((5, 3, 1, 1), (totals.Cases, totals.Pass, totals.Flaky, totals.Fail));
        Assert.Equal(0.6, totals.PassRate, 9);
        Assert.Equal(0.225, totals.MeanWer!.Value, 9);
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
    // 4 cases: one case's worth is 0.25, so a 2-case move is a change and a 1-case move is not.
    [InlineData(2, 4, DiffKind.Improved)]
    [InlineData(4, 2, DiffKind.Regressed)]
    [InlineData(3, 2, DiffKind.Unchanged)]
    [InlineData(2, 3, DiffKind.Unchanged)]
    [InlineData(3, 3, DiffKind.Unchanged)]
    public void Diff_Classifies_Family_Movement_Beyond_One_Case(int baselinePass, int currentPass, DiffKind expected)
    {
        AtcOuroborosResults baseline = Results([Family("HeadingRules", baselinePass, 4)], [], []);
        AtcOuroborosResults current = Results([Family("HeadingRules", currentPass, 4)], [], []);

        BaselineDiffResult diff = AtcOuroborosAnalysis.Compare(baseline, current);

        Assert.Equal(expected, Assert.Single(diff.Families).Kind);
        int expectedExit = expected == DiffKind.Regressed ? AtcOuroborosAnalysis.ExitRegression : AtcOuroborosAnalysis.ExitNoRegression;
        Assert.Equal(expectedExit, AtcOuroborosAnalysis.ExitCodeFor(diff));
    }

    [Fact]
    public void Diff_Uses_The_Smaller_Case_Count_For_One_Case()
    {
        // 10 cases then 5: one case's worth is 1/5, so 1.0 → 0.8 is inside it and 1.0 → 0.6 is not.
        AtcOuroborosResults baseline = Results([Family("HeadingRules", 10, 10)], [], []);

        BaselineDiffResult inside = AtcOuroborosAnalysis.Compare(baseline, Results([Family("HeadingRules", 4, 5)], [], []));
        BaselineDiffResult beyond = AtcOuroborosAnalysis.Compare(baseline, Results([Family("HeadingRules", 3, 5)], [], []));

        Assert.Equal(DiffKind.Unchanged, Assert.Single(inside.Families).Kind);
        Assert.Equal(DiffKind.Regressed, Assert.Single(beyond.Families).Kind);
    }

    [Fact]
    public void A_Template_Swing_Inside_Its_Family_Does_Not_Regress()
    {
        AtcOuroborosResults baseline = Results(
            [Family("HeadingRules", 8, 10)],
            [Template("fh", "HeadingRules", 5, 5), Template("tl", "HeadingRules", 3, 5)],
            []
        );
        AtcOuroborosResults current = Results(
            [Family("HeadingRules", 8, 10)],
            [Template("fh", "HeadingRules", 3, 5), Template("tl", "HeadingRules", 5, 5)],
            []
        );

        BaselineDiffResult diff = AtcOuroborosAnalysis.Compare(baseline, current);

        Assert.Equal(DiffKind.Unchanged, Assert.Single(diff.Families).Kind);
        Assert.False(diff.HasRegression);
        Assert.Equal(AtcOuroborosAnalysis.ExitNoRegression, AtcOuroborosAnalysis.ExitCodeFor(diff));
    }

    [Fact]
    public void Diff_Reports_New_And_Gone_Families_Without_Regression()
    {
        AtcOuroborosResults baseline = Results([Family("HoldRules", 4, 4), Family("HeadingRules", 4, 4)], [], []);
        AtcOuroborosResults current = Results([Family("HeadingRules", 4, 4), Family("Compound", 4, 4)], [], []);

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
        TemplateResult[] templates = [Template("fh", "HeadingRules", 4, 4), Template("cm", "AltitudeSpeedRules", 4, 4)];
        AtcOuroborosResults baseline = Results([Family("HeadingRules", 4, 4), Family("AltitudeSpeedRules", 4, 4)], templates, []);
        AtcOuroborosResults current = Results([Family("HeadingRules", 4, 4)], [templates[0]], [Gap("cm")]);

        BaselineDiffResult diff = AtcOuroborosAnalysis.Compare(baseline, current);

        Assert.Equal(["cm"], diff.NewGaps);
        Assert.True(diff.HasRegression);
        Assert.Equal(AtcOuroborosAnalysis.ExitRegression, AtcOuroborosAnalysis.ExitCodeFor(diff));
    }

    [Fact]
    public void Diff_Does_Not_Flag_A_Gap_The_Baseline_Already_Had()
    {
        AtcOuroborosResults baseline = Results([Family("HeadingRules", 4, 4)], [Template("fh", "HeadingRules", 4, 4)], [Gap("tl")]);
        AtcOuroborosResults current = Results([Family("HeadingRules", 4, 4)], [Template("fh", "HeadingRules", 4, 4)], [Gap("tl")]);

        BaselineDiffResult diff = AtcOuroborosAnalysis.Compare(baseline, current);

        Assert.Empty(diff.NewGaps);
        Assert.Equal(AtcOuroborosAnalysis.ExitNoRegression, AtcOuroborosAnalysis.ExitCodeFor(diff));
    }

    [Fact]
    public void Diff_Flags_A_Totals_Drop_As_A_Regression()
    {
        AtcOuroborosResults baseline = Results([Family("a", 3, 3), Family("b", 3, 3), Family("c", 3, 3)], [], []);
        AtcOuroborosResults current = Results([Family("a", 2, 3), Family("b", 2, 3), Family("c", 2, 3)], [], []);

        BaselineDiffResult diff = AtcOuroborosAnalysis.Compare(baseline, current);

        Assert.All(diff.Families, f => Assert.Equal(DiffKind.Unchanged, f.Kind));
        Assert.Equal(DiffKind.Regressed, diff.Totals);
        Assert.Equal(AtcOuroborosAnalysis.ExitRegression, AtcOuroborosAnalysis.ExitCodeFor(diff));
    }

    [Fact]
    public void No_Baseline_Exits_Clean() => Assert.Equal(AtcOuroborosAnalysis.ExitNoRegression, AtcOuroborosAnalysis.ExitCodeFor(null));

    [Fact]
    public void Baseline_Form_Drops_Only_The_Timestamp_And_Round_Trips()
    {
        AtcOuroborosResults results = Results([Family("HeadingRules", 3, 4)], [Template("fh", "HeadingRules", 3, 4)], [Gap("cm")]);

        string json = AtcOuroborosAnalysis.Serialize(AtcOuroborosAnalysis.ToBaseline(results));
        AtcOuroborosResults? back = AtcOuroborosAnalysis.Deserialize(json);

        Assert.DoesNotContain("generatedUtc", json);
        Assert.Contains("\"passRate\"", json);
        Assert.NotNull(back);
        Assert.Equal(results.Families, back.Families);
        Assert.Equal(results.Templates, back.Templates);
        Assert.Equal(results.Gaps, back.Gaps);
        Assert.Equal(results.Totals, back.Totals);
    }

    [Fact]
    public void Serialize_WritesLfLineEndingsOnly()
    {
        AtcOuroborosResults results = Results([Family("HeadingRules", 3, 4)], [Template("fh", "HeadingRules", 3, 4)], [Gap("cm")]);

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
        AtcOuroborosResults results = Results([Family("HeadingRules", 3, 4)], [Template("fh", "HeadingRules", 3, 4)], [Gap("cm")]);
        AtcOuroborosResults baseline = Results([Family("HeadingRules", 4, 4)], [Template("fh", "HeadingRules", 4, 4)], []);
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
        new($"case-{template}", "FH 270", ["FH 270"], verdict, wer, Synthetic: true, template);

    private const int AtcOuroborosSeed = 20260928;

    private static FamilyResult Family(string family, int pass, int cases) => new(family, cases, pass, 0, cases - pass, (double)pass / cases, null);

    private static TemplateResult Template(string template, string family, int pass, int cases) =>
        new(template, family, cases, pass, 0, cases - pass, (double)pass / cases, null);

    private static TemplateGap Gap(string template) => new(template, "united two thirty four", "FH 270", null, "no rule matched");

    private static AtcOuroborosResults Results(FamilyResult[] families, TemplateResult[] templates, TemplateGap[] gaps)
    {
        int cases = families.Sum(f => f.Cases);
        int pass = families.Sum(f => f.Pass);
        var totals = new TotalsResult(cases, pass, 0, cases - pass, cases == 0 ? 0 : (double)pass / cases, null);
        return new AtcOuroborosResults(1, cases, 3, "stt", "llm", DateTime.UtcNow, families, templates, totals, gaps);
    }
}
