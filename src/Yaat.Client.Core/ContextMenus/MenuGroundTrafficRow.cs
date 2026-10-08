namespace Yaat.Client.ContextMenus;

/// <summary>
/// One other aircraft on the ground as the Follow… and Give way to… lists show it, seen from the aircraft the menu
/// commands: its <paramref name="Callsign"/>, the type it shows without prefix or equipment suffix
/// (<paramref name="AircraftType"/>, empty when unknown), how far it is in feet (<paramref name="DistanceFeet"/>), what it
/// is doing and where (<paramref name="State"/>: <c>pushing back · gate 24</c>, <c>taxiing on W · ahead</c>), whether it
/// lists under Moving (<see cref="IsMoving"/>) rather than Parked or holding, and whether it is a live-traffic shadow on
/// the surface (<see cref="IsSurfaceShadow"/>), which Give way to… leaves out.
/// <see cref="RelativeGeometry.GroundTrafficRow"/> builds it.
/// </summary>
public sealed record MenuGroundTrafficRow(string Callsign, string AircraftType, double DistanceFeet, string State)
{
    /// <summary>Whether the row lists under Moving rather than Parked or holding.</summary>
    public required bool IsMoving { get; init; }

    /// <summary>Whether the aircraft is a live-traffic shadow on the surface, which Give way to… leaves out.</summary>
    public required bool IsSurfaceShadow { get; init; }
}
