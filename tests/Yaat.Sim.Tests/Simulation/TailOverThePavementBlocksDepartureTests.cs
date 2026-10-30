using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;
using static Yaat.Sim.Tests.Simulation.RejectedTakeoffTestRig;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// YAAT-825 reaches the departure: an aircraft stopped on a high-speed exit with its tail still over the runway's strict paved
/// half-width, far from any bar, is on the runway (AIM 4-3-21.b), so a departure rolling toward it treats it as an occupant
/// and rejects in the low-speed regime. Real SFO 28R layout.
/// </summary>
public class TailOverThePavementBlocksDepartureTests
{
    public TailOverThePavementBlocksDepartureTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void RollingDeparture_StoppedExitWithItsTailOverThePavementFarFromAnyBar_Rejects()
    {
        if (RunwayBarFixture.RunwayBar("KSFO", "28R") is null)
        {
            return;
        }

        ExitPose pose = Assert.IsType<ExitPose>(HighSpeedExitFixture.TailOverThePavementOnAHighSpeedExit("KSFO", "28R", "B77W"));
        AircraftState exiting = pose.Stopped();
        AircraftState dep = MakeRollingDeparture(pose.Runway, iasKts: 60);
        var ctx = new PhaseContext
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = dep,
            Targets = dep.Targets,
            Category = AircraftCategorization.Categorize(dep.AircraftType),
            DeltaSeconds = 1.0,
            Runway = pose.Runway,
            FieldElevation = pose.Runway.ElevationFt,
            GroundLayout = pose.Layout,
            Logger = NullLogger.Instance,
            ListAircraft = () => [dep, exiting],
            AutoRejectTakeoffOnOccupiedRunway = true,
        };

        AircraftState? blocker = RejectedTakeoff.FindBlockingOccupant([dep, exiting], dep, pose.Runway, pose.Layout, out double distanceFt);
        dep.Phases!.Start(ctx);
        (bool airborne, double _) = RunRoll(dep, ctx, exiting, seconds: 120);

        Assert.Same(exiting, blocker);
        Assert.True(distanceFt < double.MaxValue, "the stopped exit should lie ahead of the departure");
        Assert.False(airborne, $"a low-speed roll toward an exit with its tail over the pavement must reject (IAS={dep.IndicatedAirspeed:F0})");
        Assert.True(dep.IndicatedAirspeed < 1.0, $"the departure should be stopped, IAS={dep.IndicatedAirspeed:F0}");
    }
}
