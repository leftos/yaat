using System.IO.Compression;
using System.Text;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Sim.Commands;

namespace Yaat.Client.Tests;

/// <summary>
/// The Import / Export hub's view model: export writes one bundle for two or more ticked items and today's single-item
/// file for one; import previews a file's entries, offers Merge only where the item supports it, lists Merge clashes
/// with the planner's choices and rename errors, skips what this version cannot read, and applies to the import target.
/// </summary>
public class ImportExportViewModelTests : IDisposable
{
    private const string WrittenBy = "test-version";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "yaat-importexport-vm-tests", Guid.NewGuid().ToString("N"));

    public ImportExportViewModelTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Export_MacrosAndLayoutsTicked_WritesABundleWithExactlyThoseTwoEntries()
    {
        ImportExportViewModel vm = NewViewModel(NewTarget(), [SettingsItemType.Macros, SettingsItemType.Layouts], ImportExportTab.Export);

        SettingsExportPlan plan = Assert.IsType<SettingsExportPlan>(vm.PrepareExport());
        string path = Path.Combine(_root, plan.SuggestedFileName);
        Assert.True(vm.WriteExport(plan, path));

        Assert.Equal(SettingsBundleFile.Extension, plan.Extension);
        Assert.EndsWith(SettingsBundleFile.Extension, path, StringComparison.Ordinal);
        SettingsBundle bundle = ReadBundle(path);
        Assert.False(bundle.IsSingleItemFile);
        Assert.Equal(WrittenBy, bundle.WrittenBy);
        Assert.Equal([SettingsItemType.Macros, SettingsItemType.Layouts], bundle.Entries.Select(e => e.ItemType));
        Assert.Empty(bundle.Skipped);
    }

    [Fact]
    public void Export_MacrosOnly_WritesTodaysMacrosFile()
    {
        ImportExportViewModel vm = NewViewModel(NewTarget(), [SettingsItemType.Macros], ImportExportTab.Export);

        SettingsExportPlan plan = Assert.IsType<SettingsExportPlan>(vm.PrepareExport());
        string path = Path.Combine(_root, plan.SuggestedFileName);
        Assert.True(vm.WriteExport(plan, path));

        Assert.Equal(SettingsBundleFormats.MacrosExtension, plan.Extension);
        Assert.EndsWith(SettingsBundleFormats.MacrosExtension, path, StringComparison.Ordinal);
        Assert.Equal(new FakeExportSource().Export(SettingsItemType.Macros).Content, File.ReadAllBytes(path));
        SettingsBundle bundle = ReadBundle(path);
        Assert.True(bundle.IsSingleItemFile);
        Assert.Equal(SettingsItemType.Macros, Assert.Single(bundle.Entries).ItemType);
    }

    [Fact]
    public void Export_NothingTicked_CannotExport()
    {
        ImportExportViewModel vm = NewViewModel(NewTarget(), [], ImportExportTab.Export);

        Assert.All(vm.ExportItems, row => Assert.False(row.IsSelected));
        Assert.False(vm.CanExport);

        vm.ExportItems.Single(r => r.ItemType == SettingsItemType.Macros).IsSelected = true;

        Assert.True(vm.CanExport);
    }

    [Fact]
    public void ImportPreview_ThreeEntries_ListsThreeRows_AndOffersMergeOnlyWhereSupported()
    {
        string path = WriteBundle(
            "three.yaat-settings.zip",
            [
                SettingsBundleItems.Macros([Macro("DEP", "CTO")]),
                SettingsBundleItems.Verbs(CommandScheme.Default()),
                SettingsBundleItems.GridLayout(new SavedGridLayout { ColumnOrder = ["Callsign"] }),
            ]
        );
        ImportExportViewModel vm = NewViewModel(NewTarget(), [], ImportExportTab.Import);

        vm.OpenImportFile(path);

        Assert.Null(vm.ImportError);
        Assert.Equal("three.yaat-settings.zip", vm.ImportFileName);
        Assert.Equal(3, vm.ImportItems.Count);
        Assert.All(vm.ImportItems, row => Assert.True(row.IsSelected));
        ImportItemRow macros = vm.ImportItems.Single(r => r.ItemType == SettingsItemType.Macros);
        Assert.Contains(SettingsImportMode.Merge, macros.Modes);
        Assert.Equal(SettingsImportMode.Merge, macros.Mode);
        Assert.Equal([SettingsImportMode.Replace], vm.ImportItems.Single(r => r.ItemType == SettingsItemType.Verbs).Modes);
        Assert.Equal([SettingsImportMode.Replace], vm.ImportItems.Single(r => r.ItemType == SettingsItemType.GridLayout).Modes);
        Assert.True(vm.CanImport);
    }

    [Fact]
    public void ImportPreview_OpenedWithAPreselection_TicksOnlyThoseItems()
    {
        string path = WriteBundle(
            "two.yaat-settings.zip",
            [SettingsBundleItems.Macros([Macro("DEP", "CTO")]), SettingsBundleItems.GridLayout(new SavedGridLayout())]
        );
        ImportExportViewModel vm = NewViewModel(NewTarget(), [SettingsItemType.GridLayout], ImportExportTab.Import);

        vm.OpenImportFile(path);

        Assert.True(vm.ImportItems.Single(r => r.ItemType == SettingsItemType.GridLayout).IsSelected);
        Assert.False(vm.ImportItems.Single(r => r.ItemType == SettingsItemType.Macros).IsSelected);
    }

    [Fact]
    public void MacroClash_UnderMerge_IsListed_RenameErrorsBlockImport_AndTheSummaryReportsTheCounts()
    {
        InMemorySettingsImportTarget target = NewTarget();
        target.MacroList = [Macro("ILSAPP", "old"), Macro("DEP", "CTO")];
        string path = WriteSingleFile(
            "in" + SettingsBundleFormats.MacrosExtension,
            SettingsBundleItems.Macros([Macro("ilsapp", "new"), Macro("PAT", "TC")])
        );
        ImportExportViewModel vm = NewViewModel(target, [], ImportExportTab.Import);
        vm.OpenImportFile(path);

        ImportItemRow row = Assert.Single(vm.ImportItems);
        Assert.Equal(SettingsImportMode.Merge, row.Mode);
        ImportClashRow clash = Assert.Single(row.Clashes);
        Assert.Equal("ilsapp", clash.IncomingName);
        Assert.Equal("ILSAPP", clash.ExistingName);
        Assert.Equal(ClashChoice.Overwrite, clash.Choice);
        Assert.Equal("ilsapp_2", clash.RenameTo);
        Assert.True(vm.CanImport);

        clash.Choice = ClashChoice.Rename;
        clash.RenameTo = "dep";
        Assert.Equal("Name already exists", clash.RenameError);
        Assert.False(vm.CanImport);

        clash.RenameTo = "ILSNEW";
        Assert.Null(clash.RenameError);
        Assert.True(vm.CanImport);

        vm.Import();

        Assert.Equal(["ILSAPP", "DEP", "ILSNEW", "PAT"], target.MacroList.Select(m => m.Name));
        Assert.Equal("new", target.MacroList.Single(m => m.Name == "ILSNEW").Expansion);
        ImportSummaryRow summary = Assert.Single(vm.ImportResults);
        Assert.Equal(SettingsItemType.Macros, summary.Result.ItemType);
        Assert.Equal((1, 0, 1, 0), (summary.Result.Added, summary.Result.Overwritten, summary.Result.Renamed, summary.Result.Skipped));
        Assert.Equal("Macros: 1 added, 0 overwritten, 1 renamed, 0 skipped", summary.Text);
        Assert.False(vm.CanImport);
    }

    private string WriteNewerClientBundle()
    {
        string path = Path.Combine(_root, "newer.yaat-settings.zip");
        const string manifest = """
            {
              "bundleVersion": 2,
              "writtenBy": "9.9.9",
              "entries": [
                { "itemType": "macros", "format": "macros-json", "fileName": "macros.yaat-macros.json" },
                { "itemType": "hotkeys", "format": "hotkeys-json", "fileName": "hotkeys.json" }
              ]
            }
            """;
        using (ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            WriteZipEntry(zip, "manifest.json", Encoding.UTF8.GetBytes(manifest));
            WriteZipEntry(zip, "macros.yaat-macros.json", SettingsBundleItems.Macros([Macro("DEP", "CTO")]).Content);
            WriteZipEntry(zip, "hotkeys.json", Encoding.UTF8.GetBytes("{}"));
        }

        return path;
    }

    [Fact]
    public void NewerClientBundle_ListsTheUnknownEntryAsSkipped_AndImportsTheRest()
    {
        string path = WriteNewerClientBundle();
        InMemorySettingsImportTarget target = NewTarget();
        ImportExportViewModel vm = NewViewModel(target, [], ImportExportTab.Import);
        vm.OpenImportFile(path);

        Assert.Equal(2, vm.ImportItems.Count);
        ImportItemRow unknown = vm.ImportItems.Single(r => r.ItemType is null);
        Assert.False(unknown.IsSelectable);
        Assert.False(unknown.IsSelected);
        Assert.Contains("hotkeys", unknown.Title, StringComparison.Ordinal);
        Assert.Contains("does not know the item type 'hotkeys'", unknown.ClashSummary, StringComparison.Ordinal);
        Assert.True(vm.CanImport);

        vm.Import();

        Assert.Equal("DEP", Assert.Single(target.MacroList).Name);
        Assert.Equal(SettingsItemType.Macros, Assert.Single(vm.ImportResults).Result.ItemType);
    }

    [Fact]
    public void NonZipFile_ShowsAnErrorNamingTheFile_AndAppliesNothing()
    {
        string path = Path.Combine(_root, "broken.yaat-settings.zip");
        File.WriteAllText(path, "this is not a zip");
        InMemorySettingsImportTarget target = NewTarget();
        target.MacroList = [Macro("DEP", "CTO")];
        ImportExportViewModel vm = NewViewModel(target, [], ImportExportTab.Import);

        vm.OpenImportFile(path);

        Assert.NotNull(vm.ImportError);
        Assert.Contains("broken.yaat-settings.zip", vm.ImportError, StringComparison.Ordinal);
        Assert.Empty(vm.ImportItems);
        Assert.False(vm.CanImport);

        vm.Import();

        Assert.Equal("DEP", Assert.Single(target.MacroList).Name);
        Assert.Empty(vm.ImportResults);
    }

    [Fact]
    public void Export_PreferencesOnly_WritesABundleNamedSettings()
    {
        ImportExportViewModel vm = NewViewModel(NewTarget(), [SettingsItemType.Preferences], ImportExportTab.Export);

        SettingsExportPlan plan = Assert.IsType<SettingsExportPlan>(vm.PrepareExport());

        Assert.Equal(SettingsBundleFile.Extension, plan.Extension);
        Assert.Equal("settings" + SettingsBundleFile.Extension, plan.SuggestedFileName);
        string path = Path.Combine(_root, plan.SuggestedFileName);
        Assert.True(vm.WriteExport(plan, path));
        SettingsBundle bundle = ReadBundle(path);
        Assert.False(bundle.IsSingleItemFile);
        Assert.Equal(SettingsItemType.Preferences, Assert.Single(bundle.Entries).ItemType);
    }

    [Fact]
    public void ZipWithoutAManifest_ShowsAnErrorNamingTheFile()
    {
        string path = Path.Combine(_root, "nomanifest.yaat-settings.zip");
        using (ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            WriteZipEntry(zip, "macros.yaat-macros.json", SettingsBundleItems.Macros([Macro("DEP", "CTO")]).Content);
        }

        ImportExportViewModel vm = NewViewModel(NewTarget(), [], ImportExportTab.Import);
        vm.OpenImportFile(path);

        Assert.NotNull(vm.ImportError);
        Assert.Contains("nomanifest.yaat-settings.zip", vm.ImportError, StringComparison.Ordinal);
        Assert.Contains("manifest.json", vm.ImportError, StringComparison.Ordinal);
        Assert.Empty(vm.ImportItems);
        Assert.False(vm.HasImportItems);
        Assert.False(vm.CanImport);
    }

    [Fact]
    public void ApplyFailure_ShowsTheMessage_AndTheItemsThatFinished()
    {
        var target = new ThrowingLayoutsTarget(NewTarget());
        string path = WriteBundle(
            "fails.yaat-settings.zip",
            [SettingsBundleItems.Macros([Macro("DEP", "CTO")]), SettingsBundleItems.Layouts([new SavedLayout { Name = "GC" }])]
        );
        ImportExportViewModel vm = NewViewModel(target, [], ImportExportTab.Import);
        vm.OpenImportFile(path);

        vm.Import();

        Assert.Equal("Import failed: layouts are read-only. Items applied before the failure stay applied.", vm.ImportError);
        ImportSummaryRow finished = Assert.Single(vm.ImportResults);
        Assert.Equal(SettingsItemType.Macros, finished.Result.ItemType);
        Assert.Equal("DEP", Assert.Single(target.Inner.MacroList).Name);
        Assert.False(vm.CanImport);
    }

    [Fact]
    public void ChoosingAnotherFile_AfterAnImport_AllowsImportAgain()
    {
        string path = WriteSingleFile("in" + SettingsBundleFormats.MacrosExtension, SettingsBundleItems.Macros([Macro("DEP", "CTO")]));
        ImportExportViewModel vm = NewViewModel(NewTarget(), [], ImportExportTab.Import);
        vm.OpenImportFile(path);
        vm.Import();
        Assert.False(vm.CanImport);

        vm.OpenImportFile(path);

        Assert.True(vm.CanImport);
        Assert.Empty(vm.ImportResults);
        Assert.Null(vm.ImportError);
    }

    [Fact]
    public void SwitchingMergeToReplace_ClearsTheClashes_AndUnblocksARenameError()
    {
        InMemorySettingsImportTarget target = NewTarget();
        target.MacroList = [Macro("DEP", "CTO")];
        string path = WriteSingleFile("in" + SettingsBundleFormats.MacrosExtension, SettingsBundleItems.Macros([Macro("DEP", "new")]));
        ImportExportViewModel vm = NewViewModel(target, [], ImportExportTab.Import);
        vm.OpenImportFile(path);
        ImportItemRow row = Assert.Single(vm.ImportItems);
        ImportClashRow clash = Assert.Single(row.Clashes);
        clash.Choice = ClashChoice.Rename;
        clash.RenameTo = "";
        Assert.Equal("Name is required", clash.RenameError);
        Assert.False(vm.CanImport);

        row.Mode = SettingsImportMode.Replace;

        Assert.Empty(row.Clashes);
        Assert.False(row.HasClashes);
        Assert.False(row.HasRenameErrors);
        Assert.True(vm.CanImport);

        vm.Import();

        Assert.Equal("new", Assert.Single(target.MacroList).Expansion);
        Assert.Equal("Macros: replaced with 1", Assert.Single(vm.ImportResults).Text);
    }

    [Fact]
    public void TwoClashesRenamedToTheSameName_AreBothDuplicateRenames()
    {
        InMemorySettingsImportTarget target = NewTarget();
        target.MacroList = [Macro("DEP", "CTO"), Macro("ARR", "CTL")];
        string path = WriteSingleFile(
            "in" + SettingsBundleFormats.MacrosExtension,
            SettingsBundleItems.Macros([Macro("DEP", "new"), Macro("ARR", "new")])
        );
        ImportExportViewModel vm = NewViewModel(target, [], ImportExportTab.Import);
        vm.OpenImportFile(path);
        ImportItemRow row = Assert.Single(vm.ImportItems);
        Assert.Equal(2, row.Clashes.Count);

        foreach (ImportClashRow clash in row.Clashes)
        {
            clash.Choice = ClashChoice.Rename;
            clash.RenameTo = "SAME";
        }

        Assert.All(row.Clashes, clash => Assert.Equal("Duplicate rename", clash.RenameError));
        Assert.False(vm.CanImport);
    }

    [Fact]
    public void UnreadableEntry_InAReadableBundle_ShowsTheRowError_AndTheRestImports()
    {
        InMemorySettingsImportTarget target = NewTarget();
        var broken = new SettingsBundleEntry(
            SettingsItemType.Macros,
            SettingsBundleFormats.MacrosJson,
            "macros" + SettingsBundleFormats.MacrosExtension,
            Encoding.UTF8.GetBytes("not json")
        );
        string path = WriteBundle("mixed.yaat-settings.zip", [broken, SettingsBundleItems.Layouts([new SavedLayout { Name = "GC" }])]);
        ImportExportViewModel vm = NewViewModel(target, [], ImportExportTab.Import);

        vm.OpenImportFile(path);

        ImportItemRow macros = vm.ImportItems.Single(r => r.ItemType == SettingsItemType.Macros);
        Assert.NotNull(macros.Error);
        Assert.Contains("macros" + SettingsBundleFormats.MacrosExtension, macros.Error, StringComparison.Ordinal);
        Assert.False(macros.IsSelectable);
        Assert.False(macros.IsSelected);
        Assert.True(vm.CanImport);

        vm.Import();

        Assert.Equal("GC", Assert.Single(target.LayoutList).Name);
        Assert.Empty(target.MacroList);
        Assert.Equal(SettingsItemType.Layouts, Assert.Single(vm.ImportResults).Result.ItemType);
    }

    [Fact]
    public void SkippedRow_CannotBeTicked_OrReplanned()
    {
        string path = WriteNewerClientBundle();
        ImportExportViewModel vm = NewViewModel(NewTarget(), [], ImportExportTab.Import);
        vm.OpenImportFile(path);
        ImportItemRow skipped = vm.ImportItems.Single(r => r.ItemType is null);

        skipped.IsSelected = true;
        skipped.Mode = SettingsImportMode.Merge;

        Assert.False(skipped.IsSelected);
        Assert.Null(skipped.Plan);
        Assert.Empty(skipped.Clashes);
    }

    private static ImportExportViewModel NewViewModel(ISettingsImportTarget target, SettingsItemType[] preselected, ImportExportTab tab) =>
        new(target, new FakeExportSource(), new HashSet<SettingsItemType>(preselected), tab, WrittenBy);

    private static SavedMacro Macro(string name, string expansion) => new() { Name = name, Expansion = expansion };

    private static SettingsBundle ReadBundle(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return SettingsBundleFile.Read(path, stream);
    }

    private static void WriteZipEntry(ZipArchive zip, string name, byte[] content)
    {
        using Stream stream = zip.CreateEntry(name).Open();
        stream.Write(content);
    }

    private InMemorySettingsImportTarget NewTarget() => new(new FavoriteStore(Path.Combine(_root, "favorites-" + Guid.NewGuid().ToString("N"))));

    private string WriteBundle(string fileName, IReadOnlyList<SettingsBundleEntry> entries)
    {
        string path = Path.Combine(_root, fileName);
        using FileStream stream = File.Create(path);
        SettingsBundleFile.Write(entries, WrittenBy, stream);
        return path;
    }

    private string WriteSingleFile(string fileName, SettingsBundleEntry entry)
    {
        string path = Path.Combine(_root, fileName);
        File.WriteAllBytes(path, entry.Content);
        return path;
    }

    private sealed class FakeExportSource : ISettingsExportSource
    {
        public SettingsBundleEntry Export(SettingsItemType itemType) =>
            itemType switch
            {
                SettingsItemType.Macros => SettingsBundleItems.Macros([Macro("DEP", "CTO"), Macro("PAT &rwy", "TC")]),
                SettingsItemType.Layouts => SettingsBundleItems.Layouts([new SavedLayout { Name = "GC" }]),
                SettingsItemType.Preferences => SettingsBundleItems.Preferences(UserPreferences.CreateDefaults()),
                _ => throw new NotSupportedException($"The test export source has no {itemType}."),
            };
    }

    // Applies everything to the in-memory target except layouts, which fail as a write to a locked file would.
    private sealed class ThrowingLayoutsTarget(InMemorySettingsImportTarget inner) : ISettingsImportTarget
    {
        public InMemorySettingsImportTarget Inner => inner;

        public IReadOnlyList<SavedMacro> Macros => inner.Macros;

        public IReadOnlyList<SavedLayout> Layouts => inner.Layouts;

        public FavoriteStore Favorites => inner.Favorites;

        public void ReplaceMacros(IReadOnlyList<SavedMacro> macros) => inner.ReplaceMacros(macros);

        public void ReplaceLayouts(IReadOnlyList<SavedLayout> layouts) => throw new IOException("layouts are read-only");

        public int ApplyVerbs(CommandSchemeImport verbs) => inner.ApplyVerbs(verbs);

        public void ReplaceGridLayout(SavedGridLayout layout) => inner.ReplaceGridLayout(layout);

        public PreferencesImportResult ReplacePreferences(System.Text.Json.Nodes.JsonObject preferences) => inner.ReplacePreferences(preferences);
    }
}
