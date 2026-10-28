using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// Active touch-and-go and stop-and-go phases already past liftoff on <c>runway</c>, restored through the phases' own
/// snapshot path — the state the sim leaves them in once they rotate (7110.65 3-8-2: a departure from then on).
/// </summary>
internal static class LiftedOffOptionPhases
{
    internal static TouchAndGoPhase TouchAndGo(RunwayInfo runway) =>
        TouchAndGoPhase.FromSnapshot(
            new TouchAndGoPhaseDto
            {
                Status = (int)PhaseStatus.Active,
                ElapsedSeconds = 30,
                FieldElevation = runway.ElevationFt,
                RunwayHeadingDeg = runway.TrueHeading.Degrees,
                ThresholdLat = runway.ThresholdLatitude,
                ThresholdLon = runway.ThresholdLongitude,
                RolloutDuration = 5,
                RolloutElapsed = 5,
                Reaccelerating = true,
                Airborne = true,
            }
        );

    internal static StopAndGoPhase StopAndGo(RunwayInfo runway) =>
        StopAndGoPhase.FromSnapshot(
            new StopAndGoPhaseDto
            {
                Status = (int)PhaseStatus.Active,
                ElapsedSeconds = 60,
                FieldElevation = runway.ElevationFt,
                RunwayHeadingDeg = runway.TrueHeading.Degrees,
                ThresholdLat = runway.ThresholdLatitude,
                ThresholdLon = runway.ThresholdLongitude,
                PauseDuration = 10,
                PauseElapsed = 10,
                Stopped = true,
                Reaccelerating = true,
                Airborne = true,
                GoTriggered = true,
            }
        );
}
