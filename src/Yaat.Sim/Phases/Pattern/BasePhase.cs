using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases.Tower;
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

    /// <summary>Floor for the planning speed so a stale zero target can't blow up the descent budget.</summary>
    private const double MinPlanningSpeedKt = 60.0;

    /// <summary>
    /// Radius (nm) of the base-to-final turn at the pattern turn rate. Shared with the ERB
    /// altitude-feasibility gate so its rollout point matches the one flown here.
    /// </summary>
    internal static double TurnRadiusNm(double groundSpeedKt, AircraftCategory category)
    {
        double turnRate = CategoryPerformance.PatternTurnRate(category);
        return Math.Max(Math.Max(groundSpeedKt, MinPlanningSpeedKt) / (turnRate * 62.832), MinTurnRadiusNm);
    }

    /// <summary>
    /// The speed the base leg is planned at: a standing controller speed assignment outranks
    /// the type's base speed (7110.65 §5-7-4). Shared with the ERB altitude-feasibility gate so
    /// the gate budgets the same leg the phase flies.
    /// </summary>
    internal static double PlannedSpeedKt(AircraftState aircraft, AircraftCategory category)
    {
        double speedKt =
            aircraft.Targets.HasExplicitSpeedCommand && aircraft.Targets.TargetSpeed is { } assigned
                ? assigned
                : AircraftPerformance.BaseSpeed(aircraft.AircraftType, category);
        return Math.Max(speedKt, MinPlanningSpeedKt);
    }

    /// <summary>
    /// Steepest descent (fpm) the base leg may be flown at: the category rate ceiling, or the
    /// category angle ceiling at this speed, whichever binds — drag limits the path angle, so
    /// slowing down never buys descent room. Shared with the ERB altitude-feasibility gate.
    /// </summary>
    internal static double MaxDescentRateFpm(double speedKt, AircraftCategory category)
    {
        return Math.Min(
            CategoryPerformance.MaxPatternDescentRate(category),
            GlideSlopeGeometry.RequiredDescentRate(speedKt, CategoryPerformance.MaxPatternDescentAngleDeg(category))
        );
    }

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

        PatternReportHelper.EmitTurningLeg(ctx, ReportTrigger.Base);

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
            double plannedSpeedKt = PlannedSpeedKt(ctx.Aircraft, ctx.Category);
            double turnRadiusNm = TurnRadiusNm(plannedSpeedKt, ctx.Category);
            double rolloutDistNm = finalDist + turnRadiusNm;
            double gsAlt = GlideSlopeGeometry.AltitudeAtDistance(rolloutDistNm, thresholdElev, ctx.Category);
            targetAlt = Math.Min(ctx.Aircraft.Altitude, gsAlt);

            // Spread the descent over the base leg actually ahead: the aircraft's present
            // cross-track from the final centerline. After a downwind that is the pattern
            // width; for a present-position entry (ERB with no distance, pattern retarget) it
            // is wherever the aircraft happens to be — sizing by the nominal width there dove
            // at the nominal rate and levelled off short of the turn.
            double deltaAlt = Math.Max(ctx.Aircraft.Altitude - targetAlt, 0);
            double baseLen = Math.Max(
                Math.Abs(GeoMath.SignedCrossTrackDistanceNm(ctx.Aircraft.Position, new LatLon(_thresholdLat, _thresholdLon), _finalHeading)),
                turnRadiusNm
            );
            double timeMin = baseLen / (plannedSpeedKt / 60.0);
            double computedRate = timeMin > 0 ? deltaAlt / timeMin : descentRate;
            descentRate = Math.Clamp(computedRate, descentRate, MaxDescentRateFpm(plannedSpeedKt, ctx.Category));
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
        // A controller speed assignment outranks the leg baseline (7110.65 §5-7-4).
        if (!ctx.Targets.HasExplicitSpeedCommand)
        {
            ctx.Targets.TargetSpeed = AircraftPerformance.BaseSpeed(ctx.AircraftType, ctx.Category);
        }

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
        // DownwindPhase.OnTick for the full rationale. A cancel can replace or
        // clear this phase list mid-tick — bail out when it fires.
        if (AirborneFollowHelper.CheckLeadLifecycle(ctx))
        {
            return false;
        }

        // Break off the follow and go around when the follower can no longer sequence
        // behind a much-slower lead by speed alone (structural overtake) and is closing
        // in trail. Checked before the speed adjustment so it pre-empts the helper's
        // at-min-speed cancel (which only clears the follow, without going around). The
        // base leg offers no room to recover, so go around early rather than overfly —
        // AIM 4-3-3 NOTE 1. ClearFollowState first so the go-around's pattern re-entry
        // doesn't immediately try to chase the same lead again.
        if (AirborneFollowHelper.ShouldBreakOffFollowForSpacing(ctx))
        {
            string lead = ctx.Aircraft.Approach.FollowingCallsign!;
            Log.LogDebug(
                "[Base] {Callsign}: breaking off follow on {Lead}, going around (unable to maintain separation)",
                ctx.Aircraft.Callsign,
                lead
            );
            AirborneFollowHelper.ClearFollowState(ctx.Aircraft);
            GoAroundHelper.Trigger(ctx, "unable to maintain separation");
            return false;
        }

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
        // Gate on the follow target, NOT on TargetSpeed: physics snaps TargetSpeed to
        // null once base speed is reached, so gating on it silently stops spacing for a
        // settled follower (the issue #206 overtake).
        if (ctx.Aircraft.Approach.FollowingCallsign is not null)
        {
            double baseline = AircraftPerformance.BaseSpeed(ctx.AircraftType, ctx.Category);
            double minSpeed = AircraftPerformance.ApproachSpeed(ctx.AircraftType, ctx.Category);
            var adjusted = AirborneFollowHelper.GetAdjustedSpeed(ctx, baseline, minSpeed, AirborneFollowHelper.MaxSpeedAdjustKts);
            if (adjusted is not null)
            {
                // Spacing only ever SLOWS the follower below the leg baseline; it never
                // speeds it up to chase a far lead (that carries excess speed into final
                // and trips the stabilized-approach gate — extend/hold handles a far lead).
                ctx.Targets.TargetSpeed = Math.Min(adjusted.Value, baseline);
            }
        }

        double crossTrack = Math.Abs(
            GeoMath.SignedCrossTrackDistanceNm(ctx.Aircraft.Position, new LatLon(_thresholdLat, _thresholdLon), _finalHeading)
        );

        // Turn initiation: begin turn when cross-track from extended centerline
        // equals the turn radius. This produces a geometrically correct 90° arc
        // that rolls out on centerline at the expected final approach distance.
        double turnRadiusNm = TurnRadiusNm(ctx.Aircraft.GroundSpeed, ctx.Category);
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
