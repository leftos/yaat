using System.Reflection;
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

        foreach (var method in typeof(ServerConnection).GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (method.IsSpecialName)
            {
                continue;
            }

            var returnType = method.ReturnType;
            if (!returnType.IsGenericType || returnType.GetGenericTypeDefinition() != typeof(Task<>))
            {
                continue;
            }

            var payload = returnType.GetGenericArguments()[0];
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
        var info = YaatHubJsonContext.Default.GetTypeInfo(payloadType);

        Assert.True(
            info is not null,
            $"ServerConnection.{methodName} returns Task<{FriendlyName(payloadType)}> via InvokeAsync, but {FriendlyName(payloadType)} "
                + "has no [JsonSerializable] registration in YaatHubJsonContext. It will throw JsonSerializerIsReflectionDisabled in "
                + $"the WASM client. Add [JsonSerializable(typeof({FriendlyName(payloadType)}))] to YaatHubJsonContext."
        );
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

        var baseName = type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)];
        var args = string.Join(", ", type.GetGenericArguments().Select(FriendlyName));
        return $"{baseName}<{args}>";
    }
}
