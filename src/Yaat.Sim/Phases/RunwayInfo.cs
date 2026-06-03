using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation.Snapshots;

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

    private readonly string _designator = "";

    /// <summary>
    /// Active approach-end designator, normalized to the same zero-padded form as
    /// <see cref="RunwayIdentifier.End1"/>/<c>End2</c> (e.g. "2" → "02"). A raw single-digit
    /// designator would otherwise fail the <see cref="IsEnd1"/> end-selection comparison — flipping
    /// the runway to the opposite end — and would not match the "RWY02/20" centerline edges the
    /// runway-exit search walks.
    /// </summary>
    public required string Designator
    {
        get => _designator;
        init => _designator = RunwayIdentifier.NormalizeDesignator(value);
    }

    public required double Lat1 { get; init; }
    public required double Lon1 { get; init; }
    public required double Elevation1Ft { get; init; }
    public required TrueHeading TrueHeading1 { get; init; }
    public required double Lat2 { get; init; }
    public required double Lon2 { get; init; }
    public required double Elevation2Ft { get; init; }
    public required TrueHeading TrueHeading2 { get; init; }

    public required double LengthFt { get; init; }
    public required double WidthFt { get; init; }

    // Backward-compatible directional properties
    public double ThresholdLatitude => IsEnd1 ? Lat1 : Lat2;
    public double ThresholdLongitude => IsEnd1 ? Lon1 : Lon2;
    public TrueHeading TrueHeading => IsEnd1 ? TrueHeading1 : TrueHeading2;
    public double ElevationFt => IsEnd1 ? Elevation1Ft : Elevation2Ft;
    public double EndLatitude => IsEnd1 ? Lat2 : Lat1;
    public double EndLongitude => IsEnd1 ? Lon2 : Lon1;

    private bool IsEnd1 => Id.End1.Equals(Designator, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Same physical runway, different active approach direction.
    /// </summary>
    public RunwayInfoDto ToSnapshot() =>
        new()
        {
            AirportId = AirportId,
            End1 = Id.End1,
            End2 = Id.End2,
            Designator = Designator,
            Lat1 = Lat1,
            Lon1 = Lon1,
            Elevation1Ft = Elevation1Ft,
            TrueHeading1Deg = TrueHeading1.Degrees,
            Lat2 = Lat2,
            Lon2 = Lon2,
            Elevation2Ft = Elevation2Ft,
            TrueHeading2Deg = TrueHeading2.Degrees,
            LengthFt = LengthFt,
            WidthFt = WidthFt,
        };

    public static RunwayInfo FromSnapshot(RunwayInfoDto dto) =>
        new()
        {
            AirportId = dto.AirportId,
            Id = new RunwayIdentifier(dto.End1, dto.End2),
            Designator = dto.Designator,
            Lat1 = dto.Lat1,
            Lon1 = dto.Lon1,
            Elevation1Ft = dto.Elevation1Ft,
            TrueHeading1 = new TrueHeading(dto.TrueHeading1Deg),
            Lat2 = dto.Lat2,
            Lon2 = dto.Lon2,
            Elevation2Ft = dto.Elevation2Ft,
            TrueHeading2 = new TrueHeading(dto.TrueHeading2Deg),
            LengthFt = dto.LengthFt,
            WidthFt = dto.WidthFt,
        };

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
            TrueHeading1 = TrueHeading1,
            Lat2 = Lat2,
            Lon2 = Lon2,
            Elevation2Ft = Elevation2Ft,
            TrueHeading2 = TrueHeading2,
            LengthFt = LengthFt,
            WidthFt = WidthFt,
        };
    }
}
