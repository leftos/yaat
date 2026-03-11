using Xunit;

namespace Yaat.Sim.Tests;

public class CommandQueueTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static AircraftState MakeAircraft(double lat = 37.7, double lon = -122.2, double heading = 090, double altitude = 10_000)
    {
        return new AircraftState
        {
            Callsign = "TST01",
            AircraftType = "B738",
            Latitude = lat,
            Longitude = lon,
            Heading = heading,
            Altitude = altitude,
            IndicatedAirspeed = 250,
            IsOnGround = false,
        };
    }

    private static CommandBlock ImmediateBlock(Action<AircraftState>? applyAction = null) =>
        new() { Commands = [new TrackedCommand { Type = TrackedCommandType.Immediate }], ApplyAction = applyAction };

    private static CommandBlock TriggeredBlock(BlockTrigger trigger, Action<AircraftState>? applyAction = null) =>
        new()
        {
            Trigger = trigger,
            Commands = [new TrackedCommand { Type = TrackedCommandType.Immediate }],
            ApplyAction = applyAction,
        };

    // -------------------------------------------------------------------------
    // Trigger Tests
    // -------------------------------------------------------------------------

    [Fact]
    public void NullTrigger_AppliesImmediately()
    {
        var ac = MakeAircraft();
        ac.Queue.Blocks.Add(ImmediateBlock());

        FlightPhysics.Update(ac, 1.0, null, null);

        Assert.True(ac.Queue.Blocks[0].IsApplied);
    }

    [Fact]
    public void ReachAltitude_NotMet_WhenFarFromTarget()
    {
        var ac = MakeAircraft(altitude: 5_000);
        ac.Queue.Blocks.Add(TriggeredBlock(new BlockTrigger { Type = BlockTriggerType.ReachAltitude, Altitude = 10_000 }));

        FlightPhysics.Update(ac, 1.0, null, null);

        Assert.False(ac.Queue.Blocks[0].IsApplied);
    }

    [Fact]
    public void ReachAltitude_Met_WhenWithinSnapRange()
    {
        // AltitudeSnapFt = 10.0 — diff of 5 ft is within range
        var ac = MakeAircraft(altitude: 9_995);
        ac.Queue.Blocks.Add(TriggeredBlock(new BlockTrigger { Type = BlockTriggerType.ReachAltitude, Altitude = 10_000 }));

        FlightPhysics.Update(ac, 1.0, null, null);

        Assert.True(ac.Queue.Blocks[0].IsApplied);
    }

    [Fact]
    public void ReachFix_NotMet_WhenFar()
    {
        // Aircraft at 37.7/-122.2; fix at 38.5/-122.2 — roughly 48nm apart
        var ac = MakeAircraft(lat: 37.7, lon: -122.2);
        ac.Queue.Blocks.Add(
            TriggeredBlock(
                new BlockTrigger
                {
                    Type = BlockTriggerType.ReachFix,
                    FixLat = 38.5,
                    FixLon = -122.2,
                }
            )
        );

        FlightPhysics.Update(ac, 1.0, null, null);

        Assert.False(ac.Queue.Blocks[0].IsApplied);
    }

    [Fact]
    public void ReachFix_Met_WhenWithinHalfNm()
    {
        // NavArrivalNm = 0.5 — place fix 0.4nm north of aircraft (0.4/60 ≈ 0.00667°)
        var ac = MakeAircraft(lat: 37.7, lon: -122.2);
        double fixLat = ac.Latitude + (0.4 / 60.0);
        ac.Queue.Blocks.Add(
            TriggeredBlock(
                new BlockTrigger
                {
                    Type = BlockTriggerType.ReachFix,
                    FixLat = fixLat,
                    FixLon = -122.2,
                }
            )
        );

        FlightPhysics.Update(ac, 1.0, null, null);

        Assert.True(ac.Queue.Blocks[0].IsApplied);
    }

    [Fact]
    public void InterceptRadial_Met_WhenWithin3Degrees()
    {
        // Fix at 37.5/-122.2. Aircraft due north of fix at 37.7/-122.2.
        // Bearing from fix to aircraft ≈ 000°. Radial 001° → diff = 1° < 3°.
        var ac = MakeAircraft(lat: 37.7, lon: -122.2);
        ac.Queue.Blocks.Add(
            TriggeredBlock(
                new BlockTrigger
                {
                    Type = BlockTriggerType.InterceptRadial,
                    FixLat = 37.5,
                    FixLon = -122.2,
                    Radial = 001,
                }
            )
        );

        FlightPhysics.Update(ac, 1.0, null, null);

        Assert.True(ac.Queue.Blocks[0].IsApplied);
    }

    [Fact]
    public void InterceptRadial_NotMet_WhenFarFromRadial()
    {
        // Fix at 37.5/-122.2. Aircraft due north → bearing ≈ 000°. Radial 090° → diff = 90° >> 3°.
        var ac = MakeAircraft(lat: 37.7, lon: -122.2);
        ac.Queue.Blocks.Add(
            TriggeredBlock(
                new BlockTrigger
                {
                    Type = BlockTriggerType.InterceptRadial,
                    FixLat = 37.5,
                    FixLon = -122.2,
                    Radial = 090,
                }
            )
        );

        FlightPhysics.Update(ac, 1.0, null, null);

        Assert.False(ac.Queue.Blocks[0].IsApplied);
    }

    [Fact]
    public void GiveWay_Met_WhenTargetGone()
    {
        // No aircraftLookup provided → trigger resolves to true
        var ac = MakeAircraft();
        ac.IsOnGround = true;
        ac.Queue.Blocks.Add(TriggeredBlock(new BlockTrigger { Type = BlockTriggerType.GiveWay, TargetCallsign = "OTHER" }));

        FlightPhysics.Update(ac, 1.0, null, null);

        Assert.True(ac.Queue.Blocks[0].IsApplied);
    }

    [Fact]
    public void GiveWay_Met_WhenTargetAirborne()
    {
        var ac = MakeAircraft(lat: 37.7, lon: -122.2);
        ac.IsOnGround = true;

        var target = MakeAircraft(lat: 37.7001, lon: -122.2);
        target.Callsign = "OTHER";
        target.IsOnGround = false; // airborne — no ground conflict

        var lookup = new Dictionary<string, AircraftState> { ["OTHER"] = target };

        ac.Queue.Blocks.Add(TriggeredBlock(new BlockTrigger { Type = BlockTriggerType.GiveWay, TargetCallsign = "OTHER" }));

        FlightPhysics.Update(ac, 1.0, s => lookup.GetValueOrDefault(s), null);

        Assert.True(ac.Queue.Blocks[0].IsApplied);
    }

    [Fact]
    public void GiveWay_Met_WhenTargetFar()
    {
        // Distance > 0.1nm → conflict resolved
        var ac = MakeAircraft(lat: 37.7, lon: -122.2);
        ac.IsOnGround = true;

        // 0.5nm north: 0.5/60 ≈ 0.00833° lat
        var target = MakeAircraft(lat: 37.7083, lon: -122.2);
        target.Callsign = "OTHER";
        target.IsOnGround = true;

        var lookup = new Dictionary<string, AircraftState> { ["OTHER"] = target };

        ac.Queue.Blocks.Add(TriggeredBlock(new BlockTrigger { Type = BlockTriggerType.GiveWay, TargetCallsign = "OTHER" }));

        FlightPhysics.Update(ac, 1.0, s => lookup.GetValueOrDefault(s), null);

        Assert.True(ac.Queue.Blocks[0].IsApplied);
    }

    [Fact]
    public void GiveWay_NotMet_WhenClose_SameDirection_TargetBehind()
    {
        // Both heading 090. Target is due west (behind us) → bearingDiff = 270° → diffToTarget = 180° > 90° → NOT met.
        // GS=0 so aircraft does not move during the update, keeping the target within 0.1nm.
        var ac = MakeAircraft(lat: 37.7, lon: -122.2);
        ac.Heading = 090;
        ac.IndicatedAirspeed = 0;
        ac.IsOnGround = true;

        // Place target 0.05nm west: lon diff = 0.05 / (60 * cos(37.7°))
        double lonOffset = 0.05 / (60.0 * Math.Cos(37.7 * Math.PI / 180.0));
        var target = MakeAircraft(lat: 37.7, lon: -122.2 - lonOffset);
        target.Callsign = "OTHER";
        target.Heading = 090;
        target.IsOnGround = true;

        var lookup = new Dictionary<string, AircraftState> { ["OTHER"] = target };

        ac.Queue.Blocks.Add(TriggeredBlock(new BlockTrigger { Type = BlockTriggerType.GiveWay, TargetCallsign = "OTHER" }));

        FlightPhysics.Update(ac, 1.0, s => lookup.GetValueOrDefault(s), null);

        Assert.False(ac.Queue.Blocks[0].IsApplied);
    }

    [Fact]
    public void GiveWay_Met_WhenClose_SameDirection_TargetAhead()
    {
        // Both heading 090. Target is due east (ahead of us) → bearingDiff = 090° → diffToTarget = 0° < 90° → met.
        // GS=0 so aircraft does not move during the update, keeping the target within 0.1nm.
        var ac = MakeAircraft(lat: 37.7, lon: -122.2);
        ac.Heading = 090;
        ac.IndicatedAirspeed = 0;
        ac.IsOnGround = true;

        // Place target 0.05nm east
        double lonOffset = 0.05 / (60.0 * Math.Cos(37.7 * Math.PI / 180.0));
        var target = MakeAircraft(lat: 37.7, lon: -122.2 + lonOffset);
        target.Callsign = "OTHER";
        target.Heading = 090;
        target.IsOnGround = true;

        var lookup = new Dictionary<string, AircraftState> { ["OTHER"] = target };

        ac.Queue.Blocks.Add(TriggeredBlock(new BlockTrigger { Type = BlockTriggerType.GiveWay, TargetCallsign = "OTHER" }));

        FlightPhysics.Update(ac, 1.0, s => lookup.GetValueOrDefault(s), null);

        Assert.True(ac.Queue.Blocks[0].IsApplied);
    }

    [Fact]
    public void DistanceFinal_NotMet_WhenNoRunway()
    {
        // Phases is null → IsDistanceFinalMet returns false
        var ac = MakeAircraft();
        ac.Phases = null;
        ac.Queue.Blocks.Add(TriggeredBlock(new BlockTrigger { Type = BlockTriggerType.DistanceFinal, DistanceFinalNm = 5.0 }));

        FlightPhysics.Update(ac, 1.0, null, null);

        Assert.False(ac.Queue.Blocks[0].IsApplied);
    }

    // -------------------------------------------------------------------------
    // Completion Tests
    // -------------------------------------------------------------------------

    [Fact]
    public void ImmediateCommand_CompletesInstantly()
    {
        var ac = MakeAircraft();
        var cmd = new TrackedCommand { Type = TrackedCommandType.Immediate };
        ac.Queue.Blocks.Add(new CommandBlock { Commands = [cmd] });

        FlightPhysics.Update(ac, 1.0, null, null);

        Assert.True(cmd.IsComplete);
    }

    [Fact]
    public void HeadingCommand_Completes_WhenNoTargetHeading()
    {
        // Block is applied on tick 1; completion is evaluated on tick 2.
        var ac = MakeAircraft();
        ac.Targets.TargetHeading = null;
        var cmd = new TrackedCommand { Type = TrackedCommandType.Heading };
        ac.Queue.Blocks.Add(new CommandBlock { Commands = [cmd] });

        FlightPhysics.Update(ac, 1.0, null, null);
        FlightPhysics.Update(ac, 1.0, null, null);

        Assert.True(cmd.IsComplete);
    }

    [Fact]
    public void WaitSeconds_DecrementsByDelta()
    {
        // Tick 1: block applied (Wait starts). Ticks 2-4: completion evaluated each tick.
        // WaitRemainingSeconds: 3.0 → 2.0 → 1.0 → 0.0 (complete on tick 4).
        var ac = MakeAircraft();
        var cmd = new TrackedCommand { Type = TrackedCommandType.Wait };
        var block = new CommandBlock
        {
            Commands = [cmd],
            IsWaitBlock = true,
            WaitRemainingSeconds = 3.0,
        };
        ac.Queue.Blocks.Add(block);

        // Tick 1: apply block
        FlightPhysics.Update(ac, 1.0, null, null);
        Assert.False(cmd.IsComplete);

        // Tick 2: 3.0 - 1.0 = 2.0 → still incomplete
        FlightPhysics.Update(ac, 1.0, null, null);
        Assert.False(cmd.IsComplete);
        Assert.True(block.WaitRemainingSeconds > 0);

        // Tick 3: 2.0 - 1.0 = 1.0 → still incomplete
        FlightPhysics.Update(ac, 1.0, null, null);
        Assert.False(cmd.IsComplete);

        // Tick 4: 1.0 - 1.0 = 0.0 → complete
        FlightPhysics.Update(ac, 1.0, null, null);
        Assert.True(cmd.IsComplete);
    }

    [Fact]
    public void WaitDistance_DecrementsByGroundSpeed()
    {
        // On-ground aircraft: GS = 3600 kts → exactly 1.0 nm/sec (avoids floating-point accumulation).
        // WaitRemainingDistanceNm = 3.0. Tick 1: block applied.
        // Tick 2: 3.0 - 1.0 = 2.0 → incomplete. Tick 3: 1.0 → incomplete. Tick 4: 0.0 → complete.
        var ac = MakeAircraft();
        ac.IsOnGround = true;
        ac.IndicatedAirspeed = 3600;

        var cmd = new TrackedCommand { Type = TrackedCommandType.Wait };
        var block = new CommandBlock
        {
            Commands = [cmd],
            IsWaitBlock = true,
            WaitRemainingDistanceNm = 3.0,
        };
        ac.Queue.Blocks.Add(block);

        // Tick 1: apply block
        FlightPhysics.Update(ac, 1.0, null, null);
        Assert.False(cmd.IsComplete);

        // Tick 2: 3.0 - 1.0 = 2.0 → still incomplete
        FlightPhysics.Update(ac, 1.0, null, null);
        Assert.False(cmd.IsComplete);

        // Tick 3: 2.0 - 1.0 = 1.0 → still incomplete
        FlightPhysics.Update(ac, 1.0, null, null);
        Assert.False(cmd.IsComplete);

        // Tick 4: 1.0 - 1.0 = 0.0 → complete
        FlightPhysics.Update(ac, 1.0, null, null);
        Assert.True(cmd.IsComplete);
    }

    [Fact]
    public void MultiCommandBlock_RequiresAllComplete()
    {
        var ac = MakeAircraft();
        // Give the aircraft an active heading target so HeadingCommand stays incomplete
        ac.Targets.TargetHeading = 180.0;
        ac.Heading = 090;

        var immediateCmd = new TrackedCommand { Type = TrackedCommandType.Immediate };
        var headingCmd = new TrackedCommand { Type = TrackedCommandType.Heading };
        var block = new CommandBlock { Commands = [immediateCmd, headingCmd] };
        ac.Queue.Blocks.Add(block);

        FlightPhysics.Update(ac, 1.0, null, null);

        Assert.True(immediateCmd.IsComplete);
        Assert.False(headingCmd.IsComplete);
        Assert.False(block.AllComplete);
    }

    // -------------------------------------------------------------------------
    // Block Advancement
    // -------------------------------------------------------------------------

    [Fact]
    public void AdvancesToNextBlock_WhenAllComplete()
    {
        // Tick 1: block 0 applied (Immediate marked complete).
        // Tick 2: block 0 AllComplete → index advances → block 1 applied (Immediate marked complete).
        // Tick 3: block 1 AllComplete → index advances past end → queue complete.
        var ac = MakeAircraft();
        var firstBlock = ImmediateBlock();
        var secondBlock = ImmediateBlock();
        ac.Queue.Blocks.Add(firstBlock);
        ac.Queue.Blocks.Add(secondBlock);

        FlightPhysics.Update(ac, 1.0, null, null);
        Assert.True(firstBlock.IsApplied);
        Assert.False(secondBlock.IsApplied);

        FlightPhysics.Update(ac, 1.0, null, null);
        Assert.True(secondBlock.IsApplied);

        FlightPhysics.Update(ac, 1.0, null, null);
        Assert.Equal(2, ac.Queue.CurrentBlockIndex);
        Assert.True(ac.Queue.IsComplete);
    }

    [Fact]
    public void ApplyAction_InvokedOnTrigger()
    {
        var ac = MakeAircraft(heading: 090);
        var block = new CommandBlock { Commands = [new TrackedCommand { Type = TrackedCommandType.Immediate }], ApplyAction = a => a.Heading = 270 };
        ac.Queue.Blocks.Add(block);

        FlightPhysics.Update(ac, 1.0, null, null);

        Assert.Equal(270, ac.Heading);
        Assert.True(block.IsApplied);
    }
}
