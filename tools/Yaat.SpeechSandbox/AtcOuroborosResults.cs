using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yaat.SpeechSandbox;

/// <summary>
/// Command-level scoring of a group of cases (a family, a template, or every case), pooled over
/// every trial of every case (<see cref="CommandScoring"/>). <see cref="ErrorRate"/> exceeds 1 only
/// through inserted clauses; every rate is 0 when the group has no gold clauses.
/// </summary>
/// <param name="GoldClauses">Gold clauses × trials.</param>
/// <param name="Recognised">Gold clauses the pipeline produced exactly.</param>
/// <param name="Wrong">Wrong-argument, wrong-verb and inserted clauses.</param>
/// <param name="Rejected">Gold clauses with no hypothesis clause to pair with (including raw-text fallbacks and empty transcripts).</param>
/// <param name="CallsignAccuracy">Trials whose callsign matched over trials that expected one; null when none did.</param>
public sealed record CommandRates(
    int GoldClauses,
    int Recognised,
    int Wrong,
    int Rejected,
    double RecognitionRate,
    double ErrorRate,
    double RejectionRate,
    double? CallsignAccuracy
)
{
    /// <summary>The rates of <paramref name="clauses"/> (summed over the group) and the group's callsign tally.</summary>
    public static CommandRates From(ClauseScore clauses, int callsignsMatched, int callsignsExpected) =>
        new(
            clauses.Gold,
            clauses.Recognised,
            clauses.Wrong,
            clauses.Rejected,
            Rate(clauses.Recognised, clauses.Gold),
            Rate(clauses.Wrong, clauses.Gold),
            Rate(clauses.Rejected, clauses.Gold),
            callsignsExpected == 0 ? null : (double)callsignsMatched / callsignsExpected
        );

    /// <summary><paramref name="count"/> over <paramref name="gold"/> clauses; 0 when there are none, as an empty pass rate is.</summary>
    public static double Rate(int count, int gold) => gold == 0 ? 0 : (double)count / gold;
}

/// <summary>Verdict counts and command-level rates for one rule family (a <c>PhraseologyRules</c> builder, or <c>Compound</c>).</summary>
public sealed record FamilyResult(string Family, int Cases, int Pass, int Flaky, int Fail, double PassRate, double? MeanWer, CommandRates Commands);

/// <summary>Verdict counts and command-level rates for one template — information only, never compared against the baseline.</summary>
public sealed record TemplateResult(
    string Template,
    string Family,
    int Cases,
    int Pass,
    int Flaky,
    int Fail,
    double PassRate,
    double? MeanWer,
    CommandRates Commands
);

/// <summary>Verdict counts and command-level rates across every scored case.</summary>
public sealed record TotalsResult(int Cases, int Pass, int Flaky, int Fail, double PassRate, double? MeanWer, CommandRates Commands);

/// <summary>
/// A case that did not pass every trial, with what each trial heard and mapped — evidence for a
/// tuning wave, never compared against the baseline.
/// </summary>
/// <param name="Case">The case directory name (<c>synth-&lt;seed&gt;-NNN-&lt;template&gt;</c>).</param>
/// <param name="Template">The generator template key, or <c>(none)</c> for a real recording.</param>
/// <param name="Expected">The labeled canonical.</param>
/// <param name="Verdict">FLAKY or FAIL.</param>
/// <param name="Trials">Each trial's transcript, rule-mapper canonical and final canonical.</param>
public sealed record FailingCase(string Case, string Template, string Expected, EvalVerdict Verdict, IReadOnlyList<EvalTrial> Trials);

/// <summary>
/// The <c>results.json</c> of one <c>--atc-ouroboros</c> run, and the shape of the committed
/// baseline (which omits <see cref="GeneratedUtc"/>). <see cref="Families"/> and
/// <see cref="Templates"/> are sorted worst recognition rate first (ties: higher error rate first,
/// then name); <see cref="Failures"/> is in case order and is information only.
/// </summary>
public sealed record AtcOuroborosResults(
    int Seed,
    int Cases,
    int Trials,
    string SttModel,
    string LlmModel,
    DateTime? GeneratedUtc,
    IReadOnlyList<FamilyResult> Families,
    IReadOnlyList<TemplateResult> Templates,
    TotalsResult Totals,
    IReadOnlyList<TemplateGap> Gaps,
    IReadOnlyList<FailingCase> Failures
);

/// <summary>Per-family, per-template and overall tallies of one scored corpus.</summary>
public sealed record AtcAggregate(IReadOnlyList<FamilyResult> Families, IReadOnlyList<TemplateResult> Templates, TotalsResult Totals);

[JsonSerializable(typeof(AtcOuroborosResults))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    NewLine = "\n"
)]
internal sealed partial class AtcOuroborosJsonContext : JsonSerializerContext;

/// <summary>How one family (or the totals) moved against the baseline.</summary>
public enum DiffKind
{
    Unchanged,
    Improved,
    Regressed,
    New,
    Gone,
}

/// <summary>One family's movement against the baseline; a rate is null on the side the family is missing from.</summary>
public sealed record FamilyDiff(
    string Family,
    DiffKind Kind,
    double? BaselineRecognitionRate,
    double? CurrentRecognitionRate,
    double? BaselineErrorRate,
    double? CurrentErrorRate
);

/// <summary>A run compared with the baseline.</summary>
/// <param name="Families">Per-family movement, in current-run order followed by families gone from it.</param>
/// <param name="Totals">Movement of the recognition and error rates pooled over the families present in both runs.</param>
/// <param name="NewGaps">Templates that verified in the baseline and are gaps now.</param>
public sealed record BaselineDiffResult(IReadOnlyList<FamilyDiff> Families, DiffKind Totals, IReadOnlyList<string> NewGaps)
{
    public bool HasRegression => (Totals == DiffKind.Regressed) || (NewGaps.Count > 0) || Families.Any(f => f.Kind == DiffKind.Regressed);
}

/// <summary>Pure aggregation, baseline comparison and exit-code logic of <c>--atc-ouroboros</c>.</summary>
public static class AtcOuroborosAnalysis
{
    public const int ExitNoRegression = 0;
    public const int ExitSetupError = 2;
    public const int ExitRegression = 3;

    /// <summary>
    /// How far a family's (or the totals') recognition rate may move before it counts as improved or
    /// regressed — never finer than one case (see <see cref="Compare"/>).
    /// </summary>
    public const double RecognitionTolerance = 0.01;

    /// <summary>
    /// How far a family's (or the totals') error rate may rise before it counts as regressed — never
    /// finer than one case; tighter than <see cref="RecognitionTolerance"/>, as a wrong command is
    /// worse than a rejection.
    /// </summary>
    public const double ErrorTolerance = 0.005;

    private const double Epsilon = 1e-9;

    /// <summary>The template key reported for a case with none (a real recording).</summary>
    private const string NoTemplate = "(none)";

    /// <summary>Tallies <paramref name="verdicts"/> per rule family and per template (worst recognition first) and overall.</summary>
    /// <param name="verdicts">Scored cases; a case with no template is grouped under <c>(none)</c>.</param>
    /// <param name="familyByTemplate">Template key → rule family; an unknown template reports family <c>unknown</c>.</param>
    public static AtcAggregate Aggregate(IReadOnlyList<EvalCaseResult> verdicts, IReadOnlyDictionary<string, string> familyByTemplate)
    {
        string TemplateOf(EvalCaseResult v) => v.Template ?? NoTemplate;
        string FamilyOf(EvalCaseResult v) => familyByTemplate.TryGetValue(TemplateOf(v), out string? f) ? f : "unknown";

        List<FamilyResult> families =
        [
            .. verdicts
                .GroupBy(FamilyOf, StringComparer.Ordinal)
                .Select(g =>
                {
                    TotalsResult c = Count([.. g]);
                    return new FamilyResult(g.Key, c.Cases, c.Pass, c.Flaky, c.Fail, c.PassRate, c.MeanWer, c.Commands);
                })
                .OrderBy(f => f.Commands.RecognitionRate)
                .ThenByDescending(f => f.Commands.ErrorRate)
                .ThenBy(f => f.Family, StringComparer.Ordinal),
        ];
        List<TemplateResult> templates =
        [
            .. verdicts
                .GroupBy(TemplateOf, StringComparer.Ordinal)
                .Select(g =>
                {
                    TotalsResult c = Count([.. g]);
                    return new TemplateResult(g.Key, FamilyOf(g.First()), c.Cases, c.Pass, c.Flaky, c.Fail, c.PassRate, c.MeanWer, c.Commands);
                })
                .OrderBy(t => t.Commands.RecognitionRate)
                .ThenByDescending(t => t.Commands.ErrorRate)
                .ThenBy(t => t.Template, StringComparer.Ordinal),
        ];
        return new AtcAggregate(families, templates, Count(verdicts));
    }

    /// <summary>
    /// Compares <paramref name="current"/> with <paramref name="baseline"/> per rule family. A rate
    /// moves when it changes by more than its tolerance and by at least one case: one clause wrong
    /// on every trial of one case, <c>trials / min(baseline, current gold clauses)</c>. A family
    /// whose recognition rate fell by more than <see cref="RecognitionTolerance"/>, or whose error
    /// rate rose by more than <see cref="ErrorTolerance"/>, regressed; one whose recognition rose by
    /// more than <see cref="RecognitionTolerance"/> without that error rise improved; anything else
    /// is unchanged. Families only on one side are new or gone and never gate. The totals are
    /// compared the same way, pooled over the families present in both runs only. A template that
    /// verified in the baseline (it has scored cases there and no gap) and is a gap now is a
    /// regression too. Pass rates and per-template rows are information only.
    /// </summary>
    public static BaselineDiffResult Compare(AtcOuroborosResults baseline, AtcOuroborosResults current)
    {
        var before = baseline.Families.ToDictionary(f => f.Family, StringComparer.Ordinal);
        HashSet<string> currentFamilies = [.. current.Families.Select(f => f.Family)];
        var diffs = new List<FamilyDiff>();
        foreach (FamilyResult now in current.Families)
        {
            CommandRates n = now.Commands;
            diffs.Add(
                before.TryGetValue(now.Family, out FamilyResult? then)
                    ? new FamilyDiff(
                        now.Family,
                        Classify(Pool([then]), Pool([now]), current.Trials),
                        then.Commands.RecognitionRate,
                        n.RecognitionRate,
                        then.Commands.ErrorRate,
                        n.ErrorRate
                    )
                    : new FamilyDiff(now.Family, DiffKind.New, null, n.RecognitionRate, null, n.ErrorRate)
            );
        }
        foreach (FamilyResult gone in baseline.Families.Where(f => !currentFamilies.Contains(f.Family)))
        {
            diffs.Add(new FamilyDiff(gone.Family, DiffKind.Gone, gone.Commands.RecognitionRate, null, gone.Commands.ErrorRate, null));
        }

        HashSet<string> baselineVerified = [.. baseline.Templates.Select(t => t.Template)];
        baselineVerified.ExceptWith(baseline.Gaps.Select(g => g.Template));
        List<string> newGaps =
        [
            .. current.Gaps.Select(g => g.Template).Distinct(StringComparer.Ordinal).Where(baselineVerified.Contains).Order(StringComparer.Ordinal),
        ];
        DiffKind totals = Classify(
            Pool(baseline.Families.Where(f => currentFamilies.Contains(f.Family))),
            Pool(current.Families.Where(f => before.ContainsKey(f.Family))),
            current.Trials
        );
        return new BaselineDiffResult(diffs, totals, newGaps);
    }

    /// <summary>Every case that did not pass all its trials, in the order given, with its trials' transcripts and canonicals.</summary>
    public static IReadOnlyList<FailingCase> Failures(IReadOnlyList<EvalCaseResult> verdicts) =>
        [
            .. verdicts
                .Where(v => v.Verdict != EvalVerdict.Pass)
                .Select(v => new FailingCase(v.CaseName, v.Template ?? NoTemplate, v.ExpectedCanonical, v.Verdict, v.Trials)),
        ];

    /// <summary>Exit code for a finished run: no baseline (null) or no regression → 0, any regression → 3.</summary>
    public static int ExitCodeFor(BaselineDiffResult? diff) => (diff?.HasRegression ?? false) ? ExitRegression : ExitNoRegression;

    /// <summary>The baseline form of a run: identical except that it carries no generation timestamp.</summary>
    public static AtcOuroborosResults ToBaseline(AtcOuroborosResults results) => results with { GeneratedUtc = null };

    public static string Serialize(AtcOuroborosResults results) =>
        JsonSerializer.Serialize(results, AtcOuroborosJsonContext.Default.AtcOuroborosResults);

    /// <summary>Reads a results document; one written before failing cases were recorded reads with an empty <see cref="AtcOuroborosResults.Failures"/>.</summary>
    public static AtcOuroborosResults? Deserialize(string json) =>
        JsonSerializer.Deserialize(json, AtcOuroborosJsonContext.Default.AtcOuroborosResults) is { } back
            ? back with
            {
                Failures = back.Failures ?? [],
            }
            : null;

    /// <summary>The gold clauses and the recognition and error rates of one family, or of several pooled.</summary>
    private sealed record Pooled(int GoldClauses, double RecognitionRate, double ErrorRate);

    private static Pooled Pool(IEnumerable<FamilyResult> families)
    {
        CommandRates[] all = [.. families.Select(f => f.Commands)];
        int gold = all.Sum(c => c.GoldClauses);
        return new Pooled(gold, CommandRates.Rate(all.Sum(c => c.Recognised), gold), CommandRates.Rate(all.Sum(c => c.Wrong), gold));
    }

    /// <param name="before">The baseline side.</param>
    /// <param name="now">The current side.</param>
    /// <param name="trials">Trials per case, which make one case's worth of clauses.</param>
    private static DiffKind Classify(Pooled before, Pooled now, int trials)
    {
        double oneCase = (double)trials / Math.Max(1, Math.Min(before.GoldClauses, now.GoldClauses));
        bool Beyond(double move, double tolerance) => (move > tolerance + Epsilon) && (move >= oneCase - Epsilon);

        double recognitionMove = now.RecognitionRate - before.RecognitionRate;
        if (Beyond(-recognitionMove, RecognitionTolerance) || Beyond(now.ErrorRate - before.ErrorRate, ErrorTolerance))
        {
            return DiffKind.Regressed;
        }
        return Beyond(recognitionMove, RecognitionTolerance) ? DiffKind.Improved : DiffKind.Unchanged;
    }

    /// <summary>Verdict counts, mean WER and command-level rates of <paramref name="verdicts"/>.</summary>
    private static TotalsResult Count(IReadOnlyList<EvalCaseResult> verdicts)
    {
        int pass = verdicts.Count(v => v.Verdict == EvalVerdict.Pass);
        int flaky = verdicts.Count(v => v.Verdict == EvalVerdict.Flaky);
        int fail = verdicts.Count(v => v.Verdict == EvalVerdict.Fail);
        double[] wers = [.. verdicts.Where(v => v.Wer is not null).Select(v => v.Wer!.Value)];
        double passRate = verdicts.Count == 0 ? 0 : (double)pass / verdicts.Count;
        var commands = CommandRates.From(
            verdicts.Aggregate(ClauseScore.Zero, (sum, v) => sum + v.Clauses),
            verdicts.Sum(v => v.CallsignsMatched),
            verdicts.Sum(v => v.CallsignsExpected)
        );
        return new TotalsResult(verdicts.Count, pass, flaky, fail, passRate, wers.Length == 0 ? null : wers.Average(), commands);
    }
}
