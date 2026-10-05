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
    public List<string> GroundTraffic { get; } = [];

    /// <summary>The hold-short choices the Hold short of… submenu lists, whatever the callsign asked about.</summary>
    public List<MenuCommandChoice> HoldShortChoices { get; } = [];

    public List<TaxiRoute?> RoutePreviews { get; } = [];

    public IReadOnlyList<string> GetGroundTrafficCallsigns(string callsign) => GroundTraffic;

    public IReadOnlyList<MenuCommandChoice> GetHoldShortChoices(string callsign) => HoldShortChoices;

    public void SetRoutePreview(TaxiRoute? route) => RoutePreviews.Add(route);

    /// <summary>The pushback facings the face items list, whatever the callsign asked about.</summary>
    public List<MenuCommandChoice> PushbackFaceChoices { get; } = [];

    /// <summary>The stands the Push back to… submenu lists, whatever the callsign asked about.</summary>
    public List<MenuCommandChoice> PushbackToChoices { get; } = [];

    /// <summary>The routes the Preset taxi route submenu lists, whatever the callsign asked about.</summary>
    public List<MenuCommandChoice> PresetTaxiChoices { get; } = [];

    public List<string> PushRouteCallsigns { get; } = [];

    public IReadOnlyList<MenuCommandChoice> GetPushbackFaceChoices(string callsign) => PushbackFaceChoices;

    public IReadOnlyList<MenuCommandChoice> GetPushbackToChoices(string callsign) => PushbackToChoices;

    public IReadOnlyList<MenuCommandChoice> GetPresetTaxiChoices(string callsign) => PresetTaxiChoices;

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
