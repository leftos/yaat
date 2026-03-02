using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

public class AltitudeResolverTests
{
    private sealed class FakeFixLookup : IFixLookup
    {
        private readonly Dictionary<string, double> _elevations;

        public FakeFixLookup(Dictionary<string, double> elevations)
        {
            _elevations = elevations;
        }

        public (double Lat, double Lon)? GetFixPosition(string name) => null;

        public double? GetAirportElevation(string code)
        {
            return _elevations.TryGetValue(code, out var elev) ? elev : null;
        }

        public IReadOnlyList<string> ExpandRoute(string route) => [];

        public IReadOnlyList<string> ExpandRouteForNavigation(string route, string? departureAirport) => [];
    }

    private static readonly IFixLookup Fixes = new FakeFixLookup(
        new Dictionary<string, double>
        {
            ["KOAK"] = 9.0,
            ["OAK"] = 9.0,
            ["KSFO"] = 13.0,
        }
    );

    // --- Numeric formats (unchanged behavior) ---

    [Theory]
    [InlineData("050", 5000)]
    [InlineData("100", 10000)]
    [InlineData("5000", 5000)]
    [InlineData("1500", 1500)]
    [InlineData("1", 100)]
    public void Numeric_ReturnsExpected(string arg, int expected)
    {
        Assert.Equal(expected, AltitudeResolver.Resolve(arg, Fixes));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void Numeric_InvalidReturnsNull(string arg)
    {
        Assert.Null(AltitudeResolver.Resolve(arg, Fixes));
    }

    // --- New AGL format with '+' separator ---

    [Fact]
    public void Agl_IcaoCode_PlusFormat()
    {
        // KOAK elevation 9 ft, 010 → 1000 AGL → 1009 MSL
        Assert.Equal(1009, AltitudeResolver.Resolve("KOAK+010", Fixes));
    }

    [Fact]
    public void Agl_FaaCode_PlusFormat()
    {
        // OAK elevation 9 ft, 050 → 5000 AGL → 5009 MSL
        Assert.Equal(5009, AltitudeResolver.Resolve("OAK+050", Fixes));
    }

    [Fact]
    public void Agl_AbsoluteAglValue()
    {
        // KOAK elevation 9 ft, 1500 → 1500 AGL → 1509 MSL
        Assert.Equal(1509, AltitudeResolver.Resolve("KOAK+1500", Fixes));
    }

    // --- Old format rejected ---

    [Fact]
    public void OldFormat_NoPlus_ReturnsNull()
    {
        // KOAK010 without '+' should NOT be parsed as AGL
        Assert.Null(AltitudeResolver.Resolve("KOAK010", Fixes));
    }

    [Fact]
    public void OldFormat_FaaNoPlus_ReturnsNull()
    {
        Assert.Null(AltitudeResolver.Resolve("OAK050", Fixes));
    }

    // --- Edge cases ---

    [Fact]
    public void Agl_NullArg_ReturnsNull()
    {
        Assert.Null(AltitudeResolver.Resolve(null, Fixes));
    }

    [Fact]
    public void Agl_NullFixes_ReturnsNull()
    {
        Assert.Null(AltitudeResolver.Resolve("KOAK+010", null));
    }

    [Fact]
    public void Agl_UnknownAirport_ReturnsNull()
    {
        Assert.Null(AltitudeResolver.Resolve("ZZZZ+010", Fixes));
    }

    [Fact]
    public void Agl_NoDigitsAfterPlus_ReturnsNull()
    {
        Assert.Null(AltitudeResolver.Resolve("KOAK+", Fixes));
    }

    [Fact]
    public void Agl_PlusOnly_ReturnsNull()
    {
        Assert.Null(AltitudeResolver.Resolve("+", Fixes));
    }

    [Fact]
    public void Agl_NothingBeforePlus_ParsesAsNumeric()
    {
        // "+010" is valid for int.TryParse (sign prefix) → 10 → 1000 ft
        Assert.Equal(1000, AltitudeResolver.Resolve("+010", Fixes));
    }

    [Fact]
    public void Agl_ZeroAgl_ReturnsNull()
    {
        Assert.Null(AltitudeResolver.Resolve("KOAK+0", Fixes));
    }

    [Fact]
    public void Agl_NonNumericAfterPlus_ReturnsNull()
    {
        Assert.Null(AltitudeResolver.Resolve("KOAK+ABC", Fixes));
    }
}
