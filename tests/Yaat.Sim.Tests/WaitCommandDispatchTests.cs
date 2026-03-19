using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;

namespace Yaat.Sim.Tests;

public class WaitCommandDispatchTests
{
    private static readonly ILogger Logger = NullLoggerFactory.Instance.CreateLogger("test");

    private static AircraftState MakeGroundAircraft()
    {
        return new AircraftState
        {
            Callsign = "SWA1391",
            AircraftType = "B738",
            Latitude = 37.620,
            Longitude = -122.380,
            TrueHeading = new TrueHeading(180),
            Altitude = 13,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            Departure = "KSFO",
        };
    }

    private static AircraftState MakeAirborneAircraft()
    {
        return new AircraftState
        {
            Callsign = "UAL123",
            AircraftType = "B738",
            Latitude = 37.7,
            Longitude = -122.2,
            TrueHeading = new TrueHeading(090),
            Altitude = 3000,
            IndicatedAirspeed = 200,
            IsOnGround = false,
        };
    }

    private static void StartPhase(AircraftState ac, Phase phase)
    {
        ac.Phases = new PhaseList();
        ac.Phases.Add(phase);
        var ctx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            DeltaSeconds = 1.0,
            Logger = Logger,
            Category = AircraftCategory.Jet,
        };
        ac.Phases.Start(ctx);
    }

    private static CompoundCommand WaitCompound(double seconds)
    {
        return new CompoundCommand([new ParsedBlock(null, [new WaitCommand(seconds)])]);
    }

    // -------------------------------------------------------------------------
    // ToCanonicalType mapping
    // -------------------------------------------------------------------------

    [Fact]
    public void ToCanonicalType_WaitCommand_ReturnsWait()
    {
        var result = CommandDescriber.ToCanonicalType(new WaitCommand(10));
        Assert.Equal(CanonicalCommandType.Wait, result);
    }

    [Fact]
    public void ToCanonicalType_WaitDistanceCommand_ReturnsWaitDistance()
    {
        var result = CommandDescriber.ToCanonicalType(new WaitDistanceCommand(5));
        Assert.Equal(CanonicalCommandType.WaitDistance, result);
    }

    // -------------------------------------------------------------------------
    // WAIT during ground phases
    // -------------------------------------------------------------------------

    [Fact]
    public void WaitCommand_Standalone_DuringPushback_IsRejected()
    {
        var ac = MakeGroundAircraft();
        StartPhase(ac, new PushbackPhase());

        // Standalone WAIT (no following commands) during pushback is rejected
        // because WAIT without a payload is meaningless
        var result = CommandDispatcher.DispatchCompound(WaitCompound(15), ac, null, new Random(42), true);

        Assert.False(result.Success);
    }

    [Fact]
    public void WaitCommand_Standalone_WithoutPhases_IsAccepted()
    {
        var ac = MakeGroundAircraft();
        // No active phases — standalone WAIT goes through normal queue path
        var result = CommandDispatcher.DispatchCompound(WaitCompound(10), ac, null, new Random(42), true);

        Assert.True(result.Success, $"Expected success but got: {result.Message}");
        Assert.Single(ac.Queue.Blocks);
    }

    // -------------------------------------------------------------------------
    // WAIT + TAXI compound (deferred dispatch)
    // -------------------------------------------------------------------------

    [Fact]
    public void WaitThenTaxi_DuringPushback_CreatesDeferredDispatch()
    {
        var ac = MakeGroundAircraft();
        StartPhase(ac, new PushbackPhase());

        // WAIT 15; TAXI A — two sequential blocks
        var compound = new CompoundCommand([new ParsedBlock(null, [new WaitCommand(15)]), new ParsedBlock(null, [new TaxiCommand(["A"], [])])]);

        var result = CommandDispatcher.DispatchCompound(compound, ac, null, new Random(42), true);

        Assert.True(result.Success, $"Expected success but got: {result.Message}");
        // Phases preserved — deferred dispatch doesn't touch them
        Assert.NotNull(ac.Phases);
        // Queue untouched — deferred dispatch doesn't use it
        Assert.Empty(ac.Queue.Blocks);
        // One deferred dispatch created
        Assert.Single(ac.DeferredDispatches);
        Assert.Equal(15, ac.DeferredDispatches[0].RemainingSeconds);
        Assert.Single(ac.DeferredDispatches[0].Payload.Blocks);
    }

    [Fact]
    public void WaitThenTaxi_WithoutPhases_CreatesDeferredDispatch()
    {
        var ac = MakeGroundAircraft();

        // No active phases — deferred dispatch still works
        var compound = new CompoundCommand([new ParsedBlock(null, [new WaitCommand(10)]), new ParsedBlock(null, [new TaxiCommand(["B"], [])])]);

        var result = CommandDispatcher.DispatchCompound(compound, ac, null, new Random(42), true);

        Assert.True(result.Success, $"Expected success but got: {result.Message}");
        Assert.Single(ac.DeferredDispatches);
        Assert.Equal(10, ac.DeferredDispatches[0].RemainingSeconds);
    }

    [Fact]
    public void WaitCommaFH_CreatesDeferredDispatch()
    {
        var ac = MakeAirborneAircraft();

        // "WAIT 10, FH 270" — single block with parallel WAIT + FH 270
        var compound = new CompoundCommand([new ParsedBlock(null, [new WaitCommand(10), new FlyHeadingCommand(new MagneticHeading(270))])]);

        var result = CommandDispatcher.DispatchCompound(compound, ac, null, new Random(42), true);

        Assert.True(result.Success, $"Expected success but got: {result.Message}");
        Assert.Single(ac.DeferredDispatches);
        Assert.Equal(10, ac.DeferredDispatches[0].RemainingSeconds);
        // Payload should contain just FH 270
        Assert.Single(ac.DeferredDispatches[0].Payload.Blocks);
        Assert.Single(ac.DeferredDispatches[0].Payload.Blocks[0].Commands);
        Assert.IsType<FlyHeadingCommand>(ac.DeferredDispatches[0].Payload.Blocks[0].Commands[0]);
    }

    [Fact]
    public void ChainedWaits_CreateSingleDeferredWithNestedPayload()
    {
        var ac = MakeGroundAircraft();

        // WAIT 5; WAIT 10; FH 270 — first WAIT defers [WAIT 10; FH 270]
        var compound = new CompoundCommand([
            new ParsedBlock(null, [new WaitCommand(5)]),
            new ParsedBlock(null, [new WaitCommand(10)]),
            new ParsedBlock(null, [new FlyHeadingCommand(new MagneticHeading(270))]),
        ]);

        var result = CommandDispatcher.DispatchCompound(compound, ac, null, new Random(42), true);

        Assert.True(result.Success);
        Assert.Single(ac.DeferredDispatches);
        Assert.Equal(5, ac.DeferredDispatches[0].RemainingSeconds);
        // Payload has 2 blocks: [WAIT 10, FH 270]
        Assert.Equal(2, ac.DeferredDispatches[0].Payload.Blocks.Count);
    }
}
