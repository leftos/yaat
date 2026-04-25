namespace Yaat.Sim.Simulation.Snapshots;

public sealed class AircraftGhostTrackDto
{
    public required bool IsUnsupported { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string? AirportId { get; init; }
    public string? RunwayId { get; init; }
    public required bool IsVehicle { get; init; }
}
