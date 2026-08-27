using Xunit;
using Yaat.Sim;
using Yaat.Sim.Data.Artcc;

namespace Yaat.Sim.Tests.Artcc;

public class ArtccBoundaryDatabaseTests
{
    private static readonly LatLon Oakland = new(37.7213, -122.2208);
    private static readonly LatLon Kennedy = new(40.6413, -73.7781);

    [Fact]
    public void BundledFixture_LoadsEveryContinentalCenter()
    {
        var db = ArtccBoundaryDatabase.Default;

        Assert.True(db.Boundaries.Count >= 20, $"expected the bundled boundary set, got {db.Boundaries.Count}");
        Assert.NotNull(db.FindById("ZOA"));
        Assert.NotNull(db.FindById("zny"));
        Assert.Null(db.FindById("XXX"));
    }

    [Fact]
    public void Zoa_ContainsOakland_AndNotKennedy()
    {
        var zoa = ArtccBoundaryDatabase.Default.FindById("ZOA")!;

        Assert.True(zoa.Contains(Oakland));
        Assert.False(zoa.Contains(Kennedy));
        Assert.Equal(0, zoa.DistanceToEdgeNm(Oakland));
        Assert.True(zoa.DistanceToEdgeNm(Kennedy) > 1000);
    }

    [Fact]
    public void FindContaining_ReturnsTheCenterOverAPoint()
    {
        var ids = ArtccBoundaryDatabase.Default.FindContaining(Kennedy).Select(b => b.Id).ToList();

        Assert.Contains("ZNY", ids);
        Assert.DoesNotContain("ZOA", ids);
    }

    [Fact]
    public void FromGeoJson_SkipsFeaturesWithoutAnIdOrRing()
    {
        const string json = """
            {"type":"FeatureCollection","features":[
              {"type":"Feature","properties":{"id":"ZAA"},"geometry":{"type":"Polygon","coordinates":[[[-1,0],[1,0],[1,2],[-1,2],[-1,0]]]}},
              {"type":"Feature","properties":{},"geometry":{"type":"Polygon","coordinates":[[[-1,0],[1,0],[1,2],[-1,2],[-1,0]]]}},
              {"type":"Feature","properties":{"id":"ZBB"},"geometry":{"type":"Polygon","coordinates":[[[0,0],[1,1]]]}}
            ]}
            """;

        var db = ArtccBoundaryDatabase.FromGeoJson(json);

        var only = Assert.Single(db.Boundaries);
        Assert.Equal("ZAA", only.Id);
        Assert.True(only.Contains(new LatLon(1, 0)));
        Assert.False(only.Contains(new LatLon(3, 0)));
        Assert.InRange(only.DistanceToEdgeNm(new LatLon(3, 0)), 59, 61);
    }
}
