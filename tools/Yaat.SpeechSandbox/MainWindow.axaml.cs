using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Yaat.Client.Services;
using Yaat.Sim.Speech;

namespace Yaat.SpeechSandbox;

/// <summary>
/// Persistent location for the last recorded sandbox clip. Sits next to the YAAT preferences
/// file under %LOCALAPPDATA%/yaat/ so it lives alongside the user's existing YAAT state and
/// survives reboots, sandbox rebuilds, and main-app uninstalls (assuming they don't wipe
/// %LOCALAPPDATA%/yaat). Stored as 16 kHz mono PCM16 WAV — the same format Whisper.net expects
/// after WavHeader.WritePcm16, so round-tripping is lossless on the integer side.
/// </summary>
internal static class SandboxClipPath
{
    private static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "yaat", "sandbox");

    public static string ClipFile { get; } = Path.Combine(Dir, "last-clip.wav");

    public static void EnsureDirExists() => Directory.CreateDirectory(Dir);
}

/// <summary>
/// Code-behind for the speech sandbox window. Loads <see cref="UserPreferences"/> from the
/// standard YAAT config location, constructs a <see cref="LocalLlmService"/> backed by the
/// user's configured GGUF, and walks the speech mapping pipeline step-by-step on each
/// "Run pipeline" click. Each call instantiates fresh <see cref="LocalLlmCommandMapper"/> and
/// <see cref="LocalLlmCallsignResolver"/> instances with the prompts currently in the textboxes,
/// so editing a prompt and clicking Run uses the edited version on the very next call.
///
/// The full audio path is deliberately excluded — the sandbox starts from the literal transcript
/// the user pastes in. This keeps iteration fast and isolates prompt/rule changes from acoustic
/// model variability.
/// </summary>
public partial class MainWindow : Window
{
    private readonly UserPreferences _preferences;
    private readonly LocalLlmService _llmService;
    private readonly ModelManager _modelManager = new();
    private readonly WhisperSttEngine _whisperStt;
    private readonly AudioCaptureService _audioCapture;
    private readonly PhraseologyCommandMapper _ruleMapper = new();
    private readonly StringBuilder _log = new();

    private float[]? _currentClipSamples;
    private bool _isRecording;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);

        // Load prefs from %LOCALAPPDATA%/yaat/preferences.json — same file the main YAAT app uses.
        // The sandbox reads but never writes prefs, so it's safe to share the file.
        _preferences = new UserPreferences();
        _llmService = new LocalLlmService(new PreferencesLlmRuntimeConfig(_preferences));
        _whisperStt = new WhisperSttEngine(_preferences);
        _audioCapture = new AudioCaptureService(_preferences);

        var llmCommandPromptBox = this.FindControl<TextBox>("LlmCommandPromptBox")!;
        var llmResolverPromptBox = this.FindControl<TextBox>("LlmResolverPromptBox")!;
        llmCommandPromptBox.Text = LocalLlmCommandMapper.GetDefaultSystemPrompt();
        llmResolverPromptBox.Text = LocalLlmCallsignResolver.DefaultSystemPrompt;

        var configText = this.FindControl<TextBlock>("ConfigText")!;
        configText.Text = BuildConfigSummary();

        var runButton = this.FindControl<Button>("RunButton")!;
        runButton.Click += OnRunClicked;

        var resetCommandButton = this.FindControl<Button>("ResetCommandPromptButton")!;
        resetCommandButton.Click += (_, _) => llmCommandPromptBox.Text = LocalLlmCommandMapper.GetDefaultSystemPrompt();

        var resetResolverButton = this.FindControl<Button>("ResetResolverPromptButton")!;
        resetResolverButton.Click += (_, _) => llmResolverPromptBox.Text = LocalLlmCallsignResolver.DefaultSystemPrompt;

        var loadDefaultsButton = this.FindControl<Button>("LoadDefaultsButton")!;
        loadDefaultsButton.Click += OnLoadExampleClicked;

        var recordButton = this.FindControl<Button>("RecordButton")!;
        recordButton.Click += OnRecordToggleClicked;

        var reTranscribeButton = this.FindControl<Button>("ReTranscribeButton")!;
        reTranscribeButton.Click += OnReTranscribeClicked;

        var clearClipButton = this.FindControl<Button>("ClearClipButton")!;
        clearClipButton.Click += OnClearClipClicked;

        var buildWhisperPromptButton = this.FindControl<Button>("BuildWhisperPromptButton")!;
        buildWhisperPromptButton.Click += (_, _) => RebuildWhisperPromptFromInputs();

        TryLoadPersistedClip();

        // Background prewarm so the first Run click doesn't block on multi-second weights load.
        // Prewarm both Whisper and LLM in parallel — same as the production main-window path.
        _ = Task.Run(async () =>
        {
            try
            {
                var whisperTask = _whisperStt.PrewarmAsync(CancellationToken.None);
                var llmTask = _llmService.PrewarmAsync(CancellationToken.None);
                await Task.WhenAll(whisperTask, llmTask);
                Dispatcher.UIThread.Post(() => SetStatus("Whisper + LLM prewarmed. Ready."));
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => SetStatus($"Prewarm failed: {ex.Message}"));
            }
        });
    }

    private void TryLoadPersistedClip()
    {
        if (!File.Exists(SandboxClipPath.ClipFile))
        {
            UpdateClipStatus();
            return;
        }

        try
        {
            _currentClipSamples = WavHeader.ReadPcm16(SandboxClipPath.ClipFile);
            AppendLog($"Loaded persisted clip: {_currentClipSamples.Length} samples ({SecondsOf(_currentClipSamples):F2}s)");
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to load persisted clip {SandboxClipPath.ClipFile}: {ex.Message}");
            _currentClipSamples = null;
        }

        UpdateClipStatus();
    }

    private void UpdateClipStatus()
    {
        var status = this.FindControl<TextBlock>("ClipStatusText")!;
        var reTranscribeButton = this.FindControl<Button>("ReTranscribeButton")!;
        if (_currentClipSamples is null || _currentClipSamples.Length == 0)
        {
            status.Text = $"No clip loaded. Will save to: {SandboxClipPath.ClipFile}";
            reTranscribeButton.IsEnabled = false;
        }
        else
        {
            status.Text =
                $"Clip loaded: {_currentClipSamples.Length:N0} samples ({SecondsOf(_currentClipSamples):F2}s) at {SandboxClipPath.ClipFile}";
            reTranscribeButton.IsEnabled = true;
        }
    }

    private static double SecondsOf(float[] samples) => samples.Length / (double)AudioCaptureService.SampleRate;

    private void OnRecordToggleClicked(object? sender, RoutedEventArgs e)
    {
        var recordButton = this.FindControl<Button>("RecordButton")!;
        if (!_isRecording)
        {
            if (!_audioCapture.StartCapture())
            {
                SetStatus("Failed to start audio capture (check microphone + speech-enabled pref).");
                return;
            }
            _isRecording = true;
            recordButton.Content = "Stop recording";
            SetStatus("Recording... click 'Stop recording' when done.");
        }
        else
        {
            var samples = _audioCapture.StopCapture();
            _isRecording = false;
            recordButton.Content = "Record clip";

            if (samples.Length == 0)
            {
                SetStatus("Capture returned 0 samples — clip not saved.");
                return;
            }

            try
            {
                SandboxClipPath.EnsureDirExists();
                using var wavStream = WavHeader.WritePcm16(samples, AudioCaptureService.SampleRate);
                using var fs = File.Create(SandboxClipPath.ClipFile);
                wavStream.CopyTo(fs);
                _currentClipSamples = samples;
                SetStatus($"Saved {samples.Length:N0} samples ({SecondsOf(samples):F2}s) to {SandboxClipPath.ClipFile}");
                AppendLog($"Saved clip: {samples.Length} samples → {SandboxClipPath.ClipFile}");
            }
            catch (Exception ex)
            {
                SetStatus($"Failed to save clip: {ex.Message}");
                AppendLog(ex.ToString());
            }

            UpdateClipStatus();
        }
    }

    private async void OnReTranscribeClicked(object? sender, RoutedEventArgs e)
    {
        if (_currentClipSamples is null || _currentClipSamples.Length == 0)
        {
            SetStatus("No clip loaded — record one first.");
            return;
        }
        if (!_whisperStt.IsConfigured)
        {
            SetStatus($"Whisper model not found at {_modelManager.GetWhisperPath(_preferences.WhisperModelSize)}");
            return;
        }

        var reTranscribeButton = this.FindControl<Button>("ReTranscribeButton")!;
        reTranscribeButton.IsEnabled = false;
        try
        {
            var prompt = this.FindControl<TextBox>("WhisperPromptBox")!.Text ?? string.Empty;
            SetStatus("Transcribing with Whisper...");
            var sw = Stopwatch.StartNew();
            var transcript = await _whisperStt.TranscribeAsync(_currentClipSamples, prompt, CancellationToken.None);
            sw.Stop();

            if (string.IsNullOrWhiteSpace(transcript))
            {
                SetStatus($"Whisper returned empty/null in {sw.ElapsedMilliseconds} ms (silence or noise marker).");
                AppendLog($"[Whisper] empty result ({sw.ElapsedMilliseconds} ms)");
            }
            else
            {
                this.FindControl<TextBox>("TranscriptBox")!.Text = transcript;
                SetStatus($"Whisper transcribed in {sw.ElapsedMilliseconds} ms.");
                AppendLog($"[Whisper] '{transcript}' ({sw.ElapsedMilliseconds} ms, prompt={prompt.Length} chars)");
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Whisper failed: {ex.Message}");
            AppendLog(ex.ToString());
        }
        finally
        {
            reTranscribeButton.IsEnabled = true;
        }
    }

    private void OnClearClipClicked(object? sender, RoutedEventArgs e)
    {
        _currentClipSamples = null;
        try
        {
            if (File.Exists(SandboxClipPath.ClipFile))
            {
                File.Delete(SandboxClipPath.ClipFile);
                AppendLog($"Deleted {SandboxClipPath.ClipFile}");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to delete clip file: {ex.Message}");
        }
        SetStatus("Clip cleared.");
        UpdateClipStatus();
    }

    private string BuildConfigSummary()
    {
        var sb = new StringBuilder();
        sb.Append("Whisper model: ")
            .Append(_preferences.WhisperModelSize)
            .Append(" → ")
            .AppendLine(_modelManager.GetWhisperPath(_preferences.WhisperModelSize));
        sb.Append("Whisper file exists: ").AppendLine(_whisperStt.IsConfigured ? "yes" : "no");
        sb.Append("LLM model path: ")
            .AppendLine(string.IsNullOrWhiteSpace(_preferences.LlmModelPath) ? "(not configured)" : _preferences.LlmModelPath);
        sb.Append("LLM model file exists: ").AppendLine(_llmService.IsConfigured ? "yes" : "no");
        sb.Append("LLM GPU layers: ").AppendLine(_preferences.LlmGpuLayers.ToString());
        sb.Append("Speech enabled in prefs: ").AppendLine(_preferences.SpeechEnabled ? "yes" : "no");
        return sb.ToString();
    }

    private void OnLoadExampleClicked(object? sender, RoutedEventArgs e)
    {
        this.FindControl<TextBox>("ActiveCallsignsBox")!.Text = "N346G\nSWA123\nN9225L";
        this.FindControl<TextBox>("ProgrammedFixesBox")!.Text = "CEPIN, SUNOL";
        this.FindControl<TextBox>("TranscriptBox")!.Text = "november three four six golf turn left hitting tree one zero";
        // Rebuild the Whisper prompt from the example data so the user can see the same shape
        // production code produces (ICAO + spoken variant per callsign + fix names).
        RebuildWhisperPromptFromInputs();
    }

    /// <summary>
    /// Builds a Whisper <c>initial_prompt</c> from the current <c>ActiveCallsignsBox</c> and
    /// <c>ProgrammedFixesBox</c> contents using the same shape as
    /// <c>MainViewModel.BuildSpeechContext</c>: ICAO callsign + first spoken variant per active
    /// aircraft, followed by every programmed fix. Aircraft type is unknown in the sandbox
    /// (no scenario state) so we pass null — that just skips type-based GA variants like
    /// "skyhawk one two three". Pronunciation hints from <c>NavigationDatabase</c> are also
    /// skipped because the sandbox doesn't load nav data.
    /// </summary>
    private void RebuildWhisperPromptFromInputs()
    {
        var activeCallsigns = ParseList(this.FindControl<TextBox>("ActiveCallsignsBox")!.Text);
        var programmedFixes = ParseList(this.FindControl<TextBox>("ProgrammedFixesBox")!.Text);

        var parts = new List<string>(capacity: activeCallsigns.Count * 2 + programmedFixes.Count);
        foreach (var cs in activeCallsigns)
        {
            parts.Add(cs);
            var variants = CallsignParser.GetSpokenVariants(cs, aircraftType: null, activeCallsigns);
            if (variants.Count > 0)
            {
                parts.Add(variants[0]);
            }
        }
        parts.AddRange(programmedFixes);

        this.FindControl<TextBox>("WhisperPromptBox")!.Text = string.Join(' ', parts);
    }

    private async void OnRunClicked(object? sender, RoutedEventArgs e)
    {
        var runButton = this.FindControl<Button>("RunButton")!;
        runButton.IsEnabled = false;
        try
        {
            await RunPipelineAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"Run failed: {ex.Message}");
            AppendLog(ex.ToString());
        }
        finally
        {
            runButton.IsEnabled = true;
        }
    }

    private async Task RunPipelineAsync()
    {
        ClearResults();
        SetStatus("Running...");

        var transcript = (this.FindControl<TextBox>("TranscriptBox")!.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(transcript))
        {
            SetStatus("No transcript provided.");
            return;
        }

        var activeCallsigns = ParseList(this.FindControl<TextBox>("ActiveCallsignsBox")!.Text);
        var programmedFixes = ParseList(this.FindControl<TextBox>("ProgrammedFixesBox")!.Text);
        var commandPrompt = this.FindControl<TextBox>("LlmCommandPromptBox")!.Text ?? string.Empty;
        var resolverPrompt = this.FindControl<TextBox>("LlmResolverPromptBox")!.Text ?? string.Empty;

        AppendLog($"Transcript:        {transcript}");
        AppendLog($"Active callsigns:  [{string.Join(", ", activeCallsigns)}]");
        AppendLog($"Programmed fixes:  [{string.Join(", ", programmedFixes)}]");
        AppendLog("");

        // Step 1: Pipeline-level callsign extraction + transcript stripping.
        var (commandText, extractedCallsign) = SpeechRecognitionService.ExtractAndStripCallsign(transcript, activeCallsigns);
        this.FindControl<TextBlock>("ExtractedCallsignText")!.Text = $"Callsign: {extractedCallsign ?? "(none)"}";
        this.FindControl<TextBlock>("StrippedCommandText")!.Text = $"Stripped command text: {commandText}";
        AppendLog($"[Step 1] callsign={extractedCallsign ?? "(none)"} stripped='{commandText}'");

        var mapContext = new MapContext(activeCallsigns, programmedFixes);

        // Step 2: Rule mapper.
        var ruleSw = Stopwatch.StartNew();
        var ruleResult = await _ruleMapper.MapAsync(commandText, mapContext, CancellationToken.None);
        ruleSw.Stop();
        this.FindControl<TextBlock>("RuleResultText")!.Text = ruleResult is null
            ? "Rule mapper: (no match)"
            : $"Rule mapper: callsign={ruleResult.Callsign ?? "(none)"} canonical={ruleResult.CanonicalCommand} clauses={ruleResult.MatchedRuleCount}";
        this.FindControl<TextBlock>("RuleElapsedText")!.Text = $"{ruleSw.ElapsedMilliseconds} ms";
        AppendLog($"[Step 2] rule={(ruleResult?.CanonicalCommand ?? "(null)")}  ({ruleSw.ElapsedMilliseconds} ms)");

        string? canonical = ruleResult?.CanonicalCommand;
        string? callsign = extractedCallsign ?? ruleResult?.Callsign;
        var usedLlmFallback = false;

        // Step 3: LLM command mapper fallback — only if the rule mapper didn't match.
        if (ruleResult is null)
        {
            if (!_llmService.IsConfigured)
            {
                this.FindControl<TextBlock>("LlmCanonicalText")!.Text = "LLM not configured — skipped.";
                AppendLog("[Step 3] LLM not configured; skipped");
            }
            else
            {
                var sandboxLlmMapper = new LocalLlmCommandMapper(_llmService, commandPrompt);
                var llmSw = Stopwatch.StartNew();
                var llmResult = await sandboxLlmMapper.MapAsync(commandText, mapContext, CancellationToken.None);
                llmSw.Stop();
                this.FindControl<TextBlock>("LlmCanonicalText")!.Text = llmResult is null
                    ? "LLM mapper: (no match)"
                    : $"LLM canonical: {llmResult.CanonicalCommand}";
                this.FindControl<TextBlock>("LlmElapsedText")!.Text = $"{llmSw.ElapsedMilliseconds} ms";
                AppendLog($"[Step 3] llm={(llmResult?.CanonicalCommand ?? "(null)")}  ({llmSw.ElapsedMilliseconds} ms)");
                if (llmResult is not null)
                {
                    canonical = llmResult.CanonicalCommand;
                    usedLlmFallback = true;
                }
            }
        }

        // Step 4: LLM callsign resolver — only if we have a canonical command but no callsign.
        if (canonical is not null && callsign is null && activeCallsigns.Count > 0)
        {
            if (!_llmService.IsConfigured)
            {
                this.FindControl<TextBlock>("ResolverCallsignText")!.Text = "LLM not configured — skipped.";
                AppendLog("[Step 4] LLM not configured; resolver skipped");
            }
            else
            {
                var sandboxResolver = new LocalLlmCallsignResolver(_llmService, resolverPrompt);
                var resolverSw = Stopwatch.StartNew();
                var resolved = await sandboxResolver.ResolveAsync(transcript, activeCallsigns, CancellationToken.None);
                resolverSw.Stop();
                this.FindControl<TextBlock>("ResolverCallsignText")!.Text = resolved is null
                    ? "Resolver: (none / NONE)"
                    : $"Resolved callsign: {resolved}";
                this.FindControl<TextBlock>("ResolverElapsedText")!.Text = $"{resolverSw.ElapsedMilliseconds} ms";
                AppendLog($"[Step 4] resolver={resolved ?? "(null)"}  ({resolverSw.ElapsedMilliseconds} ms)");
                callsign = resolved;
            }
        }

        // Partial fallback: if both mappers failed but a callsign was extracted, surface the
        // raw command text so the user can still see what came out of Whisper. Mirrors the
        // production pipeline's behavior so the sandbox reflects the live UX.
        if (canonical is null && callsign is not null && !string.IsNullOrWhiteSpace(commandText))
        {
            canonical = commandText.Trim();
            AppendLog($"[Fallback] mappers failed but callsign present; surfacing raw command text");
        }

        // Final: compose what CommandText would be set to.
        string final;
        if (!string.IsNullOrEmpty(canonical))
        {
            final = string.IsNullOrEmpty(callsign) ? canonical : $"{callsign} {canonical}";
        }
        else if (!string.IsNullOrWhiteSpace(transcript))
        {
            final = transcript;
        }
        else
        {
            final = string.Empty;
        }

        this.FindControl<TextBlock>("FinalText")!.Text = final;
        AppendLog($"[Final] CommandText='{final}' usedLlmFallback={usedLlmFallback}");
        SetStatus($"Done. usedLlmFallback={usedLlmFallback}");
    }

    private void ClearResults()
    {
        this.FindControl<TextBlock>("ExtractedCallsignText")!.Text = string.Empty;
        this.FindControl<TextBlock>("StrippedCommandText")!.Text = string.Empty;
        this.FindControl<TextBlock>("RuleResultText")!.Text = string.Empty;
        this.FindControl<TextBlock>("RuleElapsedText")!.Text = string.Empty;
        this.FindControl<TextBlock>("LlmRawText")!.Text = string.Empty;
        this.FindControl<TextBlock>("LlmCanonicalText")!.Text = string.Empty;
        this.FindControl<TextBlock>("LlmElapsedText")!.Text = string.Empty;
        this.FindControl<TextBlock>("ResolverRawText")!.Text = string.Empty;
        this.FindControl<TextBlock>("ResolverCallsignText")!.Text = string.Empty;
        this.FindControl<TextBlock>("ResolverElapsedText")!.Text = string.Empty;
        this.FindControl<TextBlock>("FinalText")!.Text = string.Empty;
        _log.Clear();
        this.FindControl<TextBox>("LogBox")!.Text = string.Empty;
    }

    private void SetStatus(string status)
    {
        this.FindControl<TextBlock>("StatusText")!.Text = status;
    }

    private void AppendLog(string line)
    {
        _log.AppendLine(line);
        this.FindControl<TextBox>("LogBox")!.Text = _log.ToString();
    }

    private static List<string> ParseList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return raw.Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }
}
