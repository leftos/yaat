using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

public class MachCommandTests
{
    private static AircraftState CreateAircraft(double altitude = 35000, double ias = 280)
    {
        return new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Latitude = 37.0,
            Longitude = -122.0,
            TrueHeading = new TrueHeading(360),
            TrueTrack = new TrueHeading(360),
            Altitude = altitude,
            IndicatedAirspeed = ias,
        };
    }

    [Fact]
    public void MachCommand_SetsTargetMach_ClearsTargetSpeed()
    {
        var ac = CreateAircraft();
        ac.Targets.TargetSpeed = 300;
        ac.Targets.SpeedFloor = 250;
        ac.Targets.SpeedCeiling = 320;

        var result = CommandDispatcher.Dispatch(new MachCommand(0.82), ac, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Equal(0.82, ac.Targets.TargetMach);
        Assert.Null(ac.Targets.SpeedFloor);
        Assert.Null(ac.Targets.SpeedCeiling);
        Assert.True(ac.Targets.HasExplicitSpeedCommand);
    }

    [Fact]
    public void MachToIas_AtFL350_ReturnsReasonableValue()
    {
        // At FL350, M0.82 → TAS ~470 kts → IAS ~262 kts (varies with ISA model)
        double ias = WindInterpolator.MachToIas(0.82, 35000);

        // Should be roughly 260-280 KIAS at FL350
        Assert.InRange(ias, 250, 290);
    }

    [Fact]
    public void TasToIas_RoundTrips_WithIasToTas()
    {
        double originalIas = 280;
        double altitude = 35000;

        double tas = WindInterpolator.IasToTas(originalIas, altitude);
        double recoveredIas = WindInterpolator.TasToIas(tas, altitude);

        Assert.InRange(recoveredIas, originalIas - 0.1, originalIas + 0.1);
    }

    [Fact]
    public void SpeedOfSound_AtSeaLevel_Is661Kts()
    {
        double sos = WindInterpolator.SpeedOfSoundKts(0);
        Assert.InRange(sos, 660, 662);
    }

    [Fact]
    public void SpeedOfSound_AboveTropopause_IsConstant()
    {
        double sos36 = WindInterpolator.SpeedOfSoundKts(36089);
        double sos40 = WindInterpolator.SpeedOfSoundKts(40000);
        Assert.Equal(sos36, sos40, precision: 1);
    }

    [Fact]
    public void UpdateSpeed_WithTargetMach_RecomputesIAS()
    {
        AircraftCategorization.Initialize([]);

        // Start at IAS 250 so there's a meaningful delta from M0.82 (~279 KIAS at FL350)
        var ac = CreateAircraft(altitude: 35000, ias: 250);
        ac.Targets.TargetMach = 0.82;

        FlightPhysics.Update(ac, 1.0);

        // Mach hold recomputes TargetSpeed each tick; aircraft should accelerate toward M0.82 IAS
        Assert.True(ac.IndicatedAirspeed > 250, "Aircraft should accelerate toward Mach-equivalent IAS");
        Assert.Equal(0.82, ac.Targets.TargetMach);
    }

    [Fact]
    public void SpeedCommand_ClearsTargetMach()
    {
        var ac = CreateAircraft();
        ac.Targets.TargetMach = 0.82;

        CommandDispatcher.Dispatch(new SpeedCommand(250), ac, null, Random.Shared, true);

        Assert.Null(ac.Targets.TargetMach);
    }

    [Fact]
    public void ResumeNormalSpeed_ClearsTargetMach()
    {
        var ac = CreateAircraft();
        ac.Targets.TargetMach = 0.82;

        CommandDispatcher.Dispatch(new ResumeNormalSpeedCommand(), ac, null, Random.Shared, true);

        Assert.Null(ac.Targets.TargetMach);
    }

    [Fact]
    public void DeleteSpeedRestrictions_ClearsTargetMach()
    {
        var ac = CreateAircraft();
        ac.Targets.TargetMach = 0.82;

        CommandDispatcher.Dispatch(new DeleteSpeedRestrictionsCommand(), ac, null, Random.Shared, true);

        Assert.Null(ac.Targets.TargetMach);
    }

    [Fact]
    public void ReduceToFinalApproachSpeed_ClearsTargetMach()
    {
        AircraftCategorization.Initialize([]);

        var ac = CreateAircraft();
        ac.Targets.TargetMach = 0.82;

        CommandDispatcher.Dispatch(new ReduceToFinalApproachSpeedCommand(), ac, null, Random.Shared, true);

        Assert.Null(ac.Targets.TargetMach);
    }
}
