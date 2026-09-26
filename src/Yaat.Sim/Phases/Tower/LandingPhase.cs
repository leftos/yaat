using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
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
    private const double MinSoftBrakingRateKtsPerSec = 0.5;

    /// <summary>
    /// Margin (kts/sec) over the rate that selected the committed exit that the rollout may still brake at to make it.
    /// Absorbs the creep in the required rate between discrete ticks, so a committed exit is not abandoned over a
    /// sliver of a knot per second.
    /// </summary>
    public const double CommittedExitDecelToleranceKtsPerSec = 0.25;

    /// <summary>
    /// Tick margin (nm ≈ 50 ft) held back from a LAHSO hold-short point, on top of the aircraft's nose offset.
    /// The 50 ft covers discrete-tick overshoot alone — one sub-tick at coast speed is about 17 ft — because the
    /// hold-short distance already carries the runway safety area setback and half the crossing runway's width.
    /// The nose offset (<see cref="NoseOffsetNm"/>) is what puts the aircraft's nose rather than its centroid
    /// short of the point: no part of the aircraft may extend beyond the marking (AIM 2-3-5.a.1).
    /// </summary>
    private const double LahsoStopMarginNm = 50.0 / GeoMath.FeetPerNm;

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

    /// <summary>
    /// The runway geometry a phase restored from a snapshot without the plan's constants came back with. It is
    /// the plan's only surviving half until the first tick rebuilds the rest, and <see cref="ToSnapshot"/> writes
    /// it back so a second snapshot taken in that window still carries the runway.
    /// </summary>
    private LandingGeometry? _restoredGeometry;

    /// <summary>
    /// Set by <see cref="FromSnapshot"/> on an instance that came back mid-approach from a snapshot written
    /// before the plan's category constants — flare altitude, flare rate, Vref, touchdown speed, coast speed,
    /// brake rate — round-tripped, cleared by the first <see cref="OnTick"/> once they have been rebuilt from the
    /// category table. <see cref="PhaseRunner"/> only calls <see cref="OnStart"/> on a Pending phase, so a
    /// restored Active phase has to rebuild on its own first tick, the way <see cref="LineUpPhase"/> rebuilds its
    /// maneuver; without it a restored piston flared and braked on jet numbers. A snapshot that carries the
    /// constants needs no rebuild — it restores the plan the aircraft actually flew.
    /// </summary>
    private bool _needsRestoreRebuild;

    /// <summary>Runway-derived half of <see cref="LandingPlan"/>: the part a snapshot round-trips.</summary>
    private readonly record struct LandingGeometry(double FieldElevation, TrueHeading RunwayHeading, double ThresholdLat, double ThresholdLon);

    /// <summary>Current sub-state. Read-only except from within this class.</summary>
    public State CurrentState { get; set; } = State.StabilizedApproach;

    // Cross-tick state
    private bool _canGoAround;
    private double _lahsoHoldShortDistNm;
    private bool _hasLahso;

    /// <summary>
    /// Half the airframe's length (nm), resolved from the aircraft type on first use. Derived from the type the
    /// aircraft already carries, so it stays out of the snapshot: a restored phase resolves the same number.
    /// </summary>
    private double? _noseOffsetNm;

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

    public override PhaseDto ToSnapshot()
    {
        // Geometry survives even in the window where a pre-constants snapshot has been restored but not yet
        // ticked: writing zeros there would make the next restore hand OnTick a null plan, which the phase
        // runner reads as a completed landing and follows with a runway exit the aircraft never flew.
        LandingGeometry? geometry = _plan is { } plan
            ? new LandingGeometry(plan.FieldElevation, plan.RunwayHeading, plan.ThresholdLat, plan.ThresholdLon)
            : _restoredGeometry;
        return new LandingPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = Requirements.Count > 0 ? [.. Requirements.Select(r => r.ToSnapshot())] : null,
            FieldElevation = geometry?.FieldElevation ?? 0,
            RunwayHeadingDeg = geometry?.RunwayHeading.Degrees ?? 0,
            ThresholdLat = geometry?.ThresholdLat ?? 0,
            ThresholdLon = geometry?.ThresholdLon ?? 0,
            TouchedDown = CurrentState is State.Rollout or State.Unable or State.FullStop or State.Handoff,
            CanGoAround = _canGoAround,
            LahsoHoldShortDistNm = _lahsoHoldShortDistNm,
            HasLahso = _hasLahso,
            CandidateExitHoldShortId = _candidateExit?.HoldShortNode.Id,
            CandidateExitBranchPointId = _candidateExit?.BranchPointNode.Id,
            CandidateExitTaxiway = _candidateExit?.TaxiwayName,
            CandidateExitTurnOffSpeed = _candidateExit?.TurnOffSpeed ?? 0,
            CandidateExitPathNodeIds = _candidateExit?.Path.Select(n => n.Id).ToList(),
            CandidateExitSelectionDecelRate = _candidateExit?.SelectionDecelRate,
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
            RunwayId = _plan?.RunwayId,
            FlareEntryAgl = _plan?.FlareEntryAgl,
            FlareFpm = _plan?.FlareFpm,
            Vref = _plan?.Vref,
            Vtd = _plan?.Vtd,
            CoastSpeed = _plan?.CoastSpeed,
            DefaultDecel = _plan?.DefaultDecel,
            TouchdownAgl = _plan?.TouchdownAgl,
        };
    }

    /// <summary>
    /// True when <paramref name="path"/> starts at <paramref name="branch"/>, ends at <paramref name="holdShort"/>, and
    /// every consecutive pair of nodes shares an edge — the shape <see cref="RunwayExitPhase"/> builds its route from.
    /// </summary>
    private static bool IsDrivableExitPath(List<GroundNode> path, GroundNode branch, GroundNode holdShort)
    {
        if ((path.Count == 0) || (path[0].Id != branch.Id) || (path[^1].Id != holdShort.Id))
        {
            return false;
        }

        for (int i = 0; i < path.Count - 1; i++)
        {
            int nextId = path[i + 1].Id;
            if (!path[i].Edges.Any(edge => edge.OtherNodeId(path[i].Id) == nextId))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The candidate exit <paramref name="dto"/> carried, rebuilt on <paramref name="layout"/>. Null when the snapshot
    /// carried none, names a branch point or hold-short the layout lacks, or carries a path that is not a drivable
    /// chain on this layout.
    /// </summary>
    private static ResolvedExitInfo? RestoreCandidateExit(LandingPhaseDto dto, AirportGroundLayout layout)
    {
        if (
            (dto.CandidateExitTaxiway is not { } taxiway)
            || (dto.CandidateExitHoldShortId is not { } holdShortId)
            || (dto.CandidateExitBranchPointId is not { } branchPointId)
            || !layout.Nodes.TryGetValue(holdShortId, out GroundNode? holdShortNode)
            || !layout.Nodes.TryGetValue(branchPointId, out GroundNode? branchPointNode)
        )
        {
            return null;
        }

        List<int>? pathIds = dto.CandidateExitPathNodeIds;
        List<GroundNode> path = ResolvePathNodes(pathIds, layout);

        // Node ids are assigned when the layout is built, so a snapshot recorded against an older build of the
        // airport can name nodes that now sit elsewhere. A path that no longer runs edge by edge from the branch
        // point to the hold-short is not an exit on this layout; leave the candidate empty and the next rollout
        // tick re-resolves it from the aircraft's position. A snapshot that carried no path ids keeps the
        // candidate with an empty path, as before.
        if ((pathIds is { Count: > 0 }) && !IsDrivableExitPath(path, branchPointNode, holdShortNode))
        {
            Log.LogWarning(
                "[Landing] restored candidate exit {Taxiway} dropped: path [{Path}] is not a connected chain "
                    + "from branch {Branch} to hold-short {HoldShort} on this layout",
                taxiway,
                string.Join("→", pathIds),
                branchPointNode.Id,
                holdShortNode.Id
            );
            return null;
        }

        return new ResolvedExitInfo
        {
            HoldShortNode = holdShortNode,
            BranchPointNode = branchPointNode,
            TaxiwayName = taxiway,
            TurnOffSpeed = dto.CandidateExitTurnOffSpeed,
            Path = path,
            SelectionDecelRate = dto.CandidateExitSelectionDecelRate,
        };
    }

    /// <summary>The nodes of <paramref name="nodeIds"/> that exist on <paramref name="layout"/>, in order.</summary>
    private static List<GroundNode> ResolvePathNodes(List<int>? nodeIds, AirportGroundLayout layout)
    {
        List<GroundNode> path = [];
        if (nodeIds is null)
        {
            return path;
        }

        foreach (int nodeId in nodeIds)
        {
            if (layout.Nodes.TryGetValue(nodeId, out GroundNode? pathNode))
            {
                path.Add(pathNode);
            }
        }

        return path;
    }

    public static LandingPhase FromSnapshot(LandingPhaseDto dto, AirportGroundLayout? groundLayout)
    {
        var phase = new LandingPhase { Status = (PhaseStatus)dto.Status, ElapsedSeconds = dto.ElapsedSeconds };
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
        if (groundLayout is not null)
        {
            phase._candidateExit = RestoreCandidateExit(dto, groundLayout);
        }

        // A snapshot that carries the constants restores the plan verbatim: Vref keeps the gust additive the
        // aircraft flew and the runway id keeps the assignment it flew, so a rewind reproduces the same flare,
        // touchdown and rollout rather than recomputing them from the restore-time weather.
        if (
            dto.FlareEntryAgl is { } flareEntryAgl
            && dto.FlareFpm is { } flareFpm
            && dto.Vref is { } vref
            && dto.Vtd is { } vtd
            && dto.CoastSpeed is { } coastSpeed
            && dto.DefaultDecel is { } defaultDecel
            && dto.TouchdownAgl is { } touchdownAgl
        )
        {
            phase._plan = new LandingPlan
            {
                FieldElevation = dto.FieldElevation,
                RunwayHeading = new TrueHeading(dto.RunwayHeadingDeg),
                ThresholdLat = dto.ThresholdLat,
                ThresholdLon = dto.ThresholdLon,
                RunwayId = dto.RunwayId,
                FlareEntryAgl = flareEntryAgl,
                FlareFpm = flareFpm,
                Vref = vref,
                Vtd = vtd,
                CoastSpeed = coastSpeed,
                DefaultDecel = defaultDecel,
                TouchdownAgl = touchdownAgl,
            };
            phase._needsRestoreRebuild = false;
        }
        else if (dto.RunwayHeadingDeg != 0 || dto.ThresholdLat != 0)
        {
            // Geometry but no constants: hold the geometry so it survives another ToSnapshot, and let the first
            // tick fill the constants in from the category table.
            phase._restoredGeometry = new LandingGeometry(
                FieldElevation: dto.FieldElevation,
                RunwayHeading: new TrueHeading(dto.RunwayHeadingDeg),
                ThresholdLat: dto.ThresholdLat,
                ThresholdLon: dto.ThresholdLon
            );

            // Only a phase that was already running needs the rebuild; one snapshotted before it started
            // still gets its OnStart from the phase runner.
            phase._needsRestoreRebuild = (PhaseStatus)dto.Status == PhaseStatus.Active;
        }

        return phase;
    }

    public override void OnStart(PhaseContext ctx)
    {
        // Full-stop landing ends any standing pattern-leg reports (touch-and-go does not).
        ctx.Aircraft.Approach.ClearArmedReports();

        _needsRestoreRebuild = false;
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
        RunwayInfo? rwy = ctx.Runway;
        // The plan's threshold is the flare/touchdown/LAHSO datum, so it is the *landing* threshold:
        // pavement behind a displaced threshold is not available for landing in this direction
        // (AIM 2-3-3.h.2). Falls back to the pavement end when no airport map is loaded.
        LatLon threshold = rwy is not null ? LandingThreshold.Resolve(rwy, ctx.GroundLayout) : ctx.Aircraft.Position;
        var geometry = new LandingGeometry(
            FieldElevation: ctx.FieldElevation,
            RunwayHeading: rwy?.TrueHeading ?? ctx.Aircraft.TrueHeading,
            ThresholdLat: threshold.Lat,
            ThresholdLon: threshold.Lon
        );
        return BuildPlan(ctx, geometry);
    }

    /// <summary>
    /// Fills <paramref name="geometry"/> out into a full plan with this aircraft's category constants and its
    /// assigned runway. The single source of those constants: <see cref="OnStart"/> reaches it through the
    /// geometry it just computed, and a phase restored mid-approach reaches it with the geometry its snapshot
    /// carried.
    /// </summary>
    private static LandingPlan BuildPlan(PhaseContext ctx, LandingGeometry geometry)
    {
        return new LandingPlan
        {
            FieldElevation = geometry.FieldElevation,
            RunwayHeading = geometry.RunwayHeading,
            ThresholdLat = geometry.ThresholdLat,
            ThresholdLon = geometry.ThresholdLon,
            RunwayId = ctx.Aircraft.Phases?.AssignedRunway?.Designator,
            FlareEntryAgl = CategoryPerformance.FlareAltitude(ctx.Category),
            FlareFpm = CategoryPerformance.FlareDescentRate(ctx.Category),
            // FCTM: the gust correction is maintained to touchdown; only the steady-headwind
            // half of the approach additive bleeds off in the flare.
            Vref = CategoryPerformance.ApproachSpeed(ctx.Category) + AircraftPerformance.GustApproachAdditive(ctx.Weather),
            Vtd = AircraftPerformance.TouchdownSpeed(ctx.Aircraft.AircraftType, ctx.Category),
            CoastSpeed = CategoryPerformance.RolloutCoastSpeed(ctx.Category),
            DefaultDecel = CategoryPerformance.RolloutDecelRate(ctx.Category),
            TouchdownAgl = ctx.Category == AircraftCategory.Helicopter ? 0 : 2,
        };
    }

    public override bool OnTick(PhaseContext ctx)
    {
        // Restored mid-approach: the snapshot's geometry plus this aircraft's category constants, before the
        // state machine reads the plan. OnStart never runs on a restored Active phase.
        if (_needsRestoreRebuild)
        {
            if (_restoredGeometry is { } geometry)
            {
                _plan = BuildPlan(ctx, geometry);
            }
            _needsRestoreRebuild = false;
        }

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
            // A LAHSO lander restored straight into Handoff without a usable candidate would hand off to a
            // runway exit the planner never cleared against the hold-short point. Back to the rollout, which
            // re-resolves under the LAHSO filter and stops at the point when nothing fits.
            State.Handoff when _hasLahso && (_candidateExit is not { Path.Count: >= 2 }) => TickRollout(ctx, _plan),
            State.Handoff => TickHandoff(ctx),
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

        ApplyGlidepathFloor(ctx, plan);

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
    /// Never aim the pre-flare descent below the glidepath to the threshold. LandingPhase
    /// normally inherits a stabilized glideslope from FinalApproachPhase at &lt;30 ft AGL, but
    /// if it starts high/far out (e.g. after a 360 or S-turns on final) nothing holds it on
    /// the path and physics free-falls at the category descent rate, touching down short of
    /// the runway. Clamping the descent target to the glidepath altitude at the current
    /// distance to the threshold makes the aircraft track the path down instead. The target is
    /// bounded by current altitude so an aircraft already below the path holds level (lets the
    /// path descend to meet it) rather than climbing.
    ///
    /// Once inside the flare window the floor releases and the descent target is the field
    /// elevation, so the flare/touchdown logic runs unchanged — without this the floor would
    /// hold the aircraft a few feet up and it would float down the runway instead of landing.
    /// This is transparent to a normal short-final handoff, which arrives already in the flare
    /// window.
    ///
    /// The floor is the same path FinalApproachPhase flies, crossing-height and all, so a recovered
    /// high-entry aircraft rejoins it and lands at the category's aiming point rather than early in
    /// the touchdown zone. Past the aiming point the path is below the surface and the floor clamps
    /// to field elevation.
    /// </summary>
    private static void ApplyGlidepathFloor(PhaseContext ctx, LandingPlan plan)
    {
        double agl = ctx.Aircraft.Altitude - plan.FieldElevation;
        if (agl <= plan.FlareEntryAgl)
        {
            ctx.Targets.TargetAltitude = plan.FieldElevation;
            return;
        }

        double pastThresholdNm = GeoMath.AlongTrackDistanceNm(
            ctx.Aircraft.Position,
            new LatLon(plan.ThresholdLat, plan.ThresholdLon),
            plan.RunwayHeading
        );
        double glidepathAlt = GlideSlopeGeometry.AltitudeAtDistance(-pastThresholdNm, plan.FieldElevation, ctx.Category);
        double floor = Math.Max(plan.FieldElevation, glidepathAlt);
        ctx.Targets.TargetAltitude = Math.Min(ctx.Aircraft.Altitude, floor);
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
        // implementation checked only AGL <= 0; the current implementation widens the AGL bound to
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

        // CLANDF override is consumed at touchdown so a later auto-cycle / re-pattern doesn't inherit it.
        ctx.Aircraft.Phases?.ForceLanding = false;

        // Air → ground frame flip: the field becomes wheel speed. Snap the airborne IAS to
        // Vtd if the flare overshot it, then convert — touchdown groundspeed is touchdown
        // TAS minus the headwind component, so a 15 kt headwind shortens the rollout by
        // the v² law and makes an earlier exit reachable.
        double touchdownIas = Math.Min(ctx.Aircraft.IndicatedAirspeed, plan.Vtd);
        GroundFrame.EnterGround(ctx.Aircraft, touchdownIas);

        _touchdownLat = ctx.Aircraft.Position.Lat;
        _touchdownLon = ctx.Aircraft.Position.Lon;

        Log.LogDebug("[Landing] {Callsign}: touchdown, gs={Gs:F1}kts", ctx.Aircraft.Callsign, ctx.Aircraft.GroundSpeed);

        CurrentState = State.Rollout;
        return false;
    }

    // --- Ground states ---

    private bool TickRollout(PhaseContext ctx, LandingPlan plan)
    {
        // Steer along runway centerline with a bounded proportional XTE bias. A rollout that hands off to the
        // exit does so at coastSpeed ≥ 15 kt, well clear of the FlightPhysics.StationaryGroundSpeedKts floor;
        // a LAHSO rollout braking to a stop crosses that floor at the end, where the guard stops turning the
        // aircraft and it holds the heading it stopped on — which is what a stopped aircraft does.
        ctx.Targets.TargetTrueHeading = ComputeCenterlineSteeringTarget(ctx, plan);
        // Use ground turn rate so XTE corrections apply at taxi cadence, not
        // airborne cadence. Cleared when the phase hands off to RunwayExitPhase.
        ctx.Targets.TurnRateOverride = CategoryPerformance.GroundTurnRate(ctx.Category);

        // Re-resolve candidate from scratch if the controller changed the preference mid-rollout
        ExitPreference? currentPref = ctx.Aircraft.Phases?.RequestedExit;
        if (currentPref != _originalPreference)
        {
            _originalPreference = currentPref;
            _activePreference = currentPref;
            _candidateExit = null;
            _exitResolutionEnabled = currentPref is not null;
        }

        // LAHSO: the landing roll has to end short of the hold-short point — exit before it, or stop at it
        // (AIM 2-3-5.a.2, AIM 4-3-11.b.6). Measured along the runway from the landing threshold — the datum the
        // hold-short distance was computed against; the great-circle distance under-reads off centerline and
        // brakes late. The stop target is set back far enough that the nose, not the centroid, stays clear.
        double lahsoStopDistNm = 0;
        if (_hasLahso)
        {
            double alongFromThreshold = GeoMath.AlongTrackDistanceNm(
                ctx.Aircraft.Position,
                new LatLon(plan.ThresholdLat, plan.ThresholdLon),
                plan.RunwayHeading
            );
            double distToHoldShort = _lahsoHoldShortDistNm - alongFromThreshold;
            lahsoStopDistNm = distToHoldShort - LahsoSetbackNm(ctx);

            if (lahsoStopDistNm <= 0)
            {
                return TickLahsoStop(ctx, distToHoldShort);
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

        if (HasMissedCandidateExit(ctx, plan))
        {
            MarkExitUnable(ctx);
            CurrentState = State.Unable;
            return false;
        }

        (double targetSpeed, double decelRateOverride) = PlanExitDeceleration(ctx, plan, coastSpeed);

        if (_hasLahso)
        {
            (targetSpeed, decelRateOverride) = ApplyLahsoCeiling(ctx, plan, targetSpeed, decelRateOverride, lahsoStopDistNm);
        }

        ctx.Targets.TargetSpeed = targetSpeed;
        ctx.Targets.DesiredDecelRate = decelRateOverride;

        AircraftCategory cat = AircraftCategorization.Categorize(ctx.Aircraft.AircraftType);
        _canGoAround = ctx.Aircraft.IndicatedAirspeed >= CategoryPerformance.RejectedLandingMinSpeed(cat);

        if (CanHandOff(ctx, plan, coastSpeed))
        {
            CurrentState = State.Handoff;
            return TickHandoff(ctx);
        }

        return false;
    }

    /// <summary>
    /// True when the rollout has passed the candidate exit's branch point without being able to take it: past the
    /// branch and either still too fast for the turn-off or committed to a standard exit, which must be entered
    /// before the branch.
    /// </summary>
    private bool HasMissedCandidateExit(PhaseContext ctx, LandingPlan plan)
    {
        if (_candidateExit is null)
        {
            return false;
        }

        double distToBranchPoint = GeoMath.AlongTrackDistanceNm(_candidateExit.BranchPointNode.Position, ctx.Aircraft.Position, plan.RunwayHeading);

        // Missed-exit conditions: past branch AND (too fast OR standard exit at branch)
        double highSpeedTurnOff = CategoryPerformance.HighSpeedExitSpeed(ctx.Category);
        bool tooFast = ctx.Aircraft.IndicatedAirspeed > _candidateExit.TurnOffSpeed + RolloutBraking.TurnOffSpeedToleranceKts;
        bool standardExitAtBranch = _candidateExit.TurnOffSpeed < highSpeedTurnOff;

        return (distToBranchPoint <= 0) && (tooFast || standardExitAtBranch);
    }

    /// <summary>
    /// The rollout's speed plan: the target speed and braking rate that bring the aircraft to the candidate exit's
    /// turn-off speed by its branch point, or to coast speed when there is no candidate. Never plans above the
    /// current speed: once IAS is at or below the planned target it holds IAS. Braking below coast is
    /// <see cref="RunwayExitPhase"/>'s job.
    /// </summary>
    private (double TargetSpeed, double DecelRate) PlanExitDeceleration(PhaseContext ctx, LandingPlan plan, double coastSpeed)
    {
        double targetSpeed = coastSpeed;
        // Start at the plan's routine rollout rate (CategoryPerformance.RolloutDecelRate). We
        // always set an override rather than leaving it null — otherwise
        // FlightPhysics would fall back to AircraftPerformance.DecelRate, which
        // is the airborne rate, not the ground rollout rate. The exit-planner
        // below raises this when the turn-off requires harder braking or lowers
        // it when the exit is far enough away that the default would overshoot.
        double decelRateOverride = plan.DefaultDecel;
        if (_candidateExit is not null)
        {
            (targetSpeed, decelRateOverride) = PlanCandidateExitDeceleration(ctx, plan, coastSpeed, _candidateExit);
        }

        // Don't brake below coast speed — that's RunwayExitPhase's job.
        if (ctx.Aircraft.IndicatedAirspeed <= targetSpeed)
        {
            targetSpeed = ctx.Aircraft.IndicatedAirspeed; // freeze at current
        }

        return (targetSpeed, decelRateOverride);
    }

    /// <summary>
    /// The part of <see cref="PlanExitDeceleration"/> that shapes the plan to <paramref name="candidate"/>: coast
    /// speed at the default rollout rate unless the exit is still ahead, the aircraft is faster than its turn-off
    /// speed, and the turn-off is within braking limits.
    /// </summary>
    private (double TargetSpeed, double DecelRate) PlanCandidateExitDeceleration(
        PhaseContext ctx,
        LandingPlan plan,
        double coastSpeed,
        ResolvedExitInfo candidate
    )
    {
        double targetSpeed = coastSpeed;
        double decelRateOverride = plan.DefaultDecel;
        double distToBranch = GeoMath.AlongTrackDistanceNm(candidate.BranchPointNode.Position, ctx.Aircraft.Position, plan.RunwayHeading);

        if ((distToBranch > 0) && (ctx.Aircraft.IndicatedAirspeed > candidate.TurnOffSpeed))
        {
            double requiredDecel = RolloutBraking.RequiredDecelKtsPerSec(ctx.Aircraft.GroundSpeed, candidate.TurnOffSpeed, distToBranch);
            double brakingLimit = CommittedExitBrakingLimit(ctx, candidate);

            if (requiredDecel <= brakingLimit)
            {
                // Plan speed: down to the exit's turnoff speed if it is slower
                // than coast (e.g. 12-kt standard exits for a piston whose coast
                // is 25 kt). RunwayExitPhase still handles the final braking through
                // the turn, but letting LandingPhase drop below coast is what allows
                // a slow piston to actually take a 90° midfield exit — otherwise the
                // missed-exit check at distToBranch≤0 always fires for standard exits.
                targetSpeed = Math.Min(coastSpeed, candidate.TurnOffSpeed);

                // Raise the decel rate if the direct turn-off requires firmer
                // braking than the default — can't make the exit otherwise.
                if (requiredDecel > decelRateOverride)
                {
                    decelRateOverride = requiredDecel;
                }

                // Reserve distance for RunwayExitPhase to brake from coast to
                // turn-off speed. Aim to reach coast speed at (branch - buffer),
                // not at the branch itself.
                double brakingBufferNm = RolloutBraking.BrakingDistanceNm(coastSpeed, candidate.TurnOffSpeed, plan.DefaultDecel);
                double effectiveDist = distToBranch - brakingBufferNm;

                // Gentle decel when the exit is far enough that normal braking
                // would reach coast speed too early. Lower the rate so the
                // aircraft stays fast longer and arrives at coast near the exit.
                if (effectiveDist > 0)
                {
                    double requiredDecelToCoast = RolloutBraking.RequiredDecelKtsPerSec(ctx.Aircraft.GroundSpeed, coastSpeed, effectiveDist);
                    if ((requiredDecelToCoast > 0) && (requiredDecelToCoast < decelRateOverride))
                    {
                        decelRateOverride = Math.Max(requiredDecelToCoast, MinSoftBrakingRateKtsPerSec);
                    }
                }
            }
        }

        return (targetSpeed, decelRateOverride);
    }

    /// <summary>
    /// The rollout-to-exit hand-off gate: the aircraft is at or below coast speed, a standard exit's branch is not
    /// too close to turn onto, and a LAHSO lander has a candidate <see cref="RunwayExitPhase"/> will honour.
    /// </summary>
    private bool CanHandOff(PhaseContext ctx, LandingPlan plan, double coastSpeed)
    {
        bool handoffBlocked = IsStandardExitBranchTooClose(ctx, plan, coastSpeed);

        // A LAHSO lander hands off only with a candidate RunwayExitPhase will actually honour: one the resolver has
        // restricted to a branch point before the hold-short point, that carries a real path, and whose hold-short
        // is unclaimed. RunwayExitPhase drops a committed exit that fails either of the last two and re-searches
        // the centerline with no knowledge of the hold-short point, which would put the exit back past it. Without
        // such a candidate the aircraft stays in rollout under the LAHSO ceiling and stops at the point.
        bool lahsoAllowsHandoff = !_hasLahso || ((_candidateExit is { Path.Count: >= 2 }) && !IsHoldShortOccupied(ctx, _candidateExit));

        return lahsoAllowsHandoff && !handoffBlocked && (ctx.Aircraft.IndicatedAirspeed <= coastSpeed);
    }

    /// <summary>
    /// True when the candidate is a standard exit whose branch point is less than 0.02 nm ahead, too close for a
    /// turn arc, so hand-off waits for the next exit.
    /// </summary>
    private bool IsStandardExitBranchTooClose(PhaseContext ctx, LandingPlan plan, double coastSpeed)
    {
        if ((_candidateExit is not null) && (ctx.Aircraft.IndicatedAirspeed <= coastSpeed))
        {
            double distToBranch = GeoMath.AlongTrackDistanceNm(_candidateExit.BranchPointNode.Position, ctx.Aircraft.Position, plan.RunwayHeading);

            double hsExitSpeed = CategoryPerformance.HighSpeedExitSpeed(ctx.Category);
            bool isStandardExit = _candidateExit.TurnOffSpeed < hsExitSpeed;
            return isStandardExit && (distToBranch < 0.02);
        }

        return false;
    }

    /// <summary>
    /// The LAHSO fallback (AIM 4-3-11.b.6): no room left to the hold-short point, so stop on the runway. Returns
    /// true once stopped, which is what makes <see cref="PhaseRunner"/> hold the aircraft there instead of exiting.
    /// A stop taken with the point already behind the aircraft is an overrun and says so in the log.
    /// </summary>
    private bool TickLahsoStop(PhaseContext ctx, double distToHoldShortNm)
    {
        // Braking effort: firm while the aircraft is still short of the marking and only inside the setback,
        // maximum effort once the point itself is behind it — an overrun onto a crossing runway is the one
        // place max-effort braking belongs, and the category rate caps what the aircraft can actually do.
        double maxRate = CategoryPerformance.ExpediteExitDecelRate(ctx.Category);
        ctx.Targets.TargetSpeed = 0;
        ctx.Targets.DesiredDecelRate = distToHoldShortNm <= 0 ? maxRate : Math.Min(RolloutBraking.FirmBrakingRateKtsPerSec, maxRate);

        if (ctx.Aircraft.IndicatedAirspeed > 0.5)
        {
            return false;
        }

        StoppedForLahso = true;
        if (distToHoldShortNm < 0)
        {
            Log.LogWarning(
                "[Landing] {Callsign}: LAHSO overrun — stopped {OverrunFt:F0}ft past the hold-short point",
                ctx.Aircraft.Callsign,
                -distToHoldShortNm * GeoMath.FeetPerNm
            );
        }
        else
        {
            Log.LogDebug("[Landing] {Callsign}: LAHSO stop", ctx.Aircraft.Callsign);
        }

        return true;
    }

    /// <summary>
    /// Caps the rollout plan to what still stops inside <paramref name="stopDistNm"/>. The speed ceiling is
    /// planned at the rate the rollout brakes at anyway, so the slow-down starts early and stays comfortable —
    /// a crew flying LAHSO front-loads the braking rather than holding coast speed up to the line and standing
    /// on the brakes. The published rate is what the remaining distance requires, never below what the exit
    /// planner already asked for and never above the category's max-effort rate, which is the only thing this
    /// can lower the planner's rate to.
    /// </summary>
    private static (double TargetSpeed, double DecelRate) ApplyLahsoCeiling(
        PhaseContext ctx,
        LandingPlan plan,
        double targetSpeed,
        double decelRateOverride,
        double stopDistNm
    )
    {
        double maxRate = CategoryPerformance.ExpediteExitDecelRate(ctx.Category);
        double planningRate = Math.Min(plan.DefaultDecel, maxRate);
        double cappedSpeed = Math.Min(targetSpeed, RolloutBraking.MaxEntrySpeedKts(stopDistNm, planningRate));
        double requiredDecel = RolloutBraking.RequiredDecelKtsPerSec(ctx.Aircraft.GroundSpeed, 0, stopDistNm);
        double cappedRate = Math.Min(Math.Max(decelRateOverride, requiredDecel), maxRate);
        return (cappedSpeed, cappedRate);
    }

    private bool TickHandoff(PhaseContext ctx)
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

                // Exiting before the hold-short point satisfies the LAHSO clearance (AIM 4-3-11.b.6), so the
                // landing is no longer a hold: PhaseRunner keys the runway-hold chain on this target being set.
                // Tied to the commitment above, which the handoff gate requires of a LAHSO lander — the two can
                // never disagree about whether the aircraft is leaving the runway before the point.
                if (_hasLahso)
                {
                    ctx.Aircraft.Phases.LahsoHoldShort = null;
                }
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
        ExitPreference? userPref = ctx.Aircraft.Phases?.RequestedExit;
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
            ExitSide? keepSide = _originalPreference?.Side;
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
        // CLANDF forced-landing override: a forced aircraft is committed to the runway, so the
        // unstable-approach balked-landing go-around is suppressed entirely.
        if (ctx.Aircraft.Phases?.ForceLanding == true)
        {
            _stabilizedSinceSec = 0;
            return;
        }

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
                Pilot.PilotResponder.SoloPositionsTower
            );
            _unableBroadcast = true;
        }

        _unableBranchPoints.Add(_candidateExit.BranchPointNode.Id);
    }

    /// <summary>
    /// True when another aircraft already claims <paramref name="candidate"/>'s hold-short node. The same test
    /// <see cref="Ground.RunwayExitPhase"/> applies to a committed exit when it starts, asked here so a LAHSO
    /// lander never hands off to an exit the exit phase would drop and replace with a centerline re-search.
    /// </summary>
    private static bool IsHoldShortOccupied(PhaseContext ctx, ResolvedExitInfo candidate) =>
        ctx.OccupiedHoldShortNodes?.Contains(candidate.HoldShortNode.Id) ?? false;

    /// <summary>
    /// How far the aircraft's nose leads <c>AircraftState.Position</c>, which is the centroid: half the
    /// <see cref="AircraftLength.ResolveFt"/> length — the same resolver <see cref="Ground.RunwayExitPhase"/> uses for its
    /// tail-clearance offset. Resolved once and cached — the type does not change mid-landing.
    /// </summary>
    private double NoseOffsetNm(PhaseContext ctx)
    {
        _noseOffsetNm ??= AircraftLength.ResolveFt(ctx.Aircraft.AircraftType) / 2.0 / GeoMath.FeetPerNm;
        return _noseOffsetNm.Value;
    }

    /// <summary>
    /// Distance short of the LAHSO hold-short point the aircraft's centroid has to stop at for the nose to stay
    /// clear of the marking: the tick margin plus the nose offset.
    /// </summary>
    private double LahsoSetbackNm(PhaseContext ctx) => LahsoStopMarginNm + NoseOffsetNm(ctx);

    /// <summary>
    /// True when an aircraft turning off at <paramref name="branchNode"/> leaves the runway before the LAHSO
    /// hold-short point — the branch point sits at or before the stop target, setback included. Only meaningful
    /// while <see cref="_hasLahso"/> is set.
    /// </summary>
    private bool BranchFitsInsideLahso(PhaseContext ctx, GroundNode branchNode, LandingPlan plan)
    {
        double branchFromThreshold = GeoMath.AlongTrackDistanceNm(
            branchNode.Position,
            new LatLon(plan.ThresholdLat, plan.ThresholdLon),
            plan.RunwayHeading
        );
        return branchFromThreshold <= (_lahsoHoldShortDistNm - LahsoSetbackNm(ctx));
    }

    private void ResolveNextCandidate(PhaseContext ctx, LandingPlan plan)
    {
        if (ctx.GroundLayout is null)
        {
            return;
        }

        string? rwyDesignator = ctx.Aircraft.Phases?.AssignedRunway?.Designator;
        if ((rwyDesignator is not null) && (FindGraphCandidate(ctx, plan, rwyDesignator) is { } resolved))
        {
            _candidateExit = resolved;
            Log.LogDebug(
                "[Landing] {Callsign}: candidate exit {Taxiway}, turnOffSpeed={Speed:F0}kts, selected at {Rate:F2}kt/s",
                ctx.Aircraft.Callsign,
                resolved.TaxiwayName,
                resolved.TurnOffSpeed,
                resolved.SelectionDecelRate
            );
            return;
        }

        // Fallback: straight-line search (airports without hold-short data)
        (GroundNode Node, string Taxiway)? result = ctx.GroundLayout.FindExitAheadOnRunway(
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

        if (_hasLahso && !BranchFitsInsideLahso(ctx, result.Value.Node, plan))
        {
            Log.LogDebug(
                "[Landing] {Callsign}: fallback exit {Taxiway} is past the LAHSO hold-short point",
                ctx.Aircraft.Callsign,
                result.Value.Taxiway
            );
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
            SelectionDecelRate = null,
        };
    }

    /// <summary>
    /// Search the ground graph for the next exit ahead on <paramref name="rwyDesignator"/> that the aircraft can brake
    /// for under its current exit preference, falling back to the firm-braking search when default selection finds
    /// none. Returns null when no exit is reachable.
    /// </summary>
    private ResolvedExitInfo? FindGraphCandidate(PhaseContext ctx, LandingPlan plan, string rwyDesignator)
    {
        ExitPreference? searchPref = _activePreference;

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

        double selectionLimit(double turnOffSpeed) => BrakingLimit(ctx, turnOffSpeed);
        ResolvedExitInfo? found = TryFindCandidate(ctx, plan, rwyDesignator, searchPref, sidePref, excludeHoldShortNodes, selectionLimit);

        // Fall back to taxiway-only if inferred-side found nothing
        if ((found is null) && (searchPref != _activePreference))
        {
            found = TryFindCandidate(ctx, plan, rwyDesignator, _activePreference, sidePref, excludeHoldShortNodes, selectionLimit);
        }

        // A crew that cannot make any exit at its default-selection rates takes the next one it can make braking
        // firmly rather than rolling to the runway end and stopping on it. Instructed and expedited exits already
        // search at the firm or max-effort rate.
        bool defaultSelection = !_exitResolutionEnabled && !ctx.Aircraft.Ground.IsExpeditingExit;
        if ((found is null) && defaultSelection)
        {
            double firmCap = FirmBrakingCap(ctx.Category);
            found = TryFindCandidate(ctx, plan, rwyDesignator, _activePreference, sidePref, excludeHoldShortNodes, _ => firmCap);
        }

        return found;
    }

    /// <summary>
    /// Run the side-preferred lookahead search with a braking-reachability filter: a candidate whose turn-off speed
    /// needs more than <paramref name="brakingLimitForTurnOffSpeed"/> gives for that turn-off speed, from the current
    /// position, is skipped (the Skip verdict excludes the entire taxiway from the rest of this call). Without the
    /// filter the planner would return the first forward exit unconditionally — typically a 90° standard exit too
    /// close to brake for — so skipping unreachable candidates lets it commit to a reachable downstream exit (e.g. a
    /// high-speed at ~30°) and brake for that. The chosen exit carries the limit that admitted it as its
    /// <see cref="ResolvedExitInfo.SelectionDecelRate"/>. Returns null when no candidate (on-side or off-side
    /// fallback) is reachable from the current state.
    /// </summary>
    private ResolvedExitInfo? TryFindCandidate(
        PhaseContext ctx,
        LandingPlan plan,
        string rwyDesignator,
        ExitPreference? searchPref,
        ExitSide? sidePref,
        HashSet<int>? excludeHoldShortNodes,
        Func<double, double> brakingLimitForTurnOffSpeed
    )
    {
        if (ctx.GroundLayout is null)
        {
            return null;
        }

        AirportGroundLayout.CenterlineExitResult? found = ctx.GroundLayout.FindOnSidePreferredExit(
            ctx.Aircraft.Position.Lat,
            ctx.Aircraft.Position.Lon,
            plan.RunwayHeading,
            rwyDesignator,
            searchPref,
            sidePref,
            excludeBranchPoints: _unableBranchPoints.Count > 0 ? [.. _unableBranchPoints] : null,
            excludeHoldShortNodes: excludeHoldShortNodes,
            filter: candidate =>
            {
                double turnOffSpeed = CategoryPerformance.ExitTurnOffSpeed(ctx.Category, candidate.ExitAngle);
                GroundNode branchNode = candidate.Path[0];
                double distToBranch = GeoMath.AlongTrackDistanceNm(branchNode.Position, ctx.Aircraft.Position, plan.RunwayHeading);

                // Branch is at or behind the aircraft — try the next centerline.
                // Skip the entire taxiway so we don't keep finding the same one
                // via the BFS cluster expansion.
                if (distToBranch <= 0)
                {
                    return AirportGroundLayout.CandidateVerdict.Skip;
                }

                // Under a LAHSO clearance an exit past the hold-short point is no use, however reachable it is:
                // the aircraft has to be stopped short of the point. Skipped like an unreachable candidate, so
                // the search moves on and the stop at the point remains the fallback.
                if (_hasLahso && !BranchFitsInsideLahso(ctx, branchNode, plan))
                {
                    Log.LogDebug(
                        "[Landing] {Callsign}: skipping exit {Taxiway} — branch point is past the LAHSO hold-short point at {HoldShort:F2}nm",
                        ctx.Aircraft.Callsign,
                        candidate.Taxiway,
                        _lahsoHoldShortDistNm
                    );
                    return AirportGroundLayout.CandidateVerdict.Skip;
                }

                bool alreadySlowEnough = ctx.Aircraft.IndicatedAirspeed <= turnOffSpeed + RolloutBraking.TurnOffSpeedToleranceKts;
                double brakingLimit = brakingLimitForTurnOffSpeed(turnOffSpeed);
                bool reachable =
                    alreadySlowEnough
                    || (RolloutBraking.RequiredDecelKtsPerSec(ctx.Aircraft.GroundSpeed, turnOffSpeed, distToBranch) <= brakingLimit);

                if (!reachable)
                {
                    Log.LogDebug(
                        "[Landing] {Callsign}: skipping exit {Taxiway} (angle={Angle:F0}, turnOff={Speed:F0}kts, dist={Dist:F3}nm) — "
                            + "required decel exceeds the {Limit:F2}kt/s limit at gs={Gs:F1}kts",
                        ctx.Aircraft.Callsign,
                        candidate.Taxiway,
                        candidate.ExitAngle,
                        turnOffSpeed,
                        distToBranch,
                        brakingLimit,
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

        GroundNode branch = found.Value.Path[0];
        double turnOff = CategoryPerformance.ExitTurnOffSpeed(ctx.Category, found.Value.ExitAngle);
        return new ResolvedExitInfo
        {
            HoldShortNode = found.Value.HoldShort,
            TaxiwayName = found.Value.Taxiway,
            TurnOffSpeed = turnOff,
            Path = found.Value.Path,
            BranchPointNode = branch,
            SelectionDecelRate = brakingLimitForTurnOffSpeed(turnOff),
        };
    }

    /// <summary>
    /// Most the rollout brakes to make the committed <paramref name="candidate"/>: the max-effort rate under
    /// <c>EXP</c>; otherwise the rate that selected it plus <see cref="CommittedExitDecelToleranceKtsPerSec"/>, never
    /// above <see cref="FirmBrakingCap"/>. An exit the firm-braking fallback chose, and one restored without its
    /// selection rate, get the firm cap. Past this ceiling the rollout gives the exit up rather than brake harder
    /// than the crew accepted when choosing it.
    /// </summary>
    private static double CommittedExitBrakingLimit(PhaseContext ctx, ResolvedExitInfo candidate)
    {
        if (ctx.Aircraft.Ground.IsExpeditingExit)
        {
            return CategoryPerformance.ExpediteExitDecelRate(ctx.Category);
        }

        double firmCap = FirmBrakingCap(ctx.Category);
        return candidate.SelectionDecelRate is { } selectionRate ? Math.Min(selectionRate + CommittedExitDecelToleranceKtsPerSec, firmCap) : firmCap;
    }

    /// <summary>
    /// The firm braking rate, capped at the category's max-effort <see cref="CategoryPerformance.ExpediteExitDecelRate"/>
    /// so the firm-braking fallback never brakes harder than an expedited exit would.
    /// </summary>
    private static double FirmBrakingCap(AircraftCategory category) =>
        Math.Min(RolloutBraking.FirmBrakingRateKtsPerSec, CategoryPerformance.ExpediteExitDecelRate(category));

    /// <summary>
    /// Max deceleration the pilot will accept to select an exit with <paramref name="turnOffSpeed"/> — the
    /// exit-reachability filter (which exits qualify). Expedited exits (<c>EXP</c>) brake at the max-effort rate so
    /// the earliest reachable exit qualifies; an instructed exit (<c>ER</c>/<c>EL</c>/<c>EXIT</c>) at the firm rate.
    /// Default selection depends on the exit's class (aviation ruling 2026-09-25): a pilot brakes a little harder to
    /// make a high-speed exit (turn-off speed at or above <see cref="CategoryPerformance.HighSpeedExitSpeed"/>) than a
    /// standard one, so a high-speed exit qualifies at the comfortable-exit rate and a standard exit only at the
    /// routine rollout rate.
    /// </summary>
    private double BrakingLimit(PhaseContext ctx, double turnOffSpeed)
    {
        if (ctx.Aircraft.Ground.IsExpeditingExit)
        {
            return CategoryPerformance.ExpediteExitDecelRate(ctx.Category);
        }

        if (_exitResolutionEnabled)
        {
            return RolloutBraking.FirmBrakingRateKtsPerSec;
        }

        bool highSpeedExit = turnOffSpeed >= CategoryPerformance.HighSpeedExitSpeed(ctx.Category);
        return highSpeedExit ? CategoryPerformance.ComfortableExitDecelRate(ctx.Category) : CategoryPerformance.RolloutDecelRate(ctx.Category);
    }

    /// <summary>
    /// Drop the cached exit candidate so the next tick re-resolves it. Called when
    /// a standalone <c>EXP</c> raises the braking limit mid-rollout — the
    /// previously-chosen comfortable exit may now be beaten by an earlier one the
    /// aircraft can reach with max-effort braking. (The <c>ER</c>/<c>EL</c>/<c>EXIT</c>
    /// modifier form already re-resolves via the preference-change path.)
    /// </summary>
    internal void ResetExitCandidate() => _candidateExit = null;

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        if (CurrentState is State.StabilizedApproach or State.Flare)
        {
            // Airborne — go-around allowed, exit preference allowed, nothing else
            return cmd switch
            {
                CanonicalCommandType.GoAround => CommandAcceptance.Allowed,
                CanonicalCommandType.ForceLanding => CommandAcceptance.Allowed,
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
