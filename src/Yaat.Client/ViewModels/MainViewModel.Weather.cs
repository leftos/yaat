using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Services;

namespace Yaat.Client.ViewModels;

/// <summary>
/// Weather loading and clearing commands, and the WeatherChanged event handler.
/// </summary>
public partial class MainViewModel
{
    private readonly LiveWeatherService _liveWeather = new();
    private readonly ArtccAirportResolver _airportResolver = new();

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
    /// Loads weather from pre-fetched JSON (e.g. from the vNAS data API).
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
        await LoadLiveWeatherCoreAsync(includeTafs: false);
    }

    [RelayCommand(CanExecute = nameof(CanLoadLiveWeather))]
    private async Task LoadLiveWeatherWithTafsAsync()
    {
        await LoadLiveWeatherCoreAsync(includeTafs: true);
    }

    private bool CanLoadLiveWeather() =>
        IsConnected && ActiveRoomId is not null && !string.IsNullOrWhiteSpace(_preferences.ArtccId) && _commandInput.FixDb is not null;

    private async Task LoadLiveWeatherCoreAsync(bool includeTafs)
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

            var profile = await _liveWeather.BuildLiveWeatherAsync(artccId, airportIds, fixDb, includeTafs);
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

    private void OnWeatherChanged(WeatherChangedDto dto)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            ActiveWeatherName = dto.Name;
        });
    }
}
