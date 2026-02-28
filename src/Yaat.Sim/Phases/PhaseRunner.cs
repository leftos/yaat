namespace Yaat.Sim.Phases;

public static class PhaseRunner
{
    public static void Tick(AircraftState aircraft, PhaseContext ctx)
    {
        var phases = aircraft.Phases;
        if (phases is null || phases.IsComplete)
        {
            return;
        }

        var current = phases.CurrentPhase;
        if (current is null)
        {
            return;
        }

        // Start pending phase list on first tick
        if (current.Status == PhaseStatus.Pending)
        {
            phases.Start(ctx);
            current = phases.CurrentPhase;
            if (current is null)
            {
                return;
            }
        }

        current.ElapsedSeconds += ctx.DeltaSeconds;

        bool complete = current.OnTick(ctx);
        if (complete)
        {
            phases.AdvanceToNext(ctx);

            // Auto-cycle: if the phase list is complete and the aircraft is
            // in pattern mode, append the next circuit and clear clearances.
            if (phases.IsComplete
                && phases.TrafficDirection is { } dir
                && phases.AssignedRunway is { } runway)
            {
                var nextCircuit = PatternBuilder.BuildNextCircuit(
                    runway, ctx.Category, dir);
                phases.Phases.AddRange(nextCircuit);

                // Clear landing clearance — RPO must re-clear each approach
                phases.LandingClearance = null;
                phases.ClearedRunwayId = null;

                // Start the first phase of the new circuit
                if (phases.CurrentPhase is { Status: PhaseStatus.Pending } next)
                {
                    next.Status = PhaseStatus.Active;
                    next.OnStart(ctx);
                }
            }
        }
    }
}
