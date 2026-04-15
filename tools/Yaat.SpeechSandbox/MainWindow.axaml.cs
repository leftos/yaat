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
        var whisperPromptBox = this.FindControl<TextBox>("WhisperPromptBox")!;
        llmCommandPromptBox.Text = LocalLlmCommandMapper.GetDefaultSystemPrompt();
        llmResolverPromptBox.Text = LocalLlmCallsignResolver.DefaultSystemPrompt;
        whisperPromptBox.Text = WhisperBiasingPrompt.Default;

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

        var buildLlmUserPromptButton = this.FindControl<Button>("BuildLlmUserPromptButton")!;
        buildLlmUserPromptButton.Click += (_, _) => RebuildLlmUserPromptFromInputs();

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
            SetStatus($"Whisper model not configured (WhisperModelSize='{_preferences.WhisperModelSize}')");
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
        sb.Append("Whisper model source: ").AppendLine(_preferences.WhisperModelSize);
        sb.Append("Whisper configured: ").AppendLine(_whisperStt.IsConfigured ? "yes" : "no");
        sb.Append("LLM model source: ")
            .AppendLine(string.IsNullOrWhiteSpace(_preferences.LlmModelPath) ? "(not configured)" : _preferences.LlmModelPath);
        sb.Append("LLM configured: ").AppendLine(_llmService.IsConfigured ? "yes" : "no");
        sb.Append("LLM GPU layers: ").AppendLine(_preferences.LlmGpuLayers.ToString());
        sb.Append("Speech enabled in prefs: ").AppendLine(_preferences.SpeechEnabled ? "yes" : "no");
        return sb.ToString();
    }

    private void OnLoadExampleClicked(object? sender, RoutedEventArgs e)
    {
        this.FindControl<TextBox>("ActiveCallsignsBox")!.Text = "N346G\nSWA123\nN9225L";
        this.FindControl<TextBox>("ProgrammedFixesBox")!.Text = "CEPIN, SUNOL";
        // Seed the misheard-runway recovery scenario the user reported in the speech-pipeline
        // debug log: N9225L destined for KOAK, Whisper transcribed "28R" as "288". With the new
        // runway/destination context populated, the LLM fallback should snap "288" → "28R".
        this.FindControl<TextBox>("AvailableRunwaysBox")!.Text = "KOAK: 28R 28L 10R 10L 30 12 33 15";
        this.FindControl<TextBox>("AircraftDestinationsBox")!.Text = "N9225L: KOAK";
        this.FindControl<TextBox>("TranscriptBox")!.Text = "okay we'll enter right downwind for runway 288 november 9 or 225 lima";
        // Rebuild the Whisper prompt from the example data so the user can see the same shape
        // production code produces (ICAO + spoken variant per callsign + fix names).
        RebuildWhisperPromptFromInputs();
    }

    /// <summary>
    /// Regenerate the LLM user prompt textbox from the current input fields, mirroring what
    /// <see cref="LocalLlmCommandMapper.BuildUserPromptForDebug"/> would produce. Wired to the
    /// "Build from inputs" button next to the textbox so the user can refresh it on demand
    /// after changing inputs (without losing manual edits the rest of the time). Pulls
    /// the same fields the Run pipeline uses so what the user sees matches what gets sent.
    /// </summary>
    private void RebuildLlmUserPromptFromInputs()
    {
        var rawTranscript = (this.FindControl<TextBox>("TranscriptBox")!.Text ?? string.Empty).Trim();
        var activeCallsigns = ParseList(this.FindControl<TextBox>("ActiveCallsignsBox")!.Text);
        var programmedFixes = ParseList(this.FindControl<TextBox>("ProgrammedFixesBox")!.Text);
        var availableRunways = ParseAvailableRunways(this.FindControl<TextBox>("AvailableRunwaysBox")!.Text);
        var aircraftDestinations = ParseAircraftDestinations(this.FindControl<TextBox>("AircraftDestinationsBox")!.Text);

        // Mirror the Step 1 callsign-strip + digit-normalize so the textbox shows the same
        // command-text the production LLM mapper sees as the "Transcript:" line.
        var (commandText, _) = SpeechRecognitionService.ExtractAndStripCallsign(rawTranscript, activeCallsigns);

        var mapContext = new MapContext(activeCallsigns, programmedFixes)
        {
            AvailableRunways = availableRunways,
            AircraftDestinations = aircraftDestinations,
        };

        this.FindControl<TextBox>("LlmUserPromptBox")!.Text = LocalLlmCommandMapper.BuildUserPromptForDebug(commandText, mapContext);
    }

    /// <summary>
    /// Populates the Whisper <c>initial_prompt</c> textbox with the same value the production
    /// pipeline uses: <see cref="WhisperBiasingPrompt.Default"/> — the static NATO alphabet +
    /// phonetic numbers + every literal token from <c>PhraseologyRules.All</c>, computed once
    /// per process. Production no longer injects per-aircraft callsigns or fix names into the
    /// Whisper prompt (the static vocabulary is enough for whisper-large-turbo3 to recognize
    /// arbitrary tail numbers cleanly), so the sandbox should mirror that.
    /// </summary>
    private void RebuildWhisperPromptFromInputs()
    {
        this.FindControl<TextBox>("WhisperPromptBox")!.Text = WhisperBiasingPrompt.Default;
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
        var availableRunways = ParseAvailableRunways(this.FindControl<TextBox>("AvailableRunwaysBox")!.Text);
        var aircraftDestinations = ParseAircraftDestinations(this.FindControl<TextBox>("AircraftDestinationsBox")!.Text);
        var commandPrompt = this.FindControl<TextBox>("LlmCommandPromptBox")!.Text ?? string.Empty;
        var resolverPrompt = this.FindControl<TextBox>("LlmResolverPromptBox")!.Text ?? string.Empty;

        AppendLog($"Transcript:           {transcript}");
        AppendLog($"Active callsigns:     [{string.Join(", ", activeCallsigns)}]");
        AppendLog($"Programmed fixes:     [{string.Join(", ", programmedFixes)}]");
        AppendLog($"Available runways:    [{string.Join("; ", availableRunways.Select(kv => $"{kv.Key}={string.Join(' ', kv.Value)}"))}]");
        AppendLog($"Aircraft destinations:[{string.Join(", ", aircraftDestinations.Select(kv => $"{kv.Key}->{kv.Value}"))}]");
        AppendLog("");

        // Step 1: Pipeline-level callsign extraction + transcript stripping.
        var (commandText, extractedCallsign) = SpeechRecognitionService.ExtractAndStripCallsign(transcript, activeCallsigns);
        this.FindControl<TextBlock>("ExtractedCallsignText")!.Text = $"Callsign: {extractedCallsign ?? "(none)"}";
        this.FindControl<TextBlock>("StrippedCommandText")!.Text = $"Stripped command text: {commandText}";
        AppendLog($"[Step 1] callsign={extractedCallsign ?? "(none)"} stripped='{commandText}'");

        var mapContext = new MapContext(activeCallsigns, programmedFixes)
        {
            AvailableRunways = availableRunways,
            AircraftDestinations = aircraftDestinations,
        };

        // The LLM user prompt textbox is editable. If the user has populated it (either via
        // "Build from inputs" or by hand-editing), Run honors that exact text and skips
        // BuildUserPrompt entirely. If the box is empty, auto-fill it from the current inputs
        // so the user sees what the production path would have generated and can iterate from
        // there. Either way, the textbox content is the source of truth at Run time.
        var llmUserPromptBox = this.FindControl<TextBox>("LlmUserPromptBox")!;
        if (string.IsNullOrWhiteSpace(llmUserPromptBox.Text))
        {
            llmUserPromptBox.Text = LocalLlmCommandMapper.BuildUserPromptForDebug(commandText, mapContext);
        }
        var llmUserPromptOverride = llmUserPromptBox.Text ?? string.Empty;

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

        // Step 3: LLM command mapper fallback — only if the rule mapper didn't match. We always
        // use MapWithPromptsAsync (not MapAsync) because the user prompt textbox is editable,
        // and Run must honor any manual edits the user made. The system prompt comes from the
        // LlmCommandPromptBox above; the user prompt comes from the LlmUserPromptBox we just
        // populated. This bypasses BuildUserPrompt entirely on the sandbox path.
        if (ruleResult is null)
        {
            if (!_llmService.IsConfigured)
            {
                this.FindControl<TextBlock>("LlmCanonicalText")!.Text = "LLM not configured — skipped.";
                AppendLog("[Step 3] LLM not configured; skipped");
            }
            else
            {
                // The constructor takes a system prompt, but MapWithPromptsAsync accepts the
                // system prompt explicitly per call — so we pass _llmService alone and supply
                // the system + user prompt as method arguments. Keeps "what gets sent" purely
                // a function of the textbox contents at Run time.
                var sandboxLlmMapper = new LocalLlmCommandMapper(_llmService);
                var llmSw = Stopwatch.StartNew();
                var llmResult = await sandboxLlmMapper.MapWithPromptsAsync(commandPrompt, llmUserPromptOverride, CancellationToken.None);
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
        // Note: LlmUserPromptBox is intentionally NOT cleared — Run must preserve any manual
        // edits the user made between Runs. Use the "Build from inputs" button to refresh it.
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

    /// <summary>
    /// Parses the AvailableRunwaysBox contents into a per-airport runway list dictionary. Format:
    /// one airport per line, <c>"AIRPORT: rwy1 rwy2 ..."</c> — the same shape <see cref="LocalLlmCommandMapper.BuildUserPromptForDebug"/>
    /// renders. Whitespace and empty lines are ignored. Lines without a colon are skipped silently
    /// so the user can paste comments or partial input without breaking the parse.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseAvailableRunways(string? raw)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return result;
        }

        foreach (var line in raw.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colonIdx = line.IndexOf(':');
            if (colonIdx <= 0 || colonIdx >= line.Length - 1)
            {
                continue;
            }

            var airport = line[..colonIdx].Trim();
            var runways = line[(colonIdx + 1)..]
                .Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            if (airport.Length > 0 && runways.Count > 0)
            {
                result[airport] = runways;
            }
        }

        return result;
    }

    /// <summary>
    /// Parses the AircraftDestinationsBox contents into a callsign → airport dictionary. Format:
    /// one entry per line, <c>"CALLSIGN: AIRPORT"</c> or <c>"CALLSIGN -&gt; AIRPORT"</c>. Lines
    /// without a separator are skipped silently.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ParseAircraftDestinations(string? raw)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return result;
        }

        foreach (var line in raw.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Try arrow form first ("N9225L -> KOAK"), then colon form ("N9225L: KOAK").
            var arrowIdx = line.IndexOf("->", StringComparison.Ordinal);
            int sepIdx;
            int sepLen;
            if (arrowIdx > 0)
            {
                sepIdx = arrowIdx;
                sepLen = 2;
            }
            else
            {
                var colonIdx = line.IndexOf(':');
                if (colonIdx <= 0)
                {
                    continue;
                }
                sepIdx = colonIdx;
                sepLen = 1;
            }

            var callsign = line[..sepIdx].Trim();
            var airport = line[(sepIdx + sepLen)..].Trim();
            if (callsign.Length > 0 && airport.Length > 0)
            {
                result[callsign] = airport;
            }
        }

        return result;
    }
}
