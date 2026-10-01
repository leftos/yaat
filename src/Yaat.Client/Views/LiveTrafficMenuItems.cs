using Avalonia.Controls;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Sim.Commands;

namespace Yaat.Client.Views;

/// <summary>
/// The ground view's entry to the live-traffic items every surface offers on either side of the hand-off, built by
/// <see cref="SharedMenuGroups"/>. <see cref="Add"/> covers the shadow side: an assumable shadow
/// (<see cref="AircraftCommandApplicability.CanAssume"/>, which is every airborne one) takes the two assume items ahead
/// of the caller's own command groups, because a command sent to an airborne shadow auto-assumes it server-side and
/// then applies — so the groups a simulated aircraft gets apply to it as they are, minus the two the server refuses
/// for a shadow: the ask-pilot queries (<see cref="AircraftCommandApplicability.CanAskPilot"/>) and the flight-plan
/// editor (<see cref="AircraftCommandApplicability.CanEditFlightPlan"/>). A surface shadow is never assumable, so for
/// it nothing is added. <see cref="AddUnassume"/> covers the other side: the simulated aircraft that came from the
/// feed, which may be released back to it.
/// </summary>
public static class LiveTrafficMenuItems
{
    /// <summary>
    /// Appends "Assume control" and "Assume and track" when the shadow is assumable, ahead of the caller's own
    /// command groups; returns whether anything was added.
    /// </summary>
    public static bool Add(ContextMenu menu, AircraftModel ac, Func<string, Task> sendCommand)
    {
        if (!AircraftCommandApplicability.CanAssume(ac))
        {
            return false;
        }

        SharedMenuGroups.AddLiveTrafficAssume(menu.Items, ac, ContextFor(ac), new SendOnlyMenuHost(sendCommand));
        return true;
    }

    /// <summary>Appends "Release to live feed" to the given menu items when the aircraft was assumed from the feed.</summary>
    public static void AddUnassume(ItemCollection items, AircraftModel? ac, Func<string, Task> sendCommand)
    {
        if (SharedMenuGroups.Unassume(ac, ContextFor(ac), new SendOnlyMenuHost(sendCommand)) is { } unassume)
        {
            items.Add(unassume);
        }
    }

    /// <summary>The callsign and initials are already bound into the caller's send delegate, so the context only names the aircraft.</summary>
    private static MenuContext ContextFor(AircraftModel? ac) => new(ac?.Callsign ?? "", "", null, false, VfrCommandsForIfr.EnterFinalOnly);

    /// <summary>A host over a send delegate that already carries the callsign and initials; the live-traffic items need nothing else.</summary>
    private sealed class SendOnlyMenuHost(Func<string, Task> sendCommand) : IMenuHost
    {
        public Task SendAsync(string callsign, string command, string initials) => sendCommand(command);

        public void ShowInputPopup(string placeholder, Func<string, Task> onSubmit) =>
            throw new NotSupportedException("The live-traffic items build no input pickers");

        public MenuItem BuildFavorites(IMenuAircraft? aircraft, MenuContext context) =>
            throw new NotSupportedException("The live-traffic items build no favorites submenu");
    }
}
