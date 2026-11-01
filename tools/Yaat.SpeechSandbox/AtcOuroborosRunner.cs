using System.Globalization;
using System.Text;
using Yaat.Client.Services;

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
/// [--update-baseline] [--voice &lt;dir&gt;] [--speeds &lt;s1&gt;,&lt;s2&gt;,...] [--no-synth-cache] [--stt &lt;kind&gt;:&lt;source&gt;] [--device cpu|gpu]
/// [--decoding greedy|beam] [--num-paths N]</c>. Without <c>--stt</c> the run scores the
/// Whisper model saved in preferences; <c>--decoding beam</c> is accurate mode, which resolves the whole n-best against each
/// case's room.
/// Default paths resolve against the repo root (main checkout or worktree; the working directory when none is found);
/// paths passed as arguments are used as given. Regressions are judged per rule family and on the totals; per-template rows are
/// information only. Writes <c>corpus/</c>, <c>eval/</c>, <c>results.json</c> and
/// <c>report.md</c> into the out dir. Exit codes: 0 no regression (or no baseline), 3 regression,
/// 2 usage / setup error.
/// </summary>
public static class AtcOuroborosRunner
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

    /// <summary>A parsed <c>--atc-ouroboros</c> command line.</summary>
    /// <param name="Stt">The engine the flags chose; null keeps the Whisper model saved in preferences.</param>
    /// <param name="Speeds">The Piper speed multipliers each case's speed is drawn from.</param>
    public sealed record Options(
        int Cases,
        int Seed,
        int Trials,
        string OutDir,
        string BaselinePath,
        bool UpdateBaseline,
        string? VoiceDir,
        bool UseSynthCache,
        SttChoice? Stt,
        IReadOnlyList<float> Speeds
    );

    public static async Task<int> RunAsync(string[] args)
    {
        Options? options = ParseArgs(args);
        if (options is null)
        {
            Console.Error.WriteLine(
                "Usage: Yaat.SpeechSandbox --atc-ouroboros [--cases N] [--seed S] [--trials N] [--out-dir D] [--baseline <json>] [--update-baseline] [--voice <dir>] "
                    + "[--speeds <s1>,<s2>,...] [--no-synth-cache] [--stt <kind>:<source>] [--device cpu|gpu] [--decoding greedy|beam] [--num-paths N]"
            );
            return AtcOuroborosAnalysis.ExitSetupError;
        }

        string? voiceDir = options.VoiceDir ?? PiperSynthesizer.ResolveDefaultVoiceDir();
        if (voiceDir is null)
        {
            Console.Error.WriteLine("FATAL: Piper voice pack not found — install it via Yaat.Client Settings → Speech → TTS.");
            return AtcOuroborosAnalysis.ExitSetupError;
        }

        using EvalPipeline pipeline = await EvalPipeline.CreateAsync(options.Stt, noBiasingPrompt: false).ConfigureAwait(false);
        EvalRunner.PrintModels(pipeline);
        if (pipeline.SetupError is not null)
        {
            Console.Error.WriteLine($"FATAL: {pipeline.SetupError}");
            return AtcOuroborosAnalysis.ExitSetupError;
        }

        Directory.CreateDirectory(options.OutDir);
        string corpusDir = Path.Combine(options.OutDir, "corpus");
        SynthCorpusResult generated = await SynthCorpusGenerator
            .GenerateAsync(new SynthCorpusOptions(corpusDir, options.Cases, options.Seed, voiceDir, options.UseSynthCache, options.Speeds))
            .ConfigureAwait(false);
        int fromCache = generated.Written.Count(c => c.CacheHit);
        Console.WriteLine(
            $"Generated {generated.Written.Count} cases ({generated.Gaps.Count} gaps, {fromCache} from cache) into {Path.GetFullPath(corpusDir)}"
        );
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
            generated.Gaps,
            AtcOuroborosAnalysis.Failures(scored.Cases),
            scored.Timing,
            scored.Decode,
            AtcOuroborosAnalysis.RecordedSpeeds(options.Speeds)
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
            if ((baseline.Totals.Commands is null) || baseline.Families.Any(f => f.Commands is null))
            {
                Console.Error.WriteLine(
                    $"FATAL: baseline {options.BaselinePath} predates command-level scoring (no \"commands\" rates) — regenerate it with --update-baseline."
                );
                return AtcOuroborosAnalysis.ExitSetupError;
            }
            if (AtcOuroborosAnalysis.SpeedsDiffer(results.Speeds, baseline.Speeds))
            {
                string speedsNote = AtcOuroborosAnalysis.SpeedsNote(results.Speeds, baseline.Speeds);
                await WriteReportAsync(options, results, diff: null, baselineNote: speedsNote).ConfigureAwait(false);
                Console.WriteLine(TotalsLine(results));
                Console.WriteLine(speedsNote);
                return AtcOuroborosAnalysis.ExitNoRegression;
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

    /// <summary>Parses an <c>--atc-ouroboros</c> command line; null (after printing why) when it is malformed.</summary>
    public static Options? ParseArgs(string[] args)
    {
        var parsed = new ParsedArgs();
        for (int i = 0; i < args.Length; i++)
        {
            string? error = ApplyFlag(parsed, args, ref i);
            if (error is not null)
            {
                return Fail(error);
            }
        }
        SttChoice? stt = parsed.Engine.Resolve(out string? engineError);
        if (engineError is not null)
        {
            return Fail(engineError);
        }
        return new Options(
            parsed.Cases,
            parsed.Seed,
            parsed.Trials,
            parsed.OutDir,
            parsed.BaselinePath,
            parsed.UpdateBaseline,
            parsed.VoiceDir,
            parsed.UseSynthCache,
            stt,
            parsed.Speeds
        );
    }

    /// <summary>Applies the flag at <paramref name="i"/> (advancing past its value); returns an error or null.</summary>
    private static string? ApplyFlag(ParsedArgs parsed, string[] args, ref int i)
    {
        string flag = args[i];
        switch (flag)
        {
            case "--update-baseline":
                parsed.UpdateBaseline = true;
                return null;
            case "--no-synth-cache":
                parsed.UseSynthCache = false;
                return null;
            case "--cases" or "--seed" or "--trials" or "--out-dir" or "--baseline" or "--voice" or "--speeds":
                return (i + 1 < args.Length) ? ApplyValue(parsed, flag, args[++i]) : Malformed(flag);
            case string engineFlag when (i + 1 < args.Length) && EngineFlags.Handles(engineFlag):
                return parsed.Engine.TryApply(engineFlag, args[++i]);
            default:
                return Malformed(flag);
        }
    }

    private static string? ApplyValue(ParsedArgs parsed, string flag, string value) =>
        flag switch
        {
            "--cases" or "--trials" => ApplyCount(parsed, flag, value),
            "--seed" => ApplySeed(parsed, value),
            "--speeds" => ApplySpeeds(parsed, value),
            _ => ApplyPath(parsed, flag, value),
        };

    private static string? ApplyCount(ParsedArgs parsed, string flag, string value)
    {
        if (!TryParsePositive(value, out int count))
        {
            return Malformed(flag);
        }
        parsed.SetCount(flag, count);
        return null;
    }

    private static string? ApplySeed(ParsedArgs parsed, string value)
    {
        if (!int.TryParse(value, CultureInfo.InvariantCulture, out int seed))
        {
            return Malformed("--seed");
        }
        parsed.Seed = seed;
        return null;
    }

    private static string? ApplySpeeds(ParsedArgs parsed, string value)
    {
        if (!SynthTemplates.TryParseSpeeds(value, out IReadOnlyList<float>? speeds, out string? error))
        {
            return error;
        }
        parsed.Speeds = speeds;
        return null;
    }

    /// <summary>Applies <c>--out-dir</c>, <c>--baseline</c> or <c>--voice</c>: a free-form path.</summary>
    private static string? ApplyPath(ParsedArgs parsed, string flag, string value)
    {
        switch (flag)
        {
            case "--out-dir":
                parsed.OutDir = value;
                break;
            case "--baseline":
                parsed.BaselinePath = value;
                break;
            default:
                parsed.VoiceDir = value;
                break;
        }
        return null;
    }

    private static string Malformed(string flag) => $"unrecognised or malformed argument '{flag}'";

    private static Options? Fail(string error)
    {
        Console.Error.WriteLine($"FATAL: {error}");
        return null;
    }

    /// <summary>Flags accumulated while <see cref="ParseArgs"/> walks the command line.</summary>
    private sealed class ParsedArgs
    {
        public EngineFlags Engine { get; } = new();
        public int Cases { get; set; } = DefaultCases;
        public int Seed { get; set; } = DefaultSeed;
        public int Trials { get; set; } = DefaultTrials;
        public string OutDir { get; set; } = DefaultOutDir();
        public string BaselinePath { get; set; } = DefaultBaselinePath;
        public bool UpdateBaseline { get; set; }
        public string? VoiceDir { get; set; }
        public bool UseSynthCache { get; set; } = true;
        public IReadOnlyList<float> Speeds { get; set; } = SynthTemplates.DefaultSpeeds;

        /// <summary>Stores <paramref name="count"/> on the count flag <paramref name="flag"/> names.</summary>
        public void SetCount(string flag, int count)
        {
            if (flag == "--cases")
            {
                Cases = count;
            }
            else
            {
                Trials = count;
            }
        }
    }

    private static string DefaultOutDir() =>
        Path.Combine(DefaultsRoot, ".tmp", $"atc-ouroboros-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}");

    private static bool TryParsePositive(string text, out int value) => int.TryParse(text, CultureInfo.InvariantCulture, out value) && value > 0;

    private static string TotalsLine(AtcOuroborosResults r)
    {
        CommandRates c = r.Totals.Commands;
        return $"Totals: {r.Totals.Cases} cases, {c.GoldClauses} gold clauses — recognised {FormatRate(c.RecognitionRate)}, "
            + $"wrong {FormatRate(c.ErrorRate)}, rejected {FormatRate(c.RejectionRate)}, callsign {FormatRate(c.CallsignAccuracy)}; "
            + $"{r.Totals.Pass} PASS, {r.Totals.Flaky} FLAKY, {r.Totals.Fail} FAIL "
            + $"(pass rate {FormatRate(r.Totals.PassRate)}, mean WER {FormatWer(r.Totals.MeanWer)}), {r.Gaps.Count} gaps";
    }

    /// <summary>The Clauses, Recognised, Wrong, Rejected and Callsign cells of a report row.</summary>
    private static string RateCells(CommandRates c) =>
        $"{c.GoldClauses} | {FormatRate(c.RecognitionRate)} | {FormatRate(c.ErrorRate)} | {FormatRate(c.RejectionRate)} | {FormatRate(c.CallsignAccuracy)}";

    private static string FormatPoints(double tolerance) => (tolerance * 100).ToString("0.0", CultureInfo.InvariantCulture) + " points";

    private static string FormatWer(double? wer) => wer is null ? "n/a" : wer.Value.ToString("P1", CultureInfo.InvariantCulture);

    private static Task WriteReportAsync(Options options, AtcOuroborosResults results, BaselineDiffResult? diff, string baselineNote) =>
        File.WriteAllTextAsync(Path.Combine(options.OutDir, "report.md"), BuildReport(results, diff, baselineNote));

    /// <summary>Renders the <c>report.md</c> body for a finished run.</summary>
    public static string BuildReport(AtcOuroborosResults results, BaselineDiffResult? diff, string baselineNote)
    {
        var report = new StringBuilder();
        report.AppendLine("# ATC ouroboros report");
        report.AppendLine();
        report.AppendLine($"- Seed {results.Seed}, {results.Cases} cases planned, {results.Trials} trials per case");
        if (results.Speeds is not null)
        {
            report.AppendLine($"- Piper speeds: {AtcOuroborosAnalysis.FormatSpeeds(results.Speeds)}");
        }
        report.AppendLine($"- STT: `{results.SttModel}`  LLM: `{results.LlmModel}`");
        if (results.Timing is not null)
        {
            report.AppendLine(EvalRunner.TimingLine(results.Timing));
        }
        if (results.Decode is not null)
        {
            report.AppendLine(EvalRunner.DecodeLine(results.Decode));
        }
        report.AppendLine();
        report.AppendLine($"**{TotalsLine(results)}**");
        report.AppendLine();
        report.AppendLine("## Per rule family (worst recognition first) — compared with the baseline");
        report.AppendLine();
        report.AppendLine(
            "Each gold clause of each trial is recognised, wrong (wrong arguments, wrong verb, or an inserted clause) or rejected. "
                + $"A family regresses when its recognition rate falls by more than {FormatPoints(AtcOuroborosAnalysis.RecognitionTolerance)} "
                + $"or its error rate rises by more than {FormatPoints(AtcOuroborosAnalysis.ErrorTolerance)}, and by at least one case "
                + "(one clause wrong on every trial of one case); the totals, pooled over the families present in both runs, gate the same way. "
                + "PASS / FLAKY / FAIL are information only."
        );
        report.AppendLine();
        report.AppendLine("| Family | Cases | Clauses | Recognised | Wrong | Rejected | Callsign | Pass / Flaky / Fail | Mean WER |");
        report.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (FamilyResult f in results.Families)
        {
            report.AppendLine($"| {f.Family} | {f.Cases} | {RateCells(f.Commands)} | {f.Pass} / {f.Flaky} / {f.Fail} | {FormatWer(f.MeanWer)} |");
        }
        report.AppendLine();
        report.AppendLine("## Per template (worst recognition first) — information only");
        report.AppendLine();
        report.AppendLine("| Template | Family | Cases | Clauses | Recognised | Wrong | Rejected | Callsign | Pass / Flaky / Fail | Mean WER |");
        report.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (TemplateResult t in results.Templates)
        {
            report.AppendLine(
                $"| {t.Template} | {t.Family} | {t.Cases} | {RateCells(t.Commands)} | {t.Pass} / {t.Flaky} / {t.Fail} | {FormatWer(t.MeanWer)} |"
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
        return report.ToString().ReplaceLineEndings("\n");
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
        report.AppendLine("| Family | Change | Recognised (baseline → now) | Wrong (baseline → now) |");
        report.AppendLine("|---|---|---|---|");
        foreach (FamilyDiff f in diff.Families)
        {
            report.AppendLine(
                $"| {f.Family} | {Arrow(f.Kind)} | {FormatRate(f.BaselineRecognitionRate)} → {FormatRate(f.CurrentRecognitionRate)} "
                    + $"| {FormatRate(f.BaselineErrorRate)} → {FormatRate(f.CurrentErrorRate)} |"
            );
        }
    }

    private static string FormatRate(double? rate) => rate is null ? "—" : rate.Value.ToString("P1", CultureInfo.InvariantCulture);

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
