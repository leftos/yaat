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

    public override string ToString() => $"({Lat:F6},{Lon:F6})";
}
