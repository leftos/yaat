using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests;

// A layout carries the bar order of the favorite sets beside the loaded list, and applying it sets each one it saved.
// The VM's FavoriteStore and preferences live under the per-process YAAT_APPDATA_DIR, shared by every test in this
// assembly, so each test names its sets "LSO-…" and restores the order and loaded list it found in a finally block.
public class UserPreferencesLayoutSetOrderTests
{
    private const string Global = FavoriteSetOrdering.GlobalSlot;
    private const string Airport = FavoriteSetOrdering.AirportSlot;
    private const string Scenario = FavoriteSetOrdering.ScenarioSlot;

    [Fact]
    public void SaveLayout_RoundTripsTheFavoriteSetOrderThroughDisk()
    {
        var writer = new UserPreferences();
        writer.SaveLayout(
            new SavedLayout
            {
                Name = "LSO-Roundtrip",
                LoadedFavoriteSetIds = ["aaaa1111"],
                FavoriteSetOrder = [Scenario, "bbbb2222", Global, "aaaa1111", Airport],
            }
        );

        SavedLayout? reloaded = new UserPreferences().GetLayout("LSO-Roundtrip");

        Assert.NotNull(reloaded);
        Assert.Equal([Scenario, "bbbb2222", Global, "aaaa1111", Airport], reloaded.FavoriteSetOrder);
        Assert.Equal(["aaaa1111"], reloaded.LoadedFavoriteSetIds);
        new UserPreferences().DeleteLayout("LSO-Roundtrip");
    }

    [Fact]
    public void SaveLayout_WithoutAnOrder_ReloadsWithANullOrder()
    {
        new UserPreferences().SaveLayout(new SavedLayout { Name = "LSO-NoOrder", LoadedFavoriteSetIds = ["aaaa1111"] });

        SavedLayout? reloaded = new UserPreferences().GetLayout("LSO-NoOrder");

        Assert.NotNull(reloaded);
        Assert.Null(reloaded.FavoriteSetOrder);
        new UserPreferences().DeleteLayout("LSO-NoOrder");
    }

    [AvaloniaFact]
    public void ApplyFavoriteSets_WithAnOrderAndNoLoadedList_ChangesOnlyTheOrder()
    {
        MainViewModel vm = NewVm();
        List<string> orderBefore = [.. vm.Preferences.FavoriteSetOrder];
        List<string> loadedBefore = [.. vm.Preferences.LoadedFavoriteSetIds];
        try
        {
            FavoriteSet loaded = vm.FavoriteStore.CreateNamedSet("LSO-Loaded")!;
            vm.SetFavoriteSetLoaded(loaded.Id, true);
            List<string> loadedIds = [.. vm.Preferences.LoadedFavoriteSetIds];

            string note = MainWindow.ApplyFavoriteSets(vm, new SavedLayout { FavoriteSetOrder = [loaded.Id, Scenario, Global, Airport] });

            Assert.Equal("", note);
            Assert.Equal([loaded.Id, Scenario, Global, Airport], vm.Preferences.FavoriteSetOrder);
            Assert.Equal(loadedIds, vm.Preferences.LoadedFavoriteSetIds);
        }
        finally
        {
            Restore(vm, orderBefore, loadedBefore);
        }
    }

    [AvaloniaFact]
    public void ApplyFavoriteSets_WithANullOrder_LeavesTheOrderAlone()
    {
        MainViewModel vm = NewVm();
        List<string> orderBefore = [.. vm.Preferences.FavoriteSetOrder];
        List<string> loadedBefore = [.. vm.Preferences.LoadedFavoriteSetIds];
        try
        {
            FavoriteSet set = vm.FavoriteStore.CreateNamedSet("LSO-Kept")!;
            vm.Preferences.SetFavoriteSetOrder([Airport, set.Id, Global, Scenario]);

            MainWindow.ApplyFavoriteSets(vm, new SavedLayout { LoadedFavoriteSetIds = [set.Id] });

            Assert.Equal([Airport, set.Id, Global, Scenario], vm.Preferences.FavoriteSetOrder);
            Assert.Equal([set.Id], vm.Preferences.LoadedFavoriteSetIds);
        }
        finally
        {
            Restore(vm, orderBefore, loadedBefore);
        }
    }

    [AvaloniaFact]
    public void ApplyFavoriteSets_CountsOnlyLoadedIdsWithoutASetAsMissing_NeverSlotTokensOrOrderIds()
    {
        MainViewModel vm = NewVm();
        List<string> orderBefore = [.. vm.Preferences.FavoriteSetOrder];
        List<string> loadedBefore = [.. vm.Preferences.LoadedFavoriteSetIds];
        try
        {
            FavoriteSet set = vm.FavoriteStore.CreateNamedSet("LSO-Present")!;
            var layout = new SavedLayout
            {
                LoadedFavoriteSetIds = [set.Id, "ffff0000"],
                FavoriteSetOrder = [Scenario, "eeee0000", Airport, set.Id, Global],
            };

            string note = MainWindow.ApplyFavoriteSets(vm, layout);

            Assert.Equal(" (1 favorite set(s) no longer exist)", note);
            Assert.Equal([Scenario, "eeee0000", Airport, set.Id, Global], vm.Preferences.FavoriteSetOrder);
        }
        finally
        {
            Restore(vm, orderBefore, loadedBefore);
        }
    }

    private static MainViewModel NewVm() => new(new FakeFilePickerService());

    private static void Restore(MainViewModel vm, List<string> orderBefore, List<string> loadedBefore)
    {
        FavoriteStore store = vm.FavoriteStore;
        foreach (
            FavoriteSet set in store
                .OrderedSets.Where(s => (s.Kind == FavoriteSetKind.Named) && s.Name.StartsWith("LSO-", StringComparison.Ordinal))
                .ToList()
        )
        {
            store.DeleteSet(set.Id);
        }

        vm.Preferences.SetFavoriteSetOrder(orderBefore);
        vm.Preferences.SetLoadedFavoriteSets(loadedBefore);
    }
}
