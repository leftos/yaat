using System.ComponentModel;
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
/// Coverage for the pop-out Favorites Panel bug reported in issue #287 ("buttons broken — nothing
/// seems to happen"). The click machinery in <see cref="FavoritesBarView"/> captures the pointer on
/// every left press to support drag-to-reorder; these tests prove a plain click still fires the
/// command dispatch (i.e. is not swallowed by the capture/drag path) and that the panel exposes the
/// target/status feedback the window itself renders.
/// </summary>
public class FavoritesPanelInteractionTests
{
    private static async Task<MainViewModel> NewVmAsync()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        await vm.NavDataInitialization.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        return vm;
    }

    private static AircraftDto MakeAircraft(string callsign) =>
        new(
            Callsign: callsign,
            AircraftType: "C172",
            Latitude: 37.62,
            Longitude: -122.22,
            Heading: 90,
            Altitude: 1500,
            GroundSpeed: 90,
            BeaconCode: 1200,
            TransponderMode: "ModeC",
            IsIdenting: false,
            VerticalSpeed: 0,
            AssignedHeading: null,
            AssignedAltitude: null,
            AssignedSpeed: null,
            Departure: "OAK",
            Destination: "SQL",
            Route: "",
            FlightRules: "VFR",
            Status: "Active"
        );

    [AvaloniaTheory]
    [InlineData("FavoritesImportButton", ImportExportTab.Import)]
    [InlineData("FavoritesExportButton", ImportExportTab.Export)]
    public async Task ImportExportButton_OpensTheHubWithFavoritesTicked(string buttonName, ImportExportTab tab)
    {
        var view = new FavoritesBarView { IsPaletteMode = true };
        var window = new Window
        {
            Width = 600,
            Height = 450,
            Content = view,
            DataContext = await NewVmAsync(),
        };
        window.ShowAndRunLayout();
        try
        {
            Button button = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == buttonName);
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            ImportExportWindow hub = Assert.Single(window.OwnedWindows.OfType<ImportExportWindow>());
            ImportExportViewModel hubVm = Assert.IsType<ImportExportViewModel>(hub.DataContext);
            Assert.Equal(tab, hubVm.SelectedTab);
            Assert.Equal([SettingsItemType.Favorites], hubVm.ExportItems.Where(r => r.IsSelected).Select(r => r.ItemType));
            hub.Close();
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task FavoriteButtonClick_InPaletteMode_ReachesCommandDispatch()
    {
        MainViewModel vm = await NewVmAsync();
        // A Global air favorite carrying a real command but no callsign — exactly the shape of a
        // real favorite, which targets the currently SelectedAircraft.
        var testFav = new FavoriteCommand
        {
            Label = "TestFav",
            CommandText = "FH 270",
            Category = FavoriteCommandCategory.Air,
        };
        vm.AddFavorite(testFav, [vm.FavoriteStore.GlobalSet.Id]);

        var view = new FavoritesBarView { IsPaletteMode = true };
        var window = new Window
        {
            Width = 600,
            Height = 450,
            Content = view,
            DataContext = vm,
        };
        window.ShowAndRunLayout();

        // Closed in a finally so a leaked pointer capture can't bleed into another test's input.
        // The real headless mouse device (window.MouseDown/Up) is required here: hand-raised
        // PointerPressed/Released routed events do NOT reproduce the capture-lost cascade that ate
        // the click, so only the device path exercises the #287 regression faithfully.
        try
        {
            // Precondition: nothing is selected, so a dispatch will fall through to the "no target"
            // branch — a deterministic, observable signal that the click reached ExecuteFavoriteAsync.
            // The awaited navdata load has already replaced StatusText, so the before-state is pinned here.
            Assert.Null(vm.SelectedAircraft);
            vm.StatusText = "Disconnected";

            Button button = view.GetVisualDescendants().OfType<Button>().Single(b => b.Tag is FavoriteDisplayEntry { Favorite.Label: "TestFav" });

            // Simulate a genuine pointer press + release (not RaiseEvent(Button.ClickEvent)) so the
            // FavoritesBarView tunnel handlers that capture the pointer for drag-reorder actually run.
            // Before the fix, the eager Pointer.Capture on press ate the click and StatusText stayed
            // "Disconnected".
            Point? center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window);
            Assert.NotNull(center);
            window.MouseDown(center.Value, MouseButton.Left);
            window.MouseUp(center.Value, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            // The command was dispatched and, with no aircraft selected, reported it had no target.
            Assert.Contains("No aircraft matched", vm.StatusText);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.DeleteFavorite(testFav.Id);
        }
    }

    [AvaloniaFact]
    public async Task FavoritePanelTargetText_TracksSelectedAircraft()
    {
        MainViewModel vm = await NewVmAsync();
        Assert.Equal("No aircraft selected", vm.FavoritePanelTargetText);

        vm.OnAircraftUpdated(MakeAircraft("UAL123"));
        Dispatcher.UIThread.RunJobs();

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.SelectedAircraft = vm.Aircraft.Single(a => a.Callsign == "UAL123");

        // The computed label updates and its change is notified (via [NotifyPropertyChangedFor]) so
        // the panel status bar binding refreshes.
        Assert.Equal("Target: UAL123", vm.FavoritePanelTargetText);
        Assert.Contains(nameof(MainViewModel.FavoritePanelTargetText), raised);

        vm.SelectedAircraft = null;
        Assert.Equal("No aircraft selected", vm.FavoritePanelTargetText);
    }

    [AvaloniaFact]
    public async Task FavoritesPanelWindow_IsUnowned_AndRendersTargetFeedback()
    {
        MainViewModel vm = await NewVmAsync();
        var window = new FavoritesPanelWindow(vm.Preferences) { DataContext = vm };
        try
        {
            window.ShowAndRunLayout();

            // Fix 1 (#287): the panel is not an owned window — owned windows are locked above the
            // main window in Z-order and made the panel feel like it blocked clicks elsewhere.
            Assert.Null(window.Owner);

            // Fix 2 (#287): the panel renders its own target/status feedback so a click's result is
            // visible without switching back to the main window.
            TextBlock? targetBlock = window.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text == "No aircraft selected");
            Assert.NotNull(targetBlock);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static (Window Window, FavoritesBarView View) ShowPalette(MainViewModel vm)
    {
        var view = new FavoritesBarView { IsPaletteMode = true };
        var window = new Window
        {
            Width = 1000,
            Height = 700,
            Content = view,
            DataContext = vm,
        };
        window.ShowAndRunLayout();
        return (window, view);
    }

    private static FavoriteCommand AddTo(MainViewModel vm, string setId, FavoriteCommand favorite)
    {
        vm.AddFavorite(favorite, [setId]);
        Dispatcher.UIThread.RunJobs();
        return favorite;
    }

    /// <summary>Right-clicks the bar button showing <paramref name="favoriteId"/> from <paramref name="setId"/>, opening its Edit flyout.</summary>
    private static void OpenEditFlyout(Window window, FavoritesBarView view, string favoriteId, string setId)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Button button = view.GetVisualDescendants()
            .OfType<Button>()
            .Single(b => (b.Tag is FavoriteDisplayEntry e) && (e.Favorite.Id == favoriteId) && (e.SetId == setId));
        Point? centre = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window);
        Assert.NotNull(centre);
        window.MouseDown(centre.Value, MouseButton.Right);
        window.MouseUp(centre.Value, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
    }

    private static Button EditFlyoutButton(FavoritesBarView view, string content) =>
        Assert
            .IsAssignableFrom<Control>(Assert.Single(view.OpenEditorFlyouts).Content)
            .GetLogicalDescendants()
            .OfType<Button>()
            .Single(b => (b.Content as string) == content);

    private static void ClickEditFlyoutButton(FavoritesBarView view, string content)
    {
        EditFlyoutButton(view, content).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static void DeleteAll(MainViewModel vm, IEnumerable<string> favoriteIds)
    {
        foreach (string id in favoriteIds)
        {
            vm.DeleteFavorite(id);
        }
    }

    [AvaloniaFact]
    public void EditFlyout_CopyThenPasteOnAnotherButton_ReplacesIt()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        string globalId = vm.FavoriteStore.GlobalSet.Id;
        FavoriteSet other = vm.FavoriteStore.CreateNamedSet("FPI-CopyPaste")!;
        vm.SetFavoriteSetLoaded(other.Id, true);
        HashSet<string> before = [.. vm.FavoriteStore.AllFavorites.Select(f => f.Id)];
        FavoriteCommand source = AddTo(
            vm,
            globalId,
            new FavoriteCommand
            {
                Label = "FPI-Source",
                CommandText = "FH 270",
                GroundCommandText = "TAXI A",
                BackgroundColor = "#FF336699",
                TextColor = "#FFFFFF00",
                ButtonHeight = 40,
            }
        );
        FavoriteCommand globalTarget = AddTo(
            vm,
            globalId,
            new FavoriteCommand
            {
                Label = "FPI-GlobalTarget",
                CommandText = "FH 090",
                ButtonHeight = 56,
            }
        );
        FavoriteCommand otherTarget = AddTo(
            vm,
            other.Id,
            new FavoriteCommand
            {
                Label = "FPI-OtherTarget",
                CommandText = "FH 180",
                ButtonHeight = 30,
            }
        );
        List<string> globalBefore = [.. vm.FavoriteStore.GetSet(globalId)!.FavoriteIds];
        List<string> otherBefore = [.. vm.FavoriteStore.GetSet(other.Id)!.FavoriteIds];
        (Window window, FavoritesBarView view) = ShowPalette(vm);
        try
        {
            OpenEditFlyout(window, view, source.Id, globalId);
            ClickEditFlyoutButton(view, "Copy");
            Assert.Empty(view.OpenEditorFlyouts);

            // Copy holds a snapshot: an edit to the source after Copy does not reach the pasted copies.
            FavoriteCommand edited = vm.FavoriteStore.GetFavorite(source.Id)!.Clone();
            edited.Label = "FPI-SourceEdited";
            vm.UpdateFavorite(edited, [globalId]);
            Dispatcher.UIThread.RunJobs();

            OpenEditFlyout(window, view, globalTarget.Id, globalId);
            ClickEditFlyoutButton(view, "Paste");
            OpenEditFlyout(window, view, otherTarget.Id, other.Id);
            ClickEditFlyoutButton(view, "Paste");

            List<string> globalAfter = vm.FavoriteStore.GetSet(globalId)!.FavoriteIds;
            List<string> otherAfter = vm.FavoriteStore.GetSet(other.Id)!.FavoriteIds;
            Assert.Equal(globalBefore.Count, globalAfter.Count);
            Assert.Equal(otherBefore.Count, otherAfter.Count);
            Assert.DoesNotContain(globalTarget.Id, globalAfter);
            Assert.DoesNotContain(otherTarget.Id, otherAfter);

            FavoriteCommand inGlobal = vm.FavoriteStore.GetFavorite(globalAfter[globalBefore.IndexOf(globalTarget.Id)])!;
            FavoriteCommand inOther = vm.FavoriteStore.GetFavorite(otherAfter[otherBefore.IndexOf(otherTarget.Id)])!;
            foreach (FavoriteCommand copy in new[] { inGlobal, inOther })
            {
                Assert.NotEqual(source.Id, copy.Id);
                Assert.False(copy.IsSpacer);
                Assert.Equal("FPI-Source", copy.Label);
                Assert.Equal(FavoriteCommandCategory.Air, copy.Category);
                Assert.Equal(source.CommandText, copy.CommandText);
                Assert.Equal(source.GroundCommandText, copy.GroundCommandText);
                Assert.Equal(source.BackgroundColor, copy.BackgroundColor);
                Assert.Equal(source.TextColor, copy.TextColor);
                Assert.Equal([copy.Id == inGlobal.Id ? globalId : other.Id], vm.GetFavoriteMembership(copy.Id));
            }

            Assert.Equal(globalTarget.ButtonHeight, inGlobal.ButtonHeight);
            Assert.Equal(otherTarget.ButtonHeight, inOther.ButtonHeight);
            Assert.NotEqual(inGlobal.Id, inOther.Id);
            Assert.Equal([globalId], vm.GetFavoriteMembership(source.Id));

            // The replaced buttons leave their set but stay in the store, the way removing one from its set does.
            Assert.NotNull(vm.FavoriteStore.GetFavorite(globalTarget.Id));
            Assert.NotNull(vm.FavoriteStore.GetFavorite(otherTarget.Id));
            Assert.Empty(vm.GetFavoriteMembership(globalTarget.Id));
            Assert.Empty(vm.GetFavoriteMembership(otherTarget.Id));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
            DeleteAll(vm, [.. vm.FavoriteStore.AllFavorites.Select(f => f.Id).Where(id => !before.Contains(id))]);
            vm.Preferences.RemoveFavoriteSet(other.Id);
            vm.FavoriteStore.DeleteSet(other.Id);
        }
    }

    [AvaloniaFact]
    public void EditFlyout_PasteOnAGroundButton_TakesTheGroundTab_AndOnAnAllButtonKeepsItsOwn()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        string globalId = vm.FavoriteStore.GlobalSet.Id;
        HashSet<string> before = [.. vm.FavoriteStore.AllFavorites.Select(f => f.Id)];
        FavoriteCommand source = AddTo(
            vm,
            globalId,
            new FavoriteCommand
            {
                Label = "FPI-AirSource",
                CommandText = "FH 270",
                ButtonHeight = 40,
            }
        );
        FavoriteCommand ground = AddTo(
            vm,
            globalId,
            new FavoriteCommand
            {
                Label = "FPI-GroundTarget",
                CommandText = "TAXI B",
                Category = FavoriteCommandCategory.Ground,
                ButtonHeight = 56,
            }
        );
        FavoriteCommand all = AddTo(
            vm,
            globalId,
            new FavoriteCommand
            {
                Label = "FPI-AllTarget",
                CommandText = "SQALL",
                Category = FavoriteCommandCategory.All,
                ButtonHeight = 30,
            }
        );
        (Window window, FavoritesBarView view) = ShowPalette(vm);
        try
        {
            OpenEditFlyout(window, view, source.Id, globalId);
            ClickEditFlyoutButton(view, "Copy");

            TabControl tabs = view.GetVisualDescendants().OfType<TabControl>().Single();
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => (t.Header as string) == "Ground");
            Dispatcher.UIThread.RunJobs();

            List<string> groundTabBefore =
            [
                .. view.GetVisualDescendants().OfType<Button>().Select(b => b.Tag).OfType<FavoriteDisplayEntry>().Select(e => e.Favorite.Id),
            ];
            int groundSlot = groundTabBefore.IndexOf(ground.Id);
            Assert.InRange(groundSlot, 0, groundTabBefore.Count - 1);
            List<string> idsBefore = [.. vm.FavoriteStore.GetSet(globalId)!.FavoriteIds];

            OpenEditFlyout(window, view, ground.Id, globalId);
            ClickEditFlyoutButton(view, "Paste");
            List<string> idsAfter = vm.FavoriteStore.GetSet(globalId)!.FavoriteIds;
            FavoriteCommand onGround = vm.FavoriteStore.GetFavorite(idsAfter[idsBefore.IndexOf(ground.Id)])!;
            Assert.Equal("FPI-AirSource", onGround.Label);
            Assert.Equal(FavoriteCommandCategory.Ground, onGround.Category);
            Assert.Equal(ground.ButtonHeight, onGround.ButtonHeight);
            Assert.DoesNotContain(ground.Id, idsAfter);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            List<string> groundTab =
            [
                .. view.GetVisualDescendants().OfType<Button>().Select(b => b.Tag).OfType<FavoriteDisplayEntry>().Select(e => e.Favorite.Id),
            ];
            Assert.Equal(groundSlot, groundTab.IndexOf(onGround.Id));

            List<string> allIdsBefore = [.. vm.FavoriteStore.GetSet(globalId)!.FavoriteIds];
            OpenEditFlyout(window, view, all.Id, globalId);
            ClickEditFlyoutButton(view, "Paste");
            List<string> allIdsAfter = vm.FavoriteStore.GetSet(globalId)!.FavoriteIds;
            FavoriteCommand onAll = vm.FavoriteStore.GetFavorite(allIdsAfter[allIdsBefore.IndexOf(all.Id)])!;
            Assert.Equal(FavoriteCommandCategory.Air, onAll.Category);
            Assert.Equal(all.ButtonHeight, onAll.ButtonHeight);
            Assert.DoesNotContain(all.Id, allIdsAfter);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
            DeleteAll(vm, [.. vm.FavoriteStore.AllFavorites.Select(f => f.Id).Where(id => !before.Contains(id))]);
        }
    }

    [AvaloniaFact]
    public void EditFlyout_Paste_DisabledWhileBufferEmpty()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        string globalId = vm.FavoriteStore.GlobalSet.Id;
        FavoriteCommand favorite = AddTo(vm, globalId, new FavoriteCommand { Label = "FPI-PasteGate", CommandText = "FH 360" });
        (Window window, FavoritesBarView view) = ShowPalette(vm);
        try
        {
            OpenEditFlyout(window, view, favorite.Id, globalId);
            Assert.False(EditFlyoutButton(view, "Paste").IsEnabled);
            Assert.True(EditFlyoutButton(view, "Copy").IsEnabled);

            ClickEditFlyoutButton(view, "Copy");
            OpenEditFlyout(window, view, favorite.Id, globalId);
            Assert.True(EditFlyoutButton(view, "Paste").IsEnabled);
        }
        finally
        {
            foreach (Flyout flyout in view.OpenEditorFlyouts.ToList())
            {
                flyout.Hide();
            }

            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.DeleteFavorite(favorite.Id);
        }
    }

    [AvaloniaFact]
    public void CopyPaste_Spacer_StaysASpacer()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        string globalId = vm.FavoriteStore.GlobalSet.Id;
        HashSet<string> before = [.. vm.FavoriteStore.AllFavorites.Select(f => f.Id)];
        FavoriteCommand spacer = AddTo(vm, globalId, new FavoriteCommand { IsSpacer = true, ButtonHeight = 48 });
        FavoriteCommand target = AddTo(vm, globalId, new FavoriteCommand { Label = "FPI-SpacerTarget", CommandText = "FH 045" });
        List<string> beforeIds = [.. vm.FavoriteStore.GetSet(globalId)!.FavoriteIds];
        (Window window, FavoritesBarView view) = ShowPalette(vm);
        try
        {
            OpenEditFlyout(window, view, spacer.Id, globalId);
            ClickEditFlyoutButton(view, "Copy");
            OpenEditFlyout(window, view, target.Id, globalId);
            ClickEditFlyoutButton(view, "Paste");

            List<string> afterIds = vm.FavoriteStore.GetSet(globalId)!.FavoriteIds;
            Assert.Equal(beforeIds.Count, afterIds.Count);
            Assert.DoesNotContain(target.Id, afterIds);
            FavoriteCommand pasted = vm.FavoriteStore.GetFavorite(afterIds[beforeIds.IndexOf(target.Id)])!;
            Assert.NotEqual(spacer.Id, pasted.Id);
            Assert.True(pasted.IsSpacer);
            Assert.Equal("", pasted.Label);
            Assert.Equal(target.ButtonHeight, pasted.ButtonHeight);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
            DeleteAll(vm, [.. vm.FavoriteStore.AllFavorites.Select(f => f.Id).Where(id => !before.Contains(id))]);
        }
    }

    [AvaloniaFact]
    public void EditFlyout_PasteOnABlank_FillsTheBlankInThisSetOnly()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        string globalId = vm.FavoriteStore.GlobalSet.Id;
        FavoriteSet other = vm.FavoriteStore.CreateNamedSet("FPI-BlankFill")!;
        vm.SetFavoriteSetLoaded(other.Id, true);
        HashSet<string> before = [.. vm.FavoriteStore.AllFavorites.Select(f => f.Id)];
        FavoriteCommand source = AddTo(
            vm,
            globalId,
            new FavoriteCommand
            {
                Label = "FPI-FillSource",
                CommandText = "FH 270",
                ButtonHeight = 40,
            }
        );
        var blank = new FavoriteCommand
        {
            IsSpacer = true,
            Category = FavoriteCommandCategory.Ground,
            ButtonHeight = 48,
        };
        vm.AddFavorite(blank, [globalId, other.Id]);
        Dispatcher.UIThread.RunJobs();
        List<string> globalBefore = [.. vm.FavoriteStore.GetSet(globalId)!.FavoriteIds];
        List<string> otherBefore = [.. vm.FavoriteStore.GetSet(other.Id)!.FavoriteIds];
        (Window window, FavoritesBarView view) = ShowPalette(vm);
        try
        {
            OpenEditFlyout(window, view, source.Id, globalId);
            ClickEditFlyoutButton(view, "Copy");

            TabControl tabs = view.GetVisualDescendants().OfType<TabControl>().Single();
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => (t.Header as string) == "Ground");
            Dispatcher.UIThread.RunJobs();

            OpenEditFlyout(window, view, blank.Id, globalId);
            ClickEditFlyoutButton(view, "Paste");

            List<string> globalAfter = vm.FavoriteStore.GetSet(globalId)!.FavoriteIds;
            List<string> otherAfter = vm.FavoriteStore.GetSet(other.Id)!.FavoriteIds;
            Assert.Equal(globalBefore.Count, globalAfter.Count);
            Assert.Equal(otherBefore.Count, otherAfter.Count);
            Assert.DoesNotContain(blank.Id, globalAfter);

            FavoriteCommand filled = vm.FavoriteStore.GetFavorite(globalAfter[globalBefore.IndexOf(blank.Id)])!;
            Assert.NotEqual(blank.Id, filled.Id);
            Assert.False(filled.IsSpacer);
            Assert.Equal("FPI-FillSource", filled.Label);
            Assert.Equal(source.CommandText, filled.CommandText);
            Assert.Equal(FavoriteCommandCategory.Ground, filled.Category);
            Assert.Equal(48, filled.ButtonHeight);
            Assert.Equal([globalId], vm.GetFavoriteMembership(filled.Id));

            Assert.Equal(otherBefore.IndexOf(blank.Id), otherAfter.IndexOf(blank.Id));
            Assert.True(vm.FavoriteStore.GetFavorite(blank.Id)!.IsSpacer);
            Assert.Equal([other.Id], vm.GetFavoriteMembership(blank.Id));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
            DeleteAll(vm, [.. vm.FavoriteStore.AllFavorites.Select(f => f.Id).Where(id => !before.Contains(id))]);
            vm.Preferences.RemoveFavoriteSet(other.Id);
            vm.FavoriteStore.DeleteSet(other.Id);
        }
    }

    private static FavoriteSet LoadedSet(MainViewModel vm, string name)
    {
        FavoriteSet set = vm.FavoriteStore.CreateNamedSet(name)!;
        vm.SetFavoriteSetLoaded(set.Id, true);
        return set;
    }

    private static FavoriteCommand RealButton(string label) =>
        new()
        {
            Label = label,
            CommandText = "FH 090",
            ButtonHeight = 40,
        };

    private static FavoriteCommand BlankButton(double height) => new() { IsSpacer = true, ButtonHeight = height };

    private static void CloseAndCleanup(Window window, MainViewModel vm, HashSet<string> before, params FavoriteSet[] sets)
    {
        window.Close();
        Dispatcher.UIThread.RunJobs();
        DeleteAll(vm, [.. vm.FavoriteStore.AllFavorites.Select(f => f.Id).Where(id => !before.Contains(id))]);
        foreach (FavoriteSet set in sets)
        {
            vm.Preferences.RemoveFavoriteSet(set.Id);
            vm.FavoriteStore.DeleteSet(set.Id);
        }
    }

    [AvaloniaFact]
    public void EditFlyout_CutThenPasteOnABlankInTheSameSet_MovesTheSameButton_AndLeavesABlankOfItsSize()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        HashSet<string> before = [.. vm.FavoriteStore.AllFavorites.Select(f => f.Id)];
        FavoriteSet a = LoadedSet(vm, "FPI-CutA");
        FavoriteCommand source = AddTo(vm, a.Id, RealButton("FPI-CutSource"));
        FavoriteCommand target = AddTo(vm, a.Id, BlankButton(56));
        (Window window, FavoritesBarView view) = ShowPalette(vm);
        try
        {
            OpenEditFlyout(window, view, source.Id, a.Id);
            ClickEditFlyoutButton(view, "Cut");
            Assert.True(vm.HasFavoriteToPaste);
            Assert.Null(vm.CopiedFavorite);

            OpenEditFlyout(window, view, target.Id, a.Id);
            Assert.True(EditFlyoutButton(view, "Paste").IsEnabled);
            ClickEditFlyoutButton(view, "Paste");

            List<string> ids = vm.FavoriteStore.GetSet(a.Id)!.FavoriteIds;
            Assert.Equal(2, ids.Count);
            Assert.Equal(source.Id, ids[1]);
            FavoriteCommand left = vm.FavoriteStore.GetFavorite(ids[0])!;
            Assert.NotEqual(source.Id, left.Id);
            Assert.NotEqual(target.Id, left.Id);
            Assert.True(left.IsSpacer);
            Assert.Equal(40, left.ButtonHeight);
            Assert.Equal(FavoriteCommandCategory.Air, left.Category);

            FavoriteCommand moved = vm.FavoriteStore.GetFavorite(source.Id)!;
            Assert.Equal("FPI-CutSource", moved.Label);
            Assert.Equal(56, moved.ButtonHeight);
            Assert.Null(vm.FavoriteStore.GetFavorite(target.Id));
            Assert.Null(vm.CutFavoriteSource);
            Assert.False(vm.HasFavoriteToPaste);
        }
        finally
        {
            CloseAndCleanup(window, vm, before, a);
        }
    }

    [AvaloniaFact]
    public void EditFlyout_CutASharedButtonThenPasteInAThirdSet_BlanksOnlyTheCutSlot_AndTheOtherSetKeepsIt()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        HashSet<string> before = [.. vm.FavoriteStore.AllFavorites.Select(f => f.Id)];
        FavoriteSet a = LoadedSet(vm, "FPI-CutShareA");
        FavoriteSet b = LoadedSet(vm, "FPI-CutShareB");
        FavoriteSet c = LoadedSet(vm, "FPI-CutShareC");
        FavoriteCommand shared = RealButton("FPI-CutShared");
        vm.AddFavorite(shared, [a.Id, b.Id]);
        FavoriteCommand target = AddTo(vm, c.Id, BlankButton(40));
        List<string> bBefore = [.. vm.FavoriteStore.GetSet(b.Id)!.FavoriteIds];
        (Window window, FavoritesBarView view) = ShowPalette(vm);
        try
        {
            OpenEditFlyout(window, view, shared.Id, a.Id);
            ClickEditFlyoutButton(view, "Cut");
            OpenEditFlyout(window, view, target.Id, c.Id);
            ClickEditFlyoutButton(view, "Paste");

            string leftId = Assert.Single(vm.FavoriteStore.GetSet(a.Id)!.FavoriteIds);
            Assert.NotEqual(shared.Id, leftId);
            Assert.True(vm.FavoriteStore.GetFavorite(leftId)!.IsSpacer);
            Assert.Equal([shared.Id], vm.FavoriteStore.GetSet(c.Id)!.FavoriteIds);
            Assert.Equal(bBefore, vm.FavoriteStore.GetSet(b.Id)!.FavoriteIds);
            Assert.Equal(new HashSet<string> { b.Id, c.Id }, [.. vm.GetFavoriteMembership(shared.Id)]);
        }
        finally
        {
            CloseAndCleanup(window, vm, before, a, b, c);
        }
    }

    [AvaloniaFact]
    public void EditFlyout_CutThenPasteIntoASetThatAlreadyHoldsTheButton_ChangesNothing_AndTheCutStaysPending()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        HashSet<string> before = [.. vm.FavoriteStore.AllFavorites.Select(f => f.Id)];
        FavoriteSet a = LoadedSet(vm, "FPI-CutHeldA");
        FavoriteSet b = LoadedSet(vm, "FPI-CutHeldB");
        FavoriteCommand shared = RealButton("FPI-CutHeld");
        vm.AddFavorite(shared, [a.Id, b.Id]);
        FavoriteCommand target = AddTo(vm, b.Id, BlankButton(40));
        List<string> aBefore = [.. vm.FavoriteStore.GetSet(a.Id)!.FavoriteIds];
        List<string> bBefore = [.. vm.FavoriteStore.GetSet(b.Id)!.FavoriteIds];
        (Window window, FavoritesBarView view) = ShowPalette(vm);
        try
        {
            OpenEditFlyout(window, view, shared.Id, a.Id);
            ClickEditFlyoutButton(view, "Cut");
            OpenEditFlyout(window, view, target.Id, b.Id);
            ClickEditFlyoutButton(view, "Paste");

            Assert.Equal(aBefore, vm.FavoriteStore.GetSet(a.Id)!.FavoriteIds);
            Assert.Equal(bBefore, vm.FavoriteStore.GetSet(b.Id)!.FavoriteIds);
            Assert.NotNull(vm.FavoriteStore.GetFavorite(target.Id));
            Assert.NotNull(vm.CutFavoriteSource);
            Assert.True(vm.HasFavoriteToPaste);
        }
        finally
        {
            CloseAndCleanup(window, vm, before, a, b);
        }
    }

    [AvaloniaFact]
    public void EditFlyout_CutThenCopyAnother_PastesTheCopy_AndLeavesTheCutSourceAlone()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        HashSet<string> before = [.. vm.FavoriteStore.AllFavorites.Select(f => f.Id)];
        FavoriteSet a = LoadedSet(vm, "FPI-CutCopy");
        FavoriteCommand cut = AddTo(vm, a.Id, RealButton("FPI-CutFirst"));
        FavoriteCommand copied = AddTo(vm, a.Id, RealButton("FPI-CopySecond"));
        FavoriteCommand target = AddTo(vm, a.Id, BlankButton(40));
        (Window window, FavoritesBarView view) = ShowPalette(vm);
        try
        {
            OpenEditFlyout(window, view, cut.Id, a.Id);
            ClickEditFlyoutButton(view, "Cut");
            OpenEditFlyout(window, view, copied.Id, a.Id);
            ClickEditFlyoutButton(view, "Copy");
            Assert.Null(vm.CutFavoriteSource);
            OpenEditFlyout(window, view, target.Id, a.Id);
            ClickEditFlyoutButton(view, "Paste");

            List<string> ids = vm.FavoriteStore.GetSet(a.Id)!.FavoriteIds;
            Assert.Equal(3, ids.Count);
            FavoriteCommand pasted = vm.FavoriteStore.GetFavorite(ids[2])!;
            Assert.Equal([cut.Id, copied.Id, pasted.Id], ids);
            Assert.DoesNotContain(pasted.Id, new[] { cut.Id, copied.Id, target.Id });
            Assert.Equal("FPI-CopySecond", pasted.Label);
            Assert.Equal("FPI-CutFirst", vm.FavoriteStore.GetFavorite(cut.Id)!.Label);
        }
        finally
        {
            CloseAndCleanup(window, vm, before, a);
        }
    }

    [AvaloniaFact]
    public void EditFlyout_DeleteSettingsOnASharedButton_BlanksThisSetsSlotOnly()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        HashSet<string> before = [.. vm.FavoriteStore.AllFavorites.Select(f => f.Id)];
        FavoriteSet a = LoadedSet(vm, "FPI-ClearA");
        FavoriteSet b = LoadedSet(vm, "FPI-ClearB");
        FavoriteCommand shared = RealButton("FPI-ClearShared");
        vm.AddFavorite(shared, [a.Id, b.Id]);
        List<string> bBefore = [.. vm.FavoriteStore.GetSet(b.Id)!.FavoriteIds];
        (Window window, FavoritesBarView view) = ShowPalette(vm);
        try
        {
            OpenEditFlyout(window, view, shared.Id, a.Id);
            ClickEditFlyoutButton(view, "Delete settings");

            string blankId = Assert.Single(vm.FavoriteStore.GetSet(a.Id)!.FavoriteIds);
            Assert.NotEqual(shared.Id, blankId);
            FavoriteCommand blank = vm.FavoriteStore.GetFavorite(blankId)!;
            Assert.True(blank.IsSpacer);
            Assert.Equal(40, blank.ButtonHeight);
            Assert.Equal(FavoriteCommandCategory.Air, blank.Category);

            Assert.Equal(bBefore, vm.FavoriteStore.GetSet(b.Id)!.FavoriteIds);
            FavoriteCommand kept = vm.FavoriteStore.GetFavorite(shared.Id)!;
            Assert.Equal("FPI-ClearShared", kept.Label);
            Assert.Equal("FH 090", kept.CommandText);
            Assert.Equal([b.Id], vm.GetFavoriteMembership(shared.Id));
        }
        finally
        {
            CloseAndCleanup(window, vm, before, a, b);
        }
    }

    [AvaloniaFact]
    public void EditFlyout_DeleteSettingsOnAButtonInOneSet_ListsItUnderNotInAnySet()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        HashSet<string> before = [.. vm.FavoriteStore.AllFavorites.Select(f => f.Id)];
        FavoriteSet a = LoadedSet(vm, "FPI-ClearOnly");
        FavoriteCommand only = AddTo(vm, a.Id, RealButton("FPI-ClearOnly"));
        (Window window, FavoritesBarView view) = ShowPalette(vm);
        try
        {
            OpenEditFlyout(window, view, only.Id, a.Id);
            ClickEditFlyoutButton(view, "Delete settings");

            string blankId = Assert.Single(vm.FavoriteStore.GetSet(a.Id)!.FavoriteIds);
            Assert.NotEqual(only.Id, blankId);
            Assert.True(vm.FavoriteStore.GetFavorite(blankId)!.IsSpacer);
            Assert.Contains(vm.FavoriteStore.GetOrphanFavorites(), f => f.Id == only.Id);
        }
        finally
        {
            CloseAndCleanup(window, vm, before, a);
        }
    }

    [AvaloniaFact]
    public void EditFlyout_AtTheLargestInterfaceFont_TheDeleteSaveRowFits_AndDeleteSettingsHasItsOwnRowAboveIt()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        HashSet<string> before = [.. vm.FavoriteStore.AllFavorites.Select(f => f.Id)];
        FavoriteSet a = LoadedSet(vm, "FPI-FooterFit");
        FavoriteCommand favorite = AddTo(vm, a.Id, RealButton("FPI-FooterFit"));
        App.ApplyInterfaceFontSize(24);
        (Window window, FavoritesBarView view) = ShowPalette(vm);
        try
        {
            OpenEditFlyout(window, view, favorite.Id, a.Id);
            Button save = EditFlyoutButton(view, "Save");
            Button deleteSettings = EditFlyoutButton(view, "Delete settings");
            Assert.Equal(24, save.FontSize);
            var footer = (StackPanel)save.GetLogicalParent()!.GetLogicalParent()!;
            var saveRow = (Control)save.GetLogicalParent()!;
            Assert.Same(saveRow, footer.Children[^1]);
            Assert.Same(deleteSettings, footer.Children[^2]);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(footer.Bounds.Width, deleteSettings.Bounds.Width);

            // Each control's natural width at this font, unconstrained, so a clipped control cannot hide its overflow.
            double rowWidth = saveRow.GetLogicalChildren().OfType<Control>().Sum(NaturalWidth);
            Assert.True(rowWidth <= 220, $"the Delete / Save row needs {rowWidth} px of the 220 px footer");
            Assert.True(NaturalWidth(deleteSettings) <= 220, $"Delete settings needs {NaturalWidth(deleteSettings)} px");
        }
        finally
        {
            App.ApplyInterfaceFontSize(12);
            foreach (Flyout flyout in view.OpenEditorFlyouts.ToList())
            {
                flyout.Hide();
            }

            CloseAndCleanup(window, vm, before, a);
        }
    }

    private static double NaturalWidth(Control control)
    {
        control.Measure(Size.Infinity);
        return control.DesiredSize.Width;
    }

    [AvaloniaFact]
    public void EditSlotFlyout_OnABlank_HasNoCutAndNoDeleteSettings()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        HashSet<string> before = [.. vm.FavoriteStore.AllFavorites.Select(f => f.Id)];
        FavoriteSet a = LoadedSet(vm, "FPI-BlankMenu");
        FavoriteCommand blank = AddTo(vm, a.Id, BlankButton(40));
        (Window window, FavoritesBarView view) = ShowPalette(vm);
        try
        {
            OpenEditFlyout(window, view, blank.Id, a.Id);
            List<string> buttons =
            [
                .. Assert
                    .IsAssignableFrom<Control>(Assert.Single(view.OpenEditorFlyouts).Content)
                    .GetLogicalDescendants()
                    .OfType<Button>()
                    .Select(b => b.Content as string ?? ""),
            ];
            Assert.Contains("Copy", buttons);
            Assert.DoesNotContain("Cut", buttons);
            Assert.DoesNotContain("Delete settings", buttons);
        }
        finally
        {
            foreach (Flyout flyout in view.OpenEditorFlyouts.ToList())
            {
                flyout.Hide();
            }

            CloseAndCleanup(window, vm, before, a);
        }
    }

    [AvaloniaFact]
    public void EditFlyout_CutThenPasteOnItsOwnSlot_ChangesNothing_AndTheCutStaysPending()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        HashSet<string> before = [.. vm.FavoriteStore.AllFavorites.Select(f => f.Id)];
        FavoriteSet a = LoadedSet(vm, "FPI-CutSelf");
        FavoriteCommand source = AddTo(vm, a.Id, RealButton("FPI-CutSelf"));
        AddTo(vm, a.Id, BlankButton(40));
        List<string> idsBefore = [.. vm.FavoriteStore.GetSet(a.Id)!.FavoriteIds];
        (Window window, FavoritesBarView view) = ShowPalette(vm);
        try
        {
            OpenEditFlyout(window, view, source.Id, a.Id);
            ClickEditFlyoutButton(view, "Cut");
            OpenEditFlyout(window, view, source.Id, a.Id);
            ClickEditFlyoutButton(view, "Paste");

            Assert.Equal(idsBefore, vm.FavoriteStore.GetSet(a.Id)!.FavoriteIds);
            Assert.Equal(40, vm.FavoriteStore.GetFavorite(source.Id)!.ButtonHeight);
            Assert.NotNull(vm.CutFavoriteSource);
        }
        finally
        {
            CloseAndCleanup(window, vm, before, a);
        }
    }

    [AvaloniaFact]
    public void EditFlyout_CutThenPasteOnARealButton_ListsTheReplacedButtonUnderNotInAnySet()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        HashSet<string> before = [.. vm.FavoriteStore.AllFavorites.Select(f => f.Id)];
        FavoriteSet a = LoadedSet(vm, "FPI-CutOverA");
        FavoriteSet b = LoadedSet(vm, "FPI-CutOverB");
        FavoriteCommand source = AddTo(vm, a.Id, RealButton("FPI-CutOverSource"));
        FavoriteCommand replaced = AddTo(vm, b.Id, RealButton("FPI-CutOverReplaced"));
        (Window window, FavoritesBarView view) = ShowPalette(vm);
        try
        {
            OpenEditFlyout(window, view, source.Id, a.Id);
            ClickEditFlyoutButton(view, "Cut");
            OpenEditFlyout(window, view, replaced.Id, b.Id);
            ClickEditFlyoutButton(view, "Paste");

            Assert.Equal([source.Id], vm.FavoriteStore.GetSet(b.Id)!.FavoriteIds);
            Assert.NotNull(vm.FavoriteStore.GetFavorite(replaced.Id));
            Assert.Contains(vm.FavoriteStore.GetOrphanFavorites(), f => f.Id == replaced.Id);
            Assert.Null(vm.CutFavoriteSource);
        }
        finally
        {
            CloseAndCleanup(window, vm, before, a, b);
        }
    }

    [AvaloniaFact]
    public void PasteOfACutWhoseSourceSlotIsGone_PastesNothing_AndClearsTheCut()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        HashSet<string> before = [.. vm.FavoriteStore.AllFavorites.Select(f => f.Id)];
        FavoriteSet a = LoadedSet(vm, "FPI-CutGoneA");
        FavoriteSet b = LoadedSet(vm, "FPI-CutGoneB");
        FavoriteCommand source = AddTo(vm, a.Id, RealButton("FPI-CutGone"));
        FavoriteCommand target = AddTo(vm, b.Id, BlankButton(40));
        try
        {
            vm.CutFavorite(new FavoriteDisplayEntry(source, a.Id));
            vm.FavoriteStore.RemoveFromSet(a.Id, source.Id);

            vm.PasteFavoriteOnto(new FavoriteDisplayEntry(target, b.Id));

            Assert.Equal([target.Id], vm.FavoriteStore.GetSet(b.Id)!.FavoriteIds);
            Assert.Empty(vm.FavoriteStore.GetSet(a.Id)!.FavoriteIds);
            Assert.Null(vm.CutFavoriteSource);
            Assert.False(vm.HasFavoriteToPaste);
        }
        finally
        {
            DeleteAll(vm, [.. vm.FavoriteStore.AllFavorites.Select(f => f.Id).Where(id => !before.Contains(id))]);
            foreach (FavoriteSet set in new[] { a, b })
            {
                vm.Preferences.RemoveFavoriteSet(set.Id);
                vm.FavoriteStore.DeleteSet(set.Id);
            }
        }
    }

    [AvaloniaFact]
    public void PasteOfACutOntoAnAnchorWhoseSlotIsGone_ChangesNothing_AndTheCutStaysPending()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        HashSet<string> before = [.. vm.FavoriteStore.AllFavorites.Select(f => f.Id)];
        FavoriteSet a = LoadedSet(vm, "FPI-AnchorGoneA");
        FavoriteSet b = LoadedSet(vm, "FPI-AnchorGoneB");
        FavoriteCommand source = AddTo(vm, a.Id, RealButton("FPI-AnchorGone"));
        FavoriteCommand target = AddTo(vm, b.Id, RealButton("FPI-AnchorGoneTarget"));
        try
        {
            vm.CutFavorite(new FavoriteDisplayEntry(source, a.Id));
            vm.FavoriteStore.RemoveFromSet(b.Id, target.Id);

            vm.PasteFavoriteOnto(new FavoriteDisplayEntry(target, b.Id));

            Assert.Equal([source.Id], vm.FavoriteStore.GetSet(a.Id)!.FavoriteIds);
            Assert.Empty(vm.FavoriteStore.GetSet(b.Id)!.FavoriteIds);
            Assert.NotNull(vm.CutFavoriteSource);
        }
        finally
        {
            DeleteAll(vm, [.. vm.FavoriteStore.AllFavorites.Select(f => f.Id).Where(id => !before.Contains(id))]);
            foreach (FavoriteSet set in new[] { a, b })
            {
                vm.Preferences.RemoveFavoriteSet(set.Id);
                vm.FavoriteStore.DeleteSet(set.Id);
            }
        }
    }

    [AvaloniaFact]
    public void EditFlyout_PasteOnABlank_DeletesTheBlankNoOtherSetHolds()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        string globalId = vm.FavoriteStore.GlobalSet.Id;
        HashSet<string> before = [.. vm.FavoriteStore.AllFavorites.Select(f => f.Id)];
        FavoriteCommand source = AddTo(
            vm,
            globalId,
            new FavoriteCommand
            {
                Label = "FPI-LoneFill",
                CommandText = "FH 270",
                ButtonHeight = 40,
            }
        );
        var blank = new FavoriteCommand
        {
            IsSpacer = true,
            Category = FavoriteCommandCategory.Ground,
            ButtonHeight = 48,
        };
        vm.AddFavorite(blank, [globalId]);
        Dispatcher.UIThread.RunJobs();
        List<string> idsBefore = [.. vm.FavoriteStore.GetSet(globalId)!.FavoriteIds];
        (Window window, FavoritesBarView view) = ShowPalette(vm);
        try
        {
            OpenEditFlyout(window, view, source.Id, globalId);
            ClickEditFlyoutButton(view, "Copy");

            TabControl tabs = view.GetVisualDescendants().OfType<TabControl>().Single();
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => (t.Header as string) == "Ground");
            Dispatcher.UIThread.RunJobs();

            OpenEditFlyout(window, view, blank.Id, globalId);
            ClickEditFlyoutButton(view, "Paste");

            List<string> idsAfter = vm.FavoriteStore.GetSet(globalId)!.FavoriteIds;
            Assert.Equal(idsBefore.Count, idsAfter.Count);
            FavoriteCommand filled = vm.FavoriteStore.GetFavorite(idsAfter[idsBefore.IndexOf(blank.Id)])!;
            Assert.False(filled.IsSpacer);
            Assert.Equal("FPI-LoneFill", filled.Label);
            Assert.Equal(FavoriteCommandCategory.Ground, filled.Category);
            Assert.Equal(48, filled.ButtonHeight);
            Assert.Null(vm.FavoriteStore.GetFavorite(blank.Id));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
            DeleteAll(vm, [.. vm.FavoriteStore.AllFavorites.Select(f => f.Id).Where(id => !before.Contains(id))]);
        }
    }

    [AvaloniaFact]
    public void EditFlyout_PasteOnAFilledButton_KeepsTheReplacedButtonInTheStore()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        string globalId = vm.FavoriteStore.GlobalSet.Id;
        HashSet<string> before = [.. vm.FavoriteStore.AllFavorites.Select(f => f.Id)];
        FavoriteCommand source = AddTo(vm, globalId, new FavoriteCommand { Label = "FPI-KeepSource", CommandText = "FH 270" });
        FavoriteCommand target = AddTo(vm, globalId, new FavoriteCommand { Label = "FPI-KeepTarget", CommandText = "FH 090" });
        List<string> idsBefore = [.. vm.FavoriteStore.GetSet(globalId)!.FavoriteIds];
        (Window window, FavoritesBarView view) = ShowPalette(vm);
        try
        {
            OpenEditFlyout(window, view, source.Id, globalId);
            ClickEditFlyoutButton(view, "Copy");
            OpenEditFlyout(window, view, target.Id, globalId);
            ClickEditFlyoutButton(view, "Paste");

            List<string> idsAfter = vm.FavoriteStore.GetSet(globalId)!.FavoriteIds;
            Assert.Equal(idsBefore.Count, idsAfter.Count);
            Assert.DoesNotContain(target.Id, idsAfter);
            Assert.Equal("FPI-KeepSource", vm.FavoriteStore.GetFavorite(idsAfter[idsBefore.IndexOf(target.Id)])!.Label);

            Assert.NotNull(vm.FavoriteStore.GetFavorite(target.Id));
            Assert.Empty(vm.GetFavoriteMembership(target.Id));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
            DeleteAll(vm, [.. vm.FavoriteStore.AllFavorites.Select(f => f.Id).Where(id => !before.Contains(id))]);
        }
    }

    [AvaloniaFact]
    public void EditFlyout_PasteOnAnAllBlank_KeepsTheAllTab()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        string globalId = vm.FavoriteStore.GlobalSet.Id;
        HashSet<string> before = [.. vm.FavoriteStore.AllFavorites.Select(f => f.Id)];
        FavoriteCommand source = AddTo(
            vm,
            globalId,
            new FavoriteCommand
            {
                Label = "FPI-AirFill",
                CommandText = "FH 270",
                ButtonHeight = 40,
            }
        );
        var blank = new FavoriteCommand
        {
            IsSpacer = true,
            Category = FavoriteCommandCategory.All,
            ButtonHeight = 48,
        };
        vm.AddFavorite(blank, [globalId]);
        Dispatcher.UIThread.RunJobs();
        List<string> idsBefore = [.. vm.FavoriteStore.GetSet(globalId)!.FavoriteIds];
        (Window window, FavoritesBarView view) = ShowPalette(vm);
        try
        {
            OpenEditFlyout(window, view, source.Id, globalId);
            ClickEditFlyoutButton(view, "Copy");
            OpenEditFlyout(window, view, blank.Id, globalId);
            ClickEditFlyoutButton(view, "Paste");

            List<string> idsAfter = vm.FavoriteStore.GetSet(globalId)!.FavoriteIds;
            FavoriteCommand filled = vm.FavoriteStore.GetFavorite(idsAfter[idsBefore.IndexOf(blank.Id)])!;
            Assert.False(filled.IsSpacer);
            Assert.Equal(FavoriteCommandCategory.All, filled.Category);
            Assert.Equal(48, filled.ButtonHeight);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
            DeleteAll(vm, [.. vm.FavoriteStore.AllFavorites.Select(f => f.Id).Where(id => !before.Contains(id))]);
        }
    }
}
