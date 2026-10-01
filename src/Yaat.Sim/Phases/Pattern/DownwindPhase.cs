using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Pattern;

/// <summary>
/// Downwind leg: fly opposite runway heading at pattern altitude.
/// Maintains downwind speed, level flight.
/// Completes when reaching the base turn waypoint.
/// </summary>
public sealed class DownwindPhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("DownwindPhase");

    /// <summary>Along-track slop (nm) the abeam and base-turn triggers are armed with; shared with the
    /// callers that have to decide the same thresholds from outside the phase.</summary>
    public const double AlongTrackToleranceNm = 0.3;

    /// <summary>
    /// Along-track lead (nm) for the midfield point — the crossover exit, the midfield-downwind report,
    /// and the caller that decides whether there is any downwind left to fly before the crossover.
    /// Much tighter than <see cref="AlongTrackToleranceNm"/>: along-track grows monotonically along the
    /// leg, so a small lead cannot miss the trigger, while the turn-anticipation slop the abeam and
    /// base-turn triggers need starts the crossover 0.15 nm into a 0.9 nm runway's downwind (OAK 28R) —
    /// short of the midfield the aircraft was told to cross at.
    /// </summary>
    public const double MidfieldLeadNm = 0.05;

    // Downwind-track re-intercept. After a wrong-side / cross-runway MidfieldCrossing join the aircraft
    // can be left off the computed downwind line (e.g. dropped inside its own pattern); steer back onto
    // it rather than holding the wrong offset — which would make the base/final geometry (built for the
    // computed pattern width) turn early and overshoot onto the far side / a parallel runway. No-op once
    // established on the line (cross-track ≈ 0), so a normally-established downwind is unaffected.
    private const double DownwindTrackToleranceNm = 0.03;
    private const double DownwindInterceptGainDegPerNm = 200.0;
    private const double MaxDownwindInterceptDeg = 45.0;

    // Past-abeam descent planning: the shortest remaining downwind the line descent is spread over (a release right at
    // the base trigger), and the ground speed floor that keeps the planned rate finite for a slow or stationary aircraft.
    private const double MinDescentLegNm = 0.05;
    private const double MinDescentPlanningGroundSpeedKt = 60.0;

    private double _baseTurnAlongTrack;
    private double _abeamAlongTrack;
    private double _midfieldAlongTrack;
    private double _thresholdLat;
    private double _thresholdLon;
    private TrueHeading _downwindHeading;
    private bool _pastAbeam;

    // Held level past abeam (null = no hold latched).
    private double? _holdLevelFt;

    // The line descent's fixed altitude at the base trigger, re-planned toward every unheld tick (null = no line descent).
    private double? _descentTargetFt;

    // A controller altitude (CM/DM) issued during this leg; once set it owns the leg's altitude (AIM 4-4-10.a).
    private double? _controllerAltitudeFt;

    // The assigned altitude last seen, recorded at the leg's start; a change is a new controller assignment.
    private double? _seenAssignedAltitudeFt;

    // Restored from a recording without the baseline above: the next tick takes the assignment in force as the baseline.
    private bool _assignedAltitudeBaselinePending;
    private bool _midfieldBroadcastIssued;
    private bool _followExtensionWarningIssued;

    public PatternWaypoints? Waypoints { get; set; }

    /// <summary>
    /// If true, the downwind leg is extended beyond the normal base turn point.
    /// Aircraft continues on downwind heading until told to turn base (TB command).
    /// </summary>
    public bool IsExtended { get; set; }

    /// <summary>
    /// If true, an SA (short approach) was armed before this leg activated.
    /// On the first tick after activation, the phase completes immediately so the
    /// PhaseList advances to BasePhase — mirroring the on-Downwind semantics of
    /// <see cref="PatternCommandHandler.TryMakeShortApproach"/>.
    /// </summary>
    public bool ShortApproachArmed { get; set; }

    /// <summary>
    /// If true, the leg re-intercepts the computed downwind track when the aircraft is off it. Set only
    /// for a downwind entered from a wrong-side / cross-runway <see cref="MidfieldCrossingPhase"/> join,
    /// which can drop the aircraft inside its own pattern; a normally-established downwind is already on
    /// the track, so this stays false there to leave that flow untouched.
    /// </summary>
    public bool RejoinTrack { get; set; }

    /// <summary>
    /// If true, the leg ends at midfield (<see cref="PatternGeometry.MidfieldAlongTrackNm"/> — the same
    /// point the midfield broadcast uses) instead of at the base turn, and never starts the past-abeam
    /// descent. Set for the first leg of an in-pattern crossover to a parallel runway: the aircraft
    /// flies its current downwind up to midfield, then turns across the field at pattern altitude.
    /// </summary>
    public bool ExitAtMidfield { get; set; }

    /// <summary>
    /// Active lateral offset state set by OFL/OFR. While non-null, OnTick overrides
    /// <c>TargetTrueHeading</c> via <see cref="PatternLateralOffsetHelper"/> to
    /// dogleg perpendicular to the leg, then hold a parallel track once acquired.
    /// Discarded when the phase completes — no carry-over into BasePhase.
    /// </summary>
    public PatternLateralOffsetState? LateralOffset { get; set; }

    public override string Name => "Downwind";
    public override bool ManagesSpeed => true;

    public override void OnStart(PhaseContext ctx)
    {
        if (Waypoints is null)
        {
            return;
        }

        PatternReportHelper.EmitTurningLeg(ctx, ReportTrigger.Downwind);

        _thresholdLat = Waypoints.ThresholdLat;
        _thresholdLon = Waypoints.ThresholdLon;
        _downwindHeading = Waypoints.DownwindHeading;

        _pastAbeam = false;
        _holdLevelFt = null;
        _descentTargetFt = null;
        _controllerAltitudeFt = null;
        _seenAssignedAltitudeFt = ctx.Targets.AssignedAltitude;
        _assignedAltitudeBaselinePending = false;
        _midfieldBroadcastIssued = false;

        _abeamAlongTrack = GeoMath.AlongTrackDistanceNm(
            Waypoints.DownwindAbeamLat,
            Waypoints.DownwindAbeamLon,
            _thresholdLat,
            _thresholdLon,
            _downwindHeading
        );

        _baseTurnAlongTrack = GeoMath.AlongTrackDistanceNm(
            Waypoints.BaseTurnLat,
            Waypoints.BaseTurnLon,
            _thresholdLat,
            _thresholdLon,
            _downwindHeading
        );

        _midfieldAlongTrack = PatternGeometry.MidfieldAlongTrackNm(Waypoints);

        // Short approach armed before activation — compress the past-abeam extension
        // so the base turn fires near abeam-the-threshold instead of after the normal
        // category extension. AIM 4-3-3 lets pilots vary pattern size; the shrunk
        // extension keeps geometry sane (no teleport, base turn is still discrete).
        if (ShortApproachArmed)
        {
            _baseTurnAlongTrack = _abeamAlongTrack + CategoryPerformance.ShortApproachBaseExtensionNm(ctx.Category);
        }

        ctx.Targets.TargetTrueHeading = Waypoints.DownwindHeading;
        ctx.Targets.PreferredTurnDirection = null;
        if (!ctx.Targets.HasExplicitTurnRate)
        {
            ctx.Targets.TurnRateOverride = CategoryPerformance.PatternTurnRate(ctx.Category);
        }
        ctx.Targets.NavigationRoute.Clear();

        if (ShortApproachArmed)
        {
            // Pilot aware of upcoming short approach — start descending immediately
            // rather than waiting for abeam. Real pilots issued an SA earlier than
            // the leg begin descent on crosswind/early-downwind so the GS-intercept
            // altitude is reached by the (compressed) base-turn point. Mark _pastAbeam
            // so OnTick's normal abeam-trigger doesn't re-overwrite the targets.
            _pastAbeam = true;
            double aircraftAlongTrack = GeoMath.AlongTrackDistanceNm(
                ctx.Aircraft.Position,
                new LatLon(_thresholdLat, _thresholdLon),
                _downwindHeading
            );
            ApplyPastAbeamDescentTargets(ctx, aircraftAlongTrack);
        }
        else
        {
            // Target pattern altitude. If still above TPA (e.g., from a high pattern entry),
            // continue descending at the pattern rate instead of using the slower default.
            ctx.Targets.TargetAltitude = Waypoints.PatternAltitude;
            ctx.Targets.DesiredVerticalRate =
                (ctx.Aircraft.Altitude > Waypoints.PatternAltitude + 100) ? -CategoryPerformance.PatternDescentRate(ctx.Category) : null;
        }

        // Downwind speed (per-type if available). A controller speed assignment outranks the leg
        // baseline — per 7110.65 §5-7-4 only the controller terminates a speed adjustment, so reaching
        // the next leg must not revert it.
        if (!ctx.Targets.HasExplicitSpeedCommand)
        {
            ctx.Targets.TargetSpeed = AircraftPerformance.DownwindSpeed(ctx.AircraftType, ctx.Category);
        }

        Log.LogDebug(
            "[Downwind] {Callsign}: started, hdg={Hdg:F0}, patternAlt={Alt:F0}ft, extended={Ext}",
            ctx.Aircraft.Callsign,
            Waypoints.DownwindHeading.Degrees,
            Waypoints.PatternAltitude,
            IsExtended
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        // Lead-not-found / lead-on-ground / runaway-distance watchdog. Clears
        // FollowingCallsign + emits the appropriate pilot transmission so a
        // pattern-phase follower doesn't keep a stale follow target after the
        // lead despawns or lands. A cancel can now also end a visual approach
        // outright (VisualApproachHelper), replacing or clearing this phase list
        // mid-tick — bail out of the tick when it fires.
        if (AirborneFollowHelper.CheckLeadLifecycle(ctx))
        {
            return false;
        }

        // OFL/OFR lateral dogleg + parallel hold. Reference point must be ON the
        // downwind track (not the runway centerline) — abeam-the-threshold is the
        // canonical on-track waypoint. Runs every tick while active so the heading
        // target tracks acquisition; downstream completion logic (abeam, base-turn)
        // uses along-track distance and is unaffected by the perpendicular offset.
        if (LateralOffset is not null && Waypoints is not null)
        {
            ctx.Targets.TargetTrueHeading = PatternLateralOffsetHelper.ComputeTargetHeading(
                ctx,
                _downwindHeading,
                new LatLon(Waypoints.DownwindAbeamLat, Waypoints.DownwindAbeamLon),
                LateralOffset
            );
        }
        else if (RejoinTrack && Waypoints is not null)
        {
            // Re-intercept the computed downwind line (through the abeam point, on the downwind heading)
            // when the aircraft is off it — turn toward the line with a bounded intercept angle,
            // decreasing to zero as it re-establishes. See DownwindTrackToleranceNm above.
            double xtk = GeoMath.SignedCrossTrackDistanceNm(
                ctx.Aircraft.Position,
                new LatLon(Waypoints.DownwindAbeamLat, Waypoints.DownwindAbeamLon),
                _downwindHeading
            );
            if (Math.Abs(xtk) > DownwindTrackToleranceNm)
            {
                double intercept = Math.Min(MaxDownwindInterceptDeg, Math.Abs(xtk) * DownwindInterceptGainDegPerNm);
                double corrected = _downwindHeading.Degrees + (xtk > 0 ? -intercept : intercept);
                ctx.Targets.TargetTrueHeading = new TrueHeading(corrected);
            }
            else
            {
                ctx.Targets.TargetTrueHeading = _downwindHeading;
            }
        }

        double aircraftAlongTrack = GeoMath.AlongTrackDistanceNm(ctx.Aircraft.Position, new LatLon(_thresholdLat, _thresholdLon), _downwindHeading);

        // Crossover exit: hand off at midfield, before any descent or base-turn logic. The aircraft
        // is leaving this downwind for the parallel runway's, so it holds pattern altitude and never
        // starts the past-abeam descent toward a base turn it will not fly.
        if (ExitAtMidfield && (aircraftAlongTrack >= _midfieldAlongTrack - MidfieldLeadNm))
        {
            Log.LogDebug("[Downwind] {Callsign}: midfield reached, exiting downwind for the crossover", ctx.Aircraft.Callsign);
            return true;
        }

        // Midfield downwind broadcast: remind controller if no landing clearance.
        // Solo-training VFR pattern aircraft voice the reminder as delayed pilot speech.
        // RPO mode keeps the controller-facing warning (PendingWarnings).
        // An extended downwind (EXT) is itself a controller sequencing instruction —
        // the aircraft is being actively managed, so the "uncleared" nag is suppressed.
        if (!_midfieldBroadcastIssued && !ctx.AutoClearedToLand)
        {
            if (aircraftAlongTrack >= _midfieldAlongTrack - MidfieldLeadNm)
            {
                _midfieldBroadcastIssued = true;
                if (!HasLandingClearance(ctx) && !IsExtended)
                {
                    string runwayId = RunwayIdentifier.ToDisplayDesignator(ctx.Runway?.Designator ?? "unknown");
                    if (ctx.SoloTrainingMode && ctx.Aircraft.FlightPlan.IsVfr)
                    {
                        PilotResponder.QueueSoloPilotTransmission(
                            ctx.Aircraft,
                            PilotResponder.BuildMidfieldDownwindReminder(ctx.Aircraft, runwayId),
                            PilotTransmissionKind.Proactive,
                            PilotResponder.SourceResponse
                        );
                    }
                    else
                    {
                        PilotResponder.RouteRpoTransmission(
                            ctx.Aircraft,
                            ctx.SoloTrainingMode,
                            ctx.RpoShowPilotSpeech,
                            PilotResponder.BuildMidfieldDownwindReminder(ctx.Aircraft, runwayId).Tts,
                            $"{ctx.Aircraft.Callsign} midfield downwind runway {runwayId}"
                        );
                    }
                }
            }
        }

        // Abeam the approach end: pattern altitude is held to here (AC 90-66B §11.5), so the test has
        // no along-track slop. The descent itself starts below, once the hold decision for this tick is known.
        bool reachedAbeamThisTick = false;
        if (!_pastAbeam && (Waypoints is not null) && (aircraftAlongTrack >= _abeamAlongTrack))
        {
            _pastAbeam = true;
            reachedAbeamThisTick = true;
            Log.LogDebug("[Downwind] {Callsign}: abeam threshold", ctx.Aircraft.Callsign);

            // Begin decelerating toward base speed, unless the controller assigned a speed
            // (7110.65 §5-7-4).
            if (!ctx.Targets.HasExplicitSpeedCommand)
            {
                ctx.Targets.TargetSpeed = AircraftPerformance.BaseSpeed(ctx.AircraftType, ctx.Category);
            }
        }

        // Follow speed adjustment: modulate speed based on distance to leader.
        // Feed the phase baseline (not the previous tick's adjusted target) into the
        // helper — otherwise the +MaxSpeedAdjustKts clamp compounds each tick and
        // lets IAS escape the stabilized-approach gate downstream. Gate on the follow
        // target, NOT on TargetSpeed: physics snaps TargetSpeed to null once the leg
        // speed is reached, so gating on it silently stops spacing for a settled follower.
        if (ctx.Aircraft.Approach.FollowingCallsign is not null)
        {
            double baseline = _pastAbeam
                ? AircraftPerformance.BaseSpeed(ctx.AircraftType, ctx.Category)
                : AircraftPerformance.DownwindSpeed(ctx.AircraftType, ctx.Category);
            double minSpeed = AircraftPerformance.ApproachSpeed(ctx.AircraftType, ctx.Category);
            double? adjusted = AirborneFollowHelper.GetAdjustedSpeed(ctx, baseline, minSpeed, AirborneFollowHelper.MaxSpeedAdjustKts);
            if (adjusted is not null)
            {
                // Spacing only ever SLOWS the follower below the leg baseline; a too-far
                // lead is handled laterally (extend/hold base turn), not by accelerating.
                ctx.Targets.TargetSpeed = Math.Min(adjusted.Value, baseline);
            }
        }

        TrackControllerAltitude(ctx);

        // An extended downwind (EXT; 7110.65 §3-8-1 "EXTEND DOWNWIND", with "tower will call your base" in the
        // examples at §3-9-4 and §3-10-5) waits for the TB command. Otherwise hold the base turn if
        // following traffic and either (a) too close to a same-leg leader [proximity], (b) a pattern-flow-ahead leader is still forward of
        // the 3-9 line — never bypassed, a base turn now would cut in front of it (14 CFR
        // §91.113(g)) — or (c) turning base now would put the follower over the threshold
        // before the leader is clear of the runway / too tightly behind it on final
        // [sequencing, judged by projected ETAs]. A deliberate short approach waives (c)
        // only: the controller has taken the spacing, not the prohibition on cutting in.
        //
        // A follower does NOT turn base on its own to escape the hold: it keeps flying
        // the downwind until it is genuinely sequenced behind (the hold clears) or the
        // controller issues a turn. Past MaxFollowExtensionNm it advises once ("extending
        // downwind … unable to turn") so the controller can re-sequence, then keeps going.
        bool followHold = !IsExtended && IsFollowHoldInForce(ctx);
        if (IsExtended || followHold)
        {
            if (followHold)
            {
                AdviseFollowExtensionOnce(ctx, aircraftAlongTrack);
            }

            HoldLevelPastAbeam(ctx);
            return false;
        }

        // Start the descent at abeam, or on the release of a hold that kept the aircraft level past abeam; re-plan
        // its rate every other unheld tick. A release at or past the base trigger leaves the descent to BasePhase,
        // which plans it from there, and a controller altitude issued during the leg is never descended away from.
        bool baseTriggerReached = aircraftAlongTrack >= _baseTurnAlongTrack - AlongTrackToleranceNm;
        if (_pastAbeam && (reachedAbeamThisTick || (_holdLevelFt is not null)))
        {
            _holdLevelFt = null;
            if (!baseTriggerReached && (_controllerAltitudeFt is null))
            {
                Log.LogDebug("[Downwind] {Callsign}: beginning descent, alt={Alt:F0}ft", ctx.Aircraft.Callsign, ctx.Aircraft.Altitude);
                ApplyPastAbeamDescentTargets(ctx, aircraftAlongTrack);
            }
        }
        else if (!baseTriggerReached)
        {
            ReplanLineDescent(ctx, aircraftAlongTrack);
        }

        if (baseTriggerReached)
        {
            Log.LogDebug("[Downwind] {Callsign}: base turn point reached, alt={Alt:F0}ft", ctx.Aircraft.Callsign, ctx.Aircraft.Altitude);
        }

        return baseTriggerReached;
    }

    private bool IsFollowHoldInForce(PhaseContext ctx)
    {
        bool holdForProximity = AirborneFollowHelper.ShouldExtendDownwind(ctx);
        bool leadStillForward = AirborneFollowHelper.IsFlowAheadLeadForwardOfWingline(ctx);
        bool wantsSequenceHold =
            leadStillForward || (!ShortApproachArmed && Waypoints is not null && AirborneFollowHelper.ShouldHoldForLeadSequencing(ctx, Waypoints));
        return holdForProximity || wantsSequenceHold;
    }

    private void AdviseFollowExtensionOnce(PhaseContext ctx, double aircraftAlongTrack)
    {
        bool pastExtensionCap = aircraftAlongTrack >= _baseTurnAlongTrack + AirborneFollowHelper.MaxFollowExtensionNm;
        if (pastExtensionCap && !_followExtensionWarningIssued && (ctx.Aircraft.Approach.FollowingCallsign is { } followTarget))
        {
            PilotResponder.RouteSoloOrRpoTransmission(
                ctx.Aircraft,
                ctx.SoloTrainingMode,
                ctx.RpoShowPilotSpeech,
                ctx.StudentPositionType,
                PilotResponder.BuildFollowExtendingUnableToTurn(ctx.Aircraft, followTarget, "downwind"),
                PilotResponder.SoloPositionsTowerApproach
            );
            _followExtensionWarningIssued = true;
        }
    }

    /// <summary>
    /// A controller altitude wins over the pattern profile (AIM 4-4-10.a). A change of <see cref="ControlTargets.AssignedAltitude"/>
    /// since the leg started is a CM/DM issued on this downwind: it becomes the leg's altitude, a latched hold re-latches at
    /// it, and the line descent stops re-planning so the controller's target stands. An assignment already in force when the
    /// leg started is not one: the leg targets pattern altitude from its start, as before any hold.
    /// </summary>
    private void TrackControllerAltitude(PhaseContext ctx)
    {
        double? assigned = ctx.Targets.AssignedAltitude;
        if (_assignedAltitudeBaselinePending)
        {
            _seenAssignedAltitudeFt = assigned;
            _assignedAltitudeBaselinePending = false;
            return;
        }

        if (assigned == _seenAssignedAltitudeFt)
        {
            return;
        }

        _seenAssignedAltitudeFt = assigned;
        if (assigned is not { } controllerAltitudeFt)
        {
            return;
        }

        _controllerAltitudeFt = controllerAltitudeFt;
        _descentTargetFt = null;
        ctx.Targets.DesiredVerticalRate = null;
        if (_holdLevelFt is not null)
        {
            _holdLevelFt = controllerAltitudeFt;
        }

        Log.LogDebug("[Downwind] {Callsign}: controller altitude {Alt:F0}ft owns the leg", ctx.Aircraft.Callsign, controllerAltitudeFt);
    }

    /// <summary>
    /// A held downwind past abeam flies level: a hold in force at abeam keeps pattern altitude, and one that begins
    /// mid-descent levels off where it is; a controller altitude issued on this leg is the level instead. The level is
    /// latched on the first held tick and only a new controller altitude moves it, so the pattern never ratchets it and
    /// never commands a climb back up the pattern. Before abeam the leg already targets pattern altitude.
    /// </summary>
    private void HoldLevelPastAbeam(PhaseContext ctx)
    {
        if (!_pastAbeam || (Waypoints is null))
        {
            return;
        }

        if (_holdLevelFt is null)
        {
            _holdLevelFt = _controllerAltitudeFt ?? Math.Min(ctx.Aircraft.Altitude, Waypoints.PatternAltitude);
            Log.LogDebug("[Downwind] {Callsign}: downwind held, level at {Alt:F0}ft", ctx.Aircraft.Callsign, _holdLevelFt);
        }

        _descentTargetFt = null;
        ctx.Targets.TargetAltitude = _holdLevelFt;
        ctx.Targets.DesiredVerticalRate = null;
    }

    /// <summary>
    /// Re-plans the line descent's rate toward its fixed altitude at the base trigger from where the aircraft is now, so
    /// the flown profile stays on the line as the aircraft slows to base speed. Stops once the aircraft is at that altitude.
    /// </summary>
    private void ReplanLineDescent(PhaseContext ctx, double aircraftAlongTrack)
    {
        if (_descentTargetFt is not { } targetFt)
        {
            return;
        }

        double deltaFt = ctx.Aircraft.Altitude - targetFt;
        if (deltaFt <= 0)
        {
            _descentTargetFt = null;
            return;
        }

        ctx.Targets.TargetAltitude = targetFt;
        ctx.Targets.DesiredVerticalRate = -LineDescentRateFpm(ctx, aircraftAlongTrack, deltaFt);
    }

    /// <summary>The rate that loses <paramref name="deltaFt"/> over the downwind left to the base trigger at the present
    /// ground speed, capped at the descent ceiling <see cref="BasePhase"/> plans the base with.</summary>
    private double LineDescentRateFpm(PhaseContext ctx, double aircraftAlongTrack, double deltaFt)
    {
        double remainingDwNm = Math.Max(_baseTurnAlongTrack - AlongTrackToleranceNm - aircraftAlongTrack, MinDescentLegNm);
        double groundSpeedKt = Math.Max(ctx.Aircraft.GroundSpeed, MinDescentPlanningGroundSpeedKt);
        double timeMinToBaseTrigger = remainingDwNm / (groundSpeedKt / 60.0);
        return Math.Min(deltaFt / timeMinToBaseTrigger, BasePhase.MaxDescentRateFpm(groundSpeedKt, ctx.Category));
    }

    /// <summary>
    /// Compress the base-turn target so the aircraft turns base from its current
    /// position rather than continuing to the normal category extension. Called by
    /// <see cref="PatternCommandHandler.TryMakeShortApproach"/> when SA is issued
    /// while this leg is already active. The aircraft rolls into base via the normal
    /// turn-rate / bank logic on the next tick — no teleport (AIM 4-3-5 forbids
    /// abrupt unexpected maneuvers). Also lowers the descent target / steepens the
    /// rate so the altitude profile lines up with the compressed final-approach
    /// length (Jet 1.5 nm, Piston 0.5 nm — see <see cref="CategoryPerformance.MinShortApproachFinalNm"/>).
    /// </summary>
    public void ApplyShortApproach(PhaseContext ctx)
    {
        ShortApproachArmed = true;

        if (Waypoints is null)
        {
            return;
        }

        double currentAlongTrack = GeoMath.AlongTrackDistanceNm(ctx.Aircraft.Position, new LatLon(_thresholdLat, _thresholdLon), _downwindHeading);

        double compressedExtension = _abeamAlongTrack + CategoryPerformance.ShortApproachBaseExtensionNm(ctx.Category);

        // Take the further of the two so the aircraft never reverses backward to a
        // base turn point it has already passed: clamp to current along-track.
        double newBaseTurn = Math.Max(compressedExtension, currentAlongTrack);
        if (newBaseTurn < _baseTurnAlongTrack)
        {
            _baseTurnAlongTrack = newBaseTurn;
        }

        // If past abeam (descent already started), recompute targets so the
        // altitude profile reflects the compressed geometry. Mid-leg SA implies
        // a steeper descent to make the new base-turn altitude. A held leg stays
        // level; its release plans the descent.
        if (_pastAbeam && (_holdLevelFt is null))
        {
            ApplyPastAbeamDescentTargets(ctx, currentAlongTrack);
        }
    }

    /// <summary>
    /// Reverse <see cref="ApplyShortApproach"/> by restoring the original base-turn
    /// along-track from <see cref="Waypoints"/>. Called by MNA. If the aircraft has
    /// already passed the original base-turn point under SA, the restored value sits
    /// behind the aircraft — OnTick still reports completion on the next tick, which
    /// is the right behavior (you can't un-shorten an already-flown pattern).
    /// </summary>
    public void RemoveShortApproach(PhaseContext ctx)
    {
        ShortApproachArmed = false;

        if (Waypoints is null)
        {
            return;
        }

        _baseTurnAlongTrack = GeoMath.AlongTrackDistanceNm(
            Waypoints.BaseTurnLat,
            Waypoints.BaseTurnLon,
            _thresholdLat,
            _thresholdLon,
            _downwindHeading
        );

        // A held leg stays level and a controller altitude stands; neither plans the line descent here.
        if (_pastAbeam && (_holdLevelFt is null) && (_controllerAltitudeFt is null))
        {
            double currentAlongTrack = GeoMath.AlongTrackDistanceNm(
                ctx.Aircraft.Position,
                new LatLon(_thresholdLat, _thresholdLon),
                _downwindHeading
            );
            ApplyPastAbeamDescentTargets(ctx, currentAlongTrack);
        }
    }

    /// <summary>
    /// True once <paramref name="position"/> has reached or passed this leg's base-turn
    /// point along the downwind axis — the same threshold <see cref="OnTick"/> uses to
    /// complete the leg. An aircraft still on the downwind past this point is holding the
    /// leg out (a controller EXT, its own follow-hold, or a proximity/sequence hold) rather
    /// than progressing, so it has deferred its base turn and stays ahead in the landing
    /// sequence. Used to sequence a same-leg follower behind such a lead. Returns false when
    /// the leg's geometry is not yet initialized (<see cref="Waypoints"/> unset), so an
    /// uninitialized phase never reads as "past base turn".
    /// </summary>
    public bool HasReachedBaseTurnPoint(LatLon position) =>
        Waypoints is not null
        && GeoMath.AlongTrackDistanceNm(position, new LatLon(_thresholdLat, _thresholdLon), _downwindHeading)
            >= _baseTurnAlongTrack - AlongTrackToleranceNm;

    /// <summary>
    /// Computes the descent target and vertical rate for the past-abeam descent and writes them
    /// onto <paramref name="ctx"/>. Branches on <see cref="ShortApproachArmed"/>: a normal pattern
    /// plans the line descent (<see cref="StartLineDescent"/>); SA uses the GS-intercept altitude
    /// implied by <see cref="CategoryPerformance.MinShortApproachFinalNm"/> with a steeper rate
    /// derived from the remaining downwind distance and current ground speed. Called at abeam and on
    /// a hold's release (OnTick) and on a live SA/MNA (Apply/RemoveShortApproach).
    /// </summary>
    private void ApplyPastAbeamDescentTargets(PhaseContext ctx, double aircraftAlongTrack)
    {
        if (Waypoints is null)
        {
            return;
        }

        if (!ShortApproachArmed)
        {
            StartLineDescent(ctx, Waypoints, aircraftAlongTrack);
            return;
        }

        _descentTargetFt = null;
        double thresholdElev = ctx.Runway?.ElevationFt ?? ctx.FieldElevation;
        double patternSize = Waypoints.PatternSizeNm;
        double gsAngle = GlideSlopeGeometry.AngleForCategory(ctx.Category);
        double baseDescentRate = CategoryPerformance.PatternDescentRate(ctx.Category);

        // Compressed final length → base-turn altitude is the GS intercept
        // altitude implied by sqrt(patternSize² + finalLen²).
        double finalLen = CategoryPerformance.MinShortApproachFinalNm(ctx.Category);
        double diagonalNm = Math.Sqrt(patternSize * patternSize + finalLen * finalLen);
        double midAlt = thresholdElev + diagonalNm * GlideSlopeGeometry.FeetPerNm(gsAngle);

        // Required rate to lose the altitude delta over the remaining distance
        // to the base-turn point. Clamped at the category default (won't be slower
        // than normal) and at 1500 fpm (descent limit before "unable, too high").
        double deltaAlt = Math.Max(ctx.Aircraft.Altitude - midAlt, 0);
        double distToBaseTurnNm = Math.Max(_baseTurnAlongTrack - aircraftAlongTrack, MinDescentLegNm);
        double groundSpeedKt = Math.Max(ctx.Aircraft.GroundSpeed, MinDescentPlanningGroundSpeedKt);
        double timeMinToBaseTurn = distToBaseTurnNm / (groundSpeedKt / 60.0);
        double computedRate = timeMinToBaseTurn > 0 ? deltaAlt / timeMinToBaseTurn : baseDescentRate;
        double descentRate = Math.Clamp(computedRate, baseDescentRate, 1500);

        ctx.Targets.TargetAltitude = midAlt;
        ctx.Targets.DesiredVerticalRate = -descentRate;
    }

    /// <summary>
    /// Plans the downwind's share of the descent to the base-to-final rollout. Pattern altitude is held to abeam the
    /// approach end (AIM FIG 4-3-2 key 2; AC 90-66B §11.5), and AC 90-66B Appendix A key 2 begins the descent there and
    /// turns base at approximately 45 degrees from the intended landing point; AIM FIG 4-3-2 key 3 and AC 90-66B
    /// Appendix A key 3 only require the turn to final to be complete at least 1/4 mile out. Two simulation modelling
    /// choices, not FAA text, fill the rest. The downwind and the base descend at one ground gradient, along one straight
    /// line from the aircraft's present altitude and position to the 3° glidepath altitude at the rollout point
    /// <see cref="BasePhase"/> plans its own descent to (one turn radius beyond the base trigger's distance out, floored
    /// at one turn radius). And the turn to final is flown level at that rollout altitude: <see cref="BasePhase"/> reaches
    /// it where its leg ends, at the start of the turn to final. So the base's share of the line is the distance it
    /// descends over: the downwind-to-base arc plus the straight base to one turn radius from the final centerline. The
    /// downwind targets the line's altitude at the base trigger. Never above the current altitude: an aircraft already at
    /// or below the rollout altitude holds rather than climbs.
    /// </summary>
    private void StartLineDescent(PhaseContext ctx, PatternWaypoints waypoints, double aircraftAlongTrack)
    {
        double thresholdElev = ctx.Runway?.ElevationFt ?? ctx.FieldElevation;
        double turnRadiusNm = BasePhase.TurnRadiusNm(BasePhase.PlannedSpeedKt(ctx.Aircraft, ctx.Category), ctx.Category);
        double rolloutDistNm = Math.Max(_baseTurnAlongTrack - AlongTrackToleranceNm, turnRadiusNm) + turnRadiusNm;
        double rolloutAlt = GlideSlopeGeometry.AltitudeAtDistance(rolloutDistNm, thresholdElev, ctx.Category);
        double currentAlt = ctx.Aircraft.Altitude;
        if (currentAlt <= rolloutAlt)
        {
            _descentTargetFt = null;
            ctx.Targets.TargetAltitude = currentAlt;
            ctx.Targets.DesiredVerticalRate = -CategoryPerformance.PatternDescentRate(ctx.Category);
            return;
        }

        double baseLenNm = BaseDescentLengthNm(ctx.Aircraft.Position, waypoints.FinalHeading, turnRadiusNm);
        double remainingDwNm = Math.Max(_baseTurnAlongTrack - AlongTrackToleranceNm - aircraftAlongTrack, MinDescentLegNm);
        double targetFt = rolloutAlt + ((currentAlt - rolloutAlt) * baseLenNm / (remainingDwNm + baseLenNm));
        _descentTargetFt = targetFt;
        ctx.Targets.TargetAltitude = targetFt;
        ctx.Targets.DesiredVerticalRate = -LineDescentRateFpm(ctx, aircraftAlongTrack, currentAlt - targetFt);
    }

    /// <summary>
    /// The distance <see cref="BasePhase"/> descends over from a base turned at <paramref name="position"/>'s distance from
    /// the final centerline: a 90° downwind-to-base arc of one turn radius, which closes the centerline by that radius,
    /// then the straight base until the leg ends one turn radius from the centerline, where the turn to final starts.
    /// </summary>
    private double BaseDescentLengthNm(LatLon position, TrueHeading finalHeading, double turnRadiusNm)
    {
        double crossTrackNm = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(position, new LatLon(_thresholdLat, _thresholdLon), finalHeading));
        return (Math.PI / 2.0 * turnRadiusNm) + Math.Max(crossTrackNm - (2.0 * turnRadiusNm), 0);
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        // Speed and altitude adjustments are additive — they retarget without
        // breaking the pattern leg.
        if (IsAdditiveAirborneAdjustment(cmd))
        {
            return CommandAcceptance.Allowed;
        }

        return cmd switch
        {
            CanonicalCommandType.ClearedToLand => CommandAcceptance.Allowed,
            CanonicalCommandType.ForceLanding => CommandAcceptance.Allowed,
            CanonicalCommandType.LandAndHoldShort => CommandAcceptance.Allowed,
            CanonicalCommandType.ClearedForOption => CommandAcceptance.Allowed,
            CanonicalCommandType.GoAround => CommandAcceptance.Allowed,
            CanonicalCommandType.Follow => CommandAcceptance.Allowed,
            CanonicalCommandType.MakeShortApproach => CommandAcceptance.Allowed,
            CanonicalCommandType.MakeNormalApproach => CommandAcceptance.Allowed,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.ClearsPhase,
        };
    }

    public override PhaseDto ToSnapshot() =>
        new DownwindPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = Requirements.Count > 0 ? [.. Requirements.Select(r => r.ToSnapshot())] : null,
            Waypoints = Waypoints?.ToSnapshot(),
            IsExtended = IsExtended,
            BaseTurnAlongTrack = _baseTurnAlongTrack,
            AbeamAlongTrack = _abeamAlongTrack,
            MidfieldAlongTrack = _midfieldAlongTrack,
            ThresholdLat = _thresholdLat,
            ThresholdLon = _thresholdLon,
            DownwindHeadingDeg = _downwindHeading.Degrees,
            PastAbeam = _pastAbeam,
            HoldLevelFt = _holdLevelFt,
            DescentTargetFt = _descentTargetFt,
            ControllerAltitudeFt = _controllerAltitudeFt,
            SeenAssignedAltitudeFt = _seenAssignedAltitudeFt,
            AssignedAltitudeBaselineRecorded = !_assignedAltitudeBaselinePending,
            MidfieldBroadcastIssued = _midfieldBroadcastIssued,
            ShortApproachArmed = ShortApproachArmed,
            RejoinTrack = RejoinTrack,
            ExitAtMidfield = ExitAtMidfield,
            LateralOffsetTargetNm = LateralOffset?.TargetNm,
            LateralOffsetDirection = LateralOffset is not null ? (int)LateralOffset.Direction : null,
            LateralOffsetAcquired = LateralOffset?.Acquired ?? false,
            FollowExtensionWarningIssued = _followExtensionWarningIssued,
        };

    public static DownwindPhase FromSnapshot(DownwindPhaseDto dto)
    {
        var phase = new DownwindPhase
        {
            Waypoints = dto.Waypoints is not null ? PatternWaypoints.FromSnapshot(dto.Waypoints) : null,
            IsExtended = dto.IsExtended,
            ShortApproachArmed = dto.ShortApproachArmed,
            RejoinTrack = dto.RejoinTrack ?? false,
            ExitAtMidfield = dto.ExitAtMidfield ?? false,
            LateralOffset = dto.LateralOffsetTargetNm is { } target
                ? new PatternLateralOffsetState
                {
                    TargetNm = target,
                    Direction = (TurnDirection)(dto.LateralOffsetDirection ?? 0),
                    Acquired = dto.LateralOffsetAcquired,
                }
                : null,
            Status = (PhaseStatus)dto.Status,
            ElapsedSeconds = dto.ElapsedSeconds,
            _baseTurnAlongTrack = dto.BaseTurnAlongTrack,
            _abeamAlongTrack = dto.AbeamAlongTrack,
        };
        // Recordings predating the field carry no midfield along-track; recompute it from the
        // waypoints, which is where OnStart derives it from anyway.
        phase._midfieldAlongTrack =
            dto.MidfieldAlongTrack ?? (phase.Waypoints is not null ? PatternGeometry.MidfieldAlongTrackNm(phase.Waypoints) : 0.0);
        phase._thresholdLat = dto.ThresholdLat;
        phase._thresholdLon = dto.ThresholdLon;
        phase._downwindHeading = new TrueHeading(dto.DownwindHeadingDeg);
        phase._pastAbeam = dto.PastAbeam;
        phase._holdLevelFt = dto.HoldLevelFt;
        phase._descentTargetFt = dto.DescentTargetFt;
        phase._controllerAltitudeFt = dto.ControllerAltitudeFt;
        phase._seenAssignedAltitudeFt = dto.SeenAssignedAltitudeFt;
        phase._assignedAltitudeBaselinePending = dto.AssignedAltitudeBaselineRecorded != true;
        phase._midfieldBroadcastIssued = dto.MidfieldBroadcastIssued;
        phase._followExtensionWarningIssued = dto.FollowExtensionWarningIssued ?? false;
        return phase;
    }

    private static bool HasLandingClearance(PhaseContext ctx)
    {
        PhaseList? phases = ctx.Aircraft.Phases;
        if (phases is null)
        {
            return false;
        }

        return phases.LandingClearance
            is ClearanceType.ClearedToLand
                or ClearanceType.ClearedForOption
                or ClearanceType.ClearedTouchAndGo
                or ClearanceType.ClearedStopAndGo
                or ClearanceType.ClearedLowApproach;
    }

    protected override List<ClearanceRequirement> CreateRequirements() => [];
}
