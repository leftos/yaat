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
    [RelayCommand(CanExecute = nameof(CanToggleConnect))]
    private async Task ConnectAsync()
    {
        if (IsConnected)
        {
            await DisconnectAsync();
            return;
        }

        if (_preferences.UserInitials.Length != 2)
        {
            StatusText = "Set your 2-letter initials in Settings before connecting";
            return;
        }

        if (string.IsNullOrWhiteSpace(_preferences.VatsimCid))
        {
            StatusText = "Set your VATSIM CID in Settings before connecting";
            return;
        }

        if (string.IsNullOrWhiteSpace(_preferences.ArtccId))
        {
            StatusText = "Set your ARTCC ID in Settings before connecting";
            return;
        }

        try
        {
            StatusText = "Connecting...";
            await _connection.ConnectAsync(ServerUrl);
            IsConnected = true;
            StatusText = "Connected — select or create a room";
            await RefreshRoomListAsync();
            ShowRoomList = true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Connection failed");
            StatusText = $"Error: {ex.Message}";
            IsConnected = false;
        }
    }

    private static bool CanToggleConnect() => true;

    private async Task DisconnectAsync()
    {
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

        await _connection.DisconnectAsync();
        IsConnected = false;
        StatusText = "Disconnected";
        ClearRoomState();
    }

    [RelayCommand(CanExecute = nameof(CanCreateRoom))]
    private async Task CreateRoomAsync()
    {
        try
        {
            var roomId = await _connection.CreateRoomAsync(_preferences.VatsimCid, _preferences.UserInitials, _preferences.ArtccId);

            var state = await _connection.JoinRoomAsync(roomId, _preferences.VatsimCid, _preferences.UserInitials, _preferences.ArtccId);

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
            var state = await _connection.JoinRoomAsync(roomId, _preferences.VatsimCid, _preferences.UserInitials, _preferences.ArtccId);

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
            StatusText = "Connection lost — reconnecting...";
            AddSystemEntry("Connection lost, attempting to reconnect");
        });
    }

    private void OnReconnected(string? connectionId)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            if (ActiveRoomId is null)
            {
                return;
            }

            try
            {
                var state = await _connection.JoinRoomAsync(ActiveRoomId, _preferences.VatsimCid, _preferences.UserInitials, _preferences.ArtccId);

                if (state is not null)
                {
                    ApplyRoomState(state);
                    StatusText = "Reconnected to room";
                    AddSystemEntry("Reconnected to room");
                }
                else
                {
                    StatusText = "Room no longer active";
                    ClearRoomState();
                    await RefreshRoomListAsync();
                    ShowRoomList = true;
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Rejoin room after reconnect failed");
            }
        });
    }

    private void OnConnectionClosed(Exception? error)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var reason = error is not null ? $"Server connection lost — {error.Message}" : "Server connection closed";
            _log.LogWarning(error, "Connection closed permanently");
            IsConnected = false;
            StatusText = reason;
            AddSystemEntry(reason);
            ClearRoomState();
        });
    }

    // --- Room state helpers ---

    private void ApplyRoomState(RoomStateDto state)
    {
        ActiveRoomId = state.RoomId;
        ActiveRoomName = $"({state.CreatorArtccId}) {state.CreatorInitials}'s Room";

        RoomMembers.Clear();
        foreach (var m in state.Members)
        {
            RoomMembers.Add(m);
        }

        if (state.ScenarioId is not null)
        {
            ActiveScenarioId = state.ScenarioId;
            ActiveScenarioName = state.ScenarioName;
            _commandInput.PrimaryAirportId = state.PrimaryAirportId;
            Radar.SetPrimaryAirportId(state.PrimaryAirportId);
            SetRadarAirportPosition(state.PrimaryAirportId);
            ApplySimState(state.IsPaused, (int)state.SimRate);

            if (!string.IsNullOrEmpty(state.PrimaryAirportId))
            {
                SetDistanceReference(state.PrimaryAirportId);
                _ = Ground.LoadLayoutAsync(state.PrimaryAirportId);
            }

            if (!string.IsNullOrEmpty(_preferences.ArtccId))
            {
                _ = Radar.LoadVideoMapsForArtccAsync(_preferences.ArtccId, state.PrimaryAirportId, state.ScenarioId);
            }
        }

        Aircraft.Clear();
        foreach (var dto in state.AllAircraft)
        {
            Aircraft.Add(AircraftModel.FromDto(dto, ComputeDistance));
        }

        _ = SendAutoAcceptDelay();
        _ = SendAutoDeleteMode();
        _ = SendValidateDctFixes();
        _ = RefreshCrcLobbyAsync();
    }

    private void ClearRoomState()
    {
        ActiveRoomId = null;
        ActiveRoomName = null;
        RoomMembers.Clear();
        CrcLobbyClients.Clear();
        CrcRoomMembers.Clear();
        ShowCrcPanel = false;
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

            RoomMembers.Clear();
            foreach (var m in dto.Members)
            {
                RoomMembers.Add(m);
            }

            if (dto.ScenarioName is not null)
            {
                ActiveScenarioName = dto.ScenarioName;
            }
        });
    }

    private void OnCrcLobbyChanged(CrcLobbyChangedDto dto)
    {
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
}
