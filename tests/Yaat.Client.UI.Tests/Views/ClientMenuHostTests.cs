using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Client.Views.Radar;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Situation;

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

    [AvaloniaFact]
    public void GetNearbyTraffic_NearestFirst_ExcludesSelfDelayedAndShadows()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var at = new LatLon(37.5, -122.0);
        AircraftModel self = Airborne("N302AB", at, 3000);
        main.Aircraft.Add(self);
        // Added farthest first, so the order must come from the distance, not the list.
        foreach (int i in Enumerable.Range(1, 7).Reverse())
        {
            main.Aircraft.Add(Airborne($"AAL{i}", new LatLon(at.Lat + (i * 0.01), at.Lon), 3000 + (i * 100)));
        }

        AircraftModel delayed = Airborne("DLY1", new LatLon(at.Lat + 0.001, at.Lon), 3000);
        delayed.Status = "Delayed (5:00)";
        AircraftModel shadow = Airborne("SHD1", new LatLon(at.Lat + 0.002, at.Lon), 3000);
        shadow.IsLiveTraffic = true;
        AircraftModel onGround = Airborne("GND1", new LatLon(at.Lat + 0.003, at.Lon), 0);
        onGround.IsOnGround = true;
        main.Aircraft.Add(delayed);
        main.Aircraft.Add(shadow);
        main.Aircraft.Add(onGround);
        var host = new ClientMenuHost(main, self, new Border());

        IReadOnlyList<MenuTrafficRow> rows = host.GetNearbyTraffic("N302AB");

        Assert.Equal(["AAL1", "AAL2", "AAL3", "AAL4", "AAL5"], rows.Select(row => row.Callsign));
        Assert.Equal(new MenuTrafficRow("AAL1", "B738", rows[0].DistanceNm, 12, 100), rows[0]);
        Assert.Equal(1, RelativeGeometry.WholeNm(rows[0].DistanceNm));
        Assert.Empty(host.GetNearbyTraffic("NOPE"));
    }

    private static AircraftModel Airborne(string callsign, LatLon position, double altitude) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "B738",
            IsOnGround = false,
            Position = position,
            Heading = new TrueHeading(0),
            Altitude = altitude,
        };

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

    /// <summary>
    /// A custom quick command carries text the controller typed, so its click goes through the VFR gate as a favorite's
    /// does: a VFR-only command offered under Both is refused for an IFR aircraft when VFR commands for IFR are off, and
    /// passes the gate for a VFR aircraft (whose send then fails only because no server is connected).
    /// </summary>
    [AvaloniaTheory]
    [InlineData("IFR", true)]
    [InlineData("VFR", false)]
    public void CustomQuickCommand_Click_GoesThroughTheVfrGate(string flightRules, bool rejected)
    {
        var main = new MainViewModel(new FakeFilePickerService());
        VfrCommandsForIfr savedMode = main.Preferences.VfrCommandsForIfr;
        main.Preferences.SetVfrCommandsForIfr(VfrCommandsForIfr.None);
        try
        {
            var ac = new AircraftModel
            {
                Callsign = "N123AB",
                CurrentPhase = "",
                IsOnGround = false,
                FlightRules = flightRules,
                Situation = AircraftSituation.IfrEnroute,
            };
            main.Aircraft.Add(ac);
            IMenuHost host = new ClientMenuHost(main, ac, new Border());
            var context = new MenuContext(new MenuClick(ac.Callsign, null, null, []), host.Session);
            List<QuickCommandEntry> entries = [new CustomQuickCommandEntry("Left traffic", "MLT", null, MenuFlightRules.Both)];
            MenuCatalogEntry entry = Assert.Single(QuickCommandResolver.Resolve(entries, ac, context, _ => true).Text);
            MenuItem item = Assert.IsType<MenuItem>(entry.Build(ac, context, host));

            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            HeadlessWindowExtensions.PumpDispatcher();

            string? rejection = VfrCommandGate.Evaluate(ac, "MLT", VfrCommandsForIfr.None).RejectionMessage;
            if (rejected)
            {
                Assert.NotNull(rejection);
                Assert.Equal(rejection, main.StatusText);
            }
            else
            {
                Assert.Null(rejection);
                Assert.StartsWith("Command error:", main.StatusText);
            }
        }
        finally
        {
            main.Preferences.SetVfrCommandsForIfr(savedMode);
        }
    }

    /// <summary>A custom quick command is sent as typed input is: a macro it names goes out as its expansion.</summary>
    [AvaloniaFact]
    public void CustomQuickCommand_WithMacro_SendsTheExpansion()
    {
        using var scope = new PreferencesFileScope();
        var main = new MainViewModel(new FakeFilePickerService());
        main.Preferences.SetMacros([new MacroDefinition { Name = "WD", Expansion = "FH 270" }]);

        Assert.Equal("FH 270", CustomQuickCommandSend(main, "!WD"));
    }

    /// <summary>A custom quick command is sent as typed input is: a verb typed under a renamed alias goes out canonical.</summary>
    [AvaloniaFact]
    public void CustomQuickCommand_WithRenamedAlias_SendsCanonical()
    {
        using var scope = new PreferencesFileScope();
        var main = new MainViewModel(new FakeFilePickerService());
        var scheme = CommandScheme.Default();
        scheme.Patterns[CanonicalCommandType.FlyHeading].Aliases = ["TURNTO"];
        main.Preferences.SetCommandScheme(scheme);

        Assert.Equal("FH 270", CustomQuickCommandSend(main, "TURNTO 270"));
    }

    /// <summary>A custom quick command naming a macro that does not exist goes out as typed, so the server's rejection names it.</summary>
    [AvaloniaFact]
    public void CustomQuickCommand_WithUnknownMacro_SendsTheTextAsTyped()
    {
        using var scope = new PreferencesFileScope();
        var main = new MainViewModel(new FakeFilePickerService());
        main.Preferences.SetMacros([]);

        Assert.Equal("!GONE", CustomQuickCommandSend(main, "!GONE"));
    }

    /// <summary>Each menu reads the macros as they are when it opens, so an edit between two menus reaches the second.</summary>
    [AvaloniaFact]
    public void CustomQuickCommand_MacroEditedBetweenMenus_SendsTheNewExpansion()
    {
        using var scope = new PreferencesFileScope();
        var main = new MainViewModel(new FakeFilePickerService());
        main.Preferences.SetMacros([new MacroDefinition { Name = "WD", Expansion = "FH 270" }]);
        AircraftModel ac = AddCustomQuickCommand(main, "!WD");
        Assert.Equal("FH 270", MenuSend(main, ac));

        main.Preferences.SetMacros([new MacroDefinition { Name = "WD", Expansion = "FH 090" }]);

        Assert.Equal("FH 090", MenuSend(main, ac));
    }

    // The command the client host's menu sends for a custom quick command holding text, as its menu item records it.
    private static string? CustomQuickCommandSend(MainViewModel main, string text) => MenuSend(main, AddCustomQuickCommand(main, text));

    // Stores a custom quick command holding text as the IFR-enroute list, and adds an aircraft in that situation.
    private static AircraftModel AddCustomQuickCommand(MainViewModel main, string text)
    {
        main.Preferences.SetQuickCommandList(AircraftSituation.IfrEnroute, [new CustomQuickCommandEntry("Custom", text, null, MenuFlightRules.Both)]);
        var ac = new AircraftModel
        {
            Callsign = "N123AB",
            CurrentPhase = "",
            IsOnGround = false,
            FlightRules = "IFR",
            Situation = AircraftSituation.IfrEnroute,
        };
        main.Aircraft.Add(ac);
        return ac;
    }

    // Opens a fresh client-host menu on the aircraft and returns what its single quick command sends.
    private static string? MenuSend(MainViewModel main, AircraftModel ac)
    {
        IMenuHost host = new ClientMenuHost(main, ac, new Border());
        var context = new MenuContext(new MenuClick(ac.Callsign, null, null, []), host.Session);
        MenuCatalogEntry entry = Assert.Single(QuickCommandResolver.Resolve(ac, context, _ => true).Text);
        return MenuCommandText.GetCommand(Assert.IsType<MenuItem>(entry.Build(ac, context, host)));
    }
}
