using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Sim.Logging;
using Yaat.Sim.Simulation;

namespace Yaat.Client.ViewModels;

public partial class MainViewModel
{
    [RelayCommand(CanExecute = nameof(CanRewind))]
    private async Task RewindToStart() => await RewindToSeconds(0);

    [RelayCommand(CanExecute = nameof(CanRewind))]
    private async Task RewindBack30() => await RewindToSeconds(Math.Max(0, ScenarioElapsedSeconds - 30));

    [RelayCommand(CanExecute = nameof(CanRewind))]
    private async Task RewindBack15() => await RewindToSeconds(Math.Max(0, ScenarioElapsedSeconds - 15));

    [RelayCommand(CanExecute = nameof(CanRewind))]
    private async Task SkipForward15() => await RewindToSeconds(Math.Min(PlaybackTapeEnd, ScenarioElapsedSeconds + 15));

    [RelayCommand(CanExecute = nameof(CanRewind))]
    private async Task SkipForward30() => await RewindToSeconds(Math.Min(PlaybackTapeEnd, ScenarioElapsedSeconds + 30));

    [RelayCommand(CanExecute = nameof(CanRewind))]
    private async Task JumpToEnd() => await RewindToSeconds(PlaybackTapeEnd);

    /// <summary>Every timeline jump is a server <c>RewindTo</c>, which the server refuses while a scenario loads.</summary>
    private bool CanRewind() => !IsRoomLoading;

    private void NotifyRewindCommandsCanExecuteChanged()
    {
        RewindToStartCommand.NotifyCanExecuteChanged();
        RewindBack30Command.NotifyCanExecuteChanged();
        RewindBack15Command.NotifyCanExecuteChanged();
        SkipForward15Command.NotifyCanExecuteChanged();
        SkipForward30Command.NotifyCanExecuteChanged();
        JumpToEndCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task TogglePlayback()
    {
        try
        {
            if (IsPaused && !await ConfirmResumeAsync())
            {
                return;
            }

            string cmd = IsPaused ? "UNPAUSE" : "PAUSE";
            await _connection.SendCommandAsync("", cmd, _preferences.UserInitials);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Toggle playback failed");
            StatusText = $"Playback error: {ex.Message}";
        }
    }

    /// <summary>
    /// Set by the owning view to confirm the destructive, playback-ending Take Control. Returns
    /// true to proceed, false to cancel. Null = no confirmation wired (proceeds immediately).
    /// </summary>
    public Func<Task<bool>>? TakeControlConfirmation { get; set; }

    [RelayCommand]
    private async Task TakeControl()
    {
        if (IsPlaybackMode && TakeControlConfirmation is not null)
        {
            bool confirmed = await TakeControlConfirmation();
            if (!confirmed)
            {
                return;
            }
        }

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

    /// <summary>
    /// Moves the room's sim to <paramref name="targetSeconds"/>. The returned task completes once the client has swapped in the
    /// rewound aircraft and set the status line; the outcome says whether the rewind worked, with that status line.
    /// </summary>
    public async Task<RewindOutcome> RewindToSeconds(double targetSeconds)
    {
        // Every timeline jump (buttons, scrub, markers, bookmarks) comes through here; the server refuses RewindTo mid-load.
        if (IsRoomLoading)
        {
            _log.LogInformation("Rewind to {Seconds}s not sent: a scenario is loading in the room", targetSeconds);
            StatusText = "Rewind unavailable while a scenario loads";
            return new RewindOutcome(false, StatusText);
        }

        try
        {
            StatusText = $"Rewinding to {FormatTime(targetSeconds)}...";
            RewindResultDto? result = await _connection.RewindToAsync(targetSeconds);
            if (result is null || !result.Success)
            {
                string failed = $"Rewind failed: {result?.Error ?? "Unknown error"}";
                StatusText = failed;
                return new RewindOutcome(false, failed);
            }

            string rewound = $"Rewound to {FormatTime(targetSeconds)}";
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => ApplyRewindResult(result, targetSeconds, rewound));
            return new RewindOutcome(true, rewound);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Rewind failed");
            string error = $"Rewind error: {ex.Message}";
            StatusText = error;
            return new RewindOutcome(false, error);
        }
    }

    /// <summary>
    /// The rewinder's own successful result, on the UI thread: the rewound aircraft replace the list, the clock moves to
    /// <paramref name="targetSeconds"/>, the result's active runways replace the room's and the status line reads
    /// <paramref name="status"/>.
    /// </summary>
    public void ApplyRewindResult(RewindResultDto result, double targetSeconds, string status)
    {
        Aircraft.Clear();
        if (result.Aircraft is not null)
        {
            foreach (AircraftDto dto in result.Aircraft)
            {
                var model = Models.AircraftModel.FromDto(dto, ComputeDistance);
                ApplyAutoClearedToLand(model);
                Aircraft.Add(model);
            }
        }

        ScenarioElapsedSeconds = targetSeconds;
        ApplyActiveRunways(result.ActiveRunways);
        OnPropertyChanged(nameof(ElapsedTimeDisplay));
        OnPropertyChanged(nameof(TimelineMaximum));
        StatusText = status;
    }

    /// <summary>
    /// A test seam: replaces the server export for Save Recording and the bug report bundle; called with whether the
    /// recording is to be pseudonymised. Null = export from the connected server.
    /// </summary>
    public Func<bool, Task<byte[]?>>? RecordingExporter { get; set; }

    /// <summary>A test seam: replaces the server's <c>GetSessionServerLog</c> for the bug report bundle. Null = ask the connected server.</summary>
    public Func<Task<SessionServerLogDto>>? SessionServerLogFetcher { get; set; }

    private Task<byte[]?> ExportRecordingAsync(bool anonymize) =>
        RecordingExporter?.Invoke(anonymize) ?? _connection.ExportRecordingAsync(BuildInfo.Version, BuildInfo.BuildKind, anonymize);

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
        bool wasPaused = IsPaused;
        _connection.ExportRecordingProgress += OnExportRecordingProgress;
        try
        {
            if (!wasPaused)
            {
                await _connection.SendCommandAsync("", "PAUSE", _preferences.UserInitials);
            }

            // A saved recording is the user's own: it keeps the real initials.
            byte[]? compressedBytes = await ExportRecordingAsync(anonymize: false);
            IsExportingRecording = false;

            if (compressedBytes is null)
            {
                StatusText = "No recording available";
                return;
            }

            string? path = await _filePicker.SaveFileAsync(
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

            byte[] bytesToSave = compressedBytes;
            if (Bookmarks.Count > 0 && RecordingCompression.IsZipArchive(compressedBytes))
            {
                bytesToSave = RecordingArchive.WriteBookmarks(compressedBytes, SnapshotBookmarks());
            }

            await File.WriteAllBytesAsync(path, bytesToSave);

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

        try
        {
            byte[]? compressedBytes = await FetchRecordingForBundleAsync();
            if (compressedBytes is null)
            {
                StatusText = "No recording available";
                return;
            }

            string? path = await _filePicker.SaveFileAsync(
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

            await WriteBugReportBundleAsync(path, compressedBytes);

            StatusText = "Bug report bundle saved";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Save bug report bundle failed");
            StatusText = $"Save bug report bundle error: {ex.Message}";
        }
    }

    /// <summary>
    /// Pauses the room, exports the session recording and shows export progress while it runs. Returns
    /// null when the server has no recording to give (or another export is already in flight). Shared by
    /// Save Bug Report Bundle and File Bug Report; the caller decides what to do with the bytes.
    /// </summary>
    private async Task<byte[]?> FetchRecordingForBundleAsync()
    {
        if (IsExportingRecording)
        {
            return null;
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

            // A bundle is posted publicly: the server pseudonymises the people in the recording.
            return await ExportRecordingAsync(anonymize: true);
        }
        finally
        {
            _connection.ExportRecordingProgress -= OnExportRecordingProgress;
            IsExportingRecording = false;
        }
    }

    /// <summary>
    /// Writes the bug report bundle at <paramref name="path"/>: the recording's own entries (with the
    /// timeline bookmarks folded in) and the client logs (the live one and the rotated previous sessions),
    /// plus the server log whenever the client is in a room, recording or not. The client logs, the server log's
    /// leftovers and the bookmarks' creators are scrubbed with the server log's participant list, so a person carries
    /// the same pseudonym in all of them; the log files on disk are left as they are. In a room whose list the server
    /// could not give, everything is scrubbed with the client's own list and the bundle carries
    /// <see cref="AnonymizationNoteEntry"/> saying the pseudonyms may not line up across files. When connected, the bundle
    /// also carries <see cref="ServerEntry"/> with the dialled server's scheme, host and port, a host other than the
    /// official server's or loopback written as <c>{server}</c>, as the logs write it.
    /// </summary>
    public async Task WriteBugReportBundleAsync(string path, byte[]? recordingBytes)
    {
        SessionServerLogDto? serverLog = IsInRoom ? await FetchSessionServerLogAsync() : null;
        bool serverListMissing = IsInRoom && serverLog is not { Participants.Count: > 0 };
        var scrubContext = new ClientLogScrubContext(
            serverLog is { Participants.Count: > 0 } ? serverLog.Participants : BuildLocalLogParticipants(),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.UserName,
            Environment.MachineName,
            ClientLogScrubber.ServerHostsOf([ConnectedServerUrl, .. _preferences.SavedServers.Select(s => s.Url)])
        );

        // A user who closed and reopened YAAT before saving left the session they are reporting in a
        // rotated log; the live one holds only the relaunch.
        List<(string Path, string EntryName)> logs =
        [
            (AppLog.LogPath, "yaat-client.log"),
            .. FileLoggerProvider.PreviousLogs(AppLog.LogPath).Select(p => (p.Path, $"yaat-client.{p.Generation}.log")),
        ];

        // Reading and scrubbing a long log takes a while, so it stays off the UI thread.
        List<ScrubbedLog> scrubbedLogs = await Task.Run(() => ScrubLogs(logs, scrubContext));

        await using FileStream stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        if (recordingBytes is not null)
        {
            byte[] bytes = recordingBytes;
            if (Bookmarks.Count > 0 && RecordingCompression.IsZipArchive(bytes))
            {
                // Creators by the logs' pseudonyms; a bookmark's name stays as typed.
                bytes = RecordingArchive.WriteBookmarks(
                    bytes,
                    RecordingPseudonymizer.PseudonymizeBookmarks(SnapshotBookmarks(), scrubContext.Participants)
                );
            }

            using var recordingStream = new MemoryStream(bytes);
            using var recordingZip = new ZipArchive(recordingStream, ZipArchiveMode.Read);
            foreach (ZipArchiveEntry sourceEntry in recordingZip.Entries)
            {
                ZipArchiveEntry destEntry = archive.CreateEntry(sourceEntry.FullName);
                await using Stream sourceStream = sourceEntry.Open();
                await using Stream destStream = destEntry.Open();
                await sourceStream.CopyToAsync(destStream);
            }
        }

        foreach (ScrubbedLog log in scrubbedLogs)
        {
            AddLogToArchive(archive, log);
        }

        if (!string.IsNullOrWhiteSpace(ConnectedServerUrl))
        {
            AddTextToArchive(archive, ServerEntry, ServerEntryJson(ClientLogScrubber.Scrub(ServerAuthority(ConnectedServerUrl), scrubContext)));
        }

        // Anonymized on the server already; the client's fail-closed passes catch anyone the server's list does not hold.
        if (!string.IsNullOrEmpty(serverLog?.Text))
        {
            AddTextToArchive(archive, "yaat-server.log", ClientLogScrubber.ScrubLeftoverPeople(serverLog.Text, scrubContext.Participants));
        }

        if (serverListMissing)
        {
            AddTextToArchive(archive, AnonymizationNoteEntry, AnonymizationNote);
        }
    }

    private const string AnonymizationNoteEntry = "ANONYMIZATION-NOTE.txt";

    /// <summary>The bundle entry naming the server the client dialled: <c>{"url": ...}</c>, its host scrubbed as the logs' are.</summary>
    private const string ServerEntry = "server.json";

    /// <summary>The scheme, host and port of <paramref name="url"/>, dropping any credentials, path and query; the trimmed text when it does not parse.</summary>
    private static string ServerAuthority(string url)
    {
        string trimmed = url.Trim();
        // SchemeAndServer, not GetLeftPart(UriPartial.Authority): the authority part keeps any user:password@.
        return Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri)
            ? uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped)
            : trimmed;
    }

    private static string ServerEntryJson(string url)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("url", url);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private const string AnonymizationNote =
        "Pseudonyms in this report may not match across files: the server's participant list was unavailable when the report was built.";

    private async Task<SessionServerLogDto?> FetchSessionServerLogAsync()
    {
        try
        {
            return SessionServerLogFetcher is null ? await _connection.GetSessionServerLogAsync() : await SessionServerLogFetcher();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not retrieve session server log");
            return null;
        }
    }

    /// <summary>
    /// The participants this client knows of, for scrubbing its logs when the server sent none (no room, or the
    /// server's list could not be got): its own identity first, then the room's members and the CRC clients it has seen.
    /// </summary>
    public IReadOnlyList<LogParticipant> BuildLocalLogParticipants()
    {
        VatsimIdentity? own = _auth.GetIdentity(ConnectedServerUrl);
        List<(string Cid, string Initials)> members = [.. RoomMembers.Select(m => (m.Cid, m.Initials))];
        List<(string Cid, string RealName)> crcIdentities =
        [
            .. CrcRoomMembers.Where(m => m.Cid is not null).Select(m => (m.Cid!, RealNameOf(m.DisplayName, m.PositionId))),
            .. CrcLobbyClients.Where(c => c.Cid is not null).Select(c => (c.Cid!, RealNameOf(c.DisplayName, c.PositionId))),
        ];

        if (own is not null)
        {
            members.Insert(0, (own.Cid, _preferences.UserInitials));
            crcIdentities.Insert(0, (own.Cid, own.Name));
        }

        return SessionLogAnonymizer.BuildParticipants(members, crcIdentities);
    }

    // A CRC display name is "{callsign} ({realName})"; a client with no position shows its bare real name instead, unless
    // it has none ("unknown") or the name is a bare position callsign.
    private static string RealNameOf(string? displayName, string? positionId)
    {
        if (CrcDisplayName.TryGetRealName(displayName, out string? realName))
        {
            return realName;
        }

        bool isBareName =
            string.IsNullOrEmpty(positionId)
            && !string.IsNullOrWhiteSpace(displayName)
            && (displayName != CrcDisplayName.Unknown)
            && !CrcDisplayName.IsPositionCallsign(displayName);
        return isBareName ? displayName! : "";
    }

    private sealed record ScrubbedLog(string EntryName, string Text, DateTime LastWriteTime);

    private static List<ScrubbedLog> ScrubLogs(List<(string Path, string EntryName)> logs, ClientLogScrubContext scrubContext)
    {
        List<ScrubbedLog> scrubbed = [];
        foreach ((string logPath, string entryName) in logs)
        {
            if (File.Exists(logPath))
            {
                scrubbed.Add(
                    new ScrubbedLog(entryName, ClientLogScrubber.Scrub(ReadSharedText(logPath), scrubContext), File.GetLastWriteTime(logPath))
                );
            }
        }

        return scrubbed;
    }

    /// <summary>
    /// Writes the room's ASDE-X / SAAB SAID temp data to a folder as one sidecar JSON per facility, so
    /// a mentor can commit hand-drawn geometry (restricted areas, final-approach corridors, mile
    /// markers) into <c>src/Yaat.Sim/Data/ARTCCs/{ARTCC}/SurfaceTempData/</c>. Once committed, every
    /// room seeds from it instead of relying on the server's runtime overlay.
    /// </summary>
    [RelayCommand]
    private async Task ExportSurfaceTempData()
    {
        try
        {
            List<SurfaceTempDataExportDto> exports = await _connection.ExportSurfaceTempDataAsync();
            if (exports.Count == 0)
            {
                StatusText = "No ASDE-X/SAID temp data to export";
                return;
            }

            string? folder = await _filePicker.OpenFolderAsync(new OpenFolderOptions("Export ASDE-X / SAID Temp Data"));
            if (folder is null)
            {
                return;
            }

            foreach (SurfaceTempDataExportDto export in exports)
            {
                string path = Path.Combine(folder, $"{SanitizeFileName(export.FacilityId)}.json");
                await File.WriteAllTextAsync(path, export.Json);
            }

            StatusText = $"Exported temp data for {exports.Count} facilit{(exports.Count == 1 ? "y" : "ies")}";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Export surface temp data failed");
            StatusText = $"Export temp data error: {ex.Message}";
        }
    }

    /// <summary>Opens the confirmation for discarding drawn ASDE-X / SAID geometry.</summary>
    [RelayCommand]
    private void ResetSurfaceTempData() => ShowResetSurfaceTempDataConfirmation = true;

    [RelayCommand]
    private void CancelResetSurfaceTempData() => ShowResetSurfaceTempDataConfirmation = false;

    /// <summary>
    /// Discards every drawn area, label, and SET on the server's surface displays and puts each facility
    /// back on the geometry shipped with the project. Irreversible — the server keeps drawn geometry
    /// permanently, so this is the only way to undo a bad draw or restore an object someone deleted.
    /// </summary>
    [RelayCommand]
    private async Task ConfirmResetSurfaceTempData()
    {
        ShowResetSurfaceTempDataConfirmation = false;
        try
        {
            int facilities = await _connection.ResetSurfaceTempDataAsync();
            StatusText =
                facilities == 0
                    ? "No ASDE-X/SAID temp data to reset"
                    : $"Reset temp data to defaults for {facilities} facilit{(facilities == 1 ? "y" : "ies")}";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Reset surface temp data failed");
            StatusText = $"Reset temp data error: {ex.Message}";
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
        string? filter = TimelineFilterCallsign;
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
            SessionReportDto? report = await _connection.GetSessionReportAsync();
            if (report is null)
            {
                return;
            }

            string? filter = TimelineFilterCallsign;
            var rebuilt = new List<TimelineMarkerVm>(report.Timeline.Count + _commandMarkerHistory.Count);
            foreach (SoloTrainingEventDto ev in report.Timeline)
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
                foreach (TimelineMarkerVm cmd in _commandMarkerHistory)
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
                foreach (TimelineMarkerVm m in rebuilt)
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

    private static void AddTextToArchive(ZipArchive archive, string entryName, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(entryName);
        using Stream entryStream = entry.Open();
        using var writer = new StreamWriter(entryStream);
        writer.Write(content);
    }

    private static void AddLogToArchive(ZipArchive archive, ScrubbedLog log)
    {
        ZipArchiveEntry entry = archive.CreateEntry(log.EntryName);
        entry.LastWriteTime = log.LastWriteTime;
        using Stream entryStream = entry.Open();
        using var writer = new StreamWriter(entryStream);
        writer.Write(log.Text);
    }

    // The live log is held open for writing by the logger, so it is read with a share that allows that.
    private static string ReadSharedText(string filePath)
    {
        using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fileStream);
        return reader.ReadToEnd();
    }

    [RelayCommand]
    private async Task LoadRecording()
    {
        try
        {
            string? path = await _filePicker.OpenFileAsync(
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

            await LoadRecordingFromFileAsync(path);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Load recording file pick failed");
            StatusText = $"Load recording error: {ex.Message}";
        }
    }

    /// <summary>
    /// Loads the recording at <paramref name="path"/> into the room, then applies it and rebuilds the terminal from the room's
    /// log, both done before the returned task completes. The load command and the automation pipe's <c>load_recording</c>
    /// share it. Never throws: a failure is logged, and the outcome says whether the recording loaded, with the status line (also
    /// set as <see cref="StatusText"/>) that names the loaded scenario or the failure.
    /// </summary>
    internal async Task<RecordingLoadOutcome> LoadRecordingFromFileAsync(string path)
    {
        try
        {
            byte[] recordingBytes = await File.ReadAllBytesAsync(path);

            StatusText = "Loading recording...";
            RewindResultDto? result = await _connection.LoadRecordingAsync(recordingBytes);
            if (result is null || !result.Success)
            {
                string failed = $"Load recording failed: {result?.Error ?? "Unknown error"}";
                StatusText = failed;
                return new RecordingLoadOutcome(false, failed);
            }

            List<TerminalBroadcastDto> terminalLog = await _connection.GetTerminalLogAsync();

            // Each status is kept in a local: StatusText itself may be overwritten by another UI message before it is returned.
            string loaded = $"Recording loaded: {result.ScenarioName}";
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Bookmarks are seeded server-side from the recording's bookmarks.json and arrive via the
                // BookmarksChanged broadcast (so every RPO in the room sees them), not read locally here.
                ApplyRecordingResult(result);
                RepopulateTerminalFromRecording(terminalLog);
                StatusText = loaded;
                AddSystemEntry(loaded);
            });
            return new RecordingLoadOutcome(true, loaded);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Load recording failed");
            string error = $"Load recording error: {ex.Message}";
            StatusText = error;
            return new RecordingLoadOutcome(false, error);
        }
    }

    /// <summary>
    /// Another room member loaded a recording. Rebuilds from the same result they applied — a
    /// recording swaps the scenario and enters playback, so unlike a restart or rewind the aircraft
    /// manifest alone would leave the scope on the previous scenario. The terminal history is fetched
    /// separately, exactly as the loading client does, so both end up with the recording's tape.
    /// </summary>
    private void OnRecordingLoaded(RewindResultDto result)
    {
        _ = Task.Run(async () =>
        {
            List<TerminalBroadcastDto> terminalLog;
            try
            {
                terminalLog = await _connection.GetTerminalLogAsync();
            }
            catch (Exception ex)
            {
                // A failed terminal fetch must not cost us the scope rebuild, which is the part that
                // actually matters — apply the recording with an empty tape and say so.
                _log.LogWarning(ex, "Failed to fetch the terminal log after another client loaded a recording");
                terminalLog = [];
            }

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                ApplyRecordingResult(result);
                RepopulateTerminalFromRecording(terminalLog);
                StatusText = $"Recording loaded: {result.ScenarioName}";
                AddSystemEntry($"Recording loaded by another client: {result.ScenarioName}");
            });
        });
    }

    internal void ApplyRecordingResult(RewindResultDto result)
    {
        _sessionSettingCommitter.DiscardAll("a recording was loaded");
        SeedSessionSoloAdvisorySettings();
        RelabelRoomTitle(result.ArtccId);
        ResetPilotVoiceWarningSession();
        ActiveScenarioId = result.ScenarioId;
        ActiveScenarioName = result.ScenarioName;
        ActiveScenarioPrimaryAirportId = NormalizeFavoriteAirportId(result.PrimaryAirportId);
        _commandInput.PrimaryAirportId = result.PrimaryAirportId;
        _lastRadarPrimaryAirportId = result.PrimaryAirportId;
        // Primary only: an extra Radar View keeps the airport it was opened with (re-seeded below).
        Radar.SetPrimaryAirportId(result.PrimaryAirportId);
        SetRadarAirportPosition(result.PrimaryAirportId);
        ApplySimState(result.IsPaused, result.SimRate, result.ElapsedSeconds, result.IsPlayback, result.TapeEnd);
        // A recording replaces the scenario, so a prompt asking about the one before it closes.
        ApplyActiveRunways(result.ActiveRunways);
        ApplyToweredAirports(result.ToweredAirports);
        CloseSessionSetup();

        if (!string.IsNullOrEmpty(result.PrimaryAirportId))
        {
            SetDistanceReference(result.PrimaryAirportId);
        }

        SetStudentPositionType(result.StudentPositionType);
        // The tape's session settings, carried in the result itself: the SessionSettingsChanged broadcast for the same
        // load may land before or after this, and the per-position preference is not the session's.
        if (result.SessionSettings is { } settings)
        {
            ApplySessionSettings(settings);
        }
        else
        {
            _log.LogWarning(
                "The recording result for '{Scenario}' carried no session settings; the session's current ones stay",
                result.ScenarioName
            );
        }

        foreach (RadarViewModel radar in AllRadarViews)
        {
            radar.ShowMvaHints = _preferences.GetMvaHintDefault(_studentPositionType);
        }

        Aircraft.Clear();
        if (result.Aircraft is not null)
        {
            foreach (AircraftDto dto in result.Aircraft)
            {
                var model = Models.AircraftModel.FromDto(dto, ComputeDistance);
                ApplyAutoClearedToLand(model);
                Aircraft.Add(model);
            }
        }

        // The scenario id keys the Ground View's saved center/zoom, as on the bootstrap path.
        Ground.SetScenarioId(result.ScenarioId);
        if (!string.IsNullOrEmpty(result.PrimaryAirportId))
        {
            _ = Ground.LoadLayoutAsync(result.PrimaryAirportId);
        }

        string artccId = result.ArtccId ?? _preferences.ArtccId;
        if (!string.IsNullOrEmpty(artccId))
        {
            _ = Radar.LoadVideoMapsForArtccAsync(artccId, result.PrimaryAirportId, result.ScenarioId);

            if (!string.IsNullOrEmpty(result.PrimaryAirportId))
            {
                _ = Ground.LoadTowerCabLayersAsync(artccId, result.PrimaryAirportId);
            }
        }

        // Stashed so an extra window opened later keys its saved view on this recording's scenario and
        // loads the recording's ARTCC, exactly as the docked views just did.
        _lastScenarioArtccId = artccId;
        _lastScenarioId = result.ScenarioId;

        // Extra windows follow their own airport, not the recording's: each reloads its own video maps
        // (Radar) and re-evaluates whether it mirrors the primary's layout (Ground).
        foreach (RadarViewInstance instance in ExtraRadarViews)
        {
            SeedRadarAirport(instance);
        }

        foreach (GroundViewInstance instance in ExtraGroundViews)
        {
            instance.Vm.SetScenarioId(result.ScenarioId);
            SeedGroundAirport(instance);
        }

        ShowTimelineBar = true;

        // The recording is the active scenario now, and this path does not go through the bootstrap
        // router, so it publishes its own presence — otherwise Discord keeps showing the scenario the
        // recording replaced. The tape's position is where its clock starts.
        StartRichPresence(result.ElapsedSeconds);
    }

    private static string FormatTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}";
    }
}

/// <summary>How a recording load ended: whether the recording loaded, and the status line naming its scenario or the failure.</summary>
public readonly record struct RecordingLoadOutcome(bool Loaded, string Status);

/// <summary>How a rewind ended: whether the sim moved, and the status line it ended on.</summary>
public readonly record struct RewindOutcome(bool Rewound, string Status);
