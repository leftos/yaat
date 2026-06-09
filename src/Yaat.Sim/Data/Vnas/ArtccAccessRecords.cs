namespace Yaat.Sim.Data.Vnas;

public record AsdexAirportInfo(string AirportId, double Lat, double Lon, double Range, double Ceiling);

public record SaidAirportInfo(string AirportId, double Lat, double Lon, double Range, double Ceiling);

public record TowerCabAirportInfo(string AirportId, double Lat, double Lon, double VisibilityCeiling);

/// <summary>
/// A strip bay reachable from a given position, with a flag distinguishing
/// the viewer's own facility bays (<see cref="IsExternal"/> = false) from
/// linked external bays (<see cref="IsExternal"/> = true). External bays are
/// valid push targets but cannot be opened for viewing; their strips live on
/// the owning facility's own window.
/// </summary>
public sealed record AccessibleBay(FacilityConfig Owner, StripBayConfig Bay, bool IsExternal);

/// <summary>
/// A facility the controller at a given position can open strip windows for.
/// Always includes the position's own facility (when it has flight-strips
/// configuration); also includes descendant facilities with flight-strips
/// configuration to support top-down consolidation (TRACON working its
/// child towers). <see cref="IsStudentFacility"/> marks the position's own
/// facility so the client can pre-select it on scenario load.
/// </summary>
public sealed record AccessibleFacility(string FacilityId, string FacilityName, bool IsStudentFacility);
