using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;

namespace Yaat.Client.Services;

public sealed class ServerConnection : IAsyncDisposable
{
    private readonly ILogger _log = AppLog.CreateLogger<ServerConnection>();

    private HubConnection? _connection;
    private PeriodicTimer? _heartbeatTimer;
    private CancellationTokenSource? _heartbeatCts;

    public event Action<AircraftDto>? AircraftUpdated;
    public event Action<string>? AircraftDeleted;
    public event Action<AircraftDto>? AircraftSpawned;
    public event Action<bool, int>? SimulationStateChanged;
    public event Action<string?>? Reconnected;
    public event Action<Exception?>? Reconnecting;
    public event Action<Exception?>? Closed;
    public event Action<TerminalBroadcastDto>? TerminalEntryReceived;
    public event Action<RoomMemberChangedDto>? RoomMemberChanged;
    public event Action<CrcLobbyChangedDto>? CrcLobbyChanged;
    public event Action<CrcRoomMembersChangedDto>? CrcRoomMembersChanged;
    public event Action<WeatherChangedDto>? WeatherChanged;

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public async Task ConnectAsync(string serverUrl)
    {
        if (_connection is not null)
        {
            await DisconnectAsync();
        }

        var hubUrl = serverUrl.TrimEnd('/') + "/hubs/training";
        _log.LogInformation("Connecting to {Url}", hubUrl);

        _connection = new HubConnectionBuilder().WithUrl(hubUrl).WithAutomaticReconnect().Build();

        _connection.On<AircraftDto>("AircraftUpdated", dto => AircraftUpdated?.Invoke(dto));

        _connection.On<string>("AircraftDeleted", cs => AircraftDeleted?.Invoke(cs));

        _connection.On<AircraftDto>("AircraftSpawned", dto => AircraftSpawned?.Invoke(dto));

        _connection.On<bool, int>("SimulationStateChanged", (paused, rate) => SimulationStateChanged?.Invoke(paused, rate));

        _connection.On<TerminalBroadcastDto>("TerminalBroadcast", dto => TerminalEntryReceived?.Invoke(dto));

        _connection.On<RoomMemberChangedDto>("RoomMemberChanged", dto => RoomMemberChanged?.Invoke(dto));

        _connection.On<CrcLobbyChangedDto>("CrcLobbyChanged", dto => CrcLobbyChanged?.Invoke(dto));

        _connection.On<CrcRoomMembersChangedDto>("CrcRoomMembersChanged", dto => CrcRoomMembersChanged?.Invoke(dto));

        _connection.On<WeatherChangedDto>("WeatherChanged", dto => WeatherChanged?.Invoke(dto));

        _connection.Reconnecting += error =>
        {
            _log.LogWarning(error, "Connection lost, reconnecting");
            Reconnecting?.Invoke(error);
            return Task.CompletedTask;
        };

        _connection.Reconnected += connectionId =>
        {
            _log.LogInformation("Reconnected with id {Id}", connectionId);
            Reconnected?.Invoke(connectionId);
            return Task.CompletedTask;
        };

        _connection.Closed += error =>
        {
            _log.LogWarning(error, "Connection closed permanently");
            Closed?.Invoke(error);
            return Task.CompletedTask;
        };

        await _connection.StartAsync();
        _log.LogInformation("Connected");

        StartHeartbeat();
    }

    public async Task DisconnectAsync()
    {
        StopHeartbeat();

        if (_connection is null)
        {
            return;
        }

        await _connection.DisposeAsync();
        _connection = null;
        _log.LogInformation("Disconnected");
    }

    // --- Room lifecycle ---

    public async Task<string> CreateRoomAsync(string cid, string initials, string artccId)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<string>("CreateRoom", cid, initials, artccId);
    }

    public async Task<RoomStateDto?> JoinRoomAsync(string roomId, string cid, string initials, string artccId)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<RoomStateDto?>("JoinRoom", roomId, cid, initials, artccId);
    }

    public async Task LeaveRoomAsync()
    {
        EnsureConnected();
        await _connection!.InvokeAsync("LeaveRoom");
    }

    public async Task<List<TrainingRoomInfoDto>> GetActiveRoomsAsync()
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<List<TrainingRoomInfoDto>>("GetActiveRooms");
    }

    // --- Scenario lifecycle ---

    public async Task<LoadScenarioResultDto> LoadScenarioAsync(string scenarioJson)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<LoadScenarioResultDto>("LoadScenario", scenarioJson);
    }

    // --- Weather ---

    public async Task<CommandResultDto> LoadWeatherAsync(string weatherJson)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<CommandResultDto>("LoadWeather", weatherJson);
    }

    public async Task ClearWeatherAsync()
    {
        EnsureConnected();
        await _connection!.InvokeAsync("ClearWeather");
    }

    // --- Aircraft commands ---

    public async Task<CommandResultDto> SendCommandAsync(string callsign, string command, string initials)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<CommandResultDto>("SendCommand", callsign, command, initials);
    }

    public async Task SendChatAsync(string initials, string message)
    {
        EnsureConnected();
        await _connection!.InvokeAsync("SendChat", initials, message);
    }

    public async Task<CommandResultDto> SpawnAircraftAsync(string args)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<CommandResultDto>("SpawnAircraft", args);
    }

    public async Task DeleteAircraftAsync(string callsign)
    {
        EnsureConnected();
        await _connection!.InvokeAsync("DeleteAircraft", callsign);
    }

    public async Task WarpAircraftAsync(string callsign, double latitude, double longitude, double heading)
    {
        EnsureConnected();
        await _connection!.InvokeAsync("WarpAircraft", callsign, latitude, longitude, heading);
    }

    public async Task<UnloadScenarioResultDto> UnloadScenarioAircraftAsync()
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<UnloadScenarioResultDto>("UnloadScenarioAircraft");
    }

    public async Task ConfirmUnloadScenarioAsync()
    {
        EnsureConnected();
        await _connection!.InvokeAsync("ConfirmUnloadScenario");
    }

    // --- Simulation state ---

    public async Task PauseSimulationAsync()
    {
        EnsureConnected();
        await _connection!.InvokeAsync("PauseSimulation");
    }

    public async Task ResumeSimulationAsync()
    {
        EnsureConnected();
        await _connection!.InvokeAsync("ResumeSimulation");
    }

    public async Task SetSimRateAsync(int rate)
    {
        EnsureConnected();
        await _connection!.InvokeAsync("SetSimRate", rate);
    }

    public async Task SetAutoAcceptDelayAsync(int seconds)
    {
        EnsureConnected();
        await _connection!.InvokeAsync("SetAutoAcceptDelay", seconds);
    }

    public async Task SetAutoDeleteModeAsync(string? mode)
    {
        EnsureConnected();
        await _connection!.InvokeAsync("SetAutoDeleteMode", mode);
    }

    public async Task SetValidateDctFixesAsync(bool validate)
    {
        EnsureConnected();
        await _connection!.InvokeAsync("SetValidateDctFixes", validate);
    }

    // --- Data queries ---

    public async Task<GroundLayoutDto?> GetAirportGroundLayoutAsync(string airportId)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<GroundLayoutDto?>("GetAirportGroundLayout", airportId);
    }

    public async Task<FacilityVideoMapsDto?> GetFacilityVideoMapsAsync(string artccId, string facilityId)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<FacilityVideoMapsDto?>("GetFacilityVideoMaps", artccId, facilityId);
    }

    public async Task<FacilityVideoMapsDto?> GetFacilityVideoMapsForArtccAsync(string artccId, string? airportId = null)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<FacilityVideoMapsDto?>("GetFacilityVideoMapsForArtcc", artccId, airportId);
    }

    public async Task<ApproachReportDto?> GetApproachReportAsync()
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<ApproachReportDto?>("GetApproachReport");
    }

    // --- Admin ---

    public async Task<bool> AdminAuthenticateAsync(string password)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<bool>("AdminAuthenticate", password);
    }

    public async Task<List<TrainingRoomInfoDto>> AdminGetRoomsAsync()
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<List<TrainingRoomInfoDto>>("AdminGetScenarios");
    }

    public async Task AdminSetRoomFilterAsync(string? roomId)
    {
        EnsureConnected();
        await _connection!.InvokeAsync("AdminSetScenarioFilter", roomId);
    }

    // --- CRC client management ---

    public async Task<List<CrcLobbyClientDto>> GetCrcLobbyClientsAsync()
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<List<CrcLobbyClientDto>>("GetCrcLobbyClients");
    }

    public async Task<bool> PullCrcClientAsync(string clientId)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<bool>("PullCrcClient", clientId);
    }

    public async Task<bool> KickCrcClientAsync(string clientId)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<bool>("KickCrcClient", clientId);
    }

    public async Task<List<CrcRoomMemberDto>> GetCrcRoomMembersAsync()
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<List<CrcRoomMemberDto>>("GetCrcRoomMembers");
    }

    // --- Lifecycle ---

    public async ValueTask DisposeAsync()
    {
        StopHeartbeat();
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
    }

    private void EnsureConnected()
    {
        if (_connection is null)
        {
            throw new InvalidOperationException("Not connected.");
        }
    }

    private void StartHeartbeat()
    {
        _heartbeatCts = new CancellationTokenSource();
        _heartbeatTimer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        _ = RunHeartbeat(_heartbeatCts.Token);
    }

    private void StopHeartbeat()
    {
        _heartbeatCts?.Cancel();
        _heartbeatCts?.Dispose();
        _heartbeatCts = null;
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
    }

    private async Task RunHeartbeat(CancellationToken ct)
    {
        try
        {
            while (await _heartbeatTimer!.WaitForNextTickAsync(ct))
            {
                if (_connection?.State == HubConnectionState.Connected)
                {
                    await _connection.InvokeAsync("Heartbeat", ct);
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}

// --- DTOs ---

public record AircraftDto(
    string Callsign,
    string AircraftType,
    double Latitude,
    double Longitude,
    double Heading,
    double Altitude,
    double GroundSpeed,
    uint BeaconCode,
    string TransponderMode,
    double VerticalSpeed,
    double? AssignedHeading,
    double? AssignedAltitude,
    double? AssignedSpeed,
    string Departure,
    string Destination,
    string Route,
    string FlightRules,
    string Status,
    string PendingCommands = "",
    string NavigatingTo = "",
    string CurrentPhase = "",
    string AssignedRunway = "",
    bool IsOnGround = false,
    string PhaseSequence = "",
    int ActivePhaseIndex = -1,
    string LandingClearance = "",
    string ClearedRunway = "",
    string PatternDirection = "",
    string NavigationRoute = "",
    string EquipmentSuffix = "",
    int CruiseAltitude = 0,
    int CruiseSpeed = 0,
    string TaxiRoute = "",
    string ParkingSpot = "",
    string CurrentTaxiway = "",
    string? Owner = null,
    string? OwnerSectorCode = null,
    string? HandoffPeer = null,
    string? HandoffPeerSectorCode = null,
    string? PointoutStatus = null,
    string? Scratchpad1 = null,
    string? Scratchpad2 = null,
    int? TemporaryAltitude = null,
    bool IsAnnotated = false,
    string? ActiveApproachId = null,
    string? ExpectedApproach = null
);

public record LoadScenarioResultDto(
    bool Success,
    string Name,
    string ScenarioId,
    int AircraftCount,
    int DelayedCount,
    bool IsPaused,
    int SimRate,
    string? PrimaryAirportId,
    List<string> Warnings,
    List<AircraftDto> AllAircraft
);

public record CommandResultDto(bool Success, string? Message);

public record TrainingRoomInfoDto(
    string RoomId,
    string CreatorInitials,
    string CreatorArtccId,
    string? ScenarioName,
    List<string> MemberInitials,
    bool IsPaused,
    double SimRate,
    double ElapsedSeconds,
    int AircraftCount
);

public record RoomStateDto(
    string RoomId,
    string CreatorInitials,
    string CreatorArtccId,
    List<RoomMemberDto> Members,
    string? ScenarioName,
    string? ScenarioId,
    bool IsPaused,
    double SimRate,
    string? PrimaryAirportId,
    List<AircraftDto> AllAircraft
);

public record RoomMemberDto(string Cid, string Initials, string ArtccId);

public record RoomMemberChangedDto(string RoomId, List<RoomMemberDto> Members, string? ScenarioName);

public record UnloadScenarioResultDto(bool RequiresConfirmation, int OtherClientCount, string? Message);

public record TerminalBroadcastDto(string Initials, string Kind, string Callsign, string Message, DateTime Timestamp);

public record CrcLobbyClientDto(string ClientId, string? Cid, string? DisplayName, string? ArtccId, string? PositionId, bool IsActive);

public record CrcLobbyChangedDto(List<CrcLobbyClientDto> Clients);

public record CrcRoomMemberDto(string ClientId, string? Cid, string? DisplayName, string? PositionId, bool IsActive);

public record CrcRoomMembersChangedDto(string RoomId, List<CrcRoomMemberDto> Members);

public record GroundLayoutDto(string AirportId, List<GroundNodeDto> Nodes, List<GroundEdgeDto> Edges, List<GroundRunwayDto>? Runways);

public record GroundNodeDto(int Id, double Latitude, double Longitude, string Type, string? Name, double? Heading, string? RunwayId);

public record GroundEdgeDto(int FromNodeId, int ToNodeId, string TaxiwayName, double DistanceNm, List<double[]>? IntermediatePoints);

public record GroundRunwayDto(string Name, List<double[]> Coordinates, double WidthFt);

public record VideoMapInfoDto(
    string Id,
    string Name,
    string ShortName,
    List<string> Tags,
    string BrightnessCategory,
    int StarsId,
    bool AlwaysVisible,
    bool TdmOnly
);

public record StarsAreaDto(string Id, string Name, double CenterLat, double CenterLon, double SurveillanceRange, List<string> VideoMapIds);

public record MapGroupDto(List<int?> MapIds, List<string> TcpCodes);

public record FacilityVideoMapsDto(
    string ArtccId,
    string FacilityId,
    List<StarsAreaDto> Areas,
    List<VideoMapInfoDto> VideoMaps,
    List<MapGroupDto> MapGroups
);

public record WeatherChangedDto(string? Name, List<WindLayerDto>? WindLayers, string? Precipitation, List<string>? Metars);

public record WindLayerDto(int Altitude, int Direction, int Speed, int? Gusts);

// --- Approach Report DTOs ---

public record ApproachScoreDto(
    string Callsign,
    string AircraftType,
    string ApproachId,
    string RunwayId,
    double InterceptAngleDeg,
    double InterceptDistanceNm,
    double MinInterceptDistanceNm,
    double GlideSlopeDeviationFt,
    double SpeedAtInterceptKts,
    bool WasForced,
    bool IsPatternTraffic,
    bool IsInterceptAngleLegal,
    bool IsInterceptDistanceLegal,
    string Grade,
    double EstablishedAtSeconds,
    double? LandedAtSeconds,
    double? SeparationNm
);

public record RunwayStatsDto(
    string RunwayId,
    int LandingCount,
    double ArrivalRatePerHour,
    double AverageTimeBetweenLandingsSec,
    double? MinSeparationNm
);

public record ApproachReportDto(
    List<ApproachScoreDto> Approaches,
    List<RunwayStatsDto> RunwayStats,
    double ScenarioElapsedSeconds,
    string OverallGrade
);
