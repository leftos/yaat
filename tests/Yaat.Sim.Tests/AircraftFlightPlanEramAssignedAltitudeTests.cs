using Xunit;
using Yaat.Sim;

namespace Yaat.Sim.Tests;

/// <summary>
/// <see cref="AircraftFlightPlan.EramAssignedAltitudeFeet"/> is the display-only ERAM assigned altitude a scenario's
/// auto-track cleared altitude sets: <see cref="AircraftFlightPlan.EramAltitudeFeet"/> prefers it, any assignment of
/// <see cref="AircraftFlightPlan.Altitude"/> (even to the same value) clears it, and it survives a snapshot round trip.
/// </summary>
public class AircraftFlightPlanEramAssignedAltitudeTests
{
    private static AircraftFlightPlan PlanWithEramAssigned()
    {
        var fp = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            FlightRules = "IFR",
            Altitude = PlannedAltitude.Ifr(34000),
        };
        fp.AssignEramAltitude(24000);
        return fp;
    }

    [Fact]
    public void EramAltitudeFeet_PrefersTheEramAssignedAltitude()
    {
        AircraftFlightPlan fp = PlanWithEramAssigned();

        Assert.Equal(24000, fp.EramAltitudeFeet);
        Assert.Equal(34000, fp.Altitude.CruiseFeet);
    }

    [Fact]
    public void EramAltitudeFeet_WithoutAnEramAssignedAltitude_IsTheFiledAltitude()
    {
        var fp = new AircraftFlightPlan { Altitude = PlannedAltitude.Ifr(34000) };

        Assert.Null(fp.EramAssignedAltitudeFeet);
        Assert.Equal(34000, fp.EramAltitudeFeet);
    }

    [Fact]
    public void SettingADifferentAltitude_ClearsTheEramAssignedAltitude()
    {
        AircraftFlightPlan fp = PlanWithEramAssigned();

        fp.Altitude = PlannedAltitude.Ifr(28000);

        Assert.Null(fp.EramAssignedAltitudeFeet);
        Assert.Equal(28000, fp.EramAltitudeFeet);
    }

    [Fact]
    public void SettingTheSameAltitude_ClearsTheEramAssignedAltitude()
    {
        AircraftFlightPlan fp = PlanWithEramAssigned();

        fp.Altitude = PlannedAltitude.Ifr(34000);

        Assert.Null(fp.EramAssignedAltitudeFeet);
        Assert.Equal(34000, fp.EramAltitudeFeet);
    }

    [Fact]
    public void SettingTheSameAltitude_KeepsTheFixPassageFlags()
    {
        var fp = new AircraftFlightPlan
        {
            Altitude = PlannedAltitude.UntilFix(17000, "SJC", 11000),
            AltitudeFixApproached = true,
            AltitudeFixPassed = true,
        };

        fp.Altitude = PlannedAltitude.UntilFix(17000, "SJC", 11000);

        Assert.True(fp.AltitudeFixApproached);
        Assert.True(fp.AltitudeFixPassed);
    }

    [Fact]
    public void EramAssignedAltitude_SurvivesSnapshotRoundTrip()
    {
        var restored = AircraftFlightPlan.FromSnapshot(PlanWithEramAssigned().ToSnapshot());

        Assert.Equal(24000, restored.EramAssignedAltitudeFeet);
        Assert.Equal(34000, restored.Altitude.CruiseFeet);
        Assert.Equal(24000, restored.EramAltitudeFeet);
    }

    [Fact]
    public void NoEramAssignedAltitude_RoundTripsAsNull()
    {
        var fp = new AircraftFlightPlan { Altitude = PlannedAltitude.Ifr(34000) };

        var restored = AircraftFlightPlan.FromSnapshot(fp.ToSnapshot());

        Assert.Null(restored.EramAssignedAltitudeFeet);
    }
}
