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
using System.Runtime.InteropServices;
using System.Text;
using LLama.Native;
using Yaat.Client.Services;
using Yaat.Sim.Speech;

var modelPath = Path.GetFullPath(
    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tests", "Yaat.Client.Tests", "TestData", "llm", "test-model.gguf")
);

if (!File.Exists(modelPath))
{
    Console.Error.WriteLine($"FATAL: GGUF not found at {modelPath}");
    return 1;
}

// Auto-discover CUDA 12 the same way LlmCudaFixture does.
RepointToCuda12IfAvailable();

NativeLibraryConfig.All.WithCuda(true).WithAutoFallback(true);

// Real LocalLlmService via a lightweight ILlmRuntimeConfig.
var config = new ScratchLlmConfig(modelPath, gpuLayers: 999);
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
_ = await service.GenerateAsync("Short answer.", "hi", CancellationToken.None);
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

static void RepointToCuda12IfAvailable()
{
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        return;
    }

    const string WindowsCudaRoot = @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA";
    if (!Directory.Exists(WindowsCudaRoot))
    {
        return;
    }

    string? best = null;
    var bestMinor = -1;
    foreach (var dir in Directory.GetDirectories(WindowsCudaRoot, "v12.*"))
    {
        var name = Path.GetFileName(dir);
        var minorPart = name[4..];
        if (int.TryParse(minorPart, out var minor) && minor > bestMinor)
        {
            best = dir;
            bestMinor = minor;
        }
    }

    if (best is null)
    {
        return;
    }

    Environment.SetEnvironmentVariable("CUDA_PATH", best);
    var binDir = Path.Combine(best, "bin");
    if (Directory.Exists(binDir))
    {
        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (!currentPath.Contains(binDir, StringComparison.OrdinalIgnoreCase))
        {
            Environment.SetEnvironmentVariable("PATH", binDir + Path.PathSeparator + currentPath);
        }
    }

    Console.WriteLine($"[Scratch] Using CUDA 12 at {best}");
}

internal sealed class ScratchLlmConfig : ILlmRuntimeConfig
{
    public ScratchLlmConfig(string modelPath, int gpuLayers)
    {
        ModelPath = modelPath;
        GpuLayers = gpuLayers;
    }

    public bool Enabled => true;
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
