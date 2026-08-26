namespace Yaat.Sim.Simulation.Snapshots;

/// <summary>One entry of <see cref="AircraftLiveTrafficDto.History"/>.</summary>
public sealed class LiveTrafficHistoryPointDto
{
    public double ObservedAtSimSeconds { get; init; }
    public double Lat { get; init; }
    public double Lon { get; init; }
    public double AltitudeFt { get; init; }
    public double TrueTrackDeg { get; init; }
}

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
    public List<LiveTrafficHistoryPointDto>? History { get; init; }
    public bool IsCoasting { get; init; }
    public double? AssignedAltitudeFt { get; init; }
    public double? InterimAltitudeFt { get; init; }
    public double? ClearedHeadingDeg { get; init; }
    public double? ClearedSpeedKts { get; init; }
    public string? ClearanceText { get; init; }
    public string? ExternalId { get; init; }
}
