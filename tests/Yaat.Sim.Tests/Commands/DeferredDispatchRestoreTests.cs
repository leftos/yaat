using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

/// <summary>
/// A deferred dispatch must restore with the same payload it was created with.
///
/// <c>DeferredDispatch</c> stores the full original command text (gate included) and rebuilds its payload by
/// re-parsing that text on restore — so the restored payload carries the gate again. When it fires it re-enters the
/// deferral path instead of dispatching: a <c>WAIT</c> restarts its whole countdown, and a <c>BEHIND</c> whose target
/// has since been deleted is rejected outright, discarding the clearance. That makes a rewind or replay of a timeline
/// diverge from the live session it came from.
/// </summary>
public sealed class DeferredDispatchRestoreTests
{
    public DeferredDispatchRestoreTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static AircraftState MakeAirborneAircraft()
    {
        return new AircraftState
        {
            Callsign = "SWA100",
            AircraftType = "B738",
            Position = new LatLon(37.62, -122.38),
            TrueHeading = new TrueHeading(280),
            Altitude = 8000,
            IndicatedAirspeed = 250,
            FlightPlan = new AircraftFlightPlan { Destination = "OAK" },
        };
    }

    private static DeferredDispatch DispatchAndTakeDeferral(string command, AircraftState aircraft)
    {
        var parsed = CommandParser.ParseCompound(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);

        var result = CommandDispatcher.DispatchCompound(parsed.Value!, aircraft, TestDispatch.Context(Random.Shared));
        Assert.True(result.Success, result.Message);

        return Assert.Single(aircraft.DeferredDispatches);
    }

    [Fact]
    public void RestoredWaitDeferral_KeepsTheStrippedPayload_SoItDoesNotRestartItsCountdown()
    {
        var deferral = DispatchAndTakeDeferral("WAIT 120 FH 090", MakeAirborneAircraft());

        // Precondition: the live payload has the WAIT gate stripped off, so this cannot pass vacuously.
        Assert.DoesNotContain(deferral.Payload.Blocks.SelectMany(b => b.Commands), c => c is WaitCommand);

        var restored = DeferredDispatch.FromSnapshot(deferral.ToSnapshot());

        Assert.NotNull(restored);
        Assert.Equal(deferral.RemainingSeconds, restored.RemainingSeconds);
        Assert.DoesNotContain(restored.Payload.Blocks.SelectMany(b => b.Commands), c => c is WaitCommand);
    }

    [Fact]
    public void RestoredGiveWayDeferral_KeepsTheStrippedPayload_SoItDoesNotReenterTheGate()
    {
        const string SourceText = "BEHIND KLM605 TAXI A B";

        var parsed = CommandParser.ParseCompound(SourceText);
        Assert.True(parsed.IsSuccess, parsed.Reason);

        // Precondition: the stored text really does carry the gate, which is what restore has to strip.
        Assert.IsType<GiveWayCondition>(parsed.Value!.Blocks[0].Condition);

        // Mirror TryDeferGiveWay's construction — the payload has the condition stripped off its first block while
        // the deferral retains the full original text. Built directly rather than dispatched because the give-way
        // admission rules (ground, taxi state, resolvable target) are not what this test is about.
        var payload = new CompoundCommand([new ParsedBlock(null, parsed.Value.Blocks[0].Commands)]) { SourceText = SourceText };
        var deferral = new DeferredDispatch(payload, "KLM605") { SourceText = SourceText };

        Assert.IsNotType<GiveWayCondition>(deferral.Payload.Blocks[0].Condition);

        var restored = DeferredDispatch.FromSnapshot(deferral.ToSnapshot());

        Assert.NotNull(restored);
        Assert.Equal(deferral.GiveWayTarget, restored.GiveWayTarget);
        Assert.IsNotType<GiveWayCondition>(restored.Payload.Blocks[0].Condition);
    }

    /// <summary>
    /// The command-run reaction delay stores the whole compound as its payload with no gate in the text, so restore
    /// must leave it intact — stripping unconditionally would eat a real command.
    /// </summary>
    [Fact]
    public void RestoredReactionDelay_KeepsItsWholePayload()
    {
        var aircraft = MakeAirborneAircraft();
        var parsed = CommandParser.ParseCompound("FH 090");
        Assert.True(parsed.IsSuccess, parsed.Reason);

        var deferral = new DeferredDispatch(5.0, parsed.Value!) { SourceText = "FH 090", IsReactionDelay = true };
        aircraft.DeferredDispatches.Add(deferral);

        var restored = DeferredDispatch.FromSnapshot(deferral.ToSnapshot());

        Assert.NotNull(restored);
        Assert.True(restored.IsReactionDelay);
        Assert.Single(restored.Payload.Blocks);
        Assert.NotEmpty(restored.Payload.Blocks[0].Commands);
    }
}
