using Xunit;
using Yaat.Sim.Authoring.Imagery;

namespace Yaat.Sim.Tests.Authoring.Imagery;

/// <summary>
/// The Web Mercator tile math the USGS imagery layer requests by: a tile's EPSG:3857 box, its corners as latitude and
/// longitude, the tile under a point and the tiles covering a box.
/// </summary>
public sealed class WebMercatorTileTests
{
    private const double KoakLat = 37.7213;
    private const double KoakLon = -122.2207;

    [Theory]
    [InlineData(17, 21038, 50690, -13605179.54, 4538830.74, -13604873.79, 4539136.49)]
    [InlineData(18, 42077, 101381, -13605026.66, 4538830.74, -13604873.79, 4538983.61)]
    public void The_Box_Matches_The_Research_Vectors(int z, int x, int y, double xmin, double ymin, double xmax, double ymax)
    {
        MercatorBox box = WebMercatorTile.Box(new TileKey(z, x, y));

        Assert.Equal(xmin, box.XMin, 0.01);
        Assert.Equal(ymin, box.YMin, 0.01);
        Assert.Equal(xmax, box.XMax, 0.01);
        Assert.Equal(ymax, box.YMax, 0.01);
    }

    [Fact]
    public void The_Tile_Under_Koak_At_Zoom_17_Holds_Its_Mercator_Point()
    {
        TileKey key = WebMercatorTile.FromLatLon(KoakLat, KoakLon, 17);
        (double mx, double my) = WebMercatorTile.ToMercator(KoakLat, KoakLon);
        MercatorBox box = WebMercatorTile.Box(key);

        Assert.Equal(17, key.Z);
        Assert.InRange(mx, box.XMin, box.XMax);
        Assert.InRange(my, box.YMin, box.YMax);
    }

    [Fact]
    public void A_Tiles_Corners_Map_Back_To_The_Tile()
    {
        var key = new TileKey(17, 21038, 50690);
        TileBounds bounds = WebMercatorTile.Bounds(key);
        double latStep = (bounds.North - bounds.South) / 1000;
        double lonStep = (bounds.East - bounds.West) / 1000;

        Assert.Equal(key, WebMercatorTile.FromLatLon(bounds.North - latStep, bounds.West + lonStep, 17));
        Assert.Equal(key, WebMercatorTile.FromLatLon(bounds.South + latStep, bounds.East - lonStep, 17));
        Assert.Equal(new TileKey(17, 21039, 50691), WebMercatorTile.FromLatLon(bounds.South - latStep, bounds.East + lonStep, 17));
    }

    [Fact]
    public void The_Corners_Agree_With_The_Mercator_Box()
    {
        var key = new TileKey(18, 42077, 101381);
        TileBounds bounds = WebMercatorTile.Bounds(key);
        MercatorBox box = WebMercatorTile.Box(key);
        (double westX, double northY) = WebMercatorTile.ToMercator(bounds.North, bounds.West);
        (double eastX, double southY) = WebMercatorTile.ToMercator(bounds.South, bounds.East);

        Assert.Equal(box.XMin, westX, 0.01);
        Assert.Equal(box.YMax, northY, 0.01);
        Assert.Equal(box.XMax, eastX, 0.01);
        Assert.Equal(box.YMin, southY, 0.01);
    }

    [Fact]
    public void A_Box_Straddling_Four_Tiles_Is_Covered_By_Four_Row_By_Row()
    {
        TileBounds corner = WebMercatorTile.Bounds(new TileKey(17, 21038, 50690));
        double latStep = (corner.North - corner.South) / 4;
        double lonStep = (corner.East - corner.West) / 4;

        IReadOnlyList<TileKey> keys = WebMercatorTile.TilesCovering(
            corner.South - latStep,
            corner.East - lonStep,
            corner.South + latStep,
            corner.East + lonStep,
            17
        );

        Assert.Equal(
            [new TileKey(17, 21038, 50690), new TileKey(17, 21039, 50690), new TileKey(17, 21038, 50691), new TileKey(17, 21039, 50691)],
            keys
        );
    }

    [Fact]
    public void The_Covering_Is_Clamped_To_The_Tile_Grid()
    {
        IReadOnlyList<TileKey> keys = WebMercatorTile.TilesCovering(-89, -200, 89, 200, 1);

        Assert.Equal([new TileKey(1, 0, 0), new TileKey(1, 1, 0), new TileKey(1, 0, 1), new TileKey(1, 1, 1)], keys);
    }
}
