using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Authoring;

/// <summary>
/// Keeps an ARTCC's training scenarios on disk under <c>{root}/{artccId}/</c>: the server mirror in
/// <c>server/{id}.json</c> (the exact text data-api sent), working copies in <c>local/{id}.json</c> each with its
/// <c>local/{id}.meta.json</c> sidecar (origin id, base version, last export), and <c>index.json</c>, a rebuildable cache
/// of the mirror's names, versions and gone-from-server flags. A refresh never deletes or overwrites a working copy;
/// every corrupt or unreadable file is reported by name, never skipped silently. The root is never under <c>cache/</c>.
/// </summary>
/// <remarks>
/// A mirror's version is the SHA-256 of its text. A refreshed server body replaces the mirror only when it differs from
/// it as JSON (key order and whitespace ignored), so a re-ordered body marks nothing. A working copy is modified when its
/// content differs from the content it started from, and server-changed when its base version is not the mirror's.
/// </remarks>
public sealed partial class LocalScenarioStore
{
    private const string IndexFileName = "index.json";
    private const string ServerFolder = "server";
    private const string LocalFolder = "local";
    private const string SidecarSuffix = ".meta.json";

    private static readonly ILogger Log = SimLog.CreateLogger<LocalScenarioStore>();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    // One lock per root folder, shared by every store over it, because the index is a file they all read and rewrite.
    private static readonly ConcurrentDictionary<string, Lock> RootLocks = new(StringComparer.OrdinalIgnoreCase);

    private readonly Lock _gate;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalScenarioStore"/> class over <c>scenarios/</c> under the YAAT
    /// app-data folder.
    /// </summary>
    public LocalScenarioStore()
        : this(rootDir: null) { }

    /// <summary>Initializes a new instance of the <see cref="LocalScenarioStore"/> class.</summary>
    /// <param name="rootDir">The store's root folder, or null for <c>scenarios/</c> under the YAAT app-data folder.</param>
    /// <exception cref="InvalidOperationException">The root is under the app-data <c>cache/</c> folder.</exception>
    public LocalScenarioStore(string? rootDir)
    {
        string root = Path.GetFullPath(rootDir ?? YaatPaths.Combine("scenarios"));
        string cache = Path.GetFullPath(YaatPaths.Combine("cache"));
        if (IsUnder(root, cache))
        {
            throw new InvalidOperationException(
                $"The scenario store root {root} is under the cache folder {cache}; scenarios are user data and never live under cache."
            );
        }

        RootDirectory = root;
        _gate = RootLocks.GetOrAdd(Path.TrimEndingDirectorySeparator(root), _ => new Lock());
    }

    /// <summary>Gets the store's root folder; each ARTCC has a folder under it.</summary>
    public string RootDirectory { get; }

    /// <summary>
    /// Whether <paramref name="id"/> is a valid scenario or ARTCC id: letters, digits, '_' and '-' only, and not a
    /// Windows reserved device name (CON, PRN, AUX, NUL, COM1-9, LPT1-9, any case).
    /// </summary>
    /// <param name="id">The id to check.</param>
    /// <returns>True when the id can name a file or folder of the store.</returns>
    public static bool IsValidId(string? id) => StoreFiles.IsValidId(id);

    /// <summary>The full path of a scenario's server mirror file, whether or not it exists.</summary>
    /// <param name="artccId">The ARTCC.</param>
    /// <param name="id">The scenario id.</param>
    /// <returns>The path of <c>{root}/{artccId}/server/{id}.json</c>.</returns>
    /// <exception cref="ArgumentException"><paramref name="artccId"/> or <paramref name="id"/> is not a valid id.</exception>
    public string GetMirrorPath(string artccId, string id)
    {
        string dir = ArtccDirectory(artccId);
        RequireValidId(id);
        return MirrorPath(dir, id);
    }

    /// <summary>The version of a mirror text: the lower-case hex SHA-256 of its UTF-8 bytes.</summary>
    /// <param name="text">The mirror text.</param>
    /// <returns>The version.</returns>
    public static string ComputeVersion(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>
    /// Applies one server body for <paramref name="id"/>, the refresh step: a new id writes the mirror, a body equal to
    /// the mirror as JSON leaves it untouched, and a changed body replaces the mirror atomically with a new version.
    /// Working copies are never touched; one derived from <paramref name="id"/> is reported server-changed.
    /// </summary>
    /// <param name="artccId">The ARTCC the body was listed under; the folder it is kept in.</param>
    /// <param name="id">The scenario id the body was fetched for.</param>
    /// <param name="body">The body text data-api sent.</param>
    /// <returns>What the body did to the store, and any file problem found on the way.</returns>
    /// <exception cref="ArgumentException"><paramref name="artccId"/> is not a valid id.</exception>
    public ServerApplyResult ApplyServerBody(string artccId, string id, string body)
    {
        string dir = ArtccDirectory(artccId);
        ArgumentNullException.ThrowIfNull(body);
        if (!StoreFiles.IsValidId(id))
        {
            return Rejected("invalid id");
        }

        string? reason = CheckScenario(body, out JsonObject? scenario);
        if (reason is not null)
        {
            return Rejected(reason);
        }

        string bodyId = TextOf(scenario!, "id");
        if (!string.Equals(bodyId, id, StringComparison.Ordinal))
        {
            return Rejected($"not a scenario: its id is {bodyId}, not {id}");
        }

        LogArtccMismatch(artccId, id, scenario!);
        lock (_gate)
        {
            return ApplyLocked(dir, id, body, scenario!);
        }
    }

    /// <summary>
    /// Flags every mirror entry whose id is not in <paramref name="serverIds"/> as gone from the server, and clears the
    /// flag on the others. Nothing is deleted.
    /// </summary>
    /// <param name="artccId">The ARTCC.</param>
    /// <param name="serverIds">The ids the server lists now.</param>
    /// <returns>The ids gone from the server, and any file problem found on the way.</returns>
    public MarkGoneResult MarkGoneFromServer(string artccId, IReadOnlyCollection<string> serverIds)
    {
        string dir = ArtccDirectory(artccId);
        ArgumentNullException.ThrowIfNull(serverIds);
        var present = new HashSet<string>(serverIds, StringComparer.Ordinal);
        lock (_gate)
        {
            var problems = new List<StoreProblem>();
            StoreIndexFile index = LoadIndex(dir, problems, out _);
            var missing = new List<string>();
            bool changed = false;
            for (int i = 0; i < index.Mirrors.Count; i++)
            {
                MirrorEntry entry = index.Mirrors[i];
                bool gone = !present.Contains(entry.Id);
                if (gone)
                {
                    missing.Add(entry.Id);
                }

                if (entry.GoneFromServer != gone)
                {
                    index.Mirrors[i] = entry with { GoneFromServer = gone };
                    changed = true;
                }
            }

            if (changed)
            {
                SaveIndex(dir, index);
            }

            return new MarkGoneResult(missing, problems);
        }
    }

    /// <summary>Starts a working copy of a server scenario: the mirror's text, its version as the copy's base.</summary>
    /// <param name="artccId">The ARTCC.</param>
    /// <param name="serverId">The mirrored scenario's id; the copy takes the same id.</param>
    /// <exception cref="InvalidOperationException">There is no readable mirror of the scenario, or the copy exists.</exception>
    public void CreateWorkingCopy(string artccId, string serverId)
    {
        string dir = ArtccDirectory(artccId);
        RequireValidId(serverId);
        lock (_gate)
        {
            string mirrorPath = MirrorPath(dir, serverId);
            if (!File.Exists(mirrorPath))
            {
                throw new InvalidOperationException($"There is no server copy of {serverId} at {mirrorPath}.");
            }

            string text = File.ReadAllText(mirrorPath);
            string? reason = CheckScenario(text, out JsonObject? scenario);
            if (reason is not null)
            {
                throw new InvalidOperationException($"The server copy at {mirrorPath} is corrupt: {reason}.");
            }

            string localPath = LocalPath(dir, serverId);
            if (File.Exists(localPath))
            {
                throw new InvalidOperationException($"A working copy already exists at {localPath}.");
            }

            // The sidecar goes first: a crash between the two leaves an orphan sidecar, reported and overwritten by the
            // next attempt, rather than a copy that has lost its origin.
            WriteSidecar(dir, serverId, new WorkingCopySidecar(serverId, ComputeVersion(text), ComputeContentHash(scenario!), null));
            StoreFiles.WriteAtomically(localPath, text);
        }
    }

    /// <summary>
    /// Writes a working copy's text, keeping its sidecar; a copy with no sidecar gets a local-only one (no origin id).
    /// </summary>
    /// <param name="artccId">The ARTCC.</param>
    /// <param name="id">The working copy's id.</param>
    /// <param name="json">The scenario JSON to keep.</param>
    public void SaveWorkingCopy(string artccId, string id, string json)
    {
        string dir = ArtccDirectory(artccId);
        RequireValidId(id);
        ArgumentNullException.ThrowIfNull(json);
        lock (_gate)
        {
            StoreFiles.WriteAtomically(LocalPath(dir, id), json);
            if (!File.Exists(SidecarPath(dir, id)))
            {
                WriteSidecar(dir, id, new WorkingCopySidecar(null, null, null, null));
            }
        }
    }

    /// <summary>
    /// Lists an ARTCC's mirror and working copies with their state, rebuilding a missing or corrupt index (a corrupt one
    /// is kept as <c>index.json.corrupt-{UTC stamp}</c>). Each mirror file is checked against the index: a listed file
    /// that is missing, unreadable, not a scenario or holding another scenario's id is listed
    /// <see cref="MirrorListing.Corrupt"/>, an entry whose version is behind its file's text is corrected, and a readable
    /// file the index lacks is added to it; each problem is reported.
    /// </summary>
    /// <param name="artccId">The ARTCC.</param>
    /// <returns>The listing, whether the index was rebuilt, and every file problem found.</returns>
    public ArtccStoreListing Load(string artccId)
    {
        string dir = ArtccDirectory(artccId);
        lock (_gate)
        {
            var problems = new List<StoreProblem>();
            StoreIndexFile index = LoadIndex(dir, problems, out bool rebuilt);
            List<MirrorListing> mirrors = rebuilt
                ? [.. index.Mirrors.Select(m => new MirrorListing(m, false))]
                : ReconcileMirrors(dir, index, problems);
            List<WorkingCopyEntry> copies = ListWorkingCopies(dir, index, problems);
            return new ArtccStoreListing(artccId, [.. mirrors.OrderBy(m => m.Mirror.Id, StringComparer.Ordinal)], copies, rebuilt, problems);
        }
    }

    // Checks every indexed mirror's file, correcting an entry whose version is behind the file's text, and adds the
    // readable mirror files the index lacks to it; the index is saved once when anything in it changed.
    private static List<MirrorListing> ReconcileMirrors(string dir, StoreIndexFile index, List<StoreProblem> problems)
    {
        var listed = new List<MirrorListing>();
        bool changed = false;
        for (int i = 0; i < index.Mirrors.Count; i++)
        {
            MirrorEntry entry = index.Mirrors[i];
            string? reason = CheckIndexedMirror(MirrorPath(dir, entry.Id), entry.Id, out string? version);
            if (reason is not null)
            {
                problems.Add(new StoreProblem($"{ServerFolder}/{entry.Id}.json", reason));
            }
            else if (!string.Equals(version, entry.Version, StringComparison.Ordinal))
            {
                LogVersionCorrected(entry.Id);
                entry = entry with { Version = version! };
                index.Mirrors[i] = entry;
                changed = true;
            }

            listed.Add(new MirrorListing(entry, reason is not null));
        }

        var known = new HashSet<string>(index.Mirrors.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
        List<MirrorEntry> unlisted = ScanServerFolder(dir, known, problems, reportAdded: true);
        index.Mirrors.AddRange(unlisted);
        listed.AddRange(unlisted.Select(e => new MirrorListing(e, false)));
        if (changed || (unlisted.Count > 0))
        {
            SaveIndex(dir, index);
        }

        return listed;
    }

    // Why an indexed mirror cannot be listed, or null with the version of its text when it can.
    private static string? CheckIndexedMirror(string path, string id, out string? version)
    {
        version = null;
        if (!File.Exists(path))
        {
            return "missing: the index lists it but the file is gone";
        }

        if (!TryReadMirror(path, id, out string? text, out _, out string? reason))
        {
            return reason;
        }

        version = ComputeVersion(text);
        return null;
    }

    // Whether the mirror file is a scenario whose own id is id: its text and scenario when it is, else why it cannot be
    // used, the one reason every reader of a mirror reports.
    private static bool TryReadMirror(
        string path,
        string id,
        [NotNullWhen(true)] out string? text,
        [NotNullWhen(true)] out JsonObject? scenario,
        [NotNullWhen(false)] out string? reason
    )
    {
        scenario = null;
        reason = null;
        text = TryReadText(path, out string? failure);
        if (text is null)
        {
            // TryReadText sets the failure whenever it returns null.
            reason = failure!;
            return false;
        }

        string? corrupt = CheckScenario(text, out JsonObject? parsed);
        if (corrupt is not null)
        {
            reason = corrupt;
            return false;
        }

        JsonObject mirror = parsed!;
        string bodyId = TextOf(mirror, "id");
        if (!string.Equals(bodyId, id, StringComparison.Ordinal))
        {
            reason = $"holds scenario {bodyId}, not {id}";
            return false;
        }

        scenario = mirror;
        return true;
    }

    // The file's text, or null with the reason when it is locked or access is denied.
    private static string? TryReadText(string path, out string? failure)
    {
        failure = null;
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (Log.IsEnabled(LogLevel.Warning))
            {
                Log.LogWarning(ex, "Mirror file {Path} could not be read", path);
            }

            failure = "unreadable: " + ex.Message;
            return null;
        }
    }

    private static void LogVersionCorrected(string id)
    {
        if (Log.IsEnabled(LogLevel.Information))
        {
            Log.LogInformation("The index version of mirror {Id} was behind its file; corrected", id);
        }
    }

    private static ServerApplyResult ApplyLocked(string dir, string id, string body, JsonObject scenario)
    {
        var problems = new List<StoreProblem>();
        StoreIndexFile index = LoadIndex(dir, problems, out _);
        string mirrorPath = MirrorPath(dir, id);
        (bool exists, string existingText, JsonObject? existing) = ReadMirror(mirrorPath, id, problems);
        var entry = new MirrorEntry(id, TextOf(scenario, "name"), ComputeVersion(body), false);
        if ((existing is not null) && JsonNode.DeepEquals(existing, scenario))
        {
            if (Upsert(index, entry with { Version = ComputeVersion(existingText) }))
            {
                SaveIndex(dir, index);
            }

            return new ServerApplyResult(ServerApplyOutcome.Unchanged, null, problems);
        }

        StoreFiles.WriteAtomically(mirrorPath, body);
        Upsert(index, entry);
        SaveIndex(dir, index);
        if (!exists)
        {
            return new ServerApplyResult(ServerApplyOutcome.Added, null, problems);
        }

        bool copyBehind = HasCopyBehind(dir, id, entry.Version, problems);
        return new ServerApplyResult(copyBehind ? ServerApplyOutcome.ServerChanged : ServerApplyOutcome.Updated, null, problems);
    }

    private static (bool Exists, string Text, JsonObject? Scenario) ReadMirror(string mirrorPath, string id, List<StoreProblem> problems)
    {
        if (!File.Exists(mirrorPath))
        {
            return (false, "", null);
        }

        string text = File.ReadAllText(mirrorPath);
        string? reason = CheckScenario(text, out JsonObject? scenario);
        if (reason is not null)
        {
            problems.Add(new StoreProblem($"{ServerFolder}/{id}.json", reason + "; replaced by the fetched body"));
        }

        return (true, text, scenario);
    }

    private static bool Upsert(StoreIndexFile index, MirrorEntry entry)
    {
        int at = index.Mirrors.FindIndex(m => string.Equals(m.Id, entry.Id, StringComparison.Ordinal));
        if (at < 0)
        {
            index.Mirrors.Add(entry);
            return true;
        }

        if (index.Mirrors[at] == entry)
        {
            return false;
        }

        index.Mirrors[at] = entry;
        return true;
    }

    // Whether a working copy started from serverId has a base version other than the new mirror version.
    private static bool HasCopyBehind(string dir, string serverId, string newVersion, List<StoreProblem> problems)
    {
        foreach (string id in SidecarIdsWithCopy(dir, problems))
        {
            WorkingCopySidecar sidecar = ReadSidecar(dir, id, problems);
            if (
                string.Equals(sidecar.OriginId, serverId, StringComparison.Ordinal)
                && !string.Equals(sidecar.BaseVersion, newVersion, StringComparison.Ordinal)
            )
            {
                return true;
            }
        }

        return false;
    }

    // The ids of the sidecars that sit beside their working copy; each sidecar without one is reported.
    private static List<string> SidecarIdsWithCopy(string dir, List<StoreProblem> problems)
    {
        string localDir = Path.Combine(dir, LocalFolder);
        if (!Directory.Exists(localDir))
        {
            return [];
        }

        var ids = new List<string>();
        foreach (string path in Directory.EnumerateFiles(localDir, "*" + SidecarSuffix).Order(StringComparer.Ordinal))
        {
            string id = Path.GetFileName(path)[..^SidecarSuffix.Length];
            if (File.Exists(LocalPath(dir, id)))
            {
                ids.Add(id);
            }
            else
            {
                problems.Add(
                    new StoreProblem($"{LocalFolder}/{id}{SidecarSuffix}", $"its working copy {LocalFolder}/{id}.json is missing; not a working copy")
                );
            }
        }

        return ids;
    }

    private static List<WorkingCopyEntry> ListWorkingCopies(string dir, StoreIndexFile index, List<StoreProblem> problems)
    {
        string localDir = Path.Combine(dir, LocalFolder);
        if (!Directory.Exists(localDir))
        {
            return [];
        }

        // Reports each sidecar whose working copy is gone.
        _ = SidecarIdsWithCopy(dir, problems);

        var versions = index.Mirrors.ToDictionary(m => m.Id, m => m.Version, StringComparer.Ordinal);
        var copies = new List<WorkingCopyEntry>();
        foreach (string path in Directory.EnumerateFiles(localDir, "*.json").Order(StringComparer.Ordinal))
        {
            if (!path.EndsWith(SidecarSuffix, StringComparison.OrdinalIgnoreCase))
            {
                copies.Add(ReadWorkingCopy(dir, path, versions, problems));
            }
        }

        return copies;
    }

    private static WorkingCopyEntry ReadWorkingCopy(string dir, string path, Dictionary<string, string> versions, List<StoreProblem> problems)
    {
        string fileName = Path.GetFileName(path);
        string id = Path.GetFileNameWithoutExtension(path);
        WorkingCopySidecar sidecar = ReadSidecar(dir, id, problems);
        string? reason = CheckScenario(File.ReadAllText(path), out JsonObject? scenario);
        if (reason is not null)
        {
            problems.Add(new StoreProblem($"{LocalFolder}/{fileName}", reason));
        }

        bool modified = (scenario is not null) && (sidecar.BaseContentHash is not null) && (ComputeContentHash(scenario) != sidecar.BaseContentHash);
        return new WorkingCopyEntry(id, sidecar.OriginId, sidecar.BaseVersion, modified, IsServerChanged(sidecar, versions), reason is not null);
    }

    private static bool IsServerChanged(WorkingCopySidecar sidecar, Dictionary<string, string> versions) =>
        (sidecar.OriginId is not null)
        && (sidecar.BaseVersion is not null)
        && versions.TryGetValue(sidecar.OriginId, out string? version)
        && !string.Equals(version, sidecar.BaseVersion, StringComparison.Ordinal);

    private static WorkingCopySidecar ReadSidecar(string dir, string id, List<StoreProblem> problems)
    {
        string path = SidecarPath(dir, id);
        string fileName = $"{LocalFolder}/{id}{SidecarSuffix}";
        if (!File.Exists(path))
        {
            problems.Add(new StoreProblem(fileName, "missing; the working copy is treated as local-only"));
            return new WorkingCopySidecar(null, null, null, null);
        }

        try
        {
            return JsonSerializer.Deserialize<WorkingCopySidecar>(File.ReadAllText(path), JsonOptions)
                ?? throw new JsonException("the sidecar is null");
        }
        catch (JsonException ex)
        {
            problems.Add(new StoreProblem(fileName, "corrupt (" + ex.Message + "); the working copy is treated as local-only"));
            return new WorkingCopySidecar(null, null, null, null);
        }
    }

    private static void WriteSidecar(string dir, string id, WorkingCopySidecar sidecar) =>
        StoreFiles.WriteAtomically(SidecarPath(dir, id), JsonSerializer.Serialize(sidecar, JsonOptions));

    private static StoreIndexFile LoadIndex(string dir, List<StoreProblem> problems, out bool rebuilt)
    {
        string path = Path.Combine(dir, IndexFileName);
        rebuilt = false;
        if (!File.Exists(path))
        {
            StoreIndexFile fresh = Rebuild(dir, problems);
            if (HasAnyScenarioFile(dir))
            {
                rebuilt = true;
                problems.Add(new StoreProblem(IndexFileName, "missing; rebuilt from the server mirror and the working-copy sidecars"));
                SaveIndex(dir, fresh);
            }

            return fresh;
        }

        string? reason = TryReadIndex(path, out StoreIndexFile? read);
        if (read is not null)
        {
            return read;
        }

        string kept = KeepCorruptIndex(dir, path);
        StoreIndexFile index = Rebuild(dir, problems);
        problems.Add(new StoreProblem(IndexFileName, $"corrupt ({reason}); kept as {kept} and rebuilt"));
        SaveIndex(dir, index);
        rebuilt = true;
        return index;
    }

    private static string? TryReadIndex(string path, out StoreIndexFile? index)
    {
        index = null;
        try
        {
            StoreIndexFile? read = JsonSerializer.Deserialize<StoreIndexFile>(File.ReadAllText(path), JsonOptions);
            if (read?.Mirrors is null)
            {
                return "it holds no mirror list";
            }

            if (!read.Mirrors.All(IsUsableEntry))
            {
                return "an entry has no valid id, name or version";
            }

            index = read;
            return null;
        }
        catch (JsonException ex)
        {
            return ex.Message;
        }
    }

    // An index entry may be null or lack fields: JSON deserialization does not enforce the record's non-null members.
    private static bool IsUsableEntry(MirrorEntry? m) =>
        (m is not null) && StoreFiles.IsValidId(m.Id) && (m.Name is not null) && (m.Version is not null);

    private static string KeepCorruptIndex(string dir, string path)
    {
        string kept = IndexFileName + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        File.Move(path, Path.Combine(dir, kept));
        if (Log.IsEnabled(LogLevel.Warning))
        {
            Log.LogWarning("Scenario index {Path} is corrupt; kept as {Kept} and rebuilt", path, kept);
        }

        return kept;
    }

    private static StoreIndexFile Rebuild(string dir, List<StoreProblem> problems) =>
        new() { Mirrors = ScanServerFolder(dir, known: [], problems, reportAdded: false) };

    // An entry for each readable mirror file whose id is not in known; each unreadable one is reported, not listed.
    private static List<MirrorEntry> ScanServerFolder(string dir, HashSet<string> known, List<StoreProblem> problems, bool reportAdded)
    {
        string serverDir = Path.Combine(dir, ServerFolder);
        if (!Directory.Exists(serverDir))
        {
            return [];
        }

        var found = new List<MirrorEntry>();
        foreach (string path in Directory.EnumerateFiles(serverDir, "*.json").Order(StringComparer.Ordinal))
        {
            if (!known.Contains(Path.GetFileNameWithoutExtension(path)) && (ReadMirrorFile(path, problems, reportAdded) is { } entry))
            {
                found.Add(entry);
            }
        }

        return found;
    }

    // The entry for one mirror file, or null (and a reported problem) when it is not a readable scenario.
    private static MirrorEntry? ReadMirrorFile(string path, List<StoreProblem> problems, bool reportAdded)
    {
        string fileName = $"{ServerFolder}/{Path.GetFileName(path)}";
        string id = Path.GetFileNameWithoutExtension(path);
        string note = reportAdded ? "not in the index; " : "";
        if (!StoreFiles.IsValidId(id))
        {
            problems.Add(new StoreProblem(fileName, note + "the file name is not a valid scenario id"));
            return null;
        }

        if (!TryReadMirror(path, id, out string? text, out JsonObject? scenario, out string? reason))
        {
            problems.Add(new StoreProblem(fileName, note + reason));
            return null;
        }

        if (reportAdded)
        {
            problems.Add(new StoreProblem(fileName, note + "added to it"));
        }

        return new MirrorEntry(id, TextOf(scenario, "name"), ComputeVersion(text), false);
    }

    private static bool HasAnyScenarioFile(string dir) =>
        Directory.Exists(dir)
        && Directory
            .EnumerateFiles(dir, "*.json", SearchOption.AllDirectories)
            .Any(p => !Path.GetFileName(p).Equals(IndexFileName, StringComparison.Ordinal));

    private static void SaveIndex(string dir, StoreIndexFile index)
    {
        index.Mirrors.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        StoreFiles.WriteAtomically(Path.Combine(dir, IndexFileName), JsonSerializer.Serialize(index, JsonOptions));
    }

    /// <summary>
    /// The corrupt-file rule shared by bodies, mirrors and working copies: malformed JSON, a top-level value that is not
    /// an object, or an object without a text <c>id</c> or <c>name</c>. Null when the text is a scenario.
    /// </summary>
    internal static string? CheckScenario(string text, out JsonObject? scenario)
    {
        scenario = null;
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (JsonException ex)
        {
            return "not a scenario: malformed JSON (" + ex.Message + ")";
        }

        if (node is not JsonObject obj)
        {
            return "not a scenario: the top-level value is not an object";
        }

        if (!HasText(obj, "id") || !HasText(obj, "name"))
        {
            return "not a scenario: it has no id or no name";
        }

        scenario = obj;
        return null;
    }

    private static bool HasText(JsonObject obj, string key) =>
        (obj[key] is JsonValue value) && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text);

    private static string TextOf(JsonObject obj, string key) => obj[key]!.GetValue<string>();

    // The SHA-256 of the content with every object's keys sorted and every number in one form, so key order, whitespace
    // and number spelling (250.0, 250, 2.5e2) do not count.
    private static string ComputeContentHash(JsonNode node) => ComputeVersion(Canonical(node)!.ToJsonString());

    private static JsonNode? Canonical(JsonNode? node) =>
        node switch
        {
            JsonObject obj => new JsonObject(
                obj.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => KeyValuePair.Create(kv.Key, Canonical(kv.Value)))
            ),
            JsonArray array => new JsonArray([.. array.Select(Canonical)]),
            JsonValue value when value.GetValueKind() == JsonValueKind.Number => CanonicalNumber(value),
            null => null,
            _ => node.DeepClone(),
        };

    // A number as its double value, which the serializer writes in its shortest round-trip form (an integer with no
    // fraction), with -0 as 0; a number a double cannot hold keeps its text.
    private static JsonNode CanonicalNumber(JsonValue value) =>
        (value.TryGetValue(out double number) && double.IsFinite(number)) ? JsonValue.Create((number == 0) ? 0d : number) : value.DeepClone();

    private static void LogArtccMismatch(string artccId, string id, JsonObject scenario)
    {
        if (NamesOtherArtcc(artccId, scenario) && Log.IsEnabled(LogLevel.Information))
        {
            Log.LogInformation(
                "Scenario {Id} names ARTCC {Field} but was listed under {ArtccId}; it is kept in that ARTCC's folder",
                id,
                TextOf(scenario, "artccId"),
                artccId
            );
        }
    }

    // Whether the scenario's own artccId names an ARTCC other than artccId (letter case ignored); false when it has none.
    private static bool NamesOtherArtcc(string artccId, JsonObject scenario) =>
        HasText(scenario, "artccId") && !string.Equals(TextOf(scenario, "artccId"), artccId, StringComparison.OrdinalIgnoreCase);

    private static ServerApplyResult Rejected(string reason) => new(ServerApplyOutcome.Rejected, reason, []);

    private static void RequireValidId(string id)
    {
        if (!StoreFiles.IsValidId(id))
        {
            throw new ArgumentException($"'{id}' is not a valid scenario id (letters, digits, '_' and '-' only).", nameof(id));
        }
    }

    private string ArtccDirectory(string artccId)
    {
        if (!StoreFiles.IsValidId(artccId))
        {
            throw new ArgumentException($"'{artccId}' is not a valid ARTCC id (letters, digits, '_' and '-' only).", nameof(artccId));
        }

        return Path.Combine(RootDirectory, artccId);
    }

    private static string MirrorPath(string dir, string id) => Path.Combine(dir, ServerFolder, id + ".json");

    private static string LocalPath(string dir, string id) => Path.Combine(dir, LocalFolder, id + ".json");

    private static string SidecarPath(string dir, string id) => Path.Combine(dir, LocalFolder, id + SidecarSuffix);

    private static bool IsUnder(string path, string folder)
    {
        string trimmed = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.Equals(trimmed, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(trimmed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StoreIndexFile
    {
        public int Format { get; set; } = 1;

        public List<MirrorEntry> Mirrors { get; set; } = [];
    }

    private sealed record WorkingCopySidecar(string? OriginId, string? BaseVersion, string? BaseContentHash, DateTimeOffset? LastExport);
}

/// <summary>What one server body did to the store.</summary>
public enum ServerApplyOutcome
{
    /// <summary>The id was new: the mirror was written and an index entry added.</summary>
    Added,

    /// <summary>The body changed the mirror, which was replaced with a new version; no working copy derives from it.</summary>
    Updated,

    /// <summary>The body changed the mirror, which was replaced; its working copies are left as they were and now server-changed.</summary>
    ServerChanged,

    /// <summary>The body equals the mirror as JSON; nothing was written.</summary>
    Unchanged,

    /// <summary>The body or its id was refused and nothing was written; the result says why.</summary>
    Rejected,
}

/// <summary>A file of the store that could not be read as expected.</summary>
/// <param name="FileName">The file, relative to the ARTCC folder with '/' separators, e.g. <c>server/{id}.json</c>.</param>
/// <param name="Reason">What is wrong with it and what the store did about it.</param>
public sealed record StoreProblem(string FileName, string Reason);

/// <summary>The outcome of <see cref="LocalScenarioStore.ApplyServerBody"/>.</summary>
/// <param name="Outcome">What the body did.</param>
/// <param name="Reason">Why the body was rejected; null otherwise.</param>
/// <param name="Problems">File problems found on the way.</param>
public sealed record ServerApplyResult(ServerApplyOutcome Outcome, string? Reason, IReadOnlyList<StoreProblem> Problems);

/// <summary>The outcome of <see cref="LocalScenarioStore.MarkGoneFromServer"/>.</summary>
/// <param name="Missing">The mirrored ids the server no longer lists.</param>
/// <param name="Problems">File problems found on the way.</param>
public sealed record MarkGoneResult(IReadOnlyList<string> Missing, IReadOnlyList<StoreProblem> Problems);

/// <summary>One mirrored server scenario, as the index keeps it.</summary>
/// <param name="Id">The scenario id.</param>
/// <param name="Name">The scenario name.</param>
/// <param name="Version">The mirror's version: the SHA-256 of its text.</param>
/// <param name="GoneFromServer">Whether the server no longer listed the scenario at the last refresh.</param>
public sealed record MirrorEntry(string Id, string Name, string Version, bool GoneFromServer);

/// <summary>One mirrored server scenario as <see cref="LocalScenarioStore.Load"/> lists it: the index record and what
/// the load found of its file. The state is the load's, never persisted in the index.</summary>
/// <param name="Mirror">The index record.</param>
/// <param name="Corrupt">Whether its file is missing, unreadable, not a scenario, or holds another scenario's id.</param>
public sealed record MirrorListing(MirrorEntry Mirror, bool Corrupt);

/// <summary>One working copy and its state.</summary>
/// <param name="Id">The working copy's id, its file name.</param>
/// <param name="OriginId">The server scenario it was started from; null for a local-only copy.</param>
/// <param name="BaseVersion">The mirror version it was started from; null for a local-only copy.</param>
/// <param name="Modified">Whether its content differs from the content it was started from.</param>
/// <param name="ServerChanged">Whether the mirror of its origin has a version other than its base.</param>
/// <param name="Corrupt">Whether its file is not a readable scenario; it is never rewritten or deleted for it.</param>
public sealed record WorkingCopyEntry(string Id, string? OriginId, string? BaseVersion, bool Modified, bool ServerChanged, bool Corrupt);

/// <summary>An ARTCC's mirror and working copies, as <see cref="LocalScenarioStore.Load"/> found them.</summary>
/// <param name="ArtccId">The ARTCC.</param>
/// <param name="Mirrors">The mirrored server scenarios, by id, each with whether its file is corrupt.</param>
/// <param name="WorkingCopies">The working copies, by id.</param>
/// <param name="IndexRebuilt">Whether the index was missing or corrupt and was rebuilt from the files.</param>
/// <param name="Problems">Every file problem found.</param>
public sealed record ArtccStoreListing(
    string ArtccId,
    IReadOnlyList<MirrorListing> Mirrors,
    IReadOnlyList<WorkingCopyEntry> WorkingCopies,
    bool IndexRebuilt,
    IReadOnlyList<StoreProblem> Problems
);
