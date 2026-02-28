namespace Yaat.Sim.Phases;

public sealed class RunwayInfo
{
    public required string AirportId { get; init; }
    public required string RunwayId { get; init; }
    public required double ThresholdLatitude { get; init; }
    public required double ThresholdLongitude { get; init; }
    public required double TrueHeading { get; init; }
    public required double ElevationFt { get; init; }
    public required double LengthFt { get; init; }
    public required double WidthFt { get; init; }
    public required double EndLatitude { get; init; }
    public required double EndLongitude { get; init; }
}
