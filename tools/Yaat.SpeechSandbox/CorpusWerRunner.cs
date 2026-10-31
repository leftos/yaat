using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Yaat.Client.Services;
using Yaat.Sim.Speech;

namespace Yaat.SpeechSandbox;

/// <summary>
/// Word error rate of one STT engine on real ATC audio: the pinned UWB-ATCC test split (downloaded into
/// the YAAT cache) or a local folder of WAV + transcript pairs, transcribed through the engine
/// <c>--stt</c> picks — Whisper with the production biasing prompt and without it, or a sherpa-onnx
/// transducer export, which takes no prompt — pooled per speaker role, and compared with a committed
/// baseline.
///
/// Run with <c>--corpus-wer uwb-atcc|&lt;dir&gt; [--subset N | --all] [--seed S] [--stt &lt;kind&gt;:&lt;source&gt;]
/// [--prompt default|none|both] [--update-baseline] [--out &lt;dir&gt;]</c>. Writes <c>results.json</c>,
/// <c>results.jsonl</c> and <c>report.md</c> into the out dir. Exit codes: 0 no regression (or no
/// baseline), 1 usage error, 2 setup error, 3 regression.
/// </summary>
public static class CorpusWerRunner
{
    public const string UwbCorpusName = "uwb-atcc";
    public const int DefaultSeed = 1117;
    public const int DefaultUwbSubset = 400;
    public const int ExitUsage = 1;

    /// <summary>How many opening segments of an arm must all come back empty before the model counts as not loaded.</summary>
    public const int LoadProbeSegments = 10;

    private const string Usage =
        "Usage: Yaat.SpeechSandbox --corpus-wer uwb-atcc|<dir> [--subset N | --all] [--seed S] [--stt <kind>:<source>] "
        + "[--prompt default|none|both] [--update-baseline] [--out <dir>]";

    private const string LoadFailureHint = "the model probably failed to load (see %LOCALAPPDATA%/yaat/yaat-speech-sandbox.log)";

    private const int ProgressEvery = 50;

    private static readonly TimeSpan DownloadLimit = TimeSpan.FromMinutes(20);

    private static readonly string? RepoRoot = OuroborosRunner.TryFindRepoRoot();

    private static readonly CorpusRole[] RoleOrder = [CorpusRole.Pilot, CorpusRole.Controller, CorpusRole.Mixed, CorpusRole.Unknown];

    /// <summary>A command line resolved into a run.</summary>
    /// <param name="Corpus"><c>uwb-atcc</c>, or the folder's lowercased directory name.</param>
    /// <param name="CorpusDir">The folder's full path; null for UWB-ATCC.</param>
    /// <param name="BaselinePath">Where the baseline lives; null when the run is not inside a yaat checkout.</param>
    public sealed record Options(
        string Corpus,
        string? CorpusDir,
        int? Subset,
        int Seed,
        SttChoice Stt,
        IReadOnlyList<string> Prompts,
        bool UpdateBaseline,
        string OutDir,
        string? BaselinePath
    );

    /// <summary>The outcome of <see cref="ParseArgs"/>: the options, or the error that stopped the parse.</summary>
    public sealed record ParseResult(Options? Options, string? Error);

    /// <summary>The arguments as given, before defaults and cross-flag checks.</summary>
    private sealed class ParsedArgs(string corpus)
    {
        public string Corpus { get; } = corpus;
        public int? Subset { get; set; }
        public bool All { get; set; }
        public int Seed { get; set; } = DefaultSeed;
        public SttChoice Stt { get; set; } = new(SttKind.Whisper, LmKitModelCatalog.RecommendedWhisperId);
        public string? Prompt { get; set; }
        public bool UpdateBaseline { get; set; }
        public string? OutDir { get; set; }
    }

    /// <summary>The corpus the first argument names.</summary>
    /// <param name="Name"><c>uwb-atcc</c>, or the folder's lowercased directory name.</param>
    /// <param name="Dir">The folder's full path; null for UWB-ATCC.</param>
    private sealed record CorpusChoice(string Name, string? Dir);

    /// <summary>The loaded segments and the identity fields the results record for them.</summary>
    private sealed record LoadedCorpus(IReadOnlyList<CorpusSegment> Segments, string Revision, string Sha256);

    /// <summary>What an arm's timing needs from the run: the process to sample, the device and the load it already paid.</summary>
    private sealed record ArmTiming(Process Self, string Device, long PreLoadWorkingSetBytes, double LoadSeconds);

    public static async Task<int> RunAsync(string[] args)
    {
        ParseResult parse = ParseArgs(args);
        if (parse.Options is null)
        {
            Console.Error.WriteLine($"FATAL: {parse.Error}");
            Console.Error.WriteLine(Usage);
            return ExitUsage;
        }
        Options options = parse.Options;
        if (options.BaselinePath is null)
        {
            Console.Error.WriteLine("FATAL: --corpus-wer must run from a yaat checkout (the baseline lives in tools/Yaat.SpeechSandbox/Corpus/)");
            return AtcOuroborosAnalysis.ExitSetupError;
        }

        LoadedCorpus? loaded = await LoadAsync(options).ConfigureAwait(false);
        if (loaded is null)
        {
            return AtcOuroborosAnalysis.ExitSetupError;
        }
        Console.WriteLine(LoadedLine(loaded.Segments));

        using var self = Process.GetCurrentProcess();
        self.Refresh();
        long preLoadWorkingSetBytes = self.WorkingSet64;

        using ICorpusSttEngine engine = CorpusSttEngines.Create(options.Stt);
        string? setupError = engine.SetupError();
        if (setupError is not null)
        {
            Console.Error.WriteLine($"FATAL: {setupError}");
            return AtcOuroborosAnalysis.ExitSetupError;
        }
        double loadSeconds = await WarmUpAsync(engine).ConfigureAwait(false);
        var timing = new ArmTiming(self, CorpusSttEngines.DefaultDevice(options.Stt.Kind), preLoadWorkingSetBytes, loadSeconds);

        var arms = new List<CorpusWerArm>();
        var segmentLines = new List<CorpusWerSegmentLine>();
        foreach (string prompt in options.Prompts)
        {
            CorpusWerArm? arm = await RunArmAsync(engine, prompt, loaded.Segments, segmentLines, timing).ConfigureAwait(false);
            if (arm is null)
            {
                int probed = Math.Min(LoadProbeSegments, loaded.Segments.Count);
                string kind = CorpusSttEngines.KindText(options.Stt.Kind);
                Console.Error.WriteLine(
                    $"FATAL: {kind} model {options.Stt.Source} returned no transcript for the first {probed} segments; {LoadFailureHint}"
                );
                return AtcOuroborosAnalysis.ExitSetupError;
            }
            arms.Add(arm);
            Console.WriteLine($"{prompt}: WER {CorpusWerBaselineComparer.Percent(arm.Roles[0].Wer)} over {arm.Roles[0].Segments} segments");
            if (arm.NoTranscript > 0)
            {
                Console.WriteLine(arm.NoTranscriptLine());
            }
        }

        string subsetFingerprint = CorpusSubset.Fingerprint(loaded.Segments.Select(s => s.Id));
        var results = new CorpusWerBaseline(
            options.Corpus,
            loaded.Revision,
            loaded.Sha256,
            options.Seed,
            options.Subset,
            subsetFingerprint,
            CorpusSttEngines.RecordedModel(options.Stt),
            arms
        );
        Directory.CreateDirectory(options.OutDir);
        await WriteResultsAsync(options.OutDir, results, segmentLines).ConfigureAwait(false);
        return options.UpdateBaseline
            ? await UpdateBaselineAsync(options, options.BaselinePath, results).ConfigureAwait(false)
            : await CompareAndReportAsync(options, options.BaselinePath, results).ConfigureAwait(false);
    }

    /// <summary>
    /// True when every one of an arm's opening transcripts came back empty, which means the model
    /// never loaded rather than that the audio was silent. No transcripts at all is not a failure.
    /// </summary>
    public static bool LooksLikeLoadFailure(IReadOnlyList<string?> firstHypotheses) =>
        (firstHypotheses.Count > 0) && firstHypotheses.All(h => h is null);

    /// <summary>Loads the corpus; null (after printing why) on a download, hash or folder setup failure.</summary>
    private static async Task<LoadedCorpus?> LoadAsync(Options options)
    {
        try
        {
            if (options.CorpusDir is null)
            {
                return await LoadUwbAsync(options).ConfigureAwait(false);
            }
            string fingerprint = LocalCorpus.Fingerprint(options.CorpusDir);
            return new LoadedCorpus(LocalCorpus.Load(options.CorpusDir, options.Subset, options.Seed), string.Empty, fingerprint);
        }
        catch (OperationCanceledException ex)
        {
            string limit = DownloadLimit.TotalMinutes.ToString(CultureInfo.InvariantCulture);
            Console.Error.WriteLine($"FATAL: the {options.Corpus} download stalled and was cancelled after {limit} minutes: {ex.Message}");
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException)
        {
            Console.Error.WriteLine($"FATAL: could not load the {options.Corpus} corpus: {ex.Message}");
            return null;
        }
    }

    private static async Task<LoadedCorpus> LoadUwbAsync(Options options)
    {
        using var http = new HttpClient { Timeout = DownloadLimit };
        using var stall = new CancellationTokenSource(DownloadLimit);
        string path = await UwbAtccCorpus
            .EnsureDownloadedAsync(http, UwbAtccCorpus.TestSplit, UwbAtccCorpus.DefaultCachePath, stall.Token)
            .ConfigureAwait(false);
        IReadOnlyList<CorpusSegment> segments = await UwbAtccCorpus.LoadAsync(path, options.Subset, options.Seed).ConfigureAwait(false);
        return new LoadedCorpus(segments, UwbAtccCorpus.Revision, UwbAtccCorpus.TestSplit.Sha256);
    }

    private static string LoadedLine(IReadOnlyList<CorpusSegment> segments)
    {
        int Count(CorpusRole role) => segments.Count(s => s.Role == role);
        int unknown = Count(CorpusRole.Unknown);
        string unknownPart = (unknown > 0) ? $", unknown {unknown}" : string.Empty;
        string roles = $"pilot {Count(CorpusRole.Pilot)}, controller {Count(CorpusRole.Controller)}, mixed {Count(CorpusRole.Mixed)}";
        return $"loaded {segments.Count} segments ({roles}{unknownPart})";
    }

    /// <summary>
    /// Transcribes every segment once under one prompt arm and pools the counts per role. Null when the
    /// arm's first <see cref="LoadProbeSegments"/> segments (or all of them, if fewer) all came back empty.
    /// </summary>
    private static async Task<CorpusWerArm?> RunArmAsync(
        ICorpusSttEngine engine,
        string prompt,
        IReadOnlyList<CorpusSegment> segments,
        List<CorpusWerSegmentLine> segmentLines,
        ArmTiming timing
    )
    {
        string promptText = (prompt == "default") ? WhisperBiasingPrompt.Default : string.Empty;
        int probeCount = Math.Min(LoadProbeSegments, segments.Count);
        var firstHypotheses = new List<string?>(probeCount);
        var latencies = new List<(double LatencyMs, bool Cached)>(segments.Count);
        var overall = new WerTally();
        var byRole = new Dictionary<CorpusRole, WerTally>();
        double audioSeconds = 0;
        int noTranscript = 0;
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < segments.Count; i++)
        {
            CorpusSegment segment = segments[i];
            var segmentWatch = Stopwatch.StartNew();
            CorpusTranscript transcript = await engine.TranscribeAsync(segment.Samples16k, promptText, CancellationToken.None).ConfigureAwait(false);
            segmentWatch.Stop();
            double latencyMs = transcript.LatencyMs ?? segmentWatch.Elapsed.TotalMilliseconds;
            latencies.Add((latencyMs, transcript.Cached));
            string? hypothesis = transcript.Text;
            if (hypothesis is null)
            {
                noTranscript++;
            }
            if (i < probeCount)
            {
                firstHypotheses.Add(hypothesis);
                if ((i == probeCount - 1) && LooksLikeLoadFailure(firstHypotheses))
                {
                    return null;
                }
            }
            WerCounts counts = ScoreSegment(prompt, segment, transcript, latencyMs, segmentLines);
            overall.Add(counts);
            RoleTally(byRole, segment.Role).Add(counts);
            audioSeconds += segment.Samples16k.Length / (double)AudioCaptureService.SampleRate;
            if ((i + 1) % ProgressEvery == 0)
            {
                Console.WriteLine($"  {prompt}: {i + 1}/{segments.Count}");
            }
        }
        return new CorpusWerArm(
            prompt,
            RoleRows(overall, byRole),
            audioSeconds,
            watch.Elapsed.TotalSeconds,
            noTranscript,
            SummarizeTiming(timing, latencies)
        );
    }

    /// <summary>Loads the model and returns how long the load took, which every arm of the run reports.</summary>
    private static async Task<double> WarmUpAsync(ICorpusSttEngine engine)
    {
        var watch = Stopwatch.StartNew();
        await engine.WarmUpAsync(CancellationToken.None).ConfigureAwait(false);
        watch.Stop();
        return watch.Elapsed.TotalSeconds;
    }

    /// <summary>Samples the process now and summarizes the arm's latency and memory into its timing.</summary>
    private static CorpusWerTiming SummarizeTiming(ArmTiming timing, IReadOnlyList<(double LatencyMs, bool Cached)> latencies)
    {
        timing.Self.Refresh();
        var memory = new CorpusWerMemory(timing.PreLoadWorkingSetBytes, timing.Self.PeakWorkingSet64, -1, -1);
        return CorpusWerLatency.Summarize(timing.Device, timing.LoadSeconds, latencies, memory);
    }

    /// <summary>Normalises both sides, scores them and records the segment's <c>results.jsonl</c> line.</summary>
    private static WerCounts ScoreSegment(
        string prompt,
        CorpusSegment segment,
        CorpusTranscript transcript,
        double latencyMs,
        List<CorpusWerSegmentLine> segmentLines
    )
    {
        string reference = WerTextNormalizer.Normalize(segment.Reference);
        string? normalized = (transcript.Text is null) ? null : WerTextNormalizer.Normalize(transcript.Text);
        WerCounts counts = WerScorer.Score(reference, normalized);
        segmentLines.Add(
            new CorpusWerSegmentLine(
                prompt,
                segment.Id,
                CorpusRoles.Label(segment.Role),
                reference,
                transcript.Text,
                normalized ?? string.Empty,
                counts.Substitutions,
                counts.Deletions,
                counts.Insertions,
                counts.ReferenceWords,
                latencyMs,
                transcript.Cached
            )
        );
        return counts;
    }

    private static WerTally RoleTally(Dictionary<CorpusRole, WerTally> byRole, CorpusRole role)
    {
        if (!byRole.TryGetValue(role, out WerTally? tally))
        {
            tally = new WerTally();
            byRole[role] = tally;
        }
        return tally;
    }

    /// <summary>The <c>all</c> row, then every role with at least one segment in the fixed role order.</summary>
    private static IReadOnlyList<CorpusWerRoleTotals> RoleRows(WerTally overall, Dictionary<CorpusRole, WerTally> byRole) =>
        [
            CorpusWerRoleTotals.From("all", overall),
            .. RoleOrder.Where(byRole.ContainsKey).Select(role => CorpusWerRoleTotals.From(CorpusRoles.Label(role), byRole[role])),
        ];

    private static async Task WriteResultsAsync(string outDir, CorpusWerBaseline results, List<CorpusWerSegmentLine> segmentLines)
    {
        await File.WriteAllTextAsync(Path.Combine(outDir, "results.json"), CorpusWerBaselineComparer.Serialize(results)).ConfigureAwait(false);
        var jsonl = new StringBuilder();
        foreach (CorpusWerSegmentLine line in segmentLines)
        {
            jsonl.Append(JsonSerializer.Serialize(line, CorpusWerJsonlContext.Default.CorpusWerSegmentLine)).Append('\n');
        }
        await File.WriteAllTextAsync(Path.Combine(outDir, "results.jsonl"), jsonl.ToString()).ConfigureAwait(false);
    }

    /// <summary>Writes this run as the baseline and reports it.</summary>
    private static async Task<int> UpdateBaselineAsync(Options options, string baselinePath, CorpusWerBaseline results)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(baselinePath)!);
        await File.WriteAllTextAsync(baselinePath, CorpusWerBaselineComparer.Serialize(CorpusWerBaselineComparer.ForBaseline(results)))
            .ConfigureAwait(false);
        await WriteReportAsync(options, results, [$"Baseline updated: `{baselinePath}`"]).ConfigureAwait(false);
        Console.WriteLine($"Baseline written to {baselinePath}");
        return AtcOuroborosAnalysis.ExitNoRegression;
    }

    /// <summary>Compares the run with the baseline when there is one, writes the report and returns the exit code.</summary>
    private static async Task<int> CompareAndReportAsync(Options options, string baselinePath, CorpusWerBaseline results)
    {
        if (!File.Exists(baselinePath))
        {
            Console.WriteLine($"No baseline exists at {baselinePath}; skipping the comparison.");
            await WriteReportAsync(options, results, [$"No baseline at `{baselinePath}` — nothing to compare against."]).ConfigureAwait(false);
            return AtcOuroborosAnalysis.ExitNoRegression;
        }

        CorpusWerBaseline? baseline = CorpusWerBaselineComparer.Deserialize(await File.ReadAllTextAsync(baselinePath).ConfigureAwait(false));
        if (baseline is null)
        {
            Console.Error.WriteLine($"FATAL: baseline {baselinePath} is empty or not a results document.");
            return AtcOuroborosAnalysis.ExitSetupError;
        }

        CorpusWerComparison comparison = CorpusWerBaselineComparer.Compare(baseline, results);
        List<string> notes = [$"Compared with `{baselinePath}`."];
        notes.AddRange(comparison.Skipped ? [$"Comparison skipped, the run was measured differently: {comparison.SkipReason}"] : comparison.Lines);
        await WriteReportAsync(options, results, notes).ConfigureAwait(false);
        Console.WriteLine(Verdict(comparison));
        return comparison.HasRegression ? AtcOuroborosAnalysis.ExitRegression : AtcOuroborosAnalysis.ExitNoRegression;
    }

    private static string Verdict(CorpusWerComparison comparison)
    {
        if (comparison.Skipped)
        {
            return $"Baseline comparison skipped: {comparison.SkipReason}";
        }
        string lines = string.Join(Environment.NewLine, comparison.Lines);
        return comparison.HasRegression
            ? $"{lines}{Environment.NewLine}REGRESSION against the baseline — see report.md."
            : $"{lines}{Environment.NewLine}No regression against the baseline.";
    }

    private static Task WriteReportAsync(Options options, CorpusWerBaseline results, IReadOnlyList<string> baselineNotes) =>
        File.WriteAllTextAsync(Path.Combine(options.OutDir, "report.md"), BuildReport(results, baselineNotes));

    /// <summary>Renders the <c>report.md</c> body for a finished run.</summary>
    public static string BuildReport(CorpusWerBaseline results, IReadOnlyList<string> baselineNotes)
    {
        var report = new StringBuilder();
        report.AppendLine("# Corpus WER report");
        report.AppendLine();
        string revision = (results.Revision.Length == 0) ? "n/a" : results.Revision;
        string subset = results.Subset?.ToString(CultureInfo.InvariantCulture) ?? "all";
        report.AppendLine($"- Corpus `{results.Corpus}`, revision `{revision}`, seed {results.Seed}, subset {subset}, model `{results.SttModel}`");
        foreach (CorpusWerArm arm in results.Arms)
        {
            AppendArm(report, arm);
        }
        report.AppendLine("## Baseline comparison");
        report.AppendLine();
        foreach (string note in baselineNotes)
        {
            report.AppendLine($"- {note}");
        }
        return report.ToString().ReplaceLineEndings("\n");
    }

    private static void AppendArm(StringBuilder report, CorpusWerArm arm)
    {
        report.AppendLine();
        report.AppendLine($"## Prompt: {arm.Prompt}");
        report.AppendLine();
        report.AppendLine("| Role | Segments | Reference words | S | D | I | WER |");
        report.AppendLine("|---|---|---|---|---|---|---|");
        foreach (CorpusWerRoleTotals r in arm.Roles)
        {
            string wer = CorpusWerBaselineComparer.Percent(r.Wer);
            report.AppendLine($"| {r.Role} | {r.Segments} | {r.ReferenceWords} | {r.Substitutions} | {r.Deletions} | {r.Insertions} | {wer} |");
        }
        report.AppendLine();
        string audio = arm.AudioSeconds.ToString("0.0", CultureInfo.InvariantCulture);
        string wall = arm.WallSeconds.ToString("0.0", CultureInfo.InvariantCulture);
        string rtf = (arm.AudioSeconds > 0) ? (arm.WallSeconds / arm.AudioSeconds).ToString("0.00", CultureInfo.InvariantCulture) : "n/a";
        report.AppendLine($"Audio {audio} s, wall {wall} s, real-time factor {rtf}");
        report.AppendLine();
        if (arm.Timing is not null)
        {
            AppendTiming(report, arm.Timing);
        }
        if (arm.NoTranscript > 0)
        {
            report.AppendLine(arm.NoTranscriptLine());
            report.AppendLine();
        }
    }

    /// <summary>The device, load, latency and memory lines of an arm that measured them.</summary>
    private static void AppendTiming(StringBuilder report, CorpusWerTiming timing)
    {
        string load = timing.LoadSeconds.ToString("0.0", CultureInfo.InvariantCulture);
        string p50 = timing.LatencyP50Ms.ToString("0", CultureInfo.InvariantCulture);
        string p90 = timing.LatencyP90Ms.ToString("0", CultureInfo.InvariantCulture);
        string max = timing.LatencyMaxMs.ToString("0", CultureInfo.InvariantCulture);
        string gpu =
            (timing.PeakGpuBytes < 0) ? "n/a" : $"{Megabytes(timing.PreLoadGpuBytes)} MB before load, peak {Megabytes(timing.PeakGpuBytes)} MB";
        report.AppendLine($"Device {timing.Device}, model load {load} s");
        report.AppendLine(
            $"Latency over {timing.TimedSegments} segments ({timing.CachedSegments} cached, excluded): p50 {p50} ms, p90 {p90} ms, max {max} ms"
        );
        report.AppendLine(
            $"Memory: working set {Megabytes(timing.PreLoadWorkingSetBytes)} MB before load, peak {Megabytes(timing.PeakWorkingSetBytes)} MB; GPU memory {gpu}"
        );
        report.AppendLine();
    }

    private static string Megabytes(long bytes) => ((double)bytes / 1048576).ToString("0", CultureInfo.InvariantCulture);

    /// <summary>Parses a <c>--corpus-wer</c> command line (the arguments after the flag) into a run, or the first error.</summary>
    public static ParseResult ParseArgs(string[] args)
    {
        if ((args.Length == 0) || args[0].StartsWith("--", StringComparison.Ordinal))
        {
            return Failed("the first argument must be the corpus: uwb-atcc or a directory");
        }
        var parsed = new ParsedArgs(args[0]);
        for (int i = 1; i < args.Length; i++)
        {
            string? error = ApplyFlag(parsed, args, ref i);
            if (error is not null)
            {
                return Failed(error);
            }
        }
        return Resolve(parsed);
    }

    /// <summary>Applies the flag at <paramref name="i"/> (advancing past its value); returns an error or null.</summary>
    private static string? ApplyFlag(ParsedArgs parsed, string[] args, ref int i)
    {
        string flag = args[i];
        switch (flag)
        {
            case "--all":
                parsed.All = true;
                return null;
            case "--update-baseline":
                parsed.UpdateBaseline = true;
                return null;
            case "--subset" or "--seed" or "--stt" or "--prompt" or "--out":
                return (i + 1 < args.Length) ? ApplyValue(parsed, flag, args[++i]) : $"{flag} needs a value";
            default:
                return $"unrecognised argument '{flag}'";
        }
    }

    private static string? ApplyValue(ParsedArgs parsed, string flag, string value)
    {
        switch (flag)
        {
            case "--subset":
                return ApplySubset(parsed, value);
            case "--seed":
                return ApplySeed(parsed, value);
            case "--stt":
                return ApplyStt(parsed, value);
            case "--prompt":
                return ApplyPrompt(parsed, value);
            default:
                parsed.OutDir = value;
                return null;
        }
    }

    private static string? ApplySubset(ParsedArgs parsed, string value)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int subset) || (subset <= 0))
        {
            return $"--subset needs a positive integer, got '{value}'";
        }
        parsed.Subset = subset;
        return null;
    }

    private static string? ApplySeed(ParsedArgs parsed, string value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seed))
        {
            return $"--seed needs an integer, got '{value}'";
        }
        parsed.Seed = seed;
        return null;
    }

    private static string? ApplyStt(ParsedArgs parsed, string value)
    {
        if (!CorpusSttEngines.TryParse(value, out SttChoice? choice, out string? error))
        {
            return error;
        }
        parsed.Stt = choice;
        return null;
    }

    private static string? ApplyPrompt(ParsedArgs parsed, string value)
    {
        if (value is not ("default" or "none" or "both"))
        {
            return $"--prompt must be default, none or both, got '{value}'";
        }
        parsed.Prompt = value;
        return null;
    }

    /// <summary>Checks the flags against each other, then resolves the corpus and fills in the defaults.</summary>
    private static ParseResult Resolve(ParsedArgs parsed)
    {
        if ((parsed.Subset is not null) && parsed.All)
        {
            return Failed("--subset and --all exclude each other");
        }
        return TryResolveCorpus(parsed.Corpus, out CorpusChoice? choice, out string? error) ? ResolveDefaults(parsed, choice) : Failed(error);
    }

    /// <summary>
    /// <c>uwb-atcc</c> exactly is the download; anything else must be an existing folder whose name can
    /// key a baseline of its own: not empty (a drive root) and not <c>uwb-atcc</c> in any case.
    /// </summary>
    private static bool TryResolveCorpus(string arg, [NotNullWhen(true)] out CorpusChoice? choice, [NotNullWhen(false)] out string? error)
    {
        choice = null;
        error = null;
        if (arg == UwbCorpusName)
        {
            choice = new CorpusChoice(UwbCorpusName, null);
            return true;
        }
        if (!Directory.Exists(arg))
        {
            error = $"'{arg}' is neither {UwbCorpusName} nor an existing directory";
            return false;
        }
        string dir = Path.GetFullPath(arg);
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(dir)).ToLowerInvariant();
        if ((name.Length == 0) || (name == UwbCorpusName))
        {
            error = $"the folder {dir} cannot key a baseline as '{name}' (empty, or the download's name); rename it";
            return false;
        }
        choice = new CorpusChoice(name, dir);
        return true;
    }

    private static ParseResult ResolveDefaults(ParsedArgs parsed, CorpusChoice choice)
    {
        if (!TryResolvePrompts(parsed.Prompt, parsed.Stt, out string[]? prompts, out string? error))
        {
            return Failed(error);
        }
        if (parsed.UpdateBaseline && !parsed.Stt.TakesPrompt)
        {
            return Failed(
                $"--update-baseline writes the shipped Whisper baseline; the {CorpusSttEngines.KindText(parsed.Stt.Kind)} engine cannot update it"
            );
        }
        int? subset = parsed.All ? null : (parsed.Subset ?? ((choice.Dir is null) ? DefaultUwbSubset : null));
        string outDir = Path.GetFullPath(parsed.OutDir ?? DefaultOutDir());
        string? baselinePath =
            (RepoRoot is null) ? null : Path.Combine(RepoRoot, "tools", "Yaat.SpeechSandbox", "Corpus", $"{choice.Name}-wer-baseline.json");
        var options = new Options(choice.Name, choice.Dir, subset, parsed.Seed, parsed.Stt, prompts, parsed.UpdateBaseline, outDir, baselinePath);
        return new ParseResult(options, null);
    }

    /// <summary>
    /// The prompt arms a run takes. Whisper runs the production prompt and the bare model unless one arm
    /// is asked for; a sherpa transducer or a cloud engine takes no prompt, so its single arm is <c>none</c>
    /// and asking for either prompt arm is an error rather than a flag silently ignored.
    /// </summary>
    private static bool TryResolvePrompts(
        string? prompt,
        SttChoice stt,
        [NotNullWhen(true)] out string[]? prompts,
        [NotNullWhen(false)] out string? error
    )
    {
        prompts = null;
        error = null;
        if (stt.TakesPrompt)
        {
            prompts = (prompt is null or "both") ? ["default", "none"] : [prompt];
            return true;
        }
        if (prompt is "default" or "both")
        {
            error = $"--prompt {prompt} needs a Whisper engine; {CorpusSttEngines.KindText(stt.Kind)} engines take no prompt";
            return false;
        }
        prompts = ["none"];
        return true;
    }

    /// <summary>A timestamped folder under the checkout's <c>.tmp/corpus-wer</c>, or the current directory's outside a checkout.</summary>
    private static string DefaultOutDir()
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return Path.Combine(RepoRoot ?? Directory.GetCurrentDirectory(), ".tmp", "corpus-wer", stamp);
    }

    private static ParseResult Failed(string message) => new(null, message);
}
