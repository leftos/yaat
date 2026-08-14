using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests;

// MainViewModel-level membership behavior. The VM's FavoriteStore lives under the per-process
// YAAT_APPDATA_DIR, shared by every test in this assembly, so each test uses a unique "FMT-"
// label/set prefix and deletes everything it created in a finally block.
public class FavoriteMembershipTests
{
    private static MainViewModel NewVm() => new(new FakeFilePickerService());

    private static FavoriteCommand Fav(string label) => new() { Label = label, CommandText = label };

    private static void Cleanup(MainViewModel vm)
    {
        var store = vm.FavoriteStore;
        foreach (var favorite in store.AllFavorites.Where(f => f.Label.StartsWith("FMT-")).ToList())
        {
            store.DeleteFavorite(favorite.Id);
        }

        foreach (var set in store.OrderedSets.Where(s => (s.Kind == FavoriteSetKind.Named) && s.Name.StartsWith("FMT-")).ToList())
        {
            vm.Preferences.SetFavoriteSetLoaded(set.Id, false);
            store.DeleteSet(set.Id);
        }
    }

    [AvaloniaFact]
    public void AddFavorite_ToGlobalAndNamedSet_SharesOneEntity()
    {
        var vm = NewVm();
        try
        {
            var set = vm.FavoriteStore.CreateNamedSet("FMT-Both")!;
            var fav = Fav("FMT-Shared");

            vm.AddFavorite(fav, [vm.FavoriteStore.GlobalSet.Id, set.Id]);

            Assert.Same(fav, Assert.Single(vm.FavoriteStore.GetSetFavorites(set.Id), f => f.Label == "FMT-Shared"));
            Assert.Same(fav, Assert.Single(vm.FavoriteStore.GetSetFavorites(vm.FavoriteStore.GlobalSet.Id), f => f.Label == "FMT-Shared"));

            // Loading the set shows the same entity once per visible container.
            vm.SetFavoriteSetLoaded(set.Id, true);
            var entries = vm.DisplayFavorites.Where(e => e.Favorite.Id == fav.Id).ToList();
            Assert.Equal(2, entries.Count);
            Assert.All(entries, e => Assert.Same(fav, e.Favorite));
        }
        finally
        {
            Cleanup(vm);
        }
    }

    [AvaloniaFact]
    public void UpdateFavorite_EditsEntityEverywhere_AndSyncsMembership()
    {
        var vm = NewVm();
        try
        {
            var setA = vm.FavoriteStore.CreateNamedSet("FMT-A")!;
            var setB = vm.FavoriteStore.CreateNamedSet("FMT-B")!;
            var fav = Fav("FMT-Orig");
            vm.AddFavorite(fav, [vm.FavoriteStore.GlobalSet.Id, setA.Id]);

            var updated = fav.Clone();
            updated.Label = "FMT-Edited";
            vm.UpdateFavorite(updated, [setA.Id, setB.Id]);

            // Same id everywhere: A keeps it (edited), B gains it, Global loses it.
            Assert.Equal("FMT-Edited", Assert.Single(vm.FavoriteStore.GetSetFavorites(setA.Id)).Label);
            Assert.Equal("FMT-Edited", Assert.Single(vm.FavoriteStore.GetSetFavorites(setB.Id)).Label);
            Assert.DoesNotContain(vm.FavoriteStore.GetSetFavorites(vm.FavoriteStore.GlobalSet.Id), f => f.Id == fav.Id);
            Assert.Single(vm.FavoriteStore.AllFavorites, f => f.Id == fav.Id);
        }
        finally
        {
            Cleanup(vm);
        }
    }

    [AvaloniaFact]
    public void UpdateFavorite_RemovedFromEverySet_BecomesOrphanNotDeleted()
    {
        var vm = NewVm();
        try
        {
            var fav = Fav("FMT-Orphan");
            vm.AddFavorite(fav, [vm.FavoriteStore.GlobalSet.Id]);

            vm.UpdateFavorite(fav.Clone(), []);

            Assert.NotNull(vm.FavoriteStore.GetFavorite(fav.Id));
            Assert.Contains(vm.FavoriteStore.GetOrphanFavorites(), f => f.Id == fav.Id);
            Assert.DoesNotContain(vm.DisplayFavorites, e => e.Favorite.Id == fav.Id);
        }
        finally
        {
            Cleanup(vm);
        }
    }

    [AvaloniaFact]
    public void DeleteFavorite_RemovesTheEntityFromEverySet()
    {
        var vm = NewVm();
        try
        {
            var set = vm.FavoriteStore.CreateNamedSet("FMT-Del")!;
            var fav = Fav("FMT-Gone");
            vm.AddFavorite(fav, [vm.FavoriteStore.GlobalSet.Id, set.Id]);

            vm.DeleteFavorite(fav.Id);

            Assert.Null(vm.FavoriteStore.GetFavorite(fav.Id));
            Assert.Empty(vm.FavoriteStore.GetSetFavorites(set.Id));
            Assert.DoesNotContain(vm.DisplayFavorites, e => e.Favorite.Id == fav.Id);
        }
        finally
        {
            Cleanup(vm);
        }
    }

    [AvaloniaFact]
    public void ContainerOptions_ListGlobalFirst_AndPendingActiveAirport()
    {
        var vm = NewVm();
        try
        {
            vm.ActiveScenarioPrimaryAirportId = "FMT";

            var options = vm.BuildFavoriteContainerOptions();

            Assert.Equal(FavoriteSetKind.Global, options[0].Kind);
            Assert.Equal(vm.FavoriteStore.GlobalSet.Id, options[0].SetId);

            var pendingAirport = Assert.Single(options, o => (o.Kind == FavoriteSetKind.Airport) && (o.Key == "FMT"));
            Assert.Null(pendingAirport.SetId);

            // Checking the pending option creates the container on demand — exactly once.
            var setId = vm.EnsureFavoriteContainer(pendingAirport);
            Assert.Equal(setId, vm.EnsureFavoriteContainer(pendingAirport));
            Assert.Equal(setId, vm.FavoriteStore.FindAirportSet("FMT")!.Id);
        }
        finally
        {
            if (NewVmStoreAirport(vm) is { } set)
            {
                vm.FavoriteStore.DeleteSet(set.Id);
            }
            vm.ActiveScenarioPrimaryAirportId = null;
        }

        static FavoriteSet? NewVmStoreAirport(MainViewModel vm) => vm.FavoriteStore.FindAirportSet("FMT");
    }
}
