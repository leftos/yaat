using Avalonia.Controls;
using Yaat.Client.ContextMenus;
using Yaat.Client.Services;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// A menu host with its sends captured into <see cref="Sent"/> instead of going to a server, which a test has none of,
/// and its session sending with <paramref name="sessionInitials"/>; every other member is <paramref name="inner"/>'s own, so a
/// menu built over it answers, previews and draws through the real host.
/// </summary>
internal sealed class SendCapturingHost(IMenuHost inner, string sessionInitials) : IMenuHost
{
    public List<(string Callsign, string Command, string Initials)> Sent { get; } = [];

    public MenuSession Session => inner.Session with { Initials = sessionInitials };

    public Task SendAsync(string callsign, string command, string initials)
    {
        Sent.Add((callsign, command, initials));
        return Task.CompletedTask;
    }

    /// <summary>Captured into <see cref="Sent"/> like an ungated send; the gate itself is the real host's.</summary>
    public Task SendGatedAsync(string callsign, string command, string initials) => SendAsync(callsign, command, initials);

    public void ShowInputPopup(string placeholder, BlankInput blank, string initialText, int caretIndex, Func<string, Task> onSubmit) =>
        inner.ShowInputPopup(placeholder, blank, initialText, caretIndex, onSubmit);

    public string? DescribePoint(LatLon position) => inner.DescribePoint(position);

    public IReadOnlyList<MenuCommandChoice> GetTaxiChoices(string callsign, GroundNodeDto node, string? runwayEnd) =>
        inner.GetTaxiChoices(callsign, node, runwayEnd);

    public IReadOnlyList<RunwayHoldShortTarget> GetRunwayHoldShortTargets(string callsign, string runwayName, string runwayEnd, LatLon click) =>
        inner.GetRunwayHoldShortTargets(callsign, runwayName, runwayEnd, click);

    public MenuTextSeed GetCustomTaxiSeed(GroundNodeDto node, string? runwayEnd) => inner.GetCustomTaxiSeed(node, runwayEnd);

    public IReadOnlyList<string> GetNodeTaxiwayNames(GroundNodeDto node) => inner.GetNodeTaxiwayNames(node);

    public string? GetHoldShortTaxiwayName(GroundNodeDto node) => inner.GetHoldShortTaxiwayName(node);

    public bool CanTugReach(string callsign, GroundNodeDto node) => inner.CanTugReach(callsign, node);

    public void ShowListPopup(IReadOnlyList<object> items, object? selected, Func<object, Task> onPick) =>
        inner.ShowListPopup(items, selected, onPick);

    public void ShowFilteredListPopup(string[] sortedNames, IReadOnlyList<object>? priorityItems, Func<string, Task> onPick) =>
        inner.ShowFilteredListPopup(sortedNames, priorityItems, onPick);

    public void ShowRichListPopup(MenuRichList list, Action<MenuRichRow> onPick) => inner.ShowRichListPopup(list, onPick);

    public (string Sector, int FloorFtMsl)? GetMva(LatLon position) => inner.GetMva(position);

    public string[]? FixNames => inner.FixNames;

    public double GetFieldElevation(string? destination) => inner.GetFieldElevation(destination);

    public void EnterDrawRoute(string callsign) => inner.EnterDrawRoute(callsign);

    public void ShowWarpPopup(string callsign, string frd, int heading, int altitude, int speed, Func<string, int, int, int, Task> onSubmit) =>
        inner.ShowWarpPopup(callsign, frd, heading, altitude, speed, onSubmit);

    public void ShowCommandFlyout(string callsign, string initials) => inner.ShowCommandFlyout(callsign, initials);

    public void ShowNoteFlyout(string callsign, string currentNote, Func<string, Task> sendCommand) =>
        inner.ShowNoteFlyout(callsign, currentNote, sendCommand);

    public void OpenFlightPlanEditor(string callsign) => inner.OpenFlightPlanEditor(callsign);

    public IReadOnlyList<string> GetGroundTrafficCallsigns(string callsign) => inner.GetGroundTrafficCallsigns(callsign);

    public IReadOnlyList<MenuCommandChoice> GetHoldShortChoices(string callsign) => inner.GetHoldShortChoices(callsign);

    public void SetRoutePreview(TaxiRoute? route) => inner.SetRoutePreview(route);

    public IReadOnlyList<MenuCommandChoice> GetPushbackFaceChoices(string callsign) => inner.GetPushbackFaceChoices(callsign);

    public IReadOnlyList<MenuCommandChoice> GetPushbackToChoices(string callsign) => inner.GetPushbackToChoices(callsign);

    public IReadOnlyList<MenuCommandChoice> GetPresetTaxiChoices(string callsign) => inner.GetPresetTaxiChoices(callsign);

    public void EnterPushRoute(string callsign) => inner.EnterPushRoute(callsign);

    public MenuItem BuildFavorites(IMenuAircraft? aircraft, MenuContext context) => inner.BuildFavorites(aircraft, context);

    public IReadOnlyList<Control> BuildRpoItems(IReadOnlyList<string> callsigns) => inner.BuildRpoItems(callsigns);

    public Task AssumeSelectedLiveTrafficAsync(IReadOnlyList<string> callsigns) => inner.AssumeSelectedLiveTrafficAsync(callsigns);
}
