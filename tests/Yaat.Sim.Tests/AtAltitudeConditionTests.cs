using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

public class AtAltitudeConditionTests
{
    private static readonly CommandScheme Scheme = CommandScheme.Default();
    private static readonly NavigationDatabase NavDb = TestNavDbFactory.WithFixNames("SUNOL", "BRIXX");

    private static AircraftState MakeAircraft(double altitude = 3000, double heading = 090, double ias = 250)
    {
        return new AircraftState
        {
            Callsign = "TST01",
            AircraftType = "B738",
            Latitude = 37.7,
            Longitude = -122.2,
            Heading = heading,
            Track = heading,
            Altitude = altitude,
            IndicatedAirspeed = ias,
            IsOnGround = false,
        };
    }

    // -------------------------------------------------------------------------
    // CommandSchemeParser: canonical form
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("AT 5000 CM 280", "AT 5000 CM 280")]
    [InlineData("AT 050 DM 100", "AT 050 DM 100")]
    [InlineData("AT 19100 CM 370", "AT 19100 CM 370")]
    [InlineData("AT 25000 DEL", "AT 25000 DEL")]
    [InlineData("AT 1500 CVIA 230", "AT 1500 CVIA 230")]
    public void SchemeParser_AtAltitude_ProducesCanonical(string input, string expected)
    {
        var result = CommandSchemeParser.ParseCompound(input, Scheme);

        Assert.NotNull(result);
        Assert.Equal(expected, result.CanonicalString);
    }

    [Fact]
    public void SchemeParser_AtAltitude_WithoutCommand_ReturnsNull()
    {
        var result = CommandSchemeParser.ParseCompound("AT 5000", Scheme);

        Assert.Null(result);
    }

    [Fact]
    public void SchemeParser_AtFix_StillWorks()
    {
        var result = CommandSchemeParser.ParseCompound("AT SUNOL FH 090", Scheme);

        Assert.NotNull(result);
        Assert.Equal("AT SUNOL FH 090", result.CanonicalString);
    }

    // -------------------------------------------------------------------------
    // CommandParser: parsed block structure
    // -------------------------------------------------------------------------

    [Fact]
    public void CommandParser_AtAltitude_ProducesLevelCondition()
    {
        var result = CommandParser.ParseCompound("AT 5000 CM 280", NavDb);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value!.Blocks);
        Assert.IsType<LevelCondition>(result.Value!.Blocks[0].Condition);
        var cond = (LevelCondition)result.Value!.Blocks[0].Condition!;
        Assert.Equal(5000, cond.Altitude);
        Assert.Single(result.Value!.Blocks[0].Commands);
        Assert.IsType<ClimbMaintainCommand>(result.Value!.Blocks[0].Commands[0]);
    }

    [Fact]
    public void CommandParser_At3DigitAltitude_ResolvesCorrectly()
    {
        var result = CommandParser.ParseCompound("AT 050 DM 100", NavDb);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value!.Blocks);
        var cond = Assert.IsType<LevelCondition>(result.Value!.Blocks[0].Condition);
        Assert.Equal(5000, cond.Altitude);
    }

    [Fact]
    public void CommandParser_AtFix_StillProducesAtFixCondition()
    {
        var result = CommandParser.ParseCompound("AT SUNOL FH 090", NavDb);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value!.Blocks);
        Assert.IsType<AtFixCondition>(result.Value!.Blocks[0].Condition);
    }

    // -------------------------------------------------------------------------
    // E2E: parse → dispatch → tick → verify trigger
    // -------------------------------------------------------------------------

    [Fact]
    public void E2E_AtAltitude_CM_DoesNotFire_BelowTarget()
    {
        var aircraft = MakeAircraft(altitude: 3000);
        var compound = CommandParser.ParseCompound("AT 5000 CM 280", NavDb);
        Assert.True(compound.IsSuccess);

        CommandDispatcher.DispatchCompound(compound.Value!, aircraft, NavDb, null, Random.Shared, true);

        Assert.Single(aircraft.Queue.Blocks);
        Assert.Equal(BlockTriggerType.ReachAltitude, aircraft.Queue.Blocks[0].Trigger!.Type);
        Assert.Equal(5000, aircraft.Queue.Blocks[0].Trigger!.Altitude);

        // Tick — aircraft at 3000, target 5000 → trigger not met
        FlightPhysics.Update(aircraft, 1.0, null, null);
        Assert.False(aircraft.Queue.Blocks[0].IsApplied);
    }

    [Fact]
    public void E2E_AtAltitude_CM_Fires_WhenAtTarget()
    {
        var aircraft = MakeAircraft(altitude: 3000);
        var compound = CommandParser.ParseCompound("AT 5000 CM 280", NavDb);
        Assert.True(compound.IsSuccess);

        CommandDispatcher.DispatchCompound(compound.Value!, aircraft, NavDb, null, Random.Shared, true);

        // Move aircraft to within snap range of 5000
        aircraft.Altitude = 4998;
        FlightPhysics.Update(aircraft, 1.0, null, null);

        Assert.True(aircraft.Queue.Blocks[0].IsApplied);
    }

    [Fact]
    public void E2E_AtAltitude_DEL_QueuedWithCorrectTrigger()
    {
        var aircraft = MakeAircraft(altitude: 20000);
        var compound = CommandParser.ParseCompound("AT 25000 DEL", NavDb);
        Assert.True(compound.IsSuccess);

        CommandDispatcher.DispatchCompound(compound.Value!, aircraft, NavDb, null, Random.Shared, true);

        Assert.Single(aircraft.Queue.Blocks);
        Assert.Equal(BlockTriggerType.ReachAltitude, aircraft.Queue.Blocks[0].Trigger!.Type);
        Assert.Equal(25000, aircraft.Queue.Blocks[0].Trigger!.Altitude);
        Assert.False(aircraft.Queue.Blocks[0].IsApplied);
    }

    [Fact]
    public void E2E_AtAltitude_FH_Fires_SetsHeading()
    {
        var aircraft = MakeAircraft(altitude: 2990, heading: 180);
        var compound = CommandParser.ParseCompound("AT 3000 FH 270", NavDb);
        Assert.True(compound.IsSuccess);

        CommandDispatcher.DispatchCompound(compound.Value!, aircraft, NavDb, null, Random.Shared, true);

        // Within snap range of 3000
        aircraft.Altitude = 3005;
        FlightPhysics.Update(aircraft, 1.0, null, null);

        Assert.True(aircraft.Queue.Blocks[0].IsApplied);
        Assert.Equal(270.0, aircraft.Targets.TargetHeading);
    }

    [Fact]
    public void E2E_AtAltitude_ChainedBlocks_FireSequentially()
    {
        // "AT 5000 FH 180; AT 9000 FH 270" — two sequential FH blocks (FH = Heading type, completes when no target heading remains)
        var aircraft = MakeAircraft(altitude: 3000, heading: 090);
        var compound = CommandParser.ParseCompound("AT 5000 FH 180; AT 9000 FH 270", NavDb);
        Assert.True(compound.IsSuccess);

        CommandDispatcher.DispatchCompound(compound.Value!, aircraft, NavDb, null, Random.Shared, true);

        Assert.Equal(2, aircraft.Queue.Blocks.Count);

        // Block 0: AT 5000 FH 180 — not yet
        FlightPhysics.Update(aircraft, 1.0, null, null);
        Assert.False(aircraft.Queue.Blocks[0].IsApplied);
        Assert.False(aircraft.Queue.Blocks[1].IsApplied);

        // Reach 5000 → block 0 fires, sets heading to 180
        aircraft.Altitude = 5000;
        FlightPhysics.Update(aircraft, 1.0, null, null);
        Assert.True(aircraft.Queue.Blocks[0].IsApplied);
        Assert.Equal(180.0, aircraft.Targets.TargetHeading);
        Assert.False(aircraft.Queue.Blocks[1].IsApplied);

        // Simulate heading reaching target so block 0 completes
        aircraft.Heading = 180;
        aircraft.Targets.TargetHeading = null;
        FlightPhysics.Update(aircraft, 1.0, null, null);
        Assert.Equal(1, aircraft.Queue.CurrentBlockIndex);

        // Block 1 not met yet (altitude 5000, target 9000)
        Assert.False(aircraft.Queue.Blocks[1].IsApplied);

        // Reach 9000 → block 1 fires
        aircraft.Altitude = 9000;
        FlightPhysics.Update(aircraft, 1.0, null, null);
        Assert.True(aircraft.Queue.Blocks[1].IsApplied);
        Assert.Equal(270.0, aircraft.Targets.TargetHeading);
    }
}
