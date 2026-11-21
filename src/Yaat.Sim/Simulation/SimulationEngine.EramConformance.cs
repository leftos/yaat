using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;
using Yaat.Sim.Simulation.Coast;

namespace Yaat.Sim.Simulation;

public sealed partial class SimulationEngine
{
    /// <summary>
    /// Maintains each aircraft's ERAM vertical-conformance latch (<see cref="AircraftEramState.ReachedAssignedAltitude"/>).
    /// When the assignment (<see cref="EramConformanceKey"/>: the ERAM altitude in effect, block floor, ABV) differs from the
    /// one the latch was held against, or there is none yet, the new one is stored and the latch cleared; then the latch sets
    /// once the aircraft's altitude is inside the band. Leaving the band never clears it. Nothing is evaluated while the
    /// transponder is in standby (no Mode C) or the track is coasted or frozen. Derived state, not a brain: a spine step
    /// (<see cref="Spine.StepId.EramVerticalConformance"/>), so it runs on every run kind.
    /// </summary>
    public void TickEramVerticalConformance()
    {
        if (Scenario is null)
        {
            return;
        }

        foreach (AircraftState ac in World.GetSnapshot())
        {
            AircraftEramState eram = ac.Eram;
            bool standby = ac.Transponder.IsStandby;
            if (standby || eram.IsFrozen || eram.IsCoastTrack)
            {
                continue;
            }
            EvaluateEramVerticalConformance(ac, eram);
        }
    }

    /// <summary>
    /// Advances each aircraft's ERAM coverage-loss coast and takes its ERAM sweep (<see cref="EramCoverageCoast"/>). Derived
    /// state, not a brain: a spine step (<see cref="Spine.StepId.EramCoverageCoast"/>) just before auto-accept, which reads
    /// it, so it runs on every run kind and a replay or rewind coasts from the same anchor as the original run.
    /// </summary>
    public void TickEramCoverageCoast()
    {
        if (Scenario is not { } scenario)
        {
            return;
        }

        foreach (AircraftState ac in World.GetSnapshot())
        {
            EramCoverageCoast.Evaluate(ac, NavigationDatabase.Instance, scenario.ElapsedSeconds);
        }
    }

    private void EvaluateEramVerticalConformance(AircraftState ac, AircraftEramState eram)
    {
        var key = EramConformanceKey.Of(ac.FlightPlan);
        if (eram.ConformanceKey != key)
        {
            eram.ConformanceKey = key;
            eram.ReachedAssignedAltitude = false;
        }
        if (eram.ReachedAssignedAltitude || !key.Contains(ac.Altitude))
        {
            return;
        }

        eram.ReachedAssignedAltitude = true;
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "{Callsign} reached its ERAM assigned altitude {Feet} ft at {Altitude:F0} ft",
                ac.Callsign,
                key.AssignedFeet,
                ac.Altitude
            );
        }
    }
}
