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

    private const double AlongTrackToleranceNm = 0.3;

    // Downwind-track re-intercept. After a wrong-side / cross-runway MidfieldCrossing join the aircraft
    // can be left off the computed downwind line (e.g. dropped inside its own pattern); steer back onto
    // it rather than holding the wrong offset — which would make the base/final geometry (built for the
    // computed pattern width) turn early and overshoot onto the far side / a parallel runway. No-op once
    // established on the line (cross-track ≈ 0), so a normally-established downwind is unaffected.
    private const double DownwindTrackToleranceNm = 0.03;
    private const double DownwindInterceptGainDegPerNm = 200.0;
    private const double MaxDownwindInterceptDeg = 45.0;

    private double _baseTurnAlongTrack;
    private double _abeamAlongTrack;
    private double _thresholdLat;
    private double _thresholdLon;
    private TrueHeading _downwindHeading;
    private bool _pastAbeam;
    private double _altitudeFloor;
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

        // Downwind speed (per-type if available)
        ctx.Targets.TargetSpeed = AircraftPerformance.DownwindSpeed(ctx.AircraftType, ctx.Category);

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
        // lead despawns or lands.
        AirborneFollowHelper.CheckLeadLifecycle(ctx);

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

        // Midfield downwind broadcast: remind controller if no landing clearance.
        // Solo-training VFR pattern aircraft voice the reminder as delayed pilot speech.
        // RPO mode keeps the controller-facing warning (PendingWarnings).
        // An extended downwind (EXT) is itself a controller sequencing instruction —
        // the aircraft is being actively managed, so the "uncleared" nag is suppressed.
        if (!_midfieldBroadcastIssued && !ctx.AutoClearedToLand)
        {
            double midfieldAlongTrack = _abeamAlongTrack / 2.0;
            if (aircraftAlongTrack >= midfieldAlongTrack - AlongTrackToleranceNm)
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

        // Begin descent when abeam the approach end of the runway
        if (!_pastAbeam && Waypoints is not null)
        {
            if (aircraftAlongTrack >= _abeamAlongTrack - AlongTrackToleranceNm)
            {
                _pastAbeam = true;
                Log.LogDebug("[Downwind] {Callsign}: abeam threshold, beginning descent", ctx.Aircraft.Callsign);
                ApplyPastAbeamDescentTargets(ctx, aircraftAlongTrack);

                // Begin decelerating toward base speed
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
            var adjusted = AirborneFollowHelper.GetAdjustedSpeed(ctx, baseline, minSpeed, AirborneFollowHelper.MaxSpeedAdjustKts);
            if (adjusted is not null)
            {
                // Spacing only ever SLOWS the follower below the leg baseline; a too-far
                // lead is handled laterally (extend/hold base turn), not by accelerating.
                ctx.Targets.TargetSpeed = Math.Min(adjusted.Value, baseline);
            }
        }

        if (IsExtended)
        {
            // Level off at the glideslope intercept altitude so the
            // aircraft doesn't descend below a normal approach path
            // while waiting for the TB command.
            if (_pastAbeam && ctx.Aircraft.Altitude <= _altitudeFloor)
            {
                ctx.Targets.TargetAltitude = _altitudeFloor;
                ctx.Targets.DesiredVerticalRate = null;
            }

            return false;
        }

        // Hold the base turn if following traffic and either (a) too close to the
        // leader [proximity], or (b) turning base now would roll out ahead of /
        // too tightly behind a pattern-flow-ahead leader [sequencing]. A deliberate
        // short approach always wins. Both holds level the aircraft at the
        // glideslope-intercept altitude floor while waiting.
        //
        // A follower does NOT turn base on its own to escape the hold: it keeps flying
        // the downwind until it is genuinely sequenced behind (the hold clears) or the
        // controller issues a turn. Past MaxFollowExtensionNm it advises once ("extending
        // downwind … unable to turn") so the controller can re-sequence, then keeps going.
        bool holdForProximity = AirborneFollowHelper.ShouldExtendDownwind(ctx);
        bool wantsSequenceHold =
            !ShortApproachArmed && AirborneFollowHelper.ShouldHoldForLeadSequencing(ctx, new LatLon(_thresholdLat, _thresholdLon), _downwindHeading);

        if (holdForProximity || wantsSequenceHold)
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

            if (_pastAbeam && ctx.Aircraft.Altitude <= _altitudeFloor)
            {
                ctx.Targets.TargetAltitude = _altitudeFloor;
                ctx.Targets.DesiredVerticalRate = null;
            }

            return false;
        }

        bool complete = aircraftAlongTrack >= _baseTurnAlongTrack - AlongTrackToleranceNm;
        if (complete)
        {
            Log.LogDebug("[Downwind] {Callsign}: base turn point reached, alt={Alt:F0}ft", ctx.Aircraft.Callsign, ctx.Aircraft.Altitude);
        }

        return complete;
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
        // a steeper descent to make the new base-turn altitude.
        if (_pastAbeam)
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

        if (_pastAbeam)
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
    /// Computes the mid-altitude target, vertical rate, and altitude floor for the
    /// past-abeam descent and writes them onto <paramref name="ctx"/>. Branches on
    /// <see cref="ShortApproachArmed"/>: normal pattern uses 60% TPA midpoint and the
    /// category default rate; SA uses the GS-intercept altitude implied by
    /// <see cref="CategoryPerformance.MinShortApproachFinalNm"/> with a steeper rate
    /// derived from the remaining downwind distance and current ground speed.
    /// Called both at abeam-detect (OnTick) and live SA/MNA (Apply/RemoveShortApproach).
    /// </summary>
    private void ApplyPastAbeamDescentTargets(PhaseContext ctx, double aircraftAlongTrack)
    {
        if (Waypoints is null)
        {
            return;
        }

        double thresholdElev = ctx.Runway?.ElevationFt ?? ctx.FieldElevation;
        double patternSize = Waypoints.PatternSizeNm;
        double gsAngle = GlideSlopeGeometry.AngleForCategory(ctx.Category);
        double baseDescentRate = CategoryPerformance.PatternDescentRate(ctx.Category);

        double midAlt;
        double baseExtForFloor;
        double descentRate;

        if (ShortApproachArmed)
        {
            // Compressed final length → base-turn altitude is the GS intercept
            // altitude implied by sqrt(patternSize² + finalLen²).
            double finalLen = CategoryPerformance.MinShortApproachFinalNm(ctx.Category);
            double diagonalNm = Math.Sqrt(patternSize * patternSize + finalLen * finalLen);
            midAlt = thresholdElev + diagonalNm * GlideSlopeGeometry.FeetPerNm(gsAngle);
            baseExtForFloor = CategoryPerformance.ShortApproachBaseExtensionNm(ctx.Category);

            // Required rate to lose the altitude delta over the remaining distance
            // to the base-turn point. Clamped at the category default (won't be slower
            // than normal) and at 1500 fpm (descent limit before "unable, too high").
            double deltaAlt = Math.Max(ctx.Aircraft.Altitude - midAlt, 0);
            double distToBaseTurnNm = Math.Max(_baseTurnAlongTrack - aircraftAlongTrack, 0.05);
            double groundSpeedKt = Math.Max(ctx.Aircraft.GroundSpeed, 60);
            double timeMinToBaseTurn = distToBaseTurnNm / (groundSpeedKt / 60.0);
            double computedRate = timeMinToBaseTurn > 0 ? deltaAlt / timeMinToBaseTurn : baseDescentRate;
            descentRate = Math.Clamp(computedRate, baseDescentRate, 1500);
        }
        else
        {
            // Target: 60% of the way from threshold to pattern altitude
            midAlt = thresholdElev + (Waypoints.PatternAltitude - thresholdElev) * 0.6;
            baseExtForFloor = CategoryPerformance.BaseExtensionNm(ctx.Category);
            descentRate = baseDescentRate;
        }

        ctx.Targets.TargetAltitude = midAlt;
        ctx.Targets.DesiredVerticalRate = -descentRate;

        // Altitude floor for extended downwind: GS intercept altitude at the
        // diagonal distance from base-turn point to threshold (uses the same
        // geometry SA selects, so the floor is consistent with the descent target).
        double finalApproachDist = Math.Sqrt(patternSize * patternSize + baseExtForFloor * baseExtForFloor);
        _altitudeFloor = thresholdElev + finalApproachDist * GlideSlopeGeometry.FeetPerNm(gsAngle);
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
            Requirements = Requirements.Count > 0 ? Requirements.Select(r => r.ToSnapshot()).ToList() : null,
            Waypoints = Waypoints?.ToSnapshot(),
            IsExtended = IsExtended,
            BaseTurnAlongTrack = _baseTurnAlongTrack,
            AbeamAlongTrack = _abeamAlongTrack,
            ThresholdLat = _thresholdLat,
            ThresholdLon = _thresholdLon,
            DownwindHeadingDeg = _downwindHeading.Degrees,
            PastAbeam = _pastAbeam,
            AltitudeFloor = _altitudeFloor,
            MidfieldBroadcastIssued = _midfieldBroadcastIssued,
            ShortApproachArmed = ShortApproachArmed,
            RejoinTrack = RejoinTrack,
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
            LateralOffset = dto.LateralOffsetTargetNm is { } target
                ? new PatternLateralOffsetState
                {
                    TargetNm = target,
                    Direction = (TurnDirection)(dto.LateralOffsetDirection ?? 0),
                    Acquired = dto.LateralOffsetAcquired,
                }
                : null,
        };
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        phase._baseTurnAlongTrack = dto.BaseTurnAlongTrack;
        phase._abeamAlongTrack = dto.AbeamAlongTrack;
        phase._thresholdLat = dto.ThresholdLat;
        phase._thresholdLon = dto.ThresholdLon;
        phase._downwindHeading = new TrueHeading(dto.DownwindHeadingDeg);
        phase._pastAbeam = dto.PastAbeam;
        phase._altitudeFloor = dto.AltitudeFloor;
        phase._midfieldBroadcastIssued = dto.MidfieldBroadcastIssued;
        phase._followExtensionWarningIssued = dto.FollowExtensionWarningIssued ?? false;
        return phase;
    }

    private static bool HasLandingClearance(PhaseContext ctx)
    {
        var phases = ctx.Aircraft.Phases;
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

    protected override List<ClearanceRequirement> CreateRequirements()
    {
        return [];
    }
}
