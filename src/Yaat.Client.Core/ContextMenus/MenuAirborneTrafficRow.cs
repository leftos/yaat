namespace Yaat.Client.ContextMenus;

/// <summary>
/// One other airborne aircraft the traffic flyouts list, as seen from the aircraft the menu commands: where it is
/// (<paramref name="Traffic"/>: callsign, type, distance, clock position, altitude difference), its direction of flight as an
/// 8-point word (<paramref name="Direction"/>, <c>eastbound</c>) and the pattern leg it flies (<paramref name="Leg"/>,
/// <c>left downwind 28L</c>; null outside a pattern). <see cref="AirborneTrafficRows.Row"/> builds it.
/// </summary>
public sealed record MenuAirborneTrafficRow(MenuTrafficRow Traffic, string Direction, string? Leg)
{
    /// <summary>The aircraft's callsign.</summary>
    public string Callsign => Traffic.Callsign;
}
