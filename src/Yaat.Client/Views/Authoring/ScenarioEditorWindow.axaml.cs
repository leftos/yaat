using System.ComponentModel;
using Avalonia.Controls;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Client.ViewModels.Authoring;

namespace Yaat.Client.Views.Authoring;

/// <summary>
/// Tools › Scenario Editor: a non-modal window over a <see cref="ScenarioBrowserViewModel"/> with the ARTCC and the
/// browser's commands in a toolbar, the browser beside a content area that stays empty until the editor tabs arrive,
/// and the status line and resync progress along the bottom. Opening it reads the ARTCCs and starts the resync once;
/// closing it cancels a running resync.
/// </summary>
public partial class ScenarioEditorWindow : Window
{
    /// <summary>The key the window's position and size are kept under.</summary>
    public const string GeometryKey = "ScenarioEditor";

    private const string BaseTitle = "Scenario Editor";

    private static readonly ILogger Log = AppLog.CreateLogger<ScenarioEditorWindow>();

    private readonly WindowGeometryHelper _geometry;

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
        Browser = new ScenarioBrowserViewModel(services(new ScenarioBrowserPrompts(this, filePickerFactory(this))));
        DataContext = Browser;
        _geometry = new WindowGeometryHelper(this, preferences, GeometryKey, 1100, 700);
        _geometry.Restore();
        CanCloseNow = Browser.CanClose;
        Browser.PropertyChanged += OnBrowserPropertyChanged;
        Opened += OnOpened;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private ScenarioEditorWindow(UserPreferences preferences)
        : this(preferences, prompts => ScenarioBrowserServices.ForApp(preferences, prompts, new NoArtccs()), FilePickerFactory.Create) { }

    /// <summary>Gets the browser the window shows.</summary>
    internal ScenarioBrowserViewModel Browser { get; }

    /// <summary>Gets the browser's first load: done once the ARTCCs are read and the first rows shown. Tests await it.</summary>
    internal Task BrowserReady { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Gets or sets whether the window may close now: the browser's <see cref="ScenarioBrowserViewModel.CanClose"/>; tests replace it.
    /// </summary>
    internal Func<bool> CanCloseNow { get; set; }

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

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!CanCloseNow())
        {
            e.Cancel = true;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Browser.PropertyChanged -= OnBrowserPropertyChanged;
        Browser.Dispose();
    }

    // The designer's access: no ARTCC.
    private sealed class NoArtccs : IArtccAccessSource
    {
        public Task<IReadOnlyList<string>> GetEditableArtccsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
    }
}
