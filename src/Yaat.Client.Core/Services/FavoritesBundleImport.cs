using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;

namespace Yaat.Client.Services;

/// <summary>What a favorites import did.</summary>
/// <param name="Result">The store's report; null when the file held nothing to import.</param>
/// <param name="SetIdMap">For every named set in the file, keyed by its id there, its id in the store; null when it was not imported.</param>
/// <param name="Overwritten">Clashing sets that overwrote an existing set.</param>
/// <param name="Renamed">Clashing sets imported under a new name.</param>
/// <param name="Skipped">Clashing sets left out.</param>
internal sealed record FavoritesImportOutcome(
    FavoriteImportResult? Result,
    IReadOnlyDictionary<string, string?> SetIdMap,
    int Overwritten,
    int Renamed,
    int Skipped
);

/// <summary>
/// The favorites entry of a bundle, parsed into its json entities so a merge can resolve set clashes before the file goes
/// through <see cref="FavoriteExport.ImportFile"/>: a skipped set is dropped with the favorites only it references, an
/// overwriting set takes the existing set's id, and a renamed set takes its new name (and a new id when its own id is
/// already in the store).
/// </summary>
internal sealed class FavoritesBundleImport
{
    private const string LibraryManifestName = "library.json";

    private static readonly ILogger Log = AppLog.CreateLogger("FavoritesBundleImport");

    private FavoritesBundleImport(List<(string Name, JsonObject Root)> entities, bool isZip)
    {
        Entities = entities;
        IsZip = isZip;
    }

    /// <summary>Every json entity of the file in order, with its zip entry name (or the file's name for a lone json).</summary>
    public List<(string Name, JsonObject Root)> Entities { get; }

    public bool IsZip { get; }

    /// <summary>The sets in the file, each with its entity index.</summary>
    public IEnumerable<(int Index, FavoriteSet Set)> Sets =>
        Entities
            .Select((entity, index) => (Index: index, Set: IsSet(entity.Name, entity.Root) ? Deserialize<FavoriteSet>(entity.Root) : null))
            .Where(s => s.Set is not null)
            .Select(s => (s.Index, s.Set!));

    public static FavoritesBundleImport Parse(SettingsBundleEntry entry)
    {
        try
        {
            return (entry.Format == SettingsBundleFormats.FavoritesJson) ? ParseJson(entry) : ParseZip(entry);
        }
        catch (JsonException ex)
        {
            throw SettingsBundleItems.Unreadable(entry, ex.Message, ex);
        }
    }

    /// <summary>Imports the file into the store, applying the clash choices first when merging.</summary>
    public FavoritesImportOutcome Import(IReadOnlyList<ImportClash> clashes, FavoriteStore store, SettingsImportMode mode)
    {
        List<string> incomingIds = [.. Sets.Where(s => s.Set.Kind == FavoriteSetKind.Named).Select(s => s.Set.Id)];
        ClashOutcome applied = (mode == SettingsImportMode.Merge) ? ApplyClashChoices(clashes, store) : new ClashOutcome();
        FavoriteImportMode importMode = (mode == SettingsImportMode.Replace) ? FavoriteImportMode.Replace : FavoriteImportMode.Merge;

        // Skipping the only set of a lone set json leaves nothing to import.
        FavoriteImportResult? result = null;
        if (Entities.Count > 0)
        {
            using var buffer = new MemoryStream(Serialize());
            result = FavoriteExport.ImportFile(store, IsZip ? "favorites.zip" : "favorites.json", buffer, importMode);
        }

        Dictionary<string, string?> setIdMap = FinalSetIds(incomingIds, applied.IdChanges, result);
        return (result is null)
            ? new FavoritesImportOutcome(null, setIdMap, 0, 0, applied.Skipped)
            : new FavoritesImportOutcome(result, setIdMap, applied.Overwritten, applied.Renamed, applied.Skipped);
    }

    private static Dictionary<string, string?> FinalSetIds(
        List<string> incomingIds,
        Dictionary<string, string?> idChanges,
        FavoriteImportResult? result
    )
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in incomingIds.Where(id => id is not null))
        {
            string? afterClash = idChanges.TryGetValue(id, out string? changed) ? changed : id;
            map[id] = ((afterClash is not null) && (result is not null) && result.SetIdMap.TryGetValue(afterClash, out string? final)) ? final : null;
        }

        return map;
    }

    private static FavoritesBundleImport ParseJson(SettingsBundleEntry entry)
    {
        using var stream = new MemoryStream(entry.Content);
        JsonObject root = JsonNode.Parse(stream) as JsonObject ?? throw new JsonException("it is not a JSON object");
        return new FavoritesBundleImport([(entry.FileName, root)], isZip: false);
    }

    private static FavoritesBundleImport ParseZip(SettingsBundleEntry entry)
    {
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(new MemoryStream(entry.Content), ZipArchiveMode.Read);
        }
        catch (InvalidDataException ex)
        {
            throw SettingsBundleItems.Unreadable(entry, "it is not a zip file", ex);
        }

        using (zip)
        {
            var reader = new BoundedZipReader(entry.FileName);
            var entities = new List<(string Name, JsonObject Root)>();
            foreach (ZipArchiveEntry zipEntry in zip.Entries.Where(e => e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            {
                using var stream = new MemoryStream(reader.Read(zipEntry));
                try
                {
                    if (JsonNode.Parse(stream) is JsonObject root)
                    {
                        entities.Add((zipEntry.FullName, root));
                    }
                }
                catch (JsonException ex)
                {
                    // FavoriteExport.ImportFile skips an unreadable entity the same way; the rest of the file still imports.
                    Log.LogWarning(ex, "Skipping unreadable favorites entry {Entry} in {File}", zipEntry.FullName, entry.FileName);
                }
            }

            return new FavoritesBundleImport(entities, isZip: true);
        }
    }

    private ClashOutcome ApplyClashChoices(IReadOnlyList<ImportClash> clashes, FavoriteStore store)
    {
        var outcome = new ClashOutcome();
        var skippedIndexes = new HashSet<int>();
        foreach (ImportClash clash in clashes)
        {
            JsonObject root = Entities[clash.IncomingIndex].Root;
            string incomingId = StringValue(root, "id") ?? "";
            switch (clash.Choice)
            {
                case ClashChoice.Skip:
                    skippedIndexes.Add(clash.IncomingIndex);
                    outcome.IdChanges[incomingId] = null;
                    outcome.Skipped++;
                    break;
                case ClashChoice.Overwrite:
                    Overwrite(root, incomingId, clash, store, outcome);
                    break;
                case ClashChoice.Rename:
                    Rename(root, incomingId, clash, store, outcome);
                    break;
            }
        }

        DropSkippedSets(skippedIndexes);
        RemapLoadedSetIds(outcome.IdChanges);
        return outcome;
    }

    private static void Overwrite(JsonObject root, string incomingId, ImportClash clash, FavoriteStore store, ClashOutcome outcome)
    {
        if (store.FindNamedSet(clash.ExistingName) is not { } existing)
        {
            Log.LogWarning(
                "Set '{Existing}' to overwrite with '{Incoming}' no longer exists; the incoming set imports as is",
                clash.ExistingName,
                clash.IncomingName
            );
            return;
        }

        root["id"] = existing.Id;
        outcome.IdChanges[incomingId] = existing.Id;
        outcome.Overwritten++;
    }

    private static void Rename(JsonObject root, string incomingId, ImportClash clash, FavoriteStore store, ClashOutcome outcome)
    {
        root["name"] = clash.RenameTo.Trim();
        if (store.GetSet(incomingId) is not null)
        {
            string newId = store.NewSetId();
            root["id"] = newId;
            outcome.IdChanges[incomingId] = newId;
        }

        outcome.Renamed++;
    }

    // A skipped set takes the favorites only it references with it; one a kept set also lists still imports.
    private void DropSkippedSets(HashSet<int> skippedIndexes)
    {
        if (skippedIndexes.Count == 0)
        {
            return;
        }

        List<(int Index, FavoriteSet Set)> sets = [.. Sets];
        var keptReferences = new HashSet<string>(
            sets.Where(s => !skippedIndexes.Contains(s.Index)).SelectMany(s => s.Set.FavoriteIds),
            StringComparer.OrdinalIgnoreCase
        );
        var dropped = new HashSet<string>(
            sets.Where(s => skippedIndexes.Contains(s.Index)).SelectMany(s => s.Set.FavoriteIds).Where(id => !keptReferences.Contains(id)),
            StringComparer.OrdinalIgnoreCase
        );

        List<(string Name, JsonObject Root)> kept = [];
        for (int i = 0; i < Entities.Count; i++)
        {
            (string name, JsonObject root) = Entities[i];
            bool isFavorite = !IsSet(name, root) && !IsLibraryManifest(name);
            bool isDroppedFavorite = isFavorite && dropped.Contains(StringValue(root, "id") ?? "");
            if (!skippedIndexes.Contains(i) && !isDroppedFavorite)
            {
                kept.Add((name, root));
            }
        }

        Entities.Clear();
        Entities.AddRange(kept);
    }

    private void RemapLoadedSetIds(Dictionary<string, string?> idChanges)
    {
        foreach ((string name, JsonObject root) in Entities.Where(e => IsLibraryManifest(e.Name)))
        {
            if (root["loadedSetIds"] is not JsonArray loaded)
            {
                continue;
            }

            List<string> remapped = [];
            foreach (JsonNode? node in loaded)
            {
                string? id = ((node is JsonValue value) && value.TryGetValue(out string? text)) ? text : null;
                string? mapped = ((id is not null) && idChanges.TryGetValue(id, out string? changed)) ? changed : id;
                if (mapped is not null)
                {
                    remapped.Add(mapped);
                }
            }

            root["loadedSetIds"] = new JsonArray([.. remapped.Select(id => (JsonNode)JsonValue.Create(id))]);
            Log.LogDebug("Remapped loaded favorite set ids in {Entry}", name);
        }
    }

    private byte[] Serialize()
    {
        if (!IsZip)
        {
            return JsonSerializer.SerializeToUtf8Bytes(Entities[0].Root, UserPreferences.JsonOptions);
        }

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, JsonObject root) in Entities)
            {
                using Stream stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                JsonSerializer.Serialize(stream, root, UserPreferences.JsonOptions);
            }
        }

        return buffer.ToArray();
    }

    private static bool IsLibraryManifest(string name) =>
        string.Equals(Path.GetFileName(name), LibraryManifestName, StringComparison.OrdinalIgnoreCase);

    // The same test FavoriteExport.ImportFile uses to tell a set from a favorite.
    private static bool IsSet(string name, JsonObject root) =>
        !IsLibraryManifest(name) && (root.ContainsKey("favoriteIds") || root.ContainsKey("kind"));

    private static string? StringValue(JsonObject root, string key) =>
        ((root[key] is JsonValue value) && value.TryGetValue(out string? text)) ? text : null;

    private static T? Deserialize<T>(JsonObject root)
        where T : class
    {
        try
        {
            return root.Deserialize<T>(UserPreferences.JsonOptions);
        }
        catch (JsonException ex)
        {
            Log.LogWarning(ex, "Skipping malformed favorites entity while planning an import");
            return null;
        }
    }

    private sealed class ClashOutcome
    {
        public Dictionary<string, string?> IdChanges { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int Overwritten { get; set; }
        public int Renamed { get; set; }
        public int Skipped { get; set; }
    }
}
