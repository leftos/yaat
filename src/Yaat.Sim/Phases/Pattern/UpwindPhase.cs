using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Pattern;

/// <summary>
/// Upwind leg: climb from runway heading toward pattern altitude. Per AIM 4-3-2 the crosswind turn is
/// commenced beyond the departure end of the runway (DER) and within 300 ft of pattern altitude — so the
/// phase completes once the aircraft has flown over the DER (the crosswind-turn waypoint) AND is within
/// 300 ft of pattern altitude, whichever is later. The upwind length is therefore governed by runway
/// geometry + TPA, not pattern size.
/// </summary>
public sealed class UpwindPhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("UpwindPhase");

    private double _targetLat;
    private double _targetLon;
    private TrueHeading _upwindHeading;
    private double _minTurnAltitude;

    public PatternWaypoints? Waypoints { get; set; }

    /// <summary>
    /// When true, the aircraft continues on the current heading past the turn point
    /// until a turn command (TC) or another EXT-clearing command is issued.
    /// </summary>
    public bool IsExtended { get; set; }

    /// <summary>
    /// One-shot armed by a <c>TC</c> (turn crosswind) issued before the aircraft reached the
    /// upwind leg — i.e. during the takeoff roll / initial climb, while still in
    /// <see cref="Tower.TakeoffPhase"/>. Honored on the first tick once this leg is active, so the
    /// crosswind turn occurs no earlier than the leg's start (≈400 ft AGL — TakeoffPhase's
    /// completion floor), making <c>TC</c> behave the same whether issued at 350 ft AGL (Takeoff)
    /// or 450 ft AGL (Upwind). Takes precedence over <see cref="IsExtended"/>. See issue #208.
    /// </summary>
    public bool TurnCrosswindArmed { get; set; }

    /// <summary>
    /// Active lateral offset state set by OFL/OFR. See <see cref="DownwindPhase.LateralOffset"/>.
    /// </summary>
    public PatternLateralOffsetState? LateralOffset { get; set; }

    /// <summary>
    /// Continuous-climb target (feet MSL) for a pattern-exit departure (CTO MRC/MRD/MLC/MLD).
    /// When set, the upwind climbs toward this altitude instead of leveling at pattern altitude —
    /// a departing aircraft never levels at TPA (AIM 4-3-3 level-off discipline is for arrivals).
    /// The crosswind-turn altitude gate (pattern altitude − 300) is unaffected. Null for normal
    /// closed-traffic / arrival upwinds.
    /// </summary>
    public int? DepartureClimbTargetFt { get; set; }

    public override string Name => "Upwind";
    public override bool ManagesSpeed => true;

    public override void OnStart(PhaseContext ctx)
    {
        if (Waypoints is null)
        {
            return;
        }

        _targetLat = Waypoints.CrosswindTurnLat;
        _targetLon = Waypoints.CrosswindTurnLon;
        _upwindHeading = Waypoints.UpwindHeading;
        _minTurnAltitude = Waypoints.PatternAltitude - 300;

        ctx.Targets.TargetTrueHeading = Waypoints.UpwindHeading;
        ctx.Targets.PreferredTurnDirection = null;
        if (!ctx.Targets.HasExplicitTurnRate)
        {
            ctx.Targets.TurnRateOverride = CategoryPerformance.PatternTurnRate(ctx.Category);
        }
        ctx.Targets.NavigationRoute.Clear();

        // Climb to pattern altitude — or, for a pattern-exit departure, continue the takeoff-rate
        // climb toward the assigned/cruise altitude without leveling off.
        ctx.Targets.TargetAltitude = DepartureClimbTargetFt ?? Waypoints.PatternAltitude;
        ctx.Targets.DesiredVerticalRate = AircraftPerformance.InitialClimbRate(ctx.AircraftType, ctx.Category);

        // Accelerate toward downwind speed
        ctx.Targets.TargetSpeed = AircraftPerformance.DownwindSpeed(ctx.AircraftType, ctx.Category);

        Log.LogDebug(
            "[Upwind] {Callsign}: started, hdg={Hdg:F0}, patternAlt={Alt:F0}ft, extended={Ext}",
            ctx.Aircraft.Callsign,
            Waypoints.UpwindHeading.Degrees,
            Waypoints.PatternAltitude,
            IsExtended
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        // Early crosswind turn armed by a TC issued during takeoff/initial climb (issue #208).
        // This leg only activates at ≈400 ft AGL (TakeoffPhase's completion floor), so completing
        // on the first tick turns the aircraft crosswind at the earliest safe altitude. Checked
        // before IsExtended so a TC overrides a prior EXT (matches the IsExtended doc above).
        if (TurnCrosswindArmed)
        {
            TurnCrosswindArmed = false; // one-shot; clear so a snapshot restore / re-entry won't re-fire
            Log.LogDebug("[Upwind] {Callsign}: early crosswind turn (armed by TC during takeoff/climb)", ctx.Aircraft.Callsign);
            return true;
        }

        // Lead-not-found / lead-on-ground / runaway-distance watchdog. Mirrors
        // DownwindPhase so a pattern-phase follower doesn't keep a stale follow
        // target after the lead despawns or lands during the climb-out.
        AirborneFollowHelper.CheckLeadLifecycle(ctx);

        // OFL/OFR lateral dogleg. Reference point: departure end of runway.
        if (LateralOffset is not null && Waypoints is not null)
        {
            ctx.Targets.TargetTrueHeading = PatternLateralOffsetHelper.ComputeTargetHeading(
                ctx,
                _upwindHeading,
                new LatLon(Waypoints.DepartureEndLat, Waypoints.DepartureEndLon),
                LateralOffset
            );
        }

        if (IsExtended)
        {
            return false;
        }

        // AIM 4-3-2: the crosswind turn is commenced beyond the departure end of the runway, within
        // 300 ft of pattern altitude. The crosswind-turn waypoint sits at the DER; the aircraft must have
        // flown over it (bearing to the waypoint more than 90° off the upwind heading = abeam/behind)
        // before the turn fires, so it never turns crosswind while still over the runway.
        double bearingToTarget = GeoMath.BearingTo(ctx.Aircraft.Position, new LatLon(_targetLat, _targetLon));
        double bearingDiff = Math.Abs(GeoMath.SignedBearingDifference(bearingToTarget, _upwindHeading.Degrees));
        bool pastDepartureEnd = bearingDiff > 90.0;

        bool altitudeReached = ctx.Aircraft.Altitude >= _minTurnAltitude;
        bool complete = pastDepartureEnd && altitudeReached;
        if (complete)
        {
            Log.LogDebug("[Upwind] {Callsign}: crosswind turn at departure end, alt={Alt:F0}ft", ctx.Aircraft.Callsign, ctx.Aircraft.Altitude);
        }

        // Follow-aware spacing: slow the climbing-out follower when it's bearing
        // down on a lead too closely from behind. Feed the phase baseline
        // (DownwindSpeed) into the helper, not the previous tick's target, so the
        // ±MaxSpeedAdjustKts clamp doesn't compound across ticks. Gate on the follow
        // target, NOT on TargetSpeed: physics snaps TargetSpeed to null once the leg
        // speed is reached, which would silently stop spacing for a settled follower.
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
        new UpwindPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = Requirements.Count > 0 ? Requirements.Select(r => r.ToSnapshot()).ToList() : null,
            Waypoints = Waypoints?.ToSnapshot(),
            IsExtended = IsExtended,
            TurnCrosswindArmed = TurnCrosswindArmed,
            TargetLat = _targetLat,
            TargetLon = _targetLon,
            UpwindHeadingDeg = _upwindHeading.Degrees,
            MinTurnAltitude = _minTurnAltitude,
            DepartureClimbTargetFt = DepartureClimbTargetFt,
            LateralOffsetTargetNm = LateralOffset?.TargetNm,
            LateralOffsetDirection = LateralOffset is not null ? (int)LateralOffset.Direction : null,
            LateralOffsetAcquired = LateralOffset?.Acquired ?? false,
        };

    public static UpwindPhase FromSnapshot(UpwindPhaseDto dto)
    {
        var phase = new UpwindPhase
        {
            Waypoints = dto.Waypoints is not null ? PatternWaypoints.FromSnapshot(dto.Waypoints) : null,
            IsExtended = dto.IsExtended,
            TurnCrosswindArmed = dto.TurnCrosswindArmed ?? false,
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
        phase._upwindHeading = new TrueHeading(dto.UpwindHeadingDeg);
        phase._minTurnAltitude = dto.MinTurnAltitude;
        return phase;
    }

    protected override List<ClearanceRequirement> CreateRequirements()
    {
        return [];
    }
}
