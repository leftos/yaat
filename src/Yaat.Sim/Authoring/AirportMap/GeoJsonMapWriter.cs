using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Authoring.AirportMap;

/// <summary>
/// Writes an <see cref="AirportMapDocument"/> as a combined GeoJSON FeatureCollection, to text or atomically to a file
/// outside the vNAS airport cache.
/// </summary>
public static class GeoJsonMapWriter
{
    private static readonly ILogger Log = SimLog.CreateLogger("GeoJsonMapWriter");

    // LF on every OS: the committed maps are LF, so a save never rewrites every line ending of a map opened on Windows.
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Writes the document as indented GeoJSON: the collection's foreign members, then every feature in order with its
    /// properties, its geometry (each coordinate as the shortest text that reads back as the same double) and its own
    /// foreign members.
    /// </summary>
    /// <param name="document">The map to write.</param>
    /// <returns>The FeatureCollection text.</returns>
    public static string Write(AirportMapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var root = new JsonObject { ["type"] = "FeatureCollection" };
        AddForeign(root, document.ForeignMembers);
        var features = new JsonArray();
        foreach (MapFeature feature in document.Features)
        {
            features.Add(ToNode(feature));
        }

        root["features"] = features;
        return root.ToJsonString(Options);
    }

    /// <summary>
    /// Writes the document to <paramref name="path"/> through <see cref="AtomicTextFile.WriteAllText"/>, so a failed write
    /// leaves the old file whole.
    /// </summary>
    /// <param name="document">The map to write.</param>
    /// <param name="path">The target file.</param>
    /// <exception cref="InvalidOperationException">The target lies inside the vNAS airport cache, which only the downloader writes.</exception>
    /// <exception cref="IOException">The target stayed locked through every replace attempt.</exception>
    /// <exception cref="UnauthorizedAccessException">The target or its folder cannot be written.</exception>
    public static void WriteFile(AirportMapDocument document, string path)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrEmpty(path);
        string fullPath = Path.GetFullPath(path);
        string cacheDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(YaatPaths.Combine("cache", "airports")));
        if (
            string.Equals(fullPath, cacheDir, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(cacheDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new InvalidOperationException(
                $"Refusing to write the airport map to {path}: it is inside the vNAS airport cache ({cacheDir}), which only the downloader writes."
            );
        }

        AtomicTextFile.WriteAllText(fullPath, Write(document));
        if (Log.IsEnabled(LogLevel.Information))
        {
            Log.LogInformation("Wrote airport map {Path} ({Count} features)", fullPath, document.Features.Count);
        }
    }

    private static JsonObject ToNode(MapFeature feature)
    {
        var node = new JsonObject { ["type"] = "Feature" };
        if (feature.HasProperties)
        {
            node["properties"] = feature.CloneProperties();
        }

        if (feature.GeometryType is { } geometryType)
        {
            // An unedited feature writes its source text, whose QGIS-length literals a double cannot hold (see MapFeature).
            JsonNode? coordinates = feature.SourceCoordinatesJson is { } source ? JsonNode.Parse(source) : ToCoordinates(geometryType, feature);
            node["geometry"] = new JsonObject { ["type"] = geometryType, ["coordinates"] = coordinates };
        }

        AddForeign(node, feature.ForeignMembers);
        return node;
    }

    private static JsonArray ToCoordinates(string geometryType, MapFeature feature)
    {
        if (geometryType == "Point")
        {
            return ToPosition(feature.Coordinates[0]);
        }

        var line = new JsonArray();
        foreach (MapPosition position in feature.Coordinates)
        {
            line.Add(ToPosition(position));
        }

        return line;
    }

    private static JsonArray ToPosition(MapPosition position) => new(JsonValue.Create(position.Lon), JsonValue.Create(position.Lat));

    private static void AddForeign(JsonObject target, IEnumerable<ForeignMember> members)
    {
        foreach (ForeignMember member in members)
        {
            target[member.Name] = JsonNode.Parse(member.RawJson);
        }
    }
}
