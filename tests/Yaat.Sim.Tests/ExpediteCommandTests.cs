using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

public class ExpediteCommandTests
{
    private static AircraftState CreateAircraft(double altitude = 5000, double ias = 250)
    {
        return new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Position = new LatLon(37.0, -122.0),
            TrueHeading = new TrueHeading(360),
            TrueTrack = new TrueHeading(360),
            Altitude = altitude,
            IndicatedAirspeed = ias,
        };
    }

    [Fact]
    public void Expedite_SetsFlag_WhenClimbing()
    {
        var ac = CreateAircraft(altitude: 5000);
        ac.Targets.TargetAltitude = 10000;

        var result = CommandDispatcher.Dispatch(new ExpediteCommand(), ac, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.True(ac.Procedure.IsExpediting);
    }

    [Fact]
    public void Expedite_Rejected_WhenNoAltitudeTarget()
    {
        var ac = CreateAircraft();

        var result = CommandDispatcher.Dispatch(new ExpediteCommand(), ac, TestDispatch.Context(Random.Shared));

        Assert.False(result.Success);
        Assert.False(ac.Procedure.IsExpediting);
    }

    [Fact]
    public void Expedite_OnGroundWithRoute_SetsTaxiExpediting()
    {
        var ac = CreateAircraft();
        ac.IsOnGround = true;
        ac.Ground.AssignedTaxiRoute = new Yaat.Sim.Data.Airport.TaxiRoute { Segments = [], HoldShortPoints = [] };

        var result = CommandDispatcher.Dispatch(new ExpediteCommand(), ac, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.True(ac.Ground.IsExpeditingTaxi);
        Assert.False(ac.Procedure.IsExpediting);
    }

    [Fact]
    public void Expedite_OnGroundWithoutRoute_Fails()
    {
        var ac = CreateAircraft();
        ac.IsOnGround = true;
        ac.Ground.AssignedTaxiRoute = null;

        var result = CommandDispatcher.Dispatch(new ExpediteCommand(), ac, TestDispatch.Context(Random.Shared));

        Assert.False(result.Success);
        Assert.False(ac.Ground.IsExpeditingTaxi);
        Assert.Contains("taxi route", result.Message!);
    }

    [Fact]
    public void Expedite_WithUntilAltitude_StaysAirborneSemantics_EvenOnGround()
    {
        // EXP <alt> is unambiguously a climb/descent verb — don't intercept it
        // for taxi context. If aircraft is on the ground, it's still an
        // altitude verb, so it must reject without TargetAltitude.
        var ac = CreateAircraft();
        ac.IsOnGround = true;
        ac.Ground.AssignedTaxiRoute = new Yaat.Sim.Data.Airport.TaxiRoute { Segments = [], HoldShortPoints = [] };

        var result = CommandDispatcher.Dispatch(new ExpediteCommand(10000), ac, TestDispatch.Context(Random.Shared));

        Assert.False(result.Success);
        Assert.False(ac.Ground.IsExpeditingTaxi);
    }

    [Fact]
    public void NormalRate_ClearsFlag()
    {
        var ac = CreateAircraft();
        ac.Targets.TargetAltitude = 10000;
        ac.Procedure.IsExpediting = true;
        ac.Targets.DesiredVerticalRate = 3000;

        var result = CommandDispatcher.Dispatch(new NormalRateCommand(), ac, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.False(ac.Procedure.IsExpediting);
        Assert.Null(ac.Targets.DesiredVerticalRate);
    }

    [Fact]
    public void UpdateAltitude_Applies1_5xMultiplier_WhenExpediting()
    {
        TestVnasData.EnsureInitialized();

        var ac = CreateAircraft(altitude: 5000);
        ac.Targets.TargetAltitude = 10000;
        ac.Procedure.IsExpediting = true;

        // Record the climb without expedite for comparison
        var acNormal = CreateAircraft(altitude: 5000);
        acNormal.Targets.TargetAltitude = 10000;
        acNormal.Procedure.IsExpediting = false;

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
        ac.Procedure.IsExpediting = true;

        CommandDispatcher.Dispatch(new ClimbMaintainCommand(15000), ac, TestDispatch.Context(Random.Shared));

        Assert.False(ac.Procedure.IsExpediting);
    }

    [Fact]
    public void DescendMaintain_ClearsExpediteFlag()
    {
        var ac = CreateAircraft(altitude: 10000);
        ac.Targets.TargetAltitude = 5000;
        ac.Procedure.IsExpediting = true;

        CommandDispatcher.Dispatch(new DescendMaintainCommand(3000), ac, TestDispatch.Context(Random.Shared));

        Assert.False(ac.Procedure.IsExpediting);
    }

    [Fact]
    public void Expedite_ClearedAtAltitudeSnap()
    {
        TestVnasData.EnsureInitialized();

        var ac = CreateAircraft(altitude: 9995);
        ac.Targets.TargetAltitude = 10000;
        ac.Procedure.IsExpediting = true;

        // Should snap to target and clear expedite
        FlightPhysics.Update(ac, 10.0);

        Assert.Equal(10000, ac.Altitude);
        Assert.False(ac.Procedure.IsExpediting);
    }

    [Fact]
    public void ExpediteWithAltitude_AddsQueueBlock()
    {
        var ac = CreateAircraft(altitude: 5000);
        ac.Targets.TargetAltitude = 15000;

        var result = CommandDispatcher.Dispatch(new ExpediteCommand(10000), ac, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.True(ac.Procedure.IsExpediting);
        Assert.Single(ac.Queue.Blocks);
        Assert.Equal(BlockTriggerType.ReachAltitude, ac.Queue.Blocks[0].Trigger!.Type);
        Assert.Equal(10000, ac.Queue.Blocks[0].Trigger!.Altitude);
    }
}
