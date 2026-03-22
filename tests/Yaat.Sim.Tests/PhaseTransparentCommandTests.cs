using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;

namespace Yaat.Sim.Tests;

/// <summary>
/// Verifies that phase-transparent commands (squawk, ident, say, etc.) do NOT
/// destroy active phases or clear the command queue when dispatched.
///
/// Bug: Phase.CanAcceptCommand defaults to ClearsPhase for any unrecognized command
/// type. Squawk/ident/say commands aren't handled by any phase, so they trigger
/// phase destruction even though they only modify transponder/metadata state.
/// </summary>
public class PhaseTransparentCommandTests
{
    private static RunwayInfo DefaultRunway() => TestRunwayFactory.Make(designator: "28R", heading: 280, elevationFt: 100);

    private static AircraftState MakeAircraftInUpwind()
    {
        var rwy = DefaultRunway();
        var ac = new AircraftState
        {
            Callsign = "N569SX",
            AircraftType = "C172",
            Latitude = rwy.ThresholdLatitude,
            Longitude = rwy.ThresholdLongitude,
            TrueHeading = rwy.TrueHeading,
            Altitude = 800,
            IndicatedAirspeed = 90,
            IsOnGround = false,
            Departure = "OAK",
            AssignedBeaconCode = 7110,
            BeaconCode = 7110,
        };

        var waypoints = PatternGeometry.Compute(rwy, AircraftCategory.Piston, PatternDirection.Right, null);
        var phases = new PhaseList { AssignedRunway = rwy };
        phases.Add(new UpwindPhase { Waypoints = waypoints });
        phases.Add(new CrosswindPhase { Waypoints = waypoints });
        phases.Add(new DownwindPhase { Waypoints = waypoints });
        ac.Phases = phases;
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        return ac;
    }

    private static CommandResult DispatchSingle(AircraftState ac, ParsedCommand cmd)
    {
        var compound = new CompoundCommand([new ParsedBlock(null, [cmd])]);
        return CommandDispatcher.DispatchCompound(compound, ac, null, new Random(42), false);
    }

    [Fact]
    public void SquawkDuringPhase_PreservesPhases()
    {
        var ac = MakeAircraftInUpwind();
        Assert.NotNull(ac.Phases);
        Assert.IsType<UpwindPhase>(ac.Phases.CurrentPhase);

        var result = DispatchSingle(ac, new SquawkCommand(1234u));

        Assert.True(result.Success);
        Assert.Equal(1234u, ac.BeaconCode);
        Assert.NotNull(ac.Phases);
        Assert.IsType<UpwindPhase>(ac.Phases.CurrentPhase);
    }

    [Fact]
    public void SquawkPreservesCommandQueue()
    {
        var ac = MakeAircraftInUpwind();

        // Pre-populate queue with a block
        var speedCmd = new SpeedCommand(120);
        var block = new CommandBlock
        {
            Commands = [new TrackedCommand { Type = TrackedCommandType.Speed }],
            Description = "Speed 120",
            NaturalDescription = "Speed 120",
            ApplyAction = (a) => FlightCommandHandler.ApplySpeed(speedCmd, a),
        };
        ac.Queue.Blocks.Add(block);
        Assert.Single(ac.Queue.Blocks);

        var result = DispatchSingle(ac, new SquawkCommand(4567u));

        Assert.True(result.Success);
        Assert.Equal(4567u, ac.BeaconCode);
        Assert.Single(ac.Queue.Blocks);
    }

    [Fact]
    public void IdentDuringPhase_PreservesPhases()
    {
        var ac = MakeAircraftInUpwind();

        var result = DispatchSingle(ac, new IdentCommand());

        Assert.True(result.Success);
        Assert.True(ac.IsIdenting);
        Assert.NotNull(ac.Phases);
        Assert.IsType<UpwindPhase>(ac.Phases.CurrentPhase);
    }

    [Fact]
    public void SquawkVfrDuringPhase_PreservesPhases()
    {
        var ac = MakeAircraftInUpwind();

        var result = DispatchSingle(ac, new SquawkVfrCommand());

        Assert.True(result.Success);
        Assert.Equal(1200u, ac.BeaconCode);
        Assert.NotNull(ac.Phases);
        Assert.IsType<UpwindPhase>(ac.Phases.CurrentPhase);
    }

    [Fact]
    public void RandomSquawkDuringPhase_PreservesPhases()
    {
        var ac = MakeAircraftInUpwind();

        var result = DispatchSingle(ac, new RandomSquawkCommand());

        Assert.True(result.Success);
        Assert.NotNull(ac.Phases);
        Assert.IsType<UpwindPhase>(ac.Phases.CurrentPhase);
    }

    [Fact]
    public void MixedCompound_SquawkThenHeading_ClearsPhases()
    {
        var ac = MakeAircraftInUpwind();

        // SQ 1234; FH 360 — heading should clear phases as normal
        var compound = new CompoundCommand([
            new ParsedBlock(null, [new SquawkCommand(1234u)]),
            new ParsedBlock(null, [new FlyHeadingCommand(new MagneticHeading(360))]),
        ]);
        var result = CommandDispatcher.DispatchCompound(compound, ac, null, new Random(42), false);

        Assert.True(result.Success);
        Assert.Equal(1234u, ac.BeaconCode);
        Assert.Null(ac.Phases);
    }

    [Fact]
    public void AllTransparentCompound_PreservesPhases()
    {
        var ac = MakeAircraftInUpwind();

        // SQ 1234, ID — both transparent, phases should survive
        var compound = new CompoundCommand([new ParsedBlock(null, [new SquawkCommand(1234u), new IdentCommand()])]);
        var result = CommandDispatcher.DispatchCompound(compound, ac, null, new Random(42), false);

        Assert.True(result.Success);
        Assert.Equal(1234u, ac.BeaconCode);
        Assert.True(ac.IsIdenting);
        Assert.NotNull(ac.Phases);
        Assert.IsType<UpwindPhase>(ac.Phases.CurrentPhase);
    }

    [Fact]
    public void TransparentWithoutPhases_WorksNormally()
    {
        var ac = MakeAircraftInUpwind();
        ac.Phases = null; // no phases

        var result = DispatchSingle(ac, new SquawkCommand(5678));

        Assert.True(result.Success);
        Assert.Equal(5678u, ac.BeaconCode);
        Assert.Null(ac.Phases);
    }
}
