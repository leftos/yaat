using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;

namespace Yaat.Client.Services;

public sealed class ServerConnection : IStripsTransport, ITdlsTransport, IAsyncDisposable
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

    /// <summary>
    /// Fires once after <see cref="ConnectAsync"/> completes a successful
    /// initial handshake. Distinct from <see cref="Reconnected"/>, which only
    /// fires after a transport drop. Subscribers tracking "is the SignalR
    /// link live" need both — without this event the very first connect
    /// goes unnoticed and dependent UI stays in its disconnected state.
    /// </summary>
    public event Action? Connected;
    public event Action<TerminalBroadcastDto>? TerminalEntryReceived;
    public event Action<PilotTransmissionBroadcastDto>? PilotTransmissionReceived;
    public event Action<RoomMemberChangedDto>? RoomMemberChanged;
    public event Action<CrcLobbyChangedDto>? CrcLobbyChanged;
    public event Action<RpoLobbyChangedDto>? RpoLobbyChanged;
    public event Action<CrcRoomMembersChangedDto>? CrcRoomMembersChanged;
    public event Action<WeatherChangedDto>? WeatherChanged;
    public event Action<ArrivalGeneratorsChangedDto>? ArrivalGeneratorsChanged;
    public event Action<HeldDeparturesChangedDto>? HeldDeparturesChanged;
    public event Action<TimersChangedDto>? TimersChanged;
    public event Action<BookmarksChangedDto>? BookmarksChanged;
    public event Action<PositionDisplayConfigDto>? PositionDisplayChanged;
    public event Action<ScenarioLoadedDto>? ScenarioLoaded;
    public event Action? ScenarioUnloaded;
    public event Action<AircraftAssignmentsDto>? AircraftAssignmentsChanged;
    public event Action<SessionSettingsDto>? SessionSettingsChanged;
    public event Action<string>? KickedFromRoom;
    public event Action<string>? RoomRetired;
    public event Action<int, int>? ExportRecordingProgress;
    public event Action<FlightStripsStateDto>? FlightStripsStateChanged;
    public event Action<List<StripItemDto>>? StripItemsChanged;
    public event Action<TdlsItemDto>? TdlsItemChanged;
    public event Action<TdlsItemRemovedDto>? TdlsItemRemoved;
    public event Action<TdlsStateDto>? TdlsStateChanged;
    public event Action<string>? RoomAvailableForCid;
    public event Action<DateTime, string, int>? ServerRestarting;
    public event Action? ServerRestartReady;
    public event Action<List<RestoredRoomInfoDto>>? ServerRestartComplete;

    /// <summary>
    /// Strip-side projection of <see cref="ScenarioLoaded"/> +
    /// <see cref="ScenarioUnloaded"/>. Fires the new
    /// <see cref="FlightStripsConfigDto"/> on scenario load (may be
    /// <c>null</c> if the loaded scenario has no strips support) and
    /// <c>null</c> on scenario unload. Drives <c>VStripsViewModel</c>
    /// without leaking the broader <see cref="ScenarioLoadedDto"/>
    /// (which embeds <c>AircraftDto</c> and would force the
    /// WASM-clean Strips assembly to take a runtime dep on Core).
    /// </summary>
    public event Action<FlightStripsConfigDto?>? StripsConfigChanged;

    /// <summary>
    /// Strip-side projection of <see cref="WeatherChanged"/> — just the raw
    /// METAR strings, so the WASM-clean Strips assembly's
    /// <c>IStripsTransport</c> doesn't take a dep on <see cref="WeatherChangedDto"/>.
    /// Fired alongside <see cref="WeatherChanged"/> from the same hub handler.
    /// </summary>
    public event Action<IReadOnlyList<string>>? MetarsChanged;

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public async Task ConnectAsync(string serverUrl, Func<Task<string?>> accessTokenProvider, CancellationToken ct = default)
    {
        if (_connection is not null)
        {
            await DisconnectAsync();
        }

        var hubUrl = serverUrl.TrimEnd('/') + "/hubs/training";
        _log.LogInformation("Connecting to {Url}", hubUrl);

        _connection = new HubConnectionBuilder()
            .WithUrl(
                hubUrl,
                options =>
                {
                    // Supplies the YAAT session token as the SignalR access token. It rides as the
                    // access_token query string on the WebSocket negotiate, where the server's
                    // JwtBearer handler lifts it onto the request.
                    options.AccessTokenProvider = accessTokenProvider;
                }
            )
            .WithAutomaticReconnect(new YaatReconnectPolicy())
            .AddJsonProtocol(options =>
            {
                // Insert both source-generated contexts at the head of the
                // chain so SignalR resolves every DTO via compile-time
                // metadata. The Strips context owns the strip-side payloads
                // (FlightStripsStateDto, StripItemDto, FlightStripsConfigDto,
                // AccessibleFacilityDto, CommandResultDto) and lives in
                // Yaat.Client.Strips so the WASM client can ship without
                // Core. The Core context owns everything else (room state,
                // aircraft, weather, CRC). Resolver chains are consulted per
                // type, so each registration is self-contained.
                //
                // Falling through to the default reflection-based resolver
                // afterwards covers the few primitives source-gen still
                // passes through (cancellation tokens, Guid, etc.) and lets
                // the desktop client keep working unchanged. In WASM the
                // chain stops at the matching source-gen entry for any DTO
                // we registered, sidestepping the reflection-disabled-by-
                // default failure that blew up JoinRoom.
                var chain = options.PayloadSerializerOptions.TypeInfoResolverChain;
                chain.Insert(0, YaatHubJsonContext.Default);
                chain.Insert(1, YaatStripsHubJsonContext.Default);
                chain.Insert(2, YaatTdlsHubJsonContext.Default);
            })
            .Build();

        _connection.On<AircraftDto>("AircraftUpdated", dto => AircraftUpdated?.Invoke(dto));

        _connection.On<string>("AircraftDeleted", cs => AircraftDeleted?.Invoke(cs));

        _connection.On<AircraftDto>("AircraftSpawned", dto => AircraftSpawned?.Invoke(dto));

        _connection.On<bool, int, double, bool, double>(
            "SimulationStateChanged",
            (paused, rate, elapsed, isPlayback, tapeEnd) => SimulationStateChanged?.Invoke(paused, rate, elapsed, isPlayback, tapeEnd)
        );

        _connection.On<TerminalBroadcastDto>("TerminalBroadcast", dto => TerminalEntryReceived?.Invoke(dto));

        _connection.On<PilotTransmissionBroadcastDto>("PilotTransmissionBroadcast", dto => PilotTransmissionReceived?.Invoke(dto));

        _connection.On<RoomMemberChangedDto>("RoomMemberChanged", dto => RoomMemberChanged?.Invoke(dto));

        _connection.On<CrcLobbyChangedDto>("CrcLobbyChanged", dto => CrcLobbyChanged?.Invoke(dto));

        _connection.On<RpoLobbyChangedDto>("RpoLobbyChanged", dto => RpoLobbyChanged?.Invoke(dto));

        _connection.On<CrcRoomMembersChangedDto>("CrcRoomMembersChanged", dto => CrcRoomMembersChanged?.Invoke(dto));

        _connection.On<WeatherChangedDto>(
            "WeatherChanged",
            dto =>
            {
                WeatherChanged?.Invoke(dto);
                MetarsChanged?.Invoke(dto.Metars ?? []);
            }
        );

        _connection.On<ArrivalGeneratorsChangedDto>("ArrivalGeneratorsChanged", dto => ArrivalGeneratorsChanged?.Invoke(dto));
        _connection.On<HeldDeparturesChangedDto>("HeldDeparturesChanged", dto => HeldDeparturesChanged?.Invoke(dto));
        _connection.On<TimersChangedDto>("TimersChanged", dto => TimersChanged?.Invoke(dto));
        _connection.On<BookmarksChangedDto>("BookmarksChanged", dto => BookmarksChanged?.Invoke(dto));

        _connection.On<PositionDisplayConfigDto>("PositionDisplayChanged", dto => PositionDisplayChanged?.Invoke(dto));

        _connection.On<ScenarioLoadedDto>(
            "ScenarioLoaded",
            dto =>
            {
                ScenarioLoaded?.Invoke(dto);
                StripsConfigChanged?.Invoke(dto.FlightStripsConfig);
            }
        );
        _connection.On(
            "ScenarioUnloaded",
            () =>
            {
                ScenarioUnloaded?.Invoke();
                StripsConfigChanged?.Invoke(null);
            }
        );
        _connection.On<AircraftAssignmentsDto>("AircraftAssignmentsChanged", dto => AircraftAssignmentsChanged?.Invoke(dto));
        _connection.On<SessionSettingsDto>("SessionSettingsChanged", dto => SessionSettingsChanged?.Invoke(dto));
        _connection.On<string>("KickedFromRoom", msg => KickedFromRoom?.Invoke(msg));
        _connection.On<string>("RoomRetired", msg => RoomRetired?.Invoke(msg));
        _connection.On<int, int>("ExportRecordingProgress", (current, total) => ExportRecordingProgress?.Invoke(current, total));
        _connection.On<FlightStripsStateDto>("FlightStripsStateChanged", dto => FlightStripsStateChanged?.Invoke(dto));
        _connection.On<List<StripItemDto>>("StripItemsChanged", items => StripItemsChanged?.Invoke(items));
        _connection.On<TdlsItemDto>("TdlsItemChanged", dto => TdlsItemChanged?.Invoke(dto));
        _connection.On<TdlsItemRemovedDto>("TdlsItemRemoved", dto => TdlsItemRemoved?.Invoke(dto));
        _connection.On<TdlsStateDto>("TdlsStateChanged", dto => TdlsStateChanged?.Invoke(dto));
        _connection.On<string>("RoomAvailableForCid", roomId => RoomAvailableForCid?.Invoke(roomId));

        _connection.On<DateTime, string, int>(
            "ServerRestarting",
            (restartAt, reason, drainSeconds) => ServerRestarting?.Invoke(restartAt, reason, drainSeconds)
        );

        _connection.On("ServerRestartReady", () => ServerRestartReady?.Invoke());

        _connection.On<List<RestoredRoomInfoDto>>("ServerRestartComplete", rooms => ServerRestartComplete?.Invoke(rooms));

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
        Connected?.Invoke();

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

    public async Task<string> CreateRoomAsync(string initials, string artccId, string kind)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<string>("CreateRoom", initials, artccId, kind);
    }

    public async Task<RoomStateDto?> JoinRoomAsync(string roomId, string initials, string artccId, string kind)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<RoomStateDto?>("JoinRoom", roomId, initials, artccId, kind);
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

    public async Task<TrainingRoomInfoDto?> FindRoomForMyCidAsync()
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<TrainingRoomInfoDto?>("FindRoomForMyCid");
    }

    // --- Scenario lifecycle ---

    public async Task<LoadScenarioResultDto> LoadScenarioAsync(
        string scenarioJson,
        int soloParkingInitialCallupRatePercent,
        int soloArrivalGeneratorRatePercent,
        int soloGoAroundProbabilityPercent
    )
    {
        EnsureConnected();
        var byteSize = System.Text.Encoding.UTF8.GetByteCount(scenarioJson);
        _log.LogInformation("Sending scenario JSON to server ({ByteSize} bytes)", byteSize);
        return await _connection!.InvokeAsync<LoadScenarioResultDto>(
            "LoadScenario",
            scenarioJson,
            soloParkingInitialCallupRatePercent,
            soloArrivalGeneratorRatePercent,
            soloGoAroundProbabilityPercent
        );
    }

    /// <summary>
    /// ARTCC-tab load path, step 1. Resolves the canonical scenario JSON from the server's catalog,
    /// gated by the caller's verified VATSIM rating, so the client can run the difficulty/pacing setup
    /// before loading it back via <see cref="LoadScenarioAsync"/>. A non-null AccessDeniedReason means
    /// the caller's rating is below the scenario's minimum (or the client isn't in a room) and no JSON
    /// is returned.
    /// </summary>
    public async Task<ScenarioJsonResultDto> GetScenarioJsonByIdAsync(string scenarioId)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<ScenarioJsonResultDto>("GetScenarioJsonById", scenarioId);
    }

    /// <summary>
    /// Returns the scenarios the caller's verified VATSIM rating can load for the room's ARTCC, plus a
    /// count of scenarios hidden by the rating gate. The picker uses the count to surface "N scenarios
    /// hidden — requires a higher rating" inline.
    /// </summary>
    public async Task<ScenarioCatalogResponseDto> GetScenariosAsync()
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<ScenarioCatalogResponseDto>("GetScenarios");
    }

    // --- Flight-strips facility discovery ---

    /// <summary>
    /// Lists every facility the current room's student position can open a
    /// strips window for. Drives the facility-switcher popup and the
    /// 'Open strips window…' picker in the main client.
    /// </summary>
    public async Task<List<AccessibleFacilityDto>> GetAccessibleFacilitiesAsync()
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<List<AccessibleFacilityDto>>("GetAccessibleFacilities");
    }

    /// <summary>
    /// Fetches the bay layout DTO for a specific accessible facility. Returns
    /// null if the facility isn't in the position's accessible set, which the
    /// client treats as 'refuse to switch'.
    /// </summary>
    public async Task<FlightStripsConfigDto?> GetFlightStripsConfigForFacilityAsync(string facilityId)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<FlightStripsConfigDto?>("GetFlightStripsConfigForFacility", facilityId);
    }

    // --- vTDLS (ITdlsTransport) ---

    /// <summary>Lists facilities accessible to the room's student position that have a TDLS configuration.</summary>
    public async Task<List<AccessibleFacilityDto>> GetAccessibleTdlsFacilitiesAsync()
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<List<AccessibleFacilityDto>>("GetAccessibleTdlsFacilities");
    }

    /// <summary>Returns the TDLS bootstrap config (SIDs, transitions, dropdowns, mandatory flags) for a facility.</summary>
    public async Task<TdlsConfigDto?> GetTdlsConfigForFacilityAsync(string facilityId)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<TdlsConfigDto?>("GetTdlsConfigForFacility", facilityId);
    }

    /// <summary>Asks the server to push the room's current full TDLS state via <see cref="TdlsStateChanged"/>. Used on initial subscribe and after facility switches.</summary>
    public async Task RequestFullTdlsStateAsync()
    {
        EnsureConnected();
        await _connection!.InvokeAsync("RequestFullTdlsState");
    }

    // --- Weather ---

    public async Task<CommandResultDto> LoadWeatherAsync(string weatherJson, bool reconstructMetars)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<CommandResultDto>("LoadWeather", weatherJson, reconstructMetars);
    }

    public async Task ClearWeatherAsync()
    {
        EnsureConnected();
        await _connection!.InvokeAsync("ClearWeather");
    }

    // --- Arrival generators ---

    public async Task<CommandResultDto> LoadArrivalGeneratorsAsync(string generatorsJson)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<CommandResultDto>("LoadArrivalGenerators", generatorsJson);
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

    // --- Timeline bookmarks (shared room state) ---

    public async Task<CommandResultDto> AddBookmarkAsync(double timeSeconds, string? name, string initials)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<CommandResultDto>("AddBookmark", timeSeconds, name, initials);
    }

    public async Task<CommandResultDto> RenameBookmarkAsync(string id, string? name)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<CommandResultDto>("RenameBookmark", id, name);
    }

    public async Task<CommandResultDto> DeleteBookmarkAsync(string id)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<CommandResultDto>("DeleteBookmark", id);
    }

    public async Task<CommandResultDto> AmendFlightPlanAsync(string callsign, FlightPlanAmendmentDto dto)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<CommandResultDto>("AmendFlightPlan", callsign, dto);
    }

    /// <summary>
    /// Asks the server to recycle the aircraft's beacon code — releases the current
    /// assigned code back to the pool and assigns a fresh one. Mirrors CRC's recycle
    /// button next to the BCN field in its flight-plan editor.
    /// </summary>
    public async Task<CommandResultDto> RequestNewBeaconCodeAsync(string callsign)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<CommandResultDto>("RequestNewBeaconCode", callsign);
    }

    /// <summary>
    /// Task #18 — printer-modal "Request Strip" button. Idempotent server-side,
    /// so a double-click doesn't print twice. Returns a descriptive result so
    /// the UI can show success/failure feedback.
    /// </summary>
    public async Task<CommandResultDto> RequestFlightStripForAircraftAsync(string callsign)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<CommandResultDto>("RequestFlightStripForAircraft", callsign);
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

    public async Task SetCommandRunDelayAsync(int minSeconds, int maxSeconds)
    {
        EnsureConnected();
        await _connection!.InvokeAsync("SetCommandRunDelay", minSeconds, maxSeconds);
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

    public async Task SetRpoShowPilotSpeechAsync(bool enabled)
    {
        EnsureConnected();
        await _connection!.InvokeAsync("SetRpoShowPilotSpeech", enabled);
    }

    public async Task SetSoloTrainingModeAsync(bool enabled)
    {
        EnsureConnected();
        await _connection!.InvokeAsync("SetSoloTrainingMode", enabled);
    }

    public async Task SetSoloPacingRatesAsync(int parkingInitialCallupRatePercent, int arrivalGeneratorRatePercent, int goAroundProbabilityPercent)
    {
        EnsureConnected();
        await _connection!.InvokeAsync(
            "SetSoloPacingRates",
            parkingInitialCallupRatePercent,
            arrivalGeneratorRatePercent,
            goAroundProbabilityPercent
        );
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

    public async Task SetAutoPullUpToParallelAsync(bool enabled)
    {
        EnsureConnected();
        await _connection!.InvokeAsync("SetAutoPullUpToParallel", enabled);
    }

    // --- Timeline / Rewind ---

    public async Task<RewindResultDto?> RewindToAsync(double elapsedSeconds)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<RewindResultDto?>("RewindTo", elapsedSeconds);
    }

    public async Task<RewindResultDto?> RewindFromSnapshotAsync(double snapshotSeconds, double replayToSeconds)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<RewindResultDto?>("RewindFromSnapshot", snapshotSeconds, replayToSeconds);
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

    /// <summary>
    /// Fetches the room's recorded terminal log so the client can repopulate the terminal after
    /// loading a recording. Empty when no scenario is active or the recording predates terminal-log
    /// capture.
    /// </summary>
    public async Task<List<TerminalBroadcastDto>> GetTerminalLogAsync()
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<List<TerminalBroadcastDto>>("GetTerminalLog");
    }

    public async Task<byte[]?> ExportRecordingAsync(string clientVersion, string clientBuildKind, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var ms = new MemoryStream();
        await foreach (
            var chunk in _connection!
                .StreamAsync<byte[]>("ExportRecording", clientVersion, clientBuildKind, cancellationToken)
                .WithCancellation(cancellationToken)
        )
        {
            await ms.WriteAsync(chunk, cancellationToken);
        }

        return ms.Length == 0 ? null : ms.ToArray();
    }

    /// <summary>
    /// Returns the server's room-scoped, anonymized log slice for the current session as text, for
    /// embedding into a bug-report bundle. Works against remote servers (no disk access needed).
    /// </summary>
    public async Task<string?> GetSessionServerLogAsync()
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<string?>("GetSessionServerLog");
    }

    public async Task<RewindResultDto?> LoadRecordingAsync(byte[] recordingBytes, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<RewindResultDto?>("LoadRecording", ChunkBytes(recordingBytes, cancellationToken), cancellationToken);
    }

    private static async IAsyncEnumerable<byte[]> ChunkBytes(
        byte[] bytes,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        const int chunkSize = 16 * 1024;
        for (int offset = 0; offset < bytes.Length; offset += chunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int len = Math.Min(chunkSize, bytes.Length - offset);
            var chunk = new byte[len];
            Buffer.BlockCopy(bytes, offset, chunk, 0, len);
            yield return chunk;
            await Task.Yield();
        }
    }

    public async Task<byte[]?> MigrateRecordingAsync(string recordingJson)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<byte[]?>("MigrateRecording", recordingJson);
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

    public async Task<SessionReportDto?> GetSessionReportAsync()
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<SessionReportDto?>("GetSessionReport");
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

    // --- RPO lobby ---

    public async Task<List<RpoLobbyClientDto>> GetRpoLobbyClientsAsync()
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<List<RpoLobbyClientDto>>("GetRpoLobbyClients");
    }

    public async Task<bool> PullRpoAsync(string connectionId)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<bool>("PullRpo", connectionId);
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

    public async Task<List<OnlineControllerDto>> GetOnlineControllersAsync()
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<List<OnlineControllerDto>>("GetOnlineControllers");
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
    List<Yaat.Sim.NavRouteFixDto>? NavigationRoute = null,
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
    List<double[]>? PositionHistory = null,
    string? PatternEntryKind = null,
    string? FollowingCallsign = null,
    // Callsign of the traffic this aircraft most recently reported in sight (RTIS/RTISF).
    // Null until a successful report. Lets the client gate the "follow this traffic" menu item.
    string? LastReportedTrafficCallsign = null,
    string? ExitingRunwayId = null,
    string FiledAircraftType = "",
    bool IsUnsupported = false,
    // True when the ghost overlay sits on top of a real aircraft active in the scenario
    // (AID + slew). False for pure phantom STARS data blocks created by DA/VP typing
    // against a callsign with no aircraft body. The Aircraft List filter only hides
    // rows that are IsUnsupported && !IsGhostOverlay.
    bool IsGhostOverlay = false,
    bool HasActiveTaxiRoute = false,
    // Hold-state mirror of AircraftGroundOps.Hold. HoldKind is null/empty when free
    // to move, "HoldPosition" for unconditional HOLD, "GiveWay" for a controller
    // GIVEWAY relationship (HoldYieldTarget carries the callsign in that case).
    string? HoldKind = null,
    string? HoldYieldTarget = null,
    // Callsign this aircraft is auto-yielding to (GroundConflictDetector picked it as the yielder
    // for a converging / same-edge-trailing pair). Display-only: the aircraft is slowing, not held,
    // so IsHeld stays false. Drives the "→{target} (auto)" badge.
    string? AutoYieldTarget = null,
    // True when the auto-yield is a same-edge in-trail follow ("Following") vs a converging
    // give-way ("Yielding to"). Drives the right-click menu wording.
    bool AutoYieldIsFollowing = false,
    // Runway being crossed during CrossingRunwayPhase (e.g. "28R/10L"). Distinct from
    // AssignedRunway which holds the aircraft's departure / destination runway —
    // those differ when an aircraft taxis across one runway to reach a different
    // departure runway. Null otherwise.
    string? CrossingRunwayId = null,
    // True while the "approaching final without a landing clearance" warning has fired on
    // the current final approach and the aircraft still lacks a landing clearance. Drives
    // the flashing red "NoLndgClnc" datablock line on the radar.
    bool NoLandingClearanceWarningActive = false,
    // True when a queued ONHS DEL block is armed (or per-aircraft auto-delete has been
    // requested). Drives the small auto-delete marker in the radar / tower-cab datablock.
    bool AutoDeletePending = false,
    // One-line human-readable status for the Aircraft List Info column, computed server-side
    // by Yaat.Sim.AircraftStatusDescriber. The client displays it verbatim; SmartStatusSeverity
    // drives the column colour.
    string SmartStatus = "",
    Yaat.Sim.AircraftStatusSeverity SmartStatusSeverity = Yaat.Sim.AircraftStatusSeverity.Normal,
    // Airport id of the ground layout this aircraft is on (server AircraftGroundOps.LayoutAirportId,
    // scenario primary airport as fallback). Null when airborne / unknown. Lets the radar surface a
    // ground aircraft's speech bubble when no ground view is currently showing that airport.
    string? GroundAirportId = null,
    // True when this ground departure is held for release. Drives the radar "Release (HFR)"
    // context-menu item and a held badge.
    bool HeldForRelease = false,
    // Student-scope STARS view projected by the server (StarsDatablockClassifier) relative to the
    // scenario's student position. Null when there is no student position. Drives the instructor
    // radar's optional datablock color sync, LDB/PDB marker + collapse, and leader-direction sync.
    Yaat.Sim.StarsDatablockColor? StudentDatablockColor = null,
    Yaat.Sim.StarsDatablockLevel? StudentDatablockLevel = null,
    int? StudentLeaderDirection = null,
    // Instructor freetext note shown as an extra datablock line on radar + ground. Instructor-only.
    string Note = "",
    // Instructor TPA overlay on YAAT's own radar (emulates STARS *J/*P). TpaType: 0=None, 1=J-Ring,
    // 2=Cone. TpaSize is the J-Ring radius / Cone length in nm. Instructor-only; never on the student's CRC.
    int TpaType = 0,
    double TpaSize = 0,
    // Sector code of the recipient of a pending point-out the student's own position initiated
    // (e.g. "3E"). Non-null only while that outgoing point-out is still pending; drives the "{code}*"
    // indicator on the owner/scratchpad datablock line. Instructor-only.
    string? PointoutToTcpCode = null,
    // True while the displayed altitude rounds to 000 (below the acquisition floor, AGL < 100 ft,
    // field-elevation adjusted server-side). The radar withholds the target while set (matching CRC
    // STARS coast/skip); the ground view keeps a ghost overlay visible across liftoff until it clears.
    bool BelowDisplayFloor = false,
    // True while the aircraft is established on an approach procedure being descended below the MVA
    // (on final/glideslope, or cleared for a full approach and flying it). Computed server-side via
    // PhaseList.IsEstablishedOnApproach(); the radar uses it to inhibit the MVA altitude tint.
    bool IsEstablishedOnApproach = false,
    // Absolute-UTC bounds of the aircraft's Call-For-Release window (CFR), or null. The client
    // evaluates these against real UTC to raise instructor-facing expiry alerts; they never affect the
    // simulation (GitHub issue #230). Kept name-for-name in sync with the server's AircraftStateDto.
    DateTime? CfrWindowStartUtc = null,
    DateTime? CfrWindowEndUtc = null,
    // ASDE-style surface fix shown on the Ground View datablock and the Aircraft List "Fix" column:
    // exit fix or destination per the airport's ASDE-X/SAID facility config, resolved server-side.
    // Kept name-for-name in sync with the server's AircraftStateDto.
    string AsdexFix = "",
    // Filed-altitude notation fields mirroring PlannedAltitude, populated server-side from
    // AircraftState.FlightPlan.Altitude. CruiseAltitude above is the ceiling/single value;
    // BlockFloorAltitude is the block floor (null = not a block). Kept name-for-name in sync with the
    // server's AircraftStateDto so the client datablock/FPE can render blocks and VFR-on-top.
    int? BlockFloorAltitude = null,
    bool IsVfrOnTop = false,
    bool IsAbove = false,
    // Controller-assigned beacon code (server Transponder.AssignedCode) — the flight-plan squawk the
    // strip and the Flight Plan Editor's BCN box display. Distinct from BeaconCode above, which is the
    // reported/squawked code (Transponder.Code) and stays 0000 until the pilot squawks the assignment.
    // Kept name-for-name in sync with the server's AircraftStateDto.
    uint AssignedBeaconCode = 0,
    // Active-procedure geometry (hold racetracks, procedure turns, open-ended SID coded legs)
    // projected for the "Show nav route" overlay. Kept name-for-name in sync with AircraftStateDto.
    List<Yaat.Sim.NavRouteShapeDto>? NavRouteShapes = null,
    // Latched true when the pilot has been told to squawk VFR (SQVFR/SQV); the radar view then suppresses
    // the assigned-vs-reported beacon-code mismatch flash until a new beacon code is assigned. RPO-display
    // only. Kept name-for-name in sync with the server's AircraftStateDto.
    bool CommandedSquawkVfr = false
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
    string? AutoDeleteOverride = null,
    string? EffectiveAutoDeleteMode = null,
    int AutoAcceptDelaySeconds = -1,
    bool AutoClearedToLand = false,
    bool AutoCrossRunway = false,
    bool AutoPullUpToParallel = false,
    bool ValidateDctFixes = true,
    bool SoloTrainingMode = false,
    int SoloParkingInitialCallupRatePercent = 100,
    int SoloArrivalGeneratorRatePercent = 100,
    int SoloGoAroundProbabilityPercent = 0,
    bool HasSoloParkingInitialCallupSource = false,
    bool HasSoloArrivalGeneratorSource = false,
    bool RpoShowPilotSpeech = false,
    bool IsStudentTowerPosition = true,
    string? StudentPositionType = null,
    FlightStripsConfigDto? FlightStripsConfig = null,
    List<Yaat.Sim.Scenarios.ScenarioGeneratorConfig>? AircraftGenerators = null,
    List<Yaat.Sim.Scenarios.VfrArrivalGeneratorConfig>? VfrArrivalGenerators = null,
    List<Yaat.Sim.Scenarios.OverflightGeneratorConfig>? OverflightGenerators = null,
    List<ScenarioPositionDto>? Positions = null,
    int CommandRunDelayMinSeconds = 0,
    int CommandRunDelayMaxSeconds = 0
);

/// <summary>
/// Server-side scenario summary returned by GetScenarios. Mirrors the shape of the vNAS
/// data-api response and is the unit the picker UI binds to. MinimumRating is the rating
/// gate value used by the server-side filter.
/// </summary>
public sealed record ScenarioSummaryDto(string Id, string Name, string ArtccId, string? PrimaryAirportId, string? MinimumRating);

/// <summary>
/// Wire shape for ServerConnection.GetScenariosAsync. Mirrors the server's
/// ScenarioCatalogResponseDto. HiddenByGateCount > 0 triggers the picker's
/// "N scenarios hidden — requires training access key" affordance.
/// </summary>
public sealed record ScenarioCatalogResponseDto(ScenarioSummaryDto[] Visible, int HiddenByGateCount);

/// <summary>
/// Wire shape for ServerConnection.GetScenarioJsonByIdAsync. Mirrors the server's
/// ScenarioJsonResultDto. Exactly one of Json / AccessDeniedReason is non-null: a non-null
/// AccessDeniedReason carries a human-readable gate-denial (or not-in-a-room / not-found) message
/// the client surfaces directly.
/// </summary>
public sealed record ScenarioJsonResultDto(string? Json, string? AccessDeniedReason);

public record PositionDisplayConfigDto(List<int?> MapGroupMapIds, List<string> MapGroupTcpCodes, List<string> UnderlyingAirports, string TcpCode);

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

public record RestoredRoomInfoDto(string RoomId, string? ScenarioName, double ElapsedSeconds, int AircraftCount);

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
    double TapeEnd = 0,
    string? AutoDeleteOverride = null,
    string? EffectiveAutoDeleteMode = null,
    int AutoAcceptDelaySeconds = -1,
    bool AutoClearedToLand = false,
    bool AutoCrossRunway = false,
    bool AutoPullUpToParallel = false,
    bool ValidateDctFixes = true,
    bool SoloTrainingMode = false,
    int SoloParkingInitialCallupRatePercent = 100,
    int SoloArrivalGeneratorRatePercent = 100,
    int SoloGoAroundProbabilityPercent = 0,
    bool HasSoloParkingInitialCallupSource = false,
    bool HasSoloArrivalGeneratorSource = false,
    bool RpoShowPilotSpeech = false,
    FlightStripsConfigDto? FlightStripsConfig = null,
    RundownDto? Rundown = null,
    List<TimerDto>? Timers = null,
    List<TimelineBookmarkDto>? Bookmarks = null,
    int CommandRunDelayMinSeconds = 0,
    int CommandRunDelayMaxSeconds = 0,
    // Student position type (APP/CTR/GND/TWR) for seeding user-local position-based defaults on join.
    string? StudentPositionType = null
);

public record ScenarioLoadedDto(
    string ScenarioId,
    string ScenarioName,
    string? PrimaryAirportId,
    bool IsPaused,
    int SimRate,
    List<AircraftDto> AllAircraft,
    PositionDisplayConfigDto? PositionDisplayConfig = null,
    bool IsStudentTowerPosition = true,
    string? StudentPositionType = null,
    string? AutoDeleteOverride = null,
    string? EffectiveAutoDeleteMode = null,
    int AutoAcceptDelaySeconds = -1,
    bool AutoClearedToLand = false,
    bool AutoCrossRunway = false,
    bool AutoPullUpToParallel = false,
    bool ValidateDctFixes = true,
    bool SoloTrainingMode = false,
    int SoloParkingInitialCallupRatePercent = 100,
    int SoloArrivalGeneratorRatePercent = 100,
    int SoloGoAroundProbabilityPercent = 0,
    bool HasSoloParkingInitialCallupSource = false,
    bool HasSoloArrivalGeneratorSource = false,
    bool RpoShowPilotSpeech = false,
    FlightStripsConfigDto? FlightStripsConfig = null,
    List<Yaat.Sim.Scenarios.ScenarioGeneratorConfig>? AircraftGenerators = null,
    List<Yaat.Sim.Scenarios.VfrArrivalGeneratorConfig>? VfrArrivalGenerators = null,
    List<Yaat.Sim.Scenarios.OverflightGeneratorConfig>? OverflightGenerators = null,
    List<ScenarioPositionDto>? Positions = null,
    int CommandRunDelayMinSeconds = 0,
    int CommandRunDelayMaxSeconds = 0
);

public record ScenarioPositionDto(string Id, string Callsign, string Name);

public record ArrivalGeneratorsChangedDto(
    List<Yaat.Sim.Scenarios.ScenarioGeneratorConfig> Generators,
    List<Yaat.Sim.Scenarios.VfrArrivalGeneratorConfig> VfrArrivalGenerators,
    List<Yaat.Sim.Scenarios.OverflightGeneratorConfig> OverflightGenerators
);

/// <summary>One held departure in the hold-for-release rundown.</summary>
public record HeldDepartureDto(string Callsign, string Airport, string AircraftType, string Destination, bool IsGroundDeparture, string Status);

/// <summary>The hold-for-release rundown: airports currently armed and the departures held at them.</summary>
public record RundownDto(List<string> ArmedAirports, List<HeldDepartureDto> Held);

/// <summary>Pushed whenever the hold-for-release state changes (arm/disarm/release).</summary>
public record HeldDeparturesChangedDto(RundownDto Rundown);

/// <summary>One active TIMER countdown for the timers panel. Callsign is null for a global
/// (instructor) timer, or the aircraft the expiry SAY is attributed to.</summary>
public record TimerDto(int Id, string? Callsign, string? Message, int RemainingSeconds, int TotalSeconds);

/// <summary>Pushed whenever the active TIMER set changes (set/cancel/expire) or a second ticks down.</summary>
public record TimersChangedDto(List<TimerDto> Timers);

/// <summary>One shared timeline bookmark. <see cref="CreatorInitials"/> identifies the RPO who placed
/// it (null when unknown, e.g. a legacy recording).</summary>
public record TimelineBookmarkDto(string Id, double TimeSeconds, string? Name, string? CreatorInitials);

/// <summary>Pushed whenever the room's shared bookmark set changes (add/rename/delete or recording load).</summary>
public record BookmarksChangedDto(List<TimelineBookmarkDto> Bookmarks);

public record SessionSettingsDto(
    string? AutoDeleteOverride,
    string? EffectiveAutoDeleteMode,
    int AutoAcceptDelaySeconds,
    bool AutoClearedToLand,
    bool AutoCrossRunway,
    bool AutoPullUpToParallel,
    bool ValidateDctFixes,
    bool SoloTrainingMode,
    int SoloParkingInitialCallupRatePercent,
    int SoloArrivalGeneratorRatePercent,
    int SoloGoAroundProbabilityPercent,
    bool HasSoloParkingInitialCallupSource,
    bool HasSoloArrivalGeneratorSource,
    bool RpoShowPilotSpeech,
    int CommandRunDelayMinSeconds = 0,
    int CommandRunDelayMaxSeconds = 0
);

public record RoomMemberDto(string Cid, string Initials, string ArtccId);

public record RoomMemberChangedDto(string RoomId, List<RoomMemberDto> Members, string? ScenarioName);

public record UnloadScenarioResultDto(bool RequiresConfirmation, int OtherClientCount, string? Message);

public record TerminalBroadcastDto(string Initials, string Kind, string Callsign, string Message, DateTime Timestamp, double? ElapsedSeconds);

public record PilotTransmissionBroadcastDto(
    string ScenarioId,
    string Callsign,
    string Text,
    string SourceKind,
    int SpeakerId,
    double ElapsedSeconds,
    DateTime Timestamp
);

public record CrcLobbyClientDto(string ClientId, string? Cid, string? DisplayName, string? ArtccId, string? PositionId, bool IsActive);

public record CrcLobbyChangedDto(List<CrcLobbyClientDto> Clients);

public record RpoLobbyClientDto(string ConnectionId, string Cid, string Name, string Rating);

public record RpoLobbyChangedDto(List<RpoLobbyClientDto> Clients);

public record CrcRoomMemberDto(string ClientId, string? Cid, string? DisplayName, string? PositionId, bool IsActive);

public record CrcRoomMembersChangedDto(string RoomId, List<CrcRoomMemberDto> Members);

public record OnlineControllerDto(
    string Callsign,
    string Name,
    string Frequency,
    string? FacilityId,
    string? FacilityName,
    string PositionType,
    string? Tcp,
    string? RadioName,
    string RealName,
    bool IsActive,
    bool IsScenarioPosition
)
{
    public string StatusText => IsActive ? "Active" : "Standby";

    public string SourceText => IsScenarioPosition ? "Scenario" : "CRC";

    /// <summary>Left-column handoff/sector id for the CRC-style list; falls back to the facility id.</summary>
    public string HandoffId => !string.IsNullOrEmpty(Tcp) ? Tcp : (FacilityId ?? "");

    /// <summary>CRC identifies controllers by position name, not callsign.</summary>
    public string DisplayName => !string.IsNullOrEmpty(Name) ? Name : Callsign;

    /// <summary>Hover tooltip: callsign + controller name.</summary>
    public string Tooltip => $"{Callsign} — {RealName}" + (IsScenarioPosition ? " (scenario)" : "");
}

public record GroundLayoutDto(
    string AirportId,
    List<GroundNodeDto> Nodes,
    List<GroundEdgeDto> Edges,
    List<GroundArcDto>? Arcs,
    List<GroundRunwayDto>? Runways
);

public record GroundNodeDto(int Id, double Latitude, double Longitude, string Type, string? Name, double? Heading, string? RunwayId);

public record GroundEdgeDto(int FromNodeId, int ToNodeId, string TaxiwayName, double DistanceNm, List<double[]>? IntermediatePoints)
{
    public bool IsRunway => TaxiwayName.StartsWith("RWY", StringComparison.OrdinalIgnoreCase);
    public bool IsRamp => string.Equals(TaxiwayName, "RAMP", StringComparison.OrdinalIgnoreCase);
}

public record GroundArcDto(
    int FromNodeId,
    int ToNodeId,
    string[] TaxiwayNames,
    double P1Lat,
    double P1Lon,
    double P2Lat,
    double P2Lon,
    double MinRadiusOfCurvatureFt,
    double DistanceNm,
    double TurnAngleDeg,
    bool HiddenInGroundView = false
);

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
    Yaat.Sim.PlannedAltitude? Altitude = null,
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

public record SoloTrainingScoreBucketDto(string Name, int PointsAvailable, int PointsLost);

public record SoloTrainingEventDto(
    string Id,
    string Category,
    string Severity,
    string Title,
    string Description,
    string RuleReference,
    double StartedAtSeconds,
    double LastObservedAtSeconds,
    double ExposureSeconds,
    bool IsActive,
    List<string> Callsigns,
    string? RunwayId,
    double? RequiredHorizontalNm,
    double? ActualHorizontalNm,
    double? RequiredVerticalFt,
    double? ActualVerticalFt,
    string? RequiredText,
    string? ActualText
);

public record SessionReportDto(
    bool SoloTrainingMode,
    double ScenarioElapsedSeconds,
    int Score,
    string Grade,
    List<SoloTrainingScoreBucketDto> ScoreBuckets,
    List<SoloTrainingEventDto> ActiveEvents,
    List<SoloTrainingEventDto> Timeline,
    List<string> CoachingNotes,
    ApproachReportDto ApproachReport,
    List<AircraftDebriefDto> AircraftDebriefs
);

public record AircraftDebriefDto(
    string Callsign,
    string AircraftType,
    string? FiledDeparture,
    string? FiledDestination,
    string Operation,
    double SpawnedAtSeconds,
    double? CompletedAtSeconds,
    string CompletionReason,
    string? CompletionDetail,
    int SeparationFindingCount,
    int RunwayWakeFindingCount,
    int AdvisoryFindingCount,
    int ApproachFindingCount,
    int CoachFindingCount,
    int WarningFindingCount,
    int SafetyFindingCount,
    string CoachingNote,
    List<string> FindingIds
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
