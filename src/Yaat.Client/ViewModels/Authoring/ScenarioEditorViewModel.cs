using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Sim.Authoring;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>
/// The Scenario Editor's editing side: the one working copy open (a <see cref="ScenarioEditSession"/> over the browser's
/// store) and its General and Aircraft tabs, with Save, Undo and Redo. A copy opens on the browser's
/// <see cref="ScenarioBrowserViewModel.OpenRequested"/>; leaving it with unsaved edits (opening another, closing the
/// window, switching ARTCC: the browser's <see cref="ScenarioBrowserViewModel.LeaveGuard"/>) asks to save, discard or
/// cancel first. Deleting the open copy closes it without asking; an import that replaces it reloads it, asking first
/// (the browser's <see cref="ScenarioBrowserViewModel.ReplaceGuard"/>) when it has unsaved edits. Runs on the UI thread;
/// every await re-checks that the session it started with is still the one open, and an open abandons itself when a
/// newer open started.
/// </summary>
/// <remarks>
/// An edit made after the replace question was answered (or a copy opened after it) is never dropped: the session is
/// kept with <see cref="ReplacedByImportText"/>, and its next Save asks before writing over the import.
/// </remarks>
public sealed partial class ScenarioEditorViewModel : ObservableObject, IDisposable
{
    /// <summary>The save message when an import replaced the open copy's file while it had edits the import would drop.</summary>
    public const string ReplacedByImportText = "The file was replaced by an import; Save would overwrite it.";

    private static readonly ILogger Log = AppLog.CreateLogger<ScenarioEditorViewModel>();

    private readonly ScenarioBrowserViewModel _browser;
    private readonly ScenarioBrowserServices _services;
    private readonly Func<Task<bool>> _guard;
    private readonly Func<string, string, Task<bool>> _replaceGuard;
    private ScenarioEditSession? _session;
    private ScenarioGeneralViewModel? _general;
    private ScenarioAircraftViewModel? _aircraft;
    private int _openSeq;
    private bool _disposed;

    // Counts every committed edit, undo and redo, so the replace reload can tell whether the session changed since.
    private int _edits;

    // The session the replace guard last answered for and its edit count then; null when it answered for none.
    private (ScenarioEditSession Session, int Edits)? _replaceAnswer;

    // The session kept open over a file an import replaced; its next Save asks before writing.
    private ScenarioEditSession? _replacedUnder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsPlaceholder))]
    private string? _openError;

    [ObservableProperty]
    private string? _saveMessage;

    /// <summary>Initializes a new instance of the <see cref="ScenarioEditorViewModel"/> class over a browser.</summary>
    /// <param name="browser">The browser whose opens and deletes it follows, and whose leave guard it answers.</param>
    /// <param name="services">The browser's services: the store, the prompts, the pickers' config source and the post.</param>
    public ScenarioEditorViewModel(ScenarioBrowserViewModel browser, ScenarioBrowserServices services)
    {
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _guard = ConfirmLeaveAsync;
        _replaceGuard = ConfirmReplaceAsync;
        _browser.OpenRequested += OnOpenRequested;
        _browser.WorkingCopyDeleted += OnWorkingCopyDeleted;
        _browser.WorkingCopyReplaced += OnWorkingCopyReplaced;
        _browser.LeaveGuard = _guard;
        _browser.ReplaceGuard = _replaceGuard;
    }

    /// <summary>Gets the open working copy's session; null when nothing is open.</summary>
    public ScenarioEditSession? Session
    {
        get => _session;
        private set
        {
            if (SetProperty(ref _session, value))
            {
                OnPropertyChanged(nameof(IsOpen));
                OnPropertyChanged(nameof(ShowsPlaceholder));
                NotifyState();
            }
        }
    }

    /// <summary>Gets the open copy's General tab; null when nothing is open.</summary>
    public ScenarioGeneralViewModel? General
    {
        get => _general;
        private set => SetProperty(ref _general, value);
    }

    /// <summary>Gets the open copy's Aircraft tab; null when nothing is open.</summary>
    public ScenarioAircraftViewModel? Aircraft
    {
        get => _aircraft;
        private set => SetProperty(ref _aircraft, value);
    }

    /// <summary>Gets a value indicating whether a working copy is open.</summary>
    public bool IsOpen => Session is not null;

    /// <summary>Gets a value indicating whether the browser's placeholder texts show: no copy is open and no open failed.</summary>
    public bool ShowsPlaceholder => (!IsOpen) && (OpenError is null);

    /// <summary>Gets a value indicating whether the open copy has edits not yet saved.</summary>
    public bool IsDirty => Session?.IsDirty ?? false;

    /// <summary>Gets the description of the edit Undo would revert, for its tooltip; null when there is none.</summary>
    public string? UndoDescription => Session?.UndoDescription;

    /// <summary>Gets the description of the edit Redo would reapply, for its tooltip; null when there is none.</summary>
    public string? RedoDescription => Session?.RedoDescription;

    /// <summary>Gets the latest open, finished once the copy is open or the open was refused or failed. Tests await it.</summary>
    public Task OpenTask { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Opens a working copy, first asking about the unsaved edits of the copy already open. The copy already open opens
    /// nothing new; an open overtaken by a newer one opens nothing; a copy that cannot be read (or any other failure)
    /// opens nothing, writes nothing and sets <see cref="OpenError"/>. Never throws.
    /// </summary>
    /// <param name="artccId">The ARTCC whose folder holds the copy.</param>
    /// <param name="id">The working copy's id.</param>
    /// <returns>A task that finishes once the copy is open or the open was refused or failed.</returns>
    public async Task OpenAsync(string artccId, string id)
    {
        try
        {
            await OpenCoreAsync(artccId, id);
        }
        catch (Exception ex)
        {
            LogFailure(ex, "opening " + id);
            OpenError = $"Couldn't open {id}: {ex.Message}";
        }
    }

    /// <summary>
    /// Whether the open copy may be left: true when nothing is open or nothing is unsaved; else the user is asked. Save
    /// saves and goes on only once saved; Discard closes the copy without saving; Cancel (or a failed prompt) stays. False
    /// when the open copy changed while the question or the save's reload was pending.
    /// </summary>
    /// <returns>True to go on.</returns>
    public async Task<bool> ConfirmLeaveAsync()
    {
        if (Session is not { IsDirty: true } session)
        {
            return true;
        }

        UnsavedChangesChoice choice;
        try
        {
            choice = await _services.Prompts.AskUnsavedChangesAsync(DisplayName(session));
        }
        catch (Exception ex)
        {
            LogFailure(ex, "asking about unsaved changes");
            return false;
        }

        if (!ReferenceEquals(Session, session))
        {
            return false;
        }

        switch (choice)
        {
            case UnsavedChangesChoice.Save:
                return await SaveBeforeLeaveAsync(session);
            case UnsavedChangesChoice.Discard:
                CloseSession();
                return true;
            default:
                return false;
        }
    }

    /// <summary>Stops following the browser, gives back its leave guard when it is still this editor's, and closes the open copy unsaved.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _openSeq++;
        _browser.OpenRequested -= OnOpenRequested;
        _browser.WorkingCopyDeleted -= OnWorkingCopyDeleted;
        _browser.WorkingCopyReplaced -= OnWorkingCopyReplaced;
        if (_browser.LeaveGuard == _guard)
        {
            _browser.LeaveGuard = () => Task.FromResult(true);
        }

        if (_browser.ReplaceGuard == _replaceGuard)
        {
            _browser.ReplaceGuard = (_, _) => Task.FromResult(true);
        }

        CloseSession();
    }

    private async Task OpenCoreAsync(string artccId, string id)
    {
        if (_disposed || ((Session is { } open) && (open.ArtccId == artccId) && (open.ScenarioId == id)))
        {
            return;
        }

        int seq = ++_openSeq;
        if (!await ConfirmLeaveAsync() || (seq != _openSeq))
        {
            return;
        }

        OpenSessionNow(artccId, id);
    }

    // Closes what is open, unsaved, and opens the copy from its file; throws when it cannot be read.
    private void OpenSessionNow(string artccId, string id)
    {
        CloseSession();
        OpenError = null;
        var session = new ScenarioEditSession(_services.Store, artccId, id);
        var general = new ScenarioGeneralViewModel(session, _services);
        var aircraft = new ScenarioAircraftViewModel(session, _services)
        {
            AirportOptions = general.AirportOptions,
            PositionCatalog = general.PositionCatalog,
        };
        session.DocumentReplaced += OnDocumentReplaced;
        general.Edited += OnEdited;
        general.PropertyChanged += OnGeneralPropertyChanged;
        aircraft.Edited += OnEdited;
        Session = session;
        General = general;
        Aircraft = aircraft;
    }

    private bool CanSave() => IsDirty;

    private bool CanUndo() => Session?.CanUndo ?? false;

    private bool CanRedo() => Session?.CanRedo ?? false;

    /// <summary>
    /// Saves the open copy; the browser then re-reads its rows with it selected. A refused save sets
    /// <see cref="SaveMessage"/>. When an import replaced the file under edits, the unsaved-changes question is asked first:
    /// Save overwrites the import, Discard reloads it, Cancel writes nothing.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (Session is not { } session)
        {
            return;
        }

        if (ReferenceEquals(_replacedUnder, session) && !await ConfirmOverwriteImportAsync(session))
        {
            return;
        }

        if (TrySave(session))
        {
            _replacedUnder = null;
            await _browser.NotifyWorkingCopySavedAsync(session.ArtccId, session.ScenarioId);
        }
    }

    // True to write over the imported file; Discard reloads the import instead, and a changed session writes nothing.
    private async Task<bool> ConfirmOverwriteImportAsync(ScenarioEditSession session)
    {
        UnsavedChangesChoice choice;
        try
        {
            choice = await _services.Prompts.AskUnsavedChangesAsync(DisplayName(session));
        }
        catch (Exception ex)
        {
            LogFailure(ex, "asking before a save over an imported file");
            return false;
        }

        if (!ReferenceEquals(Session, session))
        {
            return false;
        }

        if (choice == UnsavedChangesChoice.Discard)
        {
            Reload(session.ArtccId, session.ScenarioId);
        }

        return choice == UnsavedChangesChoice.Save;
    }

    /// <summary>Reverts the last edit.</summary>
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        Session?.Undo();
        _edits++;
        NotifyState();
    }

    /// <summary>Reapplies the last undone edit.</summary>
    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        Session?.Redo();
        _edits++;
        NotifyState();
    }

    // Saves, then has the browser re-read its rows without moving its selection (it is on the copy being opened, or the
    // ARTCC being switched to); true only when saved and the session is still the one open afterwards.
    private async Task<bool> SaveBeforeLeaveAsync(ScenarioEditSession session)
    {
        if (!TrySave(session))
        {
            return false;
        }

        await _browser.NotifyWorkingCopySavedAsync(session.ArtccId, null);
        return ReferenceEquals(Session, session);
    }

    // True only when the document was written; any other outcome, or a write failure, sets the save message.
    private bool TrySave(ScenarioEditSession session)
    {
        ScenarioSaveResult result;
        try
        {
            result = session.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogFailure(ex, "saving " + session.ScenarioId);
            SaveMessage = $"Couldn't save: {ex.Message}";
            NotifyState();
            return false;
        }

        bool saved = result.Outcome == ScenarioSaveOutcome.Saved;
        SaveMessage = saved ? null : result.Reason;
        NotifyState();
        return saved;
    }

    private void OnOpenRequested(object? sender, ScenarioOpenRequestedEventArgs e) => OpenTask = OpenAsync(e.ArtccId, e.Id);

    private void OnWorkingCopyDeleted(object? sender, ScenarioWorkingCopyDeletedEventArgs e)
    {
        if ((Session is { } session) && (session.ArtccId == e.ArtccId) && (session.ScenarioId == e.Id))
        {
            CloseSession();
        }
    }

    // The browser's ReplaceGuard: an import may replace any copy but the open one with unsaved edits without asking. The
    // answer is about the file, so it stands whatever is open once the question is answered. When the copy is open, the
    // session the answer covers and its edit count are recorded, so the reload drops nothing edited after the answer.
    private async Task<bool> ConfirmReplaceAsync(string artccId, string id)
    {
        _replaceAnswer = null;
        if (!IsOpenCopy(artccId, id) || (Session is not { } session))
        {
            return true;
        }

        if (!session.IsDirty)
        {
            _replaceAnswer = (session, _edits);
            return true;
        }

        bool replace;
        try
        {
            replace = await _services.Prompts.ConfirmReplaceOpenCopyAsync(DisplayName(session));
        }
        catch (Exception ex)
        {
            LogFailure(ex, "asking before an import replaces " + id);
            return false;
        }

        if (replace && ReferenceEquals(Session, session))
        {
            _replaceAnswer = (session, _edits);
        }

        return replace;
    }

    // An import replaced the open copy's file. The session the guard answered for, unedited since, re-reads it; any
    // other (edited since, or opened after the answer) is kept with a warning, and its next Save asks first.
    private void OnWorkingCopyReplaced(object? sender, ScenarioWorkingCopyReplacedEventArgs e)
    {
        (ScenarioEditSession Session, int Edits)? answered = _replaceAnswer;
        _replaceAnswer = null;
        if (!IsOpenCopy(e.ArtccId, e.Id))
        {
            return;
        }

        if ((answered is { } a) && ReferenceEquals(a.Session, Session) && (a.Edits == _edits))
        {
            Reload(e.ArtccId, e.Id);
            return;
        }

        _replacedUnder = Session;
        SaveMessage = ReplacedByImportText;
    }

    // Re-reads the copy from its file, dropping the session's edits; a failure leaves nothing open and sets OpenError.
    private void Reload(string artccId, string id)
    {
        _openSeq++;
        try
        {
            OpenSessionNow(artccId, id);
        }
        catch (Exception ex)
        {
            LogFailure(ex, "reloading " + id);
            OpenError = $"Couldn't open {id}: {ex.Message}";
        }
    }

    private bool IsOpenCopy(string artccId, string id) => (Session is { } session) && (session.ArtccId == artccId) && (session.ScenarioId == id);

    private void OnDocumentReplaced(object? sender, EventArgs e)
    {
        General?.Reload();
        Aircraft?.Reload();
    }

    private void OnEdited(object? sender, EventArgs e)
    {
        _edits++;
        NotifyState();
    }

    // The General tab's position catalog arrived: the Aircraft tab's auto-track picker offers it too.
    private void OnGeneralPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (
            (e.PropertyName == nameof(ScenarioGeneralViewModel.PositionCatalog))
            && (sender is ScenarioGeneralViewModel general)
            && ReferenceEquals(sender, General)
            && (Aircraft is { } aircraft)
        )
        {
            aircraft.PositionCatalog = general.PositionCatalog;
        }
    }

    private void CloseSession()
    {
        if (Session is { } session)
        {
            session.DocumentReplaced -= OnDocumentReplaced;
        }

        if (General is { } general)
        {
            general.Edited -= OnEdited;
            general.PropertyChanged -= OnGeneralPropertyChanged;
            general.Dispose();
        }

        if (Aircraft is { } aircraft)
        {
            aircraft.Edited -= OnEdited;
        }

        Aircraft = null;
        General = null;
        Session = null;
        SaveMessage = null;
        _replacedUnder = null;
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(UndoDescription));
        OnPropertyChanged(nameof(RedoDescription));
        SaveCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    private static string DisplayName(ScenarioEditSession session)
    {
        string name = session.Document.Scenario.Name;
        return string.IsNullOrWhiteSpace(name) ? session.ScenarioId : name;
    }

    private static void LogFailure(Exception ex, string what)
    {
        if (Log.IsEnabled(LogLevel.Warning))
        {
            Log.LogWarning(ex, "Scenario editor: {What} failed", what);
        }
    }
}
