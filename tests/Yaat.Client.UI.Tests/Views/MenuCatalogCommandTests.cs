using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Testing;
using CatalogMenuView = Yaat.Client.ContextMenus.MenuView;

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
        (MenuIds.CoordinationCheckReleaseWindow, "", "CFR CHECK"),
        (MenuIds.DataBlockScratchpad, BlockText, $"SP {BlockText}"),
        (MenuIds.DataBlockNote, BlockText, $"NOTE {BlockText}"),
        (MenuIds.DataBlockTempAltitude, "050", "TEMPALT 50"),
        (MenuIds.DataBlockCruise, "050", "CRUISE 50"),
        (MenuIds.DataBlockAnnotate, "", "ANNOTATE"),
        (MenuIds.SimControlDelete, "", "DEL"),
        (MenuIds.DisplayBlank, "", "BLANK"),
        (MenuIds.DisplayUnblank, "", "BLANKD"),
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
        (MenuIds.GroundPushback, "", "PUSH"),
        (MenuIds.GroundHoldPosition, "", "HP"),
        (MenuIds.GroundResumeTaxi, "", "RES"),
        (MenuIds.GroundBreakConflict, "", "BREAK"),
        (MenuIds.SpawnNow, "", "SPAWN"),
    ];

    /// <summary>
    /// The entries whose item the host builds rather than the catalog: a popup, an editor, a submenu the catalog
    /// assembles, or a header the host's own state decides; and the entries whose item needs the aircraft or the
    /// previous selection (the held runway, the relative ground items), which their own tests click.
    /// </summary>
    private static readonly string[] HostBuiltIds =
    [
        MenuIds.FavoritesMenu,
        MenuIds.LiveTrafficAssumeAndTrack,
        MenuIds.SimControlWarp,
        MenuIds.AircraftEditFlightPlan,
        MenuIds.DisplayMiniDataBlock,
        MenuIds.DisplayResetDataBlockPosition,
        MenuIds.DisplayNavRoute,
        MenuIds.DisplayMeasure,
        MenuIds.DisplayLeaderDirection,
        MenuIds.DisplayJRing,
        MenuIds.DisplayCone,
        MenuIds.DisplayTaxiRoute,
        MenuIds.DisplayHideDataBlock,
        MenuIds.NavigationDrawRoute,
        MenuIds.TowerClearedForTakeoff,
        MenuIds.GroundCrossRunway,
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

    private static MenuContext Context() => RadarContext(VfrCommandsForIfr.EnterFinalOnly);

    /// <summary>A radar menu context under <paramref name="mode"/>, outside solo training, with no previous selection.</summary>
    private static MenuContext RadarContext(VfrCommandsForIfr mode) => new(Callsign, Initials, null, false, mode, CatalogMenuView.Radar);

    /// <summary>A ground-view menu context under <paramref name="mode"/>, outside solo training, with no previous selection.</summary>
    private static MenuContext GroundContext(VfrCommandsForIfr mode) => new(Callsign, Initials, null, false, mode, CatalogMenuView.Ground);

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

        SharedMenuGroups.AddAssumeSelected(menu, ["SWA1", "SWA2"], ListContext(VfrCommandsForIfr.EnterFinalOnly), host);

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

        SharedMenuGroups.AddAssumeSelected(menu, ["SWA1", "SWA2", "SWA3"], ListContext(VfrCommandsForIfr.EnterFinalOnly), host);

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

        SharedMenuGroups.AddAssumeSelected(menu, ["SWA1"], ListContext(VfrCommandsForIfr.EnterFinalOnly), host);

        Assert.Empty(menu.Items);
        Assert.Empty(host.AssumeSelectedCalls);
    }

    [AvaloniaTheory]
    [InlineData(CatalogMenuView.Radar, "ID")]
    [InlineData(CatalogMenuView.List, "IDENT")]
    public void SquawkGroup_Ident_SendsTheViewsCommand(CatalogMenuView view, string command)
    {
        var host = new RecordingMenuHost("");
        MenuItem squawk = SharedMenuGroups.Squawk(null, Context(), host, view);
        MenuItem ident = Assert.Single(squawk.Items.OfType<MenuItem>(), i => (i.Header as string) == "Ident");

        Click(ident);

        Assert.Equal([(Callsign, command, Initials)], host.Sent);
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

        Assert.Equal([(Callsign, 90, 5000, 250)], host.WarpPopups);

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

        Assert.Equal([(Callsign, 360, 0, 0)], host.WarpPopups);
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
    [InlineData(false, "Mini datablock")]
    [InlineData(true, "Full datablock")]
    public void MiniDatablock_LabelFollowsHostState_AndClickToggles(bool minified, string header)
    {
        var host = new RecordingMenuHost("");
        if (minified)
        {
            host.MinifiedCallsigns.Add(Callsign);
        }

        MenuItem? item = MenuCatalog.Get(MenuIds.DisplayMiniDataBlock).Build(null, Context(), host);

        Assert.NotNull(item);
        Assert.Equal(header, item.Header as string);

        Click(item);

        Assert.Equal([Callsign], host.MinifiedToggles);
    }

    [AvaloniaFact]
    public void ResetDatablockPosition_ShownOnlyWithManualOffset()
    {
        var host = new RecordingMenuHost("");
        Assert.Null(MenuCatalog.Get(MenuIds.DisplayResetDataBlockPosition).Build(null, Context(), host));

        host.ManualOffsetCallsigns.Add(Callsign);
        MenuItem? item = MenuCatalog.Get(MenuIds.DisplayResetDataBlockPosition).Build(null, Context(), host);

        Assert.NotNull(item);
        Assert.Equal("Reset to student position", item.Header as string);

        Click(item);

        Assert.Equal([Callsign], host.DataBlockOffsetResets);
    }

    [AvaloniaTheory]
    [InlineData(false, "Show nav route")]
    [InlineData(true, "Hide nav route")]
    public void NavRoute_LabelFollowsHostState_AndClickToggles(bool shown, string header)
    {
        var host = new RecordingMenuHost("");
        if (shown)
        {
            host.PathShownCallsigns.Add(Callsign);
        }

        MenuItem? item = MenuCatalog.Get(MenuIds.DisplayNavRoute).Build(null, Context(), host);

        Assert.NotNull(item);
        Assert.Equal(header, item.Header as string);

        Click(item);

        Assert.Equal([Callsign], host.PathToggles);
    }

    [AvaloniaFact]
    public void Measure_HiddenWithoutMeasure_FromWithoutAnchor_ToWithAnchor_AndClickPicks()
    {
        var host = new RecordingMenuHost("") { MeasureState = MenuMeasureState.None };
        Assert.Null(MenuCatalog.Get(MenuIds.DisplayMeasure).Build(null, Context(), host));

        host.MeasureState = MenuMeasureState.NoAnchor;
        MenuItem? from = MenuCatalog.Get(MenuIds.DisplayMeasure).Build(null, Context(), host);
        Assert.NotNull(from);
        Assert.Equal($"Measure from {Callsign}", from.Header as string);

        host.MeasureState = MenuMeasureState.HasAnchor;
        MenuItem? to = MenuCatalog.Get(MenuIds.DisplayMeasure).Build(null, Context(), host);
        Assert.NotNull(to);
        Assert.Equal($"Measure to {Callsign}", to.Header as string);

        Click(from);
        Click(to);

        Assert.Equal([Callsign, Callsign], host.MeasurePicks);
    }

    [AvaloniaFact]
    public void LeaderDirection_SubmenuSendsLdr_WithDefaultLabelOnFive()
    {
        var host = new RecordingMenuHost("");
        List<MenuItem> items = SubmenuItems(MenuIds.DisplayLeaderDirection, host);

        Assert.Equal(["1", "2", "3", "4", "5 (default)", "6", "7", "8", "9"], items.Select(i => i.Header as string));
        foreach (MenuItem item in items)
        {
            Click(item);
        }

        Assert.Equal(
            [
                (Callsign, "LDR 1", Initials),
                (Callsign, "LDR 2", Initials),
                (Callsign, "LDR 3", Initials),
                (Callsign, "LDR 4", Initials),
                (Callsign, "LDR 5", Initials),
                (Callsign, "LDR 6", Initials),
                (Callsign, "LDR 7", Initials),
                (Callsign, "LDR 8", Initials),
                (Callsign, "LDR 9", Initials),
            ],
            host.Sent
        );
    }

    [AvaloniaFact]
    public void Display_AllStatesOn_HeaderSequence()
    {
        var host = new RecordingMenuHost("") { MeasureState = MenuMeasureState.HasAnchor };
        host.MinifiedCallsigns.Add(Callsign);
        host.ManualOffsetCallsigns.Add(Callsign);
        host.PathShownCallsigns.Add(Callsign);

        MenuItem display = SharedMenuGroups.Display(null, Context(), host);

        Assert.Equal(
            [
                "Full datablock",
                "Reset to student position",
                "Hide nav route",
                $"Measure to {Callsign}",
                "---",
                "Leader direction",
                "J-ring",
                "Cone",
                "---",
                "Blank target",
                "Unblank target",
            ],
            display.Items.Select(Describe)
        );
    }

    [AvaloniaFact]
    public void GroundDisplayGroup_TaxiRouteRadio_ReflectsAndSetsHostMode()
    {
        var host = new RecordingMenuHost("");
        host.TaxiRouteModes[Callsign] = TaxiRouteDisplayMode.AlwaysHide;
        var menu = new ContextMenu();

        SharedMenuGroups.AddGroundDisplay(menu.Items, null, GroundContext(VfrCommandsForIfr.None), host);

        Assert.Equal(["Taxi route", "Hide datablock"], menu.Items.Select(Describe));
        MenuItem taxiRoute = menu.Items.OfType<MenuItem>().First();
        List<MenuItem> modes = [.. taxiRoute.Items.OfType<MenuItem>()];
        Assert.Equal(["Always show", "Always hide", "Follow “Show all” setting"], modes.Select(i => i.Header as string));
        Assert.All(modes, i => Assert.Equal(MenuItemToggleType.Radio, i.ToggleType));
        Assert.Equal([false, true, false], modes.Select(i => i.IsChecked));

        foreach (MenuItem mode in modes)
        {
            Click(mode);
        }

        Assert.Equal(
            [(Callsign, TaxiRouteDisplayMode.AlwaysShow), (Callsign, TaxiRouteDisplayMode.AlwaysHide), (Callsign, TaxiRouteDisplayMode.Follow)],
            host.TaxiRouteModeSets
        );
        Assert.Empty(host.Sent);
    }

    [AvaloniaTheory]
    [InlineData(false, "Hide datablock")]
    [InlineData(true, "Show datablock")]
    public void GroundDisplayGroup_HideDatablock_TogglesThroughHost(bool hidden, string header)
    {
        var host = new RecordingMenuHost("");
        if (hidden)
        {
            host.HiddenDataBlockCallsigns.Add(Callsign);
        }

        host.ManualOffsetCallsigns.Add(Callsign);
        var menu = new ContextMenu();

        SharedMenuGroups.AddGroundDisplay(menu.Items, null, GroundContext(VfrCommandsForIfr.None), host);

        Assert.Equal(["Taxi route", header, "Reset datablock position"], menu.Items.Select(Describe));
        MenuItem toggle = menu.Items.OfType<MenuItem>().Single(i => (i.Header as string) == header);

        Click(toggle);

        Assert.Equal([Callsign], host.HiddenDataBlockToggles);
        Assert.Empty(host.Sent);
    }

    [AvaloniaFact]
    public void JRing_SubmenuSendsClearAndRadii() => AssertRingSubmenu(MenuIds.DisplayJRing, "JRING");

    [AvaloniaFact]
    public void Cone_SubmenuSendsClearAndRadii() => AssertRingSubmenu(MenuIds.DisplayCone, "CONE");

    /// <summary>Clicks the Clear item and every radius of a J-ring or cone submenu and checks each command text.</summary>
    private static void AssertRingSubmenu(string id, string command)
    {
        var host = new RecordingMenuHost("");
        List<MenuItem> items = SubmenuItems(id, host);

        Assert.Equal(["Clear", "1 nm", "2 nm", "3 nm", "5 nm", "10 nm"], items.Select(i => i.Header as string));
        foreach (MenuItem item in items)
        {
            Click(item);
        }

        Assert.Equal(
            [
                (Callsign, command, Initials),
                (Callsign, $"{command} 1", Initials),
                (Callsign, $"{command} 2", Initials),
                (Callsign, $"{command} 3", Initials),
                (Callsign, $"{command} 5", Initials),
                (Callsign, $"{command} 10", Initials),
            ],
            host.Sent
        );
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
    [InlineData(10000.0, "5000", "DM 5000")]
    [InlineData(10000.0, "10000", "DM 10000")]
    public void MaintainAltitude_ClimbsAboveTheCurrentAltitude_DescendsBelowIt(double altitude, string pick, string command)
    {
        var host = new RecordingMenuHost(pick);
        MenuItem? item = MenuCatalog.Get(MenuIds.AltitudeMaintain).Build(new FakeMenuAircraft { AltitudeFeet = altitude }, Context(), host);

        Assert.NotNull(item);
        Assert.Equal("Maintain", item.Header as string);
        Click(item);

        (IReadOnlyList<string> Items, object? Selected) popup = Assert.Single(host.ListPopups);
        Assert.Null(popup.Selected);
        Assert.Contains("17500", popup.Items);
        Assert.Contains("FL180", popup.Items);
        Assert.DoesNotContain("18000", popup.Items);
        Assert.Equal([(Callsign, command, Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void MaintainAltitude_ListsFromTheDestinationsFieldElevation_In100sThen500s()
    {
        var host = new RecordingMenuHost("9500") { FieldElevation = 4321 };
        var aircraft = new FakeMenuAircraft { AltitudeFeet = 5000, Destination = "KXYZ" };
        MenuItem? item = MenuCatalog.Get(MenuIds.AltitudeMaintain).Build(aircraft, Context(), host);

        Assert.NotNull(item);
        Click(item);

        Assert.Equal(["KXYZ"], host.FieldElevationRequests);
        IReadOnlyList<string> items = Assert.Single(host.ListPopups).Items;
        Assert.Equal("4400", items[0]);
        int last100 = items.ToList().IndexOf("9300");
        Assert.True(last100 >= 0, "9300 (the last 100-ft step below field + 5,000 ft) is listed");
        Assert.Equal("9500", items[last100 + 1]);
        Assert.Equal([(Callsign, "CM 9500", Initials)], host.Sent);
    }

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

    [AvaloniaFact]
    public void AssignSpeed_FiledJet_ListsFromApproachSpeedToClimbSpeedInTens()
    {
        const double altitude = 10000;
        TestVnasData.EnsureInitialized();
        AircraftCategory category = AircraftCategorization.Categorize("B738");
        int approachFloor = (int)(Math.Floor(AircraftPerformance.ApproachSpeed("B738", category) / 10.0) * 10);
        int climbCeiling = (int)(Math.Ceiling(AircraftPerformance.ClimbSpeed("B738", category, altitude) / 10.0) * 10);
        Assert.True(climbCeiling - approachFloor >= 50, "the B738 span needs no widening");

        IReadOnlyList<string> items = AssignSpeedItems(new FakeMenuAircraft { FiledAircraftType = "B738", AltitudeFeet = altitude });

        Assert.Equal($"{approachFloor}", items[0]);
        Assert.Equal($"{climbCeiling}", items[^1]);
    }

    [AvaloniaFact]
    public void AssignSpeed_FiledPiston_WidensANarrowSpanBy20EachWay_NeverBelow40()
    {
        const double altitude = 3000;
        TestVnasData.EnsureInitialized();
        AircraftCategory category = AircraftCategorization.Categorize("C172");
        int approachFloor = (int)(Math.Floor(AircraftPerformance.ApproachSpeed("C172", category) / 10.0) * 10);
        int climbCeiling = (int)(Math.Ceiling(AircraftPerformance.ClimbSpeed("C172", category, altitude) / 10.0) * 10);
        string span = $"approach floor {approachFloor} kt, climb ceiling {climbCeiling} kt";
        Assert.True(climbCeiling - Math.Max(40, approachFloor) < 50, $"the C172 span is narrow enough to widen ({span})");
        Assert.True(approachFloor - 20 <= 40, $"the widened C172 floor reaches the 40 kt limit ({span})");

        IReadOnlyList<string> items = AssignSpeedItems(new FakeMenuAircraft { FiledAircraftType = "C172", AltitudeFeet = altitude });

        Assert.Equal("40", items[0]);
        Assert.Equal($"{climbCeiling + 20}", items[^1]);
    }

    /// <summary>The texts the Assign speed popup lists for <paramref name="aircraft"/>.</summary>
    private static IReadOnlyList<string> AssignSpeedItems(FakeMenuAircraft aircraft)
    {
        var host = new RecordingMenuHost("");
        MenuItem? item = MenuCatalog.Get(MenuIds.SpeedAssign).Build(aircraft, Context(), host);
        Assert.NotNull(item);
        Click(item);
        return Assert.Single(host.ListPopups).Items;
    }

    [AvaloniaTheory]
    [InlineData(35000.0, "Altitude (→ FL350)")]
    [InlineData(5000.0, "Altitude (→ 5000)")]
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
        var routed = new FakeMenuAircraft { NavigatingTo = "ECA", RouteFixes = [Fix, "ECA"] };

        var filteredHost = new RecordingMenuHost(Fix) { FixNames = ["ECA", "OAK", Fix] };
        Click(
            AssertPicker(MenuCatalog.Get(id).Build(routed, Context(), filteredHost), $"{label}...", MenuPickerDescriptor.FilteredList, [Fix, "ECA"])
        );
        (string[] Names, IReadOnlyList<string>? Priority) filteredPopup = Assert.Single(filteredHost.FilteredListPopups);
        Assert.Equal(["ECA", "OAK", Fix], filteredPopup.Names);
        Assert.Equal([Fix, "ECA"], filteredPopup.Priority);

        var listHost = new RecordingMenuHost("ECA");
        Click(AssertPicker(MenuCatalog.Get(id).Build(routed, Context(), listHost), label, MenuPickerDescriptor.List, [Fix, "ECA"]));
        Assert.Equal<object?>(Fix, Assert.Single(listHost.ListPopups).Selected);

        var inputHost = new RecordingMenuHost(Fix);
        Click(AssertPicker(MenuCatalog.Get(id).Build(new FakeMenuAircraft(), Context(), inputHost), $"{label}...", MenuPickerDescriptor.Input, []));

        Assert.Equal([(Callsign, $"{command} SUNOL", Initials)], filteredHost.Sent);
        Assert.Equal([(Callsign, $"{command} ECA", Initials)], listHost.Sent);
        Assert.Equal([(Callsign, $"{command} SUNOL", Initials)], inputHost.Sent);
    }

    [AvaloniaTheory]
    [InlineData(MenuIds.HoldFixLeft, "Hold at fix (left)...", "HFIXL")]
    [InlineData(MenuIds.HoldFixRight, "Hold at fix (right)...", "HFIXR")]
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
        Assert.Equal(["Direct to..."], idle.Items.Select(Describe));
        Assert.Equal("Navigation (→ SUNOL)", navigating.Header as string);
        Assert.Equal(["Direct to...", "Append direct to..."], navigating.Items.Select(Describe));
    }

    [AvaloniaFact]
    public void DrawRoute_EntersTheHostsDrawRouteMode()
    {
        var host = new RecordingMenuHost("");
        MenuItem? item = MenuCatalog.Get(MenuIds.NavigationDrawRoute).Build(null, Context(), host);

        Assert.NotNull(item);
        Assert.Equal("Draw route", item.Header as string);
        Click(item);

        Assert.Equal([Callsign], host.DrawRouteCallsigns);
        Assert.Empty(host.Sent);
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
                "Cleared approach...",
                MenuPickerDescriptor.Input,
                []
            )
        );

        Assert.Equal(["Approach ID"], host.InputPlaceholders);
        Assert.Equal([(Callsign, "CAPP I28R", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void Approach_DestinationWithApproaches_UsesListTierSeededWithTheFirst()
    {
        TestVnasData.EnsureInitialized();
        string[] ids = [.. NavigationDatabase.Instance.GetApproaches(ApproachAirport).Select(a => a.ApproachId)];
        Assert.Contains(ApproachId, ids);
        var host = new RecordingMenuHost(ApproachId);
        var aircraft = new FakeMenuAircraft { Destination = ApproachAirport };

        Click(
            AssertPicker(
                MenuCatalog.Get(MenuIds.ApproachCleared).Build(aircraft, Context(), host),
                "Cleared approach",
                MenuPickerDescriptor.List,
                ids
            )
        );

        Assert.Equal<object?>(ids[0], Assert.Single(host.ListPopups).Selected);
        Assert.Equal([(Callsign, "CAPP I28R", Initials)], host.Sent);
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
        Click(AssertPicker(item, "Cleared visual approach...", MenuPickerDescriptor.List, runways));

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
        Click(AssertPicker(other, "Cleared visual approach (other)...", MenuPickerDescriptor.List, runways));

        Assert.Equal([(Callsign, "CVA 28R", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void ReportTrafficInSight_BlankAnswer_SendsBareRtis()
    {
        var host = new RecordingMenuHost("");
        MenuItem? item = MenuCatalog.Get(MenuIds.ApproachReportTrafficInSight).Build(null, Context(), host);

        Click(AssertPicker(item, "Report traffic in sight...", MenuPickerDescriptor.Input, []));

        Assert.Equal(["Target callsign (optional)"], host.InputPlaceholders);
        Assert.Equal([(Callsign, "RTIS", Initials)], host.Sent);
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
        Click(AssertPicker(item, "Cleared visual approach...", MenuPickerDescriptor.Input, []));

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
            ["Turning base", "Turning final", "Turning crosswind", "Turning downwind", "N-mile final...", "At fix...", "---", "Stop reporting"],
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
            AssertPicker(MenuCatalog.Get(MenuIds.ProceduresJoinStar).Build(aircraft, Context(), host), "Join STAR...", MenuPickerDescriptor.Input, [])
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
        Click(AssertPicker(MenuCatalog.BuildCrossFixOther(aircraft, Context(), host), "Cross fix (other)...", MenuPickerDescriptor.Input, []));

        Assert.Equal(["Fix name"], host.InputPlaceholders);
        Assert.Equal([(Callsign, "CFIX SUNOL", Initials), (Callsign, "CFIX ECA", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void CrossFix_NoRouteFixes_UsesInputTier()
    {
        var host = new RecordingMenuHost(Fix);
        var aircraft = new FakeMenuAircraft();

        Click(
            AssertPicker(MenuCatalog.Get(MenuIds.ProceduresCrossFix).Build(aircraft, Context(), host), "Cross fix...", MenuPickerDescriptor.Input, [])
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
        Click(AssertPicker(item, "Join airway...", MenuPickerDescriptor.List, ["V25", "J80"]));
        Click(AssertPicker(MenuCatalog.BuildJoinAirwayOther(aircraft, Context(), host), "Join airway (other)...", MenuPickerDescriptor.Input, []));

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
        Click(AssertPicker(MenuCatalog.BuildJoinStarOther(aircraft, Context(), host), "Join STAR (other)...", MenuPickerDescriptor.List, stars));

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
        Click(AssertPicker(item, "Join STAR...", MenuPickerDescriptor.List, stars));

        Assert.Equal<object?>(stars[0], Assert.Single(host.ListPopups).Selected);
        Assert.Equal([(Callsign, "JARR EMZOH4", Initials)], host.Sent);
        Assert.Null(MenuCatalog.BuildJoinStarOther(aircraft, Context(), host));
    }

    [AvaloniaTheory]
    [InlineData(MenuIds.ProceduresJoinRadialOutbound, "Join radial outbound...", "Bearing from SUNOL (0-360)", "JRADO SUNOL 090")]
    [InlineData(MenuIds.ProceduresJoinRadialInbound, "Join radial inbound...", "Bearing to SUNOL (0-360)", "JRADI SUNOL 090")]
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
        Click(AssertPicker(item, "Join radial inbound...", MenuPickerDescriptor.Input, []));

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

        Click(Assert.Single(children, m => m.Header is "Custom..."));
        Assert.Equal(["CTO arg (e.g. RH 3000, LT 270, DCT BERKS)"], host.InputPlaceholders);
        Assert.Equal([(Callsign, command, Initials)], host.Sent);
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
        Assert.Equal("Cleared for takeoff", cto.Header as string);
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

        Assert.True(entry.IsApplicable(ac, GroundContext(VfrCommandsForIfr.None)));
        MenuItem? item = entry.Build(ac, GroundContext(VfrCommandsForIfr.None), host);
        Assert.NotNull(item);
        Assert.Equal("Resume taxi", item.Header as string);
        Click(item);

        Assert.Equal([(Callsign, "RES", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void GroundResumeTaxi_HiddenFromAHoldShortWithoutARoute_AndFromAnUnheldStop()
    {
        MenuCatalogEntry entry = MenuCatalog.Get(MenuIds.GroundResumeTaxi);

        Assert.False(entry.IsApplicable(OnGround("Holding Short 28R/10L", "IFR", ""), GroundContext(VfrCommandsForIfr.None)));
        Assert.False(entry.IsApplicable(OnGround("Holding In Position", "IFR", ""), GroundContext(VfrCommandsForIfr.None)));
    }

    [AvaloniaFact]
    public void GroundCrossRunway_NamesTheHeldRunway_AndSendsCrossForIt()
    {
        var host = new RecordingMenuHost("");
        AircraftModel ac = OnGround("Holding Short 28R/10L", "IFR", "30");

        MenuItem? item = MenuCatalog.Get(MenuIds.GroundCrossRunway).Build(ac, GroundContext(VfrCommandsForIfr.None), host);
        Assert.NotNull(item);
        Assert.Equal("Cross 28R", item.Header as string);
        Click(item);

        Assert.Equal([(Callsign, "CROSS 28R", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void GroundRelative_SendsAsTheSelectedAircraft_NamingTheRightClickedOne()
    {
        var selected = new AircraftModel { Callsign = Selected, IsOnGround = true };
        var context = new MenuContext(Callsign, Initials, selected, false, VfrCommandsForIfr.None, CatalogMenuView.Ground);
        var host = new RecordingMenuHost("");
        var menu = new ContextMenu();

        SharedMenuGroups.AddGroundRelative(menu.Items, OnGround("Taxiing", "IFR", ""), context, host);

        Assert.Equal([$"↪ {Selected}:", $"{Selected}: give way to {Callsign}", $"{Selected}: follow {Callsign}", "---"], menu.Items.Select(Describe));
        foreach (MenuItem item in menu.Items.OfType<MenuItem>().Where(i => i.IsEnabled))
        {
            Click(item);
        }

        Assert.Equal([(Selected, $"GW {Callsign}", Initials), (Selected, $"FOLLOWG {Callsign}", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void GroundRelative_NothingWithoutAnotherOnGroundSelection()
    {
        var airborne = new AircraftModel { Callsign = Selected, IsOnGround = false };
        var same = new AircraftModel { Callsign = Callsign, IsOnGround = true };
        foreach (AircraftModel? selected in (AircraftModel?[])[null, airborne, same])
        {
            var context = new MenuContext(Callsign, Initials, selected, false, VfrCommandsForIfr.None, CatalogMenuView.Ground);
            var menu = new ContextMenu();

            SharedMenuGroups.AddGroundRelative(menu.Items, OnGround("Taxiing", "IFR", ""), context, new RecordingMenuHost(""));

            Assert.Empty(menu.Items);
        }
    }

    [AvaloniaTheory]
    [InlineData("Holding Short 15/33", "28R", "Cleared for takeoff 15")]
    [InlineData("Taxiing", "28R", "Cleared for takeoff 28R")]
    [InlineData("LinedUpAndWaiting", "30", "Cleared for takeoff 30")]
    public void GroundCto_HeaderNamesTheHeldRunway_ElseTheAssignedOne(string phase, string assignedRunway, string header)
    {
        AircraftModel ac = OnGround(phase, "IFR", assignedRunway);
        MenuCatalogEntry entry = MenuCatalog.Get(MenuIds.TowerClearedForTakeoff);

        Assert.True(entry.IsApplicable(ac, GroundContext(VfrCommandsForIfr.None)));
        Assert.Equal(header, entry.Build(ac, GroundContext(VfrCommandsForIfr.None), new RecordingMenuHost(""))?.Header as string);
    }

    [AvaloniaFact]
    public void GroundCto_VfrDeparture_OffersTheGroundModifierSet_WithNoCustomItem()
    {
        var host = new RecordingMenuHost("");
        AircraftModel ac = OnGround("LinedUpAndWaiting", "VFR", "30");

        MenuItem? cto = MenuCatalog.Get(MenuIds.TowerClearedForTakeoff).Build(ac, GroundContext(VfrCommandsForIfr.None), host);
        Assert.NotNull(cto);
        Assert.Equal(
            [
                "Default (SID/on course)",
                "---",
                "Make left traffic",
                "Make right traffic",
                "---",
                "Runway heading",
                "On course",
                "Right crosswind (90° right)",
                "Right downwind (180° right)",
                "Left crosswind (90° left)",
                "Left downwind (180° left)",
            ],
            cto.Items.Select(Describe)
        );
        foreach (MenuItem item in cto.Items.OfType<MenuItem>())
        {
            Click(item);
        }

        Assert.Equal(["CTO", "CTO MLT", "CTO MRT", "CTO RH", "CTO OC", "CTO MRC", "CTO MRD", "CTO MLC", "CTO MLD"], host.Sent.Select(s => s.Command));
    }

    [AvaloniaFact]
    public void GroundCto_IfrDepartureWithoutTheVfrSet_OffersTheDefaultAndRunwayHeadingOnly()
    {
        var host = new RecordingMenuHost("");
        AircraftModel ac = OnGround("Holding Short 28R/10L", "IFR", "");

        MenuItem? cto = MenuCatalog.Get(MenuIds.TowerClearedForTakeoff).Build(ac, GroundContext(VfrCommandsForIfr.None), host);
        Assert.NotNull(cto);
        Assert.Equal(["Default (SID/on course)", "---", "Runway heading"], cto.Items.Select(Describe));
        foreach (MenuItem item in cto.Items.OfType<MenuItem>())
        {
            Click(item);
        }

        Assert.Equal(["CTO", "CTO RH"], host.Sent.Select(s => s.Command));
    }

    [AvaloniaTheory]
    [InlineData("LiningUp", "28R")]
    [InlineData("Takeoff", "28R")]
    [InlineData("LinedUpAndWaiting", "")]
    public void GroundCto_NeverInLiningUpOrTakeoff_NorLinedUpWithoutAnAssignedRunway(string phase, string assignedRunway)
    {
        AircraftModel ac = OnGround(phase, "IFR", assignedRunway);
        MenuCatalogEntry entry = MenuCatalog.Get(MenuIds.TowerClearedForTakeoff);

        Assert.True(entry.IsApplicable(ac, Context()));
        Assert.False(entry.IsApplicable(ac, GroundContext(VfrCommandsForIfr.None)));
    }

    [AvaloniaTheory]
    [InlineData("Taxiing", "28R")]
    public void GroundLineUpAndWait_OnlyAtAHoldShort_NeverTaxiingToTheRunway(string phase, string assignedRunway)
    {
        AircraftModel ac = OnGround(phase, "IFR", assignedRunway);
        MenuCatalogEntry entry = MenuCatalog.Get(MenuIds.TowerLineUpAndWait);

        Assert.True(entry.IsApplicable(ac, Context()));
        Assert.False(entry.IsApplicable(ac, GroundContext(VfrCommandsForIfr.None)));
    }

    [AvaloniaTheory]
    [InlineData("LiningUp", "28R")]
    public void GroundCancelTakeoff_NeverWhileLiningUp(string phase, string assignedRunway)
    {
        AircraftModel ac = OnGround(phase, "IFR", assignedRunway);
        MenuCatalogEntry entry = MenuCatalog.Get(MenuIds.TowerCancelTakeoff);

        Assert.True(entry.IsApplicable(ac, Context()));
        Assert.False(entry.IsApplicable(ac, GroundContext(VfrCommandsForIfr.None)));
    }

    [AvaloniaFact]
    public void GroundLanding_KeepsTheGroundOrder_TouchAndGoStopAndGoLowApproachThenTheOption()
    {
        var ac = new AircraftModel
        {
            Callsign = Callsign,
            IsOnGround = false,
            CurrentPhase = "FinalApproach",
            FlightRules = "VFR",
            AssignedRunway = "28R",
        };
        var menu = new ContextMenu();

        SharedMenuGroups.AddGroundLanding(menu.Items, ac, GroundContext(VfrCommandsForIfr.None), new RecordingMenuHost(""));

        Assert.Equal(
            [
                "Cleared to land 28R",
                "Force landing 28R",
                "Touch and go 28R",
                "Stop and go 28R",
                "Low approach 28R",
                "Cleared for the option 28R",
                "Go around 28R",
            ],
            menu.Items.Select(Describe)
        );
    }

    // --- The aircraft list's flat command block (MenuView.List) ---

    /// <summary>An aircraft-list context under <paramref name="mode"/>, outside solo training, with no previous selection.</summary>
    private static MenuContext ListContext(VfrCommandsForIfr mode) => new(Callsign, Initials, null, false, mode, CatalogMenuView.List);

    /// <summary>
    /// The list's command block for <paramref name="aircraft"/>: each item's header and the command clicking it sends, in
    /// menu order, so the gates, labels and bare verbs the list differs by are all pinned.
    /// </summary>
    private static List<(string Header, string Command)> ListMenuCommands(FakeMenuAircraft aircraft, RecordingMenuHost host)
    {
        var menu = new ContextMenu();
        SharedMenuGroups.AddListAircraftCommands(menu.Items, aircraft, ListContext(VfrCommandsForIfr.None), host);
        var result = new List<(string, string)>();
        foreach (MenuItem item in menu.Items.OfType<MenuItem>())
        {
            host.Sent.Clear();
            Click(item);
            result.Add(((string)item.Header!, Assert.Single(host.Sent).Command));
        }

        return result;
    }

    // A hold-short: Resume taxi (the list's route-free RES, offered with no taxi route), Cross the held runway,
    // Line up and wait and a bare Cleared for takeoff, both naming the held 28R rather than the assigned 30.
    [AvaloniaFact]
    public void ListMenu_HoldingShort_SendsResCrossLineUpAndBareCto()
    {
        var aircraft = new FakeMenuAircraft
        {
            IsOnGround = true,
            CurrentPhase = "Holding Short 28R/10L",
            AssignedRunway = "30",
        };

        Assert.Equal(
            [("Resume taxi", "RES"), ("Cross 28R", "CROSS 28R"), ("Line up and wait 28R", "LUAW"), ("Cleared for takeoff 28R", "CTO")],
            ListMenuCommands(aircraft, new RecordingMenuHost(""))
        );
    }

    // Lined up and waiting: the bare Cleared for takeoff and Cancel takeoff clearance, no line up and wait.
    [AvaloniaFact]
    public void ListMenu_LinedUpAndWaiting_SendsCtoAndCancelTakeoff()
    {
        var aircraft = new FakeMenuAircraft
        {
            IsOnGround = true,
            CurrentPhase = "LinedUpAndWaiting",
            AssignedRunway = "30",
        };

        Assert.Equal(
            [("Cleared for takeoff 30", "CTO"), ("Cancel takeoff clearance", "CTOC")],
            ListMenuCommands(aircraft, new RecordingMenuHost(""))
        );
    }

    // Airborne on final: the landing block in the ground's order, with the assigned runway in every label.
    [AvaloniaFact]
    public void ListMenu_OnFinal_SendsTheLandingClearances()
    {
        var aircraft = new FakeMenuAircraft
        {
            IsOnGround = false,
            CurrentPhase = "FinalApproach",
            AssignedRunway = "28R",
        };

        Assert.Equal(
            [("Cleared to land 28R", "CLAND"), ("Force landing 28R", "CLANDF"), ("Go around 28R", "GA")],
            ListMenuCommands(aircraft, new RecordingMenuHost(""))
        );
    }

    // Rolling out: the runway exits, and no tower clearance.
    [AvaloniaFact]
    public void ListMenu_Landing_SendsTheRunwayExits()
    {
        var aircraft = new FakeMenuAircraft
        {
            IsOnGround = true,
            CurrentPhase = "Landing",
            AssignedRunway = "28R",
        };

        Assert.Equal([("Exit left", "EL"), ("Exit right", "ER")], ListMenuCommands(aircraft, new RecordingMenuHost("")));
    }

    // Taxiing with a call-for-release window: Hold position sends HOLD (not the ground view's HP), and the
    // release-window check is offered on the phase and window alone.
    [AvaloniaFact]
    public void ListMenu_TaxiingWithCfrWindow_SendsHoldAndCheckReleaseWindow()
    {
        var aircraft = new FakeMenuAircraft
        {
            IsOnGround = true,
            CurrentPhase = "Taxiing",
            AssignedRunway = "30",
            HasCfrWindow = true,
        };

        Assert.Equal(
            [("Hold position", "HOLD"), ("Line up and wait 30", "LUAW"), ("Cleared for takeoff 30", "CTO"), ("Check release window", "CFR CHECK")],
            ListMenuCommands(aircraft, new RecordingMenuHost(""))
        );
    }

    // --- The aircraft list's delayed-spawn block (MenuView.List) ---

    /// <summary>The list's delayed-spawn items for a delayed aircraft, placed as the list places them.</summary>
    private static ContextMenu DelayedSpawnMenu(RecordingMenuHost host)
    {
        var menu = new ContextMenu();
        SharedMenuGroups.AddDelayedSpawn(menu, new FakeMenuAircraft(), ListContext(VfrCommandsForIfr.None), host);
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

        MenuItem? all = SharedMenuGroups.Pattern(aircraft, RadarContext(VfrCommandsForIfr.All), host);
        MenuItem? enterFinalOnly = SharedMenuGroups.Pattern(aircraft, RadarContext(VfrCommandsForIfr.EnterFinalOnly), host);
        MenuItem? none = SharedMenuGroups.Pattern(aircraft, RadarContext(VfrCommandsForIfr.None), host);

        Assert.NotNull(all);
        Click(Assert.Single(all.Items.OfType<MenuItem>(), m => m.Header as string == "Turn base"));
        Assert.Equal([(Callsign, "TB", Initials)], host.Sent);
        // Under the default setting an IFR aircraft keeps only the straight-in final entry: no circuit legs, no maneuvers.
        Assert.NotNull(enterFinalOnly);
        Assert.Equal(["Enter straight-in final..."], enterFinalOnly.Items.Select(Describe));
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
        Click(AssertPicker(item, "Enter left downwind...", kind, runways));

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
        Click(AssertPicker(item, "Enter left downwind...", MenuPickerDescriptor.Input, []));

        Assert.Equal(["Runway (optional)"], host.InputPlaceholders);
        Assert.Equal([(Callsign, command, Initials)], host.Sent);
        Assert.Null(MenuCatalog.BuildPatternEntryOther(MenuIds.PatternEnterLeftDownwind, aircraft, Context(), host));
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
        Click(AssertPicker(list, "Enter right base...", MenuPickerDescriptor.List, runways));
        Assert.Null(MenuCatalog.BuildPatternEntryOther(MenuIds.PatternEnterRightBase, noDefault, Context(), host));

        Assert.Equal("Enter right base 30", MenuCatalog.Get(MenuIds.PatternEnterRightBase).Build(withDefault, Context(), host)?.Header as string);
        MenuItem? other = MenuCatalog.BuildPatternEntryOther(MenuIds.PatternEnterRightBase, withDefault, Context(), host);
        Click(AssertPicker(other, "Enter right base (other)...", MenuPickerDescriptor.List, runways));

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

    /// <summary>The child items of the submenu the entry builds; asserts the entry built one.</summary>
    private static List<MenuItem> SubmenuItems(string id, RecordingMenuHost host)
    {
        MenuItem? submenu = MenuCatalog.Get(id).Build(null, Context(), host);
        Assert.NotNull(submenu);
        return [.. submenu.Items.OfType<MenuItem>()];
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

        public bool IsLiveTraffic => false;

        public bool AssumedFromLiveTraffic => false;

        public bool IsOnGround { get; init; }

        public bool IsHeld => false;

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

        public string NavigatingTo { get; init; } = "";

        public MagneticHeading? AssignedHeading { get; init; }

        public double? AssignedAltitude { get; init; }

        public double? AssignedSpeed { get; init; }

        public string FiledAircraftType { get; init; } = "";

        public string Destination { get; init; } = "";

        public string Departure { get; init; } = "";

        public string Route { get; init; } = "";

        public string? ActiveApproachId { get; init; }

        public string? ExpectedApproach { get; init; }

        public IReadOnlyList<string> RouteFixes { get; init; } = [];

        public IReadOnlyList<string> RouteFixNames() => RouteFixes;
    }
}
