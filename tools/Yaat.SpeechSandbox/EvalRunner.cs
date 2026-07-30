using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Yaat.Client.Services;
using Yaat.Sim.Speech;

namespace Yaat.SpeechSandbox;

/// <summary>
/// Ground-truth of one eval case, read from <c>expected.json</c> in the case directory.
/// <c>Canonical</c> is the only required field; the rest refine scoring and context.
/// </summary>
internal sealed record EvalExpectation(
    string Canonical,
    string? Transcript,
    string? Callsign,
    List<string>? ActiveCallsigns,
    List<string>? ProgrammedFixes
);

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
///                            "UAL234", "activeCallsigns": ["UAL234"], "programmedFixes": [] }
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
/// trials produced the expected canonical, FAIL = none did, FLAKY = some did.
/// </summary>
internal static class EvalRunner
{
    /// <summary>LLM config for the LMKIT_TEST_MODEL override path. GPU layers stay on auto.</summary>
    private sealed class OverrideLlmRuntimeConfig : ILlmRuntimeConfig
    {
        public OverrideLlmRuntimeConfig(string modelPath)
        {
            ModelPath = modelPath;
        }

        public string ModelPath { get; }
        public int GpuLayers => -1;
    }

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: Yaat.SpeechSandbox --eval <corpus-dir> [--out-dir <dir>] [--trials N]");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Scores the production STT pipeline against labeled real-audio cases.");
            Console.Error.WriteLine("Each corpus subdirectory needs audio.wav + expected.json (see EvalRunner docs).");
            return 1;
        }

        var corpusDir = args[0];
        string? outDirOverride = null;
        var trials = 1;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--out-dir" && i + 1 < args.Length)
            {
                outDirOverride = args[++i];
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

        if (!Directory.Exists(corpusDir))
        {
            Console.Error.WriteLine($"FATAL: corpus directory not found: {corpusDir}");
            return 2;
        }

        var prefs = new UserPreferences();

        // LMKIT_TEST_MODEL overrides the LLM the same way it does for --llm-probe and the
        // LocalLlmPipelineIntegrationTests fixture, so eval runs are reproducible across machines
        // instead of depending on whatever the developer's saved preferences point at.
        var llmOverride = Environment.GetEnvironmentVariable("LMKIT_TEST_MODEL");
        ILlmRuntimeConfig llmConfig = string.IsNullOrWhiteSpace(llmOverride)
            ? new PreferencesLlmRuntimeConfig(prefs)
            : new OverrideLlmRuntimeConfig(llmOverride);

        Console.WriteLine($"Whisper model: {prefs.WhisperModelSize}");
        Console.WriteLine($"LLM model:     {llmConfig.ModelPath}{(llmOverride is null ? "" : " (LMKIT_TEST_MODEL override)")}");
        Console.WriteLine();

        using var stt = new WhisperSttEngine(prefs);
        using var llm = new LocalLlmService(llmConfig);
        var ruleMapper = new PhraseologyCommandMapper();
        var llmMapper = new LocalLlmCommandMapper(llm);
        var callsignResolver = new LocalLlmCallsignResolver(llm);
        if (!stt.IsConfigured || !llm.IsConfigured)
        {
            Console.Error.WriteLine("FATAL: Whisper or LLM model not configured — set them up in Yaat.Client → Settings → Speech first.");
            return 2;
        }

        var outDir = outDirOverride ?? Path.Combine(".tmp", $"speech-eval-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(outDir);

        var report = new StringBuilder();
        report.AppendLine("# Speech pipeline eval report");
        report.AppendLine();
        report.AppendLine($"- Corpus: `{Path.GetFullPath(corpusDir)}`");
        report.AppendLine($"- Whisper: `{prefs.WhisperModelSize}`  LLM: `{llmConfig.ModelPath}`  Trials per case: {trials}");
        report.AppendLine();
        report.AppendLine("| Case | Verdict | Canonical (expected) | Canonical (got) | WER | Callsign |");
        report.AppendLine("|---|---|---|---|---|---|");

        int pass = 0,
            fail = 0,
            flaky = 0,
            skipped = 0;
        var werValues = new List<double>();

        foreach (var caseDir in Directory.EnumerateDirectories(corpusDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var caseName = Path.GetFileName(caseDir);
            var wavPath = Path.Combine(caseDir, "audio.wav");
            if (!File.Exists(wavPath))
            {
                Console.WriteLine($"SKIP  {caseName}: no audio.wav");
                skipped++;
                continue;
            }

            var expectation = LoadOrStubExpectation(caseDir, caseName);
            if (expectation is null)
            {
                skipped++;
                continue;
            }

            var samples = WavHeader.ReadPcm16(wavPath);
            var ctx = new SpeechContext(expectation.ActiveCallsigns ?? [], expectation.ProgrammedFixes ?? [], WhisperBiasingPrompt.Default);

            var matches = 0;
            string lastTranscript = string.Empty,
                lastCanonical = "<null>",
                lastCallsign = "<none>";
            double? wer = null;
            var sw = Stopwatch.StartNew();
            for (var trial = 0; trial < trials; trial++)
            {
                var transcript = await stt.TranscribeAsync(samples, ctx.WhisperInitialPrompt, CancellationToken.None).ConfigureAwait(false);
                lastTranscript = transcript ?? string.Empty;
                if (string.IsNullOrWhiteSpace(transcript))
                {
                    lastCanonical = "<empty transcript>";
                    continue;
                }

                var mapped = await SpeechRecognitionService
                    .MapTranscriptAsync(transcript, ctx, ruleMapper, llmMapper, callsignResolver, CancellationToken.None)
                    .ConfigureAwait(false);
                lastCanonical = mapped.Canonical ?? "<null>";
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
                    var trialWer = WordErrorRate(
                        AtcNumberParser.NormalizeDigits(expectation.Transcript),
                        AtcNumberParser.NormalizeDigits(transcript)
                    );
                    wer = wer is null ? trialWer : Math.Min(wer.Value, trialWer);
                }
            }
            sw.Stop();

            var verdict =
                matches == trials ? "PASS"
                : matches == 0 ? "FAIL"
                : $"FLAKY {matches}/{trials}";
            if (matches == trials)
            {
                pass++;
            }
            else if (matches == 0)
            {
                fail++;
            }
            else
            {
                flaky++;
            }
            if (wer is not null)
            {
                werValues.Add(wer.Value);
            }

            var werText = wer is null ? "—" : wer.Value.ToString("P0", CultureInfo.InvariantCulture);
            Console.WriteLine(
                $"{verdict, -10} {caseName}: got \"{lastCanonical}\" (callsign {lastCallsign}, WER {werText}, {sw.ElapsedMilliseconds} ms)"
            );
            if (verdict != "PASS")
            {
                Console.WriteLine($"           expected \"{expectation.Canonical}\"  transcript \"{lastTranscript}\"");
            }
            report.AppendLine($"| {caseName} | {verdict} | `{expectation.Canonical}` | `{lastCanonical}` | {werText} | {lastCallsign} |");
        }

        report.AppendLine();
        var meanWer = werValues.Count > 0 ? werValues.Average().ToString("P1", CultureInfo.InvariantCulture) : "n/a";
        var summary = $"{pass} PASS, {flaky} FLAKY, {fail} FAIL, {skipped} skipped — mean best-trial WER {meanWer}";
        report.AppendLine($"**Summary:** {summary}");
        var reportPath = Path.Combine(outDir, "report.md");
        await File.WriteAllTextAsync(reportPath, report.ToString()).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine(summary);
        Console.WriteLine($"Report: {reportPath}");
        return fail > 0 ? 1 : 0;
    }

    /// <summary>
    /// Loads <c>expected.json</c>, or — for cases exported straight from the speech-sample store —
    /// writes a pre-filled stub from <c>session.json</c> so labeling a captured session is a
    /// one-edit review instead of hand-authoring JSON. Stubbed cases carry an "unreviewed": true
    /// flag and are skipped until a human deletes the flag, because the recorded canonical is what
    /// the pipeline PRODUCED at capture time, not verified ground truth — scoring against it would
    /// grade the pipeline against itself.
    /// </summary>
    private static EvalExpectation? LoadOrStubExpectation(string caseDir, string caseName)
    {
        var expectedPath = Path.Combine(caseDir, "expected.json");
        if (File.Exists(expectedPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(expectedPath));
            var root = doc.RootElement;
            if (root.TryGetProperty("unreviewed", out var unreviewed) && unreviewed.GetBoolean())
            {
                Console.WriteLine($"SKIP  {caseName}: expected.json is an unreviewed stub — verify the labels and remove the \"unreviewed\" flag");
                return null;
            }
            if (!root.TryGetProperty("canonical", out var canonical) || string.IsNullOrWhiteSpace(canonical.GetString()))
            {
                Console.WriteLine($"SKIP  {caseName}: expected.json has no \"canonical\" field");
                return null;
            }
            return new EvalExpectation(
                canonical.GetString()!,
                root.TryGetProperty("transcript", out var t) ? t.GetString() : null,
                root.TryGetProperty("callsign", out var cs) ? cs.GetString() : null,
                ReadStringList(root, "activeCallsigns"),
                ReadStringList(root, "programmedFixes")
            );
        }

        var sessionPath = Path.Combine(caseDir, "session.json");
        if (!File.Exists(sessionPath))
        {
            Console.WriteLine($"SKIP  {caseName}: no expected.json (and no session.json to stub from)");
            return null;
        }

        using var session = JsonDocument.Parse(File.ReadAllText(sessionPath));
        var s = session.RootElement;
        var stub = new Dictionary<string, object?>
        {
            ["unreviewed"] = true,
            ["canonical"] = s.TryGetProperty("CanonicalCommand", out var c) ? c.GetString() : "",
            ["transcript"] = s.TryGetProperty("Transcript", out var tr) ? tr.GetString() : "",
            ["callsign"] = null,
            ["activeCallsigns"] =
                s.TryGetProperty("Trace", out var trace)
                && trace.ValueKind == JsonValueKind.Object
                && trace.TryGetProperty("ActiveCallsigns", out var acs)
                    ? acs.Deserialize<List<string>>()
                    : new List<string>(),
            ["programmedFixes"] = new List<string>(),
        };
        File.WriteAllText(expectedPath, JsonSerializer.Serialize(stub, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(
            $"SKIP  {caseName}: wrote unreviewed expected.json stub from session.json — review it, fix the labels, remove \"unreviewed\""
        );
        return null;
    }

    private static List<string>? ReadStringList(JsonElement root, string property)
    {
        return root.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.Array ? el.Deserialize<List<string>>() : null;
    }

    /// <summary>Case-insensitive canonical comparison with comma/space separators normalized.</summary>
    private static bool CanonicalsMatch(string expected, string? actual)
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
        var refWords = reference.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hypWords = hypothesis.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (refWords.Length == 0)
        {
            return hypWords.Length == 0 ? 0 : 1;
        }

        // Two-row Levenshtein over words.
        var prev = new int[hypWords.Length + 1];
        var curr = new int[hypWords.Length + 1];
        for (var j = 0; j <= hypWords.Length; j++)
        {
            prev[j] = j;
        }
        for (var i = 1; i <= refWords.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= hypWords.Length; j++)
            {
                var substitution = prev[j - 1] + (string.Equals(refWords[i - 1], hypWords[j - 1], StringComparison.OrdinalIgnoreCase) ? 0 : 1);
                curr[j] = Math.Min(Math.Min(prev[j] + 1, curr[j - 1] + 1), substitution);
            }
            (prev, curr) = (curr, prev);
        }
        return (double)prev[hypWords.Length] / refWords.Length;
    }
}
