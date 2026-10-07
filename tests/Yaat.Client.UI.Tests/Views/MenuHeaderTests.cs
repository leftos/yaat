using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Client.Views.Ground;
using Yaat.Client.Views.Radar;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The header every aircraft menu opens with (<see cref="SharedMenuGroups.AddHeader"/>): the bold title naming the
/// callsign and the filed type, the one-line state row under it, the route summary and hold status rows below that, the
/// release items (Release (HFR) and Check release window) where they apply, then the free-text Command… and Note…, which
/// ask the host to open its command and note flyouts.
/// </summary>
public class MenuHeaderTests
{
    private const string Callsign = "SWA104";
    private const string Initials = "AB";

    [AvaloniaFact]
    public void Title_IsCallsignAndFiledType_BoldAndDisabled()
    {
        AircraftModel ac = Jet();
        ac.FiledAircraftType = "B739";

        MenuItem title = Assert.IsType<MenuItem>(Header(ac, new RecordingMenuHost(""))[0]);

        Assert.Equal("SWA104 · B739", title.Header as string);
        Assert.False(title.IsEnabled);
        Assert.Equal(FontWeight.Bold, title.FontWeight);
    }

    [AvaloniaFact]
    public void Title_WithNoFiledType_ShowsTheAircraftType()
    {
        MenuItem title = Assert.IsType<MenuItem>(Header(Jet(), new RecordingMenuHost(""))[0]);

        Assert.Equal("SWA104 · B738", title.Header as string);
    }

    [AvaloniaFact]
    public void Title_WithNoAircraftModel_IsTheBareCallsign()
    {
        MenuItem title = Assert.IsType<MenuItem>(Header(null, new RecordingMenuHost(""))[0]);

        Assert.Equal(Callsign, title.Header as string);
    }

    [AvaloniaTheory]
    [InlineData("")]
    [InlineData("   ")]
    public void Title_WithABlankType_IsTheBareCallsign(string type)
    {
        AircraftModel ac = Jet();
        ac.AircraftType = type;
        ac.FiledAircraftType = type;

        MenuItem title = Assert.IsType<MenuItem>(Header(ac, new RecordingMenuHost(""))[0]);

        Assert.Equal(Callsign, title.Header as string);
    }

    // --- The state line, directly under the title ---------------------------------------------

    [AvaloniaFact]
    public void StateLine_Airborne_NamesThePhaseAltitudeSpeedAirportAndRunway()
    {
        AircraftModel ac = AirborneAircraft("ApproachNav", 2960, 180.4);
        ac.Destination = "KOAK";
        ac.AssignedRunway = "30";

        Assert.Equal("Approach · 3,000 ft · 180 kt · KOAK rwy 30", StateLine(ac));
    }

    [AvaloniaFact]
    public void StateLine_AirborneWithNoAirport_NamesOnlyTheRunway()
    {
        AircraftModel ac = AirborneAircraft("FinalApproach", 1500, 140);
        ac.AssignedRunway = "30";

        Assert.Equal("Final · 1,500 ft · 140 kt · rwy 30", StateLine(ac));
    }

    [AvaloniaFact]
    public void StateLine_AirborneWithDestinationAndNoRunway_LeavesOutTheAirportSegment()
    {
        AircraftModel ac = AirborneAircraft("Downwind", 1500, 90);
        ac.Destination = "KOAK";

        Assert.Equal("Downwind · 1,500 ft · 90 kt", StateLine(ac));
    }

    [AvaloniaTheory]
    [InlineData("Takeoff", "Takeoff")]
    [InlineData("Takeoff-H", "Takeoff")]
    [InlineData("InitialClimb", "Departure")]
    [InlineData("DepartureProcedure", "Departure")]
    public void StateLine_AirborneDeparting_UsesTheDepartureAirport(string phase, string display)
    {
        AircraftModel ac = AirborneAircraft(phase, 3000, 180);
        ac.Departure = "KOAK";
        ac.Destination = "KLAX";
        ac.AssignedRunway = "30";

        Assert.Equal($"{display} · 3,000 ft · 180 kt · KOAK rwy 30", StateLine(ac));
    }

    [AvaloniaFact]
    public void StateLine_Ground_NamesThePhaseSpotAndGroundAirport()
    {
        AircraftModel ac = GroundAircraft("Holding After Pushback");
        ac.ParkingSpot = "S/RAMP";
        ac.GroundAirportId = "KSAN";

        Assert.Equal("Holding after push · S/RAMP · KSAN", StateLine(ac));
    }

    [AvaloniaFact]
    public void StateLine_GroundWithTaxiwayAndSpot_PrefersTheTaxiway()
    {
        AircraftModel ac = GroundAircraft("Taxiing");
        ac.CurrentTaxiway = "W3";
        ac.ParkingSpot = "25";
        ac.GroundAirportId = "KOAK";

        Assert.Equal("Taxiing · W3 · KOAK", StateLine(ac));
    }

    [AvaloniaFact]
    public void StateLine_LandingRoll_NamesTheRunwayAndGroundSpeed()
    {
        AircraftModel ac = GroundAircraft("Landing");
        ac.AssignedRunway = "28R";
        ac.GroundSpeed = 62;

        Assert.Equal("Landing · runway 28R · 62 kt", StateLine(ac));
    }

    [AvaloniaFact]
    public void StateLine_HelicopterLandingRoll_NamesTheRunwayAndGroundSpeed()
    {
        AircraftModel ac = GroundAircraft("Landing-H");
        ac.AssignedRunway = "28R";
        ac.GroundSpeed = 40;

        Assert.Equal("Landing · runway 28R · 40 kt", StateLine(ac));
    }

    [AvaloniaFact]
    public void StateLine_AirborneZeroSpeed_LeavesOutTheSpeedSegment()
    {
        AircraftModel ac = AirborneAircraft("", 16000, 0);

        Assert.Equal("16,000 ft", StateLine(ac));
    }

    [AvaloniaFact]
    public void StateLine_AircraftWithNothingToShow_HasNoRow()
    {
        AircraftModel ac = AirborneAircraft("", 0, 0);

        Assert.Null(StateRow(ac));
    }

    [AvaloniaFact]
    public void StateLine_SitsDirectlyUnderTheTitle()
    {
        AircraftModel ac = GroundAircraft("Taxiing");
        ac.CurrentTaxiway = "W3";
        ac.GroundAirportId = "KOAK";

        List<string> items = [.. Header(ac, new RecordingMenuHost("")).Select(Describe)];

        Assert.Equal(["SWA104 · B738", "Taxiing · W3 · KOAK", "---", "Command…"], items[..4]);
    }

    [AvaloniaFact]
    public void Header_IsTitleStateHeldSeparatorCommandNoteSeparator()
    {
        AircraftModel ac = Jet();
        ac.HoldKind = "HoldPosition";

        List<string> items = [.. Header(ac, new RecordingMenuHost("")).Select(Describe)];

        Assert.Equal(["SWA104 · B738", "Taxiing", "Held: position", "---", "Command…", "Note…", "---"], items);
    }

    [AvaloniaFact]
    public void Command_AsksTheHostForItsCommandFlyout()
    {
        var host = new RecordingMenuHost("");

        Click(Item(Header(Jet(), host), "Command…"));

        Assert.Equal([(Callsign, Initials)], host.CommandFlyouts);
        Assert.Empty(host.Sent);
    }

    [AvaloniaFact]
    public async Task Note_AsksTheHostForItsNoteFlyout_PrefilledWithTheCurrentNote_AndSendsWhatItSubmits()
    {
        AircraftModel ac = Jet();
        ac.Note = "OLD NOTE";
        var host = new RecordingMenuHost("");

        Click(Item(Header(ac, host), "Note…"));

        (string callsign, string currentNote) = Assert.Single(host.NoteFlyouts);
        Assert.Equal((Callsign, "OLD NOTE"), (callsign, currentNote));
        Assert.NotNull(host.NoteSubmit);
        await host.NoteSubmit("NOTE NEW NOTE");
        Assert.Equal([(Callsign, "NOTE NEW NOTE", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void Note_WithNoAircraftModel_OpensAnEmptyNoteFlyout()
    {
        var host = new RecordingMenuHost("");

        Click(Item(Header(null, host), "Note…"));

        Assert.Equal([(Callsign, "")], host.NoteFlyouts);
    }

    /// <summary>
    /// Through the client host on the ground canvas: Note… opens the note popup there, prefilled with the current note
    /// and showing the 40-character limit, and hands back <c>NOTE {text}</c> for what the controller typed.
    /// </summary>
    [AvaloniaFact]
    public void ClientHost_Note_OpensThePrefilledPopupOnTheGround_AndHandsBackTheNoteCommand()
    {
        (MainViewModel main, GroundView view) = GroundOnMain();

        AssertNotePopup(new ClientMenuHost(main, Jet(), view.Canvas), view);
    }

    // --- Each host's two flyout members, over a real main view model -----------------------

    /// <summary>A VFR-only command typed for an IFR aircraft, which the VFR gate refuses under every setting but "All".</summary>
    private const string VfrOnlyCommand = "ELD 28L";

    [AvaloniaFact]
    public void ClientHost_Command_OpensOnTheRadarAndGoesThroughTheVfrGate()
    {
        (MainViewModel main, AircraftModel ac) = GatedMain();
        var view = new RadarView { DataContext = main.Radar };
        new Window { DataContext = main, Content = view }.ShowAndRunLayout();

        AssertCommandRefusedByTheGate(new ClientMenuHost(main, ac, view.Canvas), view, main);
    }

    /// <summary>
    /// A command the VFR gate allows goes on to the main view model's send. That send needs a server a test cannot reach,
    /// so the failure it shows in the status line proves the hand-off.
    /// </summary>
    [AvaloniaFact]
    public void ClientHost_Command_Allowed_ReachesTheSend()
    {
        (MainViewModel main, AircraftModel ac) = GatedMain();
        var view = new RadarView { DataContext = main.Radar };
        new Window { DataContext = main, Content = view }.ShowAndRunLayout();

        new ClientMenuHost(main, ac, view.Canvas).ShowCommandFlyout(Callsign, Initials);
        HeadlessWindowExtensions.PumpDispatcher();
        TextBox textBox = FindTextBox(view);
        textBox.Text = "FH 270";
        RaiseKey(textBox, Key.Enter);
        HeadlessWindowExtensions.PumpDispatcher();

        Assert.StartsWith("Command error:", main.StatusText);
    }

    [AvaloniaFact]
    public void ClientHost_Command_OpensOnTheGroundAndGoesThroughTheVfrGate()
    {
        (MainViewModel main, AircraftModel ac) = GatedMain();
        var view = new GroundView { DataContext = main.Ground };
        new Window { DataContext = main, Content = view }.ShowAndRunLayout();

        AssertCommandRefusedByTheGate(new ClientMenuHost(main, ac, view.Canvas), view, main);
    }

    [AvaloniaFact]
    public void ClientHost_Command_OpensOnTheMenuAnchorAndGoesThroughTheVfrGate()
    {
        (MainViewModel main, AircraftModel ac) = GatedMain();
        var anchor = new Border();
        new Window { Content = anchor }.ShowAndRunLayout();

        AssertCommandRefusedByTheGate(new ClientMenuHost(main, ac, anchor), anchor, main);
    }

    [AvaloniaFact]
    public void ClientHost_Note_OpensThePrefilledPopupOnTheRadar_AndHandsBackTheNoteCommand()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var view = new RadarView { DataContext = main.Radar };
        new Window { DataContext = main, Content = view }.ShowAndRunLayout();

        AssertNotePopup(new ClientMenuHost(main, Jet(), view.Canvas), view);
    }

    [AvaloniaFact]
    public void ClientHost_Note_OpensThePrefilledPopupOnTheMenuAnchor_AndHandsBackTheNoteCommand()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var anchor = new Border();
        new Window { Content = anchor }.ShowAndRunLayout();

        AssertNotePopup(new ClientMenuHost(main, Jet(), anchor), anchor);
    }

    /// <summary>
    /// A main view model holding the IFR jet the menu is on, on a setting that refuses <see cref="VfrOnlyCommand"/> for it.
    /// The setting is read, never written: it is a saved preference, and changing it would leak into every later test that
    /// builds a main view model (the radar goldens offer Pattern only under the default).
    /// </summary>
    private static (MainViewModel Main, AircraftModel Aircraft) GatedMain()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        Assert.NotEqual(VfrCommandsForIfr.All, main.VfrCommandsForIfr);
        AircraftModel ac = Jet();
        main.Aircraft.Add(ac);
        return (main, ac);
    }

    /// <summary>
    /// Opens the host's command popup, asserts it is on <paramref name="anchor"/>, types a VFR-only command for the IFR
    /// aircraft and asserts the main view model's VFR gate refused it.
    /// </summary>
    private static void AssertCommandRefusedByTheGate(IMenuHost host, Control anchor, MainViewModel main)
    {
        host.ShowCommandFlyout(Callsign, Initials);
        HeadlessWindowExtensions.PumpDispatcher();

        TextBox textBox = FindTextBox(anchor);
        textBox.Text = VfrOnlyCommand;
        RaiseKey(textBox, Key.Enter);
        HeadlessWindowExtensions.PumpDispatcher();

        Assert.Contains($"requires a VFR aircraft — use CIFR on {Callsign}", main.StatusText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Opens the host's note popup with a current note, asserts it is on <paramref name="anchor"/> and prefilled, and that
    /// it hands the finished <c>NOTE</c> command to the sink it was given.
    /// </summary>
    private static void AssertNotePopup(IMenuHost host, Control anchor)
    {
        var commands = new List<string>();
        host.ShowNoteFlyout(
            Callsign,
            "OLD NOTE",
            command =>
            {
                commands.Add(command);
                return Task.CompletedTask;
            }
        );
        HeadlessWindowExtensions.PumpDispatcher();

        TextBox textBox = FindTextBox(anchor);
        Assert.Equal("OLD NOTE", textBox.Text);
        Assert.Contains("max 40", textBox.PlaceholderText, StringComparison.Ordinal);
        textBox.Text = " NEW NOTE ";
        RaiseKey(textBox, Key.Enter);
        HeadlessWindowExtensions.PumpDispatcher();

        Assert.Equal(["NOTE NEW NOTE"], commands);
    }

    // --- The views ----------------------------------------------------------------------------

    [AvaloniaFact]
    public void GroundMenu_StartsWithTheSharedHeader_FavoritesBeforeAllCommands() =>
        AssertStartsWithHeaderFavoritesBeforeAllCommands(MenuView.Ground);

    [AvaloniaFact]
    public void ListMenu_StartsWithTheSharedHeader_FavoritesBeforeAllCommands() => AssertStartsWithHeaderFavoritesBeforeAllCommands(MenuView.List);

    private void AssertStartsWithHeaderFavoritesBeforeAllCommands(MenuView view)
    {
        List<string> items = TopLevel(view, "taxiing", out ContextMenu _);

        Assert.Equal(["SWA104 · B738", "Taxiing", "---", "Command…", "Note…", "---"], items[..6]);
        AssertFavoritesBeforeAllCommands(items);
    }

    /// <summary>Favorites sits once at the top level, straight before All Commands.</summary>
    private static void AssertFavoritesBeforeAllCommands(List<string> items)
    {
        int favorites = items.IndexOf("Favorite Commands");
        Assert.Single(items, i => i == "Favorite Commands");
        Assert.Equal(AircraftMenuBuilder.AllCommandsHeader, items[favorites + 1]);
    }

    [AvaloniaFact]
    public void RadarMenu_StartsWithTheSharedHeader_ItsOwnRowsUnderTheTitle_FavoritesBeforeAllCommands()
    {
        List<string> items = TopLevel(MenuView.Radar, "ifr-enroute", out ContextMenu _);

        Assert.Equal("AAL202 · B738", items[0]);
        int command = items.IndexOf("Command…");
        Assert.True(command > 1, "Command… should follow the title, any radar rows and a separator.");
        Assert.Equal("---", items[command - 1]);
        Assert.DoesNotContain("---", items[1..(command - 1)]);
        Assert.Equal(["Command…", "Note…", "---"], items[command..(command + 3)]);
        AssertFavoritesBeforeAllCommands(items);
        Assert.Single(items, i => i == "Command…");
        Assert.Single(items, i => i == "Note…");
    }

    [AvaloniaFact]
    public void RadarDataBlockSubmenu_HasNoNote()
    {
        TopLevel(MenuView.Radar, "ifr-enroute", out ContextMenu menu);

        MenuItem dataBlock = menu.Items.OfType<MenuItem>().Single(i => (i.Header as string) == "Data Block");
        List<string> children = [.. dataBlock.Items.Select(Describe)];
        Assert.Contains("Scratchpad…", children);
        Assert.DoesNotContain(children, c => c.StartsWith("Note", StringComparison.Ordinal));
    }

    // --- The release items, in the header on every view ----------------------------------------

    [AvaloniaFact]
    public void Header_HeldForRelease_ShowsReleaseUnderTitle_OnEveryView()
    {
        Assert.Equal(
            ["SWA108 · B738", "Taxiing", "Release (HFR)", "---", "Command…", "Note…", "---"],
            TopLevel(MenuView.Ground, "held-for-release", out _)[..7]
        );
        Assert.Equal(
            ["SWA108 · B738", "Taxiing", "Release (HFR)", "---", "Command…", "Note…", "---"],
            TopLevel(MenuView.List, "held-for-release", out _)[..7]
        );

        List<string> radar = TopLevel(MenuView.Radar, "held-for-release", out _);
        Assert.Equal("SWA108 · B738", radar[0]);
        Assert.Equal("Taxiing", radar[1]);
        Assert.Equal("Release (HFR)", radar[2]);
        Assert.Equal("---", radar[3]);
        Assert.Single(radar, i => i == "Release (HFR)");
    }

    [AvaloniaFact]
    public void Header_CfrWindow_ShowsCheckReleaseWindowUnderTitle_OnEveryView()
    {
        Assert.Equal(
            ["SWA109 · B738", "Taxiing", "Check release window", "---", "Command…", "Note…", "---"],
            TopLevel(MenuView.Ground, "cfr-window", out _)[..7]
        );
        Assert.Equal(
            ["SWA109 · B738", "Taxiing", "Check release window", "---", "Command…", "Note…", "---"],
            TopLevel(MenuView.List, "cfr-window", out _)[..7]
        );

        List<string> radar = TopLevel(MenuView.Radar, "cfr-window", out _);
        Assert.Equal("SWA109 · B738", radar[0]);
        Assert.Equal("Taxiing", radar[1]);
        Assert.Equal("Check release window", radar[2]);
        Assert.Equal("---", radar[3]);
        Assert.Single(radar, i => i == "Check release window");
    }

    [AvaloniaFact]
    public void Header_HeldAndCfrWindow_ReleaseBeforeCheck()
    {
        AircraftModel ac = Jet();
        ac.IsHeldForRelease = true;
        ac.CfrWindowStartUtc = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

        List<string> items = [.. Header(ac, new RecordingMenuHost("")).Select(Describe)];

        Assert.Equal(["SWA104 · B738", "Taxiing", "Release (HFR)", "Check release window", "---", "Command…", "Note…", "---"], items);
    }

    [AvaloniaFact]
    public void Header_NotHeldNoWindow_NoReleaseRows()
    {
        List<string> items = [.. Header(Jet(), new RecordingMenuHost("")).Select(Describe)];

        Assert.DoesNotContain("Release (HFR)", items);
        Assert.DoesNotContain("Check release window", items);
    }

    [AvaloniaFact]
    public void CheckReleaseWindow_NotInCommandBlock()
    {
        foreach (MenuView view in new[] { MenuView.Ground, MenuView.List })
        {
            List<string> items = TopLevel(view, "cfr-window", out ContextMenu menu);

            Assert.Equal(["SWA109 · B738", "Taxiing", "Check release window"], items[..3]);
            Assert.Single(items, i => i == "Check release window");
            Assert.Equal(1, CountOccurrences(MenuTreeSnapshot.Render(menu), "Check release window"));
        }
    }

    // A surface live-traffic shadow is not controllable, so the radar header offers neither release row even with both flags set.
    [AvaloniaFact]
    public void Header_ShadowWithReleaseFlags_ShowsNoReleaseRow_OnTheRadar()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(_navDb);
        var main = new MainViewModel(new FakeFilePickerService());
        main.DisplayFavorites.Clear();
        main.Ground.SetLayoutForTesting(MenuGoldenFixtures.OakLayoutForClient);

        AircraftModel ac = MenuGoldenFixtures.For(MenuView.Radar).Single(f => f.Name == "live-traffic-surface").Aircraft;
        ac.IsHeldForRelease = true;
        ac.CfrWindowStartUtc = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
        main.Aircraft.Add(ac);

        ContextMenu menu = MenuHostHarness.BuildRadarMenu(main, ac, null);
        List<string> items = [.. menu.Items.Select(Describe)];

        Assert.DoesNotContain("Release (HFR)", items);
        Assert.DoesNotContain("Check release window", items);
    }

    // The hold status row sits between the title and the shared release items.
    [AvaloniaFact]
    public void Header_Radar_ReleaseRowsFollowTheHoldRow()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(_navDb);
        var main = new MainViewModel(new FakeFilePickerService());
        main.DisplayFavorites.Clear();
        main.Ground.SetLayoutForTesting(MenuGoldenFixtures.OakLayoutForClient);

        AircraftModel ac = MenuGoldenFixtures.For(MenuView.Radar).Single(f => f.Name == "held-for-release").Aircraft;
        ac.HoldKind = "HoldPosition";
        main.Aircraft.Add(ac);

        ContextMenu menu = MenuHostHarness.BuildRadarMenu(main, ac, null);
        List<string> items = [.. menu.Items.Select(Describe)];

        Assert.Equal(["SWA108 · B738", "Taxiing", "Held: position", "Release (HFR)", "---"], items[..5]);
    }

    // --- The route summary and hold status rows, on every view ---------------------------------

    [AvaloniaFact]
    public void Header_RouteSummary_UnderTheStateLine_OnEveryView()
    {
        foreach (MenuView view in new[] { MenuView.Radar, MenuView.Ground, MenuView.List })
        {
            List<string> items = TopLevel(view, "ifr-enroute", ac => ac.NavigationRoute = ["OAK", "SUNOL", "MOD"], out _);

            Assert.Equal("AAL202 · B738", items[0]);
            Assert.Equal("33,000 ft · 280 kt", items[1]);
            Assert.Equal("OAK SUNOL MOD", items[2]);
            Assert.Equal("---", items[3]);
        }
    }

    [AvaloniaFact]
    public void Header_HoldStatus_UnderTheStateLine_OnEveryView()
    {
        foreach (MenuView view in new[] { MenuView.Radar, MenuView.Ground, MenuView.List })
        {
            List<string> items = TopLevel(view, "ifr-enroute", ac => ac.HoldKind = "HoldPosition", out _);

            Assert.Equal("AAL202 · B738", items[0]);
            Assert.Equal("33,000 ft · 280 kt", items[1]);
            Assert.Equal("Held: position", items[2]);
            Assert.Equal("---", items[3]);
        }
    }

    [AvaloniaFact]
    public void Header_TitleThenStateThenRouteSummaryThenHoldStatus_OnEveryView()
    {
        foreach (MenuView view in new[] { MenuView.Radar, MenuView.Ground, MenuView.List })
        {
            List<string> items = TopLevel(
                view,
                "ifr-enroute",
                ac =>
                {
                    ac.NavigationRoute = ["OAK", "SUNOL", "MOD"];
                    ac.HoldKind = "HoldPosition";
                },
                out _
            );

            Assert.Equal(["AAL202 · B738", "33,000 ft · 280 kt", "OAK SUNOL MOD", "Held: position", "---"], items[..5]);
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = text.IndexOf(value, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }

        return count;
    }

    /// <summary>The top-level items of one golden fixture's menu on <paramref name="view"/>: each item's header, a separator as <c>---</c>.</summary>
    private List<string> TopLevel(MenuView view, string fixtureName, out ContextMenu menu) => TopLevel(view, fixtureName, static _ => { }, out menu);

    /// <summary>
    /// The top-level items of one golden fixture's menu on <paramref name="view"/>, with <paramref name="setUp"/> adjusting
    /// the fixture's aircraft before the menu is built: each item's header, a separator as <c>---</c>.
    /// </summary>
    private List<string> TopLevel(MenuView view, string fixtureName, Action<AircraftModel> setUp, out ContextMenu menu)
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(_navDb);
        var main = new MainViewModel(new FakeFilePickerService());
        main.DisplayFavorites.Clear();
        main.Ground.SetLayoutForTesting(MenuGoldenFixtures.OakLayoutForClient);

        AircraftModel ac = MenuGoldenFixtures.For(view).Single(f => f.Name == fixtureName).Aircraft;
        setUp(ac);
        main.Aircraft.Add(ac);

        menu = view switch
        {
            MenuView.Radar => MenuHostHarness.BuildRadarMenu(main, ac, null),
            MenuView.Ground => MenuHostHarness.BuildGroundMenu(main, ac, null),
            _ => DataGridView.BuildAircraftMenu(main, new DataGrid(), ac, null, [ac]),
        };
        return [.. menu.Items.Select(Describe)];
    }

    // --- Fixtures -----------------------------------------------------------------------------

    private readonly NavigationDatabase _navDb = MenuGoldenFixtures.EnsureNavData();

    private static AircraftModel Jet() =>
        new()
        {
            Callsign = Callsign,
            AircraftType = "B738",
            FlightRules = "IFR",
            IsOnGround = true,
            CurrentPhase = "Taxiing",
        };

    /// <summary>An airborne IFR B738 with the given raw phase, altitude and indicated airspeed.</summary>
    private static AircraftModel AirborneAircraft(string phase, double altitude, double speed) =>
        new()
        {
            Callsign = Callsign,
            AircraftType = "B738",
            FlightRules = "IFR",
            IsOnGround = false,
            CurrentPhase = phase,
            Altitude = altitude,
            IndicatedAirspeed = speed,
        };

    /// <summary>An on-ground IFR B738 in the given raw phase.</summary>
    private static AircraftModel GroundAircraft(string phase) =>
        new()
        {
            Callsign = Callsign,
            AircraftType = "B738",
            FlightRules = "IFR",
            IsOnGround = true,
            CurrentPhase = phase,
        };

    /// <summary>The header's state row text, or null when the header has none.</summary>
    private static string? StateRow(AircraftModel ac)
    {
        ItemCollection items = Header(ac, new RecordingMenuHost(""));
        return ((items.Count > 1) && (items[1] is MenuItem { IsEnabled: false } row)) ? row.Header as string : null;
    }

    private static string StateLine(AircraftModel ac) => StateRow(ac) ?? throw new InvalidOperationException("The header has no state row.");

    private static ItemCollection Header(AircraftModel? ac, IMenuHost host)
    {
        MenuContext context = TestMenuContext.Create(Callsign, Initials, null, false, VfrCommandsForIfr.None);
        var menu = new ContextMenu();
        SharedMenuGroups.AddHeader(menu.Items, ac, context, host);
        return menu.Items;
    }

    /// <summary>A ground view over a main view model's primary ground view model, shown in a window hosted by that main view model.</summary>
    private static (MainViewModel Main, GroundView View) GroundOnMain()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var view = new GroundView { DataContext = main.Ground };
        new Window { DataContext = main, Content = view }.ShowAndRunLayout();
        return (main, view);
    }

    private static MenuItem Item(ItemCollection items, string header) => items.OfType<MenuItem>().Single(i => (i.Header as string) == header);

    private static string Describe(object? item) =>
        item switch
        {
            Separator => "---",
            MenuItem { Header: string header } => header,
            _ => item?.GetType().Name ?? "",
        };

    private static TextBox FindTextBox(Control anchor)
    {
        var overlay = OverlayLayer.GetOverlayLayer(anchor);
        Assert.NotNull(overlay);
        Popup? popup = overlay!.Children.OfType<Popup>().LastOrDefault();
        Assert.NotNull(popup);
        TextBox? textBox = popup!.Child?.GetLogicalDescendants().OfType<TextBox>().FirstOrDefault();
        Assert.NotNull(textBox);
        return textBox!;
    }

    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    private static void RaiseKey(TextBox textBox, Key key) =>
        textBox.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
}
