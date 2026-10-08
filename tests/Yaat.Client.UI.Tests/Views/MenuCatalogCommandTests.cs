using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
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
        (MenuIds.DataBlockTempAltitude, "050", "TEMPALT 50"),
        (MenuIds.DataBlockCruise, "050", "CRUISE 50"),
        (MenuIds.DataBlockAnnotate, "", "ANNOTATE"),
        (MenuIds.SimControlDelete, "", "DEL"),
        (MenuIds.HeadingPresent, "", "FPH"),
        (MenuIds.HeadingFly, "270", "FH 270"),
        (MenuIds.HeadingTurnLeft, "270", "TL 270"),
        (MenuIds.HeadingTurnRight, "270", "TR 270"),
        (MenuIds.HeadingTurnLeftDegrees, "30", "LT 30"),
        (MenuIds.HeadingTurnRightDegrees, "30", "RT 30"),
        (MenuIds.AltitudeMaintain, "FL350", "CM 35000"),
        (MenuIds.SpeedAssign, "250", "SPD 250"),
        (MenuIds.SpeedCustom, "210", "SPD 210"),
        (MenuIds.SpeedNormal, "", "RNS"),
        (MenuIds.SpeedFinalApproach, "", "RFAS"),
        (MenuIds.NavigationDirectTo, Fix, "DCT SUNOL"),
        (MenuIds.NavigationAppendDirectTo, Fix, "ADCT SUNOL"),
        (MenuIds.HoldPresentLeft, "", "HPPL"),
        (MenuIds.HoldPresentRight, "", "HPPR"),
        (MenuIds.HoldFixLeft, Fix, "HFIXL SUNOL"),
        (MenuIds.HoldFixRight, Fix, "HFIXR SUNOL"),
        (MenuIds.ApproachCleared, ApproachId, "CAPP I28R"),
        (MenuIds.ApproachJoin, ApproachId, "JAPP I28R"),
        (MenuIds.ApproachClearedStraightIn, ApproachId, "CAPPSI I28R"),
        (MenuIds.ApproachJoinStraightIn, ApproachId, "JAPPSI I28R"),
        (MenuIds.ApproachClearedForce, ApproachId, "CAPPF I28R"),
        (MenuIds.ApproachJoinForce, ApproachId, "JAPPF I28R"),
        (MenuIds.ApproachJoinFinalCourse, ApproachId, "JFAC I28R"),
        (MenuIds.ApproachExpect, ApproachId, "EAPP I28R"),
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
        (MenuIds.ProceduresCrossFix, Fix, "CFIX SUNOL"),
        (MenuIds.ProceduresDepartFix, Fix, "DEPART SUNOL"),
        (MenuIds.ProceduresPtac, "280 025 I28R", "PTAC 280 025 I28R"),
        (MenuIds.ProceduresJoinAirway, "V25", "JAWY V25"),
        (MenuIds.ProceduresJoinRadialOutbound, "SUNOL 090", "JRADO SUNOL 090"),
        (MenuIds.ProceduresJoinRadialInbound, "SUNOL 270", "JRADI SUNOL 270"),
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
        (MenuIds.TowerExitLeft, "", "EL"),
        (MenuIds.TowerExitRight, "", "ER"),
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
        (MenuIds.HoldPattern, Fix, "HOLDP SUNOL"),
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
        MenuIds.GroundDrawTaxiRoute,
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
        Click(item);

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
        Click(item);

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
        Click(item);

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
        Click(item);

        Assert.Equal([(Callsign, "", 360, 0, 0)], host.WarpPopups);
    }

    [AvaloniaFact]
    public void EditFlightPlan_CallsHostOpenFlightPlanEditor()
    {
        var host = new RecordingMenuHost("");
        MenuItem? item = MenuCatalog.Get(MenuIds.AircraftEditFlightPlan).Build(new FakeMenuAircraft(), Context(), host);

        Assert.NotNull(item);
        Click(item);

        Assert.Equal(1, host.FlightPlanEditorOpens);
    }

    [AvaloniaFact]
    public void Delete_SendsDel()
    {
        var host = new RecordingMenuHost("");
        MenuItem? item = MenuCatalog.Get(MenuIds.SimControlDelete).Build(null, Context(), host);

        Assert.NotNull(item);
        Click(item);

        Assert.Equal([(Callsign, "DEL", Initials)], host.Sent);
    }

    [AvaloniaTheory]
    [InlineData(92.0, 90)]
    [InlineData(1.0, 360)]
    public void FlyHeading_SeedsPopupWithTheHeadingRoundedToFive(double heading, int seed)
    {
        var host = new RecordingMenuHost("270");
        MenuItem? item = MenuCatalog.Get(MenuIds.HeadingFly).Build(new FakeMenuAircraft { HeadingDegrees = heading }, Context(), host);

        Assert.NotNull(item);
        Click(item);

        (IReadOnlyList<string> Items, object? Selected) popup = Assert.Single(host.ListPopups);
        Assert.Equal<object?>(seed, popup.Selected);
        Assert.Equal(72, popup.Items.Count);
        Assert.Equal([(Callsign, "FH 270", Initials)], host.Sent);
    }

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
        var host = new RecordingMenuHost(pick);
        MenuItem? item = MenuCatalog.Get(MenuIds.AltitudeMaintain).Build(new FakeMenuAircraft { AltitudeFeet = altitude }, Context(), host);

        Assert.NotNull(item);
        Assert.Equal("Maintain", item.Header as string);
        Click(item);

        MenuRichList popup = Assert.Single(host.RichListPopups);
        Assert.Equal("10,000", popup.Rows[popup.SelectedIndex].Label);
        Assert.Equal([(Callsign, command, Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void MaintainAltitude_ListsFromTheDestinationsFieldElevation_In100sThen500s()
    {
        var host = new RecordingMenuHost("9,500") { FieldElevation = 4321 };
        var aircraft = new FakeMenuAircraft { AltitudeFeet = 5000, Destination = "KXYZ" };
        MenuRichList popup = OpenMaintain(aircraft, host);

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
    public void MaintainPicker_TitleAndSubtitleNameTheAircraft()
    {
        MenuRichList assigned = OpenMaintain(new FakeMenuAircraft { AltitudeFeet = 3040, AssignedAltitude = 5000 }, new RecordingMenuHost(""));
        MenuRichList unassigned = OpenMaintain(new FakeMenuAircraft { AltitudeFeet = 24960 }, new RecordingMenuHost(""));

        Assert.Equal($"{Callsign} · Maintain", assigned.Title);
        Assert.Equal("now 3,000 · assigned 5,000 · type to jump", assigned.Subtitle);
        Assert.Equal($"{Callsign} · Maintain", unassigned.Title);
        Assert.Equal("now FL250 · type to jump", unassigned.Subtitle);
    }

    [AvaloniaFact]
    public void MaintainPicker_OpensOnTheAssignedRow()
    {
        MenuRichList popup = OpenMaintain(new FakeMenuAircraft { AltitudeFeet = 3000, AssignedAltitude = 35000 }, new RecordingMenuHost(""));

        MenuRichRow selected = popup.Rows[popup.SelectedIndex];
        Assert.Equal("FL350", selected.Label);
        Assert.Equal(MenuRichRowKind.Assigned, selected.Kind);
    }

    [AvaloniaFact]
    public void MaintainPicker_OpensOnTheCurrentRowWhenUnassigned()
    {
        MenuRichList popup = OpenMaintain(new FakeMenuAircraft { AltitudeFeet = 3040 }, new RecordingMenuHost(""));

        MenuRichRow selected = popup.Rows[popup.SelectedIndex];
        Assert.Equal("3,000", selected.Label);
        Assert.Equal(MenuRichRowKind.Now, selected.Kind);
    }

    [AvaloniaFact]
    public void MaintainPicker_MarksNowAndAssignedRows()
    {
        MenuRichList apart = OpenMaintain(new FakeMenuAircraft { AltitudeFeet = 3000, AssignedAltitude = 5000 }, new RecordingMenuHost(""));
        MenuRichList same = OpenMaintain(new FakeMenuAircraft { AltitudeFeet = 3000, AssignedAltitude = 3000 }, new RecordingMenuHost(""));

        Assert.Equal(new MenuRichRow("●", "3,000", "now", MenuRichRowKind.Now, "CM 3000", 3000, []), Row(apart, "3,000"));
        Assert.Equal(new MenuRichRow("◆", "5,000", "assigned", MenuRichRowKind.Assigned, "CM 5000", 5000, []), Row(apart, "5,000"));
        Assert.Equal(new MenuRichRow("↑", "4,000", "CM 4000", MenuRichRowKind.Climb, "CM 4000", 4000, []), Row(apart, "4,000"));
        Assert.Equal(new MenuRichRow("↓", "2,000", "DM 2000", MenuRichRowKind.Descend, "DM 2000", 2000, []), Row(apart, "2,000"));
        Assert.Equal(new MenuRichRow("●", "3,000", "now · assigned", MenuRichRowKind.NowAssigned, "CM 3000", 3000, []), Row(same, "3,000"));
        Assert.DoesNotContain(same.Rows, row => row.Kind is MenuRichRowKind.Assigned or MenuRichRowKind.Now);
    }

    [AvaloniaTheory]
    [InlineData(5000.0, "CM 3000")]
    [InlineData(1000.0, "DM 3000")]
    [InlineData(null, "CM 3000")]
    public void MaintainPicker_NowRowSendsTowardTheAssignment(double? assigned, string command)
    {
        var host = new RecordingMenuHost("3,000");
        OpenMaintain(new FakeMenuAircraft { AltitudeFeet = 3000, AssignedAltitude = assigned }, host);

        Assert.Equal([(Callsign, command, Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void MaintainPicker_StopsAtTheTypeCeiling()
    {
        TestVnasData.EnsureInitialized();
        double? ceiling = AircraftPerformance.Ceiling("C172");
        Assert.NotNull(ceiling);
        Assert.True(ceiling.Value is > 5000 and < 60000, $"C172's ceiling {ceiling} sits inside the 500-ft steps");
        int highestStep = (int)(Math.Floor(ceiling.Value / 500) * 500);

        MenuRichList popup = OpenMaintain(new FakeMenuAircraft { AltitudeFeet = 3000, FiledAircraftType = "C172" }, new RecordingMenuHost(""));

        Assert.Equal(highestStep, popup.Rows[0].Value);
        Assert.All(popup.Rows, row => Assert.True(row.Value <= ceiling.Value, $"{row.Label} is above the ceiling {ceiling}"));
    }

    [AvaloniaFact]
    public void MaintainPicker_TypeWithoutProfileKeepsTheSixtyThousandCap()
    {
        TestVnasData.EnsureInitialized();
        Assert.Null(AircraftPerformance.Ceiling("ZZZZ"));

        MenuRichList popup = OpenMaintain(new FakeMenuAircraft { AltitudeFeet = 3000, FiledAircraftType = "ZZZZ" }, new RecordingMenuHost(""));

        Assert.Equal("FL600", popup.Rows[0].Label);
    }

    [AvaloniaFact]
    public void MaintainPicker_DrawsTheMvaLineBetweenTheRightRows()
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
    public void MaintainPicker_RowsBelowTheMvaAreGreyedButSend()
    {
        var host = new RecordingMenuHost("2,500") { Mva = ("12", 2600) };
        MenuRichList popup = OpenMaintain(new FakeMenuAircraft { AltitudeFeet = 3000 }, host);

        Assert.Equal(new MenuRichRow("↓", "2,500", "below MVA", MenuRichRowKind.BelowMva, "DM 2500", 2500, []), Row(popup, "2,500"));
        Assert.Equal(new MenuRichRow("↓", "2,600", "DM 2600", MenuRichRowKind.Descend, "DM 2600", 2600, []), Row(popup, "2,600"));
        Assert.All(popup.Rows.Where(row => row.Value < 2600), row => Assert.Equal(MenuRichRowKind.BelowMva, row.Kind));
        Assert.Equal([(Callsign, "DM 2500", Initials)], host.Sent);

        var lowHost = new RecordingMenuHost("") { Mva = ("12", 2600) };
        MenuRichList apart = OpenMaintain(new FakeMenuAircraft { AltitudeFeet = 2000, AssignedAltitude = 1500 }, lowHost);
        Assert.Equal(new MenuRichRow("●", "2,000", "now · below MVA", MenuRichRowKind.BelowMva, "DM 2000", 2000, []), Row(apart, "2,000"));
        Assert.Equal(new MenuRichRow("◆", "1,500", "assigned · below MVA", MenuRichRowKind.BelowMva, "DM 1500", 1500, []), Row(apart, "1,500"));
        Assert.Equal("1,500", apart.Rows[apart.SelectedIndex].Label);

        var sameHost = new RecordingMenuHost("") { Mva = ("12", 2600) };
        MenuRichList same = OpenMaintain(new FakeMenuAircraft { AltitudeFeet = 2000, AssignedAltitude = 2000 }, sameHost);
        Assert.Equal(new MenuRichRow("●", "2,000", "now · assigned · below MVA", MenuRichRowKind.BelowMva, "CM 2000", 2000, []), Row(same, "2,000"));
    }

    [AvaloniaFact]
    public void MaintainPicker_OffStepAssignmentGetsItsOwnRow()
    {
        MenuRichList popup = OpenMaintain(new FakeMenuAircraft { AltitudeFeet = 3000, AssignedAltitude = 12300 }, new RecordingMenuHost(""));

        MenuRichRow assigned = Row(popup, "12,300");
        Assert.Equal(new MenuRichRow("◆", "12,300", "assigned", MenuRichRowKind.Assigned, "CM 12300", 12300, []), assigned);
        int index = popup.Rows.ToList().IndexOf(assigned);
        Assert.Equal(index, popup.SelectedIndex);
        Assert.Equal("12,500", popup.Rows[index - 1].Label);
        Assert.Equal("12,000", popup.Rows[index + 1].Label);
    }

    [AvaloniaFact]
    public void MaintainPicker_AssignmentAboveTheCeilingGetsItsOwnRow()
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

        Assert.Equal(new MenuRichRow("◆", "FL250", "assigned", MenuRichRowKind.Assigned, "CM 25000", 25000, []), popup.Rows[0]);
        Assert.Equal(0, popup.SelectedIndex);
        Assert.Equal((int)(Math.Floor(ceiling.Value / 500) * 500), popup.Rows[1].Value);
    }

    [AvaloniaFact]
    public void MaintainPicker_NoMvaNoLine()
    {
        MenuRichList popup = OpenMaintain(new FakeMenuAircraft { AltitudeFeet = 3000 }, new RecordingMenuHost(""));

        Assert.DoesNotContain(popup.Rows, row => row.Kind is MenuRichRowKind.MvaLine or MenuRichRowKind.BelowMva);
    }

    [AvaloniaFact]
    public void MaintainPicker_LabelsUseCommasThenFlightLevels()
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

    /// <summary>Builds and clicks the Maintain picker for <paramref name="aircraft"/> and returns the one rich list it showed.</summary>
    private static MenuRichList OpenMaintain(FakeMenuAircraft aircraft, RecordingMenuHost host)
    {
        MenuItem? item = MenuCatalog.Get(MenuIds.AltitudeMaintain).Build(aircraft, Context(), host);
        Assert.NotNull(item);
        Click(item);
        return Assert.Single(host.RichListPopups);
    }

    private static MenuRichRow Row(MenuRichList list, string label) => Assert.Single(list.Rows, row => row.Label == label);

    [AvaloniaFact]
    public void RelativeTurn_PreselectsThirtyDegrees()
    {
        var host = new RecordingMenuHost("30");
        MenuItem? item = MenuCatalog.Get(MenuIds.HeadingTurnLeftDegrees).Build(null, Context(), host);

        Assert.NotNull(item);
        Click(item);

        (IReadOnlyList<string> Items, object? Selected) popup = Assert.Single(host.ListPopups);
        Assert.Equal<object?>(30, popup.Selected);
        Assert.Equal(["5", "10", "15", "20", "30", "45", "60", "90"], popup.Items);
    }

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
    [InlineData(null, 250, "Speed")]
    public void AssignSpeed_SeedsPopupWithTheAssignedSpeed_OrTheListMiddle(double? assigned, int seed, string header)
    {
        var host = new RecordingMenuHost("200");
        var aircraft = new FakeMenuAircraft { AssignedSpeed = assigned };
        MenuItem? item = MenuCatalog.Get(MenuIds.SpeedAssign).Build(aircraft, Context(), host);

        Assert.NotNull(item);
        Click(item);

        Assert.Equal<object?>(seed, Assert.Single(host.ListPopups).Selected);
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

        Assert.Equal($"FAS - {fas:F0} kt", typed?.Header as string);
        Assert.Equal("FAS", untyped?.Header as string);
    }

    [AvaloniaTheory]
    [InlineData(MenuIds.NavigationDirectTo, "Direct to", "DCT")]
    [InlineData(MenuIds.NavigationAppendDirectTo, "Append direct to", "ADCT")]
    public void DirectTo_FilteredListWithRouteFixesFirst_ElseRouteFixList_ElseInput(string id, string label, string command)
    {
        var routed = new FakeMenuAircraft
        {
            NavigatingTo = Fix,
            NavigationRoute = [Fix, "ECA"],
            RouteFixes = [Fix, "ECA"],
        };

        var filteredHost = new RecordingMenuHost(Fix) { FixNames = ["ECA", "OAK", Fix] };
        Click(AssertPicker(MenuCatalog.Get(id).Build(routed, Context(), filteredHost), $"{label}…", MenuPickerDescriptor.FilteredList, [Fix, "ECA"]));
        (string[] Names, IReadOnlyList<string>? Priority) filteredPopup = Assert.Single(filteredHost.FilteredListPopups);
        Assert.Equal(["ECA", "OAK", Fix], filteredPopup.Names);
        Assert.Equal([Fix, "ECA"], filteredPopup.Priority);

        var listHost = new RecordingMenuHost("ECA");
        Click(AssertPicker(MenuCatalog.Get(id).Build(routed, Context(), listHost), label, MenuPickerDescriptor.List, [Fix, "ECA"]));
        Assert.Equal<object?>(Fix, Assert.Single(listHost.ListPopups).Selected);

        var inputHost = new RecordingMenuHost(Fix);
        Click(AssertPicker(MenuCatalog.Get(id).Build(new FakeMenuAircraft(), Context(), inputHost), $"{label}…", MenuPickerDescriptor.Input, []));

        Assert.Equal([(Callsign, $"{command} SUNOL", Initials)], filteredHost.Sent);
        Assert.Equal([(Callsign, $"{command} ECA", Initials)], listHost.Sent);
        Assert.Equal([(Callsign, $"{command} SUNOL", Initials)], inputHost.Sent);
    }

    [AvaloniaTheory]
    [InlineData(MenuIds.HoldFixLeft, "Hold at fix (left)…", "HFIXL")]
    [InlineData(MenuIds.HoldFixRight, "Hold at fix (right)…", "HFIXR")]
    public void HoldAtFix_FilteredListWithoutRouteFixes_ElseInput(string id, string label, string command)
    {
        var routed = new FakeMenuAircraft { NavigatingTo = "ECA", RouteFixes = [Fix, "ECA"] };

        var filteredHost = new RecordingMenuHost(Fix) { FixNames = ["ECA", "OAK", Fix] };
        Click(AssertPicker(MenuCatalog.Get(id).Build(routed, Context(), filteredHost), label, MenuPickerDescriptor.FilteredList, []));
        Assert.Null(Assert.Single(filteredHost.FilteredListPopups).Priority);

        var inputHost = new RecordingMenuHost(Fix);
        Click(AssertPicker(MenuCatalog.Get(id).Build(routed, Context(), inputHost), label, MenuPickerDescriptor.Input, []));

        Assert.Equal([(Callsign, $"{command} SUNOL", Initials)], filteredHost.Sent);
        Assert.Equal([(Callsign, $"{command} SUNOL", Initials)], inputHost.Sent);
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

        // Direct to lists SUNOL alone, so it is a one-item list rather than a free-text input: the list branch drops the ellipsis.
        Assert.Equal(["Direct to", "Append direct to…"], navigating.Items.Select(Describe));
    }

    [AvaloniaFact]
    public void Approach_DestinationWithNoApproaches_UsesInputTier()
    {
        TestVnasData.EnsureInitialized();
        Assert.Empty(NavigationDatabase.Instance.GetApproaches(UnknownAirport));
        var host = new RecordingMenuHost(ApproachId);
        var aircraft = new FakeMenuAircraft { Destination = UnknownAirport };

        Click(
            AssertPicker(
                MenuCatalog.Get(MenuIds.ApproachCleared).Build(aircraft, Context(), host),
                "Cleared approach…",
                MenuPickerDescriptor.Input,
                []
            )
        );

        Assert.Equal(["Approach ID"], host.InputPlaceholders);
        Assert.Equal([(Callsign, "CAPP I28R", Initials)], host.Sent);
    }

    /// <summary>KOAK's runways with approaches, each labelled with its approaches' short kinds, in runway order.</summary>
    private static readonly string[] KoakApproachRunways =
    [
        "10L · RNAV",
        "10R · RNAV, VOR",
        "12 · ILS, LOC, RNAV Y, RNP Z",
        "28L · RNAV Y, RNP Z",
        "28R · ILS, LOC, RNAV Y, RNP Z",
        "30 · ILS, LOC, RNAV Y, RNP Z",
    ];

    [AvaloniaFact]
    public void Approach_NoExpectedNoAssigned_OffersEveryRunwayAsATopLevelSubmenu()
    {
        TestVnasData.EnsureInitialized();
        var host = new RecordingMenuHost("");
        var aircraft = new FakeMenuAircraft { Destination = ApproachAirport };

        MenuItem item = AssertPicker(
            MenuCatalog.Get(MenuIds.ApproachCleared).Build(aircraft, Context(), host),
            "Cleared approach",
            MenuPickerDescriptor.Grouped,
            ["10L, 10R, 12, 28L, 28R, 30"]
        );

        Assert.Equal(KoakApproachRunways, item.Items.Select(Describe));
        MenuItem runway28L = item.Items.OfType<MenuItem>().Single(i => i.Header as string == "28L · RNAV Y, RNP Z");
        Assert.Equal(
            ["RNAV [disabled]", "RNAV (GPS) Y RWY 28L — CAPP R28LY", "RNP [disabled]", "RNAV (RNP) Z RWY 28L — CAPP H28LZ"],
            Outline(runway28L)
        );
        Assert.Null(MenuCatalog.BuildApproachOther(MenuIds.ApproachCleared, aircraft, Context(), host));

        Click(runway28L.Items.OfType<MenuItem>().Single(i => i.Header?.ToString() == "RNAV (RNP) Z RWY 28L — CAPP H28LZ"));
        Assert.Equal([(Callsign, "CAPP H28LZ", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void Approach_ExpectedApproach_IsTheDefaultOverTheAssignedRunwaysIls()
    {
        TestVnasData.EnsureInitialized();
        var host = new RecordingMenuHost("");
        var aircraft = new FakeMenuAircraft
        {
            Destination = ApproachAirport,
            AssignedRunway = "30",
            ExpectedApproach = "r28ry",
        };

        MenuItem? item = MenuCatalog.Get(MenuIds.ApproachCleared).Build(aircraft, Context(), host);

        Assert.NotNull(item);
        Assert.Equal("Cleared RNAV Y 28R", item.Header as string);
        Assert.Empty(item.Items);
        Click(item);
        Assert.Equal([(Callsign, "CAPP R28RY", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void Approach_AssignedRunwayWithIls_DefaultsToItsIls()
    {
        TestVnasData.EnsureInitialized();
        var host = new RecordingMenuHost("");
        var aircraft = new FakeMenuAircraft { Destination = ApproachAirport, AssignedRunway = "28R" };

        MenuItem? item = MenuCatalog.Get(MenuIds.ApproachCleared).Build(aircraft, Context(), host);

        Assert.NotNull(item);
        Assert.Equal("Cleared ILS 28R", item.Header as string);
        Click(item);
        Assert.Equal([(Callsign, "CAPP I28R", Initials)], host.Sent);
    }

    /// <summary>KOAK runway 28L has no ILS; its first published approach by kind is the RNAV (GPS) Y, though H28LZ sorts first by id.</summary>
    [AvaloniaFact]
    public void Approach_AssignedRunwayWithoutIls_DefaultsToItsFirstPublishedApproachByKind()
    {
        TestVnasData.EnsureInitialized();
        Assert.DoesNotContain(NavigationDatabase.Instance.GetApproaches(ApproachAirport), a => (a.Runway == "28L") && (a.TypeCode == 'I'));
        var host = new RecordingMenuHost("");
        var aircraft = new FakeMenuAircraft { Destination = ApproachAirport, AssignedRunway = "28L" };

        MenuItem? item = MenuCatalog.Get(MenuIds.ApproachCleared).Build(aircraft, Context(), host);

        Assert.NotNull(item);
        Assert.Equal("Cleared RNAV Y 28L", item.Header as string);
        Click(item);
        Assert.Equal([(Callsign, "CAPP R28LY", Initials)], host.Sent);
    }

    [AvaloniaTheory]
    [InlineData(MenuIds.ApproachCleared, "Cleared ILS 30", "CAPP I30", "Cleared approach (other)…")]
    [InlineData(MenuIds.ApproachJoin, "Join ILS 30", "JAPP I30", "Join approach (other)…")]
    [InlineData(MenuIds.ApproachClearedStraightIn, "Cleared straight-in ILS 30", "CAPPSI I30", "Cleared straight-in (other)…")]
    [InlineData(MenuIds.ApproachJoinStraightIn, "Join straight-in ILS 30", "JAPPSI I30", "Join straight-in (other)…")]
    [InlineData(MenuIds.ApproachClearedForce, "Cleared ILS 30 (force)", "CAPPF I30", "Cleared approach (force) (other)…")]
    [InlineData(MenuIds.ApproachJoinForce, "Join ILS 30 (force)", "JAPPF I30", "Join approach (force) (other)…")]
    [InlineData(MenuIds.ApproachJoinFinalCourse, "Join final course ILS 30", "JFAC I30", "Join final approach course (other)…")]
    [InlineData(MenuIds.ApproachExpect, "Expect ILS 30", "EAPP I30", "Expect approach (other)…")]
    public void Approach_WithADefault_LeafNamesItAndTheOtherPickerFollows(string id, string leaf, string command, string other)
    {
        TestVnasData.EnsureInitialized();
        var host = new RecordingMenuHost("");
        var aircraft = new FakeMenuAircraft { Destination = ApproachAirport, AssignedRunway = "30" };

        MenuItem? item = MenuCatalog.Get(id).Build(aircraft, Context(), host);

        Assert.NotNull(item);
        Assert.Equal(leaf, item.Header as string);
        Assert.Equal(command, MenuCommandText.GetCommand(item));
        Click(item);
        Assert.Equal([(Callsign, command, Initials)], host.Sent);
        AssertPicker(MenuCatalog.BuildApproachOther(id, aircraft, Context(), host), other, MenuPickerDescriptor.Grouped, Koak30Groups);
    }

    /// <summary>The grouped picker's descriptor for a KOAK aircraft assigned runway 30.</summary>
    private static readonly string[] Koak30Groups = ["Runway 30 · assigned: I30, L30, R30-Y, H30-Z", "Other runways: 10L, 10R, 12, 28L, 28R"];

    [AvaloniaFact]
    public void Approach_RnpDefault_LeafNamesItRnav()
    {
        TestVnasData.EnsureInitialized();
        var host = new RecordingMenuHost("");
        var aircraft = new FakeMenuAircraft { Destination = ApproachAirport, ExpectedApproach = "H28LZ" };

        MenuItem? leaf = MenuCatalog.Get(MenuIds.ApproachCleared).Build(aircraft, Context(), host);
        MenuItem? other = MenuCatalog.BuildApproachOther(MenuIds.ApproachCleared, aircraft, Context(), host);

        Assert.Equal("Cleared RNAV Z 28L", leaf?.Header as string);
        Assert.NotNull(other);
        Assert.Contains("RNP [disabled]", Outline(other));
        Assert.Contains("RNAV (RNP) Z RWY 28L · expected — CAPP H28LZ", Outline(other));
        Assert.Contains("28R · ILS, LOC, RNAV Y, RNP Z", other.Items.Select(DescribeRow));
    }

    [AvaloniaFact]
    public void Approach_ExpectedApproachInShorthand_ResolvesToTheDestinationsApproach()
    {
        TestVnasData.EnsureInitialized();
        var host = new RecordingMenuHost("");
        var aircraft = new FakeMenuAircraft { Destination = ApproachAirport, ExpectedApproach = "R28L" };

        MenuItem? leaf = MenuCatalog.Get(MenuIds.ApproachCleared).Build(aircraft, Context(), host);
        MenuItem? other = MenuCatalog.BuildApproachOther(MenuIds.ApproachCleared, aircraft, Context(), host);

        Assert.Equal("Cleared RNAV Y 28L", leaf?.Header as string);
        Assert.NotNull(other);
        Assert.Contains("RNAV (GPS) Y RWY 28L · expected — CAPP R28LY", Outline(other));
    }

    [AvaloniaFact]
    public void Approach_ExpectedApproachNotTheDestinations_FallsThroughToTheAssignedRunway()
    {
        TestVnasData.EnsureInitialized();
        Assert.DoesNotContain(NavigationDatabase.Instance.GetApproaches(ApproachAirport), a => a.ApproachId == "I33");
        var host = new RecordingMenuHost("");
        var aircraft = new FakeMenuAircraft
        {
            Destination = ApproachAirport,
            AssignedRunway = "28R",
            ExpectedApproach = "I33",
        };

        MenuItem? leaf = MenuCatalog.Get(MenuIds.ApproachCleared).Build(aircraft, Context(), host);
        MenuItem? other = MenuCatalog.BuildApproachOther(MenuIds.ApproachCleared, aircraft, Context(), host);

        Assert.Equal("Cleared ILS 28R", leaf?.Header as string);
        Assert.NotNull(other);
        Assert.DoesNotContain(Outline(other), line => line.Contains("expected", StringComparison.Ordinal));
    }

    /// <summary>KOAK runway 33 has no approach: with no expected approach there is no default, and every runway is a top-level submenu.</summary>
    [AvaloniaFact]
    public void Approach_AssignedRunwayWithoutApproaches_HasNoDefaultAndEveryRunwayAtTheTop()
    {
        TestVnasData.EnsureInitialized();
        Assert.DoesNotContain(NavigationDatabase.Instance.GetApproaches(ApproachAirport), a => a.Runway == "33");
        var host = new RecordingMenuHost("");
        var aircraft = new FakeMenuAircraft { Destination = ApproachAirport, AssignedRunway = "33" };

        MenuItem item = AssertPicker(
            MenuCatalog.Get(MenuIds.ApproachCleared).Build(aircraft, Context(), host),
            "Cleared approach",
            MenuPickerDescriptor.Grouped,
            ["10L, 10R, 12, 28L, 28R, 30"]
        );

        Assert.Equal(KoakApproachRunways, item.Items.Select(Describe));
        Assert.Null(MenuCatalog.BuildApproachOther(MenuIds.ApproachCleared, aircraft, Context(), host));
    }

    [AvaloniaFact]
    public void ApproachOther_AssignedRunwayWithoutApproaches_ListsEveryRunwayAtTheTop()
    {
        TestVnasData.EnsureInitialized();
        var host = new RecordingMenuHost("");
        var aircraft = new FakeMenuAircraft
        {
            Destination = ApproachAirport,
            AssignedRunway = "33",
            ExpectedApproach = "I30",
        };

        MenuItem? leaf = MenuCatalog.Get(MenuIds.ApproachCleared).Build(aircraft, Context(), host);
        MenuItem other = AssertPicker(
            MenuCatalog.BuildApproachOther(MenuIds.ApproachCleared, aircraft, Context(), host),
            "Cleared approach (other)…",
            MenuPickerDescriptor.Grouped,
            ["10L, 10R, 12, 28L, 28R, 30"]
        );

        Assert.Equal("Cleared ILS 30", leaf?.Header as string);
        Assert.Equal(KoakApproachRunways, other.Items.Select(Describe));
    }

    /// <summary>KLVK has approaches to runway 25R only: its default runway's group stands alone, with no "Other runways".</summary>
    [AvaloniaFact]
    public void ApproachOther_SingleRunwayAirport_HasNoOtherRunways()
    {
        TestVnasData.EnsureInitialized();
        Assert.All(NavigationDatabase.Instance.GetApproaches("KLVK"), a => Assert.Equal("25R", a.Runway));
        var host = new RecordingMenuHost("");
        var aircraft = new FakeMenuAircraft { Destination = "KLVK", AssignedRunway = "25R" };

        MenuItem other = AssertPicker(
            MenuCatalog.BuildApproachOther(MenuIds.ApproachCleared, aircraft, Context(), host),
            "Cleared approach (other)…",
            MenuPickerDescriptor.Grouped,
            ["Runway 25R · assigned: I25R, L25R, R25R"]
        );

        Assert.Equal(
            [
                "Runway 25R · assigned [disabled]",
                "ILS [disabled]",
                "ILS RWY 25R — CAPP I25R",
                "LOC [disabled]",
                "LOC RWY 25R — CAPP L25R",
                "RNAV [disabled]",
                "RNAV (GPS) RWY 25R — CAPP R25R",
            ],
            Outline(other)
        );
    }

    /// <summary>Approaches that name no runway sit last, in a Circling submenu, titled by their id prefix and variant.</summary>
    [AvaloniaTheory]
    [InlineData("KVNY", "Circling · LDA-C, VOR-A, VOR-B", "LDA-C — CAPP LDA-C")]
    [InlineData("KAVX", "Circling · VOR-A, VOR/DME-B", "VOR/DME-B — CAPP VDM-B")]
    [InlineData("KAAS", "Circling · VOR/DME-A", "VOR/DME-A — CAPP VDM-A")]
    [InlineData("KMFR", "Circling · RNAV-D, LOC BC-B, VOR/DME-C", "LOC BC-B — CAPP LBC-B")]
    public void Approach_CirclingApproaches_SitLastInACirclingSubmenu(string airport, string label, string row)
    {
        TestVnasData.EnsureInitialized();
        var host = new RecordingMenuHost("");
        var aircraft = new FakeMenuAircraft { Destination = airport };

        MenuItem? item = MenuCatalog.Get(MenuIds.ApproachCleared).Build(aircraft, Context(), host);

        Assert.NotNull(item);
        MenuItem circling = Assert.IsType<MenuItem>(item.Items[^1]);
        Assert.Equal(label, circling.Header as string);
        Assert.Contains(row, Outline(circling));
        Assert.EndsWith("Circling", Assert.IsType<MenuPickerDescriptor>(item.Tag).Items[0], StringComparison.Ordinal);
    }

    /// <summary>A circling approach id splits into its kind, its full published kind and its variant letter.</summary>
    [Theory]
    [InlineData("RNVA", "RNAV", "RNAV (GPS)", "A")]
    [InlineData("GPS-A", "GPS", "GPS", "A")]
    [InlineData("VDM-B", "VOR/DME", "VOR/DME", "B")]
    public void CirclingApproach_KindFullKindAndVariant_FromItsId(string id, string kind, string fullKind, string variant) =>
        Assert.Equal((kind, fullKind, variant), ApproachPickerBuilder.DescribeCirclingApproach(id));

    [AvaloniaFact]
    public void ApproachOther_GroupsTheAssignedRunwayByKindThenTheOtherRunways()
    {
        TestVnasData.EnsureInitialized();
        var host = new RecordingMenuHost("");
        var aircraft = new FakeMenuAircraft
        {
            Destination = ApproachAirport,
            AssignedRunway = "30",
            ExpectedApproach = "I30",
        };

        MenuItem? other = MenuCatalog.BuildApproachOther(MenuIds.ApproachJoin, aircraft, Context(), host);

        Assert.NotNull(other);
        Assert.Equal(
            [
                "Runway 30 · assigned [disabled]",
                "ILS [disabled]",
                "ILS RWY 30 · expected — JAPP I30",
                "LOC [disabled]",
                "LOC RWY 30 — JAPP L30",
                "RNAV [disabled]",
                "RNAV (GPS) Y RWY 30 — JAPP R30-Y",
                "RNP [disabled]",
                "RNAV (RNP) Z RWY 30 — JAPP H30-Z",
                "---",
                "Other runways [disabled]",
                .. KoakApproachRunways[..^1],
            ],
            other.Items.Select(DescribeRow)
        );

        Click(other.Items.OfType<MenuItem>().Single(i => i.Header?.ToString() == "RNAV (RNP) Z RWY 30 — JAPP H30-Z"));
        Assert.Equal([(Callsign, "JAPP H30-Z", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void ApproachOther_AssignedRunwaysIlsDefault_CarriesNoExpectedBadge()
    {
        TestVnasData.EnsureInitialized();
        var host = new RecordingMenuHost("");
        var aircraft = new FakeMenuAircraft { Destination = ApproachAirport, AssignedRunway = "30" };

        MenuItem? other = MenuCatalog.BuildApproachOther(MenuIds.ApproachCleared, aircraft, Context(), host);

        Assert.NotNull(other);
        Assert.Contains("ILS RWY 30 — CAPP I30", other.Items.Select(DescribeRow));
        Assert.DoesNotContain(Outline(other), line => line.Contains("expected", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void ApproachOther_DefaultRunwayFromTheExpectedApproach_IsNotMarkedAssigned()
    {
        TestVnasData.EnsureInitialized();
        var host = new RecordingMenuHost("");
        var aircraft = new FakeMenuAircraft { Destination = ApproachAirport, ExpectedApproach = "I12" };

        MenuItem? leaf = MenuCatalog.Get(MenuIds.ApproachCleared).Build(aircraft, Context(), host);
        MenuItem? other = MenuCatalog.BuildApproachOther(MenuIds.ApproachCleared, aircraft, Context(), host);

        Assert.Equal("Cleared ILS 12", leaf?.Header as string);
        Assert.NotNull(other);
        Assert.Equal("Runway 12 [disabled]", DescribeRow(other.Items[0]));
        Assert.Equal("ILS RWY 12 · expected — CAPP I12", DescribeRow(other.Items[2]));
        Assert.Contains("28R · ILS, LOC, RNAV Y, RNP Z", other.Items.Select(DescribeRow));
        Assert.DoesNotContain("12 · ILS, LOC, RNAV Y, RNP Z", other.Items.Select(DescribeRow));
    }

    [AvaloniaFact]
    public void ApproachGroup_PutsEachPickersOtherCompanionAfterItsDefaultLeaf()
    {
        TestVnasData.EnsureInitialized();
        var host = new RecordingMenuHost("");
        var aircraft = new FakeMenuAircraft { Destination = ApproachAirport, AssignedRunway = "30" };

        MenuItem approach = SharedMenuGroups.Approach(aircraft, Context(), host);

        Assert.Equal(
            [
                "Cleared ILS 30",
                "Cleared approach (other)…",
                "Join ILS 30",
                "Join approach (other)…",
                "Cleared straight-in ILS 30",
                "Cleared straight-in (other)…",
                "Join straight-in ILS 30",
                "Join straight-in (other)…",
                "Cleared ILS 30 (force)",
                "Cleared approach (force) (other)…",
                "Join ILS 30 (force)",
                "Join approach (force) (other)…",
                "Join final course ILS 30",
                "Join final approach course (other)…",
                "Expect ILS 30",
                "Expect approach (other)…",
            ],
            approach.Items.Select(Describe).Take(16)
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
            new MenuRichRow("", "AAL601 · B738", "", MenuRichRowKind.Traffic, "RTIS AAL601", null, ["2 o'clock", "4 nm", "1,500 below"]),
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
        Click(item);

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
            ["Turning base", "Turning final", "Turning crosswind", "Turning downwind", "N-mile final…", "At fix…", "---", "Stop reporting"],
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

    [AvaloniaFact]
    public void CrossFix_OneRouteFix_DirectItemPlusOther()
    {
        var host = new RecordingMenuHost("ECA");
        var aircraft = new FakeMenuAircraft { RouteFixes = [Fix] };

        MenuItem? item = MenuCatalog.Get(MenuIds.ProceduresCrossFix).Build(aircraft, Context(), host);
        Assert.NotNull(item);
        Assert.Equal("Cross fix SUNOL", item.Header as string);
        Assert.Null(item.Tag);
        Click(item);
        Click(AssertPicker(MenuCatalog.BuildCrossFixOther(aircraft, Context(), host), "Cross fix (other)…", MenuPickerDescriptor.Input, []));

        Assert.Equal(["Fix name"], host.InputPlaceholders);
        Assert.Equal([(Callsign, "CFIX SUNOL", Initials), (Callsign, "CFIX ECA", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void CrossFix_NoRouteFixes_UsesInputTier()
    {
        var host = new RecordingMenuHost(Fix);
        var aircraft = new FakeMenuAircraft();

        Click(
            AssertPicker(MenuCatalog.Get(MenuIds.ProceduresCrossFix).Build(aircraft, Context(), host), "Cross fix…", MenuPickerDescriptor.Input, [])
        );

        Assert.Equal(["Fix name"], host.InputPlaceholders);
        Assert.Equal([(Callsign, "CFIX SUNOL", Initials)], host.Sent);
        Assert.Null(MenuCatalog.BuildCrossFixOther(aircraft, Context(), host));
    }

    [AvaloniaFact]
    public void JoinAirway_TwoFiledAirways_UsesListPlusOther()
    {
        TestVnasData.EnsureInitialized();
        Assert.True(NavigationDatabase.Instance.IsAirway("V25"));
        Assert.True(NavigationDatabase.Instance.IsAirway("J80"));
        var host = new RecordingMenuHost("J80") { InputAnswer = "J6" };
        var aircraft = new FakeMenuAircraft { Route = "OAK V25 SUNOL J80 ECA V25" };

        MenuItem? item = MenuCatalog.Get(MenuIds.ProceduresJoinAirway).Build(aircraft, Context(), host);
        Click(AssertPicker(item, "Join airway…", MenuPickerDescriptor.List, ["V25", "J80"]));
        Click(AssertPicker(MenuCatalog.BuildJoinAirwayOther(aircraft, Context(), host), "Join airway (other)…", MenuPickerDescriptor.Input, []));

        Assert.Equal<object?>("V25", Assert.Single(host.ListPopups).Selected);
        Assert.Equal(["Airway ID"], host.InputPlaceholders);
        Assert.Equal([(Callsign, "JAWY J80", Initials), (Callsign, "JAWY J6", Initials)], host.Sent);
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
        Click(item);
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

    [AvaloniaTheory]
    [InlineData(MenuIds.ProceduresJoinRadialOutbound, "Join radial outbound…", "Bearing from SUNOL (0-360)", "JRADO SUNOL 090")]
    [InlineData(MenuIds.ProceduresJoinRadialInbound, "Join radial inbound…", "Bearing to SUNOL (0-360)", "JRADI SUNOL 090")]
    public void JoinRadial_WithFixNames_PicksFixThenAsksBearing(string id, string label, string prompt, string command)
    {
        var host = new RecordingMenuHost(Fix) { FixNames = ["ECA", "OAK", Fix], InputAnswer = "090" };

        MenuItem? item = MenuCatalog.Get(id).Build(null, Context(), host);
        Click(AssertPicker(item, label, MenuPickerDescriptor.FilteredList, []));

        (string[] Names, IReadOnlyList<string>? Priority) popup = Assert.Single(host.FilteredListPopups);
        Assert.Equal(["ECA", "OAK", Fix], popup.Names);
        Assert.Null(popup.Priority);
        Assert.Equal([prompt], host.InputPlaceholders);
        Assert.Equal([(Callsign, command, Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void JoinRadialInbound_WithoutFixNames_SendsInputVerbatim()
    {
        var host = new RecordingMenuHost("SUNOL 270");

        MenuItem? item = MenuCatalog.Get(MenuIds.ProceduresJoinRadialInbound).Build(null, Context(), host);
        Click(AssertPicker(item, "Join radial inbound…", MenuPickerDescriptor.Input, []));

        Assert.Empty(host.FilteredListPopups);
        Assert.Equal(["FIX bearing"], host.InputPlaceholders);
        Assert.Equal([(Callsign, "JRADI SUNOL 270", Initials)], host.Sent);
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
            { "Default (SID/on course)", "CTO" },
            { "Fly runway heading", "CTO RH" },
            { "Fly on course", "CTO OC" },
            { "Make left traffic", "CTO MLT" },
            { "Make right traffic", "CTO MRT" },
            { "Turn left crosswind", "CTO MLC" },
            { "Turn right crosswind", "CTO MRC" },
            { "Turn left downwind", "CTO MLD" },
            { "Turn right downwind", "CTO MRD" },
            { "Left 270", "CTO ML270" },
            { "Right 270", "CTO MR270" },
            { "360 overhead", "CTO 360" },
        };

    [AvaloniaTheory]
    [MemberData(nameof(ClearedForTakeoffChildren))]
    public void Tower_Cto_ChildrenSendTheirArguments(string label, string command)
    {
        var host = new RecordingMenuHost("");

        List<MenuItem> children = ClearedForTakeoffChildItems(host);

        Click(Assert.Single(children, m => m.Header as string == label));
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
        Click(item);

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
        Click(item);

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
        Click(item);

        Assert.Equal([(Callsign, "CROSS 28R", Initials)], host.Sent);
    }

    /// <summary>Off a hold-short with no runway to cross next, Cross builds nothing rather than naming the assigned runway.</summary>
    [AvaloniaFact]
    public void GroundCrossRunway_OffAHoldShortWithNoCrossingAhead_BuildsNothing()
    {
        AircraftModel ac = OnGround("Taxiing", "IFR", "28L");

        Assert.Null(MenuCatalog.Get(MenuIds.GroundCrossRunway).Build(ac, Context(VfrCommandsForIfr.None), new RecordingMenuHost("")));
    }

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
        Assert.Equal(
            [
                "Default (SID/on course)",
                "Fly runway heading",
                "Fly on course",
                "Make left traffic",
                "Make right traffic",
                "Turn left crosswind",
                "Turn right crosswind",
                "Turn left downwind",
                "Turn right downwind",
                "Left 270",
                "Right 270",
                "360 overhead",
                "---",
                "Custom…",
            ],
            cto.Items.Select(Describe)
        );

        foreach (MenuItem child in cto.Items.OfType<MenuItem>().Where(i => (i.Header as string) != "Custom…"))
        {
            Click(child);
        }

        Assert.Equal(
            ["CTO", "CTO RH", "CTO OC", "CTO MLT", "CTO MRT", "CTO MLC", "CTO MRC", "CTO MLD", "CTO MRD", "CTO ML270", "CTO MR270", "CTO 360"],
            host.Sent.Select(s => s.Command)
        );
    }

    // IFR without the VFR set: the default clearance and an explicit runway heading, then the separator and Custom.
    [AvaloniaFact]
    public void Cto_IfrDeparture_OffersDefaultAndRunwayHeading()
    {
        AircraftModel ac = OnGround("LinedUpAndWaiting", "IFR", "30");

        MenuItem cto = CtoSubmenu(ac, new RecordingMenuHost(""));
        Assert.Equal("Cleared for takeoff 30", cto.Header as string);
        Assert.Equal(["Default (SID/on course)", "Fly runway heading", "---", "Custom…"], cto.Items.Select(Describe));
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
    // stationary holds resume as well.
    [AvaloniaTheory]
    [InlineData("Holding Short 28R/10L", true, "", true)]
    [InlineData("Holding Short 28R/10L", false, "", false)]
    [InlineData("Holding In Position", false, "HP", true)]
    [InlineData("Holding In Position", false, "", false)]
    public void ResumeTaxi_OnlyWhereTheSimAcceptsRes(string phase, bool hasActiveTaxiRoute, string holdKind, bool offered)
    {
        AircraftModel ac = OnGround(phase, "IFR", "28R");
        ac.HasActiveTaxiRoute = hasActiveTaxiRoute;
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
        Assert.Equal(["Enter straight-in final…"], enterFinalOnly.Items.Select(Describe));
        Assert.Null(none);
    }

    [AvaloniaTheory]
    [InlineData(ApproachAirport, UnknownAirport, MenuPickerDescriptor.List)]
    [InlineData(UnknownAirport, ApproachAirport, MenuPickerDescriptor.Input)]
    public void PatternEntry_RunwaysComeFromTheDestinationWheneverOneIsFiled(string destination, string departure, string kind)
    {
        TestVnasData.EnsureInitialized();
        Assert.Empty(RunwayDesignators.ForAirport(UnknownAirport));
        string[] runways = kind == MenuPickerDescriptor.List ? [.. RunwayDesignators.ForAirport(ApproachAirport)] : [];
        var host = new RecordingMenuHost("28R");
        var aircraft = new FakeMenuAircraft { Destination = destination, Departure = departure };

        MenuItem? item = MenuCatalog.Get(MenuIds.PatternEnterLeftDownwind).Build(aircraft, Context(), host);
        Click(AssertPicker(item, "Enter left downwind…", kind, runways));

        Assert.Equal([(Callsign, "ELD 28R", Initials)], host.Sent);
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
        Assert.Equal("Enter straight-in final 9L", item.Header as string);
        Assert.Null(item.Tag);
        Click(item);

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
        Click(AssertPicker(list, "Enter right base…", MenuPickerDescriptor.List, runways));
        Assert.Null(MenuCatalog.BuildPatternEntryOther(MenuIds.PatternEnterRightBase, noDefault, Context(), host));

        Assert.Equal("Enter right base 30", MenuCatalog.Get(MenuIds.PatternEnterRightBase).Build(withDefault, Context(), host)?.Header as string);
        MenuItem? other = MenuCatalog.BuildPatternEntryOther(MenuIds.PatternEnterRightBase, withDefault, Context(), host);
        Click(AssertPicker(other, "Enter right base (other)…", MenuPickerDescriptor.List, runways));

        Assert.All(host.ListPopups, popup => Assert.Equal<object?>(runways[0], popup.Selected));
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

    /// <summary>Every item under <paramref name="item"/>, as <see cref="DescribeRow"/> prints it, depth first.</summary>
    private static List<string> Outline(MenuItem item)
    {
        List<string> lines = [];
        foreach (object? child in item.Items)
        {
            lines.Add(DescribeRow(child));
            if (child is MenuItem { Items.Count: > 0 } submenu)
            {
                lines.AddRange(Outline(submenu));
            }
        }

        return lines;
    }

    /// <summary>
    /// A minimal <see cref="IMenuAircraft"/>: never live traffic, with the warp seed values, the assignments the flight
    /// group headers show, the type the speed items read and the route fixes the navigation pickers offer settable.
    /// </summary>
    private sealed class FakeMenuAircraft : IMenuAircraft
    {
        public string Callsign => MenuCatalogCommandTests.Callsign;

        public bool HasActiveTaxiRoute { get; init; }

        public bool HasCfrWindow { get; init; }

        public bool IsHeldForRelease { get; init; }

        public bool IsLiveTraffic => false;

        public bool AssumedFromLiveTraffic => false;

        public bool IsOnGround { get; init; }

        public string CurrentTaxiway { get; init; } = "";

        public string ParkingSpot { get; init; } = "";

        public string? GroundAirportId { get; init; }

        public LatLon Position { get; init; }

        public string? LastReportedTrafficCallsign { get; init; }

        public bool IsHeld => false;

        public string? HoldKind => null;

        public string? HoldYieldTarget => null;

        public string? AutoYieldTarget => null;

        public bool AutoYieldIsFollowing => false;

        public bool IsDelayed => false;

        public bool HasQueuedPatternEntry => false;

        public string FlightRules => "IFR";

        public string CurrentPhase { get; init; } = "";

        public string AssignedRunway { get; init; } = "";

        public string PhaseSequence => "";

        public string LandingClearance => "";

        public string PendingLandingClearance => "";

        public double HeadingDegrees { get; init; }

        public double AltitudeFeet { get; init; }

        public double IndicatedAirspeedKnots { get; init; }

        public double GroundSpeedKnots { get; init; }

        public string NavigatingTo { get; init; } = "";

        public IReadOnlyList<string> NavigationRoute { get; init; } = [];

        public MagneticHeading? AssignedHeading { get; init; }

        public double? AssignedAltitude { get; init; }

        public double? AssignedSpeed { get; init; }

        public string FiledAircraftType { get; init; } = "";

        public string DisplayAircraftType => FiledAircraftType;

        public string Note => "";

        public string Destination { get; init; } = "";

        public string Departure { get; init; } = "";

        public string Route { get; init; } = "";

        public string ActiveSidId { get; init; } = "";

        public string ActiveStarId { get; init; } = "";

        public string? ActiveApproachId { get; init; }

        public string? ExpectedApproach { get; init; }

        public string? Owner { get; init; }

        public string? OwnerSectorCode { get; init; }

        public string? HandoffPeer { get; init; }

        public string? HandoffPeerSectorCode { get; init; }

        public string? PointoutStatus { get; init; }

        public AircraftSituation Situation { get; init; }

        public SituationFlags SituationFlags { get; init; }

        public string? NextCrossingRunway { get; init; }

        public IReadOnlyList<string> RouteFixes { get; init; } = [];

        public IReadOnlyList<string> RouteFixNames() => RouteFixes;
    }
}
