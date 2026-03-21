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
    public event Action<bool, int, double, bool, double>? SimulationStateChanged;
    public event Action<string?>? Reconnected;
    public event Action<Exception?>? Reconnecting;
    public event Action<Exception?>? Closed;
    public event Action<TerminalBroadcastDto>? TerminalEntryReceived;
    public event Action<RoomMemberChangedDto>? RoomMemberChanged;
    public event Action<CrcLobbyChangedDto>? CrcLobbyChanged;
    public event Action<CrcRoomMembersChangedDto>? CrcRoomMembersChanged;
    public event Action<WeatherChangedDto>? WeatherChanged;
    public event Action<PositionDisplayConfigDto>? PositionDisplayChanged;
    public event Action<ScenarioLoadedDto>? ScenarioLoaded;
    public event Action? ScenarioUnloaded;
    public event Action<AircraftAssignmentsDto>? AircraftAssignmentsChanged;
    public event Action<string>? KickedFromRoom;

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public async Task ConnectAsync(string serverUrl, CancellationToken ct = default)
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

        _connection.On<bool, int, double, bool, double>(
            "SimulationStateChanged",
            (paused, rate, elapsed, isPlayback, tapeEnd) => SimulationStateChanged?.Invoke(paused, rate, elapsed, isPlayback, tapeEnd)
        );

        _connection.On<TerminalBroadcastDto>("TerminalBroadcast", dto => TerminalEntryReceived?.Invoke(dto));

        _connection.On<RoomMemberChangedDto>("RoomMemberChanged", dto => RoomMemberChanged?.Invoke(dto));

        _connection.On<CrcLobbyChangedDto>("CrcLobbyChanged", dto => CrcLobbyChanged?.Invoke(dto));

        _connection.On<CrcRoomMembersChangedDto>("CrcRoomMembersChanged", dto => CrcRoomMembersChanged?.Invoke(dto));

        _connection.On<WeatherChangedDto>("WeatherChanged", dto => WeatherChanged?.Invoke(dto));

        _connection.On<PositionDisplayConfigDto>("PositionDisplayChanged", dto => PositionDisplayChanged?.Invoke(dto));

        _connection.On<ScenarioLoadedDto>("ScenarioLoaded", dto => ScenarioLoaded?.Invoke(dto));
        _connection.On("ScenarioUnloaded", () => ScenarioUnloaded?.Invoke());
        _connection.On<AircraftAssignmentsDto>("AircraftAssignmentsChanged", dto => AircraftAssignmentsChanged?.Invoke(dto));
        _connection.On<string>("KickedFromRoom", msg => KickedFromRoom?.Invoke(msg));

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

        await _connection.StartAsync(ct);
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
        var byteSize = System.Text.Encoding.UTF8.GetByteCount(scenarioJson);
        _log.LogInformation("Sending scenario JSON to server ({ByteSize} bytes)", byteSize);
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

    // --- Aircraft assignments ---

    public async Task AssignAircraftAsync(List<string> callsigns, string targetConnectionId)
    {
        EnsureConnected();
        await _connection!.InvokeAsync("AssignAircraft", callsigns, targetConnectionId);
    }

    public async Task UnassignAircraftAsync(List<string> callsigns)
    {
        EnsureConnected();
        await _connection!.InvokeAsync("UnassignAircraft", callsigns);
    }

    public async Task<AircraftAssignmentsDto?> GetAircraftAssignmentsAsync()
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<AircraftAssignmentsDto?>("GetAircraftAssignments");
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

    public async Task<CommandResultDto> AmendFlightPlanAsync(string callsign, FlightPlanAmendmentDto dto)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<CommandResultDto>("AmendFlightPlan", callsign, dto);
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

    public async Task SetAutoClearedToLandAsync(bool enabled)
    {
        EnsureConnected();
        await _connection!.InvokeAsync("SetAutoClearedToLand", enabled);
    }

    public async Task SetAutoCrossRunwayAsync(bool enabled)
    {
        EnsureConnected();
        await _connection!.InvokeAsync("SetAutoCrossRunway", enabled);
    }

    // --- Timeline / Rewind ---

    public async Task<RewindResultDto?> RewindToAsync(double elapsedSeconds)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<RewindResultDto?>("RewindTo", elapsedSeconds);
    }

    public async Task TakeControlAsync()
    {
        EnsureConnected();
        await _connection!.InvokeAsync("TakeControl");
    }

    public async Task<TimelineInfoDto?> GetTimelineInfoAsync()
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<TimelineInfoDto?>("GetTimelineInfo");
    }

    public async Task<string?> ExportRecordingAsync()
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<string?>("ExportRecording");
    }

    public async Task<string?> GetServerLogPathAsync()
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<string?>("GetServerLogPath");
    }

    public async Task<RewindResultDto?> LoadRecordingAsync(string recordingJson)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<RewindResultDto?>("LoadRecording", recordingJson);
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

    public async Task<bool> KickMemberAsync(string cid)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<bool>("KickMember", cid);
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
    string Remarks = "",
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
    List<string>? NavigationRoute = null,
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
    string? ExpectedApproach = null,
    string CwtCode = "",
    string ActiveSidId = "",
    string ActiveStarId = "",
    string DepartureRunway = "",
    string DestinationRunway = "",
    double IndicatedAirspeed = 0,
    double Mach = 0,
    double? AssignedMach = null,
    int WindDirection = 0,
    int WindSpeed = 0,
    List<double[]>? PositionHistory = null
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
    List<AircraftDto> AllAircraft,
    string? WeatherName = null,
    PositionDisplayConfigDto? PositionDisplayConfig = null,
    bool AutoClearedToLand = false,
    bool IsStudentTowerPosition = true,
    string? StudentPositionType = null
);

public record PositionDisplayConfigDto(List<int?> MapGroupMapIds, List<string> MapGroupTcpCodes, List<string> UnderlyingAirports, string TcpCode);

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
    List<AircraftDto> AllAircraft,
    PositionDisplayConfigDto? PositionDisplayConfig = null,
    double ElapsedSeconds = 0,
    bool IsPlayback = false,
    double TapeEnd = 0
);

public record ScenarioLoadedDto(
    string ScenarioId,
    string ScenarioName,
    string? PrimaryAirportId,
    bool IsPaused,
    int SimRate,
    List<AircraftDto> AllAircraft,
    PositionDisplayConfigDto? PositionDisplayConfig = null,
    bool AutoClearedToLand = false,
    bool IsStudentTowerPosition = true,
    string? StudentPositionType = null
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

public record WeatherChangedDto(string? Name, List<WindLayerDto>? WindLayers, string? Precipitation, List<string>? Metars, string? SourceJson);

public record WindLayerDto(int Altitude, int Direction, int Speed, int? Gusts);

public record FlightPlanAmendmentDto(
    string? AircraftType = null,
    string? EquipmentSuffix = null,
    string? Departure = null,
    string? Destination = null,
    int? CruiseSpeed = null,
    int? CruiseAltitude = null,
    string? FlightRules = null,
    string? Route = null,
    string? Remarks = null,
    string? Scratchpad1 = null,
    string? Scratchpad2 = null,
    uint? BeaconCode = null
);

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

public record TimelineInfoDto(double ElapsedSeconds, double TapeEnd, bool IsPlayback, bool IsAvailable);

public record RewindResultDto(
    bool Success,
    string? Error,
    List<AircraftDto>? Aircraft = null,
    string? ScenarioId = null,
    string? ScenarioName = null,
    string? PrimaryAirportId = null,
    string? ArtccId = null,
    string? StudentPositionType = null,
    bool IsPaused = true,
    int SimRate = 1,
    double ElapsedSeconds = 0,
    bool IsPlayback = false,
    double TapeEnd = 0
);

public record AssignableMemberDto(string ConnectionId, string Initials);

public record AircraftAssignmentsDto(Dictionary<string, string> Assignments, List<AssignableMemberDto> Members);
