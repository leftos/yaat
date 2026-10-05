namespace Yaat.Client.ContextMenus;

/// <summary>
/// One entry of a situation's quick-command list: a catalog action, and the flight rules it is offered under when they
/// differ from the action's catalog default.
/// </summary>
/// <param name="CatalogId">The catalog action's stable identifier (see <see cref="MenuIds"/>).</param>
/// <param name="FlightRules">
/// The flight rules this entry is offered under; null takes the catalog entry's <see cref="MenuCatalogEntry.DefaultFlightRules"/>.
/// </param>
public sealed record QuickCommandEntry(string CatalogId, MenuFlightRules? FlightRules);
