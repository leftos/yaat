using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Client.Views.Ground;
using Yaat.Client.Views.Radar;
using Yaat.Sim;
using Yaat.Sim.Commands;
using CatalogMenuView = Yaat.Client.ContextMenus.MenuView;

namespace Yaat.Client.UI.Tests.Views;

// The whole-menu builders behind the three right-click handlers. Each handler resolves the aircraft, calls its
// builder and then shows or assigns the result, so these tests pin the exact top-level sequence a right-click
// produces without opening a popup or touching a canvas. Picker items are headers whose values live in a popup,
// so their MenuPickerDescriptor carries the values for inspection.
public class ContextMenuBuilderSeamTests
{
    /// <summary>Each top-level item as its header text, with a separator written as <c>---</c>.</summary>
    private static List<string> Sequence(ContextMenu menu)
    {
        var items = new List<string>(menu.Items.Count);
        foreach (object? item in menu.Items)
        {
            items.Add(
                item switch
                {
                    Separator => "---",
                    MenuItem menuItem => menuItem.Header as string ?? "(unnamed)",
                    _ => "(unnamed)",
                }
            );
        }

        return items;
    }

    /// <summary>The whole top-level sequence, plus the disabled bold aircraft header every menu starts with.</summary>
    private static void AssertSequence(ContextMenu menu, params string[] expected)
    {
        // Compared as text so a mismatch prints both whole sequences.
        Assert.Equal(string.Join("\n", expected), string.Join("\n", Sequence(menu)));
        Assert.False(Assert.IsType<MenuItem>(menu.Items[0]).IsEnabled);
    }

    /// <summary>
    /// A radar view parented to a host carrying the MainViewModel, so <c>FindMainViewModel</c> resolves and the
    /// favorites and RPO blocks build as they do in the running client.
    /// </summary>
    private static (RadarView View, MainViewModel Main) RadarHarness()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var view = new RadarView { DataContext = main.Radar };
        var host = new Grid { DataContext = main };
        host.Children.Add(view);
        return (view, main);
    }

    /// <summary>A ground view parented the same way, with the ground view model the handler reads.</summary>
    private static (GroundView View, GroundViewModel Ground, MainViewModel Main) GroundHarness()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var ground = new GroundViewModel(new ServerConnection(), sendCommand: (_, _, _) => Task.CompletedTask);
        var view = new GroundView { DataContext = ground };
        var host = new Grid { DataContext = main };
        host.Children.Add(view);
        return (view, ground, main);
    }

    private static AircraftModel AirborneIfr(string callsign, string phase) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "B738",
            IsOnGround = false,
            FlightRules = "IFR",
            CurrentPhase = phase,
        };

    [AvaloniaFact]
    public void RadarBuildAircraftContextMenu_ReturnsTopLevelItems()
    {
        (RadarView view, MainViewModel main) = RadarHarness();
        AircraftModel ac = AirborneIfr("AAL123", "InitialClimb");
        main.Aircraft.Add(ac);

        ContextMenu menu = view.BuildAircraftContextMenu(main.Radar, ac, prevSelected: null, ac.Callsign);

        AssertSequence(
            menu,
            "AAL123 — B738",
            "---",
            "Command…",
            "Note…",
            "---",
            "Favorite Commands",
            "---",
            "Heading",
            "Altitude",
            "Speed",
            "Navigation",
            "---",
            "Hold",
            "Approach",
            "Procedures",
            "---",
            "Track",
            "Data Block",
            "Squawk",
            "Ask pilot to say...",
            "Coordination",
            "---",
            "Edit flight plan",
            "---",
            "Display",
            "Draw route",
            "---",
            "Warp...",
            "Delete"
        );
    }

    [AvaloniaFact]
    public void RadarBuildAircraftContextMenu_SurfaceShadow_ReadOnlyItems()
    {
        (RadarView view, MainViewModel main) = RadarHarness();
        AircraftModel ac = AirborneIfr("SWA9", "InitialClimb");
        ac.IsLiveTraffic = true;
        ac.IsOnGround = true;
        main.Aircraft.Add(ac);

        ContextMenu menu = view.BuildAircraftContextMenu(main.Radar, ac, prevSelected: null, ac.Callsign);

        AssertSequence(
            menu,
            "SWA9 — B738",
            "---",
            "Command…",
            "Note…",
            "---",
            "Favorite Commands",
            "---",
            "Track",
            "Data Block",
            "Coordination",
            "Display",
            "---",
            "Delete"
        );
        // A surface shadow is not assumable, so it gets neither the assume items nor any relative-traffic block.
        Assert.DoesNotContain(Sequence(menu), item => item.StartsWith('↪'));
    }

    /// <summary>A surface live-traffic shadow: on the ground, so it is never assumable and its menu stays read-only.</summary>
    private static AircraftModel SurfaceShadow(string callsign)
    {
        AircraftModel ac = AirborneIfr(callsign, "");
        ac.IsLiveTraffic = true;
        ac.IsOnGround = true;
        return ac;
    }

    /// <summary>A surface shadow's radar menu, through the radar view's whole-menu builder.</summary>
    private static ContextMenu RadarSurfaceShadowMenu(AircraftModel shadow)
    {
        (RadarView view, MainViewModel main) = RadarHarness();
        main.Aircraft.Add(shadow);
        return view.BuildAircraftContextMenu(main.Radar, shadow, prevSelected: null, shadow.Callsign);
    }

    /// <summary>A surface shadow's ground menu, through the ground view's whole-menu builder.</summary>
    private static ContextMenu GroundSurfaceShadowMenu(AircraftModel shadow)
    {
        (GroundView view, GroundViewModel ground, MainViewModel main) = GroundHarness();
        main.Aircraft.Add(shadow);
        return view.BuildAircraftContextMenu(ground, new GroundMenuTarget(shadow, PrevSelected: null, shadow.Callsign, "AB"));
    }

    /// <summary>A surface shadow's list menu, through the list's whole-menu builder.</summary>
    private static ContextMenu ListSurfaceShadowMenu(AircraftModel shadow)
    {
        var main = new MainViewModel(new FakeFilePickerService());
        main.Aircraft.Add(shadow);
        return DataGridView.BuildAircraftMenu(main, new DataGrid(), shadow, null, [shadow], "AB");
    }

    /// <summary>The top-level items after the favorites block: the groups every aircraft menu opens with.</summary>
    private static List<string> AfterFavorites(ContextMenu menu)
    {
        List<string> sequence = Sequence(menu);
        return sequence[(sequence.IndexOf("Favorite Commands") + 2)..];
    }

    /// <summary>The top-level Display submenu of a menu that offers one.</summary>
    private static MenuItem DisplayOf(ContextMenu menu) => menu.Items.OfType<MenuItem>().Single(i => (i.Header as string) == "Display");

    /// <summary>A canvas click on the shadow <c>SWA9</c> for <paramref name="view"/>, the way that view builds it.</summary>
    private static MenuContext Context(CatalogMenuView view) => TestMenuContext.Create("SWA9", "AB", null, false, VfrCommandsForIfr.None, view);

    /// <summary>The Display submenu's item headers, in order.</summary>
    private static List<string> DisplayHeaders(ContextMenu menu) =>
        [.. DisplayOf(menu).Items.OfType<MenuItem>().Select(i => i.Header as string ?? "")];

    // Every view's Display is the view's own: each builds it from its own canvas state (or from none, on the aircraft
    // list), so it still builds over a host with no canvas members at all, and so does the ground's flat item list.
    [AvaloniaFact]
    public void EveryViewsDisplay_BuildsOverAHostWithNoCanvasMembers()
    {
        var host = new RecordingMenuHost("");
        AircraftModel shadow = SurfaceShadow("SWA9");

        (RadarView radarView, MainViewModel radarMain) = RadarHarness();
        (GroundView groundView, GroundViewModel ground, MainViewModel groundMain) = GroundHarness();
        MenuContext radar = Context(CatalogMenuView.Radar);
        MenuContext groundContext = Context(CatalogMenuView.Ground);
        MenuContext list = Context(CatalogMenuView.List);

        (MenuContext Context, MenuItem Display)[] sections =
        [
            (radar, radarView.BuildCanvasDisplay(radarMain.Radar, radar, host)),
            (groundContext, groundView.BuildCanvasDisplay(ground, groundContext, host)),
            (list, DataGridView.BuildCanvasDisplay(list, host)),
        ];

        // Each view's shadow tree still builds, and its Display carries the view's own items.
        foreach ((MenuContext context, MenuItem display) in sections)
        {
            var menu = new ContextMenu();
            SharedMenuGroups.AddSurfaceShadow(menu.Items, shadow, context, host, display);
            Assert.Equal(["Track", "Data Block", "Coordination", "Display"], Sequence(menu));
            Assert.NotEmpty(display.Items);
            Assert.False(display.Items[0] is Separator);
        }

        // The ground's flat display items come from the ground view too, and still build over the same host.
        var groundMenu = new ContextMenu();
        SharedMenuGroups.AddGroundDisplay(groundMenu.Items, groundView.BuildCanvasItems(ground, groundContext));
        List<string?> groundItems = [.. groundMenu.Items.Select(i => (i as MenuItem)?.Header as string)];
        Assert.Equal("Taxi route", groundItems[0]);
        Assert.Equal("Hide datablock", groundItems[1]);
    }

    // Every view gives a surface shadow the same read-only tree: track / data block / coordination / display, then the foot.
    [AvaloniaFact]
    public void SurfaceShadow_OffersTheSameGroupsOnEveryView()
    {
        AircraftModel shadow = SurfaceShadow("SWA9");

        string[] expected = ["Track", "Data Block", "Coordination", "Display", "---", "Delete"];
        Assert.Equal(expected, AfterFavorites(RadarSurfaceShadowMenu(shadow)));
        Assert.Equal(expected, AfterFavorites(GroundSurfaceShadowMenu(shadow)));
        Assert.Equal(expected, AfterFavorites(ListSurfaceShadowMenu(shadow)));
    }

    // The list's Display has no canvas block, so it must open on the overlay group, never on a separator.
    [AvaloniaFact]
    public void ListSurfaceShadow_DisplayHasNoLeadingOrTrailingSeparator()
    {
        List<object?> items = [.. DisplayOf(ListSurfaceShadowMenu(SurfaceShadow("SWA9"))).Items];

        Assert.NotEmpty(items);
        Assert.False(items[0] is Separator);
        Assert.False(items[^1] is Separator);
        for (int i = 1; i < items.Count; i++)
        {
            Assert.False((items[i] is Separator) && (items[i - 1] is Separator));
        }
    }

    // Only the ground view's shadow offers the ground's own display entries; the radar's and the list's do not.
    [AvaloniaFact]
    public void GroundSurfaceShadow_DisplayHasTaxiRouteAndHideDatablock()
    {
        List<string> groundHeaders = DisplayHeaders(GroundSurfaceShadowMenu(SurfaceShadow("SWA9")));
        Assert.Contains("Taxi route", groundHeaders);
        Assert.Contains("Hide datablock", groundHeaders);

        Assert.DoesNotContain("Taxi route", DisplayHeaders(RadarSurfaceShadowMenu(SurfaceShadow("SWA9"))));
        Assert.DoesNotContain("Taxi route", DisplayHeaders(ListSurfaceShadowMenu(SurfaceShadow("SWA9"))));
        Assert.DoesNotContain("Hide datablock", DisplayHeaders(RadarSurfaceShadowMenu(SurfaceShadow("SWA9"))));
        Assert.DoesNotContain("Hide datablock", DisplayHeaders(ListSurfaceShadowMenu(SurfaceShadow("SWA9"))));
    }

    [AvaloniaFact]
    public void GroundBuildAircraftContextMenu_ReturnsTopLevelItems()
    {
        (GroundView view, GroundViewModel ground, MainViewModel main) = GroundHarness();
        var ac = new AircraftModel
        {
            Callsign = "UAL100",
            AircraftType = "B738",
            IsOnGround = true,
            FlightRules = "IFR",
            CurrentPhase = "At Parking",
            Position = new LatLon(37.620, -122.380),
        };
        main.Aircraft.Add(ac);

        ContextMenu menu = view.BuildAircraftContextMenu(ground, new GroundMenuTarget(ac, PrevSelected: null, ac.Callsign, "AB"));

        AssertSequence(
            menu,
            "UAL100 — B738",
            "---",
            "Command…",
            "Note…",
            "---",
            "Favorite Commands",
            "---",
            "Push back",
            "Push route...",
            "---",
            "Draw taxi route...",
            "Taxi route",
            "Hide datablock",
            "---",
            "Delete"
        );
    }

    [AvaloniaFact]
    public void ListBuildAircraftMenu_ReturnsTopLevelItems()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var ac = new AircraftModel
        {
            Callsign = "UAL100",
            AircraftType = "B738",
            IsOnGround = true,
            FlightRules = "IFR",
            CurrentPhase = "LinedUpAndWaiting",
            AssignedRunway = "30",
        };

        ContextMenu menu = DataGridView.BuildAircraftMenu(main, new DataGrid(), ac, null, [ac], "AB");

        AssertSequence(
            menu,
            "UAL100 — B738",
            "---",
            "Command…",
            "Note…",
            "---",
            "Favorite Commands",
            "---",
            "Cleared for takeoff 30",
            "Cancel takeoff clearance",
            "---",
            "Track",
            "Squawk",
            "Ask pilot to say...",
            "Coordination",
            "---",
            "Edit flight plan",
            "---",
            "Delete"
        );
    }

    [AvaloniaFact]
    public void ListBuildAircraftMenu_TwoSelectedShadows_AddsMultiSelectionItems()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        AircraftModel first = AirborneIfr("SWA1", "InitialClimb");
        first.IsLiveTraffic = true;
        AircraftModel second = AirborneIfr("SWA2", "InitialClimb");
        second.IsLiveTraffic = true;

        ContextMenu menu = DataGridView.BuildAircraftMenu(main, new DataGrid(), first, null, [first, second], "AB");

        List<string> sequence = Sequence(menu);
        // The right-clicked shadow is assumable, so it leads with the assume items...
        Assert.Contains("Assume control", sequence);
        Assert.Contains("Assume and track", sequence);
        // ...and the two selected shadows add the multi-selection item after the Delete, not the release item.
        Assert.Equal("---", sequence[^2]);
        Assert.Equal("Assume selected live traffic (2)", sequence[^1]);
        Assert.DoesNotContain("Release to live feed", sequence);
    }

    [AvaloniaFact]
    public void ListBuildAircraftMenu_DelayedAircraft_ShowsDelayedSpawnItems()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        AircraftModel ac = AirborneIfr("UAL9", "InitialClimb");
        ac.Status = "Delayed";

        ContextMenu menu = DataGridView.BuildAircraftMenu(main, new DataGrid(), ac, null, [ac], "AB");

        AssertSequence(
            menu,
            "UAL9 — B738",
            "---",
            "Command…",
            "Note…",
            "---",
            "Favorite Commands",
            "---",
            "Spawn now",
            "Change spawn delay",
            "Delete"
        );
    }

    [AvaloniaFact]
    public void RadarPicker_CarriesDescriptorWithItsValues()
    {
        (RadarView view, MainViewModel main) = RadarHarness();
        AircraftModel ac = AirborneIfr("AAL123", "InitialClimb");
        main.Aircraft.Add(ac);

        ContextMenu menu = view.BuildAircraftContextMenu(main.Radar, ac, prevSelected: null, ac.Callsign);

        // "Fly heading" lists every 5-degree heading the popup would list, in the same order.
        MenuItem heading = menu.Items.OfType<MenuItem>().Single(m => (string?)m.Header == "Heading");
        MenuItem flyHeading = heading.Items.OfType<MenuItem>().Single(m => (string?)m.Header == "Fly heading");
        MenuPickerDescriptor headingDescriptor = Assert.IsType<MenuPickerDescriptor>(flyHeading.Tag);
        Assert.Equal(MenuPickerDescriptor.List, headingDescriptor.Kind);
        Assert.Equal(72, headingDescriptor.Items.Count);
        Assert.Equal("5", headingDescriptor.Items[0]);
        Assert.Equal("360", headingDescriptor.Items[^1]);

        // A free-text picker carries the input kind and no values.
        MenuItem speed = menu.Items.OfType<MenuItem>().Single(m => (string?)m.Header == "Speed");
        MenuItem speedInput = speed.Items.OfType<MenuItem>().Single(m => (string?)m.Header == "Speed...");
        MenuPickerDescriptor inputDescriptor = Assert.IsType<MenuPickerDescriptor>(speedInput.Tag);
        Assert.Equal(MenuPickerDescriptor.Input, inputDescriptor.Kind);
        Assert.Empty(inputDescriptor.Items);
    }

    [AvaloniaFact]
    public void RadarMenu_GroundPair_OffersGiveWayAndFollowG()
    {
        (RadarView view, MainViewModel main) = RadarHarness();
        AircraftModel clicked = AirborneIfr("SWA104", "Taxiing");
        clicked.IsOnGround = true;
        AircraftModel selected = AirborneIfr("SWA602", "Taxiing");
        selected.IsOnGround = true;
        main.Aircraft.Add(clicked);
        main.Aircraft.Add(selected);

        ContextMenu menu = view.BuildAircraftContextMenu(main.Radar, clicked, selected, clicked.Callsign);

        List<string> sequence = Sequence(menu);
        Assert.Contains("↪ SWA602:", sequence);
        Assert.Contains("SWA602: give way to SWA104", sequence);
        Assert.Contains("SWA602: follow SWA104", sequence);
        Assert.DoesNotContain(sequence, item => item.Contains("report SWA104 in sight", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void GroundMenu_AirbornePair_OffersReportInSightAndFollow()
    {
        (GroundView view, GroundViewModel ground, MainViewModel main) = GroundHarness();
        AircraftModel clicked = AirborneIfr("AAL601", "ApproachNav");
        AircraftModel selected = AirborneIfr("AAL602", "ApproachNav");
        selected.LastReportedTrafficCallsign = clicked.Callsign;
        main.Aircraft.Add(clicked);
        main.Aircraft.Add(selected);

        ContextMenu menu = view.BuildAircraftContextMenu(ground, new GroundMenuTarget(clicked, selected, clicked.Callsign, "AB"));

        List<string> sequence = Sequence(menu);
        Assert.Contains("↪ AAL602:", sequence);
        Assert.Contains("AAL602: report AAL601 in sight", sequence);
        Assert.Contains("AAL602: follow AAL601", sequence);
        Assert.DoesNotContain(sequence, item => item.Contains("give way", StringComparison.Ordinal));
    }
}
