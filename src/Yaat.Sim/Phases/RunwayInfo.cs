using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Phases;

/// <summary>
/// Bidirectional runway data. Stores both ends' geometry and selects the
/// active approach end via <see cref="Designator"/>. Backward-compatible
/// properties (ThresholdLatitude, TrueHeading, etc.) use <see cref="Designator"/>
/// to pick the correct end.
/// </summary>
public sealed class RunwayInfo
{
    public required string AirportId { get; init; }
    public required RunwayIdentifier Id { get; init; }
    public required string Designator { get; init; }

    public required double Lat1 { get; init; }
    public required double Lon1 { get; init; }
    public required double Elevation1Ft { get; init; }
    public required double Heading1 { get; init; }
    public required double Lat2 { get; init; }
    public required double Lon2 { get; init; }
    public required double Elevation2Ft { get; init; }
    public required double Heading2 { get; init; }

    public required double LengthFt { get; init; }
    public required double WidthFt { get; init; }

    // Backward-compatible directional properties
    public double ThresholdLatitude => IsEnd1 ? Lat1 : Lat2;
    public double ThresholdLongitude => IsEnd1 ? Lon1 : Lon2;
    public double TrueHeading => IsEnd1 ? Heading1 : Heading2;
    public double ElevationFt => IsEnd1 ? Elevation1Ft : Elevation2Ft;
    public double EndLatitude => IsEnd1 ? Lat2 : Lat1;
    public double EndLongitude => IsEnd1 ? Lon2 : Lon1;

    private bool IsEnd1 => Id.End1.Equals(Designator, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Same physical runway, different active approach direction.
    /// </summary>
    public RunwayInfo ForApproach(string designator)
    {
        return new RunwayInfo
        {
            AirportId = AirportId,
            Id = Id,
            Designator = designator,
            Lat1 = Lat1,
            Lon1 = Lon1,
            Elevation1Ft = Elevation1Ft,
            Heading1 = Heading1,
            Lat2 = Lat2,
            Lon2 = Lon2,
            Elevation2Ft = Elevation2Ft,
            Heading2 = Heading2,
            LengthFt = LengthFt,
            WidthFt = WidthFt,
        };
    }
}
