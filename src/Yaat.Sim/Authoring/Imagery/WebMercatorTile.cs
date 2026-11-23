using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Authoring.Imagery;

/// <summary>One square of the Web Mercator tile grid: zoom <paramref name="Z"/>, column <paramref name="X"/> from the
/// antimeridian eastward, row <paramref name="Y"/> from the north edge southward.</summary>
/// <param name="Z">The zoom: the grid has 2^Z tiles a side.</param>
/// <param name="X">The column, 0 at 180 W.</param>
/// <param name="Y">The row, 0 at the grid's north edge (about 85.05 N).</param>
public readonly record struct TileKey(int Z, int X, int Y);

/// <summary>A tile's box in EPSG:3857 metres, as an <c>exportImage</c> request's <c>bbox</c> takes it.</summary>
/// <param name="XMin">West edge in metres.</param>
/// <param name="YMin">South edge in metres.</param>
/// <param name="XMax">East edge in metres.</param>
/// <param name="YMax">North edge in metres.</param>
public readonly record struct MercatorBox(double XMin, double YMin, double XMax, double YMax);

/// <summary>A tile's edges in degrees of latitude and longitude.</summary>
/// <param name="South">South edge latitude.</param>
/// <param name="West">West edge longitude.</param>
/// <param name="North">North edge latitude.</param>
/// <param name="East">East edge longitude.</param>
public readonly record struct TileBounds(double South, double West, double North, double East);

/// <summary>
/// The Web Mercator (EPSG:3857) tile math: a tile's box in metres, its edges in degrees, the tile under a point and the
/// tiles covering a latitude and longitude box. Pure: no I/O.
/// </summary>
public static class WebMercatorTile
{
    /// <summary>The width of the whole grid in metres, the equator's length on the sphere EPSG:3857 uses.</summary>
    public const double WorldWidthMetres = 40075016.6856;

    /// <summary>Half the grid's width: the grid runs from minus this to plus this on both axes.</summary>
    public const double OriginShiftMetres = 20037508.3428;

    /// <summary>The highest latitude the grid reaches, where its square ends.</summary>
    public const double MaxLatitude = 85.0511287798;

    private static readonly ILogger Log = SimLog.CreateLogger("WebMercatorTile");

    /// <summary>The tile's box in EPSG:3857 metres.</summary>
    public static MercatorBox Box(TileKey key)
    {
        double size = WorldWidthMetres / Math.Pow(2, key.Z);
        double xmin = -OriginShiftMetres + (key.X * size);
        double ymax = OriginShiftMetres - (key.Y * size);
        return new MercatorBox(xmin, ymax - size, xmin + size, ymax);
    }

    /// <summary>The tile's edges in degrees.</summary>
    public static TileBounds Bounds(TileKey key)
    {
        double n = Math.Pow(2, key.Z);
        return new TileBounds(RowLatitude(key.Y + 1, n), (key.X / n * 360) - 180, RowLatitude(key.Y, n), ((key.X + 1) / n * 360) - 180);
    }

    /// <summary>The point in EPSG:3857 metres.</summary>
    public static (double X, double Y) ToMercator(double latDeg, double lonDeg)
    {
        double lat = Math.Clamp(latDeg, -MaxLatitude, MaxLatitude) * Math.PI / 180;
        double x = lonDeg / 180 * OriginShiftMetres;
        double y = Math.Log(Math.Tan((Math.PI / 4) + (lat / 2))) / Math.PI * OriginShiftMetres;
        return (x, y);
    }

    /// <summary>The tile at zoom <paramref name="z"/> holding the point; a point off the grid gives the nearest edge tile.</summary>
    public static TileKey FromLatLon(double latDeg, double lonDeg, int z)
    {
        double n = Math.Pow(2, z);
        int last = (int)n - 1;
        double lat = Math.Clamp(latDeg, -MaxLatitude, MaxLatitude) * Math.PI / 180;
        double x = Math.Floor((lonDeg + 180) / 360 * n);
        double y = Math.Floor((1 - (Math.Asinh(Math.Tan(lat)) / Math.PI)) / 2 * n);
        return new TileKey(z, (int)Math.Clamp(x, 0, last), (int)Math.Clamp(y, 0, last));
    }

    /// <summary>
    /// The tiles at zoom <paramref name="z"/> covering the box, row by row from the north, each row west to east, clamped to
    /// the grid.
    /// </summary>
    public static IReadOnlyList<TileKey> TilesCovering(double south, double west, double north, double east, int z)
    {
        TileKey northWest = FromLatLon(north, west, z);
        TileKey southEast = FromLatLon(south, east, z);
        var keys = new List<TileKey>();
        for (int y = northWest.Y; y <= southEast.Y; y++)
        {
            for (int x = northWest.X; x <= southEast.X; x++)
            {
                keys.Add(new TileKey(z, x, y));
            }
        }

        if (Log.IsEnabled(LogLevel.Trace))
        {
            Log.LogTrace("{Count} tiles at zoom {Zoom} cover {South},{West} to {North},{East}", keys.Count, z, south, west, north, east);
        }
        return keys;
    }

    private static double RowLatitude(int y, double n) => Math.Atan(Math.Sinh(Math.PI * (1 - (2 * y / n)))) * 180 / Math.PI;
}
