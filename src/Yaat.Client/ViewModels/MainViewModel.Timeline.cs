using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Yaat.Client.ViewModels;

public partial class MainViewModel
{
    [RelayCommand]
    private async Task RewindToStart()
    {
        await RewindToSeconds(0);
    }

    [RelayCommand]
    private async Task RewindBack30()
    {
        await RewindToSeconds(Math.Max(0, ScenarioElapsedSeconds - 30));
    }

    [RelayCommand]
    private async Task RewindBack15()
    {
        await RewindToSeconds(Math.Max(0, ScenarioElapsedSeconds - 15));
    }

    [RelayCommand]
    private async Task TakeControl()
    {
        try
        {
            await _connection.TakeControlAsync();
            IsPlaybackMode = false;
            PlaybackTapeEnd = 0;
            OnPropertyChanged(nameof(TapeEndDisplay));
            StatusText = "Took control";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "TakeControl failed");
            StatusText = $"Take control error: {ex.Message}";
        }
    }

    public async Task RewindToSeconds(double targetSeconds)
    {
        try
        {
            StatusText = $"Rewinding to {FormatTime(targetSeconds)}...";
            var result = await _connection.RewindToAsync(targetSeconds);
            if (result is null || !result.Success)
            {
                StatusText = $"Rewind failed: {result?.Error ?? "Unknown error"}";
                return;
            }

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Aircraft.Clear();
                if (result.Aircraft is not null)
                {
                    foreach (var dto in result.Aircraft)
                    {
                        Aircraft.Add(Models.AircraftModel.FromDto(dto, ComputeDistance));
                    }
                }

                ScenarioElapsedSeconds = targetSeconds;
                OnPropertyChanged(nameof(ElapsedTimeDisplay));
                StatusText = $"Rewound to {FormatTime(targetSeconds)}";
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Rewind failed");
            StatusText = $"Rewind error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SaveRecording()
    {
        try
        {
            var json = await _connection.ExportRecordingAsync();
            if (json is null)
            {
                StatusText = "No recording available";
                return;
            }

            var window = Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null;

            if (window is null)
            {
                return;
            }

            var file = await window.StorageProvider.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    Title = "Save Recording",
                    DefaultExtension = "yaat-recording",
                    FileTypeChoices = [new FilePickerFileType("YAAT Recording") { Patterns = ["*.yaat-recording"] }],
                    SuggestedFileName = $"{ActiveScenarioName ?? "recording"}.yaat-recording",
                }
            );

            if (file is null)
            {
                return;
            }

            await using var stream = await file.OpenWriteAsync();
            await using var writer = new System.IO.StreamWriter(stream);
            await writer.WriteAsync(json);

            StatusText = "Recording saved";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Save recording failed");
            StatusText = $"Save recording error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task LoadRecording()
    {
        try
        {
            var window = Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null;

            if (window is null)
            {
                return;
            }

            var files = await window.StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = "Load Recording",
                    AllowMultiple = false,
                    FileTypeFilter = [new FilePickerFileType("YAAT Recording") { Patterns = ["*.yaat-recording"] }],
                }
            );

            if (files.Count == 0)
            {
                return;
            }

            await using var stream = await files[0].OpenReadAsync();
            using var reader = new System.IO.StreamReader(stream);
            var json = await reader.ReadToEndAsync();

            StatusText = "Loading recording...";
            var result = await _connection.LoadRecordingAsync(json);
            if (result is null || !result.Success)
            {
                StatusText = $"Load recording failed: {result?.Error ?? "Unknown error"}";
                return;
            }

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Aircraft.Clear();
                if (result.Aircraft is not null)
                {
                    foreach (var dto in result.Aircraft)
                    {
                        Aircraft.Add(Models.AircraftModel.FromDto(dto, ComputeDistance));
                    }
                }

                StatusText = "Recording loaded";
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Load recording failed");
            StatusText = $"Load recording error: {ex.Message}";
        }
    }

    private static string FormatTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}";
    }
}
