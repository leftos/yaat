using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using Yaat.Client.Logging;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;

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

public partial class RadarViewModel : ObservableObject
{
    public static readonly double[] RangeRingSizeSteps = [2, 5, 10, 15, 20];

    private readonly ILogger _log = AppLog.CreateLogger<RadarViewModel>();

    private readonly ServerConnection _connection;
    private readonly VideoMapService _videoMapService;
    private readonly Func<string, string, string, Task> _sendCommand;
    private readonly Action<AircraftModel?>? _onSelectionChanged;

    private string? _activeScenarioId;
    private UserPreferences? _preferences;
    private bool _isRestoring;
    private Func<string, double?>? _getAirportElevation;
    private Func<string, AircraftModel?>? _findAircraft;
    private FixDatabase? _fixDb;
    private ApproachDatabase? _approachDb;
    private ProcedureDatabase? _procedureDb;

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

    private double _primaryAirportLat;
    private double _primaryAirportLon;

    [ObservableProperty]
    private double _rangeNm = 40;

    [ObservableProperty]
    private double _centerLat;

    [ObservableProperty]
    private double _centerLon;

    [ObservableProperty]
    private bool _showRangeRings = true;

    [ObservableProperty]
    private bool _showFixes;

    [ObservableProperty]
    private AircraftModel? _selectedAircraft;

    [ObservableProperty]
    private IReadOnlySet<string>? _programmedFixNames;

    [ObservableProperty]
    private IReadOnlyList<WeatherDisplayInfo>? _weatherInfo;

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

    private string? _drawRouteCallsign;
    private readonly List<DrawnWaypoint> _drawnWaypointsMutable = [];
    private readonly Dictionary<int, WaypointCondition> _waypointConditions = new();

    // --- Show flight path state ---
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

    private readonly HashSet<string> _shownPathCallsigns = new();
    private readonly Dictionary<string, (IReadOnlyList<DrawnWaypoint> Waypoints, string Fingerprint)> _pathCache = new();
    private readonly Dictionary<string, int> _pathColorIndices = new();
    private readonly Stack<int> _freeColorIndices = new();

    public ObservableCollection<VideoMapToggleItem> MapToggles { get; } = [];

    public ObservableCollection<MapShortcutItem> MapShortcuts { get; } = [];

    /// <summary>
    /// Brightness category by map ID ("A" or "B").
    /// </summary>
    public Dictionary<string, string> BrightnessLookup { get; } = [];

    public RadarViewModel(
        ServerConnection connection,
        VideoMapService videoMapService,
        Func<string, string, string, Task> sendCommand,
        Action<AircraftModel?>? onSelectionChanged = null
    )
    {
        _connection = connection;
        _videoMapService = videoMapService;
        _sendCommand = sendCommand;
        _onSelectionChanged = onSelectionChanged;
    }

    public void SetPreferences(UserPreferences prefs)
    {
        _preferences = prefs;
    }

    public void SetAircraftLookup(Func<string, AircraftModel?> lookup)
    {
        _findAircraft = lookup;
    }

    public void SetElevationLookup(Func<string, double?> lookup)
    {
        _getAirportElevation = lookup;
    }

    public void SetFixDb(FixDatabase fixDb)
    {
        _fixDb = fixDb;
        FixNames = fixDb.AllFixNames;
        SetFixes(BuildVisibleFixes());
    }

    public void SetApproachDb(ApproachDatabase approachDb)
    {
        _approachDb = approachDb;
    }

    public ApproachDatabase? ApproachDb => _approachDb;

    public void SetProcedureDb(ProcedureDatabase procedureDb)
    {
        _procedureDb = procedureDb;
    }

    partial void OnSelectedAircraftChanged(AircraftModel? value)
    {
        _onSelectionChanged?.Invoke(value);
        UpdateProgrammedFixes(value);
        if (IsDrawingRoute && (value is null || value.Callsign != _drawRouteCallsign))
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

        ProgrammedFixNames = ProgrammedFixResolver.Resolve(ac.Route, ac.ExpectedApproach, ac.Destination, ac.Departure, _approachDb, null, _fixDb);
    }

    public string[]? FixNames { get; private set; }

    private IReadOnlyList<(string Name, double Lat, double Lon)> BuildVisibleFixes()
    {
        if (_fixDb is null)
        {
            return [];
        }

        var names = _fixDb.AllFixNames;
        var result = new List<(string, double, double)>(names.Length);
        foreach (var name in names)
        {
            var pos = _fixDb.GetFixPosition(name);
            if (pos.HasValue)
            {
                result.Add((name, pos.Value.Lat, pos.Value.Lon));
            }
        }

        return result;
    }

    public void SetPrimaryAirportId(string? id)
    {
        PrimaryAirportId = id;
    }

    public void SetPrimaryAirportPosition(double lat, double lon)
    {
        _primaryAirportLat = lat;
        _primaryAirportLon = lon;
    }

    public double GetFieldElevation(string? destination)
    {
        if (_getAirportElevation is null)
        {
            return 0;
        }

        if (!string.IsNullOrEmpty(destination))
        {
            var elev = _getAirportElevation(destination);
            if (elev.HasValue)
            {
                return elev.Value;
            }
        }

        if (!string.IsNullOrEmpty(PrimaryAirportId))
        {
            var elev = _getAirportElevation(PrimaryAirportId);
            if (elev.HasValue)
            {
                return elev.Value;
            }
        }

        return 0;
    }

    public async Task LoadVideoMapsForArtccAsync(string artccId, string? airportId = null, string? scenarioId = null)
    {
        try
        {
            var dto = await _connection.GetFacilityVideoMapsForArtccAsync(artccId, airportId);
            if (dto is null)
            {
                _log.LogWarning("No video maps for {Artcc}", artccId);
                return;
            }

            _activeScenarioId = scenarioId;
            ApplyVideoMapsDto(artccId, dto);

            // Download all referenced maps
            var data = await _videoMapService.LoadMapsAsync(artccId, dto.VideoMaps);

            // Restore per-scenario settings if available
            RestoreSettings();

            // Initially enable always-visible maps (unless restored)
            UpdateActiveMaps();

            _log.LogInformation("Video maps loaded: {Count} maps for {Facility}", data.Count, dto.FacilityId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load video maps for {Artcc}", artccId);
        }
    }

    private void ApplyVideoMapsDto(string artccId, FacilityVideoMapsDto dto)
    {
        // Build brightness lookup
        BrightnessLookup.Clear();
        foreach (var map in dto.VideoMaps)
        {
            BrightnessLookup[map.Id] = map.BrightnessCategory;
        }

        // Set center: prefer primary airport, fall back to ARTCC area center
        if (_primaryAirportLat != 0 || _primaryAirportLon != 0)
        {
            CenterLat = _primaryAirportLat;
            CenterLon = _primaryAirportLon;
            RangeRingCenterLat = _primaryAirportLat;
            RangeRingCenterLon = _primaryAirportLon;
        }
        else if (dto.Areas.Count > 0)
        {
            var area = dto.Areas[0];
            CenterLat = area.CenterLat;
            CenterLon = area.CenterLon;
            RangeRingCenterLat = area.CenterLat;
            RangeRingCenterLon = area.CenterLon;
        }

        RangeNm = 40;

        // Build toggle list
        MapToggles.Clear();
        foreach (var map in dto.VideoMaps)
        {
            var item = new VideoMapToggleItem
            {
                MapId = map.Id,
                ShortName = map.ShortName,
                Name = map.Name,
                BrightnessCategory = map.BrightnessCategory,
                StarsId = map.StarsId,
                IsEnabled = false,
            };
            item.PropertyChanged += (_, _) =>
            {
                UpdateActiveMaps();
                SaveSettings();
            };
            MapToggles.Add(item);
        }

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
        var count = Math.Min(6, mapIds.Count);
        int[] rowMajorOrder = [0, 2, 4, 1, 3, 5];
        foreach (var srcIdx in rowMajorOrder)
        {
            if (srcIdx >= count)
            {
                continue;
            }

            var starsId = mapIds[srcIdx];
            if (starsId is null)
            {
                continue;
            }

            var toggle = FindToggleByStarsId(starsId.Value);
            var shortName = toggle?.ShortName ?? $"M{starsId}";
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

    public void SortMapTogglesEnabledFirst()
    {
        var sorted = MapToggles.OrderByDescending(t => t.IsEnabled).ThenBy(t => t.StarsId).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int current = MapToggles.IndexOf(sorted[i]);
            if (current != i)
            {
                MapToggles.Move(current, i);
            }
        }
    }

    partial void OnMapSearchTextChanged(string value)
    {
        var filter = value.Trim();
        foreach (var toggle in MapToggles)
        {
            toggle.IsVisible =
                filter.Length == 0
                || toggle.DisplayLabel.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || toggle.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void ClearVideoMaps()
    {
        MapToggles.Clear();
        BrightnessLookup.Clear();
        ActiveVideoMaps = null;
        _positionMapGroupMapIds = null;
        WeatherAirports = [];
        _videoMapService.ClearMemoryCache();
    }

    public void SetFixes(IReadOnlyList<(string Name, double Lat, double Lon)> fixes)
    {
        Fixes = fixes;
    }

    public VideoMapToggleItem? FindToggleByStarsId(int starsId)
    {
        foreach (var t in MapToggles)
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
        var toggle = FindToggleByStarsId(starsId);
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
        var toggle = FindToggleByStarsId(shortcut.StarsId);
        if (toggle is not null)
        {
            toggle.IsEnabled = !toggle.IsEnabled;
            shortcut.IsEnabled = toggle.IsEnabled;
        }
    }

    public void SyncShortcutState(int starsId, bool enabled)
    {
        foreach (var sc in MapShortcuts)
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
        SaveSettings();
    }

    [RelayCommand]
    private void ToggleRangeRings()
    {
        ShowRangeRings = !ShowRangeRings;
        SaveSettings();
    }

    [RelayCommand]
    private void ToggleFixes()
    {
        ShowFixes = !ShowFixes;
        SaveSettings();
    }

    [RelayCommand]
    private void TogglePanZoomLock()
    {
        IsPanZoomLocked = !IsPanZoomLocked;
        SaveSettings();
    }

    [RelayCommand]
    private void ToggleTopDown()
    {
        ShowTopDown = !ShowTopDown;
        SaveSettings();
    }

    public void AdjustPtlLength(int delta)
    {
        var next = PtlLengthMinutes + delta * 0.5;
        PtlLengthMinutes = Math.Clamp(next, 0.5, 3.0);
        SaveSettings();
    }

    [RelayCommand]
    private void TogglePtlOwn()
    {
        PtlOwn = !PtlOwn;
        SaveSettings();
    }

    [RelayCommand]
    private void TogglePtlAll()
    {
        PtlAll = !PtlAll;
        SaveSettings();
    }

    [RelayCommand]
    private void StartPlaceRangeRing()
    {
        IsPlacingRangeRing = true;
    }

    [RelayCommand]
    private void OpenAuxMenu()
    {
        ActiveBriteTarget = null;
        DcbMode = DcbMenuMode.Aux;
    }

    [RelayCommand]
    private void OpenBriteMenu()
    {
        DcbMode = DcbMenuMode.Brite;
    }

    [RelayCommand]
    private void CloseDcbSubmenu()
    {
        ActiveBriteTarget = null;
        DcbMode = DcbMenuMode.Main;
    }

    [RelayCommand]
    private void ClearAllMaps()
    {
        foreach (var t in MapToggles)
        {
            t.IsEnabled = false;
        }

        foreach (var sc in MapShortcuts)
        {
            sc.IsEnabled = false;
        }
    }

    [RelayCommand]
    private void ResetCenter()
    {
        IsOffCenter = false;
    }

    public void AdjustRange(int delta)
    {
        var newRange = RangeNm + delta;
        if (newRange is >= 1 and <= 256)
        {
            RangeNm = newRange;
            SaveSettings();
        }
    }

    public int GetBrightnessPercent(BriteTarget target)
    {
        return _brightnessValues.TryGetValue(target, out var val) ? val : 100;
    }

    public void AdjustBrightness(BriteTarget target, int delta)
    {
        var pct = GetBrightnessPercent(target);
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

        SaveSettings();
    }

    /// <summary>
    /// Returns the next discrete RR SIZE step in the given direction.
    /// </summary>
    public static double CycleRangeRingSize(double current, int direction)
    {
        if (direction > 0)
        {
            foreach (var step in RangeRingSizeSteps)
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

    private void SaveSettings()
    {
        if (_preferences is null || _activeScenarioId is null || _isRestoring)
        {
            return;
        }

        var enabledIds = new List<int>();
        foreach (var t in MapToggles)
        {
            if (t.IsEnabled)
            {
                enabledIds.Add(t.StarsId);
            }
        }

        var brightnessDict = new Dictionary<string, int>();
        foreach (var (target, value) in _brightnessValues)
        {
            brightnessDict[target.ToString()] = value;
        }

        var settings = new SavedRadarSettings
        {
            EnabledStarsIds = enabledIds,
            CenterLat = CenterLat,
            CenterLon = CenterLon,
            RangeNm = RangeNm,
            RangeRingCenterLat = RangeRingCenterLat,
            RangeRingCenterLon = RangeRingCenterLon,
            RangeRingSizeNm = RangeRingSizeNm,
            ShowRangeRings = ShowRangeRings,
            ShowFixes = ShowFixes,
            IsPanZoomLocked = IsPanZoomLocked,
            ShowTopDown = ShowTopDown,
            PtlLengthMinutes = PtlLengthMinutes,
            PtlOwn = PtlOwn,
            PtlAll = PtlAll,
            BrightnessValues = brightnessDict,
        };

        _preferences.SetRadarSettings(_activeScenarioId, settings);
    }

    public void CopySettingsFrom(string sourceScenarioId)
    {
        if (_preferences is null || _activeScenarioId is null)
        {
            return;
        }

        var saved = _preferences.GetRadarSettings(sourceScenarioId);
        if (saved is null)
        {
            return;
        }

        ApplySettings(saved);
        SaveSettings();
    }

    private void RestoreSettings()
    {
        if (_preferences is null || _activeScenarioId is null)
        {
            return;
        }

        var saved = _preferences.GetRadarSettings(_activeScenarioId);
        if (saved is null)
        {
            return;
        }

        ApplySettings(saved);
    }

    private void ApplySettings(SavedRadarSettings saved)
    {
        _isRestoring = true;

        // Restore map toggles
        var enabledSet = new HashSet<int>(saved.EnabledStarsIds);
        foreach (var t in MapToggles)
        {
            t.IsEnabled = enabledSet.Contains(t.StarsId);
        }

        // Sync shortcut states
        foreach (var sc in MapShortcuts)
        {
            sc.IsEnabled = enabledSet.Contains(sc.StarsId);
        }

        CenterLat = saved.CenterLat;
        CenterLon = saved.CenterLon;
        RangeNm = saved.RangeNm;
        RangeRingCenterLat = saved.RangeRingCenterLat;
        RangeRingCenterLon = saved.RangeRingCenterLon;
        RangeRingSizeNm = saved.RangeRingSizeNm;
        ShowRangeRings = saved.ShowRangeRings;
        ShowFixes = saved.ShowFixes;
        IsPanZoomLocked = saved.IsPanZoomLocked;
        ShowTopDown = saved.ShowTopDown;
        PtlLengthMinutes = saved.PtlLengthMinutes;
        PtlOwn = saved.PtlOwn;
        PtlAll = saved.PtlAll;

        if (saved.BrightnessValues is { Count: > 0 })
        {
            foreach (var (key, value) in saved.BrightnessValues)
            {
                if (Enum.TryParse<BriteTarget>(key, out var target))
                {
                    _brightnessValues[target] = Math.Clamp(value, 0, 100);
                }
            }

            OnPropertyChanged(nameof(MapBrightnessA));
            OnPropertyChanged(nameof(MapBrightnessB));
            OnPropertyChanged(nameof(RangeRingBrightness));
        }

        _isRestoring = false;
    }

    private void UpdateActiveMaps()
    {
        var active = new List<VideoMapData>();
        foreach (var toggle in MapToggles)
        {
            if (!toggle.IsEnabled)
            {
                continue;
            }

            var cached = _videoMapService.GetCached(toggle.MapId);
            if (cached is not null)
            {
                active.Add(cached);
            }
        }

        ActiveVideoMaps = active;
    }

    // --- Command methods for context menus ---

    public async Task FlyHeadingAsync(string callsign, string initials, int heading)
    {
        await _sendCommand(callsign, $"FH {heading}", initials);
    }

    public async Task TurnLeftAsync(string callsign, string initials, int heading)
    {
        await _sendCommand(callsign, $"TL {heading}", initials);
    }

    public async Task TurnRightAsync(string callsign, string initials, int heading)
    {
        await _sendCommand(callsign, $"TR {heading}", initials);
    }

    public async Task ClimbAndMaintainAsync(string callsign, string initials, int altitude)
    {
        await _sendCommand(callsign, $"CM {altitude}", initials);
    }

    public async Task DescendAndMaintainAsync(string callsign, string initials, int altitude)
    {
        await _sendCommand(callsign, $"DM {altitude}", initials);
    }

    public async Task SpeedAsync(string callsign, string initials, int speed)
    {
        await _sendCommand(callsign, $"SPD {speed}", initials);
    }

    public async Task SpeedNormalAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "RNS", initials);
    }

    public async Task DirectToAsync(string callsign, string initials, string fix)
    {
        await _sendCommand(callsign, $"DCT {fix}", initials);
    }

    public async Task AppendDirectToAsync(string callsign, string initials, string fix)
    {
        await _sendCommand(callsign, $"ADCT {fix}", initials);
    }

    public async Task AppendForceDirectToAsync(string callsign, string initials, string fix)
    {
        await _sendCommand(callsign, $"ADCTF {fix}", initials);
    }

    public async Task TrackAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "TRACK", initials);
    }

    public async Task DropTrackAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "DROP", initials);
    }

    public async Task AcceptHandoffAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "ACCEPT", initials);
    }

    public async Task SendRawCommandAsync(string callsign, string initials, string command)
    {
        await _sendCommand(callsign, command, initials);
    }

    public async Task DeleteAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "DEL", initials);
    }

    public async Task IdentAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "ID", initials);
    }

    public async Task PresentHeadingAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "FPH", initials);
    }

    // --- Track operations ---

    public async Task InitiateHandoffAsync(string callsign, string initials, string position)
    {
        await _sendCommand(callsign, $"HO {position}", initials);
    }

    public async Task CancelHandoffAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "CANCEL", initials);
    }

    public async Task PointOutAsync(string callsign, string initials, string position)
    {
        await _sendCommand(callsign, $"PO {position}", initials);
    }

    public async Task AcknowledgeAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "OK", initials);
    }

    // --- Data block ---

    public async Task ScratchpadAsync(string callsign, string initials, string text)
    {
        await _sendCommand(callsign, $"SP {text}", initials);
    }

    public async Task TemporaryAltitudeAsync(string callsign, string initials, int altitude)
    {
        await _sendCommand(callsign, $"TEMPALT {altitude}", initials);
    }

    public async Task CruiseAsync(string callsign, string initials, int altitude)
    {
        await _sendCommand(callsign, $"CRUISE {altitude}", initials);
    }

    public async Task AnnotateAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "ANNOTATE", initials);
    }

    // --- Hold ---

    public async Task HoldPresentLeftAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "HPPL", initials);
    }

    public async Task HoldPresentRightAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "HPPR", initials);
    }

    public async Task HoldAtFixLeftAsync(string callsign, string initials, string fix)
    {
        await _sendCommand(callsign, $"HFIXL {fix}", initials);
    }

    public async Task HoldAtFixRightAsync(string callsign, string initials, string fix)
    {
        await _sendCommand(callsign, $"HFIXR {fix}", initials);
    }

    // --- Relative turns ---

    public async Task RelativeLeftAsync(string callsign, string initials, int degrees)
    {
        await _sendCommand(callsign, $"LT {degrees}", initials);
    }

    public async Task RelativeRightAsync(string callsign, string initials, int degrees)
    {
        await _sendCommand(callsign, $"RT {degrees}", initials);
    }

    // --- Speed ---

    public async Task SpeedAssignAsync(string callsign, string initials, int speed)
    {
        await _sendCommand(callsign, $"SPD {speed}", initials);
    }

    // --- Approach ---

    public async Task ClearedApproachAsync(string callsign, string initials, string id)
    {
        await _sendCommand(callsign, $"CAPP {id}", initials);
    }

    public async Task JoinApproachAsync(string callsign, string initials, string id)
    {
        await _sendCommand(callsign, $"JAPP {id}", initials);
    }

    public async Task ClearedApproachStraightInAsync(string callsign, string initials, string id)
    {
        await _sendCommand(callsign, $"CAPPSI {id}", initials);
    }

    public async Task JoinApproachStraightInAsync(string callsign, string initials, string id)
    {
        await _sendCommand(callsign, $"JAPPSI {id}", initials);
    }

    public async Task ClearedApproachForceAsync(string callsign, string initials, string id)
    {
        await _sendCommand(callsign, $"CAPPF {id}", initials);
    }

    public async Task JoinApproachForceAsync(string callsign, string initials, string id)
    {
        await _sendCommand(callsign, $"JAPPF {id}", initials);
    }

    public async Task JoinFinalApproachCourseAsync(string callsign, string initials, string id)
    {
        await _sendCommand(callsign, $"JFAC {id}", initials);
    }

    public async Task ExpectApproachAsync(string callsign, string initials, string id)
    {
        await _sendCommand(callsign, $"EAPP {id}", initials);
    }

    public async Task ClearedVisualApproachAsync(string callsign, string initials, string runway)
    {
        await _sendCommand(callsign, $"CVA {runway}", initials);
    }

    public async Task ReportFieldInSightAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "RFIS", initials);
    }

    public async Task ReportTrafficInSightAsync(string callsign, string initials, string? targetCallsign)
    {
        var cmd = string.IsNullOrWhiteSpace(targetCallsign) ? "RTIS" : $"RTIS {targetCallsign}";
        await _sendCommand(callsign, cmd, initials);
    }

    // --- Procedures ---

    public async Task JoinStarAsync(string callsign, string initials, string star)
    {
        await _sendCommand(callsign, $"JARR {star}", initials);
    }

    public async Task ClimbViaSidAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "CVIA", initials);
    }

    public async Task DescendViaStarAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "DVIA", initials);
    }

    public async Task CrossFixAsync(string callsign, string initials, string fix)
    {
        await _sendCommand(callsign, $"CFIX {fix}", initials);
    }

    public async Task DepartFixAsync(string callsign, string initials, string fix)
    {
        await _sendCommand(callsign, $"DEPART {fix}", initials);
    }

    public async Task PtacAsync(string callsign, string initials, string args)
    {
        await _sendCommand(callsign, $"PTAC {args}", initials);
    }

    // --- Tower / Landing ---

    public async Task ClearedToLandAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "CLAND", initials);
    }

    public async Task ClearedForOptionAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "CFO", initials);
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

    public async Task GoAroundAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "GA", initials);
    }

    // --- Pattern entry ---

    public async Task EnterLeftDownwindAsync(string callsign, string initials, string? runway)
    {
        await _sendCommand(callsign, runway is not null ? $"ELD {runway}" : "ELD", initials);
    }

    public async Task EnterRightDownwindAsync(string callsign, string initials, string? runway)
    {
        await _sendCommand(callsign, runway is not null ? $"ERD {runway}" : "ERD", initials);
    }

    public async Task EnterLeftBaseAsync(string callsign, string initials, string? runway)
    {
        await _sendCommand(callsign, runway is not null ? $"ELB {runway}" : "ELB", initials);
    }

    public async Task EnterRightBaseAsync(string callsign, string initials, string? runway)
    {
        await _sendCommand(callsign, runway is not null ? $"ERB {runway}" : "ERB", initials);
    }

    public async Task EnterFinalAsync(string callsign, string initials, string? runway)
    {
        await _sendCommand(callsign, runway is not null ? $"EF {runway}" : "EF", initials);
    }

    // --- Squawk ---

    public async Task SquawkAsync(string callsign, string initials, int code)
    {
        await _sendCommand(callsign, $"SQ {code}", initials);
    }

    public async Task SquawkVfrAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "SQVFR", initials);
    }

    public async Task SquawkNormalAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "SQNORM", initials);
    }

    public async Task SquawkStandbyAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "SQSBY", initials);
    }

    // --- Coordination ---

    public async Task CoordinationReleaseAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "RD", initials);
    }

    public async Task CoordinationHoldAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "RDH", initials);
    }

    public async Task CoordinationRecallAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "RDR", initials);
    }

    public async Task CoordinationAcknowledgeAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "RDACK", initials);
    }

    // --- Draw route ---

    public void EnterDrawRoute(string callsign)
    {
        _drawRouteCallsign = callsign;
        _drawnWaypointsMutable.Clear();
        _waypointConditions.Clear();
        DrawnWaypoints = null;
        IsDrawingRoute = true;
    }

    public void PlaceRouteWaypoint(double lat, double lon)
    {
        if (!IsDrawingRoute)
        {
            return;
        }

        string? name = Fixes is not null ? FrdResolver.ToFrd(lat, lon, Fixes) : null;
        name ??= $"{lat:F3},{lon:F3}";

        _drawnWaypointsMutable.Add(new DrawnWaypoint(name, lat, lon));
        DrawnWaypoints = _drawnWaypointsMutable.ToList();
    }

    public void UndoRouteWaypoint()
    {
        if (!IsDrawingRoute || _drawnWaypointsMutable.Count == 0)
        {
            return;
        }

        var lastIdx = _drawnWaypointsMutable.Count - 1;
        _waypointConditions.Remove(lastIdx);
        _drawnWaypointsMutable.RemoveAt(lastIdx);
        DrawnWaypoints = _drawnWaypointsMutable.Count > 0 ? _drawnWaypointsMutable.ToList() : null;
    }

    public void RemoveRouteWaypoint(int index)
    {
        if (!IsDrawingRoute || index < 0 || index >= _drawnWaypointsMutable.Count)
        {
            return;
        }

        // Remove conditions that referenced this index, shift higher indices down
        var newConditions = new Dictionary<int, WaypointCondition>();
        foreach (var (idx, cond) in _waypointConditions)
        {
            if (idx < index)
            {
                newConditions[idx] = cond;
            }
            else if (idx > index)
            {
                newConditions[idx - 1] = cond;
            }
        }

        _waypointConditions.Clear();
        foreach (var (idx, cond) in newConditions)
        {
            _waypointConditions[idx] = cond;
        }

        _drawnWaypointsMutable.RemoveAt(index);
        DrawnWaypoints = _drawnWaypointsMutable.Count > 0 ? _drawnWaypointsMutable.ToList() : null;
    }

    public void RemoveRouteWaypointsAfter(int index)
    {
        if (!IsDrawingRoute || index < 0 || index >= _drawnWaypointsMutable.Count - 1)
        {
            return;
        }

        // Remove conditions for all waypoints after index
        for (int i = _drawnWaypointsMutable.Count - 1; i > index; i--)
        {
            _waypointConditions.Remove(i);
            _drawnWaypointsMutable.RemoveAt(i);
        }

        DrawnWaypoints = _drawnWaypointsMutable.Count > 0 ? _drawnWaypointsMutable.ToList() : null;
    }

    public void CancelDrawRoute()
    {
        _drawRouteCallsign = null;
        _drawnWaypointsMutable.Clear();
        _waypointConditions.Clear();
        DrawnWaypoints = null;
        IsDrawingRoute = false;
    }

    public void SetWaypointCondition(int index, string? altitude, string? commands)
    {
        if (string.IsNullOrWhiteSpace(altitude) && string.IsNullOrWhiteSpace(commands))
        {
            _waypointConditions.Remove(index);
        }
        else
        {
            _waypointConditions[index] = new WaypointCondition(altitude?.Trim(), commands?.Trim());
        }

        WaypointConditionsSnapshot = _waypointConditions.Count > 0 ? new Dictionary<int, WaypointCondition>(_waypointConditions) : null;
    }

    public WaypointCondition? GetWaypointCondition(int index)
    {
        return _waypointConditions.GetValueOrDefault(index);
    }

    public async Task ConfirmDrawRouteAsync(string initials)
    {
        if (!IsDrawingRoute || _drawRouteCallsign is null || _drawnWaypointsMutable.Count == 0)
        {
            CancelDrawRoute();
            return;
        }

        var command = BuildDrawRouteCommand();
        var callsign = _drawRouteCallsign;
        CancelDrawRoute();
        await _sendCommand(callsign, command, initials);
    }

    private string BuildDrawRouteCommand()
    {
        var fixes = string.Join(" ", _drawnWaypointsMutable.Select(w => w.ResolvedName));
        var parts = new List<string> { $"DCTF {fixes}" };

        foreach (var (index, condition) in _waypointConditions.OrderBy(kv => kv.Key))
        {
            if (index >= _drawnWaypointsMutable.Count)
            {
                continue;
            }

            var fixName = _drawnWaypointsMutable[index].ResolvedName;
            var atParts = new List<string>();

            if (!string.IsNullOrWhiteSpace(condition.Altitude))
            {
                atParts.Add($"CFIX {condition.Altitude}");
            }

            if (!string.IsNullOrWhiteSpace(condition.Commands))
            {
                atParts.Add(condition.Commands);
            }

            if (atParts.Count > 0)
            {
                parts.Add($"AT {fixName} {string.Join(",", atParts)}");
            }
        }

        return string.Join(";", parts);
    }

    // --- Show flight path ---

    public bool IsPathShown(string callsign) => _shownPathCallsigns.Contains(callsign);

    public void ToggleShowPath(string callsign)
    {
        if (_shownPathCallsigns.Remove(callsign))
        {
            _pathCache.Remove(callsign);
            if (_pathColorIndices.Remove(callsign, out var freedIdx))
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
            return;
        }

        var entries = new List<ShownPathEntry>(_shownPathCallsigns.Count);
        foreach (var callsign in _shownPathCallsigns)
        {
            var ac = FindAircraftByCallsign(callsign);
            if (ac is null)
            {
                continue;
            }

            var fingerprint =
                $"{ac.Route}|{ac.ActiveSidId}|{ac.ActiveStarId}|{ac.ActiveApproachId}|{ac.ExpectedApproach}|{ac.DepartureRunway}|{ac.DestinationRunway}";
            if (_pathCache.TryGetValue(callsign, out var cached) && cached.Fingerprint == fingerprint)
            {
                entries.Add(new ShownPathEntry(callsign, cached.Waypoints, PathColors[_pathColorIndices[callsign]], ac.Latitude, ac.Longitude));
                continue;
            }

            var waypoints = ResolveFlightPathWaypoints(ac);
            if (waypoints.Count == 0)
            {
                continue;
            }

            _pathCache[callsign] = (waypoints, fingerprint);
            entries.Add(new ShownPathEntry(callsign, waypoints, PathColors[_pathColorIndices[callsign]], ac.Latitude, ac.Longitude));
        }

        ShownPaths = entries.Count > 0 ? entries : null;
    }

    private IReadOnlyList<DrawnWaypoint> ResolveFlightPathWaypoints(AircraftModel ac)
    {
        if (_fixDb is null)
        {
            return [];
        }

        var fixNames = ResolveCifpAwareRoute(ac);
        var result = new List<DrawnWaypoint>(fixNames.Count + 10);

        foreach (var name in fixNames)
        {
            var pos = _fixDb.GetFixPosition(name);
            if (pos.HasValue)
            {
                result.Add(new DrawnWaypoint(name, pos.Value.Lat, pos.Value.Lon));
            }
        }

        // Append approach fixes if an approach is active
        if (_approachDb is not null && !string.IsNullOrEmpty(ac.ActiveApproachId) && !string.IsNullOrEmpty(ac.Destination))
        {
            var procedure = _approachDb.GetApproach(ac.Destination, ac.ActiveApproachId);
            if (procedure is not null)
            {
                var approachFixNames = ApproachCommandHandler.GetApproachFixNames(procedure);
                var seen = new HashSet<string>(result.Select(w => w.ResolvedName), StringComparer.OrdinalIgnoreCase);
                foreach (var name in approachFixNames)
                {
                    if (!seen.Add(name))
                    {
                        continue;
                    }

                    var pos = _fixDb.GetFixPosition(name);
                    if (pos.HasValue)
                    {
                        result.Add(new DrawnWaypoint(name, pos.Value.Lat, pos.Value.Lon));
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Resolves the route fix names using CIFP data for the correct SID/STAR transitions
    /// when available, falling back to <see cref="FixDatabase.ExpandRoute"/> otherwise.
    /// </summary>
    private IReadOnlyList<string> ResolveCifpAwareRoute(AircraftModel ac)
    {
        if (string.IsNullOrWhiteSpace(ac.Route))
        {
            return [];
        }

        var routeTokens = ac.Route.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Try to resolve SID fixes from CIFP
        IReadOnlyList<string>? sidFixes = null;
        int sidTokensConsumed = 0;
        if (_procedureDb is not null && !string.IsNullOrEmpty(ac.ActiveSidId) && !string.IsNullOrEmpty(ac.Departure))
        {
            (sidFixes, sidTokensConsumed) = ResolveSidFixes(ac, routeTokens);
        }

        // Try to resolve STAR fixes from CIFP
        IReadOnlyList<string>? starFixes = null;
        string? starToken = null;
        if (_procedureDb is not null && !string.IsNullOrEmpty(ac.ActiveStarId) && !string.IsNullOrEmpty(ac.Destination))
        {
            (starFixes, starToken) = ResolveStarFixes(ac, routeTokens);
        }

        // If neither CIFP resolution succeeded, fall back entirely
        if (sidFixes is null && starFixes is null)
        {
            return _fixDb!.ExpandRoute(ac.Route);
        }

        // Build combined fix list: SID fixes → middle route tokens → STAR fixes
        var result = new List<string>();

        if (sidFixes is not null)
        {
            result.AddRange(sidFixes);
        }

        // Add middle route tokens (between SID and STAR)
        for (int i = sidFixes is not null ? sidTokensConsumed : 0; i < routeTokens.Length; i++)
        {
            var token = routeTokens[i];
            if (double.TryParse(token, out _))
            {
                continue;
            }

            // Skip the STAR token and any transition token before it
            if (starToken is not null && string.Equals(token, starToken, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            // Skip tokens already consumed as SID name or enroute transition
            if (sidFixes is not null && i < sidTokensConsumed)
            {
                continue;
            }

            // Skip SID/STAR names that FixDatabase would expand
            if (_fixDb!.IsSidOrStar(token))
            {
                continue;
            }

            result.Add(token);
        }

        if (starFixes is not null)
        {
            result.AddRange(starFixes);
        }

        // Deduplicate adjacent identical fix names
        var deduped = new List<string>(result.Count);
        foreach (var name in result)
        {
            if (deduped.Count == 0 || !string.Equals(deduped[^1], name, StringComparison.OrdinalIgnoreCase))
            {
                deduped.Add(name);
            }
        }

        return deduped;
    }

    private (IReadOnlyList<string>? Fixes, int TokensConsumed) ResolveSidFixes(AircraftModel ac, string[] routeTokens)
    {
        if (routeTokens.Length == 0)
        {
            return (null, 0);
        }

        var sid = _procedureDb!.GetSid(ac.Departure, ac.ActiveSidId);
        if (sid is null)
        {
            return (null, 0);
        }

        var orderedLegs = new List<CifpLeg>();

        if (!string.IsNullOrEmpty(ac.DepartureRunway))
        {
            var rwKey = "RW" + ac.DepartureRunway;
            if (sid.RunwayTransitions.TryGetValue(rwKey, out var rwTransition))
            {
                orderedLegs.AddRange(rwTransition.Legs);
            }
        }

        orderedLegs.AddRange(sid.CommonLegs);

        // Enroute transition (second route token)
        int tokensConsumed = 1; // SID name token
        if (routeTokens.Length > 1)
        {
            var enrouteKey = routeTokens[1].ToUpperInvariant();
            if (sid.EnrouteTransitions.TryGetValue(enrouteKey, out var enTransition))
            {
                orderedLegs.AddRange(enTransition.Legs);
                tokensConsumed = 2;
            }
        }

        if (orderedLegs.Count == 0)
        {
            return (null, 0);
        }

        var fixes = ExtractFixNames(orderedLegs);
        return fixes.Count > 0 ? (fixes, tokensConsumed) : (null, 0);
    }

    private (IReadOnlyList<string>? Fixes, string? StarToken) ResolveStarFixes(AircraftModel ac, string[] routeTokens)
    {
        var star = _procedureDb!.GetStar(ac.Destination, ac.ActiveStarId);
        if (star is null)
        {
            return (null, null);
        }

        var orderedLegs = new List<CifpLeg>();

        // Find the STAR token and possible transition in the route
        string? starToken = null;
        string? transitionName = null;
        for (int i = 0; i < routeTokens.Length; i++)
        {
            if (string.Equals(routeTokens[i], ac.ActiveStarId, StringComparison.OrdinalIgnoreCase))
            {
                starToken = routeTokens[i];
                // Check if preceding token is an enroute transition
                if (i > 0)
                {
                    var prevToken = routeTokens[i - 1].ToUpperInvariant();
                    if (star.EnrouteTransitions.ContainsKey(prevToken))
                    {
                        transitionName = prevToken;
                        starToken = routeTokens[i - 1]; // Include transition token so middle-route loop stops before it
                    }
                }

                break;
            }
        }

        // Enroute transition
        if (transitionName is not null && star.EnrouteTransitions.TryGetValue(transitionName, out var enTransition))
        {
            orderedLegs.AddRange(enTransition.Legs);
        }

        orderedLegs.AddRange(star.CommonLegs);

        // Runway transition
        if (!string.IsNullOrEmpty(ac.DestinationRunway))
        {
            var rwKey = "RW" + ac.DestinationRunway;
            if (star.RunwayTransitions.TryGetValue(rwKey, out var rwTransition))
            {
                orderedLegs.AddRange(rwTransition.Legs);
            }
        }

        if (orderedLegs.Count == 0)
        {
            return (null, null);
        }

        var fixes = ExtractFixNames(orderedLegs);
        return fixes.Count > 0 ? (fixes, starToken) : (null, null);
    }

    private static List<string> ExtractFixNames(List<CifpLeg> legs)
    {
        var result = new List<string>(legs.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var leg in legs)
        {
            if (!string.IsNullOrEmpty(leg.FixIdentifier) && seen.Add(leg.FixIdentifier))
            {
                result.Add(leg.FixIdentifier);
            }
        }

        return result;
    }

    public void ClearShownPaths()
    {
        _shownPathCallsigns.Clear();
        _pathCache.Clear();
        _pathColorIndices.Clear();
        _freeColorIndices.Clear();
        ShownPaths = null;
    }

    public void RemoveShownPath(string callsign)
    {
        if (_shownPathCallsigns.Remove(callsign))
        {
            _pathCache.Remove(callsign);
            if (_pathColorIndices.Remove(callsign, out var freedIdx))
            {
                _freeColorIndices.Push(freedIdx);
            }

            RefreshShownPaths();
        }
    }

    private AircraftModel? FindAircraftByCallsign(string callsign)
    {
        return _findAircraft?.Invoke(callsign);
    }
}

/// <summary>
/// A drawn waypoint in route drawing mode.
/// </summary>
public record DrawnWaypoint(string ResolvedName, double Lat, double Lon);

/// <summary>
/// A flight path to render on the radar for a specific aircraft.
/// </summary>
public record ShownPathEntry(string Callsign, IReadOnlyList<DrawnWaypoint> Waypoints, SKColor Color, double AircraftLat, double AircraftLon);

/// <summary>
/// Condition applied to a drawn route waypoint (crossing altitude and/or AT commands).
/// </summary>
public record WaypointCondition(string? Altitude, string? Commands)
{
    /// <summary>
    /// Returns a compact summary for rendering on the map (e.g. "A100 SPD250").
    /// </summary>
    public string ToSummary()
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(Altitude))
        {
            parts.Add(Altitude);
        }

        if (!string.IsNullOrWhiteSpace(Commands))
        {
            parts.Add(Commands.Replace(" ", ""));
        }

        return string.Join(" ", parts);
    }
}

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

    public string DisplayLabel => $"{StarsId} {ShortName}";

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private bool _isVisible = true;
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
