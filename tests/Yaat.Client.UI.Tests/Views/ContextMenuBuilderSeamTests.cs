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

namespace Yaat.Client.UI.Tests.Views;

// The whole-menu builders behind the three right-click handlers. Each handler resolves the aircraft, calls its
// builder and then shows or assigns the result, so these tests pin the exact top-level sequence a right-click
// produces without opening a popup or touching a canvas. Picker items are headers whose values live in a popup,
// so their MenuPickerDescriptor carries the values for inspection.
public class ContextMenuBuilderSeamTests
{
    /// <summary>Each top-level item as its header text, with a separator written as <c>---</c>.</summary>
    private static List<string> Sequence(ContextMenu menu) => Sequence(menu.Items);

    /// <summary>The All Commands submenu's items as header text, with a separator written as <c>---</c>.</summary>
    private static List<string> AllCommandsSequence(ContextMenu menu) => Sequence(AllCommands(menu).Items);

    private static MenuItem AllCommands(ContextMenu menu) =>
        menu.Items.OfType<MenuItem>().Single(m => (m.Header as string) == AircraftMenuBuilder.AllCommandsHeader);

    private static List<string> Sequence(ItemCollection menuItems)
    {
        var items = new List<string>(menuItems.Count);
        foreach (object? item in menuItems)
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

    /// <summary>A ground view over the main view model's primary ground view model, parented the same way.</summary>
    private static (GroundView View, GroundViewModel Ground, MainViewModel Main) GroundHarness()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var view = new GroundView { DataContext = main.Ground };
        var host = new Grid { DataContext = main };
        host.Children.Add(view);
        return (view, main.Ground, main);
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
            "AAL123 · B738",
            "Departure",
            "---",
            "Command…",
            "Note…",
            "---",
            "Track",
            "Data Block",
            "Squawk",
            "Display",
            "Draw route",
            "Favorite Commands",
            "All Commands",
            "---",
            "Delete"
        );
        Assert.Equal(
            [
                "Heading",
                "Altitude",
                "Speed",
                "Navigation",
                "Hold",
                "Approach",
                "Procedures",
                "---",
                "Ask pilot to say…",
                "Coordination",
                "---",
                "Edit flight plan",
                "---",
                "Warp…",
            ],
            AllCommandsSequence(menu)
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
            "SWA9 · B738",
            "Departure",
            "---",
            "Command…",
            "Note…",
            "---",
            "Track",
            "Data Block",
            "Display",
            "Favorite Commands",
            "All Commands",
            "---",
            "Delete"
        );
        Assert.Equal(["Coordination"], AllCommandsSequence(menu));
        // A surface shadow is not assumable, so it gets neither the assume items nor any relative-traffic block.
        Assert.DoesNotContain(Sequence(menu), item => item.EndsWith(" (selected)", StringComparison.Ordinal));
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
        return view.BuildAircraftContextMenu(ground, shadow, prevSelected: null, shadow.Callsign);
    }

    /// <summary>A surface shadow's list menu, through the list's whole-menu builder.</summary>
    private static ContextMenu ListSurfaceShadowMenu(AircraftModel shadow)
    {
        var main = new MainViewModel(new FakeFilePickerService());
        main.Aircraft.Add(shadow);
        return DataGridView.BuildAircraftMenu(main, new DataGrid(), shadow, null, [shadow]);
    }

    /// <summary>The top-level items after the header (Command…, Note… and their separator).</summary>
    private static List<string> AfterHeader(ContextMenu menu)
    {
        List<string> sequence = Sequence(menu);
        return sequence[(sequence.IndexOf("Note…") + 2)..];
    }

    /// <summary>The top-level Display submenu of a menu that offers one.</summary>
    private static MenuItem DisplayOf(ContextMenu menu) => menu.Items.OfType<MenuItem>().Single(i => (i.Header as string) == "Display");

    /// <summary>A canvas click on the shadow <c>SWA9</c>, the way a view builds it.</summary>
    private static MenuContext Context() => TestMenuContext.Create("SWA9", "AB", null, false, VfrCommandsForIfr.None);

    /// <summary>The Display submenu's item headers, in order.</summary>
    private static List<string> DisplayHeaders(ContextMenu menu) =>
        [.. DisplayOf(menu).Items.OfType<MenuItem>().Select(i => i.Header as string ?? "")];

    // Each canvas's Display is the view's own: each builds it from its own canvas state, so it still builds over a host
    // with no canvas members at all.
    [AvaloniaFact]
    public void RadarAndGroundDisplay_BuildOverAHostWithNoCanvasMembers()
    {
        var host = new RecordingMenuHost("");

        (RadarView radarView, MainViewModel radarMain) = RadarHarness();
        (GroundView groundView, GroundViewModel ground, _) = GroundHarness();
        MenuContext context = Context();

        MenuItem[] displays = [radarView.BuildCanvasDisplay(radarMain.Radar, context, host), groundView.BuildCanvasDisplay(ground, context)];

        foreach (MenuItem display in displays)
        {
            Assert.NotEmpty(display.Items);
            Assert.False(display.Items[0] is Separator);
        }
    }

    // Every view gives a surface shadow the same read-only tree: track / data block, then the view's section (Display on
    // the canvases, nothing on the list), Favorites, All Commands holding Coordination, then the foot.
    [AvaloniaFact]
    public void SurfaceShadow_OffersTheSameGroupsOnEveryView()
    {
        AircraftModel shadow = SurfaceShadow("SWA9");

        string[] canvas = ["Track", "Data Block", "Display", "Favorite Commands", "All Commands", "---", "Delete"];
        Assert.Equal(canvas, AfterHeader(RadarSurfaceShadowMenu(shadow)));
        Assert.Equal(canvas, AfterHeader(GroundSurfaceShadowMenu(shadow)));
        Assert.Equal(["Track", "Data Block", "Favorite Commands", "All Commands", "---", "Delete"], AfterHeader(ListSurfaceShadowMenu(shadow)));
        Assert.Equal(["Coordination"], AllCommandsSequence(ListSurfaceShadowMenu(shadow)));
    }

    // Only the ground view's shadow offers the ground's own display entries; the radar's does not.
    [AvaloniaFact]
    public void GroundSurfaceShadow_DisplayHasTaxiRouteAndHideDatablock()
    {
        List<string> groundHeaders = DisplayHeaders(GroundSurfaceShadowMenu(SurfaceShadow("SWA9")));
        Assert.Contains("Taxi route", groundHeaders);
        Assert.Contains("Hide datablock", groundHeaders);
        Assert.DoesNotContain("Leader direction", groundHeaders);

        Assert.DoesNotContain("Taxi route", DisplayHeaders(RadarSurfaceShadowMenu(SurfaceShadow("SWA9"))));
        Assert.DoesNotContain("Hide datablock", DisplayHeaders(RadarSurfaceShadowMenu(SurfaceShadow("SWA9"))));
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

        ContextMenu menu = view.BuildAircraftContextMenu(ground, ac, prevSelected: null, ac.Callsign);

        AssertSequence(
            menu,
            "UAL100 · B738",
            "At parking",
            "---",
            "Command…",
            "Note…",
            "---",
            "Track",
            "Data Block",
            "Squawk",
            "Display",
            "Favorite Commands",
            "All Commands",
            "---",
            "Delete"
        );
        Assert.Equal(
            [
                "Push back",
                "Push route…",
                "Taxi to runway",
                "Draw taxi route…",
                "---",
                "Ask pilot to say…",
                "Coordination",
                "---",
                "Edit flight plan",
                "---",
                "Warp…",
            ],
            AllCommandsSequence(menu)
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

        ContextMenu menu = DataGridView.BuildAircraftMenu(main, new DataGrid(), ac, null, [ac]);

        AssertSequence(
            menu,
            "UAL100 · B738",
            "Line up and wait",
            "---",
            "Command…",
            "Note…",
            "---",
            "Track",
            "Data Block",
            "Squawk",
            "Favorite Commands",
            "All Commands",
            "---",
            "Delete"
        );
        Assert.Equal(["Tower", "---", "Ask pilot to say…", "Coordination", "---", "Edit flight plan", "---", "Warp…"], AllCommandsSequence(menu));
    }

    [AvaloniaFact]
    public void ListBuildAircraftMenu_TwoSelectedShadows_AddsMultiSelectionItems()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        AircraftModel first = AirborneIfr("SWA1", "InitialClimb");
        first.IsLiveTraffic = true;
        AircraftModel second = AirborneIfr("SWA2", "InitialClimb");
        second.IsLiveTraffic = true;

        ContextMenu menu = DataGridView.BuildAircraftMenu(main, new DataGrid(), first, null, [first, second]);

        List<string> sequence = Sequence(menu);
        // The right-clicked shadow is assumable, so its command tree leads with the assume items...
        Assert.Contains("Assume control", AllCommandsSequence(menu));
        Assert.Contains("Assume and track", AllCommandsSequence(menu));
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

        ContextMenu menu = DataGridView.BuildAircraftMenu(main, new DataGrid(), ac, null, [ac]);

        AssertSequence(
            menu,
            "UAL9 · B738",
            "Departure",
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
        MenuItem heading = AllCommands(menu).Items.OfType<MenuItem>().Single(m => (string?)m.Header == "Heading");
        MenuItem flyHeading = heading.Items.OfType<MenuItem>().Single(m => (string?)m.Header == "Fly heading");
        MenuPickerDescriptor headingDescriptor = Assert.IsType<MenuPickerDescriptor>(flyHeading.Tag);
        Assert.Equal(MenuPickerDescriptor.List, headingDescriptor.Kind);
        Assert.Equal(72, headingDescriptor.Items.Count);
        Assert.Equal("5", headingDescriptor.Items[0]);
        Assert.Equal("360", headingDescriptor.Items[^1]);

        // A free-text picker carries the input kind and no values.
        MenuItem speed = AllCommands(menu).Items.OfType<MenuItem>().Single(m => (string?)m.Header == "Speed");
        MenuItem speedInput = speed.Items.OfType<MenuItem>().Single(m => (string?)m.Header == "Speed…");
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
        Assert.Contains("For SWA602 (selected)", sequence);
        Assert.Contains("Give way to SWA104", sequence);
        Assert.Contains("Follow SWA104", sequence);
        Assert.DoesNotContain("Report in sight", sequence);
        AssertForSectionIcons(menu, "Give way to SWA104", "Follow SWA104");
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

        ContextMenu menu = view.BuildAircraftContextMenu(ground, clicked, selected, clicked.Callsign);

        List<string> sequence = Sequence(menu);
        Assert.Contains("For AAL602 (selected)", sequence);
        Assert.Contains("Report in sight", sequence);
        Assert.Contains("Follow", sequence);
        Assert.DoesNotContain(sequence, item => item.Contains("Give way to", StringComparison.Ordinal));
        AssertForSectionIcons(menu, "Report in sight", "Follow");
    }

    /// <summary>Asserts each named top-level item carries the quick-command glyph it was built with.</summary>
    private static void AssertForSectionIcons(ContextMenu menu, params string[] headers)
    {
        foreach (string header in headers)
        {
            MenuItem item = menu.Items.OfType<MenuItem>().Single(m => (m.Header as string) == header);
            Assert.NotNull(item.Icon);
        }
    }
}
