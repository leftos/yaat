using Yaat.Client.Models;

namespace Yaat.Client.Services;

/// <summary>
/// The radar's gate for its relative "follow this traffic" item, which reads the selected aircraft's reported
/// traffic. The relative-context and ground gates the ground view shares live in
/// <see cref="Yaat.Client.ContextMenus.RelativeTraffic"/>.
/// </summary>
public static class RelativeTrafficActions
{
    /// <summary>
    /// True when the radar "follow this traffic" item should be offered: the selected
    /// aircraft is airborne and has reported the right-clicked aircraft in sight. Mirrors
    /// the sim's FOLLOW gate (airborne + traffic-in-sight) in CommandDispatcher.TryAirborneFollow.
    /// </summary>
    public static bool ShouldOfferFollow(AircraftModel selected, string rightClickedCallsign) =>
        !selected.IsOnGround
        && !string.IsNullOrEmpty(rightClickedCallsign)
        && string.Equals(selected.LastReportedTrafficCallsign, rightClickedCallsign, StringComparison.OrdinalIgnoreCase);
}
