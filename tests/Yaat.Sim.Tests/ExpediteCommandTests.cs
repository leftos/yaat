using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

public class ExpediteCommandTests
{
    private static AircraftState CreateAircraft(double altitude = 5000, double ias = 250)
    {
        return new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Latitude = 37.0,
            Longitude = -122.0,
            Heading = 360,
            Track = 360,
            Altitude = altitude,
            IndicatedAirspeed = ias,
        };
    }

    [Fact]
    public void Expedite_SetsFlag_WhenClimbing()
    {
        var ac = CreateAircraft(altitude: 5000);
        ac.Targets.TargetAltitude = 10000;

        var result = CommandDispatcher.Dispatch(new ExpediteCommand(), ac, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.True(ac.IsExpediting);
    }

    [Fact]
    public void Expedite_Rejected_WhenNoAltitudeTarget()
    {
        var ac = CreateAircraft();

        var result = CommandDispatcher.Dispatch(new ExpediteCommand(), ac, null, Random.Shared, true);

        Assert.False(result.Success);
        Assert.False(ac.IsExpediting);
    }

    [Fact]
    public void NormalRate_ClearsFlag()
    {
        var ac = CreateAircraft();
        ac.Targets.TargetAltitude = 10000;
        ac.IsExpediting = true;
        ac.Targets.DesiredVerticalRate = 3000;

        var result = CommandDispatcher.Dispatch(new NormalRateCommand(), ac, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.False(ac.IsExpediting);
        Assert.Null(ac.Targets.DesiredVerticalRate);
    }

    [Fact]
    public void UpdateAltitude_Applies1_5xMultiplier_WhenExpediting()
    {
        AircraftCategorization.Initialize([]);

        var ac = CreateAircraft(altitude: 5000);
        ac.Targets.TargetAltitude = 10000;
        ac.IsExpediting = true;

        // Record the climb without expedite for comparison
        var acNormal = CreateAircraft(altitude: 5000);
        acNormal.Targets.TargetAltitude = 10000;
        acNormal.IsExpediting = false;

        FlightPhysics.Update(ac, 10.0);
        FlightPhysics.Update(acNormal, 10.0);

        // Expediting aircraft should have climbed ~1.5x as much
        double expClimb = ac.Altitude - 5000;
        double normClimb = acNormal.Altitude - 5000;
        Assert.True(expClimb > normClimb, $"Expedite climb {expClimb} should exceed normal {normClimb}");
        Assert.InRange(expClimb / normClimb, 1.45, 1.55);
    }

    [Fact]
    public void ClimbMaintain_ClearsExpediteFlag()
    {
        var ac = CreateAircraft(altitude: 5000);
        ac.Targets.TargetAltitude = 10000;
        ac.IsExpediting = true;

        CommandDispatcher.Dispatch(new ClimbMaintainCommand(15000), ac, null, Random.Shared, true);

        Assert.False(ac.IsExpediting);
    }

    [Fact]
    public void DescendMaintain_ClearsExpediteFlag()
    {
        var ac = CreateAircraft(altitude: 10000);
        ac.Targets.TargetAltitude = 5000;
        ac.IsExpediting = true;

        CommandDispatcher.Dispatch(new DescendMaintainCommand(3000), ac, null, Random.Shared, true);

        Assert.False(ac.IsExpediting);
    }

    [Fact]
    public void Expedite_ClearedAtAltitudeSnap()
    {
        AircraftCategorization.Initialize([]);

        var ac = CreateAircraft(altitude: 9995);
        ac.Targets.TargetAltitude = 10000;
        ac.IsExpediting = true;

        // Should snap to target and clear expedite
        FlightPhysics.Update(ac, 10.0);

        Assert.Equal(10000, ac.Altitude);
        Assert.False(ac.IsExpediting);
    }

    [Fact]
    public void ExpediteWithAltitude_AddsQueueBlock()
    {
        var ac = CreateAircraft(altitude: 5000);
        ac.Targets.TargetAltitude = 15000;

        var result = CommandDispatcher.Dispatch(new ExpediteCommand(10000), ac, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.True(ac.IsExpediting);
        Assert.Single(ac.Queue.Blocks);
        Assert.Equal(BlockTriggerType.ReachAltitude, ac.Queue.Blocks[0].Trigger!.Type);
        Assert.Equal(10000, ac.Queue.Blocks[0].Trigger!.Altitude);
    }
}
