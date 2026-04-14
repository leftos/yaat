using System.Diagnostics;
using Avalonia;
using LMKit.Licensing;
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
///   <item><description>(no args) — launches the GUI sandbox window (App.axaml / MainWindow.axaml).
///     Interactive mode for recording, replaying, and iterating on Whisper / LLM prompts.</description></item>
/// </list>
/// Both modes share LM-Kit licensing setup: an empty SetLicenseKey call signals Community
/// Edition. Backend selection (CUDA / Vulkan / CPU) is owned by LM-Kit at model load time.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            LicenseManager.SetLicenseKey("");
        }
        catch
        {
            // Already initialized — swallow.
        }

        if (args.Length > 0 && args[0] == "--pipeline")
        {
            return RunPipelineMode(args[1..]).GetAwaiter().GetResult();
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
}
