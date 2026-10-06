using System.ComponentModel;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Client.Services;
using Yaat.Sim.Commands;

namespace Yaat.Client.Tests;

/// <summary>
/// Writing and reading <c>.yaat-settings.zip</c> bundles: the six item types round-trip, a single legacy item exports as
/// today's file, today's files still import, and unreadable or newer bundles fail or degrade as documented.
/// </summary>
public class SettingsBundleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "yaat-settingsbundle-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void SettingsBundleRoundTrip_AllSixItems()
    {
        var preferences = new UserPreferences();
        FavoriteStore source = SeededFavorites("source", "Tower", "FH 270");
        FavoriteSet towerSet = source.FindNamedSet("Tower")!;
        List<SettingsBundleEntry> entries =
        [
            SettingsBundleItems.Preferences(preferences),
            SettingsBundleItems.Macros(SampleMacros()),
            SettingsBundleItems.Verbs(SampleScheme()),
            SettingsBundleItems.Favorites(source, [towerSet.Id]),
            SettingsBundleItems.GridLayout(SampleGridLayout()),
            SettingsBundleItems.Layouts(SampleLayouts()),
        ];

        SettingsBundle bundle = WriteAndRead(entries, "YAAT 1.2.3");

        Assert.Equal(SettingsBundleFile.CurrentBundleVersion, bundle.BundleVersion);
        Assert.Equal("YAAT 1.2.3", bundle.WrittenBy);
        Assert.Empty(bundle.Skipped);
        Assert.Equal(entries.Select(e => e.ItemType), bundle.Entries.Select(e => e.ItemType));
        Assert.All(entries.Zip(bundle.Entries), pair => Assert.Equal(pair.First.Content, pair.Second.Content));

        var target = new InMemorySettingsImportTarget(NewStore("target"));
        foreach (SettingsBundleEntry entry in bundle.Entries)
        {
            SettingsImportPlan plan = SettingsImportPlanner.Plan(entry, DefaultMode(entry.ItemType), target);
            Assert.Empty(plan.Clashes);
            SettingsImportPlanner.Apply(plan, target);
        }

        Assert.True(JsonNode.DeepEquals(preferences.ExportBundlePreferences(), target.Preferences));
        Assert.Equal(Json(SampleMacros()), Json(target.MacroList));
        Assert.Equal(["FH", "HDG"], target.AppliedVerbs[CanonicalCommandType.FlyHeading]);
        Assert.Equal(Json(SampleGridLayout()), Json(target.GridLayout));
        Assert.Equal(Json(SampleLayouts()), Json(target.LayoutList));
        FavoriteSet imported = target.Favorites.FindNamedSet("Tower")!;
        Assert.Equal(towerSet.Id, imported.Id);
        Assert.Equal(["FH 270"], target.Favorites.GetSetFavorites(imported.Id).Select(f => f.Label));
    }

    [Theory]
    [InlineData(SettingsItemType.Macros)]
    [InlineData(SettingsItemType.Verbs)]
    [InlineData(SettingsItemType.Favorites)]
    [InlineData(SettingsItemType.GridLayout)]
    public async Task SingleItemExport_IsByteIdenticalToTheLegacySerializer(SettingsItemType type)
    {
        FavoriteStore store = SeededFavorites("source", "Tower", "FH 270");
        (SettingsBundleEntry entry, byte[] legacy, string extension) = type switch
        {
            SettingsItemType.Macros => (SettingsBundleItems.Macros(SampleMacros()), await LegacyMacroBytes(), ".yaat-macros.json"),
            SettingsItemType.Verbs => (SettingsBundleItems.Verbs(SampleScheme()), await LegacyVerbBytes(), CommandSchemeFile.Extension),
            SettingsItemType.Favorites => (
                SettingsBundleItems.Favorites(store, []),
                LegacyLibraryBytes(store),
                FavoriteExport.LibraryExportExtension
            ),
            _ => (SettingsBundleItems.GridLayout(SampleGridLayout()), await LegacyGridLayoutBytes(), ".yaat-grid-layout.json"),
        };

        using var exported = new MemoryStream();
        SettingsBundleFile.WriteExport([entry], "YAAT test", exported);

        Assert.Equal(extension, SettingsBundleFile.ExportExtension([entry]));
        if (type == SettingsItemType.Favorites)
        {
            // A zip stamps each entry with the time it was written, so two exports compare by their entries.
            AssertSameZipEntries(legacy, exported.ToArray());
        }
        else
        {
            Assert.Equal(legacy, exported.ToArray());
        }
    }

    [Fact]
    public void TwoItemExport_IsABundle()
    {
        SettingsBundleEntry[] entries = [SettingsBundleItems.Macros(SampleMacros()), SettingsBundleItems.Layouts(SampleLayouts())];

        using var exported = new MemoryStream();
        SettingsBundleFile.WriteExport(entries, "YAAT test", exported);
        exported.Position = 0;

        Assert.Equal(SettingsBundleFile.Extension, SettingsBundleFile.ExportExtension(entries));
        Assert.Equal(2, SettingsBundleFile.Read("two" + SettingsBundleFile.Extension, exported).Entries.Count);
    }

    [Theory]
    [InlineData("shared.yaat-macros.json")]
    [InlineData("shared.yaat-verbs.json")]
    [InlineData("shared.yaat-favset.zip")]
    [InlineData("shared.yaat-favlibrary.zip")]
    [InlineData("shared.json")]
    [InlineData("shared.yaat-grid-layout.json")]
    public async Task LegacySingleFiles_StillImport(string fileName)
    {
        FavoriteStore source = SeededFavorites("source", "Tower", "FH 270");
        byte[] content = await LegacyFileBytes(fileName, source);

        SettingsBundle bundle = SettingsBundleFile.Read(fileName, new MemoryStream(content));

        SettingsBundleEntry entry = Assert.Single(bundle.Entries);
        Assert.Empty(bundle.Skipped);
        var target = new InMemorySettingsImportTarget(NewStore("target"));
        SettingsImportPlanner.Apply(SettingsImportPlanner.Plan(entry, DefaultMode(entry.ItemType), target), target);
        AssertLegacyFileImported(fileName, target);
    }

    [Fact]
    public void NewerBundleVersion_ImportsKnownEntries_AndReportsTheRest()
    {
        const string Manifest = """
            {
              "bundleVersion": 7,
              "writtenBy": "YAAT 9.0",
              "entries": [
                { "itemType": "macros", "format": "macros-json", "fileName": "macros.yaat-macros.json" },
                { "itemType": "hotkeys", "format": "hotkeys-json", "fileName": "hotkeys.json" },
                { "itemType": "layouts", "format": "layouts-json-v2", "fileName": "layouts.json" }
              ]
            }
            """;
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteZipEntry(zip, "manifest.json", Encoding.UTF8.GetBytes(Manifest));
            WriteZipEntry(zip, "macros.yaat-macros.json", SettingsBundleItems.Macros(SampleMacros()).Content);
            WriteZipEntry(zip, "hotkeys.json", Encoding.UTF8.GetBytes("{}"));
            WriteZipEntry(zip, "layouts.json", Encoding.UTF8.GetBytes("[]"));
        }

        buffer.Position = 0;
        SettingsBundle bundle = SettingsBundleFile.Read("future.yaat-settings.zip", buffer);

        Assert.Equal(7, bundle.BundleVersion);
        Assert.Equal("YAAT 9.0", bundle.WrittenBy);
        SettingsBundleEntry entry = Assert.Single(bundle.Entries);
        Assert.Equal(SettingsItemType.Macros, entry.ItemType);
        Assert.Equal(["hotkeys", "layouts"], bundle.Skipped.Select(s => s.ItemType));
        Assert.All(bundle.Skipped, s => Assert.False(string.IsNullOrWhiteSpace(s.Reason)));
    }

    [Fact]
    public void NotAZip_FailsNamingTheFile()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("this is plain text"));

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => SettingsBundleFile.Read("broken.yaat-settings.zip", input));

        Assert.Contains("broken.yaat-settings.zip", ex.Message);
        Assert.Contains("not a zip", ex.Message);
    }

    [Fact]
    public void MissingManifest_FailsNamingTheFile()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteZipEntry(zip, "macros.yaat-macros.json", SettingsBundleItems.Macros(SampleMacros()).Content);
        }

        buffer.Position = 0;
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => SettingsBundleFile.Read("nomanifest.yaat-settings.zip", buffer));

        Assert.Contains("nomanifest.yaat-settings.zip", ex.Message);
        Assert.Contains("manifest.json", ex.Message);
    }

    private static SettingsImportMode DefaultMode(SettingsItemType type) =>
        SettingsBundleFormats.SupportsMerge(type) ? SettingsImportMode.Merge : SettingsImportMode.Replace;

    private FavoriteStore NewStore(string name) => new(Path.Combine(_root, name));

    private FavoriteStore SeededFavorites(string storeName, string setName, string label)
    {
        FavoriteStore store = NewStore(storeName);
        FavoriteSet set = store.CreateNamedSet(setName)!;
        var favorite = new FavoriteCommand { Label = label, CommandText = label };
        store.SaveFavorite(favorite);
        store.AddToSet(set.Id, favorite.Id);
        return store;
    }

    private static List<SavedMacro> SampleMacros() =>
        [new SavedMacro { Name = "ILSAPP &rwy", Expansion = "CAPP I&rwy" }, new SavedMacro { Name = "DEP", Expansion = "CTO" }];

    private static CommandScheme SampleScheme()
    {
        var scheme = CommandScheme.Default();
        scheme.Patterns[CanonicalCommandType.FlyHeading].Aliases = ["FH", "HDG"];
        return scheme;
    }

    private static SavedGridLayout SampleGridLayout() =>
        new()
        {
            ColumnOrder = ["Callsign", "Type"],
            SortColumn = "Callsign",
            SortDirection = ListSortDirection.Descending,
            ColumnWidths = new Dictionary<string, double> { ["Callsign"] = 90 },
            HiddenColumns = ["Route"],
        };

    private static List<SavedLayout> SampleLayouts() =>
        [
            new SavedLayout
            {
                Name = "GC",
                IsTerminalPoppedOut = true,
                WindowGeometries = new Dictionary<string, SavedWindowGeometry>
                {
                    ["Main"] = new SavedWindowGeometry
                    {
                        X = 10,
                        Y = 20,
                        Width = 800,
                        Height = 600,
                    },
                },
            },
            new SavedLayout { Name = "LC", IsDataGridPoppedOut = true },
        ];

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, UserPreferences.JsonOptions);

    private static SettingsBundle WriteAndRead(IReadOnlyList<SettingsBundleEntry> entries, string writtenBy)
    {
        using var buffer = new MemoryStream();
        SettingsBundleFile.Write(entries, writtenBy, buffer);
        buffer.Position = 0;
        return SettingsBundleFile.Read("round-trip" + SettingsBundleFile.Extension, buffer);
    }

    // The legacy writers below are copied from the per-feature export handlers (Settings › Macros, Settings › Commands,
    // the column chooser, the favorites bar), so the comparison is against what those handlers write today.
    private static async Task<byte[]> LegacyMacroBytes()
    {
        using var stream = new MemoryStream();
        await JsonSerializer.SerializeAsync(stream, SampleMacros(), UserPreferences.JsonOptions);
        return stream.ToArray();
    }

    private async Task<byte[]> LegacyVerbBytes()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "legacy" + CommandSchemeFile.Extension);
        await File.WriteAllTextAsync(path, CommandSchemeFile.Serialize(SampleScheme()));
        return await File.ReadAllBytesAsync(path);
    }

    private static async Task<byte[]> LegacyGridLayoutBytes()
    {
        using var stream = new MemoryStream();
        await JsonSerializer.SerializeAsync(stream, SampleGridLayout(), UserPreferences.JsonOptions);
        return stream.ToArray();
    }

    private static byte[] LegacyLibraryBytes(FavoriteStore store)
    {
        using var stream = new MemoryStream();
        FavoriteExport.ExportLibrary(store, [], stream);
        return stream.ToArray();
    }

    private static byte[] LegacySetBytes(FavoriteStore store)
    {
        using var stream = new MemoryStream();
        FavoriteExport.ExportSet(store, store.FindNamedSet("Tower")!.Id, stream);
        return stream.ToArray();
    }

    // A lone favorite json, taken from inside a set export so it is exactly what the favorites exporter writes.
    private static byte[] LegacyFavoriteJsonBytes(FavoriteStore store)
    {
        using var zip = new ZipArchive(new MemoryStream(LegacySetBytes(store)), ZipArchiveMode.Read);
        ZipArchiveEntry entry = zip.Entries.Single(e => e.FullName.StartsWith("favorites/", StringComparison.Ordinal));
        using var content = new MemoryStream();
        using (Stream entryStream = entry.Open())
        {
            entryStream.CopyTo(content);
        }

        return content.ToArray();
    }

    private async Task<byte[]> LegacyFileBytes(string fileName, FavoriteStore store) =>
        fileName switch
        {
            _ when fileName.EndsWith(".yaat-macros.json", StringComparison.Ordinal) => await LegacyMacroBytes(),
            _ when fileName.EndsWith(".yaat-verbs.json", StringComparison.Ordinal) => await LegacyVerbBytes(),
            _ when fileName.EndsWith(".yaat-favset.zip", StringComparison.Ordinal) => LegacySetBytes(store),
            _ when fileName.EndsWith(".yaat-favlibrary.zip", StringComparison.Ordinal) => LegacyLibraryBytes(store),
            _ when fileName.EndsWith(".yaat-grid-layout.json", StringComparison.Ordinal) => await LegacyGridLayoutBytes(),
            _ => LegacyFavoriteJsonBytes(store),
        };

    private static void AssertLegacyFileImported(string fileName, InMemorySettingsImportTarget target)
    {
        if (fileName.EndsWith(".yaat-macros.json", StringComparison.Ordinal))
        {
            Assert.Equal(Json(SampleMacros()), Json(target.MacroList));
        }
        else if (fileName.EndsWith(".yaat-verbs.json", StringComparison.Ordinal))
        {
            Assert.Equal(["FH", "HDG"], target.AppliedVerbs[CanonicalCommandType.FlyHeading]);
        }
        else if (fileName.EndsWith(".yaat-grid-layout.json", StringComparison.Ordinal))
        {
            Assert.Equal(Json(SampleGridLayout()), Json(target.GridLayout));
        }
        else if (fileName.EndsWith(".zip", StringComparison.Ordinal))
        {
            FavoriteSet set = target.Favorites.FindNamedSet("Tower")!;
            Assert.Equal(["FH 270"], target.Favorites.GetSetFavorites(set.Id).Select(f => f.Label));
        }
        else
        {
            Assert.Equal(["FH 270"], target.Favorites.GetSetFavorites(target.Favorites.GlobalSet.Id).Select(f => f.Label));
        }
    }

    private static void AssertSameZipEntries(byte[] expected, byte[] actual)
    {
        Dictionary<string, byte[]> expectedEntries = ZipEntries(expected);
        Dictionary<string, byte[]> actualEntries = ZipEntries(actual);
        Assert.Equal(expectedEntries.Keys.Order(), actualEntries.Keys.Order());
        Assert.All(expectedEntries, pair => Assert.Equal(pair.Value, actualEntries[pair.Key]));
    }

    private static Dictionary<string, byte[]> ZipEntries(byte[] zipBytes)
    {
        using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            using var content = new MemoryStream();
            using (Stream entryStream = entry.Open())
            {
                entryStream.CopyTo(content);
            }

            entries[entry.FullName] = content.ToArray();
        }

        return entries;
    }

    private static void WriteZipEntry(ZipArchive zip, string name, byte[] content)
    {
        using Stream stream = zip.CreateEntry(name).Open();
        stream.Write(content);
    }
}
