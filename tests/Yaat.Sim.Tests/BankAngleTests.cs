using Xunit;

namespace Yaat.Sim.Tests;

public class BankAngleTests
{
    public BankAngleTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void BankAngle_Jet250Kias_RightTurn_PositiveBank()
    {
        // 250 KIAS at 10,000ft → TAS ~291kts; 2.5°/sec → bank ~33.7°
        var ac = MakeAircraft(heading: 90, ias: 250, altitude: 10000);
        ac.Targets.TargetTrueHeading = new TrueHeading(180); // Right turn

        FlightPhysics.Update(ac, 1.0);

        Assert.True(ac.BankAngle > 30, $"Expected bank > 30°, got {ac.BankAngle:F1}°");
        Assert.True(ac.BankAngle < 40, $"Expected bank < 40°, got {ac.BankAngle:F1}°");
    }

    [Fact]
    public void BankAngle_Piston90Kias_Moderate()
    {
        var ac = MakeAircraft(heading: 90, ias: 90, altitude: 2000);
        ac.AircraftType = "C172";

        ac.Targets.TargetTrueHeading = new TrueHeading(180); // Right turn

        FlightPhysics.Update(ac, 1.0);

        // Piston at 90 KIAS, 3.0 deg/sec → bank ≈ 14°
        Assert.True(ac.BankAngle > 10, $"Expected bank > 10°, got {ac.BankAngle:F1}°");
        Assert.True(ac.BankAngle < 18, $"Expected bank < 18°, got {ac.BankAngle:F1}°");
    }

    [Fact]
    public void BankAngle_NoTargetHeading_Zero()
    {
        var ac = MakeAircraft(heading: 90, ias: 250, altitude: 10000);
        // No target heading set

        FlightPhysics.Update(ac, 1.0);

        Assert.Equal(0, ac.BankAngle);
    }

    [Fact]
    public void BankAngle_HeadingReached_Zero()
    {
        var ac = MakeAircraft(heading: 90, ias: 250, altitude: 10000);
        ac.Targets.TargetTrueHeading = new TrueHeading(90.1); // Almost there

        FlightPhysics.Update(ac, 1.0);

        // Within HeadingSnapDeg → snapped → bank = 0
        Assert.Equal(0, ac.BankAngle);
    }

    [Fact]
    public void BankAngle_LeftTurn_NegativeBank()
    {
        var ac = MakeAircraft(heading: 180, ias: 250, altitude: 10000);
        ac.Targets.TargetTrueHeading = new TrueHeading(90); // Left turn

        FlightPhysics.Update(ac, 1.0);

        Assert.True(ac.BankAngle < 0, $"Expected negative bank, got {ac.BankAngle:F1}°");
        Assert.True(ac.BankAngle > -40, $"Expected bank > -40°, got {ac.BankAngle:F1}°");
    }

    [Fact]
    public void BankAngle_RightTurn_PositiveBank()
    {
        var ac = MakeAircraft(heading: 90, ias: 250, altitude: 10000);
        ac.Targets.TargetTrueHeading = new TrueHeading(180); // Right turn

        FlightPhysics.Update(ac, 1.0);

        Assert.True(ac.BankAngle > 0, $"Expected positive bank, got {ac.BankAngle:F1}°");
    }

    private static AircraftState MakeAircraft(double heading, double ias, double altitude)
    {
        return new AircraftState
        {
            Callsign = "TST100",
            AircraftType = "B738",
            Latitude = 37.721,
            Longitude = -122.221,
            TrueHeading = new TrueHeading(heading),
            TrueTrack = new TrueHeading(heading),
            Altitude = altitude,
            IndicatedAirspeed = ias,
        };
    }
}
