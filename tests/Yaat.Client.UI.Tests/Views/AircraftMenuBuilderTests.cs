using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Services;
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
    /// The main menu runs: the header, the strip, Command… and Note…, All Commands (ending with the view section),
    /// Favorites, then Delete.
    /// </summary>
    [AvaloniaFact]
    public void MainMenu_TopLevelRunsHeaderStripCommandNoteAllCommandsFavoritesThenTheFixedGroups()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());

        ContextMenu menu = Build(Fixture("final-ifr"), new RecordingMenuHost(""), _ => [new MenuItem { Header = ViewItem }]);
        List<string> items = Sequence(menu);

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
                Label(MenuIds.SimControlDelete),
            ],
            items
        );
        Assert.Equal(["---", ViewItem], Sequence(AllCommandsItem(menu))[^2..]);
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
                    ],
                SoloAdvisoryPrefill.TrueValues,
                false,
                CommandScheme.Default(),
                ShowQuickCommandIcons: true
            ),
        };

        ContextMenu menu = Build(ac, host, _ => []);

        Assert.Equal([MenuIds.CoordinationCheckReleaseWindow, MenuIds.GroundPushback], StripTags(menu));
        Assert.DoesNotContain(Label(MenuIds.CoordinationCheckReleaseWindow), Sequence(menu));
    }

    /// <summary>
    /// With the icons off the release row leads the text quick commands unlabelled, the catalog entries follow under their
    /// category label, and a custom entry comes last, after a separator and unlabelled.
    /// </summary>
    [AvaloniaFact]
    public void IconsOff_ReleaseLeads_CategoriesLabelled_CustomEntryLastUnlabelled()
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
                        new CustomQuickCommandEntry("Say altitude", "SALT", null, MenuFlightRules.Both),
                        new CatalogQuickCommandEntry(MenuIds.GroundPushback, null),
                        new CatalogQuickCommandEntry(MenuIds.CoordinationCheckReleaseWindow, null),
                    ],
                SoloAdvisoryPrefill.TrueValues,
                false,
                CommandScheme.Default(),
                ShowQuickCommandIcons: false
            ),
        };

        ContextMenu menu = Build(ac, host, _ => []);
        List<string> items = Sequence(menu);

        int command = items.IndexOf(Label(MenuIds.AircraftCommand));
        Assert.Equal(
            [
                "---",
                Label(MenuIds.CoordinationCheckReleaseWindow),
                MenuCategories.Label(MenuCategory.Taxi),
                Label(MenuIds.GroundPushback),
                "---",
                "Say altitude",
                "---",
            ],
            items[(command - 7)..command]
        );
        Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), QuickCommandStrip.IsStrip);
        MenuItem custom = menu.Items.OfType<MenuItem>().Single(item => (item.Header as string) == "Say altitude");
        Assert.True(custom.IsEnabled);
    }

    /// <summary>
    /// A custom quick entry's row builds, icons on or off, and shows as its alias the verb its click would send for the
    /// aircraft as the menu is built: the ground text on the ground, the air text in the air.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("at-parking", true, "TAXI")]
    [InlineData("at-parking", false, "TAXI")]
    [InlineData("ifr-enroute", true, "SALT")]
    [InlineData("ifr-enroute", false, "SALT")]
    public void CustomQuickEntry_Builds_AndShowsTheVerbItWouldSend(string fixture, bool showIcons, string verb)
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        var host = new RecordingMenuHost("")
        {
            Session = new MenuSession(
                "AB",
                false,
                VfrCommandsForIfr.None,
                _ => [new CustomQuickCommandEntry("Mine", "SALT", "TAXI A", MenuFlightRules.Both)],
                SoloAdvisoryPrefill.TrueValues,
                false,
                CommandScheme.Default(),
                showIcons
            ),
        };

        ContextMenu menu = Build(Fixture(fixture), host, _ => []);

        MenuItem custom = menu.Items.OfType<MenuItem>().Single(item => (item.Header as string) == "Mine");
        Assert.Equal(MenuAliases.Display([verb], CommandScheme.Default()), MenuAlias.GetText(custom));
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

        ContextMenu menu = Build(ac, new RecordingMenuHost(""), _ => [new MenuItem { Header = ViewItem }]);
        List<string> items = Sequence(menu);

        Assert.Equal(
            [
                "UAL402 · B738",
                "---",
                Label(MenuIds.AircraftCommand),
                Label(MenuIds.AircraftNote),
                "---",
                Label(MenuIds.FavoritesMenu),
                AllCommands,
                "---",
                Label(MenuIds.SimControlDelete),
            ],
            items
        );
        Assert.Equal(["---", ViewItem], Sequence(AllCommandsItem(menu))[^2..]);
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
        List<string> grouped = Grouped(AllCommandsItem(menu));

        string[] moved = ["Ask pilot to say…", "Coordination", Label(MenuIds.SimControlWarp), Label(MenuIds.LiveTrafficUnassume)];
        foreach (string label in moved)
        {
            Assert.DoesNotContain(label, top);
            Assert.Contains(label, grouped);
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

        List<string> atParking = Grouped(AllCommandsItem(Build(Fixture("at-parking"), host, _ => [])));
        AssertBeforeCoordination(atParking, Label(MenuIds.GroundPushback), Label(MenuIds.GroundPushbackTo), Label(MenuIds.GroundFollow));

        List<string> taxiing = Grouped(AllCommandsItem(Build(Fixture("taxiing"), host, _ => [])));
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

        List<string> atParking = Grouped(AllCommandsItem(Build(Fixture("at-parking"), host, _ => [])));
        Assert.True(
            atParking.IndexOf(Label(MenuIds.GroundPushRoute)) == atParking.IndexOf(Label(MenuIds.GroundPushbackTo)) + 1,
            $"Push route… should follow Push back to… in: {string.Join(" | ", atParking)}"
        );

        List<string> taxiing = Grouped(AllCommandsItem(Build(Fixture("taxiing"), host, _ => [])));
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

        string[] heads = ["Runway", "Vectors", "Approach", "Pilot reports", "Track and coordination"];
        List<int> positions = [.. heads.Select(head => all.FindIndex(item => item.StartsWith(head, StringComparison.Ordinal)))];
        Assert.DoesNotContain(-1, positions);
        Assert.Equal([.. positions.Order()], positions);
    }

    [AvaloniaFact]
    public void ViewSection_SitsLastInAllCommands_FavoritesThenTheFoot()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        var host = new RecordingMenuHost("");

        AircraftModel ac = Fixture("ifr-enroute");

        ContextMenu menu = Build(ac, host, _ => [new MenuItem { Header = ViewItem }]);
        List<string> items = Sequence(menu);

        Assert.DoesNotContain(ViewItem, items);
        Assert.Equal(["Warp…", "---", ViewItem], Sequence(AllCommandsItem(menu))[^3..]);
        Assert.Equal([AllCommands, Label(MenuIds.FavoritesMenu), "---", Label(MenuIds.SimControlDelete)], items[^4..]);
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
    public void QuickList_JoinApproach_IsAStripButton_WithNoOtherPickerBelow()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel ac = Fixture("ifr-arrival");
        ac.AssignedRunway = "30";
        Assert.NotNull(QuickCommandGlyphs.For(MenuIds.ApproachJoin));

        ContextMenu menu = Build(ac, JoinApproachHost(), _ => []);

        Assert.Contains(MenuIds.ApproachJoin, StripTags(menu));
        List<string> items = Sequence(menu);
        Assert.DoesNotContain("Join ILS 30", items);
        Assert.DoesNotContain("Join approach (other)…", items);
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

        List<string> all = Grouped(AllCommandsItem(BuildWithSelection(fixture, new RecordingMenuHost(""))));

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

    /// <summary>An airborne IFR aircraft's All Commands lists its task groups in category order, then the sim control.</summary>
    [AvaloniaFact]
    public void AirborneIfr_AllCommandsListsTheTaskGroupsInCategoryOrder()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());

        List<string> all = Sequence(AllCommandsItem(Build(Fixture("ifr-enroute"), new RecordingMenuHost(""), _ => [])));

        Assert.Equal(["Vectors", "Approach", "Pilot reports", "Track and coordination", "---", Label(MenuIds.SimControlWarp)], all);
    }

    /// <summary>A taxiing aircraft's All Commands holds the runway, taxi, pilot-report and track groups, and no Vectors.</summary>
    [AvaloniaFact]
    public void Taxiing_AllCommandsHoldsTheGroundTaskGroups_AndNoVectors()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());

        List<string> all = Sequence(AllCommandsItem(Build(Fixture("taxiing"), new RecordingMenuHost(""), _ => [])));

        foreach (string group in (string[])["Runway", "Taxi", "Pilot reports", "Track and coordination"])
        {
            Assert.Contains(group, all);
        }

        Assert.DoesNotContain("Vectors", all);
    }

    /// <summary>Track, Data Block and Squawk live under All Commands › Track and coordination, not at the root.</summary>
    [AvaloniaFact]
    public void Root_HoldsNoTrackDataBlockOrSquawk()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());

        List<string> root = Sequence(Build(Fixture("ifr-enroute"), new RecordingMenuHost(""), _ => []));

        Assert.DoesNotContain("Track", root);
        Assert.DoesNotContain("Data Block", root);
        Assert.DoesNotContain("Squawk", root);
    }

    /// <summary>Vectors' Route block and Pattern's closed-traffic rows are commands the category map files under that group.</summary>
    [AvaloniaFact]
    public void CategoryGroups_RouteAndClosedTrafficRows_MapBackToTheirCategory()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        var host = new RecordingMenuHost("");

        MenuItem vectors = Child(AllCommandsItem(Build(Fixture("ifr-enroute"), host, _ => [])), "Vectors");
        List<string> route =
        [
            .. Sequence(vectors)
                .SkipWhile(label => !label.StartsWith("Route", StringComparison.Ordinal))
                .Skip(1)
                .Where(label => label is not ("" or "---")),
        ];
        Assert.Contains(Label(MenuIds.NavigationOnCourse), route);
        Assert.All(route, label => Assert.Equal(MenuCategory.Vectors, CategoryOfLabel(label)));

        MenuItem pattern = Child(AllCommandsItem(Build(Fixture("pattern-vfr"), host, _ => [])), "Pattern");
        foreach (string id in (string[])[MenuIds.PatternMakeLeftTraffic, MenuIds.PatternMakeRightTraffic])
        {
            Assert.Contains(Label(id), Sequence(pattern));
            Assert.Equal(MenuCategory.Pattern, CategoryOfLabel(Label(id)));
        }
    }

    /// <summary>The submenu of <paramref name="parent"/> headed <paramref name="header"/>.</summary>
    private static MenuItem Child(MenuItem parent, string header) =>
        parent.Items.OfType<MenuItem>().Single(item => (item.Header as string) == header);

    /// <summary>The category of the one catalog entry, point items aside, labelled <paramref name="label"/>.</summary>
    private static MenuCategory? CategoryOfLabel(string label) =>
        MenuCategories.Of(
            Assert.Single(MenuCatalog.All, entry => (entry.Label == label) && !entry.Id.StartsWith("point.", StringComparison.Ordinal)).Id
        );

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

    /// <summary>All Commands' own items as <see cref="Sequence(MenuItem)"/> lists them, then each of its groups' items, group by group.</summary>
    private static List<string> Grouped(MenuItem allCommands) =>
        [.. Sequence(allCommands), .. allCommands.Items.OfType<MenuItem>().SelectMany(group => Sequence(group))];

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
    /// A host whose quick list is Join approach alone: an approach flyout entry with its own glyph, so a strip button in
    /// every situation.
    /// </summary>
    private static RecordingMenuHost JoinApproachHost() =>
        new("")
        {
            Session = new MenuSession(
                "AB",
                false,
                VfrCommandsForIfr.EnterFinalOnly,
                _ => [new CatalogQuickCommandEntry(MenuIds.ApproachJoin, null)],
                SoloAdvisoryPrefill.TrueValues,
                false,
                CommandScheme.Default(),
                ShowQuickCommandIcons: true
            ),
        };
}
