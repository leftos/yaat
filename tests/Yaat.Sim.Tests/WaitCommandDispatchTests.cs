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
            Heading = 180,
            Altitude = 13,
            GroundSpeed = 0,
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
            Heading = 090,
            Altitude = 3000,
            GroundSpeed = 200,
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
    public void WaitCommand_DuringPushback_IsAccepted()
    {
        var ac = MakeGroundAircraft();
        StartPhase(ac, new PushbackPhase());

        var result = CommandDispatcher.DispatchCompound(WaitCompound(15), ac, null, null, null, Logger, new Random(42));

        Assert.True(result.Success, $"Expected success but got: {result.Message}");
        Assert.Null(ac.Phases);
        Assert.Single(ac.Queue.Blocks);
    }

    [Fact]
    public void WaitCommand_DuringTaxi_IsAccepted()
    {
        var ac = MakeGroundAircraft();
        StartPhase(ac, new TaxiingPhase());

        var result = CommandDispatcher.DispatchCompound(WaitCompound(10), ac, null, null, null, Logger, new Random(42));

        Assert.True(result.Success, $"Expected success but got: {result.Message}");
        Assert.Null(ac.Phases);
    }

    // -------------------------------------------------------------------------
    // WAIT during airborne phases
    // -------------------------------------------------------------------------

    [Fact]
    public void WaitCommand_DuringHoldingShort_IsAccepted()
    {
        var ac = MakeGroundAircraft();
        StartPhase(ac, new HoldingAfterPushbackPhase());

        var result = CommandDispatcher.DispatchCompound(WaitCompound(5), ac, null, null, null, Logger, new Random(42));

        Assert.True(result.Success, $"Expected success but got: {result.Message}");
        Assert.Null(ac.Phases);
    }

    // -------------------------------------------------------------------------
    // WAIT + TAXI compound (the original bug report scenario)
    // -------------------------------------------------------------------------

    [Fact]
    public void WaitThenTaxi_DuringPushback_IsAccepted()
    {
        var ac = MakeGroundAircraft();
        StartPhase(ac, new PushbackPhase());

        // WAIT 15; TAXI A — two sequential blocks
        var compound = new CompoundCommand([new ParsedBlock(null, [new WaitCommand(15)]), new ParsedBlock(null, [new TaxiCommand(["A"], [])])]);

        var result = CommandDispatcher.DispatchCompound(compound, ac, null, null, null, Logger, new Random(42));

        Assert.True(result.Success, $"Expected success but got: {result.Message}");
        Assert.Null(ac.Phases);
        // Should have 2 blocks in queue: wait + taxi
        Assert.Equal(2, ac.Queue.Blocks.Count);
    }
}
