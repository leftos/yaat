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
        double result = ApproachGateDatabase.GetMinInterceptDistanceNm(
            "OAK", "28L");

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

        double fafDist = GeoMath.DistanceNm(
            fafLat, fafLon, thresholdLat, thresholdLon);
        double expectedGate = Math.Max(fafDist + 1.0, 5.0);
        double expectedMin = expectedGate + 2.0;

        var cifpData = new CifpParseResult(
            new Dictionary<(string Airport, string Runway), string>
            {
                [("TST", "28L")] = "TSTFX",
            },
            new Dictionary<string, (double Lat, double Lon)>
            {
                ["TSTFX"] = (fafLat, fafLon),
            });

        var fixLookup = new StubFixLookup();
        var runwayLookup = new StubRunwayLookup(
            new RunwayInfo
            {
                AirportId = "TST",
                RunwayId = "28L",
                ThresholdLatitude = thresholdLat,
                ThresholdLongitude = thresholdLon,
                TrueHeading = 280,
                ElevationFt = 10,
                LengthFt = 10000,
                WidthFt = 150,
                EndLatitude = thresholdLat,
                EndLongitude = thresholdLon - 0.03,
            });

        ApproachGateDatabase.Initialize(
            cifpData, fixLookup, runwayLookup);

        double result = ApproachGateDatabase.GetMinInterceptDistanceNm(
            "TST", "28L");

        Assert.Equal(expectedMin, result, precision: 1);
    }

    [Fact]
    public void GetMinInterceptDistanceNm_WithKPrefix_NormalizesAirportId()
    {
        // Setup with airport "TST"
        var cifpData = new CifpParseResult(
            new Dictionary<(string Airport, string Runway), string>
            {
                [("TST", "10R")] = "NORMF",
            },
            new Dictionary<string, (double Lat, double Lon)>
            {
                // FAF at ~6nm from threshold → gate = max(6+1, 5) = 7nm →
                // min = 9nm (not 7nm default)
                ["NORMF"] = (37.82, -122.20),
            });

        var fixLookup = new StubFixLookup();
        var runwayLookup = new StubRunwayLookup(
            new RunwayInfo
            {
                AirportId = "KTST",
                RunwayId = "10R",
                ThresholdLatitude = 37.72,
                ThresholdLongitude = -122.22,
                TrueHeading = 100,
                ElevationFt = 10,
                LengthFt = 8000,
                WidthFt = 150,
                EndLatitude = 37.72,
                EndLongitude = -122.19,
            });

        ApproachGateDatabase.Initialize(
            cifpData, fixLookup, runwayLookup);

        // Query with K prefix should still find the entry
        double withK = ApproachGateDatabase.GetMinInterceptDistanceNm(
            "KTST", "10R");
        double withoutK = ApproachGateDatabase.GetMinInterceptDistanceNm(
            "TST", "10R");

        Assert.Equal(withK, withoutK);
        Assert.NotEqual(7.0, withK); // Should not be default
    }

    [Fact]
    public void GetMinInterceptDistanceNm_UnknownRunway_ReturnsDefault()
    {
        double result = ApproachGateDatabase.GetMinInterceptDistanceNm(
            "NONEXISTENT", "99Z");

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
            new Dictionary<(string Airport, string Runway), string>
            {
                [("CLO", "36")] = "CLOSF",
            },
            new Dictionary<string, (double Lat, double Lon)>
            {
                ["CLOSF"] = (fafLat, fafLon),
            });

        var fixLookup = new StubFixLookup();
        var runwayLookup = new StubRunwayLookup(
            new RunwayInfo
            {
                AirportId = "CLO",
                RunwayId = "36",
                ThresholdLatitude = thresholdLat,
                ThresholdLongitude = thresholdLon,
                TrueHeading = 360,
                ElevationFt = 10,
                LengthFt = 6000,
                WidthFt = 100,
                EndLatitude = thresholdLat + 0.02,
                EndLongitude = thresholdLon,
            });

        ApproachGateDatabase.Initialize(
            cifpData, fixLookup, runwayLookup);

        double result = ApproachGateDatabase.GetMinInterceptDistanceNm(
            "CLO", "36");

        // gate = max(~2+1, 5) = 5, min = 7
        Assert.Equal(7.0, result, precision: 0);
    }

    private class StubFixLookup : IFixLookup
    {
        public (double Lat, double Lon)? GetFixPosition(string name) => null;
        public double? GetAirportElevation(string code) => null;
    }

    private class StubRunwayLookup : IRunwayLookup
    {
        private readonly RunwayInfo? _runway;

        public StubRunwayLookup(RunwayInfo? runway = null)
        {
            _runway = runway;
        }

        public RunwayInfo? GetRunway(string airportCode, string runwayId)
        {
            if (_runway is null)
            {
                return null;
            }

            // Match with or without K prefix
            string normalizedCode = airportCode.StartsWith('K')
                ? airportCode[1..]
                : airportCode;
            string normalizedRunway = _runway.AirportId.StartsWith('K')
                ? _runway.AirportId[1..]
                : _runway.AirportId;

            return normalizedCode.Equals(
                normalizedRunway, StringComparison.OrdinalIgnoreCase)
                && runwayId == _runway.RunwayId
                ? _runway
                : null;
        }

        public IReadOnlyList<RunwayInfo> GetRunways(string airportCode)
        {
            return _runway is not null ? [_runway] : [];
        }
    }
}
