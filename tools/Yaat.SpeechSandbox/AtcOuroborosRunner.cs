using System.Globalization;
using System.Text;

namespace Yaat.SpeechSandbox;

/// <summary>
/// Controller-voice ouroboros: one repeatable measurement of the STT pipeline on what controllers
/// actually say. Generates labeled synthetic controller transmissions across every
/// <c>PhraseologyRules</c> family (<see cref="SynthTemplates"/>, unverifiable cases recorded as
/// gaps), speaks them with Piper, scores them through the full production pipeline
/// (<see cref="EvalRunner"/>), aggregates per template, and diffs against a committed baseline so a
/// tuning change shows its improvements and regressions per family.
///
/// Run with <c>--atc-ouroboros [--cases N] [--seed S] [--trials N] [--out-dir D] [--baseline &lt;json&gt;]
/// [--update-baseline] [--voice &lt;dir&gt;]</c>. Default paths resolve against the repo root (main
/// checkout or worktree; the working directory when none is found); paths passed as arguments are
/// used as given. Regressions are judged per rule family and on the totals; per-template rows are
/// information only. Writes <c>corpus/</c>, <c>eval/</c>, <c>results.json</c> and
/// <c>report.md</c> into the out dir. Exit codes: 0 no regression (or no baseline), 3 regression,
/// 2 usage / setup error.
/// </summary>
internal static class AtcOuroborosRunner
{
    /// <summary>The committed seed every default run (and the baseline) uses.</summary>
    public const int DefaultSeed = 20260928;

    private const int DefaultCases = 200;
    private const int DefaultTrials = 3;

    /// <summary>Default paths resolve against the repo root (a main checkout or a worktree), falling back to the working directory.</summary>
    private static readonly string DefaultsRoot = OuroborosRunner.TryFindRepoRoot() ?? Directory.GetCurrentDirectory();

    private static readonly string DefaultBaselinePath = Path.Combine(
        DefaultsRoot,
        "tools",
        "Yaat.SpeechSandbox",
        "Corpus",
        "atc-ouroboros-baseline.json"
    );

    private sealed record Options(int Cases, int Seed, int Trials, string OutDir, string BaselinePath, bool UpdateBaseline, string? VoiceDir);

    public static async Task<int> RunAsync(string[] args)
    {
        Options? options = ParseArgs(args);
        if (options is null)
        {
            Console.Error.WriteLine(
                "Usage: Yaat.SpeechSandbox --atc-ouroboros [--cases N] [--seed S] [--trials N] [--out-dir D] [--baseline <json>] [--update-baseline] [--voice <dir>]"
            );
            return AtcOuroborosAnalysis.ExitSetupError;
        }

        string? voiceDir = options.VoiceDir ?? PiperSynthesizer.ResolveDefaultVoiceDir();
        if (voiceDir is null)
        {
            Console.Error.WriteLine("FATAL: Piper voice pack not found — install it via Yaat.Client Settings → Speech → TTS.");
            return AtcOuroborosAnalysis.ExitSetupError;
        }

        using var pipeline = EvalPipeline.Create(whisperOverride: null, parakeetDir: null, noBiasingPrompt: false);
        EvalRunner.PrintModels(pipeline);
        if (pipeline.SetupError is not null)
        {
            Console.Error.WriteLine($"FATAL: {pipeline.SetupError}");
            return AtcOuroborosAnalysis.ExitSetupError;
        }

        Directory.CreateDirectory(options.OutDir);
        string corpusDir = Path.Combine(options.OutDir, "corpus");
        SynthCorpusResult generated = await SynthCorpusGenerator
            .GenerateAsync(new SynthCorpusOptions(corpusDir, options.Cases, options.Seed, voiceDir))
            .ConfigureAwait(false);
        Console.WriteLine($"Generated {generated.Written.Count} cases ({generated.Gaps.Count} gaps) into {Path.GetFullPath(corpusDir)}");
        Console.WriteLine();

        EvalRunResult scored = await EvalRunner
            .ScoreAsync(pipeline, corpusDir, Path.Combine(options.OutDir, "eval"), options.Trials)
            .ConfigureAwait(false);

        var familyByTemplate = SynthTemplates.All.ToDictionary(t => t.Key, t => t.Family, StringComparer.Ordinal);
        AtcAggregate aggregate = AtcOuroborosAnalysis.Aggregate(scored.Cases, familyByTemplate);
        var results = new AtcOuroborosResults(
            options.Seed,
            options.Cases,
            options.Trials,
            pipeline.SttLabel,
            pipeline.LlmModelPath,
            DateTime.UtcNow,
            aggregate.Families,
            aggregate.Templates,
            aggregate.Totals,
            generated.Gaps
        );
        await File.WriteAllTextAsync(Path.Combine(options.OutDir, "results.json"), AtcOuroborosAnalysis.Serialize(results)).ConfigureAwait(false);

        if (options.UpdateBaseline)
        {
            return await UpdateBaselineAsync(options, results).ConfigureAwait(false);
        }

        return await CompareAndReportAsync(options, results).ConfigureAwait(false);
    }

    /// <summary>Writes this run as the committed baseline and reports it.</summary>
    private static async Task<int> UpdateBaselineAsync(Options options, AtcOuroborosResults results)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.BaselinePath))!);
        string baselineJson = AtcOuroborosAnalysis.Serialize(AtcOuroborosAnalysis.ToBaseline(results));
        await File.WriteAllTextAsync(options.BaselinePath, baselineJson).ConfigureAwait(false);
        await WriteReportAsync(options, results, diff: null, baselineNote: $"Baseline updated: `{options.BaselinePath}`").ConfigureAwait(false);
        Console.WriteLine($"Baseline written to {Path.GetFullPath(options.BaselinePath)}");
        return AtcOuroborosAnalysis.ExitNoRegression;
    }

    /// <summary>Compares the run against the committed baseline (when there is one), writes the report and returns the exit code.</summary>
    private static async Task<int> CompareAndReportAsync(Options options, AtcOuroborosResults results)
    {
        BaselineDiffResult? diff = null;
        string baselineNote;
        if (File.Exists(options.BaselinePath))
        {
            AtcOuroborosResults? baseline = AtcOuroborosAnalysis.Deserialize(await File.ReadAllTextAsync(options.BaselinePath).ConfigureAwait(false));
            if (baseline is null)
            {
                Console.Error.WriteLine($"FATAL: baseline {options.BaselinePath} is empty or not a results document.");
                return AtcOuroborosAnalysis.ExitSetupError;
            }
            diff = AtcOuroborosAnalysis.Compare(baseline, results);
            baselineNote = $"Compared with `{options.BaselinePath}`.";
        }
        else
        {
            baselineNote = $"No baseline at `{options.BaselinePath}` — nothing to compare against.";
            Console.WriteLine($"No baseline exists at {options.BaselinePath}; skipping the comparison.");
        }

        await WriteReportAsync(options, results, diff, baselineNote).ConfigureAwait(false);
        Console.WriteLine(TotalsLine(results));
        if (diff is not null)
        {
            Console.WriteLine(diff.HasRegression ? "REGRESSION against the baseline — see report.md." : "No regression against the baseline.");
        }
        return AtcOuroborosAnalysis.ExitCodeFor(diff);
    }

    private static Options? ParseArgs(string[] args)
    {
        int cases = DefaultCases;
        int seed = DefaultSeed;
        int trials = DefaultTrials;
        string? outDir = null;
        string baselinePath = DefaultBaselinePath;
        bool updateBaseline = false;
        string? voiceDir = null;
        for (int i = 0; i < args.Length; i++)
        {
            bool hasValue = i + 1 < args.Length;
            switch (args[i])
            {
                case "--cases" when hasValue && TryParsePositive(args[++i], out cases):
                case "--seed" when hasValue && int.TryParse(args[++i], CultureInfo.InvariantCulture, out seed):
                case "--trials" when hasValue && TryParsePositive(args[++i], out trials):
                    break;
                case "--out-dir" when hasValue:
                    outDir = args[++i];
                    break;
                case "--baseline" when hasValue:
                    baselinePath = args[++i];
                    break;
                case "--voice" when hasValue:
                    voiceDir = args[++i];
                    break;
                case "--update-baseline":
                    updateBaseline = true;
                    break;
                default:
                    Console.Error.WriteLine($"FATAL: unrecognised or malformed argument '{args[i]}'");
                    return null;
            }
        }
        outDir ??= Path.Combine(DefaultsRoot, ".tmp", $"atc-ouroboros-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}");
        return new Options(cases, seed, trials, outDir, baselinePath, updateBaseline, voiceDir);
    }

    private static bool TryParsePositive(string text, out int value) => int.TryParse(text, CultureInfo.InvariantCulture, out value) && value > 0;

    private static string TotalsLine(AtcOuroborosResults r) =>
        $"Totals: {r.Totals.Cases} cases — {r.Totals.Pass} PASS, {r.Totals.Flaky} FLAKY, {r.Totals.Fail} FAIL "
        + $"(pass rate {r.Totals.PassRate.ToString("P1", CultureInfo.InvariantCulture)}, mean WER {FormatWer(r.Totals.MeanWer)}), {r.Gaps.Count} gaps";

    private static string FormatWer(double? wer) => wer is null ? "n/a" : wer.Value.ToString("P1", CultureInfo.InvariantCulture);

    private static async Task WriteReportAsync(Options options, AtcOuroborosResults results, BaselineDiffResult? diff, string baselineNote)
    {
        var report = new StringBuilder();
        report.AppendLine("# ATC ouroboros report");
        report.AppendLine();
        report.AppendLine($"- Seed {results.Seed}, {results.Cases} cases planned, {results.Trials} trials per case");
        report.AppendLine($"- STT: `{results.SttModel}`  LLM: `{results.LlmModel}`");
        report.AppendLine();
        report.AppendLine($"**{TotalsLine(results)}**");
        report.AppendLine();
        report.AppendLine("## Per rule family (worst pass rate first) — compared with the baseline");
        report.AppendLine();
        report.AppendLine("| Family | Cases | Pass | Flaky | Fail | Pass rate | Mean WER |");
        report.AppendLine("|---|---|---|---|---|---|---|");
        foreach (FamilyResult f in results.Families)
        {
            report.AppendLine($"| {f.Family} | {f.Cases} | {f.Pass} | {f.Flaky} | {f.Fail} | {FormatRate(f.PassRate)} | {FormatWer(f.MeanWer)} |");
        }
        report.AppendLine();
        report.AppendLine("## Per template (worst pass rate first) — information only");
        report.AppendLine();
        report.AppendLine("| Template | Family | Cases | Pass | Flaky | Fail | Pass rate | Mean WER |");
        report.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (TemplateResult t in results.Templates)
        {
            report.AppendLine(
                $"| {t.Template} | {t.Family} | {t.Cases} | {t.Pass} | {t.Flaky} | {t.Fail} | {FormatRate(t.PassRate)} | {FormatWer(t.MeanWer)} |"
            );
        }
        report.AppendLine();
        report.AppendLine("## Gaps (rendered cases the rule mapper does not map to their label)");
        report.AppendLine();
        if (results.Gaps.Count == 0)
        {
            report.AppendLine("None.");
        }
        foreach (TemplateGap g in results.Gaps)
        {
            report.AppendLine(
                $"- `{g.Template}`: \"{g.Transcript}\" — expected `{g.ExpectedCanonical}`, got `{g.GotCanonical ?? "(null)"}` ({g.FailureReason})"
            );
        }
        report.AppendLine();
        AppendDiff(report, diff, baselineNote);
        await File.WriteAllTextAsync(Path.Combine(options.OutDir, "report.md"), report.ToString()).ConfigureAwait(false);
    }

    private static void AppendDiff(StringBuilder report, BaselineDiffResult? diff, string baselineNote)
    {
        report.AppendLine("## Baseline comparison");
        report.AppendLine();
        report.AppendLine(baselineNote);
        if (diff is null)
        {
            return;
        }
        report.AppendLine();
        report.AppendLine($"- Totals: {Arrow(diff.Totals)}");
        report.AppendLine($"- Regression: {(diff.HasRegression ? "yes" : "no")}");
        foreach (string gap in diff.NewGaps)
        {
            report.AppendLine($"- `{gap}` verified in the baseline and is a gap now");
        }
        report.AppendLine();
        report.AppendLine("| Family | Change | Baseline | Now |");
        report.AppendLine("|---|---|---|---|");
        foreach (FamilyDiff f in diff.Families)
        {
            report.AppendLine($"| {f.Family} | {Arrow(f.Kind)} | {FormatRate(f.BaselinePassRate)} | {FormatRate(f.CurrentPassRate)} |");
        }
    }

    private static string FormatRate(double? rate) => rate is null ? "—" : rate.Value.ToString("P0", CultureInfo.InvariantCulture);

    private static string Arrow(DiffKind kind) =>
        kind switch
        {
            DiffKind.Improved => "↑",
            DiffKind.Regressed => "↓",
            DiffKind.New => "new",
            DiffKind.Gone => "gone",
            _ => "unchanged",
        };
}
