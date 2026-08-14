using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Sim;

namespace Yaat.Client.Services;

/// <summary>Container kind. Every container is a set on the same level; kind only decides visibility and identity key.</summary>
public enum FavoriteSetKind
{
    /// <summary>The always-visible set. Exactly one exists; cannot be renamed or deleted.</summary>
    Global,

    /// <summary>Visible while <see cref="FavoriteSet.Key"/> matches the active scenario's primary airport.</summary>
    Airport,

    /// <summary>Visible while <see cref="FavoriteSet.Key"/> matches the active scenario id.</summary>
    Scenario,

    /// <summary>User-created set; visible while loaded (load order kept in <see cref="UserPreferences.LoadedFavoriteSetIds"/>).</summary>
    Named,
}

/// <summary>
/// A favorite command as its own entity: identified by a stable 8-hex id and stored one json
/// file per favorite. Containers reference favorites by id, so one entity can live in any
/// number of sets and an edit shows up everywhere at once.
/// </summary>
public sealed class FavoriteCommand
{
    public string Id { get; set; } = "";
    public bool IsSpacer { get; set; }
    public string Label { get; set; } = "";
    public string CommandText { get; set; } = "";
    public string GroundCommandText { get; set; } = "";
    public FavoriteCommandCategory Category { get; set; } = FavoriteCommandCategory.Air;
    public string BackgroundColor { get; set; } = FavoriteCommandDefaults.BackgroundColor;
    public string TextColor { get; set; } = FavoriteCommandDefaults.TextColor;
    public double ButtonWidth { get; set; } = FavoriteCommandDefaults.ButtonWidth;
    public double ButtonHeight { get; set; } = FavoriteCommandDefaults.ButtonHeight;

    /// <summary>Member-wise copy (same Id) for staging an edit before committing it back through the store.</summary>
    public FavoriteCommand Clone() =>
        new()
        {
            Id = Id,
            IsSpacer = IsSpacer,
            Label = Label,
            CommandText = CommandText,
            GroundCommandText = GroundCommandText,
            Category = Category,
            BackgroundColor = BackgroundColor,
            TextColor = TextColor,
            ButtonWidth = ButtonWidth,
            ButtonHeight = ButtonHeight,
        };
}

/// <summary>
/// One container of favorites, referencing its members by id in display order. Global, each
/// airport, each scenario, and each user-named set are all containers of this one shape.
/// </summary>
public sealed class FavoriteSet
{
    public string Id { get; set; } = "";
    public FavoriteSetKind Kind { get; set; } = FavoriteSetKind.Named;

    /// <summary>Airport id (Airport kind) or scenario id (Scenario kind); null for Global/Named.</summary>
    public string? Key { get; set; }

    /// <summary>User-facing name: the set name (Named), the scenario display name (Scenario); unused otherwise.</summary>
    public string Name { get; set; } = "";

    public List<string> FavoriteIds { get; set; } = [];

    [JsonIgnore]
    public string DisplayName =>
        Kind switch
        {
            FavoriteSetKind.Global => "Global",
            FavoriteSetKind.Airport => $"Airport ({Key})",
            FavoriteSetKind.Scenario => $"Scenario ({Name})",
            _ => Name,
        };
}

/// <summary>One button slot of the composed favorites display: the favorite plus the container it is shown for.</summary>
public sealed record FavoriteDisplayEntry(FavoriteCommand Favorite, string SetId);

/// <summary>
/// File-per-entity store for favorites and their sets under <c>%LOCALAPPDATA%/yaat/favorites/</c>:
/// <c>commands/[Label].{id}.json</c> and <c>sets/[Name].{id}.json</c>. Filenames carry the sanitized
/// label/name so users can identify files at a glance, plus the id so they stay unique; the id
/// inside the json is authoritative. All state loads at construction; every mutation rewrites the
/// affected file(s) and raises <see cref="Changed"/>.
/// </summary>
public sealed class FavoriteStore
{
    private static readonly ILogger Log = AppLog.CreateLogger<FavoriteStore>();

    // Serializes file IO across instances (parallel tests share YAAT_APPDATA_DIR per process).
    private static readonly object FileLock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultRootDir => YaatPaths.Combine("favorites");

    private readonly string _commandsDir;
    private readonly string _setsDir;
    private readonly Dictionary<string, FavoriteCommand> _favorites = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FavoriteSet> _sets = [];
    private readonly Dictionary<string, string> _favoriteFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _setFiles = new(StringComparer.OrdinalIgnoreCase);

    public FavoriteStore(string rootDir)
    {
        _commandsDir = Path.Combine(rootDir, "commands");
        _setsDir = Path.Combine(rootDir, "sets");
        LoadedFromEmpty = !Directory.Exists(_commandsDir) && !Directory.Exists(_setsDir);
        lock (FileLock)
        {
            Directory.CreateDirectory(_commandsDir);
            Directory.CreateDirectory(_setsDir);
        }
        LoadAll();
        EnsureGlobalSet();
    }

    /// <summary>True when neither storage directory existed at construction — the trigger for the one-time legacy migration.</summary>
    public bool LoadedFromEmpty { get; }

    /// <summary>Raised after every mutation that changed an entity or a membership list.</summary>
    public event Action? Changed;

    public IReadOnlyCollection<FavoriteCommand> AllFavorites => _favorites.Values;

    /// <summary>All sets in editor display order: Global, airports by key, scenarios by name, named sets by name.</summary>
    public IReadOnlyList<FavoriteSet> OrderedSets =>
        _sets
            .OrderBy(s => s.Kind)
            .ThenBy(s => s.Key ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public FavoriteSet GlobalSet => _sets.First(s => s.Kind == FavoriteSetKind.Global);

    public FavoriteCommand? GetFavorite(string favoriteId) => _favorites.GetValueOrDefault(favoriteId);

    public FavoriteSet? GetSet(string setId) => _sets.FirstOrDefault(s => string.Equals(s.Id, setId, StringComparison.OrdinalIgnoreCase));

    public FavoriteSet? FindAirportSet(string airportId) =>
        _sets.FirstOrDefault(s =>
            (s.Kind == FavoriteSetKind.Airport) && string.Equals(s.Key, NormalizeAirportId(airportId), StringComparison.Ordinal)
        );

    public FavoriteSet? FindScenarioSet(string scenarioId) =>
        _sets.FirstOrDefault(s => (s.Kind == FavoriteSetKind.Scenario) && string.Equals(s.Key, scenarioId, StringComparison.Ordinal));

    public FavoriteSet? FindNamedSet(string name) =>
        _sets.FirstOrDefault(s => (s.Kind == FavoriteSetKind.Named) && string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string? NormalizeAirportId(string? airportId)
    {
        var trimmed = airportId?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.ToUpperInvariant();
    }

    public FavoriteSet GetOrCreateAirportSet(string airportId)
    {
        var existing = FindAirportSet(airportId);
        if (existing is not null)
        {
            return existing;
        }

        var set = new FavoriteSet
        {
            Id = NewSetId(),
            Kind = FavoriteSetKind.Airport,
            Key = NormalizeAirportId(airportId),
        };
        _sets.Add(set);
        SaveSetFile(set);
        RaiseChanged();
        return set;
    }

    public FavoriteSet GetOrCreateScenarioSet(string scenarioId, string displayName)
    {
        var existing = FindScenarioSet(scenarioId);
        if (existing is not null)
        {
            // The display name travels with whatever scenario is active when favorites are saved
            // to it; refresh it when a better name than the migration's id-fallback shows up.
            if (!string.IsNullOrWhiteSpace(displayName) && !string.Equals(existing.Name, displayName, StringComparison.Ordinal))
            {
                existing.Name = displayName;
                SaveSetFile(existing);
                RaiseChanged();
            }
            return existing;
        }

        var set = new FavoriteSet
        {
            Id = NewSetId(),
            Kind = FavoriteSetKind.Scenario,
            Key = scenarioId,
            Name = string.IsNullOrWhiteSpace(displayName) ? scenarioId : displayName,
        };
        _sets.Add(set);
        SaveSetFile(set);
        RaiseChanged();
        return set;
    }

    /// <summary>Returns the new set, or null on a blank name or a case-insensitive collision with another named set.</summary>
    public FavoriteSet? CreateNamedSet(string name)
    {
        var trimmed = name.Trim();
        if (string.IsNullOrEmpty(trimmed) || FindNamedSet(trimmed) is not null)
        {
            return null;
        }

        var set = new FavoriteSet
        {
            Id = NewSetId(),
            Kind = FavoriteSetKind.Named,
            Name = trimmed,
        };
        _sets.Add(set);
        SaveSetFile(set);
        RaiseChanged();
        return set;
    }

    /// <summary>Returns false when the set is missing/not Named, the new name is blank, or it collides with another named set.</summary>
    public bool RenameNamedSet(string setId, string newName)
    {
        var set = GetSet(setId);
        var trimmed = newName.Trim();
        if (set is null || set.Kind != FavoriteSetKind.Named || string.IsNullOrEmpty(trimmed))
        {
            return false;
        }

        var collision = FindNamedSet(trimmed);
        if (collision is not null && !string.Equals(collision.Id, set.Id, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        set.Name = trimmed;
        SaveSetFile(set);
        RaiseChanged();
        return true;
    }

    /// <summary>Deletes the container (memberships only — the favorite entities survive). Global cannot be deleted.</summary>
    public bool DeleteSet(string setId)
    {
        var set = GetSet(setId);
        if (set is null || set.Kind == FavoriteSetKind.Global)
        {
            return false;
        }

        _sets.Remove(set);
        DeleteFile(_setFiles, set.Id);
        RaiseChanged();
        return true;
    }

    /// <summary>Adds a new favorite or updates the entity with the same id, renaming its file when the label changed.</summary>
    public void SaveFavorite(FavoriteCommand favorite)
    {
        if (string.IsNullOrWhiteSpace(favorite.Id))
        {
            favorite.Id = NewFavoriteId();
        }

        _favorites[favorite.Id] = favorite;
        SaveFavoriteFile(favorite);
        RaiseChanged();
    }

    /// <summary>Deletes the entity everywhere: its file plus its membership in every set.</summary>
    public bool DeleteFavorite(string favoriteId)
    {
        if (!_favorites.Remove(favoriteId))
        {
            return false;
        }

        foreach (var set in _sets)
        {
            if (set.FavoriteIds.RemoveAll(id => string.Equals(id, favoriteId, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                SaveSetFile(set);
            }
        }

        DeleteFile(_favoriteFiles, favoriteId);
        RaiseChanged();
        return true;
    }

    /// <summary>Replaces a set's ordered membership list. Unknown favorite ids are dropped; duplicates collapse to the first occurrence.</summary>
    public void ReplaceSetFavorites(string setId, List<string> favoriteIds)
    {
        var set = GetSet(setId);
        if (set is null)
        {
            Log.LogWarning("ReplaceSetFavorites: unknown set id {SetId}", setId);
            return;
        }

        set.FavoriteIds = NormalizeMembership(favoriteIds);
        SaveSetFile(set);
        RaiseChanged();
    }

    /// <summary>Appends the favorite to the set (no-op when already a member).</summary>
    public void AddToSet(string setId, string favoriteId)
    {
        InsertInSet(setId, favoriteId, int.MaxValue);
    }

    /// <summary>Inserts the favorite at the given position in the set's order (clamped; no-op when already a member).</summary>
    public void InsertInSet(string setId, string favoriteId, int index)
    {
        var set = GetSet(setId);
        if (set is null || !_favorites.ContainsKey(favoriteId))
        {
            Log.LogWarning("InsertInSet: unknown set {SetId} or favorite {FavoriteId}", setId, favoriteId);
            return;
        }

        if (set.FavoriteIds.Contains(favoriteId, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        set.FavoriteIds.Insert(Math.Clamp(index, 0, set.FavoriteIds.Count), favoriteId);
        SaveSetFile(set);
        RaiseChanged();
    }

    public void RemoveFromSet(string setId, string favoriteId)
    {
        var set = GetSet(setId);
        if (set is null || set.FavoriteIds.RemoveAll(id => string.Equals(id, favoriteId, StringComparison.OrdinalIgnoreCase)) == 0)
        {
            return;
        }

        SaveSetFile(set);
        RaiseChanged();
    }

    /// <summary>The set's favorites resolved in order (silently skipping ids whose entity is gone).</summary>
    public List<FavoriteCommand> GetSetFavorites(string setId)
    {
        var set = GetSet(setId);
        return set is null ? [] : set.FavoriteIds.Select(GetFavorite).Where(f => f is not null).Cast<FavoriteCommand>().ToList();
    }

    /// <summary>Favorites that are not a member of any set, sorted by label (the editor's "Not in any set" view).</summary>
    public List<FavoriteCommand> GetOrphanFavorites()
    {
        var referenced = new HashSet<string>(_sets.SelectMany(s => s.FavoriteIds), StringComparer.OrdinalIgnoreCase);
        return _favorites.Values.Where(f => !referenced.Contains(f.Id)).OrderBy(f => f.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Ids of every set the favorite is a member of.</summary>
    public List<string> GetMembershipSetIds(string favoriteId)
    {
        return _sets.Where(s => s.FavoriteIds.Contains(favoriteId, StringComparer.OrdinalIgnoreCase)).Select(s => s.Id).ToList();
    }

    /// <summary>
    /// Builds the display list: each visible container's ordered block in full — Global, the active
    /// airport's set, the active scenario's set, then the loaded named sets in load order. A favorite
    /// in several visible containers appears once per container (same entity each time).
    /// </summary>
    public List<FavoriteDisplayEntry> ComposeDisplay(string? activeScenarioId, string? activeAirportId, IReadOnlyList<string> loadedSetIds)
    {
        var visible = new List<FavoriteSet> { GlobalSet };
        if (NormalizeAirportId(activeAirportId) is { } airport && FindAirportSet(airport) is { } airportSet)
        {
            visible.Add(airportSet);
        }

        if (activeScenarioId is not null && FindScenarioSet(activeScenarioId) is { } scenarioSet)
        {
            visible.Add(scenarioSet);
        }

        foreach (var setId in loadedSetIds)
        {
            if (GetSet(setId) is { Kind: FavoriteSetKind.Named } named)
            {
                visible.Add(named);
            }
        }

        return visible.SelectMany(set => GetSetFavorites(set.Id).Select(f => new FavoriteDisplayEntry(f, set.Id))).ToList();
    }

    /// <summary>Appends the given favorites to the set, skipping ids already present or unknown (import merge path).</summary>
    internal void AppendToSet(string setId, IEnumerable<string> favoriteIds)
    {
        var set = GetSet(setId);
        if (set is null)
        {
            return;
        }

        var merged = NormalizeMembership(set.FavoriteIds.Concat(favoriteIds));
        if (merged.SequenceEqual(set.FavoriteIds))
        {
            return;
        }

        set.FavoriteIds = merged;
        SaveSetFile(set);
        RaiseChanged();
    }

    /// <summary>
    /// Import path for a named set: the same id updates that set in place (name refreshed, membership
    /// replaced); a new id becomes a new named set keeping the incoming id so re-imports round-trip.
    /// Display-name collisions with a different existing set auto-suffix (" (2)", " (3)", …).
    /// </summary>
    internal (FavoriteSet Set, bool Added) UpsertImportedNamedSet(FavoriteSet incoming)
    {
        var existing = GetSet(incoming.Id);
        if (existing is { Kind: FavoriteSetKind.Named })
        {
            existing.Name = ResolveImportedSetName(incoming.Name, existing.Id);
            existing.FavoriteIds = NormalizeMembership(incoming.FavoriteIds);
            SaveSetFile(existing);
            RaiseChanged();
            return (existing, false);
        }

        var set = new FavoriteSet
        {
            Id = (string.IsNullOrWhiteSpace(incoming.Id) || existing is not null) ? NewSetId() : incoming.Id,
            Kind = FavoriteSetKind.Named,
            Name = ResolveImportedSetName(incoming.Name, selfId: null),
            FavoriteIds = NormalizeMembership(incoming.FavoriteIds),
        };
        _sets.Add(set);
        SaveSetFile(set);
        RaiseChanged();
        return (set, true);
    }

    private string ResolveImportedSetName(string name, string? selfId)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? "Imported set" : name.Trim();
        var candidate = baseName;
        var suffix = 2;
        while (FindNamedSet(candidate) is { } clash && !string.Equals(clash.Id, selfId, StringComparison.OrdinalIgnoreCase))
        {
            candidate = $"{baseName} ({suffix})";
            suffix++;
        }
        return candidate;
    }

    /// <summary>Generates an unused 8-hex favorite id.</summary>
    public string NewFavoriteId() => NewId(_favorites.ContainsKey);

    /// <summary>Generates an unused 8-hex set id.</summary>
    public string NewSetId() => NewId(id => GetSet(id) is not null);

    private static string NewId(Func<string, bool> exists)
    {
        while (true)
        {
            var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
            if (!exists(id))
            {
                return id;
            }
        }
    }

    // The Windows-invalid set (a superset of Unix's), pinned explicitly so filenames come out
    // identical on every OS — favorites directories and export zips travel between machines.
    private static readonly char[] InvalidFileNameChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>
    /// Turns a label/name into a filesystem-legal filename stem: invalid and control characters
    /// become underscores, surrounding whitespace/dots go, and long names are capped. Blank input
    /// falls back so a spacer still gets an identifiable file.
    /// </summary>
    internal static string SanitizeFileName(string name, string fallback)
    {
        var chars = name.Select(c => (InvalidFileNameChars.Contains(c) || char.IsControl(c)) ? '_' : c).ToArray();
        var sanitized = new string(chars).Trim().Trim('.');
        if (sanitized.Length > 60)
        {
            sanitized = sanitized[..60].TrimEnd().TrimEnd('.');
        }

        return sanitized.Length == 0 ? fallback : sanitized;
    }

    private List<string> NormalizeMembership(IEnumerable<string> favoriteIds)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var id in favoriteIds)
        {
            if (_favorites.ContainsKey(id) && seen.Add(id))
            {
                result.Add(id);
            }
        }
        return result;
    }

    private void EnsureGlobalSet()
    {
        if (_sets.Any(s => s.Kind == FavoriteSetKind.Global))
        {
            return;
        }

        var global = new FavoriteSet
        {
            Id = NewSetId(),
            Kind = FavoriteSetKind.Global,
            Name = "Global",
        };
        _sets.Add(global);
        SaveSetFile(global);
    }

    private void LoadAll()
    {
        foreach (var path in EnumerateJsonFiles(_commandsDir))
        {
            var favorite = ReadEntity<FavoriteCommand>(path);
            if (favorite is null || string.IsNullOrWhiteSpace(favorite.Id) || !_favorites.TryAdd(favorite.Id, favorite))
            {
                Log.LogWarning("Skipping favorite file {Path}: unreadable, missing id, or duplicate id", path);
                continue;
            }
            _favoriteFiles[favorite.Id] = path;
        }

        foreach (var path in EnumerateJsonFiles(_setsDir))
        {
            var set = ReadEntity<FavoriteSet>(path);
            var duplicate =
                set is not null
                && (GetSet(set.Id) is not null || (set.Kind == FavoriteSetKind.Global && _sets.Any(s => s.Kind == FavoriteSetKind.Global)));
            if (set is null || string.IsNullOrWhiteSpace(set.Id) || duplicate)
            {
                Log.LogWarning("Skipping set file {Path}: unreadable, missing id, or duplicate", path);
                continue;
            }

            var known = set.FavoriteIds.Where(id => _favorites.ContainsKey(id)).ToList();
            if (known.Count != set.FavoriteIds.Count)
            {
                Log.LogWarning("Set {Name} referenced {Count} missing favorite id(s); pruned", set.DisplayName, set.FavoriteIds.Count - known.Count);
                set.FavoriteIds = known;
            }

            _sets.Add(set);
            _setFiles[set.Id] = path;
        }
    }

    private static IEnumerable<string> EnumerateJsonFiles(string dir)
    {
        lock (FileLock)
        {
            return Directory.EnumerateFiles(dir, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    private static T? ReadEntity<T>(string path)
        where T : class
    {
        try
        {
            string json;
            lock (FileLock)
            {
                json = File.ReadAllText(path);
            }
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.LogWarning(ex, "Could not read favorite store file {Path}", path);
            return null;
        }
    }

    private void SaveFavoriteFile(FavoriteCommand favorite)
    {
        var stem = SanitizeFileName(favorite.IsSpacer ? "blank" : favorite.Label, "favorite");
        WriteEntityFile(_favoriteFiles, favorite.Id, _commandsDir, stem, favorite);
    }

    private void SaveSetFile(FavoriteSet set)
    {
        WriteEntityFile(_setFiles, set.Id, _setsDir, SanitizeFileName(set.DisplayName, "set"), set);
    }

    private void WriteEntityFile<T>(Dictionary<string, string> files, string id, string dir, string stem, T entity)
    {
        var path = Path.Combine(dir, $"{stem}.{id}.json");
        var json = JsonSerializer.Serialize(entity, JsonOptions);
        lock (FileLock)
        {
            var tmpPath = path + ".tmp";
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, path, overwrite: true);
            // A label/name change moves the entity to a new filename; drop the old file.
            if (
                files.TryGetValue(id, out var previous)
                && !string.Equals(previous, path, StringComparison.OrdinalIgnoreCase)
                && File.Exists(previous)
            )
            {
                File.Delete(previous);
            }
        }
        files[id] = path;
    }

    private void DeleteFile(Dictionary<string, string> files, string id)
    {
        if (!files.Remove(id, out var path))
        {
            return;
        }

        lock (FileLock)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException ex)
            {
                Log.LogWarning(ex, "Could not delete favorite store file {Path}", path);
            }
        }
    }

    private void RaiseChanged() => Changed?.Invoke();
}

public enum FavoriteCommandCategory
{
    Air,
    Ground,
    Vehicle,
    Airport,
}

public static class FavoriteCommandDefaults
{
    public const string BackgroundColor = "#F3F3EE";
    public const string TextColor = "#111111";
    public const double ButtonWidth = 118;
    public const double ButtonHeight = 32;
}
