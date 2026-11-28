using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim.Authoring;
using Yaat.Sim.Authoring.Spawns;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.Views.Authoring;

/// <summary>
/// The spawn planner over one scenario's planner: the filter, the band or workload inputs, the minimum gap, the order and
/// the planning length on the left; the workload graph, the preview table, the unassigned callsigns and the warnings on
/// the right; plans load and save through a file picker that starts in the folder last used. Closing it disposes the
/// planner.
/// </summary>
public partial class SpawnPlannerWindow : Window
{
    /// <summary>The key the window's position and size are kept under.</summary>
    public const string GeometryKey = "SpawnPlanner";

    private static readonly ILogger Log = AppLog.CreateLogger<SpawnPlannerWindow>();

    /// <summary>The start of the warning a file picker that fails shows; its message follows.</summary>
    public const string PickerErrorPrefix = "Could not open the file picker: ";

    /// <summary>The start of the warning a plan folder that cannot be remembered shows; its message follows.</summary>
    public const string FolderErrorPrefix = "Couldn't remember the folder: ";

    private static readonly IReadOnlyList<FilePickerFilter> PlanFilters = [new FilePickerFilter("Spawn plan", ["*.json"])];

    private readonly SpawnPlannerViewModel _viewModel;
    private readonly UserPreferences _preferences;
    private readonly WindowGeometryHelper _geometry;
    private readonly IFilePickerService _picker;

    // Parameterless ctor for the Avalonia designer / XamlLoader: a planner over an empty scenario that writes nowhere.
    public SpawnPlannerWindow()
        : this(DesignerPlanner(), new UserPreferences(), "", FilePickerFactory.Create) { }

    private SpawnPlannerWindow(
        SpawnPlannerViewModel viewModel,
        UserPreferences preferences,
        string scenarioName,
        Func<TopLevel, IFilePickerService> filePickerFactory
    )
    {
        InitializeComponent();
        AutomationGate.ApplyShowActivated(this);
        _viewModel = viewModel;
        _preferences = preferences;
        _picker = filePickerFactory(this);
        _geometry = new WindowGeometryHelper(this, preferences, GeometryKey, 1100, 760);
        _geometry.Restore();
        Title = $"Spawn planner — {scenarioName}";
        DataContext = viewModel;
        Graph.Attach(viewModel);
        LoadButton.Click += OnLoadClick;
        SaveButton.Click += OnSaveClick;
        Closed += OnClosed;
    }

    /// <summary>Gets the planning modes the mode box offers.</summary>
    public static IReadOnlyList<SpawnPlannerMode> Modes { get; } = Enum.GetValues<SpawnPlannerMode>();

    /// <summary>Gets the overrun remedies the remedy box offers.</summary>
    public static IReadOnlyList<SpawnOverrunRemedy> Remedies { get; } = Enum.GetValues<SpawnOverrunRemedy>();

    /// <summary>Shows a spawn delay as m:ss, a negative one with a leading minus.</summary>
    public static FuncValueConverter<int, string> Time { get; } =
        new(seconds => (seconds < 0) ? "-" + ScenarioTime.Format(-seconds) : ScenarioTime.Format(seconds));

    /// <summary>True for the Bands mode, which shows the band rows.</summary>
    public static FuncValueConverter<SpawnPlannerMode, bool> IsBands { get; } = new(mode => mode == SpawnPlannerMode.Bands);

    /// <summary>True for the Workload mode, which shows the curve's inputs.</summary>
    public static FuncValueConverter<SpawnPlannerMode, bool> IsWorkload { get; } = new(mode => mode == SpawnPlannerMode.Workload);

    /// <summary>Names an overrun remedy as the remedy box shows it.</summary>
    public static FuncValueConverter<SpawnOverrunRemedy, string> RemedyLabel { get; } =
        new(remedy =>
            remedy switch
            {
                SpawnOverrunRemedy.ExtendScenario => "Extend the planning length",
                SpawnOverrunRemedy.CompressIntervals => "Compress the intervals",
                SpawnOverrunRemedy.None => "Leave the overrun",
                _ => remedy.ToString(),
            }
        );

    /// <summary>Creates the planner window over a planner; closing the window disposes the planner.</summary>
    /// <param name="viewModel">The planner.</param>
    /// <param name="preferences">Keeps the window's position and size and the plan folder last used.</param>
    /// <param name="scenarioName">The scenario's name, shown in the title.</param>
    /// <returns>The window, not yet shown.</returns>
    public static SpawnPlannerWindow Create(SpawnPlannerViewModel viewModel, UserPreferences preferences, string scenarioName) =>
        Create(viewModel, preferences, scenarioName, FilePickerFactory.Create);

    /// <summary>Creates the planner window over a planner with a given file picker; closing the window disposes the planner.</summary>
    /// <param name="viewModel">The planner.</param>
    /// <param name="preferences">Keeps the window's position and size and the plan folder last used.</param>
    /// <param name="scenarioName">The scenario's name, shown in the title.</param>
    /// <param name="filePickerFactory">Creates the window's file picker (<see cref="FilePickerFactory.Create"/>).</param>
    /// <returns>The window, not yet shown.</returns>
    public static SpawnPlannerWindow Create(
        SpawnPlannerViewModel viewModel,
        UserPreferences preferences,
        string scenarioName,
        Func<TopLevel, IFilePickerService> filePickerFactory
    )
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(scenarioName);
        ArgumentNullException.ThrowIfNull(filePickerFactory);
        return new SpawnPlannerWindow(viewModel, preferences, scenarioName, filePickerFactory);
    }

    private static SpawnPlannerViewModel DesignerPlanner()
    {
        var scenario = new Scenario { Id = "designer", Name = "Designer" };
        var session = ScenarioEditSession.OverText("", "designer", ScenarioDocument.FromScenario(scenario).Write());
        return new SpawnPlannerViewModel(session, (_, _) => { });
    }

    private async void OnLoadClick(object? sender, RoutedEventArgs e) => await LoadPlanAsync();

    private async void OnSaveClick(object? sender, RoutedEventArgs e) => await SavePlanAsync();

    // Only the picker call is guarded as a picker failure; the plan is then loaded, and the folder remembered after it.
    private async Task LoadPlanAsync()
    {
        var options = new OpenFileOptions("Load spawn plan", PlanFilters) { StartFolder = _preferences.SpawnPlannerLastFolder };
        string? path;
        try
        {
            path = await _picker.OpenFileAsync(options);
        }
        catch (Exception ex)
        {
            ShowPickerFailure(ex, "load");
            return;
        }

        if (path is not null)
        {
            RememberFolder(path, _viewModel.LoadPlanFile(path));
        }
    }

    private async Task SavePlanAsync()
    {
        var options = new SaveFileOptions("Save spawn plan", "spawn-plan.json", PlanFilters, "json")
        {
            StartFolder = _preferences.SpawnPlannerLastFolder,
        };
        string? path;
        try
        {
            path = await _picker.SaveFileAsync(options);
        }
        catch (Exception ex)
        {
            ShowPickerFailure(ex, "save");
            return;
        }

        if (path is not null)
        {
            RememberFolder(path, _viewModel.SavePlan(path));
        }
    }

    private void ShowPickerFailure(Exception ex, string action)
    {
        Log.LogError(ex, "Spawn planner: the {Action} picker failed", action);
        _viewModel.PlanError = PickerErrorPrefix + ex.Message;
    }

    // Keeps the picked file's folder for the next picker; a failure is logged, and shown only when the plan itself
    // succeeded, so it never hides the plan's own error.
    private void RememberFolder(string path, bool planSucceeded)
    {
        if (Path.GetDirectoryName(path) is not { Length: > 0 } folder)
        {
            return;
        }

        try
        {
            _preferences.SetSpawnPlannerLastFolder(folder);
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Spawn planner: could not remember the plan folder {Folder}", folder);
            if (planSucceeded)
            {
                _viewModel.PlanError = FolderErrorPrefix + ex.Message;
            }
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        LoadButton.Click -= OnLoadClick;
        SaveButton.Click -= OnSaveClick;
        Graph.Detach();
        _viewModel.Dispose();
    }
}
