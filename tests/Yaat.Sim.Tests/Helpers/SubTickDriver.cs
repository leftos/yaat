using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>Drives a <see cref="SimulationEngine"/> through its spine one physics sub-tick at a time.</summary>
internal static class SubTickDriver
{
    /// <summary>
    /// Runs the engine a second at a time through its spine, calling <paramref name="eachSubTick"/> with <paramref name="callsign"/>
    /// after every physics sub-tick, until <paramref name="until"/> holds after one; the second is finished either way. Null when it
    /// never holds.
    /// </summary>
    internal static AircraftState? TickSubTicksUntil(
        SimulationEngine engine,
        string callsign,
        Func<AircraftState, bool> until,
        int maxSeconds,
        Action<AircraftState> eachSubTick
    )
    {
        double subDelta = 1.0 / SimulationEngine.PhysicsSubTickRate;
        for (int second = 1; second <= maxSeconds; second++)
        {
            AircraftState? done = null;
            engine.BeginSecond();
            engine.OpenSecond(engine.BareHost);
            engine.RunPrePhysics(engine.BareHost);
            for (int sub = 0; sub < SimulationEngine.PhysicsSubTickRate; sub++)
            {
                engine.RunPhysicsSubTick(subDelta, sub);
                if (engine.FindAircraft(callsign) is { } ac)
                {
                    eachSubTick(ac);
                    done ??= until(ac) ? ac : null;
                }
            }

            engine.RunPostPhysics(engine.BareHost);
            engine.RunEndOfSecond(engine.BareHost);
            if (done is not null)
            {
                return done;
            }
        }

        return null;
    }
}
