using Avalonia.Controls;
using Yaat.Client.ContextMenus;
using Yaat.Client.Services;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Records every send and popup, and answers a picker at once: an input with
/// <paramref name="input"/>, a list with the item whose text is <paramref name="input"/> (an empty input only records the list).
/// </summary>
internal sealed class RecordingMenuHost(string input) : IMenuHost
{
    public List<(string Callsign, string Command, string Initials)> Sent { get; } = [];

    /// <summary>The fixes <see cref="SuggestFixes"/> answers every partial name with; none by default.</summary>
    public IReadOnlyList<string> FixSuggestions { get; init; } = [];

    /// <summary>Every partial name <see cref="SuggestFixes"/> was asked for, in order.</summary>
    public List<string> FixSuggestionRequests { get; } = [];

    public IReadOnlyList<string> SuggestFixes(string partial)
    {
        FixSuggestionRequests.Add(partial);
        return FixSuggestions;
    }

    /// <summary>The field elevation the altitude picker is answered with; sea level by default, so the list starts at 100 ft.</summary>
    public double FieldElevation { get; init; }

    public List<string?> FieldElevationRequests { get; } = [];

    public List<string> DrawRouteCallsigns { get; } = [];

    public int FlightPlanEditorOpens { get; private set; }

    /// <summary>Every send through the VFR gate (<see cref="IMenuHost.SendGatedAsync"/>), in order; not in <see cref="Sent"/>.</summary>
    public List<(string Callsign, string Command, string Initials)> GatedSent { get; } = [];

    public Task SendGatedAsync(string callsign, string command, string initials)
    {
        GatedSent.Add((callsign, command, initials));
        return Task.CompletedTask;
    }

    public Task SendAsync(string callsign, string command, string initials)
    {
        Sent.Add((callsign, command, initials));
        return Task.CompletedTask;
    }

    public List<string> InputPlaceholders { get; } = [];

    /// <summary>The text an input popup is answered with when it differs from a picker's answer; the picker's answer when null.</summary>
    public string? InputAnswer { get; init; }

    /// <summary>The initial text and caret each input popup was opened with, in order.</summary>
    public List<(string Text, int Caret)> InputSeeds { get; } = [];

    public void ShowInputPopup(string placeholder, string initialText, int caretIndex, Func<string, Task> onSubmit)
    {
        InputPlaceholders.Add(placeholder);
        InputSeeds.Add((initialText, caretIndex));
        _ = onSubmit(InputAnswer ?? input);
    }

    /// <summary>The fix-radial-distance every point is described as; null (no fixes loaded) by default.</summary>
    public string? PointDescription { get; init; }

    /// <summary>The positions the point menu asked to describe, in order.</summary>
    public List<LatLon> DescribedPoints { get; } = [];

    public string? DescribePoint(LatLon position)
    {
        DescribedPoints.Add(position);
        return PointDescription;
    }

    /// <summary>The Taxi here choices answered for every node, whatever the callsign asked about.</summary>
    public List<MenuCommandChoice> TaxiChoices { get; } = [];

    /// <summary>The callsign, node id and runway end each Taxi here request named, in order.</summary>
    public List<(string Callsign, int NodeId, string? RunwayEnd)> TaxiChoiceRequests { get; } = [];

    public IReadOnlyList<MenuCommandChoice> GetTaxiChoices(string callsign, GroundNodeDto node, string? runwayEnd)
    {
        TaxiChoiceRequests.Add((callsign, node.Id, runwayEnd));
        return TaxiChoices;
    }

    /// <summary>The Taxi to runway targets answered per runway end (keyed by the end designator); none for an end not listed.</summary>
    public Dictionary<string, List<RunwayHoldShortTarget>> RunwayHoldShortTargets { get; } = [];

    /// <summary>The callsign, runway, end and click each Taxi to runway target request named, in order.</summary>
    public List<(string Callsign, string RunwayName, string RunwayEnd, LatLon Click)> RunwayHoldShortTargetRequests { get; } = [];

    public IReadOnlyList<RunwayHoldShortTarget> GetRunwayHoldShortTargets(string callsign, string runwayName, string runwayEnd, LatLon click)
    {
        RunwayHoldShortTargetRequests.Add((callsign, runwayName, runwayEnd, click));
        return RunwayHoldShortTargets.TryGetValue(runwayEnd, out List<RunwayHoldShortTarget>? targets) ? targets : [];
    }

    /// <summary>The seed every Custom taxi… input opens with, whatever the node and runway end.</summary>
    public MenuTextSeed CustomTaxiSeed { get; init; } = new("TAXI ", 5);

    /// <summary>Every Custom taxi… seed asked for, as the node and the clicked runway end.</summary>
    public List<(int NodeId, string? RunwayEnd)> CustomTaxiSeedRequests { get; } = [];

    public MenuTextSeed GetCustomTaxiSeed(GroundNodeDto node, string? runwayEnd)
    {
        CustomTaxiSeedRequests.Add((node.Id, runwayEnd));
        return CustomTaxiSeed;
    }

    /// <summary>The taxiways every node is answered with, whatever the node; none by default.</summary>
    public List<string> NodeTaxiways { get; } = [];

    public IReadOnlyList<string> GetNodeTaxiwayNames(GroundNodeDto node) => NodeTaxiways;

    /// <summary>The taxiway every hold-short node is answered with, whatever the node; null by default.</summary>
    public string? HoldShortTaxiway { get; init; }

    public string? GetHoldShortTaxiwayName(GroundNodeDto node) => HoldShortTaxiway;

    /// <summary>Whether the tug reaches every node, whatever the callsign and node; true by default.</summary>
    public bool TugReaches { get; init; } = true;

    public bool CanTugReach(string callsign, GroundNodeDto node) => TugReaches;

    /// <summary>The sim's answer every <see cref="SendForResultAsync"/> hands back; an accepted command by default.</summary>
    public CommandResultDto? SendResult { get; set; } = new(true, null);

    /// <summary>
    /// The answer still in flight that <see cref="SendForResultAsync"/> hands back while set, which the test completes; null
    /// (answer <see cref="SendResult"/> at once) by default.
    /// </summary>
    public TaskCompletionSource<CommandResultDto?>? PendingSend { get; set; }

    /// <summary>Records the send in <see cref="Sent"/> and answers <see cref="PendingSend"/>, else <see cref="SendResult"/>.</summary>
    public Task<CommandResultDto?> SendForResultAsync(string callsign, string command, string initials)
    {
        Sent.Add((callsign, command, initials));
        return PendingSend?.Task ?? Task.FromResult(SendResult);
    }

    /// <summary>The aircraft the live check matches against, the commanded one included; none by default.</summary>
    public List<TrafficAdvisoryCandidate> AdvisoryCandidates { get; } = [];

    /// <summary>How many times <see cref="GetAdvisoryCandidates"/> was asked.</summary>
    public int AdvisoryCandidateReads { get; private set; }

    public IReadOnlyList<TrafficAdvisoryCandidate> GetAdvisoryCandidates()
    {
        AdvisoryCandidateReads++;
        return [.. AdvisoryCandidates];
    }

    private readonly List<Action> _aircraftUpdateObservers = [];

    /// <summary>How many observers follow the aircraft updates now.</summary>
    public int AircraftUpdateObserverCount => _aircraftUpdateObservers.Count;

    public IDisposable ObserveAircraftUpdates(Action onUpdate)
    {
        _aircraftUpdateObservers.Add(onUpdate);
        return new Unsubscriber(() => _aircraftUpdateObservers.Remove(onUpdate));
    }

    /// <summary>Tells every observer an aircraft update arrived, as the client does on each one it receives.</summary>
    public void RaiseAircraftUpdate()
    {
        foreach (Action observer in _aircraftUpdateObservers.ToList())
        {
            observer();
        }
    }

    /// <summary>The host places no fix.</summary>
    public LatLon? GetFixPosition(string name) => null;

    /// <summary>The reporting points every position is answered with; none by default.</summary>
    public List<string> ReportingPoints { get; } = [];

    public IReadOnlyList<string> GetReportingPointsNear(LatLon position) => [.. ReportingPoints];

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    /// <summary>The minimum vectoring altitude every position is answered with; none by default.</summary>
    public (string Sector, int FloorFtMsl)? Mva { get; set; }

    public (string Sector, int FloorFtMsl)? GetMva(LatLon position) => Mva;

    /// <summary>The room's active-runway tokens by airport, as the server sends them (<c>OAK: [A28R, 30]</c>); none by default.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> RoomActiveRunways { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    /// <summary>
    /// The pattern entries' runway choices, with the active ends read from <see cref="RoomActiveRunways"/> as the client
    /// reads them; a token that does not read fails the test.
    /// </summary>
    public IReadOnlyList<PatternRunwayChoice> GetPatternRunwayChoices(string airportId, string aircraftType) =>
        PatternRunwayChoices.For(airportId, aircraftType, GetActiveArrivalEnds(airportId));

    /// <summary>
    /// The room's arrival ends, read from <see cref="RoomActiveRunways"/> as the client reads them; a token that does not
    /// read fails the test.
    /// </summary>
    public IReadOnlyList<string> GetActiveArrivalEnds(string airportId)
    {
        ActiveRunways runways = PatternRunwayChoices.ReadRoomActiveRunways(
            RoomActiveRunways,
            "Pattern entry active runways",
            warning => throw new InvalidOperationException($"Unreadable active runways: {warning}")
        );
        return PatternRunwayChoices.ActiveArrivalEnds(runways, airportId);
    }

    /// <summary>
    /// The airports the room gives a tower, in either form (<c>OAK</c>, <c>KOAK</c>); none by default, so every airport is
    /// untowered.
    /// </summary>
    public IReadOnlyList<string> ToweredAirports { get; init; } = [];

    public bool IsToweredAirport(string airportId) =>
        ToweredAirports.Any(towered => NavigationDatabase.NormalizeAirport(towered) == NavigationDatabase.NormalizeAirport(airportId));

    /// <summary>Records the destination asked about and answers <see cref="FieldElevation"/>.</summary>
    public double GetFieldElevation(string? destination)
    {
        FieldElevationRequests.Add(destination);
        return FieldElevation;
    }

    public void EnterDrawRoute(string callsign) => DrawRouteCallsigns.Add(callsign);

    /// <summary>The callsign each flight-plan editor was opened for, in order.</summary>
    public List<string> FlightPlanEditorCallsigns { get; } = [];

    public void OpenFlightPlanEditor(string callsign)
    {
        FlightPlanEditorOpens++;
        FlightPlanEditorCallsigns.Add(callsign);
    }

    /// <summary>The session the menu is built with: initials "AB", no solo training, no VFR commands for IFR aircraft.</summary>
    public MenuSession Session { get; init; } =
        new("AB", false, VfrCommandsForIfr.None, QuickCommandDefaults.For, SoloAdvisoryPrefill.TrueValues, false);

    /// <summary>The callsign lists the RPO items were asked for, in order; the host answers none.</summary>
    public List<IReadOnlyList<string>> RpoRequests { get; } = [];

    public IReadOnlyList<Control> BuildRpoItems(IReadOnlyList<string> callsigns)
    {
        RpoRequests.Add(callsigns);
        return [];
    }

    /// <summary>The ground traffic the follow and give-way submenus list, whatever the callsign asked about.</summary>
    public List<MenuGroundTrafficRow> GroundTraffic { get; } = [];

    /// <summary>Every highlight the menu asked for, in order, null for a clear.</summary>
    public List<string?> Highlights { get; } = [];

    /// <summary>The host answers that no aircraft asked about stands on the other's taxi route.</summary>
    public bool IsOnTaxiRoute(string callsign, string otherCallsign) => false;

    public void HighlightAircraft(string? callsign) => Highlights.Add(callsign);

    /// <summary>A parked B738 ground traffic row for <paramref name="callsign"/>, which the follow and give-way submenus list.</summary>
    public static MenuGroundTrafficRow ParkedRow(string callsign) =>
        new(callsign, "B738", 600, "at parking · gate 1")
        {
            IsMoving = false,
            IsSurfaceShadow = false,
            MeetPoint = null,
        };

    /// <summary>
    /// A preset taxi route row named <paramref name="name"/> sending <paramref name="command"/>, 1,000 ft away by a route
    /// with no segments, which the Preset taxi route submenu lists.
    /// </summary>
    public static TaxiRouteRow PresetRow(string name, string command) =>
        new(TaxiRouteRow.PresetBadge, name, null, null, false, "via B", 1000, command, new TaxiRoute { Segments = [], HoldShortPoints = [] }, []);

    /// <summary>The hold-short rows the Hold short of… submenu lists, whatever the callsign asked about.</summary>
    public List<HoldShortChoice> HoldShortChoices { get; } = [];

    /// <summary>The line heading the Hold short of… rows; null for none.</summary>
    public string? HoldShortRouteLine { get; set; }

    /// <summary>The Hold short of… ladder's line saying where the aircraft is now; null for none.</summary>
    public string? HoldShortMeLine { get; set; }

    /// <summary>Where the Hold short of… ladder's route ends; null for no destination row.</summary>
    public HoldShortDestination? HoldShortDestination { get; set; }

    public List<TaxiRoute?> RoutePreviews { get; } = [];

    /// <summary>How many times the ground traffic was asked for.</summary>
    public int GroundTrafficRequests { get; private set; }

    public IReadOnlyList<MenuGroundTrafficRow> GetGroundTrafficRows(string callsign)
    {
        GroundTrafficRequests++;
        return GroundTraffic;
    }

    /// <summary>The clock a live flyout ticks on: it moves only when the test advances it, so no flyout timer outlives a test.</summary>
    public ManualClock Clock { get; } = new();

    TimeProvider IMenuHost.Clock => Clock;

    /// <summary>The room's controller list the coordination flyouts read, handed over live (not copied); empty by default.</summary>
    public List<OnlineControllerDto> Controllers { get; } = [];

    /// <summary>The TCP the controller operates as; none by default.</summary>
    public string? ActiveTcp { get; set; }

    /// <summary>The position the controller operates as, as the server describes it; none by default.</summary>
    public ActivePositionDto? ActivePosition { get; set; }

    public MenuPositions GetPositions() => new(Controllers, ActiveTcp, ActivePosition);

    /// <summary>The scratchpad length limit the Scratchpad box checks at; the server's default of 3 unless a test sets it.</summary>
    public int ScratchpadMaxLength { get; set; } = 3;

    /// <summary>
    /// The airborne traffic the Report traffic in sight… and Follow traffic… flyouts list, whatever the callsign asked about,
    /// in the order given; none by default.
    /// </summary>
    public List<MenuAirborneTrafficRow> AirborneTraffic { get; } = [];

    public IReadOnlyList<MenuAirborneTrafficRow> GetAirborneTraffic(string callsign) => [.. AirborneTraffic];

    public HoldShortMenu GetHoldShortChoices(string callsign) =>
        new(HoldShortRouteLine, HoldShortMeLine, [.. HoldShortChoices], HoldShortDestination);

    public void SetRoutePreview(TaxiRoute? route) => RoutePreviews.Add(route);

    /// <summary>The route each callsign still has ahead of it; a callsign not listed has none.</summary>
    public Dictionary<string, TaxiRoute> RemainingRoutes { get; } = new(StringComparer.Ordinal);

    public TaxiRoute? GetRemainingTaxiRoute(string callsign) => RemainingRoutes.GetValueOrDefault(callsign);

    /// <summary>Every scope preview the menu asked for, in order, null for a clear.</summary>
    public List<ScopePreview?> ScopePreviews { get; } = [];

    public void SetScopePreview(ScopePreview? preview) => ScopePreviews.Add(preview);

    /// <summary>Every radar cue the menu asked for, in order, null for a clear; the last is the one shown.</summary>
    public List<RadarCue?> RadarCues { get; } = [];

    public void SetRadarCue(RadarCue? cue) => RadarCues.Add(cue);

    /// <summary>The pushback facings the face items list, whatever the callsign asked about.</summary>
    public List<MenuCommandChoice> PushbackFaceChoices { get; } = [];

    /// <summary>The targets the Push back to… submenu lists, whatever the callsign asked about; settled and empty by default.</summary>
    public PushTargetList PushbackTargets { get; set; } = PushTargetList.Ready([]);

    /// <summary>The routes the Preset taxi route submenu lists, whatever the callsign asked about.</summary>
    public List<TaxiRouteRow> PresetTaxiChoices { get; } = [];

    public List<string> PushRouteCallsigns { get; } = [];

    public IReadOnlyList<MenuCommandChoice> GetPushbackFaceChoices(string callsign) => PushbackFaceChoices;

    public PushTargetList GetPushbackTargets(string callsign) => PushbackTargets;

    public IReadOnlyList<TaxiRouteRow> GetPresetTaxiChoices(string callsign) => PresetTaxiChoices;

    /// <summary>The Taxi to runway flyout's runway ends, whatever the callsign asked about; none by default.</summary>
    public TaxiToRunwayMenu TaxiToRunway { get; set; } = TaxiToRunwayMenu.Empty;

    /// <summary>The callsign each Taxi to runway request named, in order.</summary>
    public List<string> TaxiToRunwayRequests { get; } = [];

    public TaxiToRunwayMenu GetTaxiToRunwayChoices(string callsign)
    {
        TaxiToRunwayRequests.Add(callsign);
        return TaxiToRunway;
    }

    /// <summary>The runway's exits the "Exit at" box matches against, per side, whatever the callsign; none by default.</summary>
    public List<ExitAheadDto> RunwayExits { get; } = [];

    public IReadOnlyList<ExitAheadDto> GetRunwayExits(string callsign, ExitSide side) => [.. RunwayExits.Where(exit => exit.Side == side)];

    /// <summary>The Air taxi / Land flyout's destinations, whatever the callsign asked about; none by default.</summary>
    public AirTaxiMenu AirTaxi { get; set; } = AirTaxiMenu.Empty;

    /// <summary>The callsign each Air taxi / Land request named, in order.</summary>
    public List<string> AirTaxiRequests { get; } = [];

    public AirTaxiMenu GetAirTaxiChoices(string callsign)
    {
        AirTaxiRequests.Add(callsign);
        return AirTaxi;
    }

    public void EnterPushRoute(string callsign) => PushRouteCallsigns.Add(callsign);

    /// <summary>The callsign lists the assume-selected item was clicked with, each in the order it was given.</summary>
    public List<IReadOnlyList<string>> AssumeSelectedCalls { get; } = [];

    public Task AssumeSelectedLiveTrafficAsync(IReadOnlyList<string> callsigns)
    {
        AssumeSelectedCalls.Add(callsigns);
        return Task.CompletedTask;
    }

    /// <summary>The callsign and initials each command flyout was opened for, in order.</summary>
    public List<(string Callsign, string Initials)> CommandFlyouts { get; } = [];

    public void ShowCommandFlyout(string callsign, string initials) => CommandFlyouts.Add((callsign, initials));

    /// <summary>The callsign and prefilled note each note flyout was opened with, in order.</summary>
    public List<(string Callsign, string CurrentNote)> NoteFlyouts { get; } = [];

    /// <summary>The command sink the last note flyout was opened with, which a test answers in the flyout's place.</summary>
    public Func<string, Task>? NoteSubmit { get; private set; }

    public void ShowNoteFlyout(string callsign, string currentNote, Func<string, Task> sendCommand)
    {
        NoteFlyouts.Add((callsign, currentNote));
        NoteSubmit = sendCommand;
    }

    /// <summary>An empty Favorite Commands submenu: the recording host has no favorites store.</summary>
    public MenuItem BuildFavorites(IMenuAircraft? aircraft, MenuContext context) => new() { Header = "Favorite Commands" };
}
