using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;

namespace Yaat.Sim.Tests;

/// <summary>
/// Verifies that dispatching an unsupported or unrecognized command to an aircraft
/// with active pattern phases does NOT destroy those phases.
///
/// Bug: UnsupportedCommand had no case in ToCanonicalType, fell through to FlyHeading,
/// which triggered ClearsPhase in pattern phases. The aircraft lost its pattern state
/// even though the command was invalid.
/// </summary>
public class UnsupportedCommandPhaseTests
{
    private static RunwayInfo DefaultRunway() => TestRunwayFactory.Make(designator: "28R", heading: 280, elevationFt: 100);

    private static AircraftState MakeAircraftInUpwind()
    {
        var rwy = DefaultRunway();
        var ac = new AircraftState
        {
            Callsign = "N342T",
            AircraftType = "C172",
            Latitude = rwy.ThresholdLatitude,
            Longitude = rwy.ThresholdLongitude,
            TrueHeading = rwy.TrueHeading,
            Altitude = 800,
            IndicatedAirspeed = 90,
            IsOnGround = false,
            Departure = "OAK",
        };

        var waypoints = PatternGeometry.Compute(rwy, AircraftCategory.Piston, PatternDirection.Right, null, null, null);
        var phases = new PhaseList { AssignedRunway = rwy };
        phases.Add(new UpwindPhase { Waypoints = waypoints });
        phases.Add(new CrosswindPhase { Waypoints = waypoints });
        phases.Add(new DownwindPhase { Waypoints = waypoints });
        ac.Phases = phases;
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        return ac;
    }

    [Fact]
    public void UnsupportedCommand_DoesNotClearPatternPhases()
    {
        var ac = MakeAircraftInUpwind();
        Assert.NotNull(ac.Phases);
        Assert.IsType<UpwindPhase>(ac.Phases.CurrentPhase);

        // Dispatch an UnsupportedCommand (what "PS 0.5M" parses to)
        var unsupported = new UnsupportedCommand("PS 0.5M");
        var compound = new CompoundCommand([new ParsedBlock(null, [unsupported])]);
        var result = CommandDispatcher.DispatchCompound(compound, ac, null, new Random(42), false);

        Assert.False(result.Success);
        Assert.Contains("not yet supported", result.Message!);

        // Phases must still be intact
        Assert.NotNull(ac.Phases);
        Assert.IsType<UpwindPhase>(ac.Phases.CurrentPhase);
    }

    [Fact]
    public void ValidHeadingCommand_DoesClearPatternPhases()
    {
        var ac = MakeAircraftInUpwind();
        Assert.NotNull(ac.Phases);

        // A real heading command should clear phases (by design)
        var heading = new FlyHeadingCommand(new MagneticHeading(360));
        var compound = new CompoundCommand([new ParsedBlock(null, [heading])]);
        var result = CommandDispatcher.DispatchCompound(compound, ac, null, new Random(42), false);

        Assert.True(result.Success);
        Assert.Null(ac.Phases);
    }
}
