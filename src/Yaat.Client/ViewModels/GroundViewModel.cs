using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using Yaat.Client.ContextMenus;
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
using Yaat.Sim.Simulation;

namespace Yaat.Client.ViewModels;

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

    /// <summary>The aircraft, shape and target of every turn about already warned of as aimed at a node the layout lacks.</summary>
    private readonly HashSet<(string Callsign, TaxiTurnAboutShape Shape, int? Target)> _warnedTurnAboutTargets = [];

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

    /// <summary>
    /// The room's active runways, read when the Hold short of… rows are built: each airport naming at least one end,
    /// keyed by FAA id, its ends as the server spells them (<c>30</c>, <c>D28L</c>, <c>A28R</c>).
    /// </summary>
    public required Func<IReadOnlyDictionary<string, IReadOnlyList<string>>> RoomActiveRunways { get; init; }

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
                ForgetRunwayEntryRoutes();
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
        ForgetRunwayEntryRoutes();
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

    /// <summary>
    /// Resolve the performance category the sim will use for <paramref name="ac"/>, so route
    /// previews match command execution — <see cref="Yaat.Sim.Commands.GroundCommandHandler"/>
    /// categorizes the same way (<c>AircraftCategorization.Categorize(aircraftType)</c>).
    /// </summary>
    public static AircraftCategory CategoryFor(AircraftModel ac) => AircraftCategorization.Categorize(ac.AircraftType);

    public static WakeTurbulenceData.WakeClass WakeClassFor(AircraftModel ac) =>
        WakeTurbulenceData.WakeClassForType(ac.AircraftType, CategoryFor(ac));

    public TaxiRoute? FindRouteToNode(int fromNodeId, int toNodeId, AircraftCategory category, WakeTurbulenceData.WakeClass wakeClass)
    {
        if (_domainLayout is null)
        {
            return null;
        }

        return TaxiPathfinder.FindRoute(_domainLayout, fromNodeId, toNodeId, category, wakeClass);
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

    /// <summary>
    /// The taxiways meeting at <paramref name="nodeId"/>, each once, in edge order: a straight edge's name, and each name
    /// a fillet arc joins (never the arc's joined <c>"W - W4"</c>), leaving out runway centerlines and the ramp. Empty
    /// when the node is not in the layout.
    /// </summary>
    public List<string> GetNodeTaxiwayNames(int nodeId)
    {
        if (_domainLayout is null || !_domainLayout.Nodes.TryGetValue(nodeId, out GroundNode? node))
        {
            return [];
        }

        var names = new List<string>();
        foreach (IGroundEdge edge in node.Edges.Where(e => !e.IsRunwayCenterline))
        {
            string[] edgeNames = (edge is GroundArc arc) ? arc.TaxiwayNames : [edge.TaxiwayName];
            foreach (string name in edgeNames)
            {
                if (IsTaxiwayName(name) && !names.Contains(name))
                {
                    names.Add(name);
                }
            }
        }

        return names;
    }

    /// <summary>
    /// Whether an edge or arc name is a taxiway's: not the ramp and not a runway centerline (the rule
    /// <see cref="IGroundEdge.IsRunwayCenterline"/> applies to a straight edge, here to each name a fillet arc joins).
    /// </summary>
    private static bool IsTaxiwayName(string name) =>
        !string.Equals(name, "RAMP", StringComparison.OrdinalIgnoreCase)
        && !(name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase) && !name.Contains(":link"));

    // --- Command methods ---

    public async Task SendRawCommandAsync(string callsign, string initials, string command) => await _sendCommand(callsign, command, initials);

    public List<TaxiRoute> FindRoutesToNode(int fromNodeId, int toNodeId, AircraftCategory category, WakeTurbulenceData.WakeClass wakeClass)
    {
        if (_domainLayout is null)
        {
            return [];
        }

        // The pathfinder returns one route per preference (FewestTurns / Shortest / Fastest), deduped — at most 3.
        // It is intentionally per-preference, not a Yen-style k-shortest generator, so requesting 3
        // matches what the router can actually produce (a 4th request always came back empty).
        return TaxiPathfinder.FindRoutes(
            _domainLayout,
            fromNodeId,
            toNodeId,
            preference: null,
            maxRoutes: 3,
            authorizedTaxiways: null,
            category,
            wakeClass
        );
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

        // A crossing is a bar of another runway before the destination bar: the destination bar itself, and any bar of the
        // destination runway (either end, however padded: 12 of 30/12, 1L of 01L/19R), is never one.
        var crossingHoldShorts = new List<(string RwyName, HoldShortPoint Hs)>();
        foreach (HoldShortPoint hs in route.HoldShortPoints)
        {
            if ((hs.Reason == HoldShortReason.RunwayCrossing) && (hs.TargetName is not null) && (hs.NodeId != destHsNodeId))
            {
                var crossed = RunwayIdentifier.Parse(hs.TargetName);
                if (!crossed.Contains(destRunway))
                {
                    crossingHoldShorts.Add((RunwayIdentifier.ToDisplayDesignator(crossed.End1), hs));
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
                $"For Departure {destRunway}, CROSS {string.Join(" ", crossingHoldShorts.Take(i + 1).Select(c => c.RwyName))}, "
                + $"HS {holdEntry.RwyName}";
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

    /// <summary>
    /// The pushback facings at the aircraft's node (<see cref="GetPushbackDirections"/>) as menu choices: "Push back,
    /// face {taxiway}", sending <c>PUSH FACE {cardinal}</c>, an absolute magnetic facing.
    /// </summary>
    public List<MenuCommandChoice> GetPushbackFaceChoices(AircraftModel ac) =>
        [.. GetPushbackDirections(ac).Select(d => new MenuCommandChoice($"Push back, {d.Label}", $"PUSH FACE {d.Cardinal}", null, []))];

    /// <summary>The most stands the Push back to… submenu lists.</summary>
    private const int MaxPushbackToChoices = 30;

    /// <summary>
    /// The named Parking, Spot and Helipad nodes the aircraft can be pushed back to, nearest first, at most
    /// <see cref="MaxPushbackToChoices"/>, excluding the node it stands on: each labelled with its name and sending the
    /// canonical PUSH command (<c>$name</c> for a spot, <c>@name</c> for parking or a helipad). Empty without a layout.
    /// </summary>
    public List<MenuCommandChoice> GetPushbackToChoices(AircraftModel ac)
    {
        if (_domainLayout is null)
        {
            return [];
        }

        int? currentNodeId = GetAircraftNearestNodeId(ac);
        return
        [
            .. _domainLayout
                .Nodes.Values.Where(node =>
                    (node.Type is GroundNodeType.Parking or GroundNodeType.Spot or GroundNodeType.Helipad)
                    && (!string.IsNullOrEmpty(node.Name))
                    && (node.Id != currentNodeId)
                )
                .OrderBy(node => GeoMath.DistanceNm(ac.Position.Lat, ac.Position.Lon, node.Position.Lat, node.Position.Lon))
                .Take(MaxPushbackToChoices)
                .Select(node => new MenuCommandChoice(node.Name!, $"PUSH {(node.Type == GroundNodeType.Spot ? '$' : '@')}{node.Name}", null, [])),
        ];
    }

    /// <summary>
    /// The loaded per-ARTCC catalog's preset taxi routes for this airport whose path resolves from the aircraft's taxi
    /// start (<see cref="TaxiStartNode"/>) for its own category and wake class, as rows: the route's name, its path's
    /// taxiways, the distance from the aircraft along the resolved path, its canonical <c>TAXI</c> command and the path
    /// to preview. A route that does not resolve from here is dropped. Empty without a layout, a navigation database,
    /// routes or a start node.
    /// </summary>
    public List<TaxiRouteRow> GetPresetTaxiChoices(AircraftModel ac) => [.. PresetTaxiRows(ac).Select(preset => preset.Row)];

    /// <summary><see cref="GetPresetTaxiChoices"/>'s rows, each with the preset it was built from.</summary>
    private List<(TaxiRouteDefinition Preset, TaxiRouteRow Row)> PresetTaxiRows(AircraftModel ac)
    {
        if ((_domainLayout is not { } layout) || (NavigationDatabase.InstanceOrNull is not { } navDb) || (TaxiStartNode(layout, ac) is not { } start))
        {
            return [];
        }

        List<(TaxiRouteDefinition Preset, TaxiRouteRow Row)> rows = [];
        foreach (TaxiRouteDefinition preset in navDb.AirportSidecars.GetTaxiRoutes(layout.AirportId))
        {
            if (PresetTaxiRow(layout, start, ac, preset) is { } row)
            {
                rows.Add((preset, row));
            }
        }

        return rows;
    }

    /// <summary>
    /// <paramref name="preset"/>'s row from <paramref name="start"/>: its path resolved for the aircraft's category and wake
    /// class, the path's taxiways as the via (what the command sends), and the distance from the aircraft along the
    /// resolved path. Null when the path does not resolve from there, or ends where the aircraft stands (the bar it is
    /// holding short at), as no runway entry row is offered there.
    /// </summary>
    private static TaxiRouteRow? PresetTaxiRow(AirportGroundLayout layout, GroundNode start, AircraftModel ac, TaxiRouteDefinition preset)
    {
        List<string> path = preset.GetPathTokens();
        TaxiRoute? route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            start.Id,
            path,
            out _,
            new ExplicitPathOptions { OccupiedTaxiway = null, DestinationRunway = preset.DestinationRunway },
            CategoryFor(ac),
            WakeClassFor(ac)
        );
        if ((route is not { Segments.Count: > 0 }) || (StandsAt(ac, route.Segments[^1].Edge.ToNode)))
        {
            return null;
        }

        string via = (path.Count > 0) ? $"via {string.Join(' ', path)}" : "direct";
        return new TaxiRouteRow(
            TaxiRouteRow.PresetBadge,
            preset.Name,
            null,
            null,
            false,
            via,
            RouteDistanceFt(route, ac.Position),
            preset.ToCanonicalCommand(),
            route,
            []
        );
    }

    /// <summary>
    /// The node a taxi from <paramref name="ac"/> starts at, as the server picks it: on the taxiway the aircraft is on, else
    /// the heading-aligned node (<see cref="AirportGroundLayout.FindNearestNodeForTaxi"/>), else the nearest node.
    /// </summary>
    private static GroundNode? TaxiStartNode(AirportGroundLayout layout, AircraftModel ac) =>
        layout.FindNearestNodeForTaxi(ac.Position, ac.Heading) ?? layout.FindNearestNode(ac.Position);

    /// <summary>Whether <paramref name="ac"/> stands at <paramref name="node"/>: under 25 ft away, 0 ft once rounded to 50 ft.</summary>
    private static bool StandsAt(AircraftModel ac, GroundNode node) =>
        TugMovePlanner.NoteDistanceFt(GeoMath.DistanceNm(ac.Position, node.Position) * GeoMath.FeetPerNm) == 0;

    /// <summary>
    /// The distance from the aircraft at <paramref name="position"/> to the end of <paramref name="route"/>, as the Hold
    /// short of… rows measure it (<see cref="RouteStart"/>), rounded to the nearest 50 ft.
    /// </summary>
    private static double RouteDistanceFt(TaxiRoute route, LatLon position) =>
        TugMovePlanner.NoteDistanceFt(RouteStart(route, position).OffsetFt + route.PrefixDistanceFt(route.Segments.Count));

    /// <summary>The most runway ends Taxi to runway shows inline when the room names no active runway at the airport.</summary>
    private const int MaxInlineRunwayEnds = 3;

    /// <summary>A hold short of a runway the aircraft can taxi to, the shortest route there and its distance from the aircraft.</summary>
    private sealed record RunwayEntry(GroundNode Bar, TaxiRoute Route, double DistanceFt);

    /// <summary>
    /// The Taxi to runway submenu for <paramref name="ac"/>, from its taxi start (<see cref="TaxiStartNode"/>) with its own
    /// category and wake class. Every runway end with a hold short the aircraft can reach gets a group: the full-length
    /// entry and the intersections long enough for its type (<see cref="EntryRowsFor"/>), then the presets ending at that end
    /// (<see cref="GetPresetTaxiChoices"/>). The groups are placed by <see cref="GroupRunwayEnds"/>.
    /// With an active departure end at the airport, only the runways of the assigned and active departure ends are searched
    /// here; the rest are searched when Other runways opens (<see cref="TaxiToRunwayMenu.FindOther"/>), and only when some
    /// runway end is not inline. Without one, the inline ends are the route-nearest, so every runway is searched at once.
    /// Other runways opened after the layout changed finds no group. Empty without a layout or a start node.
    /// </summary>
    public TaxiToRunwayMenu GetTaxiToRunwayChoices(AircraftModel ac)
    {
        if ((_domainLayout is not { } layout) || (TaxiStartNode(layout, ac) is not { } start))
        {
            return TaxiToRunwayMenu.Empty;
        }

        List<(TaxiRouteDefinition Preset, TaxiRouteRow Row)> presets = PresetTaxiRows(ac);
        List<RunwayIdentifier> runways = RunwaysWithHoldShorts(layout);
        string assignedRunway = ac.AssignedRunway;
        List<string> departureEnds = ActiveDepartureRunwayEnds(layout.AirportId);
        if (departureEnds.Count == 0)
        {
            return GroupRunwayEnds(RunwayEndRows(layout, start, ac, runways, presets), assignedRunway, departureEnds);
        }

        List<string> inlineEnds = [.. departureEnds];
        if (!string.IsNullOrEmpty(assignedRunway))
        {
            inlineEnds.Add(assignedRunway);
        }

        List<RunwayIdentifier> inlineRunways = [.. runways.Where(runway => inlineEnds.Any(runway.Contains))];
        TaxiToRunwayMenu inline = GroupRunwayEnds(RunwayEndRows(layout, start, ac, inlineRunways, presets), assignedRunway, departureEnds);
        bool anyEndNotInline = runways.SelectMany(EndsOf).Any(end => !inlineEnds.Any(inlineEnd => SameRunwayEnd(end, inlineEnd)));
        if (!anyEndNotInline)
        {
            return new TaxiToRunwayMenu(inline.Inline, [], null);
        }

        return new TaxiToRunwayMenu(
            inline.Inline,
            [],
            () =>
                ReferenceEquals(_domainLayout, layout)
                    ? GroupRunwayEnds(RunwayEndRows(layout, start, ac, runways, presets), assignedRunway, departureEnds).Other
                    : []
        );
    }

    /// <summary>A runway's ends, one for a runway named by a single end.</summary>
    private static string[] EndsOf(RunwayIdentifier runway) => SameRunwayEnd(runway.End1, runway.End2) ? [runway.End1] : [runway.End1, runway.End2];

    /// <summary>
    /// The rows of each end of <paramref name="runways"/> that has any: its entries (<see cref="EntryRowsFor"/>), then the
    /// presets that end at it. A runway searched before from the same start is answered from the route cache.
    /// </summary>
    private List<(string End, List<TaxiRouteRow> Rows)> RunwayEndRows(
        AirportGroundLayout layout,
        GroundNode start,
        AircraftModel ac,
        List<RunwayIdentifier> runways,
        List<(TaxiRouteDefinition Preset, TaxiRouteRow Row)> presets
    )
    {
        List<(string End, List<TaxiRouteRow> Rows)> ends = [];
        double takeoffDistanceFt = TakeoffDistanceFt(ac);
        foreach (RunwayIdentifier runway in runways)
        {
            List<RunwayEntry> entries = RunwayEntries(layout, start, ac, runway);
            foreach (string end in EndsOf(runway))
            {
                List<TaxiRouteRow> rows =
                [
                    .. EntryRowsFor(layout, runway, end, entries, takeoffDistanceFt),
                    .. presets.Where(p => (p.Preset.DestinationRunway is { } destination) && SameRunwayEnd(destination, end)).Select(p => p.Row),
                ];
                if (rows.Count > 0)
                {
                    ends.Add((end, rows));
                }
            }
        }

        return ends;
    }

    /// <summary>Every runway some <c>RunwayHoldShort</c> node of <paramref name="layout"/> holds short of, each once.</summary>
    private static List<RunwayIdentifier> RunwaysWithHoldShorts(AirportGroundLayout layout) =>
        [.. layout.Nodes.Values.Where(n => n.Type == GroundNodeType.RunwayHoldShort).Select(n => n.RunwayId).OfType<RunwayIdentifier>().Distinct()];

    /// <summary>Whether <paramref name="node"/> is a hold short of <paramref name="runway"/>.</summary>
    private static bool IsHoldShortOf(GroundNode node, RunwayIdentifier runway) =>
        (node.Type == GroundNodeType.RunwayHoldShort) && (node.RunwayId is { } id) && (id == runway);

    /// <summary>
    /// The hold shorts of <paramref name="runway"/> the aircraft can taxi to from <paramref name="start"/>
    /// (<see cref="RunwayEntryRoutes"/>), each with its distance from the aircraft, leaving out any hold short the aircraft
    /// stands at (0 ft once rounded): the bar it is holding short at.
    /// </summary>
    private List<RunwayEntry> RunwayEntries(AirportGroundLayout layout, GroundNode start, AircraftModel ac, RunwayIdentifier runway)
    {
        List<RunwayEntry> entries = [];
        foreach ((GroundNode bar, TaxiRoute route) in RunwayEntryRoutes(layout, start, CategoryFor(ac), WakeClassFor(ac), runway))
        {
            double distanceFt = RouteDistanceFt(route, ac.Position);
            if (distanceFt > 0)
            {
                entries.Add(new RunwayEntry(bar, route, distanceFt));
            }
        }

        return entries;
    }

    /// <summary>The most (start node, category, wake class, runway) keys the runway entry route cache holds before it starts over.</summary>
    private const int MaxCachedRunwayEntryRoutes = 2000;

    /// <summary>The layout <see cref="_runwayEntryRoutes"/> was found on; a new layout empties it.</summary>
    private AirportGroundLayout? _runwayEntryRoutesLayout;

    /// <summary>
    /// The shortest route from a start node to each hold short of a runway an aircraft of the category and wake class can
    /// taxi to, found once per key: a route depends only on the layout and those, never on where around the start node the
    /// aircraft stands, so every later menu from that node reuses it.
    /// </summary>
    private readonly Dictionary<
        (int StartNodeId, AircraftCategory Category, WakeTurbulenceData.WakeClass WakeClass, RunwayIdentifier Runway),
        List<(GroundNode Bar, TaxiRoute Route)>
    > _runwayEntryRoutes = [];

    /// <summary>Empties the runway entry route cache once its layout is gone.</summary>
    private void ForgetRunwayEntryRoutes()
    {
        _runwayEntryRoutes.Clear();
        _runwayEntryRoutesLayout = null;
    }

    /// <summary>How many hold-short route searches Taxi to runway has run; an answer from the cache runs none.</summary>
    public int TaxiToRunwayRouteSearches { get; private set; }

    /// <summary>
    /// The hold shorts of <paramref name="runway"/> reachable from <paramref name="start"/>, each by its shortest route
    /// (<see cref="RoutePreference.Shortest"/>), in node order, from the cache when this key was searched on this layout.
    /// Left out: <paramref name="start"/> itself; a hold short on another runway's pavement only
    /// (<see cref="LiesOnRunwayPavementOnly"/>), which is no entry; a hold short whose route passes another hold short of
    /// the same runway; and one whose route travels along the runway's own centreline, which crosses the runway (7110.65
    /// 3-7-2) rather than taxiing to it, as to the bar across the runway from the one the aircraft holds at.
    /// </summary>
    private List<(GroundNode Bar, TaxiRoute Route)> RunwayEntryRoutes(
        AirportGroundLayout layout,
        GroundNode start,
        AircraftCategory category,
        WakeTurbulenceData.WakeClass wakeClass,
        RunwayIdentifier runway
    )
    {
        if ((!ReferenceEquals(_runwayEntryRoutesLayout, layout)) || (_runwayEntryRoutes.Count >= MaxCachedRunwayEntryRoutes))
        {
            _runwayEntryRoutes.Clear();
            _runwayEntryRoutesLayout = layout;
        }

        (int, AircraftCategory, WakeTurbulenceData.WakeClass, RunwayIdentifier) key = (start.Id, category, wakeClass, runway);
        if (_runwayEntryRoutes.TryGetValue(key, out List<(GroundNode Bar, TaxiRoute Route)>? cached))
        {
            return cached;
        }

        List<(GroundNode Bar, TaxiRoute Route)> routes = [];
        IEnumerable<GroundNode> bars = layout.Nodes.Values.Where(n =>
            (IsHoldShortOf(n, runway)) && (n.Id != start.Id) && (!LiesOnRunwayPavementOnly(n))
        );
        foreach (GroundNode bar in bars.OrderBy(n => n.Id))
        {
            TaxiToRunwayRouteSearches++;
            TaxiRoute? route = TaxiPathfinder
                .FindRoutes(layout, start.Id, bar.Id, RoutePreference.Shortest, maxRoutes: 1, authorizedTaxiways: null, category, wakeClass)
                .FirstOrDefault();
            if ((route is { Segments.Count: > 0 }) && (!PassesAnotherHoldShortOf(layout, route, runway)) && (!TravelsOnRunway(route, runway)))
            {
                routes.Add((bar, route));
            }
        }

        _runwayEntryRoutes[key] = routes;
        return routes;
    }

    /// <summary>
    /// Whether every edge at <paramref name="node"/> runs along a runway centreline: a hold short painted on another runway's
    /// pavement, not on a taxiway leading to its own runway.
    /// </summary>
    public static bool LiesOnRunwayPavementOnly(GroundNode node) => (node.Edges.Count > 0) && node.Edges.All(edge => edge.IsRunwayCenterline);

    /// <summary>
    /// Whether <paramref name="route"/> goes onto <paramref name="runway"/>'s centreline: before the bar it ends at, it
    /// reaches a node on one of the runway's centreline edges, whether it then runs along the runway or straight across it.
    /// The bar itself is not counted, so a bar with an edge on its own runway's centreline is still an entry.
    /// </summary>
    private static bool TravelsOnRunway(TaxiRoute route, RunwayIdentifier runway) =>
        route
            .Segments.Take(route.Segments.Count - 1)
            .Any(segment => segment.Edge.ToNode.Edges.Any(edge => (edge.IsRunwayCenterline) && (edge.MatchesRunway(runway.End1))));

    /// <summary>Whether <paramref name="route"/> passes a hold short of <paramref name="runway"/> before the one it ends at.</summary>
    private static bool PassesAnotherHoldShortOf(AirportGroundLayout layout, TaxiRoute route, RunwayIdentifier runway) =>
        route
            .Segments.Take(route.Segments.Count - 1)
            .Any(segment => (layout.Nodes.TryGetValue(segment.ToNodeId, out GroundNode? node)) && (IsHoldShortOf(node, runway)));

    /// <summary>
    /// <paramref name="end"/>'s entry rows from <paramref name="entries"/> for a type needing
    /// <paramref name="takeoffDistanceFt"/> of runway (0 for a type with no figure): the full-length entry first, the
    /// nearest of <see cref="FullLengthBars"/>, then the intersections leaving enough runway ahead
    /// (<see cref="UsableIntersections"/>), most runway left first, each showing what it leaves rounded down to 50 ft
    /// (<see cref="AvailableRunwayFt"/>). The hold shorts at the other end's threshold, from which a departure on
    /// <paramref name="end"/> has no runway ahead, and the other full-length ones are left out. The nearest row is tagged
    /// <c>nearest</c> (<c>nearest, full length</c> when it is the full-length one), and it and the full-length row are
    /// highlighted. A runway whose full length is under <paramref name="takeoffDistanceFt"/> is still offered at full
    /// length, its row's reason tagged <see cref="ShortForTypeTag"/>. A row whose route names no taxiway is left out.
    /// </summary>
    private List<TaxiRouteRow> EntryRowsFor(
        AirportGroundLayout layout,
        RunwayIdentifier runway,
        string end,
        List<RunwayEntry> entries,
        double takeoffDistanceFt
    )
    {
        string otherEnd = string.Equals(end, runway.End1, StringComparison.OrdinalIgnoreCase) ? runway.End2 : runway.End1;
        HashSet<int> oppositeBars = FullLengthBars(layout, runway, otherEnd);
        HashSet<int> fullLengthBars = FullLengthBars(layout, runway, end);
        List<RunwayEntry> candidates = [.. entries.Where(entry => !oppositeBars.Contains(entry.Bar.Id))];
        RunwayEntry? fullLength = candidates.Where(entry => fullLengthBars.Contains(entry.Bar.Id)).MinBy(entry => entry.DistanceFt);
        List<(RunwayEntry Entry, double? RemainingFt)> intersections = UsableIntersections(
            runway,
            otherEnd,
            [.. candidates.Where(entry => !fullLengthBars.Contains(entry.Bar.Id))],
            takeoffDistanceFt
        );
        RunwayEntry? nearest = intersections.Select(i => i.Entry).Append(fullLength).OfType<RunwayEntry>().MinBy(entry => entry.DistanceFt);

        List<TaxiRouteRow?> rows = [];
        if (fullLength is not null)
        {
            bool isNearest = ReferenceEquals(fullLength, nearest);
            string reason = isNearest ? "nearest, full length" : "full length";
            bool isShort =
                (takeoffDistanceFt > 0) && (RunwayRemainingFt(runway, otherEnd, fullLength.Bar) is { } fullFt) && (fullFt < takeoffDistanceFt);
            rows.Add(EntryRow(fullLength, end, isShort ? $"{reason} · {ShortForTypeTag}" : reason, null, true));
        }

        foreach ((RunwayEntry entry, double? remainingFt) in intersections)
        {
            bool isNearest = ReferenceEquals(entry, nearest);
            double? availableFt = (remainingFt is { } ft) ? AvailableRunwayFt(ft) : null;
            rows.Add(EntryRow(entry, end, isNearest ? "nearest" : null, availableFt, isNearest));
        }

        return [.. rows.OfType<TaxiRouteRow>()];
    }

    /// <summary>
    /// The intersection entries of <paramref name="intersections"/> that leave enough of <paramref name="runway"/> ahead of a
    /// departure toward <paramref name="farEnd"/> (<see cref="RunwayRemainingFt(RunwayIdentifier, string, GroundNode)"/>),
    /// each with what it leaves, most runway left first. Enough is the type's <paramref name="takeoffDistanceFt"/>; for a
    /// type with no figure (0), half the runway's length, so a jet with no profile figure is never offered an entry near the
    /// far end. Without the runway's geometry nothing can be measured: every entry is kept, nearest first, with no length.
    /// </summary>
    private List<(RunwayEntry Entry, double? RemainingFt)> UsableIntersections(
        RunwayIdentifier runway,
        string farEnd,
        List<RunwayEntry> intersections,
        double takeoffDistanceFt
    )
    {
        double? requiredFt = (takeoffDistanceFt > 0) ? takeoffDistanceFt : RunwayLengthFt(runway) / 2;
        return
        [
            .. intersections
                .Select(entry => (Entry: entry, RemainingFt: RunwayRemainingFt(runway, farEnd, entry.Bar)))
                .Where(i => (i.RemainingFt is not { } remainingFt) || (requiredFt is not { } required) || (remainingFt >= required))
                .OrderByDescending(i => i.RemainingFt ?? double.MinValue)
                .ThenBy(i => i.Entry.DistanceFt),
        ];
    }

    /// <summary>
    /// The runway <paramref name="ac"/>'s type needs to take off, in feet: its profile's takeoff distance after the override
    /// layer (<see cref="AircraftProfileDatabase.Get"/>); 0 when the type has no profile or its profile carries no figure.
    /// </summary>
    public static double TakeoffDistanceFt(AircraftModel ac) =>
        (AircraftProfileDatabase.Get(ac.AircraftType) is { TakeoffDistance: > 0 } profile) ? profile.TakeoffDistance : 0;

    /// <summary>The tag a full-length entry row's reason gains when the runway is shorter than the type's takeoff distance.</summary>
    public const string ShortForTypeTag = "short for type";

    /// <summary>A runway length left ahead of an intersection as its row shows it: rounded down to 50 ft, never up.</summary>
    public static double AvailableRunwayFt(double remainingFt) => Math.Floor(remainingFt / 50) * 50;

    /// <summary>
    /// How much runway lies ahead of an aircraft entering at the hold short <paramref name="barNodeId"/> for a departure
    /// from <paramref name="departureEnd"/> (<see cref="RunwayRemainingFt(RunwayIdentifier, string, GroundNode)"/>). Null
    /// without a layout, when the node is no hold short in it, or when the layout has no geometry for its runway.
    /// </summary>
    public double? RunwayRemainingFt(string departureEnd, int barNodeId)
    {
        if ((_domainLayout is not { } layout) || (!layout.Nodes.TryGetValue(barNodeId, out GroundNode? bar)) || (bar.RunwayId is not { } runway))
        {
            return null;
        }

        return RunwayRemainingFt(runway, SameRunwayEnd(departureEnd, runway.End1) ? runway.End2 : runway.End1, bar);
    }

    /// <summary>
    /// How much of <paramref name="runway"/> lies ahead of an aircraft entering at <paramref name="bar"/> for a departure
    /// toward <paramref name="farEnd"/>: the along-track distance from where the bar's taxiway meets the centreline
    /// (<see cref="RunwayJunction"/>, the bar itself when none is found) to <paramref name="farEnd"/>'s threshold, from the
    /// runway's coordinates (<see cref="RunwayEndGeometry"/>). Null when the layout has no geometry for the runway.
    /// </summary>
    private double? RunwayRemainingFt(RunwayIdentifier runway, string farEnd, GroundNode bar)
    {
        if (RunwayEndGeometry(runway, farEnd) is not { } far)
        {
            return null;
        }

        LatLon junction = (RunwayJunction(runway, bar) ?? bar).Position;
        return GeoMath.AlongTrackDistanceNm(junction, far.Threshold, far.Heading) * GeoMath.FeetPerNm;
    }

    /// <summary>The length of <paramref name="runway"/> between its coordinates' ends, in feet; null without its geometry.</summary>
    private double? RunwayLengthFt(RunwayIdentifier runway) =>
        (RunwayCenterline(runway) is { } line) ? GeoMath.DistanceNm(line.First, line.Last) * GeoMath.FeetPerNm : null;

    /// <summary>How far along the taxiways from a hold short <see cref="RunwayJunction"/> looks for its runway's centreline.</summary>
    private const double MaxJunctionSearchFt = 2000;

    /// <summary>
    /// The node where <paramref name="bar"/>'s taxiway meets <paramref name="runway"/>'s centreline: the nearest node, along
    /// the graph's off-centreline edges and within <see cref="MaxJunctionSearchFt"/> of the bar, that has a centreline edge
    /// of the runway. Null when none is that close.
    /// </summary>
    private static GroundNode? RunwayJunction(RunwayIdentifier runway, GroundNode bar)
    {
        var settled = new HashSet<int>();
        var queue = new PriorityQueue<GroundNode, double>();
        queue.Enqueue(bar, 0);
        while (queue.TryDequeue(out GroundNode? node, out double distanceFt))
        {
            if (!settled.Add(node.Id))
            {
                continue;
            }

            if (node.Edges.Any(edge => (edge.IsRunwayCenterline) && (edge.MatchesRunway(runway.End1))))
            {
                return node;
            }

            foreach (IGroundEdge edge in node.Edges.Where(edge => !edge.IsRunwayCenterline))
            {
                double nextFt = distanceFt + (edge.DistanceNm * GeoMath.FeetPerNm);
                if (nextFt <= MaxJunctionSearchFt)
                {
                    queue.Enqueue(edge.OtherNode(node), nextFt);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The hold shorts of <paramref name="runway"/> at <paramref name="end"/>'s threshold: those whose along-track distance
    /// from it is within <see cref="FullLengthWindowFt"/> of the smallest. Empty when the layout has no geometry for the end.
    /// </summary>
    private HashSet<int> FullLengthBars(AirportGroundLayout layout, RunwayIdentifier runway, string end)
    {
        if (RunwayEndGeometry(runway, end) is not { } geometry)
        {
            return [];
        }

        List<(int Id, double AlongFt)> along =
        [
            .. layout
                .Nodes.Values.Where(n => IsHoldShortOf(n, runway))
                .Select(n => (n.Id, GeoMath.AlongTrackDistanceNm(n.Position, geometry.Threshold, geometry.Heading) * GeoMath.FeetPerNm)),
        ];
        if (along.Count == 0)
        {
            return [];
        }

        double windowFt = along.Min(a => a.AlongFt) + FullLengthWindowFt;
        return [.. along.Where(a => a.AlongFt <= windowFt).Select(a => a.Id)];
    }

    /// <summary>
    /// <paramref name="entry"/>'s row for a departure from <paramref name="end"/>: named by its hold short's taxiway
    /// (<see cref="GetHoldShortTaxiwayName"/>), showing <paramref name="reason"/>, the runway <paramref name="availableFt"/>
    /// it leaves ahead (an intersection) and the route's via, highlighted when <paramref name="isHighlighted"/>, and opening
    /// the For departure and Hold short variants (<see cref="BuildTaxiDestVariants"/>), the first of which it shows. Null
    /// when the route names no taxiway, so no variant can be sent and the entry has no name (<see cref="EntryName"/>).
    /// </summary>
    private TaxiRouteRow? EntryRow(RunwayEntry entry, string end, string? reason, double? availableFt, bool isHighlighted)
    {
        string runwayEnd = RunwayIdentifier.ToDisplayDesignator(end);
        List<MenuCommandChoice> variants =
        [
            .. BuildTaxiDestVariants(entry.Route, runwayEnd, spot: null)
                .Select(v =>
                    (v is { } variant) ? new MenuCommandChoice(variant.Label, variant.Command, variant.Preview, []) : MenuCommandChoice.Separator
                ),
        ];
        if ((variants is not [{ Command: { } firstCommand }, ..]) || (EntryName(GetHoldShortTaxiwayName(entry.Bar.Id), entry.Route) is not { } name))
        {
            return null;
        }

        return new TaxiRouteRow(
            TaxiRouteRow.RunwayEntryBadge,
            name,
            reason,
            availableFt,
            isHighlighted,
            GetTaxiwayDisplayName(entry.Route),
            entry.DistanceFt,
            firstCommand,
            entry.Route,
            variants
        );
    }

    /// <summary>
    /// A runway entry row's name, <c>At {taxiway}</c>: the taxiway its hold short sits on (<paramref name="barTaxiway"/>),
    /// else the last taxiway <paramref name="route"/> names, the one arriving at the bar. Null when neither names one.
    /// </summary>
    public static string? EntryName(string? barTaxiway, TaxiRoute route) =>
        ((barTaxiway ?? TaxiRouteFormatter.CleanTaxiwaySequence(route).LastOrDefault()) is { } taxiway) ? $"At {taxiway}" : null;

    /// <summary>
    /// Places the runway ends' groups: the end of <paramref name="assignedRunway"/> first, then each of
    /// <paramref name="departureEnds"/> in order; with no departure end at the airport, the ends nearest the aircraft fill
    /// the inline list up to <see cref="MaxInlineRunwayEnds"/>, the assigned one counted. Every other end goes under Other
    /// runways in runway order. An end with no row is never a group.
    /// An active list naming only arrival ends (<c>A28R</c>) names no departure runway, so it takes the nearest-ends
    /// fallback as an empty list does.
    /// </summary>
    private static TaxiToRunwayMenu GroupRunwayEnds(
        List<(string End, List<TaxiRouteRow> Rows)> ends,
        string assignedRunway,
        IReadOnlyList<string> departureEnds
    )
    {
        List<(string End, List<TaxiRouteRow> Rows)> remaining = [.. ends];
        List<TaxiToRunwayGroup> inline = [];
        if ((!string.IsNullOrEmpty(assignedRunway)) && (TakeEnd(remaining, assignedRunway) is { } assigned))
        {
            inline.Add(RunwayEndGroup(assigned, "assigned runway"));
        }

        foreach (string departureEnd in departureEnds)
        {
            if (TakeEnd(remaining, departureEnd) is { } departure)
            {
                inline.Add(RunwayEndGroup(departure, "departure runway"));
            }
        }

        if (departureEnds.Count == 0)
        {
            List<(string End, List<TaxiRouteRow> Rows)> nearest =
            [
                .. remaining.OrderBy(e => e.Rows.Min(row => row.DistanceFt)).Take(Math.Max(0, MaxInlineRunwayEnds - inline.Count)),
            ];
            foreach ((string End, List<TaxiRouteRow> Rows) end in nearest)
            {
                remaining.Remove(end);
                inline.Add(RunwayEndGroup(end, null));
            }
        }

        List<TaxiToRunwayGroup> other =
        [
            .. remaining.OrderBy(e => RunwayNumber(e.End)).ThenBy(e => e.End, StringComparer.OrdinalIgnoreCase).Select(e => RunwayEndGroup(e, null)),
        ];
        return new TaxiToRunwayMenu(inline, other, null);
    }

    /// <summary>Removes and returns the group of <paramref name="end"/> from <paramref name="ends"/>; null when it has none.</summary>
    private static (string End, List<TaxiRouteRow> Rows)? TakeEnd(List<(string End, List<TaxiRouteRow> Rows)> ends, string end)
    {
        int index = ends.FindIndex(e => SameRunwayEnd(e.End, end));
        if (index < 0)
        {
            return null;
        }

        (string End, List<TaxiRouteRow> Rows) taken = ends[index];
        ends.RemoveAt(index);
        return taken;
    }

    /// <summary>A runway end's group, titled <c>Runway 30</c>, with <c> · {role}</c> after it when it has one.</summary>
    private static TaxiToRunwayGroup RunwayEndGroup((string End, List<TaxiRouteRow> Rows) end, string? role)
    {
        string title = $"Runway {RunwayIdentifier.ToDisplayDesignator(end.End)}";
        return new TaxiToRunwayGroup((role is null) ? title : $"{title} · {role}", end.Rows);
    }

    /// <summary>Whether two spellings name the same runway end (<c>1L</c> and <c>01L</c>).</summary>
    private static bool SameRunwayEnd(string a, string b) =>
        string.Equals(RunwayIdentifier.NormalizeDesignator(a), RunwayIdentifier.NormalizeDesignator(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The Hold short of… rows for <paramref name="ac"/>'s remaining route: one per bar the route passes, nearest first,
    /// each sending the <c>HS</c> that arms that bar and previewing the route up to it. A taxiway the route meets on more
    /// than one of its taxiways gets one located row per crossing (<c>X@A</c>, <c>X@B</c>), since a bare <c>HS X</c> only
    /// ever binds the first; a runway gets one row, for its first bar, named by <see cref="RunwayRowName"/>. A junction
    /// arc's joined name ("X - Y") is never a row, and a bar at the aircraft's own position (0 ft once rounded) is not
    /// listed. The node the route starts at is a bar like any other: an aircraft short of a holding position starts
    /// there with no free-space leg (<see cref="TaxiApproachLeg"/>), so every distance is measured from the aircraft
    /// (<see cref="RouteStart"/>). Empty when no layout is loaded or the route does not resolve.
    /// </summary>
    public IReadOnlyList<HoldShortChoice> GetHoldShortTargets(AircraftModel ac)
    {
        if ((_domainLayout is not { } layout) || (ResolveRemainingRoute(ac) is not { Segments.Count: > 0 } route))
        {
            return [];
        }

        var routeTaxiways = new HashSet<string>(ParseRouteTaxiways(ac.TaxiRoute), StringComparer.OrdinalIgnoreCase);
        (double OffsetFt, int FirstPosition) start = RouteStart(route, ac.Position);
        HashSet<string> activeEnds = ActiveRunwayEnds(layout.AirportId);
        List<(int Position, HoldShortChoice Row)> rows = [];
        foreach (
            (string Target, string Badge, string Name) rowTarget in HoldShortRowTargets(layout, route, routeTaxiways, start.FirstPosition, activeEnds)
        )
        {
            if (HoldShortRow(layout, route, start, rowTarget) is { } row)
            {
                rows.Add(row);
            }
        }

        return
        [
            .. rows.OrderBy(r => r.Position)
                .ThenBy(r => (r.Row.Label.Badge == HoldShortChoice.RunwayBadge) ? 0 : 1)
                .ThenBy(r => r.Row.Label.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Row.Command, StringComparer.OrdinalIgnoreCase)
                .Select(r => r.Row),
        ];
    }

    /// <summary>
    /// Where the aircraft at <paramref name="position"/> stands relative to the node <paramref name="route"/> starts at:
    /// the distance to add to the route's own to measure from the aircraft, and the first route position
    /// (<see cref="RouteNodeId"/>) a bar can be at.
    /// <list type="bullet">
    /// <item>A route opening with the free-space leg from the aircraft (<see cref="WithApproachLeg"/>) already carries
    /// its length, and its virtual start is the aircraft itself: no offset, from position 1.</item>
    /// <item>An aircraft past the start node along segment 0 (the projection <see cref="TaxiApproachLeg"/> calls past the
    /// start; a turn about's route runs back over the aircraft) has that stretch behind it, and the server never binds the
    /// start node: less the stretch, from position 1.</item>
    /// <item>An aircraft standing on the start node (within <see cref="AirportGroundLayout.AtNodeToleranceFt"/>) is at its
    /// bar already: the distance to it, from position 1.</item>
    /// <item>Otherwise the aircraft is short of the start node, which is a bar like any other (an edge ending at a
    /// holding position gets no free-space leg): the great-circle distance to it, from position 0.</item>
    /// </list>
    /// </summary>
    private static (double OffsetFt, int FirstPosition) RouteStart(TaxiRoute route, LatLon position)
    {
        DirectionalEdge first = route.Segments[0].Edge;
        if (VirtualNode.IsVirtualNode(first.FromNode))
        {
            return (0, 1);
        }

        double pastStartFt =
            GeoMath.AlongTrackDistanceNm(position, first.FromNode.Position, new TrueHeading(first.DepartureBearing)) * GeoMath.FeetPerNm;
        if (pastStartFt > 0)
        {
            return (-pastStartFt, 1);
        }

        double toStartFt = GeoMath.DistanceNm(position, first.FromNode.Position) * GeoMath.FeetPerNm;
        return (toStartFt, (toStartFt <= AirportGroundLayout.AtNodeToleranceFt) ? 1 : 0);
    }

    /// <summary>
    /// The node at <paramref name="position"/> along <paramref name="route"/>: 0 is the node it starts at, and
    /// <c>p</c> the end of segment <c>p − 1</c>.
    /// </summary>
    private static int RouteNodeId(TaxiRoute route, int position) =>
        (position == 0) ? route.Segments[0].FromNodeId : route.Segments[position - 1].ToNodeId;

    /// <summary>
    /// The route taxiway the node at <paramref name="position"/> lies on: the one the route arrives by, and for the start
    /// node the one it leaves by. Null when that segment names none.
    /// </summary>
    private static string? RouteTaxiwayAt(TaxiRoute route, int position) => ArrivingTaxiway(route.Segments[Math.Max(position - 1, 0)]);

    /// <summary>
    /// The line heading the Hold short of… rows: <c>route S T V W4 · RWY 30</c>, without the runway part when the
    /// clearance names none; null when the aircraft has no taxi route.
    /// </summary>
    public static string? HoldShortRouteLine(AircraftModel ac)
    {
        List<string> taxiways = ParseRouteTaxiways(ac.TaxiRoute);
        if (taxiways.Count == 0)
        {
            return null;
        }

        string line = $"route {string.Join(' ', taxiways)}";
        return string.IsNullOrEmpty(ac.AssignedRunway) ? line : $"{line} · RWY {RunwayIdentifier.ToDisplayDesignator(ac.AssignedRunway)}";
    }

    /// <summary>
    /// The <c>HS</c> target of every row <paramref name="route"/> offers, with its badge and name: one per runway whose
    /// bar it passes, then the taxiways it meets off its own (<paramref name="routeTaxiways"/>), bare when met on one
    /// route taxiway and located once per route taxiway otherwise. The walk starts at <paramref name="firstPosition"/>
    /// (<see cref="RouteStart"/>); runway rows are named from <paramref name="activeEnds"/>.
    /// </summary>
    private List<(string Target, string Badge, string Name)> HoldShortRowTargets(
        AirportGroundLayout layout,
        TaxiRoute route,
        HashSet<string> routeTaxiways,
        int firstPosition,
        HashSet<string> activeEnds
    )
    {
        List<(string Target, string Badge, string Name)> targets = [];
        var runways = new HashSet<RunwayIdentifier>();
        var crossings = new Dictionary<string, List<string?>>(StringComparer.OrdinalIgnoreCase);
        for (int position = firstPosition; position <= route.Segments.Count; position++)
        {
            if (!layout.Nodes.TryGetValue(RouteNodeId(route, position), out GroundNode? node))
            {
                continue;
            }

            if ((node.Type == GroundNodeType.RunwayHoldShort) && (node.RunwayId is { } runway) && runways.Add(runway))
            {
                (string name, string end) = RunwayRowName(layout, runway, node, activeEnds);
                targets.Add((end, HoldShortChoice.RunwayBadge, name));
            }

            string? location = RouteTaxiwayAt(route, position);
            foreach (string name in CrossingTaxiways(node, routeTaxiways))
            {
                // A location equal to the target names no crossing: record it as unlocated.
                RecordCrossing(crossings, name, string.Equals(location, name, StringComparison.OrdinalIgnoreCase) ? null : location);
            }
        }

        targets.AddRange(crossings.SelectMany(crossing => TaxiwayRowTargets(crossing.Key, crossing.Value)));
        return targets;
    }

    /// <summary>
    /// The <c>HS</c> targets of taxiway <paramref name="name"/>'s rows: one located target per route taxiway it is met
    /// on when there are several, else the bare name.
    /// </summary>
    private static IEnumerable<(string Target, string Badge, string Name)> TaxiwayRowTargets(string name, List<string?> locations)
    {
        List<string> located = [.. locations.OfType<string>()];
        return (located.Count > 1)
            ? located.Select(location => ($"{name}@{location}", HoldShortChoice.TaxiwayBadge, name))
            : [(name, HoldShortChoice.TaxiwayBadge, name)];
    }

    private static void RecordCrossing(Dictionary<string, List<string?>> crossings, string target, string? location)
    {
        if (!crossings.TryGetValue(target, out List<string?>? locations))
        {
            locations = [];
            crossings[target] = locations;
        }

        if (!locations.Contains(location, StringComparer.OrdinalIgnoreCase))
        {
            locations.Add(location);
        }
    }

    /// <summary>
    /// The taxiways <paramref name="node"/> meets other than the route's own, as the server's <c>MatchesTaxiway</c> decides:
    /// the name of each straight edge, and each taxiway a junction arc joins ("X - Y" meets X and Y), since <c>HS X</c>
    /// binds the first node with an X arc even where X's own edges never touch the route. The joined name is never a
    /// taxiway, and runway centerlines and ramp are no hold-short target.
    /// </summary>
    private static IEnumerable<string> CrossingTaxiways(GroundNode node, HashSet<string> routeTaxiways) =>
        node
            .Edges.Where(edge => !edge.IsRunwayCenterline)
            .SelectMany(edge => (edge is GroundArc arc) ? arc.TaxiwayNames : [edge.TaxiwayName])
            .Where(name =>
                (name.Length > 0)
                && !name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(name, "RAMP", StringComparison.OrdinalIgnoreCase)
                && !routeTaxiways.Contains(name)
            )
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The row for <paramref name="rowTarget"/>: the bar <c>HS {target}</c> binds on <paramref name="route"/>
    /// (<see cref="HoldShortBindIndex"/>, walked from <paramref name="start"/>'s first position), with its position along
    /// the route, and its distance from the aircraft: the route up to it plus <paramref name="start"/>'s offset. Null when
    /// the target binds no bar, or binds one at the aircraft's own position.
    /// </summary>
    private (int Position, HoldShortChoice Row)? HoldShortRow(
        AirportGroundLayout layout,
        TaxiRoute route,
        (double OffsetFt, int FirstPosition) start,
        (string Target, string Badge, string Name) rowTarget
    )
    {
        if (!HoldShortTarget.TryParse(rowTarget.Target, out HoldShortTarget holdShort, out string? error))
        {
            _log.LogWarning("Hold short of… skips '{Target}': {Error}", rowTarget.Target, error);
            return null;
        }

        if (HoldShortBindIndex(layout, route, holdShort, start.FirstPosition) is not { } position)
        {
            return null;
        }

        double distanceFt = TugMovePlanner.NoteDistanceFt(start.OffsetFt + route.PrefixDistanceFt(position));
        if (distanceFt <= 0)
        {
            return null;
        }

        var label = new HoldShortRowLabel(rowTarget.Badge, rowTarget.Name, WhereOnRoute(route, position), distanceFt);
        var preview = new TaxiRoute { Segments = route.Segments.GetRange(0, position), HoldShortPoints = [] };
        return (position, new HoldShortChoice(label, $"HS {rowTarget.Target}", preview));
    }

    /// <summary>
    /// Where the bar at <paramref name="route"/>'s node <paramref name="position"/> (<see cref="RouteNodeId"/>) lies: "at
    /// W4, end of route" at the last node, "before turning onto T" where the route turns onto another taxiway, else
    /// "crossing on S".
    /// </summary>
    private static string WhereOnRoute(TaxiRoute route, int position)
    {
        string? location = RouteTaxiwayAt(route, position);
        if (position == route.Segments.Count)
        {
            return (location is null) ? "at the end of the route" : $"at {location}, end of route";
        }

        string? next = ArrivingTaxiway(route.Segments[position]);
        if ((location is not null) && (next is not null) && !string.Equals(location, next, StringComparison.OrdinalIgnoreCase))
        {
            return $"before turning onto {next}";
        }

        return (location is null) ? "on the route" : $"crossing on {location}";
    }

    /// <summary>
    /// A runway row's name and the end its <c>HS</c> names, from the room's active runway ends at the airport
    /// (<paramref name="active"/>, <see cref="ActiveRunwayEnds"/>): the active end when exactly one is; otherwise the end
    /// on the side of a full-length crossing (<see cref="FullLengthEnd"/>); otherwise both ends ("Runway 12/30"), sending
    /// the lower-numbered end, which binds the same bar as the other.
    /// </summary>
    private (string Name, string End) RunwayRowName(AirportGroundLayout layout, RunwayIdentifier runway, GroundNode bar, HashSet<string> active)
    {
        bool end1Active = active.Contains(runway.End1);
        bool end2Active = active.Contains(runway.End2);
        string? end = (end1Active != end2Active) ? (end1Active ? runway.End1 : runway.End2) : FullLengthEnd(layout, runway, bar);
        if (end is not null)
        {
            string shown = RunwayIdentifier.ToDisplayDesignator(end);
            return ($"Runway {shown}", shown);
        }

        (string low, string high) =
            (RunwayNumber(runway.End1) <= RunwayNumber(runway.End2)) ? (runway.End1, runway.End2) : (runway.End2, runway.End1);
        string lowShown = RunwayIdentifier.ToDisplayDesignator(low);
        return ($"Runway {lowShown}/{RunwayIdentifier.ToDisplayDesignator(high)}", lowShown);
    }

    /// <summary>
    /// The ends the room's active runway list names at <paramref name="airportId"/>, read by
    /// <see cref="ActiveRunwayListParser.FromTokenLists"/> and looked up by <c>ActiveRunways.For</c>,
    /// which normalise the airport id: the server's layout id is lower-case (<c>oak</c>) where the room keys its list by the
    /// FAA id (<c>OAK</c>). A list that does not read is logged and names no end.
    /// </summary>
    private HashSet<string> ActiveRunwayEnds(string airportId) =>
        new(RoomActiveRunwaysAt(airportId, "Hold short of… active runways").Select(runway => runway.Designator), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The ends the room's active runway list names for departures at <paramref name="airportId"/>, in the list's order:
    /// those used for both (<c>30</c>) and for departures only (<c>D28L</c>), never an arrivals-only end (<c>A28R</c>).
    /// </summary>
    private List<string> ActiveDepartureRunwayEnds(string airportId) =>
        [
            .. RoomActiveRunwaysAt(airportId, "Taxi to runway active runways")
                .Where(runway => runway.Use is ActiveRunwayUse.Both or ActiveRunwayUse.Departure)
                .Select(runway => runway.Designator),
        ];

    /// <summary>
    /// The room's active runways at <paramref name="airportId"/>, read by <see cref="ActiveRunwayListParser.FromTokenLists"/>
    /// under <paramref name="label"/> and looked up by <c>ActiveRunways.For</c>, which normalise the airport id: the server's
    /// layout id is lower-case (<c>oak</c>) where the room keys its list by the FAA id (<c>OAK</c>). A list that does not
    /// read is logged and names no runway.
    /// </summary>
    private IReadOnlyList<ActiveRunway> RoomActiveRunwaysAt(string airportId, string label)
    {
        var byAirport = RoomActiveRunways().ToDictionary(entry => entry.Key, entry => (List<string>?)[.. entry.Value], StringComparer.Ordinal);
        var warnings = new List<string>();
        ActiveRunways runways = ActiveRunwayListParser.FromTokenLists(byAirport, label, warnings);
        foreach (string warning in warnings)
        {
            _log.LogWarning("{Warning}", warning);
        }

        return runways.For(airportId);
    }

    /// <summary>The runway number of an end (<c>09L</c> → 9); <see cref="int.MaxValue"/> for an end that starts with none.</summary>
    private static int RunwayNumber(string end)
    {
        List<char> digits = [.. end.TakeWhile(char.IsAsciiDigit)];
        return (digits.Count > 0) ? digits.Aggregate(0, (number, digit) => (number * 10) + (digit - '0')) : int.MaxValue;
    }

    /// <summary>
    /// The end whose side <paramref name="bar"/> sits at when it is a full-length crossing of <paramref name="runway"/>:
    /// projected onto the centerline, within <see cref="FullLengthWindowFt"/> of the runway's outermost hold short at that
    /// end. Null for a crossing between the two, or when the layout carries no geometry for the runway.
    /// </summary>
    private string? FullLengthEnd(AirportGroundLayout layout, RunwayIdentifier runway, GroundNode bar)
    {
        if (RunwayEndGeometry(runway, runway.End1) is not { } end1)
        {
            return null;
        }

        double AlongFt(GroundNode node) =>
            GeoMath.AlongTrackDistanceNm(node.Position.Lat, node.Position.Lon, end1.Threshold.Lat, end1.Threshold.Lon, end1.Heading)
            * GeoMath.FeetPerNm;

        List<double> along =
        [
            .. layout.Nodes.Values.Where(n => (n.Type == GroundNodeType.RunwayHoldShort) && (n.RunwayId is { } id) && (id == runway)).Select(AlongFt),
        ];
        double barFt = AlongFt(bar);
        if (barFt <= along.Min() + FullLengthWindowFt)
        {
            return runway.End1;
        }

        return (barFt >= along.Max() - FullLengthWindowFt) ? runway.End2 : null;
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

    /// <summary>How far past the first hold short from a threshold, along the runway, another still counts as a full-length one.</summary>
    private const double FullLengthWindowFt = 300;

    /// <summary>
    /// The <c>RunwayHoldShort</c> node of <paramref name="runwayName"/> (e.g. <c>"28R/10L"</c>) with the smallest
    /// great-circle distance to <paramref name="point"/>; null when no layout is loaded or the runway has none.
    /// </summary>
    public int? FindHoldShortNodeNearestPoint(string runwayName, LatLon point) =>
        HoldShortNodesOf(RunwayIdentifier.Parse(runwayName)).MinBy(n => GeoMath.DistanceNm(point.Lat, point.Lon, n.Latitude, n.Longitude))?.Id;

    /// <summary>
    /// The full-length hold short for a departure from <paramref name="runwayEnd"/> of <paramref name="runwayName"/>:
    /// among the runway's <c>RunwayHoldShort</c> nodes whose along-track distance from that end's threshold is within
    /// <see cref="FullLengthWindowFt"/> of the smallest (the hold shorts either side of the runway at that end), the one
    /// with the lowest-cost route from <paramref name="ac"/>'s nearest node. Null when no layout is loaded, the runway or
    /// end is not in it, or no such node is reachable.
    /// </summary>
    public int? FindFullLengthHoldShortNode(AircraftModel ac, string runwayName, string runwayEnd)
    {
        var runway = RunwayIdentifier.Parse(runwayName);
        if ((GetAircraftNearestNodeId(ac) is not { } fromNodeId) || (RunwayEndGeometry(runway, runwayEnd) is not { } end))
        {
            return null;
        }

        List<(GroundNodeDto Node, double AlongNm)> along =
        [
            .. HoldShortNodesOf(runway)
                .Select(n => (n, GeoMath.AlongTrackDistanceNm(n.Latitude, n.Longitude, end.Threshold.Lat, end.Threshold.Lon, end.Heading))),
        ];
        if (along.Count == 0)
        {
            return null;
        }

        double windowNm = along.Min(a => a.AlongNm) + (FullLengthWindowFt / GeoMath.FeetPerNm);
        int? bestNodeId = null;
        double bestCostNm = double.MaxValue;
        foreach ((GroundNodeDto node, double alongNm) in along)
        {
            if ((alongNm <= windowNm) && (RouteCostNm(ac, fromNodeId, node.Id) is { } costNm) && (costNm < bestCostNm))
            {
                bestCostNm = costNm;
                bestNodeId = node.Id;
            }
        }

        return bestNodeId;
    }

    /// <summary>The layout's <c>RunwayHoldShort</c> nodes for <paramref name="runway"/> (either end); none when no layout is loaded.</summary>
    private IEnumerable<GroundNodeDto> HoldShortNodesOf(RunwayIdentifier runway) =>
        Layout?.Nodes.Where(n => (n.Type == "RunwayHoldShort") && (n.RunwayId is { } id) && (RunwayIdentifier.Parse(id) == runway)) ?? [];

    /// <summary>
    /// <paramref name="runwayEnd"/>'s threshold on <paramref name="runway"/> and the true heading from it toward the other
    /// end, from the runway's coordinates (<see cref="RunwayCenterline"/>); null when the runway or the end is not in the
    /// layout.
    /// </summary>
    private (LatLon Threshold, TrueHeading Heading)? RunwayEndGeometry(RunwayIdentifier runway, string runwayEnd)
    {
        if (RunwayCenterline(runway) is not { } line)
        {
            return null;
        }

        string end = RunwayIdentifier.NormalizeDesignator(runwayEnd);
        if (string.Equals(line.Ids.End1, end, StringComparison.OrdinalIgnoreCase))
        {
            return (line.First, new TrueHeading(GeoMath.BearingTo(line.First, line.Last)));
        }

        if (string.Equals(line.Ids.End2, end, StringComparison.OrdinalIgnoreCase))
        {
            return (line.Last, new TrueHeading(GeoMath.BearingTo(line.Last, line.First)));
        }

        return null;
    }

    /// <summary>
    /// <paramref name="runway"/>'s ends in its own order and the pavement ends its coordinates run between: from the domain
    /// layout's runways when it carries them (a layout parsed from GeoJSON), else from the server's layout DTO. Null when
    /// neither has the runway's coordinates.
    /// </summary>
    private (RunwayIdentifier Ids, LatLon First, LatLon Last)? RunwayCenterline(RunwayIdentifier runway)
    {
        if (_domainLayout?.Runways.FirstOrDefault(r => (r.Coordinates.Count >= 2) && (r.Id == runway)) is { } domain)
        {
            ((double lat1, double lon1), (double lat2, double lon2)) = (domain.Coordinates[0], domain.Coordinates[^1]);
            return (domain.Id, new LatLon(lat1, lon1), new LatLon(lat2, lon2));
        }

        GroundRunwayDto? dto = Layout?.Runways?.FirstOrDefault(r => (r.Coordinates.Count >= 2) && (RunwayIdentifier.Parse(r.Name) == runway));
        if (dto is null)
        {
            return null;
        }

        (double[] first, double[] last) = (dto.Coordinates[0], dto.Coordinates[^1]);
        return (RunwayIdentifier.Parse(dto.Name), new LatLon(first[0], first[1]), new LatLon(last[0], last[1]));
    }

    /// <summary>
    /// The length in nautical miles of the lowest-cost route from <paramref name="fromNodeId"/> to
    /// <paramref name="toNodeId"/> for <paramref name="ac"/>; null when no layout is loaded or no route exists.
    /// </summary>
    private double? RouteCostNm(AircraftModel ac, int fromNodeId, int toNodeId)
    {
        if (_domainLayout is null)
        {
            return null;
        }

        TaxiRoute? route = TaxiPathfinder.FindRoute(_domainLayout, fromNodeId, toNodeId, CategoryFor(ac), WakeClassFor(ac));
        if (route is null)
        {
            return null;
        }

        double costNm = 0;
        foreach (TaxiRouteSegment seg in route.Segments)
        {
            costNm += seg.Edge.DistanceNm;
        }

        return costNm;
    }

    /// <summary>
    /// The taxiway a <c>RunwayHoldShort</c> node sits on: the name of its edge that leads off the runway — not along a
    /// runway centerline, toward a node with no runway-centerline edge — else of any edge not along a centerline, the
    /// ordinal-first name when several qualify, so the answer does not depend on edge order. Null when the node is not in
    /// the layout or no such edge is named.
    /// </summary>
    public string? GetHoldShortTaxiwayName(int nodeId)
    {
        if ((_domainLayout is null) || !_domainLayout.Nodes.TryGetValue(nodeId, out GroundNode? node))
        {
            return null;
        }

        List<IGroundEdge> offRunway = [.. node.Edges.Where(e => !e.IsRunwayCenterline && !string.IsNullOrEmpty(e.TaxiwayName))];
        List<IGroundEdge> leadingOff = [.. offRunway.Where(e => !e.OtherNode(node).Edges.Any(other => other.IsRunwayCenterline))];
        return (leadingOff.Count > 0 ? leadingOff : offRunway).Select(e => e.TaxiwayName).Order(StringComparer.Ordinal).FirstOrDefault();
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

            if (RouteCostNm(ac, fromNodeId.Value, node.Id) is not { } costNm)
            {
                continue;
            }

            if (costNm < bestCostNm)
            {
                bestCostNm = costNm;
                bestNodeId = node.Id;
            }
        }

        return bestNodeId;
    }

    /// <summary>
    /// The position along <paramref name="route"/> (<see cref="RouteNodeId"/>) of the node <paramref name="holdShort"/>
    /// binds: the first that is a bar of the target runway or meets the target taxiway, a located target (C@J) only on a
    /// node of its location taxiway — the node walk the server falls back on when the route holds no point for the target
    /// yet, so the row shows the crossing the command will arm. The walk starts at <paramref name="firstPosition"/>
    /// (<see cref="RouteStart"/>): the node the route starts at counts only while the aircraft is short of it. Null when
    /// it binds none.
    /// </summary>
    private static int? HoldShortBindIndex(AirportGroundLayout layout, TaxiRoute route, HoldShortTarget holdShort, int firstPosition)
    {
        for (int position = firstPosition; position <= route.Segments.Count; position++)
        {
            if (!layout.Nodes.TryGetValue(RouteNodeId(route, position), out GroundNode? node))
            {
                continue;
            }

            if ((holdShort.OnTaxiway is { } onTaxiway) && !node.Edges.Any(e => e.MatchesTaxiway(onTaxiway)))
            {
                continue;
            }

            bool isTargetBar = (node.Type == GroundNodeType.RunwayHoldShort) && (node.RunwayId is { } runway) && runway.Contains(holdShort.Target);
            if (isTargetBar || node.Edges.Any(e => e.MatchesTaxiway(holdShort.Target)))
            {
                return position;
            }
        }

        return null;
    }

    internal TaxiRoute? ResolveRemainingRoute(AircraftModel ac)
    {
        if ((_domainLayout is not { } layout) || (RemainingRouteRequestFor(layout, ac) is not { } request))
        {
            return null;
        }

        // The server's start: on the taxiway the aircraft is on (the endpoint ahead of a straight edge or fillet arc it
        // stands mid-way along), else the heading-aligned node, else the nearest node.
        GroundNode? start = layout.FindNearestNodeForTaxi(ac.Position, ac.Heading) ?? layout.FindNearestNode(ac.Position);
        return (start is null) ? null : ResolveRemainingRouteFrom(ac, request, start);
    }

    /// <summary>
    /// <see cref="ResolveRemainingRoute"/> from the server's start node: the route from <paramref name="start"/>, or from
    /// the node the simulation's turn about turns the aircraft toward (<see cref="TurnAboutStart"/>), recovered when it
    /// resolves none.
    /// </summary>
    private TaxiRoute? ResolveRemainingRouteFrom(AircraftModel ac, RemainingRouteRequest request, GroundNode start)
    {
        (start, bool turnedAbout) = TurnAboutStart(ac, start);
        TaxiRoute? route = request.ResolveFrom(start, out PathfindingFailure? failure);
        if ((route is not null) && (ResolvedRemainingRoute(ac, request, route, turnedAbout) is { } resolved))
        {
            return resolved;
        }

        return RecoveredRemainingRoute(ac, request, start, route, failure);
    }

    /// <summary>
    /// What <see cref="ResolveRemainingRoute"/> rebuilds an aircraft's route from: the clearance's taxiways from the one
    /// it is on, the destination and the pathfinder options the server used. Null when the clearance names no taxiway.
    /// </summary>
    private static RemainingRouteRequest? RemainingRouteRequestFor(AirportGroundLayout layout, AircraftModel ac)
    {
        List<string> routeTaxiways = ParseRouteTaxiways(ac.TaxiRoute);
        if (routeTaxiways.Count == 0)
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
        GroundNode? destination = FindTaxiDestinationNode(layout, ac.TaxiDestination);
        return new RemainingRouteRequest
        {
            Layout = layout,
            RouteTaxiways = routeTaxiways,
            Destination = destination,
            Options = new ExplicitPathOptions
            {
                OccupiedTaxiway = null,
                DestinationRunway = string.IsNullOrEmpty(ac.AssignedRunway) ? null : ac.AssignedRunway,
                DestinationHintNode = destination,
                StartHeadingTrue = ac.Heading.Degrees,
            },
            Category = CategoryFor(ac),
            WakeClass = WakeClassFor(ac),
        };
    }

    /// <summary>
    /// The overlay for a <paramref name="route"/> that resolved from the start (or from the other end, when
    /// <paramref name="turnedAbout"/>): its spot line-up, else the route itself unless it starts with a reversal the
    /// recoveries in <see cref="RecoveredRemainingRoute"/> replace. A turn about starts behind the aircraft by design, so it
    /// is no reversal to replace. Null when the route starts with a reversal.
    /// </summary>
    private TaxiRoute? ResolvedRemainingRoute(AircraftModel ac, RemainingRouteRequest request, TaxiRoute route, bool turnedAbout)
    {
        // The server lines a spot cleared from the ramp up to leave it before anything else looks at the route's
        // shape, and a line-up's resolved route typically starts back up the lane behind the aircraft — so it comes
        // ahead of the reversal check, exactly as the server applies it.
        if (SpotLineUp(ac, request, route, out TaxiRoute improved) is { } lineUp)
        {
            return lineUp;
        }

        return (turnedAbout || !StartsWithReversal(route, ac.Heading)) ? WithApproachLeg(improved, ac.Position, ac.Heading) : null;
    }

    /// <summary>
    /// The server's spot line-up of <paramref name="route"/>, or null when the destination is no spot or the plan
    /// declines. <paramref name="improved"/> is the route the overlay draws otherwise: <paramref name="route"/>, or the
    /// server's cut across the apron to the stand when it has one.
    /// </summary>
    private TaxiRoute? SpotLineUp(AircraftModel ac, RemainingRouteRequest request, TaxiRoute route, out TaxiRoute improved)
    {
        improved = route;
        if (request.Destination is not { } destination)
        {
            return null;
        }

        // The server replaces a route that reaches the stand only the long way round (SFO $5A: down T5, out to Alpha,
        // back up T5A) with a drive across the apron that rolls in on the stand heading, so the overlay has to make the
        // same substitution or it draws a detour the aircraft is not flying.
        double aircraftLengthFt = AircraftLength.ResolveFt(ac.AircraftType);
        improved = RampLaneReposition.TryPlanResolvedRouteCut(request.Layout, route, destination, aircraftLengthFt)?.Route ?? route;
        return TryClientSpotLineUp(ac, improved, destination, ParseRouteTaxiways(ac.TaxiRoute), request.Category);
    }

    /// <summary>
    /// The overlay when the route from <paramref name="start"/> resolved none, or starts with a reversal: the server's
    /// ramp-lane cut at either end, else the route as resolved.
    /// </summary>
    private TaxiRoute? RecoveredRemainingRoute(
        AircraftModel ac,
        RemainingRouteRequest request,
        GroundNode start,
        TaxiRoute? route,
        PathfindingFailure? failure
    )
    {
        // While the pilot cuts across a ramp onto a parallel lane the map does not connect (SFO M3 → M4),
        // the nearest graph node is still on the old lane and the named route does not resolve from it.
        // Reconstruct the same free-space leg the server planned so the overlay follows the crossing.
        if ((failure is not null) && (RampLaneReposition.TryPlan(request.Layout, request.RepositionFor(ac), failure) is { } plan))
        {
            return plan.Route;
        }

        // The destination-end twin (OAK TE → TC for @22): the cleared lane's ramp end does not join the stand's
        // lane, so the server planned a cut from the lane across the apron. From the aircraft's own end of the
        // lane the graph may still "reach" the stand only by doubling back down the lane — a route no pilot
        // taxis — so a rebuilt route that starts with a reversal is replaced by the cut when one exists.
        if (
            (request.Destination is { } destination)
            && (RampLaneReposition.TryPlanDestinationCut(request.Layout, request.DestinationCutFor(ac, start, destination)) is { } cut)
        )
        {
            return cut.Route;
        }

        return WithApproachLeg(route, ac.Position, ac.Heading);
    }

    /// <summary>
    /// What <see cref="ResolveRemainingRoute"/> rebuilds a route from: the clearance's taxiways from the one the aircraft
    /// is on, its destination node (a stand or spot) and the pathfinder options, category and wake class the server used.
    /// </summary>
    private sealed record RemainingRouteRequest
    {
        public required AirportGroundLayout Layout { get; init; }

        public required List<string> RouteTaxiways { get; init; }

        public required GroundNode? Destination { get; init; }

        public required ExplicitPathOptions Options { get; init; }

        public required AircraftCategory Category { get; init; }

        public required WakeTurbulenceData.WakeClass WakeClass { get; init; }

        /// <summary>The clearance resolved from <paramref name="from"/>, or null with the pathfinder's failure.</summary>
        public TaxiRoute? ResolveFrom(GroundNode from, out PathfindingFailure? failure) =>
            TaxiPathfinder.ResolveExplicitPathDetailed(Layout, from.Id, RouteTaxiways, out failure, Options, Category, WakeClass);

        /// <summary>The server's ramp-lane reposition request for <paramref name="ac"/>.</summary>
        public RampLaneRepositionRequest RepositionFor(AircraftModel ac) =>
            new()
            {
                Position = ac.Position,
                Heading = ac.Heading,
                CurrentTaxiway = ac.CurrentTaxiway,
                Path = RouteTaxiways,
                Options = Options,
                Category = Category,
                WakeClass = WakeClass,
            };

        /// <summary>The server's destination-end cut request for <paramref name="ac"/> from <paramref name="start"/>.</summary>
        public RampLaneDestinationCutRequest DestinationCutFor(AircraftModel ac, GroundNode start, GroundNode destination) =>
            new()
            {
                StartNodeId = start.Id,
                Path = RouteTaxiways,
                Destination = destination,
                Options = Options,
                Category = Category,
                WakeClass = WakeClass,
                AircraftLengthFt = AircraftLength.ResolveFt(ac.AircraftType),
            };
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
            WakeClass = WakeClassFor(ac),
            AircraftLengthFt = AircraftLength.ResolveFt(ac.AircraftType),
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

    /// <summary>
    /// Where the overlay resolves the route from, and whether that route opens with the turn about the simulation sent
    /// (<see cref="AircraftModel.TaxiTurnAboutShape"/>), which starts behind the aircraft by design. The overlay draws
    /// exactly the shape it is sent and reads neither shape off the heading: both are placed by the sent target node
    /// (<see cref="AircraftModel.TaxiTurnAboutTargetNodeId"/>) and the straight edge the aircraft stands mid-way along
    /// (<see cref="AirportGroundLayout.FindMidEdgeTaxiStart"/>), which ends at that target while the turn about is still
    /// ahead of it. <see cref="TaxiTurnAboutShape.FromFarEnd"/> resolves from the target;
    /// <see cref="TaxiTurnAboutShape.InPlace"/> resolves from that edge's other end, the node ahead whose route reverses
    /// over the edge. Otherwise, with no turn about sent, or once the aircraft is off that edge, it resolves from
    /// <paramref name="start"/> with no turn about. That edge check passes on any straight edge ending at the target,
    /// segment 0's included; the simulation stops sending the shape once the aircraft has passed the target. A target the
    /// layout lacks is warned of once per callsign and value, then logged at Debug.
    /// </summary>
    private (GroundNode Start, bool TurnedAbout) TurnAboutStart(AircraftModel ac, GroundNode start)
    {
        if ((ac.TaxiTurnAboutShape == TaxiTurnAboutShape.None) || (_domainLayout is not { } layout))
        {
            return (start, false);
        }

        if ((ac.TaxiTurnAboutTargetNodeId is not { } targetId) || (layout.Nodes.GetValueOrDefault(targetId) is not { } target))
        {
            LogTurnAboutTargetMissing(ac, layout, start);
            return (start, false);
        }

        if ((layout.FindMidEdgeTaxiStart(ac.Position) is not { } occupied) || !occupied.Nodes.Contains(target))
        {
            return (start, false);
        }

        return (ac.TaxiTurnAboutShape == TaxiTurnAboutShape.FromFarEnd) ? (target, true) : (occupied.OtherNode(target), true);
    }

    /// <summary>
    /// Logs a turn about sent toward a node the layout lacks: a warning the first time for the aircraft and the sent shape
    /// and target, at Debug on every later draw of it.
    /// </summary>
    private void LogTurnAboutTargetMissing(AircraftModel ac, AirportGroundLayout layout, GroundNode start)
    {
        LogLevel level = _warnedTurnAboutTargets.Add((ac.Callsign, ac.TaxiTurnAboutShape, ac.TaxiTurnAboutTargetNodeId))
            ? LogLevel.Warning
            : LogLevel.Debug;
        _log.Log(
            level,
            "{Callsign}: turn about {Shape} toward node {Target}, which the {Airport} layout lacks; drawing the route from node {Start}",
            ac.Callsign,
            ac.TaxiTurnAboutShape,
            ac.TaxiTurnAboutTargetNodeId,
            layout.AirportId,
            start.Id
        );
    }

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

        TaxiRoute? subRoute = FindRouteToNode(
            _drawWaypointIds[^1],
            nodeId,
            _drawAircraft is { } da ? CategoryFor(da) : AircraftCategory.Jet,
            _drawAircraft is { } dw ? WakeClassFor(dw) : WakeTurbulenceData.WakeClass.Large
        );
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

        DrawHoverPreview = FindRouteToNode(
            _drawWaypointIds[^1],
            nodeId.Value,
            _drawAircraft is { } da ? CategoryFor(da) : AircraftCategory.Jet,
            _drawAircraft is { } dw ? WakeClassFor(dw) : WakeTurbulenceData.WakeClass.Large
        );
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

        DropLastTargetFacing();
        _drawWaypointIds.Add(nodeId);
        PublishPushWaypoints();
        RefreshPushRoutePreview();
        return true;
    }

    /// <summary>
    /// Clears the facing of the current last target when it is a marked point, before another target is appended
    /// after it: only the last point of a <c>PUSHM</c> takes a facing. Undoing the appended target does not restore it.
    /// </summary>
    private void DropLastTargetFacing()
    {
        int last = _drawWaypointIds.Count - 1;
        if (_pushFreePoses.TryGetValue(last, out PushFreePose? pose) && (pose.Facing is not null))
        {
            _pushFreePoses[last] = pose with { Facing = null };
        }
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

        DropLastTargetFacing();
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

    /// <summary>
    /// Moves a push-route target to where its marker was dragged and re-plans the preview. A marked point moves to
    /// <paramref name="to"/>, kept as the command will carry it (six decimals), with its facing; a node target (a spot, a
    /// stand or a plain node) snaps to the layout node nearest <paramref name="to"/>. Either keeps its forced kind.
    /// Ignored outside push-draw mode, for the start (index 0), for an index past the last point, and when the move leaves
    /// the target where it was or makes it the same point as the one before or after it.
    /// </summary>
    /// <param name="waypointIndex">The target's index in <see cref="DrawWaypoints"/>.</param>
    /// <param name="to">Where the marker was released.</param>
    public void MovePushTarget(int waypointIndex, LatLon to)
    {
        if ((_drawKind != DrawRouteKind.Push) || (waypointIndex < 1) || (waypointIndex >= _drawWaypointIds.Count))
        {
            return;
        }

        if ((MovedPushTarget(waypointIndex, to) is not { } moved) || !IsAcceptedPushTargetMove(waypointIndex, moved.Id))
        {
            return;
        }

        _drawWaypointIds[waypointIndex] = moved.Id;
        if (moved.Pose is not null)
        {
            _pushFreePoses[waypointIndex] = moved.Pose;
        }

        PublishPushWaypoints();
        RefreshPushRoutePreview();
    }

    /// <summary>
    /// Where a dragged target lands: a marked point at <paramref name="to"/> with its facing, or a node target at the
    /// layout node nearest <paramref name="to"/>, of any kind. Null when a node target has no layout to snap to.
    /// </summary>
    private (int Id, PushFreePose? Pose)? MovedPushTarget(int waypointIndex, LatLon to)
    {
        if (_pushFreePoses.TryGetValue(waypointIndex, out PushFreePose? pose))
        {
            PushFreePose moved = pose with { Latitude = Math.Round(to.Lat, 6), Longitude = Math.Round(to.Lon, 6) };
            return (VirtualNode.Create(moved.Latitude, moved.Longitude).Id, moved);
        }

        GroundNode? nearest = _domainLayout?.Nodes.Values.MinBy(node => GeoMath.DistanceNm(to, node.Position));
        return nearest is null ? null : (nearest.Id, null);
    }

    /// <summary>
    /// False when the moved target would be the point it already is, or the same point as the one before it or after it.
    /// </summary>
    private bool IsAcceptedPushTargetMove(int waypointIndex, int movedId)
    {
        bool sameAsNext = (waypointIndex + 1 < _drawWaypointIds.Count) && (_drawWaypointIds[waypointIndex + 1] == movedId);
        return (movedId != _drawWaypointIds[waypointIndex]) && (movedId != _drawWaypointIds[waypointIndex - 1]) && !sameAsNext;
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

        (PushRoutePreview, PushRouteRefusal) = PlanPushMove(_domainLayout, _drawAircraft, targets);
    }

    /// <summary>
    /// Whether the tug planner finds a move for <paramref name="aircraft"/> from where it stands to node
    /// <paramref name="nodeId"/> as the one target — the plan Push route… would preview after a click there. False when
    /// the node is not in the layout, the plan is refused, or the planner throws (logged).
    /// </summary>
    public bool CanPushRouteTo(AircraftModel aircraft, int nodeId)
    {
        if ((_domainLayout is null) || !_domainLayout.Nodes.TryGetValue(nodeId, out GroundNode? node))
        {
            return false;
        }

        try
        {
            return PlanPushMove(_domainLayout, aircraft, [PushTargetFor(node, null)]).Plan is not null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Push route gate: planning a tug move for {Callsign} to node {NodeId} threw, so Push route… is not offered",
                aircraft.Callsign,
                nodeId
            );
            return false;
        }
    }

    /// <summary>
    /// Plans a tug move for <paramref name="aircraft"/> through <paramref name="targets"/>: resolves their goals
    /// (<see cref="ResolvePushPreviewGoals"/>), then plans from where the aircraft stands (<see cref="PlanPushPreview"/>).
    /// </summary>
    /// <returns>The plan and no refusal, or no plan and the refusal.</returns>
    private (TugPlan? Plan, string? Refusal) PlanPushMove(AirportGroundLayout layout, AircraftModel aircraft, List<PushDestination> targets)
    {
        (List<TugGoal>? goals, double? finalFacingTrueDeg, string? goalRefusal) = ResolvePushPreviewGoals(layout, aircraft, targets);
        if (goals is null)
        {
            return (null, goalRefusal);
        }

        // The neighbours are chosen by the same body the simulation plans the executed move with, from the aircraft
        // the server's world holds, so a push the server would refuse for a neighbour shows that refusal here.
        TugNeighbourCandidate subject = TugCandidateOf(aircraft);
        List<TugNeighbourCandidate> others = ServerWorldCandidates();
        var request = new TugRequest
        {
            Start = new TugPose(aircraft.Position, aircraft.Heading.Degrees),
            StartsAtStand = aircraft.CurrentPhase == "At Parking",
            AircraftType = aircraft.AircraftType,
            Goals = goals,
            ParkedNeighbours = TugParkedNeighbours.Build(subject, others),
            FinalFacingTrueDeg = finalFacingTrueDeg,
            PreviousKind = null,
            Forced = false,
        };

        return PlanPushPreview(layout, request, subject, others);
    }

    /// <summary>
    /// The goals the drawn targets resolve to. A single spot or stand is sent as plain <c>PUSH $spot</c> /
    /// <c>PUSH @stand</c>, so it resolves through the server's own resolver for those forms: the preview and the server
    /// share one body and cannot drift apart, refusal wording included (the draw gestures give such a target no facing,
    /// so none is previewed). Otherwise each target resolves as the sim's
    /// <c>PUSHM</c> resolves its legs: a marked point minted where it lies (facing converted at the aircraft), every
    /// other target found in the layout, then the leg's forced kind applied.
    /// </summary>
    /// <returns>The goals and the facing to end on, or no goals and the refusal.</returns>
    private static (List<TugGoal>? Goals, double? FinalFacingTrueDeg, string? Refusal) ResolvePushPreviewGoals(
        AirportGroundLayout layout,
        AircraftModel aircraft,
        List<PushDestination> targets
    )
    {
        if ((targets.Count == 1) && ((targets[0].Spot is not null) || (targets[0].Parking is not null)))
        {
            StandOrSpotGoal single = GroundCommandHandler.ResolveStandOrSpotGoal(aircraft.Position, null, null, targets[0], layout);
            return single.Goal is { } singleGoal ? ([singleGoal], single.FinalFacingTrueDeg, null) : (null, null, single.Refusal);
        }

        var goals = new List<TugGoal>(targets.Count);
        for (int i = 0; i < targets.Count; i++)
        {
            PushDestination target = targets[i];
            TugGoal? goal = target.FreePose is { } pose
                ? GroundCommandHandler.ResolveMarkedPointGoal(pose, null, aircraft.Position, GroundCommandHandler.MarkedPointLabel(targets, i))
                : GroundCommandHandler.ResolveTugGoal(layout, target.Token);
            if (goal is null)
            {
                return (null, null, $"Cannot find {target.Token}");
            }

            goals.Add(goal with { ForcedKind = target.ForcedKind });
        }

        return (goals, null, null);
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
