using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
/// Coverage for YAAT-1325 (GH #1354): deleting the selected aircraft clears the selection, and the
/// favorites view rebuilt every button on that change, closing the Add or Edit flyout anchored to one
/// and discarding what the user had typed. The rebuild now waits until the editor flyout closes.
/// </summary>
public class FavoritesFlyoutRebuildTests
{
    private const string CommandBoxPlaceholder = "Command text (e.g. FH 270, CM 014)";

    private static (Window Window, MainViewModel Vm, FavoritesBarView View) Show(bool paletteMode)
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        var view = new FavoritesBarView { IsPaletteMode = paletteMode };
        var window = new Window
        {
            Width = 800,
            Height = 450,
            Content = view,
            DataContext = vm,
        };
        window.ShowAndRunLayout();
        return (window, vm, view);
    }

    private static AircraftModel SelectAircraft(MainViewModel vm, string callsign, bool isOnGround)
    {
        var aircraft = new AircraftModel { Callsign = callsign, IsOnGround = isOnGround };
        vm.Aircraft.Add(aircraft);
        vm.SelectedAircraft = aircraft;
        Dispatcher.UIThread.RunJobs();
        return aircraft;
    }

    private static string? EditorCommandText(FavoritesBarView view)
    {
        Flyout flyout = Assert.Single(view.OpenEditorFlyouts);
        Control content = Assert.IsAssignableFrom<Control>(flyout.Content);
        return content.GetLogicalDescendants().OfType<TextBox>().Single(b => b.PlaceholderText == CommandBoxPlaceholder).Text;
    }

    private static string? SelectedTab(FavoritesBarView view) =>
        (view.GetVisualDescendants().OfType<TabControl>().Single().SelectedItem as TabItem)?.Header as string;

    private static int FavoriteButtonCount(FavoritesBarView view) =>
        view.GetVisualDescendants().OfType<Button>().Count(b => b.Tag is FavoriteDisplayEntry);

    [AvaloniaFact]
    public void FavoritesAddFlyout_SurvivesTheSelectedAircraftBeingDeleted()
    {
        (Window window, MainViewModel vm, FavoritesBarView view) = Show(paletteMode: false);
        try
        {
            SelectAircraft(vm, "UAL1", isOnGround: false);
            view.OpenAddFlyoutForCommand("FH 270");
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.IsEditorFlyoutOpen);

            vm.OnAircraftDeleted("UAL1");
            Dispatcher.UIThread.RunJobs();

            Assert.Null(vm.SelectedAircraft);
            Assert.True(view.IsEditorFlyoutOpen);
            Assert.True(view.OpenEditorFlyouts[0].IsOpen);
            Assert.Equal("FH 270", EditorCommandText(view));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void FavoritesEditFlyout_InPalette_SurvivesTheSelectedAircraftBeingDeleted()
    {
        (Window window, MainViewModel vm, FavoritesBarView view) = Show(paletteMode: true);
        var fav = new FavoriteCommand
        {
            Label = "FlyoutRebuildTestFav1325",
            CommandText = "CM 140",
            Category = FavoriteCommandCategory.Air,
        };
        bool added = false;
        try
        {
            vm.AddFavorite(fav, [vm.FavoriteStore.GlobalSet.Id]);
            added = true;
            SelectAircraft(vm, "UAL1", isOnGround: false);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Button favButton = view.GetVisualDescendants()
                .OfType<Button>()
                .Single(b => b.Tag is FavoriteDisplayEntry entry && entry.Favorite.Id == fav.Id);
            Point centre = favButton.TranslatePoint(new Point(favButton.Bounds.Width / 2, favButton.Bounds.Height / 2), window)!.Value;
            window.MouseDown(centre, MouseButton.Right);
            window.MouseUp(centre, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.IsEditorFlyoutOpen);

            vm.OnAircraftDeleted("UAL1");
            Dispatcher.UIThread.RunJobs();

            Assert.Null(vm.SelectedAircraft);
            Assert.True(view.IsEditorFlyoutOpen);
            Assert.True(view.OpenEditorFlyouts[0].IsOpen);
            Assert.Equal("CM 140", EditorCommandText(view));
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
    public void DeferredRebuild_RunsOnceTheEditorFlyoutCloses()
    {
        (Window window, MainViewModel vm, FavoritesBarView view) = Show(paletteMode: false);
        var fav = new FavoriteCommand
        {
            Label = "FlyoutRebuildTestFav1325b",
            CommandText = "FH 090",
            Category = FavoriteCommandCategory.Air,
        };
        bool added = false;
        try
        {
            int before = FavoriteButtonCount(view);
            view.OpenAddFlyoutForCommand("FH 270");
            Dispatcher.UIThread.RunJobs();

            vm.AddFavorite(fav, [vm.FavoriteStore.GlobalSet.Id]);
            added = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.IsEditorFlyoutOpen);
            Assert.Equal(before, FavoriteButtonCount(view));

            view.OpenEditorFlyouts[0].Hide();
            Dispatcher.UIThread.RunJobs();

            Assert.False(view.IsEditorFlyoutOpen);
            Assert.Equal(before + 1, FavoriteButtonCount(view));
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
    public void SelectingAGroundAircraft_WhileAnEditorFlyoutIsOpen_KeepsTheFlyoutAndSwitchesTabOnClose()
    {
        (Window window, MainViewModel vm, FavoritesBarView view) = Show(paletteMode: true);
        try
        {
            string? tabBefore = SelectedTab(view);
            Assert.Equal("Air", tabBefore);
            view.OpenAddFlyoutForCommand("FH 270");
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.IsEditorFlyoutOpen);

            SelectAircraft(vm, "UAL2", isOnGround: true);

            Assert.True(view.IsEditorFlyoutOpen);
            Assert.True(view.OpenEditorFlyouts[0].IsOpen);
            Assert.Equal(tabBefore, SelectedTab(view));

            view.OpenEditorFlyouts[0].Hide();
            Dispatcher.UIThread.RunJobs();

            Assert.False(view.IsEditorFlyoutOpen);
            Assert.Equal("Ground", SelectedTab(view));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
