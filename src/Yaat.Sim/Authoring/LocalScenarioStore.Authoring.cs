using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Authoring;

/// <summary>
/// The authoring operations: reading stored text, and creating, duplicating, deleting and importing working copies. A
/// new, duplicated or new-id imported copy is local-only (no origin, no base); only an import that keeps a server
/// scenario's id, with no working copy of it yet, becomes that scenario's working copy. The server mirror is never
/// written or deleted here.
/// </summary>
public sealed partial class LocalScenarioStore
{
    // Used only where the store must rewrite a scenario's id: number text is kept as parsed, non-ASCII is not escaped.
    private static readonly JsonSerializerOptions RewriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The full path of a working copy's file, whether or not it exists.</summary>
    /// <param name="artccId">The ARTCC.</param>
    /// <param name="id">The working copy's id.</param>
    /// <returns>The path of <c>{root}/{artccId}/local/{id}.json</c>.</returns>
    /// <exception cref="ArgumentException"><paramref name="artccId"/> or <paramref name="id"/> is not a valid id.</exception>
    public string GetWorkingCopyPath(string artccId, string id)
    {
        string dir = ArtccDirectory(artccId);
        RequireValidId(id);
        return LocalPath(dir, id);
    }

    /// <summary>A working copy's stored text, exactly as it is on disk.</summary>
    /// <param name="artccId">The ARTCC.</param>
    /// <param name="id">The working copy's id.</param>
    /// <returns>The text.</returns>
    /// <exception cref="FileNotFoundException">There is no working copy with that id.</exception>
    public string ReadWorkingCopyText(string artccId, string id) => File.ReadAllText(GetWorkingCopyPath(artccId, id));

    /// <summary>A server mirror's stored text, exactly as data-api sent it.</summary>
    /// <param name="artccId">The ARTCC.</param>
    /// <param name="id">The scenario id.</param>
    /// <returns>The text.</returns>
    /// <exception cref="FileNotFoundException">There is no mirror with that id.</exception>
    public string ReadMirrorText(string artccId, string id) => File.ReadAllText(GetMirrorPath(artccId, id));

    /// <summary>Whether an id is taken by a working copy, a server mirror, both or neither.</summary>
    /// <param name="artccId">The ARTCC.</param>
    /// <param name="id">The id to probe.</param>
    /// <returns>What holds the id.</returns>
    public ScenarioIdConflict FindIdConflict(string artccId, string id)
    {
        string dir = ArtccDirectory(artccId);
        RequireValidId(id);
        bool copy = StoredId(dir, LocalFolder, id) is not null;
        bool mirror = StoredId(dir, ServerFolder, id) is not null;
        return (copy, mirror) switch
        {
            (true, true) => ScenarioIdConflict.Both,
            (true, false) => ScenarioIdConflict.WorkingCopy,
            (false, true) => ScenarioIdConflict.Mirror,
            _ => ScenarioIdConflict.None,
        };
    }

    /// <summary>Writes a new local-only working copy: the scenario with its <c>id</c> field set to <paramref name="id"/>.</summary>
    /// <param name="artccId">The ARTCC.</param>
    /// <param name="json">The scenario JSON: an object with a text <c>name</c>; its <c>id</c> is replaced.</param>
    /// <param name="id">The new working copy's id.</param>
    /// <exception cref="ArgumentException">The JSON is not a scenario object with a name, or an id is not valid.</exception>
    /// <exception cref="InvalidOperationException">A working copy with that id exists; it is left as it was.</exception>
    public void CreateLocalScenario(string artccId, string json, string id)
    {
        string dir = ArtccDirectory(artccId);
        RequireValidId(id);
        ArgumentNullException.ThrowIfNull(json);
        string text = WithIdOrThrow(json, id);
        lock (_gate)
        {
            RequireNoWorkingCopy(dir, id);
            WriteLocalOnly(dir, id, text);
        }
    }

    /// <summary>
    /// Copies a working copy under a new id, with the JSON <c>id</c> rewritten; the duplicate is local-only whatever
    /// the source's origin, so it is never server-changed.
    /// </summary>
    /// <param name="artccId">The ARTCC.</param>
    /// <param name="sourceId">The working copy to duplicate.</param>
    /// <param name="newId">The duplicate's id.</param>
    /// <exception cref="InvalidOperationException">
    /// The source is missing or corrupt, or a working copy with <paramref name="newId"/> exists; nothing is written.
    /// </exception>
    public void DuplicateWorkingCopy(string artccId, string sourceId, string newId)
    {
        string dir = ArtccDirectory(artccId);
        RequireValidId(sourceId);
        RequireValidId(newId);
        lock (_gate)
        {
            string sourcePath = LocalPath(dir, sourceId);
            if (!File.Exists(sourcePath))
            {
                throw new InvalidOperationException($"There is no working copy {sourceId} at {sourcePath}.");
            }

            string? reason = CheckScenario(File.ReadAllText(sourcePath), out JsonObject? scenario);
            if (reason is not null)
            {
                throw new InvalidOperationException($"The working copy at {sourcePath} is corrupt ({reason}); nothing was duplicated.");
            }

            RequireNoWorkingCopy(dir, newId);
            WriteLocalOnly(dir, newId, RewriteId(scenario!, newId));
        }
    }

    /// <summary>Deletes a working copy's file and its sidecar, either of which may be absent; a corrupt copy is deleted too.</summary>
    /// <param name="artccId">The ARTCC.</param>
    /// <param name="id">The working copy's id.</param>
    /// <returns>Whether any file was deleted.</returns>
    public bool DeleteWorkingCopy(string artccId, string id)
    {
        string dir = ArtccDirectory(artccId);
        RequireValidId(id);
        lock (_gate)
        {
            bool deleted = DeleteIfPresent(LocalPath(dir, id));
            deleted |= DeleteIfPresent(SidecarPath(dir, id));
            if (deleted && Log.IsEnabled(LogLevel.Information))
            {
                Log.LogInformation("Deleted working copy {Id} of {ArtccId}", id, artccId);
            }

            return deleted;
        }
    }

    /// <summary>
    /// Imports a scenario file's text as a working copy. <see cref="ImportIdChoice.NewId"/> takes a fresh id from
    /// <paramref name="newId"/> and rewrites the JSON <c>id</c> (local-only); <see cref="ImportIdChoice.Skip"/> writes
    /// nothing; <see cref="ImportIdChoice.KeepId"/> keeps the file's id and text: over an existing working copy it
    /// replaces the text and keeps the sidecar, over a server mirror alone it becomes that mirror's working copy based
    /// on the current mirror version, and otherwise it is a local-only copy.
    /// </summary>
    /// <param name="artccId">The ARTCC to import into.</param>
    /// <param name="json">The file's text.</param>
    /// <param name="choice">What to do with the file's id.</param>
    /// <param name="newId">Makes the id for <see cref="ImportIdChoice.NewId"/>.</param>
    /// <returns>What was written, or why nothing was.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="newId"/> gave an id a working copy already has.</exception>
    public ImportResult ImportScenario(string artccId, string json, ImportIdChoice choice, Func<string> newId)
    {
        if (!Enum.IsDefined(choice))
        {
            throw new ArgumentOutOfRangeException(nameof(choice), choice, "Not an import id choice.");
        }

        string dir = ArtccDirectory(artccId);
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(newId);
        string? reason = CheckScenario(json, out JsonObject? scenario);
        if (reason is not null)
        {
            return ImportResult.Rejected(reason);
        }

        string fileId = TextOf(scenario!, "id");
        bool mismatch = NamesOtherArtcc(artccId, scenario!);
        if (mismatch)
        {
            LogImportArtccMismatch(artccId, fileId, scenario!);
        }

        return choice switch
        {
            ImportIdChoice.Skip => new ImportResult(ImportOutcome.Skipped, null, false, mismatch, null),
            ImportIdChoice.NewId => ImportWithNewId(dir, scenario!, newId(), mismatch),
            ImportIdChoice.KeepId => StoreFiles.IsValidId(fileId)
                ? ImportKeepingId(dir, fileId, scenario!, json, mismatch)
                : ImportResult.Rejected($"its id {fileId} cannot name a file (letters, digits, '_' and '-' only)"),
            _ => throw new ArgumentOutOfRangeException(nameof(choice), choice, "Not an import id choice."),
        };
    }

    // The id comes from the caller's factory before the root lock is taken, so no foreign code runs under it.
    private ImportResult ImportWithNewId(string dir, JsonObject scenario, string id, bool mismatch)
    {
        RequireValidId(id);
        lock (_gate)
        {
            RequireNoWorkingCopy(dir, id);
            WriteLocalOnly(dir, id, RewriteId(scenario, id));
        }

        return new ImportResult(ImportOutcome.Imported, id, false, mismatch, null);
    }

    // An id that matches a stored working copy or mirror in all but letter case takes the stored casing, in the file
    // name, the JSON id and the sidecar origin, so the copy pairs with its mirror under the store's ordinal id compares.
    private ImportResult ImportKeepingId(string dir, string fileId, JsonObject scenario, string json, bool mismatch)
    {
        lock (_gate)
        {
            if (StoredId(dir, LocalFolder, fileId) is { } copyId)
            {
                StoreFiles.WriteAtomically(LocalPath(dir, copyId), TextWithId(scenario, json, fileId, copyId));
                if (!File.Exists(SidecarPath(dir, copyId)))
                {
                    WriteSidecar(dir, copyId, new WorkingCopySidecar(null, null, null, null));
                }

                return new ImportResult(ImportOutcome.Imported, copyId, true, mismatch, null);
            }

            if (StoredId(dir, ServerFolder, fileId) is { } mirrorId)
            {
                return ImportOverMirror(dir, mirrorId, TextWithId(scenario, json, fileId, mirrorId), mismatch);
            }

            WriteLocalOnly(dir, fileId, json);
            return new ImportResult(ImportOutcome.Imported, fileId, false, mismatch, null);
        }
    }

    // Makes the import the mirror's working copy, based on the mirror as it is now, so the import reads modified when
    // its content differs from the mirror's; a mirror Load would list corrupt is refused and nothing is written.
    private static ImportResult ImportOverMirror(string dir, string mirrorId, string text, bool mismatch)
    {
        if (!TryReadMirror(MirrorPath(dir, mirrorId), mirrorId, out string? mirrorText, out JsonObject? mirror, out string? reason))
        {
            return ImportResult.Rejected($"the server copy {ServerFolder}/{mirrorId}.json can't be used ({reason}); import the file with a new id");
        }

        WriteSidecar(dir, mirrorId, new WorkingCopySidecar(mirrorId, ComputeVersion(mirrorText), ComputeContentHash(mirror), null));
        StoreFiles.WriteAtomically(LocalPath(dir, mirrorId), text);
        return new ImportResult(ImportOutcome.Imported, mirrorId, false, mismatch, null);
    }

    // The file's text as read when its id is already the stored one, else the scenario with the stored id written in.
    private static string TextWithId(JsonObject scenario, string json, string fileId, string storedId) =>
        string.Equals(fileId, storedId, StringComparison.Ordinal) ? json : RewriteId(scenario, storedId);

    // The id of the file in folder whose name matches id ignoring case (the exact one first), or null when none does,
    // matched the same way on every file system.
    private static string? StoredId(string dir, string folder, string id)
    {
        string path = Path.Combine(dir, folder);
        if (!Directory.Exists(path))
        {
            return null;
        }

        var options = new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, MatchType = MatchType.Simple };
        List<string> ids = [.. Directory.EnumerateFiles(path, id + ".json", options).Select(p => Path.GetFileName(p)[..^".json".Length])];
        return ids.Contains(id, StringComparer.Ordinal) ? id : ids.Order(StringComparer.Ordinal).FirstOrDefault();
    }

    // The scenario text with its id set, or ArgumentException when it is not a scenario object with a name.
    private static string WithIdOrThrow(string json, string id)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The scenario is not valid JSON: " + ex.Message, nameof(json), ex);
        }

        if (node is not JsonObject scenario)
        {
            throw new ArgumentException("The scenario's top-level value is not an object.", nameof(json));
        }

        return HasText(scenario, "name") ? RewriteId(scenario, id) : throw new ArgumentException("The scenario has no name.", nameof(json));
    }

    private static string RewriteId(JsonObject scenario, string id)
    {
        scenario["id"] = id;
        return scenario.ToJsonString(RewriteOptions);
    }

    // A working copy whose id matches in all but letter case counts too: on Windows it is the same file.
    private static void RequireNoWorkingCopy(string dir, string id)
    {
        if (StoredId(dir, LocalFolder, id) is { } existing)
        {
            throw new InvalidOperationException($"A working copy already exists at {LocalPath(dir, existing)}.");
        }
    }

    // The sidecar goes first, as in CreateWorkingCopy, so a crash leaves an orphan sidecar rather than a copy without one.
    private static void WriteLocalOnly(string dir, string id, string text)
    {
        WriteSidecar(dir, id, new WorkingCopySidecar(null, null, null, null));
        StoreFiles.WriteAtomically(LocalPath(dir, id), text);
    }

    private static bool DeleteIfPresent(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    private static void LogImportArtccMismatch(string artccId, string fileId, JsonObject scenario)
    {
        if (Log.IsEnabled(LogLevel.Information))
        {
            Log.LogInformation("Scenario {Id}: imported file's artccId {Field} differs from {ArtccId}", fileId, TextOf(scenario, "artccId"), artccId);
        }
    }
}

/// <summary>What an import does with the file's scenario id.</summary>
public enum ImportIdChoice
{
    /// <summary>Give the import a fresh id, rewritten into its JSON; the copy is local-only.</summary>
    NewId,

    /// <summary>
    /// Keep the file's id: replace the text of a working copy that has it, else become the working copy of a server
    /// mirror that has it, else make a local-only copy with it (see <see cref="LocalScenarioStore.ImportScenario"/>).
    /// </summary>
    KeepId,

    /// <summary>Import nothing.</summary>
    Skip,
}

/// <summary>What already holds a scenario id in an ARTCC's store.</summary>
public enum ScenarioIdConflict
{
    /// <summary>Nothing: the id is free.</summary>
    None,

    /// <summary>A working copy has the id; no server mirror does.</summary>
    WorkingCopy,

    /// <summary>A server mirror has the id; no working copy does.</summary>
    Mirror,

    /// <summary>A working copy and a server mirror both have the id.</summary>
    Both,
}

/// <summary>What one import did.</summary>
public enum ImportOutcome
{
    /// <summary>A working copy was written.</summary>
    Imported,

    /// <summary>The caller chose to skip the file; nothing was written.</summary>
    Skipped,

    /// <summary>The file could not be imported; nothing was written and the result says why.</summary>
    Rejected,
}

/// <summary>The outcome of <see cref="LocalScenarioStore.ImportScenario"/>.</summary>
/// <param name="Outcome">What the import did.</param>
/// <param name="Id">The id of the working copy written; null when nothing was.</param>
/// <param name="Overwrote">Whether it replaced an existing working copy's text.</param>
/// <param name="ArtccMismatch">Whether the file's <c>artccId</c> names another ARTCC than the one it was imported into.</param>
/// <param name="Reason">Why the file was rejected; null otherwise.</param>
public sealed record ImportResult(ImportOutcome Outcome, string? Id, bool Overwrote, bool ArtccMismatch, string? Reason)
{
    internal static ImportResult Rejected(string reason) => new(ImportOutcome.Rejected, null, false, false, reason);
}
