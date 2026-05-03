namespace Yaat.Sim;

/// <summary>
/// Geographic coordinate (latitude, longitude) in degrees. Value type — no heap
/// allocation. Equality, hashing, and deconstruction come from record struct.
/// Component names match the CRC <c>Point</c> DTO and the existing tuple
/// convention used across the codebase.
/// </summary>
/// <param name="Lat">Latitude in degrees. Positive north, negative south.</param>
/// <param name="Lon">Longitude in degrees. Positive east, negative west.</param>
public readonly record struct LatLon(double Lat, double Lon)
{
    /// <summary>Origin (0, 0). Not useful for real navigation — mostly a cache sentinel.</summary>
    public static readonly LatLon Zero = new(0.0, 0.0);

    /// <summary>
    /// Linear interpolation between two coordinates. <c>t = 0</c> returns
    /// <paramref name="from"/>; <c>t = 1</c> returns <paramref name="to"/>.
    /// <c>t</c> is clamped to [0, 1]. Linear lat/lon (not great-circle); accurate
    /// to sub-metre over the &lt;~2 NM spans this is intended for (e.g. interpolating
    /// a published MAP fix toward the runway threshold during the final-approach
    /// alignment ramp). Use <see cref="GeoMath.ProjectPoint(LatLon, TrueHeading, double)"/>
    /// for precise great-circle work over longer distances.
    /// </summary>
    public static LatLon Lerp(LatLon from, LatLon to, double t)
    {
        double clamped = Math.Clamp(t, 0.0, 1.0);
        return new LatLon(from.Lat + ((to.Lat - from.Lat) * clamped), from.Lon + ((to.Lon - from.Lon) * clamped));
    }

    public override string ToString() => $"({Lat:F6},{Lon:F6})";
}
