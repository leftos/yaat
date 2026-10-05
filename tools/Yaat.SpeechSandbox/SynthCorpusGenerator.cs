using System.Globalization;
using System.Text.Json;
using Yaat.Client.Services;

namespace Yaat.SpeechSandbox;

/// <summary>Inputs of one synthetic-corpus generation run.</summary>
/// <param name="OutDir">Directory the case subdirectories are written into.</param>
/// <param name="Cases">Number of cases to plan.</param>
/// <param name="Seed">Seed for template sampling, slot values and voices.</param>
/// <param name="VoiceDir">Piper voice-pack directory.</param>
/// <param name="UseSynthCache">Reuse the content-addressed synth-audio cache instead of re-synthesizing every case.</param>
internal sealed record SynthCorpusOptions(string OutDir, int Cases, int Seed, string VoiceDir, bool UseSynthCache);

/// <summary>One case directory the generator wrote, and whether its audio came from the cache.</summary>
internal sealed record WrittenCase(string CaseDir, string TemplateKey, bool CacheHit);

/// <summary>What a generation run produced: the written cases and every gap found.</summary>
internal sealed record SynthCorpusResult(IReadOnlyList<WrittenCase> Written, IReadOnlyList<TemplateGap> Gaps);

/// <summary>
/// Grows the speech eval corpus with synthetic <b>controller-phraseology</b> cases:
/// renders instruction templates (<see cref="SynthTemplates"/>) with sampled slot values, verifies
/// the label through the real text-mapping pipeline, synthesizes the utterance with Piper across
/// varied speakers and speeds, and writes ready-to-score <c>--eval</c> case directories
/// (<c>audio.wav</c> + <c>expected.json</c> with <c>"synthetic": true</c> and the template key).
///
/// Complements <see cref="OuroborosRunner"/>, which speaks <i>pilot readbacks</i>
/// (PilotResponder output) — this generator speaks what the <i>controller</i> says, which is the
/// production STT input. Labels are provably correct by construction: a case is only written when
/// <c>SpeechRecognitionService.MapTranscriptAsync</c> (rule mapper only, no LLM) maps the exact
/// rendered transcript to the exact expected canonical and callsign. A template or render that
/// fails that check is a gap: it is printed as a warning and left out of the corpus, never written
/// with a wrong label.
///
/// Run with <c>--synth-corpus &lt;out-dir&gt; [--cases N] [--seed S] [--voice &lt;dir&gt;] [--no-synth-cache]</c>.
/// Deterministic for a given seed: each case's audio is cached by its synthesis inputs
/// (<see cref="SynthAudioCache"/>) and reused, so a repeat run of one seed is byte-identical and all
/// cache hits — Piper's own sampling differs between processes. Generated corpora are NOT meant to
/// be committed — generate into <c>.tmp/</c> and point <c>--eval</c> at the directory. Synthetic
/// results measure the phonetic surface only (clean TTS audio, limited voice variety); keep
/// real-mic cases authoritative for model decisions.
/// </summary>
internal static class SynthCorpusGenerator
{
    private const int LeadingSilenceMs = 400;
    private const int TrailingSilenceMs = 400;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: Yaat.SpeechSandbox --synth-corpus <out-dir> [--cases N] [--seed S] [--voice <dir>] [--no-synth-cache]");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Generates labeled synthetic controller-phraseology eval cases (audio.wav + expected.json).");
            Console.Error.WriteLine("Point --eval at the output directory afterwards. Deterministic per seed: synthesized audio");
            Console.Error.WriteLine("is cached by its synthesis inputs, so a repeat run is byte-identical.");
            return 1;
        }

        string outDir = args[0];
        int cases = 30;
        int seed = 20260730;
        string? voiceDir = null;
        bool useSynthCache = true;
        for (int i = 1; i < args.Length; i++)
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
            else if (args[i] == "--no-synth-cache")
            {
                useSynthCache = false;
            }
        }

        voiceDir ??= PiperSynthesizer.ResolveDefaultVoiceDir();
        if (voiceDir is null)
        {
            Console.Error.WriteLine("FATAL: Piper voice pack not found — install it via Yaat.Client Settings → Speech → TTS.");
            return 2;
        }

        SynthCorpusResult result = await GenerateAsync(new SynthCorpusOptions(outDir, cases, seed, voiceDir, useSynthCache)).ConfigureAwait(false);
        int fromCache = result.Written.Count(c => c.CacheHit);
        string fullOutDir = Path.GetFullPath(outDir);
        Console.WriteLine(
            $"Wrote {result.Written.Count} synthetic cases to {fullOutDir} (seed {seed}, {result.Gaps.Count} gaps left out, {fromCache} from cache)."
        );
        Console.WriteLine($"Score them with: --eval {outDir} [--trials N]");
        return 0;
    }

    /// <summary>
    /// Plans and verifies the cases (<see cref="SynthTemplates.PlanAsync"/>), then synthesizes and
    /// writes every verified one. Each gap is printed as a <c>GAP …</c> warning line.
    /// </summary>
    public static async Task<SynthCorpusResult> GenerateAsync(SynthCorpusOptions options)
    {
        SynthPlan plan = await SynthTemplates.PlanAsync(SynthTemplates.All, options.Cases, options.Seed).ConfigureAwait(false);
        foreach (TemplateGap gap in plan.Gaps)
        {
            Console.WriteLine(gap.Describe());
        }

        Directory.CreateDirectory(options.OutDir);
        using var piper = new PiperSynthesizer(options.VoiceDir);
        SynthAudioCache? cache = options.UseSynthCache ? new SynthAudioCache(SynthAudioCache.DefaultRoot) : null;
        var written = new List<WrittenCase>();
        foreach (RenderedCase rendered in plan.Cases)
        {
            written.Add(await WriteCaseAsync(piper, rendered, options, cache).ConfigureAwait(false));
        }
        return new SynthCorpusResult(written, plan.Gaps);
    }

    private static float[] SynthesizeCaseAudio(PiperSynthesizer piper, RenderedCase rendered, string text)
    {
        PiperSynthesizer.SynthResult synth = piper.Synthesize(text, rendered.Speaker, rendered.Speed);
        float[] resampled = PiperSynthesizer.Resample(synth.Samples, synth.SampleRate, AudioCaptureService.SampleRate);
        return PiperSynthesizer.PadWithSilence(resampled, AudioCaptureService.SampleRate, LeadingSilenceMs, TrailingSilenceMs);
    }

    private static async Task<WrittenCase> WriteCaseAsync(
        PiperSynthesizer piper,
        RenderedCase rendered,
        SynthCorpusOptions options,
        SynthAudioCache? cache
    )
    {
        EvalExpectation e = rendered.Expectation;
        string text = e.Transcript!;
        string cacheKey = SynthAudioCache.ComputeKey(
            new SynthAudioKeyInputs
            {
                VoiceIdentity = piper.VoiceIdentity,
                Text = text,
                SpeakerId = rendered.Speaker,
                Speed = rendered.Speed,
                LengthScale = PiperSynthesizer.LengthScale,
                SampleRate = AudioCaptureService.SampleRate,
                LeadingSilenceMs = LeadingSilenceMs,
                TrailingSilenceMs = TrailingSilenceMs,
            }
        );
        float[] samples;
        bool cacheHit = false;
        if ((cache is not null) && cache.TryGet(cacheKey, out float[] cached))
        {
            samples = cached;
            cacheHit = true;
        }
        else
        {
            float[] synthesized = SynthesizeCaseAudio(piper, rendered, text);
            samples = cache is null ? synthesized : cache.PutOrGetExisting(cacheKey, synthesized, AudioCaptureService.SampleRate);
        }

        string caseDir = Path.Combine(options.OutDir, $"synth-{options.Seed}-{rendered.Index:D3}-{rendered.Template.Key}");
        Directory.CreateDirectory(caseDir);
        MemoryStream wavStream = WavHeader.WritePcm16(samples, AudioCaptureService.SampleRate);
        await File.WriteAllBytesAsync(Path.Combine(caseDir, "audio.wav"), wavStream.ToArray()).ConfigureAwait(false);

        var expected = new Dictionary<string, object?>
        {
            ["synthetic"] = true,
            ["template"] = rendered.Template.Key,
            ["canonical"] = e.Canonical,
            ["transcript"] = e.Transcript,
            ["callsign"] = e.Callsign,
            ["activeCallsigns"] = e.ActiveCallsigns,
            ["programmedFixes"] = e.ProgrammedFixes,
            ["availableRunways"] = e.AvailableRunways,
            ["taxiwayNames"] = e.TaxiwayNames,
            ["destinationNames"] = e.DestinationNames,
            ["voice"] = $"piper speaker {rendered.Speaker} speed {rendered.Speed.ToString("F1", CultureInfo.InvariantCulture)}",
            ["cacheKey"] = cacheKey,
        };
        await File.WriteAllTextAsync(
                Path.Combine(caseDir, "expected.json"),
                JsonSerializer.Serialize(expected, new JsonSerializerOptions { WriteIndented = true })
            )
            .ConfigureAwait(false);
        return new WrittenCase(caseDir, rendered.Template.Key, cacheHit);
    }
}
