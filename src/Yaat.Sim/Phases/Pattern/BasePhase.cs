using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Pattern;

/// <summary>
/// Base leg: turn from downwind onto base heading, begin descent.
/// Decelerates to base speed, descends toward approach altitude.
/// Completes when reaching the final turn waypoint.
/// </summary>
public sealed class BasePhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("BasePhase");

    private const double MinTurnRadiusNm = 0.15;

    private double _thresholdLat;
    private double _thresholdLon;
    private TrueHeading _finalHeading;

    public PatternWaypoints? Waypoints { get; set; }

    /// <summary>
    /// When set, overrides the default final turn target to a point on the
    /// extended centerline at this distance from the threshold.
    /// </summary>
    public double? FinalDistanceNm { get; set; }

    /// <summary>
    /// Active lateral offset state set by OFL/OFR. On base, the dogleg pushes
    /// the final intercept point further out (cross-track-from-centerline grows,
    /// so the turn-final condition fires later). See <see cref="DownwindPhase.LateralOffset"/>.
    /// </summary>
    public PatternLateralOffsetState? LateralOffset { get; set; }

    public override string Name => "Base";
    public override bool ManagesSpeed => true;

    public override void OnStart(PhaseContext ctx)
    {
        if (Waypoints is null)
        {
            return;
        }

        _finalHeading = Waypoints.FinalHeading;

        if (FinalDistanceNm is not null)
        {
            TrueHeading reciprocal = Waypoints.FinalHeading.ToReciprocal();
            var target = GeoMath.ProjectPoint(Waypoints.ThresholdLat, Waypoints.ThresholdLon, reciprocal, FinalDistanceNm.Value);
            _thresholdLat = target.Lat;
            _thresholdLon = target.Lon;
        }
        else
        {
            _thresholdLat = Waypoints.ThresholdLat;
            _thresholdLon = Waypoints.ThresholdLon;
        }

        ctx.Targets.TargetTrueHeading = Waypoints.BaseHeading;
        ctx.Targets.PreferredTurnDirection = null;
        if (!ctx.Targets.HasExplicitTurnRate)
        {
            ctx.Targets.TurnRateOverride = CategoryPerformance.PatternTurnRate(ctx.Category);
        }
        ctx.Targets.NavigationRoute.Clear();

        // Begin descent. Default rate; if the base→final geometry calls for a
        // steeper descent (SA-shortened final), compute one. The 90° base→final
        // turn translates the aircraft one turn-radius further along the
        // final, so rollout is at (finalDist + r) from the threshold.
        double descentRate = CategoryPerformance.PatternDescentRate(ctx.Category);
        double thresholdElev = ctx.Runway?.ElevationFt ?? ctx.FieldElevation;
        double targetAlt;

        if (FinalDistanceNm is { } finalDist)
        {
            // Aim for the 3° glide-slope altitude at rollout — stabilizes the
            // aircraft on the glide path the moment it rolls out on final,
            // regardless of whether base is short (SA-shortened, steep descent)
            // or long (extended base, no descent needed). Never aim higher
            // than current altitude — controllers issuing ELB/ERB to an
            // aircraft already below GS expect them to maintain or descend,
            // not climb.
            double gsAngle = GlideSlopeGeometry.AngleForCategory(ctx.Category);
            double turnRate = CategoryPerformance.PatternTurnRate(ctx.Category);
            double groundSpeedKt = Math.Max(ctx.Aircraft.GroundSpeed, 60);
            double turnRadiusNm = Math.Max(groundSpeedKt / (turnRate * 62.832), MinTurnRadiusNm);
            double rolloutDistNm = finalDist + turnRadiusNm;
            double gsAlt = thresholdElev + rolloutDistNm * GlideSlopeGeometry.FeetPerNm(gsAngle);
            targetAlt = Math.Min(ctx.Aircraft.Altitude, gsAlt);

            double deltaAlt = Math.Max(ctx.Aircraft.Altitude - targetAlt, 0);
            double baseLen = CategoryPerformance.PatternSizeNm(ctx.Category);
            double timeMin = baseLen / (groundSpeedKt / 60.0);
            double computedRate = timeMin > 0 ? deltaAlt / timeMin : descentRate;
            descentRate = Math.Clamp(computedRate, descentRate, 1500);
        }
        else
        {
            // Wrong-side / midfield-crossing entry: BasePhase runs after a
            // downwind leg, so aircraft is already at TPA and finalDist is
            // not known up front. Fall back to halfway-between-pattern-and-
            // threshold heuristic.
            targetAlt = thresholdElev + (Waypoints.PatternAltitude - thresholdElev) * 0.5;
        }

        ctx.Targets.DesiredVerticalRate = -descentRate;
        ctx.Targets.TargetAltitude = targetAlt;

        // Slow to base speed
        ctx.Targets.TargetSpeed = AircraftPerformance.BaseSpeed(ctx.AircraftType, ctx.Category);

        Log.LogDebug(
            "[Base] {Callsign}: started, hdg={Hdg:F0}, alt={Alt:F0}ft",
            ctx.Aircraft.Callsign,
            Waypoints.BaseHeading.Degrees,
            ctx.Aircraft.Altitude
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        // Lead-not-found / lead-on-ground / runaway-distance watchdog. See
        // DownwindPhase.OnTick for the full rationale.
        AirborneFollowHelper.CheckLeadLifecycle(ctx);

        // OFL/OFR lateral dogleg. Reference point: base-turn (start of base
        // track). The acquired offset extends the final-intercept distance
        // because cross-track-from-centerline grows.
        if (LateralOffset is not null && Waypoints is not null)
        {
            ctx.Targets.TargetTrueHeading = PatternLateralOffsetHelper.ComputeTargetHeading(
                ctx,
                Waypoints.BaseHeading,
                new LatLon(Waypoints.BaseTurnLat, Waypoints.BaseTurnLon),
                LateralOffset
            );
        }

        // Follow speed adjustment — pass the phase baseline, never the previous
        // tick's adjusted target, so the +MaxSpeedAdjustKts clamp can't compound.
        if (ctx.Targets.TargetSpeed is not null)
        {
            double baseline = AircraftPerformance.BaseSpeed(ctx.AircraftType, ctx.Category);
            double minSpeed = AircraftPerformance.ApproachSpeed(ctx.AircraftType, ctx.Category);
            var adjusted = AirborneFollowHelper.GetAdjustedSpeed(ctx, baseline, minSpeed, AirborneFollowHelper.MaxSpeedAdjustKts);
            if (adjusted is not null)
            {
                ctx.Targets.TargetSpeed = adjusted.Value;
            }
        }

        double crossTrack = Math.Abs(
            GeoMath.SignedCrossTrackDistanceNm(ctx.Aircraft.Position, new LatLon(_thresholdLat, _thresholdLon), _finalHeading)
        );

        // Turn initiation: begin turn when cross-track from extended centerline
        // equals the turn radius. This produces a geometrically correct 90° arc
        // that rolls out on centerline at the expected final approach distance.
        double turnRate = CategoryPerformance.PatternTurnRate(ctx.Category);
        double turnRadiusNm = Math.Max(ctx.Aircraft.GroundSpeed / (turnRate * 62.832), MinTurnRadiusNm);
        bool complete = crossTrack <= turnRadiusNm;
        if (complete)
        {
            Log.LogDebug(
                "[Base] {Callsign}: final turn point reached, alt={Alt:F0}ft, xtrack={XT:F2}nm, turnR={R:F2}nm",
                ctx.Aircraft.Callsign,
                ctx.Aircraft.Altitude,
                crossTrack,
                turnRadiusNm
            );
        }

        return complete;
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
        new BasePhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = Requirements.Count > 0 ? Requirements.Select(r => r.ToSnapshot()).ToList() : null,
            Waypoints = Waypoints?.ToSnapshot(),
            FinalDistanceNm = FinalDistanceNm,
            ThresholdLat = _thresholdLat,
            ThresholdLon = _thresholdLon,
            FinalHeadingDeg = _finalHeading.Degrees,
            LateralOffsetTargetNm = LateralOffset?.TargetNm,
            LateralOffsetDirection = LateralOffset is not null ? (int)LateralOffset.Direction : null,
            LateralOffsetAcquired = LateralOffset?.Acquired ?? false,
        };

    public static BasePhase FromSnapshot(BasePhaseDto dto)
    {
        var phase = new BasePhase
        {
            Waypoints = dto.Waypoints is not null ? PatternWaypoints.FromSnapshot(dto.Waypoints) : null,
            FinalDistanceNm = dto.FinalDistanceNm,
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
        phase._thresholdLat = dto.ThresholdLat;
        phase._thresholdLon = dto.ThresholdLon;
        phase._finalHeading = new TrueHeading(dto.FinalHeadingDeg);
        return phase;
    }

    protected override List<ClearanceRequirement> CreateRequirements()
    {
        return [];
    }
}
