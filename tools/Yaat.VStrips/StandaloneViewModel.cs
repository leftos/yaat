using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
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
        _connection.ScenarioLoaded += dto => VStrips.ApplyBayConfig(dto.FlightStripsConfig);
        _connection.ScenarioUnloaded += () => VStrips.ApplyBayConfig(null);
        _connection.KickedFromRoom += msg =>
            Dispatcher.UIThread.Post(() =>
            {
                StatusText = $"Kicked: {msg}";
                IsInRoom = false;
                ActiveRoomName = null;
                ActiveRoomId = null;
            });
    }

    /// <summary>
    /// Connect to yaat-server. Called from <see cref="ConnectViewModel"/>'s connect action.
    /// Returns null on success, error message on failure.
    /// </summary>
    public async Task<string?> AttemptConnectAsync(string url, CancellationToken ct)
    {
        try
        {
            StatusText = $"Connecting to {url}...";
            await _connection.ConnectAsync(url, ct);
            IsConnected = true;
            StatusText = $"Connected to {url}";
            Preferences.SetSavedServers(Preferences.SavedServers, url);
            return null;
        }
        catch (Exception ex)
        {
            StatusText = "Connection failed";
            _log.LogWarning(ex, "Connect failed: {Url}", url);
            return ex.Message;
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
            return;
        }

        try
        {
            var rooms = await _connection.GetActiveRoomsAsync();
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
            return false;
        }

        try
        {
            var state = await _connection.JoinRoomAsync(roomId, Preferences.VatsimCid, Preferences.UserInitials, Preferences.ArtccId);
            if (state is null)
            {
                StatusText = "Room not found";
                return false;
            }

            IsInRoom = true;
            ActiveRoomId = state.RoomId;
            ActiveRoomName = $"({state.CreatorArtccId}) {state.CreatorInitials}'s Room";
            StatusText = $"Joined room — {state.ScenarioName ?? "no scenario"}";

            VStrips.ApplyBayConfig(state.FlightStripsConfig);

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
