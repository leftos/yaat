using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;

namespace Yaat.Client.Services;

/// <summary>
/// A saved weather file split for loading: the JSON to send (never carrying <c>windGridGrib2</c>), the embedded grid's bytes when
/// they decoded, and whether the file named a grid it does not hold, which loads without the grid under the GFS warning.
/// </summary>
public sealed record SavedWeatherGridExtract(string Json, byte[]? Grib2, bool GridMissing);

/// <summary>
/// The GFS wind grid a saved live weather file carries: Save Weather writes the grid's raw GRIB2 bytes as base64 in a top-level
/// <c>windGridGrib2</c> beside <c>windGridHash</c>, so loading the file re-uploads them and the same winds come back on any server.
/// Both names match whatever their casing, as the weather parser does; a write leaves one key, spelled canonically.
/// </summary>
public static class SavedWeatherGrid
{
    public const string GridProperty = "windGridGrib2";
    public const string HashProperty = "windGridHash";

    /// <summary>The GFS warning's reason for a file that names a grid without carrying usable bytes.</summary>
    public const string NotInSavedFile = "grid not in the saved file";

    private static readonly ILogger Log = AppLog.CreateLogger(nameof(SavedWeatherGrid));

    /// <summary>Adds <paramref name="grib2"/> as <c>windGridGrib2</c> to a v1 weather JSON object.</summary>
    /// <exception cref="JsonException">The JSON is not an object.</exception>
    public static string Embed(string json, byte[] grib2)
    {
        JsonObject root = ParseObject(json) ?? throw new JsonException("Saved weather with a GFS grid must be a JSON object");
        SetProperty(root, GridProperty, Convert.ToBase64String(grib2));
        return root.ToJsonString();
    }

    /// <summary>
    /// Reads and removes <c>windGridGrib2</c>. With decodable bytes the JSON keeps its <c>windGridHash</c>; without them (absent or
    /// not base64) a <c>windGridHash</c> is removed too and the grid counts as missing. JSON with neither field, or that is not an
    /// object, comes back unchanged.
    /// </summary>
    public static SavedWeatherGridExtract Extract(string json)
    {
        JsonObject? root;
        try
        {
            root = ParseObject(json);
        }
        catch (JsonException ex)
        {
            Log.LogWarning(ex, "Weather JSON did not parse while reading its GFS grid; sending it unchanged");
            return new SavedWeatherGridExtract(json, null, false);
        }

        if (root is null)
        {
            return new SavedWeatherGridExtract(json, null, false);
        }

        bool hasGrid = HasProperty(root, GridProperty);
        if (!hasGrid && !HasProperty(root, HashProperty))
        {
            return new SavedWeatherGridExtract(json, null, false);
        }

        byte[]? grib2 = hasGrid ? DecodeBase64(RemoveProperty(root, GridProperty)) : null;
        if (grib2 is null)
        {
            RemoveProperty(root, HashProperty);
        }

        return new SavedWeatherGridExtract(root.ToJsonString(), grib2, grib2 is null);
    }

    /// <summary>The <c>windGridHash</c> a weather JSON names, or null when it names none or is not a JSON object.</summary>
    public static string? NamedHash(string? json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            JsonObject? root = ParseObject(json);
            if ((root is null) || (FindProperty(root, HashProperty) is not { } key))
            {
                return null;
            }

            return ((root[key] is JsonValue value) && value.TryGetValue(out string? hash)) ? hash : null;
        }
        catch (JsonException ex)
        {
            Log.LogWarning(ex, "Weather JSON did not parse while reading its GFS grid hash");
            return null;
        }
    }

    /// <summary>
    /// True when <paramref name="json"/> names the grid stored under <paramref name="hash"/>: a room weather echo keeps the held
    /// grid bytes only then.
    /// </summary>
    public static bool NamesGrid(string? json, string hash) => string.Equals(NamedHash(json), hash, StringComparison.Ordinal);

    /// <summary>Sets <c>windGridHash</c> to <paramref name="hash"/>, or removes it when null.</summary>
    /// <exception cref="JsonException">The JSON is not an object.</exception>
    public static string WithHash(string json, string? hash)
    {
        JsonObject root = ParseObject(json) ?? throw new JsonException("Weather naming a GFS grid must be a JSON object");
        if (hash is null)
        {
            RemoveProperty(root, HashProperty);
        }
        else
        {
            SetProperty(root, HashProperty, hash);
        }

        return root.ToJsonString();
    }

    private static JsonObject? ParseObject(string json) => JsonNode.Parse(json) as JsonObject;

    /// <summary>The property spelling <paramref name="property"/> names: the canonical one when present, else the first variant.</summary>
    private static string? FindProperty(JsonObject root, string property)
    {
        if (root.ContainsKey(property))
        {
            return property;
        }

        foreach (KeyValuePair<string, JsonNode?> pair in root)
        {
            if (string.Equals(pair.Key, property, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Key;
            }
        }

        return null;
    }

    private static bool HasProperty(JsonObject root, string property) => FindProperty(root, property) is not null;

    /// <summary>
    /// Removes every spelling of <paramref name="property"/>, so a write leaves one key, and returns the canonical spelling's node,
    /// else the first variant's.
    /// </summary>
    private static JsonNode? RemoveProperty(JsonObject root, string property)
    {
        JsonNode? removed = null;
        if (root.Remove(property, out JsonNode? canonical))
        {
            removed = canonical;
        }

        List<string> variants = [.. root.Select(pair => pair.Key).Where(key => string.Equals(key, property, StringComparison.OrdinalIgnoreCase))];
        foreach (string key in variants)
        {
            if ((root.Remove(key, out JsonNode? value)) && (removed is null))
            {
                removed = value;
            }
        }

        return removed;
    }

    private static void SetProperty(JsonObject root, string property, string value)
    {
        RemoveProperty(root, property);
        root[property] = value;
    }

    private static byte[]? DecodeBase64(JsonNode? embedded)
    {
        if ((embedded is not JsonValue value) || !value.TryGetValue(out string? base64))
        {
            Log.LogWarning("Saved weather's {Property} is not a string; loading without the grid", GridProperty);
            return null;
        }

        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException ex)
        {
            Log.LogWarning(ex, "Saved weather's {Property} is not base64; loading without the grid", GridProperty);
            return null;
        }
    }
}
