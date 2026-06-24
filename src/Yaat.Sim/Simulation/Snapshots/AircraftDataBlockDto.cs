namespace Yaat.Sim.Simulation.Snapshots;

public sealed class AircraftDataBlockDto
{
    public required int Binding { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string? DetachedId { get; init; }
    public TrackOwnerDto? CreatedBy { get; init; }
}
