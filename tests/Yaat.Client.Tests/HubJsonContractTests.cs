using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

// Guards the SignalR JSON wire contract for the Core source-gen context.
//
// ServerConnection wraps every /hubs/training InvokeAsync<T> in a public
// Task<T> method, so its return types ARE the set the JsonHubProtocol must
// deserialize. Under the WASM publish System.Text.Json reflection metadata is
// off, so a return type without a [JsonSerializable] registration throws
// JsonSerializerIsReflectionDisabled at first use — invisible on the desktop
// client, which falls through to reflection. This fails the build instead.
//
// Scope: InvokeAsync<T> return types owned by the Yaat.Client.Core assembly,
// checked against YaatHubJsonContext. Strips/Tdls-defined return types
// (CommandResultDto, AccessibleFacilityDto, FlightStripsConfigDto,
// TdlsConfigDto) are registered in their own contexts and excluded here.
// Broadcast (.On<T>) and argument payloads share the same contexts but aren't
// reflectable from the method surface, so they aren't covered.
public class HubJsonContractTests
{
    private static readonly Assembly CoreAssembly = typeof(YaatHubJsonContext).Assembly;

    public static IEnumerable<object[]> CoreInvokeReturnTypes()
    {
        var seen = new HashSet<Type>();

        foreach (MethodInfo method in typeof(ServerConnection).GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (method.IsSpecialName)
            {
                continue;
            }

            Type returnType = method.ReturnType;
            if (!returnType.IsGenericType || returnType.GetGenericTypeDefinition() != typeof(Task<>))
            {
                continue;
            }

            Type payload = returnType.GetGenericArguments()[0];
            if (!IsOwnedByCore(payload) || !seen.Add(payload))
            {
                continue;
            }

            yield return [method.Name, payload];
        }
    }

    [Theory]
    [MemberData(nameof(CoreInvokeReturnTypes))]
    public void EveryCoreInvokeReturnType_HasSourceGenMetadata(string methodName, Type payloadType)
    {
        JsonTypeInfo? info = YaatHubJsonContext.Default.GetTypeInfo(payloadType);

        Assert.True(
            info is not null,
            $"ServerConnection.{methodName} returns Task<{FriendlyName(payloadType)}> via InvokeAsync, but {FriendlyName(payloadType)} "
                + "has no [JsonSerializable] registration in YaatHubJsonContext. It will throw JsonSerializerIsReflectionDisabled in "
                + $"the WASM client. Add [JsonSerializable(typeof({FriendlyName(payloadType)}))] to YaatHubJsonContext."
        );
    }

    // The ScenarioLoadProgress event payload is a broadcast (.On<T>), not a method return type, so the theory above does not
    // see it; these pin its registration and the server's wire shape.
    [Fact]
    public void ScenarioLoadProgressDto_ResolvesThroughYaatHubJsonContext() =>
        Assert.NotNull(YaatHubJsonContext.Default.GetTypeInfo(typeof(ScenarioLoadProgressDto)));

    [Fact]
    public void LoadStepDto_ResolvesThroughYaatHubJsonContext() => Assert.NotNull(YaatHubJsonContext.Default.GetTypeInfo(typeof(LoadStepDto)));

    // The TimersChanged / ConflictAlertsChanged broadcast envelopes are Core-owned .On<T> payloads (not method return types,
    // so the theory above does not see them) and are not reachable from any registered type — RoomStateDto carries the inner
    // List<TimerDto> / List<ConflictAlertDto>, not the envelopes. Without their own registration the WASM client throws
    // JsonSerializerIsReflectionDisabled on the first timer or conflict-alert push. Their sibling AtpaResultsChangedDto is
    // registered; these pin the same guarantee for them.
    [Fact]
    public void TimersChangedDto_ResolvesThroughYaatHubJsonContext() =>
        Assert.NotNull(YaatHubJsonContext.Default.GetTypeInfo(typeof(TimersChangedDto)));

    [Fact]
    public void ConflictAlertsChangedDto_ResolvesThroughYaatHubJsonContext() =>
        Assert.NotNull(YaatHubJsonContext.Default.GetTypeInfo(typeof(ConflictAlertsChangedDto)));

    // The active-runways broadcast, the room-join field and the load result's session-setup prompt (both sections, the
    // assigned-runway counts included) and lowest worked level, in the server's wire shape.
    [Fact]
    public void ActiveRunwaysShapes_DeserializeIntoClientRecords()
    {
        const string changedJson = """{"ByAirport":{"OAK":["D28L","A28R"],"SFO":["28R"]}}""";
        const string resultJson =
            """{"Success":true,"Name":"OAK","ScenarioId":"s-1","Warnings":[],"AllAircraft":[],"Steps":[],"ActiveRunways":{},"""
            + """ "SessionSetup":{"ActiveRunways":{"Prefill":{"OAK":["D30"]},"""
            + """ "Assigned":{"OAK":{"Departures":2,"Arrivals":1,"GeneratorArrivalRunways":["28R","30"]}}},"""
            + """ "WorkedPositions":{"Own":"GND","Prefill":"GND","Choices":["GND","DEL"]}},"""
            + """ "StudentLowestWorkedLevel":"GND"}""";
        const string roomJson =
            """{"RoomId":"room-1","CreatorInitials":"CX","CreatorArtccId":"ZOA","Members":[],"AllAircraft":[],"""
            + """ "AircraftGenerators":[],"VfrArrivalGenerators":[],"OverflightGenerators":[],"Positions":[],"LoadingBy":null,"""
            + """ "ActiveRunways":{"OAK":["30"]}}""";

        ActiveRunwaysChangedDto? changed = JsonSerializer.Deserialize(changedJson, YaatHubJsonContext.Default.ActiveRunwaysChangedDto);
        LoadScenarioResultDto? result = JsonSerializer.Deserialize(resultJson, YaatHubJsonContext.Default.LoadScenarioResultDto);
        RoomStateDto? room = JsonSerializer.Deserialize(roomJson, YaatHubJsonContext.Default.RoomStateDto);

        Assert.NotNull(changed);
        Assert.Equal(["D28L", "A28R"], changed.ByAirport["OAK"]);
        Assert.Equal(["28R"], changed.ByAirport["SFO"]);
        Assert.NotNull(result);
        Assert.Empty(result.ActiveRunways);
        ActiveRunwaysSetupDto? runways = result.SessionSetup?.ActiveRunways;
        Assert.NotNull(runways);
        Assert.Equal(["D30"], runways.Prefill["OAK"]);
        RunwayUseCountsDto oak = runways.Assigned["OAK"];
        Assert.Equal((2, 1), (oak.Departures, oak.Arrivals));
        Assert.Equal(["28R", "30"], oak.GeneratorArrivalRunways);
        WorkedPositionsSetupDto? positions = result.SessionSetup?.WorkedPositions;
        Assert.NotNull(positions);
        Assert.Equal(("GND", "GND"), (positions.Own, positions.Prefill));
        Assert.Equal(["GND", "DEL"], positions.Choices);
        Assert.Equal("GND", result.StudentLowestWorkedLevel);
        Assert.NotNull(room);
        Assert.Equal(["30"], room.ActiveRunways["OAK"]);
    }

    // The lowest worked level rides every session-settings carrier: the settings broadcast, the room join and the
    // scenario-loaded broadcast, and back out through the source-gen context unchanged.
    [Fact]
    public void SessionSettings_StudentLowestWorkedLevel_RoundTrips()
    {
        const string settingsJson =
            """{"AutoDeleteOverride":null,"EffectiveAutoDeleteMode":null,"DepartureAutoDeleteDistanceNm":null,"AutoAcceptDelaySeconds":-1,"""
            + """ "AutoClearedToLand":false,"AutoCrossRunwayMode":0,"AutoPullUpToParallel":false,"AutoGoAroundOnOccupiedRunway":false,"""
            + """ "AutoRejectTakeoffOnOccupiedRunway":false,"AutoArrivalSpacingOnOccupiedRunway":false,"ValidateDctFixes":true,"""
            + """ "SoloTrainingMode":true,"SoloParkingInitialCallupRatePercent":100,"SoloArrivalGeneratorRatePercent":100,"""
            + """ "SoloGoAroundProbabilityPercent":0,"HasSoloParkingInitialCallupSource":false,"HasSoloArrivalGeneratorSource":false,"""
            + """ "RpoShowPilotSpeech":false,"StudentLowestWorkedLevel":"DEL"}""";
        const string roomJson =
            """{"RoomId":"room-1","CreatorInitials":"CX","CreatorArtccId":"ZOA","Members":[],"AllAircraft":[],"""
            + """ "AircraftGenerators":[],"VfrArrivalGenerators":[],"OverflightGenerators":[],"Positions":[],"LoadingBy":null,"""
            + """ "ActiveRunways":{},"StudentLowestWorkedLevel":"TWR"}""";
        const string loadedJson =
            """{"ScenarioId":"s-1","ScenarioName":"OAK","PrimaryAirportId":"OAK","IsPaused":true,"SimRate":1,"AllAircraft":[],"""
            + """ "ActiveRunways":{},"StudentLowestWorkedLevel":"GND"}""";

        SessionSettingsDto? settings = JsonSerializer.Deserialize(settingsJson, YaatHubJsonContext.Default.SessionSettingsDto);
        RoomStateDto? room = JsonSerializer.Deserialize(roomJson, YaatHubJsonContext.Default.RoomStateDto);
        ScenarioLoadedDto? loaded = JsonSerializer.Deserialize(loadedJson, YaatHubJsonContext.Default.ScenarioLoadedDto);

        Assert.NotNull(settings);
        Assert.Equal("DEL", settings.StudentLowestWorkedLevel);
        SessionSettingsDto? again = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(settings, YaatHubJsonContext.Default.SessionSettingsDto),
            YaatHubJsonContext.Default.SessionSettingsDto
        );
        Assert.Equal(settings, again);
        Assert.Equal("TWR", room?.StudentLowestWorkedLevel);
        Assert.Equal("GND", loaded?.StudentLowestWorkedLevel);
    }

    // The strips state broadcast in the server's camelCase wire shape (yaat-server's
    // FlightStripsStateDtoJsonTests.JsonHubPayload_CarriesTheAutoSortedRacks writes this payload): the YAAT-only
    // autoSortedRacks map must reach the client record.
    [Fact]
    public void FlightStripsStateShape_DeserializesTheAutoSortedRacks()
    {
        const string stateJson =
            """{"printerItems":[],"bayItems":[{"bayId":"bay1","itemIds":[[],[],[]]}],"newItemInPrinter":false,"""
            + """ "newItemInArrivalPrinter":false,"newItemInBayId":null,"itemMovedOrCreatedBySessionId":null,"""
            + """ "autoSortedRacks":{"bay1":[0,2]}}""";

        FlightStripsStateDto? state = JsonSerializer.Deserialize(stateJson, YaatStripsHubJsonContext.Default.FlightStripsStateDto);

        Assert.NotNull(state);
        Assert.Equal(3, Assert.Single(state.BayItems).ItemIds.Length);
        Assert.Equal([0, 2], state.AutoSortedRacks["bay1"]);
    }

    // The scenario-loaded broadcast, the rewind and recording-load result and the restart broadcast each carry the room's
    // active runways, in the server's wire shape.
    [Fact]
    public void ActiveRunwaysCarriers_DeserializeIntoClientRecords()
    {
        const string loadedJson =
            """{"ScenarioId":"s-1","ScenarioName":"OAK","PrimaryAirportId":"OAK","IsPaused":true,"SimRate":1,"AllAircraft":[],"""
            + """ "ActiveRunways":{"OAK":["D28L","A28R"]}}""";
        const string rewindJson = """{"Success":true,"Error":null,"ActiveRunways":{"OAK":["30"]},"ToweredAirports":["OAK"],"Aircraft":[]}""";
        const string restartedJson = """{"Aircraft":[],"ActiveRunways":{"SFO":["28R"],"OAK":[]}}""";

        ScenarioLoadedDto? loaded = JsonSerializer.Deserialize(loadedJson, YaatHubJsonContext.Default.ScenarioLoadedDto);
        RewindResultDto? rewind = JsonSerializer.Deserialize(rewindJson, YaatHubJsonContext.Default.RewindResultDto);
        ScenarioRestartedDto? restarted = JsonSerializer.Deserialize(restartedJson, YaatHubJsonContext.Default.ScenarioRestartedDto);

        Assert.NotNull(loaded);
        Assert.Equal(["D28L", "A28R"], loaded.ActiveRunways["OAK"]);
        Assert.NotNull(rewind);
        Assert.Equal(["OAK"], rewind.ToweredAirports);
        Assert.Equal(["30"], rewind.ActiveRunways["OAK"]);
        Assert.NotNull(restarted);
        Assert.Empty(restarted.Aircraft);
        Assert.Equal(["28R"], restarted.ActiveRunways["SFO"]);
        Assert.Empty(restarted.ActiveRunways["OAK"]);
    }

    [Fact]
    public void ServerLoadShapes_DeserializeIntoClientRecords()
    {
        const string step =
            """{"Id":"layouts","Label":"Airport layouts","State":"warning","Detail":"1 of 2 airports","Problems":["""
            + """ "SQL: no ground map on vNAS. Aircraft at SQL cannot taxi or park."]}""";
        string progressJson = $$"""{"LoadId":"0a1b","Sequence":3,"ScenarioName":"OAK Ground 7","IsComplete":true,"Steps":[{{step}}]}""";
        string resultJson =
            $$"""{"Success":false,"Name":"OAK Ground 7","ScenarioId":"s-7","Warnings":["The load failed: boom."],"AllAircraft":[],"""
            + $$""" "Steps":[{{step}}]}""";
        const string roomJson =
            """{"RoomId":"room-1","CreatorInitials":"CX","CreatorArtccId":"ZOA","Members":[],"AllAircraft":[],"""
            + """ "AircraftGenerators":[],"VfrArrivalGenerators":[],"OverflightGenerators":[],"""
            + """ "Positions":[],"LoadingBy":"AB"}""";

        ScenarioLoadProgressDto? progress = JsonSerializer.Deserialize(progressJson, YaatHubJsonContext.Default.ScenarioLoadProgressDto);
        LoadScenarioResultDto? result = JsonSerializer.Deserialize(resultJson, YaatHubJsonContext.Default.LoadScenarioResultDto);
        RoomStateDto? room = JsonSerializer.Deserialize(roomJson, YaatHubJsonContext.Default.RoomStateDto);

        Assert.NotNull(progress);
        Assert.Equal(("0a1b", 3, "OAK Ground 7", true), (progress.LoadId, progress.Sequence, progress.ScenarioName, progress.IsComplete));
        LoadStepDto progressStep = Assert.Single(progress.Steps);
        Assert.Equal(
            ("layouts", "Airport layouts", "warning", "1 of 2 airports"),
            (progressStep.Id, progressStep.Label, progressStep.State, progressStep.Detail)
        );
        Assert.Equal("SQL: no ground map on vNAS. Aircraft at SQL cannot taxi or park.", Assert.Single(progressStep.Problems));
        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Equal("warning", Assert.Single(result.Steps).State);
        Assert.NotNull(room);
        Assert.Equal("AB", room.LoadingBy);
    }

    // A return type belongs to the Core context when the payload's underlying
    // DTO is defined in Yaat.Client.Core. The element of List<T> / T[] is the
    // discriminator: List<AccessibleFacilityDto> resolves to a Strips type and
    // is skipped, while List<TrainingRoomInfoDto> resolves to a Core type.
    // Framework primitives (string, bool, byte[]) fall out the same way.
    private static bool IsOwnedByCore(Type type) => UnderlyingDtoType(type).Assembly == CoreAssembly;

    private static Type UnderlyingDtoType(Type type)
    {
        if (type.IsArray)
        {
            return type.GetElementType()!;
        }

        if (type.IsGenericType)
        {
            return type.GetGenericArguments()[^1];
        }

        return type;
    }

    private static string FriendlyName(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.Name;
        }

        string baseName = type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)];
        string args = string.Join(", ", type.GetGenericArguments().Select(FriendlyName));
        return $"{baseName}<{args}>";
    }
}
