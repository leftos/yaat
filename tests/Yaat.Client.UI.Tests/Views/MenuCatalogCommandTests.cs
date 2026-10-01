using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Sim;
using Yaat.Sim.Commands;
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
    ];

    /// <summary>
    /// The entries whose item the host builds rather than the catalog: a popup, an editor, a submenu the catalog
    /// assembles, or a header the host's own state decides.
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
        MenuIds.NavigationDrawRoute,
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

    private static MenuContext Context() => new(Callsign, Initials, null, false, VfrCommandsForIfr.EnterFinalOnly);

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
    /// Records every send and popup, and answers a picker at once: an input or filtered list with
    /// <paramref name="input"/>, a list with the item whose text is <paramref name="input"/> (an empty input only records the list).
    /// </summary>
    private sealed class RecordingMenuHost(string input) : IMenuHost
    {
        public List<(string Callsign, string Command, string Initials)> Sent { get; } = [];

        public string[]? FixNames { get; init; }

        /// <summary>The field elevation the altitude picker is answered with; sea level by default, so the list starts at 100 ft.</summary>
        public double FieldElevation { get; init; }

        public List<string?> FieldElevationRequests { get; } = [];

        public List<(IReadOnlyList<string> Items, object? Selected)> ListPopups { get; } = [];

        public List<(string[] Names, IReadOnlyList<string>? Priority)> FilteredListPopups { get; } = [];

        public List<string> DrawRouteCallsigns { get; } = [];

        public List<(string Callsign, int Heading, int Altitude, int Speed)> WarpPopups { get; } = [];

        public Func<string, int, int, int, Task>? WarpSubmit { get; private set; }

        public int FlightPlanEditorOpens { get; private set; }

        public HashSet<string> MinifiedCallsigns { get; } = [];

        public HashSet<string> ManualOffsetCallsigns { get; } = [];

        public HashSet<string> PathShownCallsigns { get; } = [];

        public MenuMeasureState MeasureState { get; set; } = MenuMeasureState.None;

        public List<string> MinifiedToggles { get; } = [];

        public List<string> DataBlockOffsetResets { get; } = [];

        public List<string> PathToggles { get; } = [];

        public List<string> MeasurePicks { get; } = [];

        public Task SendAsync(string callsign, string command, string initials)
        {
            Sent.Add((callsign, command, initials));
            return Task.CompletedTask;
        }

        public void ShowInputPopup(string placeholder, Func<string, Task> onSubmit) => _ = onSubmit(input);

        public void ShowListPopup(IReadOnlyList<object> items, object? selected, Func<object, Task> onPick)
        {
            ListPopups.Add(([.. items.Select(i => i.ToString() ?? "")], selected));
            if (input.Length > 0)
            {
                _ = onPick(items.First(i => i.ToString() == input));
            }
        }

        public void ShowFilteredListPopup(string[] sortedNames, IReadOnlyList<object>? priorityItems, Func<string, Task> onPick)
        {
            FilteredListPopups.Add((sortedNames, priorityItems?.Select(i => i.ToString() ?? "").ToList()));
            _ = onPick(input);
        }

        /// <summary>Records the destination asked about and answers <see cref="FieldElevation"/>.</summary>
        public double GetFieldElevation(string? destination)
        {
            FieldElevationRequests.Add(destination);
            return FieldElevation;
        }

        public void EnterDrawRoute(string callsign) => DrawRouteCallsigns.Add(callsign);

        public void ShowWarpPopup(string callsign, int heading, int altitude, int speed, Func<string, int, int, int, Task> onSubmit)
        {
            WarpPopups.Add((callsign, heading, altitude, speed));
            WarpSubmit = onSubmit;
        }

        public void OpenFlightPlanEditor() => FlightPlanEditorOpens++;

        public bool IsMinified(string callsign) => MinifiedCallsigns.Contains(callsign);

        public void ToggleMinified(string callsign) => MinifiedToggles.Add(callsign);

        public bool HasManualDataBlockOffset(string callsign) => ManualOffsetCallsigns.Contains(callsign);

        public void ResetDataBlockOffset(string callsign) => DataBlockOffsetResets.Add(callsign);

        public bool IsPathShown(string callsign) => PathShownCallsigns.Contains(callsign);

        public void ToggleShowPath(string callsign) => PathToggles.Add(callsign);

        public MenuMeasureState GetMeasureState() => MeasureState;

        public void MeasurePickOnAircraft(string callsign) => MeasurePicks.Add(callsign);

        public MenuItem BuildFavorites(IMenuAircraft? aircraft, MenuContext context) =>
            throw new NotSupportedException("The command table does not build the favorites submenu");
    }

    /// <summary>
    /// A minimal <see cref="IMenuAircraft"/>: never live traffic, with the warp seed values, the assignments the flight
    /// group headers show, the type the speed items read and the route fixes the navigation pickers offer settable.
    /// </summary>
    private sealed class FakeMenuAircraft : IMenuAircraft
    {
        public bool IsLiveTraffic => false;

        public bool AssumedFromLiveTraffic => false;

        public bool IsOnGround => false;

        public bool IsHeld => false;

        public bool HasQueuedPatternEntry => false;

        public string FlightRules => "IFR";

        public string CurrentPhase => "";

        public string AssignedRunway => "";

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
