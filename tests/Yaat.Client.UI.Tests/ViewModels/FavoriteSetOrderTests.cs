using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

// The bar order of the favorite sets: MainViewModel's rows and moves, and its preferences storage.
// The VM's FavoriteStore lives under the per-process YAAT_APPDATA_DIR, shared by every test in this
// assembly, so each test uses an "FSO-" prefix and deletes everything it created in a finally block.
public class FavoriteSetOrderTests
{
    private const string Global = FavoriteSetOrdering.GlobalSlot;
    private const string Airport = FavoriteSetOrdering.AirportSlot;
    private const string Scenario = FavoriteSetOrdering.ScenarioSlot;

    private static MainViewModel NewVm() => new(new FakeFilePickerService());

    private static void Cleanup(MainViewModel vm)
    {
        FavoriteStore store = vm.FavoriteStore;
        foreach (FavoriteCommand favorite in store.AllFavorites.Where(f => f.Label.StartsWith("FSO-")).ToList())
        {
            store.DeleteFavorite(favorite.Id);
        }

        foreach (FavoriteSet set in store.OrderedSets.Where(s => (s.Kind == FavoriteSetKind.Named) && s.Name.StartsWith("FSO-")).ToList())
        {
            vm.Preferences.RemoveFavoriteSet(set.Id);
            store.DeleteSet(set.Id);
        }

        vm.ActiveScenarioPrimaryAirportId = null;
    }

    private static List<string> OurRowEntries(MainViewModel vm, params string[] entries) =>
        [.. vm.FavoriteSetRows.Select(r => r.Entry).Where(e => entries.Contains(e, StringComparer.OrdinalIgnoreCase))];

    [AvaloniaFact]
    public void MoveFavoriteSet_StepsGlobalPastTheHiddenAirportSlot_AndTheBarFollows()
    {
        MainViewModel vm = NewVm();
        try
        {
            // An active airport without a set of its own: its slot keeps its place but is hidden.
            vm.ActiveScenarioPrimaryAirportId = "FSO";
            FavoriteSet named = vm.FavoriteStore.CreateNamedSet("FSO-Named")!;
            vm.AddFavorite(new FavoriteCommand { Label = "FSO-G", CommandText = "FSO-G" }, [vm.FavoriteStore.GlobalSet.Id]);
            vm.AddFavorite(new FavoriteCommand { Label = "FSO-N", CommandText = "FSO-N" }, [named.Id]);
            vm.SetFavoriteSetLoaded(named.Id, true);

            Assert.DoesNotContain(vm.FavoriteSetRows, r => r.Entry == Airport);
            Assert.Equal([Global, named.Id], OurRowEntries(vm, Global, named.Id));
            Assert.Equal(["FSO-G", "FSO-N"], vm.DisplayFavorites.Select(e => e.Favorite.Label).Where(l => l.StartsWith("FSO-")));

            vm.MoveFavoriteSet(Global, 1);

            Assert.Equal([named.Id, Airport, Scenario, Global], vm.Preferences.FavoriteSetOrder.Take(4));
            Assert.Equal([named.Id, Global], OurRowEntries(vm, Global, named.Id));
            Assert.Equal(["FSO-N", "FSO-G"], vm.DisplayFavorites.Select(e => e.Favorite.Label).Where(l => l.StartsWith("FSO-")));
            Assert.True(vm.IsFavoriteSetLoaded(named.Id));
        }
        finally
        {
            Cleanup(vm);
        }
    }

    [AvaloniaFact]
    public void MoveUp_OnTheTopVisibleRow_IsANoOp_AndItCannotMoveUp()
    {
        MainViewModel vm = NewVm();
        try
        {
            FavoriteSet named = vm.FavoriteStore.CreateNamedSet("FSO-Bottom")!;

            FavoriteSetRow top = vm.FavoriteSetRows[0];
            Assert.Equal(Global, top.Entry);
            Assert.Equal("Global", top.Label);
            Assert.True(top.IsSlot);
            Assert.False(top.CanMoveUp);
            Assert.True(top.CanMoveDown);
            Assert.False(vm.FavoriteSetRows[^1].CanMoveDown);
            FavoriteSetRow namedRow = Assert.Single(vm.FavoriteSetRows, r => r.Entry == named.Id);
            Assert.Equal("FSO-Bottom", namedRow.Label);
            Assert.False(namedRow.IsSlot);
            Assert.False(namedRow.IsLoaded);

            vm.MoveFavoriteSet(Global, -1);

            Assert.Empty(vm.Preferences.FavoriteSetOrder);
            Assert.Equal(Global, vm.FavoriteSetRows[0].Entry);
        }
        finally
        {
            Cleanup(vm);
        }
    }

    [Fact]
    public void FavoriteSetOrder_RoundTrips_AndRemoveFavoriteSetClearsBothLists()
    {
        var prefs = new UserPreferences();
        prefs.SetFavoriteSetOrder(["aaaa1111", Global, Airport, Scenario, "bbbb2222"]);
        prefs.SetFavoriteSetLoaded("aaaa1111", true);
        prefs.SetFavoriteSetLoaded("bbbb2222", true);
        Assert.Equal(["aaaa1111", Global, Airport, Scenario, "bbbb2222"], new UserPreferences().FavoriteSetOrder);

        int raised = 0;
        prefs.FavoriteSetsChanged += () => raised++;
        prefs.RemoveFavoriteSet("AAAA1111");

        Assert.Equal(1, raised);
        Assert.Equal(["bbbb2222"], prefs.LoadedFavoriteSetIds);
        Assert.Equal([Global, Airport, Scenario, "bbbb2222"], prefs.FavoriteSetOrder);

        var reloaded = new UserPreferences();
        Assert.Equal(["bbbb2222"], reloaded.LoadedFavoriteSetIds);
        Assert.Equal([Global, Airport, Scenario, "bbbb2222"], reloaded.FavoriteSetOrder);
    }
}
