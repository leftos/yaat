using Avalonia.Controls;
using Yaat.Client.ContextMenus;
using Yaat.Client.Services;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Records every send and popup, and answers a picker at once: an input or filtered list with
/// <paramref name="input"/>, a list with the item whose text is <paramref name="input"/> (an empty input only records the list).
/// </summary>
internal sealed class RecordingMenuHost(string input) : IMenuHost
{
    public List<(string Callsign, string Command, string Initials)> Sent { get; } = [];

    public string[]? FixNames { get; init; }

    /// <summary>The field elevation the altitude picker is answered with; sea level by default, so the list starts at 100 ft.</summary>
    public double FieldElevation { get; init; }

    public List<string?> FieldElevationRequests { get; } = [];

    public List<(IReadOnlyList<string> Items, object? Selected)> ListPopups { get; } = [];

    public List<(string[] Names, IReadOnlyList<string>? Priority)> FilteredListPopups { get; } = [];

    public List<string> DrawRouteCallsigns { get; } = [];

    public List<(string Callsign, string Frd, int Heading, int Altitude, int Speed)> WarpPopups { get; } = [];

    public Func<string, int, int, int, Task>? WarpSubmit { get; private set; }

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

    /// <summary>The blank-submit setting the last input popup was opened with; null before any input popup opened.</summary>
    public BlankInput? LastBlankInput { get; private set; }

    /// <summary>The initial text and caret each input popup was opened with, in order.</summary>
    public List<(string Text, int Caret)> InputSeeds { get; } = [];

    public void ShowInputPopup(string placeholder, BlankInput blank, string initialText, int caretIndex, Func<string, Task> onSubmit)
    {
        InputPlaceholders.Add(placeholder);
        InputSeeds.Add((initialText, caretIndex));
        LastBlankInput = blank;
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

    /// <summary>Every rich-row picker shown, in order.</summary>
    public List<MenuRichList> RichListPopups { get; } = [];

    /// <summary>Records the list and, for a non-empty input, picks the row whose label is the input.</summary>
    public void ShowRichListPopup(MenuRichList list, Action<MenuRichRow> onPick)
    {
        RichListPopups.Add(list);
        if (input.Length > 0)
        {
            onPick(list.Rows.First(row => (row.IsPickable) && (row.Label == input)));
        }
    }

    /// <summary>The minimum vectoring altitude every position is answered with; none by default.</summary>
    public (string Sector, int FloorFtMsl)? Mva { get; init; }

    public (string Sector, int FloorFtMsl)? GetMva(LatLon position) => Mva;

    /// <summary>Records the destination asked about and answers <see cref="FieldElevation"/>.</summary>
    public double GetFieldElevation(string? destination)
    {
        FieldElevationRequests.Add(destination);
        return FieldElevation;
    }

    public void EnterDrawRoute(string callsign) => DrawRouteCallsigns.Add(callsign);

    public void ShowWarpPopup(string callsign, string frd, int heading, int altitude, int speed, Func<string, int, int, int, Task> onSubmit)
    {
        WarpPopups.Add((callsign, frd, heading, altitude, speed));
        WarpSubmit = onSubmit;
    }

    /// <summary>The callsign each flight-plan editor was opened for, in order.</summary>
    public List<string> FlightPlanEditorCallsigns { get; } = [];

    public void OpenFlightPlanEditor(string callsign)
    {
        FlightPlanEditorOpens++;
        FlightPlanEditorCallsigns.Add(callsign);
    }

    /// <summary>The session the menu is built with: initials "AB", no solo training, no VFR commands for IFR aircraft.</summary>
    public MenuSession Session { get; init; } = new("AB", false, VfrCommandsForIfr.None, QuickCommandDefaults.For);

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
        new(callsign, "B738", 600, "at parking · gate 1") { IsMoving = false, IsSurfaceShadow = false };

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

    public List<TaxiRoute?> RoutePreviews { get; } = [];

    public IReadOnlyList<MenuGroundTrafficRow> GetGroundTrafficRows(string callsign) => GroundTraffic;

    /// <summary>The airborne traffic the Report traffic in sight… list offers, whatever the callsign asked about; none by default.</summary>
    public List<MenuTrafficRow> NearbyTraffic { get; } = [];

    public IReadOnlyList<MenuTrafficRow> GetNearbyTraffic(string callsign) => NearbyTraffic;

    public HoldShortMenu GetHoldShortChoices(string callsign) => new(HoldShortRouteLine, HoldShortChoices);

    public void SetRoutePreview(TaxiRoute? route) => RoutePreviews.Add(route);

    /// <summary>The pushback facings the face items list, whatever the callsign asked about.</summary>
    public List<MenuCommandChoice> PushbackFaceChoices { get; } = [];

    /// <summary>The stands the Push back to… submenu lists, whatever the callsign asked about.</summary>
    public List<MenuCommandChoice> PushbackToChoices { get; } = [];

    /// <summary>The routes the Preset taxi route submenu lists, whatever the callsign asked about.</summary>
    public List<TaxiRouteRow> PresetTaxiChoices { get; } = [];

    public List<string> PushRouteCallsigns { get; } = [];

    public IReadOnlyList<MenuCommandChoice> GetPushbackFaceChoices(string callsign) => PushbackFaceChoices;

    public IReadOnlyList<MenuCommandChoice> GetPushbackToChoices(string callsign) => PushbackToChoices;

    public IReadOnlyList<TaxiRouteRow> GetPresetTaxiChoices(string callsign) => PresetTaxiChoices;

    /// <summary>The Taxi to runway submenu's groups, whatever the callsign asked about; none by default.</summary>
    public TaxiToRunwayMenu TaxiToRunway { get; set; } = TaxiToRunwayMenu.Empty;

    /// <summary>The callsign each Taxi to runway request named, in order.</summary>
    public List<string> TaxiToRunwayRequests { get; } = [];

    public TaxiToRunwayMenu GetTaxiToRunwayChoices(string callsign)
    {
        TaxiToRunwayRequests.Add(callsign);
        return TaxiToRunway;
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
