using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.Views.Authoring;

/// <summary>
/// Picks an aircraft's parking on the airport's vNAS map, with no room: the airport's stands (parking, spot and helipad
/// nodes) over its taxiways and runways, fitted to the stands with the current stand ringed. A click selects a stand and OK
/// returns its name, a double-click returns it at once; Esc or Cancel returns null. The map is fetched and parsed off the
/// UI thread; when there is none (vNAS has no map, or cannot be reached and none is cached) the status line says so.
/// </summary>
public partial class ParkingPickerWindow : Window
{
    /// <summary>The key the window's position and size are kept under.</summary>
    public const string GeometryKey = "ParkingPicker";

    private static readonly ILogger Log = AppLog.CreateLogger<ParkingPickerWindow>();

    // One downloader for every picker the app opens, never disposed: its HttpClient lives as long as the process, so
    // closing a picker cannot dispose it under a fetch still in flight. It shares the vNAS airport cache on disk.
    private static readonly Lazy<AirportLayoutDownloader> SharedDownloader = new(() => new AirportLayoutDownloader());

    private readonly string _airportId;
    private readonly string? _currentParking;
    private readonly Func<string, CancellationToken, Task<AirportGroundLayout?>> _loadLayout;
    private readonly CancellationTokenSource _closing = new();
    private readonly WindowGeometryHelper _geometry;

    // Parameterless ctor for the Avalonia designer / XamlLoader: it loads no map.
    public ParkingPickerWindow()
        : this("OAK", null, new UserPreferences(), (_, _) => Task.FromResult<AirportGroundLayout?>(null)) { }

    /// <summary>Creates the picker over the app's vNAS airport cache.</summary>
    /// <param name="airportId">The airport whose stands are offered.</param>
    /// <param name="currentParking">The aircraft's parking value now, ringed when the map lists it; null for none.</param>
    /// <param name="preferences">Keeps the window's position and size.</param>
    public ParkingPickerWindow(string airportId, string? currentParking, UserPreferences preferences)
        : this(airportId, currentParking, preferences, (id, ct) => SharedDownloader.Value.GetLayoutAsync(id, ct)) { }

    /// <summary>Creates the picker and starts loading the airport's layout.</summary>
    /// <param name="airportId">The airport whose stands are offered.</param>
    /// <param name="currentParking">The aircraft's parking value now, ringed when the map lists it; null for none.</param>
    /// <param name="preferences">Keeps the window's position and size.</param>
    /// <param name="loadLayout">Loads the airport's layout, or null when there is no map; run off the UI thread.</param>
    public ParkingPickerWindow(
        string airportId,
        string? currentParking,
        UserPreferences preferences,
        Func<string, CancellationToken, Task<AirportGroundLayout?>> loadLayout
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
        Title = $"Pick parking — {_airportId}";
        AirportText.Text = _airportId;
        StatusText.Text = $"Loading the vNAS map for {_airportId}…";
        MapCanvas.SelectionChanged += OnStandSelected;
        MapCanvas.StandActivated += OnStandActivated;
        OkButton.Click += OnOkClick;
        CancelButton.Click += OnCancelClick;
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
        ParkingPickerScene? scene;
        try
        {
            scene = await Task.Run(
                async () =>
                {
                    AirportGroundLayout? layout = await load(airportId, ct).ConfigureAwait(false);
                    return (layout is null) ? null : ParkingPickerScene.Build(layout, current);
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
            ShowScene(scene);
        }
    }

    private void ShowScene(ParkingPickerScene? scene)
    {
        if (scene is null)
        {
            StatusText.Text = $"No vNAS map for {_airportId}";
            return;
        }

        MapCanvas.Scene = scene;
        MapCanvas.FitToStands();
        StatusText.Text = scene.Stands.IsEmpty
            ? $"The vNAS map for {_airportId} has no parking"
            : "Click a stand, then OK, or double-click it. Right-drag pans, the wheel zooms.";
        ShowSelection(MapCanvas.Selected);
    }

    private void ShowSelection(string? stand)
    {
        SelectionText.Text = (stand is null) ? "" : $"Parking: {stand}";
        OkButton.IsEnabled = stand is not null;
    }

    private void OnStandSelected(object? sender, string stand) => ShowSelection(stand);

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
        MapCanvas.Dispose();
    }
}
