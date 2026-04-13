using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Sim.Speech;

namespace Yaat.Client.Services;

public enum SpeechStatus
{
    Idle,
    Recording,
    Transcribing,
    Mapping,
    Error,
}

public enum SpeechSessionOutcome
{
    /// <summary>The pipeline completed and produced a canonical command.</summary>
    CommandAccepted,

    /// <summary>Whisper returned a transcript but neither mapper produced a canonical command.</summary>
    NoMappingFound,

    /// <summary>Whisper produced nothing useful (silence / unrecognized speech).</summary>
    EmptyTranscript,

    /// <summary>An exception was thrown in one of the pipeline stages.</summary>
    Error,

    /// <summary>The pipeline was cancelled (e.g. a new PTT press arrived while the previous was processing).</summary>
    Cancelled,
}

/// <summary>
/// Debug record of a single push-to-talk pipeline execution. Captured by
/// <see cref="SpeechRecognitionService.SessionHistory"/> in a ring buffer (last
/// <see cref="SpeechRecognitionService.MaxSessionHistory"/> entries) and surfaced in the Speech
/// debug window so users can post-mortem individual PTT presses without scraping the log file.
/// </summary>
public sealed record SpeechSession(
    DateTime TimestampUtc,
    int SampleCount,
    double AudioDurationSeconds,
    string Transcript,
    string? CanonicalCommand,
    bool UsedLlmFallback,
    long TranscribeElapsedMs,
    long MapElapsedMs,
    long TotalElapsedMs,
    SpeechSessionOutcome Outcome,
    string? ErrorMessage
);

/// <summary>Snapshot of simulation state passed to the recognition pipeline at PTT press.</summary>
/// <param name="ActiveCallsigns">Callsigns currently in the scenario (ICAO form).</param>
/// <param name="ProgrammedFixes">Fix names known to the relevant aircraft.</param>
/// <param name="WhisperInitialPrompt">Free-text prompt seed for Whisper — active callsigns + fixes merged into a single string.</param>
public sealed record SpeechContext(IReadOnlyList<string> ActiveCallsigns, IReadOnlyList<string> ProgrammedFixes, string WhisperInitialPrompt)
{
    /// <summary>
    /// Speech-recognition patterns for custom fixes (from <c>NavigationDatabase.CustomFixSpeechPatterns</c>).
    /// Passed through to <see cref="MapContext.CustomFixPatterns"/> so the rule engine can collapse
    /// multi-token natural-language references into single canonical-alias tokens before rule
    /// matching. Default empty so callers that don't use custom fixes don't have to populate it.
    /// </summary>
    public IReadOnlyList<CustomFixSpeechPattern> CustomFixPatterns { get; init; } = [];
}

/// <summary>
/// Orchestrates the full push-to-talk pipeline:
/// <list type="number">
///   <item><description><see cref="AudioCaptureService"/> records 16 kHz mono Float32 samples while PTT is held.</description></item>
///   <item><description><see cref="WhisperSttEngine"/> transcribes the captured samples with a seeded
///     <c>initial_prompt</c> (active callsigns + programmed fixes).</description></item>
///   <item><description><see cref="PhraseologyCommandMapper"/> maps the transcript to a canonical
///     YAAT command via the rule-based engine.</description></item>
///   <item><description>If the rule engine returns null, the optional <see cref="LocalLlmCommandMapper"/>
///     fallback is invoked.</description></item>
///   <item><description>The final transcript and canonical command surface through the
///     <see cref="CommandReady"/> event on the thread-pool; consumers marshal to the UI thread.</description></item>
/// </list>
///
/// The service is pure service layer — no Avalonia or MVVM dependencies. <see cref="MainViewModel"/>
/// (or equivalent UI consumer) subscribes to <see cref="StatusChanged"/> and <see cref="CommandReady"/>
/// to update the mic indicator and push the canonical command into <c>CommandText</c>.
///
/// The service holds simulation context only via a <see cref="Func{SpeechContext}"/> so the caller
/// controls how state is observed (no tight coupling back to <c>MainViewModel</c>).
/// </summary>
public sealed class SpeechRecognitionService : IDisposable
{
    private static readonly ILogger Log = AppLog.CreateLogger<SpeechRecognitionService>();

    private readonly UserPreferences _preferences;
    private readonly AudioCaptureService _audio;
    private readonly WhisperSttEngine _stt;
    private readonly ISpeechCommandMapper _ruleMapper;
    private readonly ISpeechCommandMapper? _llmMapper;
    private readonly Func<SpeechContext> _contextProvider;

    private SpeechStatus _status = SpeechStatus.Idle;
    private CancellationTokenSource? _pendingCts;
    private readonly object _statusLock = new();

    // Most recent PTT sessions for the debug window. Newest entries at index 0 (insertion point).
    // Capped at MaxSessionHistory — older entries are dropped as new ones arrive.
    public const int MaxSessionHistory = 20;
    public ObservableCollection<SpeechSession> SessionHistory { get; } = [];

    /// <summary>Raised on the thread pool after a session is appended to <see cref="SessionHistory"/>.
    /// UI consumers should marshal to the UI thread before touching the collection.</summary>
    public event Action<SpeechSession>? SessionRecorded;

    public SpeechRecognitionService(
        UserPreferences preferences,
        AudioCaptureService audio,
        WhisperSttEngine stt,
        ISpeechCommandMapper ruleMapper,
        ISpeechCommandMapper? llmMapper,
        Func<SpeechContext> contextProvider
    )
    {
        _preferences = preferences;
        _audio = audio;
        _stt = stt;
        _ruleMapper = ruleMapper;
        _llmMapper = llmMapper;
        _contextProvider = contextProvider;
    }

    /// <summary>Fired whenever the pipeline status changes.</summary>
    public event Action<SpeechStatus>? StatusChanged;

    /// <summary>
    /// Fired on the thread-pool once a PTT press has been fully processed. Consumers must marshal
    /// to the UI thread. Sends the raw transcript and the canonical command (possibly null if
    /// neither mapper produced a result).
    /// </summary>
    public event Action<SpeechResult>? CommandReady;

    public SpeechStatus Status
    {
        get
        {
            lock (_statusLock)
            {
                return _status;
            }
        }
    }

    /// <summary>
    /// Begins a PTT recording. Safe to call while already recording — it's a no-op in that case.
    /// Returns false if speech is disabled in prefs or the audio stream failed to start.
    /// </summary>
    public bool StartPtt()
    {
        if (!_preferences.SpeechEnabled)
        {
            return false;
        }

        if (Status != SpeechStatus.Idle)
        {
            return false;
        }

        if (!_audio.StartCapture())
        {
            SetStatus(SpeechStatus.Error);
            return false;
        }

        SetStatus(SpeechStatus.Recording);
        return true;
    }

    /// <summary>
    /// Ends a PTT recording and runs the transcribe → map pipeline asynchronously. Fires
    /// <see cref="CommandReady"/> when done regardless of outcome (transcript may be empty, canonical
    /// may be null — the consumer decides what to do with that).
    /// </summary>
    public void StopPtt()
    {
        if (Status != SpeechStatus.Recording)
        {
            return;
        }

        var samples = _audio.StopCapture();
        if (samples.Length == 0)
        {
            Log.LogInformation("PTT ended with zero samples; resetting to idle");
            SetStatus(SpeechStatus.Idle);
            return;
        }

        _pendingCts?.Cancel();
        _pendingCts = new CancellationTokenSource();
        var ct = _pendingCts.Token;

        // Fire-and-forget — the rest of the pipeline is async and we don't want to block the
        // key-up handler on Whisper inference. Errors are logged and surfaced via the Error status.
        _ = Task.Run(async () => await ProcessPipelineAsync(samples, ct).ConfigureAwait(false), ct);
    }

    /// <summary>
    /// Cancels any in-flight transcription / mapping and forces status back to Idle. Useful for
    /// shutdown and for a future "user cancels while still processing" UX.
    /// </summary>
    public void Cancel()
    {
        _pendingCts?.Cancel();
        if (Status == SpeechStatus.Recording)
        {
            _ = _audio.StopCapture();
        }

        SetStatus(SpeechStatus.Idle);
    }

    private async Task ProcessPipelineAsync(float[] samples, CancellationToken ct)
    {
        var totalSw = Stopwatch.StartNew();
        var transcribeMs = 0L;
        var mapMs = 0L;
        var transcript = string.Empty;
        string? canonical = null;
        var usedLlmFallback = false;
        var outcome = SpeechSessionOutcome.Error;
        string? errorMessage = null;

        var audioDurationSec = (double)samples.Length / AudioCaptureService.SampleRate;

        try
        {
            SetStatus(SpeechStatus.Transcribing);
            var ctx = _contextProvider();

            var transcribeSw = Stopwatch.StartNew();
            var raw = await _stt.TranscribeAsync(samples, ctx.WhisperInitialPrompt, ct).ConfigureAwait(false);
            transcribeSw.Stop();
            transcribeMs = transcribeSw.ElapsedMilliseconds;

            if (string.IsNullOrWhiteSpace(raw))
            {
                Log.LogInformation("Whisper returned empty transcript");
                outcome = SpeechSessionOutcome.EmptyTranscript;
                SetStatus(SpeechStatus.Idle);
                CommandReady?.Invoke(new SpeechResult("", null));
                return;
            }

            transcript = raw.Trim();
            Log.LogInformation("PTT transcript: {Transcript}", transcript);

            SetStatus(SpeechStatus.Mapping);
            var mapContext = new MapContext(ctx.ActiveCallsigns, ctx.ProgrammedFixes) { CustomFixPatterns = ctx.CustomFixPatterns };

            var mapSw = Stopwatch.StartNew();
            var ruleResult = await _ruleMapper.MapAsync(transcript, mapContext, ct).ConfigureAwait(false);
            if (ruleResult is not null)
            {
                canonical = ruleResult.CanonicalCommand;
                Log.LogInformation("Rule engine mapped transcript to: {Canonical}", canonical);
            }
            else if (_llmMapper is not null)
            {
                Log.LogInformation("Rule engine returned null, trying LLM fallback");
                var llmResult = await _llmMapper.MapAsync(transcript, mapContext, ct).ConfigureAwait(false);
                if (llmResult is not null)
                {
                    canonical = llmResult.CanonicalCommand;
                    usedLlmFallback = true;
                    Log.LogInformation("LLM fallback mapped transcript to: {Canonical}", canonical);
                }
                else
                {
                    Log.LogInformation("LLM fallback also returned null");
                }
            }

            mapSw.Stop();
            mapMs = mapSw.ElapsedMilliseconds;
            outcome = canonical is not null ? SpeechSessionOutcome.CommandAccepted : SpeechSessionOutcome.NoMappingFound;
        }
        catch (OperationCanceledException)
        {
            Log.LogInformation("Speech pipeline cancelled");
            outcome = SpeechSessionOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Speech pipeline failed");
            outcome = SpeechSessionOutcome.Error;
            errorMessage = ex.Message;
            SetStatus(SpeechStatus.Error);
            RecordSession(
                samples.Length,
                audioDurationSec,
                transcript,
                canonical,
                usedLlmFallback,
                transcribeMs,
                mapMs,
                totalSw.ElapsedMilliseconds,
                outcome,
                errorMessage
            );
            CommandReady?.Invoke(new SpeechResult(transcript, null));
            return;
        }

        SetStatus(SpeechStatus.Idle);
        RecordSession(
            samples.Length,
            audioDurationSec,
            transcript,
            canonical,
            usedLlmFallback,
            transcribeMs,
            mapMs,
            totalSw.ElapsedMilliseconds,
            outcome,
            errorMessage
        );
        CommandReady?.Invoke(new SpeechResult(transcript, canonical));
    }

    private void RecordSession(
        int sampleCount,
        double audioDurationSec,
        string transcript,
        string? canonical,
        bool usedLlmFallback,
        long transcribeMs,
        long mapMs,
        long totalMs,
        SpeechSessionOutcome outcome,
        string? errorMessage
    )
    {
        var session = new SpeechSession(
            TimestampUtc: DateTime.UtcNow,
            SampleCount: sampleCount,
            AudioDurationSeconds: audioDurationSec,
            Transcript: transcript,
            CanonicalCommand: canonical,
            UsedLlmFallback: usedLlmFallback,
            TranscribeElapsedMs: transcribeMs,
            MapElapsedMs: mapMs,
            TotalElapsedMs: totalMs,
            Outcome: outcome,
            ErrorMessage: errorMessage
        );

        // Collection mutation must happen on the UI thread because the debug window binds to it
        // directly. Marshal via Dispatcher.UIThread — callers already handle that this event is
        // raised off the UI thread. If the dispatcher isn't available (tests, headless), fall back
        // to direct mutation with a lock on the collection.
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            AppendSessionOnUiThread(session);
        }
        else
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => AppendSessionOnUiThread(session));
        }
    }

    private void AppendSessionOnUiThread(SpeechSession session)
    {
        SessionHistory.Insert(0, session);
        while (SessionHistory.Count > MaxSessionHistory)
        {
            SessionHistory.RemoveAt(SessionHistory.Count - 1);
        }

        SessionRecorded?.Invoke(session);
    }

    private void SetStatus(SpeechStatus status)
    {
        lock (_statusLock)
        {
            if (_status == status)
            {
                return;
            }

            _status = status;
        }

        StatusChanged?.Invoke(status);
    }

    public void Dispose()
    {
        _pendingCts?.Cancel();
        _pendingCts?.Dispose();
        _pendingCts = null;
    }
}

/// <summary>Result bundle passed to <see cref="SpeechRecognitionService.CommandReady"/>.</summary>
/// <param name="Transcript">Raw Whisper transcript (may be empty on failure).</param>
/// <param name="CanonicalCommand">Canonical YAAT command, or null when neither mapper produced a result.</param>
public sealed record SpeechResult(string Transcript, string? CanonicalCommand);
