namespace Yaat.Sim.Simulation.Snapshots;

public sealed class AircraftProcedureDto
{
    public string? ActiveSidId { get; init; }
    public string? ActiveStarId { get; init; }
    public string? DepartureRunway { get; init; }
    public string? DestinationRunway { get; init; }
    public required bool SidViaMode { get; init; }
    public required bool StarViaMode { get; init; }
    public int? SidViaCeiling { get; init; }
    public int? StarViaFloor { get; init; }
    public required bool SpeedRestrictionsDeleted { get; init; }
    public required bool IsExpediting { get; init; }
    public double? LastProcedureSpeedKts { get; init; }
}
