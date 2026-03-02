namespace Yaat.Sim.Data;

/// <summary>
/// Parsed video map geometry: a collection of polylines.
/// </summary>
public sealed class VideoMapData
{
    public required string MapId { get; init; }
    public required List<VideoMapLine> Lines { get; init; }
}

/// <summary>
/// A single polyline (sequence of lat/lon points) from a video map.
/// </summary>
public sealed class VideoMapLine
{
    public required List<(double Lat, double Lon)> Points { get; init; }
}
