using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Phases;

public static class PhaseRunner
{
    public static void Tick(AircraftState aircraft, PhaseContext ctx)
    {
        PhaseList? phases = aircraft.Phases;
        if (phases is null || phases.IsComplete)
        {
            return;
        }

        Phase? current = phases.CurrentPhase;
        if (current is null)
        {
            return;
        }

        VoicePendingRunwayCrossing(aircraft, ctx);
        StartPendingPhase(ctx, phases);

        current.ElapsedSeconds += ctx.DeltaSeconds;

        if (!current.OnTick(ctx))
        {
            return;
        }

        TerminatorKind kind = ClassifyTerminator(current);
        StampLandingScore(aircraft, ctx, kind);

        if (TryHoldBeforeAdvance(aircraft, ctx, phases, current))
        {
            return;
        }

        phases.AdvanceToNext(ctx);

        if (TryHandlePostLahso(aircraft, ctx, phases, current))
        {
            return;
        }

        // The persistent MLT/MRT intent, which the auto-cycle prefers over the transient PhaseList.TrafficDirection.
        PatternDirection? persistentDir = aircraft.Pattern.TrafficDirection;

        if (TryHandleFullStopExit(aircraft, ctx, phases, current, kind))
        {
            return;
        }

        HandleAutoCycle(ctx, phases, current, kind, persistentDir);

        StartPendingPursuitAfterClimb(aircraft, ctx, current);

        // The list is settled (plain advance, or a new circuit appended), so offer queued blocks to the new current phase.
        FlightPhysics.NotifyPhaseAdvanced(aircraft);
    }

    /// <summary>
    /// A runway exit that stopped at a LAHSO point holds there before the exit chain's next phase starts: that phase would otherwise
    /// make its own call ("clear of the runway") for an aircraft still on it.
    /// </summary>
    private static bool TryHoldBeforeAdvance(AircraftState aircraft, PhaseContext ctx, PhaseList phases, Phase current) =>
        (current is RunwayExitPhase { StoppedForLahso: true }) && TryHandlePostLahso(aircraft, ctx, phases, current);

    /// <summary>
    /// Starts the pursuit a climb that accepted FOLLOW of a lead with no runway was holding off
    /// (<see cref="IPendingPursuitClimb"/>): with the climb handed over to its circuit's upwind, a free pursuit with a pattern
    /// return starts, exactly as for an upwind follower told to follow a runwayless lead. The pattern return and the
    /// departure-leg hold are built before the install, which replaces the phase list they are read from. Nothing changes when
    /// the flag is set but the follow or its lead is gone.
    /// </summary>
    private static void StartPendingPursuitAfterClimb(AircraftState aircraft, PhaseContext ctx, Phase completed)
    {
        if (
            (completed is not IPendingPursuitClimb { PursuesRunwaylessLeadAfterClimb: true })
            || (aircraft.Approach.FollowingCallsign is not { } target)
        )
        {
            return;
        }

        if ((ctx.AircraftLookup?.Invoke(target) is null) || (aircraft.Phases?.AssignedRunway is not { } runway))
        {
            return;
        }

        FollowPatternReturn patternReturn = VfrFollowPhase.BuildFollowPatternReturn(aircraft, runway, ctx.GroundLayout);
        CommandDispatcher.InstallVfrFollowPhase(aircraft, target, patternReturn, VfrFollowPhase.ClimbOutGateFor(aircraft, ctx.GroundLayout));
    }

    /// <summary>
    /// Activates a pending current phase in place — a freshly installed list (index 0) and a circuit spliced
    /// past a completed chain (e.g. airborne MRT after the departure chain finished) both land here. Never
    /// PhaseList.Start: that rewinds CurrentIndex to 0 and re-runs the completed prefix (the aircraft re-taxied
    /// an old runway crossing mid-air).
    /// </summary>
    private static void StartPendingPhase(PhaseContext ctx, PhaseList phases)
    {
        if (phases.CurrentPhase is { Status: PhaseStatus.Pending } next)
        {
            next.Status = PhaseStatus.Active;
            next.OnStart(ctx);
        }
    }

    /// <summary>
    /// After a LAHSO stop short of the hold-short point, hold on the runway instead of exiting (AIM 4-3-11.b.6), whichever phase
    /// brought the aircraft to rest — the landing (<see cref="LandingPhase.StoppedForLahso"/>, after the list advanced) or the runway
    /// exit with no exit before the point (<see cref="RunwayExitPhase.StoppedForLahso"/>, before it advances): rebuild the tail of
    /// the list as RunwayHoldingPhase → RunwayExitPhase → HoldingAfterExitPhase and offer queued blocks to it.
    /// </summary>
    private static bool TryHandlePostLahso(AircraftState aircraft, PhaseContext ctx, PhaseList phases, Phase current)
    {
        bool stoppedForLahso = current is LandingPhase { StoppedForLahso: true } or RunwayExitPhase { StoppedForLahso: true };
        if (!stoppedForLahso || (phases.LahsoHoldShort is not { } lahsoTarget))
        {
            return false;
        }

        // The runway exit has not been advanced past: it completes here, and the exit chain queued behind it is skipped unstarted.
        if (current.Status == PhaseStatus.Active)
        {
            current.Status = PhaseStatus.Completed;
            current.OnEnd(ctx, PhaseStatus.Completed);
        }

        // The aircraft is stopped on the runway short of the hold-short point and holds there whatever the
        // chain queued after it: end the phase AdvanceToNext just started (after a landing) and skip the rest, so the
        // hold chain goes in exactly as it does when the stopping phase was the last one (a no-op then).
        phases.Clear(ctx);

        phases.Phases.Add(new RunwayHoldingPhase(lahsoTarget.CrossingRunwayId));
        phases.Phases.Add(new RunwayExitPhase());
        phases.Phases.Add(new HoldingAfterExitPhase());

        StartPendingPhase(ctx, phases);

        phases.LahsoHoldShort = null;
        // Only now, with the list settled, may queued blocks fire: the skipped phases must never consume one.
        FlightPhysics.NotifyPhaseAdvanced(aircraft);
        return true;
    }

    /// <summary>
    /// A full-stop terminator that completed the list exits the runway regardless of pattern state: append
    /// RunwayExitPhase → HoldingAfterExitPhase and offer queued blocks to the first of them. After a landing whose LAHSO clearance
    /// the overrun call voided, the runway exit carries the crossing runway (<see cref="RunwayExitPhase.VoidedLahsoCrossingRunwayId"/>)
    /// so its no-exit stop keeps off it too (AIM 4-3-21.b).
    /// </summary>
    private static bool TryHandleFullStopExit(AircraftState aircraft, PhaseContext ctx, PhaseList phases, Phase completed, TerminatorKind kind)
    {
        if (!kind.FullStop || !phases.IsComplete)
        {
            return false;
        }

        // Drop the transient pattern direction so this PhaseList no longer
        // reads as in-pattern. The persistent AircraftPattern.TrafficDirection
        // (MLT/MRT intent) is left intact for any future re-spawn / re-clearance.
        phases.TrafficDirection = null;

        var runwayExit = new RunwayExitPhase();
        if (completed is LandingPhase landing)
        {
            runwayExit.VoidedLahsoCrossingRunwayId = landing.VoidedLahsoCrossingRunwayIdForHandOff(ctx);
        }

        phases.Phases.Add(runwayExit);
        phases.Phases.Add(new HoldingAfterExitPhase());

        StartPendingPhase(ctx, phases);

        // The list is settled with the exit chain installed, so offer queued blocks to its first phase.
        FlightPhysics.NotifyPhaseAdvanced(aircraft);
        return true;
    }

    /// <summary>
    /// Re-enters the pattern after a go-around. Prefer the persistent MLT/MRT intent; fall back to any
    /// transient direction; finally default to left. Instrument approach traffic does NOT auto-enter the
    /// pattern — they fly runway heading to 2000 AGL and await instructions.
    /// </summary>
    private static void ApplyGoAroundReentry(PhaseList phases, Phase current, PatternDirection? persistentDir)
    {
        if ((current is not GoAroundPhase { ReenterPattern: true }) || !phases.IsComplete || (phases.AssignedRunway is null))
        {
            return;
        }

        phases.TrafficDirection = persistentDir ?? phases.TrafficDirection ?? PatternDirection.Left;
    }

    /// <summary>
    /// Appends the next circuit after a cycle terminator. Never notifies: the caller holds the single notify
    /// for the settled list.
    /// </summary>
    private static void HandleAutoCycle(PhaseContext ctx, PhaseList phases, Phase current, TerminatorKind kind, PatternDirection? persistentDir)
    {
        ApplyGoAroundReentry(phases, current, persistentDir);

        // Auto-cycle: only fires after a cycle terminator (TouchAndGoPhase,
        // StopAndGoPhase, LowApproachPhase) or a GoAround that re-enters the
        // pattern. The persistent direction (MLT/MRT) wins so a single-approach
        // ERB/ELB doesn't redefine the pattern direction for subsequent circuits.
        if (!kind.StartsNextCircuit || !phases.IsComplete)
        {
            return;
        }

        PatternDirection? resolvedDir = persistentDir ?? phases.TrafficDirection;
        if (resolvedDir is null)
        {
            return;
        }

        if (phases.AssignedRunway is not { } assignedRunway)
        {
            return;
        }

        PatternDirection dir = resolvedDir.Value;

        // Re-stamp the transient field so phases built for the new circuit
        // (and downstream pattern-mode predicates that read it) reflect the
        // direction actually being flown.
        phases.TrafficDirection = dir;
        VoidArmedPatternRunwayOnGoAround(ctx, phases, current, dir);

        List<Phase> nextCircuit = AppendNextCircuit(ctx, phases, current, dir, assignedRunway);

        // Consume the one-shot EXT pre-arm set by EXT during T/G or pre-T/G
        // FinalApproach. The first UpwindPhase of the new circuit gets
        // IsExtended=true; the flag clears after consumption.
        if (ctx.Aircraft.Pattern.ExtendNextUpwind)
        {
            UpwindPhase? firstUpwind = nextCircuit.OfType<UpwindPhase>().FirstOrDefault();
            firstUpwind?.IsExtended = true;
            ctx.Aircraft.Pattern.ExtendNextUpwind = false;
        }

        // Clear landing clearance — RPO must re-clear each approach
        phases.LandingClearance = null;
        phases.ClearedRunwayId = null;

        // Start the first phase of the new circuit
        StartPendingPhase(ctx, phases);
    }

    /// <summary>
    /// The one message owed for a circuit the type cannot compress clear of another runway, voiced only while that circuit is
    /// still the one ahead: a crossing whose circuit was cleared or replaced before the tick that voices it is dropped, and its
    /// runway and side stay unwarned, so the next circuit that crosses owes it again.
    /// </summary>
    private static void VoicePendingRunwayCrossing(AircraftState aircraft, PhaseContext ctx)
    {
        if (aircraft.Pattern.PendingRunwayCrossing is not { } crossing)
        {
            return;
        }

        aircraft.Pattern.PendingRunwayCrossing = null;
        PhaseList? phases = aircraft.Phases;
        PatternWaypoints? circuitAhead = phases
            ?.Phases.Skip(phases.CurrentIndex)
            .Select(AirborneFollowHelper.PatternWaypointsOf)
            .FirstOrDefault(waypoints => waypoints?.RunwayCrossing == crossing);
        if (circuitAhead is null)
        {
            return;
        }

        aircraft.Pattern.MarkRunwayCrossingWarned(crossing, circuitAhead.Direction);
        PilotResponder.RouteByTunedPosition(aircraft, PilotReportRouting.From(ctx), PilotResponder.BuildPatternCrossesRunway(aircraft, crossing));
    }

    /// <summary>
    /// Builds the next circuit for the pattern direction and appends it, moving AssignedRunway with a
    /// cross-runway transition. Returns the appended phases.
    /// </summary>
    private static List<Phase> AppendNextCircuit(PhaseContext ctx, PhaseList phases, Phase current, PatternDirection dir, RunwayInfo flownRunway)
    {
        RunwayInfo runway = phases.PatternRunway ?? flownRunway;
        IReadOnlyList<RunwayInfo> airportRunways = Data.NavigationDatabase.Instance.GetRunways(runway.AirportId);
        // Resolve authored pattern data from the context's resolved ground layout (which falls
        // back to the assigned-runway airport when the per-aircraft Ground.Layout is unset);
        // otherwise an airborne pattern aircraft with no cached layout reverts to the category
        // default TPA and flies a long, climb-bound upwind (issue #210).
        AirportGroundLayout? patternLayout = ctx.GroundLayout ?? ctx.Aircraft.Ground.Layout;
        GroundRunway? authoredRunway = patternLayout?.FindRunway(runway.Designator);

        // A pattern runway that is not the one just flown (an option clearance's `COPT MLT 28L`
        // on the 28R final) makes this circuit the runway transition: the aircraft climbs out on
        // the runway it just used and joins the named runway's pattern (AIM 4-3-2). Every later
        // circuit belongs to the new runway, so the assignment moves with it. A cross-runway CTO
        // has already moved AssignedRunway to the pattern runway at clearance time, so its
        // designators match here and it takes the plain next circuit.
        bool runwayTransition = !string.Equals(flownRunway.Designator, runway.Designator, StringComparison.OrdinalIgnoreCase);
        if (runwayTransition)
        {
            ApplyDeferredTransitionAltitude(ctx.Aircraft, phases);
        }

        (double? sizeOv, double? altOv) = PatternGeometry.ResolveAuthoredOverrides(
            runway,
            authoredRunway,
            ctx.Category,
            ctx.Aircraft.Pattern.SizeOverrideNm,
            ctx.Aircraft.Pattern.AltitudeOverrideFt
        );
        // After a GoAroundPhase, honor the captured pre-GA landing intent
        // (full-stop → next circuit ends in LandingPhase). After any other
        // cycle terminator the aircraft was already cycling, so keep cycling with TG.
        bool nextTouchAndGo = current is not GoAroundPhase ga || !ga.NextLandingFullStop;

        List<Phase> nextCircuit = runwayTransition
            ? PatternBuilder.BuildRunwayTransitionCircuit(
                new RunwayTransitionCircuitRequest
                {
                    FlownRunway = flownRunway,
                    PatternRunway = runway,
                    Category = ctx.Category,
                    AircraftType = ctx.Aircraft.AircraftType,
                    WindSpeedKt = ctx.Aircraft.WindSpeedKts,
                    Direction = dir,
                    TouchAndGo = nextTouchAndGo,
                    PatternSizeNm = sizeOv,
                    AltitudeOverrideFt = altOv,
                    AirportRunways = airportRunways,
                    FlownAuthoredRunway = patternLayout?.FindRunway(flownRunway.Designator),
                    PatternAuthoredRunway = authoredRunway,
                    ActiveRunways = ctx.ActiveRunways,
                    Commanded = PatternCommanded.Of(ctx.Aircraft),
                }
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
                nextTouchAndGo,
                ctx.ActiveRunways,
                PatternCommanded.Of(ctx.Aircraft)
            );
        phases.Phases.AddRange(nextCircuit);
        // A circuit after a go-around or an option clearance is built here; a lap of a circuit already warned stays quiet.
        ctx.Aircraft.Pattern.NoteRunwayCrossing(nextCircuit);

        if (runwayTransition)
        {
            // The circuit/final/landing phases read AssignedRunway, and the aircraft has left the
            // old runway's pattern for good.
            phases.AssignedRunway = runway;
        }

        return nextCircuit;
    }

    /// <summary>
    /// Applies the pattern altitude an option clearance's modifier deferred to the runway-transition circuit
    /// (<see cref="PhaseList.TransitionPatternAltitude"/>) — its own altitude, or the runway change's clear —
    /// and disarms it. Nothing changes when none is armed.
    /// </summary>
    private static void ApplyDeferredTransitionAltitude(AircraftState aircraft, PhaseList phases)
    {
        if (phases.TransitionPatternAltitude is not { } deferred)
        {
            return;
        }

        aircraft.Pattern.SetAltitudeOverride(deferred.AltitudeFt, phases, AircraftCategorization.Categorize(aircraft.AircraftType));
        phases.TransitionPatternAltitude = null;
    }

    /// <summary>
    /// Stamps the landing timestamp on the approach score the first time a full-stop terminator completes.
    /// </summary>
    private static void StampLandingScore(AircraftState aircraft, PhaseContext ctx, TerminatorKind kind)
    {
        if (!kind.FullStop || (aircraft.ActiveApproachScore is not { } landingScore) || (landingScore.LandedAtSeconds is not null))
        {
            return;
        }

        landingScore.LandedAtSeconds = ctx.ScenarioElapsedSeconds;
        aircraft.PendingApproachScores.Add(landingScore);
        aircraft.ActiveApproachScore = null;
    }

    /// <summary>
    /// Classifies the completed phase by the routing it drives. The terminator type, never a pattern-mode flag,
    /// is the source of truth for what happens next. Only a go-around that re-enters the pattern arms the
    /// auto-cycle: an instrument go-around (ReenterPattern=false) flies runway heading to 2000 AGL and awaits
    /// instructions, and must not auto-enter the pattern even if a stale persistent Pattern.TrafficDirection
    /// (from an earlier MLT/MRT that survived a vector) is still set — see ResolvePatternIntent.
    /// </summary>
    private static TerminatorKind ClassifyTerminator(Phase current) =>
        new(
            current is LandingPhase or HelicopterLandingPhase,
            current is TouchAndGoPhase or StopAndGoPhase or LowApproachPhase,
            current is GoAroundPhase { ReenterPattern: true }
        );

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
        // The altitude the modifier deferred to the armed runway rode on the same clearance.
        phases.TransitionPatternAltitude = null;
        string directionWord = direction == PatternDirection.Left ? "left" : "right";
        ctx.Aircraft.PendingWarnings.Add(
            $"{ctx.Aircraft.Callsign} went around — {directionWord} traffic runway {Data.Airport.RunwayIdentifier.ToDisplayDesignator(armedRunway.Designator)} cancelled, re-issue"
        );
    }

    /// <summary>
    /// The kind of phase that just completed, as read by the post-completion routing in Tick.
    /// </summary>
    private readonly record struct TerminatorKind(bool FullStop, bool Cycle, bool PatternGoAround)
    {
        /// <summary>True when the completed phase may begin another pattern circuit.</summary>
        public bool StartsNextCircuit => Cycle || PatternGoAround;
    }
}
