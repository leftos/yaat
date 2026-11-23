using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Pattern;

/// <summary>
/// Crosswind leg: turn from upwind heading to crosswind heading,
/// fly to downwind start point. Continues climb to pattern altitude.
/// Completes when reaching the downwind start waypoint.
/// </summary>
public sealed class CrosswindPhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("CrosswindPhase");

    private const double ArrivalNm = 0.3;

    private double _targetLat;
    private double _targetLon;
    private TrueHeading _crosswindHeading;
    private bool _followExtensionWarningIssued;

    /// <summary>Set on the copy that resumes this leg after a boundary hold. Read in OnStart only, so it needs no snapshot field.</summary>
    private bool _resumeFromPosition;

    /// <summary>The assigned altitude in force when the leg started, recorded so a CM/DM issued on the leg can be told apart.</summary>
    private double? _seenAssignedAltitudeFt;

    private bool _assignedAltitudeBaselineRecorded;

    /// <summary>A CM/DM issued on the leg a boundary hold suspended, which this restart copy keeps.</summary>
    private double? _resumeControllerAltitudeFt;

    public PatternWaypoints? Waypoints { get; set; }

    /// <summary>
    /// When true, the aircraft continues on the current heading past the turn point
    /// until a turn command (TD) or another EXT-clearing command is issued.
    /// </summary>
    public bool IsExtended { get; set; }

    /// <summary>
    /// Active lateral offset state set by OFL/OFR. See <see cref="DownwindPhase.LateralOffset"/>.
    /// </summary>
    public PatternLateralOffsetState? LateralOffset { get; set; }

    /// <summary>
    /// Continuous-climb target (feet MSL) for a pattern-exit downwind departure (CTO MRD/MLD).
    /// When set, the crosswind keeps the takeoff-rate climb toward the assigned/cruise altitude
    /// instead of leveling at pattern altitude. Null for normal closed-traffic / arrival crosswinds.
    /// </summary>
    public int? DepartureClimbTargetFt { get; set; }

    /// <summary>How far below pattern altitude a crosswind starts to climb to it; one starting higher leaves the target standing.</summary>
    private const double ClimbToPatternBelowFt = 50.0;

    /// <summary>
    /// The altitude this leg flies when it starts at <paramref name="altitudeFt"/>, as <see cref="OnStart"/> sets it: a
    /// controller altitude it carries, else its departure climb target, else pattern altitude when it starts more than
    /// <see cref="ClimbToPatternBelowFt"/> below it, else <paramref name="standingTargetFt"/> (the target standing as it starts,
    /// which it leaves alone), else <paramref name="altitudeFt"/>; null without waypoints.
    /// </summary>
    internal double? PlannedLegAltitudeFt(double altitudeFt, double? standingTargetFt)
    {
        if (Waypoints is null)
        {
            return null;
        }

        if ((_resumeControllerAltitudeFt ?? DepartureClimbTargetFt) is { } ownFt)
        {
            return ownFt;
        }

        return (altitudeFt < Waypoints.PatternAltitude - ClimbToPatternBelowFt) ? Waypoints.PatternAltitude : (standingTargetFt ?? altitudeFt);
    }

    public override string Name => "Crosswind";
    public override bool ManagesSpeed => true;

    /// <inheritdoc/>
    public override bool CanSuspendForBoundaryHold(PhaseContext ctx) => true;

    /// <summary>
    /// A restart copy: the leg flies a fixed heading judged on along-track distance, so it is re-flown from where the orbit
    /// left the aircraft, with no "turning crosswind" call. It carries the leg's extension, the lateral offset (re-acquired
    /// from the new position), the departure climb target, a CM/DM issued on the leg and the follow-extension advisory.
    /// </summary>
    public override HoldResume ResumeAfterBoundaryHold(PhaseContext ctx) =>
        HoldResume.RestartWith(
            new CrosswindPhase
            {
                Waypoints = Waypoints,
                IsExtended = IsExtended,
                LateralOffset = PatternLateralOffsetState.Reacquire(LateralOffset),
                DepartureClimbTargetFt = DepartureClimbTargetFt,
                _followExtensionWarningIssued = _followExtensionWarningIssued,
                _resumeFromPosition = true,
                _seenAssignedAltitudeFt = _seenAssignedAltitudeFt,
                _assignedAltitudeBaselineRecorded = _assignedAltitudeBaselineRecorded,
                _resumeControllerAltitudeFt = ResumedLegAltitude.IssuedOnLeg(ctx.Targets, _assignedAltitudeBaselineRecorded, _seenAssignedAltitudeFt),
            }
        );

    /// <summary>
    /// A new leg makes the armed "turning crosswind" call and records the assigned altitude in force as it starts; the restart
    /// copy makes no call, keeps the baseline of the leg it replaces and holds the CM/DM issued on that leg.
    /// </summary>
    private void StartLegOrResume(PhaseContext ctx)
    {
        if (_resumeFromPosition)
        {
            ResumedLegAltitude.Apply(ctx, _resumeControllerAltitudeFt);
            return;
        }

        PatternReportHelper.EmitTurningLeg(ctx, ReportTrigger.Crosswind);
        _seenAssignedAltitudeFt = ctx.Targets.AssignedAltitude;
        _assignedAltitudeBaselineRecorded = true;
    }

    public override void OnStart(PhaseContext ctx)
    {
        if (Waypoints is null)
        {
            return;
        }

        _targetLat = Waypoints.DownwindStartLat;
        _targetLon = Waypoints.DownwindStartLon;
        _crosswindHeading = Waypoints.CrosswindHeading;

        ctx.Targets.TargetTrueHeading = Waypoints.CrosswindHeading;
        ctx.Targets.PreferredTurnDirection = null;
        if (!ctx.Targets.HasExplicitTurnRate)
        {
            ctx.Targets.TurnRateOverride = PatternCompression.TurnRate(Waypoints, ctx.Category);
        }
        ctx.Targets.NavigationRoute.Clear();

        // Continue climbing. A pattern-exit departure climbs toward its assigned/cruise altitude
        // (no level-off at TPA); a normal crosswind tops out at pattern altitude.
        if (DepartureClimbTargetFt is { } departureClimbTo)
        {
            ctx.Targets.TargetAltitude = departureClimbTo;
            ctx.Targets.DesiredVerticalRate = AircraftPerformance.InitialClimbRate(ctx.AircraftType, ctx.Category);
        }
        else if (ctx.Aircraft.Altitude < Waypoints.PatternAltitude - ClimbToPatternBelowFt)
        {
            ctx.Targets.TargetAltitude = Waypoints.PatternAltitude;
            ctx.Targets.DesiredVerticalRate = AircraftPerformance.InitialClimbRate(ctx.AircraftType, ctx.Category);
        }

        StartLegOrResume(ctx);

        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug(
                "[Crosswind] {Callsign}: started, hdg={Hdg:F0}, alt={Alt:F0}ft",
                ctx.Aircraft.Callsign,
                Waypoints.CrosswindHeading.Degrees,
                ctx.Aircraft.Altitude
            );
        }
    }

    /// <summary>
    /// A compressed circuit turns downwind one turn radius short of its width, so the turn rolls out on the downwind track
    /// instead of one radius outside it.
    /// </summary>
    private bool CompressedDownwindTurnDue(PhaseContext ctx)
    {
        if (Waypoints?.CompressedTurnRateDegPerSec is not { } turnRate)
        {
            return false;
        }

        var turnPoint = new LatLon(Waypoints.CrosswindTurnLat, Waypoints.CrosswindTurnLon);
        double outNm = GeoMath.AlongTrackDistanceNm(ctx.Aircraft.Position, turnPoint, _crosswindHeading);
        double turnStartNm =
            BasePhase.TurnRadiusNm(ctx.Aircraft.GroundSpeed, turnRate)
            + BasePhase.TurnLeadNm(ctx.Aircraft.GroundSpeed, turnRate, ctx.Category, ctx.TurnModel);
        return outNm >= Waypoints.PatternSizeNm - turnStartNm;
    }

    public override bool OnTick(PhaseContext ctx)
    {
        // Lead-not-found / lead-on-ground / runaway-distance watchdog. Mirrors
        // DownwindPhase so a pattern-phase follower doesn't keep a stale follow
        // target after the lead despawns or lands while crossing over. A cancel
        // can replace/clear this phase list mid-tick — bail out when it fires.
        if (AirborneFollowHelper.CheckLeadLifecycle(ctx))
        {
            return false;
        }

        // OFL/OFR lateral dogleg. Reference point: crosswind turn point.
        if (LateralOffset is not null && Waypoints is not null)
        {
            ctx.Targets.TargetTrueHeading = PatternLateralOffsetHelper.ComputeTargetHeading(
                ctx,
                _crosswindHeading,
                new LatLon(Waypoints.CrosswindTurnLat, Waypoints.CrosswindTurnLon),
                LateralOffset
            );
        }

        if (IsExtended)
        {
            return false;
        }

        double dist = GeoMath.DistanceNm(ctx.Aircraft.Position, new LatLon(_targetLat, _targetLon));

        // Check if the aircraft has already passed the downwind start point.
        // Detect by checking if the bearing to the target is behind us
        // (more than 90° off our crosswind heading).
        double bearingToTarget = GeoMath.BearingTo(ctx.Aircraft.Position, new LatLon(_targetLat, _targetLon));
        double bearingDiff = Math.Abs(GeoMath.SignedBearingDifference(bearingToTarget, _crosswindHeading.Degrees));
        bool targetIsBehind = bearingDiff > 90.0;

        bool complete = dist < ArrivalNm || targetIsBehind || CompressedDownwindTurnDue(ctx);
        if (complete)
        {
            if (Log.IsEnabled(LogLevel.Debug))
            {
                Log.LogDebug(
                    "[Crosswind] {Callsign}: downwind start {Reason}, alt={Alt:F0}ft",
                    ctx.Aircraft.Callsign,
                    targetIsBehind ? "passed (behind aircraft)" : "reached",
                    ctx.Aircraft.Altitude
                );
            }
        }

        // Follow-aware spacing: slow the crossing-over follower when it's bearing
        // down on a lead too closely from behind. Baseline is DownwindSpeed (what
        // the aircraft is accelerating toward as it climbs to pattern altitude);
        // matches the choice in UpwindPhase so the clamp math stays consistent. Gate on
        // the follow target, NOT on TargetSpeed: physics snaps TargetSpeed to null once
        // the leg speed is reached, which would silently stop spacing for a settled follower.
        if (ctx.Aircraft.Approach.FollowingCallsign is not null)
        {
            double baseline = PatternCompression.DownwindSpeed(Waypoints, ctx.AircraftType, ctx.Category);
            double minSpeed = AircraftPerformance.ApproachSpeed(ctx.AircraftType, ctx.Category);
            double? adjusted = AirborneFollowHelper.GetAdjustedSpeed(ctx, baseline, minSpeed, AirborneFollowHelper.MaxSpeedAdjustKts);
            if (adjusted is not null)
            {
                // Spacing only ever SLOWS the follower below the leg baseline; never speeds
                // it up to chase a far lead.
                ctx.Targets.TargetSpeed = Math.Min(adjusted.Value, baseline);
            }
        }

        // Follow-aware leg hold: a follower must not turn downwind until it is a full desired
        // spacing behind the lead it is following in remaining pattern path — turning off the leg
        // early rolls it onto the downwind ahead of the traffic it was told to follow. A follower
        // does NOT turn on its own: it keeps flying the crosswind until it is sequenced behind (the
        // hold clears) or the controller turns it. Past MaxFollowExtensionNm it advises once so the
        // controller can re-sequence, then keeps going straight.
        if (
            complete
            && (Waypoints is { } crosswindWaypoints)
            && (ctx.Aircraft.Approach.FollowingCallsign is { } followTarget)
            && AirborneFollowHelper.ShouldHoldLegForRemainingPathSequencing(ctx, crosswindWaypoints)
        )
        {
            double alongPastTurn = GeoMath.AlongTrackDistanceNm(ctx.Aircraft.Position, new LatLon(_targetLat, _targetLon), _crosswindHeading);
            if ((alongPastTurn >= AirborneFollowHelper.MaxFollowExtensionNm) && !_followExtensionWarningIssued)
            {
                PilotResponder.RouteByTunedPosition(
                    ctx.Aircraft,
                    PilotReportRouting.From(ctx),
                    PilotResponder.BuildFollowExtendingUnableToTurn(ctx.Aircraft, followTarget, "crosswind")
                );
                _followExtensionWarningIssued = true;
            }

            return false;
        }

        return complete;
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
            CanonicalCommandType.MakeShortApproach => CommandAcceptance.Allowed,
            CanonicalCommandType.MakeNormalApproach => CommandAcceptance.Allowed,
            CanonicalCommandType.Follow => CommandAcceptance.Allowed,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.ClearsPhase,
        };
    }

    public override PhaseDto ToSnapshot() =>
        new CrosswindPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = Requirements.Count > 0 ? [.. Requirements.Select(r => r.ToSnapshot())] : null,
            Waypoints = Waypoints?.ToSnapshot(),
            IsExtended = IsExtended,
            TargetLat = _targetLat,
            TargetLon = _targetLon,
            CrosswindHeadingDeg = _crosswindHeading.Degrees,
            DepartureClimbTargetFt = DepartureClimbTargetFt,
            LateralOffsetTargetNm = LateralOffset?.TargetNm,
            LateralOffsetDirection = LateralOffset is not null ? (int)LateralOffset.Direction : null,
            LateralOffsetAcquired = LateralOffset?.Acquired ?? false,
            FollowExtensionWarningIssued = _followExtensionWarningIssued,
            SeenAssignedAltitudeFt = _seenAssignedAltitudeFt,
            AssignedAltitudeBaselineRecorded = _assignedAltitudeBaselineRecorded ? true : null,
        };

    public static CrosswindPhase FromSnapshot(CrosswindPhaseDto dto)
    {
        var phase = new CrosswindPhase
        {
            Waypoints = dto.Waypoints is not null ? PatternWaypoints.FromSnapshot(dto.Waypoints) : null,
            IsExtended = dto.IsExtended,
            DepartureClimbTargetFt = dto.DepartureClimbTargetFt,
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
            _targetLat = dto.TargetLat,
            _targetLon = dto.TargetLon,
            _crosswindHeading = TrueHeading.FromSnapshot(dto.CrosswindHeadingDeg),
            _followExtensionWarningIssued = dto.FollowExtensionWarningIssued ?? false,
            _seenAssignedAltitudeFt = dto.SeenAssignedAltitudeFt,
            _assignedAltitudeBaselineRecorded = dto.AssignedAltitudeBaselineRecorded ?? false,
        };
        return phase;
    }

    protected override List<ClearanceRequirement> CreateRequirements() => [];
}
