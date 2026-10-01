using Yaat.Sim.Data.Airport;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// One item of a submenu whose choices the host answers: the text the item shows, the finished command it sends, and
/// the taxi route the surface previews while the pointer is over it.
/// </summary>
/// <param name="Label">The text the item shows.</param>
/// <param name="Command">The finished command the item sends for the menu's aircraft.</param>
/// <param name="Preview">The route the surface previews on hover; null when the choice has nothing to preview.</param>
public sealed record MenuCommandChoice(string Label, string Command, TaxiRoute? Preview);
