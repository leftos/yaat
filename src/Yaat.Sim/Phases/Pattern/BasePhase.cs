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

    /// <summary>
    /// Where the aircraft was when this base leg began: the point a follower joining this aircraft's base flies to
    /// (<see cref="VfrFollowPhase"/>). Null before the leg starts, and for a leg restored from a snapshot written
    /// before the point was recorded.
    /// </summary>
    public LatLon? StartPoint { get; private set; }

    /// <summary>
    /// True while a follower is flying <see cref="BaseFollowSpacing.WidenOffBaseDeg"/> off its base heading, away from the
    /// field, to build spacing behind its lead (<see cref="BaseFollowSpacing"/>).
    /// </summary>
    public bool FollowWidenActive { get; private set; }

    public override string Name => "Base";
    public override bool ManagesSpeed => true;

    public override void OnStart(PhaseContext ctx)
    {
        StartPoint = ctx.Aircraft.Position;

        if (Waypoints is null)
        {
            return;
        }

        PatternReportHelper.EmitTurningLeg(ctx, ReportTrigger.Base);

        _finalHeading = Waypoints.FinalHeading;

        if (FinalDistanceNm is not null)
        {
            TrueHeading reciprocal = Waypoints.FinalHeading.ToReciprocal();
            (double Lat, double Lon) target = GeoMath.ProjectPoint(Waypoints.ThresholdLat, Waypoints.ThresholdLon, reciprocal, FinalDistanceNm.Value);
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

        // Circuit entry (downwind→base) carries no explicit FinalDistanceNm; derive it from
        // the aircraft's own along-track distance from the threshold along the final course.
        // At a normal circuit's base turn that equals the base extension; after an extended
        // downwind (TB well past the base-turn point) it reads how far out the aircraft
        // actually is, so the rollout aim doesn't assume the nominal geometry.
        PlanDescent(ctx, Waypoints, FinalDistanceNm);

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

    /// <summary>
    /// Plans the base's descent from where the aircraft is: to the 3° glidepath altitude at its rollout, spread over the base
    /// ahead. The rollout is one turn radius beyond <paramref name="finalDistanceNm"/> out, or beyond the aircraft's present
    /// distance out along the final when that is null (floored at one turn radius, so a degenerate position can't shrink the
    /// aim inside the turn itself).
    /// </summary>
    private void PlanDescent(PhaseContext ctx, PatternWaypoints waypoints, double? finalDistanceNm)
    {
        // Begin descent. Default rate; if the base→final geometry calls for a
        // steeper descent (SA-shortened final), compute one. The 90° base→final
        // turn translates the aircraft one turn-radius further along the
        // final, so rollout is at (finalDist + r) from the threshold.
        double descentRate = CategoryPerformance.PatternDescentRate(ctx.Category);
        double thresholdElev = ctx.Runway?.ElevationFt ?? ctx.FieldElevation;
        double plannedSpeedKt = PlannedSpeedKt(ctx.Aircraft, ctx.Category);
        double turnRadiusNm = TurnRadiusNm(plannedSpeedKt, ctx.Category);
        double finalDist =
            finalDistanceNm
            ?? Math.Max(
                GeoMath.AlongTrackDistanceNm(
                    ctx.Aircraft.Position,
                    new LatLon(waypoints.ThresholdLat, waypoints.ThresholdLon),
                    waypoints.FinalHeading.ToReciprocal()
                ),
                turnRadiusNm
            );

        // Aim for the 3° glide-slope altitude at rollout — stabilizes the
        // aircraft on the glide path the moment it rolls out on final,
        // regardless of whether base is short (SA-shortened, steep descent)
        // or long (extended base, no descent needed). Never aim higher
        // than current altitude — controllers issuing ELB/ERB to an
        // aircraft already below GS expect them to maintain or descend,
        // not climb.
        double rolloutDistNm = finalDist + turnRadiusNm;
        double gsAlt = GlideSlopeGeometry.AltitudeAtDistance(rolloutDistNm, thresholdElev, ctx.Category);
        double targetAlt = Math.Min(ctx.Aircraft.Altitude, gsAlt);

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

        ctx.Targets.DesiredVerticalRate = -descentRate;
        ctx.Targets.TargetAltitude = targetAlt;
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
            GoAroundForSpacing(ctx);
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

        // Spacing behind a lead on final, or on base ahead: keep the base, widen it away from the field, or break off to
        // pursuit. After the structural-overtake go-around above, which keeps precedence. A break-off replaces the phase list.
        if (ApplyFollowSpacing(ctx))
        {
            return false;
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
            double? adjusted = AirborneFollowHelper.GetAdjustedSpeed(ctx, baseline, minSpeed, AirborneFollowHelper.MaxSpeedAdjustKts);
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

    /// <summary>
    /// Ends the follow and goes around: the follower cannot keep its spacing behind the lead and the base leg leaves no room
    /// to recover (AIM 4-3-3 NOTE 1). The follow is cleared first so the go-around's pattern re-entry doesn't immediately
    /// chase the same lead again.
    /// </summary>
    private static void GoAroundForSpacing(PhaseContext ctx)
    {
        Log.LogDebug(
            "[Base] {Callsign}: breaking off follow on {Lead}, going around (unable to maintain separation)",
            ctx.Aircraft.Callsign,
            ctx.Aircraft.Approach.FollowingCallsign
        );
        AirborneFollowHelper.ClearFollowState(ctx.Aircraft);
        GoAroundHelper.Trigger(ctx, "unable to maintain separation");
    }

    /// <summary>
    /// Applies <see cref="BaseFollowSpacing"/>: a widen holds the present altitude and flies its heading until it ends, then
    /// the base heading again with the descent planned anew; a break-off installs the pursuit; a late break-off goes around.
    /// True when the follower broke off or went around and this phase is no longer on its phase list.
    /// </summary>
    private bool ApplyFollowSpacing(PhaseContext ctx)
    {
        if (Waypoints is null)
        {
            return false;
        }

        BaseFollowSpacingDecision decision = BaseFollowSpacing.Evaluate(ctx, Waypoints, FollowWidenActive);
        switch (decision.Action)
        {
            case BaseFollowSpacingAction.BreakOff:
                BreakOffForSpacing(ctx);
                return true;
            case BaseFollowSpacingAction.GoAround:
                GoAroundForSpacing(ctx);
                return true;
            case BaseFollowSpacingAction.Widen:
                StartFollowWiden(ctx);
                ctx.Targets.TargetTrueHeading = decision.WidenHeading;
                return false;
            default:
                EndFollowWiden(ctx, Waypoints);
                return false;
        }
    }

    /// <summary>A widen holds the altitude it starts at: the glidepath it was descending to belongs to the shorter final.</summary>
    private void StartFollowWiden(PhaseContext ctx)
    {
        if (FollowWidenActive)
        {
            return;
        }

        FollowWidenActive = true;
        ctx.Targets.TargetAltitude = ctx.Aircraft.Altitude;
        ctx.Targets.DesiredVerticalRate = null;
    }

    /// <summary>
    /// Ends a widen: the base heading again, and the descent planned from where the follower is, as the leg's start plans it
    /// (<see cref="PlanDescent"/>), to the glidepath at its now longer final.
    /// </summary>
    private void EndFollowWiden(PhaseContext ctx, PatternWaypoints waypoints)
    {
        if (!FollowWidenActive)
        {
            return;
        }

        FollowWidenActive = false;
        // With an OFL/OFR dogleg the dogleg has already set this tick's heading.
        if (LateralOffset is null)
        {
            ctx.Targets.TargetTrueHeading = waypoints.BaseHeading;
        }

        PlanDescent(ctx, waypoints, finalDistanceNm: null);
    }

    /// <summary>
    /// Breaks off the base to pursue the lead, with the pattern return to this circuit (<see cref="VfrFollowPhase"/>), which
    /// starts by turning out to the downwind heading and makes the one call for it
    /// (<see cref="VfrFollowPhase.RequestTurnOut"/>). The standing landing clearance carries over.
    /// </summary>
    private static void BreakOffForSpacing(PhaseContext ctx)
    {
        AircraftState aircraft = ctx.Aircraft;
        // BaseFollowSpacing breaks off only with a found lead landing this aircraft's assigned runway.
        string lead = aircraft.Approach.FollowingCallsign!;
        RunwayInfo runway = aircraft.Phases!.AssignedRunway!;
        FollowPatternReturn patternReturn = VfrFollowPhase.BuildFollowPatternReturn(aircraft, runway, ctx.GroundLayout);
        Log.LogDebug("[Base] {Callsign}: breaking off the base to turn out behind {Lead} for spacing", aircraft.Callsign, lead);
        CommandDispatcher.InstallVfrFollowPhase(aircraft, lead, patternReturn, climbOutGate: null).RequestTurnOut();
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
            Requirements = Requirements.Count > 0 ? [.. Requirements.Select(r => r.ToSnapshot())] : null,
            Waypoints = Waypoints?.ToSnapshot(),
            FinalDistanceNm = FinalDistanceNm,
            ThresholdLat = _thresholdLat,
            ThresholdLon = _thresholdLon,
            FinalHeadingDeg = _finalHeading.Degrees,
            LateralOffsetTargetNm = LateralOffset?.TargetNm,
            LateralOffsetDirection = LateralOffset is not null ? (int)LateralOffset.Direction : null,
            LateralOffsetAcquired = LateralOffset?.Acquired ?? false,
            StartLat = StartPoint?.Lat,
            StartLon = StartPoint?.Lon,
            FollowWidenActive = FollowWidenActive,
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
            Status = (PhaseStatus)dto.Status,
            ElapsedSeconds = dto.ElapsedSeconds,
            _thresholdLat = dto.ThresholdLat,
            _thresholdLon = dto.ThresholdLon,
            _finalHeading = new TrueHeading(dto.FinalHeadingDeg),
            StartPoint = (dto.StartLat is { } startLat) && (dto.StartLon is { } startLon) ? new LatLon(startLat, startLon) : null,
            FollowWidenActive = dto.FollowWidenActive ?? false,
        };
        return phase;
    }

    protected override List<ClearanceRequirement> CreateRequirements() => [];
}
