using Microsoft.Extensions.Logging;

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
            bool standby = string.Equals(ac.Transponder.Mode, "Standby", StringComparison.OrdinalIgnoreCase);
            if (standby || eram.IsFrozen || eram.IsCoastTrack)
            {
                continue;
            }
            EvaluateEramVerticalConformance(ac, eram);
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
        _logger.LogDebug("{Callsign} reached its ERAM assigned altitude {Feet} ft at {Altitude:F0} ft", ac.Callsign, key.AssignedFeet, ac.Altitude);
    }
}
