using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Sim.Data;

namespace Yaat.Client.ViewModels;

public partial class RadarViewModel : ObservableObject
{
    private readonly ILogger _log =
        AppLog.CreateLogger<RadarViewModel>();

    private readonly ServerConnection _connection;
    private readonly VideoMapService _videoMapService;
    private readonly Func<string, string, string, Task> _sendCommand;

    private string? _activeScenarioId;
    private UserPreferences? _preferences;

    [ObservableProperty]
    private double _rangeNm = 60;

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
    private float _mapBrightnessA = 1.0f;

    [ObservableProperty]
    private float _mapBrightnessB = 0.6f;

    [ObservableProperty]
    private IReadOnlyList<VideoMapData>? _activeVideoMaps;

    [ObservableProperty]
    private IReadOnlyList<(string Name, double Lat, double Lon)>?
        _fixes;

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

    public ObservableCollection<VideoMapToggleItem> MapToggles { get; }
        = [];

    public ObservableCollection<MapShortcutItem> MapShortcuts { get; }
        = [];

    /// <summary>
    /// Brightness category by map ID ("A" or "B").
    /// </summary>
    public Dictionary<string, string> BrightnessLookup { get; } = [];

    public RadarViewModel(
        ServerConnection connection,
        VideoMapService videoMapService,
        Func<string, string, string, Task> sendCommand)
    {
        _connection = connection;
        _videoMapService = videoMapService;
        _sendCommand = sendCommand;
    }

    public void SetPreferences(UserPreferences prefs)
    {
        _preferences = prefs;
    }

    public async Task LoadVideoMapsForArtccAsync(
        string artccId, string? scenarioId = null)
    {
        try
        {
            var dto = await _connection
                .GetFacilityVideoMapsForArtccAsync(artccId);
            if (dto is null)
            {
                _log.LogWarning(
                    "No video maps for {Artcc}", artccId);
                return;
            }

            _activeScenarioId = scenarioId;
            ApplyVideoMapsDto(artccId, dto);

            // Download all referenced maps
            var data = await _videoMapService
                .LoadMapsAsync(artccId, dto.VideoMaps);

            // Restore per-scenario settings if available
            RestoreSettings();

            // Initially enable always-visible maps (unless restored)
            UpdateActiveMaps();

            _log.LogInformation(
                "Video maps loaded: {Count} maps for {Facility}",
                data.Count, dto.FacilityId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Failed to load video maps for {Artcc}",
                artccId);
        }
    }

    private void ApplyVideoMapsDto(
        string artccId, FacilityVideoMapsDto dto)
    {
        // Build brightness lookup
        BrightnessLookup.Clear();
        foreach (var map in dto.VideoMaps)
        {
            BrightnessLookup[map.Id] = map.BrightnessCategory;
        }

        // Set center from first area
        if (dto.Areas.Count > 0)
        {
            var area = dto.Areas[0];
            CenterLat = area.CenterLat;
            CenterLon = area.CenterLon;
            RangeNm = area.SurveillanceRange;
            RangeRingCenterLat = area.CenterLat;
            RangeRingCenterLon = area.CenterLon;
        }

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
                IsEnabled = map.AlwaysVisible,
            };
            item.PropertyChanged += (_, _) =>
            {
                UpdateActiveMaps();
                SaveSettings();
            };
            MapToggles.Add(item);
        }

        // Build map shortcuts from first mapGroup (indices 0-5)
        MapShortcuts.Clear();
        if (dto.MapGroups.Count > 0)
        {
            var group = dto.MapGroups[0];
            for (var i = 0; i < Math.Min(6, group.MapIds.Count); i++)
            {
                var starsId = group.MapIds[i];
                if (starsId is null)
                {
                    continue;
                }

                var toggle = FindToggleByStarsId(starsId.Value);
                var shortName = toggle?.ShortName ?? $"M{starsId}";
                var shortcut = new MapShortcutItem
                {
                    Index = i,
                    StarsId = starsId.Value,
                    ShortName = shortName.Length > 5
                        ? shortName[..5]
                        : shortName,
                    IsEnabled = toggle?.IsEnabled ?? false,
                };
                MapShortcuts.Add(shortcut);
            }
        }
    }

    public void ClearVideoMaps()
    {
        MapToggles.Clear();
        BrightnessLookup.Clear();
        ActiveVideoMaps = null;
        _videoMapService.ClearMemoryCache();
    }

    public void SetFixes(
        IReadOnlyList<(string Name, double Lat, double Lon)> fixes)
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

    private void SyncShortcutState(int starsId, bool enabled)
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
    private void IncreaseRange()
    {
        RangeNm = Math.Min(RangeNm * 1.25, 300);
        SaveSettings();
    }

    [RelayCommand]
    private void DecreaseRange()
    {
        RangeNm = Math.Max(RangeNm / 1.25, 5);
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
    private void IncrementRangeRingSize()
    {
        RangeRingSizeNm = Math.Min(RangeRingSizeNm + 5, 100);
        SaveSettings();
    }

    [RelayCommand]
    private void DecrementRangeRingSize()
    {
        RangeRingSizeNm = Math.Max(RangeRingSizeNm - 5, 5);
        SaveSettings();
    }

    [RelayCommand]
    private void StartPlaceRangeRing()
    {
        IsPlacingRangeRing = true;
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

    public async Task FlyHeadingAsync(
        string callsign, string initials, int heading)
    {
        await _sendCommand(callsign, $"FH {heading}", initials);
    }

    public async Task TurnLeftAsync(
        string callsign, string initials, int heading)
    {
        await _sendCommand(callsign, $"TL {heading}", initials);
    }

    public async Task TurnRightAsync(
        string callsign, string initials, int heading)
    {
        await _sendCommand(callsign, $"TR {heading}", initials);
    }

    public async Task ClimbAndMaintainAsync(
        string callsign, string initials, int altitude)
    {
        await _sendCommand(callsign, $"CM {altitude}", initials);
    }

    public async Task DescendAndMaintainAsync(
        string callsign, string initials, int altitude)
    {
        await _sendCommand(callsign, $"DM {altitude}", initials);
    }

    public async Task SpeedAsync(
        string callsign, string initials, int speed)
    {
        await _sendCommand(callsign, $"SPD {speed}", initials);
    }

    public async Task SpeedNormalAsync(
        string callsign, string initials)
    {
        await _sendCommand(callsign, "SN", initials);
    }

    public async Task DirectToAsync(
        string callsign, string initials, string fix)
    {
        await _sendCommand(callsign, $"DCT {fix}", initials);
    }

    public async Task ClearedApproachAsync(
        string callsign, string initials, string approach)
    {
        await _sendCommand(callsign, $"CA {approach}", initials);
    }

    public async Task TrackAsync(
        string callsign, string initials)
    {
        await _sendCommand(callsign, "TRACK", initials);
    }

    public async Task DropTrackAsync(
        string callsign, string initials)
    {
        await _sendCommand(callsign, "DROP", initials);
    }

    public async Task AcceptHandoffAsync(
        string callsign, string initials)
    {
        await _sendCommand(callsign, "ACCEPT", initials);
    }

    public async Task DeleteAsync(
        string callsign, string initials)
    {
        await _sendCommand(callsign, "DEL", initials);
    }

    public async Task IdentAsync(
        string callsign, string initials)
    {
        await _sendCommand(callsign, "ID", initials);
    }

    public async Task PresentHeadingAsync(
        string callsign, string initials)
    {
        await _sendCommand(callsign, "FPH", initials);
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

    [ObservableProperty]
    private bool _isEnabled;
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
