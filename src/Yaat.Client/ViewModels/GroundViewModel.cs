using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using Yaat.Client.Logging;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views.Ground;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Data.Faa;

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

/// <summary>Which interactive route the ground view's draw mode is building.</summary>
public enum DrawRouteKind
{
    /// <summary>A taxi route: consecutive clicks are joined by a pathfinder leg along the ground graph.</summary>
    Taxi,

    /// <summary>A tug move (PUSHM): each click is one target, joined by a free-space straight leg.</summary>
    Push,
}

/// <summary>
/// How the ground view draws one point of a push route being drawn, in the order of
/// <see cref="GroundViewModel.DrawWaypoints"/> (index 0 is the aircraft's own node).
/// </summary>
/// <param name="Position">Where the marker sits: the node's position, or the marked point's.</param>
/// <param name="FacingTrueDeg">A marked point's facing, degrees true, or null when it has none (and for every node).</param>
/// <param name="ForcedKind">The tug motion the leg ending here is forced to, or null when the planner chooses.</param>
public sealed record PushWaypointMark(LatLon Position, double? FacingTrueDeg, PushbackLegKind? ForcedKind);

/// <summary>What a right-click lands on while a push route is being drawn.</summary>
public enum PushRightClickTarget
{
    /// <summary>No target marker (or only the start's): the clicked node, if any, becomes the last target.</summary>
    NewPoint,

    /// <summary>The last target's marker: its leg's push/pull menu, opening with the item that sends the route.</summary>
    LastWaypoint,

    /// <summary>An earlier target's marker: the menu that forces its leg to a push or a pull.</summary>
    EarlierWaypoint,
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

    /// <summary>
    /// Suffix appended to the active scenario id when this view reads or writes its per-scenario settings,
    /// so an extra Ground View window ("#2") keeps its own center, zoom, rotation and label filters. Empty
    /// for the docked primary view, whose key stays the bare scenario id.
    /// </summary>
    public string SettingsKeySuffix { get; init; } = "";

    /// <summary>
    /// False for an extra Ground View window. Every global (non-per-scenario) preference write is gated on
    /// this so an extra window's toggles never rewrite the primary view's app-wide defaults; an extra
    /// window also mirrors the primary's layout instead of loading its own.
    /// </summary>
    public bool IsPrimary { get; init; } = true;

    /// <summary>Per-scenario settings key for this view: the active scenario id plus its instance suffix.</summary>
    private string? SettingsKey => _activeScenarioId is null ? null : _activeScenarioId + SettingsKeySuffix;

    [ObservableProperty]
    private GroundLayoutDto? _layout;

    [ObservableProperty]
    private AircraftModel? _selectedAircraft;

    [ObservableProperty]
    private WeatherDisplayInfo? _weatherInfo;

    /// <summary>Shown in place of the weather readout when the loaded weather has no METAR for this view's airport.</summary>
    [ObservableProperty]
    private string? _weatherNote;

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

    /// <summary>
    /// The plan <see cref="TugMovePlanner"/> builds for the tug move being drawn, or null when no push route
    /// is being drawn or the current one is refused. Drawn by the ground renderer as the sampled path of each
    /// move, coloured by whether the tug pushes or pulls; the path carries its own start, so the preview on
    /// screen is exactly what was planned even once the aircraft has moved on.
    /// </summary>
    [ObservableProperty]
    private TugPlan? _pushRoutePreview;

    /// <summary>
    /// Why the tug move being drawn cannot be planned, or null when it can. The waypoint stays on the list
    /// so the controller sees which leg is illegal and why instead of a silently missing line.
    /// </summary>
    [ObservableProperty]
    private string? _pushRouteRefusal;

    /// <summary>
    /// One marker per point of the push route being drawn, aligned with <see cref="DrawWaypoints"/>: where it sits
    /// (marked points are not graph nodes), its facing, and its forced tug motion. Null outside push-draw mode.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<PushWaypointMark>? _pushWaypointMarks;

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
    /// When on, the airport's published Arrival/Departure Window marks are drawn. Airports without an
    /// authored <c>adw</c> sidecar section draw nothing, so leaving this on costs nothing elsewhere.
    /// </summary>
    [ObservableProperty]
    private bool _showAdwMarkings = true;

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

    /// <summary>
    /// Per-callsign datablock view state (manual offsets, highlights, hide/show choices). Owned here
    /// rather than by the canvas so it survives tab switches and pop-out/dock-back; every
    /// <see cref="Views.Ground.GroundCanvas"/> bound to this view-model shares it.
    /// </summary>
    public GroundDataBlockViewState DataBlockState { get; } = new();

    private readonly HashSet<string> _shownTaxiRouteCallsigns = [];
    private readonly HashSet<string> _taxiRouteHiddenCallsigns = [];
    private readonly Dictionary<string, int> _taxiColorIndices = [];
    private string? _hoveredCallsign;

    [ObservableProperty]
    private IReadOnlyList<ShownTaxiRouteEntry>? _shownTaxiRoutes;

    private Func<string, AircraftModel?>? _findAircraft;
    private Func<IReadOnlyList<AircraftModel>>? _aircraftProvider;

    private string? _activeScenarioId;
    private string? _activeAirportId;
    private bool _isRestoring;
    private GroundViewModel? _mirrorSource;

    private AircraftModel? _drawAircraft;
    private DrawRouteKind _drawKind = DrawRouteKind.Taxi;
    private List<int> _drawWaypointIds = [];

    /// <summary>The push route's marked points, by their index in <see cref="_drawWaypointIds"/>.</summary>
    private readonly Dictionary<int, PushFreePose> _pushFreePoses = [];

    /// <summary>The push route's forced tug motions, by the index in <see cref="_drawWaypointIds"/> of the leg's target.</summary>
    private readonly Dictionary<int, PushbackLegKind> _pushForcedKinds = [];

    private List<TaxiRoute> _drawSubRoutes = [];

    /// <summary>Which route the active draw mode is building; <see cref="DrawRouteKind.Taxi"/> when idle.</summary>
    public DrawRouteKind DrawKind => _drawKind;

    /// <summary>The aircraft a push route is being drawn for, or null when no push route is being drawn.</summary>
    public string? PushRouteCallsign => _drawKind == DrawRouteKind.Push ? _drawAircraft?.Callsign : null;

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
            ShowAdwMarkings = preferences.GroundShowAdwMarkings;
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

    public void SetElevationLookup(Func<string, double?> lookup) => _getAirportElevation = lookup;

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
        if (IsMirroring)
        {
            // An extra Ground View window on the primary's airport mirrors its image and video map
            // (MirrorLayoutFrom) rather than downloading and decoding a second copy of the ~100 MB
            // tower-cab image. One on another airport loads its own.
            return;
        }

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
                TowerCabImage? image = await _towerCabImageService.GetImageAsync(
                    _vnasConfigService.TowerCabImagesBaseUrl,
                    artccId,
                    airportId,
                    highRes: true
                );
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
                string? videoMapId = await GetTowerCabVideoMapIdAsync(artccId, airportId);
                if (videoMapId is not null)
                {
                    TowerCabMapData? mapData = await DownloadAndParseTowerCabMapAsync(_vnasConfigService.VideoMapBaseUrl, artccId, videoMapId);
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

    private async Task<TowerCabMapData?> DownloadAndParseTowerCabMapAsync(string videoMapBaseUrl, string artccId, string videoMapId)
    {
        string cachePath = Path.Combine(YaatPaths.Combine("cache", "towercab-maps", artccId), $"{videoMapId}.geojson");
        string url = $"{videoMapBaseUrl}/{artccId}/{videoMapId}.geojson";

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        string? json = (
            await HttpFileCache.GetOrRefreshAsync(http, url, cachePath, HttpCacheFreshness.HeadLastModified, diskTtl: null, _log)
        ).Content;
        return json is null ? null : TowerCabMapParser.Parse(json);
    }

    public void SaveLayerSettings()
    {
        if (!IsPrimary)
        {
            // App-wide layer defaults: an extra Ground View window's toggles stay local to that window.
            return;
        }

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

        // Per-airport rotation default: an extra Ground View window rotates its own view only.
        if (IsPrimary && !_isRestoring && (_activeAirportId is not null))
        {
            Preferences?.SetGroundRotation(_activeAirportId, value);
        }
    }

    public void SaveLabelAndLockSettings()
    {
        SaveSettings();

        if (!IsPrimary)
        {
            // App-wide label/lock defaults: an extra Ground View window's toggles stay local to that window.
            return;
        }

        Preferences?.SetGroundLabelFilters(ShowRunwayLabels, ShowTaxiwayLabels, ShowHoldShort, ShowParking, ShowSpot, ShowAdwMarkings);
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

        if (IsPrimary)
        {
            // App-wide default: only the primary view writes it, so an extra window's mode stays its own.
            Preferences?.SetGroundDeconflictMode(DeconflictMode);
        }
    }

    /// <summary>
    /// Test-only hook: install a layout DTO directly, bypassing the server fetch.
    /// Mirrors the relevant slice of <see cref="LoadLayoutAsync"/> so tests
    /// exercise the same reconstruction path production code uses.
    /// </summary>
    internal void SetLayoutForTesting(GroundLayoutDto dto)
    {
        DataBlockState.Clear();
        _domainLayout = ReconstructLayout(dto);
        Layout = dto;
        ShownAirportChanged?.Invoke();
    }

    /// <summary>
    /// Test-only hook: install a domain layout directly. Used when a test already holds an
    /// <see cref="AirportGroundLayout"/> (e.g. parsed from a real airport GeoJSON) and does not
    /// need the DTO round-trip. Route resolution reads only <c>_domainLayout</c>.
    /// </summary>
    internal void SetDomainLayoutForTesting(AirportGroundLayout layout)
    {
        _domainLayout = layout;
        ShownAirportChanged?.Invoke();
    }

    public async Task LoadLayoutAsync(string airportId)
    {
        if (IsMirroring)
        {
            // An extra Ground View window on the primary's airport mirrors its layout (MirrorLayoutFrom)
            // rather than fetching and reconstructing its own copy. One on another airport loads its own.
            return;
        }

        try
        {
            GroundLayoutDto? dto = await _connection.GetAirportGroundLayoutAsync(airportId);
            if (dto is null)
            {
                _log.LogWarning("No ground layout for airport {Id}", airportId);
                DataBlockState.Clear();
                _domainLayout = null;
                Layout = null;
                ShownAirportChanged?.Invoke();
                return;
            }

            DataBlockState.Clear();
            // _domainLayout is assigned before Layout so any observer of the Layout change (a mirroring
            // extra window) sees a consistent DTO/domain pair.
            _domainLayout = ReconstructLayout(dto);
            Layout = dto;

            // Compute airport center from node centroid
            if (dto.Nodes.Count > 0)
            {
                double sumLat = 0,
                    sumLon = 0;
                foreach (GroundNodeDto node in dto.Nodes)
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
            double? savedRotation = Preferences?.GetGroundRotation(airportId);
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

    /// <summary>The scenario this view keys its saved center/zoom on; null while no scenario is active.</summary>
    public string? ActiveScenarioId => _activeScenarioId;

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
        _domainLayout = null;
        Layout = null;
        HoverTaxiRoute = null;
        PreviewRoute = null;
        AirportCenterLat = 0;
        AirportCenterLon = 0;
        AirportElevation = 0;
        // Drop the reference but do NOT Dispose the SKImage: Avalonia's compositor renders on
        // a separate thread from a RenderSnapshot that may still hold this image, so freeing the
        // native SkImage here races sk_image_get_width() on the render thread and crashes the
        // process (ExecutionEngineException). The image is reclaimed by finalization once no
        // in-flight snapshot references it — same as the scenario-switch swap in LoadTowerCabLayersAsync.
        BackgroundImage = null;
        TowerCabMap = null;
        GroundAircraft.Clear();
        ClearShownTaxiRoutes();
        DataBlockState.Clear();
        ShownAirportChanged?.Invoke();
    }

    /// <summary>
    /// Shows the airport <paramref name="source"/> has loaded, and keeps showing whatever it loads next.
    /// An extra Ground View window mirrors the primary view this way instead of fetching its own layout and
    /// decoding its own copy of the tower-cab image; view state (center, zoom, rotation, datablocks) stays
    /// per-window. Copies the current state immediately, then tracks changes until <see cref="StopMirroring"/>.
    /// </summary>
    public void MirrorLayoutFrom(GroundViewModel source)
    {
        StopMirroring();
        _mirrorSource = source;
        source.PropertyChanged += OnMirrorSourcePropertyChanged;
        CopyLayoutFrom(source);
    }

    /// <summary>
    /// True while this view follows another view's layout (<see cref="MirrorLayoutFrom"/>). The layout
    /// loaders are no-ops in that state: the mirror supplies the layout, the image and the video map.
    /// </summary>
    public bool IsMirroring => _mirrorSource is not null;

    /// <summary>Stops tracking the mirrored source. The layout copied so far stays on screen.</summary>
    public void StopMirroring()
    {
        if (_mirrorSource is null)
        {
            return;
        }

        _mirrorSource.PropertyChanged -= OnMirrorSourcePropertyChanged;
        _mirrorSource = null;
    }

    private void OnMirrorSourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_mirrorSource is null)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(Layout):
            case nameof(BackgroundImage):
            case nameof(TowerCabMap):
            case nameof(AirportCenterLat):
            case nameof(AirportCenterLon):
            case nameof(AirportElevation):
                CopyLayoutFrom(_mirrorSource);
                break;
        }
    }

    private void CopyLayoutFrom(GroundViewModel source)
    {
        bool layoutChanged = !ReferenceEquals(Layout, source.Layout);

        _domainLayout = source._domainLayout;
        _activeAirportId = source._activeAirportId;
        Layout = source.Layout;
        // Reference copies only. The mirrored SKImage behind BackgroundImage belongs to the source and may
        // be held by a render snapshot on either window, so this view-model must never Dispose it.
        BackgroundImage = source.BackgroundImage;
        TowerCabMap = source.TowerCabMap;
        AirportCenterLat = source.AirportCenterLat;
        AirportCenterLon = source.AirportCenterLon;
        AirportElevation = source.AirportElevation;

        if (layoutChanged)
        {
            // Same bookkeeping a fresh load does: per-callsign datablock state and route overlays belong to
            // the airport that was showing, and the airport just changed.
            DataBlockState.Clear();
            ClearShownTaxiRoutes();
            ShownAirportChanged?.Invoke();
        }
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
        ShowAdwMarkings = saved.ShowAdwMarkings;
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
            ShowAdwMarkings = ShowAdwMarkings,
        };

    private void SaveSettings()
    {
        if (Preferences is null || SettingsKey is not { } key || _isRestoring)
        {
            return;
        }

        HasSavedView = true;
        Preferences.SetGroundSettings(key, CaptureSettings());
    }

    private void RestoreSettings()
    {
        if (Preferences is null || SettingsKey is not { } key)
        {
            return;
        }

        SavedGroundSettings? saved = Preferences.GetGroundSettings(key);
        if (saved is null)
        {
            return;
        }

        ApplySettings(saved);
    }

    public void UpdateAircraftList(IEnumerable<AircraftModel> allAircraft)
    {
        GroundAircraft.Clear();
        foreach (AircraftModel ac in allAircraft)
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
        string readablePath = TaxiRouteFormatter.BuildReadableTaxiPath(route, hasNamedTerminus: spot is not null);
        List<(string Label, string Command, TaxiRoute Preview)> variants = BuildTaxiCrossingVariants(route, spot: spot, pathOverride: readablePath);
        return variants.Count > 0 ? variants[^1].Command : $"TAXI {readablePath}{(spot is not null ? $" {spot.Token}" : "")}";
    }

    public int? FindNearestNodeId(LatLon position)
    {
        if (_domainLayout is null)
        {
            return null;
        }

        GroundNode? node = _domainLayout.FindNearestNode(position);
        return node?.Id;
    }

    public int? GetAircraftNearestNodeId(AircraftModel ac)
    {
        if (_domainLayout is null)
        {
            return null;
        }

        GroundNode? node = _domainLayout.FindNearestNode(ac.Position);
        return node?.Id;
    }

    public GroundNodeDto? GetNode(int nodeId) => Layout?.Nodes.Find(n => n.Id == nodeId);

    public List<string> GetNodeTaxiwayNames(int nodeId)
    {
        if (_domainLayout is null || !_domainLayout.Nodes.TryGetValue(nodeId, out GroundNode? node))
        {
            return [];
        }

        var names = new List<string>();
        foreach (IGroundEdge edge in node.Edges)
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

        int? fromNodeId = GetAircraftNearestNodeId(SelectedAircraft);
        if (fromNodeId is null)
        {
            return;
        }

        TaxiRoute? route = FindRouteToNode(fromNodeId.Value, toNodeId, CategoryFor(SelectedAircraft));
        if (route is null)
        {
            _log.LogWarning("No route from node {From} to {To}", fromNodeId, toNodeId);
            return;
        }

        string taxiways = BuildTaxiCommand(route);
        if (string.IsNullOrEmpty(taxiways))
        {
            return;
        }

        await _sendCommand(callsign, $"TAXI {taxiways}", initials);
    }

    public async Task HoldPositionAsync(string callsign, string initials) => await _sendCommand(callsign, "HP", initials);

    public async Task ResumeAsync(string callsign, string initials) => await _sendCommand(callsign, "RES", initials);

    public async Task PushbackAsync(string callsign, string initials) => await _sendCommand(callsign, "PUSH", initials);

    public async Task CrossRunwayAsync(string callsign, string initials, string runwayId) =>
        await _sendCommand(callsign, $"CROSS {runwayId}", initials);

    public async Task LineUpAndWaitAsync(string callsign, string initials) => await _sendCommand(callsign, "LUAW", initials);

    public async Task ClearedForTakeoffAsync(string callsign, string initials, string? arg)
    {
        string cmd = string.IsNullOrWhiteSpace(arg) ? "CTO" : $"CTO {arg.Trim()}";
        await _sendCommand(callsign, cmd, initials);
    }

    public async Task GoAroundAsync(string callsign, string initials) => await _sendCommand(callsign, "GA", initials);

    public async Task CancelTakeoffClearanceAsync(string callsign, string initials) => await _sendCommand(callsign, "CTOC", initials);

    public async Task ClearedToLandAsync(string callsign, string initials) => await _sendCommand(callsign, "CLAND", initials);

    public async Task ForceLandingAsync(string callsign, string initials) => await _sendCommand(callsign, "CLANDF", initials);

    public async Task CancelLandingClearanceAsync(string callsign, string initials) => await _sendCommand(callsign, "CLC", initials);

    public async Task TouchAndGoAsync(string callsign, string initials) => await _sendCommand(callsign, "TG", initials);

    public async Task StopAndGoAsync(string callsign, string initials) => await _sendCommand(callsign, "SG", initials);

    public async Task LowApproachAsync(string callsign, string initials) => await _sendCommand(callsign, "LA", initials);

    public async Task ClearedForOptionAsync(string callsign, string initials) => await _sendCommand(callsign, "COPT", initials);

    public async Task ExitLeftAsync(string callsign, string initials) => await _sendCommand(callsign, "EL", initials);

    public async Task ExitRightAsync(string callsign, string initials) => await _sendCommand(callsign, "ER", initials);

    /// <summary>Pushes back to an absolute magnetic facing given as an 8-point compass cardinal (N, NE, E, SE, S, SW, W, NW).</summary>
    public async Task PushbackFacingAsync(string callsign, string initials, string cardinal) =>
        await _sendCommand(callsign, $"PUSH FACE {cardinal}", initials);

    public async Task SendRawCommandAsync(string callsign, string initials, string command) => await _sendCommand(callsign, command, initials);

    public async Task HoldShortAsync(string callsign, string initials, string target) => await _sendCommand(callsign, $"HS {target}", initials);

    public async Task DeleteAsync(string callsign, string initials) => await _sendCommand(callsign, "DEL", initials);

    public async Task WarpToNodeAsync(string callsign, string initials, int nodeId) => await _sendCommand(callsign, $"WARPG #{nodeId}", initials);

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
        string taxiways = pathOverride ?? BuildTaxiCommand(route);
        string spotSuffix = spot is not null ? $" {spot.Token}" : "";

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
        foreach (HoldShortPoint hs in route.HoldShortPoints)
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
        (string RwyName, HoldShortPoint Hs) firstHs = crossingHoldShorts[0];
        results.Add(($"HS {firstHs.RwyName}", $"TAXI {taxiways}{spotSuffix} HS {firstHs.RwyName}", route.TruncateAt(firstHs.Hs.NodeId)));

        // Variations 1..N-1: cross some, hold short of next
        for (int i = 0; i < crossingHoldShorts.Count - 1; i++)
        {
            IEnumerable<string> crossParts = crossingHoldShorts.Take(i + 1).Select(c => $"CROSS {c.RwyName}");
            (string RwyName, HoldShortPoint Hs) holdEntry = crossingHoldShorts[i + 1];
            string label = $"CROSS {string.Join(" ", crossingHoldShorts.Take(i + 1).Select(c => c.RwyName))} HS {holdEntry.RwyName}";
            string cmd = $"TAXI {taxiways}{spotSuffix} HS {holdEntry.RwyName}, {string.Join(", ", crossParts)}";
            results.Add((label, cmd, route.TruncateAt(holdEntry.Hs.NodeId)));
        }

        // Variation N: cross all
        IEnumerable<string> allCrossParts = crossingHoldShorts.Select(c => $"CROSS {c.RwyName}");
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
        string taxiways = BuildTaxiCommand(route);
        string spotSuffix = spot is not null ? $" {spot.Token}" : "";

        if (string.IsNullOrEmpty(taxiways))
        {
            return [];
        }

        // Find the destination hold-short node (last segment's ToNodeId is the dest)
        int destHsNodeId = route.Segments.Count > 0 ? route.Segments[^1].ToNodeId : -1;

        var crossingHoldShorts = new List<(string RwyName, HoldShortPoint Hs)>();
        foreach (HoldShortPoint hs in route.HoldShortPoints)
        {
            if (hs.Reason == HoldShortReason.RunwayCrossing && hs.TargetName is not null)
            {
                string rwyName = RunwayIdentifier.Parse(hs.TargetName).End1;
                if (!string.Equals(rwyName, destRunway, StringComparison.OrdinalIgnoreCase))
                {
                    crossingHoldShorts.Add((rwyName, hs));
                }
            }
        }

        var results = new List<(string Label, string Command, TaxiRoute Preview)?>();

        if (crossingHoldShorts.Count == 0)
        {
            results.Add(($"For Departure {destRunway}", $"TAXI {taxiways}{spotSuffix} {destRunway}", route));
            results.Add(null); // separator
            results.Add(($"Hold short RWY {destRunway}", $"TAXI {taxiways}{spotSuffix} HS {destRunway}", route.TruncateAt(destHsNodeId)));
            return results;
        }

        // --- RWY variants: progressive crossings with runway assignment ---

        // Hold short of first crossing
        (string RwyName, HoldShortPoint Hs) firstHs = crossingHoldShorts[0];
        results.Add(
            (
                $"For Departure {destRunway}, HS {firstHs.RwyName}",
                $"TAXI {taxiways}{spotSuffix} HS {firstHs.RwyName} RWY {destRunway}",
                route.TruncateAt(firstHs.Hs.NodeId)
            )
        );

        // Cross some, hold short of next
        for (int i = 0; i < crossingHoldShorts.Count - 1; i++)
        {
            IEnumerable<string> crossParts = crossingHoldShorts.Take(i + 1).Select(c => $"CROSS {c.RwyName}");
            (string RwyName, HoldShortPoint Hs) holdEntry = crossingHoldShorts[i + 1];
            string label =
                $"For Departure {destRunway}, CROSS {string.Join(" ", crossingHoldShorts.Take(i + 1).Select(c => c.RwyName))}, HS {holdEntry.RwyName}";
            string cmd = $"TAXI {taxiways}{spotSuffix} HS {holdEntry.RwyName} RWY {destRunway}, {string.Join(", ", crossParts)}";
            results.Add((label, cmd, route.TruncateAt(holdEntry.Hs.NodeId)));
        }

        // Cross all, arrive at destination with RWY assignment
        IEnumerable<string> allCross = crossingHoldShorts.Select(c => $"CROSS {c.RwyName}");
        results.Add(
            (
                $"For Departure {destRunway}, CROSS {string.Join(" ", crossingHoldShorts.Select(c => c.RwyName))}",
                $"TAXI {taxiways}{spotSuffix} {destRunway}, {string.Join(", ", allCross)}",
                route
            )
        );

        results.Add(null); // separator

        // --- Non-RWY variants: progressive crossings without runway assignment ---

        // Hold short of first crossing
        results.Add(($"Hold short RWY {firstHs.RwyName}", $"TAXI {taxiways}{spotSuffix} HS {firstHs.RwyName}", route.TruncateAt(firstHs.Hs.NodeId)));

        // Cross some, hold short of next
        for (int i = 0; i < crossingHoldShorts.Count - 1; i++)
        {
            IEnumerable<string> crossParts = crossingHoldShorts.Take(i + 1).Select(c => $"CROSS {c.RwyName}");
            (string RwyName, HoldShortPoint Hs) holdEntry = crossingHoldShorts[i + 1];
            string label = $"CROSS {string.Join(" ", crossingHoldShorts.Take(i + 1).Select(c => c.RwyName))}, HS {holdEntry.RwyName}";
            string cmd = $"TAXI {taxiways}{spotSuffix} HS {holdEntry.RwyName}, {string.Join(", ", crossParts)}";
            results.Add((label, cmd, route.TruncateAt(holdEntry.Hs.NodeId)));
        }

        // Cross all, hold short at destination (no RWY assignment)
        results.Add(
            (
                $"CROSS {string.Join(" ", crossingHoldShorts.Select(c => c.RwyName))}, HS {destRunway}",
                $"TAXI {taxiways}{spotSuffix} HS {destRunway}, {string.Join(", ", allCross)}",
                route.TruncateAt(destHsNodeId)
            )
        );

        return results;
    }

    public string GetTaxiwayDisplayName(TaxiRoute route)
    {
        List<string> names = TaxiRouteFormatter.CleanTaxiwaySequence(route);
        return names.Count > 0 ? $"via {string.Join(" ", names)}" : "direct";
    }

    /// <summary>
    /// Lists the pushback facings available at the aircraft's node, one per non-RAMP edge.
    /// </summary>
    /// <remarks>
    /// <c>PUSH FACE &lt;cardinal&gt;</c> takes an absolute <em>magnetic</em> facing, so each edge's true bearing is
    /// converted true→magnetic first and then snapped to the nearest of the eight compass points (45° buckets).
    /// </remarks>
    public List<(string Label, string Cardinal)> GetPushbackDirections(AircraftModel ac)
    {
        if (_domainLayout is null)
        {
            return [];
        }

        int? nodeId = GetAircraftNearestNodeId(ac);
        if (nodeId is null || !_domainLayout.Nodes.TryGetValue(nodeId.Value, out GroundNode? node))
        {
            return [];
        }

        var directions = new List<(string Label, string Cardinal)>();
        foreach (IGroundEdge edge in node.Edges)
        {
            int otherId = edge.OtherNodeId(nodeId.Value);

            if (!_domainLayout.Nodes.TryGetValue(otherId, out GroundNode? otherNode))
            {
                continue;
            }

            double trueBearing = GeoMath.BearingTo(node.Position, otherNode.Position);
            double magnetic = MagneticDeclination.TrueToMagnetic(trueBearing, node.Position);

            string label = edge.TaxiwayName;
            if (!string.Equals(label, "RAMP", StringComparison.OrdinalIgnoreCase))
            {
                directions.Add(($"face {label}", SnapToCardinal(magnetic)));
            }
        }

        return directions;
    }

    private static readonly string[] Cardinals = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];

    /// <summary>Snaps a magnetic bearing in degrees to the nearest 8-point compass cardinal.</summary>
    private static string SnapToCardinal(double magneticDeg)
    {
        double normalized = ((magneticDeg % 360.0) + 360.0) % 360.0;
        int bucket = (int)Math.Round(normalized / 45.0) % 8;
        return Cardinals[bucket];
    }

    public List<(string DisplayName, string Target)> GetHoldShortTargets(AircraftModel ac)
    {
        if (_domainLayout is null)
        {
            return [];
        }

        // Resolve the actual remaining path from the aircraft's position
        TaxiRoute? route = ResolveRemainingRoute(ac);
        if (route is null || route.Segments.Count == 0)
        {
            return [];
        }

        List<string> routeTaxiways = ParseRouteTaxiways(ac.TaxiRoute);
        var routeSet = new HashSet<string>(routeTaxiways, StringComparer.OrdinalIgnoreCase);

        // Per target: the distinct route taxiways it is crossed ON, in route order. A target the
        // route meets on more than one taxiway gets one LOCATED menu entry per crossing
        // (`X@A`, `X@B`) — a bare `HS X` can only ever bind the first — while a single crossing
        // keeps the bare form. Crossings on the SAME taxiway cannot be told apart by the located
        // syntax either, so they collapse into one entry like before.
        var runwayCrossings = new Dictionary<string, List<string?>>(StringComparer.OrdinalIgnoreCase);
        var taxiwayCrossings = new Dictionary<string, List<string?>>(StringComparer.OrdinalIgnoreCase);

        static void Record(Dictionary<string, List<string?>> map, string target, string? location)
        {
            if (!map.TryGetValue(target, out List<string?>? locations))
            {
                locations = [];
                map[target] = locations;
            }

            if (!locations.Contains(location, StringComparer.OrdinalIgnoreCase))
            {
                locations.Add(location);
            }
        }

        void ScanNode(int nodeId, string? location)
        {
            if (!_domainLayout.Nodes.TryGetValue(nodeId, out GroundNode? node))
            {
                return;
            }

            if (node.Type == GroundNodeType.RunwayHoldShort && node.RunwayId is { } rwyId)
            {
                Record(runwayCrossings, rwyId.End1, location);
                if (!string.Equals(rwyId.End1, rwyId.End2, StringComparison.OrdinalIgnoreCase))
                {
                    Record(runwayCrossings, rwyId.End2, location);
                }
            }

            foreach (IGroundEdge adj in node.Edges)
            {
                string name = adj.TaxiwayName;
                if (routeSet.Contains(name) || adj.IsRunwayCenterline || adj.IsRamp)
                {
                    continue;
                }

                // A junction arc's joined name can lead with the target itself ("X - A") — a
                // location equal to the target is meaningless, record the crossing as unlocated.
                Record(taxiwayCrossings, name, string.Equals(location, name, StringComparison.OrdinalIgnoreCase) ? null : location);
            }
        }

        ScanNode(route.Segments[0].FromNodeId, ArrivingTaxiway(route.Segments[0]));
        foreach (TaxiRouteSegment seg in route.Segments)
        {
            ScanNode(seg.ToNodeId, ArrivingTaxiway(seg));
        }

        var results = new List<(string DisplayName, string Target)>();
        AppendEntries(results, runwayCrossings, target => $"Runway {RunwayIdentifier.ToDisplayDesignator(target)}");
        AppendEntries(results, taxiwayCrossings, target => $"Taxiway {target}");
        return results;
    }

    /// <summary>
    /// One menu entry per target: bare when the route meets it on a single taxiway, located
    /// (<c>target@taxiway</c>, "… at J") once per distinct crossing taxiway otherwise.
    /// </summary>
    private static void AppendEntries(
        List<(string DisplayName, string Target)> results,
        Dictionary<string, List<string?>> crossings,
        Func<string, string> displayName
    )
    {
        foreach ((string? target, List<string?>? locations) in crossings.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            var located = locations.OfType<string>().ToList();
            if (located.Count > 1)
            {
                foreach (string? location in located)
                {
                    results.Add(($"{displayName(target)} at {location}", $"{target}@{location}"));
                }
            }
            else
            {
                results.Add((displayName(target), target));
            }
        }
    }

    /// <summary>
    /// The route taxiway a segment travels ON — the location half of a located hold-short. A fillet
    /// arc carries the joined junction name ("C - J"), so the composite is split and the first
    /// plain taxiway name wins. Null when the segment is pure runway/ramp pavement.
    /// </summary>
    private static string? ArrivingTaxiway(TaxiRouteSegment seg)
    {
        foreach (string name in seg.TaxiwayName.Split(" - ", StringSplitOptions.RemoveEmptyEntries))
        {
            if (!name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase) && !string.Equals(name, "RAMP", StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return null;
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

        int? fromNodeId = GetAircraftNearestNodeId(ac);
        if (fromNodeId is null)
        {
            return null;
        }

        int? bestNodeId = null;
        double bestCostNm = double.MaxValue;

        foreach (GroundNode node in _domainLayout.Nodes.Values)
        {
            if (node.Type != GroundNodeType.RunwayHoldShort)
            {
                continue;
            }

            if (node.RunwayId is not { } hsRwyId || !hsRwyId.Contains(runwayEnd))
            {
                continue;
            }

            TaxiRoute? route = TaxiPathfinder.FindRoute(_domainLayout, fromNodeId.Value, node.Id, CategoryFor(ac));
            if (route is null)
            {
                continue;
            }

            double costNm = 0;
            foreach (TaxiRouteSegment seg in route.Segments)
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

        TaxiRoute? route = ResolveRemainingRoute(ac);
        if (route is null)
        {
            return null;
        }

        if (!HoldShortTarget.TryParse(target, out HoldShortTarget holdShort, out _))
        {
            return null;
        }

        for (int i = 0; i < route.Segments.Count; i++)
        {
            TaxiRouteSegment seg = route.Segments[i];
            int nodeId = seg.ToNodeId;

            if (!_domainLayout.Nodes.TryGetValue(nodeId, out GroundNode? node))
            {
                continue;
            }

            // A located target (C@J) only binds a node on its location taxiway — the same
            // node-incidence rule the server's annotators use, so the hover preview stops at the
            // crossing the command will actually arm.
            if (holdShort.OnTaxiway is { } onTaxiway && !node.Edges.Any(e => e.MatchesTaxiway(onTaxiway)))
            {
                continue;
            }

            bool matches = node.Type == GroundNodeType.RunwayHoldShort && node.RunwayId is { } hsRwyId && hsRwyId.Contains(holdShort.Target);

            if (!matches)
            {
                foreach (IGroundEdge edge in node.Edges)
                {
                    if (edge.MatchesTaxiway(holdShort.Target))
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

    internal TaxiRoute? ResolveRemainingRoute(AircraftModel ac)
    {
        if (_domainLayout is null)
        {
            return null;
        }

        List<string> routeTaxiways = ParseRouteTaxiways(ac.TaxiRoute);
        if (routeTaxiways.Count == 0)
        {
            return null;
        }

        int? nodeId = GetAircraftNearestNodeId(ac);
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

        // Source the destination runway from the aircraft's assigned runway so the reconstruction
        // truncates at the runway hold-short instead of walking the last taxiway to its full extent.
        // The DTO taxiway string omits the held-short runway; AssignedRunway carries it, and is set
        // by the taxi clearance only when the route ends at a runway (empty for taxi-to-parking).
        // A parking / spot destination arrives separately so the reconstruction ends at the stand.
        GroundNode? destination = FindTaxiDestinationNode(_domainLayout, ac.TaxiDestination);
        var options = new ExplicitPathOptions
        {
            OccupiedTaxiway = null,
            DestinationRunway = string.IsNullOrEmpty(ac.AssignedRunway) ? null : ac.AssignedRunway,
            DestinationHintNode = destination,
        };
        AircraftCategory category = CategoryFor(ac);
        TaxiRoute? route = TaxiPathfinder.ResolveExplicitPathDetailed(
            _domainLayout,
            nodeId.Value,
            routeTaxiways,
            out PathfindingFailure? failure,
            options,
            category
        );
        // The server replaces a route that reaches the stand only the long way round (SFO $5A: down T5, out to Alpha,
        // back up T5A) with a drive across the apron that rolls in on the stand heading, so the overlay has to make the
        // same substitution or it draws a detour the aircraft is not flying.
        double aircraftLengthFt = FaaAircraftDatabase.Get(ac.AircraftType)?.LengthFt ?? HoldShortAnnotator.CwtFallbackLengthFt(ac.AircraftType);
        RampLaneDestinationCutPlan? improved =
            (route is not null) && (destination is not null)
                ? RampLaneReposition.TryPlanResolvedRouteCut(_domainLayout, route, destination, aircraftLengthFt)
                : null;

        // The server lines a spot cleared from the ramp up to leave it before anything else looks at the route's
        // shape, and a line-up's resolved route typically starts back up the lane behind the aircraft — so it comes
        // ahead of the reversal check, exactly as the server applies it.
        if (
            (route is not null)
            && (destination is not null)
            && (TryClientSpotLineUp(ac, improved?.Route ?? route, destination, ParseRouteTaxiways(ac.TaxiRoute), category) is { } lineUp)
        )
        {
            return lineUp;
        }

        if ((route is not null) && !StartsWithReversal(route, ac.Heading))
        {
            return WithApproachLeg(improved?.Route ?? route, ac.Position, ac.Heading);
        }

        // While the pilot cuts across a ramp onto a parallel lane the map does not connect (SFO M3 → M4),
        // the nearest graph node is still on the old lane and the named route does not resolve from it.
        // Reconstruct the same free-space leg the server planned so the overlay follows the crossing.
        if (failure is not null)
        {
            RampLaneRepositionPlan? plan = RampLaneReposition.TryPlan(
                _domainLayout,
                new RampLaneRepositionRequest
                {
                    Position = ac.Position,
                    Heading = ac.Heading,
                    CurrentTaxiway = ac.CurrentTaxiway,
                    Path = routeTaxiways,
                    Options = options,
                    Category = category,
                },
                failure
            );
            if (plan is not null)
            {
                return plan.Route;
            }
        }

        // The destination-end twin (OAK TE → TC for @22): the cleared lane's ramp end does not join the stand's
        // lane, so the server planned a cut from the lane across the apron. From the aircraft's own end of the
        // lane the graph may still "reach" the stand only by doubling back down the lane — a route no pilot
        // taxis — so a rebuilt route that starts with a reversal is replaced by the cut when one exists.
        if (destination is null)
        {
            return WithApproachLeg(route, ac.Position, ac.Heading);
        }

        RampLaneDestinationCutPlan? cut = RampLaneReposition.TryPlanDestinationCut(
            _domainLayout,
            new RampLaneDestinationCutRequest
            {
                StartNodeId = nodeId.Value,
                Path = routeTaxiways,
                Destination = destination,
                Options = options,
                Category = category,
                AircraftLengthFt = FaaAircraftDatabase.Get(ac.AircraftType)?.LengthFt ?? HoldShortAnnotator.CwtFallbackLengthFt(ac.AircraftType),
            }
        );
        return cut is not null ? cut.Route : WithApproachLeg(route, ac.Position, ac.Heading);
    }

    /// <summary>
    /// The server's spot line-up (<see cref="RampLaneReposition.TryPlanSpotLineUp"/>) rebuilt from what the client
    /// knows: the same start rule (at its stand — the "At Parking" phase — or off the movement area where it stands),
    /// the clearance's taxiways as broadcast, and every other aircraft on the ground the server's world holds. Null
    /// when the destination is not a spot, the aircraft starts on the movement area, or the plan declines.
    /// </summary>
    /// <param name="ac">The aircraft whose route is drawn.</param>
    /// <param name="route">The route rebuilt from its broadcast taxiways.</param>
    /// <param name="destination">Its taxi destination.</param>
    /// <param name="clearedTaxiways">The broadcast taxiway sequence.</param>
    /// <param name="category">Its performance category.</param>
    /// <returns>The line-up route, or null.</returns>
    private TaxiRoute? TryClientSpotLineUp(
        AircraftModel ac,
        TaxiRoute route,
        GroundNode destination,
        IReadOnlyList<string> clearedTaxiways,
        AircraftCategory category
    )
    {
        if (
            (_domainLayout is null)
            || (destination.Type != GroundNodeType.Spot)
            || !RampLaneReposition.StartsOffMovementArea(_domainLayout, ac.Position, ac.CurrentPhase == "At Parking", ac.AircraftType)
        )
        {
            return null;
        }

        IReadOnlyList<AircraftModel> all = _aircraftProvider?.Invoke() ?? [];
        var request = new SpotLineUpRequest
        {
            Callsign = ac.Callsign,
            AircraftType = ac.AircraftType,
            Position = ac.Position,
            Route = route,
            Spot = destination,
            Category = category,
            AircraftLengthFt = FaaAircraftDatabase.Get(ac.AircraftType)?.LengthFt ?? HoldShortAnnotator.CwtFallbackLengthFt(ac.AircraftType),
            ClearedTaxiways = clearedTaxiways,
            OtherGroundAircraft = [.. all.Where(other => !other.IsDelayed && other.IsOnGround).Select(TugCandidateOf)],
        };
        return RampLaneReposition.TryPlanSpotLineUp(_domainLayout, request);
    }

    /// <summary>
    /// The route with the free-space leg from <paramref name="position"/> to the graph node it was resolved
    /// from prepended — the same leg <see cref="TaxiApproachLeg"/> gave the server's route, so the overlay
    /// starts at the aircraft instead of at the ramp node ahead of it (the leg's own guards refuse and hand
    /// the route back unchanged when the aircraft is standing on that node, or when the drive to it is not
    /// one the pilot would make). <see cref="RampLaneReposition"/> plans already start at the aircraft and
    /// never come through here.
    /// </summary>
    private TaxiRoute? WithApproachLeg(TaxiRoute? route, LatLon position, TrueHeading heading) =>
        ((_domainLayout is null) || (route is null)) ? route : TaxiApproachLeg.Prepend(_domainLayout, position, heading, route);

    /// <summary>A rebuilt route whose first leg leaves more than <see cref="ReversalDeg"/> off the aircraft's nose doubles back on itself.</summary>
    private static bool StartsWithReversal(TaxiRoute route, TrueHeading heading) =>
        (route.Segments.Count > 0) && (GeoMath.AbsBearingDifference(route.Segments[0].Edge.DepartureBearing, heading.Degrees) > ReversalDeg);

    private const double ReversalDeg = 135.0;

    /// <summary>The stand a broadcast taxi destination names: <c>@</c> = helipad or parking, <c>$</c> = spot; null when absent or unknown.</summary>
    private static GroundNode? FindTaxiDestinationNode(AirportGroundLayout layout, string taxiDestination)
    {
        if (taxiDestination.Length < 2)
        {
            return null;
        }

        string name = taxiDestination[1..];
        return taxiDestination[0] switch
        {
            '$' => layout.FindSpotNodeByName(name),
            '@' => layout.FindHelipadByName(name) ?? layout.FindParkingByName(name),
            _ => null,
        };
    }

    private static List<string> ParseRouteTaxiways(string taxiRoute)
    {
        if (string.IsNullOrWhiteSpace(taxiRoute))
        {
            return [];
        }

        var result = new List<string>();
        foreach (string part in taxiRoute.Split(' ', StringSplitOptions.RemoveEmptyEntries))
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

    public void SetAircraftLookup(Func<string, AircraftModel?> lookup) => _findAircraft = lookup;

    /// <summary>Supplies the full live aircraft list, needed when "show all taxiing routes" is on.</summary>
    public void SetAircraftProvider(Func<IReadOnlyList<AircraftModel>> provider) => _aircraftProvider = provider;

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

        AircraftModel? ac = _findAircraft?.Invoke(callsign);
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

        foreach (string callsign in forcedShown)
        {
            if (seen.Add(callsign))
            {
                effective.Add(callsign);
            }
        }

        if (showAll)
        {
            foreach ((string? callsign, bool hasActiveTaxiRoute) in allAircraft)
            {
                if (hasActiveTaxiRoute && !forcedHidden.Contains(callsign) && seen.Add(callsign))
                {
                    effective.Add(callsign);
                }
            }
        }

        return effective;
    }

    /// <summary>Number of times <see cref="RefreshShownTaxiRoutes"/> has run. Test-only: lets the
    /// coalescing regression test assert a burst of aircraft updates triggers one rebuild, not N.</summary>
    internal int RefreshShownTaxiRoutesCallCount { get; private set; }

    public void RefreshShownTaxiRoutes()
    {
        RefreshShownTaxiRoutesCallCount++;

        // Nothing can be drawn: no aircraft is forced-shown, "show all" is off and nothing is hovered. The
        // full path below would project the whole aircraft list only to produce an empty overlay, and this
        // runs once per update burst, so short-circuit to the same end state.
        if ((_shownTaxiRouteCallsigns.Count == 0) && !ShowAllTaxiRoutes && (_hoveredCallsign is null))
        {
            _taxiColorIndices.Clear();
            ShownTaxiRoutes = null;
            HoverTaxiRoute = null;
            return;
        }

        IReadOnlyList<AircraftModel> all = _aircraftProvider?.Invoke() ?? [];
        List<string> effective = ComputeVisibleTaxiRouteCallsigns(
            _shownTaxiRouteCallsigns,
            _taxiRouteHiddenCallsigns,
            ShowAllTaxiRoutes,
            [.. all.Select(ac => (ac.Callsign, ac.HasActiveTaxiRoute))]
        );

        AllocateRouteColors(effective);

        var entries = new List<ShownTaxiRouteEntry>();
        foreach (string callsign in effective)
        {
            AircraftModel? ac = _findAircraft?.Invoke(callsign);
            if (ac is null)
            {
                continue;
            }

            TaxiRoute? route = ResolveRemainingRoute(ac);
            if (route is null || route.Segments.Count == 0)
            {
                continue;
            }

            int colorIdx = _taxiColorIndices.GetValueOrDefault(callsign, 0);
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
        foreach (string? cs in stale)
        {
            _taxiColorIndices.Remove(cs);
        }

        var used = new HashSet<int>(_taxiColorIndices.Values);
        foreach (string callsign in effective)
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

        AircraftModel? ac = _findAircraft?.Invoke(_hoveredCallsign);
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
        int? startNode = GetAircraftNearestNodeId(aircraft);
        if (startNode is null)
        {
            return;
        }

        _drawAircraft = aircraft;
        _drawKind = DrawRouteKind.Taxi;
        _drawWaypointIds = [startNode.Value];
        _pushFreePoses.Clear();
        _pushForcedKinds.Clear();
        _drawSubRoutes = [];
        DrawnRoutePreview = null;
        DrawWaypoints = [startNode.Value];
        PushWaypointMarks = null;
        IsDrawingRoute = true;
    }

    public bool AddDrawWaypoint(int nodeId)
    {
        if (_drawKind != DrawRouteKind.Taxi || _drawWaypointIds.Count == 0 || nodeId == _drawWaypointIds[^1])
        {
            return false;
        }

        TaxiRoute? subRoute = FindRouteToNode(_drawWaypointIds[^1], nodeId, _drawAircraft is { } da ? CategoryFor(da) : AircraftCategory.Jet);
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
        if (_drawKind == DrawRouteKind.Push)
        {
            UndoPushWaypoint();
            return;
        }

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

        TaxiRoute merged = MergeSubRoutes();

        // Commit every node along the previewed route, not just the clicked waypoints. Each
        // consecutive pair is one edge apart, so the server pins every leg to that single edge
        // and reproduces exactly what was drawn — no parallel-taxiway substitution. When the
        // route was drawn into a stand, carry the @parking / $spot token so the aircraft parks.
        TaxiSpotDestination? spot = ResolveDrawTerminusSpot();
        string nodeRefPath = BuildDenseNodeRefPath(merged);
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
        foreach (TaxiRouteSegment seg in merged.Segments)
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

        if (!_domainLayout.Nodes.TryGetValue(_drawWaypointIds[^1], out GroundNode? node) || node.Name is null)
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
        // A tug leg is free space, not a graph route, so there is nothing to path-find a hover preview for.
        if (_drawKind == DrawRouteKind.Push)
        {
            DrawHoverPreview = null;
            return;
        }

        if (!IsDrawingRoute || _drawWaypointIds.Count == 0 || nodeId is null || nodeId == _drawWaypointIds[^1])
        {
            DrawHoverPreview = null;
            return;
        }

        DrawHoverPreview = FindRouteToNode(_drawWaypointIds[^1], nodeId.Value, _drawAircraft is { } da ? CategoryFor(da) : AircraftCategory.Jet);
    }

    public void CancelDrawRoute() => ClearDrawState();

    private void ClearDrawState()
    {
        _drawAircraft = null;
        _drawKind = DrawRouteKind.Taxi;
        _drawWaypointIds = [];
        _pushFreePoses.Clear();
        _pushForcedKinds.Clear();
        _drawSubRoutes = [];
        IsDrawingRoute = false;
        DrawnRoutePreview = null;
        DrawHoverPreview = null;
        DrawWaypoints = null;
        PushWaypointMarks = null;
        PushRoutePreview = null;
        PushRouteRefusal = null;
    }

    private TaxiRoute MergeSubRoutes()
    {
        var segments = new List<TaxiRouteSegment>();
        var holdShorts = new List<HoldShortPoint>();
        var seenHoldShortNodes = new HashSet<int>();

        foreach (TaxiRoute sub in _drawSubRoutes)
        {
            segments.AddRange(sub.Segments);
            foreach (HoldShortPoint hs in sub.HoldShortPoints)
            {
                if (seenHoldShortNodes.Add(hs.NodeId))
                {
                    holdShorts.Add(hs);
                }
            }
        }

        return new TaxiRoute { Segments = segments, HoldShortPoints = holdShorts };
    }

    // --- Push route mode (PUSH / PUSHM) ---

    /// <summary>
    /// Enters draw mode to build a tug move for <paramref name="aircraft"/>, anchored at its nearest ground
    /// node. Every later click is one push target — a single one sends as plain <c>PUSH</c>, two or more as
    /// <c>PUSHM</c> — and unlike a taxi route the points are never graph-routed, because a tug move is free
    /// space (see docs/ground/pushback.md).
    /// </summary>
    public void StartPushRoute(AircraftModel aircraft)
    {
        int? startNode = GetAircraftNearestNodeId(aircraft);
        if (startNode is null)
        {
            return;
        }

        _drawAircraft = aircraft;
        _drawKind = DrawRouteKind.Push;
        _drawWaypointIds = [startNode.Value];
        _pushFreePoses.Clear();
        _pushForcedKinds.Clear();
        _drawSubRoutes = [];
        DrawnRoutePreview = null;
        DrawHoverPreview = null;
        PublishPushWaypoints();
        PushRoutePreview = null;
        PushRouteRefusal = null;
        IsDrawingRoute = true;
    }

    /// <summary>Adds one PUSHM target and re-plans the preview. False when the node cannot be a target.</summary>
    public bool AddPushWaypoint(int nodeId)
    {
        if (_drawKind != DrawRouteKind.Push || _drawWaypointIds.Count == 0 || nodeId == _drawWaypointIds[^1])
        {
            return false;
        }

        if (_domainLayout is null || !_domainLayout.Nodes.ContainsKey(nodeId))
        {
            return false;
        }

        _drawWaypointIds.Add(nodeId);
        PublishPushWaypoints();
        RefreshPushRoutePreview();
        return true;
    }

    /// <summary>
    /// Adds a marked point — a free position on the ramp, not snapped to any node — as the next target and re-plans
    /// the preview. The point is kept as the command will carry it (six decimals, a whole-degree facing), so the
    /// preview plans the very point the sim will. False outside push-draw mode, or when it is the last target again.
    /// </summary>
    /// <param name="latitude">Where the point lies, decimal degrees.</param>
    /// <param name="longitude">Where the point lies, decimal degrees.</param>
    /// <param name="facing">The facing to end on, magnetic, or null to leave it to the planner.</param>
    /// <returns>True when the point was added.</returns>
    public bool AddPushFreePoint(double latitude, double longitude, MagneticHeading? facing)
    {
        if (_drawKind != DrawRouteKind.Push || _drawWaypointIds.Count == 0)
        {
            return false;
        }

        var pose = new PushFreePose(
            Math.Round(latitude, 6),
            Math.Round(longitude, 6),
            facing is { } named ? new MagneticHeading(named.ToDisplayInt()) : null
        );
        int id = VirtualNode.Create(pose.Latitude, pose.Longitude).Id;
        if (id == _drawWaypointIds[^1])
        {
            return false;
        }

        _drawWaypointIds.Add(id);
        _pushFreePoses[_drawWaypointIds.Count - 1] = pose;
        PublishPushWaypoints();
        RefreshPushRoutePreview();
        return true;
    }

    /// <summary>
    /// The facing a Shift+drag gives a marked point: the true bearing from the point to where the drag was released,
    /// converted to magnetic at the point and rounded to the whole degree the command carries.
    /// </summary>
    /// <param name="point">The marked point, where the drag started.</param>
    /// <param name="toward">Where the drag was released.</param>
    /// <returns>The magnetic facing.</returns>
    public static MagneticHeading FreePointFacing(LatLon point, LatLon toward)
    {
        double trueDeg = GeoMath.BearingTo(point, toward);
        return new MagneticHeading(Math.Round(MagneticDeclination.TrueToMagnetic(trueDeg, point)));
    }

    /// <summary>The tug motion the leg ending at a push-route point is forced to, or null when the planner chooses.</summary>
    /// <param name="waypointIndex">The point's index in <see cref="DrawWaypoints"/>.</param>
    /// <returns>The forced kind, or null.</returns>
    public PushbackLegKind? PushTargetForcedKind(int waypointIndex) =>
        _pushForcedKinds.TryGetValue(waypointIndex, out PushbackLegKind kind) ? kind : null;

    /// <summary>
    /// Forces the leg ending at a push-route point to a push or a pull (its target then carries <c>/PUSH</c> or
    /// <c>/PULL</c>), or with null leaves it to the planner again, and re-plans the preview. Ignored for the start
    /// (index 0) and for an index past the last point.
    /// </summary>
    /// <param name="waypointIndex">The point's index in <see cref="DrawWaypoints"/>.</param>
    /// <param name="kind">The forced kind, or null.</param>
    public void SetPushTargetForcedKind(int waypointIndex, PushbackLegKind? kind)
    {
        if (_drawKind != DrawRouteKind.Push || waypointIndex < 1 || waypointIndex >= _drawWaypointIds.Count)
        {
            return;
        }

        if (kind is { } forced)
        {
            _pushForcedKinds[waypointIndex] = forced;
        }
        else
        {
            _pushForcedKinds.Remove(waypointIndex);
        }

        PublishPushWaypoints();
        RefreshPushRoutePreview();
    }

    /// <summary>
    /// What a right-click on the push route lands on. Markers are drawn in order, so the topmost of those under the
    /// pointer is the highest index; the start's marker (index 0) is not a target and counts as no marker.
    /// </summary>
    /// <param name="markerHits">The indices of every point marker under the pointer.</param>
    /// <returns>The target, and the point's index when it is a marker.</returns>
    public (PushRightClickTarget Target, int? WaypointIndex) ClassifyPushRightClick(IReadOnlyList<int> markerHits)
    {
        int last = _drawWaypointIds.Count - 1;
        if (_drawKind != DrawRouteKind.Push)
        {
            return (PushRightClickTarget.NewPoint, null);
        }

        int? top = markerHits.Where(i => (i >= 1) && (i <= last)).Select(i => (int?)i).Max();
        return top switch
        {
            null => (PushRightClickTarget.NewPoint, null),
            { } index when index == last => (PushRightClickTarget.LastWaypoint, index),
            { } index => (PushRightClickTarget.EarlierWaypoint, index),
        };
    }

    /// <summary>Drops the last PUSHM target, with its marked point and forced kind, and re-plans the preview.</summary>
    public void UndoPushWaypoint()
    {
        if (_drawKind != DrawRouteKind.Push || _drawWaypointIds.Count <= 1)
        {
            return;
        }

        int last = _drawWaypointIds.Count - 1;
        _drawWaypointIds.RemoveAt(last);
        _pushFreePoses.Remove(last);
        _pushForcedKinds.Remove(last);
        PublishPushWaypoints();
        RefreshPushRoutePreview();
    }

    /// <summary>Publishes the push route's points to the canvas: their ids and the markers drawn for them.</summary>
    private void PublishPushWaypoints()
    {
        DrawWaypoints = [.. _drawWaypointIds];
        PushWaypointMarks = BuildPushWaypointMarks();
    }

    /// <summary>
    /// One marker per push-route point, aligned with the waypoint list. A marked point's facing is converted to true at
    /// the aircraft's position, as the sim converts it. Null when a node is missing from the layout, since a marker
    /// list with a gap would misalign every index after it.
    /// </summary>
    private List<PushWaypointMark>? BuildPushWaypointMarks()
    {
        var marks = new List<PushWaypointMark>(_drawWaypointIds.Count);
        for (int i = 0; i < _drawWaypointIds.Count; i++)
        {
            PushbackLegKind? forced = PushTargetForcedKind(i);
            if (_pushFreePoses.TryGetValue(i, out PushFreePose? pose))
            {
                double? facingTrueDeg =
                    (pose.Facing is { } facing) && (_drawAircraft is { } aircraft)
                        ? MagneticDeclination.MagneticToTrue(facing.Degrees, aircraft.Position)
                        : null;
                marks.Add(new PushWaypointMark(new LatLon(pose.Latitude, pose.Longitude), facingTrueDeg, forced));
            }
            else if ((_domainLayout is not null) && _domainLayout.Nodes.TryGetValue(_drawWaypointIds[i], out GroundNode? node))
            {
                marks.Add(new PushWaypointMark(node.Position, null, forced));
            }
            else
            {
                return null;
            }
        }

        return marks;
    }

    /// <summary>
    /// Ends push-draw mode and returns the command for the targets clicked — plain <c>PUSH</c> for one,
    /// <c>PUSHM</c> for two or more — or null when there is nothing to send. A refused move returns null and
    /// <em>keeps</em> draw mode, so the controller can undo the illegal leg (or press Esc) while the refusal is
    /// still on screen.
    /// </summary>
    public string? FinishPushRoute()
    {
        if (_drawKind != DrawRouteKind.Push)
        {
            return null;
        }

        List<PushDestination> targets = CurrentPushTargets();
        if (targets.Count == 0)
        {
            CancelDrawRoute();
            return null;
        }

        // Sendability is its own gate, not a reading of the banner. A refused move shows its refusal but is
        // still not a command — without this, committing it would send the very plan the sim just refused.
        if (PushRouteRefusal is not null)
        {
            return null;
        }

        // One clicked point is plain `PUSH`, which takes a single $spot, @gate, #node or ~point destination (`PUSHM`
        // needs two or more). The command carries exactly the drawn targets, each with its forced kind: the sim plans
        // the moves from them, and the sigil is the only thing telling a spot apart from a gate of the same name.
        string command =
            targets.Count == 1 ? $"PUSH {targets[0].CanonicalToken}" : $"PUSHM {string.Join(" ", targets.Select(t => t.CanonicalToken))}";
        ClearDrawState();
        return command;
    }

    /// <summary>
    /// Re-plans the drawn tug move through <see cref="TugMovePlanner"/> — the same body the simulation runs,
    /// on goals resolved from the very tokens the <c>PUSH</c>/<c>PUSHM</c> will carry, so the preview and the
    /// executed move cannot disagree about which moves push, which pull, or where the aircraft ends up. One
    /// target is a complete move in itself (that is the plan <c>PUSH $spot</c> runs), so it is previewed the
    /// same way; only a route with no target clicked yet — one the controller has just started — has nothing
    /// to plan.
    /// </summary>
    private void RefreshPushRoutePreview()
    {
        List<PushDestination> targets = CurrentPushTargets();

        if (_domainLayout is null || _drawAircraft is null || targets.Count == 0)
        {
            PushRoutePreview = null;
            PushRouteRefusal = null;
            return;
        }

        // Each target resolves as the sim's PUSHM resolves its legs: a marked point minted where it lies (facing
        // converted at the aircraft), every other target found in the layout, then the leg's forced kind applied.
        var goals = new List<TugGoal>(targets.Count);
        for (int i = 0; i < targets.Count; i++)
        {
            PushDestination target = targets[i];
            TugGoal? goal = target.FreePose is { } pose
                ? GroundCommandHandler.ResolveMarkedPointGoal(pose, null, _drawAircraft.Position, GroundCommandHandler.MarkedPointLabel(targets, i))
                : GroundCommandHandler.ResolveTugGoal(_domainLayout, target.Token);
            if (goal is null)
            {
                PushRoutePreview = null;
                PushRouteRefusal = $"Cannot find {target.Token}";
                return;
            }

            goals.Add(goal with { ForcedKind = target.ForcedKind });
        }

        // The neighbours are chosen by the same body the simulation plans the executed move with, from the aircraft
        // the server's world holds, so a push the server would refuse for a neighbour shows that refusal here.
        TugNeighbourCandidate subject = TugCandidateOf(_drawAircraft);
        List<TugNeighbourCandidate> others = ServerWorldCandidates();
        var request = new TugRequest
        {
            Start = new TugPose(_drawAircraft.Position, _drawAircraft.Heading.Degrees),
            StartsAtStand = _drawAircraft.CurrentPhase == "At Parking",
            AircraftType = _drawAircraft.AircraftType,
            Goals = goals,
            ParkedNeighbours = TugParkedNeighbours.Build(subject, others),
            FinalFacingTrueDeg = null,
            PreviousKind = null,
        };

        (PushRoutePreview, PushRouteRefusal) = PlanPushPreview(_domainLayout, request, subject, others);
    }

    /// <summary>
    /// Plans the drawn move and, as the simulation does once a plan exists, refuses it when the aircraft already
    /// overlaps a parked or held neighbour where it stands.
    /// </summary>
    /// <returns>The plan and no refusal, or no plan and the refusal the server would answer the command with.</returns>
    private static (TugPlan? Plan, string? Refusal) PlanPushPreview(
        AirportGroundLayout layout,
        TugRequest request,
        TugNeighbourCandidate subject,
        IReadOnlyList<TugNeighbourCandidate> others
    )
    {
        TugPlan? plan = TugMovePlanner.Plan(layout, request, out string refusal);
        if (plan is null)
        {
            return (null, refusal);
        }

        return TugParkedNeighbours.FindStartOverlap(subject, plan, others) is { } overlap ? (null, overlap.Refusal) : (plan, null);
    }

    /// <summary>
    /// Every aircraft this view knows that the server's world holds. A delayed spawn is listed ahead of its spawn time
    /// but is not in the world yet, so the server's tug planning cannot see it and neither may the preview.
    /// </summary>
    private List<TugNeighbourCandidate> ServerWorldCandidates()
    {
        IReadOnlyList<AircraftModel> all = _aircraftProvider?.Invoke() ?? [];
        return [.. all.Where(ac => !ac.IsDelayed).Select(TugCandidateOf)];
    }

    private static TugNeighbourCandidate TugCandidateOf(AircraftModel aircraft) =>
        new()
        {
            Callsign = aircraft.Callsign,
            Position = aircraft.Position,
            TrueHeadingDeg = aircraft.Heading.Degrees,
            AircraftType = aircraft.AircraftType,
            StandName = string.IsNullOrEmpty(aircraft.ParkingSpot) ? null : aircraft.ParkingSpot,
            IsImmobile = aircraft.IsHeld,
            PhaseName = string.IsNullOrEmpty(aircraft.CurrentPhase) ? null : aircraft.CurrentPhase,
            GroundSpeedKts = aircraft.GroundSpeed,
            TargetSpeedKts = aircraft.TargetSpeedKts,
        };

    /// <summary>
    /// The drawn targets as the command names them, in order, each with its forced kind: a marked point, or a clicked
    /// node. Index 0 of the waypoint list is the aircraft's own node and is not a target.
    /// </summary>
    private List<PushDestination> CurrentPushTargets()
    {
        var targets = new List<PushDestination>();
        if (_domainLayout is null)
        {
            return targets;
        }

        for (int i = 1; i < _drawWaypointIds.Count; i++)
        {
            PushbackLegKind? forced = PushTargetForcedKind(i);
            if (_pushFreePoses.TryGetValue(i, out PushFreePose? pose))
            {
                targets.Add(PushDestination.AtFreePose(pose, forced));
            }
            else if (_domainLayout.Nodes.TryGetValue(_drawWaypointIds[i], out GroundNode? node))
            {
                targets.Add(PushTargetFor(node, forced));
            }
        }

        return targets;
    }

    /// <summary>How one clicked node is named in a push command: <c>$spot</c>, <c>@stand</c> or <c>#id</c>.</summary>
    private static PushDestination PushTargetFor(GroundNode node, PushbackLegKind? forced) =>
        node switch
        {
            { Type: GroundNodeType.Spot, Name: { Length: > 0 } spot } => PushDestination.AtSpot(spot, forced),
            { Type: GroundNodeType.Parking or GroundNodeType.Helipad, Name: { Length: > 0 } stand } => PushDestination.AtParking(stand, forced),
            _ => PushDestination.AtNode(node.Id, forced),
        };

    private static AirportGroundLayout ReconstructLayout(GroundLayoutDto dto)
    {
        var layout = new AirportGroundLayout { AirportId = dto.AirportId };

        foreach (GroundNodeDto nodeDto in dto.Nodes)
        {
            GroundNodeType type = Enum.TryParse<GroundNodeType>(nodeDto.Type, out GroundNodeType t) ? t : GroundNodeType.TaxiwayIntersection;

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

        foreach (GroundEdgeDto edgeDto in dto.Edges)
        {
            var intermediates = new List<(double Lat, double Lon)>();
            if (edgeDto.IntermediatePoints is not null)
            {
                foreach (double[] pt in edgeDto.IntermediatePoints)
                {
                    if (pt.Length >= 2)
                    {
                        intermediates.Add((pt[0], pt[1]));
                    }
                }
            }

            if (
                !layout.Nodes.TryGetValue(edgeDto.FromNodeId, out GroundNode? fromNode)
                || !layout.Nodes.TryGetValue(edgeDto.ToNodeId, out GroundNode? toNode)
            )
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
            foreach (GroundArcDto arcDto in dto.Arcs)
            {
                if (
                    !layout.Nodes.TryGetValue(arcDto.FromNodeId, out GroundNode? arcFrom)
                    || !layout.Nodes.TryGetValue(arcDto.ToNodeId, out GroundNode? arcTo)
                )
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
