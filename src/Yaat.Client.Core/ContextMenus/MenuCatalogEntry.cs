using Avalonia.Controls;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// One action in the context-menu catalog: the stable identifier a saved quick-command list stores, the text it
/// shows, the flight rules it is offered under by default, whether it fits the aircraft in front of the controller,
/// and how to build its menu item.
/// </summary>
/// <param name="Id">The stable identifier, never renamed or reused, (see <see cref="MenuIds"/>) that exported preferences carry.</param>
/// <param name="Label">The text the menu item shows; a host-built entry may show a state-dependent header instead.</param>
/// <param name="DefaultFlightRules">
/// The flight rules a quick-command list entry for the action is offered under when it sets none of its own
/// (<see cref="QuickCommandEntry.FlightRules"/> null): <see cref="QuickCommandResolver"/> filters the quick list by them at runtime.
/// </param>
/// <param name="IsApplicable">
/// Whether the action fits the aircraft now. False leaves it out of All Commands; the quick list can still show it where a
/// quick-list widening (<see cref="QuickCommandResolver"/>, the <c>AircraftCommandApplicability.Widens*</c> rules) admits it.
/// </param>
/// <param name="Build">Builds the menu item, or returns null when there is nothing to show for this aircraft.</param>
public sealed record MenuCatalogEntry(
    string Id,
    string Label,
    MenuFlightRules DefaultFlightRules,
    Func<IMenuAircraft?, MenuContext, bool> IsApplicable,
    Func<IMenuAircraft?, MenuContext, IMenuHost, MenuItem?> Build
);
