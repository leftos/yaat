namespace Yaat.Client.Views.Scope.Toolbar.Dcb;

/// <summary>The shapes a STARS DCB tile comes in.</summary>
public enum DcbTileShape
{
    /// <summary>A full-height tile, filling a column of the bar.</summary>
    Full,

    /// <summary>A half-height tile, two of which stack in one column with a gap between them.</summary>
    Half,

    /// <summary>A full-height tile narrower than <see cref="Full"/>, filling a column of its own.</summary>
    Weather,
}

/// <summary>The YAAT-size dimensions of the STARS DCB: CRC's 76x80, 76x40 and 49x80 tiles scaled into a 45 DIP bar.</summary>
public static class DcbTileSizes
{
    /// <summary>The bar's height, which a full or weather tile fills.</summary>
    public const double BarHeight = 45;

    /// <summary>The width of a full or half tile.</summary>
    public const double TileWidth = 43;

    /// <summary>The width of a weather tile.</summary>
    public const double WeatherWidth = 28;

    /// <summary>The height of a half tile.</summary>
    public const double HalfHeight = 22;

    /// <summary>The gap between two half tiles stacked in one column.</summary>
    public const double HalfGap = 1;
}

/// <summary>Each tile shape's size at YAAT size.</summary>
public static class DcbTileShapeExtensions
{
    /// <summary>The width of a tile of <paramref name="shape"/>.</summary>
    public static double Width(this DcbTileShape shape) => (shape == DcbTileShape.Weather) ? DcbTileSizes.WeatherWidth : DcbTileSizes.TileWidth;

    /// <summary>The height of a tile of <paramref name="shape"/>.</summary>
    public static double Height(this DcbTileShape shape) => (shape == DcbTileShape.Half) ? DcbTileSizes.HalfHeight : DcbTileSizes.BarHeight;
}
