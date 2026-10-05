namespace Yaat.Client.ContextMenus;

/// <summary>
/// The flight rules a quick-command list entry is offered under: its own (<see cref="CatalogQuickCommandEntry.FlightRules"/>),
/// else its catalog default (<see cref="MenuCatalogEntry.DefaultFlightRules"/>). <see cref="QuickCommandResolver"/> applies
/// it at runtime against the aircraft's filed rules, separately from the applicability predicates, so a VFR-only entry can
/// sit in a stored list and still be left out of the quick list for an IFR aircraft (unless the "VFR commands for IFR
/// aircraft" setting admits it).
/// </summary>
public enum MenuFlightRules
{
    /// <summary>Offered for both IFR and VFR aircraft.</summary>
    Both,

    /// <summary>Offered only for an aircraft operating under IFR.</summary>
    IfrOnly,

    /// <summary>Offered only for an aircraft operating under VFR.</summary>
    VfrOnly,
}
