using Xunit;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

/// <summary>
/// #268 — <see cref="LatLonParser"/> parses ERAM CRR-group lat/long location strings
/// (docs/crc/eram.md §LF Command — <c>//4220N/7110W</c>). DDMM latitude + N/S, [D]DDMM longitude + E/W
/// (2- or 3-digit degrees), optional slash separator; hemisphere signs; rejects malformed input.
/// </summary>
public class LatLonParserTests
{
    [Fact]
    public void Parse_DdmmWithSlash_ReturnsDecimalDegrees()
    {
        var result = LatLonParser.Parse("4220N/7110W");

        Assert.NotNull(result);
        Assert.Equal(42.0 + (20.0 / 60.0), result!.Value.Lat, 6);
        Assert.Equal(-(71.0 + (10.0 / 60.0)), result.Value.Lon, 6);
    }

    [Fact]
    public void Parse_DdmmNoSlash_ReturnsDecimalDegrees()
    {
        var result = LatLonParser.Parse("3730N12200W");

        Assert.NotNull(result);
        Assert.Equal(37.5, result!.Value.Lat, 6);
        Assert.Equal(-122.0, result.Value.Lon, 6);
    }

    [Fact]
    public void Parse_SouthEastHemispheres_AreNegatedAndPositive()
    {
        var result = LatLonParser.Parse("3345S15112E");

        Assert.NotNull(result);
        Assert.Equal(-(33.0 + (45.0 / 60.0)), result!.Value.Lat, 6);
        Assert.Equal(151.0 + (12.0 / 60.0), result.Value.Lon, 6);
    }

    [Fact]
    public void Parse_ThreeDigitLongitudeDegrees_Parses()
    {
        var result = LatLonParser.Parse("4220N/12210W");

        Assert.NotNull(result);
        Assert.Equal(42.0 + (20.0 / 60.0), result!.Value.Lat, 6);
        Assert.Equal(-(122.0 + (10.0 / 60.0)), result.Value.Lon, 6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("OAK")] // a fix name, not a coordinate
    [InlineData("4220N")] // longitude missing
    [InlineData("4270N7110W")] // minutes >= 60
    [InlineData("9500N7110W")] // latitude out of range
    [InlineData("4220X7110W")] // bad hemisphere
    public void Parse_Malformed_ReturnsNull(string input)
    {
        Assert.Null(LatLonParser.Parse(input));
    }
}
