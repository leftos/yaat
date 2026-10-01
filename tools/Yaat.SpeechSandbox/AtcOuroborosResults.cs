using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yaat.SpeechSandbox;

/// <summary>Verdict counts for one rule family (a <c>PhraseologyRules</c> builder, or <c>Compound</c>).</summary>
public sealed record FamilyResult(string Family, int Cases, int Pass, int Flaky, int Fail, double PassRate, double? MeanWer);

/// <summary>Verdict counts for one template — information only, never compared against the baseline.</summary>
public sealed record TemplateResult(string Template, string Family, int Cases, int Pass, int Flaky, int Fail, double PassRate, double? MeanWer);

/// <summary>Verdict counts across every scored case.</summary>
public sealed record TotalsResult(int Cases, int Pass, int Flaky, int Fail, double PassRate, double? MeanWer);

/// <summary>
/// The <c>results.json</c> of one <c>--atc-ouroboros</c> run, and the shape of the committed
/// baseline (which omits <see cref="GeneratedUtc"/>). <see cref="Families"/> and
/// <see cref="Templates"/> are sorted worst pass rate first.
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
    IReadOnlyList<TemplateGap> Gaps
);

/// <summary>Per-family, per-template and overall tallies of one scored corpus.</summary>
public sealed record AtcAggregate(IReadOnlyList<FamilyResult> Families, IReadOnlyList<TemplateResult> Templates, TotalsResult Totals);

[JsonSerializable(typeof(AtcOuroborosResults))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
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

/// <summary>One family's movement against the baseline; a pass rate is null on the side the family is missing from.</summary>
public sealed record FamilyDiff(string Family, DiffKind Kind, double? BaselinePassRate, double? CurrentPassRate);

/// <summary>A run compared with the baseline.</summary>
/// <param name="Families">Per-family movement, in current-run order followed by families gone from it.</param>
/// <param name="Totals">Movement of the overall pass rate.</param>
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

    private const double Epsilon = 1e-9;

    /// <summary>Tallies <paramref name="verdicts"/> per rule family and per template (worst pass rate first) and overall.</summary>
    /// <param name="verdicts">Scored cases; a case with no template is grouped under <c>(none)</c>.</param>
    /// <param name="familyByTemplate">Template key → rule family; an unknown template reports family <c>unknown</c>.</param>
    public static AtcAggregate Aggregate(IReadOnlyList<EvalCaseResult> verdicts, IReadOnlyDictionary<string, string> familyByTemplate)
    {
        string TemplateOf(EvalCaseResult v) => v.Template ?? "(none)";
        string FamilyOf(EvalCaseResult v) => familyByTemplate.TryGetValue(TemplateOf(v), out string? f) ? f : "unknown";

        List<FamilyResult> families =
        [
            .. verdicts
                .GroupBy(FamilyOf, StringComparer.Ordinal)
                .Select(g =>
                {
                    Counts c = Count([.. g]);
                    return new FamilyResult(g.Key, c.Cases, c.Pass, c.Flaky, c.Fail, c.PassRate, c.MeanWer);
                })
                .OrderBy(f => f.PassRate)
                .ThenBy(f => f.Family, StringComparer.Ordinal),
        ];
        List<TemplateResult> templates =
        [
            .. verdicts
                .GroupBy(TemplateOf, StringComparer.Ordinal)
                .Select(g =>
                {
                    Counts c = Count([.. g]);
                    return new TemplateResult(g.Key, FamilyOf(g.First()), c.Cases, c.Pass, c.Flaky, c.Fail, c.PassRate, c.MeanWer);
                })
                .OrderBy(t => t.PassRate)
                .ThenBy(t => t.Template, StringComparer.Ordinal),
        ];
        Counts all = Count(verdicts);
        return new AtcAggregate(families, templates, new TotalsResult(all.Cases, all.Pass, all.Flaky, all.Fail, all.PassRate, all.MeanWer));
    }

    /// <summary>
    /// Compares <paramref name="current"/> with <paramref name="baseline"/> per rule family: a family
    /// whose pass rate moved by more than one case's worth (1 / the smaller of its case counts in the
    /// two runs) is improved or regressed, anything less is unchanged; families only on one side are
    /// new or gone. The totals are compared the same way. A template that verified in the baseline
    /// (it has scored cases there and no gap) and is a gap now is a regression too. Per-template
    /// counts are information only and are not compared.
    /// </summary>
    public static BaselineDiffResult Compare(AtcOuroborosResults baseline, AtcOuroborosResults current)
    {
        var before = baseline.Families.ToDictionary(f => f.Family, StringComparer.Ordinal);
        HashSet<string> currentFamilies = [.. current.Families.Select(f => f.Family)];
        var diffs = new List<FamilyDiff>();
        foreach (FamilyResult now in current.Families)
        {
            diffs.Add(
                before.TryGetValue(now.Family, out FamilyResult? then)
                    ? new FamilyDiff(now.Family, Classify(then.PassRate, then.Cases, now.PassRate, now.Cases), then.PassRate, now.PassRate)
                    : new FamilyDiff(now.Family, DiffKind.New, null, now.PassRate)
            );
        }
        foreach (FamilyResult gone in baseline.Families.Where(f => !currentFamilies.Contains(f.Family)))
        {
            diffs.Add(new FamilyDiff(gone.Family, DiffKind.Gone, gone.PassRate, null));
        }

        HashSet<string> baselineVerified = [.. baseline.Templates.Select(t => t.Template)];
        baselineVerified.ExceptWith(baseline.Gaps.Select(g => g.Template));
        List<string> newGaps =
        [
            .. current.Gaps.Select(g => g.Template).Distinct(StringComparer.Ordinal).Where(baselineVerified.Contains).Order(StringComparer.Ordinal),
        ];
        DiffKind totals = Classify(baseline.Totals.PassRate, baseline.Totals.Cases, current.Totals.PassRate, current.Totals.Cases);
        return new BaselineDiffResult(diffs, totals, newGaps);
    }

    /// <summary>Exit code for a finished run: no baseline (null) or no regression → 0, any regression → 3.</summary>
    public static int ExitCodeFor(BaselineDiffResult? diff) => (diff?.HasRegression ?? false) ? ExitRegression : ExitNoRegression;

    /// <summary>The baseline form of a run: identical except that it carries no generation timestamp.</summary>
    public static AtcOuroborosResults ToBaseline(AtcOuroborosResults results) => results with { GeneratedUtc = null };

    public static string Serialize(AtcOuroborosResults results) =>
        JsonSerializer.Serialize(results, AtcOuroborosJsonContext.Default.AtcOuroborosResults);

    public static AtcOuroborosResults? Deserialize(string json) =>
        JsonSerializer.Deserialize(json, AtcOuroborosJsonContext.Default.AtcOuroborosResults);

    private static DiffKind Classify(double baselinePassRate, int baselineCases, double currentPassRate, int currentCases)
    {
        double oneCase = 1.0 / Math.Max(1, Math.Min(baselineCases, currentCases));
        double delta = currentPassRate - baselinePassRate;
        if (delta > oneCase + Epsilon)
        {
            return DiffKind.Improved;
        }
        return delta < -(oneCase + Epsilon) ? DiffKind.Regressed : DiffKind.Unchanged;
    }

    private sealed record Counts(int Cases, int Pass, int Flaky, int Fail, double PassRate, double? MeanWer);

    private static Counts Count(IReadOnlyList<EvalCaseResult> verdicts)
    {
        int pass = verdicts.Count(v => v.Verdict == EvalVerdict.Pass);
        int flaky = verdicts.Count(v => v.Verdict == EvalVerdict.Flaky);
        int fail = verdicts.Count(v => v.Verdict == EvalVerdict.Fail);
        double[] wers = [.. verdicts.Where(v => v.Wer is not null).Select(v => v.Wer!.Value)];
        double passRate = verdicts.Count == 0 ? 0 : (double)pass / verdicts.Count;
        return new Counts(verdicts.Count, pass, flaky, fail, passRate, wers.Length == 0 ? null : wers.Average());
    }
}
