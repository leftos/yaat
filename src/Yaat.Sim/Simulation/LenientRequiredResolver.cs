using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Yaat.Sim.Simulation;

/// <summary>
/// Strips <see cref="JsonPropertyInfo.IsRequired"/> from all properties so that
/// <c>System.Text.Json</c> does not throw when deserializing old recordings that
/// lack fields added after the recording was created. The C# <c>required</c>
/// keyword still enforces compile-time safety in <c>ToSnapshot()</c> callers;
/// only the runtime JSON check is relaxed.
/// </summary>
public sealed class LenientRequiredResolver : DefaultJsonTypeInfoResolver
{
    public override JsonTypeInfo GetTypeInfo(Type type, JsonSerializerOptions options)
    {
        var info = base.GetTypeInfo(type, options);
        foreach (var property in info.Properties)
        {
            property.IsRequired = false;
        }

        return info;
    }
}
