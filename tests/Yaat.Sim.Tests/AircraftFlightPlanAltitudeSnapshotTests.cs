using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests;

/// <summary>
/// The filed <see cref="PlannedAltitude"/> round-trips through the flattened snapshot DTO fields
/// (AltitudeCruiseFeet / AltitudeBlockFloorFeet / AltitudeIsVfr / AltitudeIsVfrOnTop / AltitudeIsAbove),
/// so block / VFR-on-top / above notations survive replay reconstruction.
/// </summary>
public class AircraftFlightPlanAltitudeSnapshotTests
{
    public static TheoryData<PlannedAltitude> Cases =>
        [
            PlannedAltitude.None,
            PlannedAltitude.Ifr(24000),
            PlannedAltitude.Block(20000, 25000),
            PlannedAltitude.Vfr(6500),
            PlannedAltitude.Vfr(null),
            PlannedAltitude.Otp(12000),
            PlannedAltitude.Otp(null),
            PlannedAltitude.UntilFix(17000, "SJC", 11000),
            PlannedAltitude.UntilFix(17000, "3730N/12200W", 11000),
        ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FixQualifiedAltitude_AndItsPassedLatch_SurviveSnapshotRoundTrip(bool passed)
    {
        var fp = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            FlightRules = "IFR",
            Altitude = PlannedAltitude.UntilFix(17000, "SJC", 11000),
            AltitudeFixPassed = passed,
        };

        var restored = AircraftFlightPlan.FromSnapshot(fp.ToSnapshot());

        Assert.Equal(PlannedAltitude.UntilFix(17000, "SJC", 11000), restored.Altitude);
        Assert.Equal(passed, restored.AltitudeFixPassed);
    }

    [Fact]
    public void ASnapshotWithoutTheFixFields_RestoresAPlainAltitude_Unlatched()
    {
        var fp = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            FlightRules = "IFR",
            Altitude = PlannedAltitude.Ifr(24000),
        };
        JsonObject json = JsonSerializer.SerializeToNode(fp.ToSnapshot())!.AsObject();
        json.Remove(nameof(AircraftFlightPlanDto.AltitudeFix));
        json.Remove(nameof(AircraftFlightPlanDto.AltitudeAfterFixFeet));
        json.Remove(nameof(AircraftFlightPlanDto.AltitudeFixPassed));

        var restored = AircraftFlightPlan.FromSnapshot(json.Deserialize<AircraftFlightPlanDto>()!);

        Assert.Equal(PlannedAltitude.Ifr(24000), restored.Altitude);
        Assert.False(restored.AltitudeFixPassed);
    }

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
