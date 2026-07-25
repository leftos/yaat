using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

/// <summary>
/// When a new command supersedes only part of a conditional block, the surviving half is rebuilt — and must carry the
/// original block's trigger runtime state with it.
///
/// <c>CreateBlock</c> returns a block with every runtime flag at its default, and the split path copied back only the
/// wait counters and the track guard. <c>TriggerMet</c> is a latch precisely because <c>IsTriggerMet</c> goes false
/// again once the aircraft flies past the fix, so a rebuilt block re-arms against a condition that has already
/// happened: it either never completes (pinning the queue behind it) or, if the trigger can still evaluate true,
/// applies its commands a second time.
/// </summary>
public sealed class SplitBlockTriggerStateTests
{
    public SplitBlockTriggerStateTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static AircraftState MakeAircraft()
    {
        return new AircraftState
        {
            Callsign = "AAL1",
            AircraftType = "B738",
            Position = new LatLon(37.62, -122.38),
            TrueHeading = new TrueHeading(280),
            Altitude = 7000,
            IndicatedAirspeed = 250,
            FlightPlan = new AircraftFlightPlan { Destination = "OAK" },
        };
    }

    private static void Dispatch(AircraftState aircraft, string command, DispatchContext ctx)
    {
        var parsed = CommandParser.ParseCompound(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);

        var result = CommandDispatcher.DispatchCompound(parsed.Value!, aircraft, ctx);
        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public void PartiallySupersededConditionalBlock_KeepsItsTriggerRuntimeState()
    {
        var aircraft = MakeAircraft();
        var ctx = TestDispatch.Context(Random.Shared);

        Dispatch(aircraft, "CM 10000; LV 5000 FH 270, SPD 210", ctx);

        // The conditional block carrying both FH 270 and SPD 210.
        var conditional = aircraft.Queue.Blocks.SingleOrDefault(b => b.Trigger is not null);
        Assert.NotNull(conditional);

        // Simulate the lookahead having already fired it as the aircraft climbed through 5,000 ft.
        conditional.IsApplied = true;
        conditional.TriggerMet = true;

        // A new speed assignment supersedes SPD 210 but not FH 270, so the block is partially split.
        Dispatch(aircraft, "SPD 250", ctx);

        var survivor = aircraft.Queue.Blocks.SingleOrDefault(b => b.Trigger is not null);
        Assert.NotNull(survivor);

        Assert.True(survivor.TriggerMet, "the surviving half re-armed against a trigger the aircraft has already passed");
        Assert.True(survivor.IsApplied, "the surviving half forgot it had already been applied");
    }
}
