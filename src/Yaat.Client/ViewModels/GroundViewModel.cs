using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using Yaat.Client.Logging;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views.Ground;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.ViewModels;

/// <summary>Per-aircraft override for how its taxi route is drawn on the ground view.</summary>
public enum TaxiRouteDisplayMode
{
    /// <summary>Track the global "show all taxiing routes" setting (the default, no override).</summary>
    Follow,

    /// <summary>Always draw this aircraft's route, regardless of the global setting.</summary>
    AlwaysShow,

    /// <summary>Never draw this aircraft's route, regardless of the global setting.</summary>
    AlwaysHide,
}

public partial class GroundViewModel : ObservableObject
{
    private readonly ILogger _log = AppLog.CreateLogger<GroundViewModel>();

    private readonly ServerConnection _connection;
    private readonly Func<string, string, string, Task> _sendCommand;
    private readonly Action<AircraftModel?>? _onSelectionChanged;
    private AirportGroundLayout? _domainLayout;

    /// <summary>
    /// Domain-side ground layout for the currently-loaded airport, or null when no layout is
    /// loaded. Exposed so sibling view-models (e.g. <see cref="MainViewModel.BuildSpeechContext"/>)
    /// can read taxiway metadata without duplicating the reconstruction pipeline.
    /// </summary>
    public AirportGroundLayout? DomainLayout => _domainLayout;

    /// <summary>
    /// Raised whenever the airport this ground view is showing changes (layout loaded, switched,
    /// or cleared). <see cref="MainViewModel"/> listens so it can re-publish
    /// <c>GroundShownAirportId</c> to the radar, which surfaces ground-aircraft speech bubbles
    /// only for airports the ground view isn't currently showing.
    /// </summary>
    public event Action? ShownAirportChanged;

    private Func<string, double?>? _getAirportElevation;
    private VnasConfigService? _vnasConfigService;
    private TowerCabImageService? _towerCabImageService;
    private ArtccAirportResolver? _artccResolver;

    public UserPreferences? Preferences { get; }

    [ObservableProperty]
    private GroundLayoutDto? _layout;

    [ObservableProperty]
    private AircraftModel? _selectedAircraft;

    [ObservableProperty]
    private WeatherDisplayInfo? _weatherInfo;

    [ObservableProperty]
    private TaxiRoute? _hoverTaxiRoute;

    [ObservableProperty]
    private TaxiRoute? _previewRoute;

    [ObservableProperty]
    private bool _isDrawingRoute;

    [ObservableProperty]
    private TaxiRoute? _drawnRoutePreview;

    [ObservableProperty]
    private IReadOnlyList<int>? _drawWaypoints;

    [ObservableProperty]
    private TaxiRoute? _drawHoverPreview;

    [ObservableProperty]
    private double _airportCenterLat;

    [ObservableProperty]
    private double _airportCenterLon;

    [ObservableProperty]
    private double _airportElevation;

    [ObservableProperty]
    private bool _showRunwayLabels = true;

    [ObservableProperty]
    private bool _showTaxiwayLabels = true;

    [ObservableProperty]
    private GroundFilterMode _showHoldShort = GroundFilterMode.LabelsAndIcons;

    [ObservableProperty]
    private GroundFilterMode _showParking = GroundFilterMode.LabelsAndIcons;

    [ObservableProperty]
    private GroundFilterMode _showSpot = GroundFilterMode.LabelsAndIcons;

    /// <summary>
    /// When on, hovering an aircraft on the ground view temporarily draws its taxi route.
    /// Persisted globally (<see cref="Services.UserPreferences.GroundShowTaxiRouteOnHover"/>); default on.
    /// </summary>
    [ObservableProperty]
    private bool _showTaxiRouteOnHover = true;

    /// <summary>
    /// When on, every taxiing aircraft's taxi route is drawn by default unless the aircraft has been
    /// individually hidden. Persisted globally (<see cref="Services.UserPreferences.GroundShowAllTaxiRoutes"/>);
    /// default off.
    /// </summary>
    [ObservableProperty]
    private bool _showAllTaxiRoutes;

    /// <summary>
    /// Opt-in datablock deconfliction mode for this ground view. Persisted globally
    /// (<see cref="Services.UserPreferences.GroundDeconflictMode"/>); cycled by the DCNF filter button.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeconflictModeLabel))]
    [NotifyPropertyChangedFor(nameof(IsDeconflictActive))]
    private DatablockDeconflictMode _deconflictMode;

    /// <summary>True when deconfliction is on (either mode) — drives the DCNF button's active styling.</summary>
    public bool IsDeconflictActive => DeconflictMode != DatablockDeconflictMode.Off;

    /// <summary>DCNF button caption reflecting the current mode (S = compass snap, F = free-form).</summary>
    public string DeconflictModeLabel =>
        DeconflictMode switch
        {
            DatablockDeconflictMode.CompassSnap => "DCNF S",
            DatablockDeconflictMode.FreeForm => "DCNF F",
            _ => "DCNF",
        };

    [ObservableProperty]
    private bool _isPanZoomLocked;

    [ObservableProperty]
    private GroundColorScheme _colorScheme = GroundColorScheme.Default;

    [ObservableProperty]
    private TowerCabImage? _backgroundImage;

    [ObservableProperty]
    private TowerCabMapData? _towerCabMap;

    [ObservableProperty]
    private bool _showSatelliteImage;

    [ObservableProperty]
    private int _satelliteImageBrightness = 50;

    [ObservableProperty]
    private bool _showVideoMapOverlay;

    [ObservableProperty]
    private int _videoMapOverlayBrightness = 70;

    [ObservableProperty]
    private bool _showYaatLayout = true;

    [ObservableProperty]
    private int _yaatLayoutBrightness = 100;

    [ObservableProperty]
    private double _viewCenterLat;

    [ObservableProperty]
    private double _viewCenterLon;

    [ObservableProperty]
    private double _viewZoom = 1.0;

    [ObservableProperty]
    private double _viewRotation;

    [ObservableProperty]
    private bool _hasSavedView;

    private static readonly SKColor[] TaxiRouteColors =
    [
        SKColor.Parse("#FF6B6B"),
        SKColor.Parse("#4ECDC4"),
        SKColor.Parse("#FFE66D"),
        SKColor.Parse("#A8E6CF"),
        SKColor.Parse("#FF8B94"),
        SKColor.Parse("#B088F9"),
        SKColor.Parse("#F8B500"),
        SKColor.Parse("#45B7D1"),
    ];

    private readonly HashSet<string> _shownTaxiRouteCallsigns = new();
    private readonly HashSet<string> _taxiRouteHiddenCallsigns = new();
    private readonly Dictionary<string, int> _taxiColorIndices = new();
    private string? _hoveredCallsign;

    [ObservableProperty]
    private IReadOnlyList<ShownTaxiRouteEntry>? _shownTaxiRoutes;

    private Func<string, AircraftModel?>? _findAircraft;
    private Func<IReadOnlyList<AircraftModel>>? _aircraftProvider;

    private string? _activeScenarioId;
    private string? _activeAirportId;
    private bool _isRestoring;

    private AircraftModel? _drawAircraft;
    private List<int> _drawWaypointIds = [];
    private List<TaxiRoute> _drawSubRoutes = [];

    public ObservableCollection<AircraftModel> GroundAircraft { get; } = [];

    public GroundViewModel(
        ServerConnection connection,
        Func<string, string, string, Task> sendCommand,
        Action<AircraftModel?>? onSelectionChanged = null,
        UserPreferences? preferences = null
    )
    {
        _connection = connection;
        _sendCommand = sendCommand;
        _onSelectionChanged = onSelectionChanged;
        Preferences = preferences;

        if (preferences is not null)
        {
            ShowRunwayLabels = preferences.GroundShowRunwayLabels;
            ShowTaxiwayLabels = preferences.GroundShowTaxiwayLabels;
            ShowHoldShort = preferences.GroundShowHoldShort;
            ShowParking = preferences.GroundShowParking;
            ShowSpot = preferences.GroundShowSpot;
            ShowTaxiRouteOnHover = preferences.GroundShowTaxiRouteOnHover;
            ShowAllTaxiRoutes = preferences.GroundShowAllTaxiRoutes;
            IsPanZoomLocked = preferences.GroundPanZoomLocked;
            ColorScheme = preferences.GroundColors;
            ShowSatelliteImage = preferences.GroundShowSatelliteImage;
            SatelliteImageBrightness = preferences.GroundSatelliteImageBrightness;
            ShowVideoMapOverlay = preferences.GroundShowVideoMapOverlay;
            VideoMapOverlayBrightness = preferences.GroundVideoMapOverlayBrightness;
            ShowYaatLayout = preferences.GroundShowYaatLayout;
            YaatLayoutBrightness = preferences.GroundYaatLayoutBrightness;
            DeconflictMode = preferences.GroundDeconflictMode;
        }
    }

    public void SetElevationLookup(Func<string, double?> lookup)
    {
        _getAirportElevation = lookup;
    }

    public void SetTowerCabServices(
        VnasConfigService vnasConfigService,
        TowerCabImageService towerCabImageService,
        ArtccAirportResolver artccResolver
    )
    {
        _vnasConfigService = vnasConfigService;
        _towerCabImageService = towerCabImageService;
        _artccResolver = artccResolver;
    }

    public async Task LoadTowerCabLayersAsync(string artccId, string airportId)
    {
        if (_vnasConfigService is null || _towerCabImageService is null)
        {
            _log.LogDebug("Tower cab services not initialized; skipping layer load");
            return;
        }

        if (!_vnasConfigService.IsInitialized)
        {
            _log.LogDebug("vNAS config not available; skipping tower cab layers");
            return;
        }

        // Load satellite image
        if (!string.IsNullOrEmpty(_vnasConfigService.TowerCabImagesBaseUrl))
        {
            try
            {
                var image = await _towerCabImageService.GetImageAsync(_vnasConfigService.TowerCabImagesBaseUrl, artccId, airportId, highRes: true);
                BackgroundImage = image;
                if (image is not null)
                {
                    _log.LogInformation("Tower cab background image loaded for {AirportId}", airportId);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to load tower cab image for {AirportId}", airportId);
            }
        }

        // Load tower cab video map
        if (!string.IsNullOrEmpty(_vnasConfigService.VideoMapBaseUrl))
        {
            try
            {
                var videoMapId = await GetTowerCabVideoMapIdAsync(artccId, airportId);
                if (videoMapId is not null)
                {
                    var mapData = await DownloadAndParseTowerCabMapAsync(_vnasConfigService.VideoMapBaseUrl, artccId, videoMapId);
                    TowerCabMap = mapData;
                    if (mapData is not null)
                    {
                        _log.LogInformation(
                            "Tower cab video map loaded for {AirportId}: {Polys} polygons, {Lines} lines",
                            airportId,
                            mapData.Polygons.Count,
                            mapData.Lines.Count
                        );
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to load tower cab video map for {AirportId}", airportId);
            }
        }
    }

    private async Task<string?> GetTowerCabVideoMapIdAsync(string artccId, string airportId)
    {
        if (_artccResolver is null)
        {
            return null;
        }

        return await _artccResolver.GetTowerCabVideoMapIdAsync(artccId, airportId);
    }

    private static async Task<TowerCabMapData?> DownloadAndParseTowerCabMapAsync(string videoMapBaseUrl, string artccId, string videoMapId)
    {
        var cacheDir = YaatPaths.Combine("cache", "towercab-maps", artccId);
        Directory.CreateDirectory(cacheDir);
        var cachePath = Path.Combine(cacheDir, $"{videoMapId}.geojson");

        // Conditional download
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var url = $"{videoMapBaseUrl}/{artccId}/{videoMapId}.geojson";

        try
        {
            if (File.Exists(cachePath))
            {
                var fileInfo = new FileInfo(cachePath);
                var request = new HttpRequestMessage(HttpMethod.Head, url);
                request.Headers.IfModifiedSince = fileInfo.LastWriteTimeUtc;
                var headResponse = await http.SendAsync(request);

                if (headResponse.StatusCode == System.Net.HttpStatusCode.NotModified)
                {
                    var cachedJson = await File.ReadAllTextAsync(cachePath);
                    return TowerCabMapParser.Parse(cachedJson);
                }
            }

            var json = await http.GetStringAsync(url);
            await File.WriteAllTextAsync(cachePath, json);
            return TowerCabMapParser.Parse(json);
        }
        catch (Exception)
        {
            // Fall back to cache if available
            if (File.Exists(cachePath))
            {
                var cachedJson = await File.ReadAllTextAsync(cachePath);
                return TowerCabMapParser.Parse(cachedJson);
            }

            throw;
        }
    }

    public void SaveLayerSettings()
    {
        Preferences?.SetGroundLayerSettings(
            ShowSatelliteImage,
            SatelliteImageBrightness,
            ShowVideoMapOverlay,
            VideoMapOverlayBrightness,
            ShowYaatLayout,
            YaatLayoutBrightness
        );
    }

    partial void OnSelectedAircraftChanged(AircraftModel? value)
    {
        _onSelectionChanged?.Invoke(value);

        if (IsDrawingRoute && value != _drawAircraft)
        {
            CancelDrawRoute();
        }
    }

    partial void OnViewCenterLatChanged(double value) => SaveSettings();

    partial void OnViewCenterLonChanged(double value) => SaveSettings();

    partial void OnViewZoomChanged(double value) => SaveSettings();

    partial void OnViewRotationChanged(double value)
    {
        SaveSettings();
        if (!_isRestoring && _activeAirportId is not null)
        {
            Preferences?.SetGroundRotation(_activeAirportId, value);
        }
    }

    public void SaveLabelAndLockSettings()
    {
        SaveSettings();
        Preferences?.SetGroundLabelFilters(ShowRunwayLabels, ShowTaxiwayLabels, ShowHoldShort, ShowParking, ShowSpot);
        Preferences?.SetGroundPanZoomLocked(IsPanZoomLocked);
    }

    /// <summary>Advances the datablock deconfliction mode (Off → Snap → Free-form) and persists it.</summary>
    public void CycleDeconflictMode()
    {
        DeconflictMode = DeconflictMode switch
        {
            DatablockDeconflictMode.Off => DatablockDeconflictMode.CompassSnap,
            DatablockDeconflictMode.CompassSnap => DatablockDeconflictMode.FreeForm,
            _ => DatablockDeconflictMode.Off,
        };
        Preferences?.SetGroundDeconflictMode(DeconflictMode);
    }

    /// <summary>
    /// Test-only hook: install a layout DTO directly, bypassing the server fetch.
    /// Mirrors the relevant slice of <see cref="LoadLayoutAsync"/> so tests
    /// exercise the same reconstruction path production code uses.
    /// </summary>
    internal void SetLayoutForTesting(GroundLayoutDto dto)
    {
        Layout = dto;
        _domainLayout = ReconstructLayout(dto);
        ShownAirportChanged?.Invoke();
    }

    public async Task LoadLayoutAsync(string airportId)
    {
        try
        {
            var dto = await _connection.GetAirportGroundLayoutAsync(airportId);
            if (dto is null)
            {
                _log.LogWarning("No ground layout for airport {Id}", airportId);
                Layout = null;
                _domainLayout = null;
                ShownAirportChanged?.Invoke();
                return;
            }

            Layout = dto;
            _domainLayout = ReconstructLayout(dto);

            // Compute airport center from node centroid
            if (dto.Nodes.Count > 0)
            {
                double sumLat = 0,
                    sumLon = 0;
                foreach (var node in dto.Nodes)
                {
                    sumLat += node.Latitude;
                    sumLon += node.Longitude;
                }

                AirportCenterLat = sumLat / dto.Nodes.Count;
                AirportCenterLon = sumLon / dto.Nodes.Count;
            }

            AirportElevation = _getAirportElevation?.Invoke(airportId) ?? 0;

            _activeAirportId = airportId;
            ShownAirportChanged?.Invoke();
            var savedRotation = Preferences?.GetGroundRotation(airportId);
            if (savedRotation.HasValue)
            {
                _isRestoring = true;
                ViewRotation = savedRotation.Value;
                _isRestoring = false;
            }

            _log.LogInformation("Ground layout loaded for {Id}: {Nodes} nodes, {Edges} edges", airportId, dto.Nodes.Count, dto.Edges.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load ground layout for {Id}", airportId);
        }
    }

    public void SetScenarioId(string? scenarioId)
    {
        _activeScenarioId = scenarioId;
        HasSavedView = false;
        if (scenarioId is not null)
        {
            RestoreSettings();
        }
    }

    /// <summary>
    /// Bootstraps ground state from a scenario activation event. Called from
    /// <see cref="MainViewModel.ApplyScenarioBootstrap"/> for all three paths
    /// that can activate a scenario (loader, other-clients broadcast, join-room).
    /// </summary>
    public void ApplyScenarioBootstrap(ScenarioBootstrap bootstrap, string? artccId)
    {
        SetScenarioId(bootstrap.ScenarioId);

        if (!string.IsNullOrEmpty(bootstrap.PrimaryAirportId))
        {
            _ = LoadLayoutAsync(bootstrap.PrimaryAirportId);

            if (!string.IsNullOrEmpty(artccId))
            {
                _ = LoadTowerCabLayersAsync(artccId, bootstrap.PrimaryAirportId);
            }
        }
    }

    public void ClearLayout()
    {
        _activeScenarioId = null;
        _activeAirportId = null;
        Layout = null;
        _domainLayout = null;
        HoverTaxiRoute = null;
        PreviewRoute = null;
        AirportCenterLat = 0;
        AirportCenterLon = 0;
        AirportElevation = 0;
        BackgroundImage?.Image.Dispose();
        BackgroundImage = null;
        TowerCabMap = null;
        GroundAircraft.Clear();
        ClearShownTaxiRoutes();
        ShownAirportChanged?.Invoke();
    }

    public void ApplyCopiedSettings(SavedGroundSettings merged)
    {
        ApplySettings(merged);
        SaveSettings();
    }

    private void ApplySettings(SavedGroundSettings saved)
    {
        _isRestoring = true;

        ViewCenterLat = saved.CenterLat;
        ViewCenterLon = saved.CenterLon;
        ViewZoom = saved.Zoom;
        ViewRotation = saved.Rotation;
        IsPanZoomLocked = saved.IsPanZoomLocked;
        ShowRunwayLabels = saved.ShowRunwayLabels;
        ShowTaxiwayLabels = saved.ShowTaxiwayLabels;
        ShowHoldShort = saved.ShowHoldShort;
        ShowParking = saved.ShowParking;
        ShowSpot = saved.ShowSpot;
        HasSavedView = true;

        _isRestoring = false;
    }

    public SavedGroundSettings CaptureSettings() =>
        new()
        {
            CenterLat = ViewCenterLat,
            CenterLon = ViewCenterLon,
            Zoom = ViewZoom,
            Rotation = ViewRotation,
            IsPanZoomLocked = IsPanZoomLocked,
            ShowRunwayLabels = ShowRunwayLabels,
            ShowTaxiwayLabels = ShowTaxiwayLabels,
            ShowHoldShort = ShowHoldShort,
            ShowParking = ShowParking,
            ShowSpot = ShowSpot,
        };

    private void SaveSettings()
    {
        if (Preferences is null || _activeScenarioId is null || _isRestoring)
        {
            return;
        }

        HasSavedView = true;
        Preferences.SetGroundSettings(_activeScenarioId, CaptureSettings());
    }

    private void RestoreSettings()
    {
        if (Preferences is null || _activeScenarioId is null)
        {
            return;
        }

        var saved = Preferences.GetGroundSettings(_activeScenarioId);
        if (saved is null)
        {
            return;
        }

        ApplySettings(saved);
    }

    public void UpdateAircraftList(IEnumerable<AircraftModel> allAircraft)
    {
        GroundAircraft.Clear();
        foreach (var ac in allAircraft)
        {
            if (ac.IsOnGround)
            {
                GroundAircraft.Add(ac);
            }
        }
    }

    /// <summary>
    /// Resolve the performance category the sim will use for <paramref name="ac"/>, so route
    /// previews match command execution — <see cref="Yaat.Sim.Commands.GroundCommandHandler"/>
    /// categorizes the same way (<c>AircraftCategorization.Categorize(aircraftType)</c>).
    /// </summary>
    public static AircraftCategory CategoryFor(AircraftModel ac) => AircraftCategorization.Categorize(ac.AircraftType);

    public TaxiRoute? FindRouteToNode(int fromNodeId, int toNodeId, AircraftCategory category)
    {
        if (_domainLayout is null)
        {
            return null;
        }

        return TaxiPathfinder.FindRoute(_domainLayout, fromNodeId, toNodeId, category);
    }

    public string BuildTaxiCommand(TaxiRoute route) => string.Join(" ", TaxiRouteFormatter.CleanTaxiwaySequence(route));

    /// <summary>
    /// Builds the readable TAXI command pasted by the ground draw-route "Copy to command input"
    /// action. Clean taxiway names keep the path constrained to the drawn corridor; a terminal pin
    /// (the spot / parking token when the route ends in a stand, otherwise a trailing node-ref) holds
    /// the aircraft at the drawn endpoint instead of running to the end of the last taxiway; CROSS
    /// clauses authorize any runways the drawn route crosses.
    /// </summary>
    public string BuildDrawRouteCopyCommand(TaxiRoute route, TaxiSpotDestination? spot)
    {
        var readablePath = TaxiRouteFormatter.BuildReadableTaxiPath(route, hasNamedTerminus: spot is not null);
        var variants = BuildTaxiCrossingVariants(route, spot: spot, pathOverride: readablePath);
        return variants.Count > 0 ? variants[^1].Command : $"TAXI {readablePath}{(spot is not null ? $" {spot.Token}" : "")}";
    }

    public int? FindNearestNodeId(LatLon position)
    {
        if (_domainLayout is null)
        {
            return null;
        }

        var node = _domainLayout.FindNearestNode(position);
        return node?.Id;
    }

    public int? GetAircraftNearestNodeId(AircraftModel ac)
    {
        if (_domainLayout is null)
        {
            return null;
        }

        var node = _domainLayout.FindNearestNode(ac.Position);
        return node?.Id;
    }

    public GroundNodeDto? GetNode(int nodeId)
    {
        return Layout?.Nodes.Find(n => n.Id == nodeId);
    }

    public List<string> GetNodeTaxiwayNames(int nodeId)
    {
        if (_domainLayout is null || !_domainLayout.Nodes.TryGetValue(nodeId, out var node))
        {
            return [];
        }

        var names = new List<string>();
        foreach (var edge in node.Edges)
        {
            if (!edge.IsRunwayCenterline && !edge.IsRamp && !names.Contains(edge.TaxiwayName))
            {
                names.Add(edge.TaxiwayName);
            }
        }

        return names;
    }

    // --- Command methods ---

    public async Task TaxiToNodeAsync(string callsign, string initials, int toNodeId)
    {
        if (_domainLayout is null || SelectedAircraft is null)
        {
            return;
        }

        var fromNodeId = GetAircraftNearestNodeId(SelectedAircraft);
        if (fromNodeId is null)
        {
            return;
        }

        var route = FindRouteToNode(fromNodeId.Value, toNodeId, CategoryFor(SelectedAircraft));
        if (route is null)
        {
            _log.LogWarning("No route from node {From} to {To}", fromNodeId, toNodeId);
            return;
        }

        var taxiways = BuildTaxiCommand(route);
        if (string.IsNullOrEmpty(taxiways))
        {
            return;
        }

        await _sendCommand(callsign, $"TAXI {taxiways}", initials);
    }

    public async Task HoldPositionAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "HP", initials);
    }

    public async Task ResumeAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "RES", initials);
    }

    public async Task PushbackAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "PUSH", initials);
    }

    public async Task CrossRunwayAsync(string callsign, string initials, string runwayId)
    {
        await _sendCommand(callsign, $"CROSS {runwayId}", initials);
    }

    public async Task LineUpAndWaitAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "LUAW", initials);
    }

    public async Task ClearedForTakeoffAsync(string callsign, string initials, string? arg)
    {
        var cmd = string.IsNullOrWhiteSpace(arg) ? "CTO" : $"CTO {arg.Trim()}";
        await _sendCommand(callsign, cmd, initials);
    }

    public async Task GoAroundAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "GA", initials);
    }

    public async Task CancelTakeoffClearanceAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "CTOC", initials);
    }

    public async Task ClearedToLandAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "CLAND", initials);
    }

    public async Task ForceLandingAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "CLANDF", initials);
    }

    public async Task CancelLandingClearanceAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "CLC", initials);
    }

    public async Task TouchAndGoAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "TG", initials);
    }

    public async Task StopAndGoAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "SG", initials);
    }

    public async Task LowApproachAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "LA", initials);
    }

    public async Task ClearedForOptionAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "COPT", initials);
    }

    public async Task ExitLeftAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "EL", initials);
    }

    public async Task ExitRightAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "ER", initials);
    }

    public async Task PushbackHeadingAsync(string callsign, string initials, int heading)
    {
        await _sendCommand(callsign, $"PUSH {heading}", initials);
    }

    public async Task SendRawCommandAsync(string callsign, string initials, string command)
    {
        await _sendCommand(callsign, command, initials);
    }

    public async Task HoldShortAsync(string callsign, string initials, string target)
    {
        await _sendCommand(callsign, $"HS {target}", initials);
    }

    public async Task DeleteAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "DEL", initials);
    }

    public async Task WarpToNodeAsync(string callsign, string initials, int nodeId)
    {
        await _sendCommand(callsign, $"WARPG #{nodeId}", initials);
    }

    public List<TaxiRoute> FindRoutesToNode(int fromNodeId, int toNodeId, AircraftCategory category)
    {
        if (_domainLayout is null)
        {
            return [];
        }

        // The pathfinder returns one route per preference (FewestTurns / Shortest / Fastest), deduped — at most 3.
        // It is intentionally per-preference, not a Yen-style k-shortest generator, so requesting 3
        // matches what the router can actually produce (a 4th request always came back empty).
        return TaxiPathfinder.FindRoutes(_domainLayout, fromNodeId, toNodeId, preference: null, maxRoutes: 3, authorizedTaxiways: null, category);
    }

    /// <summary>
    /// Returns all crossing variations for a taxi route.
    /// For N runway crossings, produces N+1 entries:
    /// hold-short at first crossing, cross 1 then HS next, ..., cross all.
    /// Routes with no crossings return a single entry.
    /// Each entry: (displayLabel, command).
    /// </summary>
    public List<(string Label, string Command, TaxiRoute Preview)> BuildTaxiCrossingVariants(
        TaxiRoute route,
        TaxiSpotDestination? spot,
        string? pathOverride
    )
    {
        var taxiways = pathOverride ?? BuildTaxiCommand(route);
        var spotSuffix = spot is not null ? $" {spot.Token}" : "";

        if (string.IsNullOrEmpty(taxiways))
        {
            // No taxiway path but have a spot destination — route via prefixed token only
            if (spot is not null)
            {
                return [("", $"TAXI {spot.Token}", route)];
            }

            return [];
        }

        var crossingHoldShorts = new List<(string RwyName, HoldShortPoint Hs)>();
        foreach (var hs in route.HoldShortPoints)
        {
            if (hs.Reason == HoldShortReason.RunwayCrossing && hs.TargetName is not null)
            {
                crossingHoldShorts.Add((RunwayIdentifier.Parse(hs.TargetName).End1, hs));
            }
        }

        if (crossingHoldShorts.Count == 0)
        {
            return [("", $"TAXI {taxiways}{spotSuffix}", route)];
        }

        var results = new List<(string Label, string Command, TaxiRoute Preview)>();

        // Variation 0: hold short of first crossing
        var firstHs = crossingHoldShorts[0];
        results.Add(($"HS {firstHs.RwyName}", $"TAXI {taxiways} HS {firstHs.RwyName}{spotSuffix}", route.TruncateAt(firstHs.Hs.NodeId)));

        // Variations 1..N-1: cross some, hold short of next
        for (int i = 0; i < crossingHoldShorts.Count - 1; i++)
        {
            var crossParts = crossingHoldShorts.Take(i + 1).Select(c => $"CROSS {c.RwyName}");
            var holdEntry = crossingHoldShorts[i + 1];
            var label = $"CROSS {string.Join(" ", crossingHoldShorts.Take(i + 1).Select(c => c.RwyName))} HS {holdEntry.RwyName}";
            var cmd = $"TAXI {taxiways} HS {holdEntry.RwyName}{spotSuffix}, {string.Join(", ", crossParts)}";
            results.Add((label, cmd, route.TruncateAt(holdEntry.Hs.NodeId)));
        }

        // Variation N: cross all
        var allCrossParts = crossingHoldShorts.Select(c => $"CROSS {c.RwyName}");
        results.Add(
            (
                $"CROSS {string.Join(" ", crossingHoldShorts.Select(c => c.RwyName))}",
                $"TAXI {taxiways}{spotSuffix}, {string.Join(", ", allCrossParts)}",
                route
            )
        );

        return results;
    }

    /// <summary>
    /// Returns all taxi variants for a route ending at a runway hold-short.
    /// Two groups separated by a null entry: RWY variants (with runway assignment),
    /// then non-RWY variants (without). Each group has N+1 entries for N crossings.
    /// Example with crossings [28R, 28L] and dest 30:
    ///   RWY 30 HS 28R  |  RWY 30 CROSS 28R HS 28L  |  RWY 30 CROSS 28R 28L
    ///   (separator)
    ///   HS 28R  |  CROSS 28R HS 28L  |  CROSS 28R 28L HS 30
    /// </summary>
    public List<(string Label, string Command, TaxiRoute Preview)?> BuildTaxiDestVariants(
        TaxiRoute route,
        string destRunway,
        TaxiSpotDestination? spot
    )
    {
        var taxiways = BuildTaxiCommand(route);
        var spotSuffix = spot is not null ? $" {spot.Token}" : "";

        if (string.IsNullOrEmpty(taxiways))
        {
            return [];
        }

        // Find the destination hold-short node (last segment's ToNodeId is the dest)
        var destHsNodeId = route.Segments.Count > 0 ? route.Segments[^1].ToNodeId : -1;

        var crossingHoldShorts = new List<(string RwyName, HoldShortPoint Hs)>();
        foreach (var hs in route.HoldShortPoints)
        {
            if (hs.Reason == HoldShortReason.RunwayCrossing && hs.TargetName is not null)
            {
                var rwyName = RunwayIdentifier.Parse(hs.TargetName).End1;
                if (!string.Equals(rwyName, destRunway, StringComparison.OrdinalIgnoreCase))
                {
                    crossingHoldShorts.Add((rwyName, hs));
                }
            }
        }

        var results = new List<(string Label, string Command, TaxiRoute Preview)?>();

        if (crossingHoldShorts.Count == 0)
        {
            results.Add(($"For Departure {destRunway}", $"TAXI {taxiways} {destRunway}{spotSuffix}", route));
            results.Add(null); // separator
            results.Add(($"Hold short RWY {destRunway}", $"TAXI {taxiways} HS {destRunway}{spotSuffix}", route.TruncateAt(destHsNodeId)));
            return results;
        }

        // --- RWY variants: progressive crossings with runway assignment ---

        // Hold short of first crossing
        var firstHs = crossingHoldShorts[0];
        results.Add(
            (
                $"For Departure {destRunway}, HS {firstHs.RwyName}",
                $"TAXI {taxiways} HS {firstHs.RwyName} RWY {destRunway}{spotSuffix}",
                route.TruncateAt(firstHs.Hs.NodeId)
            )
        );

        // Cross some, hold short of next
        for (int i = 0; i < crossingHoldShorts.Count - 1; i++)
        {
            var crossParts = crossingHoldShorts.Take(i + 1).Select(c => $"CROSS {c.RwyName}");
            var holdEntry = crossingHoldShorts[i + 1];
            var label =
                $"For Departure {destRunway}, CROSS {string.Join(" ", crossingHoldShorts.Take(i + 1).Select(c => c.RwyName))}, HS {holdEntry.RwyName}";
            var cmd = $"TAXI {taxiways} HS {holdEntry.RwyName} RWY {destRunway}{spotSuffix}, {string.Join(", ", crossParts)}";
            results.Add((label, cmd, route.TruncateAt(holdEntry.Hs.NodeId)));
        }

        // Cross all, arrive at destination with RWY assignment
        var allCross = crossingHoldShorts.Select(c => $"CROSS {c.RwyName}");
        results.Add(
            (
                $"For Departure {destRunway}, CROSS {string.Join(" ", crossingHoldShorts.Select(c => c.RwyName))}",
                $"TAXI {taxiways} {destRunway}{spotSuffix}, {string.Join(", ", allCross)}",
                route
            )
        );

        results.Add(null); // separator

        // --- Non-RWY variants: progressive crossings without runway assignment ---

        // Hold short of first crossing
        results.Add(($"Hold short RWY {firstHs.RwyName}", $"TAXI {taxiways} HS {firstHs.RwyName}{spotSuffix}", route.TruncateAt(firstHs.Hs.NodeId)));

        // Cross some, hold short of next
        for (int i = 0; i < crossingHoldShorts.Count - 1; i++)
        {
            var crossParts = crossingHoldShorts.Take(i + 1).Select(c => $"CROSS {c.RwyName}");
            var holdEntry = crossingHoldShorts[i + 1];
            var label = $"CROSS {string.Join(" ", crossingHoldShorts.Take(i + 1).Select(c => c.RwyName))}, HS {holdEntry.RwyName}";
            var cmd = $"TAXI {taxiways} HS {holdEntry.RwyName}{spotSuffix}, {string.Join(", ", crossParts)}";
            results.Add((label, cmd, route.TruncateAt(holdEntry.Hs.NodeId)));
        }

        // Cross all, hold short at destination (no RWY assignment)
        results.Add(
            (
                $"CROSS {string.Join(" ", crossingHoldShorts.Select(c => c.RwyName))}, HS {destRunway}",
                $"TAXI {taxiways} HS {destRunway}{spotSuffix}, {string.Join(", ", allCross)}",
                route.TruncateAt(destHsNodeId)
            )
        );

        return results;
    }

    public string GetTaxiwayDisplayName(TaxiRoute route)
    {
        var names = TaxiRouteFormatter.CleanTaxiwaySequence(route);
        return names.Count > 0 ? $"via {string.Join(" ", names)}" : "direct";
    }

    public List<(string Label, int Heading)> GetPushbackDirections(AircraftModel ac)
    {
        if (_domainLayout is null)
        {
            return [];
        }

        var nodeId = GetAircraftNearestNodeId(ac);
        if (nodeId is null || !_domainLayout.Nodes.TryGetValue(nodeId.Value, out var node))
        {
            return [];
        }

        var directions = new List<(string Label, int Heading)>();
        foreach (var edge in node.Edges)
        {
            int otherId = edge.OtherNodeId(nodeId.Value);

            if (!_domainLayout.Nodes.TryGetValue(otherId, out var otherNode))
            {
                continue;
            }

            var bearing = GeoMath.BearingTo(node.Position, otherNode.Position);
            var heading = (int)Math.Round(bearing);
            if (heading <= 0)
            {
                heading += 360;
            }

            var label = edge.TaxiwayName;
            if (!string.Equals(label, "RAMP", StringComparison.OrdinalIgnoreCase))
            {
                directions.Add(($"face {label}", heading));
            }
        }

        return directions;
    }

    public List<(string DisplayName, string Target)> GetHoldShortTargets(AircraftModel ac)
    {
        if (_domainLayout is null)
        {
            return [];
        }

        // Resolve the actual remaining path from the aircraft's position
        var route = ResolveRemainingRoute(ac);
        if (route is null || route.Segments.Count == 0)
        {
            return [];
        }

        var routeTaxiways = ParseRouteTaxiways(ac.TaxiRoute);
        var routeSet = new HashSet<string>(routeTaxiways, StringComparer.OrdinalIgnoreCase);
        var runways = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var taxiways = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Collect all node IDs along the resolved path
        var routeNodeIds = new HashSet<int> { route.Segments[0].FromNodeId };
        foreach (var seg in route.Segments)
        {
            routeNodeIds.Add(seg.ToNodeId);
        }

        // Scan only nodes on the actual path
        foreach (int nodeId in routeNodeIds)
        {
            if (!_domainLayout.Nodes.TryGetValue(nodeId, out var node))
            {
                continue;
            }

            if (node.Type == GroundNodeType.RunwayHoldShort && node.RunwayId is { } rwyId)
            {
                runways.Add(rwyId.End1);
                if (!string.Equals(rwyId.End1, rwyId.End2, StringComparison.OrdinalIgnoreCase))
                {
                    runways.Add(rwyId.End2);
                }
            }

            foreach (var adj in node.Edges)
            {
                var name = adj.TaxiwayName;
                if (routeSet.Contains(name))
                {
                    continue;
                }

                if (adj.IsRunwayCenterline || adj.IsRamp)
                {
                    continue;
                }

                taxiways.Add(name);
            }
        }

        var results = new List<(string DisplayName, string Target)>();
        foreach (var rwy in runways.Order())
        {
            results.Add(($"Runway {RunwayIdentifier.ToDisplayDesignator(rwy)}", rwy));
        }

        foreach (var tw in taxiways.Order())
        {
            results.Add(($"Taxiway {tw}", tw));
        }

        return results;
    }

    /// <summary>
    /// Finds the lowest-cost <c>RunwayHoldShort</c> node for <paramref name="runwayEnd"/>
    /// (e.g. <c>"28L"</c>) reachable from <paramref name="ac"/>'s current nearest node.
    /// Returns null if no route exists or the runway has no hold-short nodes.
    /// Used by the runway-end click target to pick a representative HS node for the
    /// existing taxi-to-runway submenu — equivalent to what the trainer would have
    /// picked manually if they right-clicked the closest HS on that end.
    /// </summary>
    public int? FindNearestHoldShortNodeForRunwayEnd(AircraftModel ac, string runwayEnd)
    {
        if (_domainLayout is null)
        {
            return null;
        }

        var fromNodeId = GetAircraftNearestNodeId(ac);
        if (fromNodeId is null)
        {
            return null;
        }

        int? bestNodeId = null;
        double bestCostNm = double.MaxValue;

        foreach (var node in _domainLayout.Nodes.Values)
        {
            if (node.Type != GroundNodeType.RunwayHoldShort)
            {
                continue;
            }

            if (node.RunwayId is not { } hsRwyId || !hsRwyId.Contains(runwayEnd))
            {
                continue;
            }

            var route = TaxiPathfinder.FindRoute(_domainLayout, fromNodeId.Value, node.Id, CategoryFor(ac));
            if (route is null)
            {
                continue;
            }

            double costNm = 0;
            foreach (var seg in route.Segments)
            {
                costNm += seg.Edge.DistanceNm;
            }

            if (costNm < bestCostNm)
            {
                bestCostNm = costNm;
                bestNodeId = node.Id;
            }
        }

        return bestNodeId;
    }

    public TaxiRoute? FindHoldShortPreviewRoute(AircraftModel ac, string target)
    {
        if (_domainLayout is null)
        {
            return null;
        }

        var route = ResolveRemainingRoute(ac);
        if (route is null)
        {
            return null;
        }

        for (int i = 0; i < route.Segments.Count; i++)
        {
            var seg = route.Segments[i];
            int nodeId = seg.ToNodeId;

            if (!_domainLayout.Nodes.TryGetValue(nodeId, out var node))
            {
                continue;
            }

            bool matches = node.Type == GroundNodeType.RunwayHoldShort && node.RunwayId is { } hsRwyId && hsRwyId.Contains(target);

            if (!matches)
            {
                foreach (var edge in node.Edges)
                {
                    if (edge.MatchesTaxiway(target))
                    {
                        matches = true;
                        break;
                    }
                }
            }

            if (matches)
            {
                return new TaxiRoute { Segments = route.Segments.GetRange(0, i + 1), HoldShortPoints = [] };
            }
        }

        return null;
    }

    private TaxiRoute? ResolveRemainingRoute(AircraftModel ac)
    {
        if (_domainLayout is null)
        {
            return null;
        }

        var routeTaxiways = ParseRouteTaxiways(ac.TaxiRoute);
        if (routeTaxiways.Count == 0)
        {
            return null;
        }

        var nodeId = GetAircraftNearestNodeId(ac);
        if (nodeId is null)
        {
            return null;
        }

        // Trim to start from the aircraft's current taxiway
        if (!string.IsNullOrEmpty(ac.CurrentTaxiway))
        {
            int startIdx = routeTaxiways.FindIndex(tw => string.Equals(tw, ac.CurrentTaxiway, StringComparison.OrdinalIgnoreCase));
            if (startIdx > 0)
            {
                routeTaxiways = routeTaxiways.GetRange(startIdx, routeTaxiways.Count - startIdx);
            }
        }

        return TaxiPathfinder.ResolveExplicitPath(_domainLayout, nodeId.Value, routeTaxiways, out _, new ExplicitPathOptions(), CategoryFor(ac));
    }

    private static List<string> ParseRouteTaxiways(string taxiRoute)
    {
        if (string.IsNullOrWhiteSpace(taxiRoute))
        {
            return [];
        }

        var result = new List<string>();
        foreach (var part in taxiRoute.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            // Stop at "HS" marker — everything after is hold-short metadata
            if (string.Equals(part, "HS", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            result.Add(part);
        }

        return result;
    }

    // --- Shown taxi routes ---

    public void SetAircraftLookup(Func<string, AircraftModel?> lookup)
    {
        _findAircraft = lookup;
    }

    /// <summary>Supplies the full live aircraft list, needed when "show all taxiing routes" is on.</summary>
    public void SetAircraftProvider(Func<IReadOnlyList<AircraftModel>> provider)
    {
        _aircraftProvider = provider;
    }

    partial void OnShowAllTaxiRoutesChanged(bool value) => RefreshShownTaxiRoutes();

    partial void OnShowTaxiRouteOnHoverChanged(bool value)
    {
        if (!value)
        {
            _hoveredCallsign = null;
            HoverTaxiRoute = null;
        }
    }

    /// <summary>
    /// Whether <paramref name="callsign"/>'s taxi route is currently drawn persistently (ignoring the
    /// transient hover overlay): an explicit "show" wins, an explicit "hide" wins next, otherwise the
    /// route shows when <see cref="ShowAllTaxiRoutes"/> is on and the aircraft has an active route.
    /// </summary>
    public bool IsTaxiRouteVisible(string callsign)
    {
        if (_shownTaxiRouteCallsigns.Contains(callsign))
        {
            return true;
        }

        if (_taxiRouteHiddenCallsigns.Contains(callsign))
        {
            return false;
        }

        if (!ShowAllTaxiRoutes)
        {
            return false;
        }

        var ac = _findAircraft?.Invoke(callsign);
        return ac is not null && ac.HasActiveTaxiRoute;
    }

    /// <summary>The explicit per-aircraft taxi-route override, or <see cref="TaxiRouteDisplayMode.Follow"/> when none is set.</summary>
    public TaxiRouteDisplayMode GetTaxiRouteMode(string callsign)
    {
        if (_shownTaxiRouteCallsigns.Contains(callsign))
        {
            return TaxiRouteDisplayMode.AlwaysShow;
        }

        if (_taxiRouteHiddenCallsigns.Contains(callsign))
        {
            return TaxiRouteDisplayMode.AlwaysHide;
        }

        return TaxiRouteDisplayMode.Follow;
    }

    /// <summary>
    /// Sets the per-aircraft taxi-route override backing the context-menu "Taxi route" submenu.
    /// <see cref="TaxiRouteDisplayMode.Follow"/> clears any override so the route tracks the global
    /// "show all" setting; the other two pin it on or off regardless of that setting.
    /// </summary>
    public void SetTaxiRouteMode(string callsign, TaxiRouteDisplayMode mode)
    {
        switch (mode)
        {
            case TaxiRouteDisplayMode.AlwaysShow:
                _taxiRouteHiddenCallsigns.Remove(callsign);
                _shownTaxiRouteCallsigns.Add(callsign);
                break;
            case TaxiRouteDisplayMode.AlwaysHide:
                _shownTaxiRouteCallsigns.Remove(callsign);
                _taxiRouteHiddenCallsigns.Add(callsign);
                break;
            default:
                _shownTaxiRouteCallsigns.Remove(callsign);
                _taxiRouteHiddenCallsigns.Remove(callsign);
                break;
        }

        RefreshShownTaxiRoutes();
    }

    /// <summary>
    /// The ordered set of callsigns whose taxi route should be drawn persistently: all explicitly-shown
    /// aircraft, plus — when <paramref name="showAll"/> is on — every aircraft with an active taxi route
    /// that hasn't been explicitly hidden. Pure set logic (no geometry) so it can be unit-tested.
    /// </summary>
    public static List<string> ComputeVisibleTaxiRouteCallsigns(
        IReadOnlySet<string> forcedShown,
        IReadOnlySet<string> forcedHidden,
        bool showAll,
        IReadOnlyList<(string Callsign, bool HasActiveTaxiRoute)> allAircraft
    )
    {
        var effective = new List<string>();
        var seen = new HashSet<string>();

        foreach (var callsign in forcedShown)
        {
            if (seen.Add(callsign))
            {
                effective.Add(callsign);
            }
        }

        if (showAll)
        {
            foreach (var (callsign, hasActiveTaxiRoute) in allAircraft)
            {
                if (hasActiveTaxiRoute && !forcedHidden.Contains(callsign) && seen.Add(callsign))
                {
                    effective.Add(callsign);
                }
            }
        }

        return effective;
    }

    public void RefreshShownTaxiRoutes()
    {
        var all = _aircraftProvider?.Invoke() ?? [];
        var effective = ComputeVisibleTaxiRouteCallsigns(
            _shownTaxiRouteCallsigns,
            _taxiRouteHiddenCallsigns,
            ShowAllTaxiRoutes,
            all.Select(ac => (ac.Callsign, ac.HasActiveTaxiRoute)).ToList()
        );

        AllocateRouteColors(effective);

        var entries = new List<ShownTaxiRouteEntry>();
        foreach (var callsign in effective)
        {
            var ac = _findAircraft?.Invoke(callsign);
            if (ac is null)
            {
                continue;
            }

            var route = ResolveRemainingRoute(ac);
            if (route is null || route.Segments.Count == 0)
            {
                continue;
            }

            var colorIdx = _taxiColorIndices.GetValueOrDefault(callsign, 0);
            entries.Add(new ShownTaxiRouteEntry(callsign, route, TaxiRouteColors[colorIdx % TaxiRouteColors.Length]));
        }

        ShownTaxiRoutes = entries.Count > 0 ? entries : null;

        RefreshHoverRoute();
    }

    /// <summary>
    /// Keeps a stable palette index per drawn callsign: existing assignments are preserved, callsigns no
    /// longer drawn are released, and each newcomer gets the lowest free slot (cycling past the palette).
    /// </summary>
    private void AllocateRouteColors(IReadOnlyList<string> effective)
    {
        var effectiveSet = new HashSet<string>(effective);
        var stale = _taxiColorIndices.Keys.Where(cs => !effectiveSet.Contains(cs)).ToList();
        foreach (var cs in stale)
        {
            _taxiColorIndices.Remove(cs);
        }

        var used = new HashSet<int>(_taxiColorIndices.Values);
        foreach (var callsign in effective)
        {
            if (_taxiColorIndices.ContainsKey(callsign))
            {
                continue;
            }

            int idx = 0;
            while (idx < TaxiRouteColors.Length && used.Contains(idx))
            {
                idx++;
            }

            if (idx >= TaxiRouteColors.Length)
            {
                idx = _taxiColorIndices.Count % TaxiRouteColors.Length;
            }

            _taxiColorIndices[callsign] = idx;
            used.Add(idx);
        }
    }

    /// <summary>Sets the aircraft whose route the transient hover overlay should draw (null clears it).</summary>
    public void SetHoveredAircraft(string? callsign)
    {
        _hoveredCallsign = ShowTaxiRouteOnHover ? callsign : null;
        RefreshHoverRoute();
    }

    private void RefreshHoverRoute()
    {
        if (_hoveredCallsign is null)
        {
            HoverTaxiRoute = null;
            return;
        }

        var ac = _findAircraft?.Invoke(_hoveredCallsign);
        HoverTaxiRoute = ac is null ? null : ResolveRemainingRoute(ac);
    }

    public void RemoveShownTaxiRoute(string callsign)
    {
        bool changed = _shownTaxiRouteCallsigns.Remove(callsign) | _taxiRouteHiddenCallsigns.Remove(callsign);

        if (string.Equals(_hoveredCallsign, callsign, StringComparison.Ordinal))
        {
            _hoveredCallsign = null;
            changed = true;
        }

        if (changed || ShowAllTaxiRoutes)
        {
            RefreshShownTaxiRoutes();
        }
    }

    public void ClearShownTaxiRoutes()
    {
        _shownTaxiRouteCallsigns.Clear();
        _taxiRouteHiddenCallsigns.Clear();
        _taxiColorIndices.Clear();
        _hoveredCallsign = null;
        HoverTaxiRoute = null;
        ShownTaxiRoutes = null;
    }

    // --- Draw route mode ---

    public void StartDrawRoute(AircraftModel aircraft)
    {
        var startNode = GetAircraftNearestNodeId(aircraft);
        if (startNode is null)
        {
            return;
        }

        _drawAircraft = aircraft;
        _drawWaypointIds = [startNode.Value];
        _drawSubRoutes = [];
        DrawnRoutePreview = null;
        DrawWaypoints = [startNode.Value];
        IsDrawingRoute = true;
    }

    public bool AddDrawWaypoint(int nodeId)
    {
        if (_drawWaypointIds.Count == 0 || nodeId == _drawWaypointIds[^1])
        {
            return false;
        }

        var subRoute = FindRouteToNode(_drawWaypointIds[^1], nodeId, _drawAircraft is { } da ? CategoryFor(da) : AircraftCategory.Jet);
        if (subRoute is null)
        {
            return false;
        }

        _drawSubRoutes.Add(subRoute);
        _drawWaypointIds.Add(nodeId);
        DrawWaypoints = [.. _drawWaypointIds];
        DrawnRoutePreview = MergeSubRoutes();
        DrawHoverPreview = null;
        return true;
    }

    public void UndoDrawWaypoint()
    {
        if (_drawWaypointIds.Count <= 1)
        {
            return;
        }

        _drawWaypointIds.RemoveAt(_drawWaypointIds.Count - 1);
        _drawSubRoutes.RemoveAt(_drawSubRoutes.Count - 1);
        DrawWaypoints = [.. _drawWaypointIds];
        DrawnRoutePreview = _drawSubRoutes.Count > 0 ? MergeSubRoutes() : null;
    }

    public (TaxiRoute Route, string NodeRefPath, TaxiSpotDestination? Spot)? FinishDrawRoute()
    {
        if (_drawSubRoutes.Count == 0)
        {
            CancelDrawRoute();
            return null;
        }

        var merged = MergeSubRoutes();

        // Commit every node along the previewed route, not just the clicked waypoints. Each
        // consecutive pair is one edge apart, so the server pins every leg to that single edge
        // and reproduces exactly what was drawn — no parallel-taxiway substitution. When the
        // route was drawn into a stand, carry the @parking / $spot token so the aircraft parks.
        var spot = ResolveDrawTerminusSpot();
        var nodeRefPath = BuildDenseNodeRefPath(merged);
        ClearDrawState();

        if (string.IsNullOrEmpty(nodeRefPath))
        {
            return null;
        }

        return (merged, nodeRefPath, spot);
    }

    private static string BuildDenseNodeRefPath(TaxiRoute merged)
    {
        var ids = new List<int>();
        foreach (var seg in merged.Segments)
        {
            if (ids.Count == 0 || ids[^1] != seg.ToNodeId)
            {
                ids.Add(seg.ToNodeId);
            }
        }

        return string.Join(" ", ids.Select(id => $"#{id}"));
    }

    private TaxiSpotDestination? ResolveDrawTerminusSpot()
    {
        if (_domainLayout is null || _drawWaypointIds.Count == 0)
        {
            return null;
        }

        if (!_domainLayout.Nodes.TryGetValue(_drawWaypointIds[^1], out var node) || node.Name is null)
        {
            return null;
        }

        return node.Type switch
        {
            GroundNodeType.Spot => new TaxiSpotDestination(node.Name, IsTaxiSpot: true),
            GroundNodeType.Parking or GroundNodeType.Helipad => new TaxiSpotDestination(node.Name, IsTaxiSpot: false),
            _ => null,
        };
    }

    public void UpdateDrawHoverPreview(int? nodeId)
    {
        if (!IsDrawingRoute || _drawWaypointIds.Count == 0 || nodeId is null || nodeId == _drawWaypointIds[^1])
        {
            DrawHoverPreview = null;
            return;
        }

        DrawHoverPreview = FindRouteToNode(_drawWaypointIds[^1], nodeId.Value, _drawAircraft is { } da ? CategoryFor(da) : AircraftCategory.Jet);
    }

    public void CancelDrawRoute()
    {
        ClearDrawState();
    }

    private void ClearDrawState()
    {
        _drawAircraft = null;
        _drawWaypointIds = [];
        _drawSubRoutes = [];
        IsDrawingRoute = false;
        DrawnRoutePreview = null;
        DrawHoverPreview = null;
        DrawWaypoints = null;
    }

    private TaxiRoute MergeSubRoutes()
    {
        var segments = new List<TaxiRouteSegment>();
        var holdShorts = new List<HoldShortPoint>();
        var seenHoldShortNodes = new HashSet<int>();

        foreach (var sub in _drawSubRoutes)
        {
            segments.AddRange(sub.Segments);
            foreach (var hs in sub.HoldShortPoints)
            {
                if (seenHoldShortNodes.Add(hs.NodeId))
                {
                    holdShorts.Add(hs);
                }
            }
        }

        return new TaxiRoute { Segments = segments, HoldShortPoints = holdShorts };
    }

    private static AirportGroundLayout ReconstructLayout(GroundLayoutDto dto)
    {
        var layout = new AirportGroundLayout { AirportId = dto.AirportId };

        foreach (var nodeDto in dto.Nodes)
        {
            var type = Enum.TryParse<GroundNodeType>(nodeDto.Type, out var t) ? t : GroundNodeType.TaxiwayIntersection;

            var node = new GroundNode
            {
                Id = nodeDto.Id,
                Position = new LatLon(nodeDto.Latitude, nodeDto.Longitude),
                Type = type,
                Name = nodeDto.Name,
                TrueHeading = nodeDto.Heading.HasValue ? new TrueHeading(nodeDto.Heading.Value) : null,
                RunwayId = nodeDto.RunwayId is not null ? RunwayIdentifier.Parse(nodeDto.RunwayId) : null,
            };
            layout.Nodes[node.Id] = node;
        }

        foreach (var edgeDto in dto.Edges)
        {
            var intermediates = new List<(double Lat, double Lon)>();
            if (edgeDto.IntermediatePoints is not null)
            {
                foreach (var pt in edgeDto.IntermediatePoints)
                {
                    if (pt.Length >= 2)
                    {
                        intermediates.Add((pt[0], pt[1]));
                    }
                }
            }

            if (!layout.Nodes.TryGetValue(edgeDto.FromNodeId, out var fromNode) || !layout.Nodes.TryGetValue(edgeDto.ToNodeId, out var toNode))
            {
                continue;
            }

            var edge = new GroundEdge
            {
                Nodes = [fromNode, toNode],
                TaxiwayName = edgeDto.TaxiwayName,
                DistanceNm = edgeDto.DistanceNm,
                IntermediatePoints = intermediates,
            };
            layout.Edges.Add(edge);
        }

        if (dto.Arcs is not null)
        {
            foreach (var arcDto in dto.Arcs)
            {
                if (!layout.Nodes.TryGetValue(arcDto.FromNodeId, out var arcFrom) || !layout.Nodes.TryGetValue(arcDto.ToNodeId, out var arcTo))
                {
                    continue;
                }

                var arc = new GroundArc
                {
                    Nodes = [arcFrom, arcTo],
                    TaxiwayNames = arcDto.TaxiwayNames,
                    P1Lat = arcDto.P1Lat,
                    P1Lon = arcDto.P1Lon,
                    P2Lat = arcDto.P2Lat,
                    P2Lon = arcDto.P2Lon,
                    MinRadiusOfCurvatureFt = arcDto.MinRadiusOfCurvatureFt,
                    DistanceNm = arcDto.DistanceNm,
                };
                layout.Arcs.Add(arc);
            }
        }

        layout.RebuildAdjacencyLists();
        return layout;
    }
}

public record ShownTaxiRouteEntry(string Callsign, TaxiRoute Route, SKColor Color);

/// <summary>
/// Destination spot for a TAXI/PUSH command. <see cref="IsTaxiSpot"/> selects the
/// canonical prefix: `$` for taxi spots (GroundNodeType.Spot), `@` for parking stands
/// and helipads. The two share a name space on the wire but resolve to different
/// node lookups server-side, so the prefix must match the node kind exactly.
/// </summary>
public sealed record TaxiSpotDestination(string Name, bool IsTaxiSpot)
{
    public char Prefix => IsTaxiSpot ? '$' : '@';

    public string Token => $"{Prefix}{Name}";
}
