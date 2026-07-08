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

    public override string Name => "Crosswind";
    public override bool ManagesSpeed => true;

    public override void OnStart(PhaseContext ctx)
    {
        if (Waypoints is null)
        {
            return;
        }

        PatternReportHelper.EmitTurningLeg(ctx, ReportTrigger.Crosswind);

        _targetLat = Waypoints.DownwindStartLat;
        _targetLon = Waypoints.DownwindStartLon;
        _crosswindHeading = Waypoints.CrosswindHeading;

        ctx.Targets.TargetTrueHeading = Waypoints.CrosswindHeading;
        ctx.Targets.PreferredTurnDirection = null;
        if (!ctx.Targets.HasExplicitTurnRate)
        {
            ctx.Targets.TurnRateOverride = CategoryPerformance.PatternTurnRate(ctx.Category);
        }
        ctx.Targets.NavigationRoute.Clear();

        // Continue climbing. A pattern-exit departure climbs toward its assigned/cruise altitude
        // (no level-off at TPA); a normal crosswind tops out at pattern altitude.
        if (DepartureClimbTargetFt is { } departureClimbTo)
        {
            ctx.Targets.TargetAltitude = departureClimbTo;
            ctx.Targets.DesiredVerticalRate = AircraftPerformance.InitialClimbRate(ctx.AircraftType, ctx.Category);
        }
        else if (ctx.Aircraft.Altitude < Waypoints.PatternAltitude - 50)
        {
            ctx.Targets.TargetAltitude = Waypoints.PatternAltitude;
            ctx.Targets.DesiredVerticalRate = AircraftPerformance.InitialClimbRate(ctx.AircraftType, ctx.Category);
        }

        Log.LogDebug(
            "[Crosswind] {Callsign}: started, hdg={Hdg:F0}, alt={Alt:F0}ft",
            ctx.Aircraft.Callsign,
            Waypoints.CrosswindHeading.Degrees,
            ctx.Aircraft.Altitude
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        // Lead-not-found / lead-on-ground / runaway-distance watchdog. Mirrors
        // DownwindPhase so a pattern-phase follower doesn't keep a stale follow
        // target after the lead despawns or lands while crossing over.
        AirborneFollowHelper.CheckLeadLifecycle(ctx);

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

        bool complete = dist < ArrivalNm || targetIsBehind;
        if (complete)
        {
            Log.LogDebug(
                "[Crosswind] {Callsign}: downwind start {Reason}, alt={Alt:F0}ft",
                ctx.Aircraft.Callsign,
                targetIsBehind ? "passed (behind aircraft)" : "reached",
                ctx.Aircraft.Altitude
            );
        }

        // Follow-aware spacing: slow the crossing-over follower when it's bearing
        // down on a lead too closely from behind. Baseline is DownwindSpeed (what
        // the aircraft is accelerating toward as it climbs to pattern altitude);
        // matches the choice in UpwindPhase so the clamp math stays consistent. Gate on
        // the follow target, NOT on TargetSpeed: physics snaps TargetSpeed to null once
        // the leg speed is reached, which would silently stop spacing for a settled follower.
        if (ctx.Aircraft.Approach.FollowingCallsign is not null)
        {
            double baseline = AircraftPerformance.DownwindSpeed(ctx.AircraftType, ctx.Category);
            double minSpeed = AircraftPerformance.ApproachSpeed(ctx.AircraftType, ctx.Category);
            var adjusted = AirborneFollowHelper.GetAdjustedSpeed(ctx, baseline, minSpeed, AirborneFollowHelper.MaxSpeedAdjustKts);
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
                PilotResponder.RouteSoloOrRpoTransmission(
                    ctx.Aircraft,
                    ctx.SoloTrainingMode,
                    ctx.RpoShowPilotSpeech,
                    ctx.StudentPositionType,
                    PilotResponder.BuildFollowExtendingUnableToTurn(ctx.Aircraft, followTarget, "crosswind"),
                    PilotResponder.SoloPositionsTowerApproach
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
            Requirements = Requirements.Count > 0 ? Requirements.Select(r => r.ToSnapshot()).ToList() : null,
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
        };
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        phase._targetLat = dto.TargetLat;
        phase._targetLon = dto.TargetLon;
        phase._crosswindHeading = new TrueHeading(dto.CrosswindHeadingDeg);
        phase._followExtensionWarningIssued = dto.FollowExtensionWarningIssued ?? false;
        return phase;
    }

    protected override List<ClearanceRequirement> CreateRequirements()
    {
        return [];
    }
}
