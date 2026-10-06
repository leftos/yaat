using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Yaat.Client.Services;
using Yaat.Sim.Speech;

namespace Yaat.SpeechSandbox;

/// <summary>
/// Ground-truth of one eval case, read from <c>expected.json</c> in the case directory.
/// <c>Canonical</c> is the only required field; the rest refine scoring and context.
/// <c>Template</c> is the synthetic generator's template key (null for real recordings); the
/// runway / taxiway / destination sets are the scenario context the case was labeled under.
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
    List<string>? DestinationNames
)
{
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
/// <param name="FinalCanonical">The canonical the full pipeline produced (rule mapper, then the LLM fallback), or null when nothing mapped.</param>
/// <param name="RawTextFallback">True when both mappers failed and <paramref name="FinalCanonical"/> is the raw command text surfaced beside the callsign.</param>
public sealed record EvalTrial(string Transcript, string? MapperCanonical, string? FinalCanonical, bool RawTextFallback);

/// <summary>Everything one scoring pass produced.</summary>
public sealed record EvalRunResult(IReadOnlyList<EvalCaseResult> Cases, int Skipped, string Summary, string ReportPath);

/// <summary>
/// The production STT + mapping stack an eval pass scores through: Whisper (prefs default or an
/// override) or a sherpa-onnx Parakeet export, the rule mapper, and the LLM fallback.
/// <see cref="SetupError"/> is non-null when a model is missing, in which case nothing can be scored.
/// </summary>
internal sealed class EvalPipeline : IDisposable
{
    /// <summary>LLM config for the LMKIT_TEST_MODEL override path. GPU layers stay on auto.</summary>
    private sealed class OverrideLlmRuntimeConfig(string modelPath) : ILlmRuntimeConfig
    {
        public string ModelPath { get; } = modelPath;
        public int GpuLayers => -1;
    }

    /// <summary>Whisper config carrying an explicit model source (prefs default or --whisper override).</summary>
    private sealed class OverrideWhisperRuntimeConfig(string modelSource) : IWhisperRuntimeConfig
    {
        public string ModelSource { get; } = modelSource;
    }

    private readonly WhisperSttEngine? _whisperStt;
    private readonly SherpaSttEngine? _sherpaStt;
    private readonly LocalLlmService _llm;

    public string SttLabel { get; }
    public string LlmModelPath { get; }
    public bool LlmOverridden { get; }
    public string BiasingPrompt { get; }
    public string? SetupError { get; }
    public PhraseologyCommandMapper RuleMapper { get; } = new();
    public LocalLlmCommandMapper LlmMapper { get; }
    public LocalLlmCallsignResolver CallsignResolver { get; }

    private EvalPipeline(string? whisperOverride, string? parakeetDir, bool noBiasingPrompt)
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

        // STT stage: Whisper (prefs default, or --whisper override) or a sherpa-onnx Parakeet
        // export (--parakeet). Both are exposed to the trial loop through one method so the
        // scoring path is identical regardless of engine.
        string whisperSource = whisperOverride ?? prefs.WhisperModelSize;
        _whisperStt = parakeetDir is null ? new WhisperSttEngine(new OverrideWhisperRuntimeConfig(whisperSource)) : null;
        _sherpaStt = parakeetDir is null ? null : new SherpaSttEngine(parakeetDir);
        string sttLabel = parakeetDir is null ? whisperSource : $"parakeet (sherpa-onnx, {parakeetDir})";
        // --prompt none blanks the Whisper biasing prompt for the whole run — the A/B knob for
        // asking whether the static vocabulary hint still earns its keep on a given model.
        BiasingPrompt = noBiasingPrompt ? string.Empty : WhisperBiasingPrompt.Default;
        SttLabel = sttLabel + (noBiasingPrompt ? " (no biasing prompt)" : "");

        _llm = new LocalLlmService(llmConfig);
        LlmMapper = new LocalLlmCommandMapper(_llm);
        CallsignResolver = new LocalLlmCallsignResolver(_llm);
        bool sttConfigured = _whisperStt?.IsConfigured ?? _sherpaStt!.IsConfigured;
        SetupError =
            (!sttConfigured || !_llm.IsConfigured)
                ? "STT or LLM model not configured/found — check model source arguments and Settings → Speech."
                : null;
    }

    /// <summary>
    /// Loads real navdata (the mapping stage validates canonicals through CommandParser, and
    /// PhoneticFixMatcher's full-database fallback needs it) and constructs the model stack.
    /// </summary>
    public static EvalPipeline Create(string? whisperOverride, string? parakeetDir, bool noBiasingPrompt)
    {
        Yaat.Sim.Testing.TestVnasData.EnsureInitialized();
        return new EvalPipeline(whisperOverride, parakeetDir, noBiasingPrompt);
    }

    public Task<string?> TranscribeAsync(float[] samples, string prompt, CancellationToken ct) =>
        _whisperStt is not null
            ? _whisperStt.TranscribeAsync(samples, prompt, ct)
            : Task.Run(() => _sherpaStt!.Transcribe(samples, AudioCaptureService.SampleRate), ct);

    public void Dispose()
    {
        _llm.Dispose();
        _sherpaStt?.Dispose();
        _whisperStt?.Dispose();
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
                "Usage: Yaat.SpeechSandbox --eval <corpus-dir> [--out-dir <dir>] [--trials N] [--whisper <model-source>] [--parakeet <model-dir>]"
            );
            Console.Error.WriteLine();
            Console.Error.WriteLine("Scores the production STT pipeline against labeled real-audio cases.");
            Console.Error.WriteLine("Each corpus subdirectory needs audio.wav + expected.json (see EvalRunner docs).");
            Console.Error.WriteLine("--whisper A/Bs an alternative Whisper model (curated ID, ggml .bin path, or URI).");
            Console.Error.WriteLine("--parakeet swaps the STT stage for a sherpa-onnx NeMo transducer export (Parakeet-TDT).");
            return 1;
        }

        string corpusDir = args[0];
        string? outDirOverride = null;
        string? whisperOverride = null;
        string? parakeetDir = null;
        string promptMode = "default";
        int trials = 1;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--out-dir" && i + 1 < args.Length)
            {
                outDirOverride = args[++i];
            }
            else if (args[i] == "--whisper" && i + 1 < args.Length)
            {
                whisperOverride = args[++i];
            }
            else if (args[i] == "--parakeet" && i + 1 < args.Length)
            {
                parakeetDir = args[++i];
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

        if (whisperOverride is not null && parakeetDir is not null)
        {
            Console.Error.WriteLine("FATAL: --whisper and --parakeet are mutually exclusive — pick one STT stage per run.");
            return 2;
        }

        if (!Directory.Exists(corpusDir))
        {
            Console.Error.WriteLine($"FATAL: corpus directory not found: {corpusDir}");
            return 2;
        }

        using var pipeline = EvalPipeline.Create(whisperOverride, parakeetDir, noBiasingPrompt: promptMode == "none");
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

        var report = new StringBuilder();
        report.AppendLine("# Speech pipeline eval report");
        report.AppendLine();
        report.AppendLine($"- Corpus: `{Path.GetFullPath(corpusDir)}`");
        report.AppendLine($"- STT: `{pipeline.SttLabel}`  LLM: `{pipeline.LlmModelPath}`  Trials per case: {trials}");
        report.AppendLine();
        report.AppendLine("| Case | Verdict | Canonical (expected) | Canonical (got) | WER | Callsign | STT ms/trial |");
        report.AppendLine("|---|---|---|---|---|---|---|");
        var details = new StringBuilder();
        details.AppendLine("### Per-case transcripts");
        details.AppendLine();
        var outcomes = new CaseReporter(report, details, trials);

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

            CaseScore score = await ScoreCaseAsync(pipeline, expectation, WavHeader.ReadPcm16(wavPath), trials).ConfigureAwait(false);
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

        report.AppendLine();
        report.Append(details);
        string summary = Summarize(realTally, synthTally, skipped);
        report.AppendLine($"**Summary:** {summary}");
        string reportPath = Path.Combine(outDir, "report.md");
        await File.WriteAllTextAsync(reportPath, report.ToString()).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine(summary);
        Console.WriteLine($"Report: {reportPath}");
        return new EvalRunResult(results, skipped, summary, reportPath);
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
            Console.WriteLine(
                $"{verdictText, -10} {result.CaseName}: got \"{lastCanonical}\" (callsign {score.LastCallsign}, WER {werText}, STT avg {sttMsAvg} ms/trial)"
            );
            Console.WriteLine($"           transcript \"{score.LastTranscript}\"");
            if (result.Verdict != EvalVerdict.Pass)
            {
                Console.WriteLine($"           expected \"{expectation.Canonical}\"");
            }
            report.AppendLine(
                $"| {result.CaseName} | {verdictText} | `{expectation.Canonical}` | `{lastCanonical}` | {werText} | {score.LastCallsign} | {sttMsAvg} |"
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
        int CallsignsExpected
    );

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

    private static async Task<CaseScore> ScoreCaseAsync(EvalPipeline pipeline, EvalExpectation expectation, float[] samples, int trials)
    {
        var ctx = expectation.ToSpeechContext(pipeline.BiasingPrompt);
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
            string? transcript = await pipeline.TranscribeAsync(samples, ctx.WhisperInitialPrompt, CancellationToken.None).ConfigureAwait(false);
            sttSw.Stop();
            sttMsTotal += sttSw.ElapsedMilliseconds;
            lastTranscript = transcript ?? string.Empty;
            if (string.IsNullOrWhiteSpace(transcript))
            {
                got.Add("<empty transcript>");
                trialRecords.Add(new EvalTrial(lastTranscript, null, null, RawTextFallback: false));
                trialCallsigns.Add(null);
                continue;
            }

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
            got.Add(mapped.Canonical ?? "<null>");
            trialRecords.Add(new EvalTrial(transcript, ruleTrace.OutputCanonical, mapped.Canonical, mapped.IsRawTextFallback));
            trialCallsigns.Add(mapped.Callsign);
            lastCallsign = mapped.Callsign ?? "<none>";

            if (CanonicalsMatch(expectation.Canonical, mapped.Canonical) && CallsignMatches(expectation.Callsign, mapped.Callsign))
            {
                matches++;
            }

            if (expectation.Transcript is not null)
            {
                // Score STT in isolation. Both sides run through NormalizeDigits so "two seven
                // zero" vs "270" scores as a match — the pipeline is insensitive to that split,
                // and WER should measure real recognition damage, not orthography.
                double trialWer = WordErrorRate(AtcNumberParser.NormalizeDigits(expectation.Transcript), AtcNumberParser.NormalizeDigits(transcript));
                wer = wer is null ? trialWer : Math.Min(wer.Value, trialWer);
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
            callsignsExpected
        );
    }

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
                DestinationNames: ReadStringList(root, "destinationNames")
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

    /// <summary>
    /// Word-level edit distance divided by reference length — the standard WER definition. Both
    /// strings should be pre-normalized by the caller so orthography differences don't count as
    /// errors. Returns 0 for two empty strings.
    /// </summary>
    internal static double WordErrorRate(string reference, string hypothesis)
    {
        string[] refWords = reference.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] hypWords = hypothesis.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (refWords.Length == 0)
        {
            return hypWords.Length == 0 ? 0 : 1;
        }

        // Two-row Levenshtein over words.
        int[] prev = new int[hypWords.Length + 1];
        int[] curr = new int[hypWords.Length + 1];
        for (int j = 0; j <= hypWords.Length; j++)
        {
            prev[j] = j;
        }
        for (int i = 1; i <= refWords.Length; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= hypWords.Length; j++)
            {
                int substitution = prev[j - 1] + (string.Equals(refWords[i - 1], hypWords[j - 1], StringComparison.OrdinalIgnoreCase) ? 0 : 1);
                curr[j] = Math.Min(Math.Min(prev[j] + 1, curr[j - 1] + 1), substitution);
            }
            (prev, curr) = (curr, prev);
        }
        return (double)prev[hypWords.Length] / refWords.Length;
    }
}
