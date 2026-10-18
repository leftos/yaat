using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>Runway exit phases restored mid-exit, for placing an aircraft that is leaving a runway.</summary>
public static class ExitPhaseFixture
{
    /// <summary>A runway exit past its turn-off, following the exit path off the centreline.</summary>
    /// <param name="runway">The runway being exited.</param>
    /// <returns>The active exit phase.</returns>
    public static RunwayExitPhase ExitOffTheCentreline(RunwayInfo runway) =>
        RunwayExitPhase.FromSnapshot(
            new RunwayExitPhaseDto
            {
                Status = (int)PhaseStatus.Active,
                ElapsedSeconds = 20,
                ReachedExitNode = false,
                ExitSpeed = 10,
                TimeSinceLastLog = 0,
                RunwayHeadingDeg = runway.TrueHeading.Degrees,
                ExitStateValue = (int)RunwayExitPhase.ExitState.FollowingExitPath,
            },
            groundLayout: null
        );
}
