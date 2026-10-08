using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Sim.Data;
using Yaat.Sim.Situation;

namespace Yaat.Client.UI.Tests.Views;

// The one view-independent aircraft menu builder: its tree from the aircraft, the click and the host, with the view's
// own section placed where the builder puts it.
public class AircraftMenuBuilderTests
{
    private const string ViewItem = "View item";
    private const string AllCommands = "All Commands";

    [AvaloniaFact]
    public void AirborneIfr_TopLevelRunsHeaderQuickCommandsThenTheFixedGroups()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        var host = new RecordingMenuHost("");

        List<string> items = Sequence(Build(Fixture("ifr-enroute"), host, _ => [new MenuItem { Header = ViewItem }]));

        int note = items.IndexOf(Label(MenuIds.AircraftNote));
        int track = items.IndexOf("Track");
        Assert.True(items.IndexOf(Label(MenuIds.AircraftCommand)) < note, string.Join(" | ", items));
        Assert.True(track > note + 2, $"no quick commands between the header and Track in: {string.Join(" | ", items)}");
        Assert.Equal(
            ["Track", "Data Block", "Squawk", ViewItem, Label(MenuIds.FavoritesMenu), AllCommands, "---", Label(MenuIds.SimControlDelete)],
            items[track..]
        );
    }

    [AvaloniaFact]
    public void AirborneIfr_AskPilotCoordinationAndSimControlLiveUnderAllCommands()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        var host = new RecordingMenuHost("");
        AircraftModel ac = Fixture("ifr-enroute");
        ac.AssumedFromLiveTraffic = true;

        ContextMenu menu = Build(ac, host, _ => []);
        List<string> top = Sequence(menu);
        List<string> all = Sequence(AllCommandsItem(menu));

        string[] moved = ["Ask pilot to say…", "Coordination", Label(MenuIds.SimControlWarp), Label(MenuIds.LiveTrafficUnassume)];
        foreach (string label in moved)
        {
            Assert.DoesNotContain(label, top);
            Assert.Contains(label, all);
        }

        Assert.Equal([Label(MenuIds.SimControlWarp), Label(MenuIds.LiveTrafficUnassume)], all[^2..]);
    }

    [AvaloniaFact]
    public void UnknownSituation_ShowsNoQuickCommandsAndNoStrip()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        var host = new RecordingMenuHost("");
        AircraftModel ac = Fixture("ifr-enroute");
        ac.Situation = AircraftSituation.Unknown;

        ContextMenu menu = Build(ac, host, _ => []);
        List<string> items = Sequence(menu);

        int track = items.IndexOf("Track");
        Assert.Equal([Label(MenuIds.AircraftCommand), Label(MenuIds.AircraftNote), "---", "Track"], items[(track - 3)..(track + 1)]);
        Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), item => item.Header is not string);
        Assert.Contains(AllCommands, items);
    }

    [AvaloniaFact]
    public void GroundAircraft_OffersTheGroundBlockUnderAllCommands()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        var host = new RecordingMenuHost("");
        host.GroundTraffic.Add("SWA602");
        host.PushbackToChoices.Add(new MenuCommandChoice("Gate 26", "PUSH 26", null, []));
        host.PresetTaxiChoices.Add(new MenuCommandChoice("Via B", "TAXI B 30", null, []));

        List<string> atParking = Sequence(AllCommandsItem(Build(Fixture("at-parking"), host, _ => [])));
        AssertBeforeCoordination(atParking, Label(MenuIds.GroundPushback), Label(MenuIds.GroundPushbackTo), Label(MenuIds.GroundFollow));

        List<string> taxiing = Sequence(AllCommandsItem(Build(Fixture("taxiing"), host, _ => [])));
        AssertBeforeCoordination(taxiing, Label(MenuIds.GroundHoldPosition), Label(MenuIds.GroundBreakConflict), Label(MenuIds.GroundTaxiPreset));
    }

    [AvaloniaFact]
    public void GroundAircraft_OffersDrawTaxiRouteAndPushRouteUnderAllCommands()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        var host = new RecordingMenuHost("");
        host.PushbackToChoices.Add(new MenuCommandChoice("Gate 26", "PUSH 26", null, []));
        host.PresetTaxiChoices.Add(new MenuCommandChoice("Via B", "TAXI B 30", null, []));

        List<string> atParking = Sequence(AllCommandsItem(Build(Fixture("at-parking"), host, _ => [])));
        Assert.True(
            atParking.IndexOf(Label(MenuIds.GroundPushRoute)) == atParking.IndexOf(Label(MenuIds.GroundPushbackTo)) + 1,
            $"Push route… should follow Push back to… in: {string.Join(" | ", atParking)}"
        );

        List<string> taxiing = Sequence(AllCommandsItem(Build(Fixture("taxiing"), host, _ => [])));
        Assert.True(
            taxiing.IndexOf(Label(MenuIds.GroundDrawTaxiRoute)) == taxiing.IndexOf(Label(MenuIds.GroundTaxiPreset)) + 1,
            $"Draw taxi route… should follow Preset taxi route in: {string.Join(" | ", taxiing)}"
        );
        Assert.DoesNotContain(Label(MenuIds.GroundPushRoute), taxiing);
    }

    [AvaloniaFact]
    public void GroundAircraft_AllCommandsLeavesOutTheFlightGroups()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        var host = new RecordingMenuHost("");

        List<string> taxiing = Sequence(AllCommandsItem(Build(Fixture("taxiing"), host, _ => [])));

        Assert.DoesNotContain(taxiing, item => item.StartsWith("Heading", StringComparison.Ordinal));
        Assert.DoesNotContain(taxiing, item => item.StartsWith("Altitude", StringComparison.Ordinal));
        Assert.DoesNotContain("Pattern", taxiing);
    }

    [AvaloniaFact]
    public void AirborneIfr_AllCommandsListsTheGroupsInOneFixedOrder()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        var host = new RecordingMenuHost("");

        List<string> all = Sequence(AllCommandsItem(Build(Fixture("approach-ifr"), host, _ => [])));

        string[] heads = ["Heading", "Altitude", "Speed", "Navigation", "Hold", "Approach", "Procedures", "Tower"];
        List<int> positions = [.. heads.Select(head => all.FindIndex(item => item.StartsWith(head, StringComparison.Ordinal)))];
        Assert.DoesNotContain(-1, positions);
        Assert.Equal([.. positions.Order()], positions);
    }

    [AvaloniaFact]
    public void ViewSection_SitsAfterSquawkBeforeFavorites()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        var host = new RecordingMenuHost("");

        AircraftModel ac = Fixture("ifr-enroute");

        List<string> items = Sequence(Build(ac, host, _ => [new MenuItem { Header = ViewItem }]));

        int view = items.IndexOf(ViewItem);
        Assert.Equal(["Squawk", ViewItem, Label(MenuIds.FavoritesMenu)], items[(view - 1)..(view + 2)]);
        Assert.Equal(Label(MenuIds.SimControlDelete), items[^1]);
        Assert.Equal([ac.Callsign], Assert.Single(host.RpoRequests));
    }

    [AvaloniaFact]
    public void DelayedSpawn_SkipsTheViewSection()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        var host = new RecordingMenuHost("");
        bool sectionBuilt = false;

        List<string> items = Sequence(
            Build(
                MenuGoldenFixtures.For(MenuView.List).Single(f => f.Name == "delayed-spawn").Aircraft,
                host,
                _ =>
                {
                    sectionBuilt = true;
                    return [new MenuItem { Header = ViewItem }];
                }
            )
        );

        Assert.False(sectionBuilt);
        Assert.Empty(host.RpoRequests);
        Assert.DoesNotContain(ViewItem, items);
        Assert.DoesNotContain("Track", items);
        Assert.Equal(Label(MenuIds.SimControlDelete), items[^1]);
        Assert.Contains(Label(MenuIds.SpawnNow), items);
    }

    [AvaloniaFact]
    public void QuickList_TextApproachEntryWithADefault_HasItsOtherPickerDirectlyUnderItsLeaf()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel ac = Fixture("ifr-arrival");
        ac.AssignedRunway = "30";

        List<string> items = Sequence(Build(ac, new RecordingMenuHost(""), _ => []));

        int leaf = items.IndexOf("Expect ILS 30");
        Assert.True(leaf >= 0, string.Join(" | ", items));
        Assert.Equal("Expect approach (other)…", items[leaf + 1]);
    }

    [AvaloniaFact]
    public void QuickList_ApproachEntryWithoutADefault_IsTheGroupedPickerAlone()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel ac = Fixture("ifr-arrival");
        ac.AssignedRunway = "";

        ContextMenu menu = Build(ac, new RecordingMenuHost(""), _ => []);

        MenuItem expect = menu.Items.OfType<MenuItem>().Single(item => (item.Header as string) == Label(MenuIds.ApproachExpect));
        Assert.Equal(MenuPickerDescriptor.Grouped, Assert.IsType<MenuPickerDescriptor>(expect.Tag).Kind);
        Assert.DoesNotContain("Expect approach (other)…", Sequence(menu));
    }

    [AvaloniaFact]
    public void QuickList_StripApproachEntryWithADefault_HasItsOtherPickerFirstUnderTheStrip()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());

        ContextMenu menu = Build(Fixture("approach-ifr"), new RecordingMenuHost(""), _ => []);

        MenuItem strip = Assert.Single(menu.Items.OfType<MenuItem>(), QuickCommandStrip.IsStrip);
        Assert.Contains(MenuIds.ApproachCleared, QuickCommandStrip.Buttons(strip).Select(button => (string)button.Tag!));
        int at = menu.Items.IndexOf(strip);
        Assert.Equal("Cleared approach (other)…", (menu.Items[at + 1] as MenuItem)?.Header as string);
        Assert.Single(Sequence(menu), header => header == "Cleared approach (other)…");
    }

    [AvaloniaFact]
    public void RadarMenu_WithOtherSelected_ForSectionSitsUnderHeader()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        MenuFixture fixture = RelativeFixture();

        List<string> items = Sequence(BuildWithSelection(fixture, new RecordingMenuHost("")));

        int note = items.IndexOf(Label(MenuIds.AircraftNote));
        Assert.Equal(
            ["---", "For AAL602 (selected)", "AAL601 is at its 12 o'clock, 6 nm, 1,500 ft below", "Report in sight", "Follow", "---", "For AAL601"],
            items[(note + 1)..(note + 8)]
        );
    }

    [AvaloniaFact]
    public void RadarMenu_ForSectionSendsAsTheSelectedAircraft()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        MenuFixture fixture = RelativeFixture();
        var host = new RecordingMenuHost("");

        ContextMenu menu = BuildWithSelection(fixture, host);
        foreach (string header in (string[])["Report in sight", "Follow"])
        {
            menu.Items.OfType<MenuItem>().Single(item => (item.Header as string) == header).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        }

        Assert.Equal([("AAL602", "RTIS AAL601", "AB"), ("AAL602", "FOLLOW AAL601", "AB")], host.Sent);
    }

    [AvaloniaFact]
    public void RadarMenu_FollowOnlyAfterReportedInSight()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        MenuFixture fixture = RelativeFixture();
        fixture.Selected!.LastReportedTrafficCallsign = null;

        List<string> items = Sequence(BuildWithSelection(fixture, new RecordingMenuHost("")));

        Assert.Contains("Report in sight", items);
        Assert.DoesNotContain("Follow", items);
    }

    [AvaloniaFact]
    public void RadarMenu_AllCommandsHoldsNoRelativeItemsForAirbornePair()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        MenuFixture fixture = RelativeFixture();

        List<string> all = Sequence(AllCommandsItem(BuildWithSelection(fixture, new RecordingMenuHost(""))));

        Assert.DoesNotContain(all, item => item.Contains("AAL602", StringComparison.Ordinal));
        Assert.DoesNotContain("Report in sight", all);
        Assert.DoesNotContain("Follow", all);
    }

    [AvaloniaFact]
    public void RadarMenu_MixedPair_HasNoForSection()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        MenuFixture fixture = RelativeFixture();
        fixture.Selected!.IsOnGround = true;

        List<string> items = Sequence(BuildWithSelection(fixture, new RecordingMenuHost("")));

        Assert.DoesNotContain(items, item => item.StartsWith("For ", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void ListMenu_NoPreviousSelection_HasNoForSection()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        MenuFixture fixture = RelativeFixture();

        List<string> items = Sequence(Build(fixture.Aircraft, new RecordingMenuHost(""), _ => []));

        Assert.DoesNotContain(items, item => item.StartsWith("For ", StringComparison.Ordinal));
    }

    /// <summary>
    /// The radar relative-selection fixture: AAL601 right-clicked with AAL602 selected, both airborne, AAL602 having
    /// reported AAL601 in sight.
    /// </summary>
    private static MenuFixture RelativeFixture() => MenuGoldenFixtures.For(MenuView.Radar).Single(f => f.Name == "relative-selection");

    private static ContextMenu BuildWithSelection(MenuFixture fixture, RecordingMenuHost host) =>
        AircraftMenuBuilder.Build(fixture.Aircraft, new MenuClick(fixture.Aircraft.Callsign, fixture.Selected, null, []), host, _ => []);

    private static ContextMenu Build(AircraftModel ac, RecordingMenuHost host, Func<MenuContext, IReadOnlyList<Control>> section) =>
        AircraftMenuBuilder.Build(ac, new MenuClick(ac.Callsign, null, null, []), host, section);

    /// <summary>The radar golden fixture of that name: its aircraft, the same on every view.</summary>
    private static AircraftModel Fixture(string name) => MenuGoldenFixtures.For(MenuView.Radar).Single(f => f.Name == name).Aircraft;

    private static string Label(string id) => MenuCatalog.Get(id).Label;

    /// <summary>The top-level All Commands submenu.</summary>
    private static MenuItem AllCommandsItem(ContextMenu menu) => menu.Items.OfType<MenuItem>().Single(item => (item.Header as string) == AllCommands);

    /// <summary>Each top-level item as its header text, with a separator written as <c>---</c>.</summary>
    private static List<string> Sequence(ContextMenu menu) => Sequence(menu.Items);

    /// <summary>Each of the submenu's items as its header text, with a separator written as <c>---</c>.</summary>
    private static List<string> Sequence(MenuItem submenu) => Sequence(submenu.Items);

    private static List<string> Sequence(ItemCollection items) =>
        [.. items.Select(item => item is Separator ? "---" : (item as MenuItem)?.Header as string ?? "")];

    /// <summary>Every one of <paramref name="labels"/> is an item listed before the Ask pilot and Coordination block.</summary>
    private static void AssertBeforeCoordination(List<string> items, params string[] labels)
    {
        int coordination = items.IndexOf("Coordination");
        Assert.True(coordination >= 0, string.Join(" | ", items));
        foreach (string label in labels)
        {
            int at = items.IndexOf(label);
            Assert.True((at >= 0) && (at < coordination), $"'{label}' is not above Coordination in: {string.Join(" | ", items)}");
        }
    }
}
