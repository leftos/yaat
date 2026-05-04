namespace Yaat.Sim.Simulation.Snapshots;

public sealed class AircraftFlightPlanDto
{
    public required bool HasFlightPlan { get; init; }

    /// <summary>
    /// Filed aircraft type — see <see cref="AircraftFlightPlan.AircraftType"/>.
    /// Plain <c>set</c> with default <c>""</c> so older snapshots (pre-schema-v4) deserialize
    /// cleanly with the default and <see cref="SnapshotSchemaMigrator"/> can mutate the field
    /// in place to seed it from the parent <see cref="AircraftSnapshotDto.AircraftType"/>.
    /// </summary>
    public string AircraftType { get; set; } = "";
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
