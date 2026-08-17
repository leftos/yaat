using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Coverage for issue #364 ("Favorites Panel bad redirect"): with a non-Air category tab selected,
/// any palette rebuild — adding/editing/deleting a favorite or changing the column count — snapped
/// the panel back to the Air tab. The rebuild re-adds the four TabItems in fixed order, Avalonia's
/// TabControl auto-selects the first item added (Air), and that mid-rebuild SelectionChanged echo
/// overwrote the remembered category before the intended tab was even constructed.
/// </summary>
public class FavoritesPanelTabSelectionTests
{
    private static (Window Window, MainViewModel Vm, TabControl Tabs) ShowPalette()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        var view = new FavoritesBarView { IsPaletteMode = true };
        var window = new Window
        {
            Width = 800,
            Height = 450,
            Content = view,
            DataContext = vm,
        };
        window.ShowAndRunLayout();

        var tabs = view.GetVisualDescendants().OfType<TabControl>().Single();
        return (window, vm, tabs);
    }

    private static void SelectTab(TabControl tabs, string header)
    {
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => t.Header as string == header);
        Dispatcher.UIThread.RunJobs();
    }

    private static string? SelectedHeader(TabControl tabs) => (tabs.SelectedItem as TabItem)?.Header as string;

    [AvaloniaFact]
    public void AddAndDeleteFavorite_KeepsSelectedCategoryTab()
    {
        var (window, vm, tabs) = ShowPalette();
        var fav = new FavoriteCommand
        {
            Label = "TabSelTestFav364",
            CommandText = "TAXI A",
            Category = FavoriteCommandCategory.Ground,
        };
        var added = false;
        try
        {
            SelectTab(tabs, "Ground");

            vm.AddFavorite(fav, [vm.FavoriteStore.GlobalSet.Id]);
            added = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Ground", SelectedHeader(tabs));

            vm.DeleteFavorite(fav.Id);
            added = false;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Ground", SelectedHeader(tabs));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
            if (added)
            {
                vm.DeleteFavorite(fav.Id);
            }
        }
    }

    [AvaloniaFact]
    public void ColumnCountChange_KeepsSelectedCategoryTab()
    {
        var (window, vm, tabs) = ShowPalette();
        var originalColumns = vm.Preferences.FavoritePanelColumns;
        try
        {
            SelectTab(tabs, "Airport");

            var columnsBox = window.GetVisualDescendants().OfType<NumericUpDown>().Single();
            columnsBox.Value = originalColumns + 1;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("Airport", SelectedHeader(tabs));
        }
        finally
        {
            vm.Preferences.SetFavoritePanelColumns(originalColumns);
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
