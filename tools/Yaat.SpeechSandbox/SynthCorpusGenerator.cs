using System.Globalization;
using System.Text.Json;
using Yaat.Client.Services;
using Yaat.Sim.Speech;

namespace Yaat.SpeechSandbox;

/// <summary>
/// Grows the speech eval corpus with synthetic <b>controller-phraseology</b> cases:
/// renders instruction templates with sampled slot values, verifies the label through the real
/// text-mapping pipeline, synthesizes the utterance with Piper across varied speakers and speeds,
/// and writes ready-to-score <c>--eval</c> case directories (<c>audio.wav</c> +
/// <c>expected.json</c> with <c>"synthetic": true</c>).
///
/// Complements <see cref="OuroborosRunner"/>, which speaks <i>pilot readbacks</i>
/// (PilotResponder output) — this generator speaks what the <i>controller</i> says, which is the
/// production STT input. Labels are provably correct by construction: a case is only written when
/// <c>SpeechRecognitionService.MapTranscriptAsync</c> (rule mapper only, no LLM) maps the exact
/// rendered transcript to the exact expected canonical and callsign. A template that fails that
/// check aborts the run — fix the template, don't ship a mislabeled case.
///
/// Run with <c>--synth-corpus &lt;out-dir&gt; [--cases N] [--seed S] [--voice &lt;dir&gt;]</c>.
/// Deterministic for a given seed. Generated corpora are reproducible and are NOT meant to be
/// committed — generate into <c>.tmp/</c> and point <c>--eval</c> at the directory. Synthetic
/// results measure the phonetic surface only (clean TTS audio, limited voice variety); keep
/// real-mic cases authoritative for model decisions.
/// </summary>
internal static class SynthCorpusGenerator
{
    private sealed record Template(string Key, string SpokenPattern, string CanonicalPattern);

    // Controller-phraseology templates. Slots: {hdg} {alt} {spd} {rwy} {sq} {fix}. Spoken side
    // is rendered with ATC word forms; canonical side with digit forms. Every rendered pair is
    // verified through the rule mapper before any audio is synthesized.
    private static readonly Template[] Templates =
    [
        new("tl", "turn left heading {hdg}", "TL {hdg}"),
        new("tr", "turn right heading {hdg}", "TR {hdg}"),
        new("fh", "fly heading {hdg}", "FH {hdg}"),
        new("cm", "climb and maintain {alt}", "CM {alt}"),
        new("dm", "descend and maintain {alt}", "DM {alt}"),
        new("spd", "reduce speed to {spd}", "SPD {spd}"),
        new("cto", "runway {rwy} cleared for takeoff", "CTO"),
        new("cland", "runway {rwy} cleared to land", "CLAND"),
        new("luaw", "runway {rwy} line up and wait", "LUAW"),
        new("sq", "squawk {sq}", "SQ {sq}"),
        new("ga", "go around", "GA"),
        new("dct", "proceed direct {fix}", "DCT {fix}"),
        new("dm-spd", "descend and maintain {alt} reduce speed to {spd}", "DM {alt}, SPD {spd}"),
        new("tr-dm", "turn right heading {hdg} descend and maintain {alt}", "TR {hdg}, DM {alt}"),
        new("tl-cm", "turn left heading {hdg} climb and maintain {alt}", "TL {hdg}, CM {alt}"),
    ];

    private static readonly string[] CallsignPool = ["UAL234", "SWA1943", "DAL512", "AAL2231", "ASA331", "N346G", "N9225L", "N514RM"];
    private static readonly string[] RunwayPool = ["28R", "28L", "30", "10R", "09", "27C", "33"];
    private static readonly string[] FixPool = ["CEPIN", "SUNOL", "ALTAM"];
    private static readonly string[] DigitWords = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "niner"];

    // LibriTTS-R medium is multi-speaker; a spread of ids + speeds approximates voice variety.
    // Faster speeds mimic rapid-fire controller delivery.
    private static readonly int[] SpeakerPool = [50, 92, 147, 246, 421, 588, 700, 810];
    private static readonly float[] SpeedPool = [0.9f, 1.0f, 1.1f, 1.2f];

    private const int LeadingSilenceMs = 400;
    private const int TrailingSilenceMs = 400;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: Yaat.SpeechSandbox --synth-corpus <out-dir> [--cases N] [--seed S] [--voice <dir>]");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Generates labeled synthetic controller-phraseology eval cases (audio.wav + expected.json).");
            Console.Error.WriteLine("Point --eval at the output directory afterwards. Deterministic per seed.");
            return 1;
        }

        var outDir = args[0];
        var cases = 30;
        var seed = 20260730;
        string? voiceDir = null;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--cases" && i + 1 < args.Length)
            {
                cases = int.Parse(args[++i], CultureInfo.InvariantCulture);
            }
            else if (args[i] == "--seed" && i + 1 < args.Length)
            {
                seed = int.Parse(args[++i], CultureInfo.InvariantCulture);
            }
            else if (args[i] == "--voice" && i + 1 < args.Length)
            {
                voiceDir = args[++i];
            }
        }

        voiceDir ??= PiperSynthesizer.ResolveDefaultVoiceDir();
        if (voiceDir is null)
        {
            Console.Error.WriteLine("FATAL: Piper voice pack not found — install it via Yaat.Client Settings → Speech → TTS.");
            return 2;
        }

        // Real navdata: PhraseologyMapper validates emitted canonicals through CommandParser,
        // whose fix resolution (e.g. the DCT template) requires NavigationDatabase. Loads
        // NavData.dat + CIFP the same way the test suite does (with bundled offline fallbacks).
        Yaat.Sim.Testing.TestVnasData.EnsureInitialized();

        Directory.CreateDirectory(outDir);
        using var piper = new PiperSynthesizer(voiceDir);
        var rng = new Random(seed);
        var ruleMapper = new PhraseologyCommandMapper();
        var written = 0;

        for (var i = 0; i < cases; i++)
        {
            var template = Templates[i % Templates.Length];
            var callsign = CallsignPool[rng.Next(CallsignPool.Length)];
            var (spokenBody, canonical, fix) = RenderTemplate(template, rng);
            var spokenCallsign = CallsignParser.IcaoToSpoken(callsign);
            var transcript = $"{spokenCallsign} {spokenBody}";
            var activeCallsigns = BuildActiveCallsigns(callsign, rng);
            var programmedFixes = fix is null ? new List<string>() : [fix];

            // Label verification: the exact transcript must map to the exact canonical +
            // callsign through the production text pipeline (rule mapper only — deterministic,
            // no models needed). A mismatch means the template or slot rendering is wrong;
            // abort loudly rather than emit a mislabeled case.
            var ctx = new SpeechContext(activeCallsigns, programmedFixes, WhisperBiasingPrompt.Default);
            var mapped = await SpeechRecognitionService
                .MapTranscriptAsync(transcript, ctx, ruleMapper, llmMapper: null, callsignResolver: null, CancellationToken.None)
                .ConfigureAwait(false);
            if (
                !string.Equals(mapped.Canonical, canonical, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(mapped.Callsign, callsign, StringComparison.OrdinalIgnoreCase)
            )
            {
                Console.Error.WriteLine($"FATAL: template '{template.Key}' failed label verification.");
                Console.Error.WriteLine($"  transcript: \"{transcript}\"");
                Console.Error.WriteLine($"  expected:   {callsign} {canonical}");
                Console.Error.WriteLine($"  mapped:     {mapped.Callsign ?? "(none)"} {mapped.Canonical ?? "(null)"}");
                return 3;
            }

            var speaker = SpeakerPool[rng.Next(SpeakerPool.Length)];
            var speed = SpeedPool[rng.Next(SpeedPool.Length)];
            var synth = piper.Synthesize(transcript, speaker, speed);
            var resampled = PiperSynthesizer.Resample(synth.Samples, synth.SampleRate, AudioCaptureService.SampleRate);
            var samples = PiperSynthesizer.PadWithSilence(resampled, AudioCaptureService.SampleRate, LeadingSilenceMs, TrailingSilenceMs);

            var caseDir = Path.Combine(outDir, $"synth-{seed}-{i:D3}-{template.Key}");
            Directory.CreateDirectory(caseDir);
            var wavStream = WavHeader.WritePcm16(samples, AudioCaptureService.SampleRate);
            await File.WriteAllBytesAsync(Path.Combine(caseDir, "audio.wav"), wavStream.ToArray()).ConfigureAwait(false);

            var expected = new Dictionary<string, object?>
            {
                ["synthetic"] = true,
                ["canonical"] = canonical,
                ["transcript"] = transcript,
                ["callsign"] = callsign,
                ["activeCallsigns"] = activeCallsigns,
                ["programmedFixes"] = programmedFixes,
                ["voice"] = $"piper speaker {speaker} speed {speed.ToString("F1", CultureInfo.InvariantCulture)}",
            };
            await File.WriteAllTextAsync(
                    Path.Combine(caseDir, "expected.json"),
                    JsonSerializer.Serialize(expected, new JsonSerializerOptions { WriteIndented = true })
                )
                .ConfigureAwait(false);
            written++;
        }

        Console.WriteLine($"Wrote {written} synthetic cases to {Path.GetFullPath(outDir)} (seed {seed}).");
        Console.WriteLine($"Score them with: --eval {outDir} [--trials N]");
        return 0;
    }

    /// <summary>Renders a template's spoken + canonical sides with one set of sampled slot values.</summary>
    private static (string Spoken, string Canonical, string? Fix) RenderTemplate(Template template, Random rng)
    {
        var spoken = template.SpokenPattern;
        var canonical = template.CanonicalPattern;
        string? fix = null;

        if (spoken.Contains("{hdg}", StringComparison.Ordinal))
        {
            var hdg = (rng.Next(1, 37) * 10) % 360;
            hdg = hdg == 0 ? 360 : hdg;
            var digits = hdg.ToString("D3");
            spoken = spoken.Replace("{hdg}", SpeakDigits(digits), StringComparison.Ordinal);
            canonical = canonical.Replace("{hdg}", digits, StringComparison.Ordinal);
        }
        if (spoken.Contains("{alt}", StringComparison.Ordinal))
        {
            var thousands = rng.Next(2, 17);
            var alt = thousands * 1000;
            var spokenAlt =
                thousands <= 9 ? $"{DigitWords[thousands]} thousand" : $"{SpeakDigits(thousands.ToString(CultureInfo.InvariantCulture))} thousand";
            spoken = spoken.Replace("{alt}", spokenAlt, StringComparison.Ordinal);
            canonical = canonical.Replace("{alt}", alt.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }
        if (spoken.Contains("{spd}", StringComparison.Ordinal))
        {
            var spd = rng.Next(15, 26) * 10;
            spoken = spoken.Replace("{spd}", SpeakDigits(spd.ToString(CultureInfo.InvariantCulture)), StringComparison.Ordinal);
            canonical = canonical.Replace("{spd}", spd.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }
        if (spoken.Contains("{rwy}", StringComparison.Ordinal))
        {
            var rwy = RunwayPool[rng.Next(RunwayPool.Length)];
            spoken = spoken.Replace("{rwy}", SpeakRunway(rwy), StringComparison.Ordinal);
            canonical = canonical.Replace("{rwy}", rwy, StringComparison.Ordinal);
        }
        if (spoken.Contains("{sq}", StringComparison.Ordinal))
        {
            var code = string.Concat(Enumerable.Range(0, 4).Select(_ => rng.Next(0, 8).ToString(CultureInfo.InvariantCulture)));
            spoken = spoken.Replace("{sq}", SpeakDigits(code), StringComparison.Ordinal);
            canonical = canonical.Replace("{sq}", code, StringComparison.Ordinal);
        }
        if (spoken.Contains("{fix}", StringComparison.Ordinal))
        {
            fix = FixPool[rng.Next(FixPool.Length)];
            spoken = spoken.Replace("{fix}", fix.ToLowerInvariant(), StringComparison.Ordinal);
            canonical = canonical.Replace("{fix}", fix, StringComparison.Ordinal);
        }

        return (spoken, canonical, fix);
    }

    private static string SpeakDigits(string digits) => string.Join(' ', digits.Select(c => DigitWords[c - '0']));

    /// <summary>"28R" → "two eight right"; bare numbers speak digit-by-digit ("30" → "three zero").</summary>
    private static string SpeakRunway(string runway)
    {
        var digits = new string(runway.TakeWhile(char.IsDigit).ToArray());
        var suffix = runway[digits.Length..] switch
        {
            "L" => " left",
            "R" => " right",
            "C" => " center",
            _ => "",
        };
        // Zero-padded designators are spoken without the leading zero ("09" → "niner").
        var spokenDigits = digits.TrimStart('0');
        spokenDigits = spokenDigits.Length == 0 ? "0" : spokenDigits;
        return SpeakDigits(spokenDigits) + suffix;
    }

    private static List<string> BuildActiveCallsigns(string callsign, Random rng)
    {
        var list = new List<string> { callsign };
        while (list.Count < 3)
        {
            var decoy = CallsignPool[rng.Next(CallsignPool.Length)];
            if (!list.Contains(decoy))
            {
                list.Add(decoy);
            }
        }
        return list;
    }
}
