using System.Text.Json;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Services;
using Yaat.Sim;

namespace Yaat.Client.ViewModels;

public record WeatherDisplayInfo(string? StationId, int? WindDirectionDeg, int? WindSpeedKts, int? WindGustKts, double? AltimeterInHg)
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
            var wind = $"{WindDirectionDeg:D3}{WindSpeedKts:D2}";
            if (WindGustKts is not null)
            {
                wind += $"G{WindGustKts:D2}";
            }

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
            var result = await _connection.LoadWeatherAsync(json);

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
            var result = await _connection.LoadWeatherAsync(json);

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
            var fixDb = _commandInput.FixDb!;

            var airportIds = await _airportResolver.GetAirportIdsAsync(artccId);
            if (airportIds.Count == 0)
            {
                StatusText = "No airports found for ARTCC";
                return;
            }

            var profile = await _liveWeather.BuildLiveWeatherAsync(artccId, airportIds, fixDb);
            if (profile is null)
            {
                StatusText = "Failed to fetch live weather data";
                return;
            }

            var json = JsonSerializer.Serialize(profile);
            var result = await _connection.LoadWeatherAsync(json);

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
        IsConnected && ActiveRoomId is not null && !string.IsNullOrWhiteSpace(_preferences.ArtccId) && _commandInput.FixDb is not null;

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

        var mainWindow = Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;

        if (mainWindow?.StorageProvider is not { } storageProvider)
        {
            return;
        }

        var sanitized = SanitizeFileName(ActiveWeatherName ?? "weather");
        var file = await storageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = "Save Weather As…",
                SuggestedFileName = sanitized,
                FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
                DefaultExtension = "json",
            }
        );

        if (file is null)
        {
            return;
        }

        var path = file.TryGetLocalPath();
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
                _allWeatherInfo = null;
                Ground.WeatherInfo = null;
                Radar.WeatherInfo = null;
            }
            else
            {
                var profile = new WeatherProfile
                {
                    Name = dto.Name,
                    ArtccId = _preferences.ArtccId,
                    Precipitation = dto.Precipitation,
                    Metars = dto.Metars ?? [],
                    WindLayers =
                        dto.WindLayers?.Select(w => new WindLayer
                            {
                                Altitude = w.Altitude,
                                Direction = w.Direction,
                                Speed = w.Speed,
                                Gusts = w.Gusts,
                            })
                            .ToList()
                        ?? [],
                };
                SetActiveWeatherJson(JsonSerializer.Serialize(profile));

                var allInfo = ExtractAllWeatherDisplay(dto.Metars);
                _allWeatherInfo = allInfo;
                Radar.WeatherInfo = FilterWeatherForPosition(allInfo, Radar.WeatherAirports);
                Ground.WeatherInfo = PickGroundWeather(allInfo, Ground.Layout?.AirportId);
            }
        });
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
            if (displayId?.Length == 4 && displayId.StartsWith('K'))
            {
                displayId = displayId[1..];
            }

            list.Add(new WeatherDisplayInfo(displayId, parsed.WindDirectionDeg, parsed.WindSpeedKts, parsed.WindGustKts, parsed.AltimeterInHg));
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
}
