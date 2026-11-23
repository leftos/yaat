using Xunit;
using Yaat.Sim.Authoring.Imagery;

namespace Yaat.Sim.Tests.Authoring.Imagery;

/// <summary>The areas the USGS NAIPPlus imagery is asked for: the US states and territories, nowhere else.</summary>
public sealed class UsgsCoverageTests
{
    [Theory]
    [InlineData(37.7213, -122.2207)] // KOAK
    [InlineData(61.1744, -149.9964)] // PANC
    [InlineData(21.3187, -157.9225)] // PHNL
    [InlineData(18.4394, -66.0018)] // TJSJ
    [InlineData(13.4834, 144.7960)] // PGUM
    [InlineData(-14.3310, -170.7105)] // NSTU
    public void A_Us_Airport_Is_Covered(double lat, double lon) => Assert.True(UsgsCoverage.Contains(lat, lon));

    [Theory]
    [InlineData(51.4706, -0.4619)] // EGLL
    [InlineData(49.1939, -123.1844)] // CYVR
    [InlineData(19.4363, -99.0721)] // MMMX
    [InlineData(0, 0)]
    public void A_Place_Outside_The_Us_Is_Not_Covered(double lat, double lon) => Assert.False(UsgsCoverage.Contains(lat, lon));
}
