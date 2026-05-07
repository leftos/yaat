namespace Yaat.Sim.Data.Airspace;

public sealed class AirspaceBoundaryCrossing
{
    public required AirspaceVolume Volume { get; init; }
    public required LatLon Intersection { get; init; }
    public required double DistanceNm { get; init; }
    public required double LookaheadSeconds { get; init; }
}
