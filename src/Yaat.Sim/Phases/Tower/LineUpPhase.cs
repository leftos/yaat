using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Line Up and Wait phase: Design D closed-form plan playback. At
/// <see cref="OnStart"/> the phase calls
/// <see cref="LineUpPlanBuilder.TryBuild"/> to produce an immutable
/// <see cref="LineUpPlan"/>. Each <see cref="OnTick"/> call dispatches on
/// the current state and either writes control targets (<c>NoseOut</c>,
/// <c>Rollout</c>, <c>Stop</c>) or advances a closed-form arc integrator
/// and writes the aircraft pose directly (<c>Arc</c>).
///
/// <para>
/// The key structural property (Design D invariant I2) is that during
/// <see cref="State.Arc"/> the aircraft's position and heading are both
/// functions of a single scalar phase variable (the
/// <see cref="LineUpArcPlayback"/> state's current bearing from center).
/// They cannot drift apart, so the arc cannot produce a parallel-offset
/// exit regardless of entry error — invariant I3.
/// </para>
///
/// <para>
/// The phase never runs a feedback loop: no Stanley correction, no pure-
/// pursuit lookahead, no cross-track term. Rollout steers on runway heading
/// unconditionally, so the "bearing to stop-node from an off-centerline
/// start" failure mode that dogged the earlier analog implementation is
/// absent by construction.
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

        /// <summary>Straight roll toward the arc entry tangent point.</summary>
        NoseOut,

        /// <summary>Closed-form circular arc playback.</summary>
        Arc,

        /// <summary>Straight roll along runway heading to the stop point.</summary>
        Rollout,

        /// <summary>Braking to zero at the stop point.</summary>
        Stop,

        /// <summary>Terminal — plan build failed or a runtime invariant was violated.</summary>
        Faulted,
    }

    /// <summary>Ground-speed threshold (kts) at which the arc playback is allowed to advance.</summary>
    private const double ArcSpeedFloorKts = 0.1;

    /// <summary>Distance-to-target threshold (ft) at which NoseOut transitions to Arc.</summary>
    private const double NoseOutArrivalFt = 3.0;

    /// <summary>Distance-to-stop threshold (ft) at which Rollout transitions to Stop.</summary>
    private const double RolloutArrivalFt = 2.0;

    /// <summary>Ground-speed threshold (kts) at which Stop returns true (phase complete).</summary>
    private const double StopSpeedFloorKts = 0.5;

    /// <summary>
    /// Minimum indicated airspeed (kts) at which a mid-phase upgrade to rolling
    /// mode is accepted via <see cref="TryUpgradeToRolling"/>. Below this speed
    /// a real pilot is ~2 seconds from a full stop at normal taxi decel and
    /// has already committed to the stop; forcing a re-acceleration would
    /// feel jerky and unrealistic. Let the stop complete naturally instead.
    /// </summary>
    private const double RollingUpgradeMinSpeedKts = 5.0;

    /// <summary>
    /// Fraction of the rollout length past which a mid-phase upgrade is
    /// rejected even if the aircraft is above the speed threshold. Beyond
    /// this point the aircraft is only a handful of feet from the stop
    /// point and stopping is visually cleaner than re-accelerating across
    /// the residual rollout.
    /// </summary>
    private const double RollingUpgradeMaxRolloutFraction = 0.7;

    /// <summary>Current state machine position. Public-get for observability.</summary>
    public State CurrentState { get; private set; } = State.Setup;

    /// <summary>The plan that was built at <see cref="OnStart"/>. Null in <see cref="State.Faulted"/>.</summary>
    public LineUpPlan? Plan { get; private set; }

    /// <summary>
    /// Rolling takeoff mode. When true, <see cref="TickRollout"/> skips the
    /// kinematic brake curve and completes the phase at the stop point
    /// without entering <see cref="State.Stop"/>. The aircraft hands off to
    /// <see cref="TakeoffPhase"/> at roughly <see cref="LineUpPlan.ArcSpeedKts"/>,
    /// which TakeoffPhase then accelerates to Vr.
    ///
    /// Derived at <see cref="OnStart"/> from the phase list (next pending phase
    /// is a TakeoffPhase or HelicopterTakeoffPhase ⇒ rolling; LUAW present ⇒
    /// stop-then-go), or flipped mid-phase via <see cref="TryUpgradeToRolling"/>
    /// when CTO arrives while the aircraft is still above
    /// <see cref="RollingUpgradeMinSpeedKts"/>.
    /// </summary>
    public bool RollingMode { get; private set; }

    /// <summary>
    /// Working copy of the arc playback state. Initialised from
    /// <see cref="LineUpPlan.InitialArcState"/> on transition to
    /// <see cref="State.Arc"/> and mutated in place by <see cref="TickArc"/>.
    /// Invalid unless <see cref="CurrentState"/> is <see cref="State.Arc"/>.
    /// </summary>
    private LineUpArcPlayback _arcState;

    /// <summary>True once the Faulted state has logged its warning — prevents log spam.</summary>
    private bool _faultLogged;

    public override string Name => "LiningUp";
    public override bool ManagesSpeed => true;

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Aircraft.IsOnGround = true;

        // Derive rolling-takeoff mode from the phase list shape. If the next
        // pending phase after this one is a TakeoffPhase (i.e. LUAW was
        // omitted because CTO was in hand at insertion time), engage rolling
        // mode so TickRollout skips the kinematic brake curve. This check
        // runs once at phase start; snapshot round-trips re-derive it here.
        RollingMode = DetectRollingModeFromPhaseList(ctx);

        if (ctx.Runway is null)
        {
            Fault(ctx, "ctx.Runway is null");
            return;
        }

        var plan = LineUpPlanBuilder.TryBuild(ctx);
        if (plan is null)
        {
            Fault(ctx, "LineUpPlanBuilder returned null");
            return;
        }

        Plan = plan;

        // Initial control targets — driven by the first stage we will enter.
        ctx.Targets.TargetTrueHeading = new TrueHeading(plan.NoseOutBearingDeg);
        ctx.Targets.TargetSpeed = plan.ArcSpeedKts;

        if (plan.IsAlreadyAligned)
        {
            CurrentState = State.Rollout;
            Log.LogDebug("[LineUpV2] {Callsign}: already aligned, entering Rollout directly", ctx.Aircraft.Callsign);
        }
        else if (plan.NoseOutLengthFt < NoseOutArrivalFt)
        {
            _arcState = plan.InitialArcState!.Value;
            CurrentState = State.Arc;
            Log.LogDebug("[LineUpV2] {Callsign}: zero nose-out, entering Arc directly", ctx.Aircraft.Callsign);
        }
        else
        {
            CurrentState = State.NoseOut;
            Log.LogDebug(
                "[LineUpV2] {Callsign}: starting at NoseOut (length={Len:F1}ft, arcSpeed={Speed:F1}kt, turn={Turn:F1}deg)",
                ctx.Aircraft.Callsign,
                plan.NoseOutLengthFt,
                plan.ArcSpeedKts,
                plan.TurnAngleDeg
            );
        }
    }

    public override bool OnTick(PhaseContext ctx)
    {
        if (Plan is null)
        {
            return TickFaulted(ctx);
        }

        return CurrentState switch
        {
            State.NoseOut => TickNoseOut(ctx, Plan),
            State.Arc => TickArc(ctx, Plan),
            State.Rollout => TickRollout(ctx, Plan),
            State.Stop => TickStop(ctx, Plan),
            State.Faulted => TickFaulted(ctx),
            _ => TickFaulted(ctx),
        };
    }

    private bool TickNoseOut(PhaseContext ctx, LineUpPlan plan)
    {
        // Steer on the nose-out bearing (= aircraft's initial heading from plan
        // build time). Physics will accelerate IAS toward the arc cruise speed
        // unless GroundSpeedLimit caps it lower (conflict detection, taxiway
        // speed caps, etc. — see FlightPhysics).
        ctx.Targets.TargetTrueHeading = new TrueHeading(plan.NoseOutBearingDeg);
        ctx.Targets.TargetSpeed = ClampBySpeedLimit(ctx, plan.ArcSpeedKts);

        double distToEntryFt =
            GeoMath.DistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, plan.NoseOutToLat, plan.NoseOutToLon) * GeoMath.FeetPerNm;

        if (distToEntryFt < NoseOutArrivalFt)
        {
            _arcState = plan.InitialArcState!.Value;
            CurrentState = State.Arc;
            Log.LogDebug("[LineUpV2] {Callsign}: NoseOut -> Arc (distToEntry={D:F2}ft)", ctx.Aircraft.Callsign, distToEntryFt);
        }

        return false;
    }

    private bool TickArc(PhaseContext ctx, LineUpPlan plan)
    {
        // Guard against advancing the arc when the aircraft is effectively
        // stopped — invariant I7 (no pivot-in-place). Target speed is still
        // set so physics accelerates us out of this state.
        double vKts = ctx.Aircraft.IndicatedAirspeed;
        if (vKts < ArcSpeedFloorKts)
        {
            ctx.Targets.TargetTrueHeading = new TrueHeading(_arcState.TangentHeadingDeg);
            ctx.Targets.TargetSpeed = ClampBySpeedLimit(ctx, plan.ArcSpeedKts);
            return false;
        }

        // Advance the arc by the distance we will travel this tick.
        // ds = v·dt  (ft),   dθ = ds / r  (rad)
        double vFtPerSec = vKts * GeoMath.FeetPerNm / 3600.0;
        double dsFt = vFtPerSec * ctx.DeltaSeconds;
        double dAngleRad = dsFt / _arcState.RadiusFt;
        double dAngleDeg = dAngleRad * (180.0 / Math.PI);
        _arcState.Advance(dAngleDeg);

        // Write position and heading directly from the playback — invariant I2.
        var (lat, lon) = _arcState.CurrentPosition();
        ctx.Aircraft.Latitude = lat;
        ctx.Aircraft.Longitude = lon;
        ctx.Aircraft.TrueHeading = new TrueHeading(_arcState.TangentHeadingDeg);

        // Mirror heading/speed into Targets so physics sees "already there"
        // and doesn't issue a correction against the closed-form state.
        // Arc speed is clamped by GroundSpeedLimit if one is set.
        ctx.Targets.TargetTrueHeading = new TrueHeading(_arcState.TangentHeadingDeg);
        ctx.Targets.TargetSpeed = ClampBySpeedLimit(ctx, plan.ArcSpeedKts);

        if (_arcState.IsComplete)
        {
            CurrentState = State.Rollout;
            Log.LogDebug("[LineUpV2] {Callsign}: Arc -> Rollout (sweep complete)", ctx.Aircraft.Callsign);
        }

        return false;
    }

    private bool TickRollout(PhaseContext ctx, LineUpPlan plan)
    {
        // Rollout steers unconditionally on runway heading. NO feedback, NO
        // bearing-to-stop-node pure-pursuit. The aircraft may be sub-foot off
        // centerline from fp rounding at arc exit; we accept that and let the
        // final position sit within the 3-ft end-state tolerance.
        ctx.Targets.TargetTrueHeading = new TrueHeading(plan.RunwayHeadingDeg);

        double distToStopNm = GeoMath.DistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, plan.RolloutToLat, plan.RolloutToLon);
        double distToStopFt = distToStopNm * GeoMath.FeetPerNm;

        if (RollingMode)
        {
            // Rolling takeoff: do NOT brake. Hold arc cruise speed through
            // the rollout straight and hand off to TakeoffPhase at the stop
            // point. TakeoffPhase.TickGroundRoll picks up from the current
            // indicated airspeed and accelerates to Vr.
            ctx.Targets.TargetSpeed = ClampBySpeedLimit(ctx, plan.ArcSpeedKts);

            // Completion check: use distance-from-start (along the rollout
            // axis) rather than distance-to-stop. The aircraft rolls through
            // the stop point at full speed so distance-to-stop starts
            // increasing again past it — the simple "< RolloutArrivalFt"
            // LUAW check would never fire.
            double distFromStartFt =
                GeoMath.DistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, plan.RolloutFromLat, plan.RolloutFromLon) * GeoMath.FeetPerNm;
            if (distFromStartFt >= plan.RolloutLengthFt - RolloutArrivalFt)
            {
                Log.LogDebug(
                    "[LineUpV2] {Callsign}: Rollout complete (rolling, distFromStart={D:F2}ft, gs={Gs:F2}kt)",
                    ctx.Aircraft.Callsign,
                    distFromStartFt,
                    ctx.Aircraft.IndicatedAirspeed
                );
                return true;
            }
            return false;
        }

        // LUAW: kinematic brake curve to v=0 at stop point.
        //   v² = 2·a·d
        //   v (kts) = sqrt(2·a·d)  where a is kt/s, d is the "kinematic distance"
        // In the rest of the codebase this is expressed as
        //   v = sqrt(2·a·d_nm·3600)
        // which keeps units consistent.
        double decelKtPerSec = CategoryPerformance.TaxiDecelRate(plan.Category);
        double brakeSpeedKts = Math.Sqrt(2.0 * decelKtPerSec * distToStopNm * 3600.0);
        double targetSpeed = Math.Min(plan.ArcSpeedKts, brakeSpeedKts);
        ctx.Targets.TargetSpeed = ClampBySpeedLimit(ctx, targetSpeed);

        if (distToStopFt < RolloutArrivalFt)
        {
            CurrentState = State.Stop;
            Log.LogDebug(
                "[LineUpV2] {Callsign}: Rollout -> Stop (distToStop={D:F2}ft, gs={Gs:F2}kt)",
                ctx.Aircraft.Callsign,
                distToStopFt,
                ctx.Aircraft.IndicatedAirspeed
            );
        }

        return false;
    }

    private bool TickStop(PhaseContext ctx, LineUpPlan plan)
    {
        ctx.Targets.TargetTrueHeading = new TrueHeading(plan.RunwayHeadingDeg);
        ctx.Targets.TargetSpeed = 0;

        if (ctx.Aircraft.IndicatedAirspeed < StopSpeedFloorKts)
        {
            // Snap to exact zero so downstream phases see a cleanly stopped aircraft.
            ctx.Aircraft.IndicatedAirspeed = 0;
            Log.LogDebug("[LineUpV2] {Callsign}: Stop complete, phase done", ctx.Aircraft.Callsign);
            return true;
        }

        return false;
    }

    private bool TickFaulted(PhaseContext ctx)
    {
        if (!_faultLogged)
        {
            Log.LogWarning("[LineUpV2] {Callsign}: Faulted state tick without prior Fault() call", ctx.Aircraft.Callsign);
            _faultLogged = true;
        }

        ctx.Targets.TargetSpeed = 0;
        return true;
    }

    private void Fault(PhaseContext ctx, string reason)
    {
        if (!_faultLogged)
        {
            Log.LogWarning("[LineUpV2] {Callsign}: Fault — {Reason}", ctx.Aircraft.Callsign, reason);
            _faultLogged = true;
        }
        CurrentState = State.Faulted;
        ctx.Targets.TargetSpeed = 0;
    }

    /// <summary>
    /// Return <paramref name="requested"/> clamped by any
    /// <see cref="AircraftState.GroundSpeedLimit"/> currently in effect.
    /// Used by NoseOut, Arc, and Rollout so V2 cannot overrun traffic
    /// separation or airport-imposed taxi-speed caps.
    /// </summary>
    private static double ClampBySpeedLimit(PhaseContext ctx, double requested) =>
        ctx.Aircraft.GroundSpeedLimit is { } limit ? Math.Min(requested, limit) : requested;

    /// <summary>
    /// Walk the aircraft's phase list to find this phase and return true iff
    /// the next phase after it is a <see cref="TakeoffPhase"/> or
    /// <see cref="HelicopterTakeoffPhase"/>. Used as the implicit signal that
    /// CTO was already in hand when the tower sequence was inserted (so
    /// <see cref="LinedUpAndWaitingPhase"/> was omitted) and the aircraft
    /// should roll through instead of stopping.
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
    /// from a standing start. A rolling takeoff blurs that timing.
    ///
    /// YAAT's <see cref="AircraftProfile.IsHeavy"/> flag covers both the
    /// Heavy and Super weight classes (the profiles database does not
    /// currently distinguish the two). Aircraft without a profile entry
    /// fall through to "eligible" — matches the general-aviation default.
    /// </summary>
    public static bool IsAircraftEligibleForRollingTakeoff(string aircraftType)
    {
        var profile = AircraftProfileDatabase.Get(aircraftType);
        return profile is null || !profile.IsHeavy;
    }

    /// <summary>
    /// Attempt to upgrade this in-progress lineup from LUAW (brake-to-stop)
    /// mode to rolling mode. Called from
    /// <see cref="Commands.DepartureClearanceHandler.SatisfyUpcomingTakeoffClearance"/>
    /// when CTO arrives while <see cref="LineUpPhase"/> is already active.
    /// Returns true if the upgrade took effect (or was already active).
    /// Rejected when:
    ///   - the phase has not yet been started (<see cref="State.Setup"/>)
    ///   - the phase is already in <see cref="State.Stop"/> or
    ///     <see cref="State.Faulted"/>
    ///   - the aircraft's indicated airspeed is below
    ///     <see cref="RollingUpgradeMinSpeedKts"/>
    ///   - the aircraft is in <see cref="State.Rollout"/> and has already
    ///     covered more than <see cref="RollingUpgradeMaxRolloutFraction"/>
    ///     of the rollout length
    ///   - the aircraft type is not eligible for rolling takeoff per
    ///     7110.65 §3-9-5.3 (Super/Heavy aircraft prohibited)
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
                "[LineUpV2] {Callsign}: rolling upgrade rejected — aircraft type {Type} is Super/Heavy (7110.65 §3-9-5.3)",
                ctx.Aircraft.Callsign,
                ctx.Aircraft.AircraftType
            );
            return false;
        }
        if (CurrentState == State.Rollout && Plan is { } plan)
        {
            double distFromStartFt =
                GeoMath.DistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, plan.RolloutFromLat, plan.RolloutFromLon) * GeoMath.FeetPerNm;
            if (distFromStartFt > RollingUpgradeMaxRolloutFraction * plan.RolloutLengthFt)
            {
                Log.LogDebug(
                    "[LineUpV2] {Callsign}: rolling upgrade rejected — past rollout fraction cap ({D:F1}ft / {L:F1}ft)",
                    ctx.Aircraft.Callsign,
                    distFromStartFt,
                    plan.RolloutLengthFt
                );
                return false;
            }
        }

        RollingMode = true;
        Log.LogDebug(
            "[LineUpV2] {Callsign}: upgraded to rolling mode mid-phase (state={State}, ias={Ias:F1}kt)",
            ctx.Aircraft.Callsign,
            CurrentState,
            ctx.Aircraft.IndicatedAirspeed
        );
        return true;
    }

    // ---- Snapshot ----
    // Non-round-tripping: ToSnapshot writes minimal state; FromSnapshot
    // returns an instance that re-runs OnStart on its next activation. For a
    // mid-game snapshot/restore, the aircraft resumes the lineup from its
    // current pose rather than from its saved stage, which is acceptable
    // because the whole phase completes in a handful of seconds.

    public override PhaseDto ToSnapshot() =>
        new LineUpPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = Requirements.Count > 0 ? Requirements.Select(r => r.ToSnapshot()).ToList() : null,
            RunwayHeadingDeg = Plan?.RunwayHeadingDeg ?? 0,
            Initialized = Plan is not null,
            TimeSinceLastLog = 0,
            PerpHeadingDeg = 0,
            PerpAligned = false,
            OnCenterline = false,
            RollingMode = RollingMode,
        };

    public static LineUpPhase FromSnapshot(LineUpPhaseDto dto)
    {
        var phase = new LineUpPhase { Status = (PhaseStatus)dto.Status, ElapsedSeconds = dto.ElapsedSeconds };
        phase.RestoreRequirements(dto.Requirements);
        // Plan is null — the phase will re-enter OnStart logic on its next
        // activation. Status/ElapsedSeconds are preserved for continuity.
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
            _ => CommandAcceptance.Rejected,
        };
    }
}
