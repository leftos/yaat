using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Client.Views.Ground;
using Yaat.Client.Views.Radar;
using Yaat.Sim;

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

        ContextMenu menu = view.BuildAircraftContextMenu(main.Radar, ac, prevSelected: null, ac.Callsign, "AB");

        AssertSequence(
            menu,
            "AAL123 - B738",
            "---",
            "Command…",
            "---",
            "Favorite Commands",
            "---",
            "Heading",
            "Altitude",
            "Speed",
            "Navigation",
            "---",
            "Draw route",
            "Hold",
            "Approach",
            "Procedures",
            "---",
            "Track",
            "Data Block",
            "Squawk",
            "Ask pilot to say...",
            "Coordination",
            "Display",
            "---",
            "Sim Control"
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

        ContextMenu menu = view.BuildAircraftContextMenu(main.Radar, ac, prevSelected: null, ac.Callsign, "AB");

        AssertSequence(
            menu,
            "SWA9 - B738",
            "---",
            "Command…",
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

        ContextMenu menu = DataGridView.BuildAircraftMenu(main, new DataGrid(), ac, [ac], "AB");

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

        ContextMenu menu = DataGridView.BuildAircraftMenu(main, new DataGrid(), first, [first, second], "AB");

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

        ContextMenu menu = DataGridView.BuildAircraftMenu(main, new DataGrid(), ac, [ac], "AB");

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

        ContextMenu menu = view.BuildAircraftContextMenu(main.Radar, ac, prevSelected: null, ac.Callsign, "AB");

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
}
