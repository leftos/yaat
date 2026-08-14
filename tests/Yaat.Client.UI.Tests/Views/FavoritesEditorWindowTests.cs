using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Smoke coverage for the Favorites Editor window: the container pane lists every set as a peer
/// (Global first, plus the "Not in any set" view), selecting a set shows its favorites, the
/// Loaded checkbox drives the loaded-set id list, and loading a set feeds its favorites into
/// DisplayFavorites.
/// </summary>
public class FavoritesEditorWindowTests
{
    private static FavoriteStore NewStore() => new(Path.Combine(Path.GetTempPath(), "yaat-editor-tests", Guid.NewGuid().ToString("N")));

    [AvaloniaFact]
    public void Editor_ListsContainers_AndShowsSelectedSetFavorites()
    {
        var prefs = new UserPreferences();
        var store = NewStore();
        var set = store.CreateNamedSet("FSE-Smoke")!;
        var favorite = new FavoriteCommand { Label = "SmokeFav", CommandText = "FH 090" };
        store.SaveFavorite(favorite);
        store.AddToSet(set.Id, favorite.Id);

        var editor = new FavoritesEditorWindow(prefs, store);
        editor.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            var containers = editor.FindControl<ListBox>("ContainersList");
            Assert.NotNull(containers);
            var items = Assert.IsAssignableFrom<IEnumerable<ListBoxItem>>(containers.ItemsSource).ToList();

            // Global is always the first container; the named set and the orphans view are present.
            Assert.Equal(store.GlobalSet.Id, items[0].Tag as string);
            var setRow = items.Single(i => string.Equals(i.Tag as string, set.Id, StringComparison.Ordinal));
            Assert.Contains(items, i => (i.Tag as string) == "*orphans*");

            containers.SelectedItem = setRow;
            Dispatcher.UIThread.RunJobs();

            var favorites = editor.FindControl<ListBox>("FavoritesList");
            Assert.NotNull(favorites);
            var rows = Assert.IsAssignableFrom<IEnumerable<string>>(favorites.ItemsSource).ToList();
            var row = Assert.Single(rows);
            Assert.Contains("SmokeFav", row);
        }
        finally
        {
            editor.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void Editor_LoadedCheckbox_TogglesLoadedSetIdList()
    {
        var prefs = new UserPreferences();
        var store = NewStore();
        var set = store.CreateNamedSet("FSE-Toggle")!;

        var editor = new FavoritesEditorWindow(prefs, store);
        editor.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            var containers = editor.FindControl<ListBox>("ContainersList");
            Assert.NotNull(containers);
            var items = Assert.IsAssignableFrom<IEnumerable<ListBoxItem>>(containers.ItemsSource).ToList();
            var setRow = items.Single(i => string.Equals(i.Tag as string, set.Id, StringComparison.Ordinal));
            var checkbox = ((StackPanel)setRow.Content!).Children.OfType<CheckBox>().Single();

            checkbox.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(set.Id, prefs.LoadedFavoriteSetIds);

            checkbox.IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(set.Id, prefs.LoadedFavoriteSetIds);
        }
        finally
        {
            editor.Close();
            Dispatcher.UIThread.RunJobs();
            prefs.SetFavoriteSetLoaded(set.Id, false);
        }
    }

    [AvaloniaFact]
    public void LoadingSet_FeedsDisplayFavorites_AndUnloadingRemovesThem()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        var set = vm.FavoriteStore.CreateNamedSet("FSE-Display")!;
        var favorite = new FavoriteCommand { Label = "FromSet", CommandText = "FH 180" };
        vm.FavoriteStore.SaveFavorite(favorite);
        vm.FavoriteStore.AddToSet(set.Id, favorite.Id);

        try
        {
            Assert.DoesNotContain(vm.DisplayFavorites, e => e.Favorite.Label == "FromSet");

            vm.SetFavoriteSetLoaded(set.Id, true);
            Assert.Contains(vm.DisplayFavorites, e => e.Favorite.Label == "FromSet");

            vm.SetFavoriteSetLoaded(set.Id, false);
            Assert.DoesNotContain(vm.DisplayFavorites, e => e.Favorite.Label == "FromSet");
        }
        finally
        {
            vm.FavoriteStore.DeleteFavorite(favorite.Id);
            vm.FavoriteStore.DeleteSet(set.Id);
            Dispatcher.UIThread.RunJobs();
        }
    }
}
