using System.Text.Json;
using System.Text.Json.Nodes;

namespace Yaat.Client.Services;

/// <summary>
/// The on-disk shape of an "All sets" favorites export: every named set plus which ones were
/// loaded at export time. Shares the <c>*.yaat-favorites.json</c> extension with the legacy
/// flat format; the two are told apart by the JSON root token (object = bundle, array = flat).
/// </summary>
public sealed class FavoriteSetsExportFile
{
    public int Version { get; set; } = 1;
    public List<string> LoadedSetNames { get; set; } = [];
    public List<FavoriteCommandSet> Sets { get; set; } = [];
}

/// <summary>
/// Result of parsing a <c>*.yaat-favorites.json</c> file: exactly one of the two payload
/// fields is non-null (flat legacy list or all-sets bundle).
/// </summary>
public sealed class FavoriteImportPayload
{
    public List<FavoriteCommand>? FlatFavorites { get; init; }
    public FavoriteSetsExportFile? Bundle { get; init; }
}

/// <summary>Parses and serializes favorite-command export files, auto-detecting the flat vs bundle format.</summary>
public static class FavoriteSetSerialization
{
    /// <summary>
    /// Parses a favorites export file. An array root is the legacy flat format (a bare
    /// <c>List&lt;FavoriteCommand&gt;</c>); an object root is the all-sets bundle. Returns null
    /// when the text is not valid JSON or matches neither shape.
    /// </summary>
    public static FavoriteImportPayload? Parse(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        try
        {
            if (root is JsonArray)
            {
                var flat = root.Deserialize<List<FavoriteCommand>>(UserPreferences.JsonOptions);
                return flat is null ? null : new FavoriteImportPayload { FlatFavorites = flat };
            }

            if (root is JsonObject)
            {
                var bundle = root.Deserialize<FavoriteSetsExportFile>(UserPreferences.JsonOptions);
                if ((bundle is null) || (bundle.Sets.Count == 0))
                {
                    return null;
                }
                return new FavoriteImportPayload { Bundle = bundle };
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    public static string SerializeBundle(IReadOnlyList<FavoriteCommandSet> sets, IReadOnlyList<string> loadedSetNames)
    {
        var file = new FavoriteSetsExportFile { LoadedSetNames = [.. loadedSetNames], Sets = [.. sets] };
        return JsonSerializer.Serialize(file, UserPreferences.JsonOptions);
    }
}
