using Avalonia.Controls;
using Yaat.Client.ContextMenus;
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

    public void ShowInputPopup(string placeholder, BlankInput blank, Func<string, Task> onSubmit) =>
        inner.ShowInputPopup(placeholder, blank, onSubmit);

    public MenuHostCapabilities Capabilities => inner.Capabilities;

    public void ShowListPopup(IReadOnlyList<object> items, object? selected, Func<object, Task> onPick) =>
        inner.ShowListPopup(items, selected, onPick);

    public void ShowFilteredListPopup(string[] sortedNames, IReadOnlyList<object>? priorityItems, Func<string, Task> onPick) =>
        inner.ShowFilteredListPopup(sortedNames, priorityItems, onPick);

    public string[]? FixNames => inner.FixNames;

    public double GetFieldElevation(string? destination) => inner.GetFieldElevation(destination);

    public void EnterDrawRoute(string callsign) => inner.EnterDrawRoute(callsign);

    public void ShowWarpPopup(string callsign, int heading, int altitude, int speed, Func<string, int, int, int, Task> onSubmit) =>
        inner.ShowWarpPopup(callsign, heading, altitude, speed, onSubmit);

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
