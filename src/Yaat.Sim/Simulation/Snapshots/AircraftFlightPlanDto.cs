namespace Yaat.Sim.Simulation.Snapshots;

public sealed class AircraftFlightPlanDto
{
    public required bool HasFlightPlan { get; init; }
    public required string Departure { get; init; }
    public required string Destination { get; init; }
    public required string Route { get; init; }
    public required string Remarks { get; init; }
    public int RevisionNumber { get; init; }
    public required string EquipmentSuffix { get; init; }
    public required string FlightRules { get; init; }
    public required int CruiseAltitude { get; init; }
    public required int CruiseSpeed { get; init; }
    public TrackOwnerDto? CreatedByOwner { get; init; }
}
