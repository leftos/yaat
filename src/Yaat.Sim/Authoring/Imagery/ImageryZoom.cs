using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Authoring.Imagery;

/// <summary>
/// The tile zoom whose pixels come closest to the map's at a latitude: tiles at zoom z are 156543.03 * cos(lat) / 2^z
/// metres a pixel, the map 111320 / (5000 * zoom), so z = log2(7031 * cos(lat) * zoom), rounded.
/// </summary>
public static class ImageryZoom
{
    /// <summary>The highest tile zoom asked for; above it the tiles are drawn larger rather than asked for at 19.</summary>
    public const int MaxZoom = 18;

    /// <summary>The lowest tile zoom asked for; farther out the editor draws no tiles.</summary>
    public const int MinZoom = 12;

    // 156543.03 metres a pixel at zoom 0, times the map's 5000 pixels a degree at zoom 1, over 111320 metres a degree.
    private const double PixelRatio = 7031;

    private static readonly ILogger Log = SimLog.CreateLogger("ImageryZoom");

    /// <summary>The tile zoom for the map at <paramref name="viewportZoom"/> around <paramref name="latDeg"/>: at most
    /// <see cref="MaxZoom"/>, or null when it would be below <see cref="MinZoom"/>.</summary>
    public static int? Choose(double latDeg, double viewportZoom)
    {
        double scale = PixelRatio * Math.Cos(latDeg * Math.PI / 180) * viewportZoom;
        if (!(scale > 0))
        {
            return null;
        }

        int z = (int)Math.Round(Math.Log2(scale), MidpointRounding.AwayFromZero);
        if (z < MinZoom)
        {
            if (Log.IsEnabled(LogLevel.Trace))
            {
                Log.LogTrace("Map zoom {Zoom} at {Lat} is below tile zoom {Min}", viewportZoom, latDeg, MinZoom);
            }
            return null;
        }
        return Math.Min(z, MaxZoom);
    }
}
