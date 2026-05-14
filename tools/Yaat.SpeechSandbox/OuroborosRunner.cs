using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Yaat.Client.Services;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Pilot;
using Yaat.Sim.Speech;

namespace Yaat.SpeechSandbox;

/// <summary>
/// Round-trip "ouroboros" harness: synthetic canonical command → pilot readback text → Piper
/// TTS audio → Whisper STT → rule mapper → LLM fallback → compare recovered canonical to input.
/// Designed to surface STT pipeline gaps deterministically against a curated corpus.
///
/// Run with <c>--ouroboros &lt;corpus.json&gt; [--out-dir &lt;dir&gt;] [--trials N]</c>. Output
/// lands in <c>.tmp/ouroboros-{timestamp}/</c> (or the override dir) with a markdown report, one
/// WAV per case, and a per-case text dump showing every trial's transcript + stage output for
/// failure triage.
///
/// Piper synth runs ONCE per case (deterministic given fixed speaker + speed); STT + mapping run
/// N times per case so we can see through whisper-large-turbo3's CUDA non-determinism. Cases are
/// verdicted PASS (all N trials match), FAIL (zero match), or FLAKY (some match) — the latter is
/// the signal that the underlying issue is intermittent, not an outright pipeline gap.
/// </summary>
internal static class OuroborosRunner
{
    private const int DefaultSpeakerId = 50;
    private const float DefaultSpeed = 1.0f;

    // Whisper drops the leading phoneme when fed Piper output without pre-speech silence — real
    // mic captures have natural room tone before speech, synthetic audio doesn't. 400 ms each
    // side mirrors whisper.cpp's published short-clip guidance and is the best stable point in
    // ouroboros experiments: a 200 ms trailing variant regressed descend-and-maintain-5000 to
    // multilingual hallucination without recovering anything else.
    private const int LeadingSilenceMs = 400;
    private const int TrailingSilenceMs = 400;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: Yaat.SpeechSandbox --ouroboros <corpus.json> [--out-dir <dir>] [--trials N]");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Round-trip pipeline: canonical → readback → Piper TTS → Whisper STT → rule → LLM → compare.");
            Console.Error.WriteLine("With --trials N, each case is transcribed+mapped N times to defend against whisper non-determinism.");
            Console.Error.WriteLine("Verdicts: PASS (all trials match), FAIL (none match), FLAKY (some match).");
            return 1;
        }

        var corpusPath = args[0];
        string? outDirOverride = null;
        int trials = 1;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--out-dir" && i + 1 < args.Length)
            {
                outDirOverride = args[i + 1];
                i++;
            }
            else if (args[i] == "--trials" && i + 1 < args.Length)
            {
                if (!int.TryParse(args[i + 1], CultureInfo.InvariantCulture, out trials) || trials < 1)
                {
                    Console.Error.WriteLine($"FATAL: --trials must be a positive integer, got '{args[i + 1]}'");
                    return 2;
                }
                i++;
            }
        }

        if (!File.Exists(corpusPath))
        {
            Console.Error.WriteLine($"FATAL: corpus file not found: {corpusPath}");
            return 2;
        }

        OuroborosCorpus corpus;
        try
        {
            var json = await File.ReadAllTextAsync(corpusPath).ConfigureAwait(false);
            corpus =
                JsonSerializer.Deserialize(json, OuroborosCorpusJsonContext.Default.OuroborosCorpus)
                ?? throw new InvalidDataException("Corpus deserialized as null");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: failed to parse corpus: {ex.GetType().Name}: {ex.Message}");
            return 2;
        }

        if (corpus.Cases.Count == 0)
        {
            Console.Error.WriteLine("FATAL: corpus has zero cases");
            return 2;
        }

        var prefs = new UserPreferences();
        Console.WriteLine($"Corpus:        {corpusPath} ({corpus.Cases.Count} cases × {trials} trial{(trials == 1 ? "" : "s")})");
        Console.WriteLine($"Whisper model: {prefs.WhisperModelSize}");
        Console.WriteLine($"LLM model:     {prefs.LlmModelPath}");

        var voiceDir = PiperSynthesizer.ResolveDefaultVoiceDir();
        if (voiceDir is null)
        {
            Console.Error.WriteLine(
                "FATAL: Piper voice pack not found. Expected at .tmp/voices/vits-piper-en_US-libritts_r-medium/ relative to repo root, or %LOCALAPPDATA%/yaat/voices/vits-piper-en_US-libritts_r-medium/."
            );
            return 2;
        }
        Console.WriteLine($"Piper voice:   {voiceDir}");
        Console.WriteLine();

        using var piper = new PiperSynthesizer(voiceDir);
        using var stt = new WhisperSttEngine(prefs);
        using var llm = new LocalLlmService(new PreferencesLlmRuntimeConfig(prefs));
        var ruleMapper = new PhraseologyCommandMapper();
        var llmMapper = new LocalLlmCommandMapper(llm);

        if (!stt.IsConfigured)
        {
            Console.Error.WriteLine(
                $"FATAL: Whisper STT is not configured (model='{prefs.WhisperModelSize}'). Configure it in Yaat.Client → Settings → Speech first."
            );
            return 2;
        }
        if (!llm.IsConfigured)
        {
            Console.Error.WriteLine(
                $"FATAL: LLM is not configured (model='{prefs.LlmModelPath}'). Configure it in Yaat.Client → Settings → Speech first."
            );
            return 2;
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var outDir = outDirOverride ?? Path.Combine(FindRepoRoot(), ".tmp", $"ouroboros-{stamp}");
        Directory.CreateDirectory(outDir);
        var casesDir = Path.Combine(outDir, "cases");
        Directory.CreateDirectory(casesDir);
        Console.WriteLine($"Output dir:    {outDir}");
        Console.WriteLine();

        var results = new List<CaseResult>();

        foreach (var c in corpus.Cases)
        {
            Console.WriteLine($"=== {c.Name} ===");
            var result = await RunCaseAsync(c, piper, stt, ruleMapper, llmMapper, casesDir, trials).ConfigureAwait(false);
            results.Add(result);
            Console.WriteLine($"  Verdict: {VerdictLabel(result)}");
            Console.WriteLine();
        }

        await WriteReportAsync(outDir, corpusPath, results, trials).ConfigureAwait(false);

        var passCount = results.Count(r => r.Verdict == CaseVerdict.Pass);
        var flakyCount = results.Count(r => r.Verdict == CaseVerdict.Flaky);
        var failCount = results.Count(r => r.Verdict == CaseVerdict.Fail);
        Console.WriteLine($"Summary: {passCount} pass, {flakyCount} flaky, {failCount} fail (of {results.Count}).");
        Console.WriteLine($"Report:  {Path.Combine(outDir, "report.md")}");
        return failCount == 0 && flakyCount == 0 ? 0 : 1;
    }

    private static async Task<CaseResult> RunCaseAsync(
        OuroborosCase c,
        PiperSynthesizer piper,
        WhisperSttEngine stt,
        PhraseologyCommandMapper ruleMapper,
        LocalLlmCommandMapper llmMapper,
        string casesDir,
        int trials
    )
    {
        var result = new CaseResult { Case = c };

        // --- Stage 0: parse canonical into a CompoundCommand tree ---
        var parsed = CommandParser.ParseCompound(c.Canonical);
        if (!parsed.IsSuccess)
        {
            result.SetupFailure = $"input canonical did not parse: {parsed.Reason}";
            await WriteCaseTextAsync(casesDir, result).ConfigureAwait(false);
            return result;
        }
        var compound = parsed.Value!;

        // --- Stage 1: build readback from the compound + minimal aircraft state ---
        var aircraft = BuildAircraft(c);
        var readback = PilotResponder.BuildReadback(compound, aircraft);
        if (readback is null)
        {
            result.SetupFailure = "PilotResponder.BuildReadback returned null (no verbalization for this command)";
            await WriteCaseTextAsync(casesDir, result).ConfigureAwait(false);
            return result;
        }
        result.ReadbackTerminal = readback;
        var ttsText = PilotResponder.PrepareForTts(aircraft, readback);
        result.ReadbackTts = ttsText;
        Console.WriteLine($"  TTS:     \"{ttsText}\"");

        // --- Stage 2: Piper synth ONCE (deterministic), resample to 16 kHz, pad, save as 16 kHz
        // PCM16 so `--pipeline` can replay the exact bytes Whisper consumed. ---
        var synthSw = Stopwatch.StartNew();
        var synth = piper.Synthesize(ttsText, DefaultSpeakerId, DefaultSpeed);
        synthSw.Stop();
        result.SynthMs = (int)synthSw.ElapsedMilliseconds;

        var resampled = PiperSynthesizer.Resample(synth.Samples, synth.SampleRate, AudioCaptureService.SampleRate);
        var sttSamples = PiperSynthesizer.PadWithSilence(resampled, AudioCaptureService.SampleRate, LeadingSilenceMs, TrailingSilenceMs);
        var wavPath = Path.Combine(casesDir, $"{c.Name}.wav");
        await using (var fs = File.Create(wavPath))
        {
            var wavStream = WavHeader.WritePcm16(sttSamples, AudioCaptureService.SampleRate);
            await wavStream.CopyToAsync(fs).ConfigureAwait(false);
        }
        result.WavPath = wavPath;

        // --- Stage 3+: run N trials of STT + mapping on the same bytes ---
        for (int trialIdx = 0; trialIdx < trials; trialIdx++)
        {
            var trial = await RunTrialAsync(c, sttSamples, stt, ruleMapper, llmMapper, trialIdx).ConfigureAwait(false);
            result.Trials.Add(trial);
            if (trials > 1)
            {
                Console.WriteLine(
                    $"  Trial {trialIdx + 1}/{trials}: {(trial.Passed ? "PASS" : "FAIL")} via {trial.WinnerStage} → \"{trial.RuleCanonical ?? trial.LlmCanonical ?? "<none>"}\""
                );
            }
        }

        await WriteCaseTextAsync(casesDir, result).ConfigureAwait(false);
        return result;
    }

    private static async Task<TrialResult> RunTrialAsync(
        OuroborosCase c,
        float[] sttSamples,
        WhisperSttEngine stt,
        PhraseologyCommandMapper ruleMapper,
        LocalLlmCommandMapper llmMapper,
        int trialIndex
    )
    {
        var trial = new TrialResult { TrialIndex = trialIndex };

        var sttSw = Stopwatch.StartNew();
        string? transcript;
        try
        {
            transcript = await stt.TranscribeAsync(sttSamples, WhisperBiasingPrompt.Default, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            trial.Notes = $"STT threw: {ex.GetType().Name}: {ex.Message}";
            return trial;
        }
        sttSw.Stop();
        trial.SttMs = (int)sttSw.ElapsedMilliseconds;
        trial.RawTranscript = transcript ?? "<null>";

        if (string.IsNullOrWhiteSpace(transcript))
        {
            trial.Notes = "Whisper returned empty / null transcript";
            return trial;
        }

        var normalized = AtcNumberParser.NormalizeDigits(transcript);
        trial.Normalized = normalized;

        var ruleSw = Stopwatch.StartNew();
        var ruleResult = await ruleMapper.MapAsync(normalized, MapContext.Empty, CancellationToken.None).ConfigureAwait(false);
        ruleSw.Stop();
        trial.RuleMs = (int)ruleSw.ElapsedMilliseconds;
        trial.RuleCanonical = ruleResult?.CanonicalCommand;

        // LLM fallback only when rule mapper failed — mirrors production.
        if (ruleResult is null)
        {
            var llmSw = Stopwatch.StartNew();
            var llmResult = await llmMapper.MapAsync(normalized, MapContext.Empty, CancellationToken.None).ConfigureAwait(false);
            llmSw.Stop();
            trial.LlmMs = (int)llmSw.ElapsedMilliseconds;
            trial.LlmCanonical = llmResult?.CanonicalCommand;
        }

        var recovered = trial.RuleCanonical ?? trial.LlmCanonical;
        trial.WinnerStage =
            trial.RuleCanonical is not null ? "RULE"
            : trial.LlmCanonical is not null ? "LLM"
            : "FAIL";
        trial.Passed = recovered is not null && CanonicalEquals(c.Canonical, recovered);
        return trial;
    }

    private static AircraftState BuildAircraft(OuroborosCase c)
    {
        var ac = new AircraftState { Callsign = c.Callsign, AircraftType = c.AircraftType ?? "B738" };
        if (!string.IsNullOrWhiteSpace(c.Context?.AirportId))
        {
            ac.AirportId = c.Context.AirportId;
        }
        return ac;
    }

    private static bool CanonicalEquals(string a, string b) => Normalize(a) == Normalize(b);

    private static string Normalize(string s) => Regex.Replace(s, @"\s+", " ").Trim().ToUpperInvariant();

    private static string VerdictLabel(CaseResult r) =>
        r.Verdict switch
        {
            CaseVerdict.Pass => $"PASS ({r.PassCount}/{r.TrialCount})",
            CaseVerdict.Flaky => $"FLAKY ({r.PassCount}/{r.TrialCount})",
            CaseVerdict.Fail => r.SetupFailure is not null ? $"FAIL (setup: {r.SetupFailure})" : $"FAIL (0/{r.TrialCount})",
            _ => "?",
        };

    private static async Task WriteCaseTextAsync(string casesDir, CaseResult r)
    {
        var c = r.Case;
        var sb = new StringBuilder();
        sb.AppendLine($"# {c.Name}");
        sb.AppendLine();
        sb.AppendLine($"Callsign:        {c.Callsign}");
        sb.AppendLine($"Input canonical: {c.Canonical}");
        sb.AppendLine($"Verdict:         {VerdictLabel(r)}");
        if (r.SetupFailure is not null)
        {
            sb.AppendLine($"Setup failure:   {r.SetupFailure}");
        }
        sb.AppendLine();
        sb.AppendLine("--- Readback (terminal form) ---");
        sb.AppendLine(r.ReadbackTerminal ?? "<none>");
        sb.AppendLine();
        sb.AppendLine("--- Readback (TTS form) ---");
        sb.AppendLine(r.ReadbackTts ?? "<none>");
        sb.AppendLine();
        sb.AppendLine($"Synth: {r.SynthMs} ms");
        if (r.WavPath is not null)
        {
            sb.AppendLine($"WAV:   {r.WavPath}");
        }
        sb.AppendLine();
        for (int i = 0; i < r.Trials.Count; i++)
        {
            var t = r.Trials[i];
            sb.AppendLine($"--- Trial {i + 1}/{r.Trials.Count} ({(t.Passed ? "PASS" : "FAIL")} via {t.WinnerStage}) ---");
            sb.AppendLine($"Whisper transcript: {t.RawTranscript ?? "<none>"}");
            sb.AppendLine($"Normalized:         {t.Normalized ?? "<none>"}");
            sb.AppendLine($"Rule canonical:     {t.RuleCanonical ?? "<null>"}");
            sb.AppendLine($"LLM canonical:      {t.LlmCanonical ?? "<not run>"}");
            if (t.Notes is not null)
            {
                sb.AppendLine($"Notes:              {t.Notes}");
            }
            sb.AppendLine($"Timings: stt={t.SttMs} ms, rule={t.RuleMs} ms, llm={t.LlmMs} ms");
            sb.AppendLine();
        }
        var path = Path.Combine(casesDir, $"{c.Name}.txt");
        await File.WriteAllTextAsync(path, sb.ToString()).ConfigureAwait(false);
    }

    private static async Task WriteReportAsync(string outDir, string corpusPath, IReadOnlyList<CaseResult> results, int trials)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Ouroboros round-trip report");
        sb.AppendLine();
        sb.AppendLine($"Corpus: `{corpusPath}` — {results.Count} cases × {trials} trial{(trials == 1 ? "" : "s")} each");
        sb.AppendLine();
        sb.AppendLine("| Case | Input | Verdict | Pass/N | Most-frequent recovery | synth ms |");
        sb.AppendLine("| --- | --- | --- | --: | --- | --: |");
        foreach (var r in results)
        {
            var verdict = r.Verdict switch
            {
                CaseVerdict.Pass => "PASS",
                CaseVerdict.Flaky => "**FLAKY**",
                CaseVerdict.Fail => "**FAIL**",
                _ => "?",
            };
            var ratio = $"{r.PassCount}/{r.TrialCount}";
            var mostFrequent = MostFrequentRecovery(r);
            sb.Append("| ")
                .Append(r.Case.Name)
                .Append(" | `")
                .Append(MdEscape(r.Case.Canonical))
                .Append("` | ")
                .Append(verdict)
                .Append(" | ")
                .Append(ratio)
                .Append(" | ")
                .Append(mostFrequent)
                .Append(" | ")
                .Append(r.SynthMs.ToString(CultureInfo.InvariantCulture))
                .AppendLine(" |");
        }
        sb.AppendLine();
        var passCount = results.Count(r => r.Verdict == CaseVerdict.Pass);
        var flakyCount = results.Count(r => r.Verdict == CaseVerdict.Flaky);
        var failCount = results.Count(r => r.Verdict == CaseVerdict.Fail);
        sb.AppendLine($"**{passCount} pass, {flakyCount} flaky, {failCount} fail** (of {results.Count}).");
        sb.AppendLine();
        var notGreen = results.Where(r => r.Verdict != CaseVerdict.Pass).ToList();
        if (notGreen.Count > 0)
        {
            sb.AppendLine("Cases needing attention (per-case dumps at `cases/{name}.txt`):");
            foreach (var r in notGreen)
            {
                sb.AppendLine($"- `{r.Case.Name}` ({r.PassCount}/{r.TrialCount}) — {r.SetupFailure ?? "see trials"}");
            }
        }
        await File.WriteAllTextAsync(Path.Combine(outDir, "report.md"), sb.ToString()).ConfigureAwait(false);
    }

    private static string MostFrequentRecovery(CaseResult r)
    {
        if (r.Trials.Count == 0)
        {
            return "<none>";
        }
        var grouped = r
            .Trials.Select(t => t.RuleCanonical ?? t.LlmCanonical ?? "<none>")
            .GroupBy(s => s, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ToList();
        var top = grouped[0];
        var label = $"`{MdEscape(top.Key)}` ({top.Count()}/{r.TrialCount})";
        if (grouped.Count > 1)
        {
            label += " + " + (grouped.Count - 1) + " other";
        }
        return label;
    }

    private static string MdEscape(string s) => s.Replace("|", "\\|", StringComparison.Ordinal);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return AppContext.BaseDirectory;
    }

    private enum CaseVerdict
    {
        Pass,
        Flaky,
        Fail,
    }

    private sealed class CaseResult
    {
        public required OuroborosCase Case { get; init; }
        public string? ReadbackTerminal { get; set; }
        public string? ReadbackTts { get; set; }
        public string? WavPath { get; set; }
        public int SynthMs { get; set; }
        public string? SetupFailure { get; set; }
        public List<TrialResult> Trials { get; } = [];

        public int PassCount => Trials.Count(t => t.Passed);
        public int TrialCount => Trials.Count;

        public CaseVerdict Verdict =>
            SetupFailure is not null ? CaseVerdict.Fail
            : TrialCount == 0 ? CaseVerdict.Fail
            : PassCount == TrialCount ? CaseVerdict.Pass
            : PassCount == 0 ? CaseVerdict.Fail
            : CaseVerdict.Flaky;
    }

    private sealed class TrialResult
    {
        public int TrialIndex { get; init; }
        public string? RawTranscript { get; set; }
        public string? Normalized { get; set; }
        public string? RuleCanonical { get; set; }
        public string? LlmCanonical { get; set; }
        public string WinnerStage { get; set; } = "FAIL";
        public bool Passed { get; set; }
        public string? Notes { get; set; }
        public int SttMs { get; set; }
        public int RuleMs { get; set; }
        public int LlmMs { get; set; }
    }
}
