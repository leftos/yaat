using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Yaat.Client.Services;
using Yaat.Client.Services.Speech;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Speech;
using Yaat.Sim.Speech.Context;

namespace Yaat.SpeechSandbox;

/// <summary>
/// Ground-truth of one eval case, read from <c>expected.json</c> in the case directory.
/// <c>Canonical</c> is the only required field; the rest refine scoring and context.
/// <c>Template</c> is the synthetic generator's template key (null for real recordings); the
/// runway / taxiway / destination sets are the scenario context the case was labeled under.
/// <c>Airport</c>, <c>ApproachPosition</c> and <c>FacilityRadioNames</c> describe the room accurate mode decodes the case
/// against; a real capture's reviewer fills them in by hand.
/// </summary>
public sealed record EvalExpectation(
    string Canonical,
    string? Transcript,
    string? Callsign,
    List<string>? ActiveCallsigns,
    List<string>? ProgrammedFixes,
    bool Synthetic,
    string? Template,
    Dictionary<string, List<string>>? AvailableRunways,
    List<string>? TaxiwayNames,
    List<string>? DestinationNames,
    string? Airport,
    bool ApproachPosition,
    List<string>? FacilityRadioNames
)
{
    /// <summary>
    /// The room the case is decoded against in accurate mode: one aircraft per active callsign, in order, with no type and
    /// no assigned runway; the case's programmed fixes belong to the aircraft the case addresses.
    /// </summary>
    /// <param name="layout">The airport's ground layout, or null when none is loaded.</param>
    /// <param name="caseName">The case directory name, for the error.</param>
    /// <exception cref="InvalidDataException">The case names no airport.</exception>
    public RoomContextRequest ToRoomContextRequest(AirportGroundLayout? layout, string caseName)
    {
        if (Airport is null)
        {
            throw new InvalidDataException($"{caseName}: accurate mode needs an \"airport\" in expected.json");
        }
        List<RoomAircraftInput> aircraft =
        [
            .. (ActiveCallsigns ?? []).Select(callsign => new RoomAircraftInput(
                callsign,
                null,
                string.Equals(callsign, Callsign, StringComparison.OrdinalIgnoreCase) ? (ProgrammedFixes ?? []) : [],
                null
            )),
        ];
        return new RoomContextRequest(Airport, ApproachPosition, layout, aircraft, FacilityRadioNames ?? []);
    }

    /// <summary>Builds the speech context the case is mapped under — the same one for label verification and scoring.</summary>
    public SpeechContext ToSpeechContext(string whisperInitialPrompt)
    {
        var runways = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach ((string airport, List<string> designators) in AvailableRunways ?? [])
        {
            runways[airport] = designators;
        }
        return new SpeechContext(ActiveCallsigns ?? [], ProgrammedFixes ?? [], whisperInitialPrompt)
        {
            AvailableRunways = runways,
            TaxiwayNames = new HashSet<string>(TaxiwayNames ?? [], StringComparer.OrdinalIgnoreCase),
            DestinationNames = new HashSet<string>(DestinationNames ?? [], StringComparer.OrdinalIgnoreCase),
        };
    }
}

/// <summary>Per-case verdict across all trials: PASS = every trial matched, FAIL = none did, FLAKY = some did.</summary>
public enum EvalVerdict
{
    Pass,
    Flaky,
    Fail,
}

/// <summary>Scored outcome of one eval case.</summary>
/// <param name="CaseName">Case directory name.</param>
/// <param name="ExpectedCanonical">Labeled canonical.</param>
/// <param name="GotCanonicals">The canonical each trial produced (or a <c>&lt;…&gt;</c> marker for an empty transcript / null mapping).</param>
/// <param name="Verdict">PASS / FLAKY / FAIL across the trials.</param>
/// <param name="Wer">Best (lowest) STT word-error-rate across trials; null when the case has no labeled transcript.</param>
/// <param name="Synthetic">True for generator-produced (Piper) cases.</param>
/// <param name="Template">The generator template key, or null for real recordings.</param>
/// <param name="Trials">What each trial heard and mapped, in trial order.</param>
/// <param name="Clauses">The command-level score of every trial, summed (<see cref="CommandScoring"/>).</param>
/// <param name="CallsignsMatched">Trials whose extracted callsign matched the expected one; 0 when the case expects none.</param>
/// <param name="CallsignsExpected">Trials scored against an expected callsign: the trial count, or 0 when the case expects none.</param>
public sealed record EvalCaseResult(
    string CaseName,
    string ExpectedCanonical,
    IReadOnlyList<string> GotCanonicals,
    EvalVerdict Verdict,
    double? Wer,
    bool Synthetic,
    string? Template,
    IReadOnlyList<EvalTrial> Trials,
    ClauseScore Clauses,
    int CallsignsMatched,
    int CallsignsExpected
);

/// <summary>One trial of an eval case.</summary>
/// <param name="Transcript">The raw STT transcript (empty when STT heard nothing).</param>
/// <param name="MapperCanonical">The rule mapper's canonical, or null when no rule matched.</param>
/// <param name="FinalCanonical">
/// The canonical the full pipeline produced (rule mapper, then the LLM fallback), or null when nothing mapped.
/// </param>
/// <param name="RawTextFallback">
/// True when both mappers failed and <paramref name="FinalCanonical"/> is the raw command text surfaced beside the callsign.
/// </param>
public sealed record EvalTrial(string Transcript, string? MapperCanonical, string? FinalCanonical, bool RawTextFallback);

/// <summary>Everything one scoring pass produced.</summary>
/// <param name="Timing">The engine's load, STT latency over every trial, and memory.</param>
/// <param name="Decode">The accurate-mode decode tallies; null in fast mode.</param>
public sealed record EvalRunResult(
    IReadOnlyList<EvalCaseResult> Cases,
    int Skipped,
    string Summary,
    string ReportPath,
    CorpusWerTiming Timing,
    AccurateDecodeStats? Decode
);

/// <summary>
/// The production STT + mapping stack an eval pass scores through: the STT engine <c>--stt</c> picks
/// (the prefs Whisper model by default), loaded and timed before the first trial, the rule mapper, and the LLM fallback.
/// A beam choice is accurate mode: the whole n-best is decoded against each case's room. The pipeline samples the
/// process's memory around the model load and through the run. <see cref="SetupError"/> is non-null when a model is
/// missing or the engine did not load where it was asked to, in which case nothing can be scored.
/// </summary>
internal sealed class EvalPipeline : IDisposable
{
    /// <summary>LLM config for the LMKIT_TEST_MODEL override path. GPU layers stay on auto.</summary>
    private sealed class OverrideLlmRuntimeConfig(string modelPath) : ILlmRuntimeConfig
    {
        public string ModelPath { get; } = modelPath;
        public int GpuLayers => -1;
    }

    private const string LlmSetupError = "LLM model not configured/found — check LMKIT_TEST_MODEL and Settings → Speech.";
    private const string AccurateNeedsRepoError = "accurate mode must run from a yaat checkout (it loads the airport layouts)";

    private readonly ICorpusSttEngine _stt;
    private readonly LocalLlmService _llm;
    private readonly GpuMemoryProbe _gpu;
    private readonly long _preLoadWorkingSetBytes;
    private readonly long _preLoadGpuBytes;
    private readonly string? _repoRoot;
    private readonly RoomContextCache _roomCache = new();
    private readonly Dictionary<string, AirportGroundLayout?> _layouts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The engine choice the pipeline was built from.</summary>
    public SttChoice Choice { get; }

    /// <summary>True for accurate mode: beam decoding, the whole n-best resolved against the case's room.</summary>
    public bool Accurate => Choice.Decoding.Method == SherpaDecodingMethod.Beam;

    public string SttLabel { get; }

    /// <summary>Which text accurate mode hands the mappers.</summary>
    public AccurateCommandText AccurateText { get; }

    public string LlmModelPath { get; }
    public bool LlmOverridden { get; }
    public string BiasingPrompt { get; }
    public string? SetupError { get; private set; }
    public PhraseologyCommandMapper RuleMapper { get; } = new();
    public LocalLlmCommandMapper LlmMapper { get; }
    public LocalLlmCallsignResolver CallsignResolver { get; }

    /// <summary>The rule mapper, the LLM fallback and the callsign resolver, as accurate mode takes them.</summary>
    public SpeechMappers Mappers { get; }

    /// <summary>The device the engine runs on, as <see cref="CorpusSttEngines.DeviceText"/> records it.</summary>
    public string Device { get; }

    /// <summary>How long the model load took.</summary>
    public double LoadSeconds { get; private set; }

    /// <summary>What the GPU memory around the load said about the device, when worth reporting.</summary>
    public string? DeviceWarning { get; private set; }

    /// <summary>The highest GPU memory sampled so far, starting from what the load left behind; -1 when unmeasured.</summary>
    public long PeakGpuBytes { get; private set; }

    private EvalPipeline(SttChoice? stt, bool noBiasingPrompt, AccurateCommandText accurateText)
    {
        var prefs = new UserPreferences();

        // LMKIT_TEST_MODEL overrides the LLM the same way it does for --llm-probe and the
        // LocalLlmPipelineIntegrationTests fixture, so eval runs are reproducible across machines
        // instead of depending on whatever the developer's saved preferences point at.
        string? llmOverride = Environment.GetEnvironmentVariable("LMKIT_TEST_MODEL");
        ILlmRuntimeConfig llmConfig = string.IsNullOrWhiteSpace(llmOverride)
            ? new PreferencesLlmRuntimeConfig(prefs)
            : new OverrideLlmRuntimeConfig(llmOverride);
        LlmModelPath = llmConfig.ModelPath;
        LlmOverridden = !string.IsNullOrWhiteSpace(llmOverride);

        // STT stage: the --stt engine, or the prefs Whisper model. Every engine reaches the trial
        // loop through the same seam --corpus-wer scores through, so the scoring path is identical
        // regardless of engine.
        Choice = stt ?? new SttChoice(SttKind.Whisper, prefs.WhisperModelSize, SttDevice.Default, SherpaDecoding.Greedy);
        Device = CorpusSttEngines.DeviceText(Choice);
        _repoRoot = OuroborosRunner.TryFindRepoRoot();

        using (var self = Process.GetCurrentProcess())
        {
            self.Refresh();
            _preLoadWorkingSetBytes = self.WorkingSet64;
        }
        _gpu = new GpuMemoryProbe();
        _preLoadGpuBytes = _gpu.Sample();
        PeakGpuBytes = _preLoadGpuBytes;
        _stt = CorpusSttEngines.Create(Choice);
        // --prompt none blanks the Whisper biasing prompt for the whole run — the A/B knob for
        // asking whether the static vocabulary hint still earns its keep on a given model.
        BiasingPrompt = noBiasingPrompt ? string.Empty : WhisperBiasingPrompt.Default;
        AccurateText = accurateText;
        string rewrite = (accurateText == AccurateCommandText.Rewritten) ? "+rewrite" : string.Empty;
        SttLabel = CorpusSttEngines.RecordedModel(Choice) + rewrite + (noBiasingPrompt ? " (no biasing prompt)" : "");

        _llm = new LocalLlmService(llmConfig);
        LlmMapper = new LocalLlmCommandMapper(_llm);
        CallsignResolver = new LocalLlmCallsignResolver(_llm);
        Mappers = new SpeechMappers(RuleMapper, LlmMapper, CallsignResolver);
        SetupError = CombineSetupErrors(
            _stt.SetupError(),
            _llm.IsConfigured ? null : LlmSetupError,
            (Accurate && (_repoRoot is null)) ? AccurateNeedsRepoError : null
        );
    }

    /// <summary>
    /// Loads real navdata from the checkout (the mapping stage validates canonicals through CommandParser, and
    /// PhoneticFixMatcher's full-database fallback needs it), constructs the model stack and, when it can be set up,
    /// loads the STT model, timing the load and checking it reached the device the choice asked for. Outside a yaat
    /// checkout the navdata cannot load, which is the pipeline's <see cref="SetupError"/>.
    /// </summary>
    /// <param name="stt">The STT engine; null takes the Whisper model saved in preferences.</param>
    /// <param name="noBiasingPrompt">True blanks the Whisper biasing prompt for the whole run.</param>
    /// <param name="accurateText">Which text accurate mode hands the mappers.</param>
    public static async Task<EvalPipeline> CreateAsync(SttChoice? stt, bool noBiasingPrompt, AccurateCommandText accurateText)
    {
        bool navDataLoaded = SandboxNavData.TryInitialize(out string? navDataError);
        var pipeline = new EvalPipeline(stt, noBiasingPrompt, accurateText);
        if (!navDataLoaded)
        {
            pipeline.SetupError = CombineSetupErrors(navDataError, pipeline.SetupError);
        }
        if (pipeline.SetupError is null)
        {
            await pipeline.LoadModelAsync().ConfigureAwait(false);
        }
        return pipeline;
    }

    public Task<SttHypotheses> TranscribeAsync(float[] samples, string prompt, CancellationToken ct) =>
        _stt.TranscribeAsync(samples, new SttBias(prompt, []), ct);

    /// <summary>The case's room for accurate mode, built against the airport's committed test layout when there is one.</summary>
    /// <exception cref="InvalidDataException">The case names no airport.</exception>
    public TransmissionContext RoomContext(EvalExpectation expectation, string caseName)
    {
        AirportGroundLayout? layout = (expectation.Airport is null) ? null : Layout(expectation.Airport);
        return RoomContextFactory.Build(
            _roomCache,
            expectation.ToRoomContextRequest(layout, caseName),
            NavigationDatabase.Instance,
            DecoderSettings.Default
        );
    }

    /// <summary>Samples the process's GPU memory and keeps the highest figure.</summary>
    public void SampleGpu() => PeakGpuBytes = Math.Max(PeakGpuBytes, _gpu.Sample());

    /// <summary>The run's timing: the load, the percentiles over <paramref name="latencies"/> and the memory figures now.</summary>
    public CorpusWerTiming Timing(IReadOnlyList<(double LatencyMs, bool Cached)> latencies)
    {
        using var self = Process.GetCurrentProcess();
        self.Refresh();
        var memory = new CorpusWerMemory(_preLoadWorkingSetBytes, self.PeakWorkingSet64, _preLoadGpuBytes, PeakGpuBytes);
        return CorpusWerLatency.Summarize(Device, LoadSeconds, latencies, memory) with { DeviceWarning = DeviceWarning };
    }

    public void Dispose()
    {
        _llm.Dispose();
        _stt.Dispose();
        _gpu.Dispose();
    }

    /// <summary>
    /// Warms the engine up as the timed model load, then checks the load reached the device the choice asked for; a failed
    /// check becomes <see cref="SetupError"/>, a warning is printed and kept.
    /// </summary>
    private async Task LoadModelAsync()
    {
        var watch = Stopwatch.StartNew();
        try
        {
            await _stt.WarmUpAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            SetupError = ex.Message;
            return;
        }
        LoadSeconds = watch.Elapsed.TotalSeconds;

        if (Choice.Kind == SttKind.Whisper)
        {
            string? backendVerdict = GpuUseCheck.BackendVerdict(Device, LMKit.Global.Runtime.Backend, out bool backendIsError);
            if (backendIsError)
            {
                SetupError = backendVerdict;
                return;
            }
        }

        long afterLoadGpuBytes = _gpu.Sample();
        PeakGpuBytes = afterLoadGpuBytes;
        string? warning = GpuUseCheck.Verdict(Device, _preLoadGpuBytes, afterLoadGpuBytes, out bool isError);
        if (isError)
        {
            SetupError = warning;
            return;
        }
        if (warning is not null)
        {
            Console.WriteLine($"WARNING: {warning}");
        }
        DeviceWarning = warning;
    }

    private AirportGroundLayout? Layout(string airport)
    {
        if (!_layouts.TryGetValue(airport, out AirportGroundLayout? layout))
        {
            string repo = _repoRoot ?? throw new InvalidOperationException(AccurateNeedsRepoError);
            layout = CorpusSlotRun.LoadLayout(repo, airport);
            _layouts[airport] = layout;
        }
        return layout;
    }

    private static string? CombineSetupErrors(params string?[] errors)
    {
        string[] present = [.. errors.OfType<string>()];
        return (present.Length == 0) ? null : string.Join("; ", present);
    }
}

/// <summary>
/// Real-audio eval harness: scores the full production STT pipeline (Whisper → digit
/// normalization → callsign extraction → rule mapper → LLM fallback) against a labeled corpus of
/// captured PTT recordings. Complements <see cref="OuroborosRunner"/>, which covers the same
/// pipeline with synthetic Piper-TTS audio — this harness answers "how does the pipeline do on
/// what users actually said into their mic", ouroboros answers "is the pipeline internally
/// consistent".
///
/// Corpus layout — one subdirectory per case:
/// <code>
///   corpus/
///     some-case/
///       audio.wav        — 16 kHz mono int16 PCM (AudioCaptureService format)
///       expected.json    — { "canonical": "CM 8000, FH 270", "transcript": "...", "callsign":
///                            "UAL234", "activeCallsigns": ["UAL234"], "programmedFixes": [],
///                            "availableRunways": { "KOAK": ["28R"] }, "taxiwayNames": [],
///                            "destinationNames": [], "template": "fh" }
/// </code>
/// Only <c>canonical</c> is required. <c>transcript</c> (the words actually spoken, natural
/// English) additionally enables word-error-rate scoring of the STT stage in isolation. Cases
/// exported from the in-client speech-sample store (audio.wav + session.json) are auto-stubbed:
/// when <c>expected.json</c> is missing but <c>session.json</c> exists, a pre-filled
/// <c>expected.json</c> is written from the recorded session (marked unreviewed) and the case is
/// skipped until a human confirms the labels by removing the <c>"unreviewed"</c> flag.
///
/// Run with <c>--eval &lt;corpus-dir&gt; [--out-dir &lt;dir&gt;] [--trials N]</c>. Like ouroboros,
/// N &gt; 1 transcribes+maps each case N times to see through GPU nondeterminism: PASS = all
/// trials produced the expected canonical, FAIL = none did, FLAKY = some did. Within one run a
/// case's audio is fixed, so the trials vary only the STT path; the synthetic corpus pins that
/// audio across runs with <c>SynthAudioCache</c>, because Piper's sampling differs between
/// processes.
/// </summary>
public static class EvalRunner
{
    /// <summary>The options <see cref="SpeechSampleStore"/> writes <c>session.json</c> with, so the stub reads the same schema.</summary>
    private static readonly JsonSerializerOptions SessionJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine(
                "Usage: Yaat.SpeechSandbox --eval <corpus-dir> [--out-dir <dir>] [--trials N] [--stt <kind>:<source>] [--device cpu|gpu] "
                    + "[--decoding greedy|beam] [--num-paths N] [--accurate-text raw|rewritten]"
            );
            Console.Error.WriteLine();
            Console.Error.WriteLine("Scores the production STT pipeline against labeled real-audio cases.");
            Console.Error.WriteLine("Each corpus subdirectory needs audio.wav + expected.json (see EvalRunner docs).");
            Console.Error.WriteLine(
                "--stt picks the STT stage: whisper:<model-source>, sherpa:<model-dir>, sherpa-streaming:<model-dir>, "
                    + "gemini:<model> or openai:<model> (default: the Settings Whisper model)."
            );
            Console.Error.WriteLine(
                "--device picks a local engine's device; --decoding beam (sherpa only, --num-paths 1-4, default 4) is accurate mode, "
                    + "which resolves the whole n-best against each case's room (needs \"airport\" in expected.json)."
            );
            Console.Error.WriteLine(
                "--accurate-text picks what accurate mode hands the mappers: raw (the winning hypothesis's text, the default) or "
                    + "rewritten (the decoder's transcript with its entity spans corrected)."
            );
            return 1;
        }

        string corpusDir = args[0];
        string? outDirOverride = null;
        var engine = new EngineFlags();
        string? promptMode = null;
        int trials = 1;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--out-dir" && i + 1 < args.Length)
            {
                outDirOverride = args[++i];
            }
            else if (EngineFlags.Handles(args[i]) && (i + 1 < args.Length))
            {
                string? flagError = engine.TryApply(args[i], args[++i]);
                if (flagError is not null)
                {
                    Console.Error.WriteLine($"FATAL: {flagError}");
                    return 2;
                }
            }
            else if (args[i] == "--prompt" && i + 1 < args.Length)
            {
                promptMode = args[++i];
                if (promptMode is not ("default" or "none"))
                {
                    Console.Error.WriteLine($"FATAL: --prompt must be 'default' or 'none', got '{promptMode}'");
                    return 2;
                }
            }
            else if (args[i] == "--trials" && i + 1 < args.Length)
            {
                if (!int.TryParse(args[++i], CultureInfo.InvariantCulture, out trials) || trials < 1)
                {
                    Console.Error.WriteLine($"FATAL: --trials must be a positive integer, got '{args[i]}'");
                    return 2;
                }
            }
        }

        SttChoice? stt = engine.Resolve(out string? resolveError);
        string? engineError = resolveError ?? CheckEngineOptions(stt, promptMode, trials);
        if (engineError is not null)
        {
            Console.Error.WriteLine($"FATAL: {engineError}");
            return 2;
        }

        if (!Directory.Exists(corpusDir))
        {
            Console.Error.WriteLine($"FATAL: corpus directory not found: {corpusDir}");
            return 2;
        }

        using EvalPipeline pipeline = await EvalPipeline
            .CreateAsync(stt, noBiasingPrompt: promptMode == "none", accurateText: engine.AccurateText)
            .ConfigureAwait(false);
        PrintModels(pipeline);
        if (pipeline.SetupError is not null)
        {
            Console.Error.WriteLine($"FATAL: {pipeline.SetupError}");
            return 2;
        }

        string outDir = outDirOverride ?? Path.Combine(".tmp", $"speech-eval-{DateTime.Now:yyyyMMdd-HHmmss}");
        EvalRunResult result = await ScoreAsync(pipeline, corpusDir, outDir, trials).ConfigureAwait(false);
        return result.Cases.Any(c => c.Verdict == EvalVerdict.Fail) ? 1 : 0;
    }

    /// <summary>
    /// Why the chosen engine cannot take the run's prompt or trial settings, or null. A cloud engine caches
    /// every transcript, so a second trial would only repeat the first; an engine without a prompt cannot
    /// honour an explicit <c>--prompt default</c>.
    /// </summary>
    private static string? CheckEngineOptions(SttChoice? stt, string? promptMode, int trials)
    {
        if (stt is null)
        {
            return null;
        }
        string kind = CorpusSttEngines.KindText(stt.Kind);
        if ((trials > 1) && (stt.Kind is SttKind.Gemini or SttKind.OpenAi))
        {
            return $"--trials > 1 needs a local engine; {kind} results are cached, so later trials repeat the first";
        }
        if ((promptMode == "default") && !stt.TakesPrompt)
        {
            return $"--prompt default needs a Whisper engine; {kind} engines take no prompt";
        }
        return null;
    }

    internal static void PrintModels(EvalPipeline pipeline)
    {
        Console.WriteLine($"STT model: {pipeline.SttLabel}");
        Console.WriteLine($"LLM model: {pipeline.LlmModelPath}{(pipeline.LlmOverridden ? " (LMKIT_TEST_MODEL override)" : "")}");
        Console.WriteLine();
    }

    /// <summary>
    /// Scores every case directory under <paramref name="corpusDir"/> through <paramref name="pipeline"/>,
    /// printing one verdict per case and writing <c>report.md</c> into <paramref name="outDir"/>.
    /// </summary>
    internal static async Task<EvalRunResult> ScoreAsync(EvalPipeline pipeline, string corpusDir, string outDir, int trials)
    {
        Directory.CreateDirectory(outDir);

        var table = new StringBuilder();
        table.AppendLine("| Case | Verdict | Canonical (expected) | Canonical (got) | WER | Callsign | STT ms/trial |");
        table.AppendLine("|---|---|---|---|---|---|---|");
        var details = new StringBuilder();
        details.AppendLine("### Per-case transcripts");
        details.AppendLine();
        var outcomes = new CaseReporter(table, details, trials);
        var timings = new TrialTimings();

        // Real-mic and synthetic (Piper-generated) cases are tallied separately: synthetic audio
        // measures the phonetic surface on clean TTS voices, and folding it into one number
        // would let a large synthetic batch drown out the authoritative real-mic signal.
        int skipped = 0;
        var realTally = new Tally();
        var synthTally = new Tally();
        var results = new List<EvalCaseResult>();

        foreach (string caseDir in Directory.EnumerateDirectories(corpusDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            string caseName = Path.GetFileName(caseDir);
            string wavPath = Path.Combine(caseDir, "audio.wav");
            if (!File.Exists(wavPath))
            {
                Console.WriteLine($"SKIP  {caseName}: no audio.wav");
                skipped++;
                continue;
            }

            EvalExpectation? expectation = LoadOrStubExpectation(caseDir, caseName);
            if (expectation is null)
            {
                skipped++;
                continue;
            }

            CaseScore score = await ScoreCaseAsync(pipeline, caseName, expectation, WavHeader.ReadPcm16(wavPath), trials).ConfigureAwait(false);
            timings.Add(score.Timings);
            var result = new EvalCaseResult(
                caseName,
                expectation.Canonical,
                score.GotCanonicals,
                score.Verdict,
                score.Wer,
                expectation.Synthetic,
                expectation.Template,
                score.Trials,
                score.Clauses,
                score.CallsignsMatched,
                score.CallsignsExpected
            );
            results.Add(result);
            (expectation.Synthetic ? synthTally : realTally).Add(result);

            outcomes.Append(result, score, expectation);
        }

        string summary = Summarize(realTally, synthTally, skipped);
        CorpusWerTiming timing = pipeline.Timing(timings.Latencies);
        AccurateDecodeStats? decode = pipeline.Accurate ? timings.DecodeStats() : null;
        string[] runLines = RunLines(timing, decode);

        var report = new StringBuilder();
        report.AppendLine("# Speech pipeline eval report");
        report.AppendLine();
        report.AppendLine($"- Corpus: `{Path.GetFullPath(corpusDir)}`");
        report.AppendLine($"- STT: `{pipeline.SttLabel}`  LLM: `{pipeline.LlmModelPath}`  Trials per case: {trials}");
        foreach (string line in runLines)
        {
            report.AppendLine(line);
        }
        report.AppendLine();
        report.Append(table);
        report.AppendLine();
        report.Append(details);
        report.AppendLine($"**Summary:** {summary}");
        string reportPath = Path.Combine(outDir, "report.md");
        await File.WriteAllTextAsync(reportPath, report.ToString()).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine(summary);
        foreach (string line in runLines)
        {
            Console.WriteLine(line);
        }
        Console.WriteLine($"Report: {reportPath}");
        return new EvalRunResult(results, skipped, summary, reportPath, timing, decode);
    }

    /// <summary>The run's device and latency line, and its accurate-decode line in accurate mode.</summary>
    private static string[] RunLines(CorpusWerTiming timing, AccurateDecodeStats? decode) =>
        (decode is null) ? [TimingLine(timing)] : [TimingLine(timing), DecodeLine(decode)];

    /// <summary>
    /// The report line naming the device, the model load, the STT latency percentiles over the trials and the memory; the GPU
    /// part is left out when the GPU memory was not measured.
    /// </summary>
    public static string TimingLine(CorpusWerTiming timing)
    {
        bool gpuMeasured = (timing.PreLoadGpuBytes >= 0) && (timing.PeakGpuBytes >= 0);
        string gpu = gpuMeasured ? $"; GPU {Megabytes(timing.PreLoadGpuBytes)} → peak {Megabytes(timing.PeakGpuBytes)} MB" : string.Empty;
        string load = timing.LoadSeconds.ToString("0.0", CultureInfo.InvariantCulture);
        return $"- Device: {timing.Device}, load {load} s; "
            + $"STT latency p50 {Milliseconds(timing.LatencyP50Ms)} / p90 {Milliseconds(timing.LatencyP90Ms)} / max {Milliseconds(timing.LatencyMaxMs)} ms "
            + $"over {timing.TimedSegments} trials; "
            + $"working set {Megabytes(timing.PreLoadWorkingSetBytes)} MB before load, peak {Megabytes(timing.PeakWorkingSetBytes)} MB{gpu}";
    }

    /// <summary>The report line tallying accurate mode's decode states and its decode-and-map time.</summary>
    public static string DecodeLine(AccurateDecodeStats decode) =>
        $"- Accurate decode: {decode.Resolved} resolved, {decode.SayAgain} say again, {decode.NoCallsign} no callsign; "
        + $"decode+map p50 {Milliseconds(decode.DecodeMapP50Ms)} / p90 {Milliseconds(decode.DecodeMapP90Ms)} ms";

    private static string Milliseconds(double ms) => ms.ToString("0", CultureInfo.InvariantCulture);

    private static string Megabytes(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0", CultureInfo.InvariantCulture);

    /// <summary>Every trial's STT latency and, in accurate mode, its decode state and decode-and-map time.</summary>
    private sealed class TrialTimings
    {
        public List<(double LatencyMs, bool Cached)> Latencies { get; } = [];

        public List<DecodeState> DecodeStates { get; } = [];

        public List<double> DecodeMapMs { get; } = [];

        public void Add(TrialTimings other)
        {
            Latencies.AddRange(other.Latencies);
            DecodeStates.AddRange(other.DecodeStates);
            DecodeMapMs.AddRange(other.DecodeMapMs);
        }

        public AccurateDecodeStats DecodeStats() =>
            new(
                DecodeStates.Count(s => s == DecodeState.Resolved),
                DecodeStates.Count(s => s == DecodeState.SayAgain),
                DecodeStates.Count(s => s == DecodeState.NoCallsign),
                CorpusWerLatency.Percentile(DecodeMapMs, 50),
                CorpusWerLatency.Percentile(DecodeMapMs, 90)
            );
    }

    /// <summary>One line naming each tally that produced a verdict, plus the skipped-case count.</summary>
    private static string Summarize(Tally realTally, Tally synthTally, int skipped)
    {
        var parts = new List<string>();
        if (realTally.Total > 0)
        {
            parts.Add($"real: {realTally.Describe()}");
        }
        if (synthTally.Total > 0)
        {
            parts.Add($"synthetic: {synthTally.Describe()}");
        }
        if (skipped > 0)
        {
            parts.Add($"{skipped} skipped");
        }
        return parts.Count > 0 ? string.Join(" — ", parts) : "no cases ran";
    }

    /// <summary>
    /// Writes one case's console verdict, report row and detail section, so the scoring loop stays a
    /// loop and the per-case output shape lives in one place.
    /// </summary>
    private sealed class CaseReporter(StringBuilder report, StringBuilder details, int trials)
    {
        public void Append(EvalCaseResult result, CaseScore score, EvalExpectation expectation)
        {
            string verdictText = result.Verdict switch
            {
                EvalVerdict.Pass => "PASS",
                EvalVerdict.Fail => "FAIL",
                _ => $"FLAKY {score.Matches}/{trials}",
            };
            string lastCanonical = score.GotCanonicals.Count > 0 ? score.GotCanonicals[^1] : "<null>";
            string werText = score.Wer is null ? "—" : score.Wer.Value.ToString("P0", CultureInfo.InvariantCulture);
            long sttMsAvg = score.SttMsTotal / trials;
            string scoreText = $"(callsign {score.LastCallsign}, WER {werText}, STT avg {sttMsAvg} ms/trial)";
            Console.WriteLine($"{verdictText, -10} {result.CaseName}: got \"{lastCanonical}\" {scoreText}");
            Console.WriteLine($"           transcript \"{score.LastTranscript}\"");
            if (result.Verdict != EvalVerdict.Pass)
            {
                Console.WriteLine($"           expected \"{expectation.Canonical}\"");
            }
            string expected = expectation.Canonical;
            report.AppendLine(
                $"| {result.CaseName} | {verdictText} | `{expected}` | `{lastCanonical}` | {werText} | {score.LastCallsign} | {sttMsAvg} |"
            );
            details.AppendLine($"#### {result.CaseName}");
            details.AppendLine($"- Expected transcript: `{expectation.Transcript ?? "(none)"}`");
            details.AppendLine($"- Last STT transcript: `{score.LastTranscript}`");
            details.AppendLine($"- STT avg: {sttMsAvg} ms/trial (total incl. mapping: {score.TotalMs} ms)");
            details.AppendLine();
        }
    }

    private sealed record CaseScore(
        IReadOnlyList<string> GotCanonicals,
        int Matches,
        EvalVerdict Verdict,
        double? Wer,
        string LastTranscript,
        string LastCallsign,
        long SttMsTotal,
        long TotalMs,
        IReadOnlyList<EvalTrial> Trials,
        ClauseScore Clauses,
        int CallsignsMatched,
        int CallsignsExpected,
        TrialTimings Timings
    );

    /// <summary>What one trial's mapping produced: the result, the rule mapper's canonical and, in accurate mode, the decode.</summary>
    /// <param name="DecodeState">The decoder's answer for the n-best; null in fast mode.</param>
    /// <param name="DecodeMapMs">How long the decode and the mapping took; null in fast mode.</param>
    private sealed record TrialMapping(TranscriptMapResult Result, string? RuleCanonical, DecodeState? DecodeState, double? DecodeMapMs);

    /// <summary>
    /// The command-level score of <paramref name="trials"/> against <paramref name="expectedCanonical"/>,
    /// summed. A trial with no canonical (an empty transcript, nothing mapped) or a raw-text fallback
    /// scores every gold clause as rejected.
    /// </summary>
    public static ClauseScore ScoreTrials(string expectedCanonical, IReadOnlyList<EvalTrial> trials) =>
        trials.Aggregate(ClauseScore.Zero, (sum, t) => sum + CommandScoring.Score(expectedCanonical, t.FinalCanonical, t.RawTextFallback));

    /// <summary>
    /// The callsign tally of one case: every trial counts against <paramref name="expectedCallsign"/>,
    /// and a trial that extracted none (null, such as an empty transcript) is a miss. A case with no
    /// expected callsign is excluded: (0, 0).
    /// </summary>
    /// <param name="trialCallsigns">The callsign each trial extracted, in trial order; null when it extracted none.</param>
    /// <param name="expectedCallsign">The labeled callsign, or null when the case expects none.</param>
    public static (int Matched, int Expected) TallyCallsigns(IReadOnlyList<string?> trialCallsigns, string? expectedCallsign) =>
        expectedCallsign is null ? (0, 0) : (trialCallsigns.Count(c => CallsignMatches(expectedCallsign, c)), trialCallsigns.Count);

    private static async Task<CaseScore> ScoreCaseAsync(
        EvalPipeline pipeline,
        string caseName,
        EvalExpectation expectation,
        float[] samples,
        int trials
    )
    {
        var ctx = expectation.ToSpeechContext(pipeline.BiasingPrompt);
        TransmissionContext? room = pipeline.Accurate ? pipeline.RoomContext(expectation, caseName) : null;
        var timings = new TrialTimings();
        var got = new List<string>();
        var trialRecords = new List<EvalTrial>();
        var trialCallsigns = new List<string?>();
        int matches = 0;
        string lastTranscript = string.Empty;
        string lastCallsign = "<none>";
        double? wer = null;
        long sttMsTotal = 0;
        var sw = Stopwatch.StartNew();
        for (int trial = 0; trial < trials; trial++)
        {
            var sttSw = Stopwatch.StartNew();
            SttHypotheses hyps = await pipeline.TranscribeAsync(samples, ctx.WhisperInitialPrompt, CancellationToken.None).ConfigureAwait(false);
            sttSw.Stop();
            double latencyMs = hyps.LatencyMs ?? sttSw.Elapsed.TotalMilliseconds;
            timings.Latencies.Add((latencyMs, hyps.Cached));
            sttMsTotal += (long)latencyMs;
            string? transcript = hyps.Text;
            lastTranscript = transcript ?? string.Empty;
            if (string.IsNullOrWhiteSpace(transcript))
            {
                got.Add("<empty transcript>");
                trialRecords.Add(new EvalTrial(lastTranscript, null, null, RawTextFallback: false));
                trialCallsigns.Add(null);
                pipeline.SampleGpu();
                continue;
            }

            TrialMapping trialMapping = await MapTrialAsync(pipeline, hyps, transcript, ctx, room).ConfigureAwait(false);
            pipeline.SampleGpu();
            if (trialMapping.DecodeState is DecodeState state)
            {
                timings.DecodeStates.Add(state);
                timings.DecodeMapMs.Add(trialMapping.DecodeMapMs ?? 0);
            }
            TranscriptMapResult mapped = trialMapping.Result;
            got.Add(mapped.Canonical ?? "<null>");
            trialRecords.Add(new EvalTrial(transcript, trialMapping.RuleCanonical, mapped.Canonical, mapped.IsRawTextFallback));
            trialCallsigns.Add(mapped.Callsign);
            lastCallsign = mapped.Callsign ?? "<none>";

            if (CanonicalsMatch(expectation.Canonical, mapped.Canonical) && CallsignMatches(expectation.Callsign, mapped.Callsign))
            {
                matches++;
            }

            double? trialWer = TrialWer(expectation.Transcript, transcript);
            if (trialWer is not null)
            {
                wer = wer is null ? trialWer : Math.Min(wer.Value, trialWer.Value);
            }
        }
        sw.Stop();

        EvalVerdict verdict =
            matches == trials ? EvalVerdict.Pass
            : matches == 0 ? EvalVerdict.Fail
            : EvalVerdict.Flaky;
        (int callsignsMatched, int callsignsExpected) = TallyCallsigns(trialCallsigns, expectation.Callsign);
        return new CaseScore(
            got,
            matches,
            verdict,
            wer,
            lastTranscript,
            lastCallsign,
            sttMsTotal,
            sw.ElapsedMilliseconds,
            trialRecords,
            ScoreTrials(expectation.Canonical, trialRecords),
            callsignsMatched,
            callsignsExpected,
            timings
        );
    }

    /// <summary>
    /// Maps one trial: fast mode maps the top transcript; accurate mode (a non-null <paramref name="room"/>) decodes the
    /// whole n-best against the room and times the decode and the mapping together.
    /// </summary>
    private static async Task<TrialMapping> MapTrialAsync(
        EvalPipeline pipeline,
        SttHypotheses hyps,
        string transcript,
        SpeechContext ctx,
        TransmissionContext? room
    )
    {
        if (room is null)
        {
            (TranscriptMapResult mapped, RuleMapperTrace ruleTrace, LlmMapperTrace? _) = await SpeechRecognitionService
                .MapTranscriptWithTraceAsync(
                    transcript,
                    ctx,
                    pipeline.RuleMapper,
                    pipeline.LlmMapper,
                    pipeline.CallsignResolver,
                    CancellationToken.None
                )
                .ConfigureAwait(false);
            return new TrialMapping(mapped, ruleTrace.OutputCanonical, null, null);
        }

        var watch = Stopwatch.StartNew();
        HypothesesMapResult accurate = await SpeechRecognitionService
            .MapHypothesesWithTraceAsync(
                hyps,
                new AccurateDecodeInput(room, DecoderSettings.Default, pipeline.AccurateText),
                ctx,
                pipeline.Mappers,
                CancellationToken.None
            )
            .ConfigureAwait(false);
        watch.Stop();
        return new TrialMapping(accurate.Result, accurate.RuleTrace.OutputCanonical, accurate.Decode.State, watch.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// The trial's STT word-error rate against the labeled transcript, scored in isolation; null when the case has none.
    /// Both sides run through NormalizeDigits so "two seven zero" vs "270" scores as a match — the pipeline is insensitive
    /// to that split, and WER should measure real recognition damage, not orthography.
    /// </summary>
    private static double? TrialWer(string? expectedTranscript, string transcript) =>
        (expectedTranscript is null)
            ? null
            : WerScorer.Score(AtcNumberParser.NormalizeDigits(expectedTranscript), AtcNumberParser.NormalizeDigits(transcript)).Rate;

    /// <summary>Per-source (real vs synthetic) verdict and WER accumulator.</summary>
    private sealed class Tally
    {
        private int _pass;
        private int _fail;
        private int _flaky;
        private readonly List<double> _wers = [];

        public int Total => _pass + _fail + _flaky;

        public void Add(EvalCaseResult result)
        {
            switch (result.Verdict)
            {
                case EvalVerdict.Pass:
                    _pass++;
                    break;
                case EvalVerdict.Fail:
                    _fail++;
                    break;
                default:
                    _flaky++;
                    break;
            }
            if (result.Wer is not null)
            {
                _wers.Add(result.Wer.Value);
            }
        }

        public string Describe()
        {
            string meanWer = _wers.Count > 0 ? _wers.Average().ToString("P1", CultureInfo.InvariantCulture) : "n/a";
            return $"{_pass} PASS, {_flaky} FLAKY, {_fail} FAIL, mean WER {meanWer}";
        }
    }

    /// <summary>
    /// Loads <c>expected.json</c>, or — for cases exported straight from the speech-sample store —
    /// writes a pre-filled stub from <c>session.json</c> so labeling a captured session is a
    /// one-edit review instead of hand-authoring JSON. Stubbed cases carry an "unreviewed": true
    /// flag and are skipped until a human deletes the flag, because the recorded canonical is what
    /// the pipeline PRODUCED at capture time, not verified ground truth — scoring against it would
    /// grade the pipeline against itself.
    /// </summary>
    public static EvalExpectation? LoadOrStubExpectation(string caseDir, string caseName)
    {
        string expectedPath = Path.Combine(caseDir, "expected.json");
        if (File.Exists(expectedPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(expectedPath));
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("unreviewed", out JsonElement unreviewed) && unreviewed.GetBoolean())
            {
                Console.WriteLine($"SKIP  {caseName}: expected.json is an unreviewed stub — verify the labels and remove the \"unreviewed\" flag");
                return null;
            }
            if (!root.TryGetProperty("canonical", out JsonElement canonical) || string.IsNullOrWhiteSpace(canonical.GetString()))
            {
                Console.WriteLine($"SKIP  {caseName}: expected.json has no \"canonical\" field");
                return null;
            }
            return new EvalExpectation(
                canonical.GetString()!,
                ReadString(root, "transcript"),
                ReadString(root, "callsign"),
                ReadStringList(root, "activeCallsigns"),
                ReadStringList(root, "programmedFixes"),
                Synthetic: root.TryGetProperty("synthetic", out JsonElement syn) && syn.ValueKind == JsonValueKind.True,
                Template: ReadString(root, "template"),
                AvailableRunways: root.TryGetProperty("availableRunways", out JsonElement rw) && rw.ValueKind == JsonValueKind.Object
                    ? rw.Deserialize<Dictionary<string, List<string>>>()
                    : null,
                TaxiwayNames: ReadStringList(root, "taxiwayNames"),
                DestinationNames: ReadStringList(root, "destinationNames"),
                Airport: ReadString(root, "airport") is { Length: > 0 } airport ? airport : null,
                ApproachPosition: root.TryGetProperty("approachPosition", out JsonElement approach) && approach.ValueKind == JsonValueKind.True,
                FacilityRadioNames: ReadStringList(root, "facilityRadioNames")
            );
        }

        string sessionPath = Path.Combine(caseDir, "session.json");
        if (!File.Exists(sessionPath))
        {
            Console.WriteLine($"SKIP  {caseName}: no expected.json (and no session.json to stub from)");
            return null;
        }

        SpeechSession? session = JsonSerializer.Deserialize<SpeechSession>(File.ReadAllText(sessionPath), SessionJsonOptions);
        var stub = new Dictionary<string, object?>
        {
            ["unreviewed"] = true,
            ["canonical"] = session?.CanonicalCommand ?? "",
            ["transcript"] = session?.Trace?.RawTranscript ?? session?.Transcript ?? "",
            ["callsign"] = null,
            ["activeCallsigns"] = session?.Trace?.ActiveCallsigns ?? [],
            ["programmedFixes"] = new List<string>(),
            ["airport"] = "",
            ["approachPosition"] = false,
            ["facilityRadioNames"] = new List<string>(),
        };
        File.WriteAllText(expectedPath, JsonSerializer.Serialize(stub, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(
            $"SKIP  {caseName}: wrote unreviewed expected.json stub from session.json — review it, fix the labels, remove \"unreviewed\""
        );
        return null;
    }

    private static string? ReadString(JsonElement root, string property) =>
        root.TryGetProperty(property, out JsonElement el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static List<string>? ReadStringList(JsonElement root, string property) =>
        root.TryGetProperty(property, out JsonElement el) && el.ValueKind == JsonValueKind.Array ? el.Deserialize<List<string>>() : null;

    /// <summary>Case-insensitive canonical comparison with comma/space separators normalized.</summary>
    public static bool CanonicalsMatch(string expected, string? actual)
    {
        if (actual is null)
        {
            return false;
        }
        return string.Equals(NormalizeCanonical(expected), NormalizeCanonical(actual), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeCanonical(string canonical) =>
        string.Join(", ", canonical.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Expected-callsign check. A case with no expected callsign accepts any extraction result.</summary>
    private static bool CallsignMatches(string? expected, string? actual) =>
        expected is null || string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
}
