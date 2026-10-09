using Avalonia.Controls;
using Yaat.Client.Services;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The host one point menu is built over: it answers each Taxi here, Taxi to runway and Custom taxi… question once per
/// argument set and replays the answer, so the menu's icon strip and its text items, which build those items each in
/// turn, ask the real host (and its route search) once. Every other member goes straight to the host it wraps. It lives
/// as long as one menu build.
/// </summary>
internal sealed class PointMenuHostCache(IMenuHost inner) : IMenuHost
{
    private readonly Dictionary<(string Callsign, int NodeId, string? RunwayEnd), IReadOnlyList<MenuCommandChoice>> _taxiChoices = [];
    private readonly Dictionary<
        (string Callsign, string RunwayName, string RunwayEnd, LatLon Click),
        IReadOnlyList<RunwayHoldShortTarget>
    > _holdShortTargets = [];
    private readonly Dictionary<(int NodeId, string? RunwayEnd), MenuTextSeed> _customTaxiSeeds = [];
    private readonly Dictionary<(string Callsign, int NodeId), bool> _tugReach = [];

    public MenuSession Session => inner.Session;

    public string[]? FixNames => inner.FixNames;

    public IReadOnlyList<string> SuggestFixes(string partial) => inner.SuggestFixes(partial);

    public IReadOnlyList<MenuCommandChoice> GetTaxiChoices(string callsign, GroundNodeDto node, string? runwayEnd)
    {
        (string, int, string?) key = (callsign, node.Id, runwayEnd);
        if (!_taxiChoices.TryGetValue(key, out IReadOnlyList<MenuCommandChoice>? choices))
        {
            choices = inner.GetTaxiChoices(callsign, node, runwayEnd);
            _taxiChoices[key] = choices;
        }

        return choices;
    }

    public IReadOnlyList<RunwayHoldShortTarget> GetRunwayHoldShortTargets(string callsign, string runwayName, string runwayEnd, LatLon click)
    {
        (string, string, string, LatLon) key = (callsign, runwayName, runwayEnd, click);
        if (!_holdShortTargets.TryGetValue(key, out IReadOnlyList<RunwayHoldShortTarget>? targets))
        {
            targets = inner.GetRunwayHoldShortTargets(callsign, runwayName, runwayEnd, click);
            _holdShortTargets[key] = targets;
        }

        return targets;
    }

    public MenuTextSeed GetCustomTaxiSeed(GroundNodeDto node, string? runwayEnd)
    {
        (int, string?) key = (node.Id, runwayEnd);
        if (!_customTaxiSeeds.TryGetValue(key, out MenuTextSeed? seed))
        {
            seed = inner.GetCustomTaxiSeed(node, runwayEnd);
            _customTaxiSeeds[key] = seed;
        }

        return seed;
    }

    public IReadOnlyList<string> GetNodeTaxiwayNames(GroundNodeDto node) => inner.GetNodeTaxiwayNames(node);

    public string? GetHoldShortTaxiwayName(GroundNodeDto node) => inner.GetHoldShortTaxiwayName(node);

    public bool CanTugReach(string callsign, GroundNodeDto node)
    {
        (string, int) key = (callsign, node.Id);
        if (!_tugReach.TryGetValue(key, out bool reaches))
        {
            reaches = inner.CanTugReach(callsign, node);
            _tugReach[key] = reaches;
        }

        return reaches;
    }

    public Task SendAsync(string callsign, string command, string initials) => inner.SendAsync(callsign, command, initials);

    public Task SendGatedAsync(string callsign, string command, string initials) => inner.SendGatedAsync(callsign, command, initials);

    public void ShowInputPopup(string placeholder, BlankInput blank, string initialText, int caretIndex, Func<string, Task> onSubmit) =>
        inner.ShowInputPopup(placeholder, blank, initialText, caretIndex, onSubmit);

    public string? DescribePoint(LatLon position) => inner.DescribePoint(position);

    public void ShowListPopup(IReadOnlyList<object> items, object? selected, Func<object, Task> onPick) =>
        inner.ShowListPopup(items, selected, onPick);

    public void ShowFilteredListPopup(string[] sortedNames, IReadOnlyList<object>? priorityItems, Func<string, Task> onPick) =>
        inner.ShowFilteredListPopup(sortedNames, priorityItems, onPick);

    public void ShowRichListPopup(MenuRichList list, Action<MenuRichRow> onPick) => inner.ShowRichListPopup(list, onPick);

    public (string Sector, int FloorFtMsl)? GetMva(LatLon position) => inner.GetMva(position);

    public IReadOnlyList<PatternRunwayChoice> GetPatternRunwayChoices(string airportId, string aircraftType) =>
        inner.GetPatternRunwayChoices(airportId, aircraftType);

    public double GetFieldElevation(string? destination) => inner.GetFieldElevation(destination);

    public void EnterDrawRoute(string callsign) => inner.EnterDrawRoute(callsign);

    public void ShowWarpPopup(string callsign, string frd, int heading, int altitude, int speed, Func<string, int, int, int, Task> onSubmit) =>
        inner.ShowWarpPopup(callsign, frd, heading, altitude, speed, onSubmit);

    public void ShowCommandFlyout(string callsign, string initials) => inner.ShowCommandFlyout(callsign, initials);

    public void ShowNoteFlyout(string callsign, string currentNote, Func<string, Task> sendCommand) =>
        inner.ShowNoteFlyout(callsign, currentNote, sendCommand);

    public void OpenFlightPlanEditor(string callsign) => inner.OpenFlightPlanEditor(callsign);

    public IReadOnlyList<MenuGroundTrafficRow> GetGroundTrafficRows(string callsign) => inner.GetGroundTrafficRows(callsign);

    public bool IsOnTaxiRoute(string callsign, string otherCallsign) => inner.IsOnTaxiRoute(callsign, otherCallsign);

    public void HighlightAircraft(string? callsign) => inner.HighlightAircraft(callsign);

    public IReadOnlyList<MenuTrafficRow> GetNearbyTraffic(string callsign) => inner.GetNearbyTraffic(callsign);

    public HoldShortMenu GetHoldShortChoices(string callsign) => inner.GetHoldShortChoices(callsign);

    public void SetRoutePreview(TaxiRoute? route) => inner.SetRoutePreview(route);

    public IReadOnlyList<MenuCommandChoice> GetPushbackFaceChoices(string callsign) => inner.GetPushbackFaceChoices(callsign);

    public IReadOnlyList<MenuCommandChoice> GetPushbackToChoices(string callsign) => inner.GetPushbackToChoices(callsign);

    public IReadOnlyList<TaxiRouteRow> GetPresetTaxiChoices(string callsign) => inner.GetPresetTaxiChoices(callsign);

    public TaxiToRunwayMenu GetTaxiToRunwayChoices(string callsign) => inner.GetTaxiToRunwayChoices(callsign);

    public void EnterPushRoute(string callsign) => inner.EnterPushRoute(callsign);

    public MenuItem BuildFavorites(IMenuAircraft? aircraft, MenuContext context) => inner.BuildFavorites(aircraft, context);

    public IReadOnlyList<Control> BuildRpoItems(IReadOnlyList<string> callsigns) => inner.BuildRpoItems(callsigns);

    public Task AssumeSelectedLiveTrafficAsync(IReadOnlyList<string> callsigns) => inner.AssumeSelectedLiveTrafficAsync(callsigns);
}
