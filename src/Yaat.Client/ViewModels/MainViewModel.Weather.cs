using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Services;
using Yaat.Sim;

namespace Yaat.Client.ViewModels;

/// <summary>One airport's raw METAR string, as broadcast for the currently active weather.</summary>
public record MetarEntry(string? StationId, string Raw);

public record WeatherDisplayInfo(
    string? StationId,
    int? WindDirectionDeg,
    int? WindSpeedKts,
    int? WindGustKts,
    double? AltimeterInHg,
    // Cloud ceiling (lowest BKN/OVC base) in feet AGL, or null when clear / scattered-only. The ground
    // view keeps an aircraft visible until it climbs through the ceiling (or 6,000 ft AGL if clear).
    int? CeilingFeetAgl = null
)
{
    public string ToDisplayString()
    {
        var parts = new List<string>(3);

        if (StationId is not null)
        {
            parts.Add(StationId);
        }

        if (AltimeterInHg is not null)
        {
            parts.Add($"{AltimeterInHg:F2}");
        }

        if (WindDirectionDeg is not null || WindSpeedKts is not null)
        {
            // A parsed VRB (variable-direction) wind has no numeric direction; render the METAR "VRB" token.
            var direction = WindDirectionDeg is { } dir ? $"{dir:D3}" : "VRB";
            var wind = $"{direction}{WindSpeedKts:D2}";
            if (WindGustKts is not null)
            {
                wind += $"G{WindGustKts:D2}";
            }

            wind += "KT";
            parts.Add(wind);
        }

        return string.Join(" ", parts);
    }
}

/// <summary>
/// Weather loading and clearing commands, and the WeatherChanged event handler.
/// </summary>
public partial class MainViewModel
{
    private readonly LiveWeatherService _liveWeather = new();
    private readonly ArtccAirportResolver _airportResolver = new();

    private string? _activeWeatherJson;
    private IReadOnlyList<WeatherDisplayInfo>? _allWeatherInfo;

    /// <summary>Raw METAR strings for the active weather, one per airport, shown in the METAR window.</summary>
    public ObservableCollection<MetarEntry> Metars { get; } = [];

    public string? ActiveWeatherJson => _activeWeatherJson;

    public bool HasActiveWeather => _activeWeatherJson is not null;

    private void SetActiveWeatherJson(string? json)
    {
        _activeWeatherJson = json;
        OnPropertyChanged(nameof(ActiveWeatherJson));
        OnPropertyChanged(nameof(HasActiveWeather));
        SaveWeatherCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanExecuteInRoom))]
    private async Task LoadWeatherAsync(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        try
        {
            _log.LogInformation("Loading weather from {Path}", filePath);
            var json = await File.ReadAllTextAsync(filePath);
            var result = await _connection.LoadWeatherAsync(json, reconstructMetars: true);

            if (result.Success)
            {
                StatusText = result.Message ?? "Weather loaded";
                var name = Path.GetFileNameWithoutExtension(filePath);
                _preferences.AddRecentWeather(filePath, name);
                SetActiveWeatherJson(json);
            }
            else
            {
                StatusText = result.Message ?? "Weather load failed";
                _log.LogWarning("Weather load failed: {Message}", result.Message);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Weather load error");
            StatusText = $"Weather error: {ex.Message}";
        }
    }

    /// <summary>
    /// Loads weather from pre-fetched JSON (e.g. from the vNAS data API or editor).
    /// </summary>
    public async Task LoadWeatherFromJsonAsync(string json, string displayName, string? apiId = null)
    {
        try
        {
            _log.LogInformation("Loading weather from API: {Name}", displayName);
            var result = await _connection.LoadWeatherAsync(json, reconstructMetars: true);

            if (result.Success)
            {
                StatusText = result.Message ?? $"Weather loaded: {displayName}";
                if (apiId is not null)
                {
                    _preferences.AddRecentWeather("", displayName, apiId);
                }
                SetActiveWeatherJson(json);
            }
            else
            {
                StatusText = result.Message ?? "Weather load failed";
                _log.LogWarning("Weather load failed: {Message}", result.Message);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Weather load error");
            StatusText = $"Weather error: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanLoadLiveWeather))]
    private async Task LoadLiveWeatherAsync()
    {
        try
        {
            StatusText = "Fetching live weather...";
            var artccId = _preferences.ArtccId;
            var airportIds = await _airportResolver.GetAirportIdsAsync(artccId);
            if (airportIds.Count == 0)
            {
                StatusText = "No airports found for ARTCC";
                return;
            }

            var profile = await _liveWeather.BuildLiveWeatherAsync(artccId, airportIds);
            if (profile is null)
            {
                StatusText = "Failed to fetch live weather data";
                return;
            }

            var json = JsonSerializer.Serialize(profile);
            // Live-fetched real METARs are left untouched (no dynamic reconstruction).
            var result = await _connection.LoadWeatherAsync(json, reconstructMetars: false);

            if (result.Success)
            {
                StatusText = result.Message ?? $"Loaded: {profile.Name}";
                SetActiveWeatherJson(json);
            }
            else
            {
                StatusText = result.Message ?? "Live weather load failed";
                _log.LogWarning("Live weather load failed: {Message}", result.Message);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Live weather error");
            StatusText = $"Live weather error: {ex.Message}";
        }
    }

    private bool CanLoadLiveWeather() =>
        IsConnected && ActiveRoomId is not null && !string.IsNullOrWhiteSpace(_preferences.ArtccId) && _commandInput.NavDbReady;

    [RelayCommand(CanExecute = nameof(CanClearWeather))]
    private async Task ClearWeatherAsync()
    {
        try
        {
            await _connection.ClearWeatherAsync();
            StatusText = "Weather cleared";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Clear weather error");
            StatusText = $"Weather error: {ex.Message}";
        }
    }

    private bool CanClearWeather() => CanExecuteInRoom && ActiveWeatherName is not null;

    [RelayCommand(CanExecute = nameof(HasActiveWeather))]
    private async Task SaveWeatherAsync()
    {
        if (_activeWeatherJson is null)
        {
            return;
        }

        var sanitized = SanitizeFileName(ActiveWeatherName ?? "weather");
        var path = await _filePicker.SaveFileAsync(
            new SaveFileOptions(
                Title: "Save Weather As…",
                SuggestedFileName: sanitized,
                Filters: [new FilePickerFilter("JSON", ["*.json"])],
                DefaultExtension: "json"
            )
        );

        if (path is null)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, _activeWeatherJson);
            var name = Path.GetFileNameWithoutExtension(path);
            _preferences.AddRecentWeather(path, name);
            StatusText = $"Weather saved: {name}";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Save weather error");
            StatusText = $"Save error: {ex.Message}";
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
        {
            sanitized.Append(invalid.Contains(c) ? '_' : c);
        }
        return sanitized.ToString();
    }

    private void OnWeatherChanged(WeatherChangedDto dto)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            ActiveWeatherName = dto.Name;

            if (dto.Name is null)
            {
                SetActiveWeatherJson(null);
                ApplyDefaultWeatherIfNoWeather();
            }
            else
            {
                SetActiveWeatherJson(dto.SourceJson);

                var allInfo = ExtractAllWeatherDisplay(dto.Metars);
                _allWeatherInfo = allInfo;
                Radar.WeatherInfo = FilterWeatherForPosition(allInfo, Radar.WeatherAirports);
                Ground.WeatherInfo = PickGroundWeather(allInfo, Ground.Layout?.AirportId);
                PopulateMetars(dto.Metars);
            }
        });
    }

    private void PopulateMetars(IReadOnlyList<string>? metars)
    {
        Metars.Clear();
        if (metars is null)
        {
            return;
        }

        foreach (var raw in metars)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            // Strip K prefix for US ICAO stations (KOAK → OAK) for the display label.
            var stationId = MetarParser.Parse(raw)?.StationId;
            if (stationId is { Length: 4 } && stationId.StartsWith('K'))
            {
                stationId = stationId[1..];
            }

            Metars.Add(new MetarEntry(stationId, raw.Trim()));
        }
    }

    private static IReadOnlyList<WeatherDisplayInfo>? ExtractAllWeatherDisplay(IReadOnlyList<string>? metars)
    {
        if (metars is null || metars.Count == 0)
        {
            return null;
        }

        var list = new List<WeatherDisplayInfo>(metars.Count);
        foreach (var raw in metars)
        {
            var parsed = MetarParser.Parse(raw);
            if (parsed is null)
            {
                continue;
            }

            // Strip K prefix for US ICAO stations (KOAK → OAK)
            var displayId = parsed.StationId;
            if (displayId.Length == 4 && displayId.StartsWith('K'))
            {
                displayId = displayId[1..];
            }

            list.Add(
                new WeatherDisplayInfo(
                    displayId,
                    parsed.WindDirectionDeg,
                    parsed.WindSpeedKts,
                    parsed.WindGustKts,
                    parsed.AltimeterInHg,
                    parsed.CeilingFeetAgl
                )
            );
        }

        return list.Count > 0 ? list : null;
    }

    private static WeatherDisplayInfo? PickGroundWeather(IReadOnlyList<WeatherDisplayInfo>? allInfo, string? airportId)
    {
        if (allInfo is null || airportId is null)
        {
            return allInfo?.Count > 0 ? allInfo[0] : null;
        }

        foreach (var info in allInfo)
        {
            if (string.Equals(info.StationId, airportId, StringComparison.OrdinalIgnoreCase))
            {
                return info;
            }
        }

        // Fall back to first station if no match
        return allInfo.Count > 0 ? allInfo[0] : null;
    }

    /// <summary>
    /// Filters weather display info to only stations matching the position's
    /// underlying airports. Returns all stations if no position filter is set.
    /// </summary>
    private static IReadOnlyList<WeatherDisplayInfo>? FilterWeatherForPosition(
        IReadOnlyList<WeatherDisplayInfo>? allInfo,
        List<string> weatherAirports
    )
    {
        if (allInfo is null)
        {
            return null;
        }

        if (weatherAirports.Count == 0)
        {
            return allInfo;
        }

        var filtered = new List<WeatherDisplayInfo>();
        foreach (var info in allInfo)
        {
            if (info.StationId is not null && weatherAirports.Any(a => string.Equals(a, info.StationId, StringComparison.OrdinalIgnoreCase)))
            {
                filtered.Add(info);
            }
        }

        return filtered.Count > 0 ? filtered : allInfo;
    }

    /// <summary>
    /// Re-applies weather filtering when the active position changes.
    /// Called from <see cref="OnPositionDisplayChanged"/>.
    /// </summary>
    private void UpdateRadarWeatherDisplay()
    {
        Radar.WeatherInfo = FilterWeatherForPosition(_allWeatherInfo, Radar.WeatherAirports);
    }

    /// <summary>
    /// When no weather profile is loaded, fills the METAR view AND the radar/ground per-airport
    /// wind/altimeter overlays with a standard default report (calm wind, 10SM, clear, 29.92) for
    /// each scenario airport, so every weather surface reflects the calm/standard conditions the sim
    /// already applies rather than showing nothing. No-op when real weather is loaded.
    /// </summary>
    private void ApplyDefaultWeatherIfNoWeather()
    {
        if (HasActiveWeather)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var defaults = new List<string>();
        foreach (var icao in CollectScenarioAirportIcaos())
        {
            defaults.Add(DefaultMetar.Build(icao, now));
        }

        PopulateMetars(defaults);

        var allInfo = ExtractAllWeatherDisplay(defaults);
        _allWeatherInfo = allInfo;
        Radar.WeatherInfo = FilterWeatherForPosition(allInfo, Radar.WeatherAirports);
        Ground.WeatherInfo = PickGroundWeather(allInfo, Ground.Layout?.AirportId);
    }

    private List<string> CollectScenarioAirportIcaos()
    {
        var result = new List<string>();

        void Add(string? id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            var upper = id.Trim().ToUpperInvariant();
            var icao = upper.Length == 3 ? "K" + upper : upper;
            if (!result.Contains(icao))
            {
                result.Add(icao);
            }
        }

        foreach (var airport in Radar.WeatherAirports)
        {
            Add(airport);
        }

        Add(ActiveScenarioPrimaryAirportId);
        return result;
    }
}
