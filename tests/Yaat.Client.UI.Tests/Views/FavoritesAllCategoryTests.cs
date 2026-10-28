using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Coverage for YAAT-1326 (GH #1355): a favorite in the "All" category shows in every palette tab,
/// the category box offers All last, new favorites keep the active tab's category, and reorder in a
/// tab works over the favorites that tab shows.
/// </summary>
public class FavoritesAllCategoryTests
{
    private static readonly string[] TabHeaders = ["Air", "Ground", "Vehicle", "Airport"];

    private static (Window Window, MainViewModel Vm, FavoritesBarView View, TabControl Tabs) ShowPalette()
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

        TabControl tabs = view.GetVisualDescendants().OfType<TabControl>().Single();
        return (window, vm, view, tabs);
    }

    private static TabItem Tab(TabControl tabs, string header) => tabs.Items.OfType<TabItem>().Single(t => t.Header as string == header);

    private static void SelectTab(Window window, TabControl tabs, string header)
    {
        tabs.SelectedItem = Tab(tabs, header);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static IEnumerable<string> TabFavoriteIds(TabControl tabs, string header) =>
        Tab(tabs, header).GetLogicalDescendants().OfType<Button>().Select(b => b.Tag).OfType<FavoriteDisplayEntry>().Select(e => e.Favorite.Id);

    private static FavoriteCommand AddFavorite(MainViewModel vm, List<string> added, string label, FavoriteCommandCategory category)
    {
        var fav = new FavoriteCommand
        {
            Label = label,
            CommandText = "FH 270",
            Category = category,
        };
        vm.AddFavorite(fav, [vm.FavoriteStore.GlobalSet.Id]);
        added.Add(fav.Id);
        Dispatcher.UIThread.RunJobs();
        return fav;
    }

    private static void Cleanup(Window window, MainViewModel vm, List<string> added)
    {
        window.Close();
        Dispatcher.UIThread.RunJobs();
        foreach (string id in added.Where(id => vm.DisplayFavorites.Any(f => f.Favorite.Id == id)))
        {
            vm.DeleteFavorite(id);
        }
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static Control OpenEditorContent(FavoritesBarView view)
    {
        Flyout flyout = Assert.Single(view.OpenEditorFlyouts);
        return Assert.IsAssignableFrom<Control>(flyout.Content);
    }

    private static Button FavoriteButton(TabControl tabs, string header, string favoriteId) =>
        Tab(tabs, header).GetLogicalDescendants().OfType<Button>().Single(b => (b.Tag is FavoriteDisplayEntry e) && (e.Favorite.Id == favoriteId));

    private static void OpenEditFlyout(Window window, Button button)
    {
        Point? translated = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window);
        Assert.NotNull(translated);
        Point centre = translated.Value;
        window.MouseDown(centre, MouseButton.Right);
        window.MouseUp(centre, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Clicks one of the open Edit flyout's insert-blank buttons and returns the spacer it added.</summary>
    private static FavoriteCommand ClickInsertBlank(MainViewModel vm, FavoritesBarView view, List<string> added, string buttonText)
    {
        HashSet<string> before = [.. vm.DisplayFavorites.Select(f => f.Favorite.Id)];
        Click(OpenEditorContent(view).GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == buttonText));
        Dispatcher.UIThread.RunJobs();

        FavoriteCommand blank = vm.DisplayFavorites.Select(f => f.Favorite).Single(f => !before.Contains(f.Id));
        added.Add(blank.Id);
        Assert.True(blank.IsSpacer);
        return blank;
    }

    [AvaloniaFact]
    public void AllFavorite_AppearsInEveryPaletteTab()
    {
        (Window window, MainViewModel vm, _, TabControl tabs) = ShowPalette();
        List<string> added = [];
        try
        {
            FavoriteCommand all = AddFavorite(vm, added, "AllCatTestEvery1326", FavoriteCommandCategory.All);
            FavoriteCommand ground = AddFavorite(vm, added, "AllCatTestGround1326", FavoriteCommandCategory.Ground);

            foreach (string header in TabHeaders)
            {
                Assert.Contains(all.Id, TabFavoriteIds(tabs, header));
            }

            Assert.Contains(ground.Id, TabFavoriteIds(tabs, "Ground"));
            Assert.DoesNotContain(ground.Id, TabFavoriteIds(tabs, "Air"));
        }
        finally
        {
            Cleanup(window, vm, added);
        }
    }

    [AvaloniaFact]
    public void CategoryBox_OffersAllLast_LabelledAll()
    {
        (Window window, _, FavoritesBarView view, _) = ShowPalette();
        try
        {
            view.OpenAddFlyoutForCommand("FH 270");
            Dispatcher.UIThread.RunJobs();

            ComboBox categoryBox = OpenEditorContent(view).GetLogicalDescendants().OfType<ComboBox>().Single();
            FavoriteCommandCategory[] offered = [.. Assert.IsAssignableFrom<IEnumerable<FavoriteCommandCategory>>(categoryBox.ItemsSource)];
            Assert.Equal(
                [
                    FavoriteCommandCategory.Air,
                    FavoriteCommandCategory.Ground,
                    FavoriteCommandCategory.Vehicle,
                    FavoriteCommandCategory.Airport,
                    FavoriteCommandCategory.All,
                ],
                offered
            );
            Assert.Equal("All", offered[^1].ToString());
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void NewFavoriteAddedFromGroundTab_IsGround()
    {
        (Window window, MainViewModel vm, FavoritesBarView view, TabControl tabs) = ShowPalette();
        List<string> added = [];
        try
        {
            SelectTab(window, tabs, "Ground");
            view.OpenAddFlyoutForCommand("TAXI B");
            Dispatcher.UIThread.RunJobs();

            Control content = OpenEditorContent(view);
            Assert.Equal(FavoriteCommandCategory.Ground, content.GetLogicalDescendants().OfType<ComboBox>().Single().SelectedItem);
            content.GetLogicalDescendants().OfType<TextBox>().Single(b => b.PlaceholderText == "Label (e.g. FH 270)").Text =
                "AllCatTestNewGround1326";
            Click(content.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == "Add"));
            Dispatcher.UIThread.RunJobs();

            FavoriteDisplayEntry entry = vm.DisplayFavorites.Single(f => f.Favorite.Label == "AllCatTestNewGround1326");
            added.Add(entry.Favorite.Id);
            Assert.Equal(FavoriteCommandCategory.Ground, entry.Favorite.Category);
        }
        finally
        {
            Cleanup(window, vm, added);
        }
    }

    [AvaloniaFact]
    public void MoveLeftInGroundTab_MovesAnAllFavoriteAmongTheTabsFavorites()
    {
        (Window window, MainViewModel vm, FavoritesBarView view, TabControl tabs) = ShowPalette();
        List<string> added = [];
        try
        {
            FavoriteCommand ground = AddFavorite(vm, added, "AllCatTestMoveGround1326", FavoriteCommandCategory.Ground);
            AddFavorite(vm, added, "AllCatTestMoveAir1326", FavoriteCommandCategory.Air);
            FavoriteCommand all = AddFavorite(vm, added, "AllCatTestMoveAll1326", FavoriteCommandCategory.All);
            SelectTab(window, tabs, "Ground");

            Button allButton = Tab(tabs, "Ground")
                .GetLogicalDescendants()
                .OfType<Button>()
                .Single(b => b.Tag is FavoriteDisplayEntry e && e.Favorite.Id == all.Id);
            Point centre = allButton.TranslatePoint(new Point(allButton.Bounds.Width / 2, allButton.Bounds.Height / 2), window)!.Value;
            window.MouseDown(centre, MouseButton.Right);
            window.MouseUp(centre, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();

            Click(OpenEditorContent(view).GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == "Move Left"));
            Dispatcher.UIThread.RunJobs();

            List<string> order = [.. vm.DisplayFavorites.Select(f => f.Favorite.Id)];
            Assert.True(order.IndexOf(all.Id) < order.IndexOf(ground.Id));
            Assert.Equal(all.Id, TabFavoriteIds(tabs, "Ground").First(id => (id == all.Id) || (id == ground.Id)));
        }
        finally
        {
            Cleanup(window, vm, added);
        }
    }

    [AvaloniaFact]
    public void BlankAfter_OnAnAllFavorite_AfterTheSelectionChangesWhileTheFlyoutIsOpen_FilesUnderTheShowingTab()
    {
        (Window window, MainViewModel vm, FavoritesBarView view, TabControl tabs) = ShowPalette();
        List<string> added = [];
        try
        {
            FavoriteCommand all = AddFavorite(vm, added, "AllCatTestShowingTab1326", FavoriteCommandCategory.All);
            SelectTab(window, tabs, "Vehicle");
            OpenEditFlyout(window, FavoriteButton(tabs, "Vehicle", all.Id));
            Assert.True(view.IsEditorFlyoutOpen);

            var airborne = new AircraftModel { Callsign = "UAL1", IsOnGround = false };
            vm.Aircraft.Add(airborne);
            vm.SelectedAircraft = airborne;
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.IsEditorFlyoutOpen);

            FavoriteCommand blank = ClickInsertBlank(vm, view, added, "Blank After");
            Assert.Equal(FavoriteCommandCategory.Vehicle, blank.Category);
        }
        finally
        {
            Cleanup(window, vm, added);
        }
    }

    [AvaloniaFact]
    public void MoveRight_OnAnAllFavorite_AfterTheSelectionChangesWhileTheFlyoutIsOpen_ReordersTheShowingTab()
    {
        (Window window, MainViewModel vm, FavoritesBarView view, TabControl tabs) = ShowPalette();
        List<string> added = [];
        try
        {
            // Shared order All, Vehicle, Air: Vehicle shows [All, Vehicle] and Air shows [All, Air], so
            // moving All right in Vehicle leaves Air's order alone, and moving it in Air would not.
            FavoriteCommand all = AddFavorite(vm, added, "AllCatTestMoveRightAll1326", FavoriteCommandCategory.All);
            AddFavorite(vm, added, "AllCatTestMoveRightVehicle1326", FavoriteCommandCategory.Vehicle);
            AddFavorite(vm, added, "AllCatTestMoveRightAir1326", FavoriteCommandCategory.Air);
            List<string> vehicleBefore = [.. TabFavoriteIds(tabs, "Vehicle").Where(added.Contains)];
            List<string> airBefore = [.. TabFavoriteIds(tabs, "Air").Where(added.Contains)];

            SelectTab(window, tabs, "Vehicle");
            OpenEditFlyout(window, FavoriteButton(tabs, "Vehicle", all.Id));
            Assert.True(view.IsEditorFlyoutOpen);

            var airborne = new AircraftModel { Callsign = "UAL1", IsOnGround = false };
            vm.Aircraft.Add(airborne);
            vm.SelectedAircraft = airborne;
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.IsEditorFlyoutOpen);

            Click(OpenEditorContent(view).GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == "Move Right"));
            Dispatcher.UIThread.RunJobs();

            Assert.False(view.IsEditorFlyoutOpen);
            Assert.Equal([vehicleBefore[1], vehicleBefore[0]], TabFavoriteIds(tabs, "Vehicle").Where(added.Contains));
            Assert.Equal(airBefore, TabFavoriteIds(tabs, "Air").Where(added.Contains));
        }
        finally
        {
            Cleanup(window, vm, added);
        }
    }

    [AvaloniaFact]
    public void BlankAfter_OnAnAllFavoriteFromTheGroundTab_AddsAGroundSpacer()
    {
        (Window window, MainViewModel vm, FavoritesBarView view, TabControl tabs) = ShowPalette();
        List<string> added = [];
        try
        {
            FavoriteCommand all = AddFavorite(vm, added, "AllCatTestGroundSpacer1326", FavoriteCommandCategory.All);
            SelectTab(window, tabs, "Ground");
            OpenEditFlyout(window, FavoriteButton(tabs, "Ground", all.Id));

            FavoriteCommand blank = ClickInsertBlank(vm, view, added, "Blank After");

            Assert.Equal(FavoriteCommandCategory.Ground, blank.Category);
            Assert.Contains(blank.Id, TabFavoriteIds(tabs, "Ground"));
            foreach (string header in TabHeaders.Where(h => h != "Ground"))
            {
                Assert.DoesNotContain(blank.Id, TabFavoriteIds(tabs, header));
            }
        }
        finally
        {
            Cleanup(window, vm, added);
        }
    }

    [AvaloniaFact]
    public void BlankSlot_SetToAll_ShowsInEveryTab()
    {
        (Window window, MainViewModel vm, FavoritesBarView view, TabControl tabs) = ShowPalette();
        List<string> added = [];
        try
        {
            var spacer = new FavoriteCommand { IsSpacer = true, Category = FavoriteCommandCategory.Air };
            vm.AddFavorite(spacer, [vm.FavoriteStore.GlobalSet.Id]);
            added.Add(spacer.Id);
            Dispatcher.UIThread.RunJobs();
            SelectTab(window, tabs, "Air");
            OpenEditFlyout(window, FavoriteButton(tabs, "Air", spacer.Id));

            Control content = OpenEditorContent(view);
            content.GetLogicalDescendants().OfType<ComboBox>().Single().SelectedItem = FavoriteCommandCategory.All;
            Click(content.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == "Save"));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(FavoriteCommandCategory.All, vm.DisplayFavorites.Single(f => f.Favorite.Id == spacer.Id).Favorite.Category);
            foreach (string header in TabHeaders)
            {
                Assert.Contains(spacer.Id, TabFavoriteIds(tabs, header));
            }
        }
        finally
        {
            Cleanup(window, vm, added);
        }
    }
}
