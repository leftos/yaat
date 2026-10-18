using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Situation;

namespace Yaat.Client.UI.Tests.Views;

// The one view-independent aircraft menu builder: its tree from the aircraft, the click and the host, with the view's
// own section placed where the builder puts it.
public class AircraftMenuBuilderTests
{
    private const string ViewItem = "View item";
    private const string AllCommands = "All Commands";
    private const string StripRow = "[strip]";

    /// <summary>
    /// The main menu runs: the header, the strip, Command… and Note…, All Commands, Favorites, then Track, Data Block,
    /// Squawk, the view section and Delete.
    /// </summary>
    [AvaloniaFact]
    public void MainMenu_TopLevelRunsHeaderStripCommandNoteAllCommandsFavoritesThenTheFixedGroups()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());

        List<string> items = Sequence(Build(Fixture("final-ifr"), new RecordingMenuHost(""), _ => [new MenuItem { Header = ViewItem }]));

        Assert.Equal(
            [
                "AAL207 · B738",
                "Final · 1,500 ft · 180 kt · KOAK rwy 30",
                "---",
                StripRow,
                "---",
                Label(MenuIds.AircraftCommand),
                Label(MenuIds.AircraftNote),
                AllCommands,
                Label(MenuIds.FavoritesMenu),
                "---",
                "Track",
                "Data Block",
                "Squawk",
                ViewItem,
                "---",
                Label(MenuIds.SimControlDelete),
            ],
            items
        );
    }

    /// <summary>A held departure's Release (HFR) and a call-for-release window's Check release window lead the strip and leave the header.</summary>
    [AvaloniaTheory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ReleaseItems_LeadTheStrip_NotTheHeader(bool held, bool hasWindow)
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel ac = Fixture("taxiing");
        ac.IsHeldForRelease = held;
        ac.CfrWindowStartUtc = hasWindow ? new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc) : null;
        List<string> release =
        [
            .. ((string[])[MenuIds.CoordinationReleaseHeld, MenuIds.CoordinationCheckReleaseWindow]).Where((_, i) => (i == 0) ? held : hasWindow),
        ];

        ContextMenu menu = Build(ac, new RecordingMenuHost(""), _ => []);

        List<string> tags = StripTags(menu);
        Assert.Equal(release, tags[..release.Count]);
        // With no other ground traffic, Follow stays in the strip, faded.
        Assert.Equal([MenuIds.GroundHoldPosition, MenuIds.GroundFollow, MenuIds.GroundBreakConflict], tags[release.Count..]);
        Assert.Equal(["SWA104 · B738", "Taxiing", "---", StripRow], Sequence(menu)[..4]);
        Assert.DoesNotContain(Label(MenuIds.CoordinationReleaseHeld), Sequence(menu));
        Assert.DoesNotContain(Label(MenuIds.CoordinationCheckReleaseWindow), Sequence(menu));
    }

    [AvaloniaFact]
    public void ReleaseItems_AbsentWhenNeitherApplies()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());

        ContextMenu menu = Build(Fixture("taxiing"), new RecordingMenuHost(""), _ => []);

        // With no other ground traffic, Follow stays in the strip, faded.
        Assert.Equal([MenuIds.GroundHoldPosition, MenuIds.GroundFollow, MenuIds.GroundBreakConflict], StripTags(menu));
    }

    /// <summary>An aircraft with no strip of its own still gets one holding just the release icons that apply.</summary>
    [AvaloniaFact]
    public void ReleaseItems_GiveAStripToAnAircraftWithoutOne()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel ac = Fixture("taxiing");
        ac.Situation = AircraftSituation.Unknown;
        ac.IsHeldForRelease = true;

        ContextMenu menu = Build(ac, new RecordingMenuHost(""), _ => []);

        Assert.Equal([MenuIds.CoordinationReleaseHeld], StripTags(menu));
    }

    /// <summary>Check release window in the situation's own list shows once, first in the strip.</summary>
    [AvaloniaFact]
    public void ReleaseItems_InTheSituationsListShowOnceFirst()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel ac = Fixture("at-parking");
        ac.CfrWindowStartUtc = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
        var host = new RecordingMenuHost("")
        {
            Session = new MenuSession(
                "AB",
                false,
                VfrCommandsForIfr.None,
                _ =>
                    [
                        new CatalogQuickCommandEntry(MenuIds.GroundPushback, null),
                        new CatalogQuickCommandEntry(MenuIds.CoordinationCheckReleaseWindow, null),
                    ]
            ),
        };

        ContextMenu menu = Build(ac, host, _ => []);

        Assert.Equal([MenuIds.CoordinationCheckReleaseWindow, MenuIds.GroundPushback], StripTags(menu));
        Assert.DoesNotContain(Label(MenuIds.CoordinationCheckReleaseWindow), Sequence(menu));
    }

    [AvaloniaFact]
    public void DelayedSpawn_KeepsItsOrder()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel ac = MenuGoldenFixtures.For(MenuView.Radar).Single(f => f.Name == "delayed-spawn").Aircraft;

        List<string> items = Sequence(Build(ac, new RecordingMenuHost(""), _ => []));

        Assert.Equal(
            [
                "AAL501 · B738",
                "16,000 ft",
                "---",
                Label(MenuIds.AircraftCommand),
                Label(MenuIds.AircraftNote),
                "---",
                Label(MenuIds.FavoritesMenu),
                "---",
                Label(MenuIds.SpawnNow),
                "Change spawn delay",
                Label(MenuIds.SimControlDelete),
            ],
            items
        );
    }

    /// <summary>A held delayed spawn's Release (HFR) is a strip under the header, as on the aircraft menu, not a text row.</summary>
    [AvaloniaFact]
    public void DelayedSpawn_HeldForRelease_GetsAStripOfTheReleaseIconUnderTheHeader()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel ac = MenuGoldenFixtures.For(MenuView.Radar).Single(f => f.Name == "delayed-spawn-held").Aircraft;

        ContextMenu menu = Build(ac, new RecordingMenuHost(""), _ => []);

        Assert.Equal([MenuIds.CoordinationReleaseHeld], StripTags(menu));
        Assert.Equal(
            [
                "AAL502 · B738",
                "16,000 ft",
                "---",
                StripRow,
                "---",
                Label(MenuIds.AircraftCommand),
                Label(MenuIds.AircraftNote),
                "---",
                Label(MenuIds.FavoritesMenu),
                "---",
                Label(MenuIds.SpawnNow),
                "Change spawn delay",
                Label(MenuIds.SimControlDelete),
            ],
            Sequence(menu)
        );
    }

    [AvaloniaFact]
    public void SurfaceShadow_KeepsItsOrder()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel ac = MenuGoldenFixtures.For(MenuView.Radar).Single(f => f.Name == "live-traffic-surface").Aircraft;

        List<string> items = Sequence(Build(ac, new RecordingMenuHost(""), _ => [new MenuItem { Header = ViewItem }]));

        Assert.Equal(
            [
                "UAL402 · B738",
                "---",
                Label(MenuIds.AircraftCommand),
                Label(MenuIds.AircraftNote),
                "---",
                "Track",
                "Data Block",
                ViewItem,
                Label(MenuIds.FavoritesMenu),
                AllCommands,
                "---",
                Label(MenuIds.SimControlDelete),
            ],
            items
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

        Assert.Equal(
            [
                "AAL202 · B738",
                "33,000 ft · 280 kt",
                "---",
                Label(MenuIds.AircraftCommand),
                Label(MenuIds.AircraftNote),
                AllCommands,
                Label(MenuIds.FavoritesMenu),
                "---",
                "Track",
                "Data Block",
                "Squawk",
                "---",
                Label(MenuIds.SimControlDelete),
            ],
            items
        );
        Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), item => item.Header is not string);
        Assert.Contains(AllCommands, items);
    }

    [AvaloniaFact]
    public void GroundAircraft_OffersTheGroundBlockUnderAllCommands()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        var host = new RecordingMenuHost("");
        host.GroundTraffic.Add(RecordingMenuHost.ParkedRow("SWA602"));
        host.PushbackTargets = PushTargetList.Ready([new MenuPushTarget(MenuPushTargetKind.Taxilane, "TE", null, 140, "PUSH TE", [], null)]);
        host.PresetTaxiChoices.Add(RecordingMenuHost.PresetRow("Via B", "TAXI B 30"));

        List<string> atParking = Sequence(AllCommandsItem(Build(Fixture("at-parking"), host, _ => [])));
        AssertBeforeCoordination(atParking, Label(MenuIds.GroundPushback), Label(MenuIds.GroundPushbackTo), Label(MenuIds.GroundFollow));

        List<string> taxiing = Sequence(AllCommandsItem(Build(Fixture("taxiing"), host, _ => [])));
        AssertBeforeCoordination(taxiing, Label(MenuIds.GroundHoldPosition), Label(MenuIds.GroundBreakConflict), Label(MenuIds.GroundTaxiPreset));
    }

    [AvaloniaFact]
    public void GroundAircraft_OffersDrawTaxiRouteAndPushRouteUnderAllCommands()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        var host = new RecordingMenuHost("")
        {
            PushbackTargets = PushTargetList.Ready([new MenuPushTarget(MenuPushTargetKind.Taxilane, "TE", null, 140, "PUSH TE", [], null)]),
        };
        host.PresetTaxiChoices.Add(RecordingMenuHost.PresetRow("Via B", "TAXI B 30"));

        List<string> atParking = Sequence(AllCommandsItem(Build(Fixture("at-parking"), host, _ => [])));
        Assert.True(
            atParking.IndexOf(Label(MenuIds.GroundPushRoute)) == atParking.IndexOf(Label(MenuIds.GroundPushbackTo)) + 1,
            $"Push route… should follow Push back to… in: {string.Join(" | ", atParking)}"
        );

        List<string> taxiing = Sequence(AllCommandsItem(Build(Fixture("taxiing"), host, _ => [])));
        Assert.True(
            taxiing.IndexOf(Label(MenuIds.GroundTaxiPreset)) == taxiing.IndexOf(Label(MenuIds.GroundTaxiToRunway)) + 1,
            $"Preset taxi route should follow Taxi to runway in: {string.Join(" | ", taxiing)}"
        );
        Assert.True(
            taxiing.IndexOf(Label(MenuIds.GroundDrawTaxiRoute)) > taxiing.IndexOf(Label(MenuIds.GroundTaxiPreset)),
            $"Draw taxi route… should come after Preset taxi route in: {string.Join(" | ", taxiing)}"
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
    public void GroundAircraftWithNoPhase_AllCommandsLeavesOutTheFlightGroups()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        var host = new RecordingMenuHost("");
        AircraftModel ac = Fixture("taxiing");
        ac.CurrentPhase = "";
        ac.IsOnGround = true;

        List<string> all = Sequence(AllCommandsItem(Build(ac, host, _ => [])));

        Assert.DoesNotContain(all, item => item.StartsWith("Heading", StringComparison.Ordinal));
        Assert.DoesNotContain(all, item => item.StartsWith("Altitude", StringComparison.Ordinal));
        Assert.DoesNotContain("Pattern", all);
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
    public void ViewSection_SitsAfterSquawkBeforeTheFoot()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        var host = new RecordingMenuHost("");

        AircraftModel ac = Fixture("ifr-enroute");

        List<string> items = Sequence(Build(ac, host, _ => [new MenuItem { Header = ViewItem }]));

        int view = items.IndexOf(ViewItem);
        Assert.Equal(["Squawk", ViewItem, "---"], items[(view - 1)..(view + 2)]);
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

        List<string> items = Sequence(Build(ac, JoinApproachHost(), _ => []));

        int leaf = items.IndexOf("Join ILS 30");
        Assert.True(leaf >= 0, string.Join(" | ", items));
        Assert.Equal("Join approach (other)…", items[leaf + 1]);
    }

    [AvaloniaFact]
    public void QuickList_ApproachEntryWithoutADefault_IsTheGroupedPickerAlone()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel ac = Fixture("ifr-arrival");
        ac.AssignedRunway = "";
        Assert.Null(QuickCommandGlyphs.For(MenuIds.ApproachJoin));

        ContextMenu menu = Build(ac, JoinApproachHost(), _ => []);

        MenuItem join = menu.Items.OfType<MenuItem>().Single(item => (item.Header as string) == Label(MenuIds.ApproachJoin));
        Assert.Equal(MenuPickerDescriptor.Grouped, Assert.IsType<MenuPickerDescriptor>(join.Tag).Kind);
        Assert.DoesNotContain("Join approach (other)…", Sequence(menu));
    }

    [AvaloniaFact]
    public void QuickList_StripApproachEntry_HasNoOtherPickerUnderTheStrip()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());

        ContextMenu menu = Build(Fixture("approach-ifr"), new RecordingMenuHost(""), _ => []);

        MenuItem strip = Assert.Single(menu.Items.OfType<MenuItem>(), QuickCommandStrip.IsStrip);
        Assert.Contains(MenuIds.ApproachCleared, QuickCommandStrip.Buttons(strip).Select(button => (string)button.Tag!));
        Assert.DoesNotContain("Cleared approach (other)…", Sequence(menu));
    }

    [AvaloniaFact]
    public void RadarMenu_WithOtherSelected_ForSectionSitsUnderHeader()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        MenuFixture fixture = RelativeFixture();

        List<string> items = Sequence(BuildWithSelection(fixture, new RecordingMenuHost("")));

        Assert.Equal(
            [
                "---",
                "For AAL602 (selected)",
                "AAL601 is at its 12 o'clock, 6 nm, 1,500 ft below",
                "Report in sight",
                "Follow",
                "---",
                "For AAL601",
                StripRow,
                "---",
                Label(MenuIds.AircraftCommand),
            ],
            items[2..12]
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

    /// <summary>Each item as its header text, with a separator written as <c>---</c> and the quick-command strip as <see cref="StripRow"/>.</summary>
    private static List<string> Sequence(ItemCollection items) =>
        [
            .. items.Select(item =>
                item switch
                {
                    Separator => "---",
                    MenuItem strip when QuickCommandStrip.IsStrip(strip) => StripRow,
                    MenuItem menuItem => menuItem.Header as string ?? "",
                    _ => "",
                }
            ),
        ];

    /// <summary>The catalog ids of the menu's strip buttons, in strip order.</summary>
    private static List<string> StripTags(ContextMenu menu) =>
        [.. QuickCommandStrip.Buttons(Assert.Single(menu.Items.OfType<MenuItem>(), QuickCommandStrip.IsStrip)).Select(button => (string)button.Tag!)];

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

    /// <summary>
    /// A host whose quick list is Join approach alone: an approach picker with no glyph, so it lists as a text entry
    /// below the strip in every situation.
    /// </summary>
    private static RecordingMenuHost JoinApproachHost() =>
        new("")
        {
            Session = new MenuSession("AB", false, VfrCommandsForIfr.EnterFinalOnly, _ => [new CatalogQuickCommandEntry(MenuIds.ApproachJoin, null)]),
        };
}
