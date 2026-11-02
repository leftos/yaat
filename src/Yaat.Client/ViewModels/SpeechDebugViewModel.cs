using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Services.Speech;
using Yaat.Sim.Speech;
using Yaat.Sim.Speech.Context;

namespace Yaat.Client.ViewModels;

/// <summary>
/// ViewModel for the redesigned <c>SpeechDebugWindow</c>. Merges the in-memory
/// <see cref="SpeechRecognitionService.SessionHistory"/> (last 20 sessions, always populated) with
/// the disk-backed <see cref="SpeechSampleStore.Entries"/> (opt-in, MB-capped) so each session row
/// knows whether its audio is replayable / exportable.
///
/// The window is read-mostly — the only state the user mutates here is "selected row". Capture-on /
/// capture-off, MB cap, and sample deletion all happen through the Settings window or the
/// per-row Delete/Export buttons; this VM exposes commands for those, not toggles.
///
/// Each row also projects its session's <see cref="SpeechSessionTrace"/>: the transcription mode that ran, the
/// accurate mode's n-best list, the context decoder's outcome and best guess, and the decode time, so the detail
/// pane shows which path produced the transcript and how it settled the callsign.
/// </summary>
public sealed partial class SpeechDebugViewModel : ObservableObject, IDisposable
{
    private static readonly ILogger Log = AppLog.CreateLogger<SpeechDebugViewModel>();

    private readonly SpeechRecognitionService _service;
    private readonly SpeechSampleStore _sampleStore;
    private readonly UserPreferences _preferences;

    public SpeechDebugViewModel(SpeechRecognitionService service, SpeechSampleStore sampleStore, UserPreferences preferences)
    {
        _service = service;
        _sampleStore = sampleStore;
        _preferences = preferences;
        RunningEngineText = RunningEngineTextFor(
            _preferences.SttEngine,
            _preferences.SttMode,
            SherpaNativeLoader.NbestAvailability(),
            SherpaNativeLoader.LoadedSet
        );
        CpuFallbackText = CpuFallbackTextFor(SherpaNativeLoader.CpuFallbackReason);

        Rows = [];
        RebuildRows();

        // Keep Rows in sync with the live session ring. New sessions land at index 0; older ones
        // age out at the tail. We re-resolve the sample link too because the in-memory session was
        // built before the sample landed on disk (the store assigns ids asynchronously).
        _service.SessionHistory.CollectionChanged += OnSessionHistoryChanged;
        _sampleStore.Entries.CollectionChanged += OnSampleStoreChanged;
    }

    public ObservableCollection<SpeechDebugSessionRow> Rows { get; }

    /// <summary>
    /// The header's engine line, as this window opened: the model, the mode it runs in and the device it runs on. The mode
    /// is the effective one, so an Accurate pick that cannot run reads Fast, exactly as Settings disables it.
    /// </summary>
    public string RunningEngineText { get; }

    /// <summary>
    /// Why a GPU pick fell back to the CPU at startup, as this window opened; null when it did not, which hides the line.
    /// </summary>
    public string? CpuFallbackText { get; }

    /// <summary>The GPU-fallback line for <paramref name="reason"/>, or null when there was no fallback.</summary>
    /// <param name="reason">Why a GPU pick ended on the CPU.</param>
    internal static string? CpuFallbackTextFor(string? reason) => (reason is null) ? null : $"GPU unavailable: {reason}";

    [ObservableProperty]
    private SpeechDebugSessionRow? _selectedRow;

    /// <summary>Free-form status line shown next to the audio play/stop buttons. Set by the
    /// window's playback code-behind from <see cref="NAudio"/>-driven events.</summary>
    [ObservableProperty]
    private string _playbackStatus = string.Empty;

    partial void OnSelectedRowChanged(SpeechDebugSessionRow? value)
    {
        // Clear stale status when the user picks a different session — otherwise "Playing… (3.2s)"
        // can linger after the user has navigated away from that row.
        PlaybackStatus = string.Empty;
    }

    /// <summary>True when the opt-in toggle is on. Drives the header pill color + the "Settings"
    /// hint text in the audio card when a session has no saved sample.</summary>
    public bool CaptureEnabled => _preferences.SpeechSampleCaptureEnabled;

    /// <summary>Free-form status line for the header pill — "Capture: ON · 12.3 / 50 MB · 14 saved" / "Capture: OFF".</summary>
    public string CaptureStatus
    {
        get
        {
            if (!CaptureEnabled)
            {
                return "Capture: OFF — enable in Settings to save samples";
            }
            double usedMb = _sampleStore.TotalBytes / (1024.0 * 1024.0);
            int maxMb = _preferences.SpeechSampleCacheMaxMb;
            int count = _sampleStore.Entries.Count;
            return $"Capture: ON · {usedMb:F1} / {maxMb} MB · {count} saved";
        }
    }

    /// <summary>Count of currently-checked rows that have a saved sample (i.e. are exportable).</summary>
    public int SelectedForExportCount => Rows.Count(r => r.IsSelectedForExport && r.HasSavedAudio);

    /// <summary>True when at least one exportable row is checked — backs the bundle Export button's IsEnabled.</summary>
    public bool HasSelectedForExport => SelectedForExportCount > 0;

    /// <summary>Label for the multi-select Export button — shows the count when non-zero.</summary>
    public string ExportSelectedButtonLabel => SelectedForExportCount == 0 ? "Export selected…" : $"Export selected ({SelectedForExportCount})…";

    [RelayCommand]
    private void ClearHistory()
    {
        _service.SessionHistory.Clear();
        Rows.Clear();
    }

    /// <summary>
    /// Builds a bundle zip containing every row that has its checkbox ticked AND has a saved
    /// sample. Returns the number of samples written so the caller can surface a success message
    /// (zero = the picker was cancelled or nothing matched, no file is created either way).
    /// </summary>
    public int ExportSelectedBundle(string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            return 0;
        }
        var ids = Rows.Where(r => r.IsSelectedForExport && r.HasSavedAudio).Select(r => r.Sample!.Id).ToList();
        if (ids.Count == 0)
        {
            return 0;
        }
        int written = _sampleStore.ExportBundle(ids, destinationPath);
        if (written == 0)
        {
            Log.LogWarning("Failed to write speech sample bundle ({Count} ids) to {Path}", ids.Count, destinationPath);
        }
        return written;
    }

    /// <summary>
    /// Builds a single-sample bundle from the currently-selected row's sample (used by the
    /// per-session detail-pane "Export this sample…" button). Reuses the bundle format so the
    /// reviewer sees the same structure regardless of whether one or many samples were shared.
    /// Returns true on success.
    /// </summary>
    public bool ExportSingle(string destinationPath)
    {
        if (SelectedRow?.Sample is null || string.IsNullOrWhiteSpace(destinationPath))
        {
            return false;
        }
        return _sampleStore.ExportBundle([SelectedRow.Sample.Id], destinationPath) > 0;
    }

    [RelayCommand]
    private void SelectAllForExport()
    {
        foreach (SpeechDebugSessionRow row in Rows)
        {
            if (row.HasSavedAudio)
            {
                row.IsSelectedForExport = true;
            }
        }
    }

    [RelayCommand]
    private void UnselectAllForExport()
    {
        foreach (SpeechDebugSessionRow row in Rows)
        {
            row.IsSelectedForExport = false;
        }
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (SelectedRow?.Sample is null)
        {
            return;
        }
        _sampleStore.Delete(SelectedRow.Sample.Id);
        // Refresh the selected row's sample link — the row stays (session still in history) but
        // its audio-replayable status flips to false.
        SelectedRow.RefreshSample(_sampleStore);
        SelectedRow.IsSelectedForExport = false;
        OnPropertyChanged(nameof(CaptureStatus));
    }

    private void OnSessionHistoryChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildRows();

    /// <summary>
    /// The header's engine line: the model, the mode it runs in (the effective one, so an Accurate pick that cannot run
    /// reads Fast) and the device it runs on.
    /// </summary>
    /// <param name="engine">The model the preferences save.</param>
    /// <param name="savedMode">The mode the preferences save.</param>
    /// <param name="nbest">Whether the sherpa-onnx native can run Accurate mode, and why not when it cannot.</param>
    /// <param name="loadedSet">The sherpa-onnx native set this process loaded.</param>
    internal static string RunningEngineTextFor(
        SttEngine engine,
        SttMode savedMode,
        (bool Available, string? Reason) nbest,
        SherpaNativeSet loadedSet
    )
    {
        SttMode mode = SttModelCardViewModel.EffectiveMode(savedMode, SttModelCardViewModel.AccurateUnavailableReason(engine, nbest));
        string modeText = (mode == SttMode.Accurate) ? "Accurate" : "Fast";
        return $"Running: {SttModelCardViewModel.TitleOf(engine)} · {modeText} · {DeviceTextOf(engine, loadedSet)}";
    }

    // Whisper transcribes through LM-Kit, not the sherpa-onnx native set this process loads, so the set says nothing about
    // where it runs.
    private static string DeviceTextOf(SttEngine engine, SherpaNativeSet loadedSet) =>
        ((engine != SttEngine.Whisper) && (loadedSet == SherpaNativeSet.Cuda)) ? "GPU" : "CPU";

    private void OnSampleStoreChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (SpeechDebugSessionRow row in Rows)
        {
            row.RefreshSample(_sampleStore);
        }
        OnPropertyChanged(nameof(CaptureStatus));
        NotifySelectedCountChanged();
    }

    private void RebuildRows()
    {
        DateTime? previouslySelectedId = SelectedRow?.Session.TimestampUtc;
        var previouslyExportSelected = Rows.Where(r => r.IsSelectedForExport).Select(r => r.Session.TimestampUtc).ToHashSet();

        foreach (SpeechDebugSessionRow row in Rows)
        {
            row.PropertyChanged -= OnRowPropertyChanged;
        }
        Rows.Clear();

        foreach (SpeechSession session in _service.SessionHistory)
        {
            var row = new SpeechDebugSessionRow(session);
            row.RefreshSample(_sampleStore);
            row.IsSelectedForExport = previouslyExportSelected.Contains(session.TimestampUtc);
            row.PropertyChanged += OnRowPropertyChanged;
            Rows.Add(row);
        }
        if (previouslySelectedId is { } ts)
        {
            SelectedRow = Rows.FirstOrDefault(r => r.Session.TimestampUtc == ts);
        }
        OnPropertyChanged(nameof(CaptureStatus));
        NotifySelectedCountChanged();
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SpeechDebugSessionRow.IsSelectedForExport) || e.PropertyName == nameof(SpeechDebugSessionRow.HasSavedAudio))
        {
            NotifySelectedCountChanged();
        }
    }

    private void NotifySelectedCountChanged()
    {
        OnPropertyChanged(nameof(SelectedForExportCount));
        OnPropertyChanged(nameof(HasSelectedForExport));
        OnPropertyChanged(nameof(ExportSelectedButtonLabel));
    }

    public void Dispose()
    {
        _service.SessionHistory.CollectionChanged -= OnSessionHistoryChanged;
        _sampleStore.Entries.CollectionChanged -= OnSampleStoreChanged;
        foreach (SpeechDebugSessionRow row in Rows)
        {
            row.PropertyChanged -= OnRowPropertyChanged;
        }
    }
}

/// <summary>
/// One row in the master list. Holds the underlying <see cref="SpeechSession"/> plus the resolved
/// <see cref="SpeechSampleEntry"/> (when the session was captured to disk). The row is mostly a
/// presentation projection — colors, glyphs, clipped transcript — so the XAML stays declarative.
/// </summary>
public sealed class SpeechDebugSessionRow(SpeechSession session) : INotifyPropertyChanged
{
    public SpeechSession Session { get; } = session;
    public SpeechSampleEntry? Sample { get; private set; }

    public bool HasSavedAudio => Sample is not null;

    private bool _isSelectedForExport;

    /// <summary>
    /// User has checked this row in the master list for inclusion in a multi-sample export
    /// bundle. The window's checkbox column binds two-way to this; rows without a saved sample
    /// keep their checkbox disabled (the bundle exporter also re-filters by <see cref="HasSavedAudio"/>
    /// so even a stale true here can't produce a broken bundle).
    /// </summary>
    public bool IsSelectedForExport
    {
        get => _isSelectedForExport;
        set
        {
            if (_isSelectedForExport == value)
            {
                return;
            }
            _isSelectedForExport = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelectedForExport)));
        }
    }

    public string TimestampDisplay => Session.TimestampUtc.ToLocalTime().ToString("HH:mm:ss");

    public string OutcomeGlyph =>
        Session.Outcome switch
        {
            SpeechSessionOutcome.CommandAccepted => "✓",
            SpeechSessionOutcome.NoMappingFound => "✗",
            SpeechSessionOutcome.EmptyTranscript => "—",
            SpeechSessionOutcome.Error => "!",
            SpeechSessionOutcome.Cancelled => "↩",
            _ => "?",
        };

    public IBrush OutcomeBrush =>
        Session.Outcome switch
        {
            SpeechSessionOutcome.CommandAccepted => Session.UsedLlmFallback
                ? new SolidColorBrush(Color.Parse("#6FA8DC"))
                : new SolidColorBrush(Color.Parse("#7CC07C")),
            SpeechSessionOutcome.NoMappingFound => new SolidColorBrush(Color.Parse("#E0B070")),
            SpeechSessionOutcome.Error => new SolidColorBrush(Color.Parse("#E07070")),
            SpeechSessionOutcome.Cancelled => new SolidColorBrush(Color.Parse("#A0A0A0")),
            _ => new SolidColorBrush(Color.Parse("#808080")),
        };

    public string OutcomeLabel =>
        Session.Outcome switch
        {
            SpeechSessionOutcome.CommandAccepted => Session.UsedLlmFallback ? "command (LLM)" : "command",
            SpeechSessionOutcome.NoMappingFound => "no mapping",
            SpeechSessionOutcome.EmptyTranscript => "empty",
            SpeechSessionOutcome.Error => "error",
            SpeechSessionOutcome.Cancelled => "cancelled",
            _ => Session.Outcome.ToString(),
        };

    public string TranscriptDisplay
    {
        get
        {
            string t = Session.Transcript;
            if (string.IsNullOrWhiteSpace(t))
            {
                return "(no speech detected)";
            }
            return t.Length > 80 ? t[..77] + "…" : t;
        }
    }

    public string CanonicalDisplay => Session.CanonicalCommand ?? string.Empty;

    /// <summary>
    /// Which transcription path produced this session's transcript: <c>Fast</c>, <c>Accurate</c>, or <c>unknown</c> when
    /// the trace does not record it.
    /// </summary>
    public string ModeText => ModeTextOf(Session.Trace?.Mode);

    /// <summary>How the context decoder settled the callsign, or <c>not run</c>; a failed accurate decode reads as the fallback.</summary>
    public string DecodeStateText => DecodeStateTextOf(Session.Trace?.DecodeState, Session.Trace?.DecodeError);

    /// <summary>True when the accurate decode failed and the press fell back to the fast path.</summary>
    public bool DecodeStateIsFailure => Session.Trace?.DecodeError is not null;

    /// <summary>True when no context decode ran for this session at all (a fast press, or a trace from before accurate mode).</summary>
    public bool DecodeStateIsNotRun => DecodeStateIsNotRunOf(Session.Trace?.DecodeState, Session.Trace?.DecodeError);

    /// <summary>The decoder's best-guess callsign line for a say-again, or null when there is no guess to show.</summary>
    public string? BestGuessText => BestGuessTextOf(Session.Trace?.DecodeState, Session.Trace?.BestGuess);

    public bool HasBestGuess => BestGuessText is not null;

    /// <summary>The accurate decode's wall-clock time as <c>Decode 12 ms</c>, or null when no decode ran.</summary>
    public string? DecodeTimeText => DecodeTimeTextOf(Session.Trace?.DecodeMs);

    /// <summary>
    /// The beam hypotheses, one line each: <c>#1 cleared to land  (-3.14)</c>, with the score omitted when the engine
    /// reported none.
    /// </summary>
    public IReadOnlyList<string> NBestLines =>
        Session.Trace?.NBest is { } nBest ? [.. nBest.Select((hypothesis, index) => NBestLineOf(index + 1, hypothesis))] : [];

    public bool HasNBest => Session.Trace?.NBest is { Count: > 0 };

    public string NBestHeader => $"N-best ({Session.Trace?.NBest?.Count ?? 0})";

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Re-resolve the sample link from the store. Called after the store changes or after deleting.</summary>
    public void RefreshSample(SpeechSampleStore store)
    {
        string? id = Session.SampleId;
        SpeechSampleEntry? sample = id is null ? null : store.Entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));
        if (!ReferenceEquals(sample, Sample))
        {
            Sample = sample;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Sample)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSavedAudio)));
        }
    }

    /// <summary>The mode label for <paramref name="mode"/>; a session whose trace records no mode reads <c>unknown</c>.</summary>
    /// <param name="mode">The transcription mode the session's trace records, or null.</param>
    internal static string ModeTextOf(TranscriptionMode? mode) =>
        mode switch
        {
            TranscriptionMode.Fast => "Fast",
            TranscriptionMode.Accurate => "Accurate",
            _ => "unknown",
        };

    /// <summary>
    /// The decode-state label. A decode error always reads as the fallback, because the service records a failed accurate
    /// decode as a fast press with the error set; otherwise the state's own name, or <c>not run</c> when no decode ran.
    /// </summary>
    /// <param name="state">The state the context decoder recorded, or null when it did not run.</param>
    /// <param name="decodeError">The exception message from a failed accurate decode, or null.</param>
    internal static string DecodeStateTextOf(DecodeState? state, string? decodeError) =>
        (decodeError is not null)
            ? "Fell back to fast: " + decodeError
            : state switch
            {
                DecodeState.Resolved => "Resolved",
                DecodeState.SayAgain => "Say again",
                DecodeState.NoCallsign => "No callsign",
                _ => "not run",
            };

    /// <summary>True only when the decoder recorded no state and no error, so the decode genuinely did not run.</summary>
    /// <param name="state">The state the context decoder recorded, or null when it did not run.</param>
    /// <param name="decodeError">The exception message from a failed accurate decode, or null.</param>
    internal static bool DecodeStateIsNotRunOf(DecodeState? state, string? decodeError) => (state is null) && (decodeError is null);

    /// <summary>The say-again best guess, shown only when the decoder asked for one and produced a guess.</summary>
    /// <param name="state">The state the context decoder recorded, or null when it did not run.</param>
    /// <param name="bestGuess">The decoder's best-guess callsign, or null.</param>
    internal static string? BestGuessTextOf(DecodeState? state, string? bestGuess) =>
        ((state == DecodeState.SayAgain) && !string.IsNullOrEmpty(bestGuess)) ? "Best guess: " + bestGuess : null;

    /// <summary>The decode time in whole milliseconds, invariant culture, or null when no decode ran.</summary>
    /// <param name="decodeMs">The context decode's wall-clock milliseconds, or null when it did not run.</param>
    internal static string? DecodeTimeTextOf(double? decodeMs) =>
        decodeMs is { } ms ? "Decode " + ms.ToString("F0", CultureInfo.InvariantCulture) + " ms" : null;

    /// <summary>One n-best line: the 1-based rank, the hypothesis text, and the score at two decimals when the engine reported one.</summary>
    /// <param name="rank">The hypothesis's 1-based rank in the beam list.</param>
    /// <param name="hypothesis">The hypothesis text and its decode score.</param>
    internal static string NBestLineOf(int rank, SpeechTraceHypothesis hypothesis)
    {
        string line = "#" + rank.ToString(CultureInfo.InvariantCulture) + " " + hypothesis.Text;
        return hypothesis.Score is { } score ? line + "  (" + score.ToString("F2", CultureInfo.InvariantCulture) + ")" : line;
    }
}
