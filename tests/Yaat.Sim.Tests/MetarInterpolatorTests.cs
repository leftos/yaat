using Xunit;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

public class MetarInterpolatorTests
{
    // -------------------------------------------------------------------------
    // Exact station match
    // -------------------------------------------------------------------------

    [Fact]
    public void GetWeather_ExactMatch_ReturnsThatStation()
    {
        var metars = new[] { "KOAK 121853Z 27012KT 10SM BKN025 20/12 A2992" };
        var result = MetarInterpolator.GetWeatherForAirport(metars, "OAK", null);
        Assert.NotNull(result);
        Assert.Equal("KOAK", result.StationId);
        Assert.Equal(2500, result.CeilingFeetAgl);
        Assert.Equal(10.0, result.VisibilityStatuteMiles);
    }

    // -------------------------------------------------------------------------
    // No match, no fix lookup
    // -------------------------------------------------------------------------

    [Fact]
    public void GetWeather_NoMatch_NoFixes_ReturnsNull()
    {
        var metars = new[] { "KSFO 121853Z 27012KT 10SM CLR 20/12 A2992" };
        var result = MetarInterpolator.GetWeatherForAirport(metars, "LAX", null);
        Assert.Null(result);
    }

    // -------------------------------------------------------------------------
    // Interpolation with fix lookup
    // -------------------------------------------------------------------------

    [Fact]
    public void GetWeather_SingleNearbyStation_ReturnsItsData()
    {
        var fixes = new TestFixLookup(new Dictionary<string, (double, double)> { ["LAX"] = (33.9425, -118.408), ["KLAX"] = (33.9425, -118.408) });

        var metars = new[] { "KLAX 121853Z 27012KT 5SM BKN030 20/12 A2992" };
        var result = MetarInterpolator.GetWeatherForAirport(metars, "LAX", fixes);
        Assert.NotNull(result);
        Assert.Equal(3000, result.CeilingFeetAgl);
        Assert.Equal(5.0, result.VisibilityStatuteMiles);
    }

    [Fact]
    public void GetWeather_MultipleNearby_MinCeiling_WeightedVis()
    {
        // Two stations: one close (10nm) with high ceiling, one farther (30nm) with low ceiling
        // Airport at (37.7, -122.2)
        // Station A at (37.7, -122.0) ≈ 10nm east, ceiling 5000, vis 10
        // Station B at (37.5, -122.2) ≈ 12nm south, ceiling 2000, vis 3
        var fixes = new TestFixLookup(
            new Dictionary<string, (double, double)>
            {
                ["TSTA"] = (37.7, -122.2), // target airport
                ["KSTA"] = (37.7, -122.0), // station A
                ["STA"] = (37.7, -122.0),
                ["KSTB"] = (37.5, -122.2), // station B
                ["STB"] = (37.5, -122.2),
            }
        );

        var metars = new[] { "KSTA 121853Z 27012KT 10SM BKN050 20/12 A2992", "KSTB 121853Z 27012KT 3SM BKN020 20/12 A2992" };

        var result = MetarInterpolator.GetWeatherForAirport(metars, "TSTA", fixes);
        Assert.NotNull(result);
        // Min ceiling: 2000
        Assert.Equal(2000, result.CeilingFeetAgl);
        // Weighted vis: closer station has more weight, so between 3 and 10 but closer to 10
        Assert.NotNull(result.VisibilityStatuteMiles);
        Assert.InRange(result.VisibilityStatuteMiles.Value, 3.0, 10.0);
    }

    [Fact]
    public void GetWeather_NoStationsWithin50nm_ReturnsNull()
    {
        // Airport on west coast, station on east coast — way beyond 50nm
        var fixes = new TestFixLookup(
            new Dictionary<string, (double, double)>
            {
                ["TSTA"] = (37.7, -122.2),
                ["KJFK"] = (40.6, -73.8),
                ["JFK"] = (40.6, -73.8),
            }
        );

        var metars = new[] { "KJFK 121853Z 27012KT 10SM CLR 20/12 A2992" };
        var result = MetarInterpolator.GetWeatherForAirport(metars, "TSTA", fixes);
        Assert.Null(result);
    }

    // -------------------------------------------------------------------------
    // WeatherProfile.GetWeatherForAirport caching
    // -------------------------------------------------------------------------

    [Fact]
    public void WeatherProfile_CachesResults()
    {
        var profile = new WeatherProfile { Metars = ["KOAK 121853Z 27012KT 10SM BKN025 20/12 A2992"] };

        var r1 = profile.GetWeatherForAirport("OAK", null);
        var r2 = profile.GetWeatherForAirport("OAK", null);
        Assert.NotNull(r1);
        Assert.Same(r1, r2);
    }

    [Fact]
    public void WeatherProfile_NoMetars_ReturnsNull()
    {
        var profile = new WeatherProfile();
        Assert.Null(profile.GetWeatherForAirport("OAK", null));
    }

    // -------------------------------------------------------------------------
    // Test helpers
    // -------------------------------------------------------------------------

    private sealed class TestFixLookup(Dictionary<string, (double Lat, double Lon)> fixes) : IFixLookup
    {
        public (double Lat, double Lon)? GetFixPosition(string name)
        {
            return fixes.TryGetValue(name, out var pos) ? pos : null;
        }

        public double? GetAirportElevation(string code) => null;

        public IReadOnlyList<string> ExpandRoute(string route) => [];

        public IReadOnlyList<string> ExpandRouteForNavigation(string route, string? departureAirport) => [];

        public IReadOnlyList<string>? GetStarBody(string starId) => null;

        public IReadOnlyList<(string Name, IReadOnlyList<string> Fixes)>? GetStarTransitions(string starId) => null;
    }
}
