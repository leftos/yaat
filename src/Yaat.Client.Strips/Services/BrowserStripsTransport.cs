using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Yaat.Sim;

namespace Yaat.Client.Services;

/// <summary>
/// WASM-side <see cref="IStripsTransport"/> implementation. Owns its own
/// <see cref="HubConnection"/>, wires the JsonHubProtocol against
/// <see cref="YaatStripsHubJsonContext"/> (strip-only DTO surface — no
/// reference to <c>YaatHubJsonContext</c> or any Core type), and exposes
/// the auto-join helpers the browser client needs in addition to the
/// IStripsTransport surface.
///
/// Designed to live inside Yaat.Client.Strips so any future browser-hosted
/// view (a separate radar-only WASM client, an embedded scenario monitor)
/// can reuse the same transport without dragging in
/// Avalonia.Desktop / Velopack from Yaat.Client.Core.
///
/// Wire-protocol method names match the server: GetAccessibleFacilities,
/// GetFlightStripsConfigForFacility, RequestFlightStripForAircraft,
/// FindRoomForMyCid, JoinRoom, SendCommand. Server-pushed events
/// (FlightStripsStateChanged, StripItemsChanged, ScenarioLoaded,
/// ScenarioUnloaded, RoomAvailableForCid) match too. The DTO subset
/// stays in the Strips assembly so the WASM linker tree-shakes the rest
/// of Core's payload metadata.
/// </summary>
public sealed class BrowserStripsTransport : IStripsTransport, IAsyncDisposable
{
    private static readonly ILogger Log = SimLog.CreateLogger("BrowserStripsTransport");

    private HubConnection? _connection;

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public event Action? Connected;
    public event Action<Exception?>? Closed;
    public event Action<Exception?>? Reconnecting;
    public event Action<string?>? Reconnected;
    public event Action<FlightStripsConfigDto?>? StripsConfigChanged;
    public event Action<FlightStripsStateDto>? FlightStripsStateChanged;
    public event Action<List<StripItemDto>>? StripItemsChanged;

    /// <summary>
    /// Server tells us a room bound to our CID has become available.
    /// Drives the auto-join handshake in the browser client when the
    /// initial <see cref="FindRoomForMyCidAsync"/> returned <c>null</c>.
    /// </summary>
    public event Action<string>? RoomAvailableForCid;

    /// <summary>
    /// Connects to the SignalR hub. <paramref name="serverUrl"/> can be
    /// empty / relative — SignalR.Client treats an empty URL as
    /// same-origin, which is what we want when yaat-server hosts
    /// <c>/vstrips/</c> directly.
    /// </summary>
    public async Task ConnectAsync(string serverUrl, CancellationToken ct = default)
    {
        if (_connection is not null)
        {
            await DisconnectAsync();
        }

        var hubUrl = string.IsNullOrEmpty(serverUrl) ? "/hubs/training" : serverUrl.TrimEnd('/') + "/hubs/training";
        Log.LogInformation("Connecting to {Url}", hubUrl);

        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .AddJsonProtocol(options =>
            {
                // Only the Strips context — the WASM client never sees
                // Core's broader DTO surface (room state, aircraft, weather,
                // CRC). System.Text.Json is reflection-disabled in the
                // default WASM publish; the source-gen entry resolves every
                // type the strip view actually serializes.
                options.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, YaatStripsHubJsonContext.Default);
            })
            .Build();

        _connection.On<FlightStripsStateDto>("FlightStripsStateChanged", dto => FlightStripsStateChanged?.Invoke(dto));
        _connection.On<List<StripItemDto>>("StripItemsChanged", items => StripItemsChanged?.Invoke(items));
        _connection.On<BrowserScenarioLoadedDto>("ScenarioLoaded", dto => StripsConfigChanged?.Invoke(dto.FlightStripsConfig));
        _connection.On("ScenarioUnloaded", () => StripsConfigChanged?.Invoke(null));
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

    private HubConnection RequireConnection() => _connection ?? throw new InvalidOperationException("BrowserStripsTransport is not connected");

    public Task<List<AccessibleFacilityDto>> GetAccessibleFacilitiesAsync() =>
        RequireConnection().InvokeAsync<List<AccessibleFacilityDto>>("GetAccessibleFacilities");

    public Task<FlightStripsConfigDto?> GetFlightStripsConfigForFacilityAsync(string facilityId) =>
        RequireConnection().InvokeAsync<FlightStripsConfigDto?>("GetFlightStripsConfigForFacility", facilityId);

    public Task<CommandResultDto> RequestFlightStripForAircraftAsync(string callsign) =>
        RequireConnection().InvokeAsync<CommandResultDto>("RequestFlightStripForAircraft", callsign);

    /// <summary>
    /// Looks up the room currently associated with a CID. Returns
    /// <c>null</c> when no room is active — pair with
    /// <see cref="RoomAvailableForCid"/> to wait until one becomes
    /// available.
    /// </summary>
    public Task<BrowserRoomInfoDto?> FindRoomForMyCidAsync(string cid) =>
        RequireConnection().InvokeAsync<BrowserRoomInfoDto?>("FindRoomForMyCid", cid);

    /// <summary>
    /// Joins a training room. <paramref name="kind"/> is a string from
    /// <see cref="ClientKind"/>. The server returns the broader
    /// <c>RoomStateDto</c>; we deserialize into a strip-only subset so
    /// the WASM bundle doesn't have to ship the full DTO graph.
    /// </summary>
    public Task<BrowserJoinRoomResultDto?> JoinRoomAsync(string roomId, string cid, string initials, string artccId, string kind) =>
        RequireConnection().InvokeAsync<BrowserJoinRoomResultDto?>("JoinRoom", roomId, cid, initials, artccId, kind);

    public Task<CommandResultDto> SendCommandAsync(string callsign, string command, string initials) =>
        RequireConnection().InvokeAsync<CommandResultDto>("SendCommand", callsign, command, initials);
}

/// <summary>
/// Subset of <c>TrainingRoomInfoDto</c> the browser client needs from
/// <see cref="BrowserStripsTransport.FindRoomForMyCidAsync"/>. JSON
/// deserialization is case-insensitive and ignores extra fields, so the
/// server's full payload round-trips into this narrower record without
/// pulling the rest of the room-info graph into the WASM bundle.
/// </summary>
public record BrowserRoomInfoDto(string RoomId, string CreatorInitials);

/// <summary>
/// Subset of <c>RoomStateDto</c> the browser client needs from
/// <see cref="BrowserStripsTransport.JoinRoomAsync"/>: enough to display
/// status and initialize the strip view.
/// </summary>
public record BrowserJoinRoomResultDto(string RoomId, string? ScenarioName, FlightStripsConfigDto? FlightStripsConfig);

/// <summary>
/// Subset of <c>ScenarioLoadedDto</c> consumed by the browser transport's
/// <c>ScenarioLoaded</c> hub handler. Used only to project the broader
/// payload down to its strip-relevant field for
/// <see cref="IStripsTransport.StripsConfigChanged"/>.
/// </summary>
public record BrowserScenarioLoadedDto(FlightStripsConfigDto? FlightStripsConfig);
