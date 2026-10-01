using System.Diagnostics.CodeAnalysis;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// Gating for the "relative traffic" context-menu items shown when one aircraft is selected and the controller
/// right-clicks a different aircraft. Those items issue a command to the SELECTED aircraft that references the
/// right-clicked aircraft as traffic (RTIS / FOLLOW on the radar, GIVEWAY / FOLLOWG on the ground). The radar's
/// follow gate, which reads the selected aircraft's reported traffic, stays with the radar.
/// </summary>
public static class RelativeTraffic
{
    /// <summary>
    /// True when <paramref name="selected"/> is a different aircraft than the one
    /// right-clicked — i.e. there is a selected aircraft to issue relative commands to.
    /// </summary>
    public static bool HasRelativeContext([NotNullWhen(true)] IMenuAircraft? selected, string rightClickedCallsign) =>
        selected is not null
        && !string.IsNullOrEmpty(rightClickedCallsign)
        && !string.Equals(selected.Callsign, rightClickedCallsign, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the ground "give way to" / "follow" items should be offered: both the
    /// selected and right-clicked aircraft are on the ground.
    /// </summary>
    public static bool ShouldOfferGroundActions(IMenuAircraft selected, IMenuAircraft rightClicked) => selected.IsOnGround && rightClicked.IsOnGround;

    /// <summary>
    /// True when the ground view offers the relative "give way to" / "follow" items for <paramref name="rightClicked"/>:
    /// the context's previous selection is another aircraft and both are on the ground.
    /// </summary>
    public static bool OffersGroundRelative(IMenuAircraft? rightClicked, MenuContext context) =>
        rightClicked is not null
        && HasRelativeContext(context.PreviousSelection, context.Callsign)
        && ShouldOfferGroundActions(context.PreviousSelection, rightClicked);
}
