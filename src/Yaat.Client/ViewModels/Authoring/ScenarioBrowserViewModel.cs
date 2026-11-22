using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Sim.Authoring;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>
/// The Scenario Editor's browser: the ARTCCs the user may edit, that ARTCC's working copies (Local) and server mirror
/// (Server) as rows with badges, the status line and progress of the resync, the notices, and the commands of the
/// toolbar and the context menu. Store, file and network work runs off the UI thread; every result reaches the
/// browser's state through the services' post action. A resync runs once on open and on Refresh, one at a time, and a
/// stale load or resync never overwrites newer rows or status.
/// </summary>
public sealed partial class ScenarioBrowserViewModel : ObservableObject, IDisposable
{
    /// <summary>The status line when the user may edit no ARTCC.</summary>
    public const string NoArtccText = "No ARTCC to edit: set your ARTCC in Settings.";

    private static readonly ILogger Log = AppLog.CreateLogger<ScenarioBrowserViewModel>();

    private readonly ScenarioBrowserServices _services;
    private readonly List<string> _eventNotices = [];
    private IReadOnlyList<string> _storeProblems = [];
    private IReadOnlyList<string> _artccs = [];
    private CancellationTokenSource _runCts = new();
    private RowSelection? _pendingSelection;
    private string? _artcc;
    private int _loadSeq;
    private int _resyncSeq;
    private volatile bool _disposed;
    private bool _isStatusOffline;
    private bool _writingOfflineStatus;

    [ObservableProperty]
    private ScenarioBrowserTab _selectedTab;

    [ObservableProperty]
    private ScenarioBrowserRowViewModel? _selectedLocalRow;

    [ObservableProperty]
    private ScenarioBrowserRowViewModel? _selectedServerRow;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _isResyncRunning;

    [ObservableProperty]
    private int _progressDone;

    [ObservableProperty]
    private int _progressTotal;

    /// <summary>Initializes a new instance of the <see cref="ScenarioBrowserViewModel"/> class.</summary>
    /// <param name="services">The store, network, prompts, room and UI-thread seams it works through.</param>
    public ScenarioBrowserViewModel(ScenarioBrowserServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        Notices.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(NoticeCount));
            OnPropertyChanged(nameof(HasNotices));
            OnPropertyChanged(nameof(NoticesHeaderText));
        };
    }

    /// <summary>
    /// Raised when a working copy is to be opened in the editor: on Open of a working copy, or of a server row once its
    /// working copy exists. Never raised for a corrupt row or a corrupt working copy.
    /// </summary>
    public event EventHandler<ScenarioOpenRequestedEventArgs>? OpenRequested;

    /// <summary>Gets the ARTCCs the user may edit, in the access source's order.</summary>
    public IReadOnlyList<string> Artccs
    {
        get => _artccs;
        private set
        {
            if (SetProperty(ref _artccs, value))
            {
                OnPropertyChanged(nameof(IsArtccLabelVisible));
                OnPropertyChanged(nameof(IsArtccSelectorVisible));
            }
        }
    }

    /// <summary>Gets the working-copy rows, by name.</summary>
    public ObservableCollection<ScenarioBrowserRowViewModel> LocalRows { get; } = [];

    /// <summary>Gets the server-mirror rows, by name.</summary>
    public ObservableCollection<ScenarioBrowserRowViewModel> ServerRows { get; } = [];

    /// <summary>Gets the notice lines, each once: download failures, store problems and failed commands, each by name.</summary>
    public ObservableCollection<string> Notices { get; } = [];

    /// <summary>Gets how many notices there are, for the warning button's count.</summary>
    public int NoticeCount => Notices.Count;

    /// <summary>Gets a value indicating whether there is a notice, which shows the warning button.</summary>
    public bool HasNotices => Notices.Count > 0;

    /// <summary>Gets the heading of the notices flyout: how many there are.</summary>
    public string NoticesHeaderText => (Notices.Count == 1) ? "1 problem from the last refresh" : $"{Notices.Count} problems from the last refresh";

    /// <summary>
    /// Gets a value indicating whether the status line is the offline status (the download could not reach the server),
    /// which shows it as a warning. Set only with that status; any other status clears it.
    /// </summary>
    public bool IsStatusOffline
    {
        get => _isStatusOffline;
        private set => SetProperty(ref _isStatusOffline, value);
    }

    /// <summary>Gets a value indicating whether the resync's progress has no total yet, so its bar runs without one.</summary>
    public bool IsProgressIndeterminate => ProgressTotal == 0;

    /// <summary>
    /// Gets or sets the ARTCC shown. Setting it to another listed ARTCC asks <see cref="CanSwitchArtcc"/>, remembers the
    /// pick in the editor's own preference, cancels a running resync and loads that ARTCC (<see cref="ArtccSwitch"/>).
    /// </summary>
    public string? SelectedArtcc
    {
        get => _artcc;
        set => RequestArtcc(value);
    }

    /// <summary>Gets a value indicating whether there is an ARTCC to edit.</summary>
    public bool HasArtcc => _artcc is not null;

    /// <summary>Gets a value indicating whether the ARTCC shows as a label: the user may edit exactly one.</summary>
    public bool IsArtccLabelVisible => Artccs.Count == 1;

    /// <summary>Gets a value indicating whether the ARTCC shows as a selector: the user may edit several.</summary>
    public bool IsArtccSelectorVisible => Artccs.Count > 1;

    /// <summary>Gets the ARTCC label's text: the ARTCC id, or empty when there is none.</summary>
    public string ArtccLabelText => _artcc ?? "";

    /// <summary>Gets the selected row of the tab that shows, which the commands act on.</summary>
    public ScenarioBrowserRowViewModel? SelectedRow => SelectedTab == ScenarioBrowserTab.Local ? SelectedLocalRow : SelectedServerRow;

    /// <summary>Gets the latest resync, finished once its rows and status are applied; a completed task before the first.</summary>
    public Task ResyncTask { get; private set; } = Task.CompletedTask;

    /// <summary>Gets the latest ARTCC load, finished once its rows are applied and its resync started.</summary>
    public Task ArtccSwitch { get; private set; } = Task.CompletedTask;

    /// <summary>Whether the window may close now.</summary>
    /// <returns>Always true.</returns>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "An instance hook the window asks before closing.")]
    public bool CanClose() => true;

    /// <summary>Whether the browser may switch to another ARTCC now.</summary>
    /// <returns>Always true.</returns>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "An instance hook asked before each ARTCC switch.")]
    public bool CanSwitchArtcc() => true;

    /// <summary>
    /// Reads the ARTCCs the user may edit and shows the default one: the Settings ARTCC when listed, else the remembered
    /// pick, else the first. Finishes once its rows are applied and its resync started; with no ARTCC it lists nothing.
    /// After <see cref="Dispose"/> it ends quietly.
    /// </summary>
    /// <returns>A task that finishes when the first rows are shown.</returns>
    public async Task InitializeAsync()
    {
        if (_disposed)
        {
            return;
        }

        CancellationToken ct = _runCts.Token;
        (IReadOnlyList<string> ids, string? failure) = await ReadArtccsAsync(ct).ConfigureAwait(false);
        await OnUiAsync(() => ShowArtccs(ids, failure)).ConfigureAwait(false);
        await ArtccSwitch.ConfigureAwait(false);
    }

    /// <summary>Cancels a running resync; nothing that completes afterwards changes the browser.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Increment(ref _resyncSeq);
        _runCts.Cancel();
        _runCts.Dispose();
    }

    partial void OnSelectedTabChanged(ScenarioBrowserTab value) => OnSelectionChanged();

    partial void OnSelectedLocalRowChanged(ScenarioBrowserRowViewModel? value) => OnSelectionChanged();

    partial void OnSelectedServerRowChanged(ScenarioBrowserRowViewModel? value) => OnSelectionChanged();

    partial void OnIsResyncRunningChanged(bool value) => NotifyCommands();

    partial void OnStatusTextChanged(string value) => IsStatusOffline = _writingOfflineStatus;

    partial void OnProgressTotalChanged(int value) => OnPropertyChanged(nameof(IsProgressIndeterminate));

    // The one way the offline status is written; every other write of StatusText clears the flag.
    private void ShowOfflineStatus(string text)
    {
        _writingOfflineStatus = true;
        try
        {
            StatusText = text;
            IsStatusOffline = true;
        }
        finally
        {
            _writingOfflineStatus = false;
        }
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedRow));
        NotifyCommands();
    }

    // The ARTCC ids, or none and why; a cancel (the window closing) ends with none and no reason.
    private async Task<(IReadOnlyList<string> Ids, string? Failure)> ReadArtccsAsync(CancellationToken ct)
    {
        try
        {
            IReadOnlyList<string> ids = await _services.Access.GetEditableArtccsAsync(ct).ConfigureAwait(false);
            return ([.. ids.Select(id => id.Trim().ToUpperInvariant()).Where(LocalScenarioStore.IsValidId).Distinct(StringComparer.Ordinal)], null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return ([], null);
        }
        catch (Exception ex)
        {
            LogFailure(ex, "reading the editable ARTCCs");
            return ([], ex.Message);
        }
    }

    private void ShowArtccs(IReadOnlyList<string> ids, string? failure)
    {
        if (_disposed)
        {
            return;
        }

        Artccs = ids;
        string? artcc = DefaultArtcc(ids);
        if (artcc is null)
        {
            StatusText = (failure is null) ? NoArtccText : $"Couldn't read the ARTCCs you can edit: {failure}";
            NotifyArtccState();
            return;
        }

        ArtccSwitch = SwitchToAsync(artcc);
    }

    private string? DefaultArtcc(IReadOnlyList<string> ids)
    {
        if (ids.Count == 0)
        {
            return null;
        }

        string settings = _services.Preferences.ArtccId;
        string? last = _services.Preferences.ScenarioEditorLastArtcc;
        if (ids.Contains(settings))
        {
            return settings;
        }

        return ((last is not null) && ids.Contains(last)) ? last : ids[0];
    }

    private void RequestArtcc(string? value)
    {
        if (_disposed || (value is null) || (value == _artcc) || !Artccs.Contains(value))
        {
            return;
        }

        if (!CanSwitchArtcc())
        {
            OnPropertyChanged(nameof(SelectedArtcc));
            return;
        }

        _services.Preferences.SetScenarioEditorLastArtcc(value);
        ArtccSwitch = SwitchToAsync(value);
    }

    // On the UI thread: cancels and disposes the old ARTCC's run, empties the lists, then loads the new ARTCC and starts
    // its resync unless another switch or Dispose came first.
    private async Task SwitchToAsync(string artcc)
    {
        if (_disposed)
        {
            return;
        }

        CancellationTokenSource old = _runCts;
        _runCts = new CancellationTokenSource();
        old.Cancel();
        old.Dispose();
        CancellationToken ct = _runCts.Token;
        Interlocked.Increment(ref _resyncSeq);
        _artcc = artcc;
        ClearArtccState();
        await ReloadAsync(artcc, null).ConfigureAwait(false);
        await OnUiAsync(() =>
            {
                if (!_disposed && !ct.IsCancellationRequested)
                {
                    _ = StartResync();
                }
            })
            .ConfigureAwait(false);
    }

    private void ClearArtccState()
    {
        _pendingSelection = null;
        IsResyncRunning = false;
        _eventNotices.Clear();
        _storeProblems = [];
        LocalRows.Clear();
        ServerRows.Clear();
        SelectedLocalRow = null;
        SelectedServerRow = null;
        RebuildNotices();
        NotifyArtccState();
    }

    private void NotifyArtccState()
    {
        OnPropertyChanged(nameof(SelectedArtcc));
        OnPropertyChanged(nameof(HasArtcc));
        OnPropertyChanged(nameof(ArtccLabelText));
        NotifyCommands();
    }

    // Reads the ARTCC's rows off the UI thread and applies them through the post, unless a later load or a switch came first.
    private async Task ReloadAsync(string artcc, RowSelection? select)
    {
        int seq = Interlocked.Increment(ref _loadSeq);
        LocalScenarioStore store = _services.Store;
        ScenarioBrowserSnapshot snapshot = await Task.Run(() => ScenarioBrowserRows.Read(store, artcc)).ConfigureAwait(false);
        await OnUiAsync(() => ApplySnapshot(seq, snapshot, select)).ConfigureAwait(false);
    }

    // A stale load's rows are dropped; its selection still lands on the newer rows, or on the next load's.
    private void ApplySnapshot(int seq, ScenarioBrowserSnapshot snapshot, RowSelection? select)
    {
        if (_disposed || (snapshot.ArtccId != _artcc))
        {
            return;
        }

        if (seq != Volatile.Read(ref _loadSeq))
        {
            if ((select is not null) && !TrySelect(select))
            {
                _pendingSelection = select;
            }

            return;
        }

        ApplyRows(snapshot, select ?? _pendingSelection);
    }

    private void ApplyRows(ScenarioBrowserSnapshot snapshot, RowSelection? select)
    {
        string? localId = SelectedLocalRow?.Id;
        string? serverId = SelectedServerRow?.Id;
        Replace(LocalRows, snapshot.Local);
        Replace(ServerRows, snapshot.Server);
        SelectedLocalRow = LocalRows.FirstOrDefault(r => r.Id == localId);
        SelectedServerRow = ServerRows.FirstOrDefault(r => r.Id == serverId);
        _pendingSelection = null;
        if (select is not null)
        {
            TrySelect(select);
        }

        _storeProblems = snapshot.Problems;
        RebuildNotices();
    }

    private bool TrySelect(RowSelection select)
    {
        bool local = select.Tab == ScenarioBrowserTab.Local;
        ScenarioBrowserRowViewModel? row = (local ? LocalRows : ServerRows).FirstOrDefault(r => r.Id == select.Id);
        if (row is null)
        {
            return false;
        }

        if (local)
        {
            SelectedLocalRow = row;
        }
        else
        {
            SelectedServerRow = row;
        }

        SelectedTab = select.Tab;
        return true;
    }

    private static void Replace(ObservableCollection<ScenarioBrowserRowViewModel> rows, IReadOnlyList<ScenarioBrowserRowViewModel> fresh)
    {
        rows.Clear();
        foreach (ScenarioBrowserRowViewModel row in fresh)
        {
            rows.Add(row);
        }
    }

    // Runs the action through the post seam and finishes when it has run.
    private Task OnUiAsync(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _services.Post(() =>
        {
            try
            {
                action();
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });
        return done.Task;
    }

    // Starts the asynchronous UI work (a prompt, a room load) through the post seam and finishes with its result.
    private Task<T> OnUiAsync<T>(Func<Task<T>> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _services.Post(() =>
        {
            try
            {
                work().ContinueWith(t => Complete(done, t), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });
        return done.Task;
    }

    private static void Complete<T>(TaskCompletionSource<T> done, Task<T> finished)
    {
        if (finished.IsFaulted)
        {
            done.SetException(finished.Exception!.InnerExceptions);
        }
        else if (finished.IsCanceled)
        {
            done.SetCanceled();
        }
        else
        {
            done.SetResult(finished.Result);
        }
    }

    private void AddNotice(string text)
    {
        _eventNotices.Add(text);
        RebuildNotices();
    }

    private void RebuildNotices()
    {
        Notices.Clear();
        foreach (string notice in _eventNotices.Concat(_storeProblems).Distinct(StringComparer.Ordinal))
        {
            Notices.Add(notice);
        }
    }

    private bool IsCurrentResync(int seq) => !_disposed && (seq == Volatile.Read(ref _resyncSeq));

    private static void LogFailure(Exception ex, string what)
    {
        if (Log.IsEnabled(LogLevel.Warning))
        {
            Log.LogWarning(ex, "Scenario browser: {What} failed", what);
        }
    }

    // The row to select after a reload: its tab and id.
    private sealed record RowSelection(ScenarioBrowserTab Tab, string Id);
}
