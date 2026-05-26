using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Sim.Speech;

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

        Rows = [];
        RebuildRows();

        // Keep Rows in sync with the live session ring. New sessions land at index 0; older ones
        // age out at the tail. We re-resolve the sample link too because the in-memory session was
        // built before the sample landed on disk (the store assigns ids asynchronously).
        _service.SessionHistory.CollectionChanged += OnSessionHistoryChanged;
        _sampleStore.Entries.CollectionChanged += OnSampleStoreChanged;
    }

    public ObservableCollection<SpeechDebugSessionRow> Rows { get; }

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
            var usedMb = _sampleStore.TotalBytes / (1024.0 * 1024.0);
            var maxMb = _preferences.SpeechSampleCacheMaxMb;
            var count = _sampleStore.Entries.Count;
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
        var written = _sampleStore.ExportBundle(ids, destinationPath);
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
        foreach (var row in Rows)
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
        foreach (var row in Rows)
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

    private void OnSampleStoreChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var row in Rows)
        {
            row.RefreshSample(_sampleStore);
        }
        OnPropertyChanged(nameof(CaptureStatus));
        NotifySelectedCountChanged();
    }

    private void RebuildRows()
    {
        var previouslySelectedId = SelectedRow?.Session.TimestampUtc;
        var previouslyExportSelected = Rows.Where(r => r.IsSelectedForExport).Select(r => r.Session.TimestampUtc).ToHashSet();

        foreach (var row in Rows)
        {
            row.PropertyChanged -= OnRowPropertyChanged;
        }
        Rows.Clear();

        foreach (var session in _service.SessionHistory)
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
        foreach (var row in Rows)
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
public sealed class SpeechDebugSessionRow : INotifyPropertyChanged
{
    public SpeechDebugSessionRow(SpeechSession session)
    {
        Session = session;
    }

    public SpeechSession Session { get; }
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
            var t = Session.Transcript;
            if (string.IsNullOrWhiteSpace(t))
            {
                return "(no speech detected)";
            }
            return t.Length > 80 ? t[..77] + "…" : t;
        }
    }

    public string CanonicalDisplay => Session.CanonicalCommand ?? string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Re-resolve the sample link from the store. Called after the store changes or after deleting.</summary>
    public void RefreshSample(SpeechSampleStore store)
    {
        var id = Session.SampleId;
        var sample = id is null ? null : store.Entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));
        if (!ReferenceEquals(sample, Sample))
        {
            Sample = sample;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Sample)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSavedAudio)));
        }
    }
}
