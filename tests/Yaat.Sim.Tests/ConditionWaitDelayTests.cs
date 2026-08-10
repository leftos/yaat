using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

/// <summary>
/// Issue #286: a <c>&lt;condition&gt; WAIT n &lt;cmd&gt;</c> compound must hold the payload for <c>n</c>
/// seconds after the condition fires. The parser merges the leading WAIT and its payload into one
/// conditioned block, and the queue counts the wait down after the trigger fires before applying.
/// </summary>
public class ConditionWaitDelayTests
{
    public ConditionWaitDelayTests()
    {
        // Pin navdata for the CFIX/AT-fix parser test and to avoid static-singleton races.
        TestVnasData.EnsureInitialized();
    }

    private static AircraftState MakeAircraft() =>
        new()
        {
            Callsign = "TST286",
            AircraftType = "B738",
            Position = new LatLon(37.7, -122.2),
            TrueHeading = new TrueHeading(090),
            Altitude = 10_000,
            IndicatedAirspeed = 250,
            IsOnGround = false,
        };

    // -------------------------------------------------------------------------
    // Parser: merge a leading WAIT into its conditioned block
    // -------------------------------------------------------------------------

    [Fact]
    public void ConditionThenWait_MergesIntoSingleBlock()
    {
        var result = CommandParser.ParseCompound("LV 050 WAIT 30 DM 110");

        Assert.NotNull(result.Value);
        var block = Assert.Single(result.Value!.Blocks);
        Assert.IsType<LevelCondition>(block.Condition);
        Assert.Equal(2, block.Commands.Count);
        Assert.Equal(30.0, Assert.IsType<WaitCommand>(block.Commands[0]).Seconds);
        Assert.IsType<DescendMaintainCommand>(block.Commands[1]);
    }

    [Fact]
    public void ChainedWaitsAfterCondition_MergeAndPreserveOrder()
    {
        var result = CommandParser.ParseCompound("LV 050 WAIT 5 WAIT 10 DM 110");

        Assert.NotNull(result.Value);
        var block = Assert.Single(result.Value!.Blocks);
        Assert.IsType<LevelCondition>(block.Condition);
        Assert.Collection(
            block.Commands,
            c => Assert.Equal(5.0, Assert.IsType<WaitCommand>(c).Seconds),
            c => Assert.Equal(10.0, Assert.IsType<WaitCommand>(c).Seconds),
            c => Assert.IsType<DescendMaintainCommand>(c)
        );
    }

    [Fact]
    public void CfixThenAtWait_KeepsWaitPayloadInOneBlock_NotReinjected()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var result = CommandParser.ParseCompound("CFIX TTE 140; AT TTE WAIT 30 DM 110");

        Assert.NotNull(result.Value);
        var blocks = result.Value!.Blocks;

        // Exactly two blocks: the CFIX, then the merged AT-TTE wait+descend. The DM must NOT become a
        // third, independently AT-injected block (the #286 regression).
        Assert.Equal(2, blocks.Count);
        Assert.Null(blocks[0].Condition);
        Assert.IsType<CrossFixCommand>(blocks[0].Commands[0]);

        var waitBlock = blocks[1];
        Assert.IsType<AtFixCondition>(waitBlock.Condition);
        Assert.Equal(2, waitBlock.Commands.Count);
        Assert.IsType<WaitCommand>(waitBlock.Commands[0]);
        Assert.IsType<DescendMaintainCommand>(waitBlock.Commands[1]);
    }

    [Fact]
    public void CfixWaitRoundTrip_ClientCanonicalToServer_HoldsDescentBehindWait()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        // What a controller typing this produces client-side (scheme parser → canonical string), then
        // what the server rebuilds from that canonical. The canonicalizer must keep the WAIT and its
        // descent payload in one conditioned `AT TTE` block so the queue holds the descent behind the
        // wait's countdown (issue #335 — the pre-split form orphaned the payload).
        var canonical = CommandSchemeParser.ParseCompound("CFIX TTE 140; AT TTE WAIT 30 DM 110; RNS", CommandScheme.Default());
        Assert.NotNull(canonical);

        var parsed = CommandParser.ParseCompound(canonical!.CanonicalString);
        Assert.True(parsed.IsSuccess);
        var blocks = parsed.Value!.Blocks;

        int waitIdx = blocks.FindIndex(b => b.Commands.Exists(c => c is WaitCommand));
        int descendIdx = blocks.FindIndex(b => b.Commands.Exists(c => c is DescendMaintainCommand));

        Assert.True(waitIdx >= 0, $"no wait block in: {canonical.CanonicalString}");
        Assert.True(descendIdx >= 0, $"no descend block in: {canonical.CanonicalString}");
        Assert.Equal(waitIdx, descendIdx);
        Assert.IsType<AtFixCondition>(blocks[waitIdx].Condition);
    }

    // -------------------------------------------------------------------------
    // Issue #335: the client canonicalizer must not split a condition-led WAIT
    // compound into a top-level unconditioned payload block
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("LV 050 WAIT 5 DM 110", "LV 050 WAIT 5 DM 110")]
    [InlineData("ATFN 5 WAIT 10 CM 2000", "ATFN 5 WAIT 10 CM 2000")]
    [InlineData("AT 060 WAIT 5 SPD 210", "AT 060 WAIT 5 SPD 210")]
    [InlineData("LV 050 WAIT 5 WAIT 10 DM 110", "LV 050 WAIT 5 WAIT 10 DM 110")]
    public void ClientCanonical_ConditionWait_StaysOneBlock(string typed, string expectedCanonical)
    {
        var result = CommandSchemeParser.ParseCompound(typed, CommandScheme.Default());

        Assert.NotNull(result);
        Assert.Equal(expectedCanonical, result!.CanonicalString);
    }

    [Fact]
    public void ClientCanonical_AtFixWait_StaysOneBlock_AndServerMergesIt()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var canonical = CommandSchemeParser.ParseCompound("AT TTE WAIT 170 DM 110", CommandScheme.Default());

        Assert.NotNull(canonical);
        Assert.Equal("AT TTE WAIT 170 DM 110", canonical!.CanonicalString);

        var parsed = CommandParser.ParseCompound(canonical.CanonicalString);
        Assert.True(parsed.IsSuccess);
        var block = Assert.Single(parsed.Value!.Blocks);
        Assert.IsType<AtFixCondition>(block.Condition);
        Assert.Equal(2, block.Commands.Count);
        Assert.Equal(170.0, Assert.IsType<WaitCommand>(block.Commands[0]).Seconds);
        Assert.IsType<DescendMaintainCommand>(block.Commands[1]);
    }

    [Fact]
    public void ClientCanonical_ChainedWaits_RoundTripToOneConditionedBlock()
    {
        var canonical = CommandSchemeParser.ParseCompound("LV 050 WAIT 5 WAIT 10 DM 110", CommandScheme.Default());
        Assert.NotNull(canonical);

        var parsed = CommandParser.ParseCompound(canonical!.CanonicalString);
        Assert.True(parsed.IsSuccess);
        var block = Assert.Single(parsed.Value!.Blocks);
        Assert.IsType<LevelCondition>(block.Condition);
        Assert.Collection(
            block.Commands,
            c => Assert.Equal(5.0, Assert.IsType<WaitCommand>(c).Seconds),
            c => Assert.Equal(10.0, Assert.IsType<WaitCommand>(c).Seconds),
            c => Assert.IsType<DescendMaintainCommand>(c)
        );
    }

    [Fact]
    public void ClientCanonical_ConditionWait_IsIdempotent()
    {
        var first = CommandSchemeParser.ParseCompound("LV 050 WAIT 5 DM 110", CommandScheme.Default());
        Assert.NotNull(first);

        var second = CommandSchemeParser.ParseCompound(first!.CanonicalString, CommandScheme.Default());
        Assert.NotNull(second);
        Assert.Equal(first.CanonicalString, second!.CanonicalString);
    }

    [Fact]
    public void ClientCanonical_ConditionWaitThenInnerCondition_KeepsInnerConditionSeparate()
    {
        // A payload sub-block that introduces its own condition must stay a standalone block —
        // only the unconditioned payload merges into the wait's block.
        var canonical = CommandSchemeParser.ParseCompound("LV 050 WAIT 5 AT TTE DM 110", CommandScheme.Default());
        Assert.NotNull(canonical);
        Assert.Equal("LV 050 WAIT 5; AT TTE DM 110", canonical!.CanonicalString);
    }

    [Fact]
    public void ClientCanonical_UnconditionedWait_StillSplits()
    {
        // Without a leading condition the top-level ExpandWait split is the correct canonical form.
        var result = CommandSchemeParser.ParseCompound("WAIT 5 DM 110", CommandScheme.Default());

        Assert.NotNull(result);
        Assert.Equal("WAIT 5; DM 110", result!.CanonicalString);
    }

    // -------------------------------------------------------------------------
    // CreateBlock: sum leading waits when building the queue block
    // -------------------------------------------------------------------------

    [Fact]
    public void Dispatch_ConditionWithChainedWaits_SetsSummedWaitRemainingSeconds()
    {
        var ac = MakeAircraft();
        var parsed = CommandParser.ParseCompound("LV 050 WAIT 5 WAIT 10 DM 110");
        Assert.NotNull(parsed.Value);

        var result = CommandDispatcher.DispatchCompound(parsed.Value!, ac, TestDispatch.Context(new SerializableRandom(42)));

        Assert.True(result.Success, result.Message);
        var block = Assert.Single(ac.Queue.Blocks);
        Assert.True(block.IsWaitBlock);
        Assert.Equal(15, block.WaitRemainingSeconds);
    }

    // -------------------------------------------------------------------------
    // Execution: count the wait down after the trigger fires, before applying
    // -------------------------------------------------------------------------

    [Fact]
    public void TriggeredWaitBlock_HoldsPayloadUntilCountdownElapses()
    {
        var ac = MakeAircraft();
        bool applied = false;
        var block = new CommandBlock
        {
            Trigger = new BlockTrigger
            {
                Type = BlockTriggerType.ReachFix,
                FixLat = ac.Position.Lat,
                FixLon = ac.Position.Lon,
            },
            Commands = [new TrackedCommand { Type = TrackedCommandType.Immediate }],
            IsWaitBlock = true,
            WaitRemainingSeconds = 30,
            ApplyAction = _ =>
            {
                applied = true;
                return new CommandResult(true);
            },
        };
        ac.Queue.Blocks.Add(block);

        // 10 s in: the trigger is met but the 30 s wait has not elapsed — payload must not fire.
        for (int i = 0; i < 10; i++)
        {
            FlightPhysics.Update(ac, 1.0);
        }

        Assert.False(applied, "Payload fired before the WAIT elapsed");
        Assert.False(block.IsApplied);

        // Past 30 s total — payload fires.
        for (int i = 0; i < 25; i++)
        {
            FlightPhysics.Update(ac, 1.0);
        }

        Assert.True(applied, "Payload never fired after the WAIT elapsed");
        Assert.True(block.IsApplied);
    }

    [Fact]
    public void LookaheadWaitBlock_HoldsPayload_AndSequencesLaterBlockAfterWait()
    {
        var ac = MakeAircraft();

        // A perpetually-incomplete Navigation block keeps the queue pinned at index 0 (mirrors the CFIX
        // arrival case), so the wait block and the trailing block only fire via the lookahead scan.
        ac.Targets.NavigationRoute.Add(new NavigationTarget { Name = "FARAWAY", Position = new LatLon(41.0, -120.0) });
        var navBlock = new CommandBlock { Commands = [new TrackedCommand { Type = TrackedCommandType.Navigation }], IsApplied = true };

        var trigger = new BlockTrigger
        {
            Type = BlockTriggerType.ReachFix,
            FixLat = ac.Position.Lat,
            FixLon = ac.Position.Lon,
        };
        bool waitApplied = false;
        bool laterApplied = false;
        var waitBlock = new CommandBlock
        {
            Trigger = trigger,
            Commands = [new TrackedCommand { Type = TrackedCommandType.Immediate }],
            IsWaitBlock = true,
            WaitRemainingSeconds = 30,
            ApplyAction = _ =>
            {
                waitApplied = true;
                return new CommandResult(true);
            },
        };

        // A trailing `;`-sequential block sharing the trigger (like the injected `AT fix RNS`). It must
        // run AFTER the wait block's payload, not in parallel with it.
        var laterBlock = new CommandBlock
        {
            Trigger = new BlockTrigger
            {
                Type = BlockTriggerType.ReachFix,
                FixLat = ac.Position.Lat,
                FixLon = ac.Position.Lon,
            },
            Commands = [new TrackedCommand { Type = TrackedCommandType.Immediate }],
            ApplyAction = _ =>
            {
                laterApplied = true;
                return new CommandResult(true);
            },
        };
        ac.Queue.Blocks.Add(navBlock);
        ac.Queue.Blocks.Add(waitBlock);
        ac.Queue.Blocks.Add(laterBlock);

        // 10 s in: the wait is still counting down. Neither the wait payload nor the later sequential
        // block may have fired yet (the later block is held behind the unexpired wait).
        for (int i = 0; i < 10; i++)
        {
            FlightPhysics.Update(ac, 1.0);
        }

        Assert.False(waitApplied, "WAIT payload fired before its delay elapsed");
        Assert.False(laterApplied, "Later `;`-sequential block fired before the WAIT completed");

        // Past 30 s total — the wait payload fires, then the later block (which never orphaned even
        // though the aircraft has flown well past the trigger fix during the wait).
        for (int i = 0; i < 25; i++)
        {
            FlightPhysics.Update(ac, 1.0);
        }

        Assert.True(waitApplied, "WAIT payload never fired");
        Assert.True(laterApplied, "Later block never fired after the WAIT completed");
    }

    [Fact]
    public void CurrentBlockWait_HoldsPayload_AndSequencesLaterBlockAfterWait()
    {
        var ac = MakeAircraft();

        // Same as the lookahead case but WITHOUT a leading perpetual Navigation block, so the wait
        // block is the queue's *current* (advancing) block (index 0) rather than reached via lookahead.
        // This is the non-CFIX shape `AT FIX WAIT 30 CMD1; AT FIX CMD2` given to a vectored aircraft
        // whose FIX trigger fires by proximity (IsTriggerMet), not by route-sequencing.
        var trigger = new BlockTrigger
        {
            Type = BlockTriggerType.ReachFix,
            FixLat = ac.Position.Lat,
            FixLon = ac.Position.Lon,
        };
        bool waitApplied = false;
        bool laterApplied = false;
        var waitBlock = new CommandBlock
        {
            Trigger = trigger,
            Commands = [new TrackedCommand { Type = TrackedCommandType.Immediate }],
            IsWaitBlock = true,
            WaitRemainingSeconds = 30,
            ApplyAction = _ =>
            {
                waitApplied = true;
                return new CommandResult(true);
            },
        };
        var laterBlock = new CommandBlock
        {
            Trigger = new BlockTrigger
            {
                Type = BlockTriggerType.ReachFix,
                FixLat = ac.Position.Lat,
                FixLon = ac.Position.Lon,
            },
            Commands = [new TrackedCommand { Type = TrackedCommandType.Immediate }],
            ApplyAction = _ =>
            {
                laterApplied = true;
                return new CommandResult(true);
            },
        };
        ac.Queue.Blocks.Add(waitBlock);
        ac.Queue.Blocks.Add(laterBlock);

        // 10 s in: the wait is still counting down. Neither may have fired.
        for (int i = 0; i < 10; i++)
        {
            FlightPhysics.Update(ac, 1.0);
        }

        Assert.False(waitApplied, "WAIT payload fired before its delay elapsed");
        Assert.False(laterApplied, "Later `;`-sequential block fired before the WAIT completed");

        // Past 30 s total — the wait payload fires, then the later block, which must NOT orphan even
        // though the aircraft has flown well past the trigger fix during the wait countdown.
        for (int i = 0; i < 25; i++)
        {
            FlightPhysics.Update(ac, 1.0);
        }

        Assert.True(waitApplied, "WAIT payload never fired");
        Assert.True(laterApplied, "Later block orphaned: never fired after the WAIT completed (aircraft passed the fix during the countdown)");
    }
}
