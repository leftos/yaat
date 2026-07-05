using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// A CRUISE/QZ altitude assignment (<see cref="TrackEngine.HandleCruise"/>) updates the filed
/// altitude value while preserving its notation (IFR single / plain VFR / VFR-on-top).
/// </summary>
public class CruiseCommandNotationTests
{
    private static AircraftState Aircraft(string rules, PlannedAltitude altitude) =>
        new()
        {
            Callsign = "N123AB",
            AircraftType = "C172",
            FlightPlan = new AircraftFlightPlan { FlightRules = rules, Altitude = altitude },
        };

    [Fact]
    public void HandleCruise_IfrAircraft_SetsSingleIfrAltitude()
    {
        var ac = Aircraft("IFR", PlannedAltitude.Ifr(11000));
        TrackEngine.HandleCruise(ac, 150);
        Assert.Equal(PlannedAltitude.Ifr(15000), ac.FlightPlan.Altitude);
    }

    [Fact]
    public void HandleCruise_VfrAircraft_KeepsVfrNotation()
    {
        var ac = Aircraft("VFR", PlannedAltitude.Vfr(5500));
        TrackEngine.HandleCruise(ac, 65);
        Assert.Equal(PlannedAltitude.Vfr(6500), ac.FlightPlan.Altitude);
    }

    [Fact]
    public void HandleCruise_OtpAircraft_KeepsVfrOnTopNotation()
    {
        var ac = Aircraft("VFR", PlannedAltitude.Otp(5500));
        TrackEngine.HandleCruise(ac, 120);
        Assert.Equal(PlannedAltitude.Otp(12000), ac.FlightPlan.Altitude);
        Assert.True(ac.FlightPlan.Altitude.IsVfrOnTop);
    }
}
