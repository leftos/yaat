using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Sim.Commands;
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

    /// <summary>
    /// Every catalog entry but the host-built ones (see <see cref="HostBuiltIds"/>): its id, the text an input picker
    /// is answered with, and the command it sends.
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

    /// <summary>Records every send and popup, and answers an input picker at once with <paramref name="input"/>.</summary>
    private sealed class RecordingMenuHost(string input) : IMenuHost
    {
        public List<(string Callsign, string Command, string Initials)> Sent { get; } = [];

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

    /// <summary>A minimal <see cref="IMenuAircraft"/>: never live traffic, and only the warp popup's seed values matter.</summary>
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
    }
}
