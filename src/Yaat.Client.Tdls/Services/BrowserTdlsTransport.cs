using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Yaat.Sim;

namespace Yaat.Client.Services;

/// <summary>
/// WASM-side <see cref="ITdlsTransport"/> implementation. Owns its own
/// <see cref="HubConnection"/>, wires the JsonHubProtocol against
/// <see cref="YaatTdlsHubJsonContext"/> (TDLS-only DTO surface — no reference
/// to <c>YaatHubJsonContext</c> or any Core type), and exposes the auto-join
/// helpers the browser client needs in addition to the ITdlsTransport surface.
///
/// Sibling of <see cref="BrowserStripsTransport"/> — same transport shape,
/// different DTO surface. Lives in Yaat.Client.Tdls so the WASM bundle stays
/// thin (no Avalonia.Desktop / Velopack / radar payloads dragged in via
/// Yaat.Client.Core).
///
/// Wire-protocol method names match the server: GetAccessibleTdlsFacilities,
/// GetTdlsFacilityView, RequestFullTdlsState, FindRoomForMyCid, JoinRoom,
/// SendCommand. Server-pushed events (TdlsItemChanged, TdlsItemRemoved,
/// TdlsStateChanged, RoomAvailableForCid) match too.
/// </summary>
public sealed class BrowserTdlsTransport : ITdlsTransport, IAsyncDisposable
{
    private static readonly ILogger Log = SimLog.CreateLogger("BrowserTdlsTransport");

    private HubConnection? _connection;

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public event Action? Connected;
    public event Action<Exception?>? Closed;
    public event Action<Exception?>? Reconnecting;
    public event Action<string?>? Reconnected;
    public event Action<TdlsItemDto>? TdlsItemChanged;
    public event Action<TdlsItemRemovedDto>? TdlsItemRemoved;
    public event Action<TdlsStateDto>? TdlsStateChanged;

    /// <summary>
    /// Server tells us a room bound to our CID has become available. Drives
    /// the auto-join handshake in the browser client when the initial
    /// <see cref="FindRoomForMyCidAsync"/> returned <c>null</c>.
    /// </summary>
    public event Action<string>? RoomAvailableForCid;

    /// <summary>
    /// Connects to the SignalR hub. <paramref name="serverUrl"/> can be empty
    /// or relative — SignalR.Client treats an empty URL as same-origin, which
    /// is what we want when yaat-server hosts <c>/vtdls/</c> directly.
    /// </summary>
    public async Task ConnectAsync(string serverUrl, CancellationToken ct = default)
    {
        if (_connection is not null)
        {
            await DisconnectAsync();
        }

        var baseUrl = string.IsNullOrEmpty(serverUrl) ? "/hubs/training" : serverUrl.TrimEnd('/') + "/hubs/training";
        // Identify as a vTDLS position tool so the server exempts this connection from the
        // mentor/instructor connect gate (students use vStrips/vTDLS like CRC).
        var hubUrl = $"{baseUrl}?clientKind={ClientKind.VTdls}";
        Log.LogInformation("Connecting to {Url}", hubUrl);

        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .AddJsonProtocol(options =>
            {
                // Only the TDLS context — the WASM client never sees Core's
                // broader DTO surface (room state, aircraft, weather, CRC).
                options.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, YaatTdlsHubJsonContext.Default);
            })
            .Build();

        _connection.On<TdlsItemDto>("TdlsItemChanged", dto => TdlsItemChanged?.Invoke(dto));
        _connection.On<TdlsItemRemovedDto>("TdlsItemRemoved", dto => TdlsItemRemoved?.Invoke(dto));
        _connection.On<TdlsStateDto>("TdlsStateChanged", dto => TdlsStateChanged?.Invoke(dto));
        _connection.On<string>("RoomAvailableForCid", roomId => RoomAvailableForCid?.Invoke(roomId));

        _connection.Reconnecting += error =>
        {
            Log.LogWarning(error, "Connection lost, reconnecting");
            Reconnecting?.Invoke(error);
            return Task.CompletedTask;
        };
        _connection.Reconnected += connectionId =>
        {
            Log.LogInformation("Reconnected with id {Id}", connectionId);
            Reconnected?.Invoke(connectionId);
            return Task.CompletedTask;
        };
        _connection.Closed += error =>
        {
            Log.LogWarning(error, "Connection closed");
            Closed?.Invoke(error);
            return Task.CompletedTask;
        };

        await _connection.StartAsync(ct);
        Connected?.Invoke();
    }

    public async Task DisconnectAsync()
    {
        if (_connection is null)
        {
            return;
        }
        await _connection.DisposeAsync();
        _connection = null;
    }

    public ValueTask DisposeAsync() => new(DisconnectAsync());

    private HubConnection RequireConnection() => _connection ?? throw new InvalidOperationException("BrowserTdlsTransport is not connected");

    public Task<List<AccessibleFacilityDto>> GetAccessibleTdlsFacilitiesAsync() =>
        RequireConnection().InvokeAsync<List<AccessibleFacilityDto>>("GetAccessibleTdlsFacilities");

    public Task<TdlsFacilityViewDto?> GetTdlsFacilityViewAsync(string facilityId) =>
        RequireConnection().InvokeAsync<TdlsFacilityViewDto?>("GetTdlsFacilityView", facilityId);

    public Task RequestFullTdlsStateAsync() => RequireConnection().InvokeAsync("RequestFullTdlsState");

    /// <summary>
    /// Looks up the room currently associated with a CID. Returns null when no
    /// room is active — pair with <see cref="RoomAvailableForCid"/> to wait
    /// until one becomes available.
    /// </summary>
    public Task<BrowserTdlsRoomInfoDto?> FindRoomForMyCidAsync() => RequireConnection().InvokeAsync<BrowserTdlsRoomInfoDto?>("FindRoomForMyCid");

    /// <summary>
    /// Joins a training room. <paramref name="kind"/> is a string from
    /// <see cref="ClientKind"/>. Server returns the broader RoomStateDto; we
    /// deserialize into a TDLS-only subset so the WASM bundle doesn't have
    /// to ship the full DTO graph.
    /// </summary>
    public Task<BrowserTdlsJoinRoomResultDto?> JoinRoomAsync(string roomId, string initials, string artccId, string kind) =>
        RequireConnection().InvokeAsync<BrowserTdlsJoinRoomResultDto?>("JoinRoom", roomId, initials, artccId, kind);

    public Task<CommandResultDto> SendCommandAsync(string callsign, string command, string initials) =>
        RequireConnection().InvokeAsync<CommandResultDto>("SendCommand", callsign, command, initials);
}

/// <summary>
/// Subset of <c>TrainingRoomInfoDto</c> the browser TDLS client needs from
/// <see cref="BrowserTdlsTransport.FindRoomForMyCidAsync"/>. JSON deserialization
/// is case-insensitive and ignores extra fields, so the server's full payload
/// round-trips into this narrower record.
/// </summary>
public record BrowserTdlsRoomInfoDto(string RoomId, string CreatorInitials);

/// <summary>
/// Subset of <c>RoomStateDto</c> the browser TDLS client needs from
/// <see cref="BrowserTdlsTransport.JoinRoomAsync"/>: just the bits required to
/// display status. TDLS state itself is pushed via the
/// <see cref="ITdlsTransport.TdlsStateChanged"/> event hooked at JoinRoom time
/// (server calls SendInitialStateToClientAsync on every successful join).
/// </summary>
public record BrowserTdlsJoinRoomResultDto(string RoomId, string? ScenarioName);
