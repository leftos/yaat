using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Client.ViewModels.Authoring;

namespace Yaat.Client.Views.Authoring;

/// <summary>
/// Tools › Scenario Editor: a non-modal window over a <see cref="ScenarioBrowserViewModel"/> and the
/// <see cref="ScenarioEditorViewModel"/> that follows it, with the ARTCC, the browser's commands and Undo, Redo and Save
/// (Ctrl+S) in a toolbar, the browser beside the open copy's General tab (placeholder texts while nothing is open), and
/// the status line, save message and resync progress along the bottom. Opening it reads the ARTCCs and starts the
/// resync once; closing it asks about unsaved edits (an application or OS shutdown does not) and cancels a running resync.
/// Ctrl+S and closing first commit the text field still being typed in.
/// </summary>
public partial class ScenarioEditorWindow : Window
{
    /// <summary>The key the window's position and size are kept under.</summary>
    public const string GeometryKey = "ScenarioEditor";

    private const string BaseTitle = "Scenario Editor";

    private static readonly ILogger Log = AppLog.CreateLogger<ScenarioEditorWindow>();

    private readonly WindowGeometryHelper _geometry;

    // Set once an asked close was allowed, so the close it starts again goes through.
    private bool _closeAllowed;

    // Parameterless ctor for the Avalonia designer / XamlLoader: no ARTCC to edit, so it lists nothing.
    public ScenarioEditorWindow()
        : this(new UserPreferences()) { }

    /// <summary>Creates the window.</summary>
    /// <param name="preferences">Keeps the window's position and size.</param>
    /// <param name="services">Builds the browser's services over the window's dialogs and pickers.</param>
    /// <param name="filePickerFactory">Creates the file picker for the window (<see cref="FilePickerFactory.Create"/>).</param>
    public ScenarioEditorWindow(
        UserPreferences preferences,
        Func<IScenarioBrowserPrompts, ScenarioBrowserServices> services,
        Func<TopLevel, IFilePickerService> filePickerFactory
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(filePickerFactory);
        InitializeComponent();
        ScenarioBrowserServices built = services(new ScenarioBrowserPrompts(this, filePickerFactory(this)));
        Browser = new ScenarioBrowserViewModel(built);
        Editor = new ScenarioEditorViewModel(Browser, built);
        EditorCommands.DataContext = Editor;
        EditorPane.DataContext = Editor;
        SaveMessageText.DataContext = Editor;
        DataContext = Browser;
        PlaceholderPanel.Bind(IsVisibleProperty, CompiledBinding.Create<ScenarioEditorViewModel, bool>(vm => vm.ShowsPlaceholder, source: Editor));
        _geometry = new WindowGeometryHelper(this, preferences, GeometryKey, 1100, 700);
        _geometry.Restore();
        CanCloseNow = Browser.CanCloseAsync;
        Browser.PropertyChanged += OnBrowserPropertyChanged;
        KeyDown += OnWindowKeyDown;
        Opened += OnOpened;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private ScenarioEditorWindow(UserPreferences preferences)
        : this(preferences, prompts => ScenarioBrowserServices.ForApp(preferences, prompts, new NoArtccs()), FilePickerFactory.Create) { }

    /// <summary>Gets the browser the window shows.</summary>
    internal ScenarioBrowserViewModel Browser { get; }

    /// <summary>Gets the editor over the browser: the open working copy, its General tab, and Save, Undo and Redo.</summary>
    internal ScenarioEditorViewModel Editor { get; }

    /// <summary>Gets the browser's first load: done once the ARTCCs are read and the first rows shown. Tests await it.</summary>
    internal Task BrowserReady { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Gets or sets whether the window may close now: the browser's <see cref="ScenarioBrowserViewModel.CanCloseAsync"/>; tests replace it.
    /// </summary>
    internal Func<Task<bool>> CanCloseNow { get; set; }

    // The first load is awaited here so a failure is logged and shown in the status line, never left unobserved.
    private async void OnOpened(object? sender, EventArgs e)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        BrowserReady = ready.Task;
        try
        {
            await Browser.InitializeAsync();
        }
        catch (Exception ex)
        {
            if (Log.IsEnabled(LogLevel.Error))
            {
                Log.LogError(ex, "Opening the Scenario Editor's scenario list failed");
            }

            Browser.StatusText = $"Couldn't open the scenario list: {ex.Message}";
        }
        finally
        {
            ready.SetResult();
        }
    }

    private void OnBrowserPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScenarioBrowserViewModel.ArtccLabelText))
        {
            _geometry.SetBaseTitle(Browser.HasArtcc ? $"{BaseTitle} — {Browser.ArtccLabelText}" : BaseTitle);
        }
    }

    // Ctrl+S saves, taking the text field still being typed in first.
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if ((e.Key == Key.S) && (e.KeyModifiers == KeyModifiers.Control))
        {
            e.Handled = true;
            GeneralView.CommitFocusedField();
            AircraftView.CommitFocusedField();
            if (Editor.SaveCommand.CanExecute(null))
            {
                Editor.SaveCommand.Execute(null);
            }
        }
    }

    // The text field still being typed in is taken first. An application or OS shutdown closes without asking; otherwise
    // an answer already known decides this close, and one that asks the user cancels it and closes again once allowed.
    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeAllowed)
        {
            return;
        }

        GeneralView.CommitFocusedField();
        AircraftView.CommitFocusedField();
        if (ClosesForShutdown(e.CloseReason))
        {
            return;
        }

        Task<bool> guard = CanCloseNow();
        if (guard.IsCompleted)
        {
            if ((guard.Exception is { } failure) && Log.IsEnabled(LogLevel.Error))
            {
                Log.LogError(failure.GetBaseException(), "Asking before the Scenario Editor closes failed");
            }

            e.Cancel = !(guard.IsCompletedSuccessfully && guard.Result);
            return;
        }

        e.Cancel = true;
        _ = CloseWhenAllowedAsync(guard);
    }

    // An application or OS shutdown is never held by the unsaved-changes question, nor asks it: the open copy's unsaved
    // edits are dropped, with a warning.
    private bool ClosesForShutdown(WindowCloseReason reason)
    {
        if (reason is not (WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown))
        {
            return false;
        }

        if (Editor.IsDirty && Log.IsEnabled(LogLevel.Warning))
        {
            Log.LogWarning("The app is shutting down with unsaved edits in the Scenario Editor; the open copy's unsaved edits are dropped");
        }

        return true;
    }

    private async Task CloseWhenAllowedAsync(Task<bool> guard)
    {
        bool allowed;
        try
        {
            allowed = await guard;
        }
        catch (Exception ex)
        {
            if (Log.IsEnabled(LogLevel.Error))
            {
                Log.LogError(ex, "Asking before the Scenario Editor closes failed");
            }

            allowed = false;
        }

        if (allowed)
        {
            _closeAllowed = true;
            Close();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Browser.PropertyChanged -= OnBrowserPropertyChanged;
        Editor.Dispose();
        Browser.Dispose();
    }

    // The designer's access: no ARTCC.
    private sealed class NoArtccs : IArtccAccessSource
    {
        public Task<IReadOnlyList<string>> GetEditableArtccsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
    }
}
