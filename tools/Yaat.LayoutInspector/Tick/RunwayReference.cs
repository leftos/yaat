using Yaat.Sim;
using Yaat.Sim.Data;

namespace Yaat.LayoutInspector.Tick;

/// <summary>
/// Runway centerline reference used by tick-table output to compute signed
/// cross-track (<c>xteFt</c>) and signed heading-error (<c>hdgErr</c>) columns.
/// Wraps the runway threshold position and true heading so callers don't need
/// to touch <see cref="NavigationDatabase"/> directly.
/// </summary>
public readonly record struct RunwayReference(double Lat, double Lon, TrueHeading Heading)
{
    /// <summary>
    /// Loads the referenced runway from the shared NavData. Accepts the same
    /// <c>ICAO/RWY</c> format as TickInspector's <c>--runway</c> flag (e.g.
    /// <c>SFO/28L</c>). Writes a status line to stderr and returns null on
    /// failure so the caller can exit with a useful error.
    /// </summary>
    public static RunwayReference? Load(string runwayArg)
    {
        var parts = runwayArg.Split('/');
        if (parts.Length != 2)
        {
            Console.Error.WriteLine($"error: --tick-ref expects 'ICAO/RWY' (e.g. SFO/28R), got {runwayArg}");
            return null;
        }

        string airport = parts[0].ToUpperInvariant();
        string rwy = parts[1].ToUpperInvariant();

        var runway = NavigationDatabase.Instance.GetRunway(airport, rwy);
        if (runway is null)
        {
            Console.Error.WriteLine($"error: runway {airport}/{rwy} not found in NavData");
            return null;
        }

        Console.Error.WriteLine(
            $"# ref {airport}/{rwy}: threshold=({runway.ThresholdLatitude:F6},{runway.ThresholdLongitude:F6}) hdg={runway.TrueHeading.Degrees:F3}°"
        );
        return new RunwayReference(runway.ThresholdLatitude, runway.ThresholdLongitude, runway.TrueHeading);
    }

    /// <summary>
    /// Signed cross-track distance from the aircraft to the centerline, in feet.
    /// Positive = right of the centerline along the runway heading.
    /// </summary>
    public double CrossTrackFt(double lat, double lon) => GeoMath.SignedCrossTrackDistanceNm(lat, lon, Lat, Lon, Heading) * GeoMath.FeetPerNm;

    /// <summary>
    /// Signed heading error relative to the runway true heading, in degrees.
    /// Positive = clockwise of the runway heading.
    /// </summary>
    public double HeadingErrorDeg(double aircraftHdg) => Heading.SignedAngleTo(new TrueHeading(aircraftHdg));
}
