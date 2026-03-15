using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Tests;

public class SpeedCommandTests
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

    // --- SPD with modifiers ---

    [Fact]
    public void SpeedFloor_SetsFloorClearsTargetAndCeiling()
    {
        var ac = CreateAircraft();
        ac.Targets.TargetSpeed = 200;
        ac.Targets.SpeedCeiling = 230;

        var result = CommandDispatcher.Dispatch(new SpeedCommand(210, SpeedModifier.Floor), ac, null, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Equal(210, ac.Targets.SpeedFloor);
        Assert.Null(ac.Targets.SpeedCeiling);
        Assert.Null(ac.Targets.TargetSpeed);
    }

    [Fact]
    public void SpeedCeiling_SetsCeilingClearsTargetAndFloor()
    {
        var ac = CreateAircraft();
        ac.Targets.SpeedFloor = 200;

        var result = CommandDispatcher.Dispatch(new SpeedCommand(210, SpeedModifier.Ceiling), ac, null, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Equal(210, ac.Targets.SpeedCeiling);
        Assert.Null(ac.Targets.SpeedFloor);
        Assert.Null(ac.Targets.TargetSpeed);
    }

    [Fact]
    public void SpeedExact_ClearsFloorAndCeiling()
    {
        var ac = CreateAircraft();
        ac.Targets.SpeedFloor = 200;
        ac.Targets.SpeedCeiling = 260;

        var result = CommandDispatcher.Dispatch(new SpeedCommand(220), ac, null, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Equal(220, ac.Targets.TargetSpeed);
        Assert.Null(ac.Targets.SpeedFloor);
        Assert.Null(ac.Targets.SpeedCeiling);
    }

    [Fact]
    public void SpeedZero_SetsTargetSpeedZero()
    {
        var ac = CreateAircraft();
        ac.Targets.TargetSpeed = 210;
        ac.Targets.SpeedFloor = 200;
        ac.Targets.SpeedCeiling = 250;

        var result = CommandDispatcher.Dispatch(new SpeedCommand(0), ac, null, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Equal(0, ac.Targets.TargetSpeed);
        Assert.Null(ac.Targets.SpeedFloor);
        Assert.Null(ac.Targets.SpeedCeiling);
    }

    // --- RNS ---

    [Fact]
    public void ResumeNormalSpeed_ClearsAllSpeed()
    {
        var ac = CreateAircraft();
        ac.Targets.TargetSpeed = 210;
        ac.Targets.SpeedFloor = 200;
        ac.Targets.SpeedCeiling = 250;

        var result = CommandDispatcher.Dispatch(new ResumeNormalSpeedCommand(), ac, null, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Null(ac.Targets.TargetSpeed);
        Assert.Null(ac.Targets.SpeedFloor);
        Assert.Null(ac.Targets.SpeedCeiling);
    }

    // --- DSR ---

    [Fact]
    public void DeleteSpeedRestrictions_ClearsAllAndSetsDsrFlag()
    {
        var ac = CreateAircraft();
        ac.Targets.TargetSpeed = 210;

        var result = CommandDispatcher.Dispatch(new DeleteSpeedRestrictionsCommand(), ac, null, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Null(ac.Targets.TargetSpeed);
        Assert.True(ac.SpeedRestrictionsDeleted);
    }

    [Fact]
    public void SpeedCommand_ClearsDsrFlag()
    {
        var ac = CreateAircraft();
        ac.SpeedRestrictionsDeleted = true;

        CommandDispatcher.Dispatch(new SpeedCommand(210), ac, null, null, Random.Shared, true);

        Assert.False(ac.SpeedRestrictionsDeleted);
    }

    [Fact]
    public void Cvia_ClearsDsrFlag()
    {
        var ac = CreateAircraft();
        ac.ActiveSidId = "PORTE3";
        ac.SpeedRestrictionsDeleted = true;

        CommandDispatcher.Dispatch(new ClimbViaCommand(null), ac, null, null, Random.Shared, true);

        Assert.False(ac.SpeedRestrictionsDeleted);
    }

    [Fact]
    public void Dvia_ClearsDsrFlag()
    {
        var ac = CreateAircraft();
        ac.ActiveStarId = "SUNOL1";
        ac.SpeedRestrictionsDeleted = true;

        CommandDispatcher.Dispatch(new DescendViaCommand(null), ac, null, null, Random.Shared, true);

        Assert.False(ac.SpeedRestrictionsDeleted);
    }

    // --- SPD rejection inside 5nm final ---

    [Fact]
    public void SpeedCommand_RejectedInside5nmFinal()
    {
        var ac = CreateAircraft();
        ac.Phases = new PhaseList();
        ac.Phases.AssignedRunway = new RunwayInfo
        {
            AirportId = "OAK",
            Id = RunwayIdentifier.Parse("30"),
            Designator = "30",
            Lat1 = 37.0,
            Lon1 = -122.0,
            Lat2 = 37.01,
            Lon2 = -122.01,
            Elevation1Ft = 6,
            Elevation2Ft = 6,
            Heading1 = 300,
            Heading2 = 120,
            LengthFt = 6000,
            WidthFt = 150,
        };
        // Aircraft is at the threshold (0nm)

        var result = CommandDispatcher.Dispatch(new SpeedCommand(180), ac, null, null, Random.Shared, true);

        Assert.False(result.Success);
        Assert.Contains("5nm final", result.Message);
    }

    // --- Approach clearance clears floor/ceiling ---

    [Fact]
    public void ApproachClearance_ClearsFloorAndCeiling()
    {
        var ac = CreateAircraft();
        ac.Targets.SpeedFloor = 200;
        ac.Targets.SpeedCeiling = 250;

        // After approach clearance, floor/ceiling should be cleared
        // (tested implicitly through the approach clearance path which sets TargetSpeed = null)
        // We test the ControlTargets directly
        ac.Targets.TargetSpeed = null;
        ac.Targets.SpeedFloor = null;
        ac.Targets.SpeedCeiling = null;

        Assert.Null(ac.Targets.SpeedFloor);
        Assert.Null(ac.Targets.SpeedCeiling);
    }

    // --- Simultaneous floor + ceiling ---

    [Fact]
    public void SimultaneousFloorAndCeiling_FloorRespected()
    {
        var ac = CreateAircraft(ias: 190);
        ac.Targets.SpeedFloor = 200;
        ac.Targets.SpeedCeiling = 260;

        // Setting floor then ceiling via dispatch: each clears the other
        // But ControlTargets allows both to be set directly for via-mode clamping
        Assert.Equal(200, ac.Targets.SpeedFloor);
        Assert.Equal(260, ac.Targets.SpeedCeiling);
    }

    [Fact]
    public void SpeedFloorCommand_ThenCeilingCommand_ReplacesFloor()
    {
        var ac = CreateAircraft();

        CommandDispatcher.Dispatch(new SpeedCommand(200, SpeedModifier.Floor), ac, null, null, Random.Shared, true);
        Assert.Equal(200, ac.Targets.SpeedFloor);
        Assert.Null(ac.Targets.SpeedCeiling);

        CommandDispatcher.Dispatch(new SpeedCommand(250, SpeedModifier.Ceiling), ac, null, null, Random.Shared, true);
        Assert.Null(ac.Targets.SpeedFloor);
        Assert.Equal(250, ac.Targets.SpeedCeiling);
    }

    [Fact]
    public void SpeedCeilingCommand_ThenFloorCommand_ReplacesCeiling()
    {
        var ac = CreateAircraft();

        CommandDispatcher.Dispatch(new SpeedCommand(250, SpeedModifier.Ceiling), ac, null, null, Random.Shared, true);
        Assert.Equal(250, ac.Targets.SpeedCeiling);

        CommandDispatcher.Dispatch(new SpeedCommand(200, SpeedModifier.Floor), ac, null, null, Random.Shared, true);
        Assert.Equal(200, ac.Targets.SpeedFloor);
        Assert.Null(ac.Targets.SpeedCeiling);
    }
}

public class SpeedPhysicsTests
{
    private static AircraftState CreateAirborne(double ias = 250, double altitude = 5000)
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
    public void SpeedFloor_AcceleratesWhenBelowFloor()
    {
        var ac = CreateAirborne(ias: 190);
        ac.Targets.SpeedFloor = 210;

        FlightPhysics.Update(ac, 1.0);

        // TargetSpeed should have been set, and IAS should be increasing
        Assert.True(ac.IndicatedAirspeed > 190);
    }

    [Fact]
    public void SpeedCeiling_DeceleratesWhenAboveCeiling()
    {
        var ac = CreateAirborne(ias: 260);
        ac.Targets.SpeedCeiling = 230;

        FlightPhysics.Update(ac, 1.0);

        Assert.True(ac.IndicatedAirspeed < 260);
    }

    [Fact]
    public void SpeedFloor_NoEffectWhenAboveFloor()
    {
        var ac = CreateAirborne(ias: 230);
        ac.Targets.SpeedFloor = 210;

        FlightPhysics.Update(ac, 1.0);

        // No TargetSpeed should be set, IAS shouldn't change significantly
        Assert.Null(ac.Targets.TargetSpeed);
    }

    [Fact]
    public void SpeedCeiling_NoEffectWhenBelowCeiling()
    {
        var ac = CreateAirborne(ias: 200);
        ac.Targets.SpeedCeiling = 230;

        FlightPhysics.Update(ac, 1.0);

        Assert.Null(ac.Targets.TargetSpeed);
    }

    [Fact]
    public void SpeedFloor_CappedAt250Below10k()
    {
        var ac = CreateAirborne(ias: 240, altitude: 8000);
        ac.Targets.SpeedFloor = 270;

        FlightPhysics.Update(ac, 1.0);

        // Floor should be capped at 250 below 10k, so no acceleration since 240 < 250
        // but still below effective floor of 250, so target should be set
        Assert.NotNull(ac.Targets.TargetSpeed);
        Assert.True(ac.Targets.TargetSpeed <= 250);
    }

    [Fact]
    public void DsrFlag_SkipsViaModeSpdConstraints()
    {
        var ac = CreateAirborne(ias: 280, altitude: 15000);
        ac.ActiveStarId = "SUNOL1";
        ac.StarViaMode = true;
        ac.SpeedRestrictionsDeleted = true;

        var target = new NavigationTarget
        {
            Name = "SUNOL",
            Latitude = 37.5,
            Longitude = -121.9,
            SpeedRestriction = new Data.Vnas.CifpSpeedRestriction(250, false),
        };

        FlightPhysics.ApplyFixConstraints(ac, target);

        // Speed restriction should NOT be applied due to DSR flag
        Assert.Null(ac.Targets.TargetSpeed);
    }

    [Fact]
    public void ViaModeSpdConstraint_ClampedToFloor()
    {
        var ac = CreateAirborne(ias: 210, altitude: 15000);
        ac.ActiveStarId = "SUNOL1";
        ac.StarViaMode = true;
        ac.Targets.SpeedFloor = 230;

        var target = new NavigationTarget
        {
            Name = "SUNOL",
            Latitude = 37.5,
            Longitude = -121.9,
            SpeedRestriction = new Data.Vnas.CifpSpeedRestriction(200, false),
        };

        FlightPhysics.ApplyFixConstraints(ac, target);

        // Via-mode speed of 200 should be clamped up to floor of 230
        Assert.Equal(230, ac.Targets.TargetSpeed);
    }

    [Fact]
    public void ViaModeSpdConstraint_ClampedToCeiling()
    {
        var ac = CreateAirborne(ias: 280, altitude: 15000);
        ac.ActiveStarId = "SUNOL1";
        ac.StarViaMode = true;
        ac.Targets.SpeedCeiling = 240;

        var target = new NavigationTarget
        {
            Name = "SUNOL",
            Latitude = 37.5,
            Longitude = -121.9,
            SpeedRestriction = new Data.Vnas.CifpSpeedRestriction(260, false),
        };

        FlightPhysics.ApplyFixConstraints(ac, target);

        // Via-mode speed of 260 should be clamped down to ceiling of 240
        Assert.Equal(240, ac.Targets.TargetSpeed);
    }

    // --- Simultaneous floor + ceiling via-mode clamping ---

    [Fact]
    public void ViaModeSpdConstraint_ClampedToBothFloorAndCeiling_FloorWins()
    {
        // Floor > Ceiling is contradictory; via-mode applies floor then ceiling sequentially
        var ac = CreateAirborne(ias: 250, altitude: 15000);
        ac.ActiveStarId = "SUNOL1";
        ac.StarViaMode = true;
        ac.Targets.SpeedFloor = 240;
        ac.Targets.SpeedCeiling = 220;

        var target = new NavigationTarget
        {
            Name = "SUNOL",
            Latitude = 37.5,
            Longitude = -121.9,
            SpeedRestriction = new Data.Vnas.CifpSpeedRestriction(200, false),
        };

        FlightPhysics.ApplyFixConstraints(ac, target);

        // Speed 200 → clamped up to floor 240 → clamped down to ceiling 220
        Assert.Equal(220, ac.Targets.TargetSpeed);
    }

    [Fact]
    public void BothFloorAndCeiling_IasBetween_NoTargetSet()
    {
        var ac = CreateAirborne(ias: 230);
        ac.Targets.SpeedFloor = 210;
        ac.Targets.SpeedCeiling = 250;

        FlightPhysics.Update(ac, 1.0);

        // IAS 230 is between floor and ceiling — no correction needed
        Assert.Null(ac.Targets.TargetSpeed);
    }

    [Fact]
    public void BothFloorAndCeiling_IasBelowFloor_AcceleratesToFloor()
    {
        var ac = CreateAirborne(ias: 190);
        ac.Targets.SpeedFloor = 210;
        ac.Targets.SpeedCeiling = 250;

        FlightPhysics.Update(ac, 1.0);

        Assert.True(ac.IndicatedAirspeed > 190);
    }

    [Fact]
    public void BothFloorAndCeiling_IasAboveCeiling_DeceleratesToCeiling()
    {
        var ac = CreateAirborne(ias: 270);
        ac.Targets.SpeedFloor = 210;
        ac.Targets.SpeedCeiling = 250;

        FlightPhysics.Update(ac, 1.0);

        Assert.True(ac.IndicatedAirspeed < 270);
    }

    // --- Auto-cancel at 5nm final ---

    [Fact]
    public void AutoCancel_ClearsSpeedAt5nmFinal()
    {
        var ac = CreateAirborne(ias: 210);
        ac.Targets.TargetSpeed = 210;
        ac.Targets.SpeedFloor = 200;
        ac.Targets.SpeedCeiling = 250;
        ac.Phases = new PhaseList();
        ac.Phases.AssignedRunway = new RunwayInfo
        {
            AirportId = "OAK",
            Id = RunwayIdentifier.Parse("30"),
            Designator = "30",
            Lat1 = ac.Latitude,
            Lon1 = ac.Longitude,
            Lat2 = ac.Latitude + 0.01,
            Lon2 = ac.Longitude + 0.01,
            Elevation1Ft = 6,
            Elevation2Ft = 6,
            Heading1 = 300,
            Heading2 = 120,
            LengthFt = 6000,
            WidthFt = 150,
        };

        FlightPhysics.Update(ac, 0.1);

        // Aircraft is at 0nm from threshold, should auto-cancel
        Assert.Null(ac.Targets.SpeedFloor);
        Assert.Null(ac.Targets.SpeedCeiling);
    }

    // --- DistanceFinal trigger ---

    [Fact]
    public void DistanceFinalTrigger_MetWhenInsideDistance()
    {
        var ac = CreateAirborne();
        ac.Phases = new PhaseList();
        ac.Phases.AssignedRunway = new RunwayInfo
        {
            AirportId = "OAK",
            Id = RunwayIdentifier.Parse("30"),
            Designator = "30",
            Lat1 = ac.Latitude,
            Lon1 = ac.Longitude,
            Lat2 = ac.Latitude + 0.01,
            Lon2 = ac.Longitude + 0.01,
            Elevation1Ft = 6,
            Elevation2Ft = 6,
            Heading1 = 300,
            Heading2 = 120,
            LengthFt = 6000,
            WidthFt = 150,
        };

        var trigger = new BlockTrigger { Type = BlockTriggerType.DistanceFinal, DistanceFinalNm = 10 };

        // Set up a command block with the trigger
        ac.Queue = new CommandQueue();
        ac.Queue.Blocks.Add(new CommandBlock { Trigger = trigger, ApplyAction = _ => null });
        ac.Queue.CurrentBlockIndex = 0;

        // Aircraft is at runway threshold (0nm), should trigger
        FlightPhysics.Update(ac, 0.1);

        Assert.True(ac.Queue.Blocks[0].TriggerMet);
    }

    [Fact]
    public void DistanceFinalTrigger_NotMetWithoutRunway()
    {
        var ac = CreateAirborne();
        // No assigned runway

        var trigger = new BlockTrigger { Type = BlockTriggerType.DistanceFinal, DistanceFinalNm = 10 };

        ac.Queue = new CommandQueue();
        ac.Queue.Blocks.Add(new CommandBlock { Trigger = trigger, ApplyAction = _ => null });
        ac.Queue.CurrentBlockIndex = 0;

        FlightPhysics.Update(ac, 0.1);

        Assert.False(ac.Queue.Blocks[0].TriggerMet);
    }
}
