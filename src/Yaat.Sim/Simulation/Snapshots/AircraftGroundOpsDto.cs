namespace Yaat.Sim.Simulation.Snapshots;

public sealed class AircraftGroundOpsDto
{
    public string? LayoutAirportId { get; init; }
    public TaxiRouteDto? AssignedTaxiRoute { get; init; }
    public string? ParkingSpot { get; init; }
    public string? CurrentTaxiway { get; init; }
    public required bool IsHeld { get; init; }
    public string? GiveWayTarget { get; init; }
    public required bool AutoDeleteExempt { get; init; }
    public required double ConflictBreakRemainingSeconds { get; init; }
    public double? SpeedLimit { get; init; }
    public double? PushbackTrueHeadingDeg { get; init; }
    public required bool HasAnnouncedReady { get; init; }
    public bool IsExpeditingTaxi { get; init; }
}
