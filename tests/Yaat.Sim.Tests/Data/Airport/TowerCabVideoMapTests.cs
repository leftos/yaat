using System.Text.Json;
using Xunit;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests.Data.Airport;

/// <summary>
/// The tower-cab videomap reader: which features become two-point segments, in what order, and what the segment hash
/// follows. The OAK fixture is the vNAS tower-cab map the ZOA config names for OAK.
/// </summary>
public class TowerCabVideoMapTests
{
    private static readonly string OakPath = Path.Combine(AppContext.BaseDirectory, "TestData", "TowerCabMaps", "oak.geojson");

    private const string TwoPointPartOfMultiLine =
        """{"type":"Feature","geometry":{"type":"MultiLineString","coordinates":[[[11,12],[13,14]],[[15,16],[17,18],[19,20]]]},"properties":{}}""";

    [Fact]
    public void Oak_ReadsEveryTwoPointSegment()
    {
        var map = TowerCabVideoMap.Read(File.ReadAllText(OakPath));

        Assert.Equal(517, map.Segments.Count);
        Assert.Equal(new LatLon(37.72501342930441, -122.20462414011487), map.Segments[0].A);
        Assert.Equal(new LatLon(37.72564193881924, -122.2042924124376), map.Segments[0].B);
    }

    [Fact]
    public void Oak_HashIsStable()
    {
        string geoJson = File.ReadAllText(OakPath);

        string first = TowerCabVideoMap.Read(geoJson).Hash;
        string second = TowerCabVideoMap.Read(geoJson).Hash;

        Assert.Equal("ffdca712f9697a49f6401c9a0432115d6ec9d051c9fe5c64e1498583c798bd78", first);
        Assert.Equal(first, second);
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            TowerCabVideoMap.Read("{\"type\":\"FeatureCollection\",\"features\":[]}").Hash
        );
    }

    [Fact]
    public void Read_KeepsOnlyTwoPointLines()
    {
        string geoJson = Collection(
            Line("[[1,2],[3,4]]"),
            Line("[[5,6],[7,8],[9,10]]"),
            """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]},"properties":{}}""",
            TwoPointPartOfMultiLine,
            """{"type":"Feature","geometry":null,"properties":{}}""",
            Line("[[21,22],null]")
        );

        var map = TowerCabVideoMap.Read(geoJson);

        Assert.Equal(
            [new VideoMapSegment(new LatLon(2, 1), new LatLon(4, 3)), new VideoMapSegment(new LatLon(12, 11), new LatLon(14, 13))],
            map.Segments
        );
    }

    [Fact]
    public void Read_RootNotAFeatureCollection_Throws() => Assert.Throws<JsonException>(() => TowerCabVideoMap.Read("""{"type":"Feature"}"""));

    [Fact]
    public void Read_FeatureWithNullGeometry_SkipsIt()
    {
        string geoJson = Collection("""{"type":"Feature","geometry":null,"properties":{}}""", Line("[[1,2],[3,4]]"));

        Assert.Equal([new VideoMapSegment(new LatLon(2, 1), new LatLon(4, 3))], TowerCabVideoMap.Read(geoJson).Segments);
    }

    [Fact]
    public void Read_OutOfRangeNumber_SkipsThatSegment()
    {
        string geoJson = Collection(Line("[[1e999,0],[1,1]]"), Line("[[1,2],[3,4]]"));

        Assert.Equal([new VideoMapSegment(new LatLon(2, 1), new LatLon(4, 3))], TowerCabVideoMap.Read(geoJson).Segments);
    }

    [Fact]
    public void Read_InvalidJson_Throws() => Assert.ThrowsAny<JsonException>(() => TowerCabVideoMap.Read("{"));

    [Fact]
    public void Read_HashFollowsTheSegments()
    {
        string one = TowerCabVideoMap.Read(Collection(Line("[[-122.1,37.71],[-122.2,37.72]]"))).Hash;
        string oneMoved = TowerCabVideoMap.Read(Collection(Line("[[-122.1,37.71],[-122.2,37.73]]"))).Hash;
        string inOrder = TowerCabVideoMap.Read(Collection(Line("[[1,2],[3,4]]"), Line("[[5,6],[7,8]]"))).Hash;
        string swapped = TowerCabVideoMap.Read(Collection(Line("[[5,6],[7,8]]"), Line("[[1,2],[3,4]]"))).Hash;

        Assert.NotEqual(one, oneMoved);
        Assert.NotEqual(inOrder, swapped);
    }

    private static string Line(string coordinates) =>
        """{"type":"Feature","geometry":{"type":"LineString","coordinates":""" + coordinates + """},"properties":{}}""";

    private static string Collection(params string[] features) => """{"type":"FeatureCollection","features":[""" + string.Join(",", features) + "]}";
}
