using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Continues climb after takeoff, maintains assigned heading,
/// accelerates to normal climb speed. Self-clears when reaching
/// target altitude. Handles navigation setup for route-based departures.
/// </summary>
public sealed class InitialClimbPhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("InitialClimbPhase");

    private const double DefaultSelfClearAgl = 1500.0;
    private const double HeadingToleranceDeg = 1.0;
    private const double DerMinDistanceNm = 0.0;
    private const double VfrPatternAltMarginFt = 300.0;
    private const double IfrTurnAglFloor = 400.0;
    private const double RvSidPostHandoffDelaySec = 5.0;

    private double _fieldElevation;
    private double _targetAltitude;
    private TrueHeading? _departureHeading;
    private double? _phaseCompletionAltitude;
    private double _selfClearAltitude;

    // Departure turn deferral. Applies to both VFR (AIM 4-3-2 — past DER and within
    // 300 ft of pattern altitude) and IFR (TERPS — past DER and ≥ 400 ft above field
    // elevation). The "Vfr*" naming on the snapshot DTO fields predates the IFR
    // expansion; the runtime fields are deferral-generic.
    private double _runwayDerLat;
    private double _runwayDerLon;
    private TrueHeading _runwayHeading;
    private double _deferredTurnAltitude;
    private bool _deferredTurnApplied = true;

    // Radar vectors SID: fly published heading while tower owns the track.
    // After handoff to a different TCP, continue heading for 5s then turn to first fix.
    private bool _rvSidActive;
    private double _rvSidHandoffElapsed;

    // Typed-leg SID (VA/VI/CF…): hold runway heading to the TERPS gate, then hand off to
    // DepartureProcedurePhase to fly the coded legs. Derived in OnStart; snapshotted for replay.
    private bool _proceduralDeparture;

    public override string Name => "InitialClimb";

    public override PhaseDto ToSnapshot() =>
        new InitialClimbPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = Requirements.Count > 0 ? Requirements.Select(r => r.ToSnapshot()).ToList() : null,
            Departure = Departure?.ToSnapshot(),
            AssignedAltitude = AssignedAltitude,
            DepartureRoute = DepartureRoute is { Count: > 0 } ? DepartureRoute.Select(t => t.ToSnapshot()).ToList() : null,
            IsVfr = IsVfr,
            CruiseAltitude = CruiseAltitude,
            DepartureSidId = DepartureSidId,
            SidDepartureHeadingMagnetic = SidDepartureHeadingMagnetic,
            FieldElevation = _fieldElevation,
            TargetAltitude = _targetAltitude,
            DepartureHeadingDeg = _departureHeading?.Degrees,
            PhaseCompletionAltitude = _phaseCompletionAltitude,
            SelfClearAltitude = _selfClearAltitude,
            RunwayDerLat = _runwayDerLat,
            RunwayDerLon = _runwayDerLon,
            RunwayHeadingDeg = _runwayHeading.Degrees,
            VfrTurnAltitude = _deferredTurnAltitude,
            VfrTurnApplied = _deferredTurnApplied,
            RvSidActive = _rvSidActive,
            RvSidHandoffElapsed = _rvSidHandoffElapsed,
            RvSidDeferHeadingUntilMinAlt = RvSidDeferHeadingUntilMinAlt,
            RvSidHoldRunwayHeading = RvSidHoldRunwayHeading,
            DepartureProcedureLegs = DepartureProcedureLegs?.Select(l => l.ToSnapshot()).ToList(),
            ProceduralDeparture = _proceduralDeparture,
        };

    public static InitialClimbPhase FromSnapshot(InitialClimbPhaseDto dto)
    {
        DepartureInstruction? departure = dto.Departure is not null ? DepartureInstruction.FromSnapshot(dto.Departure) : null;
        List<NavigationTarget>? departureRoute = dto.DepartureRoute?.Select(NavigationTarget.FromSnapshot).ToList();
        var phase = new InitialClimbPhase
        {
            Departure = departure,
            AssignedAltitude = dto.AssignedAltitude,
            DepartureRoute = departureRoute,
            IsVfr = dto.IsVfr,
            CruiseAltitude = dto.CruiseAltitude,
            DepartureSidId = dto.DepartureSidId,
            SidDepartureHeadingMagnetic = dto.SidDepartureHeadingMagnetic,
            RvSidDeferHeadingUntilMinAlt = dto.RvSidDeferHeadingUntilMinAlt,
            RvSidHoldRunwayHeading = dto.RvSidHoldRunwayHeading,
            DepartureProcedureLegs = dto.DepartureProcedureLegs?.Select(ProcedureLeg.FromSnapshot).ToList(),
        };
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        phase.RestoreRequirements(dto.Requirements);
        phase._fieldElevation = dto.FieldElevation;
        phase._targetAltitude = dto.TargetAltitude;
        phase._departureHeading = dto.DepartureHeadingDeg.HasValue ? new TrueHeading(dto.DepartureHeadingDeg.Value) : null;
        phase._phaseCompletionAltitude = dto.PhaseCompletionAltitude;
        phase._selfClearAltitude = dto.SelfClearAltitude;
        phase._runwayDerLat = dto.RunwayDerLat;
        phase._runwayDerLon = dto.RunwayDerLon;
        phase._runwayHeading = new TrueHeading(dto.RunwayHeadingDeg);
        phase._deferredTurnAltitude = dto.VfrTurnAltitude;
        phase._deferredTurnApplied = dto.VfrTurnApplied;
        phase._rvSidActive = dto.RvSidActive;
        phase._rvSidHandoffElapsed = dto.RvSidHandoffElapsed;
        phase._proceduralDeparture = dto.ProceduralDeparture;
        return phase;
    }

    /// <summary>Departure instruction, set by dispatcher.</summary>
    public DepartureInstruction? Departure { get; init; }

    /// <summary>Target altitude override from CTO command.</summary>
    public int? AssignedAltitude { get; init; }

    /// <summary>Pre-resolved navigation targets for route-based departures.</summary>
    public List<NavigationTarget>? DepartureRoute { get; init; }

    /// <summary>Whether the aircraft is VFR.</summary>
    public bool IsVfr { get; init; }

    /// <summary>Filed cruise altitude (feet MSL).</summary>
    public int CruiseAltitude { get; init; }

    /// <summary>SID procedure ID to activate on start (e.g. "PORTE3").</summary>
    public string? DepartureSidId { get; init; }

    /// <summary>Magnetic heading from a radar vectors SID (e.g. 315° from NIMI5 VM leg).</summary>
    public double? SidDepartureHeadingMagnetic { get; init; }

    /// <summary>
    /// Hold runway heading until DER + IFR 400 ft AGL before applying the published RV vectors heading.
    /// </summary>
    public bool RvSidDeferHeadingUntilMinAlt { get; init; }

    /// <summary>
    /// Radar-vectors SID whose published vectors heading could not be resolved (CIFP unavailable — e.g.
    /// the procedure was retired from the current FAA cycle). Hold runway heading and await vectors
    /// instead of navigating direct to the first enroute fix. <see cref="DepartureRoute"/> is retained
    /// as the post-vectors route, loaded after comms handoff like any other radar-vectors departure.
    /// </summary>
    public bool RvSidHoldRunwayHeading { get; init; }

    /// <summary>
    /// Coded leading SID legs (VA/VI/VM/CA/CF) to fly via <see cref="DepartureProcedurePhase"/>.
    /// When set, InitialClimb holds runway heading to the TERPS gate, then spawns the procedure
    /// phase and completes instead of turning direct to the first fix.
    /// </summary>
    public List<ProcedureLeg>? DepartureProcedureLegs { get; init; }

    public override void OnStart(PhaseContext ctx)
    {
        _fieldElevation = ctx.FieldElevation;
        _targetAltitude = ResolveTargetAltitude(ctx);
        // Self-clear at 1500 AGL OR the resolved target, whichever is lower. Without the
        // clamp, a VFR aircraft with a filed cruise altitude below 1500 AGL (short low
        // hops, e.g. M20P at 1400 ft) levels at cruise but never reaches self-clear, so
        // the phase loops forever and DefaultSpeed eventually pushes IAS toward cruise.
        _selfClearAltitude = Math.Min(_fieldElevation + DefaultSelfClearAgl, _targetAltitude);
        _departureHeading = ResolveDepartureHeading(ctx);
        _phaseCompletionAltitude = AssignedAltitude.HasValue ? (double)AssignedAltitude.Value : null;

        ctx.Targets.TargetAltitude = _targetAltitude;
        ctx.Targets.DesiredVerticalRate = null;
        if (!ctx.Targets.HasExplicitTurnRate)
        {
            ctx.Targets.TurnRateOverride = null;
        }

        // Start at initial climb speed; tick-based scheduling will ramp up through altitude bands
        double initialSpeed = AircraftPerformance.InitialClimbSpeed(ctx.AircraftType, ctx.Category);
        ctx.Targets.TargetSpeed = initialSpeed;

        // Radar vectors SID: fly published heading, defer nav route until handoff + 5s.
        // The heading is actively held each tick; nav route is NOT loaded yet so
        // FlightPhysics won't override the heading. RvSidHoldRunwayHeading covers the
        // degradation case where the published vectors heading is unavailable (CIFP miss):
        // hold runway heading and await vectors instead of navigating direct to the first fix.
        bool isRvSid = (SidDepartureHeadingMagnetic.HasValue && (DepartureRoute is { Count: > 0 })) || RvSidHoldRunwayHeading;
        if (isRvSid)
        {
            _rvSidActive = true;
        }

        // Typed-leg SID needs an assigned runway to anchor the TERPS gate; without one
        // (e.g. cleared present position) fall back to the flat direct-to-fix route.
        _proceduralDeparture = DepartureProcedureLegs is { Count: > 0 } && ctx.Runway is not null;

        // Defer the assigned departure turn until the aircraft is past the DER AND at
        // a safe minimum altitude. VFR uses pattern altitude − 300 ft (AIM 4-3-2); IFR
        // uses field elevation + 400 ft (TERPS criterion — IFR ODP design assumes no
        // turns below 400 ft above DER). RV SIDs with a CA/track leg before the VM leg
        // also defer the vectors heading until that gate (runway heading until ~400 ft AGL).
        bool rvDeferVectorsHeading = isRvSid && RvSidDeferHeadingUntilMinAlt && ctx.Runway is not null;
        bool deferTurn = ctx.Runway is not null && (DepartureRequiresTurn() || rvDeferVectorsHeading || _proceduralDeparture);
        if (deferTurn)
        {
            _deferredTurnApplied = false;
            _runwayDerLat = ctx.Runway!.EndLatitude;
            _runwayDerLon = ctx.Runway.EndLongitude;
            _runwayHeading = ctx.Runway.TrueHeading;
            _deferredTurnAltitude = IsVfr
                ? _fieldElevation + CategoryPerformance.PatternAltitudeAgl(ctx.Category) - VfrPatternAltMarginFt
                : _fieldElevation + IfrTurnAglFloor;
            if (rvDeferVectorsHeading || _proceduralDeparture)
            {
                ctx.Targets.TargetTrueHeading = _runwayHeading;
            }
            else if (isRvSid)
            {
                ctx.Targets.TargetTrueHeading = _departureHeading;
            }
        }
        else if (!isRvSid)
        {
            // No runway info or a non-turning departure: apply heading + nav immediately.
            _deferredTurnApplied = true;
            ApplyDepartureTurn(ctx);
        }
        else
        {
            _deferredTurnApplied = true;
            ctx.Targets.TargetTrueHeading = _departureHeading;
        }

        // Activate SID procedure state (via mode ON by default for departures)
        if (DepartureSidId is not null)
        {
            ctx.Aircraft.Procedure.ActiveSidId = DepartureSidId;
            ctx.Aircraft.Procedure.SidViaMode = true;
        }

        Log.LogDebug(
            "[InitialClimb] {Callsign}: started, targetAlt={Alt:F0}ft, speed={Spd:F0}kts, sid={Sid}, route={RouteCount} fixes",
            ctx.Aircraft.Callsign,
            _targetAltitude,
            initialSpeed,
            DepartureSidId ?? "none",
            DepartureRoute?.Count ?? 0
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        // Radar vectors SID: hold published heading while tower owns the track.
        // After handoff to a different TCP, continue heading for 5s then turn to first fix.
        if (_rvSidActive)
        {
            UpdateRvSidHeadingHold(ctx);
        }

        // Departure turn deferral: apply heading/nav route once both conditions met
        if (!_deferredTurnApplied)
        {
            bool pastDer =
                GeoMath.AlongTrackDistanceNm(ctx.Aircraft.Position, new LatLon(_runwayDerLat, _runwayDerLon), _runwayHeading) >= DerMinDistanceNm;
            bool altReached = ctx.Aircraft.Altitude >= _deferredTurnAltitude;
            if (pastDer && altReached)
            {
                _deferredTurnApplied = true;
                if (_proceduralDeparture)
                {
                    SpawnDepartureProcedure(ctx);
                    return true;
                }
                ApplyDepartureTurn(ctx);
            }
        }

        // Update speed target based on current altitude band
        double appropriateSpeed = AircraftPerformance.DefaultSpeed(ctx.AircraftType, ctx.Category, ctx.Aircraft.Altitude, ctx.Targets.TargetAltitude);
        if (ctx.Targets.TargetSpeed is null && Math.Abs(ctx.Aircraft.IndicatedAirspeed - appropriateSpeed) > 5)
        {
            ctx.Targets.TargetSpeed = appropriateSpeed;
        }

        // RV SID hold must persist until ATC hands off comms. Without an explicit
        // CTO-assigned altitude, the heading-met + null-altitude-gate combination
        // would otherwise let the phase exit on the very tick the deferred turn
        // fires (heading just snapped to the VM heading), stopping the heading
        // hold and leaving FlightPhysics to chase whatever route fixes were
        // loaded behind it. While _rvSidActive is true, only UpdateRvSidHeadingHold
        // can release the phase (by flipping _rvSidActive to false after the
        // post-handoff delay).
        TrueHeading? headingTarget = _rvSidActive && !_deferredTurnApplied && RvSidDeferHeadingUntilMinAlt ? _runwayHeading : _departureHeading;
        bool headingDone = _rvSidActive || headingTarget is null || ctx.Aircraft.TrueHeading.AbsAngleTo(headingTarget.Value) < HeadingToleranceDeg;

        bool altitudeDone = _phaseCompletionAltitude is null || ctx.Aircraft.Altitude >= _phaseCompletionAltitude.Value;

        bool complete;
        if (_proceduralDeparture)
        {
            // Only the TERPS-gate hand-off to DepartureProcedurePhase (return true above) ends this phase.
            complete = false;
        }
        else if (_rvSidActive)
        {
            complete = false;
        }
        else if (_departureHeading is not null || _phaseCompletionAltitude is not null)
        {
            complete = headingDone && altitudeDone;
        }
        else
        {
            complete = ctx.Aircraft.Altitude >= _selfClearAltitude;
        }

        if (complete)
        {
            Log.LogDebug(
                "[InitialClimb] {Callsign}: phase complete (hdg={Hdg}, alt={Alt:F0}ft, IAS={Ias:F0}kts)",
                ctx.Aircraft.Callsign,
                _departureHeading?.Degrees.ToString("F0") ?? "n/a",
                ctx.Aircraft.Altitude,
                ctx.Aircraft.IndicatedAirspeed
            );
        }

        return complete;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        // Altitude, speed, and lateral (heading/direct) instructions are all
        // additive: they update the corresponding control targets without
        // tearing down the climb. The CTO-assigned altitude continues to
        // drive the phase until reached; heading and route amendments take
        // effect immediately just as a controller would expect during the
        // initial climb after takeoff. OnCommandAccepted releases the RV SID
        // heading hold and the deferred-turn gate when needed so the phase's
        // own state machines don't clobber the controller's instruction.
        return cmd switch
        {
            CanonicalCommandType.ClimbMaintain
            or CanonicalCommandType.DescendMaintain
            or CanonicalCommandType.Speed
            or CanonicalCommandType.Mach
            or CanonicalCommandType.ReduceToFinalApproachSpeed
            or CanonicalCommandType.ResumeNormalSpeed
            or CanonicalCommandType.DeleteSpeedRestrictions
            or CanonicalCommandType.DirectTo
            or CanonicalCommandType.AppendDirectTo
            or CanonicalCommandType.TurnLeftDirectTo
            or CanonicalCommandType.TurnRightDirectTo
            or CanonicalCommandType.ForceDirectTo
            or CanonicalCommandType.AppendForceDirectTo
            or CanonicalCommandType.FlyHeading
            or CanonicalCommandType.TurnLeft
            or CanonicalCommandType.TurnRight
            or CanonicalCommandType.RelativeLeft
            or CanonicalCommandType.RelativeRight
            or CanonicalCommandType.FlyPresentHeading
            or CanonicalCommandType.ForceHeading => CommandAcceptance.Allowed,
            _ => CommandAcceptance.ClearsPhase,
        };
    }

    public override void OnCommandAccepted(CanonicalCommandType cmd, PhaseContext ctx)
    {
        // Heading and direct-to commands update Targets without clearing the phase.
        // If the RV SID heading hold or deferred-turn gate is still active, OnTick
        // would re-apply the published hold heading every tick (and _rvSidActive
        // forces complete=false), leaving the aircraft stuck in InitialClimb while
        // FlightPhysics steers along the controller's amended route.
        bool releasesRvSidHold =
            cmd
            is CanonicalCommandType.FlyHeading
                or CanonicalCommandType.TurnLeft
                or CanonicalCommandType.TurnRight
                or CanonicalCommandType.RelativeLeft
                or CanonicalCommandType.RelativeRight
                or CanonicalCommandType.FlyPresentHeading
                or CanonicalCommandType.ForceHeading
                or CanonicalCommandType.DirectTo
                or CanonicalCommandType.AppendDirectTo
                or CanonicalCommandType.ForceDirectTo
                or CanonicalCommandType.AppendForceDirectTo
                or CanonicalCommandType.TurnLeftDirectTo
                or CanonicalCommandType.TurnRightDirectTo;

        if (releasesRvSidHold)
        {
            _rvSidActive = false;
            _deferredTurnApplied = true;
        }
    }

    public override void OnEnd(PhaseContext ctx, PhaseStatus endStatus)
    {
        // Clear any directional bias the departure set (RelativeTurnDeparture /
        // FlyHeadingDeparture / DirectFixDeparture with direction). The phase
        // exits at HeadingToleranceDeg=1.0°, but UpdateHeading's snap that
        // normally clears PreferredTurnDirection requires <0.5°, so the bias
        // commonly survives phase exit. A queued DCT (or any subsequent
        // navigation) would then inherit the stale direction and turn the
        // long way around to the next heading.
        ctx.Targets.PreferredTurnDirection = null;
    }

    /// <summary>
    /// Whether the departure instruction involves a turn away from runway heading.
    /// DefaultDeparture and RunwayHeadingDeparture stay on runway heading.
    /// </summary>
    private bool DepartureRequiresTurn()
    {
        return Departure is not (null or DefaultDeparture or RunwayHeadingDeparture);
    }

    /// <summary>
    /// Hand off to <see cref="DepartureProcedurePhase"/> to fly the coded SID legs. Inserted
    /// immediately after this phase so it becomes active when InitialClimb completes at the gate.
    /// </summary>
    private void SpawnDepartureProcedure(PhaseContext ctx)
    {
        var proc = new DepartureProcedurePhase
        {
            Legs = DepartureProcedureLegs!,
            PostRoute = DepartureRoute ?? [],
            AssignedAltitude = AssignedAltitude,
            CruiseAltitude = CruiseAltitude,
        };
        ctx.Aircraft.Phases?.InsertAfterCurrent(proc);
        Log.LogDebug(
            "[InitialClimb] {Callsign}: TERPS gate reached, handing off to DepartureProcedurePhase ({Count} coded legs)",
            ctx.Aircraft.Callsign,
            DepartureProcedureLegs!.Count
        );
    }

    /// <summary>
    /// Set up navigation route and heading for route-based departures.
    /// Called immediately for IFR, deferred for VFR until DER + altitude conditions met.
    /// </summary>
    private void SetupDepartureNavigation(PhaseContext ctx)
    {
        if (DepartureRoute is { Count: > 0 })
        {
            ctx.Targets.NavigationRoute.Clear();
            foreach (var target in DepartureRoute)
            {
                ctx.Targets.NavigationRoute.Add(target);
            }

            // For DirectFixDeparture with a turn direction (TRDCT/TLDCT), pre-set the
            // heading toward the first nav target with the preferred direction. Without
            // this, FlightPhysics.UpdateNavigation clears PreferredTurnDirection on its
            // first tick, losing the controller's turn instruction.
            if (Departure is DirectFixDeparture { Direction: not null } dfd)
            {
                var first = DepartureRoute[0];
                double bearing = GeoMath.BearingTo(ctx.Aircraft.Position, first.Position);
                ctx.Targets.TargetTrueHeading = new TrueHeading(bearing);
                ctx.Targets.PreferredTurnDirection = dfd.Direction;
            }
        }
    }

    /// <summary>
    /// Apply the assigned departure heading, preferred turn direction, and (where
    /// applicable) navigation route. Called once the deferral gates (past DER AND at or
    /// above the minimum safe altitude — pattern alt − 300 ft for VFR, 400 ft AGL for IFR)
    /// are satisfied, or immediately on phase start when deferral is not possible
    /// (no runway info, or non-turning departure).
    /// </summary>
    private void ApplyDepartureTurn(PhaseContext ctx)
    {
        if (_departureHeading is not null)
        {
            ctx.Targets.TargetTrueHeading = _departureHeading.Value;
        }

        switch (Departure)
        {
            case RelativeTurnDeparture rel:
                ctx.Targets.PreferredTurnDirection = rel.Direction;
                break;
            case FlyHeadingDeparture fh:
                ctx.Targets.PreferredTurnDirection = fh.Direction;
                break;
        }

        // RV SID: don't load the nav route until handoff. The published procedure
        // is a heading only; the V6 (or other enroute) expansion is the post-vector
        // route that the pilot picks up after the controller hands them off.
        // UpdateRvSidHeadingHold calls SetupDepartureNavigation when _rvSidActive
        // flips to false after the post-handoff delay.
        if (!_rvSidActive)
        {
            SetupDepartureNavigation(ctx);
        }

        Log.LogDebug(
            "[InitialClimb] {Callsign}: departure turn applied (alt={Alt:F0}ft, hdg={Hdg}, vfr={IsVfr})",
            ctx.Aircraft.Callsign,
            ctx.Aircraft.Altitude,
            _departureHeading?.Degrees.ToString("F0") ?? "nav",
            IsVfr
        );
    }

    /// <summary>
    /// Manages the RV SID heading hold state machine, keyed off comms transfer rather than
    /// track ownership (the two are independent: a HOO or auto-track does not move comms,
    /// FAA 7110.65 §7-6-11). The controller must explicitly hand the aircraft off via CT/FCA
    /// — which sets <see cref="AircraftState.HasLeftStudentFrequency"/> — before the heading
    /// hold releases:
    /// <list type="bullet">
    ///   <item>While the controller still has comms: fly the published heading indefinitely.</item>
    ///   <item>After CT/FCA (HasLeftStudentFrequency flips true): start 5s post-handoff timer.</item>
    ///   <item>After 5s: load nav route (pilot has retuned and is now flying the route).</item>
    /// </list>
    /// Any explicit nav/heading command (D, H, etc.) clears the phase via CanAcceptCommand
    /// regardless of this state machine — a trained RPO can issue direct-to-fix without ever
    /// calling for the contact-departure handoff.
    /// </summary>
    private void UpdateRvSidHeadingHold(PhaseContext ctx)
    {
        if (!ctx.Aircraft.HasLeftStudentFrequency)
        {
            // Controller still has comms — hold heading, reset any accidental timer.
            _rvSidHandoffElapsed = 0;
            ctx.Targets.TargetTrueHeading = ActiveRvSidHoldHeading();
            return;
        }

        if (_rvSidHandoffElapsed == 0)
        {
            Log.LogDebug(
                "[InitialClimb] {Callsign}: RV SID comms handed off, starting {Delay}s delay before vectoring to route",
                ctx.Aircraft.Callsign,
                RvSidPostHandoffDelaySec
            );
        }

        _rvSidHandoffElapsed += ctx.DeltaSeconds;

        if (_rvSidHandoffElapsed >= RvSidPostHandoffDelaySec)
        {
            // Timer expired — load nav route and let FlightPhysics take over.
            _rvSidActive = false;
            SetupDepartureNavigation(ctx);
            Log.LogDebug(
                "[InitialClimb] {Callsign}: RV SID vectored to first fix after {Elapsed:F1}s post-handoff",
                ctx.Aircraft.Callsign,
                _rvSidHandoffElapsed
            );
        }
        else
        {
            // Still in post-handoff delay — continue holding heading.
            ctx.Targets.TargetTrueHeading = ActiveRvSidHoldHeading();
        }
    }

    private TrueHeading? ActiveRvSidHoldHeading()
    {
        if (!_deferredTurnApplied && RvSidDeferHeadingUntilMinAlt)
        {
            return _runwayHeading;
        }

        return _departureHeading;
    }

    private TrueHeading? ResolveDepartureHeading(PhaseContext ctx)
    {
        TrueHeading runwayHeading = ctx.Runway?.TrueHeading ?? ctx.Aircraft.TrueHeading;
        return Departure switch
        {
            RelativeTurnDeparture rel => rel.Direction == TurnDirection.Right
                ? new TrueHeading(runwayHeading.Degrees + rel.Degrees)
                : new TrueHeading(runwayHeading.Degrees - rel.Degrees),
            FlyHeadingDeparture fh => fh.MagneticHeading.ToTrue(ctx.Aircraft.Declination),
            // Radar vectors SID: fly the heading from the VM/VA leg
            DefaultDeparture when SidDepartureHeadingMagnetic.HasValue => new MagneticHeading(SidDepartureHeadingMagnetic.Value).ToTrue(
                ctx.Aircraft.Declination
            ),
            // Radar vectors SID with no resolvable published heading (CIFP unavailable):
            // fly runway heading and await vectors (FAA 7110.65 5-8-2 "FLY RUNWAY HEADING").
            DefaultDeparture when RvSidHoldRunwayHeading => runwayHeading,
            _ => null,
        };
    }

    private double ResolveTargetAltitude(PhaseContext ctx)
    {
        // 0. Controller-assigned altitude from CM/DM issued during takeoff
        // (stored in Targets.AssignedAltitude by FlightCommandHandler, survives TakeoffPhase)
        if (ctx.Aircraft.Targets.AssignedAltitude is { } targetAssigned)
        {
            return targetAssigned;
        }

        // 1. Explicit altitude from CTO command
        if (AssignedAltitude is { } assigned)
        {
            return assigned;
        }

        // 2. Closed traffic → pattern altitude
        if (Departure is ClosedTrafficDeparture)
        {
            return _fieldElevation + CategoryPerformance.PatternAltitudeAgl(ctx.Category);
        }

        // 3. VFR with filed cruise altitude → cruise altitude
        if (IsVfr && CruiseAltitude > 0)
        {
            return CruiseAltitude;
        }

        // 4. VFR without cruise → pattern altitude
        if (IsVfr)
        {
            return _fieldElevation + CategoryPerformance.PatternAltitudeAgl(ctx.Category);
        }

        // 5. IFR with filed cruise altitude → climb to cruise
        if (CruiseAltitude > 0)
        {
            return CruiseAltitude;
        }

        // 6. IFR without cruise altitude → self-clear at 1500 AGL
        return _fieldElevation + DefaultSelfClearAgl;
    }
}
