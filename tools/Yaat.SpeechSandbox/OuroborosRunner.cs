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
/// Run with <c>--ouroboros &lt;corpus.json&gt; [--out-dir &lt;dir&gt;]</c>. Output lands in
/// <c>.tmp/ouroboros-{timestamp}/</c> (or the override dir) with a markdown report, one WAV per
/// case, and a per-case text dump showing every stage's output for failure triage.
///
/// Single Piper speaker, no radio FX, no resampling artifacts beyond a linear interp (22050 →
/// 16000). v1 mirrors production verdict ordering: rule winner if non-null, else LLM.
/// </summary>
internal static class OuroborosRunner
{
    private const int DefaultSpeakerId = 50;
    private const float DefaultSpeed = 1.0f;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: Yaat.SpeechSandbox --ouroboros <corpus.json> [--out-dir <dir>]");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Round-trip pipeline: canonical → readback → Piper TTS → Whisper STT → rule → LLM → compare.");
            Console.Error.WriteLine("Reports each case as PASS / FAIL and preserves WAVs + per-stage logs for triage.");
            return 1;
        }

        var corpusPath = args[0];
        string? outDirOverride = null;
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == "--out-dir")
            {
                outDirOverride = args[i + 1];
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
        Console.WriteLine($"Corpus: {corpusPath} ({corpus.Cases.Count} cases)");
        Console.WriteLine($"Whisper model: {prefs.WhisperModelSize}");
        Console.WriteLine($"LLM model:     {prefs.LlmModelPath}");

        var voiceDir = PiperSynthesizer.ResolveDefaultVoiceDir();
        if (voiceDir is null)
        {
            Console.Error.WriteLine(
                "FATAL: Piper voice pack not found. Expected at .tmp/voices/vits-piper-en_US-libritts_r-medium/ relative to repo root."
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

        var rows = new List<CaseRow>();
        var commandScheme = CommandScheme.Default();

        foreach (var c in corpus.Cases)
        {
            Console.WriteLine($"=== {c.Name} ===");
            var row = await RunCaseAsync(c, piper, stt, ruleMapper, llmMapper, commandScheme, casesDir).ConfigureAwait(false);
            rows.Add(row);
            Console.WriteLine($"  Verdict: {(row.Passed ? "PASS" : "FAIL")} ({row.WinnerStage})");
            Console.WriteLine();
        }

        await WriteReportAsync(outDir, corpusPath, rows).ConfigureAwait(false);

        var passCount = rows.Count(r => r.Passed);
        Console.WriteLine($"Summary: {passCount}/{rows.Count} passed.");
        Console.WriteLine($"Report:  {Path.Combine(outDir, "report.md")}");
        return passCount == rows.Count ? 0 : 1;
    }

    private static async Task<CaseRow> RunCaseAsync(
        OuroborosCase c,
        PiperSynthesizer piper,
        WhisperSttEngine stt,
        PhraseologyCommandMapper ruleMapper,
        LocalLlmCommandMapper llmMapper,
        CommandScheme commandScheme,
        string casesDir
    )
    {
        var row = new CaseRow
        {
            Name = c.Name,
            Callsign = c.Callsign,
            InputCanonical = c.Canonical,
        };

        // --- Stage 0: parse canonical into a CompoundCommand tree ---
        var parsed = CommandParser.ParseCompound(c.Canonical);
        if (!parsed.IsSuccess)
        {
            row.Notes = $"input canonical did not parse: {parsed.Reason}";
            await WriteCaseTextAsync(casesDir, c, row, null).ConfigureAwait(false);
            return row;
        }
        var compound = parsed.Value!;

        // --- Stage 1: build readback from the compound + minimal aircraft state ---
        var aircraft = BuildAircraft(c);
        var readback = PilotResponder.BuildReadback(compound, aircraft);
        if (readback is null)
        {
            row.Notes = "PilotResponder.BuildReadback returned null (no verbalization for this command)";
            await WriteCaseTextAsync(casesDir, c, row, null).ConfigureAwait(false);
            return row;
        }
        row.ReadbackTerminal = readback;
        var ttsText = PilotResponder.PrepareForTts(aircraft, readback);
        row.ReadbackTts = ttsText;
        Console.WriteLine($"  TTS:     \"{ttsText}\"");

        // --- Stage 2: Piper synth at native rate, resample to 16 kHz for Whisper, save as 16 kHz
        // PCM16 WAV so `--pipeline` can replay the exact bytes Whisper consumed (and any media
        // player can still listen back). ---
        var synthSw = Stopwatch.StartNew();
        var synth = piper.Synthesize(ttsText, DefaultSpeakerId, DefaultSpeed);
        synthSw.Stop();
        row.SynthMs = (int)synthSw.ElapsedMilliseconds;

        var sttSamples = PiperSynthesizer.Resample(synth.Samples, synth.SampleRate, AudioCaptureService.SampleRate);
        var wavPath = Path.Combine(casesDir, $"{c.Name}.wav");
        await using (var fs = File.Create(wavPath))
        {
            var wavStream = WavHeader.WritePcm16(sttSamples, AudioCaptureService.SampleRate);
            await wavStream.CopyToAsync(fs).ConfigureAwait(false);
        }
        row.WavPath = wavPath;

        // --- Stage 3: Whisper STT ---
        var sttSw = Stopwatch.StartNew();
        string? transcript;
        try
        {
            transcript = await stt.TranscribeAsync(sttSamples, WhisperBiasingPrompt.Default, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            row.Notes = $"STT threw: {ex.GetType().Name}: {ex.Message}";
            await WriteCaseTextAsync(casesDir, c, row, null).ConfigureAwait(false);
            return row;
        }
        sttSw.Stop();
        row.SttMs = (int)sttSw.ElapsedMilliseconds;
        row.RawTranscript = transcript ?? "<null>";
        Console.WriteLine($"  STT ({row.SttMs, 5} ms): {(transcript is null ? "<null>" : "\"" + transcript + "\"")}");

        if (string.IsNullOrWhiteSpace(transcript))
        {
            row.Notes = "Whisper returned empty / null transcript";
            await WriteCaseTextAsync(casesDir, c, row, null).ConfigureAwait(false);
            return row;
        }

        // --- Stage 4: digit normalization ---
        var normalized = AtcNumberParser.NormalizeDigits(transcript);
        row.Normalized = normalized;

        // --- Stage 5: rule mapper ---
        var ruleSw = Stopwatch.StartNew();
        var ruleResult = await ruleMapper.MapAsync(normalized, MapContext.Empty, CancellationToken.None).ConfigureAwait(false);
        ruleSw.Stop();
        row.RuleMs = (int)ruleSw.ElapsedMilliseconds;
        row.RuleCanonical = ruleResult?.CanonicalCommand;
        Console.WriteLine($"  Rule ({row.RuleMs, 5} ms): {row.RuleCanonical ?? "<null>"}");

        // --- Stage 6: LLM fallback only if rule mapper failed (mirrors production) ---
        if (ruleResult is null)
        {
            var llmSw = Stopwatch.StartNew();
            var llmResult = await llmMapper.MapAsync(normalized, MapContext.Empty, CancellationToken.None).ConfigureAwait(false);
            llmSw.Stop();
            row.LlmMs = (int)llmSw.ElapsedMilliseconds;
            row.LlmCanonical = llmResult?.CanonicalCommand;
            Console.WriteLine($"  LLM  ({row.LlmMs, 5} ms): {row.LlmCanonical ?? "<null>"}");
        }

        // --- Stage 7: verdict ---
        var recovered = row.RuleCanonical ?? row.LlmCanonical;
        row.RecoveredCanonical = recovered;
        row.WinnerStage =
            row.RuleCanonical is not null ? "RULE"
            : row.LlmCanonical is not null ? "LLM"
            : "FAIL";
        row.Passed = recovered is not null && CanonicalEquals(c.Canonical, recovered);

        await WriteCaseTextAsync(casesDir, c, row, ttsText).ConfigureAwait(false);
        return row;
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

    private static async Task WriteCaseTextAsync(string casesDir, OuroborosCase c, CaseRow row, string? ttsText)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {c.Name}");
        sb.AppendLine();
        sb.AppendLine($"Callsign: {c.Callsign}");
        sb.AppendLine($"Input canonical:     {c.Canonical}");
        sb.AppendLine($"Recovered canonical: {row.RecoveredCanonical ?? "<none>"}");
        sb.AppendLine($"Winner stage:        {row.WinnerStage}");
        sb.AppendLine($"Verdict:             {(row.Passed ? "PASS" : "FAIL")}");
        if (!string.IsNullOrEmpty(row.Notes))
        {
            sb.AppendLine($"Notes:               {row.Notes}");
        }
        sb.AppendLine();
        sb.AppendLine("--- Readback (terminal form) ---");
        sb.AppendLine(row.ReadbackTerminal ?? "<none>");
        sb.AppendLine();
        sb.AppendLine("--- Readback (TTS form) ---");
        sb.AppendLine(ttsText ?? row.ReadbackTts ?? "<none>");
        sb.AppendLine();
        sb.AppendLine("--- Whisper transcript ---");
        sb.AppendLine(row.RawTranscript ?? "<none>");
        sb.AppendLine();
        sb.AppendLine("--- Normalized ---");
        sb.AppendLine(row.Normalized ?? "<none>");
        sb.AppendLine();
        sb.AppendLine("--- Rule mapper ---");
        sb.AppendLine(row.RuleCanonical ?? "<null>");
        sb.AppendLine();
        sb.AppendLine("--- LLM mapper ---");
        sb.AppendLine(row.LlmCanonical ?? "<not run>");
        sb.AppendLine();
        sb.AppendLine($"Timings: synth={row.SynthMs} ms, stt={row.SttMs} ms, rule={row.RuleMs} ms, llm={row.LlmMs} ms");
        if (!string.IsNullOrEmpty(row.WavPath))
        {
            sb.AppendLine($"WAV: {row.WavPath}");
        }
        var path = Path.Combine(casesDir, $"{c.Name}.txt");
        await File.WriteAllTextAsync(path, sb.ToString()).ConfigureAwait(false);
    }

    private static async Task WriteReportAsync(string outDir, string corpusPath, IReadOnlyList<CaseRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Ouroboros round-trip report");
        sb.AppendLine();
        sb.AppendLine($"Corpus: `{corpusPath}`");
        sb.AppendLine();
        sb.AppendLine("| Case | Input | Recovered | Stage | Verdict | synth ms | stt ms | rule ms | llm ms |");
        sb.AppendLine("| --- | --- | --- | --- | --- | --: | --: | --: | --: |");
        foreach (var r in rows)
        {
            var input = MdEscape(r.InputCanonical);
            var rec = MdEscape(r.RecoveredCanonical ?? "<none>");
            var verdict = r.Passed ? "PASS" : "**FAIL**";
            sb.Append("| ")
                .Append(r.Name)
                .Append(" | `")
                .Append(input)
                .Append("` ")
                .Append("| `")
                .Append(rec)
                .Append("` ")
                .Append("| ")
                .Append(r.WinnerStage)
                .Append(" | ")
                .Append(verdict)
                .Append(" | ")
                .Append(r.SynthMs.ToString(CultureInfo.InvariantCulture))
                .Append(" | ")
                .Append(r.SttMs.ToString(CultureInfo.InvariantCulture))
                .Append(" | ")
                .Append(r.RuleMs.ToString(CultureInfo.InvariantCulture))
                .Append(" | ")
                .Append(r.LlmMs == 0 ? "—" : r.LlmMs.ToString(CultureInfo.InvariantCulture))
                .AppendLine(" |");
        }
        sb.AppendLine();
        var passCount = rows.Count(r => r.Passed);
        sb.AppendLine($"**{passCount}/{rows.Count} passed.**");
        sb.AppendLine();
        if (passCount < rows.Count)
        {
            sb.AppendLine("Failed cases (per-case dumps at `cases/{name}.txt`):");
            foreach (var r in rows.Where(r => !r.Passed))
            {
                sb.AppendLine($"- `{r.Name}` — {r.Notes ?? "canonical mismatch"}");
            }
        }
        await File.WriteAllTextAsync(Path.Combine(outDir, "report.md"), sb.ToString()).ConfigureAwait(false);
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

    private sealed class CaseRow
    {
        public string Name { get; set; } = "";
        public string Callsign { get; set; } = "";
        public string InputCanonical { get; set; } = "";
        public string? ReadbackTerminal { get; set; }
        public string? ReadbackTts { get; set; }
        public string? RawTranscript { get; set; }
        public string? Normalized { get; set; }
        public string? RuleCanonical { get; set; }
        public string? LlmCanonical { get; set; }
        public string? RecoveredCanonical { get; set; }
        public string WinnerStage { get; set; } = "FAIL";
        public bool Passed { get; set; }
        public string? Notes { get; set; }
        public string? WavPath { get; set; }
        public int SynthMs { get; set; }
        public int SttMs { get; set; }
        public int RuleMs { get; set; }
        public int LlmMs { get; set; }
    }
}
