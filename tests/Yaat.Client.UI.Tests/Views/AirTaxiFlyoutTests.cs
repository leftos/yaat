using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Situation;
using Yaat.Sim.Testing;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The helicopter Air taxi and Land flyouts in the quick-command strip: an "Air taxi to" / "Land at" filter box on top, then
/// one tab per destination group (Pads · parking, Spots and, for Air taxi only, Runways), each nearest first, the parking
/// rows capped at eight with an "n more" line; a runway end is one row with its other bars as chips. Typing filters every
/// group, an exact name first, then nearest, and Enter sends the top match; an empty box sends nothing. Land's Keep NODEL
/// box appends <c>NODEL</c> to every row and to Enter. The rows refresh once a second while the flyout is open.
/// </summary>
public class AirTaxiFlyoutTests
{
    private const string Callsign = "N812HL";
    private const string Initials = "AB";

    public AirTaxiFlyoutTests() => TestVnasData.EnsureInitialized();

    // --- Tabs and rows ----------------------------------------------------------------------------

    [AvaloniaFact]
    public void AirTaxi_Opens_ThreeTabs_PadsParkingFirst_TheBoxFocused()
    {
        RecordingMenuHost host = HostWith(MenuIds.GroundAirTaxi, SampleMenu());

        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundAirTaxi, Parked());
        AirTaxiPanel panel = Panel(flyout);

        Assert.Equal(["Pads · parking", "Spots", "Runways"], panel.Strip.Tabs.Select(tab => tab.Label));
        Assert.Equal(0, panel.Strip.SelectedIndex);
        Assert.Same(panel.Box, Focused(flyout));
        Assert.Equal("Air taxi to", panel.BoxLabel);
        Assert.Null(panel.KeepNodel);
        Assert.Equal(
            [
                "Helipads",
                "HELI2 · helipad · ~350 ft — ATXI @HELI2",
                "HELI · helipad · ~1,050 ft — ATXI @HELI",
                "Parking · nearest first · live",
                "JSX1 · ~150 ft — ATXI @JSX1",
                "GA1 · ~200 ft — ATXI @GA1",
                "GA2 · ~200 ft — ATXI @GA2",
                "JSX2 · ~300 ft — ATXI @JSX2",
                "GA4 · ~450 ft — ATXI @GA4",
                "JSX3 · ~450 ft — ATXI @JSX3",
                "GA3 · ~500 ft — ATXI @GA3",
                "GA5 · ~850 ft — ATXI @GA5",
                "3 more parking positions below, nearest first (GA6 at ~850 ft … C at ~5,300 ft)",
            ],
            panel.Lines
        );
    }

    [AvaloniaFact]
    public void SpotsTab_SendsTheDollarForm_AndRunwaysTab_OneRowPerEndWithItsChips()
    {
        RecordingMenuHost host = HostWith(MenuIds.GroundAirTaxi, SampleMenu());
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundAirTaxi, Parked());
        AirTaxiPanel panel = Panel(flyout);

        panel.Strip.Select(1);
        Layout(flyout);
        Assert.Equal(
            [
                "Spots · nearest first · live",
                "I8R · spot · ~650 ft — ATXI $I8R",
                "A8RN · spot · ~950 ft — ATXI $A8RN",
                "C · spot · ~7,000 ft — ATXI $C",
            ],
            panel.Lines
        );

        panel.Strip.Select(2);
        Layout(flyout);
        Assert.Equal(
            [
                "Runway holding positions · nearest first · live",
                "RWY 28R · full length at B · ~700 ft — ATXI 28R",
                "at E G",
                "RWY 33 · full length at C · ~3,900 ft — ATXI 33",
                "at L K",
                "RWY 10L · full length at C1 · ~4,800 ft — ATXI 10L",
                "at J",
                "RWY 15 · full length · ~6,700 ft — ATXI 15",
                "at D F",
            ],
            panel.Lines
        );
        Assert.Equal(
            [
                "ATXI 28R",
                "ATXI 28R@E",
                "ATXI 28R@G",
                "ATXI 33",
                "ATXI 33@L",
                "ATXI 33@K",
                "ATXI 10L",
                "ATXI 10L@J",
                "ATXI 15",
                "ATXI 15@D",
                "ATXI 15@F",
            ],
            Commands(panel)
        );
    }

    [AvaloniaFact]
    public void Chip_SendsItsLocatedForm_AndClosesTheFlyout()
    {
        RecordingMenuHost host = HostWith(MenuIds.GroundAirTaxi, SampleMenu());
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundAirTaxi, Parked());
        AirTaxiPanel panel = Panel(flyout);
        panel.Strip.Select(2);
        Layout(flyout);

        Button chip = Row(panel, "ATXI 28R@E");
        Assert.Equal("ATXI 28R@E", Avalonia.Automation.AutomationProperties.GetName(chip));
        Click(chip);

        Assert.Equal([(Callsign, "ATXI 28R@E", Initials)], host.Sent);
        Assert.False(flyout.IsOpen);
    }

    /// <summary>A runway row whose bar the helicopter holds at sends nothing: it reads "holding short here" and only its chips send.</summary>
    [AvaloniaFact]
    public void RunwayRow_HeldAt_SendsNothing_ItsChipsStillSend()
    {
        AirTaxiMenu sample = SampleMenu();
        AirTaxiMenu menu = sample with { Runways = [sample.Runways[0] with { IsHeldAt = true }, .. sample.Runways.Skip(1)] };
        RecordingMenuHost host = HostWith(MenuIds.GroundAirTaxi, menu);
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundAirTaxi, Parked());
        AirTaxiPanel panel = Panel(flyout);

        panel.Strip.Select(2);
        Layout(flyout);

        Assert.Equal("RWY 28R · full length at B · holding short here", panel.Lines[1]);
        Assert.Equal("at E G", panel.Lines[2]);
        Assert.Equal(["ATXI 28R@E", "ATXI 28R@G", "ATXI 33"], Commands(panel).Take(3));
        Type(flyout, panel, "28R");
        Assert.Equal([AirTaxiFlyout.NoMatchText], panel.Lines);
    }

    /// <summary>With the pointer inside, a held runway row keeps its place with its chips through a refill, as every row does.</summary>
    [AvaloniaFact]
    public void RunwayRow_HeldAt_KeepsItsPlace_WhileThePointerHoldsTheOrder()
    {
        AirTaxiMenu sample = SampleMenu();
        AirTaxiMenu menu = sample with { Runways = [sample.Runways[0] with { IsHeldAt = true }, .. sample.Runways.Skip(1)] };
        RecordingMenuHost host = HostWith(MenuIds.GroundAirTaxi, menu);
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundAirTaxi, Parked());
        AirTaxiPanel panel = Panel(flyout);
        panel.Strip.Select(2);
        Layout(flyout);
        List<string> before = [.. panel.Lines];

        PointerIn(flyout);
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        Layout(flyout);

        Assert.Equal("RWY 28R · full length at B · holding short here", panel.Lines[1]);
        Assert.Equal(before, panel.Lines);
    }

    [AvaloniaFact]
    public void Land_HasNoRunwaysTab_SpotsSendTheDollarForm_PadsTheAtForm()
    {
        RecordingMenuHost host = HostWith(MenuIds.GroundLandHelicopter, SampleMenu());
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundLandHelicopter, Airborne());
        AirTaxiPanel panel = Panel(flyout);

        Assert.Equal(["Pads · parking", "Spots"], panel.Strip.Tabs.Select(tab => tab.Label));
        Assert.Equal("Land at OAK", panel.BoxLabel);
        Assert.Equal("HELI2 · helipad · ~350 ft — LAND @HELI2", panel.Lines[1]);
        Assert.Equal("JSX1 · ~150 ft — LAND @JSX1", panel.Lines[4]);

        panel.Strip.Select(1);
        Layout(flyout);
        Assert.Equal(["LAND $I8R", "LAND $A8RN", "LAND $C"], Commands(panel));
        Assert.False(panel.Strip.Move(1));
    }

    [AvaloniaFact]
    public void EmptyGroup_TabDimmedNone_PanelSaysNoDestination()
    {
        AirTaxiMenu menu = SampleMenu() with { Spots = [] };
        RecordingMenuHost host = HostWith(MenuIds.GroundAirTaxi, menu);
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundAirTaxi, Parked());
        AirTaxiPanel panel = Panel(flyout);

        Assert.Equal(FlyoutTabState.Empty, panel.Strip.Tabs[1].State);
        panel.Strip.Select(1);
        Layout(flyout);
        Assert.Equal([AirTaxiFlyout.NoDestinationText], panel.Lines);
    }

    // --- Filter box ---------------------------------------------------------------------------------

    /// <summary>The mock's worst case: "C" matches parking C and CHEVRON, spot C, and runways 33 (at C) and 10L (at C1).</summary>
    [AvaloniaFact]
    public void Typing_FiltersEveryGroup_ExactNameFirstThenNearest_TabsGiveWay_EnterSendsTheTopMatch()
    {
        RecordingMenuHost host = HostWith(MenuIds.GroundAirTaxi, SampleMenu());
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundAirTaxi, Parked());
        AirTaxiPanel panel = Panel(flyout);

        Type(flyout, panel, "c");

        Assert.False(panel.Strip.IsVisible);
        Assert.Equal(
            [
                "Helipads and parking · @",
                "C · ~5,300 ft — ATXI @C",
                "CHEVRON · ~4,650 ft — ATXI @CHEVRON",
                "Spots · $",
                "C · spot · ~7,000 ft — ATXI $C",
                "Runway holding positions",
                "RWY 33 · full length at C · ~3,900 ft — ATXI 33",
                "RWY 10L · full length at C1 · ~4,800 ft — ATXI 10L",
            ],
            panel.Lines
        );
        Assert.Equal("Enter sends ATXI @C, the first match.", panel.Hint.Text);

        Press(flyout, Key.Enter);

        Assert.Equal([(Callsign, "ATXI @C", Initials)], host.Sent);
        Assert.False(flyout.IsOpen);
    }

    [AvaloniaFact]
    public void Enter_WithAnEmptyBox_SendsNothing()
    {
        RecordingMenuHost host = HostWith(MenuIds.GroundAirTaxi, SampleMenu());
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundAirTaxi, Parked());
        AirTaxiPanel panel = Panel(flyout);

        Assert.Equal("Type to filter every group; Enter sends the top match.", panel.Hint.Text);
        Press(flyout, Key.Enter);

        Assert.Empty(host.Sent);
        Assert.True(flyout.IsOpen);
    }

    [AvaloniaFact]
    public void NoMatch_SaysSo_AndEnterSendsNothing()
    {
        RecordingMenuHost host = HostWith(MenuIds.GroundAirTaxi, SampleMenu());
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundAirTaxi, Parked());
        AirTaxiPanel panel = Panel(flyout);

        Type(flyout, panel, "ZZ");
        Assert.Equal([AirTaxiFlyout.NoMatchText], panel.Lines);
        Assert.Equal("Nothing to send.", panel.Hint.Text);
        Press(flyout, Key.Enter);

        Assert.Empty(host.Sent);
    }

    [AvaloniaFact]
    public void Filter_ReachesTheParkingRowsPastTheFirstEight()
    {
        RecordingMenuHost host = HostWith(MenuIds.GroundAirTaxi, SampleMenu());
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundAirTaxi, Parked());
        AirTaxiPanel panel = Panel(flyout);

        Type(flyout, panel, "GA6");

        Assert.Equal(["Helipads and parking · @", "GA6 · ~850 ft — ATXI @GA6"], panel.Lines);
    }

    // --- Keyboard -------------------------------------------------------------------------------------

    [AvaloniaFact]
    public void Down_FromTheBoxFocusesTheFirstRow_UpReturns_RightChoosesTheNextTab()
    {
        RecordingMenuHost host = HostWith(MenuIds.GroundAirTaxi, SampleMenu());
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundAirTaxi, Parked());
        AirTaxiPanel panel = Panel(flyout);

        Press(flyout, Key.Down);
        Assert.Equal("ATXI @HELI2", FocusedCommand(flyout));
        Press(flyout, Key.Down);
        Assert.Equal("ATXI @HELI", FocusedCommand(flyout));
        Press(flyout, Key.Up);
        Press(flyout, Key.Up);
        Assert.Same(panel.Box, Focused(flyout));

        Press(flyout, Key.Down);
        Press(flyout, Key.Right);
        Assert.Equal(1, panel.Strip.SelectedIndex);
        Assert.Equal("ATXI $I8R", FocusedCommand(flyout));
    }

    // --- Keep NODEL -----------------------------------------------------------------------------------

    [AvaloniaFact]
    public void KeepNodel_Ticked_EveryRowAndEnterSendWithNodel()
    {
        RecordingMenuHost host = HostWith(MenuIds.GroundLandHelicopter, SampleMenu());
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundLandHelicopter, Airborne());
        AirTaxiPanel panel = Panel(flyout);
        CheckBox keep = Assert.IsType<CheckBox>(panel.KeepNodel);
        Assert.Equal(AirTaxiFlyout.KeepNodelText, keep.Content);
        Assert.Equal("Type to filter both groups; Enter sends the top match.", panel.Hint.Text);

        keep.IsChecked = true;
        Layout(flyout);

        Assert.Equal("HELI2 · helipad · ~350 ft — LAND @HELI2 NODEL", panel.Lines[1]);
        Assert.Equal("Type to filter both groups; Enter sends the top match with NODEL.", panel.Hint.Text);
        Type(flyout, panel, "HELI2");
        Assert.Equal("Enter sends LAND @HELI2 NODEL, the first match.", panel.Hint.Text);
        Press(flyout, Key.Enter);
        Assert.Equal([(Callsign, "LAND @HELI2 NODEL", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void KeepNodel_AndTheBox_StartClearEachTimeTheFlyoutOpens()
    {
        RecordingMenuHost host = HostWith(MenuIds.GroundLandHelicopter, SampleMenu());
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundLandHelicopter, Airborne(), out ContextMenu menu);
        AirTaxiPanel panel = Panel(flyout);
        panel.KeepNodel!.IsChecked = true;
        Type(flyout, panel, "HELI");

        flyout.Hide();
        Dispatcher.UIThread.RunJobs();
        QuickCommandStrip.OpenSubmenu(StripButton(menu, MenuIds.GroundLandHelicopter));
        Dispatcher.UIThread.RunJobs();
        Layout(flyout);

        Assert.False(panel.KeepNodel.IsChecked);
        Assert.Equal("", panel.Box.Text);
        Assert.Equal("HELI2 · helipad · ~350 ft — LAND @HELI2", panel.Lines[1]);
    }

    [AvaloniaFact]
    public void KeepNodel_Ticked_AClickedRowSendsWithNodel()
    {
        RecordingMenuHost host = HostWith(MenuIds.GroundLandHelicopter, SampleMenu());
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundLandHelicopter, Airborne());
        AirTaxiPanel panel = Panel(flyout);
        panel.KeepNodel!.IsChecked = true;
        panel.Strip.Select(1);
        Layout(flyout);

        Click(Row(panel, "LAND $I8R NODEL"));

        Assert.Equal([(Callsign, "LAND $I8R NODEL", Initials)], host.Sent);
    }

    // --- The Land flyout's airport ------------------------------------------------------------------

    /// <summary>
    /// Bound for an airport other than the one the Land panel lists, a row and Enter load the listed airport first with
    /// <c>APT</c>, NODEL still last; the header names the airport.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(false, false, "APT OAK, LAND @HELI2")]
    [InlineData(false, true, "APT OAK, LAND @HELI2")]
    [InlineData(true, false, "APT OAK, LAND @HELI2 NODEL")]
    [InlineData(true, true, "APT OAK, LAND @HELI2 NODEL")]
    public void Land_BoundElsewhere_RowsAndEnterLoadTheListedAirportFirst(bool keepNodel, bool byEnter, string expected)
    {
        RecordingMenuHost host = HostWith(MenuIds.GroundLandHelicopter, SampleMenu());
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundLandHelicopter, AirborneBoundElsewhere());
        AirTaxiPanel panel = Panel(flyout);
        Assert.Equal("Land at OAK", panel.BoxLabel);
        panel.KeepNodel!.IsChecked = keepNodel;
        Layout(flyout);

        Assert.Equal($"HELI2 · helipad · ~350 ft — {expected}", panel.Lines[1]);
        if (byEnter)
        {
            Type(flyout, panel, "HELI2");
            Assert.Equal($"Enter sends {expected}, the first match.", panel.Hint.Text);
            Press(flyout, Key.Enter);
        }
        else
        {
            Click(Row(panel, expected));
        }

        Assert.Equal([(Callsign, expected, Initials)], host.Sent);
    }

    /// <summary>
    /// On the real KOAK layout the Land panel's header names the airport its rows come from, as the host answers its id; a
    /// helicopter bound there sends the bare <c>LAND</c>, one bound elsewhere loads the airport first.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("KOAK", "LAND @HELI1")]
    [InlineData("KSFO", "APT OAK, LAND @HELI1")]
    public void OnKoak_Land_HeaderNamesTheAirport_AndApt_OnlyWhenBoundElsewhere(string destination, string expected)
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (AircraftModel heli, MainViewModel main) = AtKoakHeli1(onGround: false);
        heli.Destination = destination;
        var host = new SendCapturingHost(new ClientMenuHost(main, heli, new Border()), Initials);
        MenuItem entry = MenuCatalog
            .Get(MenuIds.GroundLandHelicopter)
            .Build(heli, TestMenuContext.Create(Callsign, Initials, null, false, VfrCommandsForIfr.None), host)!;

        entry.IsSubMenuOpen = true;
        AirTaxiPanel panel = Assert.IsType<AirTaxiPanel>(Assert.Single(entry.Items.OfType<MenuItem>()).Header);

        Assert.Equal("Land at OAK", panel.BoxLabel);
        Assert.Equal(expected, MenuCommandText.GetCommand(panel.Rows[0]));
    }

    // --- Live refresh ---------------------------------------------------------------------------------

    [AvaloniaFact]
    public void OnceASecond_TheRowsFollowTheHost_NearestFirst_AndHoldTheirOrderWhileThePointerIsInside()
    {
        RecordingMenuHost host = HostWith(MenuIds.GroundAirTaxi, SampleMenu());
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundAirTaxi, Parked());
        AirTaxiPanel panel = Panel(flyout);
        panel.Strip.Select(1);
        Layout(flyout);
        int asked = host.AirTaxiRequests.Count;

        host.AirTaxi = SampleMenu() with { Spots = [Spot("C", 400), Spot("I8R", 900), Spot("A8RN", 1200)] };
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        Layout(flyout);

        Assert.True(host.AirTaxiRequests.Count > asked);
        Assert.Equal(["ATXI $C", "ATXI $I8R", "ATXI $A8RN"], Commands(panel));
        Assert.Equal("C · spot · ~400 ft — ATXI $C", panel.Lines[1]);

        PointerIn(flyout);
        host.AirTaxi = SampleMenu() with { Spots = [Spot("A8RN", 300), Spot("I8R", 900), Spot("C", 1500)] };
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        Layout(flyout);

        Assert.Equal(["ATXI $C", "ATXI $I8R", "ATXI $A8RN"], Commands(panel));
        Assert.Equal("C · spot · ~1,500 ft — ATXI $C", panel.Lines[1]);

        PointerOut(flyout);
        Layout(flyout);
        Assert.Equal(["ATXI $A8RN", "ATXI $I8R", "ATXI $C"], Commands(panel));
    }

    [AvaloniaFact]
    public void AfterTheFlyoutCloses_TheRowsStopRefilling()
    {
        RecordingMenuHost host = HostWith(MenuIds.GroundAirTaxi, SampleMenu());
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundAirTaxi, Parked());
        int opened = host.AirTaxiRequests.Count;
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        Layout(flyout);
        Assert.True(host.AirTaxiRequests.Count > opened);

        flyout.Hide();
        Dispatcher.UIThread.RunJobs();
        int closed = host.AirTaxiRequests.Count;
        host.Clock.Advance(TimeSpan.FromSeconds(3));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(closed, host.AirTaxiRequests.Count);
    }

    // --- The real KOAK layout through the client's host -------------------------------------------------

    /// <summary>
    /// Every runway row and chip the client builds on the real KOAK layout, sent as <c>ATXI</c> from HELI1, the sim takes:
    /// its air-taxi resolution finds the bar on the layout, the runway on the layout and the runway's navigation-data record.
    /// </summary>
    [AvaloniaFact]
    public void OnKoak_EveryRunwayRowAndChip_TheSimAirTaxisTo()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (AircraftModel heli, MainViewModel main) = AtKoakHeli1(onGround: true);
        AirTaxiMenu menu = new ClientMenuHost(main, heli, new Border()).GetAirTaxiChoices(Callsign);
        AirportGroundLayout layout = MenuGoldenFixtures.OakLayoutForSim;
        List<string> tokens =
        [
            .. menu.Runways.Where(runway => !runway.IsHeldAt).Select(runway => runway.Token),
            .. menu.Runways.SelectMany(runway => runway.Chips).Select(chip => chip.Token),
        ];
        Assert.Contains("28R@J", tokens);

        foreach (string token in tokens)
        {
            AircraftState sim = SimHelicopterAt(layout, heli.Position);

            CommandResult result = GroundCommandHandler.TryAirTaxi(sim, token, layout);

            Assert.True(result.Success, $"ATXI {token} refused: {result.Message}");
            Assert.IsType<AirTaxiPhase>(sim.Phases!.CurrentPhase);
        }
    }

    /// <summary>
    /// An EC35 on the ground at <paramref name="position"/> on <paramref name="layout"/>, as the sim holds it, bound for KOAK;
    /// the phase it stands in is the sim's own business (<c>QuickCommandSimAcceptanceTests</c> covers each one).
    /// </summary>
    private static AircraftState SimHelicopterAt(AirportGroundLayout layout, LatLon position)
    {
        var sim = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = "EC35",
            Position = position,
            TrueHeading = new TrueHeading(0),
            TrueTrack = new TrueHeading(0),
            Altitude = 9,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "KOAK", Destination = "KOAK" },
        };
        sim.Ground.Layout = layout;
        return sim;
    }

    /// <summary>
    /// A helipad and a parking position sharing a name list once, as the pad, which <c>@NAME</c> reaches first in the sim.
    /// KOAK has no helipad node, so the layout gains one named HELI2 on its HELI2 parking position.
    /// </summary>
    [AvaloniaFact]
    public void OnKoak_AHelipadSharingAParkingName_ListsOnceAsThePad()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        GroundLayoutDto oak = MenuGoldenFixtures.OakLayoutForClient;
        GroundNodeDto heli2 = oak.Nodes.First(n => (n.Type == "Parking") && (n.Name == "HELI2"));
        GroundLayoutDto withPad = oak with { Nodes = [.. oak.Nodes, heli2 with { Id = oak.Nodes.Max(n => n.Id) + 1, Type = "Helipad" }] };
        (AircraftModel heli, MainViewModel main) = AtKoakHeli1(onGround: true);
        main.Ground.SetLayoutForTesting(withPad);

        AirTaxiMenu menu = new ClientMenuHost(main, heli, new Border()).GetAirTaxiChoices(Callsign);

        Assert.Equal(["@HELI2"], menu.Pads.Select(pad => pad.Token));
        Assert.DoesNotContain(menu.Parking, row => row.Name == "HELI2");
    }

    /// <summary>
    /// An EC35 parked at HELI1 on the real KOAK layout: its own stand is left out, HELI2 is listed, the parking tab caps at
    /// eight rows with the "n more" line, and every runway row sends its bare designator while each chip's located form
    /// resolves, in the sim, to a bar other than the row's.
    /// </summary>
    [AvaloniaFact]
    public void OnKoak_AtHeli1_OwnStandLeftOut_RunwayRowsBare_EveryChipALocatedBarTheSimResolves()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (AircraftModel heli, MainViewModel main) = AtKoakHeli1(onGround: true);
        var host = new ClientMenuHost(main, heli, new Border());

        AirTaxiMenu menu = host.GetAirTaxiChoices(Callsign);

        Assert.Equal("OAK", menu.AirportId);
        Assert.DoesNotContain(menu.Parking, row => row.Name == "HELI1");
        Assert.Contains(menu.Parking, row => row.Token == "@HELI2");
        Assert.Equal(menu.Parking.OrderBy(row => row.DistanceFt), menu.Parking);
        Assert.Contains(menu.Spots, row => row.Token == "$I8R");
        AirTaxiDestination r28R = Assert.Single(menu.Runways, row => row.Token == "28R");
        Assert.NotEmpty(r28R.Chips);
        AirportGroundLayout layout = MenuGoldenFixtures.OakLayoutForSim;
        foreach (AirTaxiDestination runway in menu.Runways)
        {
            GroundNode bar = Assert.IsType<GroundNode>(
                GroundCommandHandler.ResolveRunwayHoldShortNode(layout, new HoldShortTarget(runway.Token, null, false), heli.Position)
            );
            if (runway.BarTaxiway is { } taxiway)
            {
                Assert.Equal(
                    bar.Id,
                    GroundCommandHandler.ResolveRunwayHoldShortNode(layout, new HoldShortTarget(runway.Token, taxiway, false), heli.Position)?.Id
                );
            }

            HashSet<int> chipBars = [];
            foreach (AirTaxiChip chip in runway.Chips)
            {
                Assert.Equal($"{runway.Token}@{chip.Taxiway}", chip.Token);
                GroundNode located = Assert.IsType<GroundNode>(
                    GroundCommandHandler.ResolveRunwayHoldShortNode(layout, new HoldShortTarget(runway.Token, chip.Taxiway, false), heli.Position)
                );
                Assert.NotEqual(bar.Id, located.Id);
                Assert.True(chipBars.Add(located.Id), $"{chip.Token} names a bar another chip of {runway.Name} already names");
            }
        }
    }

    /// <summary>
    /// From HELI1, runway 28R's chips are its own bars only: a bar within 300 ft of the far (10L) threshold, a 10L
    /// full-length bar, is left out as Taxi to runway leaves it out.
    /// </summary>
    [AvaloniaFact]
    public void OnKoak_28RChips_LeaveOutTheFarEndsFullLengthBars()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (AircraftModel heli, MainViewModel main) = AtKoakHeli1(onGround: true);

        AirTaxiMenu menu = new ClientMenuHost(main, heli, new Border()).GetAirTaxiChoices(Callsign);

        AirTaxiDestination r28R = Assert.Single(menu.Runways, row => row.Token == "28R");
        Assert.Equal("B", r28R.BarTaxiway);
        Assert.Equal(["28R@E", "28R@G", "28R@H", "28R@P", "28R@J"], r28R.Chips.Select(chip => chip.Token));
    }

    /// <summary>
    /// Holding short of runway 30 on W3, the helicopter is offered no air taxi to the bar it already holds at: no row's
    /// command and no chip resolves, in the sim, to that bar.
    /// </summary>
    [AvaloniaFact]
    public void OnKoak_HoldingShort_OffersNoRowOrChipForTheBarItHoldsAt()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (AircraftModel heli, MainViewModel main, GroundNode held) = HoldingShortAt30W3();
        AirportGroundLayout layout = MenuGoldenFixtures.OakLayoutForSim;

        AirTaxiMenu menu = new ClientMenuHost(main, heli, new Border()).GetAirTaxiChoices(Callsign);

        Assert.Contains(menu.Runways, row => row.Token == "30");
        foreach (AirTaxiDestination runway in menu.Runways)
        {
            if (!runway.IsHeldAt)
            {
                Assert.NotEqual(
                    held.Id,
                    GroundCommandHandler.ResolveRunwayHoldShortNode(layout, new HoldShortTarget(runway.Token, null, false), heli.Position)?.Id
                );
            }

            foreach (AirTaxiChip chip in runway.Chips)
            {
                Assert.NotEqual(
                    held.Id,
                    GroundCommandHandler.ResolveRunwayHoldShortNode(layout, new HoldShortTarget(runway.Token, chip.Taxiway, false), heli.Position)?.Id
                );
            }
        }
    }

    /// <summary>Hovering over HELI1, the helicopter may land on it: an airborne aircraft stands at nothing, so HELI1 is listed.</summary>
    [AvaloniaFact]
    public void OnKoak_HoveringOverHeli1_ListsHeli1()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (AircraftModel heli, MainViewModel main) = AtKoakHeli1(onGround: false);

        AirTaxiMenu menu = new ClientMenuHost(main, heli, new Border()).GetAirTaxiChoices(Callsign);

        Assert.Equal("@HELI1", menu.Parking[0].Token);
        Assert.Equal(0, menu.Parking[0].DistanceFt);
    }

    [AvaloniaFact]
    public void OnKoak_ParkingTab_ShowsTheNearestEight_ThenTheMoreLine()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (AircraftModel heli, MainViewModel main) = AtKoakHeli1(onGround: true);
        var host = new SendCapturingHost(new ClientMenuHost(main, heli, new Border()), Initials);
        MenuItem entry = MenuCatalog
            .Get(MenuIds.GroundAirTaxi)
            .Build(heli, TestMenuContext.Create(Callsign, Initials, null, false, VfrCommandsForIfr.None), host)!;

        entry.IsSubMenuOpen = true;
        AirTaxiPanel panel = Assert.IsType<AirTaxiPanel>(Assert.Single(entry.Items.OfType<MenuItem>()).Header);

        int parkingTitle = panel.Lines.ToList().IndexOf("Parking · nearest first · live");
        Assert.True(parkingTitle >= 0);
        Assert.Equal(AirTaxiFlyout.ParkingShown + 1, panel.Lines.Count - parkingTitle - 1);
        Assert.Matches(@"^\d+ more parking positions below, nearest first \(\S+ at ~[\d,]+ ft … \S+ at ~[\d,]+ ft\)$", panel.Lines[^1]);
    }

    // --- Helpers ----------------------------------------------------------------------------------------

    private static AirTaxiDestination Pad(string name, double ft) => new(name, AirTaxiDestinationKind.Helipad, ft, "@" + name, null, []);

    private static AirTaxiDestination Parking(string name, double ft) => new(name, AirTaxiDestinationKind.Parking, ft, "@" + name, null, []);

    private static AirTaxiDestination Spot(string name, double ft) => new(name, AirTaxiDestinationKind.Spot, ft, "$" + name, null, []);

    private static AirTaxiDestination Runway(string end, double ft, string? bar, params string[] chips) =>
        new($"RWY {end}", AirTaxiDestinationKind.Runway, ft, end, bar, [.. chips.Select(taxiway => new AirTaxiChip(taxiway, $"{end}@{taxiway}"))]);

    /// <summary>KOAK-like destinations from HELI1, nearest first per group: two pads, eleven parking, three spots, four runway ends.</summary>
    private static AirTaxiMenu SampleMenu() =>
        new(
            "OAK",
            [Pad("HELI2", 350), Pad("HELI", 1050)],
            [
                Parking("JSX1", 150),
                Parking("GA1", 200),
                Parking("GA2", 200),
                Parking("JSX2", 300),
                Parking("GA4", 450),
                Parking("JSX3", 450),
                Parking("GA3", 500),
                Parking("GA5", 850),
                Parking("GA6", 850),
                Parking("CHEVRON", 4650),
                Parking("C", 5300),
            ],
            [Spot("I8R", 650), Spot("A8RN", 950), Spot("C", 7000)],
            [Runway("28R", 700, "B", "E", "G"), Runway("33", 3900, "C", "L", "K"), Runway("10L", 4800, "C1", "J"), Runway("15", 6700, null, "D", "F")]
        );

    private static RecordingMenuHost HostWith(string id, AirTaxiMenu menu) =>
        new("")
        {
            Session = new(
                Initials,
                false,
                VfrCommandsForIfr.None,
                _ => [new CatalogQuickCommandEntry(id, null)],
                SoloAdvisoryPrefill.TrueValues,
                false,
                CommandScheme.Default(),
                ShowQuickCommandIcons: true
            ),
            AirTaxi = menu,
        };

    private static AircraftModel Parked() =>
        new()
        {
            Callsign = Callsign,
            AircraftType = "EC35",
            FlightRules = "VFR",
            IsOnGround = true,
            CurrentPhase = "At Parking",
            Situation = AircraftSituation.AtParking,
            ParkingSpot = "HELI1",
        };

    private static AircraftModel Airborne() =>
        new()
        {
            Callsign = Callsign,
            AircraftType = "EC35",
            FlightRules = "VFR",
            IsOnGround = false,
            Altitude = 500,
            Situation = AircraftSituation.VfrFlightFollowing,
            Destination = "KOAK",
        };

    /// <summary>The airborne EC35 bound for KSFO, an airport other than the one the Land flyout lists.</summary>
    private static AircraftModel AirborneBoundElsewhere()
    {
        AircraftModel ac = Airborne();
        ac.Destination = "KSFO";
        return ac;
    }

    /// <summary>An EC35 at KOAK's HELI1, parked or hovering over it, on a view model holding the KOAK layout.</summary>
    private static (AircraftModel Aircraft, MainViewModel Main) AtKoakHeli1(bool onGround)
    {
        GroundNodeDto heli1 = MenuGoldenFixtures.OakLayoutForClient.Nodes.First(n => (n.Type == "Parking") && (n.Name == "HELI1"));
        AircraftModel ac = onGround ? Parked() : Airborne();
        return AtKoak(ac, new LatLon(heli1.Latitude, heli1.Longitude));
    }

    /// <summary>The EC35 at KOAK's runway 30 bar on W3 holding short, as the holding-short helicopter fixture stands.</summary>
    private static (AircraftModel Aircraft, MainViewModel Main, GroundNode Bar) HoldingShortAt30W3()
    {
        GroundNode bar = MenuGoldenFixtures.HoldShort30AtW3(MenuGoldenFixtures.OakLayoutForSim);
        AircraftModel ac = Parked();
        ac.ParkingSpot = "";
        ac.CurrentPhase = $"Holding Short {bar.RunwayId}";
        ac.Situation = AircraftSituation.HoldingShort;
        (AircraftModel heli, MainViewModel main) = AtKoak(ac, bar.Position);
        return (heli, main, bar);
    }

    /// <summary><paramref name="ac"/> at <paramref name="position"/> on a view model holding the KOAK layout.</summary>
    private static (AircraftModel Aircraft, MainViewModel Main) AtKoak(AircraftModel ac, LatLon position)
    {
        ac.Position = position;
        var main = new MainViewModel(new FakeFilePickerService());
        main.Aircraft.Clear();
        main.Aircraft.Add(ac);
        main.Ground.SetLayoutForTesting(MenuGoldenFixtures.OakLayoutForClient);
        return (ac, main);
    }

    private static MenuFlyout OpenStripFlyout(RecordingMenuHost host, string id, AircraftModel aircraft) =>
        OpenStripFlyout(host, id, aircraft, out _);

    private static MenuFlyout OpenStripFlyout(RecordingMenuHost host, string id, AircraftModel aircraft, out ContextMenu menu)
    {
        var window = new Window { Width = 900, Height = 900 };
        window.Show();
        menu = AircraftMenuBuilder.Build(aircraft, new MenuClick(Callsign, null, null, []), host, _ => []);
        menu.Open(window);
        Dispatcher.UIThread.RunJobs();
        menu.UpdateLayout();
        Button button = StripButton(menu, id);
        QuickCommandStrip.OpenSubmenu(button);
        Dispatcher.UIThread.RunJobs();
        MenuFlyout flyout = Assert.IsType<MenuFlyout>(FlyoutBase.GetAttachedFlyout(button));
        Assert.True(flyout.IsOpen);
        Layout(flyout);
        return flyout;
    }

    private static Button StripButton(ContextMenu menu, string id) =>
        QuickCommandStrip.Buttons(Assert.Single(menu.Items.OfType<MenuItem>(), QuickCommandStrip.IsStrip)).Single(b => (string)b.Tag! == id);

    private static AirTaxiPanel Panel(MenuFlyout flyout) => Assert.IsType<AirTaxiPanel>(Assert.Single(flyout.Items.OfType<MenuItem>()).Header);

    private static List<string?> Commands(AirTaxiPanel panel) => [.. panel.Rows.Select(MenuCommandText.GetCommand)];

    private static Button Row(AirTaxiPanel panel, string command) => panel.Rows.Single(row => MenuCommandText.GetCommand(row) == command);

    private static void Click(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static void Type(MenuFlyout flyout, AirTaxiPanel panel, string text)
    {
        panel.Box.Text = text;
        Layout(flyout);
    }

    private static void Layout(MenuFlyout flyout)
    {
        (flyout.Popup.Child as Control)?.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        (flyout.Popup.Child as Control)?.UpdateLayout();
    }

    /// <summary>Presses <paramref name="key"/> on whatever holds focus in the flyout.</summary>
    private static void Press(MenuFlyout flyout, Key key)
    {
        Control focused = Assert.IsAssignableFrom<Control>(Focused(flyout));
        focused.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
        Layout(flyout);
    }

    private static IInputElement? Focused(MenuFlyout flyout) =>
        TopLevel.GetTopLevel(Assert.IsAssignableFrom<Visual>(flyout.Popup.Child))?.FocusManager?.GetFocusedElement();

    private static string? FocusedCommand(MenuFlyout flyout) =>
        ((Focused(flyout) as Visual)?.GetSelfAndVisualAncestors().OfType<Button>().FirstOrDefault() is { } row)
            ? MenuCommandText.GetCommand(row)
            : null;

    private static void PointerIn(MenuFlyout flyout) => RaisePointer((Control)flyout.Popup.Child!, InputElement.PointerEnteredEvent);

    private static void PointerOut(MenuFlyout flyout) => RaisePointer((Control)flyout.Popup.Child!, InputElement.PointerExitedEvent);

    private static void RaisePointer(Control target, RoutedEvent routedEvent) =>
        target.RaiseEvent(
            new PointerEventArgs(
                routedEvent,
                target,
                new Pointer(0, PointerType.Mouse, isPrimary: true),
                rootVisual: null,
                rootVisualPosition: default,
                timestamp: 0,
                properties: default,
                modifiers: KeyModifiers.None
            )
        );
}
