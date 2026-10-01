using Avalonia.Controls;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// One action in the context-menu catalog: the stable identifier a saved quick-command list stores, the text it
/// shows, the flight rules it is offered under by default, whether it fits the aircraft in front of the controller,
/// and how to build its menu item.
/// </summary>
/// <param name="Id">The stable, append-only identifier (see <see cref="MenuIds"/>) that exported preferences carry.</param>
/// <param name="Label">The text the menu item shows; a host-built entry may show a state-dependent header instead.</param>
/// <param name="DefaultFlightRules">The flight rules the quick-command editor offers the action under before the controller changes it.</param>
/// <param name="IsApplicable">Whether the action fits the aircraft now; false hides it from the menu.</param>
/// <param name="Build">Builds the menu item, or returns null when there is nothing to show for this aircraft.</param>
public sealed record MenuCatalogEntry(
    string Id,
    string Label,
    MenuFlightRules DefaultFlightRules,
    Func<IMenuAircraft?, MenuContext, bool> IsApplicable,
    Func<IMenuAircraft?, MenuContext, IMenuHost, MenuItem?> Build
);
