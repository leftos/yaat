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

    public required double WidthFt { get; init; }

    private readonly double? _airportElevationFt;

    /// <summary>
    /// Airport (field) elevation in feet MSL — one value for the whole field, unlike
    /// <see cref="ElevationFt"/>, which is the active end's landing threshold.
    ///
    /// Traffic pattern altitude belongs to the airport, not to a runway end: AIM 4-3-3 recommends
    /// "1,000 feet above ground level" for one pattern flown around the field, and the Chart Supplement
    /// publishes a single TPA. Referencing it to a runway end would tilt the pattern with the runway —
    /// 140 ft between KASE's two thresholds.
    ///
    /// Falls back to the mean of the two ends when unset, which is exact for a level runway (both ends
    /// equal) and keeps hand-built fixtures and pre-existing snapshots on the value they had before the
    /// ends carried their own elevations.
    /// </summary>
    public double AirportElevationFt
    {
        get => _airportElevationFt ?? ((Elevation1Ft + Elevation2Ft) / 2.0);
        init => _airportElevationFt = value;
    }

    // Backward-compatible directional properties
    public double ThresholdLatitude => IsEnd1 ? Lat1 : Lat2;
    public double ThresholdLongitude => IsEnd1 ? Lon1 : Lon2;
    public TrueHeading TrueHeading => IsEnd1 ? TrueHeading1 : TrueHeading2;
    public double ElevationFt => IsEnd1 ? Elevation1Ft : Elevation2Ft;
    public double EndLatitude => IsEnd1 ? Lat2 : Lat1;
    public double EndLongitude => IsEnd1 ? Lon2 : Lon1;

    /// <summary>
    /// Physical pavement length (feet), threshold to threshold. Derived from the two ends' coordinates
    /// rather than stored, so it cannot drift from the geometry every other calculation uses, and it is
    /// the same in both directions by construction.
    ///
    /// This is the only length the runway carries. The nav data's <c>landing_distance_available</c> is
    /// deliberately not stored: it is declared per end and can differ between them (KSJC 12L 8,831 ft
    /// vs 30R 7,597 ft, AIM 4-3-4.d.4), and every caller here wants the physical extent instead — a
    /// takeoff run (pre-threshold pavement is usable in either direction, AIM 2-3-3.b.8.2), a departure
    /// flight path projection, or "crossed the runway end" (7110.65 §3-9-6, §3-10-3). An arrival's
    /// usable distance comes from <c>LandingThreshold</c> instead, which starts at the displaced
    /// threshold.
    /// </summary>
    public double PavementLengthFt => GeoMath.DistanceNm(Lat1, Lon1, Lat2, Lon2) * GeoMath.FeetPerNm;

    private bool IsEnd1 => Id.End1.Equals(Designator, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="designator"/> names this runway's active approach end. Zero-pad-normalizes
    /// the argument first, so the FAA "8R" matches an active "08R" without a raw-string mismatch.
    /// </summary>
    public bool IsActiveEnd(string designator) =>
        Designator.Equals(RunwayIdentifier.NormalizeDesignator(designator), StringComparison.OrdinalIgnoreCase);

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
            WidthFt = WidthFt,
            AirportElevationFt = AirportElevationFt,
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
            WidthFt = dto.WidthFt,
            AirportElevationFt = dto.AirportElevationFt ?? ((dto.Elevation1Ft + dto.Elevation2Ft) / 2.0),
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
            WidthFt = WidthFt,
            AirportElevationFt = AirportElevationFt,
        };
    }
}
