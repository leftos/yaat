using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Models;
using Yaat.Client.Services;

namespace Yaat.Client.ViewModels;

/// <summary>
/// Connection lifecycle, room management, and CRC client management.
/// </summary>
public partial class MainViewModel
{
    /// <summary>
    /// Attempts to connect to the given server URL.
    /// Returns null on success, or an error message string on failure.
    /// </summary>
    internal async Task<string?> AttemptConnectAsync(string url, CancellationToken ct)
    {
        if (_preferences.UserInitials.Length != 2)
        {
            return "Set your 2-letter initials in Settings before connecting";
        }

        if (string.IsNullOrWhiteSpace(_preferences.VatsimCid))
        {
            return "Set your VATSIM CID in Settings before connecting";
        }

        if (string.IsNullOrWhiteSpace(_preferences.ArtccId))
        {
            return "Set your ARTCC ID in Settings before connecting";
        }

        try
        {
            IsConnecting = true;
            StatusText = "Connecting...";
            await _connection.ConnectAsync(url, ct);
            _connectedServerUrl = url;
            IsConnected = true;
            IsConnecting = false;
            StatusText = "Connected — select or create a room";
            AddSystemEntry($"Connected to {url}");
            await RefreshRoomListAsync();
            ShowRoomList = true;
            return null;
        }
        catch (OperationCanceledException)
        {
            IsConnecting = false;
            StatusText = "Connection cancelled";
            return "Connection cancelled";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Connection failed");
            IsConnecting = false;
            StatusText = $"Error: {ex.Message}";
            IsConnected = false;
            return $"Error: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        if (IsConnecting)
        {
            IsConnecting = false;
            StatusText = "Disconnect requested";
            return;
        }

        if (ActiveRoomId is not null)
        {
            try
            {
                await _connection.LeaveRoomAsync();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "LeaveRoom on disconnect failed");
            }
        }

        var url = _connectedServerUrl;
        await _connection.DisconnectAsync();
        IsConnected = false;
        StatusText = "Disconnected";
        AddSystemEntry($"Disconnected from {url}");
        _connectedServerUrl = "";
        ClearRoomState();
    }

    private bool CanDisconnect() => IsConnected || IsConnecting;

    /// <summary>
    /// Tools → Open Strips in Browser. Builds <c>{server}/vstrips/?cid=&amp;
    /// initials=&amp;artcc=&amp;room=</c> from the live connection state and
    /// shells out to the user's default browser via
    /// <see cref="ProcessStartInfo.UseShellExecute"/>. Lets the instructor pop
    /// the WASM strips client into a CRC tab without juggling extra processes.
    /// Gated on <see cref="IsConnected"/> because the URL only points at a real
    /// server while we're connected.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenStripsInBrowser))]
    private Task OpenStripsInBrowserAsync()
    {
        if (string.IsNullOrEmpty(_connectedServerUrl))
        {
            return Task.CompletedTask;
        }

        var baseUrl = _connectedServerUrl.TrimEnd('/');
        var qs = new List<string>();
        if (!string.IsNullOrWhiteSpace(_preferences.VatsimCid))
        {
            qs.Add($"cid={Uri.EscapeDataString(_preferences.VatsimCid)}");
        }
        if (!string.IsNullOrWhiteSpace(_preferences.UserInitials))
        {
            qs.Add($"initials={Uri.EscapeDataString(_preferences.UserInitials)}");
        }
        if (!string.IsNullOrWhiteSpace(_preferences.ArtccId))
        {
            qs.Add($"artcc={Uri.EscapeDataString(_preferences.ArtccId)}");
        }
        if (!string.IsNullOrWhiteSpace(ActiveRoomId))
        {
            qs.Add($"room={Uri.EscapeDataString(ActiveRoomId)}");
        }
        var query = qs.Count > 0 ? "?" + string.Join("&", qs) : "";
        var url = $"{baseUrl}/vstrips/{query}";

        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "OpenStripsInBrowser failed for {Url}", url);
            StatusText = "Failed to open browser";
        }
        return Task.CompletedTask;
    }

    private bool CanOpenStripsInBrowser() => IsConnected && !string.IsNullOrEmpty(_connectedServerUrl);

    [RelayCommand(CanExecute = nameof(CanCreateRoom))]
    private async Task CreateRoomAsync()
    {
        try
        {
            var roomId = await _connection.CreateRoomAsync(
                _preferences.VatsimCid,
                _preferences.UserInitials,
                _preferences.ArtccId,
                Yaat.Sim.ClientKind.Main
            );

            var state = await _connection.JoinRoomAsync(
                roomId,
                _preferences.VatsimCid,
                _preferences.UserInitials,
                _preferences.ArtccId,
                Yaat.Sim.ClientKind.Main
            );

            if (state is null)
            {
                StatusText = "Failed to join newly created room";
                return;
            }

            ApplyRoomState(state);
            ShowRoomList = false;
            StatusText = $"Room {roomId} created";
            AddSystemEntry("Created and joined room");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "CreateRoom failed");
            StatusText = $"Create room error: {ex.Message}";
        }
    }

    private bool CanCreateRoom() => IsConnected && !IsInRoom;

    [RelayCommand]
    private async Task JoinRoomAsync(string roomId)
    {
        try
        {
            var state = await _connection.JoinRoomAsync(
                roomId,
                _preferences.VatsimCid,
                _preferences.UserInitials,
                _preferences.ArtccId,
                Yaat.Sim.ClientKind.Main
            );

            if (state is null)
            {
                StatusText = "Room no longer exists";
                await RefreshRoomListAsync();
                return;
            }

            ApplyRoomState(state);
            ShowRoomList = false;
            StatusText = $"Joined room {roomId}";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "JoinRoom failed");
            StatusText = $"Join room error: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(IsInRoom))]
    private async Task LeaveRoomAsync()
    {
        if (ActiveRoomId is null)
        {
            return;
        }

        try
        {
            await _connection.LeaveRoomAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "LeaveRoom failed");
        }

        ClearRoomState();
        StatusText = "Left room";
        await RefreshRoomListAsync();
        ShowRoomList = true;
    }

    [RelayCommand]
    private async Task RefreshRoomListAsync()
    {
        try
        {
            var rooms = await _connection.GetActiveRoomsAsync();
            ActiveRooms.Clear();
            foreach (var r in rooms)
            {
                ActiveRooms.Add(r);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to refresh room list");
        }
    }

    [RelayCommand]
    private void DismissRoomList()
    {
        ShowRoomList = false;
    }

    [RelayCommand(CanExecute = nameof(CanShowRooms))]
    private async Task ShowRoomsAsync()
    {
        await RefreshRoomListAsync();
        ShowRoomList = true;
    }

    private bool CanShowRooms() => IsConnected && !IsInRoom;

    // --- Room members panel ---

    [RelayCommand]
    private void ToggleRoomMembersPanel()
    {
        ShowRoomMembersPanel = !ShowRoomMembersPanel;
        if (ShowRoomMembersPanel)
        {
            _ = RefreshCrcLobbyAsync();
        }
    }

    [RelayCommand]
    private void DismissRoomMembersPanel()
    {
        ShowRoomMembersPanel = false;
    }

    [RelayCommand]
    private async Task KickMemberAsync(string cid)
    {
        if (cid == _preferences.VatsimCid)
        {
            return;
        }

        try
        {
            var ok = await _connection.KickMemberAsync(cid);
            if (!ok)
            {
                StatusText = "Failed to kick member";
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "KickMember failed");
            StatusText = $"Kick error: {ex.Message}";
        }
    }

    // --- CRC client management ---

    [RelayCommand]
    private void ToggleCrcPanel()
    {
        ShowCrcPanel = !ShowCrcPanel;
        if (ShowCrcPanel)
        {
            _ = RefreshCrcLobbyAsync();
        }
    }

    [RelayCommand]
    private void DismissCrcPanel()
    {
        ShowCrcPanel = false;
    }

    [RelayCommand]
    private async Task RefreshCrcLobbyAsync()
    {
        try
        {
            var lobby = await _connection.GetCrcLobbyClientsAsync();
            _log.LogInformation(
                "[CrcLobby] Pull returned: {Count} clients ({Details})",
                lobby.Count,
                string.Join(", ", lobby.Select(c => $"{c.ClientId}({c.DisplayName ?? "no-name"})"))
            );
            CrcLobbyClients.Clear();
            foreach (var c in lobby)
            {
                CrcLobbyClients.Add(c);
            }

            var members = await _connection.GetCrcRoomMembersAsync();
            CrcRoomMembers.Clear();
            foreach (var c in members)
            {
                CrcRoomMembers.Add(c);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to refresh CRC lists");
        }
    }

    [RelayCommand]
    private async Task PullCrcClientAsync(string clientId)
    {
        try
        {
            var ok = await _connection.PullCrcClientAsync(clientId);
            if (!ok)
            {
                StatusText = "Failed to pull CRC client — may already be in a room";
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "PullCrcClient failed");
            StatusText = $"Pull error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task KickCrcClientAsync(string clientId)
    {
        try
        {
            var ok = await _connection.KickCrcClientAsync(clientId);
            if (!ok)
            {
                StatusText = "Failed to kick CRC client";
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "KickCrcClient failed");
            StatusText = $"Kick error: {ex.Message}";
        }
    }

    // --- Reconnection handlers ---

    private void OnReconnecting(Exception? error)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            StatusText = IsServerRestarting ? "Server restarting — reconnecting when back online..." : "Connection lost — reconnecting...";
            if (!IsServerRestarting)
            {
                AddSystemEntry($"Connection lost to {_connectedServerUrl}, attempting to reconnect");
            }
        });
    }

    private void OnReconnected(string? connectionId)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            await TryRejoinRoomAfterReconnectAsync();
        });
    }

    private async Task TryRejoinRoomAfterReconnectAsync()
    {
        var roomId = ActiveRoomId ?? _preferences.LastActiveRoomId;
        if (string.IsNullOrWhiteSpace(roomId) && !string.IsNullOrWhiteSpace(_preferences.VatsimCid))
        {
            try
            {
                var found = await _connection.FindRoomForMyCidAsync(_preferences.VatsimCid);
                roomId = found?.RoomId;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "FindRoomForMyCid after reconnect failed");
            }
        }

        if (string.IsNullOrWhiteSpace(roomId))
        {
            if (IsServerRestarting)
            {
                StatusText = "Server restarted — waiting for your session to become available";
            }

            return;
        }

        try
        {
            var state = await _connection.JoinRoomAsync(
                roomId,
                _preferences.VatsimCid,
                _preferences.UserInitials,
                _preferences.ArtccId,
                Yaat.Sim.ClientKind.Main
            );

            if (state is not null)
            {
                var wasRestart = IsServerRestarting;
                ApplyRoomState(state);
                IsServerRestarting = false;
                StatusText = "Reconnected to room";
                AddSystemEntry($"Reconnected to {_connectedServerUrl}");
                if (wasRestart)
                {
                    var elapsed = (int)Math.Round(state.ElapsedSeconds);
                    ShowRestartBannerThenAutoDismiss(
                        RestartBanner.Restored,
                        $"Reconnected to your room — session resumed at T+{elapsed}s",
                        TimeSpan.FromSeconds(5)
                    );
                }
            }
            else
            {
                IsServerRestarting = false;
                StatusText = "Room no longer active";
                ClearRoomState();
                await RefreshRoomListAsync();
                ShowRoomList = true;
                HideRestartBanner();
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Rejoin room after reconnect failed");
        }
    }

    private void OnConnectionClosed(Exception? error)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (IsServerRestarting)
            {
                StatusText = "Server restarting — session will resume when the server is back";
                ShowRestartBanner(RestartBanner.Disconnected, "Server restarting — waiting for it to come back…");
                return;
            }

            var reason = error is not null
                ? $"Connection to {_connectedServerUrl} lost — {error.Message}"
                : $"Connection to {_connectedServerUrl} closed";
            _log.LogWarning(error, "Connection closed permanently");
            IsConnected = false;
            StatusText = reason;
            AddSystemEntry(reason);
            _connectedServerUrl = "";
            ClearRoomState();
        });
    }

    private void OnServerRestarting(DateTime restartAt, string reason, int drainSeconds)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (ActiveRoomId is not null)
            {
                _preferences.LastActiveRoomId = ActiveRoomId;
            }

            IsServerRestarting = true;
            StatusText = $"Server restarting for maintenance — session resumes in ~{drainSeconds}s";
            AddSystemEntry($"Server restart scheduled ({reason}). Your session will be preserved.");
            StartRestartBannerCountdown(restartAt, drainSeconds);
        });
    }

    private void OnServerRestartReady()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            StatusText = "Server restart ready — reconnecting shortly...";
            // Drain finished; switch the banner to "waiting for server".
            ShowRestartBanner(RestartBanner.Disconnected, "Server restarting — waiting for it to come back…");
        });
    }

    private void OnServerRestartComplete(List<RestoredRoomInfoDto> rooms)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            if (!IsConnected)
            {
                return;
            }

            await TryRejoinRoomAfterReconnectAsync();
        });
    }

    private void OnRoomAvailableForCid(string roomId)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            if (!IsConnected || ActiveRoomId is not null)
            {
                return;
            }

            try
            {
                var state = await _connection.JoinRoomAsync(
                    roomId,
                    _preferences.VatsimCid,
                    _preferences.UserInitials,
                    _preferences.ArtccId,
                    Yaat.Sim.ClientKind.Main
                );

                if (state is not null)
                {
                    ApplyRoomState(state);
                    IsServerRestarting = false;
                    StatusText = "Joined restored room";
                    ShowRoomList = false;
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Auto-join on RoomAvailableForCid failed");
            }
        });
    }

    // --- Room state helpers ---

    private void ApplyRoomState(RoomStateDto state)
    {
        ActiveRoomId = state.RoomId;
        _preferences.LastActiveRoomId = state.RoomId;
        ActiveRoomName = $"({state.CreatorArtccId}) {state.CreatorInitials}'s Room";

        if (state.ScenarioId is not null)
        {
            ApplyScenarioBootstrap(
                new ScenarioBootstrap(
                    state.ScenarioId,
                    state.ScenarioName,
                    state.PrimaryAirportId,
                    state.PositionDisplayConfig,
                    state.FlightStripsConfig,
                    state.AllAircraft
                )
            );
            ApplySimState(state.IsPaused, (int)state.SimRate, state.ElapsedSeconds, state.IsPlayback, state.TapeEnd);
        }
        else
        {
            // Room with no scenario loaded: still reset the aircraft list from
            // whatever the server says (normally empty) so we don't carry
            // stale aircraft from a prior room.
            Aircraft.Clear();
            foreach (var dto in state.AllAircraft)
            {
                var model = AircraftModel.FromDto(dto, ComputeDistance);
                ApplyAutoClearedToLand(model);
                Aircraft.Add(model);
            }
        }

        // Apply the room's session settings from the server.
        // Do NOT send our preferences — use the session settings flyout to change them.
        ApplySessionSettingsFromRoom(state);

        _ = RefreshCrcLobbyAsync();
        _ = FetchAssignmentsAsync();
    }

    private void ClearRoomState()
    {
        ActiveRoomId = null;
        ActiveRoomName = null;
        CrcLobbyClients.Clear();
        CrcRoomMembers.Clear();
        RoomMembers.Clear();
        ShowCrcPanel = false;
        ShowRoomMembersPanel = false;
        _aircraftAssignments = [];
        AssignableMembers.Clear();
        OnPropertyChanged(nameof(HasAnyAssignments));
        ClearScenarioState();
    }

    private void OnRoomMemberChanged(RoomMemberChangedDto dto)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (dto.RoomId != ActiveRoomId)
            {
                return;
            }

            if (dto.ScenarioName is not null)
            {
                ActiveScenarioName = dto.ScenarioName;
            }

            RoomMembers.Clear();
            foreach (var m in dto.Members)
            {
                RoomMembers.Add(m);
            }
        });
    }

    private void OnKickedFromRoom(string reason)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            AddSystemEntry(reason);
            ClearRoomState();
            StatusText = reason;
            await RefreshRoomListAsync();
            ShowRoomList = true;
        });
    }

    private void OnCrcLobbyChanged(CrcLobbyChangedDto dto)
    {
        _log.LogInformation(
            "[CrcLobby] Push received: {Count} clients ({Details})",
            dto.Clients.Count,
            string.Join(", ", dto.Clients.Select(c => $"{c.ClientId}({c.DisplayName ?? "no-name"})"))
        );
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            CrcLobbyClients.Clear();
            foreach (var c in dto.Clients)
            {
                CrcLobbyClients.Add(c);
            }
        });
    }

    private void OnCrcRoomMembersChanged(CrcRoomMembersChangedDto dto)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (dto.RoomId != ActiveRoomId)
            {
                return;
            }

            CrcRoomMembers.Clear();
            foreach (var c in dto.Members)
            {
                CrcRoomMembers.Add(c);
            }
        });
    }

    private void OnPositionDisplayChanged(PositionDisplayConfigDto config)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Radar.ApplyPositionDisplayConfig(config);
            UpdateRadarWeatherDisplay();
        });
    }

    // --- Aircraft assignments ---

    public ObservableCollection<AssignableMemberDto> AssignableMembers { get; } = [];

    /// <summary>callsign → initials for display.</summary>
    private Dictionary<string, string> _aircraftAssignments = [];

    public bool HasAnyAssignments => _aircraftAssignments.Count > 0;

    public string? GetAssignedInitials(string callsign) => _aircraftAssignments.GetValueOrDefault(callsign);

    private void OnAircraftAssignmentsChanged(AircraftAssignmentsDto dto)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _aircraftAssignments = dto.Assignments;
            OnPropertyChanged(nameof(HasAnyAssignments));

            AssignableMembers.Clear();
            foreach (var m in dto.Members)
            {
                AssignableMembers.Add(m);
            }

            foreach (var ac in Aircraft)
            {
                ac.AssignedTo = _aircraftAssignments.GetValueOrDefault(ac.Callsign);
            }
        });
    }

    public async Task AssignAircraftAsync(List<string> callsigns, string targetConnectionId)
    {
        try
        {
            await _connection.AssignAircraftAsync(callsigns, targetConnectionId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "AssignAircraft failed");
            StatusText = $"Assign error: {ex.Message}";
        }
    }

    public async Task UnassignAircraftAsync(List<string> callsigns)
    {
        try
        {
            await _connection.UnassignAircraftAsync(callsigns);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "UnassignAircraft failed");
            StatusText = $"Unassign error: {ex.Message}";
        }
    }

    private async Task FetchAssignmentsAsync()
    {
        try
        {
            var dto = await _connection.GetAircraftAssignmentsAsync();
            if (dto is not null)
            {
                OnAircraftAssignmentsChanged(dto);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to fetch assignments");
        }
    }

    // --- RPO control ---

    public string? SelfConnectionId => AssignableMembers.FirstOrDefault(m => m.Initials == _preferences.UserInitials)?.ConnectionId;

    public async Task TakeControlAsync(string callsign)
    {
        var selfId = SelfConnectionId;
        if (selfId is null)
        {
            StatusText = "Cannot take control — not in a multi-user room";
            return;
        }

        await AssignAircraftAsync([callsign], selfId);
    }

    public async Task GiveControlAsync(string callsign, string targetInitials)
    {
        var member = AssignableMembers.FirstOrDefault(m => m.Initials == targetInitials.ToUpperInvariant());
        if (member is null)
        {
            StatusText = $"No room member with initials '{targetInitials}'";
            return;
        }

        await AssignAircraftAsync([callsign], member.ConnectionId);
    }

    public async Task ReleaseControlAsync(string callsign)
    {
        await UnassignAircraftAsync([callsign]);
    }

    public void BuildRpoMenuItems(ItemsControl menu, List<string> callsigns)
    {
        if (AssignableMembers.Count == 0 || callsigns.Count == 0)
        {
            return;
        }

        menu.Items.Add(new Separator());

        var selfInitials = _preferences.UserInitials;
        var singleCallsign = callsigns.Count == 1 ? callsigns[0] : null;

        // Determine assignment state for contextual display
        var assignedToSelf = singleCallsign is not null && GetAssignedInitials(singleCallsign) == selfInitials;
        var selfId = SelfConnectionId;

        // "Take control" — assign to self (shown unless already assigned to self)
        if (!assignedToSelf && selfId is not null)
        {
            var takeItem = new MenuItem { Header = callsigns.Count > 1 ? $"Take control ({callsigns.Count})" : "Take control" };
            takeItem.Click += async (_, _) => await AssignAircraftAsync(callsigns, selfId);
            menu.Items.Add(takeItem);
        }

        // "Give up control" — unassign (shown when assigned to self)
        if (assignedToSelf)
        {
            var releaseItem = new MenuItem { Header = "Give up control" };
            releaseItem.Click += async (_, _) => await UnassignAircraftAsync(callsigns);
            menu.Items.Add(releaseItem);
        }

        // "Give control" submenu — lists other members (not self)
        var otherMembers = AssignableMembers.Where(m => m.Initials != selfInitials).ToList();
        if (otherMembers.Count > 0)
        {
            var giveSubmenu = new MenuItem { Header = callsigns.Count > 1 ? $"Give control ({callsigns.Count})" : "Give control" };
            foreach (var member in otherMembers)
            {
                var memberItem = new MenuItem { Header = member.Initials };
                var connId = member.ConnectionId;
                memberItem.Click += async (_, _) => await AssignAircraftAsync(callsigns, connId);
                giveSubmenu.Items.Add(memberItem);
            }
            menu.Items.Add(giveSubmenu);
        }

        // "Unassign" — always available for multi-select or when assigned to someone else
        if (!assignedToSelf)
        {
            var unassignItem = new MenuItem { Header = callsigns.Count > 1 ? $"Unassign ({callsigns.Count})" : "Unassign" };
            unassignItem.Click += async (_, _) => await UnassignAircraftAsync(callsigns);
            menu.Items.Add(unassignItem);
        }
    }

    // --- Restart banner helpers ---

    private void StartRestartBannerCountdown(DateTime restartAtUtc, int drainSeconds)
    {
        _restartTargetUtc = restartAtUtc;
        ShowRestartBanner(RestartBanner.Draining, FormatDrainText(drainSeconds));
        EnsureRestartBannerTimer();
    }

    private void ShowRestartBanner(RestartBanner kind, string text)
    {
        RestartBannerKind = kind;
        RestartBannerText = text;
    }

    private void ShowRestartBannerThenAutoDismiss(RestartBanner kind, string text, TimeSpan after)
    {
        ShowRestartBanner(kind, text);
        _restartBannerHideAtUtc = DateTime.UtcNow.Add(after);
        EnsureRestartBannerTimer();
    }

    private void HideRestartBanner()
    {
        RestartBannerKind = RestartBanner.Hidden;
        RestartBannerText = "";
        _restartBannerTimer?.Stop();
    }

    private void EnsureRestartBannerTimer()
    {
        if (_restartBannerTimer is not null)
        {
            _restartBannerTimer.Start();
            return;
        }

        _restartBannerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _restartBannerTimer.Tick += (_, _) => TickRestartBanner();
        _restartBannerTimer.Start();
    }

    private void TickRestartBanner()
    {
        if (RestartBannerKind == RestartBanner.Draining)
        {
            var remaining = (int)Math.Max(0, Math.Ceiling((_restartTargetUtc - DateTime.UtcNow).TotalSeconds));
            RestartBannerText = FormatDrainText(remaining);
            return;
        }

        if (RestartBannerKind == RestartBanner.Restored && DateTime.UtcNow >= _restartBannerHideAtUtc)
        {
            HideRestartBanner();
        }
    }

    private static string FormatDrainText(int remainingSeconds) =>
        remainingSeconds > 0
            ? $"Server restarting for planned maintenance — session resumes in ~{remainingSeconds}s. Commands disabled."
            : "Server restarting now — session will resume when it comes back. Commands disabled.";
}
