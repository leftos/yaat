using System.Collections.ObjectModel;
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

    // The switch guard still waiting on the user's answer; null when none is pending.
    private Task<bool>? _switchGuard;
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

    /// <summary>Raised on the UI thread once a working copy is deleted, so an editor that has it open closes it.</summary>
    public event EventHandler<ScenarioWorkingCopyDeletedEventArgs>? WorkingCopyDeleted;

    /// <summary>
    /// Raised on the UI thread once an import that kept its id replaced a working copy's text, so an editor that has it
    /// open reloads it.
    /// </summary>
    public event EventHandler<ScenarioWorkingCopyReplacedEventArgs>? WorkingCopyReplaced;

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
    /// Gets or sets the ARTCC shown. Setting it to another listed ARTCC asks <see cref="CanSwitchArtccAsync"/>, remembers the
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

    /// <summary>
    /// Gets or sets what the open working copy with unsaved edits is, read on the UI thread when the window closes or the
    /// ARTCC switches: null when nothing is open or nothing is unsaved. The editor sets it; with no editor it answers null.
    /// </summary>
    public Func<UnsavedOpenCopy?> UnsavedCopy { get; set; } = () => null;

    /// <summary>
    /// Gets or sets what is asked, on the UI thread, before an import that keeps its id replaces a working copy (its ARTCC
    /// and id): true to replace it, false to skip that file. The editor sets it to ask when that copy is open with unsaved
    /// edits; with no editor it always answers true.
    /// </summary>
    public Func<string, string, Task<bool>> ReplaceGuard { get; set; } = (_, _) => Task.FromResult(true);

    /// <summary>
    /// Gets or sets what is asked, on the UI thread, before Merge server changes, an import's Compare… or Compare with…
    /// reads a working copy (its ARTCC and id): true to go on, false to stop. The editor sets it to ask about that copy's
    /// unsaved edits when it is open; with no editor it always answers true.
    /// </summary>
    public Func<string, string, Task<bool>> MergeGuard { get; set; } = (_, _) => Task.FromResult(true);

    /// <summary>
    /// Whether the window may close now. The rows are re-read, then one question is asked: the combined unsaved-and-
    /// unexported question when the open copy (<see cref="UnsavedCopy"/>) has unsaved edits, else the unexported-changes
    /// question when copies have unexported changes. Nothing is saved, discarded or exported before the answer. With
    /// nothing to ask the task is already complete.
    /// </summary>
    /// <returns>True to close.</returns>
    public Task<bool> CanCloseAsync() => AskBeforeLeaving(UnexportedChangesContext.Close);

    /// <summary>
    /// Whether the browser may switch to another ARTCC now, asked as <see cref="CanCloseAsync"/> asks it about the ARTCC
    /// being left.
    /// </summary>
    /// <returns>True to switch.</returns>
    public Task<bool> CanSwitchArtccAsync() => AskBeforeLeaving(UnexportedChangesContext.SwitchArtcc);

    /// <summary>
    /// Re-reads the rows after the editor saved a working copy, so its name and badges show; does nothing when the copy's
    /// ARTCC is not the one shown.
    /// </summary>
    /// <param name="artccId">The ARTCC whose folder holds the saved copy.</param>
    /// <param name="selectId">The id to select on the Local tab (the Save command's copy), or null to keep the selection.</param>
    /// <returns>A task that finishes when the rows are applied.</returns>
    public Task NotifyWorkingCopySavedAsync(string artccId, string? selectId)
    {
        if (_disposed || (_artcc is not { } artcc) || !string.Equals(artcc, artccId, StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        return ReloadAsync(artcc, (selectId is null) ? null : new RowSelection(ScenarioBrowserTab.Local, selectId));
    }

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

        if (_switchGuard is not null)
        {
            OnPropertyChanged(nameof(SelectedArtcc));
            return;
        }

        Task<bool> guard = CanSwitchArtccAsync();
        if (guard.IsCompleted)
        {
            if (guard.Exception is { } failure)
            {
                LogFailure(failure.GetBaseException(), "asking before the ARTCC switch");
            }

            ArtccSwitch = SwitchIfAllowed(value, guard.IsCompletedSuccessfully && guard.Result);
            return;
        }

        _switchGuard = guard;
        ArtccSwitch = SwitchAfterGuardAsync(value, guard);
    }

    // On the UI thread: a refused switch re-raises SelectedArtcc so the selector snaps back to the ARTCC shown.
    private Task SwitchIfAllowed(string value, bool allowed)
    {
        if (_disposed || !allowed || (value == _artcc))
        {
            OnPropertyChanged(nameof(SelectedArtcc));
            return Task.CompletedTask;
        }

        _services.Preferences.SetScenarioEditorLastArtcc(value);
        return SwitchToAsync(value);
    }

    // A guard that asks the user: its answer is applied through the post, and a failed guard refuses the switch. While it
    // is pending, another pick snaps the selector back without asking.
    private async Task SwitchAfterGuardAsync(string value, Task<bool> guard)
    {
        bool allowed;
        try
        {
            allowed = await guard.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogFailure(ex, "asking before the ARTCC switch");
            allowed = false;
        }

        Task switching = Task.CompletedTask;
        await OnUiAsync(() =>
            {
                _switchGuard = null;
                switching = SwitchIfAllowed(value, allowed);
            })
            .ConfigureAwait(false);
        await switching.ConfigureAwait(false);
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

    // On the UI thread: re-reads the rows (another tool may have written copies since the last load), then asks one
    // question, the combined one when the open copy has unsaved edits, else the unexported one when copies are marked;
    // with neither it answers true at once. A failed re-read answers Cancel, with a notice.
    private Task<bool> AskBeforeLeaving(UnexportedChangesContext context)
    {
        if (_disposed)
        {
            return Task.FromResult(true);
        }

        if (!TryRereadRows())
        {
            return Task.FromResult(false);
        }

        ScenarioBrowserRowViewModel[] marked = MarkedRows();
        if (UnsavedCopy() is { } open)
        {
            return AskUnsavedAndUnexportedAsync(open, ListedWith(open, marked), context);
        }

        return ((marked.Length == 0) || (_artcc is not { } artcc)) ? Task.FromResult(true) : AskUnexportedAsync(artcc, marked, context);
    }

    // On the UI thread: false, with a notice, when the shown ARTCC's rows cannot be re-read.
    private bool TryRereadRows()
    {
        if (_artcc is not { } artcc)
        {
            return true;
        }

        try
        {
            ApplySnapshot(Interlocked.Increment(ref _loadSeq), ScenarioBrowserRows.Read(_services.Store, artcc), null);
            return true;
        }
        catch (Exception ex)
        {
            LogFailure(ex, "re-reading the rows before asking about unexported changes");
            AddNotice($"Unexported changes: {ex.Message}");
            return false;
        }
    }

    // The marked copies' saved names with the open copy among them in row order under its edited name (saving leaves it
    // unexported), or last when it has no row shown.
    private string[] ListedWith(UnsavedOpenCopy open, ScenarioBrowserRowViewModel[] marked)
    {
        bool shown = open.ArtccId == _artcc;
        bool IsOpenRow(ScenarioBrowserRowViewModel r) => shown && (r.Id == open.Id) && !r.IsCorrupt;
        string[] names = [.. LocalRows.Where(r => marked.Contains(r) || IsOpenRow(r)).Select(r => IsOpenRow(r) ? open.Name : r.Name)];
        return FirstNames(LocalRows.Any(IsOpenRow) ? names : [.. names, open.Name]);
    }

    // Plain awaits: the answer is acted on in the caller's context, the UI thread, where the editor saves or discards.
    private async Task<bool> AskUnsavedAndUnexportedAsync(UnsavedOpenCopy open, string[] names, UnexportedChangesContext context)
    {
        UnsavedAndUnexportedChoice choice;
        try
        {
            choice = await _services.Prompts.AskUnsavedAndUnexportedAsync(open.Name, names, context);
        }
        catch (Exception ex)
        {
            LogFailure(ex, "asking about unsaved and unexported changes");
            AddNotice($"Unexported changes: {ex.Message}");
            return false;
        }

        return choice switch
        {
            UnsavedAndUnexportedChoice.DiscardAndLeave => open.Discard(),
            UnsavedAndUnexportedChoice.SaveAndLeave => await open.SaveAsync(),
            UnsavedAndUnexportedChoice.SaveAndExportAll => await open.SaveAsync() && await ExportAllAfterSaveAsync(open),
            _ => false,
        };
    }

    // Save and export all, once the open copy is saved: the rows are re-read on the UI thread, then every marked copy of
    // the shown ARTCC and the open copy, wherever its ARTCC is, are exported.
    private async Task<bool> ExportAllAfterSaveAsync(UnsavedOpenCopy open)
    {
        bool read = false;
        ExportJob[] jobs = [];
        await OnUiAsync(() =>
            {
                read = TryRereadRows();
                jobs = JobsWith(open);
            })
            .ConfigureAwait(false);
        if (!read)
        {
            return false;
        }

        return (jobs.Length == 0) || await ExportAllBeforeLeavingAsync(jobs).ConfigureAwait(false);
    }

    // The shown ARTCC's marked copies as full exports, with the open copy added when it is not among them.
    private ExportJob[] JobsWith(UnsavedOpenCopy open)
    {
        ExportJob[] marked = (_artcc is { } artcc) ? FullJobs(artcc, MarkedRows()) : [];
        if (marked.Any(j => (j.Artcc == open.ArtccId) && (j.Row.Id == open.Id)))
        {
            return marked;
        }

        var row = new ScenarioBrowserRowViewModel
        {
            Id = open.Id,
            Name = open.Name,
            Kind = ScenarioRowKind.WorkingCopy,
        };
        return [.. marked, new ExportJob(open.ArtccId, row, ExportKind.Full)];
    }

    private async Task<bool> AskUnexportedAsync(string artcc, ScenarioBrowserRowViewModel[] marked, UnexportedChangesContext context)
    {
        UnexportedChangesChoice choice = await AskUnexportedChoiceAsync(FirstNames([.. marked.Select(r => r.Name)]), context).ConfigureAwait(false);
        return choice switch
        {
            UnexportedChangesChoice.Leave => true,
            UnexportedChangesChoice.ExportAll => await ExportAllBeforeLeavingAsync(FullJobs(artcc, marked)).ConfigureAwait(false),
            _ => false,
        };
    }

    // A prompt that fails answers Cancel, with a notice.
    private async Task<UnexportedChangesChoice> AskUnexportedChoiceAsync(string[] names, UnexportedChangesContext context)
    {
        try
        {
            return await _services.Prompts.AskUnexportedChangesAsync(names, context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogFailure(ex, "asking about unexported changes");
            await OnUiAsync(() => AddNotice($"Unexported changes: {ex.Message}")).ConfigureAwait(false);
            return UnexportedChangesChoice.Cancel;
        }
    }

    private static string[] FirstNames(string[] names) =>
        (names.Length <= UnexportedNamesShown) ? names : [.. names.Take(UnexportedNamesShown), $"and {names.Length - UnexportedNamesShown} more"];

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

/// <summary>A working copy the browser deleted.</summary>
/// <param name="artccId">The ARTCC whose folder held it.</param>
/// <param name="id">The deleted copy's id.</param>
public sealed class ScenarioWorkingCopyDeletedEventArgs(string artccId, string id) : EventArgs
{
    /// <summary>Gets the ARTCC whose folder held the copy.</summary>
    public string ArtccId { get; } = artccId;

    /// <summary>Gets the deleted copy's id.</summary>
    public string Id { get; } = id;
}

/// <summary>A working copy whose text an import replaced.</summary>
/// <param name="artccId">The ARTCC whose folder holds it.</param>
/// <param name="id">The replaced copy's id.</param>
public sealed class ScenarioWorkingCopyReplacedEventArgs(string artccId, string id) : EventArgs
{
    /// <summary>Gets the ARTCC whose folder holds the copy.</summary>
    public string ArtccId { get; } = artccId;

    /// <summary>Gets the replaced copy's id.</summary>
    public string Id { get; } = id;
}

/// <summary>The open working copy with unsaved edits, as the editor holds it, and what saving or discarding it does.</summary>
/// <param name="ArtccId">The ARTCC whose folder holds it.</param>
/// <param name="Id">Its id.</param>
/// <param name="Name">Its name as edited, which the question names.</param>
/// <param name="SaveAsync">Saves it: true once saved and still the copy open.</param>
/// <param name="Discard">Closes it without saving: false when another copy was opened meanwhile.</param>
public sealed record UnsavedOpenCopy(string ArtccId, string Id, string Name, Func<Task<bool>> SaveAsync, Func<bool> Discard);
