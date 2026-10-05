using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// A favorites import through the Import / Export hub reaches the favorites bar: the sets the file asks to load are
/// loaded, at once from the main window's hub and on Apply from Settings' hub, where Cancel drops the import. The main
/// view model's store lives under the per-process YAAT_APPDATA_DIR, shared by every test in this assembly, so each test
/// names its sets and favorites "FHI-…" and deletes them afterwards.
/// </summary>
public class FavoritesHubImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "yaat-favorites-hub-import-tests", Guid.NewGuid().ToString("N"));

    public FavoritesHubImportTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [AvaloniaFact]
    public void LiveHub_ReplaceImportOfASetZip_ShowsTheSetInTheBar()
    {
        using var scope = new PreferencesFileScope();
        MainViewModel vm = NewVm();
        try
        {
            string path = WriteSetExport("FHI-Replace", "FHI-ReplaceFav");

            ImportThroughLiveHub(vm, path, SettingsImportMode.Replace);

            FavoriteSet imported = vm.FavoriteStore.FindNamedSet("FHI-Replace")!;
            Assert.Equal([imported.Id], vm.Preferences.LoadedFavoriteSetIds);
            Assert.Contains(vm.DisplayFavorites, e => (e.SetId == imported.Id) && (e.Favorite.Label == "FHI-ReplaceFav"));
        }
        finally
        {
            Cleanup(vm);
        }
    }

    [AvaloniaFact]
    public void LiveHub_MergeImportOfALibraryLoadingASet_AddsItToTheLoadedSets_KeepingTheOthers()
    {
        using var scope = new PreferencesFileScope();
        MainViewModel vm = NewVm();
        try
        {
            FavoriteSet other = vm.FavoriteStore.CreateNamedSet("FHI-Other")!;
            vm.SetFavoriteSetLoaded(other.Id, true);
            string path = WriteLibraryExport("FHI-Merged", "FHI-MergedFav");

            ImportThroughLiveHub(vm, path, SettingsImportMode.Merge);

            FavoriteSet imported = vm.FavoriteStore.FindNamedSet("FHI-Merged")!;
            Assert.Contains(other.Id, vm.Preferences.LoadedFavoriteSetIds);
            Assert.Contains(imported.Id, vm.Preferences.LoadedFavoriteSetIds);
            Assert.Contains(vm.DisplayFavorites, e => (e.SetId == imported.Id) && (e.Favorite.Label == "FHI-MergedFav"));
        }
        finally
        {
            Cleanup(vm);
        }
    }

    [AvaloniaFact]
    public void SettingsHub_FavoritesImport_ChangesNothingUntilApply_ThenAddsAndLoadsTheSet()
    {
        using var scope = new PreferencesFileScope();
        MainViewModel vm = NewVm();
        try
        {
            var settings = new SettingsViewModel(vm.Preferences);
            using var target = new SettingsViewModelImportTarget(settings, vm.FavoriteStore);
            List<FavoriteDisplayEntry> barBefore = [.. vm.DisplayFavorites];
            List<string> loadedBefore = [.. vm.Preferences.LoadedFavoriteSetIds];

            ImportThroughHub(target, vm, WriteLibraryExport("FHI-Staged", "FHI-StagedFav"), SettingsImportMode.Merge);
            Assert.NotNull(target.Favorites.FindNamedSet("FHI-Staged"));

            Assert.Null(vm.FavoriteStore.FindNamedSet("FHI-Staged"));
            Assert.Null(new FavoriteStore(FavoriteStore.DefaultRootDir).FindNamedSet("FHI-Staged"));
            Assert.Equal(barBefore, vm.DisplayFavorites);
            Assert.Equal(loadedBefore, vm.Preferences.LoadedFavoriteSetIds);

            settings.ApplyCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            FavoriteSet imported = Assert.IsType<FavoriteSet>(vm.FavoriteStore.FindNamedSet("FHI-Staged"));
            Assert.Contains(imported.Id, vm.Preferences.LoadedFavoriteSetIds);
            Assert.Contains(vm.DisplayFavorites, e => (e.SetId == imported.Id) && (e.Favorite.Label == "FHI-StagedFav"));
            Assert.Empty(StagedCopies());
        }
        finally
        {
            Cleanup(vm);
        }
    }

    [AvaloniaFact]
    public void SettingsHub_FavoritesImport_ThenCancel_LeavesTheStoreAndTheBarAsTheyWere()
    {
        using var scope = new PreferencesFileScope();
        MainViewModel vm = NewVm();
        try
        {
            var settings = new SettingsViewModel(vm.Preferences);
            var target = new SettingsViewModelImportTarget(settings, vm.FavoriteStore);
            List<FavoriteDisplayEntry> barBefore = [.. vm.DisplayFavorites];
            List<string> loadedBefore = [.. vm.Preferences.LoadedFavoriteSetIds];

            ImportThroughHub(target, vm, WriteSetExport("FHI-Cancelled", "FHI-CancelledFav"), SettingsImportMode.Replace);
            Assert.NotEmpty(StagedCopies());

            // Cancel closes the window without Apply, which disposes the target.
            target.Dispose();
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(StagedCopies());
            Assert.Null(vm.FavoriteStore.FindNamedSet("FHI-Cancelled"));
            Assert.Null(new FavoriteStore(FavoriteStore.DefaultRootDir).FindNamedSet("FHI-Cancelled"));
            Assert.Equal(barBefore, vm.DisplayFavorites);
            Assert.Equal(loadedBefore, vm.Preferences.LoadedFavoriteSetIds);
            Assert.Equal(loadedBefore, new UserPreferences().LoadedFavoriteSetIds);
        }
        finally
        {
            Cleanup(vm);
        }
    }

    [AvaloniaFact]
    public void SettingsHub_TwoFavoritesImportsBeforeOneApply_BothApplyInOrder()
    {
        using var scope = new PreferencesFileScope();
        MainViewModel vm = NewVm();
        try
        {
            var settings = new SettingsViewModel(vm.Preferences);
            using var target = new SettingsViewModelImportTarget(settings, vm.FavoriteStore);

            ImportThroughHub(target, vm, WriteLibraryExport("FHI-First", "FHI-FirstFav"), SettingsImportMode.Merge);
            ImportThroughHub(target, vm, WriteLibraryExport("FHI-Second", "FHI-SecondFav"), SettingsImportMode.Merge);
            settings.ApplyCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            string first = vm.FavoriteStore.FindNamedSet("FHI-First")!.Id;
            string second = vm.FavoriteStore.FindNamedSet("FHI-Second")!.Id;
            Assert.Equal([first, second], vm.Preferences.LoadedFavoriteSetIds.Where(id => (id == first) || (id == second)));
        }
        finally
        {
            Cleanup(vm);
        }
    }

    [AvaloniaFact]
    public void SettingsHub_BlankIdSetAndALayoutLoadingIt_ApplyKeepsTheLayoutPointingAtTheSet()
    {
        using var scope = new PreferencesFileScope();
        MainViewModel vm = NewVm();
        try
        {
            var settings = new SettingsViewModel(vm.Preferences);
            using var target = new SettingsViewModelImportTarget(settings, vm.FavoriteStore);
            var blankIdSet = new FavoriteSet
            {
                Id = "",
                Kind = FavoriteSetKind.Named,
                Name = "FHI-Blank",
            };
            var favorites = new SettingsBundleEntry(
                SettingsItemType.Favorites,
                SettingsBundleFormats.FavoritesJson,
                "set.json",
                JsonSerializer.SerializeToUtf8Bytes(blankIdSet, UserPreferences.JsonOptions)
            );
            SettingsBundleEntry layouts = SettingsBundleItems.Layouts([new SavedLayout { Name = "FHI-Layout", LoadedFavoriteSetIds = [""] }]);

            SettingsImportPlanner.ApplyAll(
                [
                    SettingsImportPlanner.Plan(favorites, SettingsImportMode.Merge, target),
                    SettingsImportPlanner.Plan(layouts, SettingsImportMode.Merge, target),
                ],
                target,
                _ => { }
            );
            settings.ApplyCommand.Execute(null);

            FavoriteSet imported = Assert.IsType<FavoriteSet>(vm.FavoriteStore.FindNamedSet("FHI-Blank"));
            Assert.Equal([imported.Id], vm.Preferences.Layouts.Single(l => l.Name == "FHI-Layout").LoadedFavoriteSetIds);
        }
        finally
        {
            Cleanup(vm);
        }
    }

    [AvaloniaFact]
    public void SettingsHub_FirstStagedCopyOfASession_DeletesCopiesLeftByEarlierSessions()
    {
        using var scope = new PreferencesFileScope();
        MainViewModel vm = NewVm();
        string leftover = Path.Combine(SettingsViewModelImportTarget.StagedFavoritesFolder, "left-by-a-crash");
        Directory.CreateDirectory(Path.Combine(leftover, "sets"));
        try
        {
            using var target = new SettingsViewModelImportTarget(new SettingsViewModel(vm.Preferences), vm.FavoriteStore);

            ImportThroughHub(target, vm, WriteSetExport("FHI-Sweep", "FHI-SweepFav"), SettingsImportMode.Merge);

            Assert.False(Directory.Exists(leftover));
            Assert.Single(StagedCopies());
        }
        finally
        {
            Cleanup(vm);
        }
    }

    [AvaloniaFact]
    public void SettingsHub_FirstStagedCopy_KeepsALiveProcesssCopy_AndDeletesDeadAndUnmarkedOnes()
    {
        using var scope = new PreferencesFileScope();
        MainViewModel vm = NewVm();
        string pid = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        string live = LeftoverCopy("live-owner", pid);
        string dead = LeftoverCopy("dead-owner", ExitedProcessId().ToString(CultureInfo.InvariantCulture));
        string unmarked = LeftoverCopy("no-owner", owner: null);
        try
        {
            using var target = new SettingsViewModelImportTarget(new SettingsViewModel(vm.Preferences), vm.FavoriteStore);

            ImportThroughHub(target, vm, WriteSetExport("FHI-Owners", "FHI-OwnersFav"), SettingsImportMode.Merge);

            Assert.True(Directory.Exists(live), "a copy whose process is running belongs to another live Settings session");
            Assert.False(Directory.Exists(dead));
            Assert.False(Directory.Exists(unmarked));
            string own = Assert.Single(StagedCopies(), copy => copy != live);
            Assert.Equal(pid, File.ReadAllText(Path.Combine(own, SettingsViewModelImportTarget.OwnerMarkerFileName)));
        }
        finally
        {
            if (Directory.Exists(live))
            {
                Directory.Delete(live, recursive: true);
            }

            Cleanup(vm);
        }
    }

    [AvaloniaFact]
    public void SettingsHub_ExportAfterAStagedFavoritesImport_CarriesTheStagedSet()
    {
        using var scope = new PreferencesFileScope();
        MainViewModel vm = NewVm();
        try
        {
            var settings = new SettingsViewModel(vm.Preferences);
            using var target = new SettingsViewModelImportTarget(settings, vm.FavoriteStore);
            ImportThroughHub(target, vm, WriteSetExport("FHI-Exported", "FHI-ExportedFav"), SettingsImportMode.Merge);

            var hub = new ImportExportViewModel(
                target,
                new SettingsViewModelExportSource(settings, vm.Preferences, target, selectedMacros: null),
                new ImportExportOpening(new HashSet<SettingsItemType> { SettingsItemType.Favorites }, ImportExportTab.Export),
                new ImportExportFiles(
                    "test-version",
                    path => File.Open(path, FileMode.CreateNew),
                    Path.Combine(_root, "backups"),
                    TimeProvider.System
                ),
                vm.Preferences
            );
            SettingsExportPlan plan = Assert.IsType<SettingsExportPlan>(hub.PrepareExport());
            SettingsBundleEntry favorites = Assert.Single(plan.Entries);
            var exported = new FavoriteStore(Path.Combine(_root, "exported"));
            using (var stream = new MemoryStream(favorites.Content))
            {
                Assert.NotNull(FavoriteExport.ImportFile(exported, favorites.FileName, stream, FavoriteImportMode.Replace));
            }

            Assert.NotNull(exported.FindNamedSet("FHI-Exported"));
            Assert.Null(vm.FavoriteStore.FindNamedSet("FHI-Exported"));
        }
        finally
        {
            Cleanup(vm);
        }
    }

    [AvaloniaFact]
    public void SettingsHub_ExportAllSetsAfterAStagedImportThatLoadsASet_ListsItAmongTheLoadedSets()
    {
        using var scope = new PreferencesFileScope();
        MainViewModel vm = NewVm();
        try
        {
            FavoriteSet other = vm.FavoriteStore.CreateNamedSet("FHI-AlreadyLoaded")!;
            vm.SetFavoriteSetLoaded(other.Id, true);
            var settings = new SettingsViewModel(vm.Preferences);
            using var target = new SettingsViewModelImportTarget(settings, vm.FavoriteStore);
            ImportThroughHub(target, vm, WriteLibraryExport("FHI-StagedLoad", "FHI-StagedLoadFav"), SettingsImportMode.Merge);
            string stagedId = target.FavoritesToExport.FindNamedSet("FHI-StagedLoad")!.Id;

            var hub = new ImportExportViewModel(
                target,
                new SettingsViewModelExportSource(settings, vm.Preferences, target, selectedMacros: null),
                new ImportExportOpening(new HashSet<SettingsItemType> { SettingsItemType.Favorites }, ImportExportTab.Export),
                new ImportExportFiles(
                    "test-version",
                    path => File.Open(path, FileMode.CreateNew),
                    Path.Combine(_root, "backups"),
                    TimeProvider.System
                ),
                vm.Preferences
            );
            ExportItemRow favoritesRow = hub.ExportItems.Single(r => r.ItemType == SettingsItemType.Favorites);
            Assert.Equal(FavoritesExportChoice.AllSets, favoritesRow.FavoritesChoice);
            Assert.Contains(favoritesRow.FavoritesChoices, c => c.SetId == stagedId);
            SettingsBundleEntry favorites = Assert.Single(Assert.IsType<SettingsExportPlan>(hub.PrepareExport()).Entries);

            Assert.Equal(SettingsBundleFormats.FavoritesLibraryZip, favorites.Format);
            List<string> loaded = LoadedSetIdsOfLibrary(favorites.Content);
            Assert.Contains(stagedId, loaded);
            Assert.Contains(other.Id, loaded);
            Assert.DoesNotContain(stagedId, vm.Preferences.LoadedFavoriteSetIds);
        }
        finally
        {
            Cleanup(vm);
        }
    }

    private static List<string> LoadedSetIdsOfLibrary(byte[] library)
    {
        using var zip = new ZipArchive(new MemoryStream(library), ZipArchiveMode.Read);
        using Stream manifest = zip.GetEntry("library.json")!.Open();
        JsonArray ids = JsonNode.Parse(manifest)!["loadedSetIds"]!.AsArray();
        return [.. ids.Select(id => id!.GetValue<string>())];
    }

    private static MainViewModel NewVm() => new(new FakeFilePickerService());

    // A staged copy left in the staged-favorites folder, marked with the owner's process id when it has one.
    private static string LeftoverCopy(string name, string? owner)
    {
        string folder = Path.Combine(SettingsViewModelImportTarget.StagedFavoritesFolder, name);
        Directory.CreateDirectory(Path.Combine(folder, "sets"));
        if (owner is not null)
        {
            File.WriteAllText(Path.Combine(folder, SettingsViewModelImportTarget.OwnerMarkerFileName), owner);
        }

        return folder;
    }

    private static int ExitedProcessId()
    {
        var start = new ProcessStartInfo("dotnet", "--version")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using Process process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.Id;
    }

    private static List<string> StagedCopies() =>
        Directory.Exists(SettingsViewModelImportTarget.StagedFavoritesFolder)
            ? [.. Directory.EnumerateDirectories(SettingsViewModelImportTarget.StagedFavoritesFolder)]
            : [];

    private static void ImportThroughLiveHub(MainViewModel vm, string path, SettingsImportMode mode) =>
        ImportThroughHub(new UserPreferencesImportTarget(vm.Preferences, vm.FavoriteStore), vm, path, mode);

    private static void ImportThroughHub(ISettingsImportTarget target, MainViewModel vm, string path, SettingsImportMode mode)
    {
        var hub = new ImportExportViewModel(
            target,
            new UserPreferencesExportSource(vm.Preferences, vm.FavoriteStore),
            new ImportExportOpening(new HashSet<SettingsItemType>(), ImportExportTab.Import),
            new ImportExportFiles(
                "test-version",
                tempPath => File.Open(tempPath, FileMode.CreateNew),
                Path.Combine(Path.GetDirectoryName(path)!, "backups"),
                TimeProvider.System
            ),
            vm.Preferences
        );
        hub.OpenImportFile(path);
        ImportItemRow row = hub.ImportItems.Single(r => r.ItemType == SettingsItemType.Favorites);
        row.Mode = mode;
        hub.Import();
        Assert.Null(hub.ImportError);

        // The store's Changed event refreshes the bar through a dispatcher post.
        Dispatcher.UIThread.RunJobs();
    }

    private static void Cleanup(MainViewModel vm)
    {
        FavoriteStore store = vm.FavoriteStore;
        foreach (FavoriteCommand favorite in store.AllFavorites.Where(f => f.Label.StartsWith("FHI-", StringComparison.Ordinal)).ToList())
        {
            store.DeleteFavorite(favorite.Id);
        }

        foreach (
            FavoriteSet set in store
                .OrderedSets.Where(s => (s.Kind == FavoriteSetKind.Named) && s.Name.StartsWith("FHI-", StringComparison.Ordinal))
                .ToList()
        )
        {
            vm.Preferences.SetFavoriteSetLoaded(set.Id, false);
            store.DeleteSet(set.Id);
        }
    }

    // A store of its own holding one named set with one favorite, as another user's export would.
    private FavoriteStore SourceStore(string setName, string favoriteLabel, out FavoriteSet set)
    {
        var store = new FavoriteStore(Path.Combine(_root, "source-" + Guid.NewGuid().ToString("N")));
        set = store.CreateNamedSet(setName)!;
        var favorite = new FavoriteCommand { Label = favoriteLabel, CommandText = favoriteLabel };
        store.SaveFavorite(favorite);
        store.AddToSet(set.Id, favorite.Id);
        return store;
    }

    private string WriteSetExport(string setName, string favoriteLabel)
    {
        FavoriteStore store = SourceStore(setName, favoriteLabel, out FavoriteSet set);
        string path = Path.Combine(_root, setName + FavoriteExport.SetExportExtension);
        using FileStream stream = File.Create(path);
        FavoriteExport.ExportSet(store, set.Id, stream);
        return path;
    }

    private string WriteLibraryExport(string loadedSetName, string favoriteLabel)
    {
        FavoriteStore store = SourceStore(loadedSetName, favoriteLabel, out FavoriteSet set);
        string path = Path.Combine(_root, loadedSetName + FavoriteExport.LibraryExportExtension);
        using FileStream stream = File.Create(path);
        FavoriteExport.ExportLibrary(store, [set.Id], stream);
        return path;
    }
}
