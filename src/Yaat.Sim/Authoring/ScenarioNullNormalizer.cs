using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Authoring;

/// <summary>
/// Repairs the empty numbers an older YAATEdit file holds as JSON null, which the scenario models cannot read: a preset's
/// or initialization trigger's <c>timeOffset</c> becomes 0, and a null optional generator number is dropped, so it reads
/// as the model's default. Every other key, and every value that is not null, is left as it is.
/// </summary>
public static class ScenarioNullNormalizer
{
    // The options scenario text is written with wherever the store or the repair rewrites it: indented, and non-ASCII text
    // kept as the source held it.
    internal static readonly JsonSerializerOptions TextOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly ILogger Log = SimLog.CreateLogger("ScenarioNullNormalizer");

    // A text JsonObject refuses to hold (one that repeats a key) is not repaired: it is left as the caller gave it, so the
    // reader or importer that follows judges it as it always did.
    private static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowDuplicateProperties = false };

    private static readonly string[] GeneratorArrays = ["aircraftGenerators", "vfrArrivalGenerators", "overflightGenerators"];

    private static readonly string[] OptionalGeneratorNumbers = ["startTimeOffset", "maxDistance", "intervalDistance", "maxTime", "intervalTime"];

    /// <summary>
    /// Repairs a scenario JSON text: every empty number an older YAATEdit file holds as null is set to 0 or dropped.
    /// </summary>
    /// <param name="json">The scenario text.</param>
    /// <param name="fixedCount">How many values it repaired; 0 when it repaired none or could not read the text.</param>
    /// <returns>
    /// The repaired text, or <paramref name="json"/> itself when nothing was repaired or the text is malformed, is not an
    /// object, or holds a repeated key.
    /// </returns>
    public static string Repair(string json, out int fixedCount)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonObject? scenario = TryParse(json);
        if (scenario is null)
        {
            fixedCount = 0;
            return json;
        }

        fixedCount = Fix(scenario);
        return (fixedCount > 0) ? scenario.ToJsonString(TextOptions) : json;
    }

    /// <summary>Fixes every empty number in <paramref name="scenario"/>, in place.</summary>
    /// <param name="scenario">The scenario JSON to repair.</param>
    /// <returns>
    /// How many values it changed: each null <c>timeOffset</c> set to 0, and each null optional generator number removed.
    /// </returns>
    public static int Fix(JsonObject scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        int count = FixTimeOffsets(scenario) + FixGeneratorNumbers(scenario);
        if ((count > 0) && Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("Set or dropped {Count} empty numbers an older YAATEdit file left null", count);
        }

        return count;
    }

    private static int FixTimeOffsets(JsonObject scenario)
    {
        int count = FixArrayTimeOffsets(scenario, "initializationTriggers");
        if (scenario["aircraft"] is JsonArray aircraft)
        {
            foreach (JsonObject entry in ObjectsIn(aircraft))
            {
                count += FixArrayTimeOffsets(entry, "presetCommands");
            }
        }

        return count;
    }

    private static int FixArrayTimeOffsets(JsonObject owner, string arrayKey)
    {
        if (owner[arrayKey] is not JsonArray entries)
        {
            return 0;
        }

        int count = 0;
        foreach (JsonObject entry in ObjectsIn(entries))
        {
            count += SetNullToZero(entry, "timeOffset");
        }

        return count;
    }

    private static int FixGeneratorNumbers(JsonObject scenario)
    {
        int count = 0;
        foreach (string arrayKey in GeneratorArrays)
        {
            if (scenario[arrayKey] is not JsonArray generators)
            {
                continue;
            }

            foreach (JsonObject generator in ObjectsIn(generators))
            {
                count += RemoveNullNumbers(generator);
            }
        }

        return count;
    }

    private static int RemoveNullNumbers(JsonObject generator)
    {
        int count = 0;
        foreach (string key in OptionalGeneratorNumbers)
        {
            if (KeyHoldingNull(generator, key) is { } found)
            {
                generator.Remove(found);
                count++;
            }
        }

        return count;
    }

    private static int SetNullToZero(JsonObject owner, string key)
    {
        if (KeyHoldingNull(owner, key) is not { } found)
        {
            return 0;
        }

        owner[found] = 0;
        return 1;
    }

    // The key of the member named key in any casing whose value is JSON null, or null when there is none.
    private static string? KeyHoldingNull(JsonObject owner, string key) =>
        owner.FirstOrDefault(member => (member.Value is null) && string.Equals(member.Key, key, StringComparison.OrdinalIgnoreCase)).Key;

    // The scenario object the text holds, or null when it is malformed or holds a repeated key.
    private static JsonObject? TryParse(string json)
    {
        try
        {
            return JsonNode.Parse(json, nodeOptions: null, ReadOptions) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<JsonObject> ObjectsIn(JsonArray array) => array.OfType<JsonObject>();
}
