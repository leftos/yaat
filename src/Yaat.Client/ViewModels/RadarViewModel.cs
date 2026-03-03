using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Models;
using Yaat.Client.Services;
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

public partial class RadarViewModel : ObservableObject
{
    public static readonly double[] RangeRingSizeSteps = [2, 5, 10, 15, 20];

    private readonly ILogger _log = AppLog.CreateLogger<RadarViewModel>();

    private readonly ServerConnection _connection;
    private readonly VideoMapService _videoMapService;
    private readonly Func<string, string, string, Task> _sendCommand;

    private string? _activeScenarioId;
    private UserPreferences? _preferences;
    private Func<string, double?>? _getAirportElevation;
    private FixDatabase? _fixDb;

    public string? PrimaryAirportId { get; private set; }

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
    private bool _isPanZoomLocked = true;

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

    public ObservableCollection<VideoMapToggleItem> MapToggles { get; } = [];

    public ObservableCollection<MapShortcutItem> MapShortcuts { get; } = [];

    /// <summary>
    /// Brightness category by map ID ("A" or "B").
    /// </summary>
    public Dictionary<string, string> BrightnessLookup { get; } = [];

    public RadarViewModel(ServerConnection connection, VideoMapService videoMapService, Func<string, string, string, Task> sendCommand)
    {
        _connection = connection;
        _videoMapService = videoMapService;
        _sendCommand = sendCommand;
    }

    public void SetPreferences(UserPreferences prefs)
    {
        _preferences = prefs;
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

        // Build map shortcuts from first mapGroup (indices 0-5).
        // vNAS stores them column-major (top-to-bottom, left-to-right for a 2-row grid)
        // but UniformGrid fills row-major, so transpose: [0,2,4,1,3,5].
        MapShortcuts.Clear();
        if (dto.MapGroups.Count > 0)
        {
            var group = dto.MapGroups[0];
            var count = Math.Min(6, group.MapIds.Count);
            int[] rowMajorOrder = [0, 2, 4, 1, 3, 5];
            foreach (var srcIdx in rowMajorOrder)
            {
                if (srcIdx >= count)
                {
                    continue;
                }

                var starsId = group.MapIds[srcIdx];
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
        if (_preferences is null || _activeScenarioId is null)
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
        await _sendCommand(callsign, "SPD 0", initials);
    }

    public async Task DirectToAsync(string callsign, string initials, string fix)
    {
        await _sendCommand(callsign, $"DCT {fix}", initials);
    }

    public async Task AppendDirectToAsync(string callsign, string initials, string fix)
    {
        await _sendCommand(callsign, $"ADCT {fix}", initials);
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

    // --- Communication ---

    public async Task FrequencyChangeAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "FC", initials);
    }

    public async Task ContactTcpAsync(string callsign, string initials, string tcp)
    {
        await _sendCommand(callsign, $"CT {tcp}", initials);
    }

    public async Task ContactTowerAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "TO", initials);
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
