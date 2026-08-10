using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Pattern;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Consequence engine for the AIM §5-5-11.a.3 disjunction: a pilot on a visual approach must
/// at all times have EITHER the airport OR the preceding aircraft in sight. The detectors —
/// <c>SimulationEngine.TickVisualDetection</c> (per-second maintain checks) and
/// <see cref="AirborneFollowHelper.CheckLeadLifecycle"/> (physics sub-tick follow lifecycle) —
/// flip the two in-sight flags and route their loss events here; this class owns what happens
/// next:
/// <list type="bullet">
/// <item><description><b>Lost the lead, field held</b> — a separation-responsibility handback,
/// not an approach termination: the follow clears (no leg freeze — §7-4-3.c.3 authorizes
/// proceeding to the airport), the pilot reports it, and the instructor is warned that radar
/// separation must be reassumed.</description></item>
/// <item><description><b>Lost the field, lead held</b> — legal steady state (§7-4-3.c.2 NOTE:
/// a follower never needed the field report); silent on frequency.</description></item>
/// <item><description><b>Lost everything</b> — the visual is no longer legal. Committed
/// (short final at/below <see cref="CommittedAglFt"/>) → go-around with the reason spoken
/// (AIM §5-5-5.a.2). Otherwise → level off, hold present heading, request vectors — AIM
/// §5-5-11.a.5 has the pilot advise ATC immediately and ask when they need to climb (the
/// controller decides), and pilots do not know the MVA, so no self-initiated climb. Either
/// way the clearance is VOIDED so a weather flicker cannot
/// silently resurrect an approach the controller believes is dead (§7-4-1: handled as any
/// go-around; a new clearance must be issued).</description></item>
/// </list>
/// </summary>
internal static class VisualApproachHelper
{
    /// <summary>
    /// Commit gate for the go-around-vs-level-off split: at/below 1000 ft AGL on an
    /// approach-terminal phase, a level-off is no longer a normal maneuver and the aircraft
    /// executes a go-around instead. 1000 ft AGL is standard pattern altitude (AIM §4-3-3)
    /// and the minimum ceiling on which a visual approach may be predicated at all
    /// (7110.65 §7-4-3.b via AIM §5-5-11.b.1).
    /// </summary>
    internal const double CommittedAglFt = 1000.0;

    public static bool IsOnVisualApproach(AircraftState aircraft) =>
        aircraft.Phases?.ActiveApproach?.ApproachId.StartsWith("VIS", StringComparison.Ordinal) == true;

    /// <summary>
    /// Shared handler for a follower losing sight of (or losing) its lead, called by both
    /// detectors so the same loss event gets the same consequence regardless of which
    /// cadence saw it first. Clears the follow; then per the disjunction: field held →
    /// handback (report + instructor warning, visual continues); nothing held on a visual →
    /// the visual ends. Non-visual follows (VFR pattern FOLLOW) keep the historical freeze
    /// behavior: the follower's only outstanding instruction was to follow, so it holds its
    /// present leg awaiting a controller turn.
    /// <paramref name="fieldAlsoLostThisTick"/> names the field in the end-of-visual
    /// transmission when both references were lost in the same evaluation.
    /// </summary>
    public static void HandleTrafficContactLost(PhaseContext ctx, string targetCallsign, bool fieldAlsoLostThisTick)
    {
        var follower = ctx.Aircraft;

        // The pilot just reported losing sight — the in-sight report is consumed with
        // the loss, unlike a command-driven follow cancel (where the pilot still sees
        // the traffic). LastReportedTrafficCallsign survives for a later bare FOLLOW.
        follower.Approach.HasReportedTrafficInSight = false;

        if (!IsOnVisualApproach(follower))
        {
            AirborneFollowHelper.CancelFollowHoldingLeg(follower);
            Pilot.PilotResponder.RouteSoloOrRpoTransmission(
                follower,
                ctx.SoloTrainingMode,
                ctx.RpoShowPilotSpeech,
                ctx.StudentPositionType,
                Pilot.PilotResponder.BuildLostSightOfTraffic(follower, targetCallsign),
                Pilot.PilotResponder.SoloPositionsTowerApproach
            );
            return;
        }

        AirborneFollowHelper.ClearFollowState(follower);

        if (follower.Approach.HasReportedFieldInSight)
        {
            Pilot.PilotResponder.RouteSoloOrRpoTransmission(
                follower,
                ctx.SoloTrainingMode,
                ctx.RpoShowPilotSpeech,
                ctx.StudentPositionType,
                Pilot.PilotResponder.BuildLostSightOfTrafficFieldInSight(follower, targetCallsign),
                Pilot.PilotResponder.SoloPositionsTowerApproach
            );
            follower.PendingWarnings.Add($"{follower.Callsign} visual separation terminated — radar separation required");
            return;
        }

        EndVisualLostReference(ctx, fieldAlsoLostThisTick, true, targetCallsign);
    }

    /// <summary>
    /// End a visual approach whose pilot no longer has any visual reference. Committed →
    /// go-around (spoken reason); otherwise level off on present heading and request vectors.
    /// Both paths void the clearance. <paramref name="lostField"/>/<paramref name="lostTraffic"/>
    /// select the transmission wording (both false = the backstop "don't have the field in
    /// sight" for an aircraft that never held the reference it needed).
    /// </summary>
    public static void EndVisualLostReference(PhaseContext ctx, bool lostField, bool lostTraffic, string? leadCallsign)
    {
        var aircraft = ctx.Aircraft;
        if (aircraft.Phases is not { } phases || aircraft.IsOnGround || phases.CurrentPhase is GoAroundPhase)
        {
            return;
        }

        // BasePhase counts as committed: at/below TPA on base the level-off heading points
        // across the extended centerline into the final approach course — the go-around is
        // the only safe self-initiated maneuver from there.
        bool committed =
            (phases.CurrentPhase is FinalApproachPhase or LandingPhase or HelicopterLandingPhase or LowApproachPhase or BasePhase)
            && ((aircraft.Altitude - ctx.FieldElevation) <= CommittedAglFt);

        if (committed)
        {
            // Trigger routes the transmission and installs the go-around;
            // InstallGoAroundPhases voids the visual clearance.
            GoAroundHelper.Trigger(ctx, Pilot.PilotResponder.BuildGoingAroundLostVisualReference(aircraft, lostField, lostTraffic));
            return;
        }

        var request = Pilot.PilotResponder.BuildUnableVisualRequestVectors(aircraft, lostField, lostTraffic, leadCallsign);
        Pilot.PilotResponder.RouteSoloOrRpoTransmission(
            aircraft,
            ctx.SoloTrainingMode,
            ctx.RpoShowPilotSpeech,
            ctx.StudentPositionType,
            request,
            Pilot.PilotResponder.SoloPositionsTowerApproach
        );
        // The transmission IS an approach request — register it so the proactive
        // "request approach assignment" nag (PilotProactive.TickArrivalApproachRequest)
        // doesn't re-ask seconds later, and so the standard unanswered-request follow-up
        // machinery covers a controller who never responds.
        string? voidedRunwayId = phases.AssignedRunway?.Designator;
        Pilot.PilotRequestTracker.RecordRequest(
            aircraft,
            Pilot.PilotPendingRequestKind.Approach,
            ctx.ScenarioElapsedSeconds,
            request,
            Pilot.PilotRequestContext.Runway(voidedRunwayId, null)
        );

        VoidVisualApproach(aircraft);
        phases.Clear(ctx);

        // Level off at the nearest 100 ft and hold present heading and speed — a plain
        // vectored IFR target awaiting a clearance. Heading mirrors the FPH idiom
        // (FlightCommandHandler); speed is pinned to the current IAS rather than released
        // to the auto schedule, which would accelerate a jet back toward 250 KIAS in the
        // terminal area the moment the approach state cleared.
        aircraft.Targets.TargetAltitude = Math.Round(aircraft.Altitude / 100.0) * 100.0;
        aircraft.Targets.TargetTrueHeading = aircraft.TrueHeading;
        aircraft.Targets.AssignedMagneticHeading = aircraft.MagneticHeading;
        aircraft.Targets.PreferredTurnDirection = null;
        aircraft.Targets.NavigationRoute.Clear();
        aircraft.Targets.TargetSpeed = Math.Round(aircraft.IndicatedAirspeed);
    }

    /// <summary>
    /// Void a visual approach clearance: the aircraft is back on a bare IFR clearance with no
    /// approach authorization (7110.65 §7-4-1), so the acquisition/maintain machinery keyed on
    /// the VIS clearance disarms and a fresh CVA is required to resume. The runway identity
    /// (phase-list AssignedRunway AND Procedure.DestinationRunway — the same fact, and the one
    /// strips/datablocks display) is deliberately kept: the controller usually re-clears the
    /// same runway. A landing clearance — issued or pre-armed — cannot outlive the approach
    /// clearance it rode on.
    /// </summary>
    public static void VoidVisualApproach(AircraftState aircraft)
    {
        if (aircraft.Phases is { } phases)
        {
            phases.ActiveApproach = null;
            phases.LandingClearance = null;
            phases.ClearedRunwayId = null;
        }

        aircraft.Approach.HasReportedFieldInSight = false;
        aircraft.Approach.HasReportedTrafficInSight = false;
        AirborneFollowHelper.ClearFollowState(aircraft);
        aircraft.PendingObservations.RemoveAll(o => o is FieldAcquisitionObservation or TrafficAcquisitionObservation);
        aircraft.Pattern.PendingLandingClearance = null;
    }
}
