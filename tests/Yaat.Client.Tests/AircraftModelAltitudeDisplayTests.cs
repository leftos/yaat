using Xunit;
using Yaat.Client.Models;

namespace Yaat.Client.Tests;

/// <summary>
/// Pins <see cref="AircraftModel.CruiseAltitudeDisplay"/> — it reconstructs the filed-altitude notation
/// from the flat wire fields (CruiseAltitude ceiling, BlockFloorAltitude floor, IsVfrOnTop, IsAbove) plus
/// FlightRules, so the datablock / FP editor render block and VFR-on-top altitudes correctly.
/// </summary>
public class AircraftModelAltitudeDisplayTests
{
    [Fact]
    public void Block_RendersFloorBCeiling()
    {
        var ac = new AircraftModel
        {
            FlightRules = "IFR",
            CruiseAltitude = 25000,
            BlockFloorAltitude = 20000,
        };
        Assert.Equal("200B250", ac.CruiseAltitudeDisplay);
    }

    [Fact]
    public void VfrOnTop_RendersOtpSlash()
    {
        // VFR-on-top is an IFR flight (AIM 4-4-8) — rules stay IFR; the notation carries "OTP".
        var ac = new AircraftModel
        {
            FlightRules = "IFR",
            IsVfrOnTop = true,
            CruiseAltitude = 6500,
        };
        Assert.Equal("OTP/065", ac.CruiseAltitudeDisplay);
    }

    [Fact]
    public void VfrWithAltitude_RendersVfrSlash()
    {
        var ac = new AircraftModel { FlightRules = "VFR", CruiseAltitude = 5500 };
        Assert.Equal("VFR/055", ac.CruiseAltitudeDisplay);
    }

    [Fact]
    public void IfrSingle_RendersHundreds()
    {
        var ac = new AircraftModel { FlightRules = "IFR", CruiseAltitude = 24000 };
        Assert.Equal("240", ac.CruiseAltitudeDisplay);
    }
}
