using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

/// <summary>
/// A plain SPD speed instruction can't assign a helicopter below the radar-speed minimum
/// (60 KIAS, 7110.65 §5-7-3); ApplySpeed floors it. The force-speed command (SPEEDN) is
/// exempt and may command any speed. The floor only applies airborne. Surfaced as a
/// follow-up from issue #177 (SPD is one of the air-taxi break-out commands).
/// </summary>
public class HelicopterSpeedFloorTests
{
    private static AircraftState Heli(bool onGround = false) =>
        new()
        {
            Callsign = "N101H",
            AircraftType = "R22",
            IsOnGround = onGround,
            Altitude = onGround ? 0 : 800,
        };

    [Fact]
    public void Spd_BelowMinimum_FlooredForAirborneHeli()
    {
        TestVnasData.EnsureInitialized();
        var heli = Heli();

        var result = FlightCommandHandler.ApplySpeed(new SpeedCommand(30), heli);

        Assert.True(result.Success, result.Message);
        Assert.Equal(60, heli.Targets.TargetSpeed);
        Assert.Equal(60, heli.Targets.AssignedSpeed);
        Assert.Contains("Speed 60", result.Message!);
    }

    [Fact]
    public void Spd_AboveMinimum_UnchangedForHeli()
    {
        TestVnasData.EnsureInitialized();
        var heli = Heli();

        var result = FlightCommandHandler.ApplySpeed(new SpeedCommand(80), heli);

        Assert.Equal(80, heli.Targets.TargetSpeed);
        Assert.Contains("Speed 80", result.Message!);
    }

    [Fact]
    public void ForceSpeed_BypassesHeliFloor()
    {
        TestVnasData.EnsureInitialized();
        var heli = Heli();

        var result = FlightCommandHandler.ApplyForceSpeed(new ForceSpeedCommand(30), heli);

        Assert.True(result.Success, result.Message);
        Assert.Equal(30, heli.Targets.TargetSpeed);
        Assert.Equal(30, heli.Targets.AssignedSpeed);
    }

    [Fact]
    public void Spd_BelowMinimum_NotFlooredForFixedWing()
    {
        TestVnasData.EnsureInitialized();
        var jet = new AircraftState
        {
            Callsign = "N123AB",
            AircraftType = "C172",
            IsOnGround = false,
            Altitude = 3000,
        };

        var result = FlightCommandHandler.ApplySpeed(new SpeedCommand(30), jet);

        Assert.Equal(30, jet.Targets.TargetSpeed);
    }
}
