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

    // The active-runways broadcast, the room-join field and the load result's prompt fields, in the server's wire shape.
    [Fact]
    public void ActiveRunwaysShapes_DeserializeIntoClientRecords()
    {
        const string changedJson = """{"ByAirport":{"OAK":["D28L","A28R"],"SFO":["28R"]}}""";
        const string resultJson =
            """{"Success":true,"Name":"OAK","ScenarioId":"s-1","Warnings":[],"AllAircraft":[],"Steps":[],"""
            + """ "ActiveRunways":{},"ActiveRunwaysPrefill":{"OAK":["D30"]},"ActiveRunwaysPromptNeeded":true}""";
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
        Assert.Equal(["D30"], result.ActiveRunwaysPrefill["OAK"]);
        Assert.True(result.ActiveRunwaysPromptNeeded);
        Assert.NotNull(room);
        Assert.Equal(["30"], room.ActiveRunways["OAK"]);
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
