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
/// transducer export, which takes no prompt but can decode by beam search with hotwords and an n-best
/// list — pooled per speaker role, and compared with a committed baseline. On a folder that carries <c>rooms.json</c> and
/// <c>slots.json</c>, <c>--slots</c> also runs the context decoder over each clip under its unpadded, padded and oracle rooms
/// and scores the slots it recovers (<see cref="CorpusSlotRun"/>); <c>--resolve</c> decodes over the whole n-best, and
/// <c>--room-hotwords</c> biases each clip's beam search with its room's spoken forms.
///
/// Run with <c>--corpus-wer uwb-atcc|&lt;dir&gt; [--subset N | --all] [--seed S] [--stt &lt;kind&gt;:&lt;source&gt;]
/// [--device cpu|gpu] [--prompt default|none|both] [--decoding greedy|beam] [--num-paths N]
/// [--hotwords-file &lt;path&gt;] [--hotword-score S] [--hotword-case asis|title|lower|upper]
/// [--slots [--resolve] [--room-hotwords unpadded|padded|oracle [--hotword-cap N]]]
/// [--update-baseline] [--out &lt;dir&gt;]</c>. Writes <c>results.json</c>, <c>results.jsonl</c> and
/// <c>report.md</c> into the out dir. Exit codes: 0 no regression (or no baseline), 1 usage error, 2 setup
/// error, 3 regression.
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
        + "[--device cpu|gpu] [--prompt default|none|both] [--decoding greedy|beam] [--num-paths N] [--hotwords-file <path>] "
        + "[--hotword-score S] [--hotword-case asis|title|lower|upper] "
        + "[--slots [--resolve] [--room-hotwords unpadded|padded|oracle [--hotword-cap N]]] [--update-baseline] [--out <dir>]";

    private const string LoadFailureHint = "the model probably failed to load (see %LOCALAPPDATA%/yaat/yaat-speech-sandbox.log)";

    private const int ProgressEvery = 50;

    private static readonly TimeSpan DownloadLimit = TimeSpan.FromMinutes(20);

    private static readonly string? RepoRoot = OuroborosRunner.TryFindRepoRoot();

    private static readonly CorpusRole[] RoleOrder = [CorpusRole.Pilot, CorpusRole.Controller, CorpusRole.Mixed, CorpusRole.Unknown];

    /// <summary>A command line resolved into a run.</summary>
    /// <param name="Corpus"><c>uwb-atcc</c>, or the folder's lowercased directory name.</param>
    /// <param name="CorpusDir">The folder's full path; null for UWB-ATCC.</param>
    /// <param name="BaselinePath">Where the baseline lives; null when the run is not inside a yaat checkout.</param>
    /// <param name="Hotwords">The phrases every clip's stream is biased toward; empty for none.</param>
    /// <param name="HotwordsNote">
    /// The <c>--hotwords-file</c> as results record it, never a directory, or the <c>--room-hotwords</c> room, cap, score and
    /// case; null with neither.
    /// </param>
    /// <param name="Slots">The slot-scoring options; null without <c>--slots</c>.</param>
    public sealed record Options(
        string Corpus,
        string? CorpusDir,
        int? Subset,
        int Seed,
        SttChoice Stt,
        IReadOnlyList<string> Prompts,
        bool UpdateBaseline,
        string OutDir,
        string? BaselinePath,
        IReadOnlyList<string> Hotwords,
        string? HotwordsNote,
        CorpusSlotOptions? Slots
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
        public SttChoice Stt { get; set; } = new(SttKind.Whisper, LmKitModelCatalog.RecommendedWhisperId, SttDevice.Default, SherpaDecoding.Greedy);
        public SttDevice? Device { get; set; }
        public string? Prompt { get; set; }
        public bool UpdateBaseline { get; set; }
        public string? OutDir { get; set; }
        public SherpaDecodingMethod? Decoding { get; set; }
        public int? NumPaths { get; set; }
        public string? HotwordsFile { get; set; }
        public float? HotwordScore { get; set; }
        public HotwordCase? Casing { get; set; }
        public IReadOnlyList<string> Hotwords { get; set; } = [];
        public string? HotwordsNote { get; set; }
        public CorpusSlotArgs Slots { get; } = new();
    }

    /// <summary>The corpus the first argument names.</summary>
    /// <param name="Name"><c>uwb-atcc</c>, or the folder's lowercased directory name.</param>
    /// <param name="Dir">The folder's full path; null for UWB-ATCC.</param>
    private sealed record CorpusChoice(string Name, string? Dir);

    /// <summary>The loaded segments and the identity fields the results record for them.</summary>
    private sealed record LoadedCorpus(IReadOnlyList<CorpusSegment> Segments, string Revision, string Sha256);

    /// <summary>What every prompt arm transcribes, and the slot work each scores; the slot run is null without <c>--slots</c>.</summary>
    private sealed record RunInput(IReadOnlyList<CorpusSegment> Segments, CorpusSlotRun? Slots);

    /// <summary>What one prompt arm transcribes and how it biases the engine.</summary>
    /// <param name="Prompt"><c>default</c> (the production biasing prompt) or <c>none</c>.</param>
    /// <param name="Slots">The run's slot work; null without <c>--slots</c>.</param>
    private sealed record ArmInput(string Prompt, IReadOnlyList<string> Hotwords, IReadOnlyList<CorpusSegment> Segments, CorpusSlotRun? Slots);

    /// <summary>
    /// What an arm's timing needs from the run: the process to sample, the device, the load it already paid,
    /// the GPU memory before and after that load, the probe to sample through the arm, and the run's warning
    /// about the device.
    /// </summary>
    private sealed record ArmTiming(
        Process Self,
        string Device,
        long PreLoadWorkingSetBytes,
        long PreLoadGpuBytes,
        long AfterLoadGpuBytes,
        double LoadSeconds,
        GpuMemoryProbe Gpu,
        string? DeviceWarning
    );

    /// <summary>The run's engine and the timing every arm of it reports; disposing it releases both.</summary>
    private sealed record EngineSetup(ICorpusSttEngine Engine, ArmTiming Timing) : IDisposable
    {
        public void Dispose()
        {
            Engine.Dispose();
            Timing.Gpu.Dispose();
        }
    }

    /// <summary>
    /// What every segment of one prompt arm shares: the engine it runs on, its bias, where its lines go and its slot tallies
    /// (null without <c>--slots</c>).
    /// </summary>
    private sealed record ArmContext(
        ICorpusSttEngine Engine,
        ArmInput Input,
        SttBias Bias,
        List<CorpusWerSegmentLine> SegmentLines,
        ArmTiming Timing,
        CorpusSlotArm? Slots
    );

    /// <summary>One prompt arm's running counts while its segments are transcribed.</summary>
    private sealed class ArmTally(int probeCount, int segmentCount, long afterLoadGpuBytes)
    {
        /// <summary>How many opening segments the load probe watches.</summary>
        public int ProbeCount { get; } = probeCount;

        /// <summary>The probe segments' hypotheses so far, in order.</summary>
        public List<string?> FirstHypotheses { get; } = new(probeCount);

        /// <summary>Every segment's latency and whether the engine served it from its cache.</summary>
        public List<(double LatencyMs, bool Cached)> Latencies { get; } = new(segmentCount);

        /// <summary>Every scored segment pooled together.</summary>
        public WerTally Overall { get; } = new();

        /// <summary>Every scored segment pooled by speaker role.</summary>
        public Dictionary<CorpusRole, WerTally> ByRole { get; } = [];

        /// <summary>Every segment's counts, in the order they were transcribed, for the group rows.</summary>
        public List<(CorpusSegment Segment, WerCounts Counts)> Scored { get; } = new(segmentCount);

        /// <summary>How much audio the arm has transcribed.</summary>
        public double AudioSeconds { get; set; }

        /// <summary>How many segments came back with no transcript.</summary>
        public int NoTranscript { get; set; }

        /// <summary>The highest GPU memory the arm sampled, starting from what the load left behind.</summary>
        public long PeakGpuBytes { get; set; } = afterLoadGpuBytes;
    }

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
        if (!CorpusSlotRun.TrySetUp(options.CorpusDir, loaded.Segments, options.Slots, out CorpusSlotRun? slots))
        {
            return AtcOuroborosAnalysis.ExitSetupError;
        }

        using var self = Process.GetCurrentProcess();
        self.Refresh();
        long preLoadWorkingSetBytes = self.WorkingSet64;

        using EngineSetup? setup = await SetUpEngineAsync(options.Stt, self, preLoadWorkingSetBytes).ConfigureAwait(false);
        if (setup is null)
        {
            return AtcOuroborosAnalysis.ExitSetupError;
        }
        ArmTiming timing = setup.Timing;

        var segmentLines = new List<CorpusWerSegmentLine>();
        var run = new RunInput(loaded.Segments, slots);
        List<CorpusWerArm>? arms = await RunArmsAsync(setup.Engine, options, run, segmentLines, timing).ConfigureAwait(false);
        if (arms is null)
        {
            return AtcOuroborosAnalysis.ExitSetupError;
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
            arms,
            options.HotwordsNote,
            CorpusSlotRun.UnbuiltOf(slots)
        );
        Directory.CreateDirectory(options.OutDir);
        await WriteResultsAsync(options.OutDir, results, segmentLines).ConfigureAwait(false);
        return options.UpdateBaseline
            ? await UpdateBaselineAsync(options, options.BaselinePath, results).ConfigureAwait(false)
            : await CompareAndReportAsync(options, options.BaselinePath, results).ConfigureAwait(false);
    }

    /// <summary>
    /// Transcribes the whole corpus once per prompt arm, reporting each arm as it finishes. Null (after
    /// printing the same FATAL line) when an arm's load probe failed.
    /// </summary>
    private static async Task<List<CorpusWerArm>?> RunArmsAsync(
        ICorpusSttEngine engine,
        Options options,
        RunInput run,
        List<CorpusWerSegmentLine> segmentLines,
        ArmTiming timing
    )
    {
        IReadOnlyList<CorpusSegment> segments = run.Segments;
        var arms = new List<CorpusWerArm>();
        foreach (string prompt in options.Prompts)
        {
            var input = new ArmInput(prompt, options.Hotwords, segments, run.Slots);
            CorpusWerArm? arm = await RunArmAsync(engine, input, segmentLines, timing).ConfigureAwait(false);
            if (arm is null)
            {
                int probed = Math.Min(LoadProbeSegments, segments.Count);
                string kind = CorpusSttEngines.KindText(options.Stt.Kind);
                Console.Error.WriteLine(
                    $"FATAL: {kind} model {options.Stt.Source} returned no transcript for the first {probed} segments; {LoadFailureHint}"
                );
                return null;
            }
            arms.Add(arm);
            timing = timing with { DeviceWarning = null };
            Console.WriteLine($"{prompt}: WER {CorpusWerBaselineComparer.Percent(arm.Roles[0].Wer)} over {arm.Roles[0].Segments} segments");
            CorpusSlotReport.WriteConsole(arm);
            if (arm.NoTranscript > 0)
            {
                Console.WriteLine(arm.NoTranscriptLine());
            }
        }
        return arms;
    }

    /// <summary>
    /// Creates the run's engine, samples the process's GPU memory around its model load and checks the run
    /// reached the device it asked for. Null (after printing why) when the engine cannot be set up.
    /// </summary>
    private static async Task<EngineSetup?> SetUpEngineAsync(SttChoice choice, Process self, long preLoadWorkingSetBytes)
    {
        var gpu = new GpuMemoryProbe();
        long preLoadGpuBytes = gpu.Sample();
        ICorpusSttEngine engine = CorpusSttEngines.Create(choice);
        string? setupError = engine.SetupError();
        if (setupError is not null)
        {
            AbortSetup(setupError, engine, gpu);
            return null;
        }

        double loadSeconds;
        try
        {
            loadSeconds = await WarmUpAsync(engine).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            AbortSetup(ex.Message, engine, gpu);
            return null;
        }

        string device = CorpusSttEngines.DeviceText(choice);
        if ((choice.Kind == SttKind.Whisper) && BackendCheckFailed(device, engine, gpu))
        {
            return null;
        }

        long afterLoadGpuBytes = gpu.Sample();
        string? warning = GpuUseCheck.Verdict(device, preLoadGpuBytes, afterLoadGpuBytes, out bool isError);
        if (isError)
        {
            AbortSetup(warning, engine, gpu);
            return null;
        }
        if (warning is not null)
        {
            Console.WriteLine($"WARNING: {warning}");
        }

        var timing = new ArmTiming(self, device, preLoadWorkingSetBytes, preLoadGpuBytes, afterLoadGpuBytes, loadSeconds, gpu, warning);
        return new EngineSetup(engine, timing);
    }

    /// <summary>
    /// Prints why a Whisper run did not reach the CPU backend and disposes the engine and the probe it
    /// cannot use on it; false when the backend is the one the run asked for.
    /// </summary>
    private static bool BackendCheckFailed(string device, ICorpusSttEngine engine, GpuMemoryProbe gpu)
    {
        string? verdict = GpuUseCheck.BackendVerdict(device, LMKit.Global.Runtime.Backend, out bool isError);
        if (!isError)
        {
            return false;
        }

        AbortSetup(verdict, engine, gpu);
        return true;
    }

    /// <summary>Prints why the run cannot go on and releases what set-up had made for it.</summary>
    private static void AbortSetup(string? reason, ICorpusSttEngine engine, GpuMemoryProbe gpu)
    {
        Console.Error.WriteLine($"FATAL: {reason}");
        engine.Dispose();
        gpu.Dispose();
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
        ArmInput input,
        List<CorpusWerSegmentLine> segmentLines,
        ArmTiming timing
    )
    {
        string prompt = input.Prompt;
        IReadOnlyList<CorpusSegment> segments = input.Segments;
        var bias = new SttBias((prompt == "default") ? WhisperBiasingPrompt.Default : string.Empty, input.Hotwords);
        var tally = new ArmTally(Math.Min(LoadProbeSegments, segments.Count), segments.Count, timing.AfterLoadGpuBytes);
        var arm = new ArmContext(engine, input, bias, segmentLines, timing, input.Slots?.StartArm());
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < segments.Count; i++)
        {
            if (!await TranscribeArmSegmentAsync(arm, tally, segments[i], i).ConfigureAwait(false))
            {
                return null;
            }
        }
        return new CorpusWerArm(
            prompt,
            RoleRows(tally.Overall, tally.ByRole),
            tally.AudioSeconds,
            watch.Elapsed.TotalSeconds,
            tally.NoTranscript,
            SummarizeTiming(timing, tally.PeakGpuBytes, tally.Latencies),
            CorpusWerGroups.ProvenanceRows(tally.Scored),
            CorpusWerGroups.FeedRows(tally.Scored),
            arm.Slots?.Results()
        );
    }

    /// <summary>
    /// Transcribes one segment into the arm's tally, printing the progress line. False when the arm's load
    /// probe has now failed, which ends the arm at this segment.
    /// </summary>
    private static async Task<bool> TranscribeArmSegmentAsync(ArmContext arm, ArmTally tally, CorpusSegment segment, int index)
    {
        SttBias bias = SegmentBias(arm, segment);
        var segmentWatch = Stopwatch.StartNew();
        SttHypotheses transcript = await arm.Engine.TranscribeAsync(segment.Samples16k, bias, CancellationToken.None).ConfigureAwait(false);
        segmentWatch.Stop();
        double latencyMs = transcript.LatencyMs ?? segmentWatch.Elapsed.TotalMilliseconds;
        tally.Latencies.Add((latencyMs, transcript.Cached));
        if (transcript.Text is null)
        {
            tally.NoTranscript++;
        }
        if (LoadProbeFailed(tally, index, transcript.Text))
        {
            return false;
        }
        WerCounts counts = ScoreSegment(arm, segment, transcript, latencyMs, bias);
        tally.Overall.Add(counts);
        tally.Scored.Add((segment, counts));
        RoleTally(tally.ByRole, segment.Role).Add(counts);
        tally.AudioSeconds += segment.Samples16k.Length / (double)AudioCaptureService.SampleRate;
        tally.PeakGpuBytes = Math.Max(tally.PeakGpuBytes, arm.Timing.Gpu.Sample());
        if ((index + 1) % ProgressEvery == 0)
        {
            Console.WriteLine($"  {arm.Input.Prompt}: {index + 1}/{arm.Input.Segments.Count}");
        }
        return true;
    }

    /// <summary>
    /// Records a probe segment's hypothesis and says whether the probe has now failed: the arm's last
    /// opening segment, with every earlier one, came back empty.
    /// </summary>
    private static bool LoadProbeFailed(ArmTally tally, int index, string? hypothesis)
    {
        if (index >= tally.ProbeCount)
        {
            return false;
        }
        tally.FirstHypotheses.Add(hypothesis);
        return (index == tally.ProbeCount - 1) && LooksLikeLoadFailure(tally.FirstHypotheses);
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
    private static CorpusWerTiming SummarizeTiming(ArmTiming timing, long peakGpuBytes, IReadOnlyList<(double LatencyMs, bool Cached)> latencies)
    {
        timing.Self.Refresh();
        var memory = new CorpusWerMemory(timing.PreLoadWorkingSetBytes, timing.Self.PeakWorkingSet64, timing.PreLoadGpuBytes, peakGpuBytes);
        return CorpusWerLatency.Summarize(timing.Device, timing.LoadSeconds, latencies, memory) with { DeviceWarning = timing.DeviceWarning };
    }

    /// <summary>The bias the segment's stream runs under: the arm's, with the clip's room hotwords under <c>--room-hotwords</c>.</summary>
    private static SttBias SegmentBias(ArmContext arm, CorpusSegment segment) =>
        (arm.Slots is null) ? arm.Bias : arm.Slots.Run.BiasFor(segment, arm.Bias);

    /// <summary>
    /// Normalises both sides, scores them, decodes the segment's slots when the run scores them and records the segment's
    /// <c>results.jsonl</c> line.
    /// </summary>
    private static WerCounts ScoreSegment(ArmContext arm, CorpusSegment segment, SttHypotheses transcript, double latencyMs, SttBias bias)
    {
        string reference = WerTextNormalizer.Normalize(segment.Reference);
        string? normalized = (transcript.Text is null) ? null : WerTextNormalizer.Normalize(transcript.Text);
        WerCounts counts = WerScorer.Score(reference, normalized);
        CorpusSlotClipLines? slots = arm.Slots?.Add(segment, transcript, bias);
        arm.SegmentLines.Add(
            new CorpusWerSegmentLine(
                arm.Input.Prompt,
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
                transcript.Cached,
                RecordedNbest(transcript),
                slots?.HotwordPhrases,
                slots?.Lines
            )
        );
        return counts;
    }

    /// <summary>The alternatives a <c>results.jsonl</c> line keeps: only an engine's that carry tokens, else null.</summary>
    public static IReadOnlyList<SttHypothesis>? RecordedNbest(SttHypotheses transcript) =>
        transcript.Entries.Any(e => e.Tokens.Count > 0) ? transcript.Entries : null;

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
        string hotwords = (results.Hotwords is null) ? string.Empty : $", hotwords {results.Hotwords}";
        string corpus = $"- Corpus `{results.Corpus}`, revision `{revision}`, seed {results.Seed}, subset {subset}";
        report.AppendLine($"{corpus}, model `{results.SttModel}`{hotwords}");
        CorpusSlotReport.AppendUnbuilt(report, results.UnbuiltCallsigns);
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
        AppendTable(report, "Role", arm.Roles);
        if (arm.Provenance is not null)
        {
            report.AppendLine();
            AppendTable(report, "Provenance", arm.Provenance);
        }
        if (arm.Feeds is not null)
        {
            report.AppendLine();
            AppendTable(report, "Feed", arm.Feeds);
        }
        CorpusSlotReport.Append(report, arm);
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

    /// <summary>One totals table: the <paramref name="label"/> column and the pooled counts beside it.</summary>
    internal static void AppendTable(StringBuilder report, string label, IReadOnlyList<CorpusWerRoleTotals> rows)
    {
        report.AppendLine($"| {label} | Segments | Reference words | S | D | I | WER |");
        report.AppendLine("|---|---|---|---|---|---|---|");
        foreach (CorpusWerRoleTotals row in rows)
        {
            report.AppendLine(Row(row));
        }
    }

    /// <summary>One <c>report.md</c> table row.</summary>
    private static string Row(CorpusWerRoleTotals row)
    {
        string wer = CorpusWerBaselineComparer.Percent(row.Wer);
        return $"| {row.Role} | {row.Segments} | {row.ReferenceWords} | {row.Substitutions} | {row.Deletions} | {row.Insertions} | {wer} |";
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
        report.AppendLine($"Device {timing.Device}, model load and warm-up {load} s");
        report.AppendLine(
            $"Latency over {timing.TimedSegments} segments ({timing.CachedSegments} cached, excluded): p50 {p50} ms, p90 {p90} ms, max {max} ms"
        );
        string workingSet = $"{Megabytes(timing.PreLoadWorkingSetBytes)} MB before load, peak {Megabytes(timing.PeakWorkingSetBytes)} MB";
        report.AppendLine($"Memory: working set {workingSet}; GPU memory {gpu}");
        if (timing.DeviceWarning is not null)
        {
            report.AppendLine(timing.DeviceWarning);
        }
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
            case "--subset" or "--seed" or "--stt" or "--device" or "--prompt" or "--out":
                return (i + 1 < args.Length) ? ApplyValue(parsed, flag, args[++i]) : $"{flag} needs a value";
            case "--decoding" or "--num-paths" or "--hotwords-file" or "--hotword-score" or "--hotword-case":
                return (i + 1 < args.Length) ? ApplySherpaValue(parsed, flag, args[++i]) : $"{flag} needs a value";
            default:
                return parsed.Slots.Apply(args, ref i);
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
            case "--device":
                return ApplyDevice(parsed, value);
            case "--prompt":
                return ApplyPrompt(parsed, value);
            default:
                parsed.OutDir = value;
                return null;
        }
    }

    private static string? ApplySherpaValue(ParsedArgs parsed, string flag, string value)
    {
        switch (flag)
        {
            case "--decoding":
                return ApplyDecoding(parsed, value);
            case "--num-paths":
                return ApplyNumPaths(parsed, value);
            case "--hotword-score":
                return ApplyHotwordScore(parsed, value);
            case "--hotword-case":
                return ApplyHotwordCase(parsed, value);
            default:
                parsed.HotwordsFile = value;
                return null;
        }
    }

    private static string? ApplyDecoding(ParsedArgs parsed, string value)
    {
        if (value is not ("greedy" or "beam"))
        {
            return $"--decoding must be greedy or beam, got '{value}'";
        }
        parsed.Decoding = (value == "greedy") ? SherpaDecodingMethod.Greedy : SherpaDecodingMethod.Beam;
        return null;
    }

    private static string? ApplyNumPaths(ParsedArgs parsed, string value)
    {
        bool valid = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int paths);
        if (!valid || (paths < 1) || (paths > SherpaDecoding.MaxBeamPaths))
        {
            return $"--num-paths needs an integer from 1 to {SherpaDecoding.MaxBeamPaths}, got '{value}'";
        }
        parsed.NumPaths = paths;
        return null;
    }

    private static string? ApplyHotwordScore(ParsedArgs parsed, string value)
    {
        bool valid = float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float score);
        if (!valid || !float.IsFinite(score) || (score <= 0))
        {
            return $"--hotword-score needs a positive number, got '{value}'";
        }
        parsed.HotwordScore = score;
        return null;
    }

    private static string? ApplyHotwordCase(ParsedArgs parsed, string value)
    {
        HotwordCase? casing = value switch
        {
            "asis" => HotwordCase.AsIs,
            "title" => HotwordCase.Title,
            "lower" => HotwordCase.Lower,
            "upper" => HotwordCase.Upper,
            _ => null,
        };
        if (casing is null)
        {
            return $"--hotword-case must be asis, title, lower or upper, got '{value}'";
        }
        parsed.Casing = casing;
        return null;
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

    private static string? ApplyDevice(ParsedArgs parsed, string value)
    {
        if (value is not ("cpu" or "gpu"))
        {
            return $"--device must be cpu or gpu, got '{value}'";
        }
        parsed.Device = (value == "cpu") ? SttDevice.Cpu : SttDevice.Gpu;
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
        string? error = ResolveDevice(parsed) ?? SlotFlagError(parsed) ?? ResolveDecoding(parsed);
        if (error is not null)
        {
            return Failed(error);
        }
        return TryResolveCorpus(parsed.Corpus, out CorpusChoice? choice, out string? corpusError)
            ? ResolveDefaults(parsed, choice)
            : Failed(corpusError);
    }

    /// <summary>Puts <c>--device</c> on the engine choice; the cloud engines have no local device to name.</summary>
    private static string? ResolveDevice(ParsedArgs parsed)
    {
        if (parsed.Device is not SttDevice device)
        {
            return null;
        }
        if (parsed.Stt.Kind is SttKind.Gemini or SttKind.OpenAi)
        {
            return $"--device applies to local engines; {CorpusSttEngines.KindText(parsed.Stt.Kind)} runs in the cloud";
        }
        parsed.Stt = parsed.Stt with { Device = device };
        return null;
    }

    /// <summary>Checks the sherpa decoding flags against each other, then puts the beam choice and its hotwords on the run.</summary>
    private static string? ResolveDecoding(ParsedArgs parsed)
    {
        string? error = DecodingFlagError(parsed) ?? HotwordFlagError(parsed);
        if ((error is not null) || (parsed.Decoding != SherpaDecodingMethod.Beam))
        {
            return error;
        }

        float score = parsed.HotwordScore ?? SherpaHotwords.DefaultScore;
        var decoding = new SherpaDecoding(SherpaDecodingMethod.Beam, parsed.NumPaths ?? SherpaDecoding.MaxBeamPaths, score);
        parsed.Stt = parsed.Stt with { Decoding = decoding };
        return (parsed.HotwordsFile is null) ? NoteRoomHotwords(parsed, score) : LoadHotwords(parsed, parsed.HotwordsFile, score);
    }

    /// <summary>Checks the slot flags against the corpus, the decoding and the hotwords file.</summary>
    private static string? SlotFlagError(ParsedArgs parsed) =>
        parsed.Slots.Error(parsed.Corpus == UwbCorpusName, parsed.Decoding == SherpaDecodingMethod.Beam, parsed.HotwordsFile is not null);

    /// <summary>Writes the <c>--room-hotwords</c> note into the run, if it has room hotwords; never an error.</summary>
    private static string? NoteRoomHotwords(ParsedArgs parsed, float score)
    {
        parsed.HotwordsNote = parsed.Slots.Note(score, parsed.Casing ?? HotwordCase.Title);
        return null;
    }

    private static string? DecodingFlagError(ParsedArgs parsed)
    {
        if ((parsed.Decoding is not null) && (parsed.Stt.Kind != SttKind.Sherpa))
        {
            return $"--decoding applies to the sherpa engine, got {CorpusSttEngines.KindText(parsed.Stt.Kind)}";
        }

        bool isBeam = parsed.Decoding == SherpaDecodingMethod.Beam;
        if ((parsed.NumPaths is not null) && !isBeam)
        {
            return "--num-paths needs --decoding beam";
        }
        return ((parsed.HotwordsFile is not null) && !isBeam) ? "--hotwords-file needs --decoding beam" : null;
    }

    private static string? HotwordFlagError(ParsedArgs parsed)
    {
        if ((parsed.HotwordsFile is not null) || (parsed.Slots.RoomHotwords is not null))
        {
            return null;
        }
        if (parsed.HotwordScore is not null)
        {
            return "--hotword-score needs --hotwords-file";
        }
        return (parsed.Casing is not null) ? "--hotword-case needs --hotwords-file" : null;
    }

    /// <summary>Reads the hotwords file into the run and writes its note, which names the file but never its directory.</summary>
    private static string? LoadHotwords(ParsedArgs parsed, string path, float score)
    {
        if (!File.Exists(path))
        {
            return $"the hotwords file {path} does not exist";
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"could not read the hotwords file {path}: {ex.Message}";
        }

        HotwordCase casing = parsed.Casing ?? HotwordCase.Title;
        parsed.Hotwords = SherpaHotwords.ParseLines(lines, casing, out string? error);
        if (error is not null)
        {
            return error;
        }

        string scoreText = score.ToString(CultureInfo.InvariantCulture);
        string caseText = casing.ToString().ToLowerInvariant();
        parsed.HotwordsNote = $"{Path.GetFileName(path)}, {parsed.Hotwords.Count} phrases, score {scoreText}, case {caseText}";
        return null;
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
        (int? subset, string outDir, string? baselinePath) = ResolveTargets(parsed, choice);
        var options = new Options(
            choice.Name,
            choice.Dir,
            subset,
            parsed.Seed,
            parsed.Stt,
            prompts,
            parsed.UpdateBaseline,
            outDir,
            baselinePath,
            parsed.Hotwords,
            parsed.HotwordsNote,
            parsed.Slots.ToOptions(parsed.Casing ?? HotwordCase.Title)
        );
        return new ParseResult(options, null);
    }

    /// <summary>
    /// The subset, out dir and baseline path the run resolves to: the subset the arguments pick (the UWB
    /// corpus's default one unless <c>--all</c>), a timestamped folder under <c>.tmp/corpus-wer</c> when
    /// <c>--out</c> names none, and the corpus's shipped baseline inside a checkout.
    /// </summary>
    private static (int? Subset, string OutDir, string? BaselinePath) ResolveTargets(ParsedArgs parsed, CorpusChoice choice)
    {
        int? subset = parsed.All ? null : (parsed.Subset ?? ((choice.Dir is null) ? DefaultUwbSubset : null));
        string outDir = Path.GetFullPath(parsed.OutDir ?? DefaultOutDir());
        string? baselinePath =
            (RepoRoot is null) ? null : Path.Combine(RepoRoot, "tools", "Yaat.SpeechSandbox", "Corpus", $"{choice.Name}-wer-baseline.json");
        return (subset, outDir, baselinePath);
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
