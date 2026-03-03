using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Services;

namespace Yaat.Client.ViewModels;

/// <summary>
/// Weather loading and clearing commands, and the WeatherChanged event handler.
/// </summary>
public partial class MainViewModel
{
    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task LoadWeatherAsync(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        if (ActiveRoomId is null)
        {
            StatusText = "Join a room before loading weather";
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

    private bool CanClearWeather() => IsConnected && ActiveWeatherName is not null;

    private void OnWeatherChanged(WeatherChangedDto dto)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            ActiveWeatherName = dto.Name;
            if (dto.Name is not null)
            {
                var layerCount = dto.WindLayers?.Count ?? 0;
                AddSystemEntry($"Weather loaded: {dto.Name} ({layerCount} layers)");
            }
            else
            {
                AddSystemEntry("Weather cleared");
            }
        });
    }
}
