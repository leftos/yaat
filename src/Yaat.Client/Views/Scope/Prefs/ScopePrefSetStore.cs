using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Yaat.Client.Services;
using Yaat.Sim;

namespace Yaat.Client.Views.Scope.Prefs;

/// <summary>
/// Per-facility store for the YAAT Scope pref sets and the Scope's saved current state: the named sets in
/// <c>&lt;root&gt;/prefsets/&lt;facility&gt;.json</c> and the current state in
/// <c>&lt;root&gt;/current/&lt;facility&gt;.json</c>. Each file is an envelope carrying a <c>version</c> beside its
/// <c>sets</c> or <c>state</c>. A version 0 file is saved back at the current version on load; a newer or unrecognised
/// version is read for the fields this build knows, with one warning and no rewrite. Every entry is read on its own, so
/// one malformed set is dropped with a warning while the rest load, and a file that is not valid JSON is copied to
/// <c>.bak</c>. A file whose contents a save cannot read is never written over, so a failed read cannot lose the
/// other sets.
/// </summary>
/// <param name="rootDir">The folder holding the <c>prefsets</c> and <c>current</c> directories, created on write.</param>
/// <param name="log">The logger unreadable and unwritable files are reported to.</param>
public sealed class ScopePrefSetStore(string rootDir, ILogger log)
{
    /// <summary>The file format version this build writes.</summary>
    public const int CurrentVersion = 1;

    // Serializes file IO across instances (parallel tests share processes).
    private static readonly Lock FileLock = new();

    // The names Windows gives a device whatever the folder, so no file can be called these.
    private static readonly HashSet<string> DeviceNames = new(
        [
            "CON",
            "PRN",
            "AUX",
            "NUL",
            "COM1",
            "COM2",
            "COM3",
            "COM4",
            "COM5",
            "COM6",
            "COM7",
            "COM8",
            "COM9",
            "LPT1",
            "LPT2",
            "LPT3",
            "LPT4",
            "LPT5",
            "LPT6",
            "LPT7",
            "LPT8",
            "LPT9",
        ],
        StringComparer.OrdinalIgnoreCase
    );

    /// <summary>The production root: <c>%LOCALAPPDATA%/yaat/scope</c>.</summary>
    public static string DefaultRootDir => YaatPaths.Combine("scope");

    /// <summary>
    /// True when <paramref name="facility"/> can name a facility's files: 1 to 16 letters, digits, <c>_</c> or <c>-</c>,
    /// and not a device name.
    /// </summary>
    /// <param name="facility">The facility id to check.</param>
    public static bool IsSafeFacility(string? facility) => IsSafeToken(facility);

    /// <summary>
    /// True with <paramref name="normalized"/> set to the uppercase form when <paramref name="name"/> is 1 to 16 letters,
    /// digits, <c>_</c> or <c>-</c> and not a device name; false with null otherwise.
    /// </summary>
    /// <param name="name">The name to normalize.</param>
    /// <param name="normalized">The uppercase invariant form, or null when the name is refused.</param>
    public static bool TryNormalizeName(string? name, [NotNullWhen(true)] out string? normalized)
    {
        if (!IsSafeToken(name))
        {
            normalized = null;
            return false;
        }

        normalized = name.ToUpperInvariant();
        return true;
    }

    /// <summary>The facility's pref sets, in file order; empty when the file is missing or cannot be read.</summary>
    /// <param name="facility">The facility whose sets to load.</param>
    /// <exception cref="ArgumentException"><paramref name="facility"/> is not a safe facility id.</exception>
    public IReadOnlyList<ScopePrefSet> LoadSets(string facility)
    {
        EnsureSafeFacility(facility);
        string path = SetsPath(facility);
        lock (FileLock)
        {
            RootRead read = ReadRoot(path);
            JsonObject? root = read.Root;
            if ((read.Outcome != ReadOutcome.Ok) || (root is null))
            {
                return [];
            }

            List<ScopePrefSet> sets = ReadSets(path, root);
            if (read.Version == VersionRead.Rewrite)
            {
                Dictionary<string, JsonElement>? extra = ReadExtra(root, "sets");
                RewriteOnLoad(
                    path,
                    () =>
                        WriteCore(
                            path,
                            new SetsFile
                            {
                                Version = CurrentVersion,
                                Sets = sets,
                                Extra = extra,
                            }
                        )
                );
            }

            return sets;
        }
    }

    /// <summary>The facility's set with <paramref name="name"/>, compared without case, or null when it has none.</summary>
    /// <param name="facility">The facility whose sets to search.</param>
    /// <param name="name">The set name to find.</param>
    /// <exception cref="ArgumentException"><paramref name="facility"/> is not a safe facility id.</exception>
    public ScopePrefSet? FindSet(string facility, string name) => LoadSets(facility).FirstOrDefault(set => NameEquals(set.Name, name));

    /// <summary>
    /// Saves <paramref name="set"/> under its name in its uppercase form, replacing the set of the same name (compared
    /// without case) in place or appending it.
    /// </summary>
    /// <param name="facility">The facility to save the set for.</param>
    /// <param name="set">The set to save; its <see cref="ScopePrefSet.Name"/> must be a usable name.</param>
    /// <exception cref="ArgumentException"><paramref name="facility"/> is not a safe facility id, or the set has no usable name.</exception>
    /// <exception cref="IOException">The file cannot be read or its version is unknown, so writing would lose its contents.</exception>
    public void SaveSet(string facility, ScopePrefSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        EnsureSafeFacility(facility);
        if (!TryNormalizeName(set.Name, out string? normalized))
        {
            throw new ArgumentException($"'{set.Name}' is not a usable pref-set name: 1 to 16 letters, digits, '_' or '-'.", nameof(set));
        }

        ScopePrefSet stored = (set.Name == normalized) ? set : (set with { Name = normalized });
        string path = SetsPath(facility);
        lock (FileLock)
        {
            (JsonObject root, int writeVersion) = ReadForWrite(path);
            List<ScopePrefSet> sets = ReadSets(path, root);
            int index = sets.FindIndex(existing => NameEquals(existing.Name, normalized));
            if (index < 0)
            {
                sets.Add(stored);
            }
            else
            {
                sets[index] = stored;
            }

            WriteLogged(
                path,
                new SetsFile
                {
                    Version = writeVersion,
                    Sets = sets,
                    Extra = ReadExtra(root, "sets"),
                }
            );
        }
    }

    /// <summary>
    /// Renames the set <paramref name="oldName"/> to <paramref name="newName"/> (stored in its uppercase form) keeping its
    /// position; false, changing nothing, when there is no such set or another set already holds the new name.
    /// </summary>
    /// <param name="facility">The facility whose set to rename.</param>
    /// <param name="oldName">The set's current name, compared without case.</param>
    /// <param name="newName">The name to give it; an unusable name is refused.</param>
    /// <exception cref="ArgumentException"><paramref name="facility"/> is not a safe facility id.</exception>
    /// <exception cref="IOException">The file cannot be read or its version is unknown, so writing would lose its contents.</exception>
    public bool RenameSet(string facility, string oldName, string newName)
    {
        EnsureSafeFacility(facility);
        if (!TryNormalizeName(newName, out string? normalized))
        {
            return false;
        }

        string path = SetsPath(facility);
        lock (FileLock)
        {
            (JsonObject root, int writeVersion) = ReadForWrite(path);
            List<ScopePrefSet> sets = ReadSets(path, root);
            int index = sets.FindIndex(set => NameEquals(set.Name, oldName));
            if (index < 0)
            {
                return false;
            }

            int taken = sets.FindIndex(set => NameEquals(set.Name, normalized));
            if ((taken >= 0) && (taken != index))
            {
                return false;
            }

            sets[index] = sets[index] with { Name = normalized };
            WriteLogged(
                path,
                new SetsFile
                {
                    Version = writeVersion,
                    Sets = sets,
                    Extra = ReadExtra(root, "sets"),
                }
            );
            return true;
        }
    }

    /// <summary>Deletes the facility's set <paramref name="name"/>; false when it holds no such set.</summary>
    /// <param name="facility">The facility whose set to delete.</param>
    /// <param name="name">The set name, compared without case.</param>
    /// <exception cref="ArgumentException"><paramref name="facility"/> is not a safe facility id.</exception>
    /// <exception cref="IOException">The file cannot be read or its version is unknown, so writing would lose its contents.</exception>
    public bool DeleteSet(string facility, string name)
    {
        EnsureSafeFacility(facility);
        string path = SetsPath(facility);
        lock (FileLock)
        {
            (JsonObject root, int writeVersion) = ReadForWrite(path);
            List<ScopePrefSet> sets = ReadSets(path, root);
            int index = sets.FindIndex(set => NameEquals(set.Name, name));
            if (index < 0)
            {
                return false;
            }

            sets.RemoveAt(index);
            WriteLogged(
                path,
                new SetsFile
                {
                    Version = writeVersion,
                    Sets = sets,
                    Extra = ReadExtra(root, "sets"),
                }
            );
            return true;
        }
    }

    /// <summary>The facility's saved current state, or null when it has none or it cannot be read.</summary>
    /// <param name="facility">The facility whose current state to load.</param>
    /// <exception cref="ArgumentException"><paramref name="facility"/> is not a safe facility id.</exception>
    public ScopePrefSet? LoadCurrent(string facility)
    {
        EnsureSafeFacility(facility);
        string path = CurrentPath(facility);
        lock (FileLock)
        {
            RootRead read = ReadRoot(path);
            JsonObject? root = read.Root;
            if ((read.Outcome != ReadOutcome.Ok) || (root is null))
            {
                return null;
            }

            ScopePrefSet? state = ReadCurrent(root, path);
            if ((state is not null) && (read.Version == VersionRead.Rewrite))
            {
                Dictionary<string, JsonElement>? extra = ReadExtra(root, "state");
                RewriteOnLoad(
                    path,
                    () =>
                        WriteCore(
                            path,
                            new CurrentFile
                            {
                                Version = CurrentVersion,
                                State = state,
                                Extra = extra,
                            }
                        )
                );
            }

            return state;
        }
    }

    /// <summary>Saves <paramref name="state"/> as the facility's current state, text and name as given.</summary>
    /// <param name="facility">The facility to save the state for.</param>
    /// <param name="state">The Scope's state; its name is the active set's, or null.</param>
    /// <exception cref="ArgumentException"><paramref name="facility"/> is not a safe facility id.</exception>
    /// <exception cref="IOException">The file is corrupt with no backup, or its version is unknown, so writing would lose its contents.</exception>
    public void SaveCurrent(string facility, ScopePrefSet state)
    {
        ArgumentNullException.ThrowIfNull(state);
        EnsureSafeFacility(facility);
        string path = CurrentPath(facility);
        lock (FileLock)
        {
            RootRead read = ReadRoot(path);
            if (read.Outcome == ReadOutcome.CorruptUnbacked)
            {
                throw RefuseWrite(path, "it is not valid JSON and its backup could not be written");
            }

            WriteLogged(
                path,
                new CurrentFile
                {
                    Version = CurrentWriteVersion(read, path),
                    State = state,
                    Extra = null,
                }
            );
        }
    }

    private static bool IsSafeToken([NotNullWhen(true)] string? token) =>
        (token is not null)
        && (token.Length is >= 1 and <= 16)
        && token.All(c => char.IsAsciiLetterOrDigit(c) || (c == '_') || (c == '-'))
        && !DeviceNames.Contains(token);

    private static bool NameEquals(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static void EnsureSafeFacility(string facility)
    {
        if (!IsSafeFacility(facility))
        {
            throw new ArgumentException($"'{facility}' is not a safe facility id: 1 to 16 letters, digits, '_' or '-'.", nameof(facility));
        }
    }

    private string SetsPath(string facility) => Path.Combine(rootDir, "prefsets", $"{facility}.json");

    private string CurrentPath(string facility) => Path.Combine(rootDir, "current", $"{facility}.json");

    /// <summary>
    /// The root a mutating call rewrites, and the version number it writes: a fresh object for a missing or backed-up
    /// file, and the file's own version when that is newer.
    /// </summary>
    private (JsonObject Root, int WriteVersion) ReadForWrite(string path)
    {
        RootRead read = ReadRoot(path);
        switch (read.Outcome)
        {
            case ReadOutcome.Missing:
            case ReadOutcome.Corrupt:
                return (new JsonObject(), CurrentVersion);
            case ReadOutcome.Unreadable:
                throw RefuseWrite(path, "it could not be read");
            case ReadOutcome.CorruptUnbacked:
                throw RefuseWrite(path, "it is not valid JSON and its backup could not be written");
            default:
                break;
        }

        JsonObject? root = read.Root ?? throw RefuseWrite(path, "it could not be read");
        return read.Version switch
        {
            VersionRead.Rewrite or VersionRead.Current => (root, CurrentVersion),
            VersionRead.Newer => (root, read.VersionNumber),
            _ => throw RefuseWrite(path, "its version is not one this build knows"),
        };
    }

    /// <summary>
    /// The version a current-state save writes: the file's own when it is newer, and a refusal when it is not one this
    /// build knows.
    /// </summary>
    private int CurrentWriteVersion(RootRead read, string path)
    {
        if (read.Outcome != ReadOutcome.Ok)
        {
            return CurrentVersion;
        }

        return read.Version switch
        {
            VersionRead.Newer => read.VersionNumber,
            VersionRead.Unknown => throw RefuseWrite(path, "its version is not one this build knows"),
            _ => CurrentVersion,
        };
    }

    /// <summary>The file's root object, its version, and what reading it produced.</summary>
    private RootRead ReadRoot(string path)
    {
        string json;
        try
        {
            if (!File.Exists(path))
            {
                return NoFile(ReadOutcome.Missing);
            }

            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Could not read scope pref file {Path}", path);
            return NoFile(ReadOutcome.Unreadable);
        }

        JsonObject? root;
        int? version;
        try
        {
            root = JsonNode.Parse(json) as JsonObject;

            // The object builds its property map lazily, and a duplicate name throws only then, so it is built here.
            _ = root?.Count;
            version = (root is null) ? null : ReadVersion(root);
        }
        catch (ArgumentException ex)
        {
            return NoFile(MarkCorrupt(path, ex.Message));
        }
        catch (JsonException ex)
        {
            return NoFile(MarkCorrupt(path, ex.Message));
        }

        if (root is null)
        {
            return NoFile(MarkCorrupt(path, "its root is not a JSON object"));
        }

        return new RootRead(ReadOutcome.Ok, root, ReadAtVersion(version, path), version ?? CurrentVersion);
    }

    private static RootRead NoFile(ReadOutcome outcome) => new(outcome, null, VersionRead.Current, CurrentVersion);

    /// <summary>
    /// How this build reads a file of <paramref name="version"/>: only version 0 is rewritten on load, and an
    /// unrecognised version is read for the fields this build knows.
    /// </summary>
    private VersionRead ReadAtVersion(int? version, string path)
    {
        switch (version)
        {
            case 0:
                return VersionRead.Rewrite;
            case CurrentVersion:
                return VersionRead.Current;
            case null:
                log.LogWarning("Scope pref file {Path} has no version this build reads; reading only the fields this build knows", path);
                return VersionRead.Unknown;
            default:
                if (version > CurrentVersion)
                {
                    log.LogWarning(
                        "Scope pref file {Path} is version {Version}, newer than this build's {CurrentVersion}; "
                            + "reading only the fields this build knows",
                        path,
                        version,
                        CurrentVersion
                    );
                    return VersionRead.Newer;
                }

                log.LogWarning("Scope pref file {Path} has no version this build reads; reading only the fields this build knows", path);
                return VersionRead.Unknown;
        }
    }

    /// <summary>The file's version: 0 when it has none or exactly 0, and null when the value is not a version this build can name.</summary>
    private static int? ReadVersion(JsonObject root)
    {
        if (!root.TryGetPropertyValue("version", out JsonNode? node))
        {
            return 0;
        }

        if ((node is JsonValue value) && value.TryGetValue(out int version))
        {
            return (version >= 0) ? version : null;
        }

        return null;
    }

    private List<ScopePrefSet> ReadSets(string path, JsonObject root)
    {
        List<ScopePrefSet> sets = [];
        if (!root.TryGetPropertyValue("sets", out JsonNode? node))
        {
            return sets;
        }

        if (node is not JsonArray array)
        {
            log.LogWarning("The sets of scope pref file {Path} are not a JSON array; reading none", path);
            return sets;
        }

        foreach (JsonNode? entry in array)
        {
            ScopePrefSet? set = ReadSet(path, entry);
            if (set is not null)
            {
                sets.Add(set);
            }
        }

        return sets;
    }

    private ScopePrefSet? ReadCurrent(JsonObject root, string path) =>
        root.TryGetPropertyValue("state", out JsonNode? node) ? ReadSet(path, node) : null;

    private ScopePrefSet? ReadSet(string path, JsonNode? node)
    {
        if (node is null)
        {
            log.LogWarning("Skipping an empty entry in scope pref file {Path}", path);
            return null;
        }

        ScopePrefSet? set;
        try
        {
            set = node.Deserialize<ScopePrefSet>(ScopePrefSet.JsonOptions);
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "Skipping an unreadable entry in scope pref file {Path}", path);
            return null;
        }

        if (set is null)
        {
            log.LogWarning("Skipping an unreadable entry in scope pref file {Path}", path);
        }

        return set;
    }

    /// <summary>The envelope fields this build does not know, so a resave keeps them.</summary>
    private static Dictionary<string, JsonElement>? ReadExtra(JsonObject root, string contentKey)
    {
        Dictionary<string, JsonElement> extra = [];
        foreach ((string key, JsonNode? node) in root)
        {
            if ((key != "version") && (key != contentKey) && (node is not null))
            {
                extra[key] = JsonSerializer.SerializeToElement(node, ScopePrefSet.JsonOptions);
            }
        }

        return (extra.Count > 0) ? extra : null;
    }

    /// <summary>The file's bytes are replaced by <paramref name="envelope"/>; a failed write is logged as an error and rethrown.</summary>
    private void WriteLogged<T>(string path, T envelope)
    {
        try
        {
            WriteCore(path, envelope);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogError(ex, "Could not write scope pref file {Path}", path);
            throw;
        }
    }

    private static void WriteCore<T>(string path, T envelope)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(envelope, ScopePrefSet.JsonOptions));
    }

    /// <summary>A load's version rewrite: a folder that refuses the write leaves the file as it is and says so.</summary>
    private void RewriteOnLoad(string path, Action write)
    {
        try
        {
            write();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Could not save scope pref file {Path} back at version {Version}; reading the file as it is", path, CurrentVersion);
        }
    }

    /// <summary>Copies a corrupt file to <c>.bak</c> and says what reading it produced.</summary>
    private ReadOutcome MarkCorrupt(string path, string reason)
    {
        log.LogWarning(
            "Scope pref file {Path} is not valid JSON ({Reason}); starting empty and keeping a copy at {Backup}",
            path,
            reason,
            path + ".bak"
        );
        try
        {
            File.Copy(path, path + ".bak", overwrite: true);
            return ReadOutcome.Corrupt;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogError(ex, "Could not back up corrupt scope pref file {Path}; refusing to write over the only copy", path);
            return ReadOutcome.CorruptUnbacked;
        }
    }

    private IOException RefuseWrite(string path, string why)
    {
        log.LogError("Refusing to write scope pref file {Path}: {Why}", path, why);
        return new IOException($"Refusing to write scope pref file {path}: {why}; its contents would be lost.");
    }

    /// <summary>What reading one file produced.</summary>
    private enum ReadOutcome
    {
        /// <summary>There is no file.</summary>
        Missing,

        /// <summary>The root object parsed.</summary>
        Ok,

        /// <summary>The file exists but could not be read, so its contents are unknown.</summary>
        Unreadable,

        /// <summary>The file is not valid JSON and its bytes are copied to <c>.bak</c>.</summary>
        Corrupt,

        /// <summary>The file is not valid JSON and the <c>.bak</c> copy failed, so its bytes exist only in the file.</summary>
        CorruptUnbacked,
    }

    /// <summary>How this build reads the version a file carries.</summary>
    private enum VersionRead
    {
        /// <summary>Version 0: the current shape without the field, saved back at <see cref="CurrentVersion"/> on load.</summary>
        Rewrite,

        /// <summary>The version this build writes.</summary>
        Current,

        /// <summary>A newer version: read for the fields this build knows, never rewritten on load, and kept when it is written.</summary>
        Newer,

        /// <summary>Not a version this build can name: read for the fields it knows, and refused when written.</summary>
        Unknown,
    }

    /// <summary>One file read: what it produced, its root when it parsed, and the version it carries.</summary>
    private sealed record RootRead(ReadOutcome Outcome, JsonObject? Root, VersionRead Version, int VersionNumber);

    /// <summary>The pref-sets file: a version and the named sets in file order.</summary>
    private sealed record SetsFile
    {
        public int Version { get; init; } = CurrentVersion;

        public IReadOnlyList<ScopePrefSet> Sets { get; init; } = [];

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; init; }
    }

    /// <summary>The current-state file: a version and the Scope's saved state.</summary>
    private sealed record CurrentFile
    {
        public int Version { get; init; } = CurrentVersion;

        public ScopePrefSet? State { get; init; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; init; }
    }
}
