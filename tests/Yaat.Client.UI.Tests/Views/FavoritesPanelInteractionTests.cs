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
