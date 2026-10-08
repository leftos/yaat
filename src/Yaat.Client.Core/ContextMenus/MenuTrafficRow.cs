namespace Yaat.Client.ContextMenus;

/// <summary>
/// One other aircraft a traffic list offers, as seen from the aircraft the menu commands: its
/// <paramref name="Callsign"/>, the type it shows (<paramref name="AircraftType"/>, empty when unknown), how far it is
/// (<paramref name="DistanceNm"/>), where it is on the clock from the commanding aircraft's heading
/// (<paramref name="ClockPosition"/>, 1 to 12) and how far above (positive) or below (negative) it is, rounded to 100 ft
/// with 0 for under 100 ft (<paramref name="AltitudeDeltaFeet"/>). <see cref="RelativeGeometry.TrafficRow"/> builds it.
/// </summary>
public sealed record MenuTrafficRow(string Callsign, string AircraftType, double DistanceNm, int ClockPosition, int AltitudeDeltaFeet);
