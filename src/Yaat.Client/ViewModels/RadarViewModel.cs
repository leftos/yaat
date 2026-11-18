using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using Yaat.Client.ContextMenus;
using Yaat.Client.Logging;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Sim;
using Yaat.Sim.Data;

namespace Yaat.Client.ViewModels;

public enum DcbMenuMode
{
    Main,
    Aux,
    Brite,
}

public enum BriteTarget
{
    Dcb,
    Bkc,
    MapA,
    MapB,
    Fdb,
    Lst,
    Pos,
    Ldb,
    Oth,
    Tls,
    RangeRing,
    Cmp,
    Bcn,
    Pri,
    Hst,
    Wx,
    Wxc,
}

public partial class RadarViewModel(
    ServerConnection connection,
    VideoMapService videoMapService,
    Func<string, string, string, Task> sendCommand,
    Action<AircraftModel?>? onSelectionChanged = null
) : ObservableObject
{
    public static readonly double[] RangeRingSizeSteps = [2, 5, 10, 15, 20];

    private readonly ILogger _log = AppLog.CreateLogger<RadarViewModel>();

    private readonly ServerConnection _connection = connection;
    private readonly VideoMapService _videoMapService = videoMapService;
    private readonly Func<string, string, string, Task> _sendCommand = sendCommand;
    private readonly Action<AircraftModel?>? _onSelectionChanged = onSelectionChanged;

    private string? _activeScenarioId;
    private string? _activeArtccId;
    private string? _activeAirportId;
    private UserPreferences? _preferences;
    private Func<string, double?>? _getAirportElevation;
    private Func<string, AircraftModel?>? _findAircraft;
    private bool _navDbReady;

    /// <summary>
    /// Suffix appended to the active scenario id when this view reads or writes its per-scenario settings,
    /// so an extra Radar View window ("#2") keeps its own center, range, maps and filters. Empty for the
    /// docked primary view, whose key stays the bare scenario id.
    /// </summary>
    public string SettingsKeySuffix { get; init; } = "";

    /// <summary>
    /// False for an extra Radar View window. Every global (non-per-scenario) preference write is gated on
    /// this so an extra window's toggles never rewrite the primary view's app-wide defaults.
    /// </summary>
    public bool IsPrimary { get; init; } = true;

    public string? PrimaryAirportId { get; private set; }

    /// <summary>
    /// Airport IDs from the active position's STARS area. Used to filter
    /// which METARs (wind/altimeter) to display on the radar.
    /// </summary>
    public List<string> WeatherAirports { get; private set; } = [];

    /// <summary>
    /// Position-scoped mapGroup mapIds, set by <see cref="ApplyPositionDisplayConfig"/>.
    /// Used by <see cref="ApplyVideoMapsDto"/> when it loads later to pick the right group.
    /// </summary>
    private List<int?>? _positionMapGroupMapIds;

    private LatLon _primaryAirportPosition;

    /// <summary>
    /// The ARTCC area centre <see cref="ApplyVideoMapsDto"/> fell back to because the primary airport's position was not
    /// known yet; null when it centred on the airport. <see cref="RecenterOnPrimaryAirport"/> moves the scope only while
    /// it is still there.
    /// </summary>
    private LatLon? _areaFallbackCenter;

    /// <summary>The range a video-map load sets the scope to.</summary>
    private const double DefaultRangeNm = 40;

    [ObservableProperty]
    private double _rangeNm = DefaultRangeNm;

    /// <summary>The map cue a pointed or focused menu row shows (<see cref="IMenuHost.SetMapCue"/>); null for none.</summary>
    [ObservableProperty]
    private MapCue? _menuCue;

    /// <summary>
    /// The facility whose video maps the radar loaded (<see cref="FacilityVideoMapsDto.FacilityId"/>): every MVA readout on this
    /// radar (altitude tint, Ctrl-hover, point menu) prefers that facility's chart where MVA charts overlap
    /// (<see cref="Yaat.Sim.Data.Mva.MvaDatabase.FindSector"/>). Null until video maps load and after <see cref="ClearVideoMaps"/>.
    /// </summary>
    [ObservableProperty]
    private string? _mvaFacilityId;

    [ObservableProperty]
    private double _centerLat;

    [ObservableProperty]
    private double _centerLon;

    [ObservableProperty]
    private bool _showRangeRings = true;

    [ObservableProperty]
    private bool _showFixes;

    /// <summary>
    /// Live MVA-altitude-tint toggle. Session state, not persisted: each scenario load resets it to the
    /// student position type's default (<see cref="Services.UserPreferences.GetMvaHintDefault"/>); the DCB
    /// MVA button flips it for the current session.
    /// </summary>
    [ObservableProperty]
    private bool _showMvaHints;

    /// <summary>
    /// Opt-in datablock deconfliction mode for this radar view. Persisted globally
    /// (<see cref="Services.UserPreferences.RadarDeconflictMode"/>); cycled by the DCB DCNF button.
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

    /// <summary>
    /// Whether the DCB (Display Control Bar) is shown. Toggled by Ctrl+F8 and persisted globally
    /// (<see cref="Services.UserPreferences.RadarDcbVisible"/>), mirroring CRC's <c>IsDcbVisible</c>.
    /// </summary>
    [ObservableProperty]
    private bool _isDcbVisible = true;

    [ObservableProperty]
    private AircraftModel? _selectedAircraft;

    [ObservableProperty]
    private IReadOnlySet<string>? _programmedFixNames;

    [ObservableProperty]
    private IReadOnlyList<WeatherDisplayInfo>? _weatherInfo;

    /// <summary>Shown in place of the weather readout when the loaded weather has no METAR for this position's airports.</summary>
    [ObservableProperty]
    private string? _weatherNote;

    private readonly Dictionary<BriteTarget, int> _brightnessValues = new()
    {
        [BriteTarget.Dcb] = 100,
        [BriteTarget.Bkc] = 100,
        [BriteTarget.MapA] = 100,
        [BriteTarget.MapB] = 60,
        [BriteTarget.Fdb] = 100,
        [BriteTarget.Lst] = 100,
        [BriteTarget.Pos] = 100,
        [BriteTarget.Ldb] = 100,
        [BriteTarget.Oth] = 100,
        [BriteTarget.Tls] = 100,
        [BriteTarget.RangeRing] = 60,
        [BriteTarget.Cmp] = 100,
        [BriteTarget.Bcn] = 100,
        [BriteTarget.Pri] = 100,
        [BriteTarget.Hst] = 100,
        [BriteTarget.Wx] = 100,
        [BriteTarget.Wxc] = 100,
    };

    [ObservableProperty]
    private BriteTarget? _activeBriteTarget;

    public float MapBrightnessA => _brightnessValues[BriteTarget.MapA] / 100f;

    public float MapBrightnessB => _brightnessValues[BriteTarget.MapB] / 100f;

    public float RangeRingBrightness => _brightnessValues[BriteTarget.RangeRing] / 100f;

    [ObservableProperty]
    private IReadOnlyList<VideoMapData>? _activeVideoMaps;

    [ObservableProperty]
    private IReadOnlyList<(string Name, double Lat, double Lon)>? _fixes;

    /// <summary>Instructor scope-marker pins resolved to render positions (label + lat/lon). Backed by
    /// <see cref="_pinnedMarkerTokens"/> (fix/NAVAID names or FRD strings). Always drawn, regardless of
    /// the Show Fixes toggle.</summary>
    [ObservableProperty]
    private IReadOnlyList<(string Name, double Lat, double Lon)>? _pinnedMarkers;

    // Stored marker tokens (fix/NAVAID names or FRD strings, upper-cased), persisted per view.
    private readonly List<string> _pinnedMarkerTokens = [];

    [ObservableProperty]
    private bool _isPanZoomLocked;

    [ObservableProperty]
    private double _rangeRingCenterLat;

    [ObservableProperty]
    private double _rangeRingCenterLon;

    [ObservableProperty]
    private double _rangeRingSizeNm = 5;

    [ObservableProperty]
    private bool _isPlacingRangeRing;

    [ObservableProperty]
    private bool _isAdjustingRangeRingSize;

    [ObservableProperty]
    private string _mapSearchText = "";

    [ObservableProperty]
    private string _artccFavoriteMenuHeader = "Favorite for ARTCC";

    [ObservableProperty]
    private string _airportFavoriteMenuHeader = "Favorite for airport";

    [ObservableProperty]
    private bool _hasAirportFavoriteScope;

    [ObservableProperty]
    private bool _hasScenarioFavoriteScope;

    [ObservableProperty]
    private bool _showTopDown;

    [ObservableProperty]
    private double _ptlLengthMinutes = 0.5;

    [ObservableProperty]
    private bool _ptlOwn;

    [ObservableProperty]
    private bool _ptlAll;

    [ObservableProperty]
    private bool _isAdjustingPtlLength;

    [ObservableProperty]
    private int _historyCount;

    [ObservableProperty]
    private bool _isAdjustingHistory;

    [ObservableProperty]
    private DcbMenuMode _dcbMode = DcbMenuMode.Main;

    [ObservableProperty]
    private bool _isAdjustingRange;

    [ObservableProperty]
    private bool _isOffCenter;

    // Draw route mode state
    [ObservableProperty]
    private bool _isDrawingRoute;

    [ObservableProperty]
    private IReadOnlyList<DrawnWaypoint>? _drawnWaypoints;

    [ObservableProperty]
    private IReadOnlyDictionary<int, WaypointCondition>? _waypointConditionsSnapshot;

    [ObservableProperty]
    private IReadOnlyList<ShownPathEntry>? _shownPaths;

    [ObservableProperty]
    private IReadOnlyList<ShownShapeEntry>? _shownShapes;

    private DrawRouteState? _drawRoute;

    // --- Show nav route state ---
    private static readonly SKColor[] PathColors =
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
    /// Per-callsign datablock view state (manual offsets, highlights, minified choices). Owned here
    /// rather than by the canvas so it survives tab switches and pop-out/dock-back; every
    /// <see cref="Views.Radar.RadarCanvas"/> bound to this view-model shares it.
    /// </summary>
    public ScopeDataBlockViewState DataBlockState { get; } = new();

    /// <summary>
    /// Moves the callsign's datablock to a screen-pixel <paramref name="offset"/> from its target symbol, as dragging it does;
    /// every bound radar canvas repaints on <see cref="DataBlockViewState.ManualOffsetsChanged"/>.
    /// </summary>
    public void SetDataBlockOffset(string callsign, SKPoint offset) => DataBlockState.SetManualOffset(callsign, offset);

    /// <summary>Returns the callsign's datablock to its default placement.</summary>
    /// <returns>True when the datablock had a manual offset to remove.</returns>
    public bool ResetDataBlockOffset(string callsign) => DataBlockState.RemoveManualOffset(callsign);

    private readonly HashSet<string> _shownPathCallsigns = [];
    private readonly Dictionary<string, (IReadOnlyList<ShownPathEntry> Segments, string Fingerprint)> _pathCache = [];
    private readonly Dictionary<string, int> _pathColorIndices = [];
    private readonly Stack<int> _freeColorIndices = new();

    public ObservableCollection<VideoMapToggleItem> MapToggles { get; } = [];

    public ObservableCollection<MapShortcutItem> MapShortcuts { get; } = [];

    /// <summary>
    /// Brightness category by map ID ("A" or "B").
    /// </summary>
    public Dictionary<string, string> BrightnessLookup { get; } = [];

    // Test/tooling accessor: lets Yaat.GuideCapture poll for video-map download
    // completion before flipping a MapToggle.IsEnabled. Without this, enabling
    // a toggle before LoadMapsAsync finishes leaves UpdateActiveMaps with a
    // null cache hit and the map silently fails to draw.
    internal bool IsMapDataCached(string mapId) => _videoMapService.GetCached(mapId) is not null;

    /// <summary>
    /// Test-only hook: binds the view-model to a scenario id, the slice of
    /// <see cref="LoadVideoMapsForArtccAsync"/> that per-scenario settings key off, without the
    /// server round-trip that method needs.
    /// </summary>
    internal void SetScenarioIdForTesting(string? scenarioId) => _activeScenarioId = scenarioId;

    /// <summary>
    /// True once the latest <see cref="LoadVideoMapsForArtccAsync"/> attempt is over: it downloaded the maps and restored the
    /// scenario's saved radar settings over them, or it ended with no maps or failed, leaving nothing to restore. False while a
    /// load is in flight and after <see cref="ClearVideoMaps"/>; in flight the toggles may exist, but a map turned on, or a centre
    /// or range set, would be overwritten by the restore.
    /// </summary>
    public bool VideoMapsReady { get; private set; }

    /// <summary>Counts load starts and clears, so only the latest load attempt marks the maps ready when it ends.</summary>
    private int _videoMapLoadGeneration;

    /// <summary>Test-only hook: marks the maps and saved settings as applied, without the server round-trip a load needs.</summary>
    internal void SetVideoMapsReadyForTesting() => VideoMapsReady = true;

    public void SetPreferences(UserPreferences prefs)
    {
        _preferences = prefs;
        DeconflictMode = prefs.RadarDeconflictMode;
        IsDcbVisible = prefs.RadarDcbVisible;
    }

    public void SetAircraftLookup(Func<string, AircraftModel?> lookup) => _findAircraft = lookup;

    public void SetElevationLookup(Func<string, double?> lookup) => _getAirportElevation = lookup;

    public void SetNavDbReady()
    {
        _navDbReady = true;
        SetFixes(BuildVisibleFixes());
        RebuildPinnedMarkers();
    }

    partial void OnSelectedAircraftChanged(AircraftModel? value)
    {
        _onSelectionChanged?.Invoke(value);
        UpdateProgrammedFixes(value);
        if (IsDrawingRoute && (value is null || value.Callsign != _drawRoute?.Callsign))
        {
            CancelDrawRoute();
        }
    }

    partial void OnShowFixesChanged(bool value)
    {
        if (value)
        {
            UpdateProgrammedFixes(SelectedAircraft);
        }
        else
        {
            ProgrammedFixNames = null;
        }
    }

    private void UpdateProgrammedFixes(AircraftModel? ac)
    {
        if (!ShowFixes || ac is null)
        {
            ProgrammedFixNames = null;
            return;
        }

        ProgrammedFixNames = ProgrammedFixResolver.Resolve(
            ac.Route,
            ac.ExpectedApproach,
            ac.Destination,
            ac.Departure,
            null,
            ac.ActiveStarId,
            ac.DestinationRunway
        );
    }

    private IReadOnlyList<(string Name, double Lat, double Lon)> BuildVisibleFixes() => ScopeMapSource.BuildChartFixes(_navDbReady);

    // --- Scope-marker pins (CRC ".ff" / ".marker" / ".markers" / ".nomarkers") ---

    /// <summary>Toggles a fix/NAVAID or FRD marker (CRC <c>.ff</c>/<c>.marker</c> semantics): adds it if
    /// absent, removes it if already pinned. Returns false (no change) if the token can't be resolved to
    /// a location.</summary>
    public bool ToggleMarker(string token)
    {
        string normalized = token.Trim().ToUpperInvariant();
        if (normalized.Length == 0)
        {
            return false;
        }

        int existing = _pinnedMarkerTokens.FindIndex(t => t.Equals(normalized, StringComparison.Ordinal));
        if (existing >= 0)
        {
            _pinnedMarkerTokens.RemoveAt(existing);
            RebuildPinnedMarkers();
            return true;
        }

        if (!_navDbReady || FrdResolver.Resolve(normalized, NavigationDatabase.Instance) is null)
        {
            return false;
        }

        _pinnedMarkerTokens.Add(normalized);
        RebuildPinnedMarkers();
        return true;
    }

    /// <summary>Pins an already-resolved FRD string (right-click "Pin marker here"); no-op if already
    /// pinned. Unlike <see cref="ToggleMarker"/> this only ever adds.</summary>
    public void AddMarker(string frd)
    {
        string normalized = frd.Trim().ToUpperInvariant();
        if (normalized.Length == 0 || _pinnedMarkerTokens.Contains(normalized, StringComparer.Ordinal))
        {
            return;
        }

        _pinnedMarkerTokens.Add(normalized);
        RebuildPinnedMarkers();
    }

    /// <summary>Removes the pinned marker nearest <paramref name="lat"/>/<paramref name="lon"/> within
    /// <paramref name="maxNm"/>. Returns true if one was removed.</summary>
    public bool RemoveNearestMarker(double lat, double lon, double maxNm)
    {
        IReadOnlyList<(string Name, double Lat, double Lon)>? markers = PinnedMarkers;
        if (markers is null || markers.Count == 0)
        {
            return false;
        }

        int bestIndex = -1;
        double bestNm = maxNm;
        for (int i = 0; i < markers.Count; i++)
        {
            double nm = GeoMath.DistanceNm(new LatLon(lat, lon), new LatLon(markers[i].Lat, markers[i].Lon));
            if (nm <= bestNm)
            {
                bestNm = nm;
                bestIndex = i;
            }
        }

        if (bestIndex < 0)
        {
            return false;
        }

        _pinnedMarkerTokens.RemoveAt(bestIndex);
        RebuildPinnedMarkers();
        return true;
    }

    /// <summary>Removes all pinned markers (CRC <c>.nomarkers</c> / right-click "Clear pinned markers").</summary>
    public void ClearMarkers()
    {
        if (_pinnedMarkerTokens.Count == 0)
        {
            return;
        }

        _pinnedMarkerTokens.Clear();
        RebuildPinnedMarkers();
    }

    public bool HasMarkers => _pinnedMarkerTokens.Count > 0;

    private void RebuildPinnedMarkers()
    {
        if (!_navDbReady || _pinnedMarkerTokens.Count == 0)
        {
            PinnedMarkers = null;
            return;
        }

        var result = new List<(string, double, double)>(_pinnedMarkerTokens.Count);
        foreach (string token in _pinnedMarkerTokens)
        {
            LatLon? pos = FrdResolver.Resolve(token, NavigationDatabase.Instance);
            if (pos.HasValue)
            {
                result.Add((token, pos.Value.Lat, pos.Value.Lon));
            }
        }

        PinnedMarkers = result.Count > 0 ? result : null;
    }

    public void SetPrimaryAirportId(string? id) => PrimaryAirportId = id;

    /// <summary>
    /// Bootstraps radar state from a scenario activation event. Called from
    /// <see cref="MainViewModel.ApplyScenarioBootstrap"/> for all three paths
    /// that can activate a scenario (loader, other-clients broadcast, join-room).
    /// </summary>
    public void ApplyScenarioBootstrap(ScenarioBootstrap bootstrap, string? artccId)
    {
        SetPrimaryAirportId(bootstrap.PrimaryAirportId);

        if (!string.IsNullOrEmpty(artccId))
        {
            _ = LoadVideoMapsForArtccAsync(artccId, bootstrap.PrimaryAirportId, bootstrap.ScenarioId);
        }

        if (bootstrap.PositionDisplayConfig is { NoStarsDisplay: false })
        {
            ApplyPositionDisplayConfig(bootstrap.PositionDisplayConfig);
        }
    }

    public void SetPrimaryAirportPosition(double lat, double lon) => _primaryAirportPosition = new LatLon(lat, lon);

    /// <summary>
    /// Sets the primary airport's position once it becomes known late (the navigation data finished loading after the
    /// scenario bootstrap), and centres the scope on it as <see cref="ApplyVideoMapsDto"/> would have, but only while the
    /// scope is still on the area fallback centre at the default range: a scope the user panned or zoomed is left alone.
    /// </summary>
    public void RecenterOnPrimaryAirport(double lat, double lon)
    {
        SetPrimaryAirportPosition(lat, lon);
        if (!IsOnAreaFallback(out LatLon fallback))
        {
            return;
        }

        _areaFallbackCenter = null;
        CenterLat = lat;
        CenterLon = lon;
        if ((RangeRingCenterLat == fallback.Lat) && (RangeRingCenterLon == fallback.Lon))
        {
            RangeRingCenterLat = lat;
            RangeRingCenterLon = lon;
        }
    }

    /// <summary>Whether the scope is still where a video-map load's area fallback put it: same centre, default range.</summary>
    private bool IsOnAreaFallback(out LatLon fallback)
    {
        fallback = _areaFallbackCenter.GetValueOrDefault();
        return (_areaFallbackCenter is not null) && (CenterLat == fallback.Lat) && (CenterLon == fallback.Lon) && (RangeNm == DefaultRangeNm);
    }

    public double GetFieldElevation(string? destination) => ScopeMapSource.ResolveFieldElevation(_getAirportElevation, destination, PrimaryAirportId);

    public async Task LoadVideoMapsForArtccAsync(string artccId, string? airportId = null, string? scenarioId = null)
    {
        VideoMapsReady = false;
        int load = ++_videoMapLoadGeneration;
        try
        {
            FacilityVideoMapsDto? dto = await _connection.GetFacilityVideoMapsForArtccAsync(artccId, airportId);
            if (dto is null)
            {
                _log.LogWarning("No video maps for {Artcc}", artccId);
                return;
            }

            _activeScenarioId = scenarioId;
            _activeArtccId = string.IsNullOrWhiteSpace(artccId) ? null : artccId.ToUpperInvariant();
            _activeAirportId = MainViewModel.NormalizeFavoriteAirportId(airportId);
            UpdateFavoriteScopeState();
            ApplyVideoMapsDto(dto);

            // Download all referenced maps
            List<VideoMapData> data = await _videoMapService.LoadMapsAsync(artccId, dto.VideoMaps);

            // Initially enable always-visible maps
            UpdateActiveMaps();

            _log.LogInformation("Video maps loaded: {Count} maps for {Facility}", data.Count, dto.FacilityId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load video maps for {Artcc}", artccId);
        }
        finally
        {
            // Loaded, no maps, or failed: the attempt is over and nothing will restore settings over a later change. A newer load
            // or a clear since this one began owns the flag instead.
            if (load == _videoMapLoadGeneration)
            {
                VideoMapsReady = true;
            }
        }
    }

    /// <summary>Applies a facility's video-map list as the server's answer would, with no server round-trip.</summary>
    internal void ApplyVideoMapsDtoForTesting(FacilityVideoMapsDto dto) => ApplyVideoMapsDto(dto);

    private void ApplyVideoMapsDto(FacilityVideoMapsDto dto)
    {
        MvaFacilityId = string.IsNullOrWhiteSpace(dto.FacilityId) ? null : dto.FacilityId;

        // Build brightness lookup
        BrightnessLookup.Clear();
        foreach (VideoMapInfoDto map in dto.VideoMaps)
        {
            BrightnessLookup[map.Id] = map.BrightnessCategory;
        }

        // Set center: prefer primary airport, fall back to ARTCC area center
        _areaFallbackCenter = null;
        if ((_primaryAirportPosition.Lat != 0) || (_primaryAirportPosition.Lon != 0))
        {
            CenterLat = _primaryAirportPosition.Lat;
            CenterLon = _primaryAirportPosition.Lon;
            RangeRingCenterLat = _primaryAirportPosition.Lat;
            RangeRingCenterLon = _primaryAirportPosition.Lon;
        }
        else if (dto.Areas.Count > 0)
        {
            StarsAreaDto area = dto.Areas[0];
            CenterLat = area.CenterLat;
            CenterLon = area.CenterLon;
            RangeRingCenterLat = area.CenterLat;
            RangeRingCenterLon = area.CenterLon;
            _areaFallbackCenter = new LatLon(area.CenterLat, area.CenterLon);
        }

        RangeNm = DefaultRangeNm;

        // Build toggle list
        MapToggles.Clear();
        foreach (VideoMapInfoDto map in dto.VideoMaps)
        {
            var item = new VideoMapToggleItem
            {
                MapId = map.Id,
                ShortName = map.ShortName,
                Name = map.Name,
                BrightnessCategory = map.BrightnessCategory,
                StarsId = map.StarsId,
                TdmOnly = map.TdmOnly,
                IsEnabled = false,
            };
            PopulateFavoriteFlags(item);
            item.PropertyChanged += (_, _) =>
            {
                UpdateActiveMaps();
            };
            MapToggles.Add(item);
        }

        SortMapTogglesFavoritesFirst();

        // Build map shortcuts: prefer position-scoped mapGroup if set,
        // otherwise fall back to first mapGroup from the DTO.
        if (_positionMapGroupMapIds is not null)
        {
            BuildMapShortcutsFromGroup(_positionMapGroupMapIds);
        }
        else if (dto.MapGroups.Count > 0)
        {
            BuildMapShortcutsFromGroup(dto.MapGroups[0].MapIds);
        }
        else
        {
            MapShortcuts.Clear();
        }
    }

    /// <summary>
    /// Rebuilds the 3x2 DCB map shortcut grid from a mapGroup's mapIds.
    /// vNAS stores them column-major (top-to-bottom, left-to-right for a 2-row grid)
    /// but UniformGrid fills row-major, so transpose: [0,2,4,1,3,5].
    /// </summary>
    private void BuildMapShortcutsFromGroup(List<int?> mapIds)
    {
        MapShortcuts.Clear();
        int count = Math.Min(6, mapIds.Count);
        int[] rowMajorOrder = [0, 2, 4, 1, 3, 5];
        foreach (int srcIdx in rowMajorOrder)
        {
            if (srcIdx >= count)
            {
                continue;
            }

            int? starsId = mapIds[srcIdx];
            if (starsId is null)
            {
                continue;
            }

            VideoMapToggleItem? toggle = FindToggleByStarsId(starsId.Value);
            string shortName = toggle?.ShortName ?? $"M{starsId}";
            var shortcut = new MapShortcutItem
            {
                Index = srcIdx,
                StarsId = starsId.Value,
                ShortName = shortName,
                IsEnabled = toggle?.IsEnabled ?? false,
            };
            MapShortcuts.Add(shortcut);
        }
    }

    /// <summary>
    /// Applies position-scoped display config: rebuilds DCB map shortcuts
    /// from the position's mapGroup and stores underlying airports for
    /// weather readout.
    /// </summary>
    public void ApplyPositionDisplayConfig(PositionDisplayConfigDto config)
    {
        _positionMapGroupMapIds = config.MapGroupMapIds;
        BuildMapShortcutsFromGroup(config.MapGroupMapIds);
        WeatherAirports = config.UnderlyingAirports;
        _log.LogInformation(
            "Applied position display config for TCP {TcpCode}: {MapCount} maps, {AirportCount} weather airports",
            config.TcpCode,
            config.MapGroupMapIds.Count,
            config.UnderlyingAirports.Count
        );
    }

    public void SortMapTogglesFavoritesFirst()
    {
        var sorted = MapToggles.OrderByDescending(t => t.IsFavorite).ThenByDescending(t => t.IsEnabled).ThenBy(t => t.StarsId).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int current = MapToggles.IndexOf(sorted[i]);
            if (current != i)
            {
                MapToggles.Move(current, i);
            }
        }
    }

    /// <summary>
    /// Sets the per-scope favorite flags on a toggle from saved preferences, using the
    /// scope keys captured when the current facility's maps were loaded.
    /// </summary>
    private void PopulateFavoriteFlags(VideoMapToggleItem item)
    {
        if (_preferences is null)
        {
            return;
        }

        item.IsFavoriteArtcc = (_activeArtccId is not null) && _preferences.IsFavoriteVideoMap(FavoriteMapScope.Artcc, _activeArtccId, item.MapId);
        item.IsFavoriteAirport =
            (_activeAirportId is not null) && _preferences.IsFavoriteVideoMap(FavoriteMapScope.Airport, _activeAirportId, item.MapId);
        item.IsFavoriteScenario =
            (_activeScenarioId is not null) && _preferences.IsFavoriteVideoMap(FavoriteMapScope.Scenario, _activeScenarioId, item.MapId);
    }

    /// <summary>
    /// Toggles whether <paramref name="item"/> is a favorite under the given scope, persists the
    /// change, and re-sorts so favorites stay pinned to the top of the list. No-op when the scope's
    /// key is unavailable (e.g. a scenario with no primary airport).
    /// </summary>
    public void ToggleMapFavorite(VideoMapToggleItem item, FavoriteMapScope scope)
    {
        if (_preferences is null)
        {
            return;
        }

        string? key = scope switch
        {
            FavoriteMapScope.Artcc => _activeArtccId,
            FavoriteMapScope.Airport => _activeAirportId,
            FavoriteMapScope.Scenario => _activeScenarioId,
            _ => null,
        };
        if (key is null)
        {
            return;
        }

        bool newValue = scope switch
        {
            FavoriteMapScope.Artcc => !item.IsFavoriteArtcc,
            FavoriteMapScope.Airport => !item.IsFavoriteAirport,
            FavoriteMapScope.Scenario => !item.IsFavoriteScenario,
            _ => false,
        };

        switch (scope)
        {
            case FavoriteMapScope.Artcc:
                item.IsFavoriteArtcc = newValue;
                break;
            case FavoriteMapScope.Airport:
                item.IsFavoriteAirport = newValue;
                break;
            case FavoriteMapScope.Scenario:
                item.IsFavoriteScenario = newValue;
                break;
        }

        _preferences.SetFavoriteVideoMap(scope, key, item.MapId, newValue);
        SortMapTogglesFavoritesFirst();
    }

    private void UpdateFavoriteScopeState()
    {
        HasAirportFavoriteScope = _activeAirportId is not null;
        HasScenarioFavoriteScope = _activeScenarioId is not null;
        ArtccFavoriteMenuHeader = _activeArtccId is null ? "Favorite for ARTCC" : $"Favorite for ARTCC ({_activeArtccId})";
        AirportFavoriteMenuHeader = _activeAirportId is null ? "Favorite for airport" : $"Favorite for airport ({_activeAirportId})";
    }

    partial void OnMapSearchTextChanged(string value)
    {
        string filter = value.Trim();
        foreach (VideoMapToggleItem toggle in MapToggles)
        {
            toggle.IsVisible =
                filter.Length == 0
                || toggle.DisplayLabel.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || toggle.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void ClearVideoMaps()
    {
        VideoMapsReady = false;
        _videoMapLoadGeneration++;
        MapToggles.Clear();
        BrightnessLookup.Clear();
        MvaFacilityId = null;
        ActiveVideoMaps = null;
        _positionMapGroupMapIds = null;
        WeatherAirports = [];
        _videoMapService.ClearMemoryCache();
    }

    public void SetFixes(IReadOnlyList<(string Name, double Lat, double Lon)> fixes) => Fixes = fixes;

    public VideoMapToggleItem? FindToggleByStarsId(int starsId)
    {
        foreach (VideoMapToggleItem t in MapToggles)
        {
            if (t.StarsId == starsId)
            {
                return t;
            }
        }

        return null;
    }

    public void ToggleMapByStarsId(int starsId)
    {
        VideoMapToggleItem? toggle = FindToggleByStarsId(starsId);
        if (toggle is not null)
        {
            toggle.IsEnabled = !toggle.IsEnabled;
            SyncShortcutState(starsId, toggle.IsEnabled);
        }
        else
        {
            _log.LogWarning("No map with starsId {StarsId}", starsId);
        }
    }

    public void ToggleMapShortcut(MapShortcutItem shortcut)
    {
        VideoMapToggleItem? toggle = FindToggleByStarsId(shortcut.StarsId);
        if (toggle is not null)
        {
            toggle.IsEnabled = !toggle.IsEnabled;
            shortcut.IsEnabled = toggle.IsEnabled;
        }
    }

    public void SyncShortcutState(int starsId, bool enabled)
    {
        foreach (MapShortcutItem sc in MapShortcuts)
        {
            if (sc.StarsId == starsId)
            {
                sc.IsEnabled = enabled;
            }
        }
    }

    public void PlaceRangeRing(double lat, double lon)
    {
        RangeRingCenterLat = lat;
        RangeRingCenterLon = lon;
        IsPlacingRangeRing = false;
    }

    [RelayCommand]
    private void ToggleRangeRings() => ShowRangeRings = !ShowRangeRings;

    [RelayCommand]
    private void ToggleFixes() => ShowFixes = !ShowFixes;

    [RelayCommand]
    private void ToggleMvaHints() =>
        // Transient session toggle — deliberately not persisted; scenario load re-seeds it per position type.
        ShowMvaHints = !ShowMvaHints;

    [RelayCommand]
    private void CycleDeconflictMode()
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
            _preferences?.SetRadarDeconflictMode(DeconflictMode);
        }
    }

    [RelayCommand]
    private void TogglePanZoomLock() => IsPanZoomLocked = !IsPanZoomLocked;

    [RelayCommand]
    private void ToggleTopDown() => ShowTopDown = !ShowTopDown;

    public void AdjustPtlLength(int delta)
    {
        double next = PtlLengthMinutes + delta * 0.5;
        PtlLengthMinutes = Math.Clamp(next, 0.5, 3.0);
    }

    public void AdjustHistoryCount(int delta) => HistoryCount = Math.Clamp(HistoryCount + delta, 0, 10);

    [RelayCommand]
    private void TogglePtlOwn() => PtlOwn = !PtlOwn;

    [RelayCommand]
    private void TogglePtlAll() => PtlAll = !PtlAll;

    [RelayCommand]
    private void StartPlaceRangeRing() => IsPlacingRangeRing = true;

    [RelayCommand]
    private void OpenAuxMenu()
    {
        ActiveBriteTarget = null;
        DcbMode = DcbMenuMode.Aux;
    }

    [RelayCommand]
    private void OpenBriteMenu() => DcbMode = DcbMenuMode.Brite;

    [RelayCommand]
    private void CloseDcbSubmenu()
    {
        ActiveBriteTarget = null;
        DcbMode = DcbMenuMode.Main;
    }

    [RelayCommand]
    private void ToggleDcbVisible()
    {
        IsDcbVisible = !IsDcbVisible;
        if (!IsDcbVisible)
        {
            ActiveBriteTarget = null;
            DcbMode = DcbMenuMode.Main;
        }

        if (IsPrimary)
        {
            // App-wide default: only the primary view writes it, so an extra window's DCB stays its own.
            _preferences?.SetRadarDcbVisible(IsDcbVisible);
        }
    }

    [RelayCommand]
    private void ClearAllMaps()
    {
        foreach (VideoMapToggleItem t in MapToggles)
        {
            t.IsEnabled = false;
        }

        foreach (MapShortcutItem sc in MapShortcuts)
        {
            sc.IsEnabled = false;
        }
    }

    [RelayCommand]
    private void ResetCenter() => IsOffCenter = false;

    public void AdjustRange(int delta)
    {
        double newRange = RangeNm + delta;
        if (newRange is >= 1 and <= 256)
        {
            RangeNm = newRange;
        }
    }

    public int GetBrightnessPercent(BriteTarget target) => _brightnessValues.TryGetValue(target, out int val) ? val : 100;

    public void AdjustBrightness(BriteTarget target, int delta)
    {
        int pct = GetBrightnessPercent(target);
        pct = Math.Clamp(pct + delta, 0, 100);
        _brightnessValues[target] = pct;

        if (target is BriteTarget.MapA or BriteTarget.MapB or BriteTarget.RangeRing)
        {
            OnPropertyChanged(
                target switch
                {
                    BriteTarget.MapA => nameof(MapBrightnessA),
                    BriteTarget.MapB => nameof(MapBrightnessB),
                    BriteTarget.RangeRing => nameof(RangeRingBrightness),
                    _ => "",
                }
            );
        }
    }

    /// <summary>
    /// Returns the next discrete RR SIZE step in the given direction.
    /// </summary>
    public static double CycleRangeRingSize(double current, int direction)
    {
        if (direction > 0)
        {
            foreach (double step in RangeRingSizeSteps)
            {
                if (step > current)
                {
                    return step;
                }
            }

            return RangeRingSizeSteps[^1];
        }

        for (int i = RangeRingSizeSteps.Length - 1; i >= 0; i--)
        {
            if (RangeRingSizeSteps[i] < current)
            {
                return RangeRingSizeSteps[i];
            }
        }

        return RangeRingSizeSteps[0];
    }

    private void UpdateActiveMaps()
    {
        var active = new List<VideoMapData>();
        foreach (VideoMapToggleItem toggle in MapToggles)
        {
            if (!toggle.IsEnabled)
            {
                continue;
            }

            VideoMapData? cached = _videoMapService.GetCached(toggle.MapId);
            if (cached is not null)
            {
                active.Add(cached);
            }
        }

        ActiveVideoMaps = active;
    }

    // --- Command methods for context menus ---

    public async Task FlyHeadingAsync(string callsign, string initials, int heading) => await _sendCommand(callsign, $"FH {heading}", initials);

    public async Task ClimbAndMaintainAsync(string callsign, string initials, int altitude) =>
        await _sendCommand(callsign, $"CM {altitude}", initials);

    public async Task DescendAndMaintainAsync(string callsign, string initials, int altitude) =>
        await _sendCommand(callsign, $"DM {altitude}", initials);

    public async Task SpeedAsync(string callsign, string initials, int speed) => await _sendCommand(callsign, $"SPD {speed}", initials);

    public async Task SpeedNormalAsync(string callsign, string initials) => await _sendCommand(callsign, "RNS", initials);

    public async Task ReduceFinalApproachSpeedAsync(string callsign, string initials) => await _sendCommand(callsign, "RFAS", initials);

    public async Task AcceptHandoffAsync(string callsign, string initials) => await _sendCommand(callsign, "ACCEPT", initials);

    public async Task SendRawCommandAsync(string callsign, string initials, string command) => await _sendCommand(callsign, command, initials);

    // --- Track operations ---

    public async Task InitiateHandoffAsync(string callsign, string initials, string position) =>
        await _sendCommand(callsign, $"HO {position}", initials);

    // --- Draw route ---

    /// <summary>The radar's draw-route mode, made on first use; its changes are mirrored into the radar's bound properties.</summary>
    private DrawRouteState DrawRoute => _drawRoute ??= CreateDrawRoute();

    private DrawRouteState CreateDrawRoute()
    {
        var route = new DrawRouteState(() => Fixes, _sendCommand);
        route.Changed += () =>
        {
            DrawnWaypoints = route.Waypoints;
            WaypointConditionsSnapshot = route.ConditionsSnapshot;
            IsDrawingRoute = route.IsDrawing;
        };
        return route;
    }

    public void EnterDrawRoute(string callsign) => DrawRoute.Enter(callsign);

    /// <summary>
    /// The point named by <see cref="FrdResolver.ToFrd"/> over <paramref name="fixes"/> and the loaded nav database; null when
    /// the fixes or the nav database are not loaded yet, or no fix is in range.
    /// </summary>
    public static string? TryToFrd(IReadOnlyList<(string Name, double Lat, double Lon)>? fixes, double lat, double lon) =>
        ScopeMapSource.ToFrd(fixes, lat, lon);

    public void PlaceRouteWaypoint(double lat, double lon) => DrawRoute.Place(lat, lon);

    public void UndoRouteWaypoint() => DrawRoute.Undo();

    public void RemoveRouteWaypoint(int index) => DrawRoute.RemoveAt(index);

    public void RemoveRouteWaypointsAfter(int index) => DrawRoute.RemoveAfter(index);

    public void CancelDrawRoute() => DrawRoute.Cancel();

    public void SetWaypointCondition(int index, string? altitude, string? commands) => DrawRoute.SetCondition(index, altitude, commands);

    public WaypointCondition? GetWaypointCondition(int index) => DrawRoute.GetCondition(index);

    public Task ConfirmDrawRouteAsync(string initials) => DrawRoute.ConfirmAsync(initials);

    // --- Show nav route ---

    public bool IsPathShown(string callsign) => _shownPathCallsigns.Contains(callsign);

    public void ToggleShowPath(string callsign)
    {
        if (_shownPathCallsigns.Remove(callsign))
        {
            _pathCache.Remove(callsign);
            if (_pathColorIndices.Remove(callsign, out int freedIdx))
            {
                _freeColorIndices.Push(freedIdx);
            }
        }
        else
        {
            _shownPathCallsigns.Add(callsign);
            int colorIdx = _freeColorIndices.Count > 0 ? _freeColorIndices.Pop() : _pathColorIndices.Count % PathColors.Length;
            _pathColorIndices[callsign] = colorIdx;
        }

        RefreshShownPaths();
    }

    public void RefreshShownPaths()
    {
        if (_shownPathCallsigns.Count == 0)
        {
            ShownPaths = null;
            ShownShapes = null;
            return;
        }

        var entries = new List<ShownPathEntry>(_shownPathCallsigns.Count);
        var shapes = new List<ShownShapeEntry>();
        foreach (string callsign in _shownPathCallsigns)
        {
            AircraftModel? ac = FindAircraftByCallsign(callsign);
            if (ac is null)
            {
                continue;
            }

            string fingerprint = BuildShownPathFingerprint(ac);
            SKColor color = PathColors[_pathColorIndices[callsign]];

            // Active-procedure geometry (holds/PTs/coded legs/corridors, queued holds, orbit circles) is
            // computed server-side and arrives ready to draw — map it straight through with the aircraft's
            // overlay color. No caching needed (the points are cheap and change only on a phase transition
            // or a queue edit).
            foreach (NavRouteShapeDto shape in ac.NavRouteShapes)
            {
                shapes.Add(new ShownShapeEntry(shape, color));
            }

            if (
                _pathCache.TryGetValue(callsign, out (IReadOnlyList<ShownPathEntry> Segments, string Fingerprint) cached)
                && cached.Fingerprint == fingerprint
            )
            {
                // Aircraft position changes every tick — re-emit cached segments with the current
                // position, and the heading tail re-anchored on it, so the dashed leader and the
                // vectored aircraft's tail track the target.
                foreach (ShownPathEntry seg in cached.Segments)
                {
                    entries.Add(
                        seg with
                        {
                            AircraftLat = ac.Position.Lat,
                            AircraftLon = ac.Position.Lon,
                            Tail = seg.Tail is { } t ? t with { FromLat = ac.Position.Lat, FromLon = ac.Position.Lon } : null,
                        }
                    );
                }
                continue;
            }

            if (!_navDbReady)
            {
                continue;
            }

            (List<DrawnWaypoint>? primaryWps, VectorTail? primaryTail) = ShownRouteBuilder.BuildPrimary(ac);
            if ((primaryWps.Count == 0) && (primaryTail is null))
            {
                continue;
            }

            List<ShownPathEntry> segments = [new ShownPathEntry(callsign, primaryWps, color, ac.Position.Lat, ac.Position.Lon, Tail: primaryTail)];
            _pathCache[callsign] = (segments, fingerprint);
            entries.AddRange(segments);
        }

        ShownPaths = entries.Count > 0 ? entries : null;
        ShownShapes = shapes.Count > 0 ? shapes : null;
    }

    internal static string BuildShownPathFingerprint(AircraftModel ac)
    {
        // Cache key must reflect every field ShownRouteBuilder reads: the fix list with its
        // restriction tokens and the assigned heading. Otherwise a new heading — or a CFIX that only
        // changes a crossing restriction without changing the fix list — would not invalidate cached
        // segments and the overlay would stay stuck on stale geometry or labels.
        var sb = new System.Text.StringBuilder();
        foreach (NavRouteFixDto fix in ac.NavRouteFixes)
        {
            sb.Append(fix.Name)
                .Append(':')
                .Append(fix.Lat.ToString("F4", System.Globalization.CultureInfo.InvariantCulture))
                .Append(',')
                .Append(fix.Lon.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
            if (fix.RestrictionTokens is { } lines)
            {
                sb.Append('[');
                foreach (List<NavRouteRestrictionToken> line in lines)
                {
                    foreach (NavRouteRestrictionToken token in line)
                    {
                        sb.Append(token.Text).Append(token.Latent ? '~' : ' ');
                    }
                    sb.Append('/');
                }
                sb.Append(']');
            }
            sb.Append('>');
        }

        return string.Join("|", sb.ToString(), ac.AssignedHeading?.Degrees.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) ?? "");
    }

    public void ClearShownPaths()
    {
        _shownPathCallsigns.Clear();
        _pathCache.Clear();
        _pathColorIndices.Clear();
        _freeColorIndices.Clear();
        ShownPaths = null;
        ShownShapes = null;
    }

    public void RemoveShownPath(string callsign)
    {
        if (_shownPathCallsigns.Remove(callsign))
        {
            _pathCache.Remove(callsign);
            if (_pathColorIndices.Remove(callsign, out int freedIdx))
            {
                _freeColorIndices.Push(freedIdx);
            }

            RefreshShownPaths();
        }
    }

    private AircraftModel? FindAircraftByCallsign(string callsign) => _findAircraft?.Invoke(callsign);
}

/// <summary>
/// The flight path segment to render on the radar for a specific aircraft: its route as the
/// server sent it, with a dashed leader from the aircraft target to the first waypoint. Everything
/// else the overlay draws arrives as server shapes (<see cref="ShownShapeEntry"/>).
/// </summary>
/// <param name="Tail">
/// The heading arrow of a vectored aircraft with no route fixes, drawn from the aircraft position
/// along its assigned heading; null otherwise.
/// </param>
public record ShownPathEntry(
    string Callsign,
    IReadOnlyList<DrawnWaypoint> Waypoints,
    SKColor Color,
    double AircraftLat,
    double AircraftLon,
    VectorTail? Tail
);

/// <summary>
/// A server-computed active-procedure shape (hold racetrack, procedure turn, or coded-leg vector)
/// drawn on the "Show nav route" overlay in the owning aircraft's <see cref="Color"/>.
/// </summary>
public record ShownShapeEntry(Yaat.Sim.NavRouteShapeDto Shape, SKColor Color);

/// <summary>
/// A video map with an on/off toggle for the map selection list.
/// </summary>
public partial class VideoMapToggleItem : ObservableObject
{
    public required string MapId { get; init; }
    public required string ShortName { get; init; }
    public required string Name { get; init; }
    public required string BrightnessCategory { get; init; }
    public required int StarsId { get; init; }

    /// <summary>Whether the map is TDM-only (an airport diagram): the YAAT Scope draws it only in top-down mode; the Radar ignores it.</summary>
    public bool TdmOnly { get; init; }

    public string DisplayLabel => $"{StarsId} {ShortName}";

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private bool _isVisible = true;

    [ObservableProperty]
    private bool _isFavoriteArtcc;

    [ObservableProperty]
    private bool _isFavoriteAirport;

    [ObservableProperty]
    private bool _isFavoriteScenario;

    /// <summary>True when this map is favorited under any applicable scope.</summary>
    public bool IsFavorite => IsFavoriteArtcc || IsFavoriteAirport || IsFavoriteScenario;

    partial void OnIsFavoriteArtccChanged(bool value) => OnPropertyChanged(nameof(IsFavorite));

    partial void OnIsFavoriteAirportChanged(bool value) => OnPropertyChanged(nameof(IsFavorite));

    partial void OnIsFavoriteScenarioChanged(bool value) => OnPropertyChanged(nameof(IsFavorite));
}

/// <summary>
/// A DCB map shortcut button (from mapGroups[0].mapIds[0..5]).
/// </summary>
public partial class MapShortcutItem : ObservableObject
{
    public required int Index { get; init; }
    public required int StarsId { get; init; }
    public required string ShortName { get; init; }

    [ObservableProperty]
    private bool _isEnabled;
}
