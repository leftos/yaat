using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
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
    private const string PositionOrCode = "1234";
    private const string SayText = "say again";
    private const string BlockText = "gate 25";
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
        (MenuIds.TrackInitiateHandoff, PositionOrCode, "HO 1234"),
        (MenuIds.TrackCancelHandoff, "", "CANCEL"),
        (MenuIds.TrackPointOut, PositionOrCode, "PO 1234"),
        (MenuIds.TrackAcknowledgePointout, "", "OK"),
        (MenuIds.SquawkCode, PositionOrCode, "SQ 1234"),
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
        (MenuIds.AskPilotCustom, SayText, "SAY say again"),
        (MenuIds.CoordinationRelease, "", "RD"),
        (MenuIds.CoordinationHold, "", "RDH"),
        (MenuIds.CoordinationRecall, "", "RDR"),
        (MenuIds.CoordinationAcknowledge, "", "RDACK"),
        (MenuIds.CoordinationReleaseHeld, "", $"REL {Callsign}"),
        (MenuIds.CoordinationCheckReleaseWindow, "", "CFR CHECK"),
        (MenuIds.DataBlockScratchpad, BlockText, $"SP {BlockText}"),
        (MenuIds.DataBlockAnnotate, "", "ANNOTATE"),
        (MenuIds.SimControlDelete, "", "DEL"),
        (MenuIds.HeadingPresent, "", "FPH"),
        (MenuIds.SpeedNormal, "", "RNS"),
        (MenuIds.SpeedFinalApproach, "", "RFAS"),
        (MenuIds.HoldPresentLeft, "", "HPPL"),
        (MenuIds.HoldPresentRight, "", "HPPR"),
        (MenuIds.ApproachClearedVisual, "28R", "CVA 28R"),
        (MenuIds.ApproachReportFieldInSight, "", "RFIS"),
        (MenuIds.ApproachReportTrafficInSight, "AAL12", "RTIS AAL12"),
        (MenuIds.ApproachReportBase, "", "REPORT BASE"),
        (MenuIds.ApproachReportFinal, "", "REPORT FINAL"),
        (MenuIds.ApproachReportCrosswind, "", "REPORT CROSSWIND"),
        (MenuIds.ApproachReportDownwind, "", "REPORT DOWNWIND"),
        (MenuIds.ApproachReportNMileFinal, "5", "REPORT 5 FINAL"),
        (MenuIds.ApproachReportAtFix, Fix, "REPORT SUNOL"),
        (MenuIds.ApproachReportOffBase, "", "REPORT OFF BASE"),
        (MenuIds.ApproachReportOffFinal, "", "REPORT OFF FINAL"),
        (MenuIds.ApproachReportOffCrosswind, "", "REPORT OFF CROSSWIND"),
        (MenuIds.ApproachReportOffDownwind, "", "REPORT OFF DOWNWIND"),
        (MenuIds.ApproachReportOffAll, "", "REPORT OFF"),
        (MenuIds.ProceduresJoinStar, StarId, "JARR EMZOH4"),
        (MenuIds.ProceduresClimbViaSid, "", "CVIA"),
        (MenuIds.ProceduresDescendViaStar, "", "DVIA"),
        (MenuIds.ProceduresPtac, "280 025 I28R", "PTAC 280 025 I28R"),
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
        (MenuIds.PatternEnterLeftDownwind, "28R", "ELD 28R"),
        (MenuIds.PatternEnterRightDownwind, "28R", "ERD 28R"),
        (MenuIds.PatternEnterLeftBase, "28R", "ELB 28R"),
        (MenuIds.PatternEnterRightBase, "28R", "ERB 28R"),
        (MenuIds.PatternEnterFinal, "28R", "EF 28R"),
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
        (MenuIds.PatternFollow, Fix, "FOLLOW SUNOL"),
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
        MenuIds.SpawnDelay,
        MenuIds.LiveTrafficAssumeSelected,
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
        MenuItem ident = Assert.Single(squawk.Items.OfType<MenuItem>(), i => (i.Header as string) == "Ident");

        Click(ident);

        Assert.Equal([(Callsign, "IDENT", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public async Task Warp_OpensPopupWithAircraftValues_AndSubmitSendsWarpText()
    {
        var host = new RecordingMenuHost("");
        var aircraft = new FakeMenuAircraft
        {
            HeadingDegrees = 90.4,
            AltitudeFeet = 5000,
            IndicatedAirspeedKnots = 249.6,
        };
        MenuItem? item = MenuCatalog.Get(MenuIds.SimControlWarp).Build(aircraft, Context(), host);

        Assert.NotNull(item);
        Click((MenuItem)item);

        Assert.Equal([(Callsign, "", 90, 5000, 250)], host.WarpPopups);

        Func<string, int, int, int, Task>? submit = host.WarpSubmit;
        Assert.NotNull(submit);
        await submit("OAK090010", 180, 6000, 210);

        Assert.Equal([(Callsign, "WARP OAK090010 180 6000 210", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void Warp_ZeroHeading_OpensPopupWith360()
    {
        var host = new RecordingMenuHost("");
        MenuItem? item = MenuCatalog.Get(MenuIds.SimControlWarp).Build(new FakeMenuAircraft(), Context(), host);

        Assert.NotNull(item);
        Click((MenuItem)item);

        Assert.Equal([(Callsign, "", 360, 0, 0)], host.WarpPopups);
    }

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
        Assert.Empty(host.ListPopups);
    }

    [AvaloniaFact]
    public void Warp_SeedsPopupWithTheMagneticHeading_NotTheTrueOne()
    {
        AircraftModel aircraft = OverKoakOnTrueHeading100();
        int heading = int.Parse(aircraft.HeadingDisplay, CultureInfo.InvariantCulture);
        Assert.NotEqual(100, heading);
        var host = new RecordingMenuHost("");

        MenuItem? item = MenuCatalog.Get(MenuIds.SimControlWarp).Build(aircraft, Context(), host);
        Assert.NotNull(item);
        Click((MenuItem)item);

        Assert.Equal(heading, Assert.Single(host.WarpPopups).Heading);
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

        Assert.Equal(new MenuRichRow("●", "3,000", "now", MenuRichRowKind.Now, "CM 3000", []), Row(apart, "3,000"));
        Assert.Equal(new MenuRichRow("◆", "5,000", "assigned", MenuRichRowKind.Assigned, "CM 5000", []), Row(apart, "5,000"));
        Assert.Equal(new MenuRichRow("↑", "4,000", "CM 4000", MenuRichRowKind.Climb, "CM 4000", []), Row(apart, "4,000"));
        Assert.Equal(new MenuRichRow("↓", "2,000", "DM 2000", MenuRichRowKind.Descend, "DM 2000", []), Row(apart, "2,000"));
        Assert.Equal(new MenuRichRow("●", "3,000", "now · assigned", MenuRichRowKind.NowAssigned, "CM 3000", []), Row(same, "3,000"));
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

        Assert.Equal(new MenuRichRow("↓", "2,500", "below MVA", MenuRichRowKind.BelowMva, "DM 2500", []), Row(popup, "2,500"));
        Assert.Equal(new MenuRichRow("↓", "2,600", "DM 2600", MenuRichRowKind.Descend, "DM 2600", []), Row(popup, "2,600"));
        Assert.All(
            popup.Rows.Where(row => (row.Kind != MenuRichRowKind.MvaLine) && (AltitudeOf(row) < 2600)),
            row => Assert.Equal(MenuRichRowKind.BelowMva, row.Kind)
        );
        Assert.Equal([(Callsign, "DM 2500", Initials)], host.Sent);

        var lowHost = new RecordingMenuHost("") { Mva = ("12", 2600) };
        MenuRichList apart = OpenMaintain(new FakeMenuAircraft { AltitudeFeet = 2000, AssignedAltitude = 1500 }, lowHost);
        Assert.Equal(new MenuRichRow("●", "2,000", "now · below MVA", MenuRichRowKind.BelowMva, "DM 2000", []), Row(apart, "2,000"));
        Assert.Equal(new MenuRichRow("◆", "1,500", "assigned · below MVA", MenuRichRowKind.BelowMva, "DM 1500", []), Row(apart, "1,500"));
        Assert.Equal("1,500", apart.Rows[apart.SelectedIndex].Label);

        var sameHost = new RecordingMenuHost("") { Mva = ("12", 2600) };
        MenuRichList same = OpenMaintain(new FakeMenuAircraft { AltitudeFeet = 2000, AssignedAltitude = 2000 }, sameHost);
        Assert.Equal(new MenuRichRow("●", "2,000", "now · assigned · below MVA", MenuRichRowKind.BelowMva, "CM 2000", []), Row(same, "2,000"));
    }

    [AvaloniaFact]
    public void MaintainFlyout_OffStepAssignmentGetsItsOwnRow()
    {
        MenuRichList popup = OpenMaintain(new FakeMenuAircraft { AltitudeFeet = 3000, AssignedAltitude = 12300 }, new RecordingMenuHost(""));

        MenuRichRow assigned = Row(popup, "12,300");
        Assert.Equal(new MenuRichRow("◆", "12,300", "assigned", MenuRichRowKind.Assigned, "CM 12300", []), assigned);
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

        Assert.Equal(new MenuRichRow("◆", "FL250", "assigned", MenuRichRowKind.Assigned, "CM 25000", []), popup.Rows[0]);
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
        Assert.Empty(host.RichListPopups);
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
        Assert.Empty(host.ListPopups);
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
    [InlineData(MenuIds.PatternEnterFinal, "Make straight-in", "Make straight-in…")]
    [InlineData(MenuIds.PatternFollow, "Follow traffic…", "Follow traffic…")]
    [InlineData(MenuIds.SpeedFinalApproach, "Reduce to final approach speed", "Reduce to final approach speed")]
    [InlineData(MenuIds.ApproachReportNMileFinal, "At N-mile final…", "At N-mile final…")]
    [InlineData(MenuIds.ApproachReportAtFix, "At fix…", "At fix…")]
    [InlineData(MenuIds.NavigationOnCourse, "Proceed on course", "Proceed on course")]
    public void RenamedEntry_LabelAndHeader_ReadAsItsCommand(string id, string label, string header)
    {
        Assert.Equal(label, MenuCatalog.Get(id).Label);
        Assert.Equal(header, MenuCatalog.Get(id).Build(null, Context(), new RecordingMenuHost(""))?.Header as string);
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
        Assert.Empty(host.ListPopups);
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

    [AvaloniaFact]
    public void NavigationGroup_OffersAppendDirectToOnlyWhileNavigating()
    {
        var host = new RecordingMenuHost("");
        MenuItem idle = SharedMenuGroups.Navigation(new FakeMenuAircraft(), Context(), host);
        MenuItem navigating = SharedMenuGroups.Navigation(new FakeMenuAircraft { NavigatingTo = Fix }, Context(), host);

        Assert.Equal("Navigation", idle.Header as string);
        Assert.Equal(["Direct to…"], idle.Items.Select(Describe));
        Assert.Equal("Navigation (→ SUNOL)", navigating.Header as string);
        Assert.Equal(["Direct to…", "Append direct to…"], navigating.Items.Select(Describe));
    }

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

        MenuItem approach = SharedMenuGroups.Approach(aircraft, Context(), host);

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

    [AvaloniaFact]
    public void ClearedVisual_NoDefaultWithRunways_UsesListTier()
    {
        TestVnasData.EnsureInitialized();
        string[] runways = [.. RunwayDesignators.ForAirport(ApproachAirport)];
        Assert.Contains("28R", runways);
        var host = new RecordingMenuHost("28R");
        var aircraft = new FakeMenuAircraft { Destination = ApproachAirport };

        MenuItem? item = MenuCatalog.Get(MenuIds.ApproachClearedVisual).Build(aircraft, Context(), host);
        Click(AssertPicker(item, "Cleared visual approach…", MenuPickerDescriptor.List, runways));

        Assert.Equal<object?>(runways[0], Assert.Single(host.ListPopups).Selected);
        Assert.Equal([(Callsign, "CVA 28R", Initials)], host.Sent);
        Assert.Null(MenuCatalog.BuildClearedVisualOther(aircraft, Context(), host));
    }

    [AvaloniaFact]
    public void ClearedVisualOther_BesideADefaultRunway_ListsTheRunwaysAndSendsCva()
    {
        TestVnasData.EnsureInitialized();
        string[] runways = [.. RunwayDesignators.ForAirport(ApproachAirport)];
        Assert.Contains("28R", runways);
        var host = new RecordingMenuHost("28R");
        var aircraft = new FakeMenuAircraft { Destination = ApproachAirport, AssignedRunway = "30" };

        Assert.Equal("Cleared visual approach 30", MenuCatalog.Get(MenuIds.ApproachClearedVisual).Build(aircraft, Context(), host)?.Header as string);
        MenuItem? other = MenuCatalog.BuildClearedVisualOther(aircraft, Context(), host);
        Click(AssertPicker(other, "Cleared visual approach (other)…", MenuPickerDescriptor.List, runways));

        Assert.Equal([(Callsign, "CVA 28R", Initials)], host.Sent);
    }

    /// <summary>A scenario's expected approach in shorthand (R28L for KOAK's R28LY) still gives the visual approach its runway.</summary>
    [AvaloniaFact]
    public void ClearedVisual_ExpectedApproachInShorthand_DefaultsToItsRunway()
    {
        TestVnasData.EnsureInitialized();
        var host = new RecordingMenuHost("");
        var aircraft = new FakeMenuAircraft { Destination = ApproachAirport, ExpectedApproach = "R28L" };

        Assert.Equal("28L", MenuCatalog.SmartVisualRunway(aircraft));
        MenuItem? item = MenuCatalog.Get(MenuIds.ApproachClearedVisual).Build(aircraft, Context(), host);
        Assert.NotNull(item);
        Assert.Equal("Cleared visual approach 28L", item.Header as string);
        Click((MenuItem)item);

        Assert.Equal([(Callsign, "CVA 28L", Initials)], host.Sent);
    }

    /// <summary>An expected visual approach (<c>EAPP VIS28R</c>) names no procedure; the visual approach defaults to its runway.</summary>
    [AvaloniaFact]
    public void ClearedVisual_ExpectedVisualApproach_DefaultsToItsRunway()
    {
        TestVnasData.EnsureInitialized();
        var host = new RecordingMenuHost("");
        var aircraft = new FakeMenuAircraft { Destination = ApproachAirport, ExpectedApproach = "VIS28R" };

        Assert.Equal("28R", MenuCatalog.SmartVisualRunway(aircraft));
        MenuItem? item = MenuCatalog.Get(MenuIds.ApproachClearedVisual).Build(aircraft, Context(), host);
        Assert.NotNull(item);
        Click((MenuItem)item);

        Assert.Equal([(Callsign, "CVA 28R", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void ReportTrafficInSight_BlankAnswer_SendsBareRtis()
    {
        var host = new RecordingMenuHost("");
        MenuItem? item = MenuCatalog.Get(MenuIds.ApproachReportTrafficInSight).Build(null, Context(), host);

        Click(AssertPicker(item, "Report traffic in sight…", MenuPickerDescriptor.Input, []));

        Assert.Equal(["Target callsign (optional)"], host.InputPlaceholders);
        Assert.Equal([(Callsign, "RTIS", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void ReportTrafficInSight_AsksWithBlankSubmits()
    {
        var host = new RecordingMenuHost("");
        MenuItem? item = MenuCatalog.Get(MenuIds.ApproachReportTrafficInSight).Build(null, Context(), host);

        Click(AssertPicker(item, "Report traffic in sight…", MenuPickerDescriptor.Input, []));

        Assert.Equal(BlankInput.Submits, host.LastBlankInput);
    }

    [AvaloniaFact]
    public void ReportTrafficInSight_NoNearbyTraffic_KeepsInputBox()
    {
        var host = new RecordingMenuHost("AAL12");
        MenuItem? item = MenuCatalog.Get(MenuIds.ApproachReportTrafficInSight).Build(null, Context(), host);

        Click(AssertPicker(item, "Report traffic in sight…", MenuPickerDescriptor.Input, []));

        Assert.Empty(host.RichListPopups);
        Assert.Equal([(Callsign, "RTIS AAL12", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void ReportTrafficInSight_ListsNearestTrafficThenAnyAndOther()
    {
        RecordingMenuHost host = TrafficHost("", null);
        MenuItem? item = MenuCatalog.Get(MenuIds.ApproachReportTrafficInSight).Build(null, Context(), host);

        Click(
            AssertPicker(
                item,
                "Report traffic in sight…",
                MenuPickerDescriptor.RichList,
                ["AAL601 · B738", "N302AB", "---", "Any traffic (no target)", "Other callsign…"]
            )
        );

        MenuRichList list = Assert.Single(host.RichListPopups);
        Assert.Equal("Nearest traffic", list.Title);
        Assert.Equal(0, list.SelectedIndex);
        Assert.Equal(
            new MenuRichRow("", "AAL601 · B738", "", MenuRichRowKind.Traffic, "RTIS AAL601", ["2 o'clock", "4 nm", "1,500 below"]),
            list.Rows[0]
        );
        Assert.Equal(["2 o'clock", "4 nm", "1,500 below"], list.Rows[0].Columns);
        Assert.Equal(["11 o'clock", "7 nm", "same altitude"], list.Rows[1].Columns);
        Assert.Equal(
            [MenuRichRowKind.Traffic, MenuRichRowKind.Traffic, MenuRichRowKind.Separator, MenuRichRowKind.Action, MenuRichRowKind.Prompt],
            list.Rows.Select(row => row.Kind)
        );
        Assert.Equal([true, true, false, true, true], list.Rows.Select(row => row.IsPickable));
        Assert.Empty(host.Sent);
    }

    [AvaloniaFact]
    public void ReportTrafficInSight_RowSendsRtisCallsign()
    {
        RecordingMenuHost host = TrafficHost("N302AB", null);

        Click(MenuCatalog.Get(MenuIds.ApproachReportTrafficInSight).Build(null, Context(), host)!);

        Assert.Equal([(Callsign, "RTIS N302AB", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void ReportTrafficInSight_AnyTrafficSendsBareRtis()
    {
        RecordingMenuHost host = TrafficHost("Any traffic (no target)", null);

        Click(MenuCatalog.Get(MenuIds.ApproachReportTrafficInSight).Build(null, Context(), host)!);

        Assert.Equal([(Callsign, "RTIS", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void ReportTrafficInSight_OtherCallsignOpensInput()
    {
        RecordingMenuHost typed = TrafficHost("Other callsign…", "UAL5");
        Click(MenuCatalog.Get(MenuIds.ApproachReportTrafficInSight).Build(null, Context(), typed)!);

        Assert.Equal(["Target callsign (optional)"], typed.InputPlaceholders);
        Assert.Equal(BlankInput.Submits, typed.LastBlankInput);
        Assert.Equal([(Callsign, "RTIS UAL5", Initials)], typed.Sent);

        RecordingMenuHost blank = TrafficHost("Other callsign…", "");
        Click(MenuCatalog.Get(MenuIds.ApproachReportTrafficInSight).Build(null, Context(), blank)!);

        Assert.Equal([(Callsign, "RTIS", Initials)], blank.Sent);
    }

    /// <summary>
    /// A host answering two aircraft of nearby traffic, picking the rich-list row <paramref name="pick"/> and typing
    /// <paramref name="typed"/>.
    /// </summary>
    private static RecordingMenuHost TrafficHost(string pick, string? typed)
    {
        var host = new RecordingMenuHost(pick) { InputAnswer = typed };
        host.NearbyTraffic.Add(new MenuTrafficRow("AAL601", "B738", 4.2, 2, -1500));
        host.NearbyTraffic.Add(new MenuTrafficRow("N302AB", "", 6.5, 11, 0));
        return host;
    }

    [AvaloniaFact]
    public void Squawk_AsksWithBlankCloses()
    {
        var host = new RecordingMenuHost(PositionOrCode);
        MenuItem? item = MenuCatalog.Get(MenuIds.SquawkCode).Build(null, Context(), host);

        Click(AssertPicker(item, "Squawk…", MenuPickerDescriptor.Input, []));

        Assert.Equal(BlankInput.Closes, host.LastBlankInput);
    }

    [AvaloniaFact]
    public void Approach_Header_ShowsActiveThenExpected()
    {
        var host = new RecordingMenuHost("");

        Assert.Equal("Approach", SharedMenuGroups.Approach(new FakeMenuAircraft(), Context(), host).Header as string);
        Assert.Equal(
            "Approach (exp: I28R)",
            SharedMenuGroups.Approach(new FakeMenuAircraft { ExpectedApproach = ApproachId }, Context(), host).Header as string
        );
        Assert.Equal(
            "Approach (I30)",
            SharedMenuGroups.Approach(new FakeMenuAircraft { ActiveApproachId = "I30", ExpectedApproach = ApproachId }, Context(), host).Header
                as string
        );
    }

    [AvaloniaFact]
    public void ClearedVisual_NoDefaultNoRunways_UsesInputTier()
    {
        TestVnasData.EnsureInitialized();
        Assert.Empty(NavigationDatabase.Instance.GetRunways(UnknownAirport));
        var host = new RecordingMenuHost("28R");
        var aircraft = new FakeMenuAircraft { Destination = UnknownAirport };

        MenuItem? item = MenuCatalog.Get(MenuIds.ApproachClearedVisual).Build(aircraft, Context(), host);
        Click(AssertPicker(item, "Cleared visual approach…", MenuPickerDescriptor.Input, []));

        Assert.Equal(["Runway (e.g. 28R)"], host.InputPlaceholders);
        Assert.Equal([(Callsign, "CVA 28R", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void ClearedVisual_DefaultFromAssignedRunway_LabelsAndSendsCva()
    {
        var host = new RecordingMenuHost("");
        var aircraft = new FakeMenuAircraft { AssignedRunway = "09L" };

        MenuItem? item = MenuCatalog.Get(MenuIds.ApproachClearedVisual).Build(aircraft, Context(), host);
        Assert.NotNull(item);
        Assert.Equal("Cleared visual approach 9L", item.Header as string);
        Assert.Null(item.Tag);
        Click((MenuItem)item);

        Assert.Equal([(Callsign, "CVA 09L", Initials)], host.Sent);
        Assert.Null(MenuCatalog.BuildClearedVisualOther(aircraft, Context(), host));
    }

    [AvaloniaFact]
    public void ReportWhen_LeavesSendTheirCommands()
    {
        var host = new RecordingMenuHost("5");
        MenuItem reportWhen = Assert.Single(
            SharedMenuGroups.Approach(null, Context(), host).Items.OfType<MenuItem>(),
            i => (i.Header as string) == "Report when…"
        );
        MenuItem stop = Assert.IsType<MenuItem>(reportWhen.Items[^1]);

        Assert.Equal(
            ["Turning base", "Turning final", "Turning crosswind", "Turning downwind", "At N-mile final…", "At fix…", "---", "Stop reporting"],
            reportWhen.Items.Select(Describe)
        );
        Assert.Equal(["Base", "Final", "Crosswind", "Downwind", "---", "All reports"], stop.Items.Select(Describe));

        foreach (MenuItem item in reportWhen.Items.OfType<MenuItem>().Where(i => i != stop).Concat(stop.Items.OfType<MenuItem>()))
        {
            Click(item);
        }

        Assert.Equal(["Distance (NM)", "Fix name"], host.InputPlaceholders);
        Assert.Equal(
            [
                "REPORT BASE",
                "REPORT FINAL",
                "REPORT CROSSWIND",
                "REPORT DOWNWIND",
                "REPORT 5 FINAL",
                "REPORT 5",
                "REPORT OFF BASE",
                "REPORT OFF FINAL",
                "REPORT OFF CROSSWIND",
                "REPORT OFF DOWNWIND",
                "REPORT OFF",
            ],
            host.Sent.Select(s => s.Command)
        );
    }

    [AvaloniaFact]
    public void JoinStar_NoStarsNoDefault_UsesInputTier()
    {
        TestVnasData.EnsureInitialized();
        Assert.Empty(NavigationDatabase.Instance.GetStars(UnknownAirport));
        var host = new RecordingMenuHost(StarId);
        var aircraft = new FakeMenuAircraft { Destination = UnknownAirport, Route = $"SUNOL {StarId}" };

        Click(
            AssertPicker(MenuCatalog.Get(MenuIds.ProceduresJoinStar).Build(aircraft, Context(), host), "Join STAR…", MenuPickerDescriptor.Input, [])
        );

        Assert.Equal(["STAR name"], host.InputPlaceholders);
        Assert.Equal([(Callsign, "JARR EMZOH4", Initials)], host.Sent);
        Assert.Null(MenuCatalog.BuildJoinStarOther(aircraft, Context(), host));
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

        Assert.Empty(host.ListPopups);
        Assert.Empty(host.InputPlaceholders);
        Assert.Equal([(Callsign, "JAWY V25", Initials)], host.Sent);
        Assert.Equal("V25", Assert.IsType<JoinAirwayCommand>(CommandParser.Parse("JAWY V25").Value).AirwayId);
    }

    [AvaloniaFact]
    public void JoinStar_FiledStar_LeafPlusOtherList()
    {
        TestVnasData.EnsureInitialized();
        string[] stars =
        [
            .. NavigationDatabase.Instance.GetStars(ApproachAirport).Select(s => s.ProcedureId).Order(StringComparer.OrdinalIgnoreCase),
        ];
        Assert.Contains(StarId, stars);
        string other = stars.First(s => s != StarId);
        var host = new RecordingMenuHost(other);
        var aircraft = new FakeMenuAircraft { Destination = ApproachAirport, Route = $"RGOOD.{StarId}.{ApproachAirport}" };

        MenuItem? item = MenuCatalog.Get(MenuIds.ProceduresJoinStar).Build(aircraft, Context(), host);
        Assert.NotNull(item);
        Assert.Equal("Join STAR EMZOH4", item.Header as string);
        Assert.Null(item.Tag);
        Click((MenuItem)item);
        Click(AssertPicker(MenuCatalog.BuildJoinStarOther(aircraft, Context(), host), "Join STAR (other)…", MenuPickerDescriptor.List, stars));

        Assert.Equal<object?>(stars[0], Assert.Single(host.ListPopups).Selected);
        Assert.Equal([(Callsign, "JARR EMZOH4", Initials), (Callsign, $"JARR {other}", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void JoinStar_NoFiledStar_ListsTheStars()
    {
        TestVnasData.EnsureInitialized();
        string[] stars =
        [
            .. NavigationDatabase.Instance.GetStars(ApproachAirport).Select(s => s.ProcedureId).Order(StringComparer.OrdinalIgnoreCase),
        ];
        Assert.Contains(StarId, stars);
        var host = new RecordingMenuHost(StarId);
        var aircraft = new FakeMenuAircraft { Destination = ApproachAirport, Route = "" };

        MenuItem? item = MenuCatalog.Get(MenuIds.ProceduresJoinStar).Build(aircraft, Context(), host);
        Click(AssertPicker(item, "Join STAR…", MenuPickerDescriptor.List, stars));

        Assert.Equal<object?>(stars[0], Assert.Single(host.ListPopups).Selected);
        Assert.Equal([(Callsign, "JARR EMZOH4", Initials)], host.Sent);
        Assert.Null(MenuCatalog.BuildJoinStarOther(aircraft, Context(), host));
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
    public void Tower_CtoCustom_BlankSendsCto_TextIsTrimmed(string input, string command)
    {
        var host = new RecordingMenuHost(input);

        List<MenuItem> children = ClearedForTakeoffChildItems(host);

        Click(Assert.Single(children, m => m.Header is "Custom…"));
        Assert.Equal(["CTO arg (e.g. RH 3000, LT 270, DCT BERKS)"], host.InputPlaceholders);
        Assert.Equal([(Callsign, command, Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void Tower_CtoCustom_AsksWithBlankSubmits()
    {
        var host = new RecordingMenuHost("");

        List<MenuItem> children = ClearedForTakeoffChildItems(host);

        Click(Assert.Single(children, m => m.Header is "Custom…"));
        Assert.Equal(BlankInput.Submits, host.LastBlankInput);
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
                "Custom…",
            ],
            cto.Items.Select(DescribeRow)
        );

        foreach (MenuItem child in cto.Items.OfType<MenuItem>().Where(i => (i.Header as string) != "Custom…"))
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
            ["Cleared for takeoff — CTO", "Fly heading — CTO ···", "Fly runway heading — CTO RH", "---", "Custom…"],
            cto.Items.Select(DescribeRow)
        );
    }

    // The takeoff submenu ends with the separator and Custom, on the recording host and on the client host every view builds.
    [AvaloniaFact]
    public void Cto_EndsWithSeparatorAndCustom()
    {
        MenuItem cto = CtoSubmenu(OnGround("LinedUpAndWaiting", "IFR", "30"), new RecordingMenuHost(""));

        Assert.Equal("Custom…", Assert.IsType<MenuItem>(cto.Items[^1]).Header as string);
        Assert.IsType<Separator>(cto.Items[^2]);

        AircraftModel ac = OnGround("Taxiing", "IFR", "30");
        IMenuHost host = new ClientMenuHost(new MainViewModel(new FakeFilePickerService()), ac, new Border());

        MenuItem? clientCto = MenuCatalog.Get(MenuIds.TowerClearedForTakeoff).Build(ac, Context(VfrCommandsForIfr.None), host);

        Assert.NotNull(clientCto);
        Assert.IsType<Separator>(clientCto.Items[^2]);
        Assert.Equal("Custom…", Assert.IsType<MenuItem>(clientCto.Items[^1]).Header as string);
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
        Assert.Equal(["Make straight-in…"], enterFinalOnly.Items.Select(Describe));
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
        MenuItem? input = MenuCatalog
            .Get(MenuIds.PatternEnterLeftDownwind)
            .Build(new FakeMenuAircraft { Destination = UnknownAirport, Departure = ApproachAirport }, Context(), host);
        Click(AssertPicker(input, "Enter left downwind…", MenuPickerDescriptor.Input, []));

        Assert.Equal([(Callsign, "ELD 28R", Initials), (Callsign, "ELD 28R", Initials)], host.Sent);
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

    [AvaloniaTheory]
    [InlineData("", "ELD")]
    [InlineData("   ", "ELD")]
    [InlineData("28R ", "ELD 28R ")]
    public void PatternEntry_NoDefaultNoRunways_UsesInputTier_BlankSendsTheBareVerb_TextUntrimmed(string input, string command)
    {
        TestVnasData.EnsureInitialized();
        Assert.Empty(RunwayDesignators.ForAirport(UnknownAirport));
        var host = new RecordingMenuHost(input);
        var aircraft = new FakeMenuAircraft { Destination = UnknownAirport };

        MenuItem? item = MenuCatalog.Get(MenuIds.PatternEnterLeftDownwind).Build(aircraft, Context(), host);
        Click(AssertPicker(item, "Enter left downwind…", MenuPickerDescriptor.Input, []));

        Assert.Equal(["Runway (optional)"], host.InputPlaceholders);
        Assert.Equal([(Callsign, command, Initials)], host.Sent);
        Assert.Null(MenuCatalog.BuildPatternEntryOther(MenuIds.PatternEnterLeftDownwind, aircraft, Context(), host));
    }

    [AvaloniaFact]
    public void PatternEntry_InputTier_AsksWithBlankSubmits()
    {
        TestVnasData.EnsureInitialized();
        Assert.Empty(RunwayDesignators.ForAirport(UnknownAirport));
        var host = new RecordingMenuHost("");
        var aircraft = new FakeMenuAircraft { Destination = UnknownAirport };

        MenuItem? item = MenuCatalog.Get(MenuIds.PatternEnterLeftDownwind).Build(aircraft, Context(), host);
        Click(AssertPicker(item, "Enter left downwind…", MenuPickerDescriptor.Input, []));

        Assert.Equal(BlankInput.Submits, host.LastBlankInput);
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
        Assert.Null(MenuCatalog.BuildPatternEntryOther(MenuIds.PatternEnterFinal, aircraft, Context(), host));
    }

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
        Assert.Null(MenuCatalog.BuildPatternEntryOther(MenuIds.PatternEnterRightBase, noDefault, Context(), host));

        Assert.Equal("Enter right base 30", MenuCatalog.Get(MenuIds.PatternEnterRightBase).Build(withDefault, Context(), host)?.Header as string);
        MenuItem? other = MenuCatalog.BuildPatternEntryOther(MenuIds.PatternEnterRightBase, withDefault, Context(), host);
        Click(PatternRunwayRow(other, "Enter right base (other)", runways, "28R"));

        Assert.Empty(host.ListPopups);
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
