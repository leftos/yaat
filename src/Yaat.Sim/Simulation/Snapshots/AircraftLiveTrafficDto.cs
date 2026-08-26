namespace Yaat.Sim.Simulation.Snapshots;

/// <summary>
/// Snapshot form of <see cref="LiveTraffic.AircraftLiveTraffic"/>. Present only on shadow aircraft;
/// <see cref="AircraftSnapshotDto.LiveTraffic"/> is null for every other aircraft and for snapshots
/// written before the field existed.
/// </summary>
public sealed class AircraftLiveTrafficDto
{
    public int Source { get; init; }
    public double ObservedAtSimSeconds { get; init; }
    public double SecondsSinceSample { get; init; }
    public double SampleLat { get; init; }
    public double SampleLon { get; init; }
    public double SampleAltitude { get; init; }
    public double SampleGroundSpeed { get; init; }
    public double SampleTrueTrack { get; init; }
    public double SampleVerticalSpeed { get; init; }
    public double? PreviousSampleAltitude { get; init; }
    public double? PreviousObservedAtSimSeconds { get; init; }
    public bool IsCoasting { get; init; }
    public string? ExternalId { get; init; }
}
