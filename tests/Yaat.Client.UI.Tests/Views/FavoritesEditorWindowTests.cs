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
        FavoriteStore store = NewStore();
        FavoriteSet set = store.CreateNamedSet("FSE-Smoke")!;
        var favorite = new FavoriteCommand { Label = "SmokeFav", CommandText = "FH 090" };
        store.SaveFavorite(favorite);
        store.AddToSet(set.Id, favorite.Id);

        var editor = new FavoritesEditorWindow(prefs, store);
        editor.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            ListBox? containers = editor.FindControl<ListBox>("ContainersList");
            Assert.NotNull(containers);
            var items = Assert.IsAssignableFrom<IEnumerable<ListBoxItem>>(containers.ItemsSource).ToList();

            // Global is always the first container; the named set and the orphans view are present.
            Assert.Equal(store.GlobalSet.Id, items[0].Tag as string);
            ListBoxItem setRow = items.Single(i => string.Equals(i.Tag as string, set.Id, StringComparison.Ordinal));
            Assert.Contains(items, i => (i.Tag as string) == "*orphans*");

            containers.SelectedItem = setRow;
            Dispatcher.UIThread.RunJobs();

            ListBox? favorites = editor.FindControl<ListBox>("FavoritesList");
            Assert.NotNull(favorites);
            var rows = Assert.IsAssignableFrom<IEnumerable<string>>(favorites.ItemsSource).ToList();
            string row = Assert.Single(rows);
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
        FavoriteStore store = NewStore();
        FavoriteSet set = store.CreateNamedSet("FSE-Toggle")!;

        var editor = new FavoritesEditorWindow(prefs, store);
        editor.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            ListBox? containers = editor.FindControl<ListBox>("ContainersList");
            Assert.NotNull(containers);
            var items = Assert.IsAssignableFrom<IEnumerable<ListBoxItem>>(containers.ItemsSource).ToList();
            ListBoxItem setRow = items.Single(i => string.Equals(i.Tag as string, set.Id, StringComparison.Ordinal));
            CheckBox checkbox = ((StackPanel)setRow.Content!).Children.OfType<CheckBox>().Single();

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
        FavoriteSet set = vm.FavoriteStore.CreateNamedSet("FSE-Display")!;
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

    [AvaloniaFact]
    public void Editor_SetAndSelectionButtons_ReadLinkDuplicateAndMove()
    {
        var prefs = new UserPreferences();
        FavoriteStore store = NewStore();
        var editor = new FavoritesEditorWindow(prefs, store);
        editor.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            Assert.Equal("Link to...", editor.FindControl<Button>("LinkToButton")!.Content as string);
            Assert.Equal("Duplicate to...", editor.FindControl<Button>("DuplicateToButton")!.Content as string);
            Assert.Equal("Move to...", editor.FindControl<Button>("MoveToButton")!.Content as string);
            Assert.Equal("Duplicate set...", editor.FindControl<Button>("DuplicateSetButton")!.Content as string);
            Assert.Null(editor.FindControl<Button>("AddToButton"));
        }
        finally
        {
            editor.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void TransferSelection_Duplicate_AddsIndependentCopiesToTheTarget()
    {
        var prefs = new UserPreferences();
        FavoriteStore store = NewStore();
        FavoriteSet setA = store.CreateNamedSet("A")!;
        FavoriteSet setB = store.CreateNamedSet("B")!;
        FavoriteCommand f1 = new() { Label = "One", CommandText = "FH 090" };
        FavoriteCommand f2 = new() { Label = "Two", CommandText = "FH 180" };
        foreach (FavoriteCommand favorite in new[] { f1, f2 })
        {
            store.SaveFavorite(favorite);
            store.AddToSet(setA.Id, favorite.Id);
        }

        var editor = new FavoritesEditorWindow(prefs, store);
        editor.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            SelectContainer(editor, setA.Id);
            editor.FindControl<ListBox>("FavoritesList")!.SelectAll();
            Dispatcher.UIThread.RunJobs();

            editor.TransferSelection(setB.Id, FavoriteTransferMode.Duplicate);

            Assert.Equal(["One", "Two"], store.GetSetFavorites(setB.Id).Select(f => f.Label));
            Assert.DoesNotContain(store.GetSet(setB.Id)!.FavoriteIds, id => (id == f1.Id) || (id == f2.Id));
            Assert.Equal([f1.Id, f2.Id], store.GetSet(setA.Id)!.FavoriteIds);
        }
        finally
        {
            editor.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void TransferSelection_Link_SharesTheSameFavorite()
    {
        var prefs = new UserPreferences();
        FavoriteStore store = NewStore();
        FavoriteSet setA = store.CreateNamedSet("A")!;
        FavoriteSet setB = store.CreateNamedSet("B")!;
        FavoriteCommand f1 = new() { Label = "One", CommandText = "FH 090" };
        FavoriteCommand f2 = new() { Label = "Two", CommandText = "FH 180" };
        foreach (FavoriteCommand favorite in new[] { f1, f2 })
        {
            store.SaveFavorite(favorite);
            store.AddToSet(setA.Id, favorite.Id);
        }

        var editor = new FavoritesEditorWindow(prefs, store);
        editor.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            SelectContainer(editor, setA.Id);
            editor.FindControl<ListBox>("FavoritesList")!.SelectAll();
            Dispatcher.UIThread.RunJobs();

            editor.TransferSelection(setB.Id, FavoriteTransferMode.Link);

            Assert.Equal([f1.Id, f2.Id], store.GetSet(setB.Id)!.FavoriteIds);
            Assert.Equal([f1.Id, f2.Id], store.GetSet(setA.Id)!.FavoriteIds);
        }
        finally
        {
            editor.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void SelectContainer(FavoritesEditorWindow editor, string containerId)
    {
        ListBox containers = editor.FindControl<ListBox>("ContainersList")!;
        var items = Assert.IsAssignableFrom<IEnumerable<ListBoxItem>>(containers.ItemsSource).ToList();
        containers.SelectedItem = items.Single(i => string.Equals(i.Tag as string, containerId, StringComparison.Ordinal));
        Dispatcher.UIThread.RunJobs();
    }
}
