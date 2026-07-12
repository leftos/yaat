using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
    private static MainViewModel NewVm() => new(new FakeFilePickerService());

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

    [AvaloniaFact]
    public void FavoriteButtonClick_InPaletteMode_ReachesCommandDispatch()
    {
        var vm = NewVm();
        // A global (unscoped) air favorite carrying a real command but no callsign — exactly the
        // shape of a real favorite, which targets the currently SelectedAircraft.
        vm.AddFavorite(
            new FavoriteCommand
            {
                Label = "TestFav",
                CommandText = "FH 270",
                Category = FavoriteCommandCategory.Air,
            }
        );

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
            Assert.Null(vm.SelectedAircraft);
            Assert.Equal("Disconnected", vm.StatusText);

            var button = view.GetVisualDescendants().OfType<Button>().Single(b => b.Tag is FavoriteCommand { Label: "TestFav" });

            // Simulate a genuine pointer press + release (not RaiseEvent(Button.ClickEvent)) so the
            // FavoritesBarView tunnel handlers that capture the pointer for drag-reorder actually run.
            // Before the fix, the eager Pointer.Capture on press ate the click and StatusText stayed
            // "Disconnected".
            var center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window);
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
        }
    }

    [AvaloniaFact]
    public void FavoritePanelTargetText_TracksSelectedAircraft()
    {
        var vm = NewVm();
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
    public void FavoritesPanelWindow_IsUnowned_AndRendersTargetFeedback()
    {
        var vm = NewVm();
        var window = new FavoritesPanelWindow(vm.Preferences) { DataContext = vm };
        try
        {
            window.ShowAndRunLayout();

            // Fix 1 (#287): the panel is not an owned window — owned windows are locked above the
            // main window in Z-order and made the panel feel like it blocked clicks elsewhere.
            Assert.Null(window.Owner);

            // Fix 2 (#287): the panel renders its own target/status feedback so a click's result is
            // visible without switching back to the main window.
            var targetBlock = window.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text == "No aircraft selected");
            Assert.NotNull(targetBlock);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
