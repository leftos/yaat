using System.IO;
using System.IO.Compression;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Services;

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
    private async Task SkipForward15()
    {
        await RewindToSeconds(Math.Min(PlaybackTapeEnd, ScenarioElapsedSeconds + 15));
    }

    [RelayCommand]
    private async Task SkipForward30()
    {
        await RewindToSeconds(Math.Min(PlaybackTapeEnd, ScenarioElapsedSeconds + 30));
    }

    [RelayCommand]
    private async Task JumpToEnd()
    {
        await RewindToSeconds(PlaybackTapeEnd);
    }

    [RelayCommand]
    private async Task TogglePlayback()
    {
        try
        {
            var cmd = IsPaused ? "UNPAUSE" : "PAUSE";
            await _connection.SendCommandAsync("", cmd, _preferences.UserInitials);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Toggle playback failed");
            StatusText = $"Playback error: {ex.Message}";
        }
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
            OnPropertyChanged(nameof(TimelineMaximum));
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
                        var model = Models.AircraftModel.FromDto(dto, ComputeDistance);
                        ApplyAutoClearedToLand(model);
                        Aircraft.Add(model);
                    }
                }

                ScenarioElapsedSeconds = targetSeconds;
                OnPropertyChanged(nameof(ElapsedTimeDisplay));
                OnPropertyChanged(nameof(TimelineMaximum));
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
        if (IsExportingRecording)
        {
            return;
        }

        IsExportingRecording = true;
        ExportingStatusText = "Preparing recording...";
        IsExportIndeterminate = true;
        ExportProgress = 0;
        var wasPaused = IsPaused;
        _connection.ExportRecordingProgress += OnExportRecordingProgress;
        try
        {
            if (!wasPaused)
            {
                await _connection.SendCommandAsync("", "PAUSE", _preferences.UserInitials);
            }

            var compressedBytes = await _connection.ExportRecordingAsync();
            IsExportingRecording = false;

            if (compressedBytes is null)
            {
                StatusText = "No recording available";
                return;
            }

            var path = await _filePicker.SaveFileAsync(
                new SaveFileOptions(
                    Title: "Save Recording",
                    SuggestedFileName: $"{SanitizeFileName(ActiveScenarioName ?? "recording")}.yaat-recording.zip",
                    Filters: [new FilePickerFilter("YAAT Recording", ["*.yaat-recording.zip", "*.yaat-recording.br"])],
                    DefaultExtension: "yaat-recording.zip"
                )
            );

            if (path is null)
            {
                return;
            }

            await File.WriteAllBytesAsync(path, compressedBytes);

            StatusText = "Recording saved";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Save recording failed");
            StatusText = $"Save recording error: {ex.Message}";
        }
        finally
        {
            _connection.ExportRecordingProgress -= OnExportRecordingProgress;
            IsExportingRecording = false;
            if (!wasPaused)
            {
                try
                {
                    await _connection.SendCommandAsync("", "UNPAUSE", _preferences.UserInitials);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Failed to unpause after save recording");
                }
            }
        }
    }

    [RelayCommand]
    private async Task SaveBugReportBundle()
    {
        if (IsExportingRecording)
        {
            return;
        }

        IsExportingRecording = true;
        ExportingStatusText = "Preparing bug report bundle...";
        IsExportIndeterminate = true;
        ExportProgress = 0;
        _connection.ExportRecordingProgress += OnExportRecordingProgress;
        try
        {
            if (!IsPaused)
            {
                await _connection.SendCommandAsync("", "PAUSE", _preferences.UserInitials);
            }

            var compressedBytes = await _connection.ExportRecordingAsync();
            _connection.ExportRecordingProgress -= OnExportRecordingProgress;
            IsExportingRecording = false;

            if (compressedBytes is null)
            {
                StatusText = "No recording available";
                return;
            }

            var path = await _filePicker.SaveFileAsync(
                new SaveFileOptions(
                    Title: "Save Bug Report Bundle",
                    SuggestedFileName: $"{SanitizeFileName(ActiveScenarioName ?? "recording")}.yaat-bug-report-bundle.zip",
                    Filters: [new FilePickerFilter("YAAT Bug Report Bundle", ["*.yaat-bug-report-bundle.zip"])],
                    DefaultExtension: "yaat-bug-report-bundle.zip"
                )
            );

            if (path is null)
            {
                return;
            }

            await using var stream = File.Create(path);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

            using var recordingStream = new MemoryStream(compressedBytes);
            using var recordingZip = new ZipArchive(recordingStream, ZipArchiveMode.Read);
            foreach (var sourceEntry in recordingZip.Entries)
            {
                var destEntry = archive.CreateEntry(sourceEntry.FullName);
                await using var sourceStream = sourceEntry.Open();
                await using var destStream = destEntry.Open();
                await sourceStream.CopyToAsync(destStream);
            }

            AddFileToArchive(archive, AppLog.LogPath, "yaat-client.log");

            if (IsLocalServer(_connectedServerUrl))
            {
                try
                {
                    var serverLogPath = await _connection.GetServerLogPathAsync();
                    if (serverLogPath is not null && File.Exists(serverLogPath))
                    {
                        AddFileToArchive(archive, serverLogPath, "yaat-server.log");
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Could not retrieve server log path");
                }
            }

            StatusText = "Bug report bundle saved";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Save bug report bundle failed");
            StatusText = $"Save bug report bundle error: {ex.Message}";
        }
        finally
        {
            _connection.ExportRecordingProgress -= OnExportRecordingProgress;
            IsExportingRecording = false;
        }
    }

    private void OnExportRecordingProgress(int currentSeconds, int totalSeconds)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (totalSeconds > 0)
            {
                IsExportIndeterminate = false;
                ExportProgress = (double)currentSeconds / totalSeconds;
                ExportingStatusText = $"Generating snapshots ({currentSeconds}/{totalSeconds}s)...";
            }
        });
    }

    private bool _timelineMarkerRefreshInFlight;
    private bool _timelineMarkerRefreshPending;

    // Bounded command-marker buffer. Sized to a few hours' worth of busy session traffic
    // (~1 command/aircraft/minute × 30 aircraft × 60 min = 1,800); older entries drop off.
    private const int CommandMarkerBufferCapacity = 2000;
    private readonly List<TimelineMarkerVm> _commandMarkerHistory = [];
    private readonly Lock _commandMarkerLock = new();

    /// <summary>
    /// Capture a successfully-dispatched controller command as a marker for the M12.5
    /// timeline overlay. Called from <c>SendCommandAsync</c> dispatch paths after the
    /// server acknowledges. Global commands (empty callsign) are skipped — they're not
    /// per-aircraft and would clutter the rail.
    /// </summary>
    /// <param name="serverElapsedSeconds">
    /// Scenario elapsed seconds at the moment of server-side dispatch (from
    /// <see cref="Services.CommandResultDto.ServerElapsedSeconds"/>). When 0 (legacy
    /// servers or non-scenario contexts), fall back to the client's
    /// <see cref="ScenarioElapsedSeconds"/> which lags by the broadcast cadence.
    /// </param>
    public void RecordCommandMarker(string callsign, string canonical, double serverElapsedSeconds = 0)
    {
        if (string.IsNullOrWhiteSpace(callsign))
        {
            return;
        }

        double timeSeconds = serverElapsedSeconds > 0 ? serverElapsedSeconds : ScenarioElapsedSeconds;
        var marker = new TimelineMarkerVm
        {
            Id = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"cmd-{timeSeconds:0.000}-{callsign}-{canonical}"),
            Kind = TimelineMarkerKind.Command,
            TimeSeconds = timeSeconds,
            Title = canonical,
            Callsigns = [callsign],
            CommandText = canonical,
        };

        lock (_commandMarkerLock)
        {
            _commandMarkerHistory.Add(marker);
            if (_commandMarkerHistory.Count > CommandMarkerBufferCapacity)
            {
                _commandMarkerHistory.RemoveRange(0, _commandMarkerHistory.Count - CommandMarkerBufferCapacity);
            }
        }

        // Live-add to the visible collection without waiting for the next 5 s poll so the
        // user sees their command land on the rail immediately.
        var filter = TimelineFilterCallsign;
        if (filter is null || string.Equals(filter, callsign, StringComparison.OrdinalIgnoreCase))
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => TimelineMarkers.Add(marker));
        }
    }

    /// <summary>
    /// Fetches the current session report and rebuilds <see cref="TimelineMarkers"/>.
    /// Called by the timeline-marker poll (MainWindow code-behind) and on demand from
    /// the Aircraft tab "Show on Timeline" cross-link. Idempotent; if another refresh is
    /// already in flight when a new request arrives, the request is queued so that the
    /// filter state observed at call time gets a refresh pass — preventing the Aircraft tab
    /// click from silently waiting for the next 5 s poll.
    /// </summary>
    public async Task RefreshTimelineMarkersAsync()
    {
        if (!IsTimelineAvailable)
        {
            return;
        }
        if (_timelineMarkerRefreshInFlight)
        {
            _timelineMarkerRefreshPending = true;
            return;
        }
        _timelineMarkerRefreshInFlight = true;
        try
        {
            var report = await _connection.GetSessionReportAsync();
            if (report is null)
            {
                return;
            }

            var filter = TimelineFilterCallsign;
            var rebuilt = new List<TimelineMarkerVm>(report.Timeline.Count + _commandMarkerHistory.Count);
            foreach (var ev in report.Timeline)
            {
                if (filter is not null && !ev.Callsigns.Any(c => string.Equals(c, filter, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                rebuilt.Add(
                    new TimelineMarkerVm
                    {
                        Id = ev.Id,
                        Kind = TimelineMarkerKind.Finding,
                        TimeSeconds = ev.StartedAtSeconds,
                        Severity = ev.Severity,
                        Title = ev.Title,
                        Category = ev.Category,
                        Callsigns = [.. ev.Callsigns],
                    }
                );
            }

            lock (_commandMarkerLock)
            {
                foreach (var cmd in _commandMarkerHistory)
                {
                    if (filter is not null && !cmd.Callsigns.Any(c => string.Equals(c, filter, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }
                    rebuilt.Add(cmd);
                }
            }

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                TimelineMarkers.Clear();
                foreach (var m in rebuilt)
                {
                    TimelineMarkers.Add(m);
                }
            });
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Timeline marker refresh failed");
        }
        finally
        {
            _timelineMarkerRefreshInFlight = false;
        }

        // If a refresh request arrived while this one was in flight (typically the user
        // clicking "Show on Timeline" between the in-flight call and its completion), run
        // one more pass against the latest filter state. The flag is read-and-clear so a
        // pile-up of requests collapses to a single follow-up.
        if (_timelineMarkerRefreshPending)
        {
            _timelineMarkerRefreshPending = false;
            await RefreshTimelineMarkersAsync();
        }
    }

    /// <summary>
    /// Apply a per-aircraft filter to the timeline markers and rewind to that aircraft's
    /// spawn time. Called by the Session Report's Aircraft tab "Show on Timeline" button
    /// (M12.4 → M12.5 cross-link).
    /// </summary>
    public async Task ShowAircraftOnTimelineAsync(string callsign, double spawnedAtSeconds)
    {
        TimelineFilterCallsign = callsign;
        await RefreshTimelineMarkersAsync();
        if (spawnedAtSeconds > 0 && spawnedAtSeconds <= TimelineMaximum)
        {
            await RewindToSeconds(spawnedAtSeconds);
        }
    }

    [RelayCommand]
    private void ClearTimelineFilter()
    {
        TimelineFilterCallsign = null;
        // Fire-and-forget — the marker list will rebuild without the filter.
        _ = RefreshTimelineMarkersAsync();
    }

    private static bool IsLocalServer(string url)
    {
        return url.Contains("localhost", StringComparison.OrdinalIgnoreCase) || url.Contains("127.0.0.1", StringComparison.Ordinal);
    }

    private static void AddFileToArchive(ZipArchive archive, string filePath, string entryName)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        var entry = archive.CreateEntry(entryName);
        using var entryStream = entry.Open();
        using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fileStream.CopyTo(entryStream);
    }

    [RelayCommand]
    private async Task LoadRecording()
    {
        try
        {
            var path = await _filePicker.OpenFileAsync(
                new OpenFileOptions(
                    Title: "Load Recording",
                    Filters:
                    [
                        new FilePickerFilter(
                            "YAAT Recording",
                            ["*.yaat-recording.zip", "*.yaat-recording.br", "*.yaat-recording.json", "*.yaat-bug-report-bundle.zip"]
                        ),
                    ]
                )
            );

            if (path is null)
            {
                return;
            }

            var recordingBytes = await File.ReadAllBytesAsync(path);

            StatusText = "Loading recording...";
            var result = await _connection.LoadRecordingAsync(recordingBytes);
            if (result is null || !result.Success)
            {
                StatusText = $"Load recording failed: {result?.Error ?? "Unknown error"}";
                return;
            }

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                ApplyRecordingResult(result);
                StatusText = $"Recording loaded: {result.ScenarioName}";
                AddSystemEntry($"Recording loaded: {result.ScenarioName}");
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Load recording failed");
            StatusText = $"Load recording error: {ex.Message}";
        }
    }

    internal void ApplyRecordingResult(RewindResultDto result)
    {
        ActiveScenarioId = result.ScenarioId;
        ActiveScenarioName = result.ScenarioName;
        ActiveScenarioPrimaryAirportId = NormalizeFavoriteAirportId(result.PrimaryAirportId);
        _commandInput.PrimaryAirportId = result.PrimaryAirportId;
        Radar.SetPrimaryAirportId(result.PrimaryAirportId);
        SetRadarAirportPosition(result.PrimaryAirportId);
        ApplySimState(result.IsPaused, result.SimRate, result.ElapsedSeconds, result.IsPlayback, result.TapeEnd);

        if (!string.IsNullOrEmpty(result.PrimaryAirportId))
        {
            SetDistanceReference(result.PrimaryAirportId);
        }

        _studentPositionType = result.StudentPositionType;
        _isAutoClearedToLand = _preferences.GetAutoClearedToLand(_studentPositionType);

        Aircraft.Clear();
        if (result.Aircraft is not null)
        {
            foreach (var dto in result.Aircraft)
            {
                var model = Models.AircraftModel.FromDto(dto, ComputeDistance);
                ApplyAutoClearedToLand(model);
                Aircraft.Add(model);
            }
        }

        if (!string.IsNullOrEmpty(result.PrimaryAirportId))
        {
            _ = Ground.LoadLayoutAsync(result.PrimaryAirportId);
        }

        var artccId = result.ArtccId ?? _preferences.ArtccId;
        if (!string.IsNullOrEmpty(artccId))
        {
            _ = Radar.LoadVideoMapsForArtccAsync(artccId, result.PrimaryAirportId, result.ScenarioId);
            if (!string.IsNullOrEmpty(result.PrimaryAirportId))
            {
                _ = Ground.LoadTowerCabLayersAsync(artccId, result.PrimaryAirportId);
            }
        }

        ShowTimelineBar = true;
    }

    private static string FormatTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}";
    }
}
