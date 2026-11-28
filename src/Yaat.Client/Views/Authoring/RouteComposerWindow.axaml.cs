using Avalonia.Controls;
using Yaat.Client.Services;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim.Authoring.Routes;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Client.Views.Authoring;

/// <summary>
/// The route composer window: the route library filtered to the composer's ARTCC on the left (Show all ARTCCs lists every
/// route), the name and colour of the route being edited with New, Save and Delete; the map in the centre with a Maps
/// drop-down of the ARTCC's video maps; under it the route's nav-path text and the warning or failure line; the navdata
/// state in the status bar. The maps load off the UI thread when the window opens. Code-behind only wires the view model
/// to the canvas.
/// </summary>
public partial class RouteComposerWindow : Window
{
    /// <summary>The key the window's position and size are kept under.</summary>
    public const string GeometryKey = "RouteComposer";

    private readonly CancellationTokenSource _closing = new();
    private readonly WindowGeometryHelper _geometry;

    // Parameterless ctor for the Avalonia designer / XamlLoader: it loads no map and lists the per-user library.
    public RouteComposerWindow()
        : this(
            new RouteComposerViewModel(
                new RouteComposerContext("ZOA", FixResolutionContext.None, _ => Task.FromResult<IReadOnlyList<RouteComposerMap>>([])),
                new RouteLibrary(),
                () => null
            ),
            new UserPreferences()
        ) { }

    /// <summary>Creates the window over a composer and starts loading its video maps.</summary>
    /// <param name="viewModel">The composer.</param>
    /// <param name="preferences">Keeps the window's position and size.</param>
    public RouteComposerWindow(RouteComposerViewModel viewModel, UserPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(preferences);
        InitializeComponent();
        AutomationGate.ApplyShowActivated(this);
        _geometry = new WindowGeometryHelper(this, preferences, GeometryKey, 1100, 750);
        _geometry.Restore();
        ViewModel = viewModel;
        DataContext = viewModel;
        Title = viewModel.Title;
        MapCanvas.ViewModel = viewModel;
        viewModel.RouteLoaded += OnRouteLoaded;
        Closed += OnClosed;
        LoadTask = LoadAsync();
    }

    /// <summary>Gets the composer the window shows.</summary>
    public RouteComposerViewModel ViewModel { get; }

    /// <summary>Gets the load of the video maps, done once the canvas shows them or the message line says why not. Test seam.</summary>
    internal Task LoadTask { get; }

    /// <summary>Creates the composer window for an ARTCC over the loaded navigation database, if any.</summary>
    /// <param name="context">The ARTCC, fix resolution and maps the composer opens over.</param>
    /// <param name="library">The per-user route library.</param>
    /// <param name="preferences">Keeps the window's position and size.</param>
    /// <returns>The window, not yet shown.</returns>
    public static RouteComposerWindow Create(RouteComposerContext context, RouteLibrary library, UserPreferences preferences) =>
        new(new RouteComposerViewModel(context, library, () => NavigationDatabase.InstanceOrNull), preferences);

    private async Task LoadAsync()
    {
        CancellationToken ct = _closing.Token;
        await ViewModel.LoadMapsAsync(ct);
        if (!ct.IsCancellationRequested)
        {
            MapCanvas.FitToContent();
        }
    }

    private void OnRouteLoaded(object? sender, EventArgs e) => MapCanvas.FitToContent();

    private void OnClosed(object? sender, EventArgs e)
    {
        _closing.Cancel();
        _closing.Dispose();
        ViewModel.RouteLoaded -= OnRouteLoaded;
        MapCanvas.Dispose();
    }
}
