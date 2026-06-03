using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

/// <summary>
/// The unified conditional list spans pending queue trigger blocks AND deferred dispatches
/// (WAIT/WAITD/BEHIND), numbers them together, excludes internal reaction-delay deferrals,
/// and deletes either kind by that number — backing SHOWAT/SHOWCOND, the "Pending Cmds"
/// column, and DELAT/DELCOND.
/// </summary>
public class ConditionalListTests
{
    private static AircraftState MakeGroundAircraft() =>
        new()
        {
            Callsign = "N69WS",
            AircraftType = "C700",
            Position = new LatLon(30.190, -97.662),
            TrueHeading = new TrueHeading(350),
            Altitude = 542,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "KAUS" },
        };

    private static AircraftState BuildAircraftWithMixedConditionals()
    {
        var ac = MakeGroundAircraft();
        var ctx = TestDispatch.Context(new SerializableRandom(42), validateDctFixes: false);

        // Deferred WAIT-led taxi.
        CommandDispatcher.DispatchCompound(
            new CompoundCommand([new ParsedBlock(null, [new WaitCommand(120), new TaxiCommand(["N", "B"], [])])]),
            ac,
            ctx
        );
        // Precondition-gated queue block (additive — does not clear the deferral).
        CommandDispatcher.DispatchCompound(
            new CompoundCommand([new ParsedBlock(new OnHandoffCondition(), [new ClimbMaintainCommand(12000)])]),
            ac,
            ctx
        );

        // Internal reaction-delay deferral — must be hidden from the conditional list.
        ac.DeferredDispatches.Add(
            new DeferredDispatch(3, new CompoundCommand([new ParsedBlock(null, [new FlyHeadingCommand(new MagneticHeading(270))])]))
            {
                IsReactionDelay = true,
            }
        );

        return ac;
    }

    [Fact]
    public void Enumerate_SpansQueueBlocksAndDeferrals_ExcludesReactionDelay()
    {
        var ac = BuildAircraftWithMixedConditionals();

        var entries = ConditionalList.Enumerate(ac, liveCountdown: true);

        // ONHO queue block (#1) + WAIT-taxi deferral (#2); reaction-delay excluded.
        Assert.Equal(2, entries.Count);
        Assert.Equal(ConditionalEntryKind.QueueBlock, entries[0].Kind);
        Assert.Equal(1, entries[0].Number);
        Assert.Equal(ConditionalEntryKind.Deferred, entries[1].Kind);
        Assert.Equal(2, entries[1].Number);
        Assert.Contains("taxi", entries[1].Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DescribeDeferred_StableVsLive()
    {
        var d = new DeferredDispatch(90, new CompoundCommand([new ParsedBlock(null, [new ClimbMaintainCommand(5000)])]));
        Assert.StartsWith("in ", ConditionalList.DescribeDeferred(d, liveCountdown: true));
        Assert.StartsWith("waiting:", ConditionalList.DescribeDeferred(d, liveCountdown: false));
    }

    [Fact]
    public void Delete_ByIndex_RemovesDeferral_KeepsQueueBlockAndReactionDelay()
    {
        var ac = BuildAircraftWithMixedConditionals();

        var result = ConditionalList.Delete(ac, 2); // the WAIT-taxi deferral

        Assert.True(result.Success);
        Assert.Equal(1, result.DeletedCount);
        Assert.Single(ac.Queue.Blocks); // ONHO block stays
        Assert.Single(ac.DeferredDispatches); // only the reaction-delay remains
        Assert.True(ac.DeferredDispatches[0].IsReactionDelay);
    }

    [Fact]
    public void Delete_ByIndex_RemovesQueueBlock_KeepsDeferral()
    {
        var ac = BuildAircraftWithMixedConditionals();

        var result = ConditionalList.Delete(ac, 1); // the ONHO queue block

        Assert.True(result.Success);
        Assert.Empty(ac.Queue.Blocks);
        // WAIT-taxi deferral + reaction-delay both remain.
        Assert.Equal(2, ac.DeferredDispatches.Count);
        Assert.Contains(ac.DeferredDispatches, d => !d.IsReactionDelay);
    }

    [Fact]
    public void Delete_All_RemovesQueueBlocksAndDeferrals_KeepsReactionDelay()
    {
        var ac = BuildAircraftWithMixedConditionals();

        var result = ConditionalList.Delete(ac, null);

        Assert.True(result.Success);
        Assert.Equal(2, result.DeletedCount); // ONHO block + WAIT-taxi deferral
        Assert.Empty(ac.Queue.Blocks);
        Assert.Single(ac.DeferredDispatches); // reaction-delay survives
        Assert.True(ac.DeferredDispatches[0].IsReactionDelay);
    }

    [Fact]
    public void Delete_OutOfRange_ReportsDeletableCount()
    {
        var ac = BuildAircraftWithMixedConditionals();

        var result = ConditionalList.Delete(ac, 9);

        Assert.False(result.Success);
        Assert.Equal(2, result.DeletableCount);
    }
}
