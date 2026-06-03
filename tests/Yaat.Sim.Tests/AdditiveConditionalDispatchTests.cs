using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

/// <summary>
/// Issue #181: conditional commands (a block led by a precondition such as ONHO or AT)
/// are purely additive — they never clear sibling pending conditionals or deferred
/// dispatches. Only a fresh immediate command supersedes pending work, and a firing
/// deferral (PreserveConditionals) supersedes only conflicting *untriggered* work.
/// </summary>
public class AdditiveConditionalDispatchTests
{
    private static AircraftState MakeGroundAircraft() =>
        new()
        {
            Callsign = "N69WS",
            AircraftType = "C700",
            Position = new LatLon(30.190, -97.662),
            TrueHeading = new TrueHeading(350),
            Altitude = 542,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "KAUS" },
        };

    private static AircraftState MakeAirborneAircraft() =>
        new()
        {
            Callsign = "N69WS",
            AircraftType = "C700",
            Position = new LatLon(30.30, -97.70),
            TrueHeading = new TrueHeading(180),
            Altitude = 3000,
            IndicatedAirspeed = 220,
            IsOnGround = false,
        };

    private static CompoundCommand WaitThenTaxi(double seconds) =>
        new([new ParsedBlock(null, [new WaitCommand(seconds), new TaxiCommand(["N", "B"], [])])]);

    private static CompoundCommand OnHandoffClimb(int altitude) =>
        new([new ParsedBlock(new OnHandoffCondition(), [new ClimbMaintainCommand(altitude)])]);

    private static CompoundCommand AtAltitudeClimb(int triggerAltitude, int targetAltitude) =>
        new([new ParsedBlock(new LevelCondition(triggerAltitude), [new ClimbMaintainCommand(targetAltitude)])]);

    /// <summary>
    /// The exact issue-#181 spawn sequence: a WAIT-led taxi is deferred, then the ONHO and
    /// AT conditional presets are dispatched. The deferred taxi must survive both — before
    /// the fix, ONHO's DeferredDispatches.Clear() wiped it and the aircraft never taxied.
    /// </summary>
    [Fact]
    public void ConditionalPresets_DoNotClearDeferredTaxi()
    {
        var ac = MakeGroundAircraft();
        var ctx = TestDispatch.Context(new SerializableRandom(42), validateDctFixes: false);

        var taxiResult = CommandDispatcher.DispatchCompound(WaitThenTaxi(120), ac, ctx);
        Assert.True(taxiResult.Success, $"WAIT-taxi: {taxiResult.Message}");
        Assert.Single(ac.DeferredDispatches);

        var onhoResult = CommandDispatcher.DispatchCompound(OnHandoffClimb(12000), ac, ctx);
        Assert.True(onhoResult.Success, $"ONHO CM: {onhoResult.Message}");
        Assert.Single(ac.DeferredDispatches); // taxi survives ONHO
        Assert.Single(ac.Queue.Blocks); // ONHO conditional queued

        var atResult = CommandDispatcher.DispatchCompound(AtAltitudeClimb(6000, 16000), ac, ctx);
        Assert.True(atResult.Success, $"AT CM: {atResult.Message}");
        Assert.Single(ac.DeferredDispatches); // taxi still survives AT
        Assert.Equal(2, ac.Queue.Blocks.Count); // both conditionals queued

        // The surviving deferral is the taxi, not something else.
        Assert.Contains(ac.DeferredDispatches[0].Payload.Blocks.SelectMany(b => b.Commands), c => c is TaxiCommand);
        // Both queued blocks are precondition-gated (not applied immediately).
        Assert.All(ac.Queue.Blocks, b => Assert.NotNull(b.Trigger));
    }

    /// <summary>
    /// A fresh immediate command still supersedes pending conditionals (decision: only
    /// immediate commands supersede). A conditional vertical block is cleared by a fresh
    /// immediate vertical command.
    /// </summary>
    [Fact]
    public void ImmediateCommand_SupersedesPendingConditional()
    {
        var ac = MakeAirborneAircraft();
        var ctx = TestDispatch.Context(new SerializableRandom(42), validateDctFixes: false);

        CommandDispatcher.DispatchCompound(AtAltitudeClimb(6000, 16000), ac, ctx);
        Assert.Single(ac.Queue.Blocks);

        // Fresh immediate CM 5000 — same (vertical) dimension — supersedes the pending AT block.
        var cm = new CompoundCommand([new ParsedBlock(null, [new DescendMaintainCommand(5000)])]);
        var result = CommandDispatcher.DispatchCompound(cm, ac, ctx);
        Assert.True(result.Success, $"CM: {result.Message}");
        Assert.DoesNotContain(ac.Queue.Blocks, b => b.Trigger is { Type: BlockTriggerType.ReachAltitude });
    }

    /// <summary>
    /// A firing deferral (PreserveConditionals=true) executes an already-issued instruction,
    /// so its immediate payload must preserve pending triggered conditionals — even a
    /// same-dimension one — rather than superseding them like a fresh command would.
    /// </summary>
    [Fact]
    public void DeferredFiring_PreservesPendingConditional()
    {
        var ac = MakeAirborneAircraft();
        var liveCtx = TestDispatch.Context(new SerializableRandom(42), validateDctFixes: false);
        var firingCtx = TestDispatch.Context(new SerializableRandom(42), validateDctFixes: false, preserveConditionals: true);

        CommandDispatcher.DispatchCompound(AtAltitudeClimb(6000, 16000), ac, liveCtx);
        Assert.Single(ac.Queue.Blocks);

        // Same-dimension immediate payload, but dispatched as a firing deferral — the
        // pending AT conditional must survive.
        var payload = new CompoundCommand([new ParsedBlock(null, [new DescendMaintainCommand(5000)])]);
        var result = CommandDispatcher.DispatchCompound(payload, ac, firingCtx);
        Assert.True(result.Success, $"payload: {result.Message}");
        Assert.Contains(ac.Queue.Blocks, b => b.Trigger is { Type: BlockTriggerType.ReachAltitude });
    }
}
