using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Authoring.Stamping;

/// <summary>
/// The field-by-field JSON diff of two scenario aircraft with each field's old and new value for the stamp preview, which
/// <see cref="ScenarioDiff"/> does not give (it reports paths and kinds only).
/// </summary>
/// <remarks>
/// A list whose items all carry an id matches items by id, an item with an empty id matching only an empty-id item at its own
/// position; other lists are compared by index. A stamp never reorders a list, so no reorder change is produced.
/// </remarks>
public static class StampChanges
{
    private static readonly ILogger Log = SimLog.CreateLogger("StampChanges");

    /// <summary>Lists every field that differs from <paramref name="before"/> to <paramref name="after"/>.</summary>
    /// <param name="before">The aircraft before the stamp.</param>
    /// <param name="after">The aircraft after it.</param>
    /// <returns>The changes in document order, the old side's order first.</returns>
    public static IReadOnlyList<StampChange> Between(ScenarioAircraft before, ScenarioAircraft after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var changes = new List<StampChange>();
        Node("", ToNode(before), ToNode(after), changes);
        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("Stamp changed {Count} fields of {Callsign}", changes.Count, after.AircraftId);
        }

        return changes;
    }

    internal static JsonNode? ToNode(ScenarioAircraft aircraft) => JsonSerializer.SerializeToNode(aircraft, ScenarioLoader.JsonOptions);

    private static void Node(string path, JsonNode? before, JsonNode? after, List<StampChange> changes)
    {
        if ((before is JsonObject beforeObject) && (after is JsonObject afterObject))
        {
            Objects(path, beforeObject, afterObject, changes);
        }
        else if ((before is JsonArray beforeArray) && (after is JsonArray afterArray))
        {
            Arrays(path, beforeArray, afterArray, changes);
        }
        else if (!JsonNode.DeepEquals(before, after))
        {
            changes.Add(new StampChange(path, DiffKind.Changed, Text(before), Text(after)));
        }
    }

    private static void Objects(string path, JsonObject before, JsonObject after, List<StampChange> changes)
    {
        foreach ((string key, JsonNode? value) in before)
        {
            string child = path + "/" + Escape(key);
            if (after.TryGetPropertyValue(key, out JsonNode? next))
            {
                Node(child, value, next, changes);
            }
            else
            {
                changes.Add(new StampChange(child, DiffKind.Removed, Text(value), null));
            }
        }

        foreach ((string key, JsonNode? value) in after.Where(p => !before.ContainsKey(p.Key)))
        {
            changes.Add(new StampChange(path + "/" + Escape(key), DiffKind.Added, null, Text(value)));
        }
    }

    private static void Arrays(string path, JsonArray before, JsonArray after, List<StampChange> changes)
    {
        if ((Ids(before) is { } beforeIds) && (Ids(after) is { } afterIds))
        {
            ById(path, (before, beforeIds), (after, afterIds), changes);
            return;
        }

        int common = Math.Min(before.Count, after.Count);
        for (int i = 0; i < common; i++)
        {
            Node($"{path}/{i}", before[i], after[i], changes);
        }

        for (int i = common; i < before.Count; i++)
        {
            changes.Add(new StampChange($"{path}/{i}", DiffKind.Removed, Text(before[i]), null));
        }

        for (int i = common; i < after.Count; i++)
        {
            changes.Add(new StampChange($"{path}/{i}", DiffKind.Added, null, Text(after[i])));
        }
    }

    private static void ById(string path, (JsonArray Items, string[] Ids) before, (JsonArray Items, string[] Ids) after, List<StampChange> changes)
    {
        for (int i = 0; i < before.Ids.Length; i++)
        {
            int at = Partner(before.Ids, i, after.Ids);
            if (at >= 0)
            {
                Node($"{path}/{at}", before.Items[i], after.Items[at], changes);
            }
            else
            {
                changes.Add(new StampChange($"{path}/{i}", DiffKind.Removed, Text(before.Items[i]), null));
            }
        }

        for (int i = 0; i < after.Ids.Length; i++)
        {
            if (Partner(after.Ids, i, before.Ids) < 0)
            {
                changes.Add(new StampChange($"{path}/{i}", DiffKind.Added, null, Text(after.Items[i])));
            }
        }
    }

    // Where the item at index on one side sits on the other: by its id, or an empty id at the same index; -1 when it has no partner.
    private static int Partner(string[] ids, int index, string[] other)
    {
        if (ids[index].Length > 0)
        {
            return Array.IndexOf(other, ids[index]);
        }

        return ((index < other.Length) && (other[index].Length == 0)) ? index : -1;
    }

    // Each item's id when every item is an object with a string id and the non-empty ids are distinct, else null.
    private static string[]? Ids(JsonArray items)
    {
        string[] ids = new string[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            if ((items[i] is not JsonObject item) || (item["id"] is not JsonValue value) || !value.TryGetValue(out string? id) || (id is null))
            {
                return null;
            }

            ids[i] = id;
        }

        string[] named = [.. ids.Where(id => id.Length > 0)];
        return (named.Distinct(StringComparer.Ordinal).Count() == named.Length) ? ids : null;
    }

    private static string Text(JsonNode? node) => node?.ToJsonString() ?? "null";

    private static string Escape(string key) => key.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}
