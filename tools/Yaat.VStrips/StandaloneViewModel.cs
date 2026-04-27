using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Velopack;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.VStrips;

/// <summary>
/// Purpose-built root view-model for the standalone vStrips app. Owns just enough
/// to connect to yaat-server, join a room, and display flight strips — no speech
/// pipeline, no ground/radar VMs, no aircraft grid, no terminal. Approximately
/// 10% of MainViewModel's surface.
/// </summary>
public partial class StandaloneViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ILogger _log = AppLog.CreateLogger<StandaloneViewModel>();
    private readonly ServerConnection _connection;

    public UserPreferences Preferences { get; }
    public VStripsViewModel VStrips { get; }

    [ObservableProperty]
    private string _statusText = "Disconnected";

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isInRoom;

    [ObservableProperty]
    private string? _activeRoomName;

    [ObservableProperty]
    private string? _activeRoomId;

    public ObservableCollection<TrainingRoomInfoDto> AvailableRooms { get; } = [];

    private readonly UpdateService _updateService = new(channel: Program.VStripsChannel);
    private UpdateInfo? _pendingUpdate;

    [ObservableProperty]
    private bool _isUpdateAvailable;

    [ObservableProperty]
    private string _updateVersion = "";

    [ObservableProperty]
    private int _updateProgress;

    [ObservableProperty]
    private bool _isDownloadingUpdate;

    public StandaloneViewModel()
    {
        Preferences = new UserPreferences();
        _connection = new ServerConnection();

        VStrips = new VStripsViewModel(_connection, SendCommandForViewAsync, Preferences);

        _connection.Reconnecting += _ => Dispatcher.UIThread.Post(() => StatusText = "Reconnecting...");
        _connection.Reconnected += _ => Dispatcher.UIThread.Post(() => StatusText = "Reconnected");
        _connection.Closed += _ =>
            Dispatcher.UIThread.Post(() =>
            {
                StatusText = "Disconnected";
                IsConnected = false;
                IsInRoom = false;
                ActiveRoomName = null;
                ActiveRoomId = null;
            });
        _connection.ScenarioLoaded += dto =>
        {
            VStrips.ApplyBayConfig(dto.FlightStripsConfig);
            _ = VStrips.RefreshAccessibleFacilitiesAsync();
        };
        _connection.ScenarioUnloaded += () => VStrips.ApplyBayConfig(null);
        _connection.KickedFromRoom += msg =>
            Dispatcher.UIThread.Post(() =>
            {
                StatusText = $"Kicked: {msg}";
                IsInRoom = false;
                ActiveRoomName = null;
                ActiveRoomId = null;
            });

        _ = CheckForUpdateAsync();
    }

    private async Task CheckForUpdateAsync()
    {
        try
        {
            var update = await _updateService.CheckForUpdateAsync();
            if (update is null)
            {
                return;
            }

            _pendingUpdate = update;
            UpdateVersion = update.TargetFullRelease.Version.ToString();
            IsUpdateAvailable = true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Update check failed");
        }
    }

    [RelayCommand]
    private async Task UpdateNowAsync()
    {
        if (_pendingUpdate is null)
        {
            return;
        }

        try
        {
            IsDownloadingUpdate = true;
            await _updateService.DownloadUpdateAsync(_pendingUpdate, progress => Dispatcher.UIThread.Post(() => UpdateProgress = progress));
            _updateService.ApplyUpdateAndRestart(_pendingUpdate);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Update apply failed");
            IsDownloadingUpdate = false;
        }
    }

    [RelayCommand]
    private void DismissUpdate()
    {
        IsUpdateAvailable = false;
        _pendingUpdate = null;
    }

    /// <summary>
    /// Connect to yaat-server. Called from <see cref="ConnectViewModel"/>'s connect action.
    /// Returns null on success, error message on failure.
    /// </summary>
    public async Task<string?> AttemptConnectAsync(string url, CancellationToken ct)
    {
        try
        {
            _log.LogInformation(
                "AttemptConnect to {Url}; identity cid='{Cid}' initials='{Initials}' artcc='{Artcc}'",
                url,
                Preferences.VatsimCid,
                Preferences.UserInitials,
                Preferences.ArtccId
            );
            StatusText = $"Connecting to {url}...";
            await _connection.ConnectAsync(url, ct);
            IsConnected = true;
            StatusText = $"Connected to {url}";
            Preferences.SetSavedServers(Preferences.SavedServers, url);

            await TryAutoJoinForCidAsync();
            return null;
        }
        catch (Exception ex)
        {
            StatusText = "Connection failed";
            _log.LogWarning(ex, "Connect failed: {Url}", url);
            return ex.Message;
        }
    }

    private async Task TryAutoJoinForCidAsync()
    {
        var cid = Preferences.VatsimCid;
        if (string.IsNullOrWhiteSpace(cid))
        {
            _log.LogInformation("Auto-join skipped: no VATSIM CID set");
            return;
        }

        try
        {
            var room = await _connection.FindRoomForMyCidAsync(cid);
            if (room is null)
            {
                _log.LogInformation("Auto-join: no existing room for CID {Cid}", cid);
                return;
            }

            _log.LogInformation("Auto-join: room {RoomId} found for CID {Cid}, joining", room.RoomId, cid);
            StatusText = $"Auto-joining {room.CreatorInitials}'s room...";
            await JoinRoomAsync(room.RoomId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Auto-join lookup failed");
        }
    }

    [RelayCommand]
    public async Task DisconnectAsync()
    {
        await _connection.DisconnectAsync();
        IsConnected = false;
        IsInRoom = false;
        ActiveRoomName = null;
        ActiveRoomId = null;
        StatusText = "Disconnected";
    }

    [RelayCommand]
    public async Task RefreshRoomsAsync()
    {
        if (!IsConnected)
        {
            _log.LogInformation("RefreshRooms skipped: not connected");
            return;
        }

        try
        {
            _log.LogInformation("RefreshRooms calling GetActiveRooms");
            var rooms = await _connection.GetActiveRoomsAsync();
            _log.LogInformation("RefreshRooms got {Count} rooms", rooms.Count);
            AvailableRooms.Clear();
            foreach (var room in rooms)
            {
                AvailableRooms.Add(room);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to list rooms");
            StatusText = "Failed to list rooms";
        }
    }

    public async Task<bool> JoinRoomAsync(string roomId)
    {
        if (!IsConnected)
        {
            _log.LogInformation("JoinRoom skipped: not connected");
            return false;
        }

        try
        {
            _log.LogInformation(
                "JoinRoom {RoomId} as cid='{Cid}' initials='{Initials}' artcc='{Artcc}'",
                roomId,
                Preferences.VatsimCid,
                Preferences.UserInitials,
                Preferences.ArtccId
            );
            var state = await _connection.JoinRoomAsync(
                roomId,
                Preferences.VatsimCid,
                Preferences.UserInitials,
                Preferences.ArtccId,
                Yaat.Sim.ClientKind.VStrips
            );
            if (state is null)
            {
                _log.LogWarning("JoinRoom {RoomId} returned null state", roomId);
                StatusText = "Room not found";
                return false;
            }

            IsInRoom = true;
            ActiveRoomId = state.RoomId;
            ActiveRoomName = $"({state.CreatorArtccId}) {state.CreatorInitials}'s Room";
            StatusText = $"Joined room — {state.ScenarioName ?? "no scenario"}";

            VStrips.ApplyBayConfig(state.FlightStripsConfig);
            _ = VStrips.RefreshAccessibleFacilitiesAsync();

            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to join room {RoomId}", roomId);
            StatusText = "Failed to join room";
            return false;
        }
    }

    [RelayCommand]
    public async Task LeaveRoomAsync()
    {
        if (!IsInRoom)
        {
            return;
        }

        try
        {
            await _connection.LeaveRoomAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to leave room");
        }

        IsInRoom = false;
        ActiveRoomName = null;
        ActiveRoomId = null;
        VStrips.ApplyBayConfig(null);
        StatusText = "Left room";
    }

    private async Task SendCommandForViewAsync(string callsign, string command, string initials)
    {
        if (!IsConnected || !IsInRoom)
        {
            return;
        }

        try
        {
            await _connection.SendCommandAsync(callsign, command, initials);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "SendCommand failed: {Command}", command);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }
}
