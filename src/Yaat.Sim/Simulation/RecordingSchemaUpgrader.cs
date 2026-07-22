using System.IO.Compression;
using System.Text.Json;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Simulation;

/// <summary>
/// Outcome of a surgical schema upgrade. <see cref="Changed"/> is true only when snapshots were
/// migrated; <see cref="NeedsResimulation"/> flags a v1 recording (no snapshots) that the caller must
/// bootstrap by re-simulating. When neither is set, the input was already at the current schema.
/// </summary>
public readonly record struct RecordingUpgradeResult(bool Changed, byte[] Output, bool NeedsResimulation)
{
    /// <summary>Nothing to migrate (already current, or an unrecognized container); output == input.</summary>
    public static RecordingUpgradeResult Unchanged(byte[] input) => new(false, input, false);

    /// <summary>A v1 recording with no snapshots; only re-simulation can bootstrap it to v2.</summary>
    public static RecordingUpgradeResult NeedsResim(byte[] input) => new(false, input, true);

    /// <summary>Snapshots were migrated in place; output holds the rewritten container bytes.</summary>
    public static RecordingUpgradeResult Migrated(byte[] output) => new(true, output, false);
}

/// <summary>
/// Surgically upgrades the state snapshots inside a recording to
/// <see cref="SnapshotSchemaMigrator.CurrentSchemaVersion"/> by transforming each snapshot's JSON in
/// place via <see cref="SnapshotSchemaMigrator"/> — never by re-simulating. Re-simulation with current
/// code would rewrite a frozen pre-fix snapshot into the fixed state, silently invalidating the
/// hybrid-replay tests that pin a bug's setup (see docs/e2e-tdd-issue-debugging.md §5b). Handles the
/// recording containers: a non-zip <see cref="SessionRecording"/> blob (inline snapshots), a v4 archive
/// zip (<c>snapshots/NNN.json.br</c> entries), a bug-report bundle wrapping a v4 archive, and a legacy
/// bundle wrapping an inline-snapshot blob. A v1 recording (no snapshots) is reported via
/// <see cref="RecordingUpgradeResult.NeedsResimulation"/> for the caller to bootstrap.
/// </summary>
public static class RecordingSchemaUpgrader
{
    private const string InnerRecordingEntry = "recording.yaat-recording.zip";
    private const string ActionsEntry = "actions.json.br";
    private const string ArtccConfigEntry = "artcc-config.json.br";
    private const string ScenarioEntry = "scenario.json.br";
    private static readonly string[] LegacyInlineEntries =
    [
        "recording.yaat-recording.br",
        "recording.yaat-recording.json.gz",
        "recording.yaat-recording.json",
    ];

    /// <summary>
    /// Migrate every snapshot in <paramref name="input"/> to the current schema, returning the rewritten
    /// container bytes. See the type summary for the container-detection contract.
    /// </summary>
    public static RecordingUpgradeResult Upgrade(byte[] input)
    {
        return RecordingCompression.IsZipArchive(input) ? UpgradeZip(input) : UpgradeSessionRecordingBytes(input);
    }

    // --- non-zip SessionRecording blob (.br / .json[.gz]) ---

    private static RecordingUpgradeResult UpgradeSessionRecordingBytes(byte[] input)
    {
        var json = RecordingCompression.Decompress(input);
        var recording =
            JsonSerializer.Deserialize<SessionRecording>(json, RecordingJsonOptions.Default)
            ?? throw new InvalidOperationException("Failed to deserialize recording.");

        if (!recording.HasSnapshots)
        {
            return RecordingUpgradeResult.NeedsResim(input);
        }

        var changed = MigrateSnapshots(recording.Snapshots!);
        changed |= QualifyStripBays(
            recording.Actions,
            ResolveBays(recording.ArtccConfigJson, recording.StudentPositionState?.Position?.Callsign, recording.ScenarioJson)
        );
        if (!changed)
        {
            return RecordingUpgradeResult.Unchanged(input);
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(recording, RecordingJsonOptions.Default);
        return RecordingUpgradeResult.Migrated(RecordingCompression.Compress(bytes));
    }

    // --- strip-bay qualification: recorded canonicals predating the required
    //     FACILITY/BAY bay token are rewritten in place. Idempotent, so no schema
    //     gate is needed and re-running the upgrade is a no-op. ---

    private static IReadOnlyList<AccessibleBay> ResolveBays(string? artccConfigJson, string? positionCallsign, string? scenarioJson)
    {
        if (string.IsNullOrEmpty(artccConfigJson))
        {
            return [];
        }

        var config = JsonSerializer.Deserialize<ArtccConfigRoot>(artccConfigJson, RecordingJsonOptions.Default);
        if (config is null)
        {
            return [];
        }

        var callsign = positionCallsign ?? ResolveStudentCallsignFromScenario(config, scenarioJson);
        if (string.IsNullOrEmpty(callsign))
        {
            return [];
        }

        // The display set (own facility + linked external bays) is exactly what the
        // recorded session could address, so it never invents a facility the
        // original controller had no access to.
        return config.GetAllAccessibleStripBays(callsign);
    }

    /// <summary>
    /// Fallback student-position lookup for recordings whose snapshots carry a null
    /// <c>StudentPosition</c> (the server had not resolved one at capture time). The
    /// scenario JSON always names the position by id, which the ARTCC config resolves
    /// to the callsign the bay lookup needs.
    /// </summary>
    private static string? ResolveStudentCallsignFromScenario(ArtccConfigRoot config, string? scenarioJson)
    {
        if (string.IsNullOrEmpty(scenarioJson))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(scenarioJson);
        if (!doc.RootElement.TryGetProperty("studentPositionId", out var idElement) || idElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var positionId = idElement.GetString();
        if (string.IsNullOrEmpty(positionId))
        {
            return null;
        }

        var (_, _, _, position) = config.FindPosition(positionId);
        return position?.Callsign;
    }

    private static bool QualifyStripBays(List<RecordedAction> actions, IReadOnlyList<AccessibleBay> bays)
    {
        if (bays.Count == 0)
        {
            return false;
        }

        var changed = false;
        for (var i = 0; i < actions.Count; i++)
        {
            if (actions[i] is not RecordedCommand command)
            {
                continue;
            }
            var qualified = StripBayCanonicalQualifier.QualifyCompound(command.Command, bays);
            if (string.Equals(qualified, command.Command, StringComparison.Ordinal))
            {
                continue;
            }
            actions[i] = command with { Command = qualified };
            changed = true;
        }

        return changed;
    }

    // --- zip containers ---

    private static RecordingUpgradeResult UpgradeZip(byte[] input)
    {
        using var ms = new MemoryStream(input, writable: false);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

        if (zip.GetEntry("manifest.json") is not null)
        {
            var (changed, output) = RewriteArchiveSnapshots(input);
            return changed ? RecordingUpgradeResult.Migrated(output) : RecordingUpgradeResult.Unchanged(input);
        }

        if (zip.GetEntry(InnerRecordingEntry) is not null)
        {
            return UpgradeNestedArchiveBundle(input);
        }

        foreach (var legacy in LegacyInlineEntries)
        {
            if (zip.GetEntry(legacy) is not null)
            {
                return UpgradeLegacyInlineBundle(input, legacy);
            }
        }

        return RecordingUpgradeResult.Unchanged(input);
    }

    private static RecordingUpgradeResult UpgradeNestedArchiveBundle(byte[] outerBytes)
    {
        var inner = ReadZipEntry(outerBytes, InnerRecordingEntry);
        var (changed, newInner) = RewriteArchiveSnapshots(inner);
        return changed
            ? RecordingUpgradeResult.Migrated(ReplaceZipEntry(outerBytes, InnerRecordingEntry, newInner))
            : RecordingUpgradeResult.Unchanged(outerBytes);
    }

    private static RecordingUpgradeResult UpgradeLegacyInlineBundle(byte[] outerBytes, string innerEntry)
    {
        var inner = ReadZipEntry(outerBytes, innerEntry);
        var result = UpgradeSessionRecordingBytes(inner);
        if (result.NeedsResimulation)
        {
            return RecordingUpgradeResult.NeedsResim(outerBytes);
        }
        return result.Changed
            ? RecordingUpgradeResult.Migrated(ReplaceZipEntry(outerBytes, innerEntry, result.Output))
            : RecordingUpgradeResult.Unchanged(outerBytes);
    }

    // --- v4 archive snapshot rewrite: entry-by-entry, Store framing + brotli content (matches
    //     RecordingArchiveWriter). Non-snapshot entries are copied verbatim so layouts, geojson,
    //     manifest, and bug-bundle logs survive untouched. ---

    private static (bool Changed, byte[] Output) RewriteArchiveSnapshots(byte[] archiveBytes)
    {
        using var source = new MemoryStream(archiveBytes, writable: false);
        using var sourceZip = new ZipArchive(source, ZipArchiveMode.Read);

        var bays = ResolveArchiveBays(sourceZip);

        bool changed = false;
        using var output = new MemoryStream();
        using (var destZip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in sourceZip.Entries)
            {
                var content = ReadEntryBytes(entry);
                if (IsSnapshotEntry(entry.FullName))
                {
                    content = MigrateSnapshotEntry(content, out var entryChanged);
                    changed |= entryChanged;
                }
                else if (entry.FullName == ActionsEntry)
                {
                    content = MigrateActionsEntry(content, bays, out var entryChanged);
                    changed |= entryChanged;
                }

                var dest = destZip.CreateEntry(entry.FullName, CompressionLevel.NoCompression);
                using var ds = dest.Open();
                ds.Write(content);
            }
        }

        return changed ? (true, output.ToArray()) : (false, archiveBytes);
    }

    /// <summary>
    /// The accessible bays of the recorded session's student position, read from the
    /// archive's own <c>artcc-config.json.br</c> plus the student position in the
    /// first snapshot's scenario block (the scenario JSON does not carry it — the
    /// server resolves it at load). Empty when either is absent, which leaves the
    /// action log untouched.
    /// </summary>
    private static IReadOnlyList<AccessibleBay> ResolveArchiveBays(ZipArchive zip)
    {
        var configEntry = zip.GetEntry(ArtccConfigEntry);
        if (configEntry is null)
        {
            return [];
        }

        var snapshotEntry = zip.Entries.Where(e => IsSnapshotEntry(e.FullName)).OrderBy(e => e.FullName, StringComparer.Ordinal).FirstOrDefault();
        if (snapshotEntry is null)
        {
            return [];
        }

        var snapshotJson = DecompressBrotli(ReadEntryBytes(snapshotEntry));
        var snapshot = JsonSerializer.Deserialize<StateSnapshotDto>(snapshotJson, RecordingJsonOptions.Default);
        var positionCallsign = snapshot?.Scenario.StudentPosition?.Callsign;
        var scenarioEntry = zip.GetEntry(ScenarioEntry);
        var scenarioJson = scenarioEntry is null ? null : DecompressBrotli(ReadEntryBytes(scenarioEntry));

        return ResolveBays(DecompressBrotli(ReadEntryBytes(configEntry)), positionCallsign, scenarioJson);
    }

    private static byte[] MigrateActionsEntry(byte[] brotliContent, IReadOnlyList<AccessibleBay> bays, out bool changed)
    {
        if (bays.Count == 0)
        {
            changed = false;
            return brotliContent;
        }

        var json = DecompressBrotli(brotliContent);
        var actions =
            JsonSerializer.Deserialize<List<RecordedAction>>(json, RecordingJsonOptions.Default)
            ?? throw new InvalidOperationException("Failed to deserialize actions entry.");

        changed = QualifyStripBays(actions, bays);
        return changed ? RecordingCompression.Compress(JsonSerializer.SerializeToUtf8Bytes(actions, RecordingJsonOptions.Default)) : brotliContent;
    }

    private static byte[] MigrateSnapshotEntry(byte[] brotliContent, out bool changed)
    {
        // Snapshot entries are always Brotli — decompress explicitly (mirroring
        // RecordingArchive.ReadBrotliEntry) rather than via the autodetecting
        // RecordingCompression.Decompress, whose plain-JSON heuristic can misfire on a Brotli stream
        // whose first byte happens to be '{' or '['.
        var json = DecompressBrotli(brotliContent);
        var dto =
            JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default)
            ?? throw new InvalidOperationException("Failed to deserialize snapshot entry.");

        if (dto.SchemaVersion == SnapshotSchemaMigrator.CurrentSchemaVersion)
        {
            changed = false;
            return brotliContent;
        }

        SnapshotSchemaMigrator.Migrate(dto);
        changed = true;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(dto, RecordingJsonOptions.Default);
        return RecordingCompression.Compress(bytes);
    }

    private static bool MigrateSnapshots(List<TimedSnapshot> snapshots)
    {
        bool changed = false;
        foreach (var snapshot in snapshots)
        {
            if (snapshot.State.SchemaVersion != SnapshotSchemaMigrator.CurrentSchemaVersion)
            {
                SnapshotSchemaMigrator.Migrate(snapshot.State);
                changed = true;
            }
        }

        return changed;
    }

    private static bool IsSnapshotEntry(string name) =>
        name.StartsWith("snapshots/", StringComparison.Ordinal) && name.EndsWith(".json.br", StringComparison.Ordinal);

    // --- outer-bundle zip helpers: Deflate framing (the bundle wraps already-compressed content). ---

    private static byte[] ReadZipEntry(byte[] zipBytes, string entryName)
    {
        using var ms = new MemoryStream(zipBytes, writable: false);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var entry = zip.GetEntry(entryName) ?? throw new InvalidOperationException($"Zip entry not found: {entryName}");
        return ReadEntryBytes(entry);
    }

    private static byte[] ReplaceZipEntry(byte[] zipBytes, string entryName, byte[] newContent)
    {
        using var source = new MemoryStream(zipBytes, writable: false);
        using var sourceZip = new ZipArchive(source, ZipArchiveMode.Read);

        using var output = new MemoryStream();
        using (var destZip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in sourceZip.Entries)
            {
                var content = string.Equals(entry.FullName, entryName, StringComparison.Ordinal) ? newContent : ReadEntryBytes(entry);
                var dest = destZip.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                using var ds = dest.Open();
                ds.Write(content);
            }
        }

        return output.ToArray();
    }

    private static byte[] ReadEntryBytes(ZipArchiveEntry entry)
    {
        using var es = entry.Open();
        using var ms = new MemoryStream();
        es.CopyTo(ms);
        return ms.ToArray();
    }

    private static string DecompressBrotli(byte[] brotliBytes)
    {
        using var input = new MemoryStream(brotliBytes);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(brotli);
        return reader.ReadToEnd();
    }
}
