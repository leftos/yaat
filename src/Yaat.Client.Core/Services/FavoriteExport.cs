using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;

namespace Yaat.Client.Services;

/// <summary>What an import changed, for the status line the UI shows afterwards.</summary>
public sealed record FavoriteImportResult(
    int FavoritesAdded,
    int FavoritesUpdated,
    int SetsAdded,
    int SetsUpdated,
    int MissingReferences,
    List<string> NewSetIdsToLoad
);

/// <summary>
/// Zip-based sharing of favorites. A set export is <c>[Name].yaat-favset.zip</c> holding
/// <c>set.json</c> plus <c>favorites/[Label].{id}.json</c> for each referenced favorite — the set
/// and the cut-down favorites side by side, each entity its own json. A library export is
/// <c>[name].yaat-favlibrary.zip</c> holding every set under <c>sets/</c>, every favorite under
/// <c>favorites/</c> (orphans included), and <c>library.json</c> with the loaded-set ids.
/// Import accepts either zip or a single entity json, merging by id.
/// </summary>
public static class FavoriteExport
{
    public const string SetExportExtension = ".yaat-favset.zip";
    public const string LibraryExportExtension = ".yaat-favlibrary.zip";

    private static readonly ILogger Log = AppLog.CreateLogger("FavoriteExport");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed class LibraryManifest
    {
        public int Version { get; set; } = 1;
        public List<string> LoadedSetIds { get; set; } = [];
    }

    /// <summary>Writes one set and its referenced favorites as a zip. Throws when the set id is unknown.</summary>
    public static void ExportSet(FavoriteStore store, string setId, Stream output)
    {
        var set = store.GetSet(setId) ?? throw new ArgumentException($"Unknown favorite set id '{setId}'", nameof(setId));
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        WriteEntry(zip, "set.json", set);
        foreach (var favorite in store.GetSetFavorites(set.Id))
        {
            WriteEntry(zip, FavoriteEntryName(favorite), favorite);
        }
    }

    /// <summary>Writes every set and every favorite (orphans included) plus the loaded-set ids as a zip.</summary>
    public static void ExportLibrary(FavoriteStore store, IReadOnlyList<string> loadedSetIds, Stream output)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        WriteEntry(zip, "library.json", new LibraryManifest { LoadedSetIds = [.. loadedSetIds] });
        foreach (var set in store.OrderedSets)
        {
            var stem = FavoriteStore.SanitizeFileName(set.DisplayName, "set");
            WriteEntry(zip, $"sets/{stem}.{set.Id}.json", set);
        }

        foreach (var favorite in store.AllFavorites.OrderBy(f => f.Label, StringComparer.OrdinalIgnoreCase))
        {
            WriteEntry(zip, FavoriteEntryName(favorite), favorite);
        }
    }

    /// <summary>
    /// Imports a shared file into the store: a set/library zip or a single favorite/set json.
    /// Favorites merge by id (same id overwrites the entity); Global/Airport/Scenario sets merge
    /// into the matching local container; named sets merge by id or are added. A lone new favorite
    /// json lands in Global so it is immediately visible. Returns null when nothing usable was found.
    /// </summary>
    public static FavoriteImportResult? ImportFile(FavoriteStore store, string fileName, Stream input)
    {
        return fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? ImportZip(store, input) : ImportSingleJson(store, input);
    }

    private static FavoriteImportResult? ImportZip(FavoriteStore store, Stream input)
    {
        List<(string Name, JsonObject Root)> entries = [];
        try
        {
            using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
            foreach (var entry in zip.Entries.Where(e => e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            {
                using var reader = new StreamReader(entry.Open());
                if (TryParseObject(reader.ReadToEnd()) is { } root)
                {
                    entries.Add((entry.FullName, root));
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            Log.LogWarning(ex, "Could not read favorites zip");
            return null;
        }

        var favorites = new List<FavoriteCommand>();
        var sets = new List<FavoriteSet>();
        var manifest = default(LibraryManifest);
        foreach (var (name, root) in entries)
        {
            if (string.Equals(Path.GetFileName(name), "library.json", StringComparison.OrdinalIgnoreCase))
            {
                manifest = Deserialize<LibraryManifest>(root);
            }
            else if (root.ContainsKey("favoriteIds") || root.ContainsKey("kind"))
            {
                if (Deserialize<FavoriteSet>(root) is { } set)
                {
                    sets.Add(set);
                }
            }
            else if (Deserialize<FavoriteCommand>(root) is { } favorite)
            {
                favorites.Add(favorite);
            }
        }

        if (favorites.Count == 0 && sets.Count == 0)
        {
            return null;
        }

        return Merge(store, favorites, sets, manifest?.LoadedSetIds ?? [], addLoneFavoritesToGlobal: sets.Count == 0);
    }

    private static FavoriteImportResult? ImportSingleJson(FavoriteStore store, Stream input)
    {
        using var reader = new StreamReader(input);
        if (TryParseObject(reader.ReadToEnd()) is not { } root)
        {
            return null;
        }

        if (root.ContainsKey("favoriteIds") || root.ContainsKey("kind"))
        {
            return Deserialize<FavoriteSet>(root) is { } set ? Merge(store, [], [set], [], addLoneFavoritesToGlobal: false) : null;
        }

        return Deserialize<FavoriteCommand>(root) is { } favorite ? Merge(store, [favorite], [], [], addLoneFavoritesToGlobal: true) : null;
    }

    private static FavoriteImportResult Merge(
        FavoriteStore store,
        List<FavoriteCommand> favorites,
        List<FavoriteSet> sets,
        List<string> loadedSetIds,
        bool addLoneFavoritesToGlobal
    )
    {
        var favoritesAdded = 0;
        var favoritesUpdated = 0;
        foreach (var favorite in favorites)
        {
            var isNew = string.IsNullOrWhiteSpace(favorite.Id) || store.GetFavorite(favorite.Id) is null;
            store.SaveFavorite(favorite);
            if (isNew)
            {
                favoritesAdded++;
                if (addLoneFavoritesToGlobal)
                {
                    store.AddToSet(store.GlobalSet.Id, favorite.Id);
                }
            }
            else
            {
                favoritesUpdated++;
            }
        }

        var setsAdded = 0;
        var setsUpdated = 0;
        var missing = 0;
        var newSetIdsToLoad = new List<string>();
        foreach (var incoming in sets)
        {
            missing += incoming.FavoriteIds.Count(id => store.GetFavorite(id) is null);
            switch (incoming.Kind)
            {
                case FavoriteSetKind.Global:
                    store.AppendToSet(store.GlobalSet.Id, incoming.FavoriteIds);
                    setsUpdated++;
                    break;
                case FavoriteSetKind.Airport when FavoriteStore.NormalizeAirportId(incoming.Key) is { } airport:
                    store.AppendToSet(store.GetOrCreateAirportSet(airport).Id, incoming.FavoriteIds);
                    setsUpdated++;
                    break;
                case FavoriteSetKind.Scenario when !string.IsNullOrWhiteSpace(incoming.Key):
                    store.AppendToSet(store.GetOrCreateScenarioSet(incoming.Key, incoming.Name).Id, incoming.FavoriteIds);
                    setsUpdated++;
                    break;
                default:
                    var (set, added) = store.UpsertImportedNamedSet(incoming);
                    if (added)
                    {
                        setsAdded++;
                        if (loadedSetIds.Contains(incoming.Id, StringComparer.OrdinalIgnoreCase))
                        {
                            newSetIdsToLoad.Add(set.Id);
                        }
                    }
                    else
                    {
                        setsUpdated++;
                    }
                    break;
            }
        }

        return new FavoriteImportResult(favoritesAdded, favoritesUpdated, setsAdded, setsUpdated, missing, newSetIdsToLoad);
    }

    private static string FavoriteEntryName(FavoriteCommand favorite)
    {
        var stem = FavoriteStore.SanitizeFileName(favorite.IsSpacer ? "blank" : favorite.Label, "favorite");
        return $"favorites/{stem}.{favorite.Id}.json";
    }

    private static void WriteEntry<T>(ZipArchive zip, string entryName, T entity)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(JsonSerializer.Serialize(entity, JsonOptions));
    }

    private static JsonObject? TryParseObject(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static T? Deserialize<T>(JsonObject root)
        where T : class
    {
        try
        {
            return root.Deserialize<T>(JsonOptions);
        }
        catch (JsonException ex)
        {
            Log.LogWarning(ex, "Skipping malformed favorites import entry");
            return null;
        }
    }
}
