namespace Yaat.Client.ContextMenus;

/// <summary>
/// The flight-rules filter an action carries by default in the quick-command editor, independent of the
/// applicability predicates: a VFR-only action can sit in a stored list and still be filtered out of the menu by
/// <see cref="AircraftCommandApplicability"/> for the aircraft in front of the controller.
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
