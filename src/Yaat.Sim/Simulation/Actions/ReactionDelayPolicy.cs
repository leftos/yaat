using Yaat.Sim.Commands;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Simulation.Actions;

/// <summary>
/// Decides the pilot-reaction delay (the command-run delay) for one aviation command: the seconds between the
/// controller issuing an instruction and the aircraft acting on it, simulating FMC / autopilot set-up time. A value
/// baked into a recorded command always wins — re-sampling on replay would draw from a divergent RNG state and break
/// determinism. Otherwise, when a delay range is active, the delay is sampled from
/// <see cref="SimulationWorld.ReactionDelayRng"/> (never the shared RNG, so it cannot perturb replay-critical
/// emergent events) and clamped so a command issued later never starts complying before one issued earlier.
///
/// <para>
/// Two kinds of command are never delayed: one carrying explicit leading timing (a <c>WAIT</c>/<c>WAITD</c> or a
/// <c>BEHIND</c> give-way condition — the controller's timing already models the wait and produces its own deferred
/// dispatch inside <see cref="CommandDispatcher.DispatchCompound"/>), and a purely frequency-change / radio-contact
/// compound (AIM 4-2-3 expects a pilot to switch "as soon as possible"; holding the aircraft on frequency for several
/// seconds would teach a backwards habit — a mixed compound such as <c>FH 270; CON TWR</c> is still delayed as a whole).
/// </para>
/// </summary>
public static class ReactionDelayPolicy
{
    /// <summary>The delay to defer <paramref name="compound"/> by, or null to dispatch it immediately.</summary>
    public static double? Decide(SimScenarioState scenario, SimulationWorld world, AircraftState aircraft, CompoundCommand compound, double? baked)
    {
        if (baked is double bakedSeconds)
        {
            return bakedSeconds;
        }

        if (scenario.CommandRunDelayMaxSeconds <= 0)
        {
            return null;
        }

        if (HasExplicitLeadingTiming(compound) || IsPureCommCompound(compound))
        {
            return null;
        }

        int max = scenario.CommandRunDelayMaxSeconds;
        int min = Math.Clamp(scenario.CommandRunDelayMinSeconds, 0, max);
        int sampled = min >= max ? max : world.ReactionDelayRng.Next(min, max + 1);

        // Preserve issue order: clamp so this command fires no sooner than any reaction deferral already pending on
        // the aircraft (ProcessDeferredDispatches applies same-tick expiries FIFO).
        double clampFloor = 0;
        foreach (var pending in aircraft.DeferredDispatches)
        {
            if ((pending.IsReactionDelay) && (pending.RemainingSeconds > clampFloor))
            {
                clampFloor = pending.RemainingSeconds;
            }
        }

        return Math.Max(sampled, clampFloor);
    }

    private static bool HasExplicitLeadingTiming(CompoundCommand compound)
    {
        if (compound.Blocks.Count == 0)
        {
            return false;
        }

        var first = compound.Blocks[0];
        if (first.Condition is GiveWayCondition)
        {
            return true;
        }

        foreach (var cmd in first.Commands)
        {
            if (cmd is WaitCommand or WaitDistanceCommand)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPureCommCompound(CompoundCommand compound)
    {
        bool hasAny = false;
        foreach (var block in compound.Blocks)
        {
            foreach (var cmd in block.Commands)
            {
                hasAny = true;
                if (cmd is not (ContactCommand or FrequencyChangeApprovedCommand or AcknowledgePilotContactCommand))
                {
                    return false;
                }
            }
        }

        return hasAny;
    }
}
