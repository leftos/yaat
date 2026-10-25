using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
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

    private static Button MoveButton(MenuItem row, string glyph) => Arrows(row).Children.OfType<Button>().Single(b => (string?)b.Content == glyph);

    private static StackPanel Arrows(MenuItem row) => ((Grid)row.Header!).Children.OfType<StackPanel>().Single();

    /// <summary>The centre of <paramref name="target"/> in its top level (the flyout's popup), after a layout pass.</summary>
    private static (TopLevel Top, Point Centre) OnScreenCentre(Control target)
    {
        Dispatcher.UIThread.RunJobs();
        TopLevel top = TopLevel.GetTopLevel(target) ?? throw new InvalidOperationException("the control is in no top level");
        top.UpdateLayout();
        Point centre =
            target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), top)
            ?? throw new InvalidOperationException("the control has no position in its top level");
        return (top, centre);
    }

    /// <summary>Moves the real headless mouse to the centre of <paramref name="target"/>, then presses and releases the left button there.</summary>
    private static void PointerClick(Control target)
    {
        (TopLevel top, Point centre) = OnScreenCentre(target);
        top.MouseMove(centre);
        top.MouseDown(centre, MouseButton.Left);
        top.MouseUp(centre, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

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
    public void PointerClickOnAnEnabledMoveDown_MovesTheSetOnePlace_KeepsLoadedStates_AndTheFlyoutOpen()
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

            PointerClick(MoveButton(Row(flyout, "FSF-Loaded"), "▼"));

            Assert.Equal(["Global", "FSF-Unloaded", "FSF-Loaded"], OurLabels(flyout));
            Assert.True(flyout.IsOpen);
            Assert.Equal([loaded.Id], vm.Preferences.LoadedFavoriteSetIds);
            Assert.False(vm.IsFavoriteSetLoaded(unloaded.Id));
        }
        finally
        {
            flyout?.Hide();
            window?.Close();
            vm.Preferences.SetFavoriteSetOrder([]);
            Cleanup(vm);
        }
    }

    [AvaloniaFact]
    public void PointerClickOnADisabledMoveUp_ChangesNeitherTheOrderNorTheLoadedStates_AndKeepsTheFlyoutOpen()
    {
        MainViewModel vm = NewVm();
        (Window? window, MenuFlyout? flyout) = (null, null);
        try
        {
            FavoriteSet loaded = vm.FavoriteStore.CreateNamedSet("FSF-Loaded")!;
            FavoriteSet unloaded = vm.FavoriteStore.CreateNamedSet("FSF-Unloaded")!;
            vm.SetFavoriteSetLoaded(loaded.Id, true);
            List<string> order = [loaded.Id, FavoriteSetOrdering.GlobalSlot, FavoriteSetOrdering.AirportSlot, FavoriteSetOrdering.ScenarioSlot];
            vm.Preferences.SetFavoriteSetOrder([.. order]);
            (window, flyout) = ShowFlyout(vm);
            Assert.Equal(["FSF-Loaded", "Global", "FSF-Unloaded"], OurLabels(flyout));
            Button up = MoveButton(Row(flyout, "FSF-Loaded"), "▲");
            Assert.False(up.IsEnabled);

            PointerClick(up);

            Assert.Equal(["FSF-Loaded", "Global", "FSF-Unloaded"], OurLabels(flyout));
            Assert.Equal(order, vm.Preferences.FavoriteSetOrder);
            Assert.True(flyout.IsOpen);
            Assert.Equal([loaded.Id], vm.Preferences.LoadedFavoriteSetIds);
            Assert.False(vm.IsFavoriteSetLoaded(unloaded.Id));
        }
        finally
        {
            flyout?.Hide();
            window?.Close();
            vm.Preferences.SetFavoriteSetOrder([]);
            Cleanup(vm);
        }
    }

    [AvaloniaFact]
    public void KeyboardClickOnARow_TogglesItsLoading_WhileThePointerRestsOverItsArrows()
    {
        MainViewModel vm = NewVm();
        (Window? window, MenuFlyout? flyout) = (null, null);
        try
        {
            FavoriteSet set = vm.FavoriteStore.CreateNamedSet("FSF-Toggle")!;
            (window, flyout) = ShowFlyout(vm);
            MenuItem row = Row(flyout, "FSF-Toggle");
            (TopLevel top, Point overArrows) = OnScreenCentre(MoveButton(row, "▼"));
            top.MouseMove(overArrows);
            Dispatcher.UIThread.RunJobs();
            Assert.True(Arrows(row).IsPointerOver);

            // A keyboard Enter or Space on a menu item raises its Click with no pointer press.
            row.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

            Assert.True(vm.IsFavoriteSetLoaded(set.Id));
            Assert.True(flyout.IsOpen);
        }
        finally
        {
            flyout?.Hide();
            window?.Close();
            Cleanup(vm);
        }
    }

    [AvaloniaFact]
    public void KeyboardClickOnARow_TogglesItsLoading_AfterAPressOnItsArrowsWasReleasedElsewhere()
    {
        MainViewModel vm = NewVm();
        (Window? window, MenuFlyout? flyout) = (null, null);
        try
        {
            FavoriteSet set = vm.FavoriteStore.CreateNamedSet("FSF-Toggle")!;
            (window, flyout) = ShowFlyout(vm);
            MenuItem row = Row(flyout, "FSF-Toggle");
            (TopLevel top, Point onArrow) = OnScreenCentre(MoveButton(row, "▼"));
            var outside = new Point(-100, -100);
            top.MouseMove(onArrow);
            top.MouseDown(onArrow, MouseButton.Left);
            top.MouseMove(outside, RawInputModifiers.LeftMouseButton);
            top.MouseUp(outside, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(vm.Preferences.FavoriteSetOrder);
            Assert.False(vm.IsFavoriteSetLoaded(set.Id));

            // A keyboard click on a menu item: a KeyDown, then a Click with no pointer press. Tab stands in for
            // the KeyDown so the menu's own Enter handling cannot raise a second Click.
            row.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Tab });
            row.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

            Assert.True(vm.IsFavoriteSetLoaded(set.Id));
            Assert.True(flyout.IsOpen);
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
        // The store is shared by the assembly; drop any named set another test left behind.
        foreach (FavoriteSet leaked in vm.FavoriteStore.OrderedSets.Where(s => s.Kind == FavoriteSetKind.Named).ToList())
        {
            vm.Preferences.RemoveFavoriteSet(leaked.Id);
            vm.FavoriteStore.DeleteSet(leaked.Id);
        }

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
