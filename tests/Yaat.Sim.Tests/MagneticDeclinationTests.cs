using Xunit;

namespace Yaat.Sim.Tests;

public class MagneticDeclinationTests
{
    [Fact]
    public void GetDeclination_WestCoast_PositiveEast()
    {
        // San Francisco area: lon ~-122 → expected ~+12-14° east declination
        double decl = MagneticDeclination.GetDeclination(37.6, -122.4);
        Assert.InRange(decl, 10.0, 16.0);
    }

    [Fact]
    public void GetDeclination_EastCoast_NegativeWest()
    {
        // New York area: lon ~-74 → expected ~-12-14° west declination
        double decl = MagneticDeclination.GetDeclination(40.7, -74.0);
        Assert.InRange(decl, -14.0, -10.0);
    }

    [Fact]
    public void GetDeclination_CentralUS_NearZero()
    {
        // Somewhere around lon -97 → declination near 0
        double decl = MagneticDeclination.GetDeclination(39.0, -97.0);
        Assert.InRange(decl, -2.0, 2.0);
    }

    [Fact]
    public void TrueToMagnetic_WestCoast()
    {
        // True 270° on West Coast with ~+13° east declination → magnetic ~257°
        double mag = MagneticDeclination.TrueToMagnetic(270.0, 37.6, -122.4);
        Assert.InRange(mag, 254.0, 260.0);
    }

    [Fact]
    public void TrueToMagnetic_WrapsCorrectly()
    {
        // True 5° with positive declination → should wrap past 360
        double mag = MagneticDeclination.TrueToMagnetic(5.0, 37.6, -122.4);
        Assert.InRange(mag, 348.0, 358.0);
    }

    [Fact]
    public void TrueToMagnetic_EastCoast()
    {
        // True 270° on East Coast with ~-12° west declination → magnetic ~282°
        double mag = MagneticDeclination.TrueToMagnetic(270.0, 40.7, -74.0);
        Assert.InRange(mag, 280.0, 286.0);
    }
}
