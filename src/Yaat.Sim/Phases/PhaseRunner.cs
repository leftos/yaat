using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;

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
            bool wasLanding = current is LandingPhase or HelicopterLandingPhase;

            // Stamp landing timestamp on approach score
            if (wasLanding && aircraft.ActiveApproachScore is { } landingScore && landingScore.LandedAtSeconds is null)
            {
                landingScore.LandedAtSeconds = ctx.ScenarioElapsedSeconds;
                aircraft.PendingApproachScores.Add(landingScore);
                aircraft.ActiveApproachScore = null;
            }

            phases.AdvanceToNext(ctx);

            // After a LAHSO landing, hold on the runway instead of exiting
            bool wasLahso = current is LandingPhase { StoppedForLahso: true };
            if (wasLahso && phases.LahsoHoldShort is { } lahsoTarget)
            {
                phases.Phases.Add(new RunwayHoldingPhase(lahsoTarget.CrossingRunwayId));
                phases.Phases.Add(new RunwayExitPhase());
                phases.Phases.Add(new HoldingAfterExitPhase());

                if (phases.CurrentPhase is { Status: PhaseStatus.Pending } lahsoNext)
                {
                    lahsoNext.Status = PhaseStatus.Active;
                    lahsoNext.OnStart(ctx);
                }

                phases.LahsoHoldShort = null;
                return;
            }

            // After a full-stop landing (not pattern mode), auto-exit the runway.
            // RunwayExitPhase handles the case where GroundLayout is null (stops immediately).
            if (wasLanding && phases.IsComplete && phases.TrafficDirection is null)
            {
                phases.Phases.Add(new RunwayExitPhase());
                phases.Phases.Add(new HoldingAfterExitPhase());

                if (phases.CurrentPhase is { Status: PhaseStatus.Pending } next)
                {
                    next.Status = PhaseStatus.Active;
                    next.OnStart(ctx);
                }

                return;
            }

            // Pattern/visual traffic re-enters the pattern after a go-around.
            // If TrafficDirection was cleared (e.g. by CLAND during go-around), default to left.
            // Instrument approach traffic does NOT auto-enter the pattern — they fly
            // runway heading to 2000 AGL and await instructions.
            if (current is GoAroundPhase { ReenterPattern: true } && phases.IsComplete && phases.AssignedRunway is not null)
            {
                phases.TrafficDirection ??= PatternDirection.Left;
            }

            // Auto-cycle: if the phase list is complete and the aircraft is
            // in pattern mode, append the next circuit and clear clearances.
            if (phases.IsComplete && phases.TrafficDirection is { } dir && phases.AssignedRunway is not null)
            {
                var runway = phases.PatternRunway ?? phases.AssignedRunway;
                var nextCircuit = PatternBuilder.BuildNextCircuit(runway, ctx.Category, dir, ctx.Aircraft.PatternSizeOverrideNm);
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
