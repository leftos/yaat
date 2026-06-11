using Xunit;
using Yaat.Sim.Data.Mva;

namespace Yaat.Sim.Tests;

public sealed class MvaDatabaseTests
{
    [Fact]
    public void Default_LoadsAllFaaFacilities()
    {
        var db = MvaDatabase.Default;

        // The merged FAA FUS3 fixture spans every facility the FAA publishes (~148), not just NorCal.
        Assert.True(db.Sectors.Count > 3000, $"expected the merged all-facility fixture, got {db.Sectors.Count} sectors");
        Assert.True(db.Sectors.Select(s => s.Facility).Distinct().Count() > 100);
        Assert.Equal(150, db.Sectors.Count(s => s.Facility == "NCT"));
        Assert.All(db.Sectors, s => Assert.InRange(s.FloorFtMsl, 1000, 18000));
    }

    [Theory]
    [InlineData(33.9416, -118.4085, 1600, "SCT")] // LAX — SoCal TRACON
    [InlineData(40.6413, -73.7781, 1500, "N90")] // JFK — New York TRACON
    [InlineData(32.8998, -97.0403, 2000, "D10")] // DFW — Dallas TRACON
    public void FindSector_ResolvesAcrossFacilities(double lat, double lon, int expectedFloor, string expectedFacility)
    {
        var sector = MvaDatabase.Default.FindSector(new LatLon(lat, lon));

        Assert.NotNull(sector);
        Assert.Equal(expectedFloor, sector!.FloorFtMsl);
        Assert.Equal(expectedFacility, sector.Facility);
    }

    [Theory]
    [InlineData(37.6189, -122.3750, 2600)] // SFO
    [InlineData(37.7213, -122.2208, 2000)] // OAK
    [InlineData(37.3626, -121.9291, 2000)] // SJC
    [InlineData(38.6951, -121.5910, 1700)] // SMF / Sacramento
    [InlineData(39.1000, -120.0500, 11200)] // Sierra near Tahoe
    public void GetFloorFtMsl_ReturnsChartedFloor(double lat, double lon, int expectedFloor)
    {
        Assert.Equal(expectedFloor, MvaDatabase.Default.GetFloorFtMsl(new LatLon(lat, lon)));
    }

    [Fact]
    public void GetFloorFtMsl_OutsideCoverage_ReturnsNull()
    {
        // Mid-Atlantic — well outside any NorCal sector.
        Assert.Null(MvaDatabase.Default.GetFloorFtMsl(new LatLon(40.0, -70.0)));
    }

    [Fact]
    public void Contains_PointInsideHole_BelongsToTheInnerSectorNotTheSurroundingOne()
    {
        var surrounding = new MvaSector
        {
            Sector = "SURROUND",
            Facility = "TST",
            FloorFtMsl = 3000,
            Rings =
            [
                Ring((37.0, -122.0), (38.0, -122.0), (38.0, -121.0), (37.0, -121.0)),
                Ring((37.4, -121.6), (37.6, -121.6), (37.6, -121.4), (37.4, -121.4)), // hole
            ],
        };
        var island = new MvaSector
        {
            Sector = "ISLAND",
            Facility = "TST",
            FloorFtMsl = 6000,
            Rings = [Ring((37.4, -121.6), (37.6, -121.6), (37.6, -121.4), (37.4, -121.4))],
        };
        var db = new MvaDatabase([surrounding, island]);

        var insideHole = new LatLon(37.5, -121.5);
        var outsideHole = new LatLon(37.1, -121.9);

        Assert.False(surrounding.Contains(insideHole));
        Assert.True(island.Contains(insideHole));
        Assert.Equal(6000, db.GetFloorFtMsl(insideHole));
        Assert.Equal("SURROUND", db.FindSector(outsideHole)!.Sector);
    }

    [Fact]
    public void FindSector_OverlappingSectors_HighestFloorWins()
    {
        var low = new MvaSector
        {
            Sector = "LOW",
            Facility = "TST",
            FloorFtMsl = 2000,
            Rings = [Ring((37.0, -122.0), (38.0, -122.0), (38.0, -121.0), (37.0, -121.0))],
        };
        var high = new MvaSector
        {
            Sector = "HIGH",
            Facility = "TST",
            FloorFtMsl = 5000,
            Rings = [Ring((37.0, -122.0), (38.0, -122.0), (38.0, -121.0), (37.0, -121.0))],
        };
        var db = new MvaDatabase([low, high]);

        Assert.Equal("HIGH", db.FindSector(new LatLon(37.5, -121.5))!.Sector);
        Assert.Equal(5000, db.GetFloorFtMsl(new LatLon(37.5, -121.5)));
    }

    [Theory]
    [InlineData(2400, MvaRelation.Below)] // 200 below the 2600 SFO floor
    [InlineData(2550, MvaRelation.At)] //  within +/-100
    [InlineData(2650, MvaRelation.At)] //  within +/-100
    [InlineData(2800, MvaRelation.Above)] // 200 above
    public void Classify_AppliesAtBandAroundFloor(double altitudeFt, MvaRelation expected)
    {
        // SFO point sits in a 2600 ft sector.
        var (relation, sector) = MvaDatabase.Default.Classify(new LatLon(37.6189, -122.3750), altitudeFt, atBandFt: 100);

        Assert.Equal(expected, relation);
        Assert.Equal(2600, sector!.FloorFtMsl);
    }

    [Fact]
    public void Classify_OutsideCoverage_IsNoData()
    {
        var (relation, sector) = MvaDatabase.Default.Classify(new LatLon(40.0, -70.0), 5000, atBandFt: 100);

        Assert.Equal(MvaRelation.NoData, relation);
        Assert.Null(sector);
    }

    private static List<LatLon> Ring(params (double Lat, double Lon)[] vertices)
    {
        var ring = vertices.Select(v => new LatLon(v.Lat, v.Lon)).ToList();
        ring.Add(ring[0]); // close the ring
        return ring;
    }
}
