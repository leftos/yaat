using Yaat.Client.Models;

namespace Yaat.Client.Services;

/// <summary>
/// Pure gating logic for the "relative traffic" context-menu items shown when one
/// aircraft is selected and the controller right-clicks a different aircraft. Those
/// items issue a command to the SELECTED aircraft that references the right-clicked
/// aircraft as traffic (RTIS / FOLLOW in radar, GIVEWAY / FOLLOWG on the ground).
/// </summary>
public static class RelativeTrafficActions
{
    /// <summary>
    /// True when <paramref name="selected"/> is a different aircraft than the one
    /// right-clicked — i.e. there is a selected aircraft to issue relative commands to.
    /// </summary>
    public static bool HasRelativeContext(AircraftModel? selected, string rightClickedCallsign) =>
        selected is not null
        && !string.IsNullOrEmpty(rightClickedCallsign)
        && !string.Equals(selected.Callsign, rightClickedCallsign, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the radar "follow this traffic" item should be offered: the selected
    /// aircraft is airborne and has reported the right-clicked aircraft in sight. Mirrors
    /// the sim's FOLLOW gate (airborne + traffic-in-sight) in CommandDispatcher.TryAirborneFollow.
    /// </summary>
    public static bool ShouldOfferFollow(AircraftModel selected, string rightClickedCallsign) =>
        !selected.IsOnGround
        && !string.IsNullOrEmpty(rightClickedCallsign)
        && string.Equals(selected.LastReportedTrafficCallsign, rightClickedCallsign, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the ground "give way to" / "follow" items should be offered: both the
    /// selected and right-clicked aircraft are on the ground.
    /// </summary>
    public static bool ShouldOfferGroundActions(AircraftModel selected, AircraftModel rightClicked) => selected.IsOnGround && rightClicked.IsOnGround;
}
