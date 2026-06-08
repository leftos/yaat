using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Line-up-and-wait phase. Drives the aircraft from its current hold-short
/// or taxiway pose onto the runway centerline, aligned with runway heading,
/// ready for takeoff roll. Two maneuver shapes are supported:
///
/// <list type="bullet">
///   <item><b>Aligned</b>: optional straight nose-out → closed-form fillet
///         arc → rollout straight. Used for normal hold-short geometries
///         (perpendicular or near-perpendicular hold-shorts) where a
///         standard-radius arc fits the available cross-track distance and
///         doesn't consume excessive along-runway length.</item>
///   <item><b>Pivot</b>: slow tight turn to perpendicular-toward-centerline
///         → perpendicular straight → slow tight turn to runway heading →
///         rollout. Used when the aircraft arrives at the runway boundary
///         with a shallow heading relative to the runway (issue #142:
///         UAL859 at SFO 01R with heading 22° right of runway heading and
///         324 ft left of centerline would have wasted 1861 ft of runway
///         on a straight path).</item>
/// </list>
///
/// <para>
/// The decision between aligned and pivot is made once at <see cref="OnStart"/>
/// via <see cref="LineUpGeometry.Compute"/> and drives the state machine
/// thereafter. A pose with aircraft past the arc entry, heading diverging
/// from centerline, or runway already behind produces a
/// <see cref="LineUpPathKind.Fault"/> plan; the phase enters <see cref="State.Faulted"/>
/// and remains stopped (<c>TickFaulted</c> returns false) until user
/// intervention via TAXI or CANCEL CLEARANCE.
/// </para>
///
/// <para>
/// During arc playback (both the aligned arc and pivot tight turns) the
/// phase writes position and heading directly from the arc integrator —
/// invariant I2 of the Design D lineup spec: the aircraft's pose cannot
/// drift off the circle and the tangent cannot drift from the radial by
/// construction, eliminating any risk of a "bearing-to-stop-node"
/// feedback-saturation failure during off-axis entries.
/// </para>
/// </summary>
public sealed class LineUpPhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("LineUpPhase");

    /// <summary>
    /// Observable state of the lineup state machine. Public so tests can
    /// assert the exact sequence of transitions without inspecting internals.
    /// </summary>
    public enum State
    {
        /// <summary>Before <see cref="OnStart"/> has built a plan.</summary>
        Setup,

        /// <summary>Aligned path: straight roll toward the arc entry tangent point.</summary>
        NoseOut,

        /// <summary>Aligned path: closed-form fillet-arc playback onto centerline.</summary>
        Arc,

        /// <summary>Pivot path: slow tight turn from aircraft heading to perpendicular-toward-centerline.</summary>
        PivotTurn1,

        /// <summary>Pivot path: straight roll perpendicular to runway until on centerline minus turn radius.</summary>
        PivotStraight,

        /// <summary>Pivot path: slow tight turn from perpendicular to runway heading, ending on centerline.</summary>
        PivotTurn2,

        /// <summary>Straight roll along runway heading to the stop point (shared by both paths).</summary>
        Rollout,

        /// <summary>Braking to zero at the stop point (LUAW only).</summary>
        Stop,

        /// <summary>Terminal: geometry could not be resolved; aircraft remains stopped.</summary>
        Faulted,
    }

    /// <summary>Ground-speed threshold (kts) at which arc playback is allowed to advance. I7: no pivot-in-place.</summary>
    private const double ArcSpeedFloorKts = 0.1;

    /// <summary>Distance-to-target threshold (ft) at which a straight segment transitions to its successor.</summary>
    private const double StraightArrivalFt = 3.0;

    /// <summary>Distance-to-stop threshold (ft) at which Rollout transitions to Stop.</summary>
    private const double RolloutArrivalFt = 2.0;

    /// <summary>Ground-speed threshold (kts) below which Stop declares the phase complete.</summary>
    private const double StopSpeedFloorKts = 0.5;

    /// <summary>
    /// Minimum indicated airspeed (kts) at which a mid-phase upgrade to rolling
    /// mode is accepted via <see cref="TryUpgradeToRolling"/>. Below this speed
    /// a real pilot is ~2 seconds from a full stop at normal taxi decel and
    /// has already committed to the stop; forcing a re-acceleration would feel
    /// jerky and unrealistic. Let the stop complete naturally instead.
    /// </summary>
    private const double RollingUpgradeMinSpeedKts = 5.0;

    /// <summary>
    /// Fraction of the rollout length past which a mid-phase upgrade is
    /// rejected even if the aircraft is above the speed threshold.
    /// </summary>
    private const double RollingUpgradeMaxRolloutFraction = 0.7;

    /// <summary>Current state machine position. Public-get for observability.</summary>
    public State CurrentState { get; private set; } = State.Setup;

    /// <summary>
    /// The plan computed at <see cref="OnStart"/>. Null until then and for
    /// <see cref="State.Faulted"/> where the plan could not be built.
    /// </summary>
    public LineUpPathPlan? PathPlan { get; private set; }

    /// <summary>
    /// Rolling takeoff mode. When true, <see cref="State.Rollout"/> holds
    /// speed at the plan's cruise speed instead of braking to zero, and the
    /// phase completes at the stop point for handoff to <see cref="TakeoffPhase"/>.
    /// Derived at <see cref="OnStart"/> from the phase list shape (next pending
    /// phase is TakeoffPhase ⇒ rolling) and mutable mid-phase via
    /// <see cref="TryUpgradeToRolling"/> (upgrade) or
    /// <see cref="DepartureClearanceHandler.TryCancelTakeoff"/> (revert via CTOC).
    /// </summary>
    public bool RollingMode { get; internal set; }

    /// <summary>
    /// Hold-in-position freeze. Set either by <see cref="DepartureClearanceHandler.TryCancelTakeoff"/>
    /// (CTOC, per 7110.65 §3-9-11 — cancelling a takeoff clearance means hold position) or by a
    /// HOLD command issued mid-line-up (<see cref="GroundCommandHandler.TryHoldPosition"/>). When
    /// true the aircraft stops at its current position and holds — it does not continue onto the
    /// runway centerline and does not complete the phase. Cleared when a fresh takeoff clearance
    /// re-clears the aircraft (<see cref="DepartureClearanceHandler.SatisfyUpcomingTakeoffClearance"/>)
    /// or a re-issued LUAW lifts the hold. RES does not clear it.
    /// </summary>
    public bool HoldPosition { get; internal set; }

    /// <summary>Working copy of the arc/slow-turn playback state. Valid during Arc, PivotTurn1, PivotTurn2.</summary>
    private LineUpArcPlayback _arcState;

    /// <summary>Target forward-speed cap for the active arc. Varies by state (aligned arc vs slow turn).</summary>
    private double _arcTargetSpeedKts;

    /// <summary>True once the Faulted state has logged its warning — prevents log spam.</summary>
    private bool _faultLogged;

    public override string Name => "LiningUp";
    public override bool ManagesSpeed => true;

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Aircraft.IsOnGround = true;

        // Derive rolling-takeoff mode from the phase list shape. Next pending
        // phase is TakeoffPhase/HelicopterTakeoffPhase ⇒ rolling (CTO was
        // in hand at insertion time and the LUAW phase was omitted).
        RollingMode = DetectRollingModeFromPhaseList(ctx);

        // For cross-runway closed traffic the aircraft lines up on the DEPARTURE
        // runway, not the pattern runway carried in AssignedRunway/ctx.Runway.
        var rwy = ctx.Aircraft.Phases?.DepartureRunway ?? ctx.Runway;

        if (rwy is null)
        {
            Fault(ctx, "ctx.Runway is null");
            return;
        }

        if (ctx.GroundLayout is null)
        {
            // Design decision (per user): LineUpPhase requires a ground
            // layout. The only "null layout" case we support is aircraft
            // spawned directly on a runway or in the air — those don't
            // reach LineUpPhase via the normal ground pipeline.
            Fault(ctx, "ctx.GroundLayout is null (LineUpPhase requires an airport ground layout)");
            return;
        }

        var plan = LineUpGeometry.Compute(rwy, ctx.Aircraft.Position.Lat, ctx.Aircraft.Position.Lon, ctx.Aircraft.TrueHeading, ctx.Category);
        PathPlan = plan;

        if (plan.Kind == LineUpPathKind.Fault)
        {
            Fault(ctx, plan.FaultReason ?? "unknown");
            return;
        }

        // Seed initial targets. The per-state tick methods overwrite these
        // each tick; the values here apply only until the first OnTick.
        ctx.Targets.TargetTrueHeading = new TrueHeading(plan.RunwayHeadingDeg);
        ctx.Targets.TargetSpeed = plan.ArcSpeedKts;

        if (plan.Kind == LineUpPathKind.Aligned)
        {
            if (plan.IsAlreadyAligned)
            {
                CurrentState = State.Rollout;
                Log.LogDebug("[LineUp] {Callsign}: already aligned, entering Rollout directly", ctx.Aircraft.Callsign);
            }
            else if (plan.NoseOutLengthFt < StraightArrivalFt)
            {
                EnterAlignedArc(plan);
                Log.LogDebug("[LineUp] {Callsign}: zero nose-out, entering Arc directly", ctx.Aircraft.Callsign);
            }
            else
            {
                CurrentState = State.NoseOut;
                ctx.Targets.TargetTrueHeading = new TrueHeading(plan.NoseOutBearingDeg);
                Log.LogDebug(
                    "[LineUp] {Callsign}: aligned path NoseOut (len={Len:F1}ft, arcSpeed={Speed:F1}kt, turn={Turn:F1}°)",
                    ctx.Aircraft.Callsign,
                    plan.NoseOutLengthFt,
                    plan.ArcSpeedKts,
                    plan.TurnAngleDeg
                );
            }
        }
        else // Pivot
        {
            EnterPivotTurn1(plan);
            Log.LogDebug(
                "[LineUp] {Callsign}: pivot path PivotTurn1 (turn={Turn:F1}°, straight={StraightLen:F1}ft)",
                ctx.Aircraft.Callsign,
                plan.TurnAngleDeg,
                plan.PivotStraightLengthFt
            );
        }
    }

    public override bool OnTick(PhaseContext ctx)
    {
        // CTOC mid-line-up: hold position where we are. Stop and stay in the
        // phase until a fresh takeoff clearance clears HoldPosition.
        if (HoldPosition)
        {
            ctx.Targets.TargetSpeed = 0;
            return false;
        }

        if (PathPlan is null)
        {
            return TickFaulted(ctx);
        }

        return CurrentState switch
        {
            State.NoseOut => TickStraight(
                ctx,
                PathPlan,
                PathPlan.NoseOutBearingDeg,
                PathPlan.NoseOutToLat,
                PathPlan.NoseOutToLon,
                PathPlan.ArcSpeedKts,
                onArrive: PathPlan.InitialArcState is null ? State.Rollout : State.Arc
            ),
            State.Arc => TickArcPlayback(ctx, PathPlan, onComplete: State.Rollout),
            State.PivotTurn1 => TickArcPlayback(ctx, PathPlan, onComplete: State.PivotStraight),
            State.PivotStraight => TickStraight(
                ctx,
                PathPlan,
                PathPlan.PivotStraightBearingDeg,
                PathPlan.PivotStraightToLat,
                PathPlan.PivotStraightToLon,
                CategoryPerformance.TaxiCornerSpeed(PathPlan.Category),
                onArrive: State.PivotTurn2
            ),
            State.PivotTurn2 => TickArcPlayback(ctx, PathPlan, onComplete: State.Rollout),
            State.Rollout => TickRollout(ctx, PathPlan),
            State.Stop => TickStop(ctx, PathPlan),
            State.Faulted => TickFaulted(ctx),
            _ => TickFaulted(ctx),
        };
    }

    private void EnterAlignedArc(LineUpPathPlan plan)
    {
        _arcState = plan.InitialArcState!.Value;
        _arcTargetSpeedKts = plan.ArcSpeedKts;
        CurrentState = State.Arc;
    }

    private void EnterPivotTurn1(LineUpPathPlan plan)
    {
        _arcState = ArcPlaybackFromSlowTurn(plan.PivotTurn1!);
        _arcTargetSpeedKts = plan.PivotTurn1!.MaxSpeedKts;
        CurrentState = State.PivotTurn1;
    }

    private void EnterPivotTurn2(LineUpPathPlan plan)
    {
        _arcState = ArcPlaybackFromSlowTurn(plan.PivotTurn2!);
        _arcTargetSpeedKts = plan.PivotTurn2!.MaxSpeedKts;
        CurrentState = State.PivotTurn2;
    }

    /// <summary>
    /// Bridge <see cref="PathPrimitiveSlowTurn"/> geometry into a
    /// <see cref="LineUpArcPlayback"/> so the arc-playback tick can reuse
    /// the same closed-form integrator for aligned fillets and pivot slow
    /// turns.
    /// </summary>
    private static LineUpArcPlayback ArcPlaybackFromSlowTurn(PathPrimitiveSlowTurn turn) =>
        new()
        {
            CenterLat = turn.CenterLat,
            CenterLon = turn.CenterLon,
            RadiusFt = turn.RadiusFt,
            CurrentBearingFromCenterDeg = turn.StartBearingFromCenterDeg,
            RemainingSweepDeg = turn.SweepDeg,
            RightTurn = turn.RightTurn,
        };

    private bool TickStraight(
        PhaseContext ctx,
        LineUpPathPlan plan,
        double bearingDeg,
        double toLat,
        double toLon,
        double targetSpeedKts,
        State onArrive
    )
    {
        ctx.Targets.TargetTrueHeading = new TrueHeading(bearingDeg);
        ctx.Targets.TargetSpeed = ClampBySpeedLimit(ctx, targetSpeedKts);

        double distFt = GeoMath.DistanceNm(ctx.Aircraft.Position, new LatLon(toLat, toLon)) * GeoMath.FeetPerNm;

        if (distFt < StraightArrivalFt)
        {
            Log.LogDebug("[LineUp] {Callsign}: {From} -> {To} (dist={D:F2}ft)", ctx.Aircraft.Callsign, CurrentState, onArrive, distFt);
            switch (onArrive)
            {
                case State.Arc:
                    EnterAlignedArc(plan);
                    break;
                case State.PivotTurn2:
                    EnterPivotTurn2(plan);
                    break;
                default:
                    CurrentState = onArrive;
                    break;
            }
        }

        return false;
    }

    private bool TickArcPlayback(PhaseContext ctx, LineUpPathPlan plan, State onComplete)
    {
        // I7 speed floor — arc refuses to advance when aircraft is stopped.
        // Physics is still driven by target speed so it re-accelerates us.
        double vKts = ctx.Aircraft.IndicatedAirspeed;
        if (vKts < ArcSpeedFloorKts)
        {
            ctx.Targets.TargetTrueHeading = new TrueHeading(_arcState.TangentHeadingDeg);
            ctx.Targets.TargetSpeed = ClampBySpeedLimit(ctx, _arcTargetSpeedKts);
            return false;
        }

        // Advance the arc by ds = v·dt, clamped to remaining sweep.
        double vFtPerSec = vKts * GeoMath.FeetPerNm / 3600.0;
        double dsFt = vFtPerSec * ctx.DeltaSeconds;
        double dAngleDeg = (dsFt / _arcState.RadiusFt) * (180.0 / Math.PI);
        _arcState.Advance(dAngleDeg);

        // Write pose directly from the closed-form state (invariant I2).
        var (lat, lon) = _arcState.CurrentPosition();
        ctx.Aircraft.Position = new LatLon(lat, lon);
        ctx.Aircraft.TrueHeading = new TrueHeading(_arcState.TangentHeadingDeg);

        ctx.Targets.TargetTrueHeading = new TrueHeading(_arcState.TangentHeadingDeg);
        ctx.Targets.TargetSpeed = ClampBySpeedLimit(ctx, _arcTargetSpeedKts);

        if (_arcState.IsComplete)
        {
            Log.LogDebug("[LineUp] {Callsign}: {From} -> {To} (arc complete)", ctx.Aircraft.Callsign, CurrentState, onComplete);
            switch (onComplete)
            {
                case State.PivotStraight:
                    CurrentState = State.PivotStraight;
                    break;
                case State.Rollout:
                    CurrentState = State.Rollout;
                    break;
                default:
                    CurrentState = onComplete;
                    break;
            }
        }

        return false;
    }

    private bool TickRollout(PhaseContext ctx, LineUpPathPlan plan)
    {
        // Rollout steers unconditionally on runway heading — no feedback loop.
        ctx.Targets.TargetTrueHeading = new TrueHeading(plan.RunwayHeadingDeg);

        double distToStopNm = GeoMath.DistanceNm(ctx.Aircraft.Position, new LatLon(plan.RolloutToLat, plan.RolloutToLon));
        double distToStopFt = distToStopNm * GeoMath.FeetPerNm;

        if (RollingMode)
        {
            // Hold cruise speed through the rollout; hand off to TakeoffPhase
            // at the stop point. Completion uses distance-from-start because
            // distance-to-stop increases past the stop point under rolling.
            ctx.Targets.TargetSpeed = ClampBySpeedLimit(ctx, plan.ArcSpeedKts);
            double distFromStartFt =
                GeoMath.DistanceNm(ctx.Aircraft.Position, new LatLon(plan.RolloutFromLat, plan.RolloutFromLon)) * GeoMath.FeetPerNm;
            if (distFromStartFt >= plan.RolloutLengthFt - RolloutArrivalFt)
            {
                Log.LogDebug(
                    "[LineUp] {Callsign}: Rollout complete (rolling, distFromStart={D:F2}ft, gs={Gs:F2}kt)",
                    ctx.Aircraft.Callsign,
                    distFromStartFt,
                    ctx.Aircraft.IndicatedAirspeed
                );
                return true;
            }
            return false;
        }

        // LUAW: kinematic brake curve v = sqrt(2·a·d).
        double decelKtPerSec = CategoryPerformance.TaxiDecelRate(plan.Category);
        double brakeSpeedKts = Math.Sqrt(2.0 * decelKtPerSec * distToStopNm * 3600.0);
        double targetSpeed = Math.Min(plan.ArcSpeedKts, brakeSpeedKts);
        ctx.Targets.TargetSpeed = ClampBySpeedLimit(ctx, targetSpeed);

        if (distToStopFt < RolloutArrivalFt)
        {
            CurrentState = State.Stop;
            Log.LogDebug(
                "[LineUp] {Callsign}: Rollout -> Stop (distToStop={D:F2}ft, gs={Gs:F2}kt)",
                ctx.Aircraft.Callsign,
                distToStopFt,
                ctx.Aircraft.IndicatedAirspeed
            );
        }

        return false;
    }

    private bool TickStop(PhaseContext ctx, LineUpPathPlan plan)
    {
        ctx.Targets.TargetTrueHeading = new TrueHeading(plan.RunwayHeadingDeg);
        ctx.Targets.TargetSpeed = 0;

        if (ctx.Aircraft.IndicatedAirspeed < StopSpeedFloorKts)
        {
            ctx.Aircraft.IndicatedAirspeed = 0;
            Log.LogDebug("[LineUp] {Callsign}: Stop complete, phase done", ctx.Aircraft.Callsign);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Tick while faulted. Holds the aircraft stopped indefinitely and
    /// returns false so the phase never auto-completes with a bad pose.
    /// Recovery is the user's responsibility (TAXI, CANCEL CLEARANCE).
    /// This replaces the earlier behaviour of returning true and advancing to
    /// TakeoffPhase with a bad pose — that was worse than staying parked.
    /// </summary>
    private bool TickFaulted(PhaseContext ctx)
    {
        if (!_faultLogged)
        {
            Log.LogWarning("[LineUp] {Callsign}: Faulted tick without prior Fault() call", ctx.Aircraft.Callsign);
            _faultLogged = true;
        }

        ctx.Targets.TargetSpeed = 0;
        return false;
    }

    private void Fault(PhaseContext ctx, string reason)
    {
        if (!_faultLogged)
        {
            Log.LogWarning("[LineUp] {Callsign}: Fault — {Reason}", ctx.Aircraft.Callsign, reason);
            _faultLogged = true;
        }
        CurrentState = State.Faulted;
        ctx.Targets.TargetSpeed = 0;
    }

    /// <summary>
    /// Return <paramref name="requested"/> clamped by any
    /// <see cref="AircraftState.GroundSpeedLimit"/> currently in effect.
    /// </summary>
    private static double ClampBySpeedLimit(PhaseContext ctx, double requested) =>
        ctx.Aircraft.Ground.SpeedLimit is { } limit ? Math.Min(requested, limit) : requested;

    /// <summary>
    /// Walk the aircraft's phase list and return true iff the next pending
    /// phase after this one is a <see cref="TakeoffPhase"/> or
    /// <see cref="HelicopterTakeoffPhase"/>. Signals that CTO was in hand at
    /// phase insertion time and LUAW was therefore omitted — the aircraft
    /// should roll through the stop point instead of braking to zero.
    /// </summary>
    private bool DetectRollingModeFromPhaseList(PhaseContext ctx)
    {
        var phases = ctx.Aircraft.Phases?.Phases;
        if (phases is null)
        {
            return false;
        }
        int selfIdx = phases.IndexOf(this);
        if (selfIdx < 0 || selfIdx + 1 >= phases.Count)
        {
            return false;
        }
        var next = phases[selfIdx + 1];
        return next is TakeoffPhase or HelicopterTakeoffPhase;
    }

    /// <summary>
    /// Return true iff the given aircraft type is eligible for a rolling
    /// takeoff per FAA 7110.65 §3-9-5.3. Super and Heavy aircraft are
    /// prohibited from rolling takeoffs (except during volcanic-ash ops,
    /// which YAAT does not model) because the controller wake-turbulence
    /// separation timers key off the moment full takeoff power is applied
    /// from a standing start. YAAT's <see cref="AircraftProfile.IsHeavy"/>
    /// flag covers both the Heavy and Super weight classes.
    /// </summary>
    public static bool IsAircraftEligibleForRollingTakeoff(string aircraftType)
    {
        var profile = AircraftProfileDatabase.Get(aircraftType);
        return profile is null || !profile.IsHeavy;
    }

    /// <summary>
    /// Attempt to upgrade this in-progress lineup from LUAW (brake-to-stop)
    /// mode to rolling mode. Called when CTO arrives while the phase is
    /// already active. Rejected when:
    /// <list type="bullet">
    ///   <item>the phase has not yet been started (<see cref="State.Setup"/>),</item>
    ///   <item>the phase is in <see cref="State.Stop"/> or <see cref="State.Faulted"/>,</item>
    ///   <item>the aircraft's indicated airspeed is below <see cref="RollingUpgradeMinSpeedKts"/>,</item>
    ///   <item>the aircraft is in <see cref="State.Rollout"/> and has covered more
    ///         than <see cref="RollingUpgradeMaxRolloutFraction"/> of the rollout length,</item>
    ///   <item>the aircraft type is not eligible for rolling takeoff per 7110.65 §3-9-5.3.</item>
    /// </list>
    /// </summary>
    public bool TryUpgradeToRolling(PhaseContext ctx)
    {
        if (RollingMode)
        {
            return true;
        }
        if (CurrentState is State.Setup or State.Stop or State.Faulted)
        {
            return false;
        }
        if (ctx.Aircraft.IndicatedAirspeed < RollingUpgradeMinSpeedKts)
        {
            return false;
        }
        if (!IsAircraftEligibleForRollingTakeoff(ctx.Aircraft.AircraftType))
        {
            Log.LogDebug(
                "[LineUp] {Callsign}: rolling upgrade rejected — type {Type} is Super/Heavy (7110.65 §3-9-5.3)",
                ctx.Aircraft.Callsign,
                ctx.Aircraft.AircraftType
            );
            return false;
        }
        if (CurrentState == State.Rollout && PathPlan is { } plan)
        {
            double distFromStartFt =
                GeoMath.DistanceNm(ctx.Aircraft.Position, new LatLon(plan.RolloutFromLat, plan.RolloutFromLon)) * GeoMath.FeetPerNm;
            if (distFromStartFt > RollingUpgradeMaxRolloutFraction * plan.RolloutLengthFt)
            {
                Log.LogDebug(
                    "[LineUp] {Callsign}: rolling upgrade rejected — past rollout fraction cap ({D:F1}ft / {L:F1}ft)",
                    ctx.Aircraft.Callsign,
                    distFromStartFt,
                    plan.RolloutLengthFt
                );
                return false;
            }
        }

        RollingMode = true;
        Log.LogDebug(
            "[LineUp] {Callsign}: upgraded to rolling mode mid-phase (state={State}, ias={Ias:F1}kt)",
            ctx.Aircraft.Callsign,
            CurrentState,
            ctx.Aircraft.IndicatedAirspeed
        );
        return true;
    }

    // ---- Snapshot ----
    // Non-round-tripping: ToSnapshot writes only the fields needed for
    // diagnostic continuity. FromSnapshot returns an instance that re-runs
    // OnStart on its next activation. A mid-phase snapshot/restore resumes
    // from the aircraft's current pose rather than from its saved state,
    // which is acceptable because the phase completes in seconds.

    public override PhaseDto ToSnapshot() =>
        new LineUpPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = Requirements.Count > 0 ? Requirements.Select(r => r.ToSnapshot()).ToList() : null,
            RunwayHeadingDeg = PathPlan?.RunwayHeadingDeg ?? 0,
            Initialized = PathPlan is not null,
            TimeSinceLastLog = 0,
            PerpHeadingDeg = 0,
            PerpAligned = false,
            OnCenterline = false,
            RollingMode = RollingMode,
            HoldPosition = HoldPosition,
        };

    public static LineUpPhase FromSnapshot(LineUpPhaseDto dto)
    {
        var phase = new LineUpPhase
        {
            Status = (PhaseStatus)dto.Status,
            ElapsedSeconds = dto.ElapsedSeconds,
            HoldPosition = dto.HoldPosition,
        };
        phase.RestoreRequirements(dto.Requirements);
        return phase;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.ClimbMaintain => CommandAcceptance.Allowed,
            CanonicalCommandType.DescendMaintain => CommandAcceptance.Allowed,
            CanonicalCommandType.CancelTakeoffClearance => CommandAcceptance.Allowed,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected(
                "aircraft is taxiing into position on the runway; only CM/DM, CTOC, or DEL apply until line-up completes"
            ),
        };
    }
}
