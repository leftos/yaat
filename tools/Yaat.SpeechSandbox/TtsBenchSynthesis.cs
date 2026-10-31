using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using NAudio.Wave;
using SherpaOnnx;
using Yaat.Client.Services;
using Yaat.Sim;

namespace Yaat.SpeechSandbox;

/// <summary>One timed phase of a model: the lines and voices it speaks, the thread count, and whether its clips are kept.</summary>
internal sealed record TtsBenchPhase(IReadOnlyList<TtsBenchLine> Lines, IReadOnlyList<TtsBenchVoice> Voices, int Threads, bool SaveClips)
{
    /// <summary>"full" for the full run (clips kept), "screen" otherwise.</summary>
    public string Kind => SaveClips ? "full" : "screen";

    /// <summary>How long the phase's child process may run before it is killed.</summary>
    public TimeSpan Deadline => SaveClips ? TtsBenchSynthesis.FullRunDeadline : TtsBenchSynthesis.ScreenDeadline;
}

/// <summary>
/// The timed synthesis behind <c>--tts-bench</c>: the sherpa-onnx child process (<c>--tts-bench-one</c>), the
/// Kitten-TTS 2 script, the first-audio measurement and the clip files.
/// </summary>
internal static class TtsBenchSynthesis
{
    /// <summary>The sentence the cloning references read (about 6 s at speed 1.0).</summary>
    public const string ReferenceText =
        "Good afternoon, this is a short reference recording for the voice benchmark, read clearly and at an even, normal pace.";

    /// <summary>A screen phase's child process is killed after this.</summary>
    public static readonly TimeSpan ScreenDeadline = TimeSpan.FromSeconds(300);

    /// <summary>A full-run phase's child process is killed after this.</summary>
    public static readonly TimeSpan FullRunDeadline = TimeSpan.FromSeconds(1800);

    /// <summary>The reference-rendering child process is killed after this.</summary>
    public static readonly TimeSpan ReferencesDeadline = TimeSpan.FromSeconds(300);

    private const string WarmupText = "Radio check, how do you read.";

    public static string ReferencePath(string referenceDir, int speaker) => Path.Combine(referenceDir, $"speaker-{speaker}.wav");

    public static TtsBenchModelResult NotRun(TtsBenchModel model, string reason, TtsBenchRunner.ModelAssets? assets) =>
        new(
            model.Id,
            model.Name,
            model.Runtime.ToString(),
            model.License,
            reason,
            assets?.DownloadBytes,
            assets?.DiskBytes,
            null,
            null,
            null,
            null,
            [.. model.Voices.Select(v => v.Label)],
            null,
            [],
            [],
            TtsBenchVerdict.NotRun
        );

    /// <summary>
    /// Runs one phase of a sherpa-onnx model in a child sandbox process (<c>--tts-bench-one &lt;job.json&gt;</c>), so a
    /// native abort costs only that phase: a non-zero exit, a timeout or a missing or malformed result becomes its not-run reason.
    /// </summary>
    public static async Task<TtsBenchModelResult> RunChildAsync(
        TtsBenchModel model,
        TtsBenchRunner.ModelAssets assets,
        string modelDir,
        TtsBenchPhase phase,
        string tmpRoot
    )
    {
        string jobDir = Path.Combine(tmpRoot, "jobs");
        string stem = $"{model.Id}-t{phase.Threads}-{phase.Kind}";
        var job = new TtsBenchJob(
            TtsBenchJobKind.Phase,
            model.Id,
            modelDir,
            assets.VocoderPath,
            phase.Threads,
            [.. phase.Voices.Select(v => v.Label)],
            phase.SaveClips,
            Path.Combine(tmpRoot, "clips"),
            Path.Combine(tmpRoot, "references"),
            Path.Combine(jobDir, $"{stem}.result.json"),
            phase.Lines
        );
        string? error = await RunJobAsync(job, Path.Combine(jobDir, $"{stem}.job.json"), phase.Deadline).ConfigureAwait(false);
        if (error is not null)
        {
            return NotRun(model, error, assets);
        }
        try
        {
            return JsonSerializer.Deserialize(
                    await File.ReadAllTextAsync(job.ResultPath).ConfigureAwait(false),
                    TtsBenchJsonContext.Default.TtsBenchModelResult
                ) ?? throw new InvalidDataException($"{job.ResultPath} holds null.");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidDataException)
        {
            return NotRun(model, $"not run: {ex.Message}", assets);
        }
    }

    /// <summary>
    /// Renders the cloning references in a child process: the reference model's speakers reading
    /// <see cref="ReferenceText"/> at speed 1.0 into <c>references/speaker-N.wav</c>. Returns null on success, else why not.
    /// </summary>
    public static Task<string?> RenderReferencesAsync(TtsBenchModel referenceModel, string modelDir, string tmpRoot)
    {
        string jobDir = Path.Combine(tmpRoot, "jobs");
        var job = new TtsBenchJob(
            TtsBenchJobKind.References,
            referenceModel.Id,
            modelDir,
            null,
            TtsBenchModels.ProductionNumThreads,
            [.. referenceModel.Voices.Select(v => v.Label)],
            false,
            Path.Combine(tmpRoot, "clips"),
            Path.Combine(tmpRoot, "references"),
            Path.Combine(jobDir, "references.result.json"),
            []
        );
        return RunJobAsync(job, Path.Combine(jobDir, "references.job.json"), ReferencesDeadline);
    }

    /// <summary>
    /// Writes <paramref name="job"/> and runs it in a child sandbox process; returns null when it wrote its result,
    /// else the not-run reason.
    /// </summary>
    private static async Task<string?> RunJobAsync(TtsBenchJob job, string jobPath, TimeSpan deadline)
    {
        string jobDir = Path.GetDirectoryName(jobPath)!;
        Directory.CreateDirectory(jobDir);
        File.Delete(job.ResultPath);
        await File.WriteAllTextAsync(jobPath, JsonSerializer.Serialize(job, TtsBenchCorpusJsonContext.Default.TtsBenchJob)).ConfigureAwait(false);

        string self = Environment.ProcessPath ?? throw new InvalidOperationException("No process path to re-invoke the sandbox.");
        var args = new List<string>();
        if (Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            args.Add(typeof(TtsBenchSynthesis).Assembly.Location);
        }
        args.AddRange(["--tts-bench-one", jobPath]);
        ProcessOutcome outcome = await RunProcessAsync(StartInfo(self, args, jobDir), deadline).ConfigureAwait(false);
        if (outcome.TimedOut)
        {
            return $"not run: timed out after {deadline.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)} s";
        }
        if (outcome.ExitCode != 0 || !File.Exists(job.ResultPath))
        {
            return $"not run: exit code {outcome.ExitCode}, {outcome.FirstError ?? "no stderr"}";
        }
        return null;
    }

    /// <summary>
    /// Child entry point (<c>--tts-bench-one &lt;job.json&gt;</c>): runs one phase of one sherpa-onnx model, or renders
    /// the cloning references, and writes the result JSON (to a <c>.partial</c> file first, then moved into place).
    /// </summary>
    public static async Task<int> RunOneAsync(string[] args)
    {
        if (args.Length != 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: Yaat.SpeechSandbox --tts-bench-one <job.json> (started by --tts-bench)");
            return TtsBenchRunner.ExitSetupError;
        }
        TtsBenchJob job =
            JsonSerializer.Deserialize(await File.ReadAllTextAsync(args[0]).ConfigureAwait(false), TtsBenchCorpusJsonContext.Default.TtsBenchJob)
            ?? throw new InvalidDataException($"{args[0]} is empty.");
        TtsBenchModel model = TtsBenchModels.All.Single(m => m.Id == job.ModelId);
        string json =
            job.Kind == TtsBenchJobKind.References
                ? JsonSerializer.Serialize(RenderReferences(model, job), TtsBenchCorpusJsonContext.Default.ListString)
                : JsonSerializer.Serialize(RunSherpa(model, job), TtsBenchJsonContext.Default.TtsBenchModelResult);
        string partial = job.ResultPath + ".partial";
        await File.WriteAllTextAsync(partial, json).ConfigureAwait(false);
        File.Move(partial, job.ResultPath, overwrite: true);
        return TtsBenchRunner.ExitOk;
    }

    private sealed record ReferenceClip(float[] Samples, int SampleRate, string Text);

    private sealed record SynthTiming(float[] Samples, int SampleRate, double? FirstAudioMs, double SynthMs);

    private sealed record ProcessOutcome(int ExitCode, bool TimedOut, string? FirstError, string? LastError);

    /// <summary>The espeak-ng data directory the model's text is phonemized from, or null when its runtime tokenizes its own text.</summary>
    private static string? PhonemeDataDir(TtsBenchModel model, TtsBenchJob job) =>
        TtsBenchModels.NeedsPhonemes(model.Runtime) ? TtsBenchModels.EspeakDataDir(job.ModelDir) : null;

    /// <summary>Renders each of the reference model's speakers reading <see cref="ReferenceText"/>; returns the files written.</summary>
    private static List<string> RenderReferences(TtsBenchModel model, TtsBenchJob job)
    {
        using var tts = new OfflineTts(TtsBenchModels.BuildConfig(model, job.ModelDir, null, job.Threads));
        Directory.CreateDirectory(job.ReferenceDir);
        string? espeakDataDir = PhonemeDataDir(model, job);
        var written = new List<string>();
        foreach (int speaker in model.Voices.Select(v => v.Sid))
        {
            var gen = new OfflineTtsGenerationConfig { Sid = speaker, Speed = 1.0f };
            if (espeakDataDir is not null)
            {
                gen.PhonemeCodepoints = EspeakPhonemizer.Phonemize(ReferenceText, EspeakPhonemizer.VoiceFor(tts.Lang), espeakDataDir);
            }

            OfflineTtsGeneratedAudio audio = tts.GenerateWithConfig(espeakDataDir is null ? ReferenceText : "", gen, null);
            float[] samples = EspeakPhonemizer.RequireSpeech(audio.Samples, $"{model.Id} speaker {speaker} reference clip");
            string path = ReferencePath(job.ReferenceDir, speaker);
            WritePcm16(path, samples, tts.SampleRate);
            written.Add(path);
            Console.WriteLine($"  reference speaker {speaker}: {samples.Length / (double)tts.SampleRate:F1} s");
        }
        return written;
    }

    private static TtsBenchModelResult RunSherpa(TtsBenchModel model, TtsBenchJob job)
    {
        List<TtsBenchVoice> voices = [.. model.Voices.Where(v => job.Voices.Contains(v.Label))];
        Dictionary<int, ReferenceClip> references = LoadReferences(voices, job.ReferenceDir);

        var loadWatch = Stopwatch.StartNew();
        using var tts = new OfflineTts(TtsBenchModels.BuildConfig(model, job.ModelDir, job.VocoderPath, job.Threads));
        loadWatch.Stop();
        double loadSeconds = loadWatch.Elapsed.TotalSeconds;
        string? stoppedEarly =
            !job.SaveClips && loadSeconds > TtsBenchRunner.LoadLimitSeconds
                ? $"{TtsBenchRunner.ScreenedOutAtOnce}: load {loadSeconds.ToString("F1", CultureInfo.InvariantCulture)} s"
                : null;
        double? warmupMs = null;
        List<TtsBenchClip> clips = [];
        if (stoppedEarly is null)
        {
            warmupMs = Synthesize(tts, WarmupText, voices[0], RefFor(references, voices[0]), PhonemeDataDir(model, job)).SynthMs;
            (clips, stoppedEarly) = SynthesizeAll(tts, model, job, voices, references);
        }
        return new TtsBenchModelResult(
            model.Id,
            model.Name,
            model.Runtime.ToString(),
            model.License,
            stoppedEarly,
            null,
            null,
            job.Threads,
            loadSeconds,
            warmupMs,
            tts.SampleRate,
            [.. voices.Select(v => v.Label)],
            model.IsCloning ? "Voices cloned from Piper LibriTTS-R fp32 references (speakers 10, 300, 600) reading one fixed sentence." : null,
            clips,
            [],
            stoppedEarly is null ? TtsBenchVerdict.Pass : TtsBenchVerdict.ScreenedOut
        );
    }

    /// <summary>The reference clips the cloning voices among <paramref name="voices"/> need, keyed by reference speaker.</summary>
    private static Dictionary<int, ReferenceClip> LoadReferences(IEnumerable<TtsBenchVoice> voices, string referenceDir)
    {
        var references = new Dictionary<int, ReferenceClip>();
        foreach (int speaker in voices.Where(v => v.ReferenceSpeaker is not null).Select(v => v.ReferenceSpeaker!.Value))
        {
            float[] samples = ReadWav(ReferencePath(referenceDir, speaker), out int rate);
            references[speaker] = new ReferenceClip(samples, rate, ReferenceText);
        }
        return references;
    }

    private static ReferenceClip? RefFor(Dictionary<int, ReferenceClip> references, TtsBenchVoice voice) =>
        voice.ReferenceSpeaker is { } s ? references[s] : null;

    /// <summary>
    /// Every voice speaks every line. A screen stops at once when the first line's first audio is over
    /// <see cref="TtsBenchRunner.FirstLineFirstAudioLimitMs"/>; the second item is then the reason.
    /// </summary>
    private static (List<TtsBenchClip> Clips, string? StoppedEarly) SynthesizeAll(
        OfflineTts tts,
        TtsBenchModel model,
        TtsBenchJob job,
        IReadOnlyList<TtsBenchVoice> voices,
        Dictionary<int, ReferenceClip> references
    )
    {
        var clips = new List<TtsBenchClip>();
        string? espeakDataDir = PhonemeDataDir(model, job);
        foreach (TtsBenchVoice voice in voices)
        {
            foreach (TtsBenchLine line in job.Lines)
            {
                SynthTiming t = Synthesize(tts, line.Text, voice, RefFor(references, voice), espeakDataDir);
                clips.Add(ToClip(job.SaveClips ? job.ClipsRoot : null, model.Id, voice.Label, line.Index, t));
                if (!job.SaveClips && clips.Count == 1 && t.FirstAudioMs is { } firstMs && firstMs > TtsBenchRunner.FirstLineFirstAudioLimitMs)
                {
                    string seconds = (firstMs / 1000).ToString("F1", CultureInfo.InvariantCulture);
                    return (clips, $"{TtsBenchRunner.ScreenedOutAtOnce}: first audio {seconds} s on the first screen line");
                }
            }
        }
        return (clips, null);
    }

    /// <summary>
    /// One synthesis through the callback API: first audio is the first callback carrying samples, null when none did.
    /// <paramref name="espeakDataDir"/> is the model's espeak-ng data directory when its runtime needs phonemes,
    /// null for one that tokenizes its own text.
    /// </summary>
    private static SynthTiming Synthesize(OfflineTts tts, string text, TtsBenchVoice voice, ReferenceClip? reference, string? espeakDataDir)
    {
        var gen = new OfflineTtsGenerationConfig { Sid = voice.Sid, Speed = TtsBenchRunner.Speed };
        if (reference is not null)
        {
            gen.ReferenceAudio = reference.Samples;
            gen.ReferenceSampleRate = reference.SampleRate;
            gen.ReferenceText = reference.Text;
        }
        long firstTicks = -1;
        var watch = Stopwatch.StartNew();
        if (espeakDataDir is not null)
        {
            gen.PhonemeCodepoints = EspeakPhonemizer.Phonemize(text, EspeakPhonemizer.VoiceFor(tts.Lang), espeakDataDir);
        }
        OfflineTtsCallbackProgressWithArg callback = (_, n, _, _) =>
        {
            if (n > 0 && firstTicks < 0)
            {
                firstTicks = watch.ElapsedTicks;
            }
            return 1;
        };
        OfflineTtsGeneratedAudio audio = tts.GenerateWithConfig(espeakDataDir is null ? text : "", gen, callback);
        watch.Stop();
        GC.KeepAlive(callback);
        float[] samples = EspeakPhonemizer.RequireSpeech(audio.Samples, $"Synthesis for voice {voice.Label}");
        double? firstMs = firstTicks < 0 ? null : firstTicks * 1000.0 / Stopwatch.Frequency;
        return new SynthTiming(samples, tts.SampleRate, firstMs, watch.Elapsed.TotalMilliseconds);
    }

    /// <summary>The clip's timing row; with a clips root, also writes its dry and radio-FX copies, peak-normalised for the page.</summary>
    private static TtsBenchClip ToClip(string? clipsRoot, string modelId, string voice, int line, SynthTiming t)
    {
        if (t.Samples.Length == 0)
        {
            throw new InvalidDataException($"{modelId} voice {voice} line {line} rendered an empty clip.");
        }
        if (clipsRoot is not null)
        {
            string dir = Path.Combine(clipsRoot, modelId, voice);
            Directory.CreateDirectory(dir);
            WritePcm16(Path.Combine(dir, $"{line:00}-dry.wav"), NormalizePeak(t.Samples), t.SampleRate);
            WritePcm16(Path.Combine(dir, $"{line:00}-fx.wav"), NormalizePeak(RadioAudioFx.Apply(t.Samples, t.SampleRate)), t.SampleRate);
        }
        double audioSeconds = t.Samples.Length / (double)t.SampleRate;
        return new TtsBenchClip(voice, line, t.FirstAudioMs, t.SynthMs, audioSeconds, t.SynthMs / 1000.0 / audioSeconds);
    }

    /// <summary>Scales a copy of <paramref name="samples"/> so its peak sits at -1 dBFS; silence is returned unchanged.</summary>
    private static float[] NormalizePeak(float[] samples)
    {
        float peak = samples.Length == 0 ? 0f : samples.Max(MathF.Abs);
        if (peak <= 0f)
        {
            return samples;
        }
        float gain = MathF.Pow(10f, -1f / 20f) / peak;
        return [.. samples.Select(s => s * gain)];
    }

    public static void WritePcm16(string path, float[] samples, int sampleRate)
    {
        using var writer = new WaveFileWriter(path, new WaveFormat(sampleRate, 16, 1));
        writer.WriteSamples(samples, 0, samples.Length);
    }

    private static float[] ReadWav(string path, out int sampleRate)
    {
        using var reader = new WaveFileReader(path);
        sampleRate = reader.WaveFormat.SampleRate;
        ISampleProvider provider = reader.ToSampleProvider();
        var samples = new List<float>();
        float[] buffer = new float[16384];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            samples.AddRange(buffer.AsSpan(0, read));
        }
        return [.. samples];
    }

    /// <summary>
    /// Runs one phase of Kitten-TTS 2 through <c>tts-bench/kitten2_bench.py</c> on the <c>kitten_tts_cpp</c> package and
    /// merges its timings. A runtime that will not install or run, or that writes malformed timings, is recorded as not run.
    /// </summary>
    public static async Task<TtsBenchModelResult> RunKitten2Async(TtsBenchModel model, TtsBenchPhase phase, string tmpRoot, string repoRoot)
    {
        string work = Path.Combine(tmpRoot, "kitten2", $"t{phase.Threads}-{phase.Kind}");
        string rawDir = Path.Combine(work, "raw");
        string linesPath = Path.Combine(work, "lines.json");
        string timingsPath = Path.Combine(work, "timings.json");
        Directory.CreateDirectory(work);
        File.Delete(timingsPath);
        await File.WriteAllTextAsync(
                linesPath,
                JsonSerializer.Serialize([.. phase.Lines.Select(l => l.Text)], TtsBenchCorpusJsonContext.Default.ListString)
            )
            .ConfigureAwait(false);
        string script = Path.Combine(repoRoot, "tools", "Yaat.SpeechSandbox", "tts-bench", "kitten2_bench.py");
        string? error = await RunPythonAsync(
                script,
                work,
                [linesPath, rawDir, timingsPath, string.Join(",", phase.Voices.Select(v => v.Label))],
                phase
            )
            .ConfigureAwait(false);
        if (error is not null)
        {
            return NotRun(model, $"not run: {error}", null);
        }
        try
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(timingsPath).ConfigureAwait(false));
            return Kitten2Result(model, phase, doc.RootElement, rawDir, Path.Combine(tmpRoot, "clips"));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidDataException)
        {
            return NotRun(model, $"not run: {timingsPath}: {ex.Message}", null);
        }
    }

    private static TtsBenchModelResult Kitten2Result(TtsBenchModel model, TtsBenchPhase phase, JsonElement root, string rawDir, string clipsRoot)
    {
        var clips = new List<TtsBenchClip>();
        foreach (JsonElement c in root.GetProperty("clips").EnumerateArray())
        {
            string voice = c.GetProperty("voice").GetString() ?? throw new InvalidDataException("A Kitten-TTS 2 clip has a null voice.");
            int scriptLine = c.GetProperty("line").GetInt32();
            float[] samples = ReadWav(Path.Combine(rawDir, voice, $"{scriptLine:00}.wav"), out int rate);
            JsonElement first = c.GetProperty("firstAudioSeconds");
            var timing = new SynthTiming(
                samples,
                rate,
                first.ValueKind == JsonValueKind.Null ? null : first.GetDouble() * 1000,
                c.GetProperty("synthSeconds").GetDouble() * 1000
            );
            clips.Add(ToClip(phase.SaveClips ? clipsRoot : null, model.Id, voice, phase.Lines[scriptLine - 1].Index, timing));
        }
        string runtimePackage = root.GetProperty("package").GetString() ?? throw new InvalidDataException("Kitten-TTS 2 timings name no package.");
        return new TtsBenchModelResult(
            model.Id,
            model.Name,
            model.Runtime.ToString(),
            model.License,
            null,
            null,
            null,
            phase.Threads,
            root.GetProperty("loadSeconds").GetDouble(),
            root.GetProperty("warmupSeconds").GetDouble() * 1000,
            root.GetProperty("sampleRate").GetInt32(),
            [.. phase.Voices.Select(v => v.Label)],
            $"Runs on the `{runtimePackage}` package (llama.cpp-based, outside sherpa-onnx); first audio is the first streamed chunk. "
                + "The runtime has no speed control, so it speaks at its native rate, not 1.1. Download and disk size are not measured "
                + "(the package fetches the weights into its own cache).",
            clips,
            [],
            TtsBenchVerdict.Pass
        );
    }

    /// <summary>
    /// Runs the Kitten-TTS 2 script in <paramref name="work"/>, with its weights cached under
    /// <c>YaatPaths.Combine("tts-bench", "kitten2")</c>; <paramref name="paths"/> is the lines file, the raw clip folder,
    /// the timings file and the voice list. Returns null on success or the error's last stderr line.
    /// </summary>
    private static async Task<string?> RunPythonAsync(string script, string work, string[] paths, TtsBenchPhase phase)
    {
        string cacheDir = YaatPaths.Combine("tts-bench", "kitten2");
        string[] args =
        [
            "run",
            "--no-project",
            "--with",
            "kitten_tts_cpp",
            "python",
            "-X",
            "utf8",
            "-I",
            script,
            "--lines",
            paths[0],
            "--out-dir",
            paths[1],
            "--timings",
            paths[2],
            "--voices",
            paths[3],
            "--threads",
            phase.Threads.ToString(CultureInfo.InvariantCulture),
            "--cache-dir",
            cacheDir,
        ];
        ProcessStartInfo psi = StartInfo("uv", args, work);
        psi.Environment["HF_HOME"] = cacheDir;
        ProcessOutcome outcome = await RunProcessAsync(psi, phase.Deadline).ConfigureAwait(false);
        if (outcome.TimedOut)
        {
            return $"timed out after {phase.Deadline.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)} s";
        }
        return outcome.ExitCode == 0 && File.Exists(paths[2]) ? null : outcome.LastError ?? $"exit code {outcome.ExitCode}";
    }

    private static ProcessStartInfo StartInfo(string fileName, IEnumerable<string> args, string workingDirectory)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
        };
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        return psi;
    }

    /// <summary>
    /// Runs a process, echoing its output indented, and kills its whole tree once <paramref name="deadline"/> passes.
    /// Returns its exit code, whether it timed out, and its first and last non-empty stderr lines.
    /// </summary>
    private static async Task<ProcessOutcome> RunProcessAsync(ProcessStartInfo psi, TimeSpan deadline)
    {
        using Process process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {psi.FileName}.");
        var errors = new List<string>();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                Console.WriteLine($"  {e.Data}");
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                Console.Error.WriteLine($"  {e.Data}");
                lock (errors)
                {
                    errors.Add(e.Data.Trim());
                }
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        bool timedOut = false;
        using (var cts = new CancellationTokenSource(deadline))
        {
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine($"  {psi.FileName} passed its {deadline.TotalSeconds:F0} s deadline; killing its process tree");
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
                timedOut = true;
            }
        }
        lock (errors)
        {
            return new ProcessOutcome(process.ExitCode, timedOut, errors.FirstOrDefault(), errors.LastOrDefault());
        }
    }
}
