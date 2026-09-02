using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Simulation.Oracle;

/// <summary>One field-level difference between two snapshots, at the path that reaches it.</summary>
public sealed record SnapshotDivergence(string Path, string Left, string Right);

/// <summary>
/// Structural comparator between two <see cref="StateSnapshotDto"/> captures, producing one
/// <see cref="SnapshotDivergence"/> per differing leaf. This is the oracle's measuring instrument: it answers
/// "where exactly do these two runs disagree", where <see cref="Replay.SnapshotDiff"/> answers the narrower
/// "has this replay drifted from its recording" over nine aircraft fields with tolerances.
///
/// The comparison runs over <see cref="JsonNode"/> trees serialized with <see cref="RecordingJsonOptions.Default"/>
/// rather than over reflection. That options instance is the one recordings already use and sets no naming policy,
/// so a path here is literally the JSON pointer into a recorded snapshot, and the two polymorphic hierarchies in the
/// tree (<c>PhaseDto</c>, <c>DepartureInstructionDto</c>) resolve through their existing <c>$type</c> discriminators
/// instead of needing to be re-implemented here.
/// </summary>
public static class SnapshotTreeDiff
{
    private static readonly ILogger Log = SimLog.CreateLogger("SnapshotTreeDiff");

    /// <summary>Rendered in place of a value that one side does not have.</summary>
    public const string Absent = "(absent)";

    private const string VirtualNodeId = "-V";
    private const string AircraftArrayProperty = "Aircraft";
    private const string CallsignProperty = "Callsign";

    /// <summary>The highest id <see cref="Data.Airport.VirtualNode"/> can mint, from its counter's -100 start.</summary>
    private const int LowestNonVirtualNodeId = -101;

    /// <summary>
    /// Members holding serialized JSON inside a <c>string</c>. Without re-parsing, each reports as one giant string
    /// leaf — and <c>WeatherJson</c> carries the weather-advance divergence between the live and engine paths, which
    /// is exactly the one worth localizing to a station and a field.
    /// </summary>
    private static readonly HashSet<string> EmbeddedJsonProperties = new(StringComparer.Ordinal)
    {
        "WeatherJson",
        "WeatherSourceJson",
        "AircraftJson",
        "ConfigJson",
    };

    /// <summary>Compares two captures. Paths are rooted at the snapshot, e.g. <c>Aircraft[SWA123].Altitude</c>.</summary>
    public static IReadOnlyList<SnapshotDivergence> Compare(StateSnapshotDto left, StateSnapshotDto right) =>
        CompareNodes(ToNode(left, "left"), ToNode(right, "right"));

    /// <summary>Compares two already-serialized trees. The entry point the comparator's own tests drive.</summary>
    public static IReadOnlyList<SnapshotDivergence> CompareNodes(JsonNode? left, JsonNode? right)
    {
        var sink = new List<SnapshotDivergence>();
        Walk(string.Empty, null, left, right, sink);
        return sink;
    }

    private static JsonNode? ToNode(StateSnapshotDto snapshot, string side)
    {
        try
        {
            return JsonSerializer.SerializeToNode(snapshot, RecordingJsonOptions.Default);
        }
        catch (Exception ex)
        {
            // Rethrown, never swallowed. RecordingJsonOptions does not set AllowNamedFloatingPointLiterals, so a
            // NaN or Infinity anywhere in the state fails here rather than at the comparison — say so, because the
            // raw exception names neither the side nor the second.
            throw new InvalidOperationException(
                $"Could not serialize the {side} snapshot at ElapsedSeconds {snapshot.ElapsedSeconds}. A non-finite double "
                    + "(NaN or Infinity) in the simulation state is the usual cause.",
                ex
            );
        }
    }

    private static void Walk(string path, string? propertyName, JsonNode? left, JsonNode? right, List<SnapshotDivergence> sink)
    {
        if (left is null && right is null)
        {
            return;
        }

        if (left is null || right is null)
        {
            sink.Add(new SnapshotDivergence(path, Render(left), Render(right)));
            return;
        }

        switch (left)
        {
            case JsonObject leftObject when right is JsonObject rightObject:
                WalkObject(path, leftObject, rightObject, sink);
                return;
            case JsonArray leftArray when right is JsonArray rightArray:
                WalkArray(path, propertyName, leftArray, rightArray, sink);
                return;
            case JsonValue when right is JsonValue:
                WalkValue(path, propertyName, left, right, sink);
                return;
            default:
                sink.Add(new SnapshotDivergence(path, Render(left), Render(right)));
                return;
        }
    }

    private static void WalkObject(string path, JsonObject left, JsonObject right, List<SnapshotDivergence> sink)
    {
        foreach (var key in UnionOrdered(left.Select(p => p.Key), right.Select(p => p.Key)))
        {
            // JsonNode represents a JSON null as a C# null reference, so a property that is absent and one written as
            // null are indistinguishable by value — only the lookup's own result separates them. Handling the null
            // cases here rather than in Walk is what keeps "absent on one side, null on the other" from comparing
            // equal, which would be a silent miss in the one instrument whose whole purpose is not to miss.
            bool leftHas = left.TryGetPropertyValue(key, out var leftChild);
            bool rightHas = right.TryGetPropertyValue(key, out var rightChild);

            if (leftChild is null || rightChild is null)
            {
                if ((leftChild is not null) || (rightChild is not null) || (leftHas != rightHas))
                {
                    sink.Add(
                        new SnapshotDivergence(
                            Join(path, key),
                            leftChild is null ? RenderPresence(leftHas) : Render(leftChild),
                            rightChild is null ? RenderPresence(rightHas) : Render(rightChild)
                        )
                    );
                }

                continue;
            }

            Walk(Join(path, key), key, leftChild, rightChild, sink);
        }
    }

    /// <summary>Distinguishes a property written as JSON null from one that is not there at all.</summary>
    private static string RenderPresence(bool present) => present ? "null" : Absent;

    private static void WalkArray(string path, string? propertyName, JsonArray left, JsonArray right, List<SnapshotDivergence> sink)
    {
        // Index keying is correct for every collection in the snapshot except the aircraft list. Several are
        // index-semantic — Queue.Blocks is addressed by CurrentBlockIndex, Phases.Phases by CurrentIndex,
        // AssignedTaxiRoute.Segments by CurrentSegmentIndex — and Targets.NavigationRoute may legally repeat a fix
        // name. Only the aircraft list genuinely reorders between runs, so only it gets a natural key.
        if (string.Equals(propertyName, AircraftArrayProperty, StringComparison.Ordinal))
        {
            WalkAircraftArray(path, left, right, sink);
            return;
        }

        int count = Math.Max(left.Count, right.Count);
        for (int i = 0; i < count; i++)
        {
            Walk($"{path}[{i}]", propertyName, i < left.Count ? left[i] : null, i < right.Count ? right[i] : null, sink);
        }
    }

    private static void WalkAircraftArray(string path, JsonArray left, JsonArray right, List<SnapshotDivergence> sink)
    {
        var leftByCallsign = IndexByCallsign(left);
        var rightByCallsign = IndexByCallsign(right);

        foreach (var callsign in UnionOrdered(leftByCallsign.Keys, rightByCallsign.Keys))
        {
            leftByCallsign.TryGetValue(callsign, out var leftChild);
            rightByCallsign.TryGetValue(callsign, out var rightChild);
            Walk($"{path}[{callsign}]", null, leftChild, rightChild, sink);
        }
    }

    /// <summary>Keys aircraft elements by callsign, falling back to the index for an element that has none.</summary>
    private static Dictionary<string, JsonNode?> IndexByCallsign(JsonArray array)
    {
        var byCallsign = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        for (int i = 0; i < array.Count; i++)
        {
            string callsign = (array[i] as JsonObject)?[CallsignProperty]?.ToString() ?? i.ToString();

            // Two aircraft sharing a callsign in one snapshot is itself the class of state bug this tool exists to
            // catch — SimulationWorld holds aircraft in a list and AddAircraft de-duplicates defensively because an
            // appended duplicate has gone wrong before. Suffixing the index keeps both in the union, where a plain
            // assignment would let the later one replace the earlier and hide the difference entirely.
            string key = byCallsign.ContainsKey(callsign) ? $"{callsign}#{i}" : callsign;
            byCallsign[key] = array[i];
        }

        return byCallsign;
    }

    private static void WalkValue(string path, string? propertyName, JsonNode left, JsonNode right, List<SnapshotDivergence> sink)
    {
        if (propertyName is not null && EmbeddedJsonProperties.Contains(propertyName))
        {
            // Only recurse when both sides parse. If one holds something that is not JSON, the string comparison
            // below is the honest report — recursing would render the unparsed side as absent and hide its value.
            var leftInner = TryParseEmbedded(left, propertyName);
            var rightInner = TryParseEmbedded(right, propertyName);
            if (leftInner is not null && rightInner is not null)
            {
                Walk(path, null, leftInner, rightInner, sink);
                return;
            }
        }

        string leftText = RenderValue(propertyName, left);
        string rightText = RenderValue(propertyName, right);
        if (!string.Equals(leftText, rightText, StringComparison.Ordinal))
        {
            sink.Add(new SnapshotDivergence(path, leftText, rightText));
        }
    }

    private static JsonNode? TryParseEmbedded(JsonNode value, string propertyName)
    {
        if (value.GetValueKind() != JsonValueKind.String)
        {
            return null;
        }

        string text = value.GetValue<string>();
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException ex)
        {
            // The caller falls back to comparing the raw strings, which is honest but unlocalized. Both sides of a
            // real sweep are produced by JsonSerializer, so a property that does not parse means something upstream
            // already wrote something unexpected — worth a line rather than passing silently.
            Log.LogDebug(ex, "Embedded-JSON property {Property} did not parse; comparing it as a plain string.", propertyName);
            return null;
        }
    }

    private static string RenderValue(string? propertyName, JsonNode value)
    {
        string text = value.ToJsonString();

        // VirtualNode ids come from a process-wide counter that is never reset, so two rooms in one process label
        // identical geometry with different negative ids. The label is not behaviour. Layout ids come from the
        // GeoJSON and are meaningful, so anything outside the virtual range still reports.
        return IsVirtualNodeIdProperty(propertyName) && IsVirtualNodeId(text) ? VirtualNodeId : text;
    }

    private static bool IsVirtualNodeIdProperty(string? propertyName) =>
        propertyName is not null
        && (propertyName.EndsWith("NodeId", StringComparison.Ordinal) || propertyName.EndsWith("NodeIds", StringComparison.Ordinal));

    /// <summary>
    /// True only inside <see cref="Data.Airport.VirtualNode"/>'s actual id range. Its counter starts at -100 and
    /// decrements before use, so every virtual id is at most -101. Testing that range rather than "any negative
    /// number" fails safe: a field that later adopts a small negative sentinel such as -1 reports as a divergence
    /// instead of being silently folded together with a real virtual-node id. Anything that is not a plain integer
    /// (an exponent form, a value beyond <see cref="int"/>) also reports rather than being normalized away.
    /// </summary>
    private static bool IsVirtualNodeId(string text) => int.TryParse(text, out int id) && id <= LowestNonVirtualNodeId;

    /// <summary>A container renders as a shape, not as its whole serialization — the path already says which one it is.</summary>
    private static string Render(JsonNode? node) =>
        node switch
        {
            null => Absent,
            JsonObject obj => $"{{object, {obj.Count} properties}}",
            JsonArray array => $"[array, {array.Count} items]",
            _ => node.ToJsonString(),
        };

    private static IEnumerable<string> UnionOrdered(IEnumerable<string> left, IEnumerable<string> right) =>
        left.Union(right, StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal);

    private static string Join(string path, string key) => path.Length == 0 ? key : $"{path}.{key}";
}
