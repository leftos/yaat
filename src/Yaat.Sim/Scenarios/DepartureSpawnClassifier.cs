using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Scenarios;

/// <summary>
/// Classifies how a loaded departure should be gated by hold-for-release based on its spawn state.
///
/// <para>A departure that spawns <em>lined up on the runway</em> or <em>airborne climbing out</em> is a
/// "held-spawn candidate": hold-for-release gates its <em>spawn</em>, so it appears on the scope only
/// when released. A departure that spawns at <em>parking/taxiway</em> is not — it spawns and taxis
/// normally, then holds short of the runway via the per-aircraft
/// <see cref="AircraftGroundOps.HeldForRelease"/> flag (its takeoff clearance is withheld).</para>
/// </summary>
public static class DepartureSpawnClassifier
{
    /// <summary>
    /// True when this loaded aircraft is an IFR departure whose spawn should be gated by
    /// hold-for-release (spawns lined up on the runway, or airborne in the initial climb-out). VFR
    /// aircraft, arrivals, and parking/taxiway departures return false.
    /// </summary>
    public static bool IsHeldSpawnCandidate(LoadedAircraft loaded)
    {
        var state = loaded.State;
        if (state.FlightPlan.IsVfr)
        {
            return false;
        }

        var phase = state.Phases?.CurrentPhase;

        // Lined up on the runway, ready to roll.
        if (phase is LinedUpAndWaitingPhase)
        {
            return true;
        }

        // Airborne, still in the initial climb-out off the departure end.
        return !state.IsOnGround && phase is InitialClimbPhase;
    }
}
