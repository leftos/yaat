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

    /// <summary>Every catalog entry but the host-built ones (favorites, assume-and-track, warp, edit flight plan): its id, the text an input picker is answered with, and the command it sends.</summary>
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
        IEnumerable<string> covered = Expected
            .Select(e => e.Id)
            .Append(MenuIds.FavoritesMenu)
            .Append(MenuIds.LiveTrafficAssumeAndTrack)
            .Append(MenuIds.SimControlWarp)
            .Append(MenuIds.AircraftEditFlightPlan);
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

    /// <summary>Records every send and popup, and answers an input picker at once with <paramref name="input"/>.</summary>
    private sealed class RecordingMenuHost(string input) : IMenuHost
    {
        public List<(string Callsign, string Command, string Initials)> Sent { get; } = [];

        public List<(string Callsign, int Heading, int Altitude, int Speed)> WarpPopups { get; } = [];

        public Func<string, int, int, int, Task>? WarpSubmit { get; private set; }

        public int FlightPlanEditorOpens { get; private set; }

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
