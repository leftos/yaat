using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;

namespace Yaat.Client.Services;

/// <summary>How an import lands on the existing favorites.</summary>
public enum FavoriteImportMode
{
    /// <summary>Keep everything already in the store and merge the file into it by id.</summary>
    Merge,

    /// <summary>Delete every favorite and set first, so the store ends up holding only the imported file.</summary>
    Replace,
}

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
        FavoriteSet set = store.GetSet(setId) ?? throw new ArgumentException($"Unknown favorite set id '{setId}'", nameof(setId));
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        WriteEntry(zip, "set.json", set);
        foreach (FavoriteCommand favorite in store.GetSetFavorites(set.Id))
        {
            WriteEntry(zip, FavoriteEntryName(favorite), favorite);
        }
    }

    /// <summary>Writes every set and every favorite (orphans included) plus the loaded-set ids as a zip.</summary>
    public static void ExportLibrary(FavoriteStore store, IReadOnlyList<string> loadedSetIds, Stream output)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        WriteEntry(zip, "library.json", new LibraryManifest { LoadedSetIds = [.. loadedSetIds] });
        foreach (FavoriteSet set in store.OrderedSets)
        {
            string stem = FavoriteStore.SanitizeFileName(set.DisplayName, "set");
            WriteEntry(zip, $"sets/{stem}.{set.Id}.json", set);
        }

        foreach (FavoriteCommand? favorite in store.AllFavorites.OrderBy(f => f.Label, StringComparer.OrdinalIgnoreCase))
        {
            WriteEntry(zip, FavoriteEntryName(favorite), favorite);
        }
    }

    /// <summary>
    /// Imports a shared file into the store: a set/library zip or a single favorite/set json.
    /// Favorites merge by id (same id overwrites the entity); Global/Airport/Scenario sets merge
    /// into the matching local container; named sets merge by id or are added. A lone new favorite
    /// json lands in Global so it is immediately visible. <see cref="FavoriteImportMode.Replace"/>
    /// wipes the store first, but only once the file has parsed into something usable — an
    /// unrecognized file returns null and leaves the store untouched either way.
    /// </summary>
    public static FavoriteImportResult? ImportFile(FavoriteStore store, string fileName, Stream input, FavoriteImportMode mode) =>
        fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? ImportZip(store, input, mode) : ImportSingleJson(store, input, mode);

    private static FavoriteImportResult? ImportZip(FavoriteStore store, Stream input, FavoriteImportMode mode)
    {
        List<(string Name, JsonObject Root)> entries = [];
        try
        {
            using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
            foreach (ZipArchiveEntry? entry in zip.Entries.Where(e => e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
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
        foreach ((string? name, JsonObject? root) in entries)
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

        List<string> loadedSetIds = manifest?.LoadedSetIds ?? SetIdsToLoadWithoutManifest(sets, mode);
        ClearWhenReplacing(store, mode);
        return Merge(store, favorites, sets, loadedSetIds, addLoneFavoritesToGlobal: sets.Count == 0);
    }

    private static FavoriteImportResult? ImportSingleJson(FavoriteStore store, Stream input, FavoriteImportMode mode)
    {
        using var reader = new StreamReader(input);
        if (TryParseObject(reader.ReadToEnd()) is not { } root)
        {
            return null;
        }

        if (root.ContainsKey("favoriteIds") || root.ContainsKey("kind"))
        {
            if (Deserialize<FavoriteSet>(root) is not { } set)
            {
                return null;
            }

            List<string> loadedSetIds = SetIdsToLoadWithoutManifest([set], mode);
            ClearWhenReplacing(store, mode);
            return Merge(store, [], [set], loadedSetIds, addLoneFavoritesToGlobal: false);
        }

        if (Deserialize<FavoriteCommand>(root) is not { } favorite)
        {
            return null;
        }

        ClearWhenReplacing(store, mode);
        return Merge(store, [favorite], [], [], addLoneFavoritesToGlobal: true);
    }

    /// <summary>
    /// A replace has nothing left loaded afterwards, so a file that carries no library manifest
    /// (a set zip, a lone set json) has to name its own sets as loaded or the import is invisible.
    /// A merge keeps today's behaviour: only a manifest can ask for a set to be loaded.
    /// </summary>
    private static List<string> SetIdsToLoadWithoutManifest(List<FavoriteSet> sets, FavoriteImportMode mode) =>
        mode == FavoriteImportMode.Replace ? sets.Select(s => s.Id).ToList() : [];

    private static void ClearWhenReplacing(FavoriteStore store, FavoriteImportMode mode)
    {
        if (mode == FavoriteImportMode.Replace)
        {
            store.Clear();
        }
    }

    private static FavoriteImportResult Merge(
        FavoriteStore store,
        List<FavoriteCommand> favorites,
        List<FavoriteSet> sets,
        List<string> loadedSetIds,
        bool addLoneFavoritesToGlobal
    )
    {
        int favoritesAdded = 0;
        int favoritesUpdated = 0;
        foreach (FavoriteCommand favorite in favorites)
        {
            bool isNew = string.IsNullOrWhiteSpace(favorite.Id) || store.GetFavorite(favorite.Id) is null;
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

        int setsAdded = 0;
        int setsUpdated = 0;
        int missing = 0;
        var newSetIdsToLoad = new List<string>();
        foreach (FavoriteSet incoming in sets)
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
                    (FavoriteSet? set, bool added) = store.UpsertImportedNamedSet(incoming);
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
        string stem = FavoriteStore.SanitizeFileName(favorite.IsSpacer ? "blank" : favorite.Label, "favorite");
        return $"favorites/{stem}.{favorite.Id}.json";
    }

    private static void WriteEntry<T>(ZipArchive zip, string entryName, T entity)
    {
        ZipArchiveEntry entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
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
