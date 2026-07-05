using System.IO.Compression;
using System.Text.Json;
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

        if (!MigrateSnapshots(recording.Snapshots!))
        {
            return RecordingUpgradeResult.Unchanged(input);
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(recording, RecordingJsonOptions.Default);
        return RecordingUpgradeResult.Migrated(RecordingCompression.Compress(bytes));
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

                var dest = destZip.CreateEntry(entry.FullName, CompressionLevel.NoCompression);
                using var ds = dest.Open();
                ds.Write(content);
            }
        }

        return changed ? (true, output.ToArray()) : (false, archiveBytes);
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
