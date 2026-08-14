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
/// Smoke coverage for the Favorites Editor window (issue #354): the container pane lists the
/// base pool plus every named set, selecting a set shows its favorites, the Loaded checkbox
/// drives the loaded-set list, and loading a set feeds its favorites into DisplayFavorites.
/// </summary>
public class FavoritesEditorWindowTests
{
    [AvaloniaFact]
    public void Editor_ListsContainers_AndShowsSelectedSetFavorites()
    {
        var prefs = new UserPreferences();
        prefs.CreateFavoriteSet("FSE-Smoke");
        prefs.SetFavoriteSetFavorites("FSE-Smoke", [new FavoriteCommand { Label = "SmokeFav", CommandText = "FH 090" }]);

        var editor = new FavoritesEditorWindow(prefs);
        editor.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            var containers = editor.FindControl<ListBox>("ContainersList");
            Assert.NotNull(containers);
            var items = Assert.IsAssignableFrom<IEnumerable<ListBoxItem>>(containers.ItemsSource).ToList();

            // First row is always the base pool; the created set is present.
            Assert.Null(items[0].Tag);
            var setRow = items.Single(i => string.Equals(i.Tag as string, "FSE-Smoke", StringComparison.Ordinal));

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
            prefs.DeleteFavoriteSet("FSE-Smoke");
        }
    }

    [AvaloniaFact]
    public void Editor_LoadedCheckbox_TogglesLoadedSetList()
    {
        var prefs = new UserPreferences();
        prefs.CreateFavoriteSet("FSE-Toggle");

        var editor = new FavoritesEditorWindow(prefs);
        editor.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            var containers = editor.FindControl<ListBox>("ContainersList");
            Assert.NotNull(containers);
            var items = Assert.IsAssignableFrom<IEnumerable<ListBoxItem>>(containers.ItemsSource).ToList();
            var setRow = items.Single(i => string.Equals(i.Tag as string, "FSE-Toggle", StringComparison.Ordinal));
            var checkbox = ((StackPanel)setRow.Content!).Children.OfType<CheckBox>().Single();

            checkbox.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("FSE-Toggle", prefs.LoadedFavoriteSetNames);

            checkbox.IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain("FSE-Toggle", prefs.LoadedFavoriteSetNames);
        }
        finally
        {
            editor.Close();
            Dispatcher.UIThread.RunJobs();
            prefs.DeleteFavoriteSet("FSE-Toggle");
        }
    }

    [AvaloniaFact]
    public void LoadingSet_FeedsDisplayFavorites_AndUnloadingRemovesThem()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.Preferences.CreateFavoriteSet("FSE-Display");
        vm.Preferences.SetFavoriteSetFavorites("FSE-Display", [new FavoriteCommand { Label = "FromSet", CommandText = "FH 180" }]);

        try
        {
            Assert.DoesNotContain(vm.DisplayFavorites, f => f.Label == "FromSet");

            vm.SetFavoriteSetLoaded("FSE-Display", true);
            Assert.Contains(vm.DisplayFavorites, f => f.Label == "FromSet");

            vm.SetFavoriteSetLoaded("FSE-Display", false);
            Assert.DoesNotContain(vm.DisplayFavorites, f => f.Label == "FromSet");
        }
        finally
        {
            vm.Preferences.DeleteFavoriteSet("FSE-Display");
            Dispatcher.UIThread.RunJobs();
        }
    }
}
