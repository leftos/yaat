using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The bar's Sets flyout: one row per set in bar order, each with ▲/▼ buttons that move it, a row text
/// that toggles a named set's loading, then "Manage sets…". The VM's FavoriteStore is shared by every
/// test in this assembly, so each test uses an "FSF-" prefix and deletes everything it created.
/// </summary>
public class FavoritesSetsFlyoutTests
{
    private const string AirportId = "FSF";

    private static MainViewModel NewVm() => new(new FakeFilePickerService());

    private static void Cleanup(MainViewModel vm)
    {
        FavoriteStore store = vm.FavoriteStore;
        foreach (FavoriteSet set in store.OrderedSets.Where(s => (s.Kind == FavoriteSetKind.Named) && s.Name.StartsWith("FSF-")).ToList())
        {
            vm.Preferences.RemoveFavoriteSet(set.Id);
            store.DeleteSet(set.Id);
        }

        if (store.FindAirportSet(AirportId) is { } airport)
        {
            store.DeleteSet(airport.Id);
        }

        vm.ActiveScenarioPrimaryAirportId = null;
    }

    private static List<MenuItem> SetRows(MenuFlyout flyout) => [.. flyout.Items.OfType<MenuItem>().Where(item => item.Header is Grid)];

    private static string Label(MenuItem row) => ((Grid)row.Header!).Children.OfType<TextBlock>().Single(t => Grid.GetColumn(t) == 1).Text!;

    private static MenuItem Row(MenuFlyout flyout, string label) => SetRows(flyout).Single(row => Label(row) == label);

    /// <summary>The rows this test owns (Global, the FSF airport, FSF- sets), so sets other tests leave behind cannot interfere.</summary>
    private static List<string> OurLabels(MenuFlyout flyout) =>
        [.. SetRows(flyout).Select(Label).Where(l => (l == "Global") || l.StartsWith("FSF-") || (l == $"Airport ({AirportId})"))];

    private static Button MoveButton(MenuItem row, string glyph) =>
        ((Grid)row.Header!).Children.OfType<StackPanel>().Single().Children.OfType<Button>().Single(b => (string?)b.Content == glyph);

    private static (Window Window, MenuFlyout Flyout) ShowFlyout(MainViewModel vm)
    {
        var anchor = new Button { Content = "anchor" };
        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = anchor,
        };
        window.ShowAndRunLayout();

        var flyout = new MenuFlyout();
        FavoritesBarView.FillSetsFlyout(flyout, vm, () => { });
        flyout.ShowAt(anchor);
        return (window, flyout);
    }

    [AvaloniaFact]
    public void Flyout_ListsGlobal_TheAirportRowOnlyWithItsSet_NamedSetsLoadedAndUnloaded_ThenManageSets()
    {
        MainViewModel vm = NewVm();
        try
        {
            vm.ActiveScenarioPrimaryAirportId = AirportId;
            FavoriteSet loaded = vm.FavoriteStore.CreateNamedSet("FSF-Loaded")!;
            vm.FavoriteStore.CreateNamedSet("FSF-Unloaded");
            vm.SetFavoriteSetLoaded(loaded.Id, true);

            var flyout = new MenuFlyout();
            FavoritesBarView.FillSetsFlyout(flyout, vm, () => { });
            Assert.Equal(["Global", "FSF-Loaded", "FSF-Unloaded"], OurLabels(flyout));
            Assert.IsType<Separator>(flyout.Items[^2]);
            Assert.Equal("Manage sets…", Assert.IsType<MenuItem>(flyout.Items[^1]).Header);

            vm.FavoriteStore.GetOrCreateAirportSet(AirportId);
            FavoritesBarView.FillSetsFlyout(flyout, vm, () => { });
            Assert.Equal(["Global", $"Airport ({AirportId})", "FSF-Loaded", "FSF-Unloaded"], OurLabels(flyout));

            Assert.False(MoveButton(Row(flyout, "Global"), "▲").IsEnabled);
            Assert.True(MoveButton(Row(flyout, "Global"), "▼").IsEnabled);
            Assert.False(MoveButton(SetRows(flyout)[^1], "▼").IsEnabled);
        }
        finally
        {
            Cleanup(vm);
        }
    }

    [AvaloniaFact]
    public void MoveDownOnGlobal_MovesItOneVisiblePlace_KeepsLoadedStates_AndRebuildsTheOpenFlyout()
    {
        MainViewModel vm = NewVm();
        (Window? window, MenuFlyout? flyout) = (null, null);
        try
        {
            FavoriteSet loaded = vm.FavoriteStore.CreateNamedSet("FSF-Loaded")!;
            FavoriteSet unloaded = vm.FavoriteStore.CreateNamedSet("FSF-Unloaded")!;
            vm.SetFavoriteSetLoaded(loaded.Id, true);
            (window, flyout) = ShowFlyout(vm);
            Assert.Equal(["Global", "FSF-Loaded", "FSF-Unloaded"], OurLabels(flyout));
            MenuItem globalRowBefore = Row(flyout, "Global");

            MoveButton(globalRowBefore, "▼").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Equal(["FSF-Loaded", "Global", "FSF-Unloaded"], OurLabels(flyout));
            Assert.NotSame(globalRowBefore, Row(flyout, "Global"));
            Assert.True(flyout.IsOpen);
            Assert.Equal([loaded.Id], vm.Preferences.LoadedFavoriteSetIds);
            Assert.False(vm.IsFavoriteSetLoaded(unloaded.Id));
        }
        finally
        {
            flyout?.Hide();
            window?.Close();
            Cleanup(vm);
        }
    }

    [AvaloniaFact]
    public void ClickingANamedRow_TogglesItsLoading_AndClickingASlotRowChangesNothing()
    {
        MainViewModel vm = NewVm();
        (Window? window, MenuFlyout? flyout) = (null, null);
        try
        {
            FavoriteSet set = vm.FavoriteStore.CreateNamedSet("FSF-Toggle")!;
            (window, flyout) = ShowFlyout(vm);

            Row(flyout, "FSF-Toggle").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(vm.IsFavoriteSetLoaded(set.Id));
            Assert.True(flyout.IsOpen);

            List<FavoriteDisplayEntry> barBefore = [.. vm.DisplayFavorites];
            List<string> labelsBefore = OurLabels(flyout);
            Row(flyout, "Global").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

            Assert.Equal([set.Id], vm.Preferences.LoadedFavoriteSetIds);
            Assert.Empty(vm.Preferences.FavoriteSetOrder);
            Assert.Equal(barBefore, vm.DisplayFavorites);
            Assert.Equal(labelsBefore, OurLabels(flyout));
            Assert.True(flyout.IsOpen);

            Row(flyout, "FSF-Toggle").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.False(vm.IsFavoriteSetLoaded(set.Id));
        }
        finally
        {
            flyout?.Hide();
            window?.Close();
            Cleanup(vm);
        }
    }

    [AvaloniaFact]
    public void Bar_ShowsTheSetsButton_WithNoNamedSets()
    {
        MainViewModel vm = NewVm();
        Assert.Empty(vm.NamedFavoriteSets);
        var view = new FavoritesBarView();
        var window = new Window
        {
            Width = 800,
            Height = 200,
            Content = view,
            DataContext = vm,
        };
        try
        {
            window.ShowAndRunLayout();

            Assert.Single(view.GetVisualDescendants().OfType<Button>(), b => (b.Content as string) == "Sets");
        }
        finally
        {
            window.Close();
        }
    }
}
