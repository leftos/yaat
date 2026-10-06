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

    // The backup's name for the fixed clock's instant, without the extension.
    private const string BackupName = "settings-backup-20261005-142233";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "yaat-importexport-vm-tests", Guid.NewGuid().ToString("N"));

    private readonly UserPreferences _preferences = new();

    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 5, 14, 22, 33, TimeSpan.Zero));

    public ImportExportViewModelTests() => Directory.CreateDirectory(_root);

    private string BackupsFolder => Path.Combine(_root, "backups");

    public void Dispose()
    {
        // A test that unticks the backup saves it in the shared preferences file; every hub after it starts ticked again.
        _preferences.SetBackUpSettingsBeforeImport(true);
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
        Assert.Equal(new FakeExportSource(null).Export(SettingsItemType.Macros).Content, File.ReadAllBytes(path));
        SettingsBundle bundle = ReadBundle(path);
        Assert.True(bundle.IsSingleItemFile);
        Assert.Equal(SettingsItemType.Macros, Assert.Single(bundle.Entries).ItemType);
    }

    [Fact]
    public void Summarize_VerbsWithUnknownCommands_CountsTheChangesAndListsTheSkippedCommands()
    {
        var result = new SettingsImportResult
        {
            ItemType = SettingsItemType.Verbs,
            Overwritten = 1,
            UnknownCommands = ["FOO", "BAR"],
        };

        string text = ImportExportViewModel.Summarize(result, SettingsImportMode.Replace);

        Assert.Contains("1 changed", text, StringComparison.Ordinal);
        Assert.Contains("unknown commands skipped: FOO, BAR", text, StringComparison.Ordinal);
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
    public void AppliedItemTypes_KeepAnEarlierFilesImport_AfterAnotherFileIsOpened()
    {
        InMemorySettingsImportTarget target = NewTarget();
        ImportExportViewModel vm = NewViewModel(target, [], ImportExportTab.Import);

        vm.OpenImportFile(WriteSingleFile("in" + SettingsBundleFormats.MacrosExtension, SettingsBundleItems.Macros([Macro("DEP", "CTO")])));
        vm.Import();
        vm.OpenImportFile(
            WriteSingleFile(
                "in" + SettingsBundleFormats.GridLayoutExtension,
                SettingsBundleItems.GridLayout(new SavedGridLayout { ColumnOrder = ["Callsign"] })
            )
        );

        Assert.Equal(SettingsItemType.Macros, Assert.Single(vm.AppliedItemTypes));

        vm.Import();

        Assert.True(vm.AppliedItemTypes.SetEquals([SettingsItemType.Macros, SettingsItemType.GridLayout]));
        Assert.NotNull(target.GridLayout);
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

    [Fact]
    public void Export_FavoritesOneSet_WritesTheSingleSetFileHoldingOnlyThatSet_WhichImportsAsThatSet()
    {
        InMemorySettingsImportTarget source = NewTarget();
        FavoriteSet ground = AddSet(source.Favorites, "Ground", "TAXI");
        AddSet(source.Favorites, "Tower", "CTO");
        ImportExportViewModel vm = NewViewModel(source, [SettingsItemType.Favorites], ImportExportTab.Export);
        ExportItemRow row = vm.ExportItems.Single(r => r.ItemType == SettingsItemType.Favorites);
        Assert.Equal(FavoritesExportChoice.AllSets, row.FavoritesChoices[0]);
        Assert.Equal("All sets", row.FavoritesChoices[0].Name);
        Assert.Equal(FavoritesExportChoice.AllSets, row.FavoritesChoice);

        row.FavoritesChoice = row.FavoritesChoices.Single(c => c.Name == "Ground");
        SettingsExportPlan plan = Assert.IsType<SettingsExportPlan>(vm.PrepareExport());
        string path = Path.Combine(_root, plan.SuggestedFileName);
        Assert.True(vm.WriteExport(plan, path));

        Assert.Equal(FavoriteExport.SetExportExtension, row.ExportedAs);
        Assert.Equal(FavoriteExport.SetExportExtension, plan.Extension);
        Assert.Equal("Ground" + FavoriteExport.SetExportExtension, plan.SuggestedFileName);
        SettingsBundleEntry entry = Assert.Single(ReadBundle(path).Entries);
        Assert.Equal(SettingsBundleFormats.FavoritesSetZip, entry.Format);
        InMemorySettingsImportTarget other = ImportFavorites(path, SettingsImportMode.Replace);
        FavoriteSet imported = Assert.Single(other.Favorites.OrderedSets, s => s.Kind == FavoriteSetKind.Named);
        Assert.Equal("Ground", imported.Name);
        Assert.Equal(ground.Id, imported.Id);
        Assert.Equal(["TAXI"], other.Favorites.GetSetFavorites(imported.Id).Select(f => f.Label));
        Assert.DoesNotContain(other.Favorites.AllFavorites, f => f.Label == "CTO");
        Assert.Equal([imported.Id], other.LoadedFavoriteSetIds);
    }

    [Fact]
    public void Export_FavoritesAllSets_WritesTheLibraryFileWithEverySet()
    {
        InMemorySettingsImportTarget source = NewTarget();
        AddSet(source.Favorites, "Ground", "TAXI");
        AddSet(source.Favorites, "Tower", "CTO");
        ImportExportViewModel vm = NewViewModel(source, [SettingsItemType.Favorites], ImportExportTab.Export);

        SettingsExportPlan plan = Assert.IsType<SettingsExportPlan>(vm.PrepareExport());
        string path = Path.Combine(_root, plan.SuggestedFileName);
        Assert.True(vm.WriteExport(plan, path));

        Assert.Equal(FavoriteExport.LibraryExportExtension, plan.Extension);
        Assert.Equal("favorites" + FavoriteExport.LibraryExportExtension, plan.SuggestedFileName);
        Assert.Equal(SettingsBundleFormats.FavoritesLibraryZip, Assert.Single(ReadBundle(path).Entries).Format);
        InMemorySettingsImportTarget other = ImportFavorites(path, SettingsImportMode.Replace);
        Assert.Equal(["Ground", "Tower"], other.Favorites.OrderedSets.Where(s => s.Kind == FavoriteSetKind.Named).Select(s => s.Name).Order());
    }

    [Fact]
    public void Export_OneFavoriteSetWithMacros_BundlesTheSetFile_WhichImportsAsThatSet()
    {
        InMemorySettingsImportTarget source = NewTarget();
        AddSet(source.Favorites, "Ground", "TAXI");
        AddSet(source.Favorites, "Tower", "CTO");
        ImportExportViewModel vm = NewViewModel(source, [SettingsItemType.Macros, SettingsItemType.Favorites], ImportExportTab.Export);
        ExportItemRow row = vm.ExportItems.Single(r => r.ItemType == SettingsItemType.Favorites);
        row.FavoritesChoice = row.FavoritesChoices.Single(c => c.Name == "Tower");

        SettingsExportPlan plan = Assert.IsType<SettingsExportPlan>(vm.PrepareExport());
        string path = Path.Combine(_root, plan.SuggestedFileName);
        Assert.True(vm.WriteExport(plan, path));

        Assert.Equal(SettingsBundleFile.Extension, plan.Extension);
        SettingsBundleEntry favorites = ReadBundle(path).Entries.Single(e => e.ItemType == SettingsItemType.Favorites);
        Assert.Equal(SettingsBundleFormats.FavoritesSetZip, favorites.Format);
        InMemorySettingsImportTarget other = ImportFavorites(path, SettingsImportMode.Merge);
        Assert.Equal(["Tower"], other.Favorites.OrderedSets.Where(s => s.Kind == FavoriteSetKind.Named).Select(s => s.Name));
    }

    [Fact]
    public void Import_BackUpFirstWithAReplace_SavesEverySettingToATimestampedBackup_ThenImports()
    {
        (ImportExportViewModel vm, InMemorySettingsImportTarget target) = OpenMacrosReplaceLayoutsMerge();
        Assert.True(vm.BackUpFirst);

        vm.Import();

        string backup = Assert.Single(Directory.GetFiles(BackupsFolder));
        Assert.Equal(BackupName + SettingsBundleFile.Extension, Path.GetFileName(backup));
        SettingsBundle saved = ReadBundle(backup);
        Assert.False(saved.IsSingleItemFile);
        Assert.Equal(Enum.GetValues<SettingsItemType>().Order(), saved.Entries.Select(e => e.ItemType).Order());
        Assert.Empty(saved.Skipped);
        Assert.Equal(["IMP"], target.MacroList.Select(m => m.Name));
        Assert.Equal(["OLDLAYOUT", "NEWLAYOUT"], target.LayoutList.Select(l => l.Name));
        Assert.Null(vm.ImportError);
        Assert.Equal($"Backed up all settings to {BackupName}{SettingsBundleFile.Extension}.", vm.BackupResult);
    }

    [Fact]
    public void Import_BackUpFirstWithMergeOnly_SavesTheBackup_ThenImports()
    {
        (ImportExportViewModel vm, InMemorySettingsImportTarget target) = OpenMacrosReplaceLayoutsMerge();
        vm.ImportItems.Single(r => r.ItemType == SettingsItemType.Macros).Mode = SettingsImportMode.Merge;

        vm.Import();

        Assert.Equal(BackupName + SettingsBundleFile.Extension, Path.GetFileName(Assert.Single(Directory.GetFiles(BackupsFolder))));
        Assert.Equal(["OLD", "IMP"], target.MacroList.Select(m => m.Name));
        Assert.Null(vm.ImportError);
    }

    [Fact]
    public void Import_BackUpFirstUnticked_WritesNoBackup_AndImports()
    {
        (ImportExportViewModel vm, InMemorySettingsImportTarget target) = OpenMacrosReplaceLayoutsMerge();
        vm.BackUpFirst = false;

        vm.Import();

        Assert.False(Directory.Exists(BackupsFolder) && (Directory.GetFiles(BackupsFolder).Length > 0));
        Assert.Equal(["IMP"], target.MacroList.Select(m => m.Name));
        Assert.Null(vm.BackupResult);
        Assert.Null(vm.ImportError);
    }

    [Fact]
    public void Import_BackupThatFailsPartway_ImportsNothing_SaysWhy_AndLeavesNoTemporaryFile()
    {
        (ImportExportViewModel vm, InMemorySettingsImportTarget target) = OpenMacrosReplaceLayoutsMerge(FailAfterFirstWrite);

        vm.Import();

        Assert.Empty(Directory.GetFiles(BackupsFolder));
        Assert.Equal(["OLD"], target.MacroList.Select(m => m.Name));
        Assert.Equal(["OLDLAYOUT"], target.LayoutList.Select(l => l.Name));
        Assert.Empty(vm.ImportResults);
        Assert.Empty(vm.AppliedItemTypes);
        Assert.Null(vm.BackupResult);
        const string diskFull = "There is not enough space on the disk (test)";
        Assert.Equal(
            $"Could not write the backup '{BackupName}{SettingsBundleFile.Extension}': " + diskFull + ". Nothing was imported.",
            vm.ImportError
        );
        Assert.True(vm.CanImport);
    }

    [Fact]
    public void Import_SecondBackupInTheSameSecond_IsNumbered()
    {
        (ImportExportViewModel first, _) = OpenMacrosReplaceLayoutsMerge();
        first.Import();
        (ImportExportViewModel second, _) = OpenMacrosReplaceLayoutsMerge();

        second.Import();

        Assert.Equal(
            [BackupName + "-2" + SettingsBundleFile.Extension, BackupName + SettingsBundleFile.Extension],
            Directory.GetFiles(BackupsFolder).Select(Path.GetFileName).Order(StringComparer.Ordinal)
        );
        Assert.Equal($"Backed up all settings to {BackupName}-2{SettingsBundleFile.Extension}.", second.BackupResult);
    }

    [Fact]
    public void BackUpFirst_Toggled_IsSavedInThePreferences_AndANewHubReadsItBack()
    {
        ImportExportViewModel vm = NewViewModel(NewTarget(), [], ImportExportTab.Import);
        Assert.True(vm.BackUpFirst);

        vm.BackUpFirst = false;

        Assert.False(_preferences.BackUpSettingsBeforeImport);
        var reloaded = new UserPreferences();
        Assert.False(reloaded.BackUpSettingsBeforeImport);
        InMemorySettingsImportTarget target = NewTarget();
        var next = new ImportExportViewModel(
            target,
            new FakeExportSource(target.Favorites),
            new ImportExportOpening(new HashSet<SettingsItemType>(), ImportExportTab.Import),
            Files(CreateFile),
            reloaded
        );
        Assert.False(next.BackUpFirst);
    }

    [Fact]
    public void Import_BackupThatCannotBeBuilt_ImportsNothing_AndSaysWhy()
    {
        InMemorySettingsImportTarget target = NewTarget();
        target.ReplaceMacros([Macro("OLD", "X")]);
        string path = WriteBundle("macros.yaat-settings.zip", [SettingsBundleItems.Macros([Macro("IMP", "CTO")])]);
        ImportExportViewModel vm = NewHub(target, new FakeExportSource(null), [], ImportExportTab.Import, CreateFile);
        vm.OpenImportFile(path);

        vm.Import();

        Assert.Equal("Backup failed: The test export source has no Favorites. Nothing was imported.", vm.ImportError);
        Assert.Equal(["OLD"], target.MacroList.Select(m => m.Name));
        Assert.Empty(vm.ImportResults);
        Assert.Empty(vm.AppliedItemTypes);
        Assert.Null(vm.BackupResult);
        Assert.False(Directory.Exists(BackupsFolder) && (Directory.GetFiles(BackupsFolder).Length > 0));
    }

    [Fact]
    public void Import_BackedUpThenTheFirstItemFails_StillSaysWhereTheBackupWent()
    {
        var target = new ThrowingLayoutsTarget(NewTarget());
        string path = WriteBundle("layouts.yaat-settings.zip", [SettingsBundleItems.Layouts([new SavedLayout { Name = "NEWLAYOUT" }])]);
        ImportExportViewModel vm = NewHub(target, new FakeExportSource(target.Favorites), [], ImportExportTab.Import, CreateFile);
        vm.OpenImportFile(path);

        vm.Import();

        Assert.Empty(vm.ImportResults);
        Assert.False(vm.HasImportResults);
        Assert.StartsWith("Import failed: layouts are read-only", vm.ImportError, StringComparison.Ordinal);
        Assert.Equal($"Backed up all settings to {BackupName}{SettingsBundleFile.Extension}.", vm.BackupResult);
        Assert.True(vm.HasBackupResult);
        Assert.Single(Directory.GetFiles(BackupsFolder));
    }

    [Fact]
    public void EffectText_FavoritesReplaceWhenYouHaveNoFavorites_SaysThereIsNoneToDelete()
    {
        ImportExportViewModel vm = NewViewModel(NewTarget(), [], ImportExportTab.Import);
        vm.OpenImportFile(WriteFavoritesFile());
        ImportItemRow favorites = vm.ImportItems.Single(r => r.ItemType == SettingsItemType.Favorites);

        favorites.Mode = SettingsImportMode.Replace;

        Assert.StartsWith("Adds the file's ", favorites.EffectText, StringComparison.Ordinal);
        Assert.EndsWith("; you have none to delete.", favorites.EffectText, StringComparison.Ordinal);
    }

    [Fact]
    public void EffectText_FavoritesReplaceWithFavoritesInGlobal_CountsTheGlobalSet()
    {
        InMemorySettingsImportTarget target = NewTarget();
        var favorite = new FavoriteCommand { Label = "CTO", CommandText = "CTO" };
        target.Favorites.SaveFavorite(favorite);
        target.Favorites.AddToSet(target.Favorites.GlobalSet.Id, favorite.Id);
        ImportExportViewModel vm = NewViewModel(target, [], ImportExportTab.Import);
        vm.OpenImportFile(WriteFavoritesFile());
        ImportItemRow favorites = vm.ImportItems.Single(r => r.ItemType == SettingsItemType.Favorites);

        favorites.Mode = SettingsImportMode.Replace;

        Assert.StartsWith("Deletes your 1 favorite set, then adds the file's ", favorites.EffectText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SettingsItemType.Macros, SettingsImportMode.Merge, 6, 14, 0, "Adds the file's 6 macros to yours.")]
    [InlineData(SettingsItemType.Macros, SettingsImportMode.Merge, 1, 14, 1, "Adds the file's 1 macro to yours; 1 name clashes, listed below.")]
    [InlineData(SettingsItemType.Macros, SettingsImportMode.Merge, 6, 14, 2, "Adds the file's 6 macros to yours; 2 names clash, listed below.")]
    [InlineData(SettingsItemType.Macros, SettingsImportMode.Replace, 6, 14, 0, "Deletes your 14 macros, then adds the file's 6.")]
    [InlineData(SettingsItemType.Macros, SettingsImportMode.Replace, 1, 1, 0, "Deletes your 1 macro, then adds the file's 1.")]
    [InlineData(SettingsItemType.Macros, SettingsImportMode.Replace, 6, 0, 0, "Adds the file's 6 macros; you have none to delete.")]
    [InlineData(SettingsItemType.Layouts, SettingsImportMode.Merge, 1, 3, 0, "Adds the file's 1 layout to yours.")]
    [InlineData(SettingsItemType.Layouts, SettingsImportMode.Merge, 4, 3, 3, "Adds the file's 4 layouts to yours; 3 names clash, listed below.")]
    [InlineData(SettingsItemType.Layouts, SettingsImportMode.Replace, 2, 1, 0, "Deletes your 1 layout, then adds the file's 2.")]
    [InlineData(SettingsItemType.Layouts, SettingsImportMode.Replace, 1, 0, 0, "Adds the file's 1 layout; you have none to delete.")]
    [InlineData(SettingsItemType.Favorites, SettingsImportMode.Merge, 2, 4, 0, "Adds the file's 2 favorite sets to yours.")]
    [InlineData(
        SettingsItemType.Favorites,
        SettingsImportMode.Merge,
        1,
        4,
        1,
        "Adds the file's 1 favorite set to yours; 1 name clashes, listed below."
    )]
    [InlineData(SettingsItemType.Favorites, SettingsImportMode.Replace, 3, 5, 0, "Deletes your 5 favorite sets, then adds the file's 3.")]
    [InlineData(SettingsItemType.Favorites, SettingsImportMode.Replace, 1, 0, 0, "Adds the file's 1 favorite set; you have none to delete.")]
    [InlineData(
        SettingsItemType.Verbs,
        SettingsImportMode.Replace,
        1,
        0,
        0,
        "Changes only the 1 command the file lists; every other command keeps its verbs."
    )]
    [InlineData(
        SettingsItemType.Verbs,
        SettingsImportMode.Replace,
        12,
        0,
        0,
        "Changes only the 12 commands the file lists; every other command keeps its verbs."
    )]
    [InlineData(
        SettingsItemType.Preferences,
        SettingsImportMode.Replace,
        1,
        0,
        0,
        "Sets the 1 setting the file carries; every other setting stays as it is."
    )]
    [InlineData(
        SettingsItemType.Preferences,
        SettingsImportMode.Replace,
        40,
        0,
        0,
        "Sets the 40 settings the file carries; every other setting stays as it is."
    )]
    [InlineData(SettingsItemType.GridLayout, SettingsImportMode.Replace, 1, 0, 0, "Replaces your Aircraft list columns with the file's.")]
    public void Effect_SaysWhatTheImportDoesToTheItem(
        SettingsItemType itemType,
        SettingsImportMode mode,
        int fileCount,
        int currentCount,
        int clashCount,
        string expected
    ) => Assert.Equal(expected, ImportItemRow.Effect(itemType, mode, fileCount, currentCount, clashCount));

    [Fact]
    public void EffectText_MacrosSwitchedFromMergeToReplace_SaysWhatReplaceDeletes()
    {
        (ImportExportViewModel vm, _) = OpenMacrosReplaceLayoutsMerge();
        ImportItemRow macros = vm.ImportItems.Single(r => r.ItemType == SettingsItemType.Macros);
        macros.Mode = SettingsImportMode.Merge;
        Assert.Equal("Adds the file's 1 macro to yours.", macros.EffectText);

        macros.Mode = SettingsImportMode.Replace;

        Assert.Equal("Deletes your 1 macro, then adds the file's 1.", macros.EffectText);
    }

    [Fact]
    public void Export_ThatFailsPartway_LeavesTheExistingFileAsItWas_AndNoTemporaryFile()
    {
        ImportExportViewModel vm = NewHub(
            NewTarget(),
            new FakeExportSource(null),
            [SettingsItemType.Macros],
            ImportExportTab.Export,
            FailAfterFirstWrite
        );
        SettingsExportPlan plan = Assert.IsType<SettingsExportPlan>(vm.PrepareExport());
        string path = WriteSingleFile(plan.SuggestedFileName, SettingsBundleItems.Macros([Macro("KEEP", "X")]));
        byte[] existing = File.ReadAllBytes(path);
        string[] filesBefore = Directory.GetFiles(_root);

        Assert.False(vm.WriteExport(plan, path));

        Assert.Equal(existing, File.ReadAllBytes(path));
        Assert.Equal(filesBefore.Order(), Directory.GetFiles(_root).Order());
        Assert.StartsWith($"Could not write '{plan.SuggestedFileName}'", vm.ExportStatus);
    }

    // A hub over a target holding macro OLD and layout OLDLAYOUT, with a file of macro IMP and layout NEWLAYOUT open:
    // macros set to Replace, layouts left on Merge.
    private (ImportExportViewModel Vm, InMemorySettingsImportTarget Target) OpenMacrosReplaceLayoutsMerge() =>
        OpenMacrosReplaceLayoutsMerge(CreateFile);

    private (ImportExportViewModel Vm, InMemorySettingsImportTarget Target) OpenMacrosReplaceLayoutsMerge(Func<string, Stream> createFile)
    {
        InMemorySettingsImportTarget target = NewTarget();
        target.ReplaceMacros([Macro("OLD", "X")]);
        target.ReplaceLayouts([new SavedLayout { Name = "OLDLAYOUT" }]);
        string path = WriteBundle(
            "replace.yaat-settings.zip",
            [SettingsBundleItems.Macros([Macro("IMP", "CTO")]), SettingsBundleItems.Layouts([new SavedLayout { Name = "NEWLAYOUT" }])]
        );
        ImportExportViewModel vm = NewHub(target, new FakeExportSource(target.Favorites), [], ImportExportTab.Import, createFile);
        vm.OpenImportFile(path);
        vm.ImportItems.Single(r => r.ItemType == SettingsItemType.Macros).Mode = SettingsImportMode.Replace;
        Assert.Equal(SettingsImportMode.Merge, vm.ImportItems.Single(r => r.ItemType == SettingsItemType.Layouts).Mode);
        Assert.True(vm.CanImport);
        return (vm, target);
    }

    private static FavoriteSet AddSet(FavoriteStore store, string name, string favoriteLabel)
    {
        FavoriteSet set = store.CreateNamedSet(name)!;
        var favorite = new FavoriteCommand { Label = favoriteLabel, CommandText = favoriteLabel };
        store.SaveFavorite(favorite);
        store.AddToSet(set.Id, favorite.Id);
        return set;
    }

    private InMemorySettingsImportTarget ImportFavorites(string path, SettingsImportMode mode)
    {
        InMemorySettingsImportTarget other = NewTarget();
        ImportExportViewModel importer = NewViewModel(other, [], ImportExportTab.Import);
        importer.OpenImportFile(path);
        foreach (ImportItemRow row in importer.ImportItems)
        {
            row.IsSelected = row.ItemType == SettingsItemType.Favorites;
        }

        importer.ImportItems.Single(r => r.ItemType == SettingsItemType.Favorites).Mode = mode;
        importer.Import();
        Assert.Null(importer.ImportError);
        return other;
    }

    // The export source reads the target's favorites store, as the hub's live export source reads the store it imports into.
    private ImportExportViewModel NewViewModel(ISettingsImportTarget target, SettingsItemType[] preselected, ImportExportTab tab) =>
        NewHub(target, new FakeExportSource(target.Favorites), preselected, tab, CreateFile);

    private ImportExportViewModel NewHub(
        ISettingsImportTarget target,
        ISettingsExportSource source,
        SettingsItemType[] preselected,
        ImportExportTab tab,
        Func<string, Stream> createFile
    ) => new(target, source, new ImportExportOpening(new HashSet<SettingsItemType>(preselected), tab), Files(createFile), _preferences);

    private ImportExportFiles Files(Func<string, Stream> createFile) => new(WrittenBy, createFile, BackupsFolder, _clock);

    // A favorites file holding the named set Ground with one favorite.
    private string WriteFavoritesFile()
    {
        InMemorySettingsImportTarget source = NewTarget();
        AddSet(source.Favorites, "Ground", "TAXI");
        return WriteBundle("favorites.yaat-settings.zip", [SettingsBundleItems.Favorites(source.Favorites, [])]);
    }

    private static Stream CreateFile(string path) => File.Open(path, FileMode.CreateNew);

    // Creates the file as the hub does, then fails as a full disk would once its first bytes are on disk.
    private static Stream FailAfterFirstWrite(string path) => new FailsAfterFirstWriteStream(File.Open(path, FileMode.CreateNew));

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

    // Fixed macros, layouts and preferences; favorites from the store given, when there is one.
    private sealed class FakeExportSource(FavoriteStore? favorites) : ISettingsExportSource
    {
        public IReadOnlyList<FavoriteSet> FavoriteSets => favorites?.OrderedSets ?? [];

        public IReadOnlyList<SavedMacro>? SelectedMacros => null;

        public SettingsBundleEntry Export(SettingsItemType itemType) =>
            itemType switch
            {
                SettingsItemType.Macros => SettingsBundleItems.Macros([Macro("DEP", "CTO"), Macro("PAT &rwy", "TC")]),
                SettingsItemType.Layouts => SettingsBundleItems.Layouts([new SavedLayout { Name = "GC" }]),
                SettingsItemType.Preferences => SettingsBundleItems.Preferences(UserPreferences.CreateDefaults()),
                SettingsItemType.Favorites when favorites is not null => SettingsBundleItems.Favorites(favorites, []),
                SettingsItemType.Verbs => SettingsBundleItems.Verbs(CommandScheme.Default()),
                SettingsItemType.GridLayout => SettingsBundleItems.GridLayout(new SavedGridLayout()),
                _ => throw new NotSupportedException($"The test export source has no {itemType}."),
            };

        public SettingsBundleEntry ExportFavoriteSet(string setId) =>
            SettingsBundleItems.FavoriteSet(favorites ?? throw new NotSupportedException("The test export source has no favorites."), setId);
    }

    // A clock stopped at one instant, in a UTC local time zone, so the backup's name is the same on every machine.
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => now;
    }

    // Writes the first block to the file and flushes it, then throws IOException on that and every later write.
    private sealed class FailsAfterFirstWriteStream(FileStream inner) : Stream
    {
        private bool _wrote;

        public override bool CanRead => false;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => true;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("The test stream is write-only.");

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (!_wrote)
            {
                _wrote = true;
                inner.Write(buffer);
                inner.Flush();
            }

            throw new IOException("There is not enough space on the disk (test).");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    // Applies everything to the in-memory target except layouts, which fail as a write to a locked file would.
    private sealed class ThrowingLayoutsTarget(InMemorySettingsImportTarget inner) : ISettingsImportTarget
    {
        public InMemorySettingsImportTarget Inner => inner;

        public IReadOnlyList<SavedMacro> Macros => inner.Macros;

        public IReadOnlyList<SavedLayout> Layouts => inner.Layouts;

        public FavoriteStore Favorites => inner.Favorites;

        public FavoriteImportResult? ImportFavoritesFile(string fileName, byte[] content, FavoriteImportMode mode) =>
            inner.ImportFavoritesFile(fileName, content, mode);

        public void ReplaceMacros(IReadOnlyList<SavedMacro> macros) => inner.ReplaceMacros(macros);

        public void ReplaceLayouts(IReadOnlyList<SavedLayout> layouts) => throw new IOException("layouts are read-only");

        public int ApplyVerbs(CommandSchemeImport verbs) => inner.ApplyVerbs(verbs);

        public void ReplaceGridLayout(SavedGridLayout layout) => inner.ReplaceGridLayout(layout);

        public PreferencesImportResult ReplacePreferences(System.Text.Json.Nodes.JsonObject preferences) => inner.ReplacePreferences(preferences);

        public void LoadImportedFavoriteSets(IReadOnlyList<string> setIds, bool replace) => inner.LoadImportedFavoriteSets(setIds, replace);
    }
}
