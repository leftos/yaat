using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Flare, touchdown, rollout, and handoff to <see cref="Ground.RunwayExitPhase"/>.
///
/// <para>
/// State machine with public <see cref="CurrentState"/> for test observability
/// (mirrors <see cref="LineUpPhase"/>'s pattern):
/// <c>StabilizedApproach → Flare → Touchdown → Rollout → Handoff</c>, with
/// <c>GoAround</c>, <c>Unable</c>, <c>FullStop</c>, and <c>Faulted</c> as
/// branching terminals.
/// </para>
///
/// <para>
/// Flare is <b>closed-form AGL-indexed playback</b>: the descent rate and
/// airspeed targets are pure functions of current AGL, not of elapsed time or
/// history. This matches LineUpPhase's Design D invariant I2 (position and
/// heading are functions of a single scalar phase variable) and eliminates
/// the floating-landing risk that a constant-rate flare has.
/// </para>
///
/// <para>
/// The phase <b>never writes aircraft pose or IAS directly</b>. All speed
/// changes flow through <c>Targets.TargetSpeed</c> plus
/// <c>Targets.DesiredDecelRate</c> (when firm-braking is required), and the
/// shared <see cref="FlightPhysics"/> integrator does the actual work.
/// Rollout never approaches the 0.1 kt ground-rotation guard because it
/// hands off at <c>coastSpeed + 3 kt</c>.
/// </para>
/// </summary>
public sealed class LandingPhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("LandingPhase");

    // --- Braking / steering constants ---

    private const double CenterlineGainDegPerNm = 150.0;
    private const double MaxCenterlineCorrectionDeg = 10.0;
    private const double FirmBrakingRateKtsPerSec = 5.0;
    private const double ComfortableBrakingMultiplier = 1.5;
    private const double MinSoftBrakingRateKtsPerSec = 0.5;
    private const double TurnOffSpeedToleranceKts = 3.0;

    // --- Stabilization gate (FSF ALAR Briefing Note 7.1; FAA InFO 11009 endorses FSF criteria) ---

    private const double StabilizedSpeedFactor = 1.3; // above 1.3·Vref → unstabilized
    private const double StabilizedBankDeg = 15.0;
    private const double StabilizedVsiFpm = -1200.0;
    private const double StabilizedXteNm = 0.08;
    private const double StabilizedGraceSeconds = 1.0;

    // --- Float-on-arrival (short-approach rollout) ---
    //
    // When LandingPhase activates while the aircraft is still rolling out from
    // a tight base→final turn (e.g. after `SA`), the wings-level/heading-aligned
    // gate isn't met. Per AIM 4-3-3 the pilot may vary pattern size; on a short
    // approach the pilot floats down the runway while wings level out before
    // touchdown — the runway is long enough to absorb the delay. We hold level
    // flight and suppress the stab gate while heading-error from the runway
    // exceeds RolloutHeadingErrorDeg, capped at MaxFloatDistanceNm past the
    // threshold so a misaligned approach can still trigger GA.

    private const double RolloutHeadingErrorDeg = 5.0;
    private const double MaxFloatDistanceNm = 0.5; // ~3000 ft — more than half a typical GA runway

    /// <summary>
    /// Observable sub-state of the landing phase. Public so tests can assert
    /// the exact sequence of transitions. Mirrors <see cref="LineUpPhase.State"/>.
    /// </summary>
    public enum State
    {
        /// <summary>Post-threshold, pre-flare. Holds runway heading / approach speed / glideslope vsi.</summary>
        StabilizedApproach,

        /// <summary>AGL ≤ FlareAltitude. Closed-form vsi(agl) + spd(agl), no feedback.</summary>
        Flare,

        /// <summary>Single-tick atomic transition: IsOnGround = true, snap altitude.</summary>
        Touchdown,

        /// <summary>Ground rollout with bounded XTE steering and kinematic braking plan.</summary>
        Rollout,

        /// <summary>Ready to hand off to RunwayExitPhase. Commits preference and returns true.</summary>
        Handoff,

        /// <summary>Missed exit — broadcast, relax preference, re-search.</summary>
        Unable,

        /// <summary>No runway/graph fallback, or all exits exhausted. Brake to zero on centerline.</summary>
        FullStop,

        /// <summary>Go-around triggered via command or stabilization failure. Hands off to GoAroundPhase.</summary>
        GoAround,

        /// <summary>Unrecoverable state (null runway at OnStart). Logs and returns true.</summary>
        Faulted,
    }

    private LandingPlan? _plan;

    /// <summary>Immutable plan built at OnStart. Null before construction or in Faulted state.</summary>
    public LandingPlan? Plan => _plan;

    /// <summary>Current sub-state. Read-only except from within this class.</summary>
    public State CurrentState { get; private set; } = State.StabilizedApproach;

    // Cross-tick state
    private bool _canGoAround;
    private double _lahsoHoldShortDistNm;
    private bool _hasLahso;
    private double _stabilizedSinceSec;
    private double _touchdownLat;
    private double _touchdownLon;
    private bool _floatingForRollout;

    // Exit resolution state
    private ResolvedExitInfo? _candidateExit;
    private ExitPreference? _activePreference;
    private ExitPreference? _originalPreference;
    private bool _exitResolutionEnabled;
    private ExitSide? _inferredSide;
    private readonly HashSet<int> _unableBranchPoints = [];
    private bool _unableBroadcast;

    /// <summary>The currently committed candidate exit chosen by the rollout planner. Null before resolution.</summary>
    public ResolvedExitInfo? CandidateExit => _candidateExit;

    /// <summary>The inferred preferred side from runway/parking layout, or null if undetermined.</summary>
    public ExitSide? InferredSide => _inferredSide;

    public bool StoppedForLahso { get; private set; }

    public override string Name => "Landing";

    public override PhaseDto ToSnapshot() =>
        new LandingPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = Requirements.Count > 0 ? Requirements.Select(r => r.ToSnapshot()).ToList() : null,
            FieldElevation = _plan?.FieldElevation ?? 0,
            RunwayHeadingDeg = _plan?.RunwayHeading.Degrees ?? 0,
            ThresholdLat = _plan?.ThresholdLat ?? 0,
            ThresholdLon = _plan?.ThresholdLon ?? 0,
            TouchedDown = CurrentState is State.Rollout or State.Unable or State.FullStop or State.Handoff,
            CanGoAround = _canGoAround,
            LahsoHoldShortDistNm = _lahsoHoldShortDistNm,
            HasLahso = _hasLahso,
            CandidateExitHoldShortId = _candidateExit?.HoldShortNode.Id,
            CandidateExitBranchPointId = _candidateExit?.BranchPointNode.Id,
            CandidateExitTaxiway = _candidateExit?.TaxiwayName,
            CandidateExitTurnOffSpeed = _candidateExit?.TurnOffSpeed ?? 0,
            CandidateExitPathNodeIds = _candidateExit?.Path.Select(n => n.Id).ToList(),
            ActivePreferenceSide = (int?)_activePreference?.Side,
            ActivePreferenceTaxiway = _activePreference?.Taxiway,
            OriginalPreferenceSide = (int?)_originalPreference?.Side,
            OriginalPreferenceTaxiway = _originalPreference?.Taxiway,
            ExitResolutionEnabled = _exitResolutionEnabled,
            StoppedForLahso = StoppedForLahso,
            CurrentStateValue = (int)CurrentState,
            TouchdownLat = _touchdownLat,
            TouchdownLon = _touchdownLon,
            StabilizedSinceSec = _stabilizedSinceSec,
            UnableBranchPointIds = _unableBranchPoints.Count > 0 ? [.. _unableBranchPoints] : null,
            InferredSideValue = (int?)_inferredSide,
        };

    public static LandingPhase FromSnapshot(LandingPhaseDto dto, AirportGroundLayout? groundLayout)
    {
        var phase = new LandingPhase();
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        phase.RestoreRequirements(dto.Requirements);
        phase._canGoAround = dto.CanGoAround;
        phase._lahsoHoldShortDistNm = dto.LahsoHoldShortDistNm;
        phase._hasLahso = dto.HasLahso;
        phase._exitResolutionEnabled = dto.ExitResolutionEnabled;
        phase.StoppedForLahso = dto.StoppedForLahso;
        phase.CurrentState = (State)dto.CurrentStateValue;
        phase._touchdownLat = dto.TouchdownLat;
        phase._touchdownLon = dto.TouchdownLon;
        phase._stabilizedSinceSec = dto.StabilizedSinceSec;
        if (dto.UnableBranchPointIds is not null)
        {
            foreach (int id in dto.UnableBranchPointIds)
            {
                phase._unableBranchPoints.Add(id);
            }
        }
        if (dto.InferredSideValue.HasValue)
        {
            phase._inferredSide = (ExitSide)dto.InferredSideValue.Value;
        }
        if (dto.ActivePreferenceSide.HasValue || dto.ActivePreferenceTaxiway is not null)
        {
            phase._activePreference = new ExitPreference
            {
                Side = dto.ActivePreferenceSide.HasValue ? (ExitSide)dto.ActivePreferenceSide.Value : null,
                Taxiway = dto.ActivePreferenceTaxiway,
            };
        }
        if (dto.OriginalPreferenceSide.HasValue || dto.OriginalPreferenceTaxiway is not null)
        {
            phase._originalPreference = new ExitPreference
            {
                Side = dto.OriginalPreferenceSide.HasValue ? (ExitSide)dto.OriginalPreferenceSide.Value : null,
                Taxiway = dto.OriginalPreferenceTaxiway,
            };
        }
        if (
            groundLayout is not null
            && dto.CandidateExitHoldShortId.HasValue
            && dto.CandidateExitBranchPointId.HasValue
            && dto.CandidateExitTaxiway is not null
            && groundLayout.Nodes.TryGetValue(dto.CandidateExitHoldShortId.Value, out var holdShortNode)
            && groundLayout.Nodes.TryGetValue(dto.CandidateExitBranchPointId.Value, out var branchPointNode)
        )
        {
            List<GroundNode> path = [];
            if (dto.CandidateExitPathNodeIds is not null)
            {
                foreach (int nodeId in dto.CandidateExitPathNodeIds)
                {
                    if (groundLayout.Nodes.TryGetValue(nodeId, out var pathNode))
                    {
                        path.Add(pathNode);
                    }
                }
            }
            phase._candidateExit = new ResolvedExitInfo
            {
                HoldShortNode = holdShortNode,
                BranchPointNode = branchPointNode,
                TaxiwayName = dto.CandidateExitTaxiway,
                TurnOffSpeed = dto.CandidateExitTurnOffSpeed,
                Path = path,
            };
        }

        // Plan is not round-tripped; rebuild from DTO fields on first tick if the phase is restored mid-state.
        if (dto.RunwayHeadingDeg != 0 || dto.ThresholdLat != 0)
        {
            phase._plan = new LandingPlan
            {
                FieldElevation = dto.FieldElevation,
                RunwayHeading = new TrueHeading(dto.RunwayHeadingDeg),
                ThresholdLat = dto.ThresholdLat,
                ThresholdLon = dto.ThresholdLon,
                // Category constants aren't round-tripped — restored phase needs OnStart context to rebuild.
                // These defaults are jet-shaped; if the restored phase needs to continue flare/rollout,
                // OnTick will regenerate its plan via the category table when the first non-restoration
                // tick runs. For most practical replays, landing state restores post-touchdown.
                FlareEntryAgl = 30,
                FlareFpm = 200,
                Vref = 140,
                Vtd = 135,
                CoastSpeed = 40,
                DefaultDecel = 2.5,
                TouchdownAgl = 2,
            };
        }

        return phase;
    }

    public override void OnStart(PhaseContext ctx)
    {
        _plan = BuildPlan(ctx);

        if (ctx.Runway is null)
        {
            CurrentState = State.Faulted;
            Log.LogWarning("[Landing] {Callsign}: no runway at OnStart, faulting", ctx.Aircraft.Callsign);
            return;
        }

        // Capture LAHSO target if set
        if (ctx.Aircraft.Phases?.LahsoHoldShort is { } lahso)
        {
            _hasLahso = true;
            _lahsoHoldShortDistNm = lahso.DistFromThresholdNm;
        }

        _originalPreference = ctx.Aircraft.Phases?.RequestedExit;
        _activePreference = _originalPreference;
        _exitResolutionEnabled = _originalPreference is not null;

        // Infer a side preference from the runway's high-speed exit layout and parking
        // proximity. Applied when no side is set (default selection or after unable-replan).
        if (
            (_activePreference?.Side is null)
            && (ctx.GroundLayout is not null)
            && (ctx.Aircraft.Phases?.AssignedRunway?.Designator is { } rwyDesignator)
        )
        {
            _inferredSide = ctx.GroundLayout.InferPreferredExitSide(rwyDesignator, _plan.RunwayHeading);
            if ((_inferredSide is not null) && (_activePreference?.Taxiway is null))
            {
                _activePreference = new ExitPreference { Side = _inferredSide.Value };
            }
        }

        // Continue approach descent toward field elevation
        ctx.Targets.TargetAltitude = _plan.FieldElevation;

        // Choose initial state based on current AGL
        double agl = ctx.Aircraft.Altitude - _plan.FieldElevation;
        CurrentState =
            ctx.Aircraft.IsOnGround ? State.Rollout
            : agl <= _plan.FlareEntryAgl ? State.Flare
            : State.StabilizedApproach;

        Log.LogDebug(
            "[Landing] {Callsign}: started, fieldElev={Elev:F0}ft, gs={Gs:F1}kts, state={State}{Lahso}",
            ctx.Aircraft.Callsign,
            _plan.FieldElevation,
            ctx.Aircraft.GroundSpeed,
            CurrentState,
            _hasLahso ? $", LAHSO hold-short at {_lahsoHoldShortDistNm:F2}nm" : ""
        );
    }

    private static LandingPlan BuildPlan(PhaseContext ctx)
    {
        var rwy = ctx.Runway;
        return new LandingPlan
        {
            FieldElevation = ctx.FieldElevation,
            RunwayHeading = rwy?.TrueHeading ?? ctx.Aircraft.TrueHeading,
            ThresholdLat = rwy?.ThresholdLatitude ?? ctx.Aircraft.Position.Lat,
            ThresholdLon = rwy?.ThresholdLongitude ?? ctx.Aircraft.Position.Lon,
            RunwayId = ctx.Aircraft.Phases?.AssignedRunway?.Designator,
            FlareEntryAgl = CategoryPerformance.FlareAltitude(ctx.Category),
            FlareFpm = CategoryPerformance.FlareDescentRate(ctx.Category),
            Vref = CategoryPerformance.ApproachSpeed(ctx.Category),
            Vtd = AircraftPerformance.TouchdownSpeed(ctx.Aircraft.AircraftType, ctx.Category),
            CoastSpeed = CategoryPerformance.RolloutCoastSpeed(ctx.Category),
            DefaultDecel = CategoryPerformance.RolloutDecelRate(ctx.Category),
            TouchdownAgl = ctx.Category == AircraftCategory.Helicopter ? 0 : 2,
        };
    }

    public override bool OnTick(PhaseContext ctx)
    {
        if (_plan is null)
        {
            return true; // Faulted — should not happen if OnStart ran
        }

        return CurrentState switch
        {
            State.StabilizedApproach => TickStabilizedApproach(ctx, _plan),
            State.Flare => TickFlare(ctx, _plan),
            State.Touchdown => TickTouchdown(ctx, _plan),
            State.Rollout => TickRollout(ctx, _plan),
            State.Handoff => TickHandoff(ctx, _plan),
            State.Unable => TickUnable(ctx, _plan),
            State.FullStop => TickFullStop(ctx, _plan),
            State.GoAround => true,
            State.Faulted => true,
            _ => true,
        };
    }

    // --- Airborne states ---

    private bool TickStabilizedApproach(PhaseContext ctx, LandingPlan plan)
    {
        double agl = ctx.Aircraft.Altitude - plan.FieldElevation;

        // Write approach targets: runway heading + bounded XTE crab toward centerline,
        // mirroring TickRollout. Without this crab, the aircraft commits to runway heading
        // alone — fine if it crossed the threshold exactly on centerline, but for offset
        // approaches the FAC-to-centerline lateral convergence is still completing in
        // FinalApproachPhase. Picking up its bearing-derived crab here keeps the heading
        // continuous across the handoff, avoiding a snap that trips the bank-stab gate.
        ctx.Targets.TargetTrueHeading = ComputeCenterlineSteeringTarget(ctx, plan);
        // Speed: don't accelerate above current IAS. FinalApproachPhase handles the
        // approach deceleration profile; if the aircraft arrives at the flare window
        // at 126 kts after a continuous-descent approach, targeting Vref (140) would
        // push speed UP mid-approach. Clamp to min(Vref, IAS) so we only trim overspeed.
        ctx.Targets.TargetSpeed = Math.Min(plan.Vref, ctx.Aircraft.IndicatedAirspeed);
        // TargetAltitude = fieldElevation was set at OnStart and is maintained by FinalApproachPhase's predecessor.

        if (IsRollingOutOverRunway(ctx, plan))
        {
            HoldLevelDuringRollout(ctx);
            _floatingForRollout = true;
            _stabilizedSinceSec = 0;
            return false;
        }

        if (_floatingForRollout)
        {
            // Float ended — restore the descent target so the aircraft resumes
            // its descent toward the runway instead of holding the float altitude.
            ctx.Targets.TargetAltitude = plan.FieldElevation;
            ctx.Targets.DesiredVerticalRate = null;
            _floatingForRollout = false;
        }

        // Stabilization gate
        CheckStabilizationGate(ctx, plan);
        if (CurrentState == State.GoAround)
        {
            return false; // GoAroundHelper.Trigger handles the handoff; PhaseList advances next tick
        }

        // Transition to Flare when AGL drops to flare entry altitude
        if (agl <= plan.FlareEntryAgl)
        {
            CurrentState = State.Flare;
        }

        return false;
    }

    /// <summary>
    /// Returns true when the aircraft entered LandingPhase while still rolling out
    /// from a tight turn (e.g. short-approach base→final). Heading error from the
    /// runway centerline exceeds <see cref="RolloutHeadingErrorDeg"/> and the
    /// aircraft is within <see cref="MaxFloatDistanceNm"/> past the threshold so
    /// the float doesn't extend forever down the runway.
    /// </summary>
    private static bool IsRollingOutOverRunway(PhaseContext ctx, LandingPlan plan)
    {
        if (ctx.Aircraft.IsOnGround)
        {
            return false;
        }

        double headingError = Math.Abs(NormalizeAngle180(ctx.Aircraft.TrueHeading.Degrees - plan.RunwayHeading.Degrees));
        if (headingError <= RolloutHeadingErrorDeg)
        {
            return false;
        }

        // Cap the float so a genuinely misaligned approach doesn't fly down the
        // entire runway — the stab gate still applies past the cap.
        double bearing = GeoMath.BearingTo(new LatLon(plan.ThresholdLat, plan.ThresholdLon), ctx.Aircraft.Position);
        double alongTrack = NormalizeAngle180(bearing - plan.RunwayHeading.Degrees);
        bool pastThreshold = Math.Abs(alongTrack) <= 90.0;
        if (!pastThreshold)
        {
            return true; // not yet over the runway → keep floating to reach centerline
        }

        double distFromThresholdNm = GeoMath.DistanceNm(ctx.Aircraft.Position, new LatLon(plan.ThresholdLat, plan.ThresholdLon));
        return distFromThresholdNm <= MaxFloatDistanceNm;
    }

    /// <summary>
    /// Holds level flight while the aircraft completes its rollout. No descent,
    /// no flare progression — let bank/heading bleed off naturally before the
    /// stab gate (and the flare entry) re-engage.
    /// </summary>
    private static void HoldLevelDuringRollout(PhaseContext ctx)
    {
        ctx.Targets.TargetAltitude = ctx.Aircraft.Altitude;
        ctx.Targets.DesiredVerticalRate = 0;
    }

    private static double NormalizeAngle180(double degrees)
    {
        double a = degrees % 360.0;
        if (a > 180.0)
        {
            a -= 360.0;
        }
        else if (a < -180.0)
        {
            a += 360.0;
        }
        return a;
    }

    private bool TickFlare(PhaseContext ctx, LandingPlan plan)
    {
        if (IsRollingOutOverRunway(ctx, plan))
        {
            // Defer the flare playback until wings level — see TickStabilizedApproach.
            HoldLevelDuringRollout(ctx);
            ctx.Targets.TargetTrueHeading = ComputeCenterlineSteeringTarget(ctx, plan);
            ctx.Targets.TargetSpeed = Math.Min(plan.Vref, ctx.Aircraft.IndicatedAirspeed);
            CurrentState = State.StabilizedApproach;
            _floatingForRollout = true;
            _stabilizedSinceSec = 0;
            return false;
        }

        double agl = ctx.Aircraft.Altitude - plan.FieldElevation;

        // Closed-form AGL-indexed playback. Invariant I2: vsi and spd are pure
        // functions of current AGL, not of elapsed time or history.
        // fraction = 0 at flare entry, 1 at touchdown (agl = 0).
        double fraction = Math.Clamp(1.0 - agl / plan.FlareEntryAgl, 0.0, 1.0);
        ctx.Targets.DesiredVerticalRate = -plan.FlareFpm * (1.0 - fraction);
        // Ramp target from the Vref→Vtd curve but never push speed UP above current
        // IAS: a continuous-descent approach may arrive below Vref, and adding energy
        // in the flare would be a bug. IAS is monotone-decreasing in the flare so the
        // clamp has no time-dependent state — still effectively closed-form.
        double rampTarget = plan.Vref - ((plan.Vref - plan.Vtd) * fraction);
        ctx.Targets.TargetSpeed = Math.Min(rampTarget, ctx.Aircraft.IndicatedAirspeed);
        ctx.Targets.TargetTrueHeading = ComputeCenterlineSteeringTarget(ctx, plan);

        // Still monitor stabilization — a sudden disqualification in the flare
        // triggers a balked-landing go-around if speed allows.
        CheckStabilizationGate(ctx, plan);
        if (CurrentState == State.GoAround)
        {
            return false;
        }

        // Touchdown gate: AGL below threshold AND not climbing. The old
        // implementation checked only AGL <= 0; V2 widens the AGL bound to
        // catch one sub-tick of descent at the peak flare rate
        // (flareFpm × 0.25s / 60 ≈ 0.83 ft) without requiring a floating
        // float. We deliberately do NOT gate on IAS: synthetic fixtures
        // and LAHSO scenarios can arrive at the flare window at any speed,
        // and physics at AGL ≤ 0 is already on the ground regardless.
        bool touchdownGate = agl <= plan.TouchdownAgl && ctx.Aircraft.VerticalSpeed <= 0;
        if (touchdownGate)
        {
            CurrentState = State.Touchdown;
            return TickTouchdown(ctx, plan);
        }

        return false;
    }

    private bool TickTouchdown(PhaseContext ctx, LandingPlan plan)
    {
        ctx.Aircraft.IsOnGround = true;
        ctx.Aircraft.Altitude = plan.FieldElevation;
        ctx.Aircraft.VerticalSpeed = 0;
        ctx.Targets.TargetAltitude = null;
        ctx.Targets.DesiredVerticalRate = null;

        if (ctx.Aircraft.CompletionReason == Training.CompletionReason.Active)
        {
            ctx.Aircraft.CompletedAtSeconds = ctx.ScenarioElapsedSeconds;
            ctx.Aircraft.CompletionReason = Training.CompletionReason.Landed;
            ctx.Aircraft.CompletionDetail = plan.RunwayId;
        }

        // Snap IAS down to Vtd if flare overshot it — prevents rollout from starting too fast.
        if (ctx.Aircraft.IndicatedAirspeed > plan.Vtd)
        {
            ctx.Aircraft.IndicatedAirspeed = plan.Vtd;
        }

        _touchdownLat = ctx.Aircraft.Position.Lat;
        _touchdownLon = ctx.Aircraft.Position.Lon;

        Log.LogDebug("[Landing] {Callsign}: touchdown, gs={Gs:F1}kts", ctx.Aircraft.Callsign, ctx.Aircraft.GroundSpeed);

        CurrentState = State.Rollout;
        return false;
    }

    // --- Ground states ---

    private bool TickRollout(PhaseContext ctx, LandingPlan plan)
    {
        // Steer along runway centerline with a bounded proportional XTE bias.
        // Safe from the FlightPhysics.StationaryGroundSpeedKts guard because
        // rollout hands off at coastSpeed ≥ 15 kt, never approaching 0.1 kt.
        ctx.Targets.TargetTrueHeading = ComputeCenterlineSteeringTarget(ctx, plan);
        // Use ground turn rate so XTE corrections apply at taxi cadence, not
        // airborne cadence. Cleared when the phase hands off to RunwayExitPhase.
        ctx.Targets.TurnRateOverride = CategoryPerformance.GroundTurnRate(ctx.Category);

        // Re-resolve candidate from scratch if the controller changed the preference mid-rollout
        var currentPref = ctx.Aircraft.Phases?.RequestedExit;
        if (currentPref != _originalPreference)
        {
            _originalPreference = currentPref;
            _activePreference = currentPref;
            _candidateExit = null;
            _exitResolutionEnabled = currentPref is not null;
        }

        // Default decel rate — may be bumped by braking plan or LAHSO
        double decelRate = plan.DefaultDecel;

        // LAHSO: enforce stop at the hold-short distance
        if (_hasLahso)
        {
            double distFromThreshold = GeoMath.DistanceNm(ctx.Aircraft.Position, new LatLon(plan.ThresholdLat, plan.ThresholdLon));
            double distToHoldShort = _lahsoHoldShortDistNm - distFromThreshold;

            if ((distToHoldShort > 0) && (ctx.Aircraft.IndicatedAirspeed > 1.0))
            {
                double lahsoDecel = ComputeRequiredDecel(ctx.Aircraft.GroundSpeed, 0, distToHoldShort);
                if (lahsoDecel > decelRate)
                {
                    decelRate = lahsoDecel;
                }
            }
            else if (distToHoldShort <= 0)
            {
                // Past the hold-short point — enforce immediate stop via a sub-coast target.
                ctx.Targets.TargetSpeed = 0;
                ctx.Targets.DesiredDecelRate = FirmBrakingRateKtsPerSec;
                if (ctx.Aircraft.IndicatedAirspeed <= 0.5)
                {
                    StoppedForLahso = true;
                    Log.LogDebug("[Landing] {Callsign}: LAHSO stop", ctx.Aircraft.Callsign);
                    return true;
                }
                return false;
            }
        }

        // Coast speed: decelerate to this speed and hold it while searching for exits
        double coastSpeed = plan.CoastSpeed;

        // Always search for the next exit ahead — even without an explicit
        // preference, the pilot plans deceleration for the first reachable exit.
        if (_candidateExit is null)
        {
            ResolveNextCandidate(ctx, plan);
        }

        if (_candidateExit is not null)
        {
            double distToBranchPoint = GeoMath.AlongTrackDistanceNm(
                _candidateExit.BranchPointNode.Position,
                ctx.Aircraft.Position,
                plan.RunwayHeading
            );

            // Missed-exit conditions: past branch AND (too fast OR standard exit at branch)
            double highSpeedTurnOff = CategoryPerformance.HighSpeedExitSpeed(ctx.Category);
            bool tooFast = ctx.Aircraft.IndicatedAirspeed > _candidateExit.TurnOffSpeed + TurnOffSpeedToleranceKts;
            bool standardExitAtBranch = _candidateExit.TurnOffSpeed < highSpeedTurnOff;

            if ((distToBranchPoint <= 0) && (tooFast || standardExitAtBranch))
            {
                MarkExitUnable(ctx);
                CurrentState = State.Unable;
                return false;
            }
        }

        // Speed planning: if we have a candidate exit ahead, compute the required
        // decel to reach its turn-off speed by the branch point.
        double targetSpeed = coastSpeed;
        // Start at the rollout decel rate (2.5 kt/s jet, 1.5 kt/s piston). We
        // always set an override rather than leaving it null — otherwise
        // FlightPhysics would fall back to AircraftPerformance.DecelRate, which
        // is the airborne rate, not the ground rollout rate. The exit-planner
        // below raises this when the turn-off requires harder braking or lowers
        // it when the exit is far enough away that the default would overshoot.
        double decelRateOverride = plan.DefaultDecel;
        if (_candidateExit is not null)
        {
            double distToBranch = GeoMath.AlongTrackDistanceNm(_candidateExit.BranchPointNode.Position, ctx.Aircraft.Position, plan.RunwayHeading);

            if ((distToBranch > 0) && (ctx.Aircraft.IndicatedAirspeed > _candidateExit.TurnOffSpeed))
            {
                double requiredDecel = ComputeRequiredDecel(ctx.Aircraft.GroundSpeed, _candidateExit.TurnOffSpeed, distToBranch);
                double brakingLimit = _exitResolutionEnabled ? FirmBrakingRateKtsPerSec : plan.DefaultDecel * ComfortableBrakingMultiplier;

                if (requiredDecel <= brakingLimit)
                {
                    // Plan speed: down to the exit's turnoff speed if it is slower
                    // than coast (e.g. 12-kt standard exits for a piston whose coast
                    // is 25 kt). RunwayExitPhase still handles the final braking through
                    // the turn, but letting LandingPhase drop below coast is what allows
                    // a slow piston to actually take a 90° midfield exit — otherwise the
                    // missed-exit check at distToBranch≤0 always fires for standard exits.
                    targetSpeed = Math.Min(coastSpeed, _candidateExit.TurnOffSpeed);

                    // Raise the decel rate if the direct turn-off requires firmer
                    // braking than the default — can't make the exit otherwise.
                    if (requiredDecel > decelRateOverride)
                    {
                        decelRateOverride = requiredDecel;
                    }

                    // Reserve distance for RunwayExitPhase to brake from coast to
                    // turn-off speed. Aim to reach coast speed at (branch - buffer),
                    // not at the branch itself.
                    double brakingBufferNm = ComputeBrakingDistance(coastSpeed, _candidateExit.TurnOffSpeed, plan.DefaultDecel);
                    double effectiveDist = distToBranch - brakingBufferNm;

                    // Gentle decel when the exit is far enough that normal braking
                    // would reach coast speed too early. Lower the rate so the
                    // aircraft stays fast longer and arrives at coast near the exit.
                    if (effectiveDist > 0)
                    {
                        double requiredDecelToCoast = ComputeRequiredDecel(ctx.Aircraft.GroundSpeed, coastSpeed, effectiveDist);
                        if ((requiredDecelToCoast > 0) && (requiredDecelToCoast < decelRateOverride))
                        {
                            decelRateOverride = Math.Max(requiredDecelToCoast, MinSoftBrakingRateKtsPerSec);
                        }
                    }
                }
            }
        }

        // Don't brake below coast speed — that's RunwayExitPhase's job.
        if (ctx.Aircraft.IndicatedAirspeed <= targetSpeed)
        {
            targetSpeed = ctx.Aircraft.IndicatedAirspeed; // freeze at current
        }

        ctx.Targets.TargetSpeed = targetSpeed;
        ctx.Targets.DesiredDecelRate = decelRateOverride;

        var cat = AircraftCategorization.Categorize(ctx.Aircraft.AircraftType);
        _canGoAround = ctx.Aircraft.IndicatedAirspeed >= CategoryPerformance.RejectedLandingMinSpeed(cat);

        // Handoff gate — aircraft must be at or below coast speed. For standard
        // exits (large turn angle) the branch must be at least 0.02 nm ahead to
        // leave room for a proper turn arc; otherwise we block handoff and keep
        // coasting until the next exit is resolved.
        bool handoffBlocked = false;
        if ((_candidateExit is not null) && (ctx.Aircraft.IndicatedAirspeed <= coastSpeed))
        {
            double distToBranch = GeoMath.AlongTrackDistanceNm(_candidateExit.BranchPointNode.Position, ctx.Aircraft.Position, plan.RunwayHeading);

            double hsExitSpeed = CategoryPerformance.HighSpeedExitSpeed(ctx.Category);
            bool isStandardExit = _candidateExit.TurnOffSpeed < hsExitSpeed;
            if (isStandardExit && (distToBranch < 0.02))
            {
                handoffBlocked = true;
            }
        }

        if (!_hasLahso && !handoffBlocked && (ctx.Aircraft.IndicatedAirspeed <= coastSpeed))
        {
            CurrentState = State.Handoff;
            return TickHandoff(ctx, plan);
        }

        return false;
    }

    private bool TickHandoff(PhaseContext ctx, LandingPlan plan)
    {
        // Commit relaxed preference back to the aircraft so RunwayExitPhase sees it
        if (ctx.Aircraft.Phases is not null)
        {
            ctx.Aircraft.Phases.RequestedExit = _activePreference;

            // Commit the resolved exit so RunwayExitPhase uses it directly
            // rather than re-searching from the handoff position. Re-searching
            // can miss the committed exit whenever the handoff position sits
            // past the specific centerline node that branches to this exit —
            // even though the hold-short itself is still geometrically ahead.
            //
            // RunwayExitPhase requires Path.Count >= 2 to build the exit route
            // (virtual approach segment + at least one taxiway edge). When the
            // candidate came from the straight-line fallback (Path = [single
            // node]), leave ResolvedExit null so RunwayExitPhase falls back to
            // its own analog search.
            if (_candidateExit is { Path.Count: >= 2 })
            {
                ctx.Aircraft.Phases.ResolvedExit = _candidateExit;
            }
        }

        // Clear decel and turn-rate overrides so RunwayExitPhase starts clean;
        // it will re-set them from its own category constants in TickRolling.
        ctx.Targets.DesiredDecelRate = null;
        ctx.Targets.TurnRateOverride = null;

        Log.LogDebug(
            "[Landing] {Callsign}: handoff at ({Lat:F6},{Lon:F6}) gs={Gs:F1}kts pref={Pref} candidate={Cand}",
            ctx.Aircraft.Callsign,
            ctx.Aircraft.Position.Lat,
            ctx.Aircraft.Position.Lon,
            ctx.Aircraft.GroundSpeed,
            _activePreference is null ? "(none)" : $"{_activePreference.Taxiway ?? "?"}/{_activePreference.Side?.ToString() ?? "?"}",
            _candidateExit is null ? "(none)" : $"{_candidateExit.TaxiwayName} branchId={_candidateExit.BranchPointNode.Id}"
        );

        return true;
    }

    private bool TickUnable(PhaseContext ctx, LandingPlan plan)
    {
        // Missed an exit — relax our internal preference and re-resolve.
        _candidateExit = null;

        // If the controller has sent a NEW preference since we entered Unable
        // (e.g., "ER H" after the pilot already passed a different exit), honor
        // it over our relaxation. TickRollout's own preference-change detection
        // runs AFTER TickUnable in the state machine, so if we don't check here
        // we risk clobbering the user's command with `Phases.RequestedExit = null`
        // before TickRollout ever sees it.
        var userPref = ctx.Aircraft.Phases?.RequestedExit;
        if (userPref != _originalPreference)
        {
            _originalPreference = userPref;
            _activePreference = userPref;
            _exitResolutionEnabled = userPref is not null;
        }
        else
        {
            // Preserve the user's side if one was originally set (EL/ER), drop
            // only the specific taxiway. We intentionally do NOT overwrite
            // `Phases.RequestedExit` — that's the user's intent and should remain
            // visible for diagnostics; relaxation is a LandingPhase-internal
            // concern tracked in `_activePreference`.
            var keepSide = _originalPreference?.Side;
            _activePreference = keepSide is not null ? new ExitPreference { Side = keepSide } : null;
            _originalPreference = _activePreference;
            _exitResolutionEnabled = false;
        }

        // Back to rollout to look for the next exit
        CurrentState = State.Rollout;
        return TickRollout(ctx, plan);
    }

    private bool TickFullStop(PhaseContext ctx, LandingPlan plan)
    {
        // Brake to zero along runway heading. No exit found or LAHSO-like stop.
        ctx.Targets.TargetTrueHeading = plan.RunwayHeading;
        ctx.Targets.TargetSpeed = 0;
        ctx.Targets.DesiredDecelRate = plan.DefaultDecel;

        if (ctx.Aircraft.IndicatedAirspeed <= 0.5)
        {
            Log.LogDebug("[Landing] {Callsign}: full-stop rollout complete", ctx.Aircraft.Callsign);
            return true;
        }

        return false;
    }

    // --- Helpers ---

    /// <summary>
    /// Runway centerline steering target = runway heading + bounded proportional XTE
    /// correction. Shared by StabilizedApproach, Flare, and Rollout so the heading
    /// command is continuous across the FinalApproach→LandingPhase handoff and across
    /// flare/touchdown — for an aircraft still converging laterally onto the centerline
    /// (offset approach), the bearing-derived crab in <see cref="FinalApproachPhase"/>
    /// matches the runway-heading-plus-correction computed here, avoiding a snap that
    /// would trip the bank-angle stabilization gate.
    /// </summary>
    private static TrueHeading ComputeCenterlineSteeringTarget(PhaseContext ctx, LandingPlan plan)
    {
        double signedXte = GeoMath.SignedCrossTrackDistanceNm(
            ctx.Aircraft.Position,
            new LatLon(plan.ThresholdLat, plan.ThresholdLon),
            plan.RunwayHeading
        );
        double correction = Math.Clamp(signedXte * CenterlineGainDegPerNm, -MaxCenterlineCorrectionDeg, MaxCenterlineCorrectionDeg);
        return new TrueHeading(plan.RunwayHeading.Degrees - correction);
    }

    private void CheckStabilizationGate(PhaseContext ctx, LandingPlan plan)
    {
        double signedXte = GeoMath.SignedCrossTrackDistanceNm(
            ctx.Aircraft.Position,
            new LatLon(plan.ThresholdLat, plan.ThresholdLon),
            plan.RunwayHeading
        );

        double ias = ctx.Aircraft.IndicatedAirspeed;
        double vrefLimit = plan.Vref * StabilizedSpeedFactor;
        double bank = Math.Abs(ctx.Aircraft.BankAngle);
        double vs = ctx.Aircraft.VerticalSpeed;
        double xteFt = Math.Abs(signedXte) * 6076.12;

        List<string>? failures = null;
        if (ias > vrefLimit)
        {
            (failures ??= []).Add($"IAS {ias:F0} > {vrefLimit:F0} kt (1.3·Vref)");
        }
        if (Math.Abs(signedXte) > StabilizedXteNm)
        {
            (failures ??= []).Add($"{xteFt:F0} ft off centerline");
        }
        if (bank > StabilizedBankDeg)
        {
            (failures ??= []).Add($"bank {bank:F0}°");
        }
        if (vs < StabilizedVsiFpm)
        {
            (failures ??= []).Add($"descent {-vs:F0} fpm");
        }

        if (failures is not null)
        {
            _stabilizedSinceSec += ctx.DeltaSeconds;
            if (_stabilizedSinceSec >= StabilizedGraceSeconds)
            {
                GoAroundHelper.Trigger(ctx, $"unstable: {string.Join(", ", failures)}");
                CurrentState = State.GoAround;
            }
        }
        else
        {
            _stabilizedSinceSec = 0;
        }
    }

    private void MarkExitUnable(PhaseContext ctx)
    {
        if (_candidateExit is null)
        {
            return;
        }

        string missedTaxiway = _candidateExit.TaxiwayName;
        Log.LogDebug(
            "[Landing] {Callsign}: missed exit {Taxiway} (gs={Gs:F1}kts > {TurnOff:F0}kts)",
            ctx.Aircraft.Callsign,
            missedTaxiway,
            ctx.Aircraft.GroundSpeed,
            _candidateExit.TurnOffSpeed
        );

        if ((_originalPreference?.Taxiway is not null) && !_unableBroadcast)
        {
            Pilot.PilotResponder.RouteSoloOrRpoTransmission(
                ctx.Aircraft,
                ctx.SoloTrainingMode,
                ctx.RpoShowPilotSpeech,
                ctx.StudentPositionType,
                Pilot.PilotResponder.BuildUnableToExit(ctx.Aircraft, missedTaxiway),
                $"{ctx.Aircraft.Callsign} unable to exit at {missedTaxiway}",
                Pilot.PilotResponder.SoloPositionsTower
            );
            _unableBroadcast = true;
        }

        _unableBranchPoints.Add(_candidateExit.BranchPointNode.Id);
    }

    private void ResolveNextCandidate(PhaseContext ctx, LandingPlan plan)
    {
        if (ctx.GroundLayout is null)
        {
            return;
        }

        string? rwyDesignator = ctx.Aircraft.Phases?.AssignedRunway?.Designator;
        if (rwyDesignator is not null)
        {
            var searchPref = _activePreference;

            // Try inferred side first for taxiway-only preferences
            if ((_activePreference is { Taxiway: not null, Side: null }) && (_inferredSide is not null))
            {
                searchPref = new ExitPreference { Taxiway = _activePreference.Taxiway, Side = _inferredSide.Value };
            }

            // Effective side preference (explicit beats inferred). Used to decide
            // whether to defer an off-side candidate while looking forward for an
            // on-side option further down the runway.
            ExitSide? sidePref = _activePreference?.Side ?? _inferredSide;

            // Pass occupancy info to the planner only for default selection (no
            // explicit taxiway). When the controller named a specific exit, the
            // pilot brakes for it regardless and RunwayExitPhase deals with any
            // late-breaking occupancy at handoff. For default selection, the
            // planner can do better by routing around known-occupied exits.
            HashSet<int>? excludeHoldShortNodes = (_activePreference?.Taxiway is null) ? ctx.OccupiedHoldShortNodes : null;

            // Skip exits whose required braking exceeds the comfort limit from
            // the current position (via Skip verdict, which excludes the entire
            // taxiway from the rest of this call). Without this, the planner
            // would return the first forward exit unconditionally — typically a
            // 90° standard exit too close to brake to its turn-off speed
            // comfortably. Skipping uncomfortable candidates lets the planner
            // commit to a reachable downstream exit (e.g. a high-speed at ~45°)
            // and brake decisively for it.
            double comfortLimit = _exitResolutionEnabled ? FirmBrakingRateKtsPerSec : plan.DefaultDecel * ComfortableBrakingMultiplier;

            var found = TryFindCandidate(ctx, plan, rwyDesignator, searchPref, sidePref, excludeHoldShortNodes, comfortLimit);

            // Fall back to taxiway-only if inferred-side found nothing
            if ((found is null) && (searchPref != _activePreference))
            {
                found = TryFindCandidate(ctx, plan, rwyDesignator, _activePreference, sidePref, excludeHoldShortNodes, comfortLimit);
            }

            if (found is { } resolved)
            {
                _candidateExit = resolved;
                Log.LogDebug(
                    "[Landing] {Callsign}: candidate exit {Taxiway}, turnOffSpeed={Speed:F0}kts",
                    ctx.Aircraft.Callsign,
                    resolved.TaxiwayName,
                    resolved.TurnOffSpeed
                );
                return;
            }
        }

        // Fallback: straight-line search (airports without hold-short data)
        var result = ctx.GroundLayout.FindExitAheadOnRunway(
            ctx.Aircraft.Position.Lat,
            ctx.Aircraft.Position.Lon,
            plan.RunwayHeading,
            _activePreference,
            rwyDesignator
        );

        if (result is null)
        {
            return;
        }

        double? fallbackAngle = ctx.GroundLayout.ComputeExitAngle(result.Value.Node, result.Value.Taxiway, plan.RunwayHeading);
        double fallbackTurnOffSpeed = CategoryPerformance.ExitTurnOffSpeed(ctx.Category, fallbackAngle);

        _candidateExit = new ResolvedExitInfo
        {
            HoldShortNode = result.Value.Node,
            TaxiwayName = result.Value.Taxiway,
            TurnOffSpeed = fallbackTurnOffSpeed,
            Path = [result.Value.Node],
            BranchPointNode = result.Value.Node,
        };
    }

    /// <summary>
    /// Run the side-preferred lookahead search with a comfort-braking filter.
    /// Returns null when no candidate (on-side or off-side fallback) is reachable
    /// from the current state.
    /// </summary>
    private ResolvedExitInfo? TryFindCandidate(
        PhaseContext ctx,
        LandingPlan plan,
        string rwyDesignator,
        ExitPreference? searchPref,
        ExitSide? sidePref,
        HashSet<int>? excludeHoldShortNodes,
        double comfortLimit
    )
    {
        if (ctx.GroundLayout is null)
        {
            return null;
        }

        var found = ctx.GroundLayout.FindOnSidePreferredExit(
            ctx.Aircraft.Position.Lat,
            ctx.Aircraft.Position.Lon,
            plan.RunwayHeading,
            rwyDesignator,
            searchPref,
            sidePref,
            excludeBranchPoints: _unableBranchPoints.Count > 0 ? new HashSet<int>(_unableBranchPoints) : null,
            excludeHoldShortNodes: excludeHoldShortNodes,
            filter: candidate =>
            {
                double turnOffSpeed = CategoryPerformance.ExitTurnOffSpeed(ctx.Category, candidate.ExitAngle);
                var branchNode = candidate.Path[0];
                double distToBranch = GeoMath.AlongTrackDistanceNm(branchNode.Position, ctx.Aircraft.Position, plan.RunwayHeading);

                // Branch is at or behind the aircraft — try the next centerline.
                // Skip the entire taxiway so we don't keep finding the same one
                // via the BFS cluster expansion.
                if (distToBranch <= 0)
                {
                    return AirportGroundLayout.CandidateVerdict.Skip;
                }

                bool alreadySlowEnough = ctx.Aircraft.IndicatedAirspeed <= turnOffSpeed + TurnOffSpeedToleranceKts;
                bool comfortablyReachable =
                    alreadySlowEnough || (ComputeRequiredDecel(ctx.Aircraft.GroundSpeed, turnOffSpeed, distToBranch) <= comfortLimit);

                if (!comfortablyReachable)
                {
                    Log.LogDebug(
                        "[Landing] {Callsign}: skipping exit {Taxiway} (angle={Angle:F0}, turnOff={Speed:F0}kts, dist={Dist:F3}nm) — required decel exceeds comfort limit at gs={Gs:F1}kts",
                        ctx.Aircraft.Callsign,
                        candidate.Taxiway,
                        candidate.ExitAngle,
                        turnOffSpeed,
                        distToBranch,
                        ctx.Aircraft.GroundSpeed
                    );
                    return AirportGroundLayout.CandidateVerdict.Skip;
                }

                return AirportGroundLayout.CandidateVerdict.Accept;
            }
        );

        if (found is null)
        {
            return null;
        }

        var branch = found.Value.Path[0];
        double turnOff = CategoryPerformance.ExitTurnOffSpeed(ctx.Category, found.Value.ExitAngle);
        return new ResolvedExitInfo
        {
            HoldShortNode = found.Value.HoldShort,
            TaxiwayName = found.Value.Taxiway,
            TurnOffSpeed = turnOff,
            Path = found.Value.Path,
            BranchPointNode = branch,
        };
    }

    /// <summary>
    /// Compute required deceleration (kts/sec) to go from current ground speed to target speed
    /// over the given distance. Uses kinematic equation: v_final² = v_initial² - 2·a·d.
    /// </summary>
    private static double ComputeRequiredDecel(double currentGroundSpeedKts, double targetSpeedKts, double distanceNm)
    {
        double currentFps = currentGroundSpeedKts * 6076.12 / 3600.0;
        double targetFps = targetSpeedKts * 6076.12 / 3600.0;
        double distFt = distanceNm * 6076.12;

        if (distFt <= 0)
        {
            return FirmBrakingRateKtsPerSec;
        }

        double requiredDecelFps2 = (currentFps * currentFps - targetFps * targetFps) / (2.0 * distFt);
        return requiredDecelFps2 * 3600.0 / 6076.12;
    }

    /// <summary>
    /// Compute distance (nm) required to brake from one speed to another at a given decel rate.
    /// Inverse of ComputeRequiredDecel: d = (v_i² - v_f²) / (2·a).
    /// </summary>
    private static double ComputeBrakingDistance(double fromSpeedKts, double toSpeedKts, double decelRateKtsPerSec)
    {
        if (decelRateKtsPerSec <= 0)
        {
            return 0;
        }

        double fromFps = fromSpeedKts * 6076.12 / 3600.0;
        double toFps = toSpeedKts * 6076.12 / 3600.0;
        double decelFps2 = decelRateKtsPerSec * 6076.12 / 3600.0;
        double distFt = (fromFps * fromFps - toFps * toFps) / (2.0 * decelFps2);
        return distFt / 6076.12;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        if (CurrentState is State.StabilizedApproach or State.Flare)
        {
            // Airborne — go-around allowed, exit preference allowed, nothing else
            return cmd switch
            {
                CanonicalCommandType.GoAround => CommandAcceptance.Allowed,
                CanonicalCommandType.ExitLeft => CommandAcceptance.Allowed,
                CanonicalCommandType.ExitRight => CommandAcceptance.Allowed,
                CanonicalCommandType.ExitTaxiway => CommandAcceptance.Allowed,
                CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
                _ => CommandAcceptance.Rejected("aircraft is committed to landing on stabilized approach / flare; only GA, EL/ER/EXIT, or DEL apply"),
            };
        }

        // Ground — exit preference allowed, go-around only if still fast enough
        return cmd switch
        {
            CanonicalCommandType.ExitLeft => CommandAcceptance.Allowed,
            CanonicalCommandType.ExitRight => CommandAcceptance.Allowed,
            CanonicalCommandType.ExitTaxiway => CommandAcceptance.Allowed,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.GoAround => _canGoAround
                ? CommandAcceptance.Allowed
                : CommandAcceptance.Rejected("aircraft is below the go-around speed gate after touchdown; GA is no longer available"),
            _ => CommandAcceptance.Rejected(
                "aircraft is rolling out after touchdown; only EL/ER/EXIT or DEL apply (issue TAXI after the runway exit)"
            ),
        };
    }
}
