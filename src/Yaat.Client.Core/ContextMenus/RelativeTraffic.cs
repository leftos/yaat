using System.Diagnostics.CodeAnalysis;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// Gating for the "relative traffic" context-menu items shown when one aircraft is selected and the controller
/// right-clicks a different aircraft. Those items issue a command to the SELECTED aircraft that references the
/// right-clicked aircraft as traffic: report in sight and follow for an airborne pair (RTIS / FOLLOW), give way and
/// follow for a ground pair (GW / FOLLOWG). A mixed pair gets none of them.
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
    /// True when the airborne relative items ("report in sight" and "follow") should be offered: the context's
    /// previous selection is another, controllable aircraft and both it and the right-clicked one are airborne.
    /// </summary>
    public static bool OffersAirborneRelative(IMenuAircraft? rightClicked, MenuContext context)
    {
        if ((context.PreviousSelection is not { } selected) || (rightClicked is null))
        {
            return false;
        }

        return HasRelativeContext(selected, context.Callsign)
            && AircraftCommandApplicability.IsControllable(selected)
            && ShouldOfferAirborne(selected, rightClicked);
    }

    /// <summary>
    /// True when the airborne "follow" item should be offered on top of the report-in-sight one: the pair is airborne
    /// (<see cref="OffersAirborneRelative"/>) and the selected aircraft has reported the right-clicked one in sight.
    /// </summary>
    public static bool OffersAirborneFollow(IMenuAircraft? rightClicked, MenuContext context)
    {
        if (context.PreviousSelection is not { } selected)
        {
            return false;
        }

        return OffersAirborneRelative(rightClicked, context) && ShouldOfferFollow(selected, context.Callsign);
    }

    /// <summary>True when both the selected and right-clicked aircraft are airborne.</summary>
    private static bool ShouldOfferAirborne(IMenuAircraft selected, IMenuAircraft rightClicked) =>
        (!selected.IsOnGround) && (!rightClicked.IsOnGround);

    /// <summary>
    /// True when the selected aircraft may follow the right-clicked one in the air: it is airborne and has reported
    /// that callsign as traffic in sight. Mirrors the sim's FOLLOW gate (airborne + traffic in sight) in
    /// <c>CommandDispatcher.TryAirborneFollow</c>.
    /// </summary>
    public static bool ShouldOfferFollow(IMenuAircraft selected, string rightClickedCallsign) =>
        (!selected.IsOnGround)
        && !string.IsNullOrEmpty(rightClickedCallsign)
        && string.Equals(selected.LastReportedTrafficCallsign, rightClickedCallsign, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the ground "give way to" / "follow" items should be offered: both the
    /// selected and right-clicked aircraft are on the ground.
    /// </summary>
    public static bool ShouldOfferGroundActions(IMenuAircraft selected, IMenuAircraft rightClicked) => selected.IsOnGround && rightClicked.IsOnGround;

    /// <summary>
    /// True when the ground relative items should be offered: the context's previous selection is another, controllable
    /// aircraft and both it and the right-clicked one are on the ground.
    /// </summary>
    public static bool OffersGroundRelative(IMenuAircraft? rightClicked, MenuContext context)
    {
        if ((context.PreviousSelection is not { } selected) || (rightClicked is null))
        {
            return false;
        }

        return HasRelativeContext(selected, context.Callsign)
            && AircraftCommandApplicability.IsControllable(selected)
            && ShouldOfferGroundActions(selected, rightClicked);
    }
}
