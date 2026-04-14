// LLM prompt iteration harness.
//
// Run with:  dotnet run --project tools/Yaat.Scratch
//
// Loads the real LocalLlmService + LocalLlmCommandMapper with the shared test GGUF, auto-discovers
// CUDA 12 via the same logic as LlmCudaFixture, then feeds a batch of ATC transcripts through the
// full pipeline. For each case we print:
//   - the raw LLM output (what the model actually emitted, pre-validation),
//   - the NormalizeOutput result (null = rejected, string = accepted canonical),
//   - the MapResult.CanonicalCommand (if the mapper returned a non-null result).
//
// This lets us iterate on system prompt / sampling / validator without xunit overhead.

using System.Diagnostics;
using System.Reflection;
using System.Text;
using LMKit.Licensing;
using Yaat.Client.Services;
using Yaat.Sim.Speech;

// LLM iteration harness uses an LM-Kit model source, not a file path. Override via env var
// when iterating against a different model. Default matches LlmCudaFixture so the fixture and
// scratch run the same model.
var modelSource = Environment.GetEnvironmentVariable("LMKIT_TEST_MODEL") ?? "qwen3.5:4b";

LicenseManager.SetLicenseKey("");

// Dump the FILTERED YAAT catalogs (Whisper + LLM) as they'll appear in the Settings picker.
// Use this to sanity-check the filter logic in LmKitModelCatalog without running the full UI.
if (args.Length > 0 && args[0] == "--yaat-catalog")
{
    LMKit.Licensing.LicenseManager.SetLicenseKey("");
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

// Enumerate LM-Kit's predefined model catalog. Dumps every ModelCard with the metadata YAAT
// cares about: ModelID, capabilities, parameter count, file size, license, local-availability
// status. Used to decide what to expose in LmKitModelCatalog.
if (args.Length > 0 && args[0] == "--lmkit-models")
{
    LMKit.Licensing.LicenseManager.SetLicenseKey("");
    Console.WriteLine("=== LM-Kit predefined model catalog ===");
    var cards = LMKit.Model.ModelCard.GetPredefinedModelCards();
    Console.WriteLine($"  Total: {cards.Count}");
    Console.WriteLine();
    foreach (var c in cards.OrderBy(c => c.Capabilities.HasFlag(LMKit.Model.ModelCapabilities.SpeechToText) ? 0 : 1).ThenBy(c => c.ParameterCount))
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

// Quick GPU enumeration probe — tells us what LMKit.Hardware.Gpu.GpuDeviceInfo.Devices returns
// on this machine, so we can shape the Settings UI around real values.
if (args.Length > 0 && args[0] == "--lmkit-gpus")
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

// LM-Kit SpeechToText evaluation probe — does NOT touch any production code paths.
// Loads one or more Whisper variants via LM-Kit's LM.LoadFromModelID, transcribes a captured
// PTT clip, and reflects over the SpeechToText surface to discover any initial-prompt /
// context-bias parameter equivalent to Whisper.net's `WithPrompt`. This is the gate before
// we commit to swapping WhisperSttEngine onto LM-Kit.
//
// Usage:
//   dotnet run --project tools/Yaat.Scratch -- --lmkit-stt <wav-path> [<model-id> [<model-id> ...]]
//
// Default model list: whisper-base, whisper-medium, whisper-large-turbo3 — matches the
// LM-Kit single_turn_chat sample's published model identifiers. The first run downloads each
// model into LM-Kit's default cache (~%LOCALAPPDATA%/LM-Kit/Models/), so first invocations are
// slow; subsequent runs reuse the cache.
if (args.Length > 0 && args[0] == "--lmkit-stt")
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: --lmkit-stt <wav-path> [<model-id> ...]");
        Console.Error.WriteLine("Default model list: whisper-base whisper-medium whisper-large-turbo3");
        return 1;
    }

    var wavPath = args[1];
    if (!File.Exists(wavPath))
    {
        Console.Error.WriteLine($"WAV not found: {wavPath}");
        return 1;
    }

    var modelIds = args.Length > 2 ? args[2..] : ["whisper-base", "whisper-medium", "whisper-large-turbo3"];

    LMKit.Licensing.LicenseManager.SetLicenseKey("");

    // Reflect over SpeechToText once so we know what configuration knobs exist BEFORE we burn
    // download/load time on three models. The whole point of the probe is the biasing question:
    // if no relevant property exists, we know the answer immediately.
    Console.WriteLine("=== Reflecting over LMKit.Speech.SpeechToText ===");
    var sttType = typeof(LMKit.Speech.SpeechToText);
    var props = sttType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
    Console.WriteLine($"  {props.Length} public instance properties:");
    foreach (var prop in props.OrderBy(p => p.Name, StringComparer.Ordinal))
    {
        Console.WriteLine($"    {prop.PropertyType.Name} {prop.Name} (read={prop.CanRead} write={prop.CanWrite})");
    }
    Console.WriteLine();
    var methods = sttType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(m => !m.IsSpecialName).ToList();
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

    // Run transcription on each model.
    foreach (var modelId in modelIds)
    {
        Console.WriteLine($"=== Model: {modelId} ===");
        bool downloading = false;
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
            continue;
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

    return 0;
}

// Quick probe of audio input device enumeration.
if (args.Length > 0 && args[0] == "--list-audio")
{
    Console.WriteLine("=== Enumerating audio input devices ===");
    var prefs = new UserPreferences();
    using var audio = new AudioCaptureService(prefs);
    var devices = audio.ListInputDevices();
    foreach (var (idx, name) in devices)
    {
        Console.WriteLine($"  [{idx}] {name}");
    }

    Console.WriteLine($"Total: {devices.Count}");
    return 0;
}

// (Legacy LLamaSharp + Whisper.net GPU runtime probe modes removed in the LM-Kit swap. LM-Kit
// owns backend selection internally so there's no equivalent to GpuRuntimeDownloader /
// NativeLibraryConfig / Whisper.net RuntimeOptions to probe. If you need to inspect LM-Kit's
// backend selection, use the --lmkit-stt mode above.)

// Real LocalLlmService via a lightweight ILlmRuntimeConfig. -1 GPU layers = let LM-Kit pick.
var config = new ScratchLlmConfig(modelSource, gpuLayers: -1);
using var service = new LocalLlmService(config);
var mapper = new LocalLlmCommandMapper(service);

// Grab the internal BuildUserPrompt / NormalizeOutput via reflection so we can step-by-step the
// pipeline. We deliberately SKIP the mapper's BuildSystemPrompt and build our own from
// PhraseologyRules so we can iterate on prompt strategy without touching production code.
var mapperType = typeof(LocalLlmCommandMapper);
var buildUserMethod = mapperType.GetMethod("BuildUserPrompt", BindingFlags.NonPublic | BindingFlags.Static)!;
var normalizeMethod = mapperType.GetMethod("NormalizeOutput", BindingFlags.NonPublic | BindingFlags.Static)!;
var systemPrompt = PromptBuilder.BuildRuleDerivedSystemPrompt();

Console.WriteLine("=== System prompt (first 20 lines) ===");
var lines = systemPrompt.Split('\n');
for (var i = 0; i < Math.Min(20, lines.Length); i++)
{
    Console.WriteLine($"  {lines[i]}");
}

Console.WriteLine($"  ... ({lines.Length} total lines, {systemPrompt.Length} chars)");
Console.WriteLine();

// Warm up the model so first-case timing isn't dominated by load.
Console.WriteLine("=== Warming up model ===");
var warmSw = Stopwatch.StartNew();
_ = await service.GenerateAsync("Short answer.", "hi", gbnfGrammar: null, CancellationToken.None);
Console.WriteLine($"  Warm-up: {warmSw.ElapsedMilliseconds} ms");
Console.WriteLine();

// Test cases — transcript + exact expected canonical (or "REJECT" for null case).
// Each case asserts the model produces the EXACT right verb + arg, not just "contains X".
var cases = new (string Transcript, MapContext Context, string Expected)[]
{
    ("climb and maintain five thousand", MapContext.Empty, "CM 5000"),
    ("descend and maintain three thousand", MapContext.Empty, "DM 3000"),
    ("fly heading two seven zero", MapContext.Empty, "FH 270"),
    ("turn right heading zero nine zero", MapContext.Empty, "TR 090"),
    ("squawk seven five zero zero", MapContext.Empty, "SQ 7500"),
    ("direct to CEPIN", new MapContext([], ["CEPIN", "SUNOL"]), "DCT CEPIN"),
    ("cleared for takeoff", MapContext.Empty, "CTO"),
    ("reduce speed to two three zero", MapContext.Empty, "SPD 230"),
    ("good morning how are you doing today", MapContext.Empty, "REJECT"),
};

Console.WriteLine("=== Running test cases ===");
Console.WriteLine();

var totalSw = Stopwatch.StartNew();
var passed = 0;
var failed = 0;
foreach (var (transcript, ctx, expected) in cases)
{
    Console.WriteLine($"--- \"{transcript}\"");
    Console.WriteLine($"    Expected: {expected}");

    // Call mapper.MapAsync directly — same path the integration tests use. This proves the
    // scratch and test harness are exercising exactly the same production code.
    var sw = Stopwatch.StartNew();
    var result = await mapper.MapAsync(transcript, ctx, CancellationToken.None);
    sw.Stop();

    var normalized = result?.CanonicalCommand;
    Console.WriteLine($"    Time: {sw.ElapsedMilliseconds} ms");
    Console.WriteLine($"    MapResult: {normalized ?? "<null>"}");

    bool pass;
    if (expected == "REJECT")
    {
        pass = normalized is null;
    }
    else
    {
        pass = string.Equals(normalized, expected, StringComparison.Ordinal);
    }

    Console.WriteLine($"    {(pass ? "PASS" : "FAIL")}");
    Console.WriteLine();

    if (pass)
    {
        passed++;
    }
    else
    {
        failed++;
    }
}

totalSw.Stop();
Console.WriteLine($"=== Results: {passed} passed, {failed} failed in {totalSw.ElapsedMilliseconds} ms ===");
return failed > 0 ? 1 : 0;

internal sealed class ScratchLlmConfig : ILlmRuntimeConfig
{
    public ScratchLlmConfig(string modelPath, int gpuLayers)
    {
        ModelPath = modelPath;
        GpuLayers = gpuLayers;
    }

    public string ModelPath { get; }
    public int GpuLayers { get; }
}

internal static class PromptBuilder
{
    /// <summary>
    /// Builds a compressed system prompt from PhraseologyRules.All. Groups all 163 rules by their
    /// canonical output, so each canonical command appears once with ALL its natural-language
    /// variants listed — exactly the discrimination signal a small instruct model needs.
    /// This replaces the alphabetical CommandRegistry dump which was confusing the model.
    /// </summary>
    public static string BuildRuleDerivedSystemPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine("You convert spoken ATC instructions into YAAT canonical commands.");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Output ONLY the canonical command. No quotes, no explanation, no prose.");
        sb.AppendLine("- Multiple commands: comma-separated, e.g. 'CM 5000, FH 270'.");
        sb.AppendLine("- Condition prefix: 'AT <FIX>' or 'LV <ALT>' before the clause, e.g. 'AT CEPIN CAPP'.");
        sb.AppendLine("- Altitudes in feet (5000), flight levels converted (FL350 -> 35000).");
        sb.AppendLine("- Headings as 3-digit degrees (270, 090).");
        sb.AppendLine("- If the transcript is NOT a recognizable instruction, output nothing.");
        sb.AppendLine();
        sb.AppendLine("Canonical commands (spoken phrasings -> canonical):");
        sb.AppendLine();

        // Group rules by their output template so each canonical command lists all variants.
        // The output ordering matches PhraseologyRules.Build() — heading, altitude/speed, nav,
        // tower, approach, pattern, hold, helicopter, transponder, ground, broadcast.
        var grouped = PhraseologyRules
            .All.GroupBy(r => r.OutputTemplate, StringComparer.Ordinal)
            .Select(g =>
                (
                    Output: g.Key,
                    Patterns: g.Select(r => string.Join(' ', r.Pattern.Select(p => p.TrimEnd('?')))).Distinct(StringComparer.Ordinal).ToList()
                )
            );

        foreach (var (output, patterns) in grouped)
        {
            // Compact row: "verb arg: phrase1 / phrase2 / phrase3"
            sb.Append(output).Append(": ").AppendLine(string.Join(" / ", patterns));
        }

        return sb.ToString();
    }
}
