using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Situation;
using Yaat.Sim.Testing;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Clicks every catalog leaf and checks the command text it sends, so a moved or mistyped command shows up here
/// rather than at the server. Input pickers are answered at once by the recording host.
/// </summary>
public class MenuCatalogCommandTests
{
    private const string Callsign = "N123AB";
    private const string Initials = "AB";
    private const string Fix = "SUNOL";
    private const string ApproachId = "I28R";
    private const string StarId = "EMZOH4";

    /// <summary>An airport the navigation data has approaches (among them <see cref="ApproachId"/>) and runways for.</summary>
    private const string ApproachAirport = "KOAK";

    /// <summary>An airport the navigation data has neither approaches nor runways for.</summary>
    private const string UnknownAirport = "ZZZZ";

    /// <summary>
    /// Every catalog entry but the host-built ones (see <see cref="HostBuiltIds"/>): its id, the text an input or
    /// filtered-list picker is answered with or the item text a list picker picks, and the command it sends.
    /// </summary>
    private static readonly (string Id, string Input, string Command)[] Expected =
    [
        (MenuIds.LiveTrafficAssume, "", "ASSUME"),
        (MenuIds.LiveTrafficUnassume, "", "UNASSUME"),
        (MenuIds.TrackTrack, "", "TRACK"),
        (MenuIds.TrackDrop, "", "DROP"),
        (MenuIds.TrackAcceptHandoff, "", "ACCEPT"),
        (MenuIds.TrackCancelHandoff, "", "CANCEL"),
        (MenuIds.TrackAcknowledgePointout, "", "OK"),
        (MenuIds.SquawkAssigned, "", "SQ"),
        (MenuIds.SquawkRandom, "", "RANDSQ"),
        (MenuIds.SquawkVfr, "", "SQVFR"),
        (MenuIds.SquawkNormal, "", "SQNORM"),
        (MenuIds.SquawkStandby, "", "SQSBY"),
        (MenuIds.SquawkIdent, "", "IDENT"),
        (MenuIds.AskPilotAltitude, "", "SALT"),
        (MenuIds.AskPilotHeading, "", "SHDG"),
        (MenuIds.AskPilotSpeed, "", "SSPD"),
        (MenuIds.AskPilotMach, "", "SMACH"),
        (MenuIds.AskPilotPosition, "", "SPOS"),
        (MenuIds.AskPilotExpectedApproach, "", "SEAPP"),
        (MenuIds.CoordinationRelease, "", "RD"),
        (MenuIds.CoordinationHold, "", "RDH"),
        (MenuIds.CoordinationRecall, "", "RDR"),
        (MenuIds.CoordinationAcknowledge, "", "RDACK"),
        (MenuIds.CoordinationReleaseHeld, "", $"REL {Callsign}"),
        (MenuIds.CoordinationCheckReleaseWindow, "", "CFR CHECK"),
        (MenuIds.DataBlockScratchpadClear1, "", "SP1"),
        (MenuIds.DataBlockScratchpadClear2, "", "SP2"),
        (MenuIds.DataBlockAnnotate, "", "ANNOTATE"),
        (MenuIds.SimControlDelete, "", "DEL"),
        (MenuIds.HeadingPresent, "", "FPH"),
        (MenuIds.SpeedNormal, "", "RNS"),
        (MenuIds.SpeedFinalApproach, "", "RFAS"),
        (MenuIds.HoldPresentLeft, "", "HPPL"),
        (MenuIds.HoldPresentRight, "", "HPPR"),
        (MenuIds.ProceduresClimbViaSid, "", "CVIA"),
        (MenuIds.ProceduresDescendViaStar, "", "DVIA"),
        (MenuIds.TowerLineUpAndWait, "", "LUAW"),
        (MenuIds.TowerCancelTakeoff, "", "CTOC"),
        (MenuIds.TowerClearedToLand, "", "CLAND"),
        (MenuIds.TowerForceLanding, "", "CLANDF"),
        (MenuIds.TowerClearedOption, "", "COPT"),
        (MenuIds.TowerTouchAndGo, "", "TG"),
        (MenuIds.TowerStopAndGo, "", "SG"),
        (MenuIds.TowerLowApproach, "", "LA"),
        (MenuIds.TowerGoAround, "", "GA"),
        (MenuIds.TowerCancelLanding, "", "CLC"),
        (MenuIds.PatternTurnCrosswind, "", "TC"),
        (MenuIds.PatternTurnDownwind, "", "TD"),
        (MenuIds.PatternTurnBase, "", "TB"),
        (MenuIds.PatternExtend, "", "EXT"),
        (MenuIds.PatternShortApproach, "", "MSA"),
        (MenuIds.PatternNormalApproach, "", "MNA"),
        (MenuIds.PatternLeft360, "", "L360"),
        (MenuIds.PatternRight360, "", "R360"),
        (MenuIds.PatternLeft270, "", "L270"),
        (MenuIds.PatternRight270, "", "R270"),
        (MenuIds.PatternPlan270, "", "P270"),
        (MenuIds.PatternCancel270, "", "NO270"),
        (MenuIds.PatternCircleAirport, "", "CA"),
        (MenuIds.PatternMakeLeftTraffic, "", "MLT"),
        (MenuIds.PatternMakeRightTraffic, "", "MRT"),
        (MenuIds.NavigationOnCourse, "", "OC"),
        (MenuIds.GroundPushback, "", "PUSH"),
        (MenuIds.GroundHoldPosition, "", "HOLD"),
        (MenuIds.GroundResumeTaxi, "", "RES"),
        (MenuIds.GroundBreakConflict, "", "BREAK"),
        (MenuIds.SpawnNow, "", "SPAWN"),
    ];

    /// <summary>
    /// The entries whose item the host builds rather than the catalog: a popup, an editor, a submenu the catalog
    /// assembles, or a header the host's own state decides; and the entries whose item needs the aircraft or the
    /// previous selection (the held runway, the relative items), which their own tests click.
    /// </summary>
    private static readonly string[] HostBuiltIds =
    [
        // Flyouts with nothing to send without an aircraft (disabled with a reason): their own tests pin their sends.
        MenuIds.ApproachClearedVisual,
        MenuIds.ProceduresJoinStar,
        MenuIds.PatternEnterLeftDownwind,
        MenuIds.PatternEnterRightDownwind,
        MenuIds.PatternEnterLeftBase,
        MenuIds.PatternEnterRightBase,
        MenuIds.PatternEnterFinal,
        MenuIds.FavoritesMenu,
        MenuIds.LiveTrafficAssumeAndTrack,
        MenuIds.SimControlWarp,
        MenuIds.AircraftEditFlightPlan,
        MenuIds.AircraftCommand,
        MenuIds.AircraftNote,
        MenuIds.TowerClearedForTakeoff,
        MenuIds.TowerExitLeft,
        MenuIds.TowerExitRight,
        MenuIds.GroundCrossRunway,
        MenuIds.RelativeReportInSight,
        MenuIds.RelativeFollow,
        MenuIds.GroundRelativeGiveWay,
        MenuIds.GroundRelativeFollow,
        MenuIds.GroundHoldShort,
        MenuIds.GroundFollow,
        MenuIds.GroundGiveWay,
        MenuIds.GroundPushbackFace,
        MenuIds.GroundPushbackTo,
        MenuIds.GroundPushRoute,
        MenuIds.GroundTaxiPreset,
        MenuIds.GroundTaxiToRunway,
        MenuIds.GroundAirTaxi,
        MenuIds.GroundLandHelicopter,
        MenuIds.GroundDrawTaxiRoute,
        // The approach flyout's entries open a panel built from the aircraft; ApproachFlyoutTests clicks their rows.
        MenuIds.ApproachCleared,
        MenuIds.ApproachJoin,
        MenuIds.ApproachClearedStraightIn,
        MenuIds.ApproachJoinStraightIn,
        MenuIds.ApproachClearedForce,
        MenuIds.ApproachJoinForce,
        MenuIds.ApproachJoinFinalCourse,
        MenuIds.ApproachExpect,
        // The heading entries open the heading flyout; HeadingFlyoutTests clicks its rose, box and chips.
        MenuIds.HeadingFly,
        MenuIds.HeadingTurnLeft,
        MenuIds.HeadingTurnRight,
        MenuIds.HeadingTurnLeftDegrees,
        MenuIds.HeadingTurnRightDegrees,
        // The altitude entries open the altitude flyouts; AltitudeFlyoutTests clicks their rows and boxes.
        MenuIds.AltitudeMaintain,
        MenuIds.DataBlockTempAltitude,
        MenuIds.DataBlockCruise,
        // Assign speed and Speed… open the speed flyout; SpeedFlyoutTests clicks its rows, chips and box.
        MenuIds.SpeedAssign,
        MenuIds.SpeedCustom,
        // Direct to… and Append direct to… open the Direct to flyout; DirectToFlyoutTests clicks its rows, toggle and box.
        MenuIds.NavigationDirectTo,
        MenuIds.NavigationAppendDirectTo,
        // Cross fix and Depart fix open their flyouts; CrossFixFlyoutTests and DepartFixFlyoutTests click their rows and boxes.
        MenuIds.ProceduresCrossFix,
        MenuIds.ProceduresDepartFix,
        // Hold at fix… opens the holding-pattern flyout and the two hold-at-fix entries the 360s flyout; HoldFlyoutTests clicks them.
        MenuIds.HoldPattern,
        MenuIds.HoldFixLeft,
        MenuIds.HoldFixRight,
        // Join airway… opens the Join airway flyout and the two join-radial entries the Join radial flyout; JoinAirwayFlyoutTests and
        // JoinRadialFlyoutTests click them.
        MenuIds.ProceduresJoinAirway,
        MenuIds.ProceduresJoinRadialOutbound,
        MenuIds.ProceduresJoinRadialInbound,
        // Initiate handoff… and Point out… open the coordination flyouts; CoordinationFlyoutTests clicks their rows and box.
        MenuIds.TrackInitiateHandoff,
        MenuIds.TrackPointOut,
        // PTAC… opens the PTAC flyout; PtacFlyoutTests clicks its rows and boxes.
        MenuIds.ProceduresPtac,
        // Report traffic in sight… and Follow traffic… open the traffic flyouts over the host's airborne traffic (Report traffic in
        // sight… opens the solo advisory flyout in solo, below); AirborneTrafficFlyoutTests and SoloAdvisoryFlyoutTests click them.
        MenuIds.ApproachReportTrafficInSight,
        MenuIds.PatternFollow,
        // Report field in sight… opens the field advisory flyout over the destination field; FieldAdvisoryFlyoutTests clicks it.
        MenuIds.ApproachReportFieldInSight,
        // Report when… and its aliases At N-mile final… and At fix… open the Report when… flyout; ReportWhenFlyoutTests clicks its
        // rows, chips and boxes.
        MenuIds.ApproachReportWhen,
        MenuIds.ApproachReportNMileFinal,
        MenuIds.ApproachReportAtFix,
        MenuIds.SpawnDelay,
        MenuIds.LiveTrafficAssumeSelected,
        // Squawk… and Ask pilot to say › Custom… open the Squawk and Say flyouts; the Squawk and Say tests below type in their box rows.
        MenuIds.SquawkCode,
        MenuIds.AskPilotCustom,
        // Scratchpad… opens the Scratchpad flyout; the Scratchpad tests below type in its box row and click its Clear rows.
        MenuIds.DataBlockScratchpad,
        // The point entries build only for a point click (MenuClick.Point set), which this table's context never has;
        // PointMenuTests builds them through AircraftMenuBuilder and clicks their items there.
        MenuIds.PointFlyHeading,
        MenuIds.PointDirectTo,
        MenuIds.PointAppendDirectTo,
        MenuIds.PointHoldLeft,
        MenuIds.PointHoldRight,
        MenuIds.PointTaxiHere,
        MenuIds.PointTaxiToRunway,
        MenuIds.PointPushTo,
        MenuIds.PointCustomTaxi,
        MenuIds.PointWarpHere,
    ];

    public static TheoryData<string, string, string> SingleCommandLeaves()
    {
        var data = new TheoryData<string, string, string>();
        foreach ((string id, string input, string command) in Expected)
        {
            data.Add(id, input, command);
        }

        return data;
    }

    private static MenuContext Context() => Context(VfrCommandsForIfr.EnterFinalOnly);

    /// <summary>A menu context under <paramref name="mode"/>, outside solo training, with no previous selection.</summary>
    private static MenuContext Context(VfrCommandsForIfr mode) => TestMenuContext.Create(Callsign, Initials, null, false, mode);

    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    [AvaloniaTheory]
    [MemberData(nameof(SingleCommandLeaves))]
    public void CatalogLeaf_Click_SendsItsCommand(string id, string input, string command)
    {
        var host = new RecordingMenuHost(input);
        MenuItem? item = MenuCatalog.Get(id).Build(null, Context(), host);

        Assert.NotNull(item);
        Click((MenuItem)item);

        Assert.Equal([(Callsign, command, Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void CommandTable_CoversEveryCatalogEntryButTheHostBuiltOnes()
    {
        IEnumerable<string> covered = Expected.Select(e => e.Id).Concat(HostBuiltIds);
        Assert.Equal(MenuCatalog.All.Select(e => e.Id).Order(StringComparer.Ordinal), covered.Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(SingleCommandLeaves))]
    public void AliasTable_SingleCommandLeaf_HasItsCommandsFirstTokenAsItsOnlyVerb(string id, string input, string command)
    {
        _ = input;
        Assert.Equal([command.Split(' ')[0]], MenuAliases.CanonicalVerbs[id]);
    }

    [AvaloniaFact]
    public void AssumeAndTrack_Click_SendsAssumeThenTrack()
    {
        var host = new RecordingMenuHost("");
        MenuItem? item = MenuCatalog.Get(MenuIds.LiveTrafficAssumeAndTrack).Build(null, Context(), host);

        Assert.NotNull(item);
        Click((MenuItem)item);

        Assert.Equal([(Callsign, "ASSUME", Initials), (Callsign, "TRACK", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void AssumeSelected_TwoShadows_AddsSeparatorThenItemThatAssumesThem()
    {
        var host = new RecordingMenuHost("");
        var menu = new ContextMenu();

        SharedMenuGroups.AddAssumeSelected(menu, ShadowSelectionContext("SWA1", "SWA2"), host);

        Assert.Equal(2, menu.Items.Count);
        Assert.IsType<Separator>(menu.Items[0]);
        MenuItem item = Assert.IsType<MenuItem>(menu.Items[1]);
        Assert.Equal("Assume selected live traffic (2)", item.Header);

        Click(item);

        IReadOnlyList<string> assumed = Assert.Single(host.AssumeSelectedCalls);
        Assert.Equal(2, assumed.Count);
        Assert.Equal("SWA1", assumed[0]);
        Assert.Equal("SWA2", assumed[1]);
    }

    [AvaloniaFact]
    public void AssumeSelected_ThreeShadows_LabelNamesThreeAndAssumesAllThree()
    {
        var host = new RecordingMenuHost("");
        var menu = new ContextMenu();

        SharedMenuGroups.AddAssumeSelected(menu, ShadowSelectionContext("SWA1", "SWA2", "SWA3"), host);

        Assert.Equal(2, menu.Items.Count);
        Assert.IsType<Separator>(menu.Items[0]);
        MenuItem item = Assert.IsType<MenuItem>(menu.Items[1]);
        Assert.Equal("Assume selected live traffic (3)", item.Header);

        Click(item);

        IReadOnlyList<string> assumed = Assert.Single(host.AssumeSelectedCalls);
        Assert.Equal(3, assumed.Count);
        Assert.Equal("SWA1", assumed[0]);
        Assert.Equal("SWA2", assumed[1]);
        Assert.Equal("SWA3", assumed[2]);
    }

    [AvaloniaFact]
    public void AssumeSelected_OneShadow_AddsNothing()
    {
        var host = new RecordingMenuHost("");
        var menu = new ContextMenu();

        SharedMenuGroups.AddAssumeSelected(menu, ShadowSelectionContext("SWA1"), host);

        Assert.Empty(menu.Items);
        Assert.Empty(host.AssumeSelectedCalls);
    }

    [AvaloniaFact]
    public void SquawkGroup_Ident_SendsIdent()
    {
        var host = new RecordingMenuHost("");
        MenuItem squawk = SharedMenuGroups.Squawk(null, Context(), host);
        MenuItem ident = Assert.Single(squawk.Items.OfType<MenuItem>(), i => i.Header?.ToString() == "Ident");

        Click(ident);

        Assert.Equal([(Callsign, "IDENT", Initials)], host.Sent);
    }

    /// <summary>Only the fix typed: Enter in the FRD box sends <c>WARP {frd}</c>, the sim keeping the rest.</summary>
    [AvaloniaFact]
    public void Warp_TypedFrdOnly_SendsWarpFrd()
    {
        var host = new RecordingMenuHost("");
        IReadOnlyList<InlineBoxRow> rows = WarpRows(Warp(WarpAircraft(), host));

        rows[0].Text = "SFO";
        PressEnter(rows[0].Input);

        Assert.Equal([(Callsign, "WARP SFO", Initials)], host.Sent);
    }

    /// <summary>
    /// A blank heading and altitude before a typed speed are sent as the present values (the sim fills its slots left to right),
    /// each shown as kept in its command column.
    /// </summary>
    [AvaloniaFact]
    public void Warp_SpeedOnly_FillsHeadingAndAltitudeWithPresentValues()
    {
        var host = new RecordingMenuHost("");
        IReadOnlyList<InlineBoxRow> rows = WarpRows(Warp(WarpAircraft(), host));

        rows[0].Text = "SFO";
        rows[3].Text = "230";

        Assert.Equal("270", rows[1].CommandColumn.Text);
        Assert.Equal("110", rows[2].CommandColumn.Text);
        PressEnter(rows[3].Input);

        Assert.Equal([(Callsign, "WARP SFO 270 110 230", Initials)], host.Sent);
    }

    /// <summary>A pattern aircraft at 800 ft keeps its altitude as the sim's hundreds, <c>8</c>, never <c>800</c> (80,000 ft).</summary>
    [AvaloniaFact]
    public void Warp_At800Feet_SendsTheAltitudeAsHundreds()
    {
        var host = new RecordingMenuHost("");
        var aircraft = new FakeMenuAircraft
        {
            HeadingDegrees = 130,
            AltitudeFeet = 800,
            IndicatedAirspeedKnots = 85,
        };
        IReadOnlyList<InlineBoxRow> rows = WarpRows(Warp(aircraft, host));

        rows[0].Text = "SFO";
        rows[3].Text = "90";
        PressEnter(rows[3].Input);

        Assert.Equal([(Callsign, "WARP SFO 130 8 90", Initials)], host.Sent);
    }

    /// <summary>A zero present heading is kept as 360: WARP needs a heading from 001 to 360.</summary>
    [AvaloniaFact]
    public void Warp_ZeroHeading_KeepsAs360()
    {
        var host = new RecordingMenuHost("");
        var aircraft = new FakeMenuAircraft
        {
            HeadingDegrees = 0,
            AltitudeFeet = 5000,
            IndicatedAirspeedKnots = 250,
        };
        IReadOnlyList<InlineBoxRow> rows = WarpRows(Warp(aircraft, host));

        rows[0].Text = "SFO";
        rows[3].Text = "200";
        PressEnter(rows[3].Input);

        Assert.Equal([(Callsign, "WARP SFO 360 50 200", Initials)], host.Sent);
    }

    /// <summary>The heading kept for a blank Heading box is the aircraft's magnetic heading, as WARP reads its heading.</summary>
    [AvaloniaFact]
    public void Warp_PresentHeadingIsMagnetic_NotTrue()
    {
        AircraftModel aircraft = OverKoakOnTrueHeading100();
        int heading = int.Parse(aircraft.HeadingDisplay, CultureInfo.InvariantCulture);
        Assert.NotEqual(100, heading);
        var host = new RecordingMenuHost("");
        IReadOnlyList<InlineBoxRow> rows = WarpRows(Warp(aircraft, host));

        rows[0].Text = "SFO";
        rows[2].Text = "50";
        PressEnter(rows[2].Input);

        Assert.Equal([(Callsign, $"WARP SFO {heading:D3} 50", Initials)], host.Sent);
    }

    /// <summary>The Altitude box reads as the menu's other altitude boxes do: hundreds, feet or a flight level.</summary>
    [AvaloniaTheory]
    [InlineData("110")]
    [InlineData("11000")]
    [InlineData("FL110")]
    public void Warp_AltitudeBoxReadsLikeTheOtherAltitudeBoxes(string typed)
    {
        var host = new RecordingMenuHost("");
        IReadOnlyList<InlineBoxRow> rows = WarpRows(Warp(WarpAircraft(), host));

        rows[0].Text = "SFO";
        rows[2].Text = typed;
        PressEnter(rows[2].Input);

        Assert.Equal([(Callsign, "WARP SFO 270 110", Initials)], host.Sent);
    }

    /// <summary>A fix the navigation data does not know is refused inline under the FRD box; Enter in any box sends nothing.</summary>
    [AvaloniaFact]
    public void Warp_UnknownFix_RefusesInline_AndEnterSendsNothing()
    {
        TestVnasData.EnsureInitialized();
        var host = new RecordingMenuHost("");
        MenuItem warp = Warp(WarpAircraft(), host);
        IReadOnlyList<InlineBoxRow> rows = WarpRows(warp);

        rows[0].Text = "QQQZZ";
        rows[3].Text = "230";

        Assert.Contains(RefusableBox.RefusedClass, rows[0].Input.Classes);
        Assert.True(rows[0].Refusal.ReasonLine.IsVisible, "An unknown fix should show its reason line.");
        Assert.Equal(WarpFlyout.UnknownFix, rows[0].Refusal.ReasonLine.Text);
        Assert.Equal("✕", rows[0].CommandColumn.Text);
        Assert.Equal(InlineBoxRow.CantSend, WarpSendLine(warp).CommandColumn.Text);

        PressEnter(rows[0].Input);
        PressEnter(rows[3].Input);

        Assert.Empty(host.Sent);
    }

    /// <summary>A heading outside 001-360 is refused inline under the Heading box; Enter sends nothing.</summary>
    [AvaloniaFact]
    public void Warp_BadHeading_RefusesInline()
    {
        var host = new RecordingMenuHost("");
        IReadOnlyList<InlineBoxRow> rows = WarpRows(Warp(WarpAircraft(), host));

        rows[0].Text = "SFO";
        rows[1].Text = "400";

        Assert.Contains(RefusableBox.RefusedClass, rows[1].Input.Classes);
        Assert.Equal(WarpFlyout.InvalidHeading, rows[1].Refusal.ReasonLine.Text);
        Assert.Equal("✕", rows[1].CommandColumn.Text);

        PressEnter(rows[1].Input);
        PressEnter(rows[0].Input);

        Assert.Empty(host.Sent);
    }

    /// <summary>With the FRD box blank there is nowhere to warp to: Enter in any box sends nothing.</summary>
    [AvaloniaFact]
    public void Warp_BlankFrd_EnterSendsNothing()
    {
        var host = new RecordingMenuHost("");
        MenuItem warp = Warp(WarpAircraft(), host);
        IReadOnlyList<InlineBoxRow> rows = WarpRows(warp);

        rows[3].Text = "230";
        PressEnter(rows[3].Input);
        PressEnter(rows[0].Input);
        Click(Assert.Single(warp.Items.OfType<MenuItem>(), m => m.Header is WarpSendLine));

        Assert.Empty(host.Sent);
    }

    /// <summary>
    /// An aircraft at 9 ft (a helicopter just lifted off) has no altitude word the sim reads back as its present altitude: a
    /// blank Altitude before a typed Speed is refused, and Enter sends nothing.
    /// </summary>
    [AvaloniaFact]
    public void Warp_BlankAltitudeBeforeSpeed_PresentBelow100Ft_RefusesAndSendsNothing()
    {
        var host = new RecordingMenuHost("");
        var aircraft = new FakeMenuAircraft
        {
            HeadingDegrees = 90,
            AltitudeFeet = 9,
            IndicatedAirspeedKnots = 0,
        };
        MenuItem warp = Warp(aircraft, host);
        IReadOnlyList<InlineBoxRow> rows = WarpRows(warp);

        rows[0].Text = "SFO";
        rows[3].Text = "120";
        PressEnter(rows[3].Input);

        Assert.Empty(host.Sent);
        Assert.True(rows[2].Refusal.ReasonLine.IsVisible, "A blank altitude with no kept word should show its reason line.");
        Assert.Equal(
            "Blank before a filled field, and the present 9 ft has no word the sim reads as it: type an altitude",
            rows[2].Refusal.ReasonLine.Text
        );
        Assert.Equal(InlineBoxRow.CantSend, WarpSendLine(warp).CommandColumn.Text);
    }

    /// <summary>A click on a field row focuses that row's box and keeps the menu open; it never sends, even with a command ready.</summary>
    [AvaloniaFact]
    public void Warp_ClickOnAFieldRow_FocusesItsBoxAndSendsNothing()
    {
        var window = new Window { Width = 600, Height = 900 };
        window.Show();
        var host = new RecordingMenuHost("");
        MenuItem warp = Warp(WarpAircraft(), host);
        var menu = new ContextMenu { Items = { warp } };
        try
        {
            menu.Open(window);
            Dispatcher.UIThread.RunJobs();
            warp.IsSubMenuOpen = true;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            IReadOnlyList<InlineBoxRow> rows = WarpRows(warp);
            rows[0].Text = "SFO";
            rows[3].Text = "230";

            Click(rows[1].Item);
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(host.Sent);
            Assert.True(BoxOf(rows[1]).IsFocused, "a click on the Heading row did not focus its box");
            Assert.True(rows[1].Item.StaysOpenOnClick, "a click on a field row should leave the menu open");
            Assert.True(menu.IsOpen);
        }
        finally
        {
            menu.Close();
            window.Close();
        }
    }

    /// <summary>A click on the send line sends the whole command.</summary>
    [AvaloniaFact]
    public void Warp_ClickOnTheSendLine_SendsTheWholeCommand()
    {
        var host = new RecordingMenuHost("");
        MenuItem warp = Warp(WarpAircraft(), host);
        IReadOnlyList<InlineBoxRow> rows = WarpRows(warp);

        rows[0].Text = "SFO";
        rows[2].Text = "90";
        Click(Assert.Single(warp.Items.OfType<MenuItem>(), m => m.Header is WarpSendLine));

        Assert.Equal([(Callsign, "WARP SFO 270 90", Initials)], host.Sent);
    }

    /// <summary>A typed altitude is read back under its box: <c>800</c> is 80,000 ft, the way the menu's altitude boxes read it.</summary>
    [AvaloniaFact]
    public void Warp_TypedAltitude_ShowsItsReadingUnderTheBox()
    {
        IReadOnlyList<InlineBoxRow> rows = WarpRows(Warp(PatternAircraft(), new RecordingMenuHost("")));

        rows[2].Text = "800";

        Assert.True(rows[2].NoteLine.IsVisible, "a typed altitude should show its reading under the box");
        Assert.Equal("→ 80,000 ft", rows[2].NoteLine.Text);
        Assert.Equal("800", rows[2].CommandColumn.Text);
    }

    /// <summary>Blanks after the last filled field are left out: <c>—</c> in the command column, the line naming what the sim keeps.</summary>
    [AvaloniaFact]
    public void Warp_TrailingBlank_ShowsDashAndLeftOutLine()
    {
        MenuItem warp = Warp(PatternAircraft(), new RecordingMenuHost(""));
        IReadOnlyList<InlineBoxRow> rows = WarpRows(warp);

        rows[0].Text = "SFO";
        rows[1].Text = "90";

        Assert.Equal(InlineBoxRow.LeftOutMark, rows[2].CommandColumn.Text);
        Assert.Equal("left out: the sim keeps 800 ft", rows[2].NoteLine.Text);
        Assert.Equal(InlineBoxRow.LeftOutMark, rows[3].CommandColumn.Text);
        Assert.Equal("left out: the sim keeps 85 kt", rows[3].NoteLine.Text);
        Assert.Equal("WARP SFO 090", WarpSendLine(warp).CommandColumn.Text);
    }

    /// <summary>A blank field before a filled one shows the present value it keeps, in amber, and the line saying why.</summary>
    [AvaloniaFact]
    public void Warp_BlankBeforeFilled_ShowsKeptValueAndKeptLine()
    {
        IReadOnlyList<InlineBoxRow> rows = WarpRows(Warp(PatternAircraft(), new RecordingMenuHost("")));

        rows[0].Text = "SFO";
        rows[3].Text = "90";

        Assert.Equal("130", rows[1].CommandColumn.Text);
        Assert.Equal(FlyoutPanelParts.KeptBrush, rows[1].CommandColumn.Foreground);
        Assert.Equal("kept: 130 sent in its place, since a gap would shift the slots", rows[1].NoteLine.Text);
        Assert.Equal("8", rows[2].CommandColumn.Text);
        Assert.Equal("kept: 8 sent in its place, since a gap would shift the slots", rows[2].NoteLine.Text);
    }

    /// <summary>A blank FRD box waits, with no refusal under it, while the send line says it can't send.</summary>
    [AvaloniaFact]
    public void Warp_BlankFrd_SendLineSaysCantSend()
    {
        MenuItem warp = Warp(WarpAircraft(), new RecordingMenuHost(""));
        IReadOnlyList<InlineBoxRow> rows = WarpRows(warp);

        rows[3].Text = "230";

        Assert.False(rows[0].Refusal.ReasonLine.IsVisible, "a blank FRD box should wait, not refuse");
        Assert.Equal(InlineBoxRow.CantSend, WarpSendLine(warp).CommandColumn.Text);
        Assert.Equal(FlyoutPanelParts.RefusalBrush, WarpSendLine(warp).CommandColumn.Foreground);
    }

    /// <summary>A refused field shows <c>✕</c> in its own command column; <c>can't send</c> is the send line's alone.</summary>
    [AvaloniaFact]
    public void Warp_RefusedHeading_ShowsCrossInItsColumnAndCantSendOnTheSendLine()
    {
        MenuItem warp = Warp(WarpAircraft(), new RecordingMenuHost(""));
        IReadOnlyList<InlineBoxRow> rows = WarpRows(warp);

        rows[0].Text = "SFO";
        rows[1].Text = "400";

        Assert.Equal("✕", rows[1].CommandColumn.Text);
        Assert.Equal(FlyoutPanelParts.RefusalBrush, rows[1].CommandColumn.Foreground);
        Assert.Equal(InlineBoxRow.CantSend, WarpSendLine(warp).CommandColumn.Text);
    }

    /// <summary>A partial fix the suggestions still offer names for waits: no refusal under the FRD box, the send line can't send.</summary>
    [AvaloniaFact]
    public void Warp_PartialFrdWithSuggestions_ShowsNoRefusalYet()
    {
        TestVnasData.EnsureInitialized();
        var host = new RecordingMenuHost("") { FixSuggestions = ["SUNOL"] };
        MenuItem warp = Warp(WarpAircraft(), host);
        IReadOnlyList<InlineBoxRow> rows = WarpRows(warp);

        rows[0].Text = "SUNO";

        Assert.False(rows[0].Refusal.ReasonLine.IsVisible, "a partial fix with suggestions should not be refused yet");
        Assert.DoesNotContain(RefusableBox.RefusedClass, rows[0].Input.Classes);
        Assert.Equal(TakeoffFlyout.Unfilled, rows[0].CommandColumn.Text);
        Assert.Equal(InlineBoxRow.CantSend, WarpSendLine(warp).CommandColumn.Text);
    }

    /// <summary>
    /// A fix-radial-distance the FRD box reads is read back under the box in green; a plain fix reads as <c>{fix} · fix</c>
    /// (<c>SFO · fix</c>).
    /// </summary>
    [AvaloniaFact]
    public void Warp_ReadingFrd_ShowsItsReadingUnderTheBox()
    {
        TestVnasData.EnsureInitialized();
        IReadOnlyList<InlineBoxRow> rows = WarpRows(Warp(WarpAircraft(), new RecordingMenuHost("")));

        rows[0].Text = "OAK090010";

        Assert.True(rows[0].NoteLine.IsVisible, "a reading FRD should show its reading under the box");
        Assert.Equal("10 NM on the 090° radial of OAK (magnetic)", rows[0].NoteLine.Text);
        Assert.Equal(FlyoutPanelParts.ReadingBrush, rows[0].NoteLine.Foreground);

        rows[0].Text = "SFO";

        Assert.True(rows[0].NoteLine.IsVisible, "a plain fix should read as SFO · fix");
        Assert.Equal("SFO · fix", rows[0].NoteLine.Text);
    }

    /// <summary>Warp… is not offered on the ground: WARP leaves the aircraft airborne; Warp here at a taxi node is the ground warp.</summary>
    [AvaloniaFact]
    public void Warp_HiddenOnTheGround()
    {
        MenuCatalogEntry warp = MenuCatalog.Get(MenuIds.SimControlWarp);

        Assert.False(warp.IsApplicable(new FakeMenuAircraft { IsOnGround = true }, Context()));
        Assert.True(warp.IsApplicable(WarpAircraft(), Context()));
    }

    /// <summary>Warp here at a map point is not offered for an aircraft on the ground.</summary>
    [AvaloniaFact]
    public void WarpHere_AtAMapPoint_HiddenOnTheGround()
    {
        MenuCatalogEntry warpHere = MenuCatalog.Get(MenuIds.PointWarpHere);
        MenuContext context = PointContext(new MenuPoint(new LatLon(37.6, -121.9), null, null, [], null));

        Assert.False(warpHere.IsApplicable(new FakeMenuAircraft { IsOnGround = true }, context));
        Assert.True(warpHere.IsApplicable(WarpAircraft(), context));
    }

    /// <summary>Warp here at a taxi node stays the ground warp, <c>WARPG #node</c>, for an aircraft on the ground.</summary>
    [AvaloniaFact]
    public void WarpHere_AtATaxiNode_SendsWarpgOnTheGround()
    {
        var host = new RecordingMenuHost("");
        var node = new GroundNodeDto(7, 37.7210, -122.2200, "TaxiwayIntersection", null, null, null);
        MenuContext context = PointContext(new MenuPoint(new LatLon(node.Latitude, node.Longitude), node, null, [], null));
        var aircraft = new FakeMenuAircraft { IsOnGround = true };
        MenuCatalogEntry warpHere = MenuCatalog.Get(MenuIds.PointWarpHere);

        Assert.True(warpHere.IsApplicable(aircraft, context));
        MenuItem item = Assert.IsType<MenuItem>(warpHere.Build(aircraft, context, host));
        Assert.Equal("Warp here", item.Header);
        Click(item);

        Assert.Equal([(Callsign, "WARPG #7", Initials)], host.Sent);
    }

    /// <summary>Warp here at a taxi node shows only the verb it sends, <c>WARPG</c>, and the row's alias pass keeps it.</summary>
    [AvaloniaFact]
    public void WarpHere_AtATaxiNode_AliasIsWarpgAlone()
    {
        var node = new GroundNodeDto(7, 37.7210, -122.2200, "TaxiwayIntersection", null, null, null);
        MenuContext context = PointContext(new MenuPoint(new LatLon(node.Latitude, node.Longitude), node, null, [], null));
        MenuItem item = Assert.IsType<MenuItem>(
            MenuCatalog.Get(MenuIds.PointWarpHere).Build(new FakeMenuAircraft { IsOnGround = true }, context, new RecordingMenuHost(""))
        );

        MenuAlias.Apply(MenuIds.PointWarpHere, item, context);

        Assert.Equal("WARPG", MenuAlias.GetText(item));
    }

    /// <summary>Warp here at a map point opens the Warp flyout and shows only the verb it sends, <c>WARP</c>.</summary>
    [AvaloniaFact]
    public void WarpHere_AtAMapPoint_AliasIsWarpAlone()
    {
        MenuItem warpHere = WarpHere(new RecordingMenuHost("") { PointDescription = "OAK090010" });

        Assert.Equal("WARP", MenuAlias.GetText(warpHere));
    }

    /// <summary>Warp here at a map point opens the Warp flyout with the clicked point's FRD in the FRD box; Enter sends it.</summary>
    [AvaloniaFact]
    public void WarpHere_SeedsTheClickedFrd()
    {
        var host = new RecordingMenuHost("") { PointDescription = "OAK090010" };
        MenuItem warpHere = WarpHere(host);

        Assert.Equal("Warp here (OAK090010)", warpHere.Header);
        IReadOnlyList<InlineBoxRow> rows = WarpRows(warpHere);
        Assert.Equal("OAK090010", rows[0].Text);
        PressEnter(rows[0].Input);

        Assert.Equal([(Callsign, "WARP OAK090010", Initials)], host.Sent);
    }

    /// <summary>The FRD box, a fix box with suggestions, takes the focus as the flyout opens, its Warp here seed selected.</summary>
    [AvaloniaFact]
    public void WarpHere_FrdBox_TakesTheFocusOnOpen_WithTheSeedSelected()
    {
        var window = new Window { Width = 600, Height = 900 };
        window.Show();
        var host = new RecordingMenuHost("") { PointDescription = "OAK090010" };
        MenuItem warpHere = WarpHere(host);
        var menu = new ContextMenu { Items = { warpHere } };
        try
        {
            menu.Open(window);
            Dispatcher.UIThread.RunJobs();
            warpHere.IsSubMenuOpen = true;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            InlineBoxRow frd = WarpRows(warpHere)[0];
            AutoCompleteBox fixBox = Assert.IsAssignableFrom<AutoCompleteBox>(frd.Input);
            TextBox inner = fixBox.GetVisualDescendants().OfType<TextBox>().First();
            Assert.True(inner.IsFocused, "the FRD box did not take focus when the Warp flyout opened");
            Assert.Equal((0, 9), (Math.Min(inner.SelectionStart, inner.SelectionEnd), Math.Max(inner.SelectionStart, inner.SelectionEnd)));
        }
        finally
        {
            menu.Close();
            window.Close();
        }
    }

    /// <summary>An airborne aircraft on magnetic heading 270 at 11,000 ft and 250 knots.</summary>
    private static FakeMenuAircraft WarpAircraft() =>
        new()
        {
            HeadingDegrees = 270,
            AltitudeFeet = 11000,
            IndicatedAirspeedKnots = 250,
        };

    /// <summary>A pattern aircraft on magnetic heading 130 at 800 ft and 85 knots.</summary>
    private static FakeMenuAircraft PatternAircraft() =>
        new()
        {
            HeadingDegrees = 130,
            AltitudeFeet = 800,
            IndicatedAirspeedKnots = 85,
        };

    private static MenuItem Warp(IMenuAircraft aircraft, RecordingMenuHost host)
    {
        TestVnasData.EnsureInitialized();
        return Assert.IsType<MenuItem>(MenuCatalog.Get(MenuIds.SimControlWarp).Build(aircraft, Context(), host));
    }

    private static MenuItem WarpHere(RecordingMenuHost host)
    {
        TestVnasData.EnsureInitialized();
        return Assert.IsType<MenuItem>(
            MenuCatalog
                .Get(MenuIds.PointWarpHere)
                .Build(WarpAircraft(), PointContext(new MenuPoint(new LatLon(37.6, -121.9), null, null, [], null)), host)
        );
    }

    private static MenuContext PointContext(MenuPoint point) => Context() with { Click = new MenuClick(Callsign, null, point, []) };

    /// <summary>The Warp flyout's box rows, in order: FRD, Heading, Altitude, Speed.</summary>
    private static List<InlineBoxRow> WarpRows(MenuItem warp)
    {
        List<InlineBoxRow> rows = [.. warp.Items.OfType<MenuItem>().Select(m => m.Header).OfType<InlineBoxRow>()];
        Assert.Equal(4, rows.Count);
        return rows;
    }

    private static WarpSendLine WarpSendLine(MenuItem warp) => warp.Items.OfType<MenuItem>().Select(m => m.Header).OfType<WarpSendLine>().Single();

    [AvaloniaFact]
    public void EditFlightPlan_CallsHostOpenFlightPlanEditor()
    {
        var host = new RecordingMenuHost("");
        MenuItem? item = MenuCatalog.Get(MenuIds.AircraftEditFlightPlan).Build(new FakeMenuAircraft(), Context(), host);

        Assert.NotNull(item);
        Click((MenuItem)item);

        Assert.Equal(1, host.FlightPlanEditorOpens);
    }

    [AvaloniaFact]
    public void Delete_SendsDel()
    {
        var host = new RecordingMenuHost("");
        MenuItem? item = MenuCatalog.Get(MenuIds.SimControlDelete).Build(null, Context(), host);

        Assert.NotNull(item);
        Click((MenuItem)item);

        Assert.Equal([(Callsign, "DEL", Initials)], host.Sent);
    }

    [AvaloniaTheory]
    [InlineData(MenuIds.HeadingFly)]
    [InlineData(MenuIds.HeadingTurnLeft)]
    [InlineData(MenuIds.HeadingTurnRight)]
    public void HeadingFlyout_ShowsTheMagneticHeading_NotTheTrueOne(string id)
    {
        AircraftModel aircraft = OverKoakOnTrueHeading100();
        double magnetic = aircraft.Heading.ToMagnetic(MagneticDeclination.GetDeclination(aircraft.Position)).Degrees;
        Assert.NotEqual(100, (int)(Math.Round(magnetic / 5) * 5));
        var host = new RecordingMenuHost("");

        MenuItem item = Assert.IsType<MenuItem>(MenuCatalog.Get(id).Build(aircraft, Context(), host));
        item.IsSubMenuOpen = true;
        HeadingFlyoutPanel panel = HeadingFlyoutTests.PanelOf(item);

        Assert.Equal(magnetic, panel.Rose.PresentHeading, 6);
    }

    /// <summary>An aircraft over KOAK on true heading 100, which the local declination puts more than five degrees off magnetic.</summary>
    private static AircraftModel OverKoakOnTrueHeading100() =>
        new()
        {
            Callsign = Callsign,
            Position = new LatLon(37.7213, -122.2208),
            Heading = new TrueHeading(100),
        };

    [AvaloniaFact]
    public void HeadingGroup_HeaderPrefersNavigatingTo_ThenTheAssignedMagneticHeading()
    {
        var host = new RecordingMenuHost("");
        var assigned = new MagneticHeading(270);

        Assert.Equal("Heading", SharedMenuGroups.Heading(new FakeMenuAircraft(), Context(), host).Header as string);
        Assert.Equal(
            "Heading (→ 270)",
            SharedMenuGroups.Heading(new FakeMenuAircraft { AssignedHeading = assigned }, Context(), host).Header as string
        );
        Assert.Equal(
            "Heading (→ SUNOL)",
            SharedMenuGroups.Heading(new FakeMenuAircraft { AssignedHeading = assigned, NavigatingTo = Fix }, Context(), host).Header as string
        );
    }

    [AvaloniaTheory]
    [InlineData(10000.0, "FL350", "CM 35000")]
    [InlineData(10000.0, "5,000", "DM 5000")]
    [InlineData(10000.0, "10,000", "CM 10000")]
    public void MaintainAltitude_ClimbsAboveTheCurrentAltitude_DescendsBelowIt(double altitude, string pick, string command)
    {
        var host = new RecordingMenuHost("");
        MenuRichList popup = OpenMaintainAndPick(new FakeMenuAircraft { AltitudeFeet = altitude }, host, pick);

        Assert.Equal("10,000", popup.Rows[popup.SelectedIndex].Label);
        Assert.Equal([(Callsign, command, Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void MaintainAltitude_ListsFromTheDestinationsFieldElevation_In100sThen500s()
    {
        var host = new RecordingMenuHost("") { FieldElevation = 4321 };
        var aircraft = new FakeMenuAircraft { AltitudeFeet = 5000, Destination = "KXYZ" };
        MenuRichList popup = OpenMaintainAndPick(aircraft, host, "9,500");

        Assert.Equal(["KXYZ"], host.FieldElevationRequests);
        List<string> labels = [.. popup.Rows.Select(row => row.Label)];
        Assert.Equal("FL600", labels[0]);
        Assert.Equal("4,400", labels[^1]);
        int last100 = labels.IndexOf("9,300");
        Assert.True(last100 >= 1, "9,300 (the last 100-ft step below field + 5,000 ft) is listed");
        Assert.Equal("9,500", labels[last100 - 1]);
        Assert.Equal("9,200", labels[last100 + 1]);
        Assert.Equal([(Callsign, "CM 9500", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void MaintainFlyout_TitleLineNamesTheVerbAndTheAltitudes()
    {
        MenuRichList assigned = OpenMaintain(new FakeMenuAircraft { AltitudeFeet = 3040, AssignedAltitude = 5000 }, new RecordingMenuHost(""));
        MenuRichList unassigned = OpenMaintain(new FakeMenuAircraft { AltitudeFeet = 24960 }, new RecordingMenuHost(""));

        Assert.Equal("Maintain altitude", assigned.Title);
        Assert.Equal("now 3,000 · assigned 5,000", assigned.Subtitle);
        Assert.Equal("Maintain altitude", unassigned.Title);
        Assert.Equal("now FL250", unassigned.Subtitle);
    }

    [AvaloniaFact]
    public void MaintainFlyout_OpensOnTheAssignedRow()
    {
        MenuRichList popup = OpenMaintain(new FakeMenuAircraft { AltitudeFeet = 3000, AssignedAltitude = 35000 }, new RecordingMenuHost(""));

        MenuRichRow selected = popup.Rows[popup.SelectedIndex];
        Assert.Equal("FL350", selected.Label);
        Assert.Equal(MenuRichRowKind.Assigned, selected.Kind);
    }

    [AvaloniaFact]
    public void MaintainFlyout_OpensOnTheCurrentRowWhenUnassigned()
    {
        MenuRichList popup = OpenMaintain(new FakeMenuAircraft { AltitudeFeet = 3040 }, new RecordingMenuHost(""));

        MenuRichRow selected = popup.Rows[popup.SelectedIndex];
        Assert.Equal("3,000", selected.Label);
        Assert.Equal(MenuRichRowKind.Now, selected.Kind);
    }

    [AvaloniaFact]
    public void MaintainFlyout_MarksNowAndAssignedRows()
    {
        MenuRichList apart = OpenMaintain(new FakeMenuAircraft { AltitudeFeet = 3000, AssignedAltitude = 5000 }, new RecordingMenuHost(""));
        MenuRichList same = OpenMaintain(new FakeMenuAircraft { AltitudeFeet = 3000, AssignedAltitude = 3000 }, new RecordingMenuHost(""));

        Assert.Equal(new MenuRichRow("●", "3,000", "now", MenuRichRowKind.Now, "CM 3000"), Row(apart, "3,000"));
        Assert.Equal(new MenuRichRow("◆", "5,000", "assigned", MenuRichRowKind.Assigned, "CM 5000"), Row(apart, "5,000"));
        Assert.Equal(new MenuRichRow("↑", "4,000", "CM 4000", MenuRichRowKind.Climb, "CM 4000"), Row(apart, "4,000"));
        Assert.Equal(new MenuRichRow("↓", "2,000", "DM 2000", MenuRichRowKind.Descend, "DM 2000"), Row(apart, "2,000"));
        Assert.Equal(new MenuRichRow("●", "3,000", "now · assigned", MenuRichRowKind.NowAssigned, "CM 3000"), Row(same, "3,000"));
        Assert.DoesNotContain(same.Rows, row => row.Kind is MenuRichRowKind.Assigned or MenuRichRowKind.Now);
    }

    [AvaloniaTheory]
    [InlineData(5000.0, "CM 3000")]
    [InlineData(1000.0, "DM 3000")]
    [InlineData(null, "CM 3000")]
    public void MaintainFlyout_NowRowSendsTowardTheAssignment(double? assigned, string command)
    {
        var host = new RecordingMenuHost("");
        OpenMaintainAndPick(new FakeMenuAircraft { AltitudeFeet = 3000, AssignedAltitude = assigned }, host, "3,000");

        Assert.Equal([(Callsign, command, Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void MaintainFlyout_StopsAtTheTypeCeiling()
    {
        TestVnasData.EnsureInitialized();
        double? ceiling = AircraftPerformance.Ceiling("C172");
        Assert.NotNull(ceiling);
        Assert.True(ceiling.Value is > 5000 and < 60000, $"C172's ceiling {ceiling} sits inside the 500-ft steps");
        int highestStep = (int)(Math.Floor(ceiling.Value / 500) * 500);

        MenuRichList popup = OpenMaintain(new FakeMenuAircraft { AltitudeFeet = 3000, FiledAircraftType = "C172" }, new RecordingMenuHost(""));

        Assert.Equal(highestStep, AltitudeOf(popup.Rows[0]));
        Assert.All(popup.Rows, row => Assert.True(AltitudeOf(row) <= ceiling.Value, $"{row.Label} is above the ceiling {ceiling}"));
    }

    [AvaloniaFact]
    public void MaintainFlyout_TypeWithoutProfileKeepsTheSixtyThousandCap()
    {
        TestVnasData.EnsureInitialized();
        Assert.Null(AircraftPerformance.Ceiling("ZZZZ"));

        MenuRichList popup = OpenMaintain(new FakeMenuAircraft { AltitudeFeet = 3000, FiledAircraftType = "ZZZZ" }, new RecordingMenuHost(""));

        Assert.Equal("FL600", popup.Rows[0].Label);
    }

    [AvaloniaFact]
    public void MaintainFlyout_DrawsTheMvaLineBetweenTheRightRows()
    {
        var host = new RecordingMenuHost("") { Mva = ("12", 2600) };
        MenuRichList popup = OpenMaintain(new FakeMenuAircraft { AltitudeFeet = 3000 }, host);

        int line = Assert.Single(Enumerable.Range(0, popup.Rows.Count), i => popup.Rows[i].Kind == MenuRichRowKind.MvaLine);
        Assert.Equal("MVA 2,600 here (sector 12)", popup.Rows[line].Label);
        Assert.Null(popup.Rows[line].Command);
        Assert.Equal("2,600", popup.Rows[line - 1].Label);
        Assert.Equal("2,500", popup.Rows[line + 1].Label);
    }

    [AvaloniaFact]
    public void MaintainFlyout_RowsBelowTheMvaAreGreyedButSend()
    {
        var host = new RecordingMenuHost("") { Mva = ("12", 2600) };
        MenuRichList popup = OpenMaintainAndPick(new FakeMenuAircraft { AltitudeFeet = 3000 }, host, "2,500");

        Assert.Equal(new MenuRichRow("↓", "2,500", "below MVA", MenuRichRowKind.BelowMva, "DM 2500"), Row(popup, "2,500"));
        Assert.Equal(new MenuRichRow("↓", "2,600", "DM 2600", MenuRichRowKind.Descend, "DM 2600"), Row(popup, "2,600"));
        Assert.All(
            popup.Rows.Where(row => (row.Kind != MenuRichRowKind.MvaLine) && (AltitudeOf(row) < 2600)),
            row => Assert.Equal(MenuRichRowKind.BelowMva, row.Kind)
        );
        Assert.Equal([(Callsign, "DM 2500", Initials)], host.Sent);

        var lowHost = new RecordingMenuHost("") { Mva = ("12", 2600) };
        MenuRichList apart = OpenMaintain(new FakeMenuAircraft { AltitudeFeet = 2000, AssignedAltitude = 1500 }, lowHost);
        Assert.Equal(new MenuRichRow("●", "2,000", "now · below MVA", MenuRichRowKind.BelowMva, "DM 2000"), Row(apart, "2,000"));
        Assert.Equal(new MenuRichRow("◆", "1,500", "assigned · below MVA", MenuRichRowKind.BelowMva, "DM 1500"), Row(apart, "1,500"));
        Assert.Equal("1,500", apart.Rows[apart.SelectedIndex].Label);

        var sameHost = new RecordingMenuHost("") { Mva = ("12", 2600) };
        MenuRichList same = OpenMaintain(new FakeMenuAircraft { AltitudeFeet = 2000, AssignedAltitude = 2000 }, sameHost);
        Assert.Equal(new MenuRichRow("●", "2,000", "now · assigned · below MVA", MenuRichRowKind.BelowMva, "CM 2000"), Row(same, "2,000"));
    }

    [AvaloniaFact]
    public void MaintainFlyout_OffStepAssignmentGetsItsOwnRow()
    {
        MenuRichList popup = OpenMaintain(new FakeMenuAircraft { AltitudeFeet = 3000, AssignedAltitude = 12300 }, new RecordingMenuHost(""));

        MenuRichRow assigned = Row(popup, "12,300");
        Assert.Equal(new MenuRichRow("◆", "12,300", "assigned", MenuRichRowKind.Assigned, "CM 12300"), assigned);
        int index = popup.Rows.ToList().IndexOf(assigned);
        Assert.Equal(index, popup.SelectedIndex);
        Assert.Equal("12,500", popup.Rows[index - 1].Label);
        Assert.Equal("12,000", popup.Rows[index + 1].Label);
    }

    [AvaloniaFact]
    public void MaintainFlyout_AssignmentAboveTheCeilingGetsItsOwnRow()
    {
        TestVnasData.EnsureInitialized();
        double? ceiling = AircraftPerformance.Ceiling("C172");
        Assert.NotNull(ceiling);
        Assert.True(ceiling.Value < 25000, $"C172's ceiling {ceiling} is below the FL250 assignment");
        var aircraft = new FakeMenuAircraft
        {
            AltitudeFeet = 3000,
            AssignedAltitude = 25000,
            FiledAircraftType = "C172",
        };

        MenuRichList popup = OpenMaintain(aircraft, new RecordingMenuHost(""));

        Assert.Equal(new MenuRichRow("◆", "FL250", "assigned", MenuRichRowKind.Assigned, "CM 25000"), popup.Rows[0]);
        Assert.Equal(0, popup.SelectedIndex);
        Assert.Equal((int)(Math.Floor(ceiling.Value / 500) * 500), AltitudeOf(popup.Rows[1]));
    }

    [AvaloniaFact]
    public void MaintainFlyout_NoMvaNoLine()
    {
        MenuRichList popup = OpenMaintain(new FakeMenuAircraft { AltitudeFeet = 3000 }, new RecordingMenuHost(""));

        Assert.DoesNotContain(popup.Rows, row => row.Kind is MenuRichRowKind.MvaLine or MenuRichRowKind.BelowMva);
    }

    [AvaloniaFact]
    public void MaintainFlyout_LabelsUseCommasThenFlightLevels()
    {
        MenuRichList popup = OpenMaintain(new FakeMenuAircraft { AltitudeFeet = 3000 }, new RecordingMenuHost(""));
        List<string> labels = [.. popup.Rows.Select(row => row.Label)];

        Assert.Contains("100", labels);
        Assert.Contains("3,000", labels);
        Assert.Contains("17,500", labels);
        Assert.Contains("FL180", labels);
        Assert.Contains("FL350", labels);
        Assert.DoesNotContain("18,000", labels);
        Assert.DoesNotContain("17500", labels);
    }

    /// <summary>Builds and opens the Maintain altitude flyout for <paramref name="aircraft"/> and returns its list as it opened.</summary>
    private static MenuRichList OpenMaintain(FakeMenuAircraft aircraft, RecordingMenuHost host) => OpenMaintainPanel(aircraft, host).List;

    /// <summary>Opens the Maintain altitude flyout, clicks the row labelled <paramref name="label"/> and returns the list.</summary>
    private static MenuRichList OpenMaintainAndPick(FakeMenuAircraft aircraft, RecordingMenuHost host, string label)
    {
        MaintainAltitudePanel panel = OpenMaintainPanel(aircraft, host);
        panel.RowFor(AltitudeOf(Row(panel.List, label))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return panel.List;
    }

    private static MaintainAltitudePanel OpenMaintainPanel(FakeMenuAircraft aircraft, RecordingMenuHost host)
    {
        MenuItem? item = MenuCatalog.Get(MenuIds.AltitudeMaintain).Build(aircraft, Context(), host);
        Assert.NotNull(item);
        Assert.Equal("Maintain altitude", item.Header as string);
        item.IsSubMenuOpen = true;
        AltitudeFlyoutHost holder = Assert.IsType<AltitudeFlyoutHost>(Assert.Single(item.Items.OfType<MenuItem>()).Header);
        return Assert.IsType<MaintainAltitudePanel>(holder.Child);
    }

    /// <summary>The altitude an altitude row's label names: <c>FL250</c> is 25,000 ft, <c>12,300</c> is 12,300 ft.</summary>
    private static int AltitudeOf(MenuRichRow row) =>
        row.Label.StartsWith("FL", StringComparison.Ordinal)
            ? int.Parse(row.Label[2..], CultureInfo.InvariantCulture) * 100
            : int.Parse(row.Label, NumberStyles.AllowThousands, CultureInfo.InvariantCulture);

    private static MenuRichRow Row(MenuRichList list, string label) => Assert.Single(list.Rows, row => row.Label == label);

    [AvaloniaTheory]
    [InlineData(35000.0, "Altitude (→ FL350)")]
    [InlineData(5000.0, "Altitude (→ 5,000)")]
    public void AltitudeGroup_HeaderShowsTheAssignedAltitude(double assigned, string header) =>
        Assert.Equal(
            header,
            SharedMenuGroups.Altitude(new FakeMenuAircraft { AssignedAltitude = assigned }, Context(), new RecordingMenuHost("")).Header as string
        );

    [AvaloniaTheory]
    [InlineData(213.0, 210, "Speed (→ 213)")]
    [InlineData(null, null, "Speed")]
    public void AssignSpeed_OutlinesTheAssignedSpeedsChip(double? assigned, int? outlined, string header)
    {
        var host = new RecordingMenuHost("");
        var aircraft = new FakeMenuAircraft { AssignedSpeed = assigned };
        MenuItem? item = MenuCatalog.Get(MenuIds.SpeedAssign).Build(aircraft, Context(), host);

        Assert.NotNull(item);
        item.IsSubMenuOpen = true;
        SpeedFlyoutPanel panel = Assert.IsType<SpeedFlyoutPanel>(
            Assert.IsType<SpeedFlyoutHost>(Assert.Single(item.Items.OfType<MenuItem>()).Header).Child
        );
        Assert.Equal(
            (outlined is { } knots) ? [panel.KnotChip(knots)] : [],
            panel.KnotChips.Where(chip => chip.Classes.Contains(SpeedFlyout.AssignedClass))
        );
        panel.KnotChip(200).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal([(Callsign, "SPD 200", Initials)], host.Sent);
        Assert.Equal(header, SharedMenuGroups.Speed(aircraft, Context(), host).Header as string);
    }

    [AvaloniaFact]
    public void FinalApproachSpeed_LabelShowsTheTypesApproachSpeed()
    {
        var host = new RecordingMenuHost("");
        TestVnasData.EnsureInitialized();
        double fas = AircraftPerformance.ApproachSpeed("B738", AircraftCategorization.Categorize("B738"));

        MenuItem? typed = MenuCatalog.Get(MenuIds.SpeedFinalApproach).Build(new FakeMenuAircraft { FiledAircraftType = "B738" }, Context(), host);
        MenuItem? untyped = MenuCatalog.Get(MenuIds.SpeedFinalApproach).Build(new FakeMenuAircraft(), Context(), host);

        Assert.Equal($"Reduce to final approach speed - {fas:F0} kt", typed?.Header as string);
        Assert.Equal("Reduce to final approach speed", untyped?.Header as string);
    }

    [AvaloniaTheory]
    [InlineData(MenuIds.GroundBreakConflict, "Ignore ground conflicts (15 s)", "Ignore ground conflicts (15 s)")]
    [InlineData(MenuIds.AltitudeMaintain, "Maintain altitude", "Maintain altitude")]
    [InlineData(MenuIds.HoldPattern, "Hold at fix…", "Hold at fix…")]
    [InlineData(MenuIds.PatternFollow, "Follow traffic…", "Follow traffic…")]
    [InlineData(MenuIds.SpeedFinalApproach, "Reduce to final approach speed", "Reduce to final approach speed")]
    [InlineData(MenuIds.ApproachReportNMileFinal, "At N-mile final…", "At N-mile final…")]
    [InlineData(MenuIds.ApproachReportAtFix, "At fix…", "At fix…")]
    [InlineData(MenuIds.NavigationOnCourse, "Proceed on course", "Proceed on course")]
    public void RenamedEntry_LabelAndHeader_ReadAsItsCommand(string id, string label, string header)
    {
        Assert.Equal(label, MenuCatalog.Get(id).Label);
        // Other aircraft airborne, so a traffic entry (Follow traffic…) builds its flyout rather than its no-traffic reason.
        RecordingMenuHost host = TrafficHost("", null);
        Assert.Equal(header, MenuCatalog.Get(id).Build(null, Context(), host)?.Header as string);
    }

    [AvaloniaTheory]
    [InlineData(MenuIds.NavigationDirectTo, "Direct to", "DCT")]
    [InlineData(MenuIds.NavigationAppendDirectTo, "Append direct to", "ADCT")]
    public void DirectTo_OpensTheDirectToFlyoutOnItsVerb_NoPopup(string id, string label, string command)
    {
        TestVnasData.EnsureInitialized();
        var routed = new FakeMenuAircraft { NavigatingTo = Fix, NavigationRoute = [Fix, "ECA"] };
        var host = new RecordingMenuHost(Fix);

        MenuItem item = Assert.IsType<MenuItem>(MenuCatalog.Get(id).Build(routed, Context(), host));
        Assert.Equal($"{label}…", item.Header as string);
        item.IsSubMenuOpen = true;
        DirectToFlyoutPanel panel = DirectToFlyoutTests.PanelOf(item);
        panel.RouteRows[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(command, DirectToFlyout.VerbText(panel.Verb));
        Assert.Equal([(Callsign, $"{command} SUNOL", Initials)], host.Sent);
    }

    /// <summary>
    /// Hold at fix… and the two 360s-at-fix entries send, from a flyout rather than a popup, a command with the row's fix that
    /// the sim's parser accepts.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(MenuIds.HoldPattern, "HOLDP")]
    [InlineData(MenuIds.HoldFixLeft, "HFIXL")]
    [InlineData(MenuIds.HoldFixRight, "HFIXR")]
    public void HoldAtFix_SendsACommandTheParserAccepts(string id, string verb)
    {
        string command = SendFromHoldRow(id);

        Assert.StartsWith($"{verb} {Fix}", command);
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(command);
        Assert.True(parsed.IsSuccess, $"{command}: {parsed.Reason}");
    }

    /// <summary>Hold at fix… always sends four <c>HOLDP</c> tokens: fix, course, leg, turns.</summary>
    [AvaloniaFact]
    public void HoldAtFix_SendsAFourTokenHoldp()
    {
        string command = SendFromHoldRow(MenuIds.HoldPattern);

        Assert.Equal(5, command.Split(' ').Length);
        Assert.IsType<HoldingPatternCommand>(CommandParser.Parse(command).Value);
    }

    /// <summary>
    /// Opens the hold entry <paramref name="id"/> over an aircraft navigating to SUNOL, clicks its SUNOL row, returns what it sent.
    /// </summary>
    private static string SendFromHoldRow(string id)
    {
        TestVnasData.EnsureInitialized();
        (double lat, double lon) = NavigationDatabase.Instance.GetFixPosition("OAK")!.Value;
        var host = new RecordingMenuHost(Fix);
        var aircraft = new FakeMenuAircraft
        {
            Position = new LatLon(lat, lon),
            NavigatingTo = Fix,
            NavigationRoute = [Fix],
            RouteFixes = [Fix],
            AltitudeFeet = 5000,
        };

        MenuItem item = Assert.IsType<MenuItem>(MenuCatalog.Get(id).Build(aircraft, Context(), host));
        item.IsSubMenuOpen = true;
        FlyoutPanelHost holder = Assert.IsAssignableFrom<FlyoutPanelHost>(Assert.Single(item.Items.OfType<MenuItem>()).Header);
        FixRow row = Assert.Single(holder.GetLogicalDescendants().OfType<FixRow>(), r => r.Fix == Fix);
        row.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Empty(host.InputPlaceholders);
        (string _, string command, string _) = Assert.Single(host.Sent);
        return command;
    }

    /// <summary>Squawk assigned code (bare <c>SQ</c>) is offered only to an aircraft with an assigned code, else it would squawk 0000.</summary>
    [AvaloniaFact]
    public void SquawkAssigned_NotApplicableWithoutAnAssignedCode()
    {
        MenuCatalogEntry entry = MenuCatalog.Get(MenuIds.SquawkAssigned);
        var vfr1200 = new FakeMenuAircraft
        {
            FlightRules = "VFR",
            BeaconCode = 1200,
            AssignedBeaconCode = 0,
        };

        Assert.False(entry.IsApplicable(vfr1200, Context()));
        Assert.True(entry.IsApplicable(vfr1200 with { AssignedBeaconCode = 4521 }, Context()));
    }

    /// <summary>The left / right 360s at a fix are VFR entries: an IFR aircraft gets them only under the full VFR-for-IFR setting.</summary>
    [AvaloniaTheory]
    [InlineData(MenuIds.HoldFixLeft, "Left 360s at fix…")]
    [InlineData(MenuIds.HoldFixRight, "Right 360s at fix…")]
    public void HoldAtFix360s_OfferedToAVfrAircraftOnly(string id, string label)
    {
        var host = new RecordingMenuHost("");
        var vfr = new FakeMenuAircraft { FlightRules = "VFR" };
        var ifr = new FakeMenuAircraft { FlightRules = "IFR" };
        MenuCatalogEntry entry = MenuCatalog.Get(id);

        Assert.Equal(MenuFlightRules.VfrOnly, entry.DefaultFlightRules);
        Assert.True(entry.IsApplicable(vfr, Context()));
        Assert.False(entry.IsApplicable(ifr, Context()));
        Assert.True(entry.IsApplicable(ifr, Context(VfrCommandsForIfr.All)));
        Assert.False(entry.IsApplicable(vfr with { IsOnGround = true }, Context()));
        Assert.Contains(label, SharedMenuGroups.Hold(vfr, Context(), host).Items.OfType<MenuItem>().Select(m => m.Header as string));
        Assert.DoesNotContain(label, SharedMenuGroups.Hold(ifr, Context(), host).Items.OfType<MenuItem>().Select(m => m.Header as string));
    }

    /// <summary>
    /// All Commands › Hold offers Hold at fix… to an IFR aircraft in the air, opening the holding-pattern flyout; never to a VFR
    /// one or on the ground.
    /// </summary>
    [AvaloniaFact]
    public void HoldGroup_OffersHoldAtFixToAnIfrAirborneAircraftOnly()
    {
        TestVnasData.EnsureInitialized();
        var host = new RecordingMenuHost("");
        var ifr = new FakeMenuAircraft { FlightRules = "IFR", AltitudeFeet = 5000 };

        MenuItem item = Assert.Single(
            SharedMenuGroups.Hold(ifr, Context(), host).Items.OfType<MenuItem>(),
            m => m.Header as string == "Hold at fix…"
        );
        item.IsSubMenuOpen = true;
        Assert.IsType<HoldPatternFlyoutPanel>(Assert.IsAssignableFrom<FlyoutPanelHost>(Assert.Single(item.Items.OfType<MenuItem>()).Header).Child);
        Assert.DoesNotContain(
            "Hold at fix…",
            SharedMenuGroups.Hold(ifr with { FlightRules = "VFR" }, Context(), host).Items.OfType<MenuItem>().Select(m => m.Header as string)
        );
        Assert.DoesNotContain(
            "Hold at fix…",
            SharedMenuGroups.Hold(ifr with { IsOnGround = true }, Context(), host).Items.OfType<MenuItem>().Select(m => m.Header as string)
        );
    }

    [AvaloniaFact]
    public void RouteBlock_OffersAppendDirectToOnlyWhileNavigating()
    {
        var host = new RecordingMenuHost("");
        MenuItem idle = SharedMenuGroups.Route(new FakeMenuAircraft(), Context(), host);
        MenuItem navigating = SharedMenuGroups.Route(new FakeMenuAircraft { NavigatingTo = Fix }, Context(), host);

        Assert.Equal("Route", idle.Header as string);
        Assert.Equal(["Direct to…", .. RouteTail], idle.Items.Select(Describe));
        Assert.Equal("Route (→ SUNOL)", navigating.Header as string);
        Assert.Equal(["Direct to…", "Append direct to…", .. RouteTail], navigating.Items.Select(Describe));
    }

    /// <summary>The Route block's rows after Direct to… and Append direct to….</summary>
    private static readonly string[] RouteTail =
    [
        "Proceed on course",
        "Cross fix…",
        "Depart fix…",
        "Join airway…",
        "Join radial outbound…",
        "Join radial inbound…",
        "Climb via SID",
    ];

    /// <summary>A circling approach id splits into its kind, its full published kind and its variant letter.</summary>
    [Theory]
    [InlineData("RNVA", "RNAV", "RNAV (GPS)", "A")]
    [InlineData("GPS-A", "GPS", "GPS", "A")]
    [InlineData("VDM-B", "VOR/DME", "VOR/DME", "B")]
    public void CirclingApproach_KindFullKindAndVariant_FromItsId(string id, string kind, string fullKind, string variant) =>
        Assert.Equal((kind, fullKind, variant), ApproachChoices.DescribeCirclingApproach(id));

    [AvaloniaFact]
    public void ApproachGroup_ListsTheEightApproachFlyoutEntries_WithNoOtherCompanion()
    {
        TestVnasData.EnsureInitialized();
        var host = new RecordingMenuHost("");
        var aircraft = new FakeMenuAircraft { Destination = ApproachAirport, AssignedRunway = "30" };

        MenuItem approach = SharedMenuGroups.Approach(aircraft, Context(), host)!;

        Assert.Equal(
            [
                "Cleared approach",
                "Join approach",
                "Cleared straight-in",
                "Join straight-in",
                "Cleared approach (force)",
                "Join approach (force)",
                "Join final approach course",
                "Expect approach",
            ],
            approach.Items.Select(Describe).Take(8)
        );
        Assert.All(
            approach.Items.OfType<MenuItem>().Take(8),
            entry => Assert.IsType<ApproachFlyoutHost>(Assert.IsType<MenuItem>(Assert.Single(entry.Items)).Header)
        );
    }

    /// <summary>
    /// Cleared visual approach opens its own visual-only list, titled for it, focusing the default runway's visual row where
    /// Cleared approach itself focuses its pinned default row; the row sends <c>CVA</c>.
    /// </summary>
    [AvaloniaFact]
    public void ClearedVisual_OpensItsVisualListOnTheDefaultRunwaysVisualRow()
    {
        TestVnasData.EnsureInitialized();
        RecordingMenuHost host = KoakLandingHost();
        FakeMenuAircraft aircraft = VisualArrival() with { AssignedRunway = "30" };

        MenuItem? entry = MenuCatalog.Get(MenuIds.ApproachClearedVisual).Build(aircraft, Context(), host);
        Assert.Equal("Cleared visual approach", entry?.Header as string);
        ApproachFlyoutPanel panel = OpenApproachFlyout(entry);
        ApproachFlyoutPanel cleared = OpenApproachFlyout(MenuCatalog.Get(MenuIds.ApproachCleared).Build(aircraft, Context(), host));

        Assert.StartsWith("Cleared visual approach", panel.Lines[0]);
        Button opening = Assert.IsType<Button>(panel.OpeningFocus);
        Assert.Equal("Visual RWY 30", Assert.IsType<ApproachRow>(opening.Content).Name);
        Assert.Equal(ApproachFlyoutModel.DefaultBadge, Assert.IsType<ApproachRow>(Assert.IsType<Button>(cleared.OpeningFocus).Content).Badge);
        opening.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal([(Callsign, "CVA 30", Initials)], host.Sent);
    }

    /// <summary>With no default runway, Cleared visual approach opens on the first landing runway's visual row.</summary>
    [AvaloniaFact]
    public void ClearedVisual_NoDefault_OpensOnTheFirstLandingRunwaysVisualRow()
    {
        TestVnasData.EnsureInitialized();
        RecordingMenuHost host = KoakLandingHost();

        ApproachFlyoutPanel panel = OpenApproachFlyout(MenuCatalog.Get(MenuIds.ApproachClearedVisual).Build(VisualArrival(), Context(), host));

        Assert.Same(panel.Rows[0], panel.OpeningFocus);
        ApproachRow first = Assert.IsType<ApproachRow>(panel.Rows[0].Content);
        Assert.Equal(ApproachRowKind.Visual, first.Kind);
        Assert.Equal("Visual RWY 30", first.Name);
    }

    /// <summary>
    /// A scenario's expected approach in shorthand (R28L for KOAK's R28LY) gives the visual approach its runway; though the room
    /// does not land on 28L, its row is the list's first, above the landing runways 30 and 28R and the fold row, and has the
    /// focus, so Enter sends <c>CVA 28L</c>. Unfolded, the other runways do not repeat 28L.
    /// </summary>
    [AvaloniaFact]
    public void ClearedVisual_ExpectedApproachOnANonArrivalRunway_IsTheFirstRow_FocusedForEnter()
    {
        TestVnasData.EnsureInitialized();
        RecordingMenuHost host = KoakLandingHost();
        FakeMenuAircraft aircraft = VisualArrival() with { ExpectedApproach = "R28L" };

        Assert.Equal("28L", MenuCatalog.SmartVisualRunway(aircraft));
        MenuItem entry = Assert.IsType<MenuItem>(MenuCatalog.Get(MenuIds.ApproachClearedVisual).Build(aircraft, Context(), host));
        var window = new Window { Width = 900, Height = 900 };
        window.Show();
        var menu = new ContextMenu { Items = { entry } };
        menu.Open(window);
        Dispatcher.UIThread.RunJobs();
        entry.IsSubMenuOpen = true;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        ApproachFlyoutPanel panel = Assert.IsType<ApproachFlyoutPanel>(
            Assert.IsType<ApproachFlyoutHost>(Assert.Single(entry.Items.OfType<MenuItem>()).Header).Child
        );

        Assert.Equal(
            [
                "Visual approach to KOAK · pilot picks the runway — CVA KOAK",
                "---",
                "Visual RWY 28L — CVA 28L",
                "Visual RWY 30 — CVA 30",
                "Visual RWY 28R — CVA 28R",
                "Non-arrival runways… · 5",
            ],
            panel.Lines.Skip(1).Take(6)
        );
        Assert.Same(panel.Rows[0], panel.OpeningFocus);
        Assert.Same(panel.Rows[0], window.FocusManager?.GetFocusedElement());
        Avalonia.Headless.HeadlessWindowExtensions.KeyPressQwerty(window, PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([(Callsign, "CVA 28L", Initials)], host.Sent);
        Assert.IsType<Button>(panel.OtherRunwaysRow).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        List<string> unfolded = VisibleRowNames(panel);
        Assert.Equal(8, unfolded.Count);
        Assert.Single(unfolded, name => name == "Visual RWY 28L");
    }

    /// <summary>
    /// An aircraft assigned 28R, a landing runway, opens on 28R as the first row, focused, then 30; 28R is not repeated in the
    /// landing group or under the fold.
    /// </summary>
    [AvaloniaFact]
    public void ClearedVisual_AssignedLandingRunway_IsTheFirstRow_NotRepeated()
    {
        TestVnasData.EnsureInitialized();
        RecordingMenuHost host = KoakLandingHost();
        FakeMenuAircraft aircraft = VisualArrival() with { AssignedRunway = "28R" };

        ApproachFlyoutPanel panel = OpenApproachFlyout(MenuCatalog.Get(MenuIds.ApproachClearedVisual).Build(aircraft, Context(), host));

        Assert.Equal(["Visual RWY 28R", "Visual RWY 30"], VisibleRowNames(panel));
        Assert.Same(panel.Rows[0], panel.OpeningFocus);
        Assert.IsType<Button>(panel.OtherRunwaysRow).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        List<string> unfolded = VisibleRowNames(panel);
        Assert.Equal(8, unfolded.Count);
        Assert.Single(unfolded, name => name == "Visual RWY 28R");
    }

    /// <summary>Before the field is reported in sight the default runway's first row waits greyed, and no row has the focus.</summary>
    [AvaloniaFact]
    public void ClearedVisual_DefaultRunwayRow_IsNotFocusedBeforeTheFieldIsReported()
    {
        TestVnasData.EnsureInitialized();
        RecordingMenuHost host = KoakLandingHost();
        FakeMenuAircraft aircraft = VisualArrival() with { ExpectedApproach = "R28L", SituationFlags = SituationFlags.None };

        ApproachFlyoutPanel panel = OpenApproachFlyout(MenuCatalog.Get(MenuIds.ApproachClearedVisual).Build(aircraft, Context(), host));

        Assert.Equal("Visual RWY 28L", Assert.IsType<ApproachRow>(panel.Rows[0].Content).Name);
        Assert.False(panel.Rows[0].IsEnabled);
        Assert.Null(panel.OpeningFocus);
    }

    private static List<string> VisibleRowNames(ApproachFlyoutPanel panel) =>
        [.. panel.Rows.Where(button => button.IsVisible).Select(button => Assert.IsType<ApproachRow>(button.Content).Name)];

    /// <summary>An expected visual approach (<c>EAPP VIS28R</c>) names no procedure; the visual approach defaults to its runway.</summary>
    [AvaloniaFact]
    public void ClearedVisual_ExpectedVisualApproach_DefaultsToItsRunway()
    {
        TestVnasData.EnsureInitialized();
        RecordingMenuHost host = KoakLandingHost();
        FakeMenuAircraft aircraft = VisualArrival() with { ExpectedApproach = "VIS28R" };

        Assert.Equal("28R", MenuCatalog.SmartVisualRunway(aircraft));
        ApproachFlyoutPanel panel = OpenApproachFlyout(MenuCatalog.Get(MenuIds.ApproachClearedVisual).Build(aircraft, Context(), host));
        Assert.IsType<Button>(panel.OpeningFocus).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal([(Callsign, "CVA 28R", Initials)], host.Sent);
    }

    /// <summary>
    /// Cleared visual approach is offered to an IFR aircraft only (ruling 2): a VFR aircraft's Approach submenu has no such
    /// entry, greyed or otherwise, where an IFR aircraft's does.
    /// </summary>
    [AvaloniaFact]
    public void ClearedVisual_IsOfferedToIfrAircraftOnly()
    {
        TestVnasData.EnsureInitialized();
        var host = new RecordingMenuHost("");
        FakeMenuAircraft ifr = VisualArrival();
        FakeMenuAircraft vfr = VisualArrival() with { FlightRules = "VFR" };
        MenuCatalogEntry entry = MenuCatalog.Get(MenuIds.ApproachClearedVisual);

        Assert.Equal(MenuFlightRules.IfrOnly, entry.DefaultFlightRules);
        Assert.True(entry.IsApplicable(ifr, Context()));
        Assert.False(entry.IsApplicable(vfr, Context()));
        Assert.Contains("Cleared visual approach", SharedMenuGroups.Approach(ifr, Context(), host)!.Items.Select(Describe));
        Assert.DoesNotContain("Cleared visual approach", SharedMenuGroups.Approach(vfr, Context(), host)!.Items.Select(Describe));
    }

    /// <summary>An IFR arrival to <see cref="ApproachAirport"/> that has reported the field in sight, so its visual rows are live.</summary>
    private static FakeMenuAircraft VisualArrival() =>
        new()
        {
            Destination = ApproachAirport,
            FlightRules = "IFR",
            SituationFlags = SituationFlags.HasReportedFieldInSight,
        };

    /// <summary>A host whose room lands KOAK's 30 (both ways) and 28R (arrivals).</summary>
    private static RecordingMenuHost KoakLandingHost() =>
        new("") { RoomActiveRunways = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["OAK"] = ["30", "A28R"] } };

    /// <summary>Opens <paramref name="entry"/>'s approach flyout and returns its panel.</summary>
    private static ApproachFlyoutPanel OpenApproachFlyout(MenuItem? entry)
    {
        MenuItem item = Assert.IsType<MenuItem>(entry);
        item.IsSubMenuOpen = true;
        return Assert.IsType<ApproachFlyoutPanel>(Assert.IsType<ApproachFlyoutHost>(Assert.Single(item.Items.OfType<MenuItem>()).Header).Child);
    }

    /// <summary>
    /// Asserts <paramref name="entry"/> is disabled with <paramref name="reason"/> beside <paramref name="label"/> (ruling 3, nothing
    /// to pick) and its strip button inert.
    /// </summary>
    private static void AssertUnavailable(MenuItem? entry, string label, string reason)
    {
        Assert.NotNull(entry);
        Assert.False(entry.IsEnabled);
        Assert.Equal(new MenuDisabledReasonRow(label, reason), entry.Header);
        Assert.True(QuickCommandStrip.IsInert(entry));
        Assert.Empty(entry.Items);
    }

    /// <summary>Outside solo training, Report traffic in sight… is the traffic flyout over the host's airborne traffic, not a picker.</summary>
    [AvaloniaFact]
    public void ReportTrafficInSight_OutsideSolo_OpensTheTrafficFlyout()
    {
        RecordingMenuHost host = TrafficHost("", null);

        MenuItem item = Assert.IsType<MenuItem>(MenuCatalog.Get(MenuIds.ApproachReportTrafficInSight).Build(null, Context(), host));

        Assert.Null(item.Tag);
        Assert.Equal("report traffic in sight flyout", Assert.IsType<MenuItem>(Assert.Single(item.Items)).Header?.ToString());
        Assert.Empty(host.Sent);
    }

    [AvaloniaFact]
    public void ReportTrafficInSight_Solo_NoTraffic_OffersNoFlyout()
    {
        MenuItem? item = MenuCatalog.Get(MenuIds.ApproachReportTrafficInSight).Build(null, SoloContext(), new RecordingMenuHost(""));

        Assert.NotNull(item);
        Assert.Empty(item.Items);
    }

    [AvaloniaFact]
    public void ReportTrafficInSight_Solo_OpensTheSoloAdvisoryFlyout()
    {
        RecordingMenuHost host = TrafficHost("", null);

        MenuItem item = Assert.IsType<MenuItem>(MenuCatalog.Get(MenuIds.ApproachReportTrafficInSight).Build(null, SoloContext(), host));

        Assert.IsType<SoloAdvisoryFlyoutHost>(Assert.IsType<MenuItem>(Assert.Single(item.Items)).Header);
        Assert.Empty(host.Sent);
    }

    /// <summary>A menu context in solo training, where Report traffic in sight… opens the solo advisory flyout.</summary>
    private static MenuContext SoloContext() => TestMenuContext.Create(Callsign, Initials, null, true, VfrCommandsForIfr.EnterFinalOnly);

    /// <summary>
    /// A host answering two aircraft of nearby traffic, answering a picker with <paramref name="pick"/> and an input with
    /// <paramref name="typed"/>.
    /// </summary>
    private static RecordingMenuHost TrafficHost(string pick, string? typed)
    {
        var host = new RecordingMenuHost(pick) { InputAnswer = typed };
        host.AirborneTraffic.Add(new MenuAirborneTrafficRow(new MenuTrafficRow("AAL601", "B738", 4.2, 2, -1500), "northbound", null));
        host.AirborneTraffic.Add(new MenuAirborneTrafficRow(new MenuTrafficRow("N302AB", "", 6.5, 11, 0), "southbound", null));
        return host;
    }

    /// <summary>
    /// The Squawk flyout opens on the code box row, always open, its <c>SQ</c> drawn in the box; then after a separator Squawk
    /// assigned code and the presets, then after another separator Ident; no input popup.
    /// </summary>
    [AvaloniaFact]
    public void Squawk_Flyout_HoldsTheCodeBoxThenTheGroup()
    {
        var host = new RecordingMenuHost("");
        var aircraft = new FakeMenuAircraft { BeaconCode = 1200, AssignedBeaconCode = 4613 };

        MenuItem squawk = SharedMenuGroups.Squawk(aircraft, Context(), host);

        Assert.Equal(
            [
                $"Code — SQ {TakeoffFlyout.Unfilled}",
                "---",
                "Squawk assigned code",
                "Squawk random",
                "Squawk VFR",
                "Squawk normal",
                "Squawk standby",
                "---",
                "Ident",
            ],
            squawk.Items.Select(DescribeRow)
        );
        Assert.Empty(host.InputPlaceholders);
    }

    /// <summary>
    /// With no assigned code the Squawk flyout leaves out Squawk assigned code: the presets follow the code box's separator directly,
    /// with no doubled separator.
    /// </summary>
    [AvaloniaFact]
    public void Squawk_Flyout_NoAssignedCode_LeavesOutSquawkAssignedCode()
    {
        var host = new RecordingMenuHost("");
        var aircraft = new FakeMenuAircraft { BeaconCode = 1200, AssignedBeaconCode = 0 };

        MenuItem squawk = SharedMenuGroups.Squawk(aircraft, Context(), host);

        Assert.Equal(
            [$"Code — SQ {TakeoffFlyout.Unfilled}", "---", "Squawk random", "Squawk VFR", "Squawk normal", "Squawk standby", "---", "Ident"],
            squawk.Items.Select(DescribeRow)
        );
    }

    /// <summary>All Commands › Track and coordination's Squawk flyout leaves out Squawk assigned code for an aircraft with no assigned code.</summary>
    [AvaloniaFact]
    public void TrackAndCoordination_SquawkFlyout_NoAssignedCode_LeavesOutSquawkAssignedCode()
    {
        var host = new RecordingMenuHost("");
        var aircraft = new FakeMenuAircraft { BeaconCode = 1200, AssignedBeaconCode = 0 };

        MenuItem? track = SharedMenuGroups.TrackAndCoordination(aircraft, Context(), host, commandsTheAircraft: true);

        Assert.NotNull(track);
        MenuItem squawk = Assert.Single(track.Items.OfType<MenuItem>(), m => (m.Header as string == "Squawk") && (m.Items.Count > 0));
        string[] rows = [.. squawk.Items.Select(DescribeRow)];
        Assert.Contains("Squawk random", rows);
        Assert.DoesNotContain("Squawk assigned code", rows);
    }

    /// <summary>The Squawk flyout opens with the caret in its code box, its first row, so typed digits land there.</summary>
    [AvaloniaFact]
    public void Squawk_Flyout_OpensWithFocusInTheCodeBox()
    {
        var window = new Window { Width = 600, Height = 900 };
        window.Show();
        var host = new RecordingMenuHost("");
        MenuItem squawk = SharedMenuGroups.Squawk(new FakeMenuAircraft { AssignedBeaconCode = 4613 }, Context(), host);
        var menu = new ContextMenu { Items = { squawk } };
        try
        {
            menu.Open(window);
            Dispatcher.UIThread.RunJobs();
            squawk.IsSubMenuOpen = true;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            MenuItem first = Assert.IsType<MenuItem>(squawk.Items[0]);
            InlineBoxRow row = Assert.IsType<InlineBoxRow>(first.Header);
            Assert.True(Assert.IsType<TextBox>(row.Input).IsFocused, "the code box did not take focus when the Squawk flyout opened");
        }
        finally
        {
            menu.Close();
            window.Close();
        }
    }

    /// <summary>
    /// Squawk…, Custom… and Scratchpad… opened from the quick-command strip, whose flyout holds the entry's items without opening
    /// its submenu, open with the caret in the box too. Custom… and Scratchpad… have no quick-command glyph, so the resolver never
    /// puts them on the strip; they are given Squawk…'s glyph here so the strip's path is pinned for every box flyout. The aircraft
    /// holds a Scratchpad 1, so Scratchpad…'s box opens holding it, all of it selected; the other boxes open empty.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(MenuIds.SquawkCode, "")]
    [InlineData(MenuIds.AskPilotCustom, "")]
    [InlineData(MenuIds.DataBlockScratchpad, "I30")]
    public void StripFlyout_OpensWithFocusInTheBox(string id, string seed)
    {
        var host = new RecordingMenuHost("");
        MenuCatalogEntry entry = MenuCatalog.Get(id);
        var aircraft = new FakeMenuAircraft { AssignedBeaconCode = 4613, Scratchpad1 = "I30" };
        MenuItem built = Assert.IsType<MenuItem>(entry.Build(aircraft, Context(), host));
        InlineBoxRow row = BoxRow(built);
        var menu = new ContextMenu();
        MenuItem strip = Assert.IsType<MenuItem>(
            QuickCommandStrip.FromBuilt(menu, [(new QuickCommandStripItem(entry, QuickCommandGlyphs.For(MenuIds.SquawkCode)!), built)])
        );
        menu.Items.Add(strip);
        var window = new Window { Width = 600, Height = 900 };
        window.Show();
        try
        {
            menu.Open(window);
            Dispatcher.UIThread.RunJobs();
            menu.UpdateLayout();
            Button button = Assert.Single(QuickCommandStrip.Buttons(strip));
            QuickCommandStrip.OpenSubmenu(button);
            Dispatcher.UIThread.RunJobs();
            MenuFlyout flyout = Assert.IsType<MenuFlyout>(FlyoutBase.GetAttachedFlyout(button));
            Assert.IsAssignableFrom<Control>(flyout.Popup.Child).UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            TextBox box = Assert.IsType<TextBox>(row.Input);
            Assert.True(box.IsFocused, $"the box did not take focus when the strip opened {id}'s flyout");
            Assert.Equal(seed, box.Text ?? "");
            Assert.Equal((0, seed.Length), (Math.Min(box.SelectionStart, box.SelectionEnd), Math.Max(box.SelectionStart, box.SelectionEnd)));
        }
        finally
        {
            menu.Close();
            window.Close();
        }
    }

    /// <summary>The Say flyout opens with the caret in its Custom box, its last row, so typed words land there.</summary>
    [AvaloniaFact]
    public void Say_Flyout_OpensWithFocusInTheCustomBox()
    {
        var window = new Window { Width = 600, Height = 900 };
        window.Show();
        var host = new RecordingMenuHost("");
        MenuItem say = SharedMenuGroups.AskPilot(new FakeMenuAircraft(), Context(), host);
        var menu = new ContextMenu { Items = { say } };
        try
        {
            menu.Open(window);
            Dispatcher.UIThread.RunJobs();
            say.IsSubMenuOpen = true;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            MenuItem last = Assert.IsType<MenuItem>(say.Items[^1]);
            InlineBoxRow row = Assert.IsType<InlineBoxRow>(last.Header);
            Assert.True(Assert.IsType<TextBox>(row.Input).IsFocused, "the Custom box did not take focus when the Say flyout opened");
        }
        finally
        {
            menu.Close();
            window.Close();
        }
    }

    /// <summary>
    /// Every Squawk flyout row leads with the glyph column, a blank slot where the row has no glyph, so the code box starts
    /// where the presets' labels start.
    /// </summary>
    [AvaloniaFact]
    public void Squawk_Flyout_TheCodeBoxLinesUpWithThePresetLabels()
    {
        var window = new Window { Width = 600, Height = 900 };
        window.Show();
        var host = new RecordingMenuHost("");
        MenuItem squawk = SharedMenuGroups.Squawk(new FakeMenuAircraft { AssignedBeaconCode = 4613 }, Context(), host);
        var menu = new ContextMenu { Items = { squawk } };
        try
        {
            menu.Open(window);
            Dispatcher.UIThread.RunJobs();
            squawk.IsSubMenuOpen = true;
            Dispatcher.UIThread.RunJobs();
            InlineBoxRow row = BoxRow(squawk);
            (TopLevel.GetTopLevel(row) as Control)?.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            double boxLeft = LeftEdge(Assert.IsType<TextBox>(row.Input));
            foreach (string preset in new[] { "Squawk assigned code", "Squawk random", "Squawk normal", "Ident" })
            {
                MenuItem item = Assert.Single(squawk.Items.OfType<MenuItem>(), m => m.Header?.ToString() == preset);
                TextBlock label = Assert.Single(item.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == preset);
                Assert.Equal(boxLeft, LeftEdge(label), 1);
            }
        }
        finally
        {
            menu.Close();
            window.Close();
        }
    }

    /// <summary>The left edge of <paramref name="control"/> in its top level.</summary>
    private static double LeftEdge(Control control) =>
        Assert.NotNull(control.TranslatePoint(default, Assert.IsAssignableFrom<Visual>(TopLevel.GetTopLevel(control)))).X;

    /// <summary>The Squawk… entry, which a quick-command list can name, builds the same flyout, so its strip button opens it.</summary>
    [AvaloniaFact]
    public void SquawkCodeEntry_BuildsTheSquawkFlyout()
    {
        var host = new RecordingMenuHost("");

        MenuItem item = Assert.IsType<MenuItem>(MenuCatalog.Get(MenuIds.SquawkCode).Build(null, Context(), host));

        Assert.Equal("Squawk…", item.Header);
        Assert.Contains(item.Items.OfType<MenuItem>(), m => m.Header?.ToString() == "Ident");
        Assert.NotNull(BoxRow(item));
    }

    /// <summary>Four octal digits typed in the Squawk box are sent as typed, a leading zero kept, on Enter.</summary>
    [AvaloniaTheory]
    [InlineData("1234", "SQ 1234")]
    [InlineData("0123", "SQ 0123")]
    [InlineData(" 4613 ", "SQ 4613")]
    public void Squawk_CodeBox_EnterSendsTheFourDigits(string typed, string command)
    {
        var host = new RecordingMenuHost("");
        InlineBoxRow row = BoxRow(SharedMenuGroups.Squawk(null, Context(), host));

        BoxOf(row).Text = typed;
        PressEnter(BoxOf(row));

        Assert.Equal([(Callsign, command, Initials)], host.Sent);
        Assert.Empty(host.InputPlaceholders);
    }

    /// <summary>A click on the Squawk box row sends its code too.</summary>
    [AvaloniaFact]
    public void Squawk_CodeBox_ClickSendsTheCode()
    {
        var host = new RecordingMenuHost("");
        MenuItem squawk = SharedMenuGroups.Squawk(null, Context(), host);
        InlineBoxRow row = BoxRow(squawk);

        BoxOf(row).Text = "7000";
        Click(Assert.Single(squawk.Items.OfType<MenuItem>(), m => ReferenceEquals(m.Header, row)));

        Assert.Equal([(Callsign, "SQ 7000", Initials)], host.Sent);
    }

    /// <summary>A click on the Squawk box row while its box is blank or refused sends nothing, as Enter does.</summary>
    [AvaloniaTheory]
    [InlineData("")]
    [InlineData("8888")]
    public void Squawk_CodeBox_BlankOrRefused_ClickSendsNothing(string typed)
    {
        var host = new RecordingMenuHost("");
        MenuItem squawk = SharedMenuGroups.Squawk(null, Context(), host);
        InlineBoxRow row = BoxRow(squawk);

        BoxOf(row).Text = typed;
        MenuItem item = Assert.Single(squawk.Items.OfType<MenuItem>(), m => ReferenceEquals(m.Header, row));
        Click(item);

        Assert.Empty(host.Sent);
        Assert.True(item.StaysOpenOnClick, "A row that cannot send should stay open on its click.");
    }

    /// <summary>
    /// Text that is not four octal digits is refused inline: the red underline, the reason line under the box, "can't send"
    /// in the command column; Enter sends nothing.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("8000")]
    [InlineData("123")]
    [InlineData("12345")]
    [InlineData("abcd")]
    public void Squawk_CodeBox_NotFourOctalDigits_RefusesInline_EnterSendsNothing(string typed)
    {
        var host = new RecordingMenuHost("");
        InlineBoxRow row = BoxRow(SharedMenuGroups.Squawk(null, Context(), host));

        BoxOf(row).Text = typed;

        Assert.Contains(RefusableBox.RefusedClass, BoxOf(row).Classes);
        Assert.True(row.Refusal.ReasonLine.IsVisible, "A refused code should show its reason line.");
        Assert.Equal(MenuCatalog.InvalidSquawkCode, row.Refusal.ReasonLine.Text);
        Assert.Equal(InlineBoxRow.CantSend, row.CommandColumn.Text);

        PressEnter(BoxOf(row));

        Assert.Empty(host.Sent);
    }

    /// <summary>A blank Squawk box sends nothing: a bare <c>SQ</c> is Squawk assigned code, which keeps its own row.</summary>
    [AvaloniaTheory]
    [InlineData("")]
    [InlineData("   ")]
    public void Squawk_CodeBox_Blank_EnterSendsNothing(string typed)
    {
        var host = new RecordingMenuHost("");
        InlineBoxRow row = BoxRow(SharedMenuGroups.Squawk(null, Context(), host));

        BoxOf(row).Text = typed;
        PressEnter(BoxOf(row));

        Assert.Empty(host.Sent);
        Assert.DoesNotContain(RefusableBox.RefusedClass, BoxOf(row).Classes);
        Assert.Equal($"SQ {TakeoffFlyout.Unfilled}", row.CommandColumn.Text);
    }

    /// <summary>The Say flyout carries the six Say queries, then after a separator the Custom box row, its <c>SAY</c> drawn in the box.</summary>
    [AvaloniaFact]
    public void Say_Flyout_HoldsTheSixQueriesThenTheCustomBox()
    {
        var host = new RecordingMenuHost("");

        MenuItem say = SharedMenuGroups.AskPilot(new FakeMenuAircraft(), Context(), host);

        Assert.Equal(
            ["Altitude", "Heading", "Speed", "Mach", "Position", "Expected approach", "---", $"Custom — SAY {TakeoffFlyout.Unfilled}"],
            say.Items.Select(DescribeRow)
        );
        Assert.Empty(host.InputPlaceholders);
    }

    /// <summary>The Custom… entry, which a quick-command list can name, builds the same flyout, so its strip button opens it.</summary>
    [AvaloniaFact]
    public void AskPilotCustomEntry_BuildsTheSayFlyout()
    {
        var host = new RecordingMenuHost("");

        MenuItem item = Assert.IsType<MenuItem>(MenuCatalog.Get(MenuIds.AskPilotCustom).Build(new FakeMenuAircraft(), Context(), host));

        Assert.Equal("Custom…", item.Header);
        Assert.Contains(item.Items.OfType<MenuItem>(), m => (m.Header as string) == "Altitude");
        Assert.NotNull(BoxRow(item));
    }

    /// <summary>The Say Custom box sends <c>SAY</c> and the text trimmed, case kept, on Enter.</summary>
    [AvaloniaTheory]
    [InlineData("say again", "SAY say again")]
    [InlineData("  Request Higher ", "SAY Request Higher")]
    public void Say_CustomBox_EnterSendsTheTrimmedText(string typed, string command)
    {
        var host = new RecordingMenuHost("");
        InlineBoxRow row = BoxRow(SharedMenuGroups.AskPilot(new FakeMenuAircraft(), Context(), host));

        BoxOf(row).Text = typed;
        PressEnter(BoxOf(row));

        Assert.Equal([(Callsign, command, Initials)], host.Sent);
        Assert.Empty(host.InputPlaceholders);
    }

    /// <summary>A blank Say Custom box sends nothing: a bare <c>SAY</c> parses as an unsupported command.</summary>
    [AvaloniaTheory]
    [InlineData("")]
    [InlineData("   ")]
    public void Say_CustomBox_Blank_EnterSendsNothing(string typed)
    {
        var host = new RecordingMenuHost("");
        InlineBoxRow row = BoxRow(SharedMenuGroups.AskPilot(new FakeMenuAircraft(), Context(), host));

        BoxOf(row).Text = typed;
        PressEnter(BoxOf(row));

        Assert.Empty(host.Sent);
        Assert.Equal($"SAY {TakeoffFlyout.Unfilled}", row.CommandColumn.Text);
    }

    /// <summary>The Scratchpad… entry's flyout for <paramref name="aircraft"/>.</summary>
    private static MenuItem Scratchpad(IMenuAircraft? aircraft, RecordingMenuHost host) =>
        Assert.IsType<MenuItem>(MenuCatalog.Get(MenuIds.DataBlockScratchpad).Build(aircraft, Context(), host));

    /// <summary>
    /// The Scratchpad… entry opens a flyout whose first row is the box, <c>SP</c> drawn in it, holding the present Scratchpad 1
    /// and, as it opens, the focus with the whole text selected, so typing replaces it.
    /// </summary>
    [AvaloniaFact]
    public void Scratchpad_Flyout_OpensOnTheBox_HoldingScratchpad1Selected()
    {
        var window = new Window { Width = 600, Height = 900 };
        window.Show();
        var host = new RecordingMenuHost("");
        MenuItem scratchpad = Scratchpad(new FakeMenuAircraft { Scratchpad1 = "I30" }, host);
        var menu = new ContextMenu { Items = { scratchpad } };
        try
        {
            menu.Open(window);
            Dispatcher.UIThread.RunJobs();
            scratchpad.IsSubMenuOpen = true;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("Scratchpad…", scratchpad.Header);
            MenuItem first = Assert.IsType<MenuItem>(scratchpad.Items[0]);
            InlineBoxRow row = Assert.IsType<InlineBoxRow>(first.Header);
            Assert.Equal("I30", BoxOf(row).Text);
            Assert.True(BoxOf(row).IsFocused, "the Scratchpad box did not take focus when the flyout opened");
            Assert.Equal(
                (0, 3),
                (Math.Min(BoxOf(row).SelectionStart, BoxOf(row).SelectionEnd), Math.Max(BoxOf(row).SelectionStart, BoxOf(row).SelectionEnd))
            );
        }
        finally
        {
            menu.Close();
            window.Close();
        }
    }

    /// <summary>
    /// The seeded Scratchpad box selects the whole seed again each time it takes the focus back still holding it untouched, so
    /// the first keystroke after a visit to another row still replaces it.
    /// </summary>
    [AvaloniaFact]
    public void ScratchpadBox_RefocusedWhileUntouched_SelectsTheSeedAgain()
    {
        var window = new Window { Width = 600, Height = 900 };
        window.Show();
        var host = new RecordingMenuHost("");
        MenuItem scratchpad = Scratchpad(new FakeMenuAircraft { Scratchpad1 = "ABC" }, host);
        var menu = new ContextMenu { Items = { scratchpad } };
        try
        {
            menu.Open(window);
            Dispatcher.UIThread.RunJobs();
            scratchpad.IsSubMenuOpen = true;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            InlineBoxRow row = BoxRow(scratchpad);
            MenuItem clear = Assert.Single(scratchpad.Items.OfType<MenuItem>(), m => (m.Header as string) == "Clear Scratchpad 1");

            Assert.True(clear.Focus(), "the Clear Scratchpad 1 row did not take the focus");
            Dispatcher.UIThread.RunJobs();
            Assert.False(BoxOf(row).IsFocused, "the Scratchpad box kept the focus after another row took it");
            BoxOf(row).Focus();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("ABC", BoxOf(row).Text);
            Assert.True(BoxOf(row).IsFocused, "the Scratchpad box did not take the focus back");
            Assert.Equal(
                (0, 3),
                (Math.Min(BoxOf(row).SelectionStart, BoxOf(row).SelectionEnd), Math.Max(BoxOf(row).SelectionStart, BoxOf(row).SelectionEnd))
            );
        }
        finally
        {
            menu.Close();
            window.Close();
        }
    }

    /// <summary>
    /// Enter on the present scratchpad, as seeded or typed again in any case, sends nothing: <c>SP</c> with the present value
    /// is the sim's toggle back to the previous scratchpad.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(null)]
    [InlineData("I30")]
    [InlineData(" i30 ")]
    public void Scratchpad_Box_PresentValue_EnterSendsNothing(string? typed)
    {
        var host = new RecordingMenuHost("");
        InlineBoxRow row = BoxRow(Scratchpad(new FakeMenuAircraft { Scratchpad1 = "I30" }, host));

        if (typed is not null)
        {
            BoxOf(row).Text = typed;
        }

        PressEnter(BoxOf(row));

        Assert.Empty(host.Sent);
        Assert.DoesNotContain(RefusableBox.RefusedClass, BoxOf(row).Classes);
        Assert.Equal($"SP {TakeoffFlyout.Unfilled}", row.CommandColumn.Text);
    }

    /// <summary>New text in the Scratchpad box is sent trimmed and upper-cased after <c>SP</c>, on Enter.</summary>
    [AvaloniaTheory]
    [InlineData("ABC", "SP ABC")]
    [InlineData(" ab ", "SP AB")]
    public void Scratchpad_Box_NewText_EnterSendsIt(string typed, string command)
    {
        var host = new RecordingMenuHost("");
        InlineBoxRow row = BoxRow(Scratchpad(new FakeMenuAircraft { Scratchpad1 = "I30" }, host));

        BoxOf(row).Text = typed;
        PressEnter(BoxOf(row));

        Assert.Equal([(Callsign, command, Initials)], host.Sent);
        Assert.Empty(host.InputPlaceholders);
    }

    /// <summary>A click on the Scratchpad box row sends its text too.</summary>
    [AvaloniaFact]
    public void Scratchpad_Box_ClickSendsTheText()
    {
        var host = new RecordingMenuHost("");
        MenuItem scratchpad = Scratchpad(new FakeMenuAircraft(), host);
        InlineBoxRow row = BoxRow(scratchpad);

        BoxOf(row).Text = "V28";
        Click(Assert.Single(scratchpad.Items.OfType<MenuItem>(), m => ReferenceEquals(m.Header, row)));

        Assert.Equal([(Callsign, "SP V28", Initials)], host.Sent);
    }

    /// <summary>A blank Scratchpad box sends nothing: the Clear rows clear a scratchpad.</summary>
    [AvaloniaTheory]
    [InlineData("")]
    [InlineData("   ")]
    public void Scratchpad_Box_Blank_EnterSendsNothing(string typed)
    {
        var host = new RecordingMenuHost("");
        InlineBoxRow row = BoxRow(Scratchpad(new FakeMenuAircraft { Scratchpad1 = "I30" }, host));

        BoxOf(row).Text = typed;
        PressEnter(BoxOf(row));

        Assert.Empty(host.Sent);
        Assert.Equal($"SP {TakeoffFlyout.Unfilled}", row.CommandColumn.Text);
    }

    /// <summary>
    /// Text longer than the room's scratchpad limit is refused inline: the red underline, the reason line naming the limit, "can't
    /// send" in the command column; Enter and a click send nothing.
    /// </summary>
    [AvaloniaFact]
    public void Scratchpad_Box_LongerThanTheLimit_RefusesInline_SendsNothing()
    {
        var host = new RecordingMenuHost("") { ScratchpadMaxLength = 3 };
        MenuItem scratchpad = Scratchpad(new FakeMenuAircraft(), host);
        InlineBoxRow row = BoxRow(scratchpad);

        BoxOf(row).Text = "ABCD";

        Assert.Contains(RefusableBox.RefusedClass, BoxOf(row).Classes);
        Assert.True(row.Refusal.ReasonLine.IsVisible, "A refused scratchpad should show its reason line.");
        Assert.Equal("Scratchpad holds at most 3 characters", row.Refusal.ReasonLine.Text);
        Assert.Equal(InlineBoxRow.CantSend, row.CommandColumn.Text);

        PressEnter(BoxOf(row));
        Click(Assert.Single(scratchpad.Items.OfType<MenuItem>(), m => ReferenceEquals(m.Header, row)));

        Assert.Empty(host.Sent);
    }

    /// <summary>Where the room allows four-character scratchpads, four characters are sent.</summary>
    [AvaloniaFact]
    public void Scratchpad_Box_FourCharactersUnderAFourCharacterLimit_EnterSendsThem()
    {
        var host = new RecordingMenuHost("") { ScratchpadMaxLength = 4 };
        InlineBoxRow row = BoxRow(Scratchpad(new FakeMenuAircraft(), host));

        BoxOf(row).Text = "ABCD";
        PressEnter(BoxOf(row));

        Assert.Equal([(Callsign, "SP ABCD", Initials)], host.Sent);
    }

    /// <summary>
    /// The destination the scope shows where no real Scratchpad 1 is set (<see cref="AircraftModel.AutoScratchpad1"/>) seeds
    /// nothing: seeded, it would be sent as typed text.
    /// </summary>
    [AvaloniaFact]
    public void Scratchpad_Box_AutoScratchpad1Alone_SeedsNothing()
    {
        var host = new RecordingMenuHost("");
        var aircraft = new AircraftModel { Callsign = Callsign, AutoScratchpad1 = "OAK" };

        InlineBoxRow row = BoxRow(Scratchpad(aircraft, host));

        Assert.Equal("", BoxOf(row).Text ?? "");
    }

    /// <summary>
    /// Clear Scratchpad 1 and Clear Scratchpad 2 follow the box after a separator, each only while its scratchpad holds a value
    /// (a bare <c>SP1</c> or <c>SP2</c> on an empty slot puts back the previous value); with neither, no separator.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(null, null, new string[0])]
    [InlineData("I30", null, new[] { "---", "Clear Scratchpad 1" })]
    [InlineData(null, "HEL", new[] { "---", "Clear Scratchpad 2" })]
    [InlineData("I30", "HEL", new[] { "---", "Clear Scratchpad 1", "Clear Scratchpad 2" })]
    public void Scratchpad_Flyout_ClearRowsShowOnlyWhileTheirSlotHoldsAValue(string? scratchpad1, string? scratchpad2, string[] after)
    {
        var host = new RecordingMenuHost("");

        MenuItem scratchpad = Scratchpad(new FakeMenuAircraft { Scratchpad1 = scratchpad1, Scratchpad2 = scratchpad2 }, host);

        Assert.Equal([$"Scratchpad — SP {TakeoffFlyout.Unfilled}", .. after], scratchpad.Items.Select(DescribeRow));
    }

    /// <summary>Clear Scratchpad 1 sends a bare <c>SP1</c> and Clear Scratchpad 2 a bare <c>SP2</c>.</summary>
    [AvaloniaTheory]
    [InlineData("Clear Scratchpad 1", "SP1")]
    [InlineData("Clear Scratchpad 2", "SP2")]
    public void Scratchpad_Flyout_ClearRow_SendsTheBareSlotVerb(string label, string command)
    {
        var host = new RecordingMenuHost("");
        MenuItem scratchpad = Scratchpad(new FakeMenuAircraft { Scratchpad1 = "I30", Scratchpad2 = "HEL" }, host);

        Click(Assert.Single(scratchpad.Items.OfType<MenuItem>(), m => (m.Header as string) == label));

        Assert.Equal([(Callsign, command, Initials)], host.Sent);
    }

    /// <summary>The one inline box row among <paramref name="flyout"/>'s items.</summary>
    private static InlineBoxRow BoxRow(MenuItem flyout) =>
        Assert.IsType<InlineBoxRow>(Assert.Single(flyout.Items.OfType<MenuItem>(), m => m.Header is InlineBoxRow).Header);

    private static void PressEnter(Control box) => box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });

    /// <summary>The plain text box of <paramref name="row"/>, a row that is no fix box.</summary>
    private static TextBox BoxOf(InlineBoxRow row) => Assert.IsType<TextBox>(row.Input);

    [AvaloniaFact]
    public void Approach_Header_ShowsActiveThenExpected()
    {
        var host = new RecordingMenuHost("");

        Assert.Equal("Approach", SharedMenuGroups.Approach(new FakeMenuAircraft(), Context(), host)!.Header as string);
        Assert.Equal(
            "Approach (exp: I28R)",
            SharedMenuGroups.Approach(new FakeMenuAircraft { ExpectedApproach = ApproachId }, Context(), host)!.Header as string
        );
        Assert.Equal(
            "Approach (I30)",
            SharedMenuGroups.Approach(new FakeMenuAircraft { ActiveApproachId = "I30", ExpectedApproach = ApproachId }, Context(), host)!.Header
                as string
        );
    }

    /// <summary>A destination without runway data leaves Cleared visual approach nothing to pick (ruling 3): disabled, inert.</summary>
    [AvaloniaFact]
    public void ClearedVisual_NoRunwayData_IsDisabledWithTheReason()
    {
        TestVnasData.EnsureInitialized();
        Assert.Empty(NavigationDatabase.Instance.GetRunways(UnknownAirport));
        var host = new RecordingMenuHost("");
        FakeMenuAircraft aircraft = VisualArrival() with { Destination = UnknownAirport };

        MenuItem? item = MenuCatalog.Get(MenuIds.ApproachClearedVisual).Build(aircraft, Context(), host);

        AssertUnavailable(item, "Cleared visual approach", "no runway data at ZZZZ");
    }

    /// <summary>Without a destination, Cleared visual approach has nothing to pick (ruling 3): disabled with the reason, inert.</summary>
    [AvaloniaFact]
    public void ClearedVisual_NoDestination_IsDisabledWithTheReason()
    {
        var host = new RecordingMenuHost("");
        var aircraft = new FakeMenuAircraft { AssignedRunway = "09L", SituationFlags = SituationFlags.HasReportedFieldInSight };

        MenuItem? item = MenuCatalog.Get(MenuIds.ApproachClearedVisual).Build(aircraft, Context(), host);

        AssertUnavailable(item, "Cleared visual approach", "no destination");
        Assert.Empty(host.Sent);
    }

    /// <summary>
    /// At a destination with no STARs (KLVK), Join STAR has nothing to pick (ruling 3): disabled with the reason, its strip
    /// button inert, and the separate Descend via STAR entry stays in Procedures.
    /// </summary>
    [AvaloniaFact]
    public void JoinStar_NoStars_IsDisabledWithTheReason()
    {
        TestVnasData.EnsureInitialized();
        Assert.Empty(NavigationDatabase.Instance.GetStars("KLVK"));
        var host = new RecordingMenuHost("");
        var aircraft = new FakeMenuAircraft { Destination = "KLVK", Route = $"SUNOL {StarId}" };

        AssertUnavailable(MenuCatalog.Get(MenuIds.ProceduresJoinStar).Build(aircraft, Context(), host), "Join STAR", "no STARs at KLVK");
        AssertUnavailable(MenuCatalog.Get(MenuIds.ProceduresJoinStar).Build(new FakeMenuAircraft(), Context(), host), "Join STAR", "no destination");
        Assert.Contains("Descend via STAR", SharedMenuGroups.Approach(aircraft, Context(), host)!.Items.Select(Describe));
    }

    /// <summary>
    /// What the Cross fix and Depart fix entries send over a route fix is a whole command the sim's parser accepts, never the
    /// fix alone (YAAT-910).
    /// </summary>
    [AvaloniaTheory]
    [InlineData(MenuIds.ProceduresCrossFix)]
    [InlineData(MenuIds.ProceduresDepartFix)]
    public void CrossAndDepartFix_SendACommandTheParserAccepts(string id)
    {
        TestVnasData.EnsureInitialized();
        var host = new RecordingMenuHost(Fix);
        var aircraft = new FakeMenuAircraft { RouteFixes = [Fix] };

        MenuItem item = Assert.IsType<MenuItem>(MenuCatalog.Get(id).Build(aircraft, Context(), host));
        item.IsSubMenuOpen = true;
        FixValueFlyoutPanel panel = Assert.IsAssignableFrom<FixValueFlyoutPanel>(
            Assert.IsAssignableFrom<FlyoutPanelHost>(Assert.Single(item.Items.OfType<MenuItem>()).Header).Child
        );
        TextBox value = (panel is CrossFixFlyoutPanel cross) ? cross.AltitudeBox : Assert.IsType<DepartFixFlyoutPanel>(panel).HeadingBox;
        value.Text = "110";
        panel.RouteRows[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Empty(host.InputPlaceholders);
        (string _, string command, string _) = Assert.Single(host.Sent);
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(command);
        Assert.True(parsed.IsSuccess, $"{command}: {parsed.Reason}");
    }

    /// <summary>Join airway… opens the flyout; its filed airway row sends <c>JAWY V25</c>, which the parser reads, and no popup opens.</summary>
    [AvaloniaFact]
    public void JoinAirway_FiledRowSendsJawy_TheParserReads()
    {
        TestVnasData.EnsureInitialized();
        var host = new RecordingMenuHost("");
        var aircraft = new FakeMenuAircraft { Route = "OAK V25 SUNOL" };

        MenuItem item = Assert.IsType<MenuItem>(MenuCatalog.Get(MenuIds.ProceduresJoinAirway).Build(aircraft, Context(), host));
        Assert.Equal("Join airway…", item.Header as string);
        item.IsSubMenuOpen = true;
        JoinAirwayFlyoutPanel panel = Assert.IsType<JoinAirwayFlyoutPanel>(
            Assert.IsAssignableFrom<FlyoutPanelHost>(Assert.Single(item.Items.OfType<MenuItem>()).Header).Child
        );
        panel.Rows[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Empty(host.InputPlaceholders);
        Assert.Equal([(Callsign, "JAWY V25", Initials)], host.Sent);
        Assert.Equal("V25", Assert.IsType<JoinAirwayCommand>(CommandParser.Parse("JAWY V25").Value).AirwayId);
    }

    /// <summary>
    /// SWA1957 to KOAK filing OAKES3: the Join STAR flyout (ruling 1, lane C) has a tab per KOAK STAR, the filed one first marked
    /// ◆ and selected; its rows are Join, Join at each entry fix and For runway each runway its transitions serve (RW28B gives
    /// 28L and 28R), each sending a <c>JARR</c> the parser reads; Descend via STAR sits under the tabs and leaves Procedures.
    /// </summary>
    [AvaloniaFact]
    public void JoinStar_FiledStar_TabbedFlyout_FiledFirst()
    {
        TestVnasData.EnsureInitialized();
        CifpStarProcedure oakes = NavigationDatabase.Instance.GetStar(ApproachAirport, "OAKES3") ?? throw new InvalidOperationException("No OAKES3.");
        Assert.NotEmpty(oakes.EnrouteTransitions);
        var host = new RecordingMenuHost("");
        var aircraft = new FakeMenuAircraft { Destination = ApproachAirport, Route = $"RUSME.OAKES3.{ApproachAirport}" };

        MenuItem? entry = MenuCatalog.Get(MenuIds.ProceduresJoinStar).Build(aircraft, Context(), host);
        Assert.Equal("Join STAR", entry?.Header as string);
        JoinStarFlyoutPanel panel = OpenStarFlyout(entry);

        List<string> rows =
        [
            "Join · filed · nearest fix ahead — JARR OAKES3",
            .. oakes.EnrouteTransitions.Keys.Order(StringComparer.OrdinalIgnoreCase).Select(fix => $"Join at {fix} — JARR OAKES3 {fix}"),
            "For runway 12 — JARR OAKES3 12",
            "For runway 28L — JARR OAKES3 28L",
            "For runway 28R — JARR OAKES3 28R",
            "For runway 30 — JARR OAKES3 30",
        ];
        Assert.Equal(["Join STAR", "[OAKES3 ◆] | AANET1 | EMZOH4 | PIRAT3 | PXN6 | WNDSR2", .. rows, "---", "Descend via STAR — DVIA"], panel.Lines);
        Assert.All(rows, row => Assert.True(CommandParser.Parse(row[(row.IndexOf("— ", StringComparison.Ordinal) + 2)..]).IsSuccess, row));

        panel.Rows[^2].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        panel.DescendViaRow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal([(Callsign, "JARR OAKES3 28R", Initials), (Callsign, "DVIA", Initials)], host.Sent);

        panel.Strip.Select(1);
        Assert.Equal("OAKES3 ◆ | [AANET1] | EMZOH4 | PIRAT3 | PXN6 | WNDSR2", panel.Lines[1]);
        Assert.Equal("Join · nearest fix ahead — JARR AANET1", panel.Lines[2]);
        Assert.DoesNotContain("Descend via STAR", SharedMenuGroups.Approach(aircraft, Context(), host)!.Items.Select(Describe));
    }

    /// <summary>With no STAR filed, the Join STAR flyout's tabs are alphabetical and the first is selected.</summary>
    [AvaloniaFact]
    public void JoinStar_NoFiledStar_FirstTabSelected()
    {
        TestVnasData.EnsureInitialized();
        var host = new RecordingMenuHost("");
        var aircraft = new FakeMenuAircraft { Destination = ApproachAirport, Route = "" };

        JoinStarFlyoutPanel panel = OpenStarFlyout(MenuCatalog.Get(MenuIds.ProceduresJoinStar).Build(aircraft, Context(), host));
        panel.Rows[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal("[AANET1] | EMZOH4 | OAKES3 | PIRAT3 | PXN6 | WNDSR2", panel.Lines[1]);
        Assert.Equal([(Callsign, "JARR AANET1", Initials)], host.Sent);
    }

    /// <summary>
    /// Right and Left switch the Join STAR flyout to the next and previous tab and focus a row on the tab that lands, so a
    /// second Enter sends that tab's row (the keyboard contract in <see cref="JoinStarFlyoutPanel"/>).
    /// </summary>
    [AvaloniaFact]
    public void JoinStar_LeftAndRight_SwitchTabs_AndFocusARowOnTheNewTab()
    {
        TestVnasData.EnsureInitialized();
        var host = new RecordingMenuHost("");
        var aircraft = new FakeMenuAircraft { Destination = ApproachAirport, Route = $"RUSME.OAKES3.{ApproachAirport}" };
        (Window window, JoinStarFlyoutPanel panel) = OpenStarFlyoutInWindow(
            MenuCatalog.Get(MenuIds.ProceduresJoinStar).Build(aircraft, Context(), host)
        );

        Assert.Same(panel.Rows[0], FocusedButton(window));
        int opening = panel.Strip.SelectedIndex;

        PressKey(window, Key.Right);

        Assert.Equal(opening + 1, panel.Strip.SelectedIndex);
        Assert.Same(panel.Rows[0], FocusedButton(window));
        Assert.Contains("[AANET1]", panel.Lines[1]);

        PressKey(window, Key.Left);

        Assert.Equal(opening, panel.Strip.SelectedIndex);
        Assert.Same(panel.Rows[0], FocusedButton(window));
        Assert.Contains("[OAKES3 ◆]", panel.Lines[1]);
        Assert.Empty(host.Sent);
    }

    /// <summary>Down and Tab walk the tab's rows and reach Descend via STAR under them; Up walks back through them.</summary>
    [AvaloniaFact]
    public void JoinStar_DownAndTab_WalkTheRowsToDescendViaStar_AndUpWalksBack()
    {
        TestVnasData.EnsureInitialized();
        var host = new RecordingMenuHost("");
        var aircraft = new FakeMenuAircraft { Destination = ApproachAirport, Route = $"RUSME.OAKES3.{ApproachAirport}" };
        (Window window, JoinStarFlyoutPanel panel) = OpenStarFlyoutInWindow(
            MenuCatalog.Get(MenuIds.ProceduresJoinStar).Build(aircraft, Context(), host)
        );

        for (int step = 1; step < panel.Rows.Count; step++)
        {
            PressKey(window, Key.Down);
        }

        Assert.Same(panel.Rows[^1], FocusedButton(window));
        PressKey(window, Key.Tab);
        Assert.Same(panel.DescendViaRow, FocusedButton(window));
        PressKey(window, Key.Up);
        Assert.Same(panel.Rows[^1], FocusedButton(window));
        Assert.Empty(host.Sent);
    }

    /// <summary>Up at the first row and Down or Tab at Descend via STAR stay where they are, at either end of the walk.</summary>
    [AvaloniaFact]
    public void JoinStar_UpAndDown_StopAtEitherEnd()
    {
        TestVnasData.EnsureInitialized();
        var host = new RecordingMenuHost("");
        var aircraft = new FakeMenuAircraft { Destination = ApproachAirport, Route = $"RUSME.OAKES3.{ApproachAirport}" };
        (Window window, JoinStarFlyoutPanel panel) = OpenStarFlyoutInWindow(
            MenuCatalog.Get(MenuIds.ProceduresJoinStar).Build(aircraft, Context(), host)
        );

        PressKey(window, Key.Up);
        Assert.Same(panel.Rows[0], FocusedButton(window));
        PressKey(window, Key.Tab, RawInputModifiers.Shift);
        Assert.Same(panel.Rows[0], FocusedButton(window));

        for (int step = 0; step <= panel.Rows.Count; step++)
        {
            PressKey(window, Key.Down);
        }

        Assert.Same(panel.DescendViaRow, FocusedButton(window));
        PressKey(window, Key.Down);
        Assert.Same(panel.DescendViaRow, FocusedButton(window));
        PressKey(window, Key.Tab);
        Assert.Same(panel.DescendViaRow, FocusedButton(window));
        Assert.Empty(host.Sent);
    }

    /// <summary>Opens <paramref name="entry"/>'s Join STAR flyout over a shown window, so its keyboard focus is real.</summary>
    private static (Window Window, JoinStarFlyoutPanel Panel) OpenStarFlyoutInWindow(MenuItem? entry)
    {
        var window = new Window { Width = 600, Height = 900 };
        window.Show();
        var menu = new ContextMenu { Items = { Assert.IsType<MenuItem>(entry) } };
        menu.Open(window);
        Dispatcher.UIThread.RunJobs();
        MenuItem item = Assert.IsType<MenuItem>(menu.Items[0]);
        item.IsSubMenuOpen = true;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        JoinStarFlyoutPanel panel = Assert.IsType<JoinStarFlyoutPanel>(
            Assert.IsType<JoinStarFlyoutHost>(Assert.Single(item.Items.OfType<MenuItem>()).Header).Child
        );
        return (window, panel);
    }

    /// <summary>The button holding the window's keyboard focus, or null when no row holds it.</summary>
    private static Button? FocusedButton(Window window) =>
        (window.FocusManager?.GetFocusedElement() as Visual)?.GetSelfAndVisualAncestors().OfType<Button>().FirstOrDefault();

    private static void PressKey(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None) => window.DispatchKey(key, modifiers);

    /// <summary>Opens <paramref name="entry"/>'s Join STAR flyout and returns its panel.</summary>
    private static JoinStarFlyoutPanel OpenStarFlyout(MenuItem? entry)
    {
        MenuItem item = Assert.IsType<MenuItem>(entry);
        item.IsSubMenuOpen = true;
        return Assert.IsType<JoinStarFlyoutPanel>(Assert.IsType<JoinStarFlyoutHost>(Assert.Single(item.Items.OfType<MenuItem>()).Header).Child);
    }

    /// <summary>
    /// Each join-radial entry opens the Join radial flyout on its side and sends one token, the navaid and the three-digit
    /// radial (north 360), which the parser reads; no popup opens (YAAT-910).
    /// </summary>
    [AvaloniaTheory]
    [InlineData(MenuIds.ProceduresJoinRadialOutbound, "Join radial outbound…", "184", "JRADO RBL184")]
    [InlineData(MenuIds.ProceduresJoinRadialInbound, "Join radial inbound…", "184", "JRADI RBL184")]
    [InlineData(MenuIds.ProceduresJoinRadialOutbound, "Join radial outbound…", "360", "JRADO RBL360")]
    public void JoinRadial_SendsOneToken_TheParserReads(string id, string label, string radial, string command)
    {
        TestVnasData.EnsureInitialized();
        var host = new RecordingMenuHost("");
        // Half a degree (30 NM) south of Red Bluff, well inside the 130 NM a route navaid opens the flyout from.
        (double lat, double lon) = NavigationDatabase.Instance.GetFixPosition("RBL")!.Value;
        var aircraft = new FakeMenuAircraft { Position = new LatLon(lat - 0.5, lon), RouteFixes = ["RBL"] };

        MenuItem item = Assert.IsType<MenuItem>(MenuCatalog.Get(id).Build(aircraft, Context(), host));
        Assert.Equal(label, item.Header as string);
        item.IsSubMenuOpen = true;
        JoinRadialFlyoutPanel panel = Assert.IsType<JoinRadialFlyoutPanel>(
            Assert.IsAssignableFrom<FlyoutPanelHost>(Assert.Single(item.Items.OfType<MenuItem>()).Header).Child
        );
        panel.RadialBox.Text = radial;
        panel.RadialBox.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });

        Assert.Empty(host.InputPlaceholders);
        Assert.Equal([(Callsign, command, Initials)], host.Sent);
        ParsedCommand? parsed = CommandParser.Parse(command).Value;
        (string fix, int parsedRadial) = parsed switch
        {
            JoinRadialOutboundCommand outbound => (outbound.FixName, outbound.Radial),
            JoinRadialInboundCommand inbound => (inbound.FixName, inbound.Radial),
            _ => throw new InvalidOperationException($"{command} parsed as {parsed}"),
        };
        Assert.Equal(("RBL", int.Parse(radial, CultureInfo.InvariantCulture)), (fix, parsedRadial));
    }

    /// <summary>
    /// PTAC… opens the PTAC flyout; a typed heading and altitude and a click on an approach row send one PTAC command the
    /// parser reads, and no popup opens.
    /// </summary>
    [AvaloniaFact]
    public void Ptac_SendsACommandTheParserAccepts()
    {
        TestVnasData.EnsureInitialized();
        var host = new RecordingMenuHost("");
        var aircraft = new FakeMenuAircraft { Destination = ApproachAirport };

        MenuItem item = Assert.IsType<MenuItem>(MenuCatalog.Get(MenuIds.ProceduresPtac).Build(aircraft, Context(), host));
        Assert.Equal("PTAC…", item.Header as string);
        item.IsSubMenuOpen = true;
        PtacFlyoutPanel panel = Assert.IsType<PtacFlyoutPanel>(
            Assert.IsAssignableFrom<FlyoutPanelHost>(Assert.Single(item.Items.OfType<MenuItem>()).Header).Child
        );
        panel.HeadingBox.Text = "280";
        panel.AltitudeBox.Text = "025";
        Assert.Single(panel.Rows, row => row.ApproachId == ApproachId).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Empty(host.InputPlaceholders);
        Assert.Equal([(Callsign, "PTAC 280 025 I28R", Initials)], host.Sent);
        PositionTurnAltitudeClearanceCommand parsed = Assert.IsType<PositionTurnAltitudeClearanceCommand>(
            CommandParser.Parse(host.Sent[0].Command).Value
        );
        Assert.Equal(280, parsed.MagneticHeading?.Degrees);
        Assert.Equal(2500, parsed.Altitude);
        Assert.Equal(ApproachId, parsed.ApproachId);
    }

    [AvaloniaFact]
    public void Tower_CancelLandingClearance_SendsClc()
    {
        var host = new RecordingMenuHost("");
        var ac = new AircraftModel
        {
            Callsign = Callsign,
            IsOnGround = false,
            CurrentPhase = "FinalApproach",
            FlightRules = "IFR",
            AssignedRunway = "28R",
            LandingClearance = "CTL",
        };

        MenuItem? tower = SharedMenuGroups.Tower(ac, Context(), host);

        Assert.NotNull(tower);
        Click(Assert.Single(tower.Items.OfType<MenuItem>(), m => m.Header is "Cancel landing clearance"));
        Assert.Equal([(Callsign, "CLC", Initials)], host.Sent);
    }

    public static TheoryData<string, string> ClearedForTakeoffChildren() =>
        new()
        {
            { "Cleared for takeoff", "CTO" },
            { "Make left closed traffic", "CTO MLT" },
            { "Make right closed traffic", "CTO MRT" },
            { "Straight out", "CTO MSO" },
            { "Left crosswind departure", "CTO MLC" },
            { "Right crosswind departure", "CTO MRC" },
            { "Left downwind departure", "CTO MLD" },
            { "Right downwind departure", "CTO MRD" },
            { "Left turnout (45°)", "CTO ML45" },
            { "Right turnout (45°)", "CTO MR45" },
        };

    [AvaloniaTheory]
    [MemberData(nameof(ClearedForTakeoffChildren))]
    public void Tower_Cto_ChildrenSendTheirArguments(string label, string command)
    {
        var host = new RecordingMenuHost("");

        List<MenuItem> children = ClearedForTakeoffChildItems(host);

        Click(Assert.Single(children, m => (m.Header as TakeoffFlyoutRow)?.Label == label));
        Assert.Equal([(Callsign, command, Initials)], host.Sent);
    }

    [AvaloniaTheory]
    [InlineData("", "CTO")]
    [InlineData("   ", "CTO")]
    [InlineData("  RH 3000 ", "CTO RH 3000")]
    [InlineData("28R", "CTO 28R")]
    public void Tower_CtoCustom_BlankSendsCto_TextIsTrimmed(string input, string command)
    {
        var host = new RecordingMenuHost("");

        List<MenuItem> children = ClearedForTakeoffChildItems(host);
        MenuItem custom = Assert.Single(children, m => m.Header is TakeoffFlyoutRow { Label: TakeoffFlyout.CustomLabel });
        ((TakeoffFlyoutRow)custom.Header!).InputText = input;

        Click(custom);
        Assert.Empty(host.InputPlaceholders);
        Assert.Equal([(Callsign, command, Initials)], host.Sent);
    }

    /// <summary>
    /// The Custom row is an inline box row, always open (ruling 4): the last item, after a separator, a box whose fixed <c>CTO</c>
    /// prefix is drawn inside it, and no popup.
    /// </summary>
    [AvaloniaFact]
    public void Tower_CtoCustom_IsAnInlineBoxRow_WithTheCtoPrefixInTheBox()
    {
        var host = new RecordingMenuHost("");

        List<MenuItem> children = ClearedForTakeoffChildItems(host);
        MenuItem custom = children[^1];
        TakeoffFlyoutRow row = Assert.IsType<TakeoffFlyoutRow>(custom.Header);
        Control view = Assert.IsAssignableFrom<Control>(custom.HeaderTemplate?.Build(row));
        TextBox box = view.GetLogicalDescendants().OfType<TextBox>().Single(b => b.Name == TakeoffFlyout.CustomBoxName);

        Assert.Equal(TakeoffRowInput.Custom, row.Input);
        Assert.Equal("CTO", Assert.IsType<TextBlock>(box.InnerLeftContent).Text);
        Assert.Equal("Custom — CTO", row.ToString());
        Assert.Null(custom.Tag);
    }

    /// <summary>The Cleared for takeoff submenu's items for a VFR departure lined up on 30, which is offered every one.</summary>
    private static List<MenuItem> ClearedForTakeoffChildItems(RecordingMenuHost host)
    {
        var ac = new AircraftModel
        {
            Callsign = Callsign,
            IsOnGround = true,
            CurrentPhase = "LinedUpAndWaiting",
            FlightRules = "VFR",
            AssignedRunway = "30",
        };
        MenuItem? cto = MenuCatalog.Get(MenuIds.TowerClearedForTakeoff).Build(ac, Context(), host);
        Assert.NotNull(cto);
        Assert.Equal("Cleared for takeoff 30", cto.Header as string);
        return [.. cto.Items.OfType<MenuItem>()];
    }

    /// <summary>A rolling-out B738 with the named exits ahead listed on both sides, W2 the planned one.</summary>
    private static FakeMenuAircraft RollingOutWithExits(IReadOnlyList<ExitAheadDto>? exits) =>
        new()
        {
            IsOnGround = true,
            CurrentPhase = "Landing",
            Situation = AircraftSituation.RolloutExit,
            SituationFlags = SituationFlags.RolloutDecelerating,
            AssignedRunway = "30",
            ExitsAhead = exits,
        };

    private static readonly ExitAheadDto[] ExitsBothSides =
    [
        new("W1", ExitSide.Left, 900, false, 90),
        new("W2", ExitSide.Right, 1800, true, 45),
        new("W3", ExitSide.Right, 3600, false, 90),
        new("W4", ExitSide.Right, 12400, false, 128),
    ];

    /// <summary>The flyout's rows as shown: a section header's text, <c>---</c> for a separator, else the row's accessible name.</summary>
    private static List<string> FlyoutRows(MenuItem flyout) =>
        [
            .. flyout.Items.Select(child =>
                child switch
                {
                    Separator => "---",
                    MenuItem { IsEnabled: false } section => $"[{section.Header}]",
                    MenuItem row => (AutomationProperties.GetName(row) is { Length: > 0 } name) ? name : row.Header as string ?? "",
                    _ => throw new Xunit.Sdk.XunitException($"unexpected flyout child {child}"),
                }
            ),
        ];

    [AvaloniaFact]
    public void ExitFlyout_RightSide_ListsRowsThenPilotsChoice()
    {
        var host = new RecordingMenuHost("");
        MenuItem flyout = Assert.IsType<MenuItem>(
            MenuCatalog.Get(MenuIds.TowerExitRight).Build(RollingOutWithExits(ExitsBothSides), Context(), host)
        );

        Assert.Equal("Exit right", flyout.Header);
        Assert.Equal(
            [
                "[Exits ahead, right side · from the aircraft]",
                "Taxiway W2 · planned · ~1,800 ft — ER W2",
                "Taxiway W3 · ~3,600 ft — ER W3",
                "Taxiway W4 · ~12,400 ft — ER W4",
                "---",
                "Pilot's choice — ER",
            ],
            FlyoutRows(flyout)
        );

        List<MenuItem> picks = [.. flyout.Items.OfType<MenuItem>().Where(item => item.IsEnabled)];
        foreach (MenuItem pick in picks)
        {
            Click(pick);
        }

        Assert.Equal(
            [(Callsign, "ER W2", Initials), (Callsign, "ER W3", Initials), (Callsign, "ER W4", Initials), (Callsign, "ER", Initials)],
            host.Sent
        );
    }

    [AvaloniaFact]
    public void ExitFlyout_LeftSide_ListsOnlyTheLeftRows()
    {
        var host = new RecordingMenuHost("");
        MenuItem flyout = Assert.IsType<MenuItem>(MenuCatalog.Get(MenuIds.TowerExitLeft).Build(RollingOutWithExits(ExitsBothSides), Context(), host));

        Assert.Equal("Exit left", flyout.Header);
        Assert.Equal(
            ["[Exits ahead, left side · from the aircraft]", "Taxiway W1 · ~900 ft — EL W1", "---", "Pilot's choice — EL"],
            FlyoutRows(flyout)
        );
    }

    /// <summary>
    /// With no list (no layout or hold-short data for the runway) the side is not judged: both entries are offered, each with
    /// its pilot's-choice row alone.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(MenuIds.TowerExitLeft, "EL")]
    [InlineData(MenuIds.TowerExitRight, "ER")]
    public void ExitFlyout_NoList_OnlyPilotsChoice(string id, string verb)
    {
        FakeMenuAircraft aircraft = RollingOutWithExits(null);
        var host = new RecordingMenuHost("");
        Assert.True(MenuCatalog.Get(id).IsApplicable(aircraft, Context()));
        MenuItem flyout = Assert.IsType<MenuItem>(MenuCatalog.Get(id).Build(aircraft, Context(), host));

        MenuItem only = Assert.IsType<MenuItem>(Assert.Single(flyout.Items));
        Assert.Equal($"Pilot's choice — {verb}", AutomationProperties.GetName(only));
        Click(only);
        Assert.Equal([(Callsign, verb, Initials)], host.Sent);
    }

    /// <summary>
    /// A side the list holds no exit on has no entry, pilot's choice included: the exit search falls back to the other side
    /// when nothing is makeable on the one asked for, so the aircraft would turn the opposite way (7110.65 3-10-9.a phrases
    /// the exit by side). An empty list (nothing makeable) leaves neither side.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("right only", MenuIds.TowerExitLeft)]
    [InlineData("empty", MenuIds.TowerExitLeft)]
    [InlineData("empty", MenuIds.TowerExitRight)]
    public void ExitFlyout_NoExitOnTheSide_HasNoEntry(string list, string id)
    {
        FakeMenuAircraft aircraft = RollingOutWithExits((list == "empty") ? [] : [new ExitAheadDto("W2", ExitSide.Right, 1800, true, 45)]);

        Assert.False(MenuCatalog.Get(id).IsApplicable(aircraft, Context()));
        Assert.Null(MenuCatalog.Get(id).Build(aircraft, Context(), new RecordingMenuHost("")));
    }

    /// <summary>
    /// On final the flyout leads with Pilot's choice, every distance reads from the landing threshold, and the "Exit at" box
    /// comes last, sending nothing while empty.
    /// </summary>
    [AvaloniaFact]
    public void ExitFlyout_OnFinal_PilotsChoiceFirst_DistancesFromThreshold_BoxLast()
    {
        var aircraft = new FakeMenuAircraft
        {
            CurrentPhase = "FinalApproach",
            Situation = AircraftSituation.Final,
            AssignedRunway = "30",
            ExitsAhead = ExitsBothSides,
        };
        MenuItem flyout = Assert.IsType<MenuItem>(MenuCatalog.Get(MenuIds.TowerExitLeft).Build(aircraft, Context(), new RecordingMenuHost("")));

        Assert.Equal(["Pilot's choice — EL", "---", "Taxiway W1 · ~900 ft from thr — EL W1", "---", "Exit at — EL ···"], FlyoutRows(flyout));
    }

    /// <summary>
    /// The flyout is read once, as the menu opens: a row picked after the list changed (the exit passed or no longer
    /// makeable) still sends the command it shows, and the pilot refuses an exit that is no longer makeable; the client
    /// never filters it.
    /// </summary>
    [AvaloniaFact]
    public void ExitFlyout_StalePick_SendsTheCommandAsShown()
    {
        var host = new RecordingMenuHost("");
        var aircraft = new AircraftModel
        {
            Callsign = Callsign,
            IsOnGround = true,
            CurrentPhase = "Landing",
            Situation = AircraftSituation.RolloutExit,
            SituationFlags = SituationFlags.RolloutDecelerating,
            AssignedRunway = "30",
            ExitsAhead = ExitsBothSides,
        };
        MenuItem flyout = Assert.IsType<MenuItem>(MenuCatalog.Get(MenuIds.TowerExitRight).Build(aircraft, Context(), host));

        aircraft.ExitsAhead = [new ExitAheadDto("W4", ExitSide.Right, 11000, true, 128)];
        Click(
            Assert.Single(flyout.Items.OfType<MenuItem>(), item => AutomationProperties.GetName(item) == "Taxiway W2 · planned · ~1,800 ft — ER W2")
        );

        Assert.Equal([(Callsign, "ER W2", Initials)], host.Sent);
    }

    /// <summary>The aircraft selected before the right-click, which the relative ground items send as.</summary>
    private const string Selected = "N436MS";

    /// <summary>
    /// A ground aircraft in <paramref name="phase"/> under <paramref name="flightRules"/>, assigned <paramref name="assignedRunway"/>.
    /// </summary>
    private static AircraftModel OnGround(string phase, string flightRules, string assignedRunway) =>
        new()
        {
            Callsign = Callsign,
            IsOnGround = true,
            CurrentPhase = phase,
            FlightRules = flightRules,
            AssignedRunway = assignedRunway,
        };

    [AvaloniaTheory]
    [InlineData("Holding Short 28R/10L", true, "")]
    [InlineData("Holding In Position", false, "HP")]
    public void GroundResumeTaxi_ShownFromAHoldShortWithARoute_AndFromAHeldStop_SendsRes(string phase, bool hasRoute, string holdKind)
    {
        AircraftModel ac = OnGround(phase, "IFR", "");
        ac.HasActiveTaxiRoute = hasRoute;
        ac.HoldKind = holdKind;
        var host = new RecordingMenuHost("");
        MenuCatalogEntry entry = MenuCatalog.Get(MenuIds.GroundResumeTaxi);

        Assert.True(entry.IsApplicable(ac, Context(VfrCommandsForIfr.None)));
        MenuItem? item = entry.Build(ac, Context(VfrCommandsForIfr.None), host);
        Assert.NotNull(item);
        Assert.Equal("Resume taxi", item.Header as string);
        Click((MenuItem)item);

        Assert.Equal([(Callsign, "RES", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void GroundCrossRunway_NamesTheHeldRunway_AndSendsCrossForIt()
    {
        var host = new RecordingMenuHost("");
        AircraftModel ac = OnGround("Holding Short 28R/10L", "IFR", "30");

        MenuItem? item = MenuCatalog.Get(MenuIds.GroundCrossRunway).Build(ac, Context(VfrCommandsForIfr.None), host);
        Assert.NotNull(item);
        Assert.Equal("Cross 28R", item.Header as string);
        Click((MenuItem)item);

        Assert.Equal([(Callsign, "CROSS 28R", Initials)], host.Sent);
    }

    /// <summary>Off a hold-short, Cross names the runway to cross next and sends CROSS for it, never the assigned runway.</summary>
    [AvaloniaFact]
    public void GroundCrossRunway_OffAHoldShort_NamesTheRunwayToCrossNext()
    {
        var host = new RecordingMenuHost("");
        AircraftModel ac = OnGround("Taxiing", "IFR", "28L");
        ac.NextCrossingRunway = "28R";

        MenuItem? item = MenuCatalog.Get(MenuIds.GroundCrossRunway).Build(ac, Context(VfrCommandsForIfr.None), host);
        Assert.NotNull(item);
        Assert.Equal("Cross 28R", item.Header as string);
        Click((MenuItem)item);

        Assert.Equal([(Callsign, "CROSS 28R", Initials)], host.Sent);
    }

    /// <summary>Off a hold-short with no runway to cross next, Cross builds nothing rather than naming the assigned runway.</summary>
    [AvaloniaFact]
    public void GroundCrossRunway_OffAHoldShortWithNoCrossingAhead_BuildsNothing()
    {
        AircraftModel ac = OnGround("Taxiing", "IFR", "28L");

        Assert.Null(MenuCatalog.Get(MenuIds.GroundCrossRunway).Build(ac, Context(VfrCommandsForIfr.None), new RecordingMenuHost("")));
    }

    // --- Taxi to runway ---

    /// <summary>
    /// Taxi to runway holds the tabbed panel, asking the host for its runway ends only when it first opens: a tab per end
    /// over the selected end's rows, each entry's title then its variants as two-line rows and the end's presets. A row sends
    /// its own command and previews its own route when the pointer enters it; a tab not searched yet is searched when chosen.
    /// </summary>
    [AvaloniaFact]
    public void GroundTaxiToRunway_RowsSendTheirCommands_AndPreviewTheirRoutes()
    {
        TaxiRoute toW4 = new() { Segments = [], HoldShortPoints = [] };
        TaxiRoute toB = new() { Segments = [], HoldShortPoints = [] };
        TaxiRoute preset = new() { Segments = [], HoldShortPoints = [] };
        var runway28R = new RunwayIdentifier("10L", "28R");
        IReadOnlyList<(string Designator, IReadOnlyList<TaxiRouteRow> Rows)> found28R =
        [
            ("28R", [EntryRow("At B", "via B", toB, ("For Departure 28R", "TAXI B 28R"), ("Hold short RWY 28R", "TAXI B HS 28R"))]),
            ("10L", []),
        ];
        TaxiToRunwayEnd[] ends =
        [
            new(
                "30",
                new RunwayIdentifier("12", "30"),
                TaxiToRunwayEndRole.Departure,
                TaxiToRunwayEndState.Ready,
                [
                    EntryRow("At W4", "via S T V W4", toW4, ("For Departure 30", "TAXI S T V W4 30"), ("Hold short RWY 30", "TAXI S T V W4 HS 30")),
                    new TaxiRouteRow(
                        TaxiRouteRow.PresetBadge,
                        "TERMINAL to 30",
                        null,
                        null,
                        false,
                        "via T U W",
                        5800,
                        "TAXI T U W RWY 30",
                        preset,
                        []
                    ),
                ]
            ),
            new("28R", runway28R, TaxiToRunwayEndRole.Other, TaxiToRunwayEndState.NotSearched, []),
        ];
        var host = new RecordingMenuHost("")
        {
            TaxiToRunway = new TaxiToRunwayMenu(
                new TaxiToRunwayEndList(
                    ends,
                    runway =>
                        Task.FromResult<IReadOnlyList<(string Designator, IReadOnlyList<TaxiRouteRow> Rows)>?>(
                            (runway == runway28R) ? found28R : null
                        )
                )
            ),
        };
        AircraftModel ac = OnGround("At Parking", "IFR", "");

        MenuItem? item = MenuCatalog.Get(MenuIds.GroundTaxiToRunway).Build(ac, Context(VfrCommandsForIfr.None), host);

        Assert.NotNull(item);
        Assert.Equal("Taxi to runway", item.Header as string);
        TaxiToRunwayPanel panel = Assert.IsType<TaxiToRunwayPanel>(Assert.Single(item.Items.OfType<MenuItem>()).Header);
        Assert.Equal([TaxiToRunwayFlyout.SearchingText], panel.Lines);
        Assert.Empty(host.TaxiToRunwayRequests);

        item.IsSubMenuOpen = true;
        item.IsSubMenuOpen = false;
        item.IsSubMenuOpen = true;

        Assert.Equal([Callsign], host.TaxiToRunwayRequests);
        Assert.Equal(["30 departure", "28R"], panel.Strip.Tabs.Select(tab => tab.Label));
        Assert.Equal(
            [
                "RW At W4 · nearest · via S T V W4 · ~2,300 ft [bold]",
                "For Departure 30 — TAXI S T V W4 30",
                "---",
                "Hold short RWY 30 — TAXI S T V W4 HS 30",
                "---",
                "TERMINAL to 30 — TAXI T U W RWY 30",
            ],
            panel.Lines
        );

        RaisePointerEntered(panel.Rows[0]);
        Assert.Same(toW4, host.RoutePreviews[^1]);
        RaisePointerEntered(panel.Rows[2]);
        Assert.Same(preset, host.RoutePreviews[^1]);
        foreach (Button row in panel.Rows)
        {
            row.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }

        panel.Strip.Select(1);
        Assert.Equal(
            ["RW At B · nearest · via B · ~2,300 ft [bold]", "For Departure 28R — TAXI B 28R", "---", "Hold short RWY 28R — TAXI B HS 28R"],
            panel.Lines
        );
        panel.Rows[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(
            [
                (Callsign, "TAXI S T V W4 30", Initials),
                (Callsign, "TAXI S T V W4 HS 30", Initials),
                (Callsign, "TAXI T U W RWY 30", Initials),
                (Callsign, "TAXI B 28R", Initials),
            ],
            host.Sent
        );
    }

    [AvaloniaFact]
    public void GroundTaxiToRunway_NoRunwayEnd_SaysNoRunwayEntryFound()
    {
        MenuItem? item = MenuCatalog.Get(MenuIds.GroundTaxiToRunway).Build(OnGround("At Parking", "IFR", ""), Context(), new RecordingMenuHost(""));

        Assert.NotNull(item);
        item.IsSubMenuOpen = true;
        TaxiToRunwayPanel panel = Assert.IsType<TaxiToRunwayPanel>(Assert.Single(item.Items.OfType<MenuItem>()).Header);
        Assert.Empty(panel.Strip.Tabs);
        Assert.Equal([TaxiToRunwayFlyout.NoRunwayEntryText], panel.Lines);
        Assert.Empty(panel.Rows);
    }

    [AvaloniaTheory]
    [InlineData("At Parking", true)]
    [InlineData("Taxiing", true)]
    [InlineData("Holding Short 28R/10L", true)]
    [InlineData("Following SWA1", true)]
    [InlineData("LinedUpAndWaiting", false)]
    [InlineData("Takeoff", false)]
    public void GroundTaxiToRunway_OfferedWhereATaxiRouteCanBeDrawn(string phase, bool offered) =>
        AssertSame(MenuIds.GroundTaxiToRunway, OnGround(phase, "IFR", ""), offered);

    /// <summary>
    /// On the real KOAK layout, every runway a departure runway so each tab comes searched, an entry whose route crosses
    /// another runway offers the crossing variants: For departure holding short of the crossing, and crossing it, which
    /// the row sends as typed.
    /// </summary>
    [AvaloniaFact]
    public void GroundTaxiToRunway_RouteCrossingARunway_OffersTheCrossVariants()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        GroundNodeDto gate = MenuGoldenFixtures.OakLayoutForClient.Nodes.First(n => (n.Type == "Parking") && (n.Name == "25"));
        AircraftModel ac = OnGround("At Parking", "IFR", "");
        ac.AircraftType = "B738";
        ac.Position = new LatLon(gate.Latitude, gate.Longitude);
        var main = new MainViewModel(new FakeFilePickerService());
        main.Aircraft.Clear();
        main.Aircraft.Add(ac);
        main.Ground.SetLayoutForTesting(MenuGoldenFixtures.OakLayoutForClient);
        main.ApplyActiveRunways(new Dictionary<string, List<string>> { ["OAK"] = ["30", "12", "28L", "10R", "28R", "10L", "15", "33"] });
        var host = new SendCapturingHost(new ClientMenuHost(main, ac, new Border()), Initials);

        MenuItem? item = MenuCatalog.Get(MenuIds.GroundTaxiToRunway).Build(ac, Context(VfrCommandsForIfr.None), host);

        Assert.NotNull(item);
        item.IsSubMenuOpen = true;
        TaxiToRunwayPanel panel = Assert.IsType<TaxiToRunwayPanel>(Assert.Single(item.Items.OfType<MenuItem>()).Header);
        Button? crossing = null;
        for (int tab = 0; (tab < panel.Strip.Tabs.Count) && (crossing is null); tab++)
        {
            panel.Strip.Select(tab);
            Assert.NotEqual(FlyoutTabState.Searching, panel.Strip.Tabs[tab].State);
            crossing = panel.Rows.FirstOrDefault(row => MenuCommandText.GetCommand(row)?.Contains(", CROSS ", StringComparison.Ordinal) == true);
        }

        Assert.NotNull(crossing);
        string command = MenuCommandText.GetCommand(crossing)!;
        Assert.StartsWith("TAXI ", command);
        crossing.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal([(Callsign, command, Initials)], host.Sent);
    }

    /// <summary>A runway entry row named <paramref name="name"/> 2,300 ft away, nearest, opening its two variants.</summary>
    private static TaxiRouteRow EntryRow(
        string name,
        string via,
        TaxiRoute preview,
        (string Label, string Command) departure,
        (string Label, string Command) holdShort
    ) =>
        new(
            TaxiRouteRow.RunwayEntryBadge,
            name,
            "nearest",
            null,
            true,
            via,
            2300,
            departure.Command,
            preview,
            [
                new MenuCommandChoice(departure.Label, departure.Command, preview, []),
                MenuCommandChoice.Separator,
                new MenuCommandChoice(holdShort.Label, holdShort.Command, preview, []),
            ]
        );

    /// <summary>
    /// Raises a pointer enter on <paramref name="item"/> with a real <see cref="PointerEventArgs"/>, which the typed handler
    /// requires.
    /// </summary>
    private static void RaisePointerEntered(Control item) =>
        item.RaiseEvent(
            new PointerEventArgs(
                InputElement.PointerEnteredEvent,
                item,
                new Pointer(0, PointerType.Mouse, isPrimary: true),
                rootVisual: null,
                rootVisualPosition: default,
                timestamp: 0,
                properties: default,
                modifiers: KeyModifiers.None
            )
        );

    [AvaloniaFact]
    public void GroundRelative_SendsAsTheSelectedAircraft_NamingTheRightClickedOne()
    {
        var selected = new AircraftModel { Callsign = Selected, IsOnGround = true };
        MenuContext context = TestMenuContext.Create(Callsign, Initials, selected, false, VfrCommandsForIfr.None);
        var host = new RecordingMenuHost("");
        var menu = new ContextMenu();

        SharedMenuGroups.AddForSection(menu.Items, OnGround("Taxiing", "IFR", ""), context, host);

        Assert.Equal(
            [
                $"For {Selected} (selected)",
                $"{Callsign} is taxiing, 0 ft ahead",
                $"Follow {Callsign}",
                $"Give way to {Callsign}",
                "---",
                $"For {Callsign}",
            ],
            menu.Items.Select(Describe)
        );
        foreach (MenuItem item in menu.Items.OfType<MenuItem>().Where(i => i.IsEnabled))
        {
            Click(item);
        }

        Assert.Equal([(Selected, $"FOLLOWG {Callsign}", Initials), (Selected, $"GW {Callsign}", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void GroundRelative_NothingWithoutAnotherOnGroundSelection()
    {
        var airborne = new AircraftModel { Callsign = Selected, IsOnGround = false };
        var same = new AircraftModel { Callsign = Callsign, IsOnGround = true };
        foreach (AircraftModel? selected in (AircraftModel?[])[null, airborne, same])
        {
            MenuContext context = TestMenuContext.Create(Callsign, Initials, selected, false, VfrCommandsForIfr.None);
            var menu = new ContextMenu();

            SharedMenuGroups.AddForSection(menu.Items, OnGround("Taxiing", "IFR", ""), context, new RecordingMenuHost(""));

            Assert.Empty(menu.Items);
        }
    }

    [AvaloniaFact]
    public void AirborneRelative_SendsAsTheSelectedAircraft()
    {
        AircraftModel selected = AirborneSelected();
        selected.LastReportedTrafficCallsign = Callsign;
        MenuContext context = TestMenuContext.Create(Callsign, Initials, selected, false, VfrCommandsForIfr.None);
        var host = new RecordingMenuHost("");
        var menu = new ContextMenu();

        SharedMenuGroups.AddForSection(menu.Items, AirborneClicked(), context, host);

        Assert.Equal(
            [
                $"For {Selected} (selected)",
                $"{Callsign} is at its 2 o'clock, 4 nm, 1,500 ft below",
                "Report in sight",
                "Follow",
                "---",
                $"For {Callsign}",
            ],
            menu.Items.Select(Describe)
        );
        foreach (MenuItem item in menu.Items.OfType<MenuItem>().Where(i => i.IsEnabled))
        {
            Click(item);
        }

        Assert.Equal([(Selected, $"RTIS {Callsign}", Initials), (Selected, $"FOLLOW {Callsign}", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void AirborneRelative_FollowOnlyAfterTrafficReportedInSight()
    {
        MenuContext context = TestMenuContext.Create(Callsign, Initials, AirborneSelected(), false, VfrCommandsForIfr.None);
        var menu = new ContextMenu();

        SharedMenuGroups.AddForSection(menu.Items, AirborneClicked(), context, new RecordingMenuHost(""));

        Assert.Equal(
            [$"For {Selected} (selected)", $"{Callsign} is at its 2 o'clock, 4 nm, 1,500 ft below", "Report in sight", "---", $"For {Callsign}"],
            menu.Items.Select(Describe)
        );
    }

    /// <summary>The selected aircraft of the airborne For-section tests: at 3,000 ft, heading 030 true.</summary>
    private static AircraftModel AirborneSelected() =>
        new()
        {
            Callsign = Selected,
            IsOnGround = false,
            Position = new LatLon(37.0, -122.0),
            Heading = new TrueHeading(30),
            Altitude = 3000,
        };

    /// <summary>
    /// The right-clicked aircraft of the airborne For-section tests: 4 nm due east of the selected one (its 2 o'clock),
    /// at 1,500 ft.
    /// </summary>
    private static AircraftModel AirborneClicked() =>
        new()
        {
            Callsign = Callsign,
            IsOnGround = false,
            Position = new LatLon(37.0, -122.0 + (4.0 / (60 * Math.Cos(37.0 * Math.PI / 180)))),
            Altitude = 1500,
        };

    [AvaloniaFact]
    public void Relative_MixedPair_HasNoForSection()
    {
        (AircraftModel Selected, AircraftModel Clicked)[] pairs =
        [
            (new AircraftModel { Callsign = Selected, IsOnGround = false }, new AircraftModel { Callsign = Callsign, IsOnGround = true }),
            (new AircraftModel { Callsign = Selected, IsOnGround = true }, new AircraftModel { Callsign = Callsign, IsOnGround = false }),
        ];
        foreach ((AircraftModel selected, AircraftModel clicked) in pairs)
        {
            MenuContext context = TestMenuContext.Create(Callsign, Initials, selected, false, VfrCommandsForIfr.None);
            var menu = new ContextMenu();

            SharedMenuGroups.AddForSection(menu.Items, clicked, context, new RecordingMenuHost(""));

            Assert.Empty(menu.Items);
        }
    }

    /// <summary>
    /// Each header row: the phase, the assigned runway and the header the submenu carries. The held-runway rows name
    /// the held runway, the empty one names none.
    /// </summary>
    public static TheoryData<string, string, string> CtoHeaderRows()
    {
        TheoryData<string, string, string> data = [];
        (string Phase, string AssignedRunway, string Header)[] rows =
        [
            ("Holding Short 15/33", "28R", "Cleared for takeoff 15"),
            ("Taxiing", "28R", "Cleared for takeoff 28R"),
            ("LinedUpAndWaiting", "30", "Cleared for takeoff 30"),
            ("LinedUpAndWaiting", "", "Cleared for takeoff"),
        ];
        foreach ((string phase, string assignedRunway, string header) in rows)
        {
            data.Add(phase, assignedRunway, header);
        }

        return data;
    }

    [AvaloniaTheory]
    [MemberData(nameof(CtoHeaderRows))]
    public void Cto_HeaderNamesTheHeldRunway_ElseTheAssignedOne(string phase, string assignedRunway, string header)
    {
        AircraftModel ac = OnGround(phase, "IFR", assignedRunway);
        MenuCatalogEntry entry = MenuCatalog.Get(MenuIds.TowerClearedForTakeoff);
        MenuContext context = Context(VfrCommandsForIfr.None);

        Assert.True(entry.IsApplicable(ac, context));
        Assert.Equal(header, CtoSubmenu(ac, new RecordingMenuHost("")).Header as string);
    }

    // Holding short of crossing runway 15 while assigned 28R: line up and wait names the held 15.
    [AvaloniaFact]
    public void LuawLabel_NamesHeldRunway()
    {
        AircraftModel ac = OnGround("Holding Short 15/33", "IFR", "28R");
        MenuContext context = Context(VfrCommandsForIfr.None);
        MenuCatalogEntry entry = MenuCatalog.Get(MenuIds.TowerLineUpAndWait);

        Assert.True(entry.IsApplicable(ac, context));
        Assert.Equal("Line up and wait 15", entry.Build(ac, context, new RecordingMenuHost(""))?.Header as string);
    }

    // A VFR C172 on the downwind to 28R: the Tower submenu offers the landing items in this order.
    [AvaloniaFact]
    public void LandingBlockOrder_FollowsTheSharedOrder()
    {
        var ac = new AircraftModel
        {
            Callsign = Callsign,
            AircraftType = "C172",
            FlightRules = "VFR",
            CurrentPhase = "Downwind",
            IsOnGround = false,
            Departure = "KOAK",
            Destination = "KOAK",
            AssignedRunway = "28R",
        };
        string[] expected =
        [
            "Cleared to land 28R",
            "Force landing 28R",
            "Cleared for the option 28R",
            "Touch and go 28R",
            "Stop and go 28R",
            "Low approach 28R",
            "Go around 28R",
        ];
        var host = new RecordingMenuHost("");

        MenuItem? tower = SharedMenuGroups.Tower(ac, Context(VfrCommandsForIfr.None), host);

        Assert.NotNull(tower);
        Assert.Equal(expected, tower.Items.Select(Describe));
    }

    /// <summary>
    /// The Cleared for takeoff submenu built for <paramref name="aircraft"/> over <paramref name="host"/>.
    /// </summary>
    private static MenuItem CtoSubmenu(AircraftModel aircraft, RecordingMenuHost host)
    {
        MenuItem? cto = MenuCatalog.Get(MenuIds.TowerClearedForTakeoff).Build(aircraft, Context(VfrCommandsForIfr.None), host);
        Assert.NotNull(cto);
        return cto;
    }

    // The takeoff submenu: one runway-bearing header, one child list, and one command per child.
    [AvaloniaFact]
    public void Cto_VfrDeparture_OffersTheSameChildren()
    {
        AircraftModel ac = OnGround("LinedUpAndWaiting", "VFR", "30");
        var host = new RecordingMenuHost("");

        MenuItem cto = CtoSubmenu(ac, host);

        Assert.Equal("Cleared for takeoff 30", cto.Header as string);
        // No destination, so On course is disabled; the heading and direct rows send nothing until their boxes are filled.
        Assert.Equal(
            [
                "Initial altitude · default climb",
                "Cleared for takeoff — CTO",
                "Make left closed traffic — CTO MLT",
                "Make right closed traffic — CTO MRT",
                "Straight out — CTO MSO",
                "Left crosswind departure — CTO MLC",
                "Right crosswind departure — CTO MRC",
                "Left downwind departure — CTO MLD",
                "Right downwind departure — CTO MRD",
                "Left turnout (45°) — CTO ML45",
                "Right turnout (45°) — CTO MR45",
                "Fly heading — CTO ···",
                "On course — CTO OC [disabled]",
                "Turn left direct — CTO TLDCT ···",
                "Turn right direct — CTO TRDCT ···",
                "---",
                "Custom — CTO",
            ],
            cto.Items.Select(DescribeRow)
        );

        foreach (MenuItem child in cto.Items.OfType<MenuItem>().Where(i => (i.Header as TakeoffFlyoutRow)?.Label != TakeoffFlyout.CustomLabel))
        {
            Click(child);
        }

        Assert.Equal(
            ["CTO", "CTO MLT", "CTO MRT", "CTO MSO", "CTO MLC", "CTO MRC", "CTO MLD", "CTO MRD", "CTO ML45", "CTO MR45"],
            host.Sent.Select(s => s.Command)
        );
    }

    // IFR without the VFR set: the plain clearance, a heading and runway heading, then the separator and Custom.
    [AvaloniaFact]
    public void Cto_IfrDeparture_OffersTakeoffHeadingAndRunwayHeading()
    {
        AircraftModel ac = OnGround("LinedUpAndWaiting", "IFR", "30");

        MenuItem cto = CtoSubmenu(ac, new RecordingMenuHost(""));
        Assert.Equal("Cleared for takeoff 30", cto.Header as string);
        Assert.Equal(
            ["Cleared for takeoff — CTO", "Fly heading — CTO ···", "Fly runway heading — CTO RH", "---", "Custom — CTO"],
            cto.Items.Select(DescribeRow)
        );
    }

    // The takeoff submenu ends with the separator and Custom, on the recording host and on the client host every view builds.
    [AvaloniaFact]
    public void Cto_EndsWithSeparatorAndCustom()
    {
        MenuItem cto = CtoSubmenu(OnGround("LinedUpAndWaiting", "IFR", "30"), new RecordingMenuHost(""));

        Assert.Equal(TakeoffFlyout.CustomLabel, Assert.IsType<TakeoffFlyoutRow>(Assert.IsType<MenuItem>(cto.Items[^1]).Header).Label);
        Assert.IsType<Separator>(cto.Items[^2]);

        AircraftModel ac = OnGround("Taxiing", "IFR", "30");
        IMenuHost host = new ClientMenuHost(new MainViewModel(new FakeFilePickerService()), ac, new Border());

        MenuItem? clientCto = MenuCatalog.Get(MenuIds.TowerClearedForTakeoff).Build(ac, Context(VfrCommandsForIfr.None), host);

        Assert.NotNull(clientCto);
        Assert.IsType<Separator>(clientCto.Items[^2]);
        Assert.Equal(TakeoffFlyout.CustomLabel, Assert.IsType<TakeoffFlyoutRow>(Assert.IsType<MenuItem>(clientCto.Items[^1]).Header).Label);
    }

    // CTO where the sim takes it and never once rolling: a hold-short naming a runway (held, else assigned), taxiing
    // with a runway (a deferred clearance), lined up, and lining up (a mid-line-up upgrade).
    [AvaloniaTheory]
    [InlineData("Holding Short 28R/10L", "", true)]
    [InlineData("Holding Short", "28R", true)]
    [InlineData("Holding Short", "", false)]
    [InlineData("Taxiing", "28R", true)]
    [InlineData("Taxiing", "", false)]
    [InlineData("LinedUpAndWaiting", "", true)]
    [InlineData("LiningUp", "28R", true)]
    [InlineData("Takeoff", "28R", false)]
    [InlineData("At Parking", "28R", false)]
    public void Cto_OfferedInLiningUp_NeverInTakeoff(string phase, string assignedRunway, bool offered) =>
        AssertSame(MenuIds.TowerClearedForTakeoff, OnGround(phase, "IFR", assignedRunway), offered);

    // LUAW at a hold-short naming a runway or taxiing with a runway.
    [AvaloniaTheory]
    [InlineData("Holding Short 28R/10L", "", true)]
    [InlineData("Holding Short", "28R", true)]
    [InlineData("Holding Short", "", false)]
    [InlineData("Taxiing", "28R", true)]
    [InlineData("Taxiing", "", false)]
    [InlineData("LinedUpAndWaiting", "28R", false)]
    [InlineData("LiningUp", "28R", false)]
    public void Luaw_AtAHoldShortOrTaxiingWithARunway(string phase, string assignedRunway, bool offered) =>
        AssertSame(MenuIds.TowerLineUpAndWait, OnGround(phase, "IFR", assignedRunway), offered);

    // Cancel takeoff clearance while lining up, lined up or rolling.
    [AvaloniaTheory]
    [InlineData("LiningUp", true)]
    [InlineData("LinedUpAndWaiting", true)]
    [InlineData("Takeoff", true)]
    [InlineData("Holding Short 28R/10L", false)]
    [InlineData("Taxiing", false)]
    public void CancelTakeoff_LiningUpLinedUpOrRolling(string phase, bool offered) =>
        AssertSame(MenuIds.TowerCancelTakeoff, OnGround(phase, "IFR", "28R"), offered);

    // RES from a hold-short only where HoldingShortPhase takes it: a mid-route bar (a crossing or an explicit hold-short)
    // with route left to resume onto. The bars where the sim refuses RES — the departure-runway bar and the end of an
    // incomplete route — both end the route, so "route left" is the whole gate. The departure-runway situation flag is
    // no substitute: it is also set at an intersection-departure explicit bar, where the sim accepts RES. The held
    // stationary holds resume as well, and so does a rolling aircraft with a taxiway hold short ahead, which RES pre-clears;
    // a rolling aircraft with none ahead is not offered it, and a held rolling aircraft is (RES clears the hold directive).
    [AvaloniaTheory]
    [InlineData("Holding Short 28R/10L", true, "", false, true)]
    [InlineData("Holding Short 28R/10L", false, "", false, false)]
    [InlineData("Holding In Position", false, "HP", false, true)]
    [InlineData("Holding In Position", false, "", false, false)]
    [InlineData("Holding In Position", true, "", true, false)]
    [InlineData("Taxiing", true, "", true, true)]
    [InlineData("Taxiing", true, "", false, false)]
    [InlineData("Taxiing", true, "HP", true, true)]
    public void ResumeTaxi_OnlyWhereTheSimAcceptsRes(string phase, bool hasActiveTaxiRoute, string holdKind, bool taxiwayHoldShortAhead, bool offered)
    {
        AircraftModel ac = OnGround(phase, "IFR", "28R");
        ac.HasActiveTaxiRoute = hasActiveTaxiRoute;
        ac.HasTaxiwayHoldShortAhead = taxiwayHoldShortAhead;
        ac.HoldKind = holdKind;

        AssertSame(MenuIds.GroundResumeTaxi, ac, offered);
    }

    // A taxiway or spot bar names no runway: line up and wait and Cleared for takeoff name the assigned departure
    // runway instead.
    [AvaloniaTheory]
    [InlineData("Holding Short B", MenuIds.TowerLineUpAndWait, "Line up and wait 28R")]
    [InlineData("Holding Short spot 17", MenuIds.TowerLineUpAndWait, "Line up and wait 28R")]
    [InlineData("Holding Short B", MenuIds.TowerClearedForTakeoff, "Cleared for takeoff 28R")]
    [InlineData("Holding Short spot 17", MenuIds.TowerClearedForTakeoff, "Cleared for takeoff 28R")]
    public void TaxiwayBar_WithAnAssignedRunway_LuawAndCtoNameTheAssignedRunway(string phase, string id, string header)
    {
        AircraftModel ac = OnGround(phase, "IFR", "28R");
        AssertSame(id, ac, true);

        MenuItem? item = MenuCatalog.Get(id).Build(ac, Context(VfrCommandsForIfr.None), new RecordingMenuHost(""));
        Assert.NotNull(item);
        Assert.Equal(header, item.Header as string);
    }

    // A taxiway or spot bar with no assigned runway has no runway to name: no line up and wait or Cleared for takeoff.
    [AvaloniaTheory]
    [InlineData("Holding Short B", MenuIds.TowerLineUpAndWait)]
    [InlineData("Holding Short spot 17", MenuIds.TowerLineUpAndWait)]
    [InlineData("Holding Short B", MenuIds.TowerClearedForTakeoff)]
    [InlineData("Holding Short spot 17", MenuIds.TowerClearedForTakeoff)]
    public void TaxiwayBar_WithNoAssignedRunway_NoLuawOrCto(string phase, string id) => AssertSame(id, OnGround(phase, "IFR", ""), false);

    // A taxiway or spot bar protects no runway, so Cross is never offered there, even with a runway assigned.
    [AvaloniaTheory]
    [InlineData("Holding Short B", "28R")]
    [InlineData("Holding Short C", "28R")]
    [InlineData("Holding Short F1", "")]
    [InlineData("Holding Short spot 17", "28R")]
    public void TaxiwayBar_NeverOffersCross(string phase, string assignedRunway) =>
        AssertSame(MenuIds.GroundCrossRunway, OnGround(phase, "IFR", assignedRunway), false);

    [AvaloniaFact]
    public void ResumeTaxi_NeverForASurfaceShadow()
    {
        AircraftModel shadow = OnGround("Holding Short 28R/10L", "IFR", "28R");
        shadow.IsLiveTraffic = true;
        shadow.HasActiveTaxiRoute = true;

        AssertSame(MenuIds.GroundResumeTaxi, shadow, false);
    }

    // Cross the runway held short of, named by the phase or the assignment, and never for an uncontrollable shadow.
    [AvaloniaTheory]
    [InlineData("Holding Short 28R/10L", "", false, true)]
    [InlineData("Holding Short", "28R", false, true)]
    [InlineData("Holding Short", "", false, false)]
    [InlineData("Taxiing", "28R", false, false)]
    [InlineData("Holding Short 28R/10L", "28R", true, false)]
    public void CrossRunway_HoldingShortOfANamedRunway_NeverForAShadow(string phase, string assignedRunway, bool surfaceShadow, bool offered)
    {
        AircraftModel ac = OnGround(phase, "IFR", assignedRunway);
        ac.IsLiveTraffic = surfaceShadow;

        AssertSame(MenuIds.GroundCrossRunway, ac, offered);
    }

    // The release-window check for an aircraft on the ground with a window, and never for an uncontrollable shadow.
    [AvaloniaTheory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    public void CheckReleaseWindow_OnTheGroundWithAWindow_NeverForAShadow(bool hasWindow, bool surfaceShadow, bool offered)
    {
        AircraftModel ac = OnGround("Taxiing", "IFR", "28R");
        ac.CfrWindowStartUtc = hasWindow ? new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc) : null;
        ac.IsLiveTraffic = surfaceShadow;

        AssertSame(MenuIds.CoordinationCheckReleaseWindow, ac, offered);
    }

    // Release (HFR) for a held departure, and never for an uncontrollable surface shadow.
    [AvaloniaTheory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    public void ReleaseHeld_OfferedOnlyWhenHeldAndControllable(bool held, bool surfaceShadow, bool offered)
    {
        AircraftModel ac = OnGround("Taxiing", "IFR", "28R");
        ac.IsHeldForRelease = held;
        ac.IsLiveTraffic = surfaceShadow;

        AssertSame(MenuIds.CoordinationReleaseHeld, ac, offered);
    }

    private static void AssertSame(string id, AircraftModel ac, bool offered)
    {
        MenuCatalogEntry entry = MenuCatalog.Get(id);

        Assert.Equal(offered, entry.IsApplicable(ac, Context(VfrCommandsForIfr.None)));
    }

    // --- The context helpers the command tests share ---

    /// <summary>A context whose selected rows are airborne live-traffic shadows named <paramref name="callsigns"/>.</summary>
    private static MenuContext ShadowSelectionContext(params string[] callsigns)
    {
        MenuContext context = Context(VfrCommandsForIfr.EnterFinalOnly);
        List<IMenuAircraft> shadows =
        [
            .. callsigns.Select(callsign => new AircraftModel
            {
                Callsign = callsign,
                IsLiveTraffic = true,
                IsOnGround = false,
            }),
        ];
        return context with { Click = context.Click with { Selection = shadows } };
    }

    // --- The delayed-spawn block every view's menu places ---

    /// <summary>The delayed-spawn items for a delayed aircraft, the block every view's menu places.</summary>
    private static ContextMenu DelayedSpawnMenu(RecordingMenuHost host)
    {
        var menu = new ContextMenu();
        SharedMenuGroups.AddDelayedSpawn(menu, new FakeMenuAircraft(), Context(VfrCommandsForIfr.None), host);
        return menu;
    }

    /// <summary>The Change spawn delay submenu of <paramref name="menu"/>.</summary>
    private static MenuItem DelaySubmenu(ContextMenu menu) =>
        Assert.Single(menu.Items.OfType<MenuItem>(), i => (i.Header as string) == "Change spawn delay");

    /// <summary>The delay submenu's free-text box, the item after its separator.</summary>
    private static TextBox CustomDelayBox(ContextMenu menu) => Assert.IsType<TextBox>(DelaySubmenu(menu).Items[^1]);

    private static void RaiseEnter(TextBox textBox) =>
        textBox.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });

    /// <summary>
    /// Opens <paramref name="menu"/> on an anchor in a shown window, so its open state can be observed, and asserts it
    /// opened. The caller closes the window when it is done.
    /// </summary>
    private static Window OpenMenuOnAnchor(ContextMenu menu)
    {
        var anchor = new Border();
        var window = new Window
        {
            Width = 400,
            Height = 200,
            Content = anchor,
        };
        window.ShowAndRunLayout();
        menu.Open(anchor);
        HeadlessWindowExtensions.PumpDispatcher();
        Assert.True(menu.IsOpen, "the menu opens before the custom box is used");
        return window;
    }

    // The delayed-spawn menu replaces the phase-aware block: Spawn now, the preset submenu (whose custom box sits
    // after a separator, placeholder and size as the list always had them) and Delete.
    [AvaloniaFact]
    public void DelayedSpawn_OffersSpawnNowTheDelaySubmenuAndDelete()
    {
        ContextMenu menu = DelayedSpawnMenu(new RecordingMenuHost(""));

        Assert.Equal(["Spawn now", "Change spawn delay", "Delete"], menu.Items.OfType<MenuItem>().Select(i => i.Header as string));
        Assert.Equal(
            ["15 seconds", "30 seconds", "1 minute", "2 minutes", "5 minutes", "10 minutes", "---", "TextBox"],
            DelaySubmenu(menu).Items.Select(Describe)
        );

        TextBox box = CustomDelayBox(menu);
        Assert.Equal("Custom (e.g. 90, 2m15s, 1h)", box.PlaceholderText);
        Assert.Equal(12, box.FontSize);
        Assert.Equal(180, box.MinWidth);
    }

    [AvaloniaFact]
    public void DelayedSpawn_SpawnNow_SendsSpawn()
    {
        var host = new RecordingMenuHost("");
        ContextMenu menu = DelayedSpawnMenu(host);

        Click(Assert.Single(menu.Items.OfType<MenuItem>(), i => (i.Header as string) == "Spawn now"));

        Assert.Equal([(Callsign, "SPAWN", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void DelayedSpawn_Delete_SendsDel()
    {
        var host = new RecordingMenuHost("");
        ContextMenu menu = DelayedSpawnMenu(host);

        Click(Assert.Single(menu.Items.OfType<MenuItem>(), i => (i.Header as string) == "Delete"));

        Assert.Equal([(Callsign, "DEL", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void DelayedSpawn_DelayPreset_SendsSpawndelayWithItsSeconds()
    {
        var host = new RecordingMenuHost("");
        ContextMenu menu = DelayedSpawnMenu(host);

        Click(Assert.Single(DelaySubmenu(menu).Items.OfType<MenuItem>(), i => (i.Header as string) == "1 minute"));

        Assert.Equal([(Callsign, "SPAWNDELAY 60", Initials)], host.Sent);
    }

    // The custom box closes the menu it sits in, so the test opens the menu on a real anchor and asserts it closes.
    [AvaloniaFact]
    public void DelayedSpawn_CustomDelayBox_ParsedText_SendsItsSecondsAndClosesTheMenu()
    {
        var host = new RecordingMenuHost("");
        ContextMenu menu = DelayedSpawnMenu(host);
        TextBox box = CustomDelayBox(menu);
        Window window = OpenMenuOnAnchor(menu);
        try
        {
            box.Text = "2m15s";
            RaiseEnter(box);
            HeadlessWindowExtensions.PumpDispatcher();

            Assert.Equal([(Callsign, "SPAWNDELAY 135", Initials)], host.Sent);
            Assert.False(menu.IsOpen, "a parsed delay closes the menu");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void DelayedSpawn_CustomDelayBox_UnparsableText_SendsNothingAndLeavesTheMenuOpen()
    {
        var host = new RecordingMenuHost("");
        ContextMenu menu = DelayedSpawnMenu(host);
        TextBox box = CustomDelayBox(menu);
        Window window = OpenMenuOnAnchor(menu);
        try
        {
            box.Text = "abc";
            RaiseEnter(box);
            HeadlessWindowExtensions.PumpDispatcher();

            Assert.Empty(host.Sent);
            Assert.True(menu.IsOpen, "text that names no delay leaves the menu open");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Every leg of the circuit (<see cref="AircraftCommandApplicability.IsPatternPhase"/>), comma-separated.</summary>
    private const string AllPatternLegs = "Pattern Entry,Upwind,Crosswind,Downwind,Base,MidfieldCrossing";

    [AvaloniaTheory]
    [InlineData("Turn crosswind", "Upwind", "Crosswind", "TC")]
    [InlineData("Turn downwind", "Crosswind", "Upwind", "TD")]
    [InlineData("Turn base", "Downwind", "Base", "TB")]
    [InlineData("Extend pattern leg", "Upwind,Crosswind,Downwind", "Base", "EXT")]
    [InlineData("Make short approach", "Downwind,Base", "Crosswind", "MSA")]
    [InlineData("Make normal approach", "Downwind,Base", "Upwind", "MNA")]
    [InlineData("Make left 360", AllPatternLegs, "FinalApproach", "L360")]
    [InlineData("Make right 360", AllPatternLegs, "FinalApproach", "R360")]
    [InlineData("Make left 270", AllPatternLegs, "FinalApproach", "L270")]
    [InlineData("Make right 270", AllPatternLegs, "FinalApproach", "R270")]
    [InlineData("Plan 270 at next turn", "Upwind,Crosswind,Downwind,Base", "Pattern Entry", "P270")]
    [InlineData("Cancel 270", "Upwind,Crosswind,Downwind,Base", "MidfieldCrossing", "NO270")]
    [InlineData("Circle airport", AllPatternLegs, "FinalApproach", "CA")]
    public void Pattern_Maneuver_ShownOnEveryLegItFits_HiddenElsewhere_AndSendsItsCommand(
        string label,
        string shownLegs,
        string hiddenPhase,
        string command
    )
    {
        foreach (string leg in shownLegs.Split(','))
        {
            var host = new RecordingMenuHost("");
            MenuItem? shown = SharedMenuGroups.Pattern(VfrInPattern(leg), Context(), host);

            Assert.True(shown is not null, $"the Pattern submenu is offered on {leg}");
            List<MenuItem> matches = [.. shown.Items.OfType<MenuItem>().Where(m => m.Header as string == label)];
            Assert.True(matches.Count == 1, $"'{label}' is offered once on {leg}, found {matches.Count}");
            Click(matches[0]);
            Assert.Equal([(Callsign, command, Initials)], host.Sent);
        }

        MenuItem? hidden = SharedMenuGroups.Pattern(VfrInPattern(hiddenPhase), Context(), new RecordingMenuHost(""));
        Assert.DoesNotContain(hidden?.Items.OfType<MenuItem>() ?? [], m => m.Header as string == label);
    }

    [AvaloniaFact]
    public void Pattern_IfrOnDownwind_ManeuversFollowTheVfrForIfrMode()
    {
        var aircraft = new AircraftModel
        {
            Callsign = Callsign,
            IsOnGround = false,
            CurrentPhase = "Downwind",
            FlightRules = "IFR",
        };
        var host = new RecordingMenuHost("");

        MenuItem? all = SharedMenuGroups.Pattern(aircraft, Context(VfrCommandsForIfr.All), host);
        MenuItem? enterFinalOnly = SharedMenuGroups.Pattern(aircraft, Context(VfrCommandsForIfr.EnterFinalOnly), host);
        MenuItem? none = SharedMenuGroups.Pattern(aircraft, Context(VfrCommandsForIfr.None), host);

        Assert.NotNull(all);
        Click(Assert.Single(all.Items.OfType<MenuItem>(), m => m.Header as string == "Turn base"));
        Assert.Equal([(Callsign, "TB", Initials)], host.Sent);
        // Under the default setting an IFR aircraft keeps only the straight-in final entry: no circuit legs, no maneuvers.
        Assert.NotNull(enterFinalOnly);
        Assert.Equal(["Make straight-in · no airport"], enterFinalOnly.Items.Select(Describe));
        Assert.Null(none);
    }

    [AvaloniaFact]
    public void PatternEntry_RunwaysComeFromTheDestinationWheneverOneIsFiled()
    {
        TestVnasData.EnsureInitialized();
        Assert.Empty(RunwayDesignators.ForAirport(UnknownAirport));
        var host = new RecordingMenuHost("28R");

        MenuItem? flyout = MenuCatalog
            .Get(MenuIds.PatternEnterLeftDownwind)
            .Build(new FakeMenuAircraft { Destination = ApproachAirport, Departure = UnknownAirport }, Context(), host);
        Click(PatternRunwayRow(flyout, "Enter left downwind", [.. RunwayDesignators.ForAirport(ApproachAirport)], "28R"));
        MenuItem? none = MenuCatalog
            .Get(MenuIds.PatternEnterLeftDownwind)
            .Build(new FakeMenuAircraft { Destination = UnknownAirport, Departure = ApproachAirport }, Context(), host);
        AssertUnavailable(none, "Enter left downwind", "no runway data at ZZZZ");

        Assert.Equal([(Callsign, "ELD 28R", Initials)], host.Sent);
    }

    /// <summary>
    /// Asserts a pattern entry's runway flyout: its header, no picker, and one row per runway in
    /// <paramref name="runways"/> order (a type with no landing distance finds every runway landable, and the recording
    /// host's room has no active runways, so the order is the runway order); returns the row for <paramref name="runway"/>.
    /// </summary>
    private static MenuItem PatternRunwayRow(MenuItem? flyout, string header, string[] runways, string runway)
    {
        Assert.NotNull(flyout);
        Assert.Equal(header, flyout.Header as string);
        Assert.Null(flyout.Tag);
        List<MenuItem> rows = [.. flyout.Items.Cast<MenuItem>()];
        Assert.Equal(runways.Select(r => $"Runway {r}"), rows.Select(row => Assert.IsType<MenuGlyphRow>(row.Header).Label));
        return rows.Single(row => ((MenuGlyphRow)row.Header!).Label == $"Runway {runway}");
    }

    /// <summary>An airborne VFR aircraft in <paramref name="phase"/>, with no runway assigned.</summary>
    private static AircraftModel VfrInPattern(string phase) =>
        new()
        {
            Callsign = Callsign,
            IsOnGround = false,
            CurrentPhase = phase,
            FlightRules = "VFR",
        };

    /// <summary>
    /// A pattern entry with nothing to pick (ruling 3): with no airport at all, disabled with "no airport"; at an airport without
    /// runway data, disabled naming it; either way its strip button inert and no box.
    /// </summary>
    [AvaloniaFact]
    public void PatternEntry_NoAirportOrNoRunwayData_IsDisabledWithTheReason()
    {
        TestVnasData.EnsureInitialized();
        Assert.Empty(RunwayDesignators.ForAirport(UnknownAirport));
        var host = new RecordingMenuHost("");

        MenuCatalogEntry entry = MenuCatalog.Get(MenuIds.PatternEnterLeftDownwind);
        AssertUnavailable(entry.Build(new FakeMenuAircraft(), Context(), host), "Enter left downwind", "no airport");
        AssertUnavailable(
            entry.Build(new FakeMenuAircraft { Destination = UnknownAirport }, Context(), host),
            "Enter left downwind",
            "no runway data at ZZZZ"
        );
        Assert.Empty(host.InputPlaceholders);
    }

    [AvaloniaFact]
    public void PatternEntry_DefaultRunwayWithoutRunwayList_IsALeafOnly_SendingTheRawRunway()
    {
        TestVnasData.EnsureInitialized();
        Assert.Empty(RunwayDesignators.ForAirport(UnknownAirport));
        var host = new RecordingMenuHost("");
        var aircraft = new FakeMenuAircraft { Destination = UnknownAirport, AssignedRunway = "09L" };

        MenuItem? item = MenuCatalog.Get(MenuIds.PatternEnterFinal).Build(aircraft, Context(), host);
        Assert.NotNull(item);
        Assert.Equal("Make straight-in 9L", item.Header as string);
        Assert.Null(item.Tag);
        Click((MenuItem)item);

        Assert.Equal([(Callsign, "EF 09L", Initials)], host.Sent);
    }

    /// <summary>
    /// Without a destination the entry lists the departure runways; with a runway assigned it is the same flyout, not a leaf
    /// beside an "(other)" flyout (ruling 2: one entry per verb, its flyout holding every choice).
    /// </summary>
    [AvaloniaFact]
    public void PatternEntry_NoDestination_ListsTheDepartureRunways()
    {
        TestVnasData.EnsureInitialized();
        string[] runways = [.. RunwayDesignators.ForAirport(ApproachAirport)];
        Assert.Contains("28R", runways);
        var host = new RecordingMenuHost("28R");
        var noDefault = new FakeMenuAircraft { Departure = ApproachAirport };
        var withDefault = new FakeMenuAircraft { Departure = ApproachAirport, AssignedRunway = "30" };

        MenuItem? list = MenuCatalog.Get(MenuIds.PatternEnterRightBase).Build(noDefault, Context(), host);
        Click(PatternRunwayRow(list, "Enter right base", runways, "28R"));
        MenuItem? assigned = MenuCatalog.Get(MenuIds.PatternEnterRightBase).Build(withDefault, Context(), host);
        Click(PatternRunwayRow(assigned, "Enter right base", runways, "28R"));

        Assert.Equal([(Callsign, "ERB 28R", Initials), (Callsign, "ERB 28R", Initials)], host.Sent);
    }

    /// <summary>Asserts a picker item's header and the descriptor the menu walker prints for it, and returns the item.</summary>
    private static MenuItem AssertPicker(MenuItem? item, string header, string kind, string[] texts)
    {
        Assert.NotNull(item);
        Assert.Equal(header, item.Header as string);
        MenuPickerDescriptor descriptor = Assert.IsType<MenuPickerDescriptor>(item.Tag);
        Assert.Equal(kind, descriptor.Kind);
        Assert.Equal(texts, descriptor.Items);
        return item;
    }

    /// <summary>An item's header text, or "---" for a separator, so a menu's whole item sequence can be asserted.</summary>
    private static string Describe(object? item) =>
        item switch
        {
            Separator => "---",
            MenuItem { Header: MenuDisabledReasonRow reason } => reason.ToString(),
            MenuItem menuItem => menuItem.Header as string ?? "",
            _ => item?.GetType().Name ?? "null",
        };

    /// <summary>
    /// An item as the menu golden prints it: its header text (a picker row's header names its approach and command),
    /// followed by <c>[disabled]</c> on a section or kind header; "---" for a separator.
    /// </summary>
    private static string DescribeRow(object? item) =>
        item switch
        {
            Separator => "---",
            MenuItem { IsEnabled: false } header => $"{header.Header} [disabled]",
            MenuItem menuItem => menuItem.Header?.ToString() ?? "",
            _ => item?.GetType().Name ?? "null",
        };
}
