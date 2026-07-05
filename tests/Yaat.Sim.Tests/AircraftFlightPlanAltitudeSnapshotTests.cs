using Xunit;
using Yaat.Sim;

namespace Yaat.Sim.Tests;

/// <summary>
/// The filed <see cref="PlannedAltitude"/> round-trips through the flattened snapshot DTO fields
/// (AltitudeCruiseFeet / AltitudeBlockFloorFeet / AltitudeIsVfr / AltitudeIsVfrOnTop / AltitudeIsAbove),
/// so block / VFR-on-top / above notations survive replay reconstruction.
/// </summary>
public class AircraftFlightPlanAltitudeSnapshotTests
{
    public static TheoryData<PlannedAltitude> Cases =>
        new()
        {
            PlannedAltitude.None,
            PlannedAltitude.Ifr(24000),
            PlannedAltitude.Block(20000, 25000),
            PlannedAltitude.Vfr(6500),
            PlannedAltitude.Vfr(null),
            PlannedAltitude.Otp(12000),
            PlannedAltitude.Otp(null),
        };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Altitude_SurvivesSnapshotRoundTrip(PlannedAltitude altitude)
    {
        var fp = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            FlightRules = "IFR",
            Altitude = altitude,
        };
        var restored = AircraftFlightPlan.FromSnapshot(fp.ToSnapshot());
        Assert.Equal(altitude, restored.Altitude);
    }
}
