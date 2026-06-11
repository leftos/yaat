using Xunit;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Ground;
using Yaat.Sim;

namespace Yaat.Client.Tests;

/// <summary>
/// Covers the surface-display airborne cutoff: an aircraft stays on the Ground View until it climbs
/// through the reported cloud ceiling or 6,000 ft AGL (whichever is lower), and never beyond the
/// 10 nm range cap. Exercises the pure helpers <see cref="GroundRenderer.ResolveAirborneMaxAglFt"/>
/// and <see cref="GroundRenderer.IsAirborneVisible"/>.
/// </summary>
public class GroundRendererAirborneVisibilityTests
{
    private const double CenterLat = 37.62;
    private const double CenterLon = -122.38;
    private const double FieldElevation = 0;

    private static WeatherDisplayInfo Weather(int? ceilingFeetAgl) => new("SFO", 270, 10, null, 29.92, ceilingFeetAgl);

    [Fact]
    public void ResolveMaxAgl_NoWeather_UsesSixThousand()
    {
        Assert.Equal(6000, GroundRenderer.ResolveAirborneMaxAglFt(null));
    }

    [Fact]
    public void ResolveMaxAgl_ClearSky_UsesSixThousand()
    {
        Assert.Equal(6000, GroundRenderer.ResolveAirborneMaxAglFt(Weather(ceilingFeetAgl: null)));
    }

    [Fact]
    public void ResolveMaxAgl_LowCeiling_CapsAtCeiling()
    {
        Assert.Equal(800, GroundRenderer.ResolveAirborneMaxAglFt(Weather(ceilingFeetAgl: 800)));
    }

    [Fact]
    public void ResolveMaxAgl_HighCeiling_CapsAtSixThousand()
    {
        Assert.Equal(6000, GroundRenderer.ResolveAirborneMaxAglFt(Weather(ceilingFeetAgl: 8000)));
    }

    [Fact]
    public void IsAirborneVisible_JustBelowCap_Visible()
    {
        var ac = AtCenter(altitude: 5900);
        Assert.True(GroundRenderer.IsAirborneVisible(ac, CenterLat, CenterLon, FieldElevation, maxAglFt: 6000));
    }

    [Fact]
    public void IsAirborneVisible_AboveCap_Hidden()
    {
        var ac = AtCenter(altitude: 6100);
        Assert.False(GroundRenderer.IsAirborneVisible(ac, CenterLat, CenterLon, FieldElevation, maxAglFt: 6000));
    }

    [Fact]
    public void IsAirborneVisible_AboveLowCeiling_Hidden()
    {
        // 900 ft AGL under an 800 ft ceiling — gone into the clouds, off the surface display.
        var ac = AtCenter(altitude: 900);
        Assert.False(GroundRenderer.IsAirborneVisible(ac, CenterLat, CenterLon, FieldElevation, maxAglFt: 800));
    }

    [Fact]
    public void IsAirborneVisible_LowAltButFarAway_HiddenByRangeCap()
    {
        // Well under the altitude cutoff but ~12 nm north of the field — beyond the 10 nm range cap.
        var ac = new AircraftModel
        {
            Callsign = "N1",
            Position = new LatLon(CenterLat + 0.2, CenterLon),
            Altitude = 2000,
        };
        Assert.False(GroundRenderer.IsAirborneVisible(ac, CenterLat, CenterLon, FieldElevation, maxAglFt: 6000));
    }

    private static AircraftModel AtCenter(double altitude) =>
        new()
        {
            Callsign = "N1",
            Position = new LatLon(CenterLat, CenterLon),
            Altitude = altitude,
        };
}
