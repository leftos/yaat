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
/// Three kinds of command are never delayed: one carrying explicit leading timing (a <c>WAIT</c>/<c>WAITD</c> or a
/// <c>BEHIND</c> give-way condition — the controller's timing already models the wait and produces its own deferred
/// dispatch inside <see cref="CommandDispatcher.DispatchCompound"/>), a purely frequency-change / radio-contact
/// compound (AIM 4-2-3 expects a pilot to switch "as soon as possible"; holding the aircraft on frequency for several
/// seconds would teach a backwards habit — a mixed compound such as <c>FH 270; CON TWR</c> is still delayed as a whole),
/// and one carrying an instructor action (a <c>"Sim Control"</c> registry verb — force heading/altitude/speed, warp,
/// turn rate, delete). The instructor sets the state directly, no pilot is in the loop, so the whole compound
/// dispatches at once; <c>WAIT</c>/<c>WAITD</c> share that category but are timing, not actions, and are skipped by
/// that check so a WAIT later in a chain (<c>FH 090; AT 4000 WAIT 10 FH 270</c>) does not turn the chain immediate.
/// A compound carrying an unsupported verb is exempt as well, so the controller gets <c>DispatchCompound</c>'s refusal
/// at once rather than a "Pilot complying in Ns" acknowledgement followed by the refusal seconds later.
/// </para>
/// </summary>
public static class ReactionDelayPolicy
{
    private const string InstructorCategory = "Sim Control";

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

        if (ContainsUnsupported(compound) || HasExplicitLeadingTiming(compound) || IsPureCommCompound(compound) || ContainsInstructorAction(compound))
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

    // An unsupported verb is refused by CommandDispatcher.DispatchCompound: refusing it now beats a "Pilot complying
    // in Ns" acknowledgement followed by the refusal seconds later. It also has no canonical type, so it must be
    // caught before ContainsInstructorAction asks CommandDescriber.ToCanonicalType for one, which throws.
    private static bool ContainsUnsupported(CompoundCommand compound)
    {
        foreach (var block in compound.Blocks)
        {
            foreach (var cmd in block.Commands)
            {
                if (cmd is UnsupportedCommand)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ContainsInstructorAction(CompoundCommand compound)
    {
        foreach (var block in compound.Blocks)
        {
            foreach (var cmd in block.Commands)
            {
                // WAIT/WAITD live in the same registry category but are timing modifiers, not instructor actions.
                if (cmd is WaitCommand or WaitDistanceCommand)
                {
                    continue;
                }

                if (CommandRegistry.Get(CommandDescriber.ToCanonicalType(cmd))?.Category == InstructorCategory)
                {
                    return true;
                }
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
