using System.Text.Json.Nodes;

namespace Yaat.Sim.Tests.Authoring;

/// <summary>
/// Real vNAS scenarios committed under <c>TestData/VnasTraining</c>, and the edits the store and downloader tests make to
/// them: a changed server copy (one field changed), a key re-order, a working-copy edit.
/// </summary>
internal static class TrainingFixtures
{
    public const string Artcc = "ZOA";
    public const string IdA = "01H06NVK7VN8BS7MCDXHKJZ7MQ";
    public const string IdB = "01H08FSWF5NCDXTQMQ3BWB0BND";

    public static string Read(string id) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "VnasTraining", id + ".json"));

    public static string NameOf(string id) => JsonNode.Parse(Read(id))!["name"]!.GetValue<string>();

    /// <summary>The scenario with its name suffixed: one field changed.</summary>
    public static string WithNameSuffix(string id, string suffix) => SuffixName(Read(id), suffix);

    /// <summary>A scenario text with its name suffixed: one field changed.</summary>
    public static string SuffixName(string json, string suffix)
    {
        JsonObject scenario = JsonNode.Parse(json)!.AsObject();
        scenario["name"] = scenario["name"]!.GetValue<string>() + suffix;
        return scenario.ToJsonString();
    }

    public static string Changed(string id) => WithNameSuffix(id, " (revised on vNAS)");

    /// <summary>The scenario with its id replaced, so one real body can serve several ids.</summary>
    public static string WithId(string id, string newId)
    {
        JsonObject scenario = JsonNode.Parse(Read(id))!.AsObject();
        scenario["id"] = newId;
        return scenario.ToJsonString();
    }

    /// <summary>The scenario with its first two keys (id and name) swapped: semantically the same JSON.</summary>
    public static string Reordered(string id) => Reorder(Read(id));

    /// <summary>A scenario text with its first two keys swapped: semantically the same JSON.</summary>
    public static string Reorder(string json)
    {
        JsonObject scenario = JsonNode.Parse(json)!.AsObject();
        List<KeyValuePair<string, JsonNode?>> members = [.. scenario.Select(kv => KeyValuePair.Create(kv.Key, kv.Value?.DeepClone()))];
        (members[0], members[1]) = (members[1], members[0]);
        return new JsonObject(members).ToJsonString();
    }

    public static string NewRoot() => Path.Combine(Path.GetTempPath(), "yaat-scenarios-" + Guid.NewGuid().ToString("N"));

    public static void DeleteDir(string dir)
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    public static string MirrorPath(string root, string id) => Path.Combine(root, Artcc, "server", id + ".json");

    public static string LocalPath(string root, string id) => Path.Combine(root, Artcc, "local", id + ".json");

    public static string SidecarPath(string root, string id) => Path.Combine(root, Artcc, "local", id + ".meta.json");

    public static string IndexPath(string root) => Path.Combine(root, Artcc, "index.json");
}
