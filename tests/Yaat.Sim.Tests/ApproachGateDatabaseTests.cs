using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Tests;

public class ApproachGateDatabaseTests
{
    [Fact]
    public void GetMinInterceptDistanceNm_NotInitialized_ReturnsDefault()
    {
        // Before any initialization, should return 7.0nm default
        double result = ApproachGateDatabase.GetMinInterceptDistanceNm("OAK", "28L");

        Assert.Equal(7.0, result);
    }

    [Fact]
    public void Initialize_ComputesGateFromFafDistance()
    {
        // FAF at 6nm from threshold → gate = max(6+1, 5) = 7nm →
        // min intercept = 7 + 2 = 9nm
        var fafLat = 37.8;
        var fafLon = -122.2;
        var thresholdLat = 37.72;
        var thresholdLon = -122.22;

        double fafDist = GeoMath.DistanceNm(fafLat, fafLon, thresholdLat, thresholdLon);
        double expectedGate = Math.Max(fafDist + 1.0, 5.0);
        double expectedMin = expectedGate + 2.0;

        var cifpData = new CifpParseResult(
            new Dictionary<(string Airport, string Runway), string> { [("TST", "28L")] = "TSTFX" },
            new Dictionary<string, (double Lat, double Lon)> { ["TSTFX"] = (fafLat, fafLon) }
        );

        var navDb = TestNavDbFactory.WithRunways(
            TestRunwayFactory.Make(
                designator: "28L",
                airportId: "TST",
                thresholdLat: thresholdLat,
                thresholdLon: thresholdLon,
                endLat: thresholdLat,
                endLon: thresholdLon - 0.03,
                heading: 280,
                elevationFt: 10
            )
        );
        NavigationDatabase.SetInstance(navDb);

        ApproachGateDatabase.Initialize(cifpData);

        double result = ApproachGateDatabase.GetMinInterceptDistanceNm("TST", "28L");

        Assert.Equal(expectedMin, result, precision: 1);
    }

    [Fact]
    public void GetMinInterceptDistanceNm_WithKPrefix_NormalizesAirportId()
    {
        // Setup with airport "TST"
        var cifpData = new CifpParseResult(
            new Dictionary<(string Airport, string Runway), string> { [("TST", "10R")] = "NORMF" },
            new Dictionary<string, (double Lat, double Lon)>
            {
                // FAF at ~6nm from threshold → gate = max(6+1, 5) = 7nm →
                // min = 9nm (not 7nm default)
                ["NORMF"] = (37.82, -122.20),
            }
        );

        var navDb = TestNavDbFactory.WithRunways(
            TestRunwayFactory.Make(
                designator: "10R",
                airportId: "KTST",
                thresholdLat: 37.72,
                thresholdLon: -122.22,
                endLat: 37.72,
                endLon: -122.19,
                heading: 100,
                elevationFt: 10,
                lengthFt: 8000
            )
        );
        NavigationDatabase.SetInstance(navDb);

        ApproachGateDatabase.Initialize(cifpData);

        // Query with K prefix should still find the entry
        double withK = ApproachGateDatabase.GetMinInterceptDistanceNm("KTST", "10R");
        double withoutK = ApproachGateDatabase.GetMinInterceptDistanceNm("TST", "10R");

        Assert.Equal(withK, withoutK);
        Assert.NotEqual(7.0, withK); // Should not be default
    }

    [Fact]
    public void GetMinInterceptDistanceNm_UnknownRunway_ReturnsDefault()
    {
        double result = ApproachGateDatabase.GetMinInterceptDistanceNm("NONEXISTENT", "99Z");

        Assert.Equal(7.0, result);
    }

    [Fact]
    public void Initialize_FafCloseToThreshold_UsesMinGateFloor()
    {
        // FAF at 2nm from threshold → gate = max(2+1, 5) = 5nm →
        // min intercept = 5 + 2 = 7nm
        var thresholdLat = 37.72;
        var thresholdLon = -122.22;
        // ~2nm north of threshold
        var fafLat = 37.753;
        var fafLon = -122.22;

        var cifpData = new CifpParseResult(
            new Dictionary<(string Airport, string Runway), string> { [("CLO", "36")] = "CLOSF" },
            new Dictionary<string, (double Lat, double Lon)> { ["CLOSF"] = (fafLat, fafLon) }
        );

        var navDb = TestNavDbFactory.WithRunways(
            TestRunwayFactory.Make(
                designator: "36",
                airportId: "CLO",
                thresholdLat: thresholdLat,
                thresholdLon: thresholdLon,
                endLat: thresholdLat + 0.02,
                endLon: thresholdLon,
                heading: 360,
                elevationFt: 10,
                lengthFt: 6000,
                widthFt: 100
            )
        );
        NavigationDatabase.SetInstance(navDb);

        ApproachGateDatabase.Initialize(cifpData);

        double result = ApproachGateDatabase.GetMinInterceptDistanceNm("CLO", "36");

        // gate = max(~2+1, 5) = 5, min = 7
        Assert.Equal(7.0, result, precision: 0);
    }
}
