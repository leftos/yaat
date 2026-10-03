using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Client.Views.Radar;
using Yaat.Sim.Data;

namespace Yaat.Client.UI.Tests.Views;

// The one menu host every view builds per right-click.
public class ClientMenuHostTests
{
    [AvaloniaFact]
    public async Task FailedSend_IsShownInTheStatusLine()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        IMenuHost host = new ClientMenuHost(main, null, new Border());

        // Not connected to a server, so the send throws; the host shows it rather than letting it escape the click.
        await host.SendAsync("AAL123", "FH 270", "AB");

        Assert.StartsWith("Command error:", main.StatusText);
    }

    /// <summary>
    /// On the radar, the host's list picker opens the shared menu popup on the radar canvas's overlay, seeded with the
    /// current value, and a pick hands the item back and closes it.
    /// </summary>
    [AvaloniaFact]
    public void ListPicker_OnTheRadar_OpensTheSharedPopup()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var view = new RadarView { DataContext = main.Radar };
        var window = new Window { DataContext = main, Content = view };
        window.ShowAndRunLayout();
        try
        {
            IMenuHost host = new ClientMenuHost(main, null, view.Canvas);
            object? picked = null;
            host.ShowListPopup(
                ["090", "180", "270"],
                "180",
                value =>
                {
                    picked = value;
                    return Task.CompletedTask;
                }
            );
            HeadlessWindowExtensions.PumpDispatcher();

            var overlay = OverlayLayer.GetOverlayLayer(view);
            Assert.NotNull(overlay);
            Popup popup = Assert.Single(overlay.Children.OfType<Popup>());
            Assert.True(popup.IsOpen, "The list picker should open the shared popup.");
            ListBox list = Assert.Single(popup.Child!.GetLogicalDescendants().OfType<ListBox>());
            Assert.Equal<object?>("180", list.SelectedItem);

            list.SelectedIndex = 2;
            HeadlessWindowExtensions.PumpDispatcher();

            Assert.Equal<object?>("270", picked);
            Assert.False(popup.IsOpen, "A pick should close the popup.");
            Assert.DoesNotContain(popup, overlay.Children);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The flight-plan editor opens on the named aircraft, found in the main view model's list when it is not the menu's
    /// own; a callsign no longer in the list opens nothing.
    /// </summary>
    [AvaloniaFact]
    public void OpenFlightPlanEditor_OpensOnTheCallsignsAircraft()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        main.Aircraft.Add(new AircraftModel { Callsign = "DAL2" });
        IMenuHost host = new ClientMenuHost(main, new AircraftModel { Callsign = "UAL1" }, new Border());
        try
        {
            host.OpenFlightPlanEditor("DAL2");
            Assert.Equal("DAL2 - Flight Plan", FlightPlanEditorManager.OpenEditor?.Title);

            host.OpenFlightPlanEditor("GONE1");
            Assert.Equal("DAL2 - Flight Plan", FlightPlanEditorManager.OpenEditor?.Title);
        }
        finally
        {
            FlightPlanEditorManager.Close();
        }
    }

    [AvaloniaFact]
    public void EnterDrawRoute_ShowsTheGroundViewThenStartsTheDraw()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        var main = new MainViewModel(new FakeFilePickerService());
        main.Ground.SetLayoutForTesting(MenuGoldenFixtures.OakLayoutForClient);
        AircraftModel ac = MenuGoldenFixtures.For(MenuView.Radar).Single(f => f.Name == "taxiing").Aircraft;
        main.Aircraft.Add(ac);
        main.SelectedTabIndex = 0;
        Assert.False(main.IsGroundViewPoppedOut);
        Assert.Null(main.GroundShownAirportId);

        new ClientMenuHost(main, ac, new Border()).EnterDrawRoute(ac.Callsign);

        Assert.NotNull(main.GroundShownAirportId);
        Assert.True(main.Ground.IsDrawingRoute, "The draw should start on the primary ground view.");
    }
}
