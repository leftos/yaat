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

    public ObservableCollection<VideoMapToggleItem> MapToggles { get; }
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

    public async Task LoadVideoMapsAsync(
        string artccId, string facilityId)
    {
        try
        {
            var dto = await _connection
                .GetFacilityVideoMapsAsync(artccId, facilityId);
            if (dto is null)
            {
                _log.LogWarning(
                    "No video maps for {Artcc}/{Facility}",
                    artccId, facilityId);
                return;
            }

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
            }

            // Download all referenced maps
            var data = await _videoMapService
                .LoadMapsAsync(artccId, dto.VideoMaps);

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
                item.PropertyChanged += (_, _) => UpdateActiveMaps();
                MapToggles.Add(item);
            }

            // Initially enable always-visible maps
            UpdateActiveMaps();

            _log.LogInformation(
                "Video maps loaded: {Count} maps for {Facility}",
                data.Count, facilityId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Failed to load video maps for {Artcc}/{Facility}",
                artccId, facilityId);
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

    [RelayCommand]
    private void IncreaseRange()
    {
        RangeNm = Math.Min(RangeNm * 1.25, 300);
    }

    [RelayCommand]
    private void DecreaseRange()
    {
        RangeNm = Math.Max(RangeNm / 1.25, 5);
    }

    [RelayCommand]
    private void ToggleRangeRings()
    {
        ShowRangeRings = !ShowRangeRings;
    }

    [RelayCommand]
    private void ToggleFixes()
    {
        ShowFixes = !ShowFixes;
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
