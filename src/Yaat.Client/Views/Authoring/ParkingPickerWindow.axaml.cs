using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim.Authoring.Gates;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.Views.Authoring;

/// <summary>
/// Picks an aircraft's parking on the airport's vNAS map, with no room: the airport's stands (parking, spot and helipad
/// nodes) over its taxiways and runways, fitted to the stands with the current stand ringed. A click selects a stand and OK
/// returns its name, a double-click returns it at once; Esc or Cancel returns null. The map is fetched and parsed off the
/// UI thread; when there is none (vNAS has no map, or cannot be reached and none is cached) the status line says so.
///
/// <para>Given a ranking (<see cref="GateAssignContext"/>) it is Assign Gate: every ranked stand is coloured and listed with
/// its reason, and a mode draws the stand OK returns. Random draws a free, fitting stand; In area draws one within the
/// chosen parking area; From map leaves the choice to a click, and a stand that is too small, taken or another airline's
/// is still chosen, its status line naming the reason. When nothing free fits, nothing is selected and the status line
/// says the parking is unchanged.</para>
/// </summary>
public partial class ParkingPickerWindow : Window
{
    /// <summary>The key the window's position and size are kept under.</summary>
    public const string GeometryKey = "ParkingPicker";

    private const string FromMapText = "Click a stand, then OK, or double-click it. A stand that does not fit is assigned anyway.";

    private static readonly ILogger Log = AppLog.CreateLogger<ParkingPickerWindow>();

    // One downloader for every picker the app opens, never disposed: its HttpClient lives as long as the process, so
    // closing a picker cannot dispose it under a fetch still in flight. It shares the vNAS airport cache on disk.
    private static readonly Lazy<AirportLayoutDownloader> SharedDownloader = new(() => new AirportLayoutDownloader());

    private readonly string _airportId;
    private readonly string? _currentParking;
    private readonly Func<string, CancellationToken, Task<AirportGroundLayout?>> _loadLayout;
    private readonly GateAssignContext? _ranking;
    private readonly CancellationTokenSource _closing = new();
    private readonly WindowGeometryHelper _geometry;
    private AirportGroundLayout? _layout;
    private IReadOnlyList<GateCandidate> _ranked = [];
    private IReadOnlyList<GateCandidate> _listed = [];

    // Parameterless ctor for the Avalonia designer / XamlLoader: it loads no map.
    public ParkingPickerWindow()
        : this("OAK", null, new UserPreferences(), (_, _) => Task.FromResult<AirportGroundLayout?>(null), null) { }

    /// <summary>Creates the picker over the app's vNAS airport cache.</summary>
    /// <param name="airportId">The airport whose stands are offered.</param>
    /// <param name="currentParking">The aircraft's parking value now, ringed when the map lists it; null for none.</param>
    /// <param name="preferences">Keeps the window's position and size.</param>
    /// <param name="ranking">The aircraft and scenario Assign Gate ranks the stands for, or null for the plain picker.</param>
    public ParkingPickerWindow(string airportId, string? currentParking, UserPreferences preferences, GateAssignContext? ranking)
        : this(airportId, currentParking, preferences, (id, ct) => SharedDownloader.Value.GetLayoutAsync(id, ct), ranking) { }

    /// <summary>Creates the picker and starts loading the airport's layout.</summary>
    /// <param name="airportId">The airport whose stands are offered.</param>
    /// <param name="currentParking">The aircraft's parking value now, ringed when the map lists it; null for none.</param>
    /// <param name="preferences">Keeps the window's position and size.</param>
    /// <param name="loadLayout">Loads the airport's layout, or null when there is no map; run off the UI thread.</param>
    /// <param name="ranking">The aircraft and scenario Assign Gate ranks the stands for, or null for the plain picker.</param>
    public ParkingPickerWindow(
        string airportId,
        string? currentParking,
        UserPreferences preferences,
        Func<string, CancellationToken, Task<AirportGroundLayout?>> loadLayout,
        GateAssignContext? ranking
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(airportId);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(loadLayout);
        InitializeComponent();
        AutomationGate.ApplyShowActivated(this);
        _geometry = new WindowGeometryHelper(this, preferences, GeometryKey, 900, 650);
        _geometry.Restore();
        _airportId = airportId.Trim();
        _currentParking = currentParking;
        _loadLayout = loadLayout;
        _ranking = ranking;
        Title = (ranking is null) ? $"Pick parking — {_airportId}" : $"Assign gate — {_airportId}";
        AirportText.Text = _airportId;
        StatusText.Text = $"Loading the vNAS map for {_airportId}…";
        MapCanvas.SelectionChanged += OnStandSelected;
        MapCanvas.StandActivated += OnStandActivated;
        OkButton.Click += OnOkClick;
        CancelButton.Click += OnCancelClick;
        RandomMode.IsCheckedChanged += OnModeChanged;
        AreaMode.IsCheckedChanged += OnModeChanged;
        MapMode.IsCheckedChanged += OnModeChanged;
        AreaBox.SelectionChanged += OnAreaChanged;
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        Closed += OnClosed;
        LoadTask = LoadAsync();
    }

    /// <summary>Gets the load of the airport's layout, done once the canvas shows it or the status line says why not. Test seam.</summary>
    internal Task LoadTask { get; }

    /// <summary>Shows the picker over <paramref name="owner"/> and finishes with the picked stand's name.</summary>
    /// <param name="owner">The window the picker is modal over.</param>
    /// <returns>The stand's name, or null when the picker was cancelled or closed.</returns>
    public Task<string?> ShowAsync(Window owner) => DialogPresenter.ShowModalAsync<string?>(this, owner);

    private async Task LoadAsync()
    {
        CancellationToken ct = _closing.Token;
        string airportId = _airportId;
        string? current = _currentParking;
        Func<string, CancellationToken, Task<AirportGroundLayout?>> load = _loadLayout;
        GateAssignContext? ranking = _ranking;
        PickerLoad? loaded;
        try
        {
            loaded = await Task.Run(
                async () =>
                {
                    AirportGroundLayout? layout = await load(airportId, ct).ConfigureAwait(false);
                    return (layout is null) ? null : PickerLoad.Of(layout, current, ranking);
                },
                ct
            );
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                Log.LogWarning(ex, "Parking picker: loading the vNAS map for {AirportId} failed", airportId);
                StatusText.Text = $"Could not load the vNAS map for {airportId}: {ex.Message}";
            }

            return;
        }

        if (!ct.IsCancellationRequested)
        {
            ShowScene(loaded);
        }
    }

    private void ShowScene(PickerLoad? loaded)
    {
        if (loaded is null)
        {
            StatusText.Text = $"No vNAS map for {_airportId}";
            return;
        }

        ParkingPickerScene scene = loaded.Scene;
        MapCanvas.Scene = scene;
        MapCanvas.FitToStands();
        StatusText.Text = scene.Stands.IsEmpty
            ? $"The vNAS map for {_airportId} has no parking"
            : "Click a stand, then OK, or double-click it. Right-drag pans, the wheel zooms.";
        ShowSelection(MapCanvas.Selected);
        if ((_ranking is not null) && !scene.Stands.IsEmpty)
        {
            ShowRanking(loaded);
        }
    }

    // Assign Gate's controls over a loaded ranking: the modes, the areas of the airport's stands, the note while navdata
    // loads, and the current mode's draw.
    private void ShowRanking(PickerLoad loaded)
    {
        if (_ranking is not { } ranking)
        {
            return;
        }

        _layout = loaded.Layout;
        _ranked = loaded.Ranked;
        GateModes.IsVisible = true;
        RankList.IsVisible = true;
        NoteText.IsVisible = ranking.NavdataLoading;
        NoteText.Text = ranking.NavdataLoading ? GateAssignContext.NavdataLoadingText : "";
        AreaBox.ItemsSource = GateAssignContext.AreasOf(_ranked);
        ApplyMode(new[] { RandomMode, AreaMode, MapMode }.First(m => m.IsChecked == true));
    }

    private void OnModeChanged(object? sender, RoutedEventArgs e)
    {
        if ((sender is RadioButton { IsChecked: true } mode) && (_layout is not null))
        {
            ApplyMode(mode);
        }
    }

    private void OnAreaChanged(object? sender, SelectionChangedEventArgs e)
    {
        if ((AreaMode.IsChecked == true) && (_layout is not null))
        {
            ApplyMode(AreaMode);
        }
    }

    // Random draws from the ranking the load made; In area ranks the chosen area; with no area chosen, and in From map, no
    // stand is drawn, so OK cannot write an earlier draw. The mode is the button just checked: a group's other button may
    // still read checked while its change is raised.
    private void ApplyMode(RadioButton mode)
    {
        if ((_ranking is not { } ranking) || (_layout is not { } layout))
        {
            return;
        }

        AreaBox.IsEnabled = ReferenceEquals(mode, AreaMode);
        if (ReferenceEquals(mode, RandomMode))
        {
            ShowDraw(ranking, new GateAssignment(GateRanking.Pick(_ranked, ranking.Random)?.Stand, _ranked));
        }
        else if (ReferenceEquals(mode, AreaMode) && (AreaBox.SelectedItem is string area))
        {
            DrawInArea(ranking, layout, area);
        }
        else
        {
            ClearDraw(ranking, ReferenceEquals(mode, AreaMode) ? "Choose a parking area" : FromMapText);
        }
    }

    private void DrawInArea(GateAssignContext ranking, AirportGroundLayout layout, string area)
    {
        GateAssignment drawn;
        try
        {
            drawn = ranking.Assign(layout, GateScope.Area(area));
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Parking picker: ranking the stands of {Area} at {AirportId} failed", area, _airportId);
            ClearDraw(ranking, $"Could not rank stands: {ex.Message}");
            return;
        }

        ShowDraw(ranking, drawn);
    }

    private void ShowDraw(GateAssignContext ranking, GateAssignment drawn)
    {
        ListRanking(ranking, drawn.Ranked);
        MapCanvas.Select(drawn.Stand);
        ShowSelection(MapCanvas.Selected);
        StatusText.Text = (drawn.Stand is null) ? GateAssignContext.NoFitText : ReasonLine(ranking, drawn.Stand);
    }

    private void ClearDraw(GateAssignContext ranking, string status)
    {
        ListRanking(ranking, _ranked);
        MapCanvas.Select(null);
        ShowSelection(null);
        StatusText.Text = status;
    }

    private void ListRanking(GateAssignContext ranking, IReadOnlyList<GateCandidate> ranked)
    {
        _listed = ranked;
        RankList.ItemsSource = ranked.Select(c => $"{c.Stand}: {ranking.ReasonText(c)}").ToList();
    }

    // The status line for a chosen stand: its reason in the ranking the list shows (the whole airport's for a stand outside
    // the listed area), and for one that does not fit or is taken, that it is assigned anyway.
    private string ReasonLine(GateAssignContext ranking, string stand)
    {
        string name = stand.Trim();
        if ((Find(_listed, name) ?? Find(_ranked, name)) is not { } candidate)
        {
            return name;
        }

        string reason = ranking.ReasonText(candidate);
        return candidate.IsFit ? $"{candidate.Stand}: {reason}" : $"{candidate.Stand}: {reason}; assigned anyway";
    }

    private static GateCandidate? Find(IReadOnlyList<GateCandidate> ranked, string name) =>
        ranked.FirstOrDefault(c => string.Equals(c.Stand, name, StringComparison.OrdinalIgnoreCase));

    private void ShowSelection(string? stand)
    {
        SelectionText.Text = (stand is null) ? "" : $"Parking: {stand}";
        OkButton.IsEnabled = stand is not null;
    }

    private void OnStandSelected(object? sender, string stand)
    {
        ShowSelection(stand);
        if ((_ranking is { } ranking) && (_layout is not null))
        {
            StatusText.Text = ReasonLine(ranking, stand);
        }
    }

    private void OnStandActivated(object? sender, string stand) => DialogPresenter.Close(this, stand);

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        if (MapCanvas.Selected is { } stand)
        {
            DialogPresenter.Close(this, stand);
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => DialogPresenter.Close(this, null);

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DialogPresenter.Close(this, null);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closing.Cancel();
        _closing.Dispose();
        MapCanvas.SelectionChanged -= OnStandSelected;
        MapCanvas.StandActivated -= OnStandActivated;
        RandomMode.IsCheckedChanged -= OnModeChanged;
        AreaMode.IsCheckedChanged -= OnModeChanged;
        MapMode.IsCheckedChanged -= OnModeChanged;
        AreaBox.SelectionChanged -= OnAreaChanged;
        MapCanvas.Dispose();
    }

    // What the load hands the UI thread: the layout, its scene (coloured when ranked) and the ranking of every stand.
    private sealed record PickerLoad(AirportGroundLayout Layout, ParkingPickerScene Scene, IReadOnlyList<GateCandidate> Ranked)
    {
        internal static PickerLoad Of(AirportGroundLayout layout, string? currentParking, GateAssignContext? ranking)
        {
            var scene = ParkingPickerScene.Build(layout, currentParking);
            if (ranking is null)
            {
                return new PickerLoad(layout, scene, []);
            }

            IReadOnlyList<GateCandidate> ranked = ranking.Rank(layout, GateScope.Any);
            return new PickerLoad(layout, scene.WithRanking(ranked), ranked);
        }
    }
}
