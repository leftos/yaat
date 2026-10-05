using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Yaat.Client.Services;

/// <summary>
/// Reads and writes the settings bundle (<c>*.yaat-settings.zip</c>): a zip holding <c>manifest.json</c> plus one file
/// per included item, each byte-identical to the single-item file that item type exports on its own.
///
/// Reading also accepts every single-item file exported before bundles existed (<c>*.yaat-macros.json</c>,
/// <c>*.yaat-verbs.json</c>, <c>*.yaat-favset.zip</c>, <c>*.yaat-favlibrary.zip</c>, <c>*.yaat-grid-layout.json</c>, a
/// bare favorite or favorite set <c>.json</c>) and a lone <c>preferences.json</c> or <c>layouts.json</c> as a one-entry
/// bundle, so old exports import through the same path. Every read is bounded by <see cref="MaxEntryBytes"/> and
/// <see cref="MaxBundleBytes"/>.
/// </summary>
public static class SettingsBundleFile
{
    /// <summary>The extension (including the dot) of a settings bundle.</summary>
    public const string Extension = ".yaat-settings.zip";

    /// <summary>The bundle version this build writes. A reader skips the entries of a newer bundle it cannot read.</summary>
    public const int CurrentBundleVersion = 1;

    /// <summary>The manifest's file name inside the bundle.</summary>
    public const string ManifestFileName = "manifest.json";

    /// <summary>The most one entry (or one single-item file) may unpack to: 32 MB.</summary>
    public const long MaxEntryBytes = 32L * 1024 * 1024;

    /// <summary>The most the entries of one file may unpack to together: 128 MB.</summary>
    public const long MaxBundleBytes = 128L * 1024 * 1024;

    /// <summary>Writes a bundle holding the entries, in order, with a manifest naming each one.</summary>
    /// <param name="entries">At most one entry per item type, with distinct file names other than the manifest's.</param>
    /// <param name="writtenBy">The client version string, recorded in the manifest.</param>
    /// <param name="output">The stream the zip is written to; left open.</param>
    /// <exception cref="ArgumentException">Two entries share an item type or a file name, or one is named like the manifest.</exception>
    public static void Write(IReadOnlyList<SettingsBundleEntry> entries, string writtenBy, Stream output)
    {
        EnsureWritable(entries);
        var manifest = new Manifest
        {
            BundleVersion = CurrentBundleVersion,
            WrittenBy = writtenBy,
            Entries =
            [
                .. entries.Select(e => new ManifestEntry
                {
                    ItemType = SettingsBundleFormats.ItemTypeName(e.ItemType),
                    Format = e.Format,
                    FileName = e.FileName,
                }),
            ],
        };

        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        WriteZipEntry(zip, ManifestFileName, JsonSerializer.SerializeToUtf8Bytes(manifest, UserPreferences.JsonOptions));
        foreach (SettingsBundleEntry entry in entries)
        {
            WriteZipEntry(zip, entry.FileName, entry.Content);
        }
    }

    /// <summary>
    /// The extension an export of these entries carries: the item's own single-item extension when exactly one entry is
    /// exported and its type has one, otherwise <see cref="Extension"/>.
    /// </summary>
    public static string ExportExtension(IReadOnlyList<SettingsBundleEntry> entries) =>
        SingleItemExport(entries) is { } single ? single.Extension : Extension;

    /// <summary>
    /// Writes an export of the entries: a single entry whose type has a single-item file is written as that file, byte for
    /// byte, so older YAAT versions can import it; anything else is written as a bundle. Name the file with
    /// <see cref="ExportExtension"/>.
    /// </summary>
    public static void WriteExport(IReadOnlyList<SettingsBundleEntry> entries, string writtenBy, Stream output)
    {
        if (SingleItemExport(entries) is { } single)
        {
            output.Write(single.Entry.Content);
            return;
        }

        Write(entries, writtenBy, output);
    }

    /// <summary>Reads a bundle, or a single-item file as a one-entry bundle.</summary>
    /// <param name="fileName">The file's name (a path is fine); its name and extension pick single-item file or bundle.</param>
    /// <param name="input">The file's contents.</param>
    /// <returns>The entries this version can import, and the manifest entries it skipped with the reason.</returns>
    /// <exception cref="InvalidDataException">
    /// The file is not a zip, has no readable manifest, is a JSON file of no known kind, or is over a size ceiling; the
    /// message names the file (and the entry, for an entry over its ceiling).
    /// </exception>
    public static SettingsBundle Read(string fileName, Stream input)
    {
        string name = Path.GetFileName(fileName);
        if (SettingsBundleFormats.SingleItemFormat(name) is { } single)
        {
            return SingleItemBundle(name, single.ItemType, single.Format, BoundedZipReader.ReadFile(input, name));
        }

        if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            byte[] content = BoundedZipReader.ReadFile(input, name);
            (SettingsItemType itemType, string format) = ClassifyLoneJson(name, content);
            return SingleItemBundle(name, itemType, format, content);
        }

        using ZipArchive zip = OpenZip(name, input);
        var reader = new BoundedZipReader(name);
        Manifest manifest = ReadManifest(name, zip, reader);
        return ReadEntries(name, manifest, zip, reader);
    }

    private static SettingsBundle SingleItemBundle(string name, SettingsItemType itemType, string format, byte[] content) =>
        new(name, 0, "", [new SettingsBundleEntry(itemType, format, name, content)], []);

    // A lone .json is preferences or layouts by its in-bundle name, otherwise a favorite or favorite set by its shape.
    private static (SettingsItemType ItemType, string Format) ClassifyLoneJson(string name, byte[] content)
    {
        if (string.Equals(name, SettingsBundleFormats.PreferencesFileName, StringComparison.OrdinalIgnoreCase))
        {
            return (SettingsItemType.Preferences, SettingsBundleFormats.PreferencesJson);
        }

        if (string.Equals(name, SettingsBundleFormats.LayoutsFileName, StringComparison.OrdinalIgnoreCase))
        {
            return (SettingsItemType.Layouts, SettingsBundleFormats.LayoutsJson);
        }

        JsonNode? root;
        try
        {
            using var stream = new MemoryStream(content);
            root = JsonNode.Parse(stream);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"'{name}' is not a YAAT settings file: it is not valid JSON.", ex);
        }

        bool isFavoriteShape =
            (root is JsonObject entity)
            && (
                entity.ContainsKey("favoriteIds") || entity.ContainsKey("kind") || entity.ContainsKey("commandText") || entity.ContainsKey("isSpacer")
            );
        return isFavoriteShape
            ? (SettingsItemType.Favorites, SettingsBundleFormats.FavoritesJson)
            : throw new InvalidDataException(
                $"'{name}' is not a YAAT settings file: it is not a favorite, a favorite set, preferences.json or layouts.json."
            );
    }

    private static (SettingsBundleEntry Entry, string Extension)? SingleItemExport(IReadOnlyList<SettingsBundleEntry> entries) =>
        ((entries is [{ } only]) && (SettingsBundleFormats.SingleItemExtension(only.ItemType, only.Format) is { } extension))
            ? (only, extension)
            : null;

    private static void EnsureWritable(IReadOnlyList<SettingsBundleEntry> entries)
    {
        if (entries.GroupBy(e => e.ItemType).FirstOrDefault(g => g.Count() > 1) is { } duplicateType)
        {
            throw new ArgumentException($"A settings bundle holds one entry per item type; {duplicateType.Key} appears twice.", nameof(entries));
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ManifestFileName };
        foreach (SettingsBundleEntry entry in entries)
        {
            if (!names.Add(entry.FileName))
            {
                throw new ArgumentException(
                    $"Settings bundle entry file name '{entry.FileName}' is used twice or clashes with the manifest.",
                    nameof(entries)
                );
            }
        }
    }

    private static ZipArchive OpenZip(string name, Stream input)
    {
        try
        {
            return new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"'{name}' is not a YAAT settings bundle: it is not a zip file.", ex);
        }
    }

    private static Manifest ReadManifest(string name, ZipArchive zip, BoundedZipReader reader)
    {
        ZipArchiveEntry manifestEntry =
            zip.GetEntry(ManifestFileName)
            ?? throw new InvalidDataException($"'{name}' is not a YAAT settings bundle: it has no {ManifestFileName}.");

        Manifest? manifest;
        try
        {
            using var stream = new MemoryStream(reader.Read(manifestEntry));
            manifest = JsonSerializer.Deserialize<Manifest>(stream, UserPreferences.JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"'{name}' has an unreadable {ManifestFileName}: {ex.Message}", ex);
        }

        if ((manifest is null) || (manifest.BundleVersion < 1))
        {
            throw new InvalidDataException($"'{name}' has an unreadable {ManifestFileName}: it has no bundle version.");
        }

        return manifest;
    }

    private static SettingsBundle ReadEntries(string name, Manifest manifest, ZipArchive zip, BoundedZipReader reader)
    {
        var entries = new List<SettingsBundleEntry>();
        var skipped = new List<SkippedBundleEntry>();
        foreach (ManifestEntry listed in manifest.Entries ?? [])
        {
            if (TryAccept(listed, zip, entries, out AcceptedEntry? accepted, out string reason))
            {
                entries.Add(new SettingsBundleEntry(accepted.ItemType, accepted.Format, accepted.ZipEntry.FullName, reader.Read(accepted.ZipEntry)));
            }
            else
            {
                skipped.Add(new SkippedBundleEntry(listed.ItemType ?? "", listed.Format ?? "", listed.FileName ?? "", reason));
            }
        }

        return new SettingsBundle(name, manifest.BundleVersion, manifest.WrittenBy ?? "", entries, skipped);
    }

    private static bool TryAccept(
        ManifestEntry listed,
        ZipArchive zip,
        List<SettingsBundleEntry> accepted,
        [NotNullWhen(true)] out AcceptedEntry? entry,
        out string reason
    )
    {
        entry = null;
        if (SettingsBundleFormats.ParseItemType(listed.ItemType) is not { } itemType)
        {
            reason = $"this version does not know the item type '{listed.ItemType}'";
        }
        else if ((listed.Format is not { } format) || !SettingsBundleFormats.IsKnownFormat(itemType, format))
        {
            reason = $"this version does not know the format '{listed.Format}'";
        }
        else if (accepted.Any(e => e.ItemType == itemType))
        {
            reason = "the bundle lists this item type twice; the first entry is imported";
        }
        else if (string.IsNullOrEmpty(listed.FileName) || (zip.GetEntry(listed.FileName) is not { } zipEntry))
        {
            reason = $"the file '{listed.FileName}' is missing from the bundle";
        }
        else
        {
            entry = new AcceptedEntry(itemType, format, zipEntry);
            reason = "";
        }

        return entry is not null;
    }

    private static void WriteZipEntry(ZipArchive zip, string entryName, byte[] content)
    {
        ZipArchiveEntry entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using Stream stream = entry.Open();
        stream.Write(content);
    }

    private sealed record AcceptedEntry(SettingsItemType ItemType, string Format, ZipArchiveEntry ZipEntry);

    private sealed class Manifest
    {
        public int BundleVersion { get; set; }
        public string? WrittenBy { get; set; }
        public List<ManifestEntry>? Entries { get; set; }
    }

    private sealed class ManifestEntry
    {
        public string? ItemType { get; set; }
        public string? Format { get; set; }
        public string? FileName { get; set; }
    }
}
