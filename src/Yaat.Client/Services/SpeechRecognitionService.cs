using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Services.Speech;
using Yaat.Sim;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Speech;
using Yaat.Sim.Speech.Context;

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

    /// <summary>
    /// The speech-recognition model the preferences name is not downloaded, so a push-to-talk press was refused. Treated
    /// as start-legal by <see cref="SpeechRecognitionService.StartPtt"/>, so a press after the download records.
    /// </summary>
    ModelMissing,
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
)
{
    /// <summary>
    /// Per-stage pipeline trace. Populated for every session that reached the transcription stage.
    /// Null for very early failures (e.g. zero-sample buffer at <see cref="SpeechRecognitionService.StopPtt"/>).
    /// The redesigned Speech Debug window expands the trace into a master-detail view; the opt-in
    /// sample store also serializes it alongside the recorded audio.
    /// </summary>
    public SpeechSessionTrace? Trace { get; init; }

    /// <summary>
    /// Stable identifier used to correlate this in-memory session with a persisted on-disk sample
    /// (see <c>SpeechSampleStore</c>). Null when capture was off at the time of the press, so the
    /// debug UI can grey out the "Play audio" / "Export" affordances for that row.
    /// </summary>
    public string? SampleId { get; init; }
}

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

    /// <summary>
    /// Airport code → list of runway designators for every airport relevant to the active scenario
    /// (destinations + departures of currently-active aircraft). Passed through to
    /// <see cref="MapContext.AvailableRunways"/> so the rule engine can validate <c>{rwy}</c> captures
    /// and the LLM fallback can recover misheard runways from scenario knowledge.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> AvailableRunways { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// ICAO callsign → destination airport code for every active aircraft with a flight plan.
    /// Passed through to <see cref="MapContext.AircraftDestinations"/> so the LLM fallback can
    /// correlate an in-transcript callsign with the right airport's runways.
    /// </summary>
    public IReadOnlyDictionary<string, string> AircraftDestinations { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Uppercase taxiway-name set for the ground layout of the aircraft being commanded. Passed
    /// through to <see cref="MapContext.TaxiwayNames"/> so <see cref="NatoLetterNormalizer"/>
    /// can disambiguate multi-letter taxiway names from adjacent single-letter taxiways when
    /// collapsing NATO phonetic runs in the transcript. Empty when no ground layout is loaded.
    /// </summary>
    public IReadOnlySet<string> TaxiwayNames { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Uppercase parking / spot / helipad names of the loaded ground layout. Passed through to
    /// <see cref="MapContext.DestinationNames"/> so a spoken taxi destination is validated against
    /// the airport. Empty when no ground layout is loaded.
    /// </summary>
    public IReadOnlySet<string> DestinationNames { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// SID / STAR procedure patterns for every airport relevant to the active scenario
    /// (filed departures + destinations + primary scenario airport). Passed through to
    /// <see cref="MapContext.Procedures"/> so <see cref="SidStarNameNormalizer"/> can collapse
    /// multi-token spoken procedure names ("eagul five") into canonical IDs ("EAGUL5") via
    /// <see cref="PhoneticFixMatcher"/>'s fuzzy matching, and so the procedure-capture validation
    /// post-pass in <see cref="PhraseologyMapper"/> rejects rule matches whose {sid}/{star}
    /// capture isn't a real procedure in scope.
    /// </summary>
    public IReadOnlyList<ProcedurePattern> Procedures { get; init; } = [];

    /// <summary>
    /// The room the accurate speech mode decodes against, captured on the UI thread at push-to-talk press; null for a typed
    /// natural command, and when the client knows no room airport. <see cref="RoomSnapshot.ToRequest"/> turns it into the
    /// decoder's <see cref="RoomContextRequest"/> off the UI thread.
    /// </summary>
    public RoomSnapshot? Room { get; init; }
}

/// <summary>One room aircraft as captured on the UI thread: plain fields, its programmed fixes not yet resolved.</summary>
/// <param name="Callsign">The ICAO callsign ("SWA2495").</param>
/// <param name="AircraftType">The bare ICAO type designator ("B738"), or null when unknown.</param>
/// <param name="AssignedRunway">The runway the aircraft is assigned at the room's airport ("28R"), or null.</param>
/// <param name="Route">The filed route.</param>
/// <param name="ExpectedApproach">The expected approach, or null.</param>
/// <param name="Destination">The destination airport.</param>
/// <param name="Departure">The departure airport.</param>
/// <param name="ActiveStarId">The active STAR.</param>
/// <param name="DestinationRunway">The destination runway.</param>
public sealed record RoomAircraftSnapshot(
    string Callsign,
    string? AircraftType,
    string? AssignedRunway,
    string? Route,
    string? ExpectedApproach,
    string? Destination,
    string? Departure,
    string? ActiveStarId,
    string? DestinationRunway
);

/// <summary>The room as captured on the UI thread at push-to-talk press.</summary>
/// <param name="AirportIcao">The room's airport ("KOAK").</param>
/// <param name="ApproachPosition">Whether the active position is a TRACON or ARTCC position.</param>
/// <param name="Layout">The room airport's ground layout, or null when none is loaded for it.</param>
/// <param name="Aircraft">The room's aircraft.</param>
/// <param name="FacilityRadioNames">The radio names of the active position and of every online controller.</param>
public sealed record RoomSnapshot(
    string AirportIcao,
    bool ApproachPosition,
    AirportGroundLayout? Layout,
    IReadOnlyList<RoomAircraftSnapshot> Aircraft,
    IReadOnlyList<string> FacilityRadioNames
)
{
    /// <summary>
    /// The decoder's request for <paramref name="room"/>, each aircraft's programmed fixes resolved with
    /// <see cref="ProgrammedFixResolver.Resolve"/>. Reads the navigation database, so it runs off the UI thread.
    /// </summary>
    /// <param name="room">The captured room.</param>
    /// <returns>The request <see cref="RoomContextFactory.Build"/> takes.</returns>
    public static RoomContextRequest ToRequest(RoomSnapshot room)
    {
        RoomAircraftInput[] aircraft =
        [
            .. room.Aircraft.Select(a => new RoomAircraftInput(
                a.Callsign,
                a.AircraftType,
                [
                    .. ProgrammedFixResolver.Resolve(
                        a.Route,
                        a.ExpectedApproach,
                        a.Destination,
                        a.Departure,
                        null,
                        a.ActiveStarId,
                        a.DestinationRunway
                    ),
                ],
                a.AssignedRunway
            )),
        ];
        return new RoomContextRequest(room.AirportIcao, room.ApproachPosition, room.Layout, aircraft, room.FacilityRadioNames);
    }
}

/// <summary>
/// Orchestrates the full push-to-talk pipeline:
/// <list type="number">
///   <item><description><see cref="AudioCaptureService"/> records 16 kHz mono Float32 samples while PTT is held.</description></item>
///   <item><description>The configured <see cref="ISttEngine"/> transcribes the captured samples, with a seeded
///     biasing prompt (active callsigns + programmed fixes) for an engine that takes one.</description></item>
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
public sealed class SpeechRecognitionService(
    UserPreferences preferences,
    IAudioCapture audio,
    ISttEngine stt,
    LocalLlmService? llmService,
    ISpeechCommandMapper ruleMapper,
    ISpeechCommandMapper? llmMapper,
    LocalLlmCallsignResolver? callsignResolver,
    Func<SpeechContext> contextProvider,
    SpeechSampleStore? sampleStore,
    Func<bool> isModelReady
) : IDisposable
{
    private static readonly ILogger Log = AppLog.CreateLogger<SpeechRecognitionService>();

    private readonly UserPreferences _preferences = preferences;
    private readonly IAudioCapture _audio = audio;
    private readonly ISttEngine _stt = stt;
    private readonly LocalLlmService? _llmService = llmService;
    private readonly ISpeechCommandMapper _ruleMapper = ruleMapper;
    private readonly ISpeechCommandMapper? _llmMapper = llmMapper;
    private readonly LocalLlmCallsignResolver? _callsignResolver = callsignResolver;
    private readonly Func<SpeechContext> _contextProvider = contextProvider;
    private readonly SpeechSampleStore? _sampleStore = sampleStore;
    private readonly Func<bool> _isModelReady = isModelReady;

    // One for the service's life, so an airport's entities are built once and reused across presses.
    private readonly RoomContextCache _roomCache = new();

    private SpeechStatus _status = SpeechStatus.Idle;
    private CancellationTokenSource? _pendingCts;
    private readonly Lock _statusLock = new();

    // Most recent PTT sessions for the debug window. Newest entries at index 0 (insertion point).
    // Capped at MaxSessionHistory — older entries are dropped as new ones arrive.
    public const int MaxSessionHistory = 20;
    public ObservableCollection<SpeechSession> SessionHistory { get; } = [];

    /// <summary>Raised on the thread pool after a session is appended to <see cref="SessionHistory"/>.
    /// UI consumers should marshal to the UI thread before touching the collection.</summary>
    public event Action<SpeechSession>? SessionRecorded;

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
    /// <param name="prepareEngine">
    /// Runs first, under <see cref="SpeechStatus.Warming"/>, before the engine warms up: the caller's chance to swap in
    /// the engine the warm-up should load. A failure is logged and the warm-up runs on the engine already in place.
    /// </param>
    /// <param name="ct">Cancels the warm-up.</param>
    public async Task PrewarmAsync(Func<Task> prepareEngine, CancellationToken ct)
    {
        if (!_preferences.SpeechEnabled)
        {
            return;
        }

        SetStatus(SpeechStatus.Warming);
        await WarmAsync(prepareEngine, ct).ConfigureAwait(false);
    }

    /// <summary>Whether speech recognition is switched on in the preferences; with it off nothing warms or records.</summary>
    public bool IsSpeechEnabled => _preferences.SpeechEnabled;

    /// <summary>
    /// Moves the status to <see cref="SpeechStatus.Warming"/> only from a resting status (<see cref="SpeechStatus.Idle"/>,
    /// <see cref="SpeechStatus.ModelMissing"/> or <see cref="SpeechStatus.Error"/>), atomically with the check, so a
    /// push-to-talk that reached <see cref="SpeechStatus.Recording"/> first is never overwritten. Raises
    /// <see cref="StatusChanged"/> after leaving the status lock.
    /// </summary>
    /// <returns>True when the status is now Warming and the caller must run <see cref="WarmAsync"/>; false when the mic is busy.</returns>
    public bool TryBeginWarming()
    {
        lock (_statusLock)
        {
            if (_status is not (SpeechStatus.Idle or SpeechStatus.ModelMissing or SpeechStatus.Error))
            {
                return false;
            }

            _status = SpeechStatus.Warming;
        }

        StatusChanged?.Invoke(SpeechStatus.Warming);
        return true;
    }

    /// <summary>
    /// The warm-up of <see cref="PrewarmAsync"/>, entered with the status already <see cref="SpeechStatus.Warming"/> (by
    /// <see cref="PrewarmAsync"/> or <see cref="TryBeginWarming"/>); moves it back to <see cref="SpeechStatus.Idle"/> unless
    /// another transition raced it.
    /// </summary>
    /// <param name="prepareEngine">Runs first, before the engine warms up; a failure is logged and the warm-up goes on.</param>
    /// <param name="ct">Cancels the warm-up.</param>
    public async Task WarmAsync(Func<Task> prepareEngine, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            try
            {
                await prepareEngine().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.LogWarning(ex, "Preparing the speech-recognition engine failed; warming up the one in place");
            }
            Task sttTask = Task.CompletedTask;
            if (_isModelReady())
            {
                sttTask = _stt.WarmUpAsync(ct);
            }
            else
            {
                Log.LogInformation("Speech-recognition model not downloaded; skipping its warm-up");
            }
            Task llmTask = _llmService?.PrewarmAsync(ct) ?? Task.CompletedTask;
            await Task.WhenAll(sttTask, llmTask).ConfigureAwait(false);
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
        // ModelMissing is too: the model may have been downloaded since the refused press.
        SpeechStatus current = Status;
        if ((current != SpeechStatus.Idle) && (current != SpeechStatus.Warming) && (current != SpeechStatus.ModelMissing))
        {
            return false;
        }

        if (!_isModelReady())
        {
            Log.LogInformation("PTT refused: the speech-recognition model is not downloaded");
            SetStatus(SpeechStatus.ModelMissing);
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

        float[] samples = _audio.StopCapture();
        if (samples.Length == 0)
        {
            Log.LogInformation("PTT ended with zero samples; resetting to idle");
            SetStatus(SpeechStatus.Idle);
            return;
        }

        _pendingCts?.Cancel();
        _pendingCts = new CancellationTokenSource();
        CancellationToken ct = _pendingCts.Token;

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
        long transcribeMs = 0L;
        long mapMs = 0L;
        string transcript = string.Empty;
        string? canonical = null;
        string? callsign = null;
        bool usedLlmFallback = false;
        SpeechSessionOutcome outcome = SpeechSessionOutcome.Error;
        string? errorMessage = null;
        SpeechSessionTrace? trace = null;

        double audioDurationSec = (double)samples.Length / AudioCaptureService.SampleRate;

        try
        {
            SetStatus(SpeechStatus.Transcribing);
            SpeechContext ctx = _contextProvider();

            var transcribeSw = Stopwatch.StartNew();
            SttHypotheses hyps = await _stt.TranscribeAsync(samples, ctx.WhisperInitialPrompt, ct).ConfigureAwait(false);
            string? raw = hyps.Text;
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
                trace = BuildTrace(
                    ctx,
                    rawTranscript: raw ?? string.Empty,
                    callsignStripped: string.Empty,
                    callsign: null,
                    RuleMapperTrace.Empty,
                    null,
                    PressDecodeTrace.Fast(null)
                );
            }
            else
            {
                transcript = raw.Trim();
                Log.LogInformation("PTT transcript: {Transcript}", transcript);

                SetStatus(SpeechStatus.Mapping);
                var mapSw = Stopwatch.StartNew();
                PressMapping pressed = await MapPressAsync(hyps, transcript, ctx, ct).ConfigureAwait(false);
                mapSw.Stop();
                mapMs = mapSw.ElapsedMilliseconds;

                TranscriptMapResult mapping = pressed.Result;
                canonical = mapping.Canonical;
                callsign = mapping.Callsign;
                usedLlmFallback = mapping.UsedLlmFallback;
                outcome = canonical is not null ? SpeechSessionOutcome.CommandAccepted : SpeechSessionOutcome.NoMappingFound;
                trace = BuildTrace(
                    ctx,
                    rawTranscript: transcript,
                    callsignStripped: mapping.CommandText,
                    callsign: mapping.Callsign,
                    pressed.RuleTrace,
                    pressed.LlmTrace,
                    pressed.Decode
                );
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
                errorMessage,
                trace,
                samples
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
            errorMessage,
            trace,
            samples
        );
        CommandReady?.Invoke(new SpeechResult(transcript, canonical, callsign));
    }

    private static SpeechSessionTrace BuildTrace(
        SpeechContext ctx,
        string rawTranscript,
        string callsignStripped,
        string? callsign,
        RuleMapperTrace ruleTrace,
        LlmMapperTrace? llmTrace,
        PressDecodeTrace decode
    )
    {
        return new SpeechSessionTrace(
            WhisperBiasingPrompt: ctx.WhisperInitialPrompt,
            RawTranscript: rawTranscript,
            CallsignStrippedTranscript: callsignStripped,
            CallsignExtracted: callsign,
            Rule: ruleTrace,
            Llm: llmTrace,
            ActiveCallsigns: [.. ctx.ActiveCallsigns],
            ProgrammedFixes: [.. ctx.ProgrammedFixes],
            AvailableRunwaysByAirport: ctx.AvailableRunways,
            TaxiwayNames: [.. ctx.TaxiwayNames],
            AircraftDestinations: ctx.AircraftDestinations,
            Mode: decode.Mode,
            NBest: decode.NBest,
            DecodeState: decode.State,
            BestGuess: decode.BestGuess,
            DecodeMs: decode.DecodeMs,
            DecodeError: decode.DecodeError
        );
    }

    /// <summary>
    /// Maps one press's transcription. An accurate engine with a room and a non-empty n-best decodes the n-best against the
    /// room; every other press, and an accurate press whose room build or decode throws, maps hypothesis 0 on the fast path.
    /// </summary>
    private async Task<PressMapping> MapPressAsync(SttHypotheses hyps, string transcript, SpeechContext ctx, CancellationToken ct)
    {
        if ((_stt.Mode == TranscriptionMode.Accurate) && (ctx.Room is { } room) && (hyps.Entries.Count > 0))
        {
            try
            {
                return await MapAccurateAsync(hyps, room, ctx, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.LogWarning(ex, "Accurate decode failed for room airport {Airport}; mapping the press on the fast path", room.AirportIcao);
                return await MapFastAsync(transcript, ctx, ex.Message, ct).ConfigureAwait(false);
            }
        }

        return await MapFastAsync(transcript, ctx, null, ct).ConfigureAwait(false);
    }

    /// <summary>Builds the room's transmission context on this (pool) thread and decodes the n-best against it.</summary>
    private async Task<PressMapping> MapAccurateAsync(SttHypotheses hyps, RoomSnapshot room, SpeechContext ctx, CancellationToken ct)
    {
        var buildSw = Stopwatch.StartNew();
        TransmissionContext context = RoomContextFactory.Build(
            _roomCache,
            RoomSnapshot.ToRequest(room),
            NavigationDatabase.Instance,
            DecoderSettings.Default
        );
        buildSw.Stop();

        HypothesesMapResult mapped = await MapHypothesesWithTraceAsync(
                hyps,
                new AccurateDecodeInput(context, DecoderSettings.Default),
                ctx,
                new SpeechMappers(_ruleMapper, _llmMapper, _callsignResolver),
                ct
            )
            .ConfigureAwait(false);

        var decode = new PressDecodeTrace(
            TranscriptionMode.Accurate,
            [.. hyps.Entries.Select(e => new SpeechTraceHypothesis(e.Text, e.Score))],
            mapped.Decode.State,
            mapped.Decode.BestGuess?.Entity.Value,
            buildSw.Elapsed.TotalMilliseconds + mapped.DecodeMs,
            null
        );
        return new PressMapping(mapped.Result, mapped.RuleTrace, mapped.LlmTrace, decode);
    }

    /// <summary>Maps hypothesis 0's transcript on the fast path; <paramref name="decodeError"/> marks an accurate decode that failed.</summary>
    private async Task<PressMapping> MapFastAsync(string transcript, SpeechContext ctx, string? decodeError, CancellationToken ct)
    {
        (TranscriptMapResult result, RuleMapperTrace ruleTrace, LlmMapperTrace? llmTrace) = await MapTranscriptWithTraceAsync(
                transcript,
                ctx,
                _ruleMapper,
                _llmMapper,
                _callsignResolver,
                ct
            )
            .ConfigureAwait(false);
        return new PressMapping(result, ruleTrace, llmTrace, PressDecodeTrace.Fast(decodeError));
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
        (TranscriptMapResult? result, RuleMapperTrace _, LlmMapperTrace? _) = await MapTranscriptWithTraceAsync(
                transcript,
                ctx,
                ruleMapper,
                llmMapper,
                callsignResolver,
                ct
            )
            .ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Trace-collecting variant of <see cref="MapTranscriptAsync"/>. Returns the same
    /// <see cref="TranscriptMapResult"/> plus the rule-mapper and LLM-mapper traces so the speech
    /// debug pipeline can surface per-stage diagnostics on every push-to-talk. The LLM trace is
    /// null when the rule mapper succeeded (and therefore the LLM never ran). The rule trace is
    /// always present, even on miss — it carries the failure reason.
    ///
    /// When a caller passes <see cref="ISpeechCommandMapper"/> implementations that don't expose
    /// a trace-collecting overload (e.g. test stubs), the trace falls back to the empty values so
    /// callers don't have to special-case mock mappers.
    /// </summary>
    internal static async Task<(TranscriptMapResult Result, RuleMapperTrace RuleTrace, LlmMapperTrace? LlmTrace)> MapTranscriptWithTraceAsync(
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
        (string commandText, string? callsign) = ExtractAndStripCallsign(transcript, ctx.ActiveCallsigns);
        return await MapExtractedAsync(transcript, (commandText, callsign), ctx, new SpeechMappers(ruleMapper, llmMapper, callsignResolver), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// What both mapping entries do once the callsign is extracted: the rule mapper on the stripped command text, the LLM
    /// fallback when the rule mapper misses, the LLM callsign resolver when a canonical command lost its callsign, and the raw
    /// text beside an extracted callsign when both mappers miss.
    /// </summary>
    /// <param name="transcript">The transcript the LLM callsign resolver reads.</param>
    /// <param name="extracted">The command text with its callsign stripped, and the ICAO callsign when one was found.</param>
    /// <param name="ctx">The speech snapshot taken at PTT press.</param>
    /// <param name="mappers">The rule mapper, the optional LLM fallback and the optional callsign resolver.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The mapping result with the rule-mapper and LLM-mapper traces.</returns>
    private static async Task<(TranscriptMapResult Result, RuleMapperTrace RuleTrace, LlmMapperTrace? LlmTrace)> MapExtractedAsync(
        string transcript,
        (string CommandText, string? Callsign) extracted,
        SpeechContext ctx,
        SpeechMappers mappers,
        CancellationToken ct
    )
    {
        (string commandText, string? callsign) = extracted;
        ISpeechCommandMapper ruleMapper = mappers.Rule;
        ISpeechCommandMapper? llmMapper = mappers.Llm;
        LocalLlmCallsignResolver? callsignResolver = mappers.CallsignResolver;

        var mapContext = new MapContext(ctx.ActiveCallsigns, ctx.ProgrammedFixes)
        {
            CustomFixPatterns = ctx.CustomFixPatterns,
            AvailableRunways = ctx.AvailableRunways,
            AircraftDestinations = ctx.AircraftDestinations,
            TaxiwayNames = ctx.TaxiwayNames,
            DestinationNames = ctx.DestinationNames,
            Procedures = ctx.Procedures,
        };

        string? canonical = null;
        bool usedLlmFallback = false;
        RuleMapperTrace ruleTrace;
        LlmMapperTrace? llmTrace = null;

        MapResult? ruleResult;
        if (ruleMapper is PhraseologyCommandMapper phr)
        {
            (ruleResult, ruleTrace) = await phr.MapWithTraceAsync(commandText, mapContext, ct).ConfigureAwait(false);
        }
        else
        {
            // Test/mock implementations of ISpeechCommandMapper don't expose the trace overload;
            // fall back to the plain interface call and emit an empty trace so the rest of the
            // pipeline is uniform.
            ruleResult = await ruleMapper.MapAsync(commandText, mapContext, ct).ConfigureAwait(false);
            ruleTrace = RuleMapperTrace.Empty with { FailureReason = ruleResult is null ? "no rule matched" : null };
        }

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
            MapResult? llmResult;
            if (llmMapper is LocalLlmCommandMapper local)
            {
                (llmResult, llmTrace) = await local.MapWithTraceAsync(commandText, mapContext, ct).ConfigureAwait(false);
            }
            else
            {
                llmResult = await llmMapper.MapAsync(commandText, mapContext, ct).ConfigureAwait(false);
                llmTrace = new LlmMapperTrace(
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    llmResult?.CanonicalCommand,
                    llmResult is null ? "LLM mapper returned null" : null
                );
            }

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
        bool isRawTextFallback = false;
        if (canonical is null && callsign is not null && !string.IsNullOrWhiteSpace(commandText))
        {
            canonical = commandText.Trim();
            isRawTextFallback = true;
            Log.LogInformation("Both mappers failed; surfacing extracted callsign {Callsign} + raw command text", callsign);
        }

        return (new TranscriptMapResult(commandText, canonical, callsign, usedLlmFallback, isRawTextFallback), ruleTrace, llmTrace);
    }

    /// <summary>
    /// Maps an STT n-best the accurate way: every hypothesis is decoded against the room's transmission context, so an
    /// aircraft the transcript names is resolved from the room rather than from the active-callsign list alone. A resolved
    /// decode names the callsign itself and makes the decoder's rewritten transcript minus the callsign span the command
    /// text, digit normalized; a <see cref="DecodeState.SayAgain"/> or <see cref="DecodeState.NoCallsign"/> decode falls back
    /// to the callsign parser on the decoder's rewritten transcript, so accurate mode never resolves fewer callsigns than the
    /// fast path. An n-best with no entry is mapped as an empty transcript without a decode.
    /// </summary>
    /// <param name="hypotheses">The engine's alternatives, best first.</param>
    /// <param name="decode">The room's transmission context and the decoder settings.</param>
    /// <param name="ctx">The speech snapshot taken at PTT press.</param>
    /// <param name="mappers">The rule mapper, the optional LLM fallback and the optional callsign resolver.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The mapping result, both traces, and the decode the callsign came from.</returns>
    internal static async Task<HypothesesMapResult> MapHypothesesWithTraceAsync(
        SttHypotheses hypotheses,
        AccurateDecodeInput decode,
        SpeechContext ctx,
        SpeechMappers mappers,
        CancellationToken ct
    )
    {
        var decodeSw = Stopwatch.StartNew();
        DecodeResult decoded = DecodeHypotheses(hypotheses, decode);
        decodeSw.Stop();
        string transcript = decoded.RewrittenTranscript;
        (string CommandText, string? Callsign) extracted =
            (decoded.State == DecodeState.Resolved) ? ResolvedCommandText(decoded) : ExtractAndStripCallsign(transcript, ctx.ActiveCallsigns);

        Log.LogDebug(
            "Accurate decode of {Count} hypotheses: {State} on hypothesis {Index}, callsign {Callsign}",
            hypotheses.Entries.Count,
            decoded.State,
            decoded.HypothesisIndex,
            decoded.Callsign?.Entity.Value ?? "(none)"
        );

        (TranscriptMapResult result, RuleMapperTrace ruleTrace, LlmMapperTrace? llmTrace) = await MapExtractedAsync(
                transcript,
                extracted,
                ctx,
                mappers,
                ct
            )
            .ConfigureAwait(false);
        return new HypothesesMapResult(result, ruleTrace, llmTrace, decoded, decodeSw.Elapsed.TotalMilliseconds);
    }

    /// <summary>The n-best decoded against the room, or a no-callsign result without a decode when there is no hypothesis to decode.</summary>
    private static DecodeResult DecodeHypotheses(SttHypotheses hypotheses, AccurateDecodeInput decode)
    {
        if (hypotheses.Entries.Count == 0)
        {
            return new DecodeResult(DecodeState.NoCallsign, null, null, [], string.Empty, string.Empty, 0, 0, false);
        }

        DecoderInput input = new([.. hypotheses.Entries.Select(SttDecoderInput.ToDecoderHypothesis)], InputSource.Spoken, MatchMode.Fuzzy);
        return ContextDecoder.Decode(input, decode.Context, decode.Settings);
    }

    /// <summary>
    /// The command text a resolved decode leaves, beside the callsign the decoder resolved: its remainder, digit-normalized.
    /// </summary>
    /// <exception cref="InvalidOperationException">The decode resolved without naming a callsign, which the decoder never does.</exception>
    private static (string CommandText, string? Callsign) ResolvedCommandText(DecodeResult decoded)
    {
        EntityMatch callsign =
            decoded.Callsign
            ?? throw new InvalidOperationException($"The decode resolved without a callsign on hypothesis {decoded.HypothesisIndex}.");
        return (AtcNumberParser.NormalizeDigits(decoded.Remainder), callsign.Entity.Value);
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
        string normalized = AtcNumberParser.NormalizeDigits(transcript);
        string[] tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return (normalized, null);
        }

        CallsignParser.ParsedCallsign? leading = CallsignParser.TryParseLeading(normalized, activeCallsigns);
        if (leading is not null && leading.TokensConsumed > 0 && leading.TokensConsumed <= tokens.Length)
        {
            string stripped = string.Join(' ', tokens.Skip(leading.TokensConsumed));
            return (stripped, leading.IcaoCallsign);
        }

        CallsignParser.ParsedCallsign? trailing = CallsignParser.TryParseTrailing(normalized, activeCallsigns);
        if (trailing is not null && trailing.TokensConsumed > 0 && trailing.TokensConsumed <= tokens.Length)
        {
            string stripped = string.Join(' ', tokens.Take(tokens.Length - trailing.TokensConsumed));
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
        string? errorMessage,
        SpeechSessionTrace? trace,
        float[] audioSamples
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
        )
        {
            Trace = trace,
        };

        // Opt-in persistence — store call is gated on both the preference toggle and the presence
        // of trace data (no point persisting a session with no pipeline visibility). The store
        // returns a sample id we splice back into the session so the debug UI can correlate the
        // in-memory entry with the on-disk WAV for playback / export.
        if (trace is not null && _sampleStore is not null && _preferences.SpeechSampleCaptureEnabled && audioSamples.Length > 0)
        {
            string? sampleId = _sampleStore.Add(session, audioSamples, queueForUpload: _preferences.SpeechTelemetryEnabled);
            if (sampleId is not null)
            {
                session = session with { SampleId = sampleId };
            }
        }

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
/// <param name="IsRawTextFallback">True when both mappers failed and <see cref="Canonical"/> is the raw <see cref="CommandText"/> surfaced beside the extracted callsign.</param>
internal sealed record TranscriptMapResult(string CommandText, string? Canonical, string? Callsign, bool UsedLlmFallback, bool IsRawTextFallback);

/// <summary>The mappers a transcript is mapped through, bundled so both mapping entries take the same set.</summary>
/// <param name="Rule">The rule mapper every transcript goes through first.</param>
/// <param name="Llm">The LLM fallback mapper, or null where the deployment has none.</param>
/// <param name="CallsignResolver">The LLM callsign resolver, or null where the deployment has none.</param>
internal sealed record SpeechMappers(ISpeechCommandMapper Rule, ISpeechCommandMapper? Llm, LocalLlmCallsignResolver? CallsignResolver);

/// <summary>What an accurate-mode decode runs against.</summary>
/// <param name="Context">The room's transmission context: its aircraft and the airport's entities.</param>
/// <param name="Settings">The decoder's thresholds and weights.</param>
internal sealed record AccurateDecodeInput(TransmissionContext Context, DecoderSettings Settings);

/// <summary>An accurate-mode mapping: what the mappers made of the n-best and how the decoder settled the callsign.</summary>
/// <param name="Result">The mapped transcript.</param>
/// <param name="RuleTrace">The rule mapper's trace; it carries the failure reason on a miss.</param>
/// <param name="LlmTrace">The LLM mapper's trace, or null when the rule mapper succeeded.</param>
/// <param name="Decode">The decoder's answer for the n-best.</param>
/// <param name="DecodeMs">Wall-clock milliseconds the decode took, mapping excluded.</param>
internal sealed record HypothesesMapResult(
    TranscriptMapResult Result,
    RuleMapperTrace RuleTrace,
    LlmMapperTrace? LlmTrace,
    DecodeResult Decode,
    double DecodeMs
);

/// <summary>One press's mapping in the live pipeline, with what its trace records of the path it took.</summary>
/// <param name="Result">The mapped transcript.</param>
/// <param name="RuleTrace">The rule mapper's trace.</param>
/// <param name="LlmTrace">The LLM mapper's trace, or null when the rule mapper succeeded.</param>
/// <param name="Decode">The transcription path and, on the accurate path, the decode.</param>
internal sealed record PressMapping(TranscriptMapResult Result, RuleMapperTrace RuleTrace, LlmMapperTrace? LlmTrace, PressDecodeTrace Decode);

/// <summary>The trace fields a press's transcription path fills.</summary>
/// <param name="Mode">The path the press was mapped on.</param>
/// <param name="NBest">The n-best the decoder read, or null on the fast path.</param>
/// <param name="State">How the decoder settled the callsign, or null on the fast path.</param>
/// <param name="BestGuess">The decoder's best-guess callsign, or null when it has none or did not run.</param>
/// <param name="DecodeMs">Milliseconds the room context build and the decode took, or null on the fast path.</param>
/// <param name="DecodeError">The exception message of an accurate decode that failed over to the fast path, or null.</param>
internal sealed record PressDecodeTrace(
    TranscriptionMode Mode,
    IReadOnlyList<SpeechTraceHypothesis>? NBest,
    DecodeState? State,
    string? BestGuess,
    double? DecodeMs,
    string? DecodeError
)
{
    /// <summary>A fast-path press: no decode fields, and the failure message when an accurate decode fell back.</summary>
    public static PressDecodeTrace Fast(string? decodeError) => new(TranscriptionMode.Fast, null, null, null, null, decodeError);
}

/// <summary>Result bundle passed to <see cref="SpeechRecognitionService.CommandReady"/>.</summary>
/// <param name="Transcript">Raw Whisper transcript (may be empty on failure).</param>
/// <param name="CanonicalCommand">Canonical YAAT command, or null when neither mapper produced a result.</param>
/// <param name="Callsign">
/// Extracted ICAO callsign (e.g. "SWA123") if present in the transcript and matched against the
/// active callsigns at PTT time; null otherwise. Consumers prepend this to the canonical command
/// so <see cref="MainViewModel.SendCommandAsync"/>'s existing <see cref="CallsignPrefixResolver"/>
/// path auto-dispatches to the right aircraft.
/// </param>
public sealed record SpeechResult(string Transcript, string? CanonicalCommand, string? Callsign);
