using System.IO;
using System.Linq;
using Xunit;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests;

/// <summary>
/// The vNAS airport map records displaced thresholds on a runway feature's <c>threshold</c> property
/// ("0 - 957", feet per end in name order). Anything measuring distance along a final approach course —
/// ADW markings, in particular — has to start at the landing threshold, not the pavement end.
/// </summary>
public class RunwayDisplacedThresholdTests
{
    private const string DisplacedRunwayGeoJson = """
        {
          "type": "FeatureCollection",
          "features": [
            {
              "type": "Feature",
              "properties": { "type": "runway", "name": "36 - 18", "threshold": "1000 - 500" },
              "geometry": {
                "type": "LineString",
                "coordinates": [
                  [ -122.2230, 37.7000 ],
                  [ -122.2230, 37.7200 ]
                ]
              }
            }
          ]
        }
        """;

    [Fact]
    public void Parse_ReadsThresholdDisplacementPerEnd()
    {
        var layout = GeoJsonParser.Parse("TEST", DisplacedRunwayGeoJson, null);

        var rwy = layout.Runways.Single();
        Assert.Equal(1000, rwy.ThresholdDisplacementForEnd("36"));
        Assert.Equal(500, rwy.ThresholdDisplacementForEnd("18"));
    }

    [Theory]
    [InlineData("\"threshold\": null,")]
    [InlineData("\"threshold\": \"garbage\",")]
    [InlineData("\"threshold\": \"1000\",")]
    [InlineData("")]
    public void Parse_AbsentOrMalformedThreshold_LeavesDisplacementZero(string thresholdProperty)
    {
        string json = $$"""
            {
              "type": "FeatureCollection",
              "features": [
                {
                  "type": "Feature",
                  "properties": { "type": "runway", "name": "36 - 18", {{thresholdProperty}} "turnoff": "left" },
                  "geometry": {
                    "type": "LineString",
                    "coordinates": [ [ -122.2230, 37.7000 ], [ -122.2230, 37.7200 ] ]
                  }
                }
              ]
            }
            """;

        var rwy = GeoJsonParser.Parse("TEST", json, null).Runways.Single();

        Assert.Equal(0, rwy.ThresholdDisplacementForEnd("36"));
        Assert.Equal(0, rwy.ThresholdDisplacementForEnd("18"));
    }

    [Fact]
    public void LandingThresholdForEnd_ShiftsDownfieldAlongLandingCourse()
    {
        var layout = GeoJsonParser.Parse("TEST", DisplacedRunwayGeoJson, null);
        var rwy = layout.Runways.Single();

        // Coordinates[0] is the south end (RWY 36's approach end, landing north).
        var pavement36 = rwy.Coordinates[0];
        var pavement18 = rwy.Coordinates[^1];

        var landing36 = rwy.LandingThresholdForEnd("36");
        Assert.NotNull(landing36);
        Assert.Equal(0, landing36.Value.LandingCourseDeg, 1);
        AssertFeetApart(1000, pavement36, landing36.Value.Threshold);
        Assert.True(landing36.Value.Threshold.Lat > pavement36.Lat, "RWY 36's landing threshold must sit north of its pavement end");

        var landing18 = rwy.LandingThresholdForEnd("18");
        Assert.NotNull(landing18);
        Assert.Equal(180, landing18.Value.LandingCourseDeg, 1);
        AssertFeetApart(500, pavement18, landing18.Value.Threshold);
        Assert.True(landing18.Value.Threshold.Lat < pavement18.Lat, "RWY 18's landing threshold must sit south of its pavement end");
    }

    [Fact]
    public void LandingThresholdForEnd_WithoutDisplacement_IsThePavementEnd()
    {
        TestVnasData.EnsureInitialized();
        string path = Path.Combine("TestData", "mia.geojson");
        var layout = GeoJsonParser.Parse("MIA", File.ReadAllText(path), "MIA");

        // KMIA 8L/26R has "threshold": null in the vNAS map.
        var rwy = layout.Runways.First(r => r.Name == "8L - 26R");
        var landing = rwy.LandingThresholdForEnd("26R");

        Assert.NotNull(landing);
        Assert.Equal(rwy.Coordinates[^1].Lat, landing.Value.Threshold.Lat, 9);
        Assert.Equal(rwy.Coordinates[^1].Lon, landing.Value.Threshold.Lon, 9);
    }

    [Fact]
    public void LandingThresholdForEnd_Mia30_AppliesThe957FootDisplacement()
    {
        TestVnasData.EnsureInitialized();
        string path = Path.Combine("TestData", "mia.geojson");
        var layout = GeoJsonParser.Parse("MIA", File.ReadAllText(path), "MIA");

        // KMIA 12/30 carries "threshold": "0 - 957" — RWY 12 undisplaced, RWY 30 displaced 957 ft.
        var rwy = layout.Runways.First(r => r.Name == "12 - 30");
        Assert.Equal(0, rwy.ThresholdDisplacementForEnd("12"));
        Assert.Equal(957, rwy.ThresholdDisplacementForEnd("30"));

        var pavement30 = rwy.Coordinates[^1];
        var landing30 = rwy.LandingThresholdForEnd("30");
        Assert.NotNull(landing30);
        AssertFeetApart(957, pavement30, landing30.Value.Threshold);

        // RWY 30 lands to the northwest, so the displaced threshold moves that way.
        Assert.True(landing30.Value.Threshold.Lat > pavement30.Lat);
        Assert.True(landing30.Value.Threshold.Lon < pavement30.Lon);
        Assert.InRange(landing30.Value.LandingCourseDeg, 290, 310);

        var landing12 = rwy.LandingThresholdForEnd("12");
        Assert.NotNull(landing12);
        Assert.Equal(rwy.Coordinates[0].Lat, landing12.Value.Threshold.Lat, 9);
        Assert.Equal(rwy.Coordinates[0].Lon, landing12.Value.Threshold.Lon, 9);
    }

    [Fact]
    public void LandingThresholdForEnd_UnknownEnd_ReturnsNull()
    {
        var rwy = GeoJsonParser.Parse("TEST", DisplacedRunwayGeoJson, null).Runways.Single();
        Assert.Null(rwy.LandingThresholdForEnd("27"));
    }

    [Fact]
    public void LandingThresholdForEnd_RunwayWithoutCoordinates_ReturnsNull()
    {
        var rwy = new GroundRunway
        {
            Name = "36 - 18",
            Coordinates = [],
            WidthFt = 150,
        };

        Assert.Null(rwy.LandingThresholdForEnd("36"));
    }

    [Fact]
    public void ThresholdDisplacementForEnd_SingleDigitRunway_NormalizesDesignator()
    {
        TestVnasData.EnsureInitialized();
        string path = Path.Combine("TestData", "mia.geojson");
        var layout = GeoJsonParser.Parse("MIA", File.ReadAllText(path), "MIA");

        // KMIA 9/27 carries "threshold": "1375 - 272"; "9" and "09" must resolve identically.
        var rwy = layout.Runways.First(r => r.Name == "9 - 27");
        Assert.Equal(1375, rwy.ThresholdDisplacementForEnd("9"));
        Assert.Equal(1375, rwy.ThresholdDisplacementForEnd("09"));
        Assert.Equal(272, rwy.ThresholdDisplacementForEnd("27"));
    }

    /// <summary>
    /// Projection is equirectangular while <see cref="GeoMath.DistanceNm"/> is haversine, so a
    /// projected point measures back a hair long. 2 ft covers that at runway scale.
    /// </summary>
    private static void AssertFeetApart(double expectedFt, (double Lat, double Lon) from, LatLon to)
    {
        double actualFt = GeoMath.DistanceNm(from.Lat, from.Lon, to.Lat, to.Lon) * GeoMath.FeetPerNm;
        Assert.InRange(actualFt, expectedFt - 2, expectedFt + 2);
    }
}
