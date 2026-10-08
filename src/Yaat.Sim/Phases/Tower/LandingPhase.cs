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
    internal readonly record struct LandingGeometry(double FieldElevation, TrueHeading RunwayHeading, double ThresholdLat, double ThresholdLon);

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

    // A forced rollout's exit searches found nothing: skip them until the aircraft passes this point (nm along the runway
    // from the landing threshold) — the next branch point the last search saw, or the runway end when it saw none.
    private double? _forcedNoExitUntilAlongNm;
    private bool _floatingForRollout;

    // Exit resolution state
    private ResolvedExitInfo? _candidateExit;
    private ExitPreference? _activePreference;
    private ExitPreference? _originalPreference;
    private bool _exitResolutionEnabled;
    private ExitSide? _inferredSide;
    private readonly HashSet<int> _unableBranchPoints = [];
    private bool _unableBroadcast;

    // Restored from a snapshot: the live run's _originalPreference was the very instance PhaseList.RequestedExit held, so
    // the restore shares that instance again (ShareRequestedExit). False on a phase built live.
    private bool _restoredOriginalIsRequestedExit;

    /// <summary>The currently committed candidate exit chosen by the rollout planner. Null before resolution.</summary>
    public ResolvedExitInfo? CandidateExit => _candidateExit;

    /// <summary>The inferred preferred side from runway/parking layout, or null if undetermined.</summary>
    public ExitSide? InferredSide => _inferredSide;

    public bool StoppedForLahso { get; private set; }

    /// <summary>
    /// Where the wheels met the runway, and the ground speed just after the touchdown frame flip. Null before a touchdown
    /// flown in this phase. Both survive a snapshot restore: the position is the snapshotted touchdown point, and the
    /// ground speed is snapshotted with it.
    /// </summary>
    public LatLon? TouchdownPosition => TouchdownGroundSpeedKts is null ? null : new LatLon(_touchdownLat, _touchdownLon);

    /// <inheritdoc cref="TouchdownPosition"/>
    public double? TouchdownGroundSpeedKts { get; private set; }

    public override string Name => "Landing";

    /// <summary>
    /// The snapshot as taken outside a <see cref="PhaseList"/>, which cannot say whether the exit preference this phase
    /// remembers is the list's <see cref="PhaseList.RequestedExit"/>: restored, the phase treats the list's preference as a
    /// new instruction. <see cref="PhaseList.ToSnapshot"/> uses <see cref="ToSnapshot(ExitPreference?)"/>.
    /// </summary>
    public override PhaseDto ToSnapshot() => ToSnapshot(requestedExit: null);

    /// <summary>
    /// The snapshot, recording whether the user's exit preference this phase remembers (<c>_originalPreference</c>) is the
    /// very instance <paramref name="requestedExit"/> — the owning list's <see cref="PhaseList.RequestedExit"/> — is: the
    /// rollout tells a new exit instruction from the one it already acted on by reference, so a re-issued identical
    /// <c>EXIT</c> not yet seen by a tick must restore as new, as it is live.
    /// </summary>
    internal LandingPhaseDto ToSnapshot(ExitPreference? requestedExit)
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
            CandidateExitSide = (int?)_candidateExit?.Side,
            ActivePreferenceSide = (int?)_activePreference?.Side,
            ActivePreferenceTaxiway = _activePreference?.Taxiway,
            OriginalPreferenceSide = (int?)_originalPreference?.Side,
            OriginalPreferenceTaxiway = _originalPreference?.Taxiway,
            ExitResolutionEnabled = _exitResolutionEnabled,
            UnableBroadcast = _unableBroadcast,
            OriginalPreferenceIsRequestedExit = (_originalPreference is not null) && ReferenceEquals(_originalPreference, requestedExit),
            StoppedForLahso = StoppedForLahso,
            CurrentStateValue = (int)CurrentState,
            TouchdownLat = _touchdownLat,
            TouchdownLon = _touchdownLon,
            TouchdownGroundSpeedKts = TouchdownGroundSpeedKts,
            ForcedNoExitUntilAlongNm = _forcedNoExitUntilAlongNm,
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
            Side = (ExitSide?)dto.CandidateExitSide,
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
        phase._unableBroadcast = dto.UnableBroadcast;
        phase._restoredOriginalIsRequestedExit = dto.OriginalPreferenceIsRequestedExit;
        phase.StoppedForLahso = dto.StoppedForLahso;
        phase.CurrentState = (State)dto.CurrentStateValue;
        phase._touchdownLat = dto.TouchdownLat;
        phase._touchdownLon = dto.TouchdownLon;
        phase.TouchdownGroundSpeedKts = dto.TouchdownGroundSpeedKts;
        phase._forcedNoExitUntilAlongNm = dto.ForcedNoExitUntilAlongNm;
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

        // A new landing starts with nothing given up: a taxiway the crew refused belongs to the landing it was refused on.
        ctx.Aircraft.Phases?.GivenUpExitTaxiways.Clear();

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
    private static LandingPlan BuildPlan(PhaseContext ctx, LandingGeometry geometry) => BuildPlan(ctx.Aircraft, ctx.Category, ctx.Weather, geometry);

    /// <summary>
    /// The plan <paramref name="aircraft"/> of <paramref name="category"/> lands on <paramref name="geometry"/> with, without a running
    /// phase: the same constants <see cref="OnStart"/> fills in, which the final-approach exit forecast
    /// (<see cref="FinalApproachExitForecast"/>) needs before this phase has started.
    /// </summary>
    internal static LandingPlan BuildPlan(AircraftState aircraft, AircraftCategory category, WeatherProfile? weather, LandingGeometry geometry)
    {
        return new LandingPlan
        {
            FieldElevation = geometry.FieldElevation,
            RunwayHeading = geometry.RunwayHeading,
            ThresholdLat = geometry.ThresholdLat,
            ThresholdLon = geometry.ThresholdLon,
            RunwayId = aircraft.Phases?.AssignedRunway?.Designator,
            FlareEntryAgl = CategoryPerformance.FlareAltitude(category),
            FlareFpm = CategoryPerformance.FlareDescentRate(category),
            // FCTM: the gust correction is maintained to touchdown; only the steady-headwind
            // half of the approach additive bleeds off in the flare.
            Vref = CategoryPerformance.ApproachSpeed(category) + AircraftPerformance.GustApproachAdditive(weather),
            Vtd = AircraftPerformance.TouchdownSpeed(aircraft.AircraftType, category),
            CoastSpeed = CategoryPerformance.RolloutCoastSpeed(category),
            DefaultDecel = CategoryPerformance.RolloutDecelRate(category),
            TouchdownAgl = category == AircraftCategory.Helicopter ? 0 : 2,
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

        // CLANDF: the forced-landing profile owns the descent and the speed down to the flare, aimed at a point
        // on the runway ahead of the aircraft — including an aircraft that handed off here already past the
        // threshold, which the glidepath floor would otherwise leave diving at the threshold behind it.
        if (ctx.Aircraft.Phases?.ForceLanding == true)
        {
            ForcedLandingProfile.ApplyAirborneGuidance(
                ctx,
                new LatLon(plan.ThresholdLat, plan.ThresholdLon),
                plan.RunwayHeading,
                plan.FieldElevation
            );
        }
        else
        {
            ApplyGlidepathFloor(ctx, plan);
        }

        // Stabilization gate
        CheckStabilizationGate(ctx, plan);
        if (CurrentState == State.GoAround)
        {
            return false; // GoAroundHelper.Trigger handles the handoff; PhaseList advances next tick
        }

        // Transition to Flare when AGL drops to flare entry altitude. A forced descent can cross the whole flare band
        // in one sub-tick, so it runs the flare (and its touchdown gate) on this tick instead of the next — a sub-tick
        // at its ground speed is ~60 ft of runway it may not have.
        if (agl <= plan.FlareEntryAgl)
        {
            CurrentState = State.Flare;
            if (ctx.Aircraft.Phases?.ForceLanding == true)
            {
                return TickFlare(ctx, plan);
            }
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
        ApplyForcedFlareFloor(ctx, plan);
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

    /// <summary>
    /// CLANDF: when the forced descent had to exceed its cap to reach the latest touchdown point, the flare may not
    /// float — it keeps descending at the forced rate so the wheels meet the runway where there is still room to stop.
    /// </summary>
    private static void ApplyForcedFlareFloor(PhaseContext ctx, LandingPlan plan)
    {
        if (ctx.Aircraft.Phases?.ForceLanding != true)
        {
            return;
        }

        var threshold = new LatLon(plan.ThresholdLat, plan.ThresholdLon);
        if (ForcedLandingProfile.FlareFloorFpm(ctx, threshold, plan.RunwayHeading, plan.FieldElevation) is { } floorFpm)
        {
            ctx.Targets.DesiredVerticalRate = Math.Min(ctx.Targets.DesiredVerticalRate ?? 0, -floorFpm);
        }
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

        // A CLANDF override stays set through the rollout: it is the forced-rollout state, which brakes the aircraft
        // to an exit or to a stop on the runway (TickForcedRollout). It is consumed when the landing hands off or
        // ends on the ground (OnEnd), so a later auto-cycle / re-pattern doesn't inherit it.

        // Air → ground frame flip: the field becomes wheel speed. Snap the airborne IAS to
        // Vtd if the flare overshot it, then convert — touchdown groundspeed is touchdown
        // TAS minus the headwind component, so a 15 kt headwind shortens the rollout by
        // the v² law and makes an earlier exit reachable.
        double touchdownIas = Math.Min(ctx.Aircraft.IndicatedAirspeed, plan.Vtd);
        GroundFrame.EnterGround(ctx.Aircraft, touchdownIas);

        _touchdownLat = ctx.Aircraft.Position.Lat;
        _touchdownLon = ctx.Aircraft.Position.Lon;
        TouchdownGroundSpeedKts = ctx.Aircraft.GroundSpeed;

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
            TakeNewExitInstruction(ctx.Aircraft, currentPref);
            _candidateExit = null;
            _forcedNoExitUntilAlongNm = null;
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
            lahsoStopDistNm = distToHoldShort - LahsoSetbackNm(ctx.Aircraft);

            if (lahsoStopDistNm <= 0)
            {
                return TickLahsoStop(ctx, distToHoldShort);
            }
        }

        // Coast speed: decelerate to this speed and hold it while searching for exits
        double coastSpeed = plan.CoastSpeed;

        bool forced = ctx.Aircraft.Phases?.ForceLanding == true;

        // A LAHSO hold-short point outranks the forced rollout: its own stop logic already keeps the aircraft short.
        bool forcedRollout = forced && !_hasLahso;

        ResolveRolloutCandidate(ctx, plan, forced, forcedRollout);

        // The forced rollout judges its own candidate — TickForcedRollout gives up one it can no longer make, a
        // passed one included, and keeps braking to its stop — so the missed-exit path, which ends in State.Unable
        // and a handoff to the runway exit, is for the other rollouts only.
        if (!forcedRollout && HasMissedCandidateExit(ctx, plan))
        {
            MarkExitUnable(ctx);
            CurrentState = State.Unable;
            return false;
        }

        AircraftCategory category = AircraftCategorization.Categorize(ctx.Aircraft.AircraftType);
        _canGoAround = ctx.Aircraft.IndicatedAirspeed >= CategoryPerformance.RejectedLandingMinSpeed(category);

        if (forcedRollout)
        {
            return TickForcedRollout(ctx, plan, coastSpeed);
        }

        (double targetSpeed, double decelRateOverride) = PlanExitDeceleration(ctx, plan, coastSpeed);

        if (_hasLahso)
        {
            (targetSpeed, decelRateOverride) = ApplyLahsoCeiling(ctx, plan, targetSpeed, decelRateOverride, lahsoStopDistNm);
        }

        ctx.Targets.TargetSpeed = targetSpeed;
        ctx.Targets.DesiredDecelRate = decelRateOverride;

        if (CanHandOff(ctx, plan, coastSpeed))
        {
            CurrentState = State.Handoff;
            return TickHandoff(ctx);
        }

        return false;
    }

    /// <summary>
    /// Resolves the rollout's candidate exit when it has none. Always search for the next exit ahead — even without an
    /// explicit preference, the pilot plans deceleration for the first reachable exit. The forced rollout takes only a
    /// turnoff off this runway's centerline graph: the straight-line fallback returns any taxiway node ahead within 1.5 nm
    /// (a ramp or a parallel taxiway included) with a one-node path the runway exit cannot drive, and such a node would
    /// hand the rollout off short of its stop. A forced landing the normal planner cannot serve falls back to its own
    /// search (<see cref="ResolveForcedCandidate"/>); when that too finds nothing, both are skipped until the aircraft
    /// passes the next branch point (<see cref="IsForcedSearchMissCached"/>).
    /// </summary>
    private void ResolveRolloutCandidate(PhaseContext ctx, LandingPlan plan, bool forced, bool forcedRollout)
    {
        if ((_candidateExit is not null) || (forced && IsForcedSearchMissCached(ctx, plan)))
        {
            return;
        }

        _forcedNoExitUntilAlongNm = null;
        if (forcedRollout)
        {
            TryResolveGraphCandidate(ctx, plan);
        }
        else
        {
            ResolveNextCandidate(ctx, plan);
        }

        if (forced && (_candidateExit is null))
        {
            ResolveForcedCandidate(ctx, plan);
        }
    }

    /// <summary>
    /// True while a forced rollout's last exit search found nothing and nothing it could find has changed since: the
    /// aircraft has not reached the next branch point that search saw, and it is braking no harder than
    /// <see cref="ForcedLandingProfile.RolloutMaxDecelKtsPerSec"/>. At or below that rate an exit that needed more only
    /// needs more as the aircraft closes on it, so re-searching could not find one; above it (a runway-end stop braking
    /// harder) an exit can come within reach, so the search runs every tick.
    /// </summary>
    private bool IsForcedSearchMissCached(PhaseContext ctx, LandingPlan plan)
    {
        if ((_forcedNoExitUntilAlongNm is not { } untilAlongNm) || (ctx.Targets.DesiredDecelRate > ForcedLandingProfile.RolloutMaxDecelKtsPerSec))
        {
            return false;
        }

        return AlongFromThresholdNm(plan, ctx.Aircraft.Position) < untilAlongNm;
    }

    private static double AlongFromThresholdNm(LandingPlan plan, LatLon position) =>
        GeoMath.AlongTrackDistanceNm(position, new LatLon(plan.ThresholdLat, plan.ThresholdLon), plan.RunwayHeading);

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
            double requiredDecel = RolloutBraking.RequiredDecelKtsPerSec(
                ctx.Aircraft.GroundSpeed,
                candidate.TurnOffSpeed,
                distToBranch,
                ctx.Category
            );
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
                    double requiredDecelToCoast = RolloutBraking.RequiredDecelKtsPerSec(
                        ctx.Aircraft.GroundSpeed,
                        coastSpeed,
                        effectiveDist,
                        ctx.Category
                    );
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
        ctx.Targets.DesiredDecelRate = distToHoldShortNm <= 0 ? maxRate : CategoryPerformance.FirmBrakingRate(ctx.Category);

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
        double requiredDecel = RolloutBraking.RequiredDecelKtsPerSec(ctx.Aircraft.GroundSpeed, 0, stopDistNm, ctx.Category);
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
            TakeNewExitInstruction(ctx.Aircraft, userPref);
        }
        else
        {
            RelaxPreferenceToSide();
        }

        // Back to rollout to look for the next exit
        CurrentState = State.Rollout;
        return TickRollout(ctx, plan);
    }

    /// <summary>
    /// Takes <paramref name="instruction"/> as the controller's new exit instruction: the preference the rollout resolves and
    /// judges by, with its "unable" yet to be said; a taxiway it names is no longer given up, so a new instruction for an exit
    /// the crew refused is judged afresh.
    /// </summary>
    private void TakeNewExitInstruction(AircraftState aircraft, ExitPreference? instruction)
    {
        _originalPreference = instruction;
        _activePreference = instruction;
        _exitResolutionEnabled = instruction is not null;
        _unableBroadcast = false;
        if (instruction?.Taxiway is { } taxiway)
        {
            aircraft.Phases?.GivenUpExitTaxiways.Remove(taxiway);
        }
    }

    /// <summary>
    /// Drops the taxiway from the preference the rollout resolves by, keeping the controller's side (<c>EL</c>/<c>ER</c>) if one
    /// was set. <c>Phases.RequestedExit</c> is deliberately not overwritten — that is the user's intent and stays visible for
    /// diagnostics; relaxation is a LandingPhase-internal concern tracked in <c>_activePreference</c>. <c>_originalPreference</c>
    /// stays the user's preference too, so TickRollout's change detection does not read the relaxation as a new instruction
    /// and restore the exit just given up.
    /// </summary>
    private void RelaxPreferenceToSide()
    {
        ExitSide? keepSide = _originalPreference?.Side;
        _activePreference = keepSide is not null ? new ExitPreference { Side = keepSide } : null;
        _exitResolutionEnabled = false;
    }

    /// <summary>
    /// Gives up <paramref name="taxiway"/>, the exit the controller named, for the rest of this landing: the crew has told the
    /// controller it is unable (P/CG UNABLE — the controller may already be acting on it; 7110.65 3-10-9.a, AIM 4-3-21.a), so
    /// every later exit search skips the taxiway and the preference relaxes to the controller's side. The set is the phase
    /// list's (<see cref="PhaseList.GivenUpExitTaxiways"/>), shared with <see cref="Ground.RunwayExitPhase"/>, so the exit's own
    /// searches after the hand-off skip it too. A later bare <c>EXP</c> does not revive it; only a new instruction naming it does
    /// (<see cref="TakeNewExitInstruction"/>), judged afresh by <see cref="EvaluateAndApplyNamedExitInstruction"/>. The taxiway,
    /// not a branch node, is what is given up: a bar is reached from several centerline nodes (OAK 30's W3 from 87, 714, 715 and
    /// 716), so excluding one branch would let the next search re-find the exit at the next node.
    /// </summary>
    private void GiveUpNamedExit(AircraftState aircraft, string taxiway)
    {
        aircraft.Phases?.GivenUpExitTaxiways.Add(taxiway);
        RelaxPreferenceToSide();
    }

    /// <summary>True when the crew has given <paramref name="taxiway"/> up on this landing (<see cref="GiveUpNamedExit"/>).</summary>
    private static bool IsGivenUp(AircraftState aircraft, string taxiway) => aircraft.Phases?.GivenUpExitTaxiways.Contains(taxiway) is true;

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

    // --- Forced rollout (CLANDF) ---

    /// <summary>
    /// The CLANDF rollout: brake for the committed exit per <see cref="ForcedLandingProfile"/>, or — when no exit is
    /// usable at <see cref="ForcedLandingProfile.RolloutMaxDecelKtsPerSec"/> — stop on the runway short of its end.
    /// The exit, when there is one, is the normal planner's or else the last usable exit ahead
    /// (<see cref="ResolveForcedCandidate"/>).
    /// </summary>
    private bool TickForcedRollout(PhaseContext ctx, LandingPlan plan, double coastSpeed)
    {
        if (_candidateExit is not null)
        {
            if (PlanForcedExitDeceleration(ctx, plan, coastSpeed, _candidateExit) is { } exitPlan)
            {
                ctx.Targets.TargetSpeed = exitPlan.TargetSpeed;
                ctx.Targets.DesiredDecelRate = exitPlan.DecelRate;
                if (CanHandOff(ctx, plan, coastSpeed))
                {
                    CurrentState = State.Handoff;
                    return TickHandoff(ctx);
                }

                return false;
            }

            // The committed exit now needs more than the forced rollout's ceiling: give it up and stop instead,
            // re-searching (ResolveRolloutCandidate) so an exit that comes within reach as the aircraft slows is still taken.
            MarkExitUnable(ctx);
            _candidateExit = null;
        }

        return TickForcedRunwayEndStop(ctx, plan);
    }

    /// <summary>
    /// Target speed and braking rate for the forced rollout to <paramref name="candidate"/>: its turn-off speed (no
    /// faster than coast) reached <see cref="ForcedLandingProfile.ExitBrakingMarginFt"/> before its branch point, at
    /// the rate that needs, clamped to the forced rollout's band. Null when the exit needs more than the band's
    /// ceiling. An aircraft already at or below the target holds its speed.
    /// </summary>
    private static (double TargetSpeed, double DecelRate)? PlanForcedExitDeceleration(
        PhaseContext ctx,
        LandingPlan plan,
        double coastSpeed,
        ResolvedExitInfo candidate
    )
    {
        double targetSpeed = Math.Min(coastSpeed, candidate.TurnOffSpeed);
        double distToBranchNm = GeoMath.AlongTrackDistanceNm(candidate.BranchPointNode.Position, ctx.Aircraft.Position, plan.RunwayHeading);
        double brakingDistNm = distToBranchNm - (ForcedLandingProfile.ExitBrakingMarginFt / GeoMath.FeetPerNm);
        bool alreadySlow = ctx.Aircraft.IndicatedAirspeed <= targetSpeed;
        double requiredDecel = alreadySlow
            ? 0
            : RolloutBraking.RequiredDecelKtsPerSec(ctx.Aircraft.GroundSpeed, targetSpeed, brakingDistNm, ctx.Category);

        if (!alreadySlow && ((brakingDistNm <= 0) || (requiredDecel > ForcedLandingProfile.RolloutMaxDecelKtsPerSec)))
        {
            return null;
        }

        double decel = Math.Clamp(requiredDecel, ForcedLandingProfile.RolloutMinDecelKtsPerSec, ForcedLandingProfile.RolloutMaxDecelKtsPerSec);
        return (alreadySlow ? ctx.Aircraft.IndicatedAirspeed : targetSpeed, decel);
    }

    /// <summary>
    /// No usable exit: stop on the pavement <see cref="ForcedLandingProfile.RunwayEndStopMarginFt"/> before the runway
    /// end, braking at least the forced rollout's floor and up to
    /// <see cref="ForcedLandingProfile.RunwayEndStopMaxDecelKtsPerSec"/> — more only when even that would leave the
    /// runway. Completes once stopped, handing the runway to <see cref="Ground.RunwayExitPhase"/>, whose backstop
    /// finds a way off.
    /// </summary>
    private bool TickForcedRunwayEndStop(PhaseContext ctx, LandingPlan plan)
    {
        ctx.Targets.TargetTrueHeading = ComputeCenterlineSteeringTarget(ctx, plan);
        ctx.Targets.TargetSpeed = 0;

        double alongFt =
            GeoMath.AlongTrackDistanceNm(ctx.Aircraft.Position, new LatLon(plan.ThresholdLat, plan.ThresholdLon), plan.RunwayHeading)
            * GeoMath.FeetPerNm;
        double runwayLengthFt = ctx.Runway is { } runway ? ForcedLandingProfile.LandingDistanceFt(runway, ctx.GroundLayout) : alongFt;
        double toEndFt = runwayLengthFt - alongFt;
        ctx.Targets.DesiredDecelRate = ForcedRunwayEndStopDecelKtsPerSec(ctx.Aircraft.GroundSpeed, toEndFt);

        if (ctx.Aircraft.IndicatedAirspeed > 0.5)
        {
            return false;
        }

        Log.LogInformation(
            "[Landing] {Callsign}: forced landing stopped on runway {Runway}, {ToEndFt:F0} ft before its end, no exit usable",
            ctx.Aircraft.Callsign,
            plan.RunwayId ?? "?",
            toEndFt
        );
        return true;
    }

    /// <summary>
    /// Braking (kts/s) for the forced rollout's runway-end stop at <paramref name="groundSpeedKts"/> with
    /// <paramref name="toEndFt"/> of runway left: the rate that stops the aircraft
    /// <see cref="ForcedLandingProfile.RunwayEndStopMarginFt"/> before the end, no less than
    /// <see cref="ForcedLandingProfile.RolloutMinDecelKtsPerSec"/> and no more than
    /// <see cref="ForcedLandingProfile.RunwayEndStopMaxDecelKtsPerSec"/> — unless even that would leave the runway, when
    /// it is the rate that stops the aircraft at the end. Inside the margin there is no stopping short of it, so the cap
    /// applies; past the end, the cap.
    /// </summary>
    public static double ForcedRunwayEndStopDecelKtsPerSec(double groundSpeedKts, double toEndFt)
    {
        if (toEndFt <= 0)
        {
            return ForcedLandingProfile.RunwayEndStopMaxDecelKtsPerSec;
        }

        double toStopShortFt = toEndFt - ForcedLandingProfile.RunwayEndStopMarginFt;
        double stopShortDecel =
            toStopShortFt > 0
                ? RolloutBraking.DecelOverDistanceKtsPerSec(groundSpeedKts, 0, toStopShortFt / GeoMath.FeetPerNm)
                : double.PositiveInfinity;
        double stopAtEndDecel = RolloutBraking.DecelOverDistanceKtsPerSec(groundSpeedKts, 0, toEndFt / GeoMath.FeetPerNm);
        return Math.Min(
            Math.Max(stopShortDecel, ForcedLandingProfile.RolloutMinDecelKtsPerSec),
            Math.Max(ForcedLandingProfile.RunwayEndStopMaxDecelKtsPerSec, stopAtEndDecel)
        );
    }

    /// <summary>
    /// The forced rollout's own exit when the normal planner found none it could make: the last exit ahead whose
    /// turn-off speed is reachable <see cref="ForcedLandingProfile.ExitBrakingMarginFt"/> before its branch at no
    /// more than <see cref="ForcedLandingProfile.RolloutMaxDecelKtsPerSec"/>. The last one is the gentlest to make;
    /// occupied hold-shorts and exits already given up are excluded, and any side will do.
    /// </summary>
    private void ResolveForcedCandidate(PhaseContext ctx, LandingPlan plan)
    {
        if ((ctx.GroundLayout is null) || (ctx.Aircraft.Phases?.AssignedRunway?.Designator is not { } rwyDesignator))
        {
            return;
        }

        ResolvedExitInfo? last = null;
        double lastDistNm = double.MinValue;
        double nearestDistNm = double.MaxValue;
        ctx.GroundLayout.FindOnSidePreferredExit(
            ctx.Aircraft.Position.Lat,
            ctx.Aircraft.Position.Lon,
            plan.RunwayHeading,
            rwyDesignator,
            preference: null,
            sidePref: null,
            excludeBranchPoints: _unableBranchPoints.Count > 0 ? [.. _unableBranchPoints] : null,
            excludeHoldShortNodes: ctx.OccupiedHoldShortNodes,
            filter: candidate =>
            {
                if (IsGivenUp(ctx.Aircraft, candidate.Taxiway))
                {
                    return AirportGroundLayout.CandidateVerdict.Skip;
                }

                GroundNode branch = candidate.Path[0];
                double distNm = GeoMath.AlongTrackDistanceNm(branch.Position, ctx.Aircraft.Position, plan.RunwayHeading);
                double turnOff = CategoryPerformance.ExitTurnOffSpeed(ctx.Category, candidate.ExitAngle);
                var info = new ResolvedExitInfo
                {
                    HoldShortNode = candidate.HoldShort,
                    TaxiwayName = candidate.Taxiway,
                    TurnOffSpeed = turnOff,
                    Path = candidate.Path,
                    BranchPointNode = branch,
                    SelectionDecelRate = ForcedLandingProfile.RolloutMaxDecelKtsPerSec,
                    Side = candidate.Side,
                };
                if ((distNm > lastDistNm) && (PlanForcedExitDeceleration(ctx, plan, plan.CoastSpeed, info) is not null))
                {
                    last = info;
                    lastDistNm = distNm;
                }

                if (distNm > 0)
                {
                    nearestDistNm = Math.Min(nearestDistNm, distNm);
                }

                // Skip every candidate so the walk goes on to the end of the runway; the last usable one is kept above.
                return AirportGroundLayout.CandidateVerdict.Skip;
            }
        );

        if (last is null)
        {
            // Nothing usable: no need to look again before the next branch point, or at all when none is ahead.
            _forcedNoExitUntilAlongNm =
                nearestDistNm < double.MaxValue ? AlongFromThresholdNm(plan, ctx.Aircraft.Position) + nearestDistNm : double.MaxValue;
        }
        else
        {
            _candidateExit = last;
            Log.LogDebug(
                "[Landing] {Callsign}: forced rollout committing to the last usable exit {Taxiway}, turnOffSpeed={Speed:F0}kts",
                ctx.Aircraft.Callsign,
                last.TaxiwayName,
                last.TurnOffSpeed
            );
        }
    }

    /// <summary>
    /// A forced landing that ends on the ground — handed off to the runway exit, stopped, or cleared by a command — has
    /// finished its rollout, so the CLANDF override is consumed here and a later auto-cycle / re-pattern does not
    /// inherit it. An airborne end (a commanded go-around) leaves the flag to the command that ended it.
    /// </summary>
    public override void OnEnd(PhaseContext ctx, PhaseStatus endStatus)
    {
        if (ctx.Aircraft.IsOnGround && (ctx.Aircraft.Phases is { ForceLanding: true } phases))
        {
            phases.ForceLanding = false;

            // The forced rollout completed — stopped on the runway or handed off to the exit — so the runway exit
            // finishes it as one (PhaseList.ForcedRollout). A command that cleared the phase leaves the aircraft to it.
            phases.ForcedRollout = (endStatus == PhaseStatus.Completed) && !_hasLahso;
        }
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

        ReportUnableToExit(ctx, missedTaxiway);
        _unableBranchPoints.Add(_candidateExit.BranchPointNode.Id);

        // The exit the controller named is given up with the call, not just its branch node: the bar is reached from other
        // centerline nodes ahead, and the forced (CLANDF) rollout, which never passes through TickUnable, would otherwise keep
        // resolving the named taxiway at each of them.
        if (string.Equals(missedTaxiway, _originalPreference?.Taxiway, StringComparison.OrdinalIgnoreCase))
        {
            GiveUpNamedExit(ctx.Aircraft, missedTaxiway);
        }
    }

    /// <summary>
    /// Tells the controller the crew cannot make <paramref name="taxiway"/> — once per exit the controller named: the flag
    /// clears whenever a new exit instruction is accepted, and survives a snapshot. Exits the rollout chose on its own are
    /// given up silently.
    /// </summary>
    private void ReportUnableToExit(PhaseContext ctx, string taxiway)
    {
        if ((_originalPreference?.Taxiway is null) || _unableBroadcast)
        {
            return;
        }

        Pilot.PilotResponder.RouteSoloOrRpoTransmission(
            ctx.Aircraft,
            ctx.SoloTrainingMode,
            ctx.RpoShowPilotSpeech,
            ctx.StudentPositionType,
            Pilot.PilotResponder.BuildUnableToExit(ctx.Aircraft, taxiway),
            Pilot.PilotResponder.SoloPositionsTower
        );
        _unableBroadcast = true;
    }

    /// <summary>
    /// Whether an exit instruction naming a taxiway (<c>EL</c>/<c>ER</c>/<c>EXIT</c>, with or without <c>EXP</c>) can be
    /// accepted on the rollout, and if not, what the pilot says. It asks the rollout's own named-exit search
    /// (<see cref="FindWithInferredSide"/> over <see cref="TryFindCandidate"/>), a taxiway the crew has given up included —
    /// a new instruction is judged afresh: the inferred side first for a taxiway-only instruction, connections at or behind
    /// the aircraft skipped, slow enough by indicated airspeed or makeable at <see cref="RolloutBraking.NamedExitBrakingLimit"/>
    /// (firm, or max-effort under <paramref name="expedite"/>). Refused with the crew's "unable" when the named exit is ahead
    /// but past that limit or past a LAHSO hold-short point — and when it is the exit the aircraft is already braking for, that
    /// refusal gives it up (<see cref="RefuseNamedExit"/>) — and as no such exit ahead when no connection of the taxiway is ahead on
    /// a runway with hold-short data. Allowed before touchdown, on a forced (<c>CLANDF</c>) rollout, with no layout, and on a runway
    /// without hold-short data, where the straight-line fallback resolves the exit. The refusal is applied here, not only reported:
    /// the caller's own state (the standing preference) is left as it was.
    /// </summary>
    public ExitInstructionVerdict EvaluateAndApplyNamedExitInstruction(AircraftState aircraft, ExitPreference preference, bool expedite)
    {
        if ((preference.Taxiway is not { } taxiway) || (NamedExitQuery(aircraft, preference, expedite) is not { } query))
        {
            return new ExitInstructionVerdict(true, null);
        }

        return FindWithInferredSide(query) is not null ? new ExitInstructionVerdict(true, null) : RefuseNamedExit(aircraft, query, taxiway);
    }

    /// <summary>
    /// The search a named exit instruction is judged by on the rollout (<see cref="EvaluateAndApplyNamedExitInstruction"/>), and the
    /// one the exits-ahead list repeats per taxiway and side (<see cref="ListExitsAhead(AircraftState)"/>): from the aircraft as it
    /// stands, a taxiway the crew has given up judged afresh, at <see cref="RolloutBraking.NamedExitBrakingLimit"/>. Null — nothing
    /// to judge — before touchdown and after the rollout, on a forced (<c>CLANDF</c>) rollout, and with no assigned runway or layout.
    /// </summary>
    private ExitCandidateQuery? NamedExitQuery(AircraftState aircraft, ExitPreference preference, bool expedite)
    {
        if (
            (CurrentState != State.Rollout)
            || (_plan is not { } plan)
            || (aircraft.Phases is not { ForceLanding: false, AssignedRunway: { } runway })
            || (aircraft.Ground.Layout is not { } layout)
        )
        {
            return null;
        }

        AircraftCategory category = AircraftCategorization.Categorize(aircraft.AircraftType);
        double limit = RolloutBraking.NamedExitBrakingLimit(category, expedite);
        return new ExitCandidateQuery
        {
            Aircraft = aircraft,
            Category = category,
            Layout = layout,
            Plan = plan,
            RwyDesignator = runway.Designator,
            SearchPref = preference,
            SidePref = preference.Side ?? _inferredSide,
            ExcludeHoldShortNodes = null,
            BrakingLimitForTurnOffSpeed = _ => limit,
            IncludeGivenUp = true,
            IgnoreLahso = false,
            From = ExitReachOrigin.Of(aircraft),
            ExcludeBranchPoints = UnableBranchPointsOrNull(),
            LahsoStopLimitNm = LahsoStopLimitNm(aircraft),
        };
    }

    /// <summary>
    /// The named exits ahead this aircraft can make on its rollout, for the exits-ahead list: every taxiway an <c>EL</c>/<c>ER</c>
    /// naming it would be accepted for right now — the very search <see cref="EvaluateAndApplyNamedExitInstruction"/> runs, without
    /// expedite, per taxiway and side (<see cref="ListExitsAhead(ExitCandidateQuery, LatLon, Func{string, ExitSide, bool})"/>) — with
    /// the distance from the aircraft and the exit it is braking for (<see cref="CandidateExit"/>) marked planned. Read-only: it never
    /// refuses, gives up or commits anything. Null when no list applies: off the rollout, on a forced (<c>CLANDF</c>) rollout, and with
    /// no assigned runway, layout or hold-short data for the runway (where the straight-line fallback, not the graph, resolves exits).
    /// </summary>
    public IReadOnlyList<ExitAheadDto>? ListExitsAhead(AircraftState aircraft)
    {
        if (
            (NamedExitQuery(aircraft, new ExitPreference(), expedite: false) is not { } query)
            || (query.Layout.GetRunwayHoldShortNodes(query.RwyDesignator).Count == 0)
        )
        {
            return null;
        }

        // Matched on the side the candidate's own search found, never measured again from its nodes: a bar near the runway axis
        // (at a runway end) can measure onto the other side and lose the mark.
        ResolvedExitInfo? planned = _candidateExit;
        return ListExitsAhead(
            query,
            aircraft.Position,
            (taxiway, side) =>
                (planned is { Side: { } plannedSide })
                && (side == plannedSide)
                && string.Equals(taxiway, planned.TaxiwayName, StringComparison.OrdinalIgnoreCase)
        );
    }

    /// <summary>
    /// The exits-ahead list for <paramref name="query"/>: each taxiway of the runway (<see cref="AirportGroundLayout.ExitListTaxiways"/>, authored
    /// no-turnoff taxiways left out) judged on each side by <paramref name="query"/> naming that taxiway and side, and listed on that
    /// side when the search finds a connection of it there whose bar is at the runway's holding distance — a short stub, accepted only
    /// when named, is not an exit a crew would offer to take. One row per taxiway and side, at its first makeable connection. A
    /// taxiway with no edge at a centerline node the search walks (<see cref="AirportGroundLayout.TaxiwaysSeededAhead"/>) cannot be
    /// found ahead, so it is passed over without searching.
    /// Distances run along the runway from <paramref name="distanceDatum"/> to the branch point, rounded to 100 ft; rows come left side
    /// first, each side nearest first. Shared by the rollout (<see cref="ListExitsAhead(AircraftState)"/>) and the final-approach
    /// forecast (<see cref="FinalApproachExitForecast"/>).
    /// </summary>
    internal static IReadOnlyList<ExitAheadDto> ListExitsAhead(ExitCandidateQuery query, LatLon distanceDatum, Func<string, ExitSide, bool> isPlanned)
    {
        List<(ExitAheadDto Row, double AlongNm)> rows = [];
        HashSet<string> seededAhead = query.Layout.TaxiwaysSeededAhead(
            query.From.Position.Lat,
            query.From.Position.Lon,
            query.Plan.RunwayHeading,
            query.RwyDesignator,
            query.ExcludeBranchPoints
        );
        foreach (string taxiway in query.Layout.ExitListTaxiways(query.RwyDesignator).Where(seededAhead.Contains))
        {
            foreach (ExitSide side in ListedSides)
            {
                ExitCandidateQuery named = query with
                {
                    SearchPref = new ExitPreference { Side = side, Taxiway = taxiway },
                    SidePref = side,
                };
                if (
                    (FindCenterlineCandidate(named) is not { } exit)
                    || (exit.Side != side)
                    || !query.Layout.IsAtRunwayHoldingDistance(exit.HoldShort, query.RwyDesignator)
                )
                {
                    continue;
                }

                double alongNm = GeoMath.AlongTrackDistanceNm(exit.Path[0].Position, distanceDatum, query.Plan.RunwayHeading);
                rows.Add((new ExitAheadDto(taxiway, side, RoundToHundredFt(alongNm), isPlanned(taxiway, side)), alongNm));
            }
        }

        return [.. rows.OrderBy(r => r.Row.Side).ThenBy(r => r.AlongNm).ThenBy(r => r.Row.Taxiway, StringComparer.Ordinal).Select(r => r.Row)];
    }

    /// <summary>The sides the exits-ahead list judges every taxiway on, in the order its rows come.</summary>
    private static readonly ExitSide[] ListedSides = [ExitSide.Left, ExitSide.Right];

    private static int RoundToHundredFt(double distanceNm) =>
        (int)(Math.Round(distanceNm * GeoMath.FeetPerNm / 100.0, MidpointRounding.AwayFromZero) * 100);

    /// <summary>
    /// The refusal for <paramref name="taxiway"/>, whose <paramref name="query"/> found nothing at the named limit: the crew's
    /// "unable" when a connection is ahead past that limit or past a LAHSO hold-short point (the connection is there, the crew
    /// cannot use it), "no {taxiway} ahead" when none is ahead on a runway with hold-short data, and allowed on a runway without
    /// (the straight-line fallback resolves the exit). An "unable" for the exit the aircraft is already braking for is the crew's
    /// one call for it: the exit is given up here (<see cref="GiveUpNamedExit"/>), the call recorded as made and the candidate
    /// dropped, so the next tick resolves a later exit without a second call at the branch. A command is replayed on the same tick
    /// it was issued, so the give-up is as deterministic as the tick's own.
    /// </summary>
    private ExitInstructionVerdict RefuseNamedExit(AircraftState aircraft, ExitCandidateQuery query, string taxiway)
    {
        ExitCandidateQuery anyConnectionAhead = query with { BrakingLimitForTurnOffSpeed = _ => double.PositiveInfinity, IgnoreLahso = true };
        if (FindWithInferredSide(anyConnectionAhead) is { } unreachable)
        {
            if (_exitResolutionEnabled && string.Equals(unreachable.TaxiwayName, _originalPreference?.Taxiway, StringComparison.OrdinalIgnoreCase))
            {
                GiveUpNamedExit(aircraft, unreachable.TaxiwayName);
                _unableBroadcast = true;
                _candidateExit = null;
            }

            Pilot.PilotSpeechText unable = Pilot.PilotResponder.BuildUnableToExit(aircraft, unreachable.TaxiwayName);
            return new ExitInstructionVerdict(false, unable.Terminal) { PilotUnable = unable };
        }

        if (query.Layout.GetRunwayHoldShortNodes(query.RwyDesignator).Count == 0)
        {
            return new ExitInstructionVerdict(true, null);
        }

        Pilot.PilotSpeechText noExit = Pilot.PilotResponder.BuildUnableNoExitAhead(aircraft, taxiway);
        return new ExitInstructionVerdict(false, $"Unable, no {taxiway} ahead") { PilotUnable = noExit };
    }

    /// <summary>Where an exit connection stands for the rollout's reachability test (<see cref="JudgeExitReach"/>).</summary>
    private enum ExitReach
    {
        AtOrBehind,
        Reachable,
        BeyondLimit,
    }

    /// <summary>
    /// Where and how fast an exit search judges reach from (<see cref="JudgeExitReach"/>): on the rollout the aircraft's centroid,
    /// indicated airspeed (wheel speed on the ground) and ground speed as they stand (<see cref="Of"/>); on final the projected
    /// touchdown point and wheel speed (<see cref="FinalApproachExitForecast"/>).
    /// </summary>
    internal readonly record struct ExitReachOrigin(LatLon Position, double IndicatedAirspeedKts, double GroundSpeedKts)
    {
        public static ExitReachOrigin Of(AircraftState aircraft) => new(aircraft.Position, aircraft.IndicatedAirspeed, aircraft.GroundSpeed);
    }

    /// <summary>
    /// The one reachability test every exit search on the rollout applies, named or not, at command time and on the tick, and the
    /// final-approach forecast applies from the projected touchdown: a connection whose <paramref name="branch"/> is at or behind
    /// <paramref name="from"/> along the runway is passed; otherwise it is reachable when the indicated airspeed is already within
    /// <see cref="RolloutBraking.TurnOffSpeedToleranceKts"/> of the turn-off speed, or when the ground speed needs no more than the
    /// braking limit to come down to it by the branch.
    /// </summary>
    private static ExitReach JudgeExitReach(
        ExitReachOrigin from,
        AircraftCategory category,
        TrueHeading runwayHeading,
        GroundNode branch,
        (double TurnOffSpeed, double BrakingLimit) target
    )
    {
        double distToBranch = GeoMath.AlongTrackDistanceNm(branch.Position, from.Position, runwayHeading);
        if (distToBranch <= 0)
        {
            return ExitReach.AtOrBehind;
        }

        bool alreadySlowEnough = from.IndicatedAirspeedKts <= target.TurnOffSpeed + RolloutBraking.TurnOffSpeedToleranceKts;
        bool reachable =
            alreadySlowEnough
            || (RolloutBraking.RequiredDecelKtsPerSec(from.GroundSpeedKts, target.TurnOffSpeed, distToBranch, category) <= target.BrakingLimit);
        return reachable ? ExitReach.Reachable : ExitReach.BeyondLimit;
    }

    /// <summary>
    /// After a snapshot restore, makes the user's preference this phase remembers (<c>_originalPreference</c>) the very
    /// instance <see cref="PhaseList.RequestedExit"/> holds when the snapshot says they were one instance in the live run
    /// (<see cref="LandingPhaseDto.OriginalPreferenceIsRequestedExit"/>): the rollout detects a new exit instruction by
    /// reference, so two restored copies would read as a new one and re-enable an exit already given up, while an equal
    /// but separate instruction (re-issued, not yet ticked) must stay new.
    /// </summary>
    internal void ShareRequestedExit(ExitPreference? requestedExit)
    {
        if (_restoredOriginalIsRequestedExit && (requestedExit is not null))
        {
            _originalPreference = requestedExit;
        }
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
    private double NoseOffsetNm(AircraftState aircraft)
    {
        _noseOffsetNm ??= NoseOffsetNmOf(aircraft.AircraftType);
        return _noseOffsetNm.Value;
    }

    private static double NoseOffsetNmOf(string aircraftType) => AircraftLength.ResolveFt(aircraftType) / 2.0 / GeoMath.FeetPerNm;

    /// <summary>
    /// Distance short of the LAHSO hold-short point the aircraft's centroid has to stop at for the nose to stay
    /// clear of the marking: the tick margin plus the nose offset.
    /// </summary>
    private double LahsoSetbackNm(AircraftState aircraft) => LahsoStopMarginNm + NoseOffsetNm(aircraft);

    /// <summary>
    /// Furthest a branch point may lie from the landing threshold, along the runway, for an aircraft of
    /// <paramref name="aircraftType"/> turning off there to leave the runway before a LAHSO hold-short point
    /// <paramref name="holdShortDistNm"/> from the threshold: the point less the tick margin and the nose offset. The final-approach
    /// forecast's form of the limit the rollout keeps (<see cref="LahsoStopLimitNm(AircraftState)"/>).
    /// </summary>
    internal static double LahsoStopLimitNm(double holdShortDistNm, string aircraftType) =>
        holdShortDistNm - (LahsoStopMarginNm + NoseOffsetNmOf(aircraftType));

    /// <summary>The LAHSO stop limit for this landing's exit searches (<see cref="BranchFitsBefore"/>); null without LAHSO.</summary>
    private double? LahsoStopLimitNm(AircraftState aircraft) => _hasLahso ? _lahsoHoldShortDistNm - LahsoSetbackNm(aircraft) : null;

    /// <summary>The branch points the crew has said "unable" at on this landing, for an exit search to pass over; null when none.</summary>
    private HashSet<int>? UnableBranchPointsOrNull() => _unableBranchPoints.Count > 0 ? [.. _unableBranchPoints] : null;

    /// <summary>
    /// True when an aircraft turning off at <paramref name="branchNode"/> leaves the runway before the LAHSO
    /// hold-short point — the branch point sits at or before the stop target, setback included. Only meaningful
    /// while <see cref="_hasLahso"/> is set.
    /// </summary>
    private bool BranchFitsInsideLahso(AircraftState aircraft, GroundNode branchNode, LandingPlan plan) =>
        BranchFitsBefore(branchNode, plan, _lahsoHoldShortDistNm - LahsoSetbackNm(aircraft));

    /// <summary>
    /// True when <paramref name="branchNode"/> lies no further than <paramref name="stopLimitNm"/> along the runway from the landing
    /// threshold.
    /// </summary>
    private static bool BranchFitsBefore(GroundNode branchNode, LandingPlan plan, double stopLimitNm)
    {
        double branchFromThreshold = GeoMath.AlongTrackDistanceNm(
            branchNode.Position,
            new LatLon(plan.ThresholdLat, plan.ThresholdLon),
            plan.RunwayHeading
        );
        return branchFromThreshold <= stopLimitNm;
    }

    private void ResolveNextCandidate(PhaseContext ctx, LandingPlan plan)
    {
        if ((ctx.GroundLayout is null) || TryResolveGraphCandidate(ctx, plan))
        {
            return;
        }

        string? rwyDesignator = ctx.Aircraft.Phases?.AssignedRunway?.Designator;

        // Fallback: straight-line search (airports without hold-short data)
        (GroundNode Node, string Taxiway)? result = ctx.GroundLayout.FindExitAheadOnRunway(
            ctx.Aircraft.Position.Lat,
            ctx.Aircraft.Position.Lon,
            plan.RunwayHeading,
            _activePreference,
            rwyDesignator,
            excludeTaxiways: ctx.Aircraft.Phases?.GivenUpExitTaxiways
        );

        if (result is null)
        {
            return;
        }

        if (_hasLahso && !BranchFitsInsideLahso(ctx.Aircraft, result.Value.Node, plan))
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
            Side = null,
        };
    }

    /// <summary>
    /// Commits the next exit ahead that the runway's centerline graph offers (<see cref="FindGraphCandidate"/>) as the
    /// candidate. Returns false, leaving the candidate unset, when there is no layout or assigned runway or the graph
    /// has no reachable exit.
    /// </summary>
    private bool TryResolveGraphCandidate(PhaseContext ctx, LandingPlan plan)
    {
        if ((ctx.GroundLayout is not { } layout) || (ctx.Aircraft.Phases?.AssignedRunway?.Designator is not { } rwyDesignator))
        {
            return false;
        }

        if (FindGraphCandidate(ctx, layout, plan, rwyDesignator) is not { } resolved)
        {
            return false;
        }

        _candidateExit = resolved;
        Log.LogDebug(
            "[Landing] {Callsign}: candidate exit {Taxiway}, turnOffSpeed={Speed:F0}kts, selected at {Rate:F2}kt/s",
            ctx.Aircraft.Callsign,
            resolved.TaxiwayName,
            resolved.TurnOffSpeed,
            resolved.SelectionDecelRate
        );
        return true;
    }

    /// <summary>
    /// One exit-candidate search's inputs: everything <see cref="TryFindCandidate"/> needs, bundled so the search takes one argument
    /// rather than a positional one per field.
    /// </summary>
    internal readonly record struct ExitCandidateQuery
    {
        /// <summary>The point and speeds reach is judged from, and the centerline walk starts at.</summary>
        public required ExitReachOrigin From { get; init; }

        /// <summary>Centerline nodes the walk passes over: the branch points the crew has said "unable" at; null when none.</summary>
        public required HashSet<int>? ExcludeBranchPoints { get; init; }

        /// <summary>
        /// Under LAHSO, the furthest a branch point may lie from the landing threshold (<see cref="BranchFitsBefore"/>); null without.
        /// </summary>
        public required double? LahsoStopLimitNm { get; init; }

        public required AircraftState Aircraft { get; init; }
        public required AircraftCategory Category { get; init; }
        public required AirportGroundLayout Layout { get; init; }
        public required LandingPlan Plan { get; init; }
        public required string RwyDesignator { get; init; }
        public required ExitPreference? SearchPref { get; init; }
        public required ExitSide? SidePref { get; init; }
        public required HashSet<int>? ExcludeHoldShortNodes { get; init; }
        public required Func<double, double> BrakingLimitForTurnOffSpeed { get; init; }

        /// <summary>
        /// True to judge a taxiway the crew has given up as any other — a new instruction naming it is judged afresh; false
        /// skips every given-up taxiway, as the rollout's own searches do.
        /// </summary>
        public required bool IncludeGivenUp { get; init; }

        /// <summary>
        /// True to judge a connection past a LAHSO hold-short point as any other — the refusal asks whether the named taxiway has
        /// a connection ahead at all; false skips it, as every search for an exit to take does.
        /// </summary>
        public required bool IgnoreLahso { get; init; }
    }

    /// <summary>
    /// Search the ground graph for the next exit ahead on <paramref name="rwyDesignator"/> that the aircraft can brake
    /// for under its current exit preference, falling back to the firm-braking search when default selection finds
    /// none. Returns null when no exit is reachable.
    /// </summary>
    private ResolvedExitInfo? FindGraphCandidate(PhaseContext ctx, AirportGroundLayout layout, LandingPlan plan, string rwyDesignator)
    {
        // Pass occupancy info to the planner only for default selection (no
        // explicit taxiway). When the controller named a specific exit, the
        // pilot brakes for it regardless and RunwayExitPhase deals with any
        // late-breaking occupancy at handoff. For default selection, the
        // planner can do better by routing around known-occupied exits.
        HashSet<int>? excludeHoldShortNodes = (_activePreference?.Taxiway is null) ? ctx.OccupiedHoldShortNodes : null;

        // The side preference (explicit beats inferred) decides whether to defer an off-side candidate while looking
        // forward for an on-side option further down the runway.
        var query = new ExitCandidateQuery
        {
            Aircraft = ctx.Aircraft,
            Category = ctx.Category,
            Layout = layout,
            Plan = plan,
            RwyDesignator = rwyDesignator,
            SearchPref = _activePreference,
            SidePref = _activePreference?.Side ?? _inferredSide,
            ExcludeHoldShortNodes = excludeHoldShortNodes,
            BrakingLimitForTurnOffSpeed = turnOffSpeed => BrakingLimit(ctx, turnOffSpeed),
            IncludeGivenUp = false,
            IgnoreLahso = false,
            From = ExitReachOrigin.Of(ctx.Aircraft),
            ExcludeBranchPoints = UnableBranchPointsOrNull(),
            LahsoStopLimitNm = LahsoStopLimitNm(ctx.Aircraft),
        };
        ResolvedExitInfo? found = FindWithInferredSide(query);

        // A crew that cannot make any exit at its default-selection rates takes the next one it can make braking
        // firmly rather than rolling to the runway end and stopping on it. Instructed and expedited exits already
        // search at the firm or max-effort rate.
        bool defaultSelection = !_exitResolutionEnabled && !ctx.Aircraft.Ground.IsExpeditingExit;
        if ((found is null) && defaultSelection)
        {
            double firmRate = CategoryPerformance.FirmBrakingRate(ctx.Category);
            found = TryFindCandidate(query with { BrakingLimitForTurnOffSpeed = _ => firmRate });
        }

        if ((found is null) && GiveUpUnreachableNamedExit(ctx, query))
        {
            // The named exit is given up and the preference relaxed to the controller's side: resolve again under it, as
            // the rollout would on its next tick, so the aircraft brakes for the next exit it can make from this tick on.
            return FindGraphCandidate(ctx, layout, plan, rwyDesignator);
        }

        return found;
    }

    /// <summary>
    /// <see cref="TryFindCandidate"/> for <paramref name="query"/>, trying a taxiway-only preference on the inferred exit
    /// side first and on any side after: the side the crew expects to turn off on is the one it judges a named exit by.
    /// </summary>
    private ResolvedExitInfo? FindWithInferredSide(ExitCandidateQuery query) => FindWithInferredSide(query, _inferredSide);

    /// <summary>
    /// <see cref="FindWithInferredSide(ExitCandidateQuery)"/> with the side the crew expects to turn off on given rather than read from
    /// a running phase: the final-approach forecast's form, which judges a bare <c>EXIT &lt;twy&gt;</c> before this phase has started.
    /// </summary>
    internal static ResolvedExitInfo? FindWithInferredSide(ExitCandidateQuery query, ExitSide? inferredSide)
    {
        if ((query.SearchPref is { Taxiway: { } taxiway, Side: null }) && (inferredSide is { } inferred))
        {
            ResolvedExitInfo? onInferredSide = TryFindCandidate(
                query with
                {
                    SearchPref = new ExitPreference { Taxiway = taxiway, Side = inferred },
                }
            );
            if (onInferredSide is not null)
            {
                return onInferredSide;
            }
        }

        return TryFindCandidate(query);
    }

    /// <summary>
    /// Makes the call and gives the exit up when the exit the controller named (<c>ER</c>/<c>EL</c>/<c>EXIT</c> with a
    /// taxiway) is still ahead but past <see cref="RolloutBraking.NamedExitBrakingLimit"/> — firm, or max-effort under
    /// <c>EXP</c> — returning true; false when no named exit is in force or none is ahead. <paramref name="query"/> is the
    /// search that just failed at that limit, rerun here with no limit. The crew tells the controller it is unable on the tick
    /// this first finds it (<see cref="ReportUnableToExit"/>) and gives the taxiway up (<see cref="GiveUpNamedExit"/>): the
    /// preference relaxes to the controller's side and the caller resolves again under it, so the rollout brakes for the next
    /// exit it can make and never for the one refused, and the missed-exit path (<see cref="MarkExitUnable"/>) has nothing
    /// left to call at the branch. Without this the straight-line fallback would hand back the same exit as a one-node path
    /// with no braking check. Every category gives an exit up this way (7110.65 3-10-9.a: exit instructions are "if able").
    /// A forced (<c>CLANDF</c>) rollout judges its own candidate and is left out.
    /// </summary>
    private bool GiveUpUnreachableNamedExit(PhaseContext ctx, ExitCandidateQuery query)
    {
        bool forced = ctx.Aircraft.Phases?.ForceLanding == true;
        if (forced || !_exitResolutionEnabled || (_activePreference?.Taxiway is null))
        {
            return false;
        }

        ResolvedExitInfo? named = FindWithInferredSide(
            query with
            {
                ExcludeHoldShortNodes = null,
                BrakingLimitForTurnOffSpeed = _ => double.PositiveInfinity,
            }
        );
        if (named is null)
        {
            return false;
        }

        Log.LogDebug(
            "[Landing] {Callsign}: instructed exit {Taxiway} needs more than {Limit:F1} kt/s; giving it up",
            ctx.Aircraft.Callsign,
            named.TaxiwayName,
            RolloutBraking.NamedExitBrakingLimit(ctx.Category, ctx.Aircraft.Ground.IsExpeditingExit)
        );
        ReportUnableToExit(ctx, named.TaxiwayName);
        GiveUpNamedExit(ctx.Aircraft, named.TaxiwayName);
        return true;
    }

    /// <summary>
    /// Run the side-preferred lookahead search with a braking-reachability filter: a candidate whose turn-off speed
    /// needs more than <see cref="ExitCandidateQuery.BrakingLimitForTurnOffSpeed"/> gives for that turn-off speed, from the current
    /// position, is skipped (the Skip verdict excludes the entire taxiway from the rest of this call). Without the
    /// filter the planner would return the first forward exit unconditionally — typically a 90° standard exit too
    /// close to brake for — so skipping unreachable candidates lets it commit to a reachable downstream exit (e.g. a
    /// high-speed at ~30°) and brake for that. The chosen exit carries the limit that admitted it as its
    /// <see cref="ResolvedExitInfo.SelectionDecelRate"/>. Returns null when no candidate (on-side or off-side
    /// fallback) is reachable from the current state.
    /// A connection at or behind the aircraft (the centerline walk looks up to ~30 ft back) is skipped with its taxiway, then
    /// the search runs again with that connection's hold-short excluded, so a later connection of the same taxiway ahead is
    /// still judged — at most <see cref="MaxPassedConnectionSearches"/> runs.
    /// </summary>
    private static ResolvedExitInfo? TryFindCandidate(ExitCandidateQuery query)
    {
        if (FindCenterlineCandidate(query) is not { } found)
        {
            return null;
        }

        GroundNode branch = found.Path[0];
        double turnOff = CategoryPerformance.ExitTurnOffSpeed(query.Category, found.ExitAngle);
        return new ResolvedExitInfo
        {
            HoldShortNode = found.HoldShort,
            TaxiwayName = found.Taxiway,
            TurnOffSpeed = turnOff,
            Path = found.Path,
            BranchPointNode = branch,
            SelectionDecelRate = query.BrakingLimitForTurnOffSpeed(turnOff),
            Side = found.Side,
        };
    }

    /// <summary>
    /// <see cref="TryFindCandidate"/>'s search, returning the centerline connection itself (its side included) rather than the
    /// resolved exit: the side-preferred walk from <see cref="ExitCandidateQuery.From"/>, every candidate judged by
    /// <see cref="JudgeCandidate"/>, rerun with passed connections set aside.
    /// </summary>
    private static AirportGroundLayout.CenterlineExitResult? FindCenterlineCandidate(ExitCandidateQuery query)
    {
        HashSet<int>? excludeHoldShortNodes = query.ExcludeHoldShortNodes;
        AirportGroundLayout.CenterlineExitResult? found = null;
        for (int run = 1; run <= MaxPassedConnectionSearches; run++)
        {
            List<int> passedHoldShorts = [];
            found = query.Layout.FindOnSidePreferredExit(
                query.From.Position.Lat,
                query.From.Position.Lon,
                query.Plan.RunwayHeading,
                query.RwyDesignator,
                query.SearchPref,
                query.SidePref,
                excludeBranchPoints: query.ExcludeBranchPoints,
                excludeHoldShortNodes: excludeHoldShortNodes,
                filter: candidate => JudgeCandidate(query, candidate, passedHoldShorts)
            );
            if (passedHoldShorts.Count == 0)
            {
                break;
            }

            excludeHoldShortNodes = [.. excludeHoldShortNodes ?? [], .. passedHoldShorts];
        }

        return found;
    }

    /// <summary>
    /// Most runs <see cref="TryFindCandidate"/> makes while it keeps meeting connections at or behind the aircraft. The
    /// centerline walk looks only ~30 ft back, so one or two runs (a taxiway crossing the runway has a bar on each side)
    /// clear them all.
    /// </summary>
    private const int MaxPassedConnectionSearches = 4;

    /// <summary>
    /// <see cref="TryFindCandidate"/>'s verdict on one <paramref name="candidate"/>: skipped when its taxiway is one the crew
    /// has given up (unless the query judges afresh), when its branch is at or behind the aircraft (its hold-short added to
    /// <paramref name="passedHoldShorts"/>), when it lies past a LAHSO hold-short point, or when <see cref="JudgeExitReach"/>
    /// finds it beyond the query's braking limit for its turn-off speed.
    /// </summary>
    private static AirportGroundLayout.CandidateVerdict JudgeCandidate(
        ExitCandidateQuery query,
        AirportGroundLayout.CenterlineExitResult candidate,
        List<int> passedHoldShorts
    )
    {
        if (!query.IncludeGivenUp && IsGivenUp(query.Aircraft, candidate.Taxiway))
        {
            return AirportGroundLayout.CandidateVerdict.Skip;
        }

        AircraftState aircraft = query.Aircraft;
        GroundNode branchNode = candidate.Path[0];
        double turnOffSpeed = CategoryPerformance.ExitTurnOffSpeed(query.Category, candidate.ExitAngle);
        double brakingLimit = query.BrakingLimitForTurnOffSpeed(turnOffSpeed);
        ExitReach reach = JudgeExitReach(query.From, query.Category, query.Plan.RunwayHeading, branchNode, (turnOffSpeed, brakingLimit));

        if (reach == ExitReach.AtOrBehind)
        {
            passedHoldShorts.Add(candidate.HoldShort.Id);
            return AirportGroundLayout.CandidateVerdict.Skip;
        }

        // Under a LAHSO clearance an exit past the hold-short point is no use, however reachable it is: the aircraft has
        // to be stopped short of the point. Skipped like an unreachable candidate, so the search moves on and the stop at
        // the point remains the fallback.
        if ((query.LahsoStopLimitNm is { } lahsoStopLimitNm) && !query.IgnoreLahso && !BranchFitsBefore(branchNode, query.Plan, lahsoStopLimitNm))
        {
            Log.LogDebug(
                "[Landing] {Callsign}: skipping exit {Taxiway} — branch point is past the LAHSO stop limit at {StopLimit:F2}nm",
                aircraft.Callsign,
                candidate.Taxiway,
                lahsoStopLimitNm
            );
            return AirportGroundLayout.CandidateVerdict.Skip;
        }

        if (reach == ExitReach.BeyondLimit)
        {
            Log.LogDebug(
                "[Landing] {Callsign}: skipping exit {Taxiway} (angle={Angle:F0}, turnOff={Speed:F0}kts) — "
                    + "required decel exceeds the {Limit:F2}kt/s limit at gs={Gs:F1}kts",
                aircraft.Callsign,
                candidate.Taxiway,
                candidate.ExitAngle,
                turnOffSpeed,
                brakingLimit,
                query.From.GroundSpeedKts
            );
            return AirportGroundLayout.CandidateVerdict.Skip;
        }

        return AirportGroundLayout.CandidateVerdict.Accept;
    }

    /// <summary>
    /// Most the rollout brakes to make the committed <paramref name="candidate"/>: the max-effort rate under
    /// <c>EXP</c>; otherwise the rate that selected it plus <see cref="CommittedExitDecelToleranceKtsPerSec"/>, never
    /// above <see cref="CategoryPerformance.FirmBrakingRate"/>. An exit the firm-braking fallback chose, and one restored
    /// without its selection rate, get the firm rate. Past this ceiling the rollout gives the exit up rather than brake
    /// harder than the crew accepted when choosing it.
    /// </summary>
    private static double CommittedExitBrakingLimit(PhaseContext ctx, ResolvedExitInfo candidate)
    {
        if (ctx.Aircraft.Ground.IsExpeditingExit)
        {
            return CategoryPerformance.ExpediteExitDecelRate(ctx.Category);
        }

        double firmRate = CategoryPerformance.FirmBrakingRate(ctx.Category);
        return candidate.SelectionDecelRate is { } selectionRate
            ? Math.Min(selectionRate + CommittedExitDecelToleranceKtsPerSec, firmRate)
            : firmRate;
    }

    /// <summary>
    /// Max deceleration the pilot will accept to select an exit with <paramref name="turnOffSpeed"/> — the
    /// exit-reachability filter (which exits qualify). An instructed exit (<c>ER</c>/<c>EL</c>/<c>EXIT</c>) and an expedited
    /// one (<c>EXP</c>) are judged at <see cref="RolloutBraking.NamedExitBrakingLimit"/>: the firm rate, or the max-effort
    /// rate without delay, so the earliest reachable exit qualifies. Default selection depends on the exit's class (aviation
    /// ruling 2026-09-25): a pilot brakes a little harder to make a high-speed exit (turn-off speed at or above
    /// <see cref="CategoryPerformance.HighSpeedExitSpeed"/>) than a standard one, so a high-speed exit qualifies at the
    /// comfortable-exit rate and a standard exit only at the routine rollout rate.
    /// </summary>
    private double BrakingLimit(PhaseContext ctx, double turnOffSpeed)
    {
        bool expedite = ctx.Aircraft.Ground.IsExpeditingExit;
        if (expedite || _exitResolutionEnabled)
        {
            return RolloutBraking.NamedExitBrakingLimit(ctx.Category, expedite);
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
