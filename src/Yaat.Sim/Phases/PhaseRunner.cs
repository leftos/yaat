using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Pattern;
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

        // Activate a pending current phase in place — a freshly installed list (index 0) and a
        // circuit spliced past a completed chain (e.g. airborne MRT after the departure chain
        // finished) both land here. Never PhaseList.Start: that rewinds CurrentIndex to 0 and
        // re-runs the completed prefix (the aircraft re-taxied an old runway crossing mid-air).
        if (current.Status == PhaseStatus.Pending)
        {
            current.Status = PhaseStatus.Active;
            current.OnStart(ctx);
        }

        current.ElapsedSeconds += ctx.DeltaSeconds;

        bool complete = current.OnTick(ctx);
        if (complete)
        {
            bool wasFullStopTerminator = current is LandingPhase or HelicopterLandingPhase;
            bool wasCycleTerminator = current is TouchAndGoPhase or StopAndGoPhase or LowApproachPhase;
            // Only a go-around that re-enters the pattern arms the auto-cycle. An instrument go-around
            // (ReenterPattern=false) flies runway heading to 2000 AGL and awaits instructions — it must
            // NOT auto-enter the pattern even if a stale persistent Pattern.TrafficDirection (from an
            // earlier MLT/MRT that survived a vector) is still set. See ResolvePatternIntent, which
            // returns null for a non-in-pattern IFR aircraft precisely to prevent this.
            bool wasPatternGoAround = current is GoAroundPhase { ReenterPattern: true };

            // Stamp landing timestamp on approach score
            if (wasFullStopTerminator && aircraft.ActiveApproachScore is { } landingScore && landingScore.LandedAtSeconds is null)
            {
                landingScore.LandedAtSeconds = ctx.ScenarioElapsedSeconds;
                aircraft.PendingApproachScores.Add(landingScore);
                aircraft.ActiveApproachScore = null;
            }

            phases.AdvanceToNext(ctx);

            // Give queued sequential blocks a chance to fire now that the current
            // phase changed (e.g. TaxiingPhase → HoldingShortPhase unlocks a queued
            // CTO MRT). See FlightPhysics.NotifyPhaseAdvanced.
            FlightPhysics.NotifyPhaseAdvanced(aircraft);

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

            // Post-completion routing is driven by the terminator phase type, not
            // by TrafficDirection state. The chain builder picks the terminator
            // based on the clearance intent at build time (CLAND → LandingPhase,
            // CTL → TouchAndGoPhase, etc.), so the terminator is the source of
            // truth for what should happen next. Branching on TrafficDirection
            // misroutes a CLAND'd aircraft to auto-cycle when it was given ERB/ELB
            // after CLAND (ERB stamps direction *after* the chain is built).
            var persistentDir = aircraft.Pattern.TrafficDirection;

            // Full-stop terminator → exit the runway regardless of pattern state.
            if (wasFullStopTerminator && phases.IsComplete)
            {
                // Drop the transient pattern direction so this PhaseList no longer
                // reads as in-pattern. The persistent AircraftPattern.TrafficDirection
                // (MLT/MRT intent) is left intact for any future re-spawn / re-clearance.
                phases.TrafficDirection = null;

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
            // Prefer the persistent MLT/MRT intent; fall back to any transient
            // direction; finally default to left. Instrument approach traffic
            // does NOT auto-enter the pattern — they fly runway heading to 2000
            // AGL and await instructions.
            if (current is GoAroundPhase { ReenterPattern: true } && phases.IsComplete && phases.AssignedRunway is not null)
            {
                phases.TrafficDirection = persistentDir ?? phases.TrafficDirection ?? PatternDirection.Left;
            }

            // Auto-cycle: only fires after a cycle terminator (TouchAndGoPhase,
            // StopAndGoPhase, LowApproachPhase) or a GoAround that re-enters the
            // pattern. The persistent direction (MLT/MRT) wins so a single-approach
            // ERB/ELB doesn't redefine the pattern direction for subsequent circuits.
            if (
                (wasCycleTerminator || wasPatternGoAround)
                && phases.IsComplete
                && (persistentDir ?? phases.TrafficDirection) is { } dir
                && phases.AssignedRunway is not null
            )
            {
                // Re-stamp the transient field so phases built for the new circuit
                // (and downstream pattern-mode predicates that read it) reflect the
                // direction actually being flown.
                phases.TrafficDirection = dir;
                VoidArmedPatternRunwayOnGoAround(ctx, phases, current, dir);
                var runway = phases.PatternRunway ?? phases.AssignedRunway;
                var airportRunways = Data.NavigationDatabase.Instance.GetRunways(runway.AirportId);
                // Resolve authored pattern data from the context's resolved ground layout (which falls
                // back to the assigned-runway airport when the per-aircraft Ground.Layout is unset);
                // otherwise an airborne pattern aircraft with no cached layout reverts to the category
                // default TPA and flies a long, climb-bound upwind (issue #210).
                var patternLayout = ctx.GroundLayout ?? ctx.Aircraft.Ground.Layout;
                var authoredRunway = patternLayout?.FindRunway(runway.Designator);
                var (sizeOv, altOv) = PatternGeometry.ResolveAuthoredOverrides(
                    runway,
                    authoredRunway,
                    ctx.Category,
                    ctx.Aircraft.Pattern.SizeOverrideNm,
                    ctx.Aircraft.Pattern.AltitudeOverrideFt
                );
                // After a GoAroundPhase, honor the captured pre-GA landing intent
                // (full-stop → next circuit ends in LandingPhase). After any other
                // cycle terminator the aircraft was already cycling, so keep cycling with TG.
                bool nextTouchAndGo = current is GoAroundPhase ga ? !ga.NextLandingFullStop : true;

                // A pattern runway that is not the one just flown (an option clearance's `COPT MLT 28L`
                // on the 28R final) makes this circuit the runway transition: the aircraft climbs out on
                // the runway it just used and joins the named runway's pattern (AIM 4-3-2). Every later
                // circuit belongs to the new runway, so the assignment moves with it. A cross-runway CTO
                // has already moved AssignedRunway to the pattern runway at clearance time, so its
                // designators match here and it takes the plain next circuit.
                var flownRunway = phases.AssignedRunway;
                bool runwayTransition = !string.Equals(flownRunway.Designator, runway.Designator, StringComparison.OrdinalIgnoreCase);
                var nextCircuit = runwayTransition
                    ? PatternBuilder.BuildRunwayTransitionCircuit(
                        flownRunway,
                        runway,
                        ctx.Category,
                        ctx.Aircraft.AircraftType,
                        ctx.Aircraft.WindSpeedKts,
                        dir,
                        nextTouchAndGo,
                        sizeOv,
                        altOv,
                        airportRunways,
                        patternLayout?.FindRunway(flownRunway.Designator),
                        authoredRunway
                    )
                    : PatternBuilder.BuildNextCircuit(
                        runway,
                        ctx.Category,
                        ctx.Aircraft.AircraftType,
                        ctx.Aircraft.WindSpeedKts,
                        dir,
                        sizeOv,
                        altOv,
                        airportRunways,
                        authoredRunway,
                        nextTouchAndGo
                    );
                phases.Phases.AddRange(nextCircuit);

                if (runwayTransition)
                {
                    // The circuit/final/landing phases read AssignedRunway, and the aircraft has left the
                    // old runway's pattern for good.
                    phases.AssignedRunway = runway;
                }

                // Consume the one-shot EXT pre-arm set by EXT during T/G or pre-T/G
                // FinalApproach. The first UpwindPhase of the new circuit gets
                // IsExtended=true; the flag clears after consumption.
                if (ctx.Aircraft.Pattern.ExtendNextUpwind)
                {
                    var firstUpwind = nextCircuit.OfType<UpwindPhase>().FirstOrDefault();
                    if (firstUpwind is not null)
                    {
                        firstUpwind.IsExtended = true;
                    }
                    ctx.Aircraft.Pattern.ExtendNextUpwind = false;
                }

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

    /// <summary>
    /// Drops a pattern runway armed by an option clearance's MLT/MRT modifier when the approach ended in
    /// a go-around, and tells the RPO. The modifier rode on the clearance ("cleared for the option runway
    /// 28R, make left traffic runway 28L"), and a go-around voids that clearance — AIM 5-5-5.a.6 has the
    /// pilot request the next action rather than carry the old instruction into the climb-out, so the
    /// aircraft stays in the pattern it is flying until the controller re-issues. A completed terminator
    /// (touch-and-go / stop-and-go / low approach) is the clearance flown as issued and still transitions.
    ///
    /// <para>Only an <em>armed</em> modifier is affected: a cross-runway takeoff clearance and an
    /// <c>MLT</c>/<c>MRT</c> runway switch both write <see cref="PhaseList.AssignedRunway"/> and
    /// <see cref="PhaseList.PatternRunway"/> together, so their designators match here and nothing is
    /// voided. An <c>OTG</c>-queued pattern change is a standalone instruction that fires on the
    /// climb-out through its own command path and is likewise untouched.</para>
    /// </summary>
    private static void VoidArmedPatternRunwayOnGoAround(PhaseContext ctx, PhaseList phases, Phase current, PatternDirection direction)
    {
        if (
            (current is not GoAroundPhase)
            || (phases.AssignedRunway is not { } assignedRunway)
            || (phases.PatternRunway is not { } armedRunway)
            || string.Equals(armedRunway.Designator, assignedRunway.Designator, StringComparison.OrdinalIgnoreCase)
        )
        {
            return;
        }

        phases.PatternRunway = assignedRunway;
        string directionWord = direction == PatternDirection.Left ? "left" : "right";
        ctx.Aircraft.PendingWarnings.Add(
            $"{ctx.Aircraft.Callsign} went around — {directionWord} traffic runway {Data.Airport.RunwayIdentifier.ToDisplayDesignator(armedRunway.Designator)} cancelled, re-issue"
        );
    }
}
