namespace Yaat.Sim.Data.Vnas;

public record AsdexAirportInfo(string AirportId, double Lat, double Lon, double Range, double Ceiling);

public record SaidAirportInfo(string AirportId, double Lat, double Lon, double Range);

public record TowerCabAirportInfo(string AirportId, double Lat, double Lon, double VisibilityCeiling);

/// <summary>
/// A strip bay reachable from a given position, with a flag distinguishing
/// the viewer's own facility bays (<see cref="IsExternal"/> = false) from
/// bays owned by another facility (<see cref="IsExternal"/> = true). In the
/// position's own strips window an external bay is a push-only drop-zone;
/// its contents are viewed by opening that facility's own strips tab (see
/// <see cref="AccessibleFacility"/>).
/// </summary>
public sealed record AccessibleBay(FacilityConfig Owner, StripBayConfig Bay, bool IsExternal);

/// <summary>
/// A facility the controller at a given position can open a strips window for.
/// Always includes the position's own facility (when it has flight-strips
/// configuration); also includes descendant facilities with flight-strips
/// configuration to support top-down consolidation (TRACON working its
/// child towers), and any facility the own facility links a bay from via
/// <see cref="FlightStripsConfig.ExternalBays"/> — so a tower that scans
/// strips to its parent TRACON can also open that TRACON's bays.
/// <see cref="IsStudentFacility"/> marks the position's own facility so the
/// client can pre-select it on scenario load.
/// </summary>
public sealed record AccessibleFacility(string FacilityId, string FacilityName, bool IsStudentFacility);
