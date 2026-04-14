using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Sim.Speech;

namespace Yaat.Client.Services;

public enum SpeechStatus
{
    Idle,

    /// <summary>
    /// Model weights are being pre-loaded at startup (or after speech was toggled on). Treated
    /// as start-legal by <see cref="SpeechRecognitionService.StartPtt"/> — the engine locks
    /// already serialize a user PTT against an in-flight prewarm.
    /// </summary>
    Warming,
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
    private readonly LocalLlmService? _llmService;
    private readonly ISpeechCommandMapper _ruleMapper;
    private readonly ISpeechCommandMapper? _llmMapper;
    private readonly LocalLlmCallsignResolver? _callsignResolver;
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
        LocalLlmService? llmService,
        ISpeechCommandMapper ruleMapper,
        ISpeechCommandMapper? llmMapper,
        LocalLlmCallsignResolver? callsignResolver,
        Func<SpeechContext> contextProvider
    )
    {
        _preferences = preferences;
        _audio = audio;
        _stt = stt;
        _llmService = llmService;
        _ruleMapper = ruleMapper;
        _llmMapper = llmMapper;
        _callsignResolver = callsignResolver;
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
    /// Pre-loads Whisper and LLM weights so the first PTT press doesn't incur a multi-second
    /// stall. Idempotent — safe to call multiple times because each engine guards itself. Fires
    /// <see cref="StatusChanged"/> with <see cref="SpeechStatus.Warming"/> on entry and transitions
    /// back to <see cref="SpeechStatus.Idle"/> on completion (unless another transition raced us —
    /// in that case we leave the current status alone).
    /// </summary>
    public async Task PrewarmAsync(CancellationToken ct)
    {
        if (!_preferences.SpeechEnabled)
        {
            return;
        }

        SetStatus(SpeechStatus.Warming);
        var sw = Stopwatch.StartNew();
        try
        {
            var whisperTask = _stt.PrewarmAsync(ct);
            var llmTask = _llmService?.PrewarmAsync(ct) ?? Task.CompletedTask;
            await Task.WhenAll(whisperTask, llmTask).ConfigureAwait(false);
            sw.Stop();
            Log.LogInformation("Speech pipeline pre-warmed in {Ms} ms", sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            Log.LogInformation("Speech pipeline prewarm cancelled");
        }
        catch (Exception ex)
        {
            // Prewarm must never crash startup — the lazy-load path will retry on first PTT.
            Log.LogWarning(ex, "Speech pipeline prewarm failed; lazy-load will retry on first PTT");
        }
        finally
        {
            lock (_statusLock)
            {
                // Only transition Warming → Idle. If a user PTT fired mid-warmup and moved us
                // to Recording/Transcribing/Mapping, we must not clobber that transition.
                if (_status == SpeechStatus.Warming)
                {
                    _status = SpeechStatus.Idle;
                }
            }
            StatusChanged?.Invoke(Status);
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

        // Warming is treated as start-legal — the engine locks already serialize a user PTT
        // against an in-flight prewarm, which matches the previous lazy-load behavior exactly.
        var current = Status;
        if (current != SpeechStatus.Idle && current != SpeechStatus.Warming)
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
        string? callsign = null;
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
                // Fall through to the common RecordSession path at the end of the method so the
                // debug window still shows empty-transcript sessions. Previously this early-
                // returned and the session disappeared without a trace — indistinguishable from
                // a successful recording with no audio.
                Log.LogInformation("Whisper returned empty transcript");
                outcome = SpeechSessionOutcome.EmptyTranscript;
            }
            else
            {
                transcript = raw.Trim();
                Log.LogInformation("PTT transcript: {Transcript}", transcript);

                SetStatus(SpeechStatus.Mapping);
                var mapSw = Stopwatch.StartNew();
                var mapping = await MapTranscriptAsync(transcript, ctx, _ruleMapper, _llmMapper, _callsignResolver, ct).ConfigureAwait(false);
                mapSw.Stop();
                mapMs = mapSw.ElapsedMilliseconds;

                canonical = mapping.Canonical;
                callsign = mapping.Callsign;
                usedLlmFallback = mapping.UsedLlmFallback;
                outcome = canonical is not null ? SpeechSessionOutcome.CommandAccepted : SpeechSessionOutcome.NoMappingFound;
            }
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
            CommandReady?.Invoke(new SpeechResult(transcript, null, callsign));
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
        CommandReady?.Invoke(new SpeechResult(transcript, canonical, callsign));
    }

    /// <summary>
    /// Runs the full transcript-to-canonical-command mapping stage of the speech pipeline:
    /// callsign extraction → rule mapper → LLM mapper fallback → LLM callsign resolver → partial-
    /// match surfacing. Returns a fully-populated <see cref="TranscriptMapResult"/> regardless of
    /// outcome — <see cref="TranscriptMapResult.Canonical"/> is null only when neither mapper
    /// produced a result and the LLM callsign resolver didn't find a callsign either.
    ///
    /// Exposed as an internal static method so integration tests can drive the mapping pipeline
    /// end-to-end from a literal transcript (e.g. a real Whisper output captured in a debug log)
    /// without having to construct the whole <see cref="SpeechRecognitionService"/>.
    /// </summary>
    internal static async Task<TranscriptMapResult> MapTranscriptAsync(
        string transcript,
        SpeechContext ctx,
        ISpeechCommandMapper ruleMapper,
        ISpeechCommandMapper? llmMapper,
        LocalLlmCallsignResolver? callsignResolver,
        CancellationToken ct
    )
    {
        // Step 1: Extract the callsign up-front and strip it from the transcript so both mappers
        // see a clean command-only string. Previously each mapper did its own extraction —
        // PhraseologyMapper internally, LocalLlmCommandMapper not at all — which meant (a) partial
        // rule-matches lost the callsign when the command didn't parse, and (b) the LLM saw the
        // full noisy transcript plus an "Active callsigns:" line that distracted it into echoing
        // callsigns as output.
        var (commandText, callsign) = ExtractAndStripCallsign(transcript, ctx.ActiveCallsigns);

        var mapContext = new MapContext(ctx.ActiveCallsigns, ctx.ProgrammedFixes) { CustomFixPatterns = ctx.CustomFixPatterns };

        string? canonical = null;
        var usedLlmFallback = false;

        var ruleResult = await ruleMapper.MapAsync(commandText, mapContext, ct).ConfigureAwait(false);
        if (ruleResult is not null)
        {
            canonical = ruleResult.CanonicalCommand;
            // PhraseologyMapper.Map still runs its own extraction pass against the
            // (already-stripped) input; if we somehow missed a callsign at the pipeline layer
            // but the rule mapper found one on a second look, prefer its result.
            callsign ??= ruleResult.Callsign;
            Log.LogInformation("Rule engine mapped transcript to: {Callsign} {Canonical}", callsign ?? "(no callsign)", canonical);
        }
        else if (llmMapper is not null)
        {
            Log.LogInformation("Rule engine returned null, trying LLM fallback");
            var llmResult = await llmMapper.MapAsync(commandText, mapContext, ct).ConfigureAwait(false);
            if (llmResult is not null)
            {
                canonical = llmResult.CanonicalCommand;
                usedLlmFallback = true;
                Log.LogInformation("LLM fallback mapped transcript to: {Callsign} {Canonical}", callsign ?? "(no callsign)", canonical);
            }
            else
            {
                Log.LogInformation("LLM fallback also returned null");
            }
        }

        // Callsign fallback: we have a canonical command but no callsign. This happens when
        // Whisper mistranscribes the spoken callsign beyond what CallsignParser can recover
        // ("diner" for "niner", merged words, dropped digits, etc.). Ask the LLM to disambiguate
        // from the active-callsign list — it tolerates noisy inputs and its output is validated
        // against the active list, so it can't hallucinate a phantom callsign that doesn't exist
        // in the scenario.
        if (canonical is not null && callsign is null && callsignResolver is not null && ctx.ActiveCallsigns.Count > 0)
        {
            Log.LogInformation("Rule-based callsign extraction failed, trying LLM resolver");
            callsign = await callsignResolver.ResolveAsync(transcript, ctx.ActiveCallsigns, ct).ConfigureAwait(false);
        }

        // If both mappers failed but we did extract a callsign, surface the partial result:
        // "N346G turn left hitting 310" lets the user manually correct "hitting" → "heading"
        // and press Enter, rather than losing the callsign context entirely.
        if (canonical is null && callsign is not null && !string.IsNullOrWhiteSpace(commandText))
        {
            canonical = commandText.Trim();
            Log.LogInformation("Both mappers failed; surfacing extracted callsign {Callsign} + raw command text", callsign);
        }

        return new TranscriptMapResult(commandText, canonical, callsign, usedLlmFallback);
    }

    /// <summary>
    /// Extract a callsign from the transcript and return both the extracted ICAO form and the
    /// transcript with the callsign tokens removed. Runs on a <see cref="AtcNumberParser.NormalizeDigits"/>-
    /// pre-processed copy of the transcript so rule-engine and LLM-mapper inputs are consistent.
    /// Returns the original transcript unchanged (with just digit normalization) and a null
    /// callsign when no leading or trailing callsign was found.
    /// </summary>
    internal static (string CommandText, string? Callsign) ExtractAndStripCallsign(string transcript, IReadOnlyCollection<string> activeCallsigns)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return (transcript ?? string.Empty, null);
        }

        // Normalize digit words ("three" → "3") once so downstream mappers see consistent input.
        // NormalizeDigits returns a space-joined, lowercased, punctuation-free string.
        var normalized = AtcNumberParser.NormalizeDigits(transcript);
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return (normalized, null);
        }

        var leading = CallsignParser.TryParseLeading(normalized, activeCallsigns);
        if (leading is not null && leading.TokensConsumed > 0 && leading.TokensConsumed <= tokens.Length)
        {
            var stripped = string.Join(' ', tokens.Skip(leading.TokensConsumed));
            return (stripped, leading.IcaoCallsign);
        }

        var trailing = CallsignParser.TryParseTrailing(normalized, activeCallsigns);
        if (trailing is not null && trailing.TokensConsumed > 0 && trailing.TokensConsumed <= tokens.Length)
        {
            var stripped = string.Join(' ', tokens.Take(tokens.Length - trailing.TokensConsumed));
            return (stripped, trailing.IcaoCallsign);
        }

        return (normalized, null);
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

/// <summary>
/// Output of <see cref="SpeechRecognitionService.MapTranscriptAsync"/>. Captures every signal
/// the full mapping stage produces so callers (the live pipeline and integration tests) can
/// inspect which path a transcript took and what came out. <see cref="Canonical"/> is null only
/// when neither mapper produced output and no callsign was extracted either.
/// </summary>
/// <param name="CommandText">Transcript with callsign tokens stripped and digits normalized — the exact string handed to the rule + LLM mappers.</param>
/// <param name="Canonical">Final canonical command, or null on total failure. Falls back to the raw <see cref="CommandText"/> when a callsign was extracted but no mapper matched.</param>
/// <param name="Callsign">Extracted ICAO callsign (rule parser or LLM callsign resolver), or null if none recovered.</param>
/// <param name="UsedLlmFallback">True when the rule mapper returned null and the LLM command mapper produced the final canonical.</param>
internal sealed record TranscriptMapResult(string CommandText, string? Canonical, string? Callsign, bool UsedLlmFallback);

/// <summary>Result bundle passed to <see cref="SpeechRecognitionService.CommandReady"/>.</summary>
/// <param name="Transcript">Raw Whisper transcript (may be empty on failure).</param>
/// <param name="CanonicalCommand">Canonical YAAT command, or null when neither mapper produced a result.</param>
/// <param name="Callsign">
/// Extracted ICAO callsign (e.g. "SWA123") if present in the transcript and matched against the
/// active callsigns at PTT time; null otherwise. Consumers prepend this to the canonical command
/// so <see cref="MainViewModel.SendCommandAsync"/>'s existing <c>TryResolveCallsignPrefix</c> path
/// auto-dispatches to the right aircraft.
/// </param>
public sealed record SpeechResult(string Transcript, string? CanonicalCommand, string? Callsign);
