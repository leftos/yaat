using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

/// <summary>
/// A settings bundle is untrusted input: imported preference values must meet their key's rule, model sources may only
/// point at curated ids or huggingface.co, zip reads are bounded, null entries and unknown JSON fail naming the file, and
/// favorite sets that reuse an existing id are clashes whose final ids follow into the imported layouts.
/// </summary>
public class SettingsBundleHardeningTests : IDisposable
{
    private const string MacrosEntryName = "macros.yaat-macros.json";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "yaat-settingsbundle-hardening-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void EveryBundledKey_HasARuleForItsOwnType()
    {
        foreach (string key in UserPreferences.BundledPreferenceKeys)
        {
            Assert.True(UserPreferences.BundledPreferenceRules.ContainsKey(key), $"{key} has no rule");
            BundledPreferenceRule rule = UserPreferences.BundledPreferenceRules[key];
            Assert.True(rule.ValueType == UserPreferences.PreferenceType(key), $"{key}'s rule is for {rule.ValueType.Name}");
        }
    }

    [Fact]
    public void DefaultPreferences_ExportEveryKey_AndPassTheirOwnRules()
    {
        JsonObject defaults = UserPreferences.CreateDefaults().ExportBundlePreferences();

        List<string> rejected = [.. defaults.Where(p => !UserPreferences.TryReadBundledPreference(p.Key, p.Value, out _)).Select(p => p.Key)];

        Assert.Empty(rejected);
        Assert.Equal(UserPreferences.BundledPreferenceKeys, defaults.Select(p => p.Key));
    }

    [Theory]
    [InlineData("groundTaxiwayColor", "\"#80FF00AA\"", true)]
    [InlineData("groundTaxiwayColor", "\"#12345\"", false)]
    [InlineData("groundTaxiwayColor", "\"red\"", false)]
    [InlineData("terminalFontSize", "24", true)]
    [InlineData("terminalFontSize", "25", false)]
    [InlineData("speechSampleCacheMaxMb", "4096", false)]
    [InlineData("tpaConeHalfAngleDegrees", "30.0", true)]
    [InlineData("tpaConeHalfAngleDegrees", "30.5", false)]
    [InlineData("vfrCommandsForIfr", "\"All\"", true)]
    [InlineData("vfrCommandsForIfr", "\"2\"", false)]
    [InlineData("vfrCommandsForIfr", "\"Sometimes\"", false)]
    [InlineData("aircraftSelectKey", "\"Ctrl+Shift+T\"", true)]
    [InlineData("aircraftSelectKey", "\"Ctrl+<b>\"", false)]
    [InlineData("departureAutoDeleteDistanceNm", "null", true)]
    [InlineData("departureAutoDeleteDistanceNm", "0.5", false)]
    [InlineData("autoAcceptEnabled", "null", false)]
    [InlineData("autoAcceptEnabled", "\"yes\"", false)]
    public void ImportedValue_IsCheckedAgainstItsKeysRule(string key, string json, bool accepted) =>
        Assert.Equal(accepted, UserPreferences.TryReadBundledPreference(key, JsonNode.Parse(json), out _));

    [Fact]
    public void PreferencesImport_IgnoresValuesOutsideTheirRule_WithoutWriting()
    {
        var preferences = new UserPreferences();
        string before = preferences.ExportBundlePreferences().ToJsonString();
        var incoming = new JsonObject
        {
            ["groundTaxiwayColor"] = "not-a-colour",
            ["speechSampleCacheMaxMb"] = 4096,
            ["terminalFontSize"] = 99,
            ["tpaConeHalfAngleDegrees"] = 45.0,
            ["vfrCommandsForIfr"] = "Sometimes",
        };

        PreferencesImportResult result = preferences.ImportBundlePreferences(incoming);

        Assert.Empty(result.AppliedKeys);
        Assert.Equal(incoming.Select(p => p.Key), result.IgnoredKeys);
        Assert.Equal(before, preferences.ExportBundlePreferences().ToJsonString());
    }

    [Fact]
    public void PreferencesImport_IgnoresARunDelayMinimumAboveItsMaximum()
    {
        var preferences = new UserPreferences();
        var incoming = new JsonObject { ["commandRunDelayMinSeconds"] = 50, ["commandRunDelayMaxSeconds"] = 10 };

        PreferencesImportResult result = preferences.ImportBundlePreferences(incoming);

        Assert.Empty(result.AppliedKeys);
        Assert.Equal(["commandRunDelayMinSeconds", "commandRunDelayMaxSeconds"], result.IgnoredKeys);
    }

    [Theory]
    [InlineData("gemma4:e4b", true)]
    [InlineData("https://huggingface.co/org/model/resolve/main/m.bin", true)]
    [InlineData("https://cdn-lfs.huggingface.co/org/m.bin", true)]
    [InlineData("http://huggingface.co/org/m.bin", false)]
    [InlineData("https://huggingface.co.evil.example/m.bin", false)]
    [InlineData("https://example.com/m.bin", false)]
    [InlineData("https://user:secret@huggingface.co/m.bin", false)]
    [InlineData("https://huggingface.co/m.bin?token=abc", false)]
    [InlineData(@"C:\models\m.gguf", false)]
    [InlineData("models/m.gguf", false)]
    [InlineData("C:model.gguf", false)]
    [InlineData("whisper-medium", true)]
    public void ModelSource_ImportAcceptsOnlyCuratedIdsAndHuggingFace(string source, bool accepted) =>
        Assert.Equal(accepted, BundledPreferenceRule.IsImportableModelSource(source));

    [Theory]
    [InlineData("gemma4:e4b", true)]
    [InlineData("https://example.com/m.bin", true)]
    [InlineData("https://user@example.com/m.bin", false)]
    [InlineData("https://example.com/m.bin?sig=abc", false)]
    [InlineData(@"C:\models\m.gguf", false)]
    [InlineData("C:model.gguf", false)]
    [InlineData("d:m.bin", false)]
    public void ModelSource_ExportLeavesOutPathsUserPartsAndQueries(string source, bool exported) =>
        Assert.Equal(exported, BundledPreferenceRule.IsExportableModelSource(source));

    [Fact]
    public void OversizedEntry_FailsNamingTheFileAndEntry()
    {
        byte[] bundle = BundleWithMacrosEntryOf(SettingsBundleFile.MaxEntryBytes + 1);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() =>
            SettingsBundleFile.Read("big.yaat-settings.zip", new MemoryStream(bundle))
        );

        Assert.Contains("big.yaat-settings.zip", ex.Message);
        Assert.Contains(MacrosEntryName, ex.Message);
    }

    [Fact]
    public void LyingEntryHeader_NeverReadsPastTheCeiling()
    {
        byte[] bundle = BundleWithMacrosEntryOf(SettingsBundleFile.MaxEntryBytes + 1);
        PatchUncompressedSize(bundle, MacrosEntryName, 16);

        try
        {
            SettingsBundle read = SettingsBundleFile.Read("liar.yaat-settings.zip", new MemoryStream(bundle));
            Assert.All(read.Entries, e => Assert.True(e.Content.Length <= SettingsBundleFile.MaxEntryBytes));
        }
        catch (InvalidDataException ex)
        {
            Assert.Contains("liar.yaat-settings.zip", ex.Message);
            Assert.Contains(MacrosEntryName, ex.Message);
        }
    }

    [Theory]
    [InlineData("macros.yaat-macros.json", "[null]")]
    [InlineData("macros.yaat-macros.json", "[{\"name\": null, \"expansion\": \"CTO\"}]")]
    [InlineData("layouts.json", "[null]")]
    [InlineData("layouts.json", "[{\"name\": null}]")]
    [InlineData("layouts.json", "[{\"name\": \"GC\", \"loadedFavoriteSetIds\": [null]}]")]
    public void NullEntries_FailNamingTheFile(string fileName, string json)
    {
        SettingsBundle bundle = SettingsBundleFile.Read(fileName, new MemoryStream(Encoding.UTF8.GetBytes(json)));
        SettingsBundleEntry entry = Assert.Single(bundle.Entries);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() =>
            SettingsImportPlanner.Plan(entry, SettingsImportMode.Replace, NewTarget())
        );

        Assert.Contains(fileName, ex.Message);
    }

    [Fact]
    public void LonePreferencesJson_ReadsAsPreferences()
    {
        SettingsBundle bundle = SettingsBundleFile.Read("preferences.json", new MemoryStream(Encoding.UTF8.GetBytes("{\"showAtpa\": true}")));

        Assert.Equal(SettingsItemType.Preferences, Assert.Single(bundle.Entries).ItemType);
    }

    [Fact]
    public void UnknownJson_FailsNamingTheFile()
    {
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() =>
            SettingsBundleFile.Read("random.json", new MemoryStream(Encoding.UTF8.GetBytes("{\"hello\": 1}")))
        );

        Assert.Contains("random.json", ex.Message);
    }

    [Fact]
    public void FavoriteSetReusingAnExistingIdUnderAnotherName_IsAClash()
    {
        InMemorySettingsImportTarget target = NewTarget();
        FavoriteSet existing = target.Favorites.CreateNamedSet("Old name")!;
        var incoming = new FavoriteSet
        {
            Id = existing.Id,
            Kind = FavoriteSetKind.Named,
            Name = "New name",
        };
        var entry = new SettingsBundleEntry(
            SettingsItemType.Favorites,
            SettingsBundleFormats.FavoritesJson,
            "set.json",
            JsonSerializer.SerializeToUtf8Bytes(incoming, UserPreferences.JsonOptions)
        );

        SettingsImportPlan plan = SettingsImportPlanner.Plan(entry, SettingsImportMode.Merge, target);
        ImportClash clash = Assert.Single(plan.Clashes);
        clash.Choice = ClashChoice.Skip;
        SettingsImportPlanner.Apply(plan, target);

        Assert.Equal("New name", clash.IncomingName);
        Assert.Equal("Old name", clash.ExistingName);
        Assert.Equal("Old name", target.Favorites.GetSet(existing.Id)!.Name);
    }

    [Theory]
    [InlineData(ClashChoice.Overwrite)]
    [InlineData(ClashChoice.Skip)]
    public void ApplyAll_PointsImportedLayoutsAtTheImportedFavoriteSets(ClashChoice choice)
    {
        InMemorySettingsImportTarget target = NewTarget();
        FavoriteSet existing = target.Favorites.CreateNamedSet("Tower")!;
        FavoriteStore source = new(Path.Combine(_root, "source"));
        FavoriteSet incoming = source.CreateNamedSet("Tower")!;
        SettingsBundleEntry favorites = SettingsBundleItems.Favorites(source, [incoming.Id]);
        SettingsBundleEntry layouts = SettingsBundleItems.Layouts([
            new SavedLayout { Name = "GC", LoadedFavoriteSetIds = [incoming.Id, "elsewhere"] },
        ]);
        SettingsImportPlan layoutsPlan = SettingsImportPlanner.Plan(layouts, SettingsImportMode.Merge, target);
        SettingsImportPlan favoritesPlan = SettingsImportPlanner.Plan(favorites, SettingsImportMode.Merge, target);
        favoritesPlan.Clashes[0].Choice = choice;

        IReadOnlyList<SettingsImportResult> results = SettingsImportPlanner.ApplyAll([layoutsPlan, favoritesPlan], target);

        Assert.Equal([SettingsItemType.Favorites, SettingsItemType.Layouts], results.Select(r => r.ItemType));
        List<string> expected = (choice == ClashChoice.Overwrite) ? [existing.Id, "elsewhere"] : ["elsewhere"];
        Assert.Equal(expected, Assert.Single(target.LayoutList).LoadedFavoriteSetIds);
    }

    private InMemorySettingsImportTarget NewTarget() => new(new FavoriteStore(Path.Combine(_root, "target")));

    private static byte[] BundleWithMacrosEntryOf(long size)
    {
        const string Manifest = """
            {
              "bundleVersion": 1,
              "writtenBy": "test",
              "entries": [{ "itemType": "macros", "format": "macros-json", "fileName": "macros.yaat-macros.json" }]
            }
            """;
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (Stream manifest = zip.CreateEntry(SettingsBundleFile.ManifestFileName).Open())
            {
                manifest.Write(Encoding.UTF8.GetBytes(Manifest));
            }

            using Stream content = zip.CreateEntry(MacrosEntryName).Open();
            byte[] chunk = new byte[1024 * 1024];
            for (long left = size; left > 0; left -= chunk.Length)
            {
                content.Write(chunk, 0, (int)Math.Min(chunk.Length, left));
            }
        }

        return buffer.ToArray();
    }

    // Rewrites the entry's uncompressed size in its local header and its central directory record.
    private static void PatchUncompressedSize(byte[] zip, string entryName, uint size)
    {
        byte[] name = Encoding.UTF8.GetBytes(entryName);
        for (int i = 0; i + 46 < zip.Length; i++)
        {
            uint signature = BitConverter.ToUInt32(zip, i);
            if ((signature == 0x04034b50) && NameAt(zip, i + 30, BitConverter.ToUInt16(zip, i + 26), name))
            {
                BitConverter.TryWriteBytes(zip.AsSpan(i + 22, 4), size);
            }
            else if ((signature == 0x02014b50) && NameAt(zip, i + 46, BitConverter.ToUInt16(zip, i + 28), name))
            {
                BitConverter.TryWriteBytes(zip.AsSpan(i + 24, 4), size);
            }
        }
    }

    private static bool NameAt(byte[] zip, int offset, int length, byte[] name) =>
        (length == name.Length) && (offset + length <= zip.Length) && zip.AsSpan(offset, length).SequenceEqual(name);
}
