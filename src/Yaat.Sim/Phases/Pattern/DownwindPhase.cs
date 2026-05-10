using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
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

    private double _baseTurnAlongTrack;
    private double _abeamAlongTrack;
    private double _thresholdLat;
    private double _thresholdLon;
    private TrueHeading _downwindHeading;
    private bool _pastAbeam;
    private double _altitudeFloor;
    private bool _midfieldBroadcastIssued;

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

    public override string Name => "Downwind";
    public override bool ManagesSpeed => true;

    public override void OnStart(PhaseContext ctx)
    {
        if (Waypoints is null)
        {
            return;
        }

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
        double aircraftAlongTrack = GeoMath.AlongTrackDistanceNm(ctx.Aircraft.Position, new LatLon(_thresholdLat, _thresholdLon), _downwindHeading);

        // Midfield downwind broadcast: remind controller if no landing clearance.
        // Solo-training VFR pattern aircraft voice the reminder as delayed pilot speech.
        // RPO mode keeps the controller-facing warning (PendingWarnings).
        if (!_midfieldBroadcastIssued && !ctx.AutoClearedToLand)
        {
            double midfieldAlongTrack = _abeamAlongTrack / 2.0;
            if (aircraftAlongTrack >= midfieldAlongTrack - AlongTrackToleranceNm)
            {
                _midfieldBroadcastIssued = true;
                if (!HasLandingClearance(ctx))
                {
                    string runwayId = ctx.Runway?.Designator ?? "unknown";
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
                            PilotResponder.BuildMidfieldDownwindReminder(ctx.Aircraft, runwayId),
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
        // lets IAS escape the stabilized-approach gate downstream.
        if (ctx.Targets.TargetSpeed is not null)
        {
            double baseline = _pastAbeam
                ? AircraftPerformance.BaseSpeed(ctx.AircraftType, ctx.Category)
                : AircraftPerformance.DownwindSpeed(ctx.AircraftType, ctx.Category);
            double minSpeed = AircraftPerformance.ApproachSpeed(ctx.AircraftType, ctx.Category);
            var adjusted = AirborneFollowHelper.GetAdjustedSpeed(ctx, baseline, minSpeed, AirborneFollowHelper.MaxSpeedAdjustKts);
            if (adjusted is not null)
            {
                ctx.Targets.TargetSpeed = adjusted.Value;
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

        // Extend downwind if following traffic and too close
        if (AirborneFollowHelper.ShouldExtendDownwind(ctx))
        {
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
        double patternSize = CategoryPerformance.PatternSizeNm(ctx.Category);
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
        return cmd switch
        {
            CanonicalCommandType.ClearedToLand => CommandAcceptance.Allowed,
            CanonicalCommandType.LandAndHoldShort => CommandAcceptance.Allowed,
            CanonicalCommandType.ClearedForOption => CommandAcceptance.Allowed,
            CanonicalCommandType.GoAround => CommandAcceptance.Allowed,
            CanonicalCommandType.Follow => CommandAcceptance.Allowed,
            CanonicalCommandType.ClimbMaintain => CommandAcceptance.Allowed,
            CanonicalCommandType.DescendMaintain => CommandAcceptance.Allowed,
            CanonicalCommandType.MakeShortApproach => CommandAcceptance.Allowed,
            CanonicalCommandType.MakeNormalApproach => CommandAcceptance.Allowed,
            CanonicalCommandType.Speed => CommandAcceptance.Allowed,
            CanonicalCommandType.ReduceToFinalApproachSpeed => CommandAcceptance.Allowed,
            CanonicalCommandType.ResumeNormalSpeed => CommandAcceptance.Allowed,
            CanonicalCommandType.DeleteSpeedRestrictions => CommandAcceptance.Allowed,
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
        };

    public static DownwindPhase FromSnapshot(DownwindPhaseDto dto)
    {
        var phase = new DownwindPhase
        {
            Waypoints = dto.Waypoints is not null ? PatternWaypoints.FromSnapshot(dto.Waypoints) : null,
            IsExtended = dto.IsExtended,
            ShortApproachArmed = dto.ShortApproachArmed,
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
