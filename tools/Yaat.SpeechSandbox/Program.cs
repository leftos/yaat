using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Yaat.Client.Services;
using Yaat.Sim.Speech;

namespace Yaat.SpeechSandbox;

/// <summary>
/// Entry point for Yaat.SpeechSandbox. Dispatches on the first command-line argument:
/// <list type="bullet">
///   <item><description><c>--pipeline &lt;wav&gt; [&lt;wav&gt; ...]</c> — runs each WAV through the full
///     production speech pipeline (Whisper STT → AtcNumberParser.NormalizeDigits → rule mapper
///     → LLM mapper) and prints each stage's output side-by-side. Console mode; no GUI window.
///     Uses the same services the main app uses, so a successful run here means the same audio
///     would resolve cleanly via PTT in YAAT itself.</description></item>
///   <item><description><c>--lmkit-stt &lt;wav&gt; [&lt;model-id&gt; ...]</c> — transcribes a WAV through one or
///     more LM-Kit Whisper variants and reflects over <c>LMKit.Speech.SpeechToText</c> to enumerate
///     its public surface. Useful for iterating on Whisper model selection and confirming
///     <c>SpeechToText.Prompt</c> biasing behavior on captured PTT clips.</description></item>
///   <item><description><c>--lmkit-models</c> — dumps every entry in
///     <c>LMKit.Model.ModelCard.GetPredefinedModelCards()</c> with capabilities, file size,
///     license, and <c>IsLocallyAvailable</c>. Used to decide what to expose in
///     <c>LmKitModelCatalog</c>.</description></item>
///   <item><description><c>--lmkit-gpus</c> — enumerates <c>LMKit.Hardware.Gpu.GpuDeviceInfo.Devices</c>
///     so we can see what LM-Kit detects on the local machine. Useful for diagnosing
///     "model didn't load on GPU" reports.</description></item>
///   <item><description><c>--yaat-catalog</c> — dumps the FILTERED Whisper + LLM catalogs as they'll
///     appear in the Settings picker (after <c>LmKitModelCatalog.Build*Catalog()</c> filters and
///     recommended-tier annotation). Sanity check before shipping changes to the catalog filter
///     logic.</description></item>
///   <item><description><c>--llm-probe &lt;transcript&gt;</c> — runs a single transcript through
///     <c>LocalLlmCommandMapper</c> (after <c>AtcNumberParser.NormalizeDigits</c>) and prints the
///     canonical command. Used for "what would the LLM produce here?" investigations during
///     rule-engine triage. Loads the model identified by <c>LMKIT_TEST_MODEL</c> env var
///     (default <c>qwen3.5:4b</c>), NOT the user's saved preference, so the probe is reproducible
///     across machines.</description></item>
///   <item><description><c>--ouroboros &lt;corpus.json&gt; [--out-dir &lt;dir&gt;]</c> — round-trip harness:
///     for each canonical command in the corpus, build a pilot readback via
///     <c>PilotResponder.BuildReadback</c>, synthesize it with Piper, run the resulting audio
///     through the full STT pipeline (Whisper → rule mapper → LLM fallback), and compare the
///     recovered canonical to the input. Writes a markdown report plus per-case WAV and
///     stage-by-stage text dumps to <c>.tmp/ouroboros-{timestamp}/</c>. Used to surface STT
///     coverage gaps deterministically against a curated corpus.</description></item>
///   <item><description>(no args) — launches the GUI sandbox window (App.axaml / MainWindow.axaml).
///     Interactive mode for recording, replaying, and iterating on Whisper / LLM prompts.</description></item>
/// </list>
/// All console modes share LM-Kit licensing setup via <see cref="LmKitLicense.Initialize"/>,
/// which resolves the key from LMKIT_LICENSE_KEY or the solution-root .env file and falls back
/// to Community Edition. Backend selection (CUDA / Vulkan / CPU) is owned by LM-Kit at model
/// load time.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        LmKitLicense.Initialize();

        if (args.Length > 0)
        {
            switch (args[0])
            {
                case "--pipeline":
                    return RunPipelineMode(args[1..]).GetAwaiter().GetResult();
                case "--lmkit-stt":
                    return RunLmKitSttMode(args[1..]);
                case "--lmkit-models":
                    return RunLmKitModelsMode();
                case "--lmkit-gpus":
                    return RunLmKitGpusMode();
                case "--yaat-catalog":
                    return RunYaatCatalogMode();
                case "--llm-probe":
                    return RunLlmProbeMode(args[1..]).GetAwaiter().GetResult();
                case "--ouroboros":
                    return OuroborosRunner.RunAsync(args[1..]).GetAwaiter().GetResult();
            }
        }

        // Default: launch the interactive GUI sandbox.
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();

    /// <summary>
    /// Console E2E pipeline: walks each WAV file through every stage of the production speech
    /// pipeline and prints what came out at each step. Use this to diagnose where a real PTT
    /// recording got mangled — was it Whisper, was it the rule engine, did the LLM fallback help?
    /// </summary>
    /// <param name="wavPaths">List of WAV file paths to process. Each must be 16 kHz mono int16 PCM (the format AudioCaptureService records and WhisperSttEngine expects).</param>
    private static async Task<int> RunPipelineMode(string[] wavPaths)
    {
        if (wavPaths.Length == 0)
        {
            Console.Error.WriteLine("Usage: Yaat.SpeechSandbox --pipeline <wav-path> [<wav-path> ...]");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Each WAV is fed through STT → NormalizeDigits → rule mapper → LLM mapper.");
            Console.Error.WriteLine("Both mappers run regardless of order, so you see the side-by-side comparison.");
            return 1;
        }

        var prefs = new UserPreferences();
        if (!prefs.SpeechEnabled)
        {
            Console.WriteLine(
                "Note: speech recognition is disabled in user preferences. The pipeline still runs in this tool because we instantiate the services directly."
            );
            Console.WriteLine();
        }

        Console.WriteLine($"Whisper model: {prefs.WhisperModelSize}");
        Console.WriteLine($"LLM model:     {prefs.LlmModelPath}");
        Console.WriteLine();

        using var stt = new WhisperSttEngine(prefs);
        using var llm = new LocalLlmService(new PreferencesLlmRuntimeConfig(prefs));
        var ruleMapper = new PhraseologyCommandMapper();
        var llmMapper = new LocalLlmCommandMapper(llm);

        if (!stt.IsConfigured)
        {
            Console.Error.WriteLine(
                $"FATAL: Whisper STT is not configured (model='{prefs.WhisperModelSize}'). Open Yaat.Client → Settings → Speech and download the model first."
            );
            return 2;
        }
        if (!llm.IsConfigured)
        {
            Console.Error.WriteLine(
                $"FATAL: LLM is not configured (model='{prefs.LlmModelPath}'). Open Yaat.Client → Settings → Speech and download the model first."
            );
            return 2;
        }

        var anyFailed = false;
        foreach (var path in wavPaths)
        {
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"=== {path} ===");
                Console.Error.WriteLine("  FILE NOT FOUND");
                Console.Error.WriteLine();
                anyFailed = true;
                continue;
            }

            Console.WriteLine($"=== {path} ===");
            float[] samples;
            try
            {
                samples = WavHeader.ReadPcm16(path);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  FAILED to read WAV: {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine();
                anyFailed = true;
                continue;
            }
            var durationSec = samples.Length / (double)AudioCaptureService.SampleRate;
            Console.WriteLine($"  Duration:  {durationSec:F2}s ({samples.Length:N0} samples @ {AudioCaptureService.SampleRate} Hz)");

            // --- Stage 1: Whisper STT ---
            var sttSw = Stopwatch.StartNew();
            string? transcript;
            try
            {
                transcript = await stt.TranscribeAsync(samples, WhisperBiasingPrompt.Default, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  STT FAILED: {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine();
                anyFailed = true;
                continue;
            }
            sttSw.Stop();
            Console.WriteLine($"  STT ({sttSw.ElapsedMilliseconds, 5} ms): {(transcript is null ? "<null>" : "\"" + transcript + "\"")}");
            if (string.IsNullOrWhiteSpace(transcript))
            {
                Console.WriteLine("  (empty transcript — no further stages)");
                Console.WriteLine();
                continue;
            }

            // --- Stage 2: AtcNumberParser.NormalizeDigits (digit normalization + runway collapse + comma stripping + filler insulation) ---
            var normalized = AtcNumberParser.NormalizeDigits(transcript);
            Console.WriteLine($"  Normalize:        \"{normalized}\"");

            // --- Stage 3: rule engine mapper ---
            var ruleSw = Stopwatch.StartNew();
            var ruleResult = await ruleMapper.MapAsync(normalized, MapContext.Empty, CancellationToken.None).ConfigureAwait(false);
            ruleSw.Stop();
            var ruleCanonical = ruleResult?.CanonicalCommand ?? "<null>";
            var ruleCallsign = ruleResult?.Callsign ?? "<none>";
            Console.WriteLine($"  Rule ({ruleSw.ElapsedMilliseconds, 5} ms): callsign={ruleCallsign} canonical={ruleCanonical}");

            // --- Stage 4: LLM mapper (always run, for side-by-side comparison) ---
            var llmSw = Stopwatch.StartNew();
            var llmResult = await llmMapper.MapAsync(normalized, MapContext.Empty, CancellationToken.None).ConfigureAwait(false);
            llmSw.Stop();
            var llmCanonical = llmResult?.CanonicalCommand ?? "<null>";
            Console.WriteLine($"  LLM  ({llmSw.ElapsedMilliseconds, 5} ms): canonical={llmCanonical}");

            // --- Verdict line ---
            // Mirrors the production decision: rule mapper wins if it produced anything, otherwise
            // the LLM is the fallback. This is the canonical command that would land in the YAAT
            // command input box for this PTT press.
            var winner =
                ruleCanonical != "<null>" ? $"RULE → {ruleCanonical}"
                : llmCanonical != "<null>" ? $"LLM  → {llmCanonical}"
                : "BOTH FAILED";
            Console.WriteLine($"  Verdict:   {winner}");
            Console.WriteLine();
        }

        return anyFailed ? 1 : 0;
    }

    /// <summary>
    /// Dumps the FILTERED YAAT catalogs (Whisper + LLM) as they'll appear in the Settings picker.
    /// Use this to sanity-check the filter logic in <c>LmKitModelCatalog</c> without running the
    /// full UI.
    /// </summary>
    private static int RunYaatCatalogMode()
    {
        Console.WriteLine("=== YAAT Whisper catalog ===");
        var whisper = LmKitModelCatalog.BuildWhisperCatalog();
        Console.WriteLine($"  {whisper.Count} entries");
        foreach (var e in whisper)
        {
            var cached = e.IsLocallyAvailable ? " [cached]" : "";
            var tier = e.Tier == LmKitModelTier.Recommended ? " ★" : "";
            Console.WriteLine($"    {e.ModelId, -30} {e.ApproxSizeMb, 6} MB  {e.DisplayName}{tier}{cached}");
        }
        Console.WriteLine();
        Console.WriteLine("=== YAAT LLM catalog ===");
        var llm = LmKitModelCatalog.BuildLlmCatalog();
        Console.WriteLine($"  {llm.Count} entries");
        foreach (var e in llm)
        {
            var cached = e.IsLocallyAvailable ? " [cached]" : "";
            var tier = e.Tier == LmKitModelTier.Recommended ? " ★" : "";
            var gpu = e.GpuRecommended ? " [GPU]" : "";
            Console.WriteLine($"    {e.ModelId, -30} {e.ApproxSizeMb, 6} MB  {e.DisplayName}{tier}{gpu}{cached}");
        }
        return 0;
    }

    /// <summary>
    /// Enumerates LM-Kit's predefined model catalog. Dumps every <c>ModelCard</c> with the
    /// metadata YAAT cares about (ModelID, capabilities, parameter count, file size, license,
    /// local-availability). Used when LM-Kit publishes a new model bundle and we need to decide
    /// what to expose in <c>LmKitModelCatalog</c>.
    /// </summary>
    private static int RunLmKitModelsMode()
    {
        Console.WriteLine("=== LM-Kit predefined model catalog ===");
        var cards = LMKit.Model.ModelCard.GetPredefinedModelCards();
        Console.WriteLine($"  Total: {cards.Count}");
        Console.WriteLine();
        foreach (
            var c in cards.OrderBy(c => c.Capabilities.HasFlag(LMKit.Model.ModelCapabilities.SpeechToText) ? 0 : 1).ThenBy(c => c.ParameterCount)
        )
        {
            Console.WriteLine($"  {c.ModelID}");
            Console.WriteLine($"    Name:           {c.ModelName}");
            Console.WriteLine($"    ShortName:      {c.ShortModelName}");
            Console.WriteLine($"    Publisher:      {c.Publisher}");
            Console.WriteLine($"    License:        {c.License}");
            Console.WriteLine($"    Capabilities:   {c.Capabilities}");
            Console.WriteLine($"    Architecture:   {c.Architecture}");
            Console.WriteLine($"    Parameters:     {c.ParameterCount / 1_000_000.0:F1} M");
            Console.WriteLine($"    FileSize:       {c.FileSize / (1024 * 1024)} MB");
            Console.WriteLine($"    ContextLength:  {c.ContextLength}");
            Console.WriteLine($"    Quantization:   {c.QuantizationPrecision}");
            Console.WriteLine($"    LocallyAvail:   {c.IsLocallyAvailable}");
            Console.WriteLine($"    LocalPath:      {c.LocalPath}");
            Console.WriteLine();
        }
        return 0;
    }

    /// <summary>
    /// Enumerates <c>LMKit.Hardware.Gpu.GpuDeviceInfo.Devices</c> so we can see what LM-Kit
    /// detects on the local machine. Useful for diagnosing "model didn't load on GPU" reports.
    /// </summary>
    private static int RunLmKitGpusMode()
    {
        Console.WriteLine("=== LM-Kit GPU enumeration ===");
        var devices = LMKit.Hardware.Gpu.GpuDeviceInfo.Devices;
        Console.WriteLine($"  Detected device count: {devices.Count}");
        for (var i = 0; i < devices.Count; i++)
        {
            var d = devices[i];
            Console.WriteLine($"  [{i}] {d.DeviceName}");
            Console.WriteLine($"      Description: {d.DeviceDescription}");
            Console.WriteLine($"      DeviceType:  {d.DeviceType}");
            Console.WriteLine($"      DeviceNumber: {d.DeviceNumber}");
            Console.WriteLine($"      TotalMemory: {d.TotalMemorySize / (1024 * 1024)} MB");
            Console.WriteLine($"      FreeMemory:  {d.FreeMemorySize / (1024 * 1024)} MB");
        }
        return 0;
    }

    /// <summary>
    /// One-shot LLM probe: runs a single transcript through <c>LocalLlmCommandMapper</c>
    /// directly, bypassing the rule engine. Use this to see what the LLM would produce when the
    /// rule engine fails or returns a lossy match. Mimics the production path by normalizing the
    /// transcript through <c>AtcNumberParser.NormalizeDigits</c> first — that's what
    /// <c>SpeechRecognitionService</c> does before handing the text to the LLM mapper.
    /// </summary>
    /// <remarks>
    /// Loads the model identified by <c>LMKIT_TEST_MODEL</c> (default <c>qwen3.5:4b</c>) via
    /// <see cref="ProbeLlmRuntimeConfig"/>, NOT the user's saved preference. This keeps the probe
    /// reproducible across machines and matches the test fixture's model.
    /// </remarks>
    private static async Task<int> RunLlmProbeMode(string[] args)
    {
        var transcript = args.Length > 0 ? args[0] : "okay we'll enter right downwind for runway 28 right at november niner 225 lima";
        var modelSource = Environment.GetEnvironmentVariable("LMKIT_TEST_MODEL") ?? "qwen3.5:4b";

        var normalized = AtcNumberParser.NormalizeDigits(transcript);
        Console.WriteLine($"Transcript: \"{transcript}\"");
        Console.WriteLine($"Normalized: \"{normalized}\"");
        Console.WriteLine($"Loading model: {modelSource}");
        var probeConfig = new ProbeLlmRuntimeConfig(modelSource, gpuLayers: -1);
        using var probeService = new LocalLlmService(probeConfig);
        var probeMapper = new LocalLlmCommandMapper(probeService);

        var probeSw = Stopwatch.StartNew();
        var probeResult = await probeMapper.MapAsync(normalized, MapContext.Empty, CancellationToken.None).ConfigureAwait(false);
        probeSw.Stop();

        Console.WriteLine($"Time: {probeSw.ElapsedMilliseconds} ms");
        Console.WriteLine($"Canonical: {probeResult?.CanonicalCommand ?? "<null>"}");
        Console.WriteLine($"Callsign:  {probeResult?.Callsign ?? "<null>"}");
        return 0;
    }

    /// <summary>
    /// LM-Kit SpeechToText evaluation probe — does NOT touch any production code paths.
    /// Loads one or more Whisper variants via LM-Kit's <c>LM.LoadFromModelID</c>, transcribes a
    /// captured PTT clip, and reflects over the <c>SpeechToText</c> surface to discover any
    /// initial-prompt / context-bias parameter equivalent to Whisper.net's <c>WithPrompt</c>.
    /// This is the gate before swapping <c>WhisperSttEngine</c> onto LM-Kit (already done) and
    /// remains useful for evaluating new Whisper model bundles.
    /// </summary>
    /// <remarks>
    /// Default model list is <c>whisper-base</c>, <c>whisper-medium</c>, <c>whisper-large-turbo3</c>
    /// — matches the LM-Kit single_turn_chat sample's published model identifiers. The first run
    /// downloads each model into LM-Kit's default cache (~%LOCALAPPDATA%/LM-Kit/Models/), so first
    /// invocations are slow; subsequent runs reuse the cache.
    /// </remarks>
    private static int RunLmKitSttMode(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: --lmkit-stt <wav-path> [<model-id> ...]");
            Console.Error.WriteLine("Default model list: whisper-base whisper-medium whisper-large-turbo3");
            return 1;
        }

        var wavPath = args[0];
        if (!File.Exists(wavPath))
        {
            Console.Error.WriteLine($"WAV not found: {wavPath}");
            return 1;
        }

        var modelIds = args.Length > 1 ? args[1..] : ["whisper-base", "whisper-medium", "whisper-large-turbo3"];

        // Reflect over SpeechToText once so we know what configuration knobs exist BEFORE we burn
        // download/load time on three models. The whole point of the probe is the biasing question:
        // if no relevant property exists, we know the answer immediately.
        ReflectSpeechToTextSurface();

        // Run transcription on each model.
        foreach (var modelId in modelIds)
        {
            ProbeOneWhisperModel(modelId, wavPath);
        }

        return 0;
    }

    /// <summary>
    /// Dumps every public instance property and declared method on
    /// <c>LMKit.Speech.SpeechToText</c>, then highlights names that look like
    /// initial-prompt / context-bias knobs.
    /// </summary>
    private static void ReflectSpeechToTextSurface()
    {
        Console.WriteLine("=== Reflecting over LMKit.Speech.SpeechToText ===");
        var sttType = typeof(LMKit.Speech.SpeechToText);
        var props = sttType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Console.WriteLine($"  {props.Length} public instance properties:");
        foreach (var prop in props.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            Console.WriteLine($"    {prop.PropertyType.Name} {prop.Name} (read={prop.CanRead} write={prop.CanWrite})");
        }
        Console.WriteLine();
        var methods = sttType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .ToList();
        Console.WriteLine($"  {methods.Count} declared public instance methods:");
        foreach (var m in methods.OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            var sig = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
            Console.WriteLine($"    {m.ReturnType.Name} {m.Name}({sig})");
        }
        Console.WriteLine();

        // Spot-check: any property name that looks like an initial-prompt / biasing knob?
        var biasCandidates = props
            .Where(p =>
                p.Name.Contains("Prompt", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("Bias", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("Context", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("Hint", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("Vocab", StringComparison.OrdinalIgnoreCase)
            )
            .ToList();
        if (biasCandidates.Count > 0)
        {
            Console.WriteLine("  ✓ Possible biasing-related properties:");
            foreach (var p in biasCandidates)
            {
                Console.WriteLine($"    {p.PropertyType.Name} {p.Name}");
            }
        }
        else
        {
            Console.WriteLine("  ✗ No property name suggesting initial-prompt / context biasing.");
            Console.WriteLine("    (Will need to follow up with the API docs or sample code if reflection misses an interface.)");
        }
        Console.WriteLine();
    }

    /// <summary>
    /// Loads one Whisper model via <c>LM.LoadFromModelID</c>, transcribes the WAV three times
    /// (cold, warm, biased), and prints timings + segment text. Per-model section so the outer
    /// loop in <see cref="RunLmKitSttMode"/> can iterate over the model list cleanly.
    /// </summary>
    private static void ProbeOneWhisperModel(string modelId, string wavPath)
    {
        Console.WriteLine($"=== Model: {modelId} ===");
        var downloading = false;
        var loadSw = Stopwatch.StartNew();
        LMKit.Model.LM model;
        try
        {
            model = LMKit.Model.LM.LoadFromModelID(
                modelId,
                downloadingProgress: (path, length, read) =>
                {
                    downloading = true;
                    if (length.HasValue)
                    {
                        Console.Write($"\r  Downloading {(double)read / length.Value * 100:F1}%   ");
                    }
                    return true;
                },
                loadingProgress: progress =>
                {
                    if (downloading)
                    {
                        Console.WriteLine();
                        downloading = false;
                    }
                    Console.Write($"\r  Loading {progress * 100:F0}%   ");
                    return true;
                }
            );
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"  FAILED to load: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine();
            return;
        }
        loadSw.Stop();
        Console.WriteLine();
        Console.WriteLine($"  Loaded in {loadSw.ElapsedMilliseconds} ms — model.Name={model.Name}");

        try
        {
            var stt = new LMKit.Speech.SpeechToText(model);
            var segments = new List<string>();
            stt.OnNewSegment += (_, e) => segments.Add(e.Segment.ToString() ?? string.Empty);

            var wave = new LMKit.Media.Audio.WaveFile(wavPath);
            Console.WriteLine($"  Audio: duration={wave.Duration:mm\\:ss\\.ff}");

            // First call (cold post-load) — no prompt, baseline transcript quality.
            var sttColdSw = Stopwatch.StartNew();
            stt.Transcribe(wave);
            sttColdSw.Stop();
            Console.WriteLine($"  [no prompt] Cold transcribe: {sttColdSw.ElapsedMilliseconds} ms ({segments.Count} segments)");
            foreach (var seg in segments)
            {
                Console.WriteLine($"    \"{seg.Trim()}\"");
            }

            // Second call (warm — same model, same audio) to measure pure inference time.
            segments.Clear();
            var sttWarmSw = Stopwatch.StartNew();
            stt.Transcribe(wave);
            sttWarmSw.Stop();
            Console.WriteLine($"  [no prompt] Warm transcribe: {sttWarmSw.ElapsedMilliseconds} ms ({segments.Count} segments)");

            // Third call — with the production WhisperBiasingPrompt (NATO + phonetic numbers +
            // every literal token from PhraseologyRules.All). This is what real PTT calls send.
            segments.Clear();
            stt.Prompt = WhisperBiasingPrompt.Default;
            var sttBiasedSw = Stopwatch.StartNew();
            stt.Transcribe(wave);
            sttBiasedSw.Stop();
            Console.WriteLine($"  [WhisperBiasingPrompt] Transcribe: {sttBiasedSw.ElapsedMilliseconds} ms ({segments.Count} segments)");
            foreach (var seg in segments)
            {
                Console.WriteLine($"    \"{seg.Trim()}\"");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  TRANSCRIBE FAILED: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            model.Dispose();
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Lightweight <see cref="ILlmRuntimeConfig"/> for <c>--llm-probe</c>. Carries an explicit
    /// model source string and GPU layer count so the probe can target a known test model
    /// (overridable via <c>LMKIT_TEST_MODEL</c>) regardless of what the user has saved in their
    /// preferences. Without this we'd silently rebind to <c>PreferencesLlmRuntimeConfig</c> and
    /// the probe would no longer be reproducible across machines.
    /// </summary>
    private sealed class ProbeLlmRuntimeConfig : ILlmRuntimeConfig
    {
        public ProbeLlmRuntimeConfig(string modelPath, int gpuLayers)
        {
            ModelPath = modelPath;
            GpuLayers = gpuLayers;
        }

        public string ModelPath { get; }
        public int GpuLayers { get; }
    }
}
