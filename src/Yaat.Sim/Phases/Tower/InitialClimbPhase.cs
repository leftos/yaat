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
    private const double DefaultSelfClearAgl = 1500.0;
    private const double HeadingToleranceDeg = 1.0;
    private const double VfrDerMinDistanceNm = 0.0;
    private const double VfrPatternAltMarginFt = 300.0;

    private double _fieldElevation;
    private double _targetAltitude;
    private TrueHeading? _departureHeading;
    private double? _phaseCompletionAltitude;
    private double _selfClearAltitude;

    // VFR departure turn deferral (AIM 4-3-2)
    private double _runwayDerLat;
    private double _runwayDerLon;
    private TrueHeading _runwayHeading;
    private double _vfrTurnAltitude;
    private bool _vfrTurnApplied = true;

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
            FieldElevation = _fieldElevation,
            TargetAltitude = _targetAltitude,
            DepartureHeadingDeg = _departureHeading?.Degrees,
            PhaseCompletionAltitude = _phaseCompletionAltitude,
            SelfClearAltitude = _selfClearAltitude,
            RunwayDerLat = _runwayDerLat,
            RunwayDerLon = _runwayDerLon,
            RunwayHeadingDeg = _runwayHeading.Degrees,
            VfrTurnAltitude = _vfrTurnAltitude,
            VfrTurnApplied = _vfrTurnApplied,
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
        phase._vfrTurnAltitude = dto.VfrTurnAltitude;
        phase._vfrTurnApplied = dto.VfrTurnApplied;
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

    public override void OnStart(PhaseContext ctx)
    {
        _fieldElevation = ctx.FieldElevation;
        _selfClearAltitude = _fieldElevation + DefaultSelfClearAgl;
        _targetAltitude = ResolveTargetAltitude(ctx);
        _departureHeading = ResolveDepartureHeading(ctx);
        _phaseCompletionAltitude = AssignedAltitude.HasValue ? (double)AssignedAltitude.Value : null;

        ctx.Targets.TargetAltitude = _targetAltitude;
        ctx.Targets.DesiredVerticalRate = null;
        ctx.Targets.TurnRateOverride = null;

        // Start at initial climb speed; tick-based scheduling will ramp up through altitude bands
        double initialSpeed = AircraftPerformance.InitialClimbSpeed(ctx.AircraftType, ctx.Category);
        ctx.Targets.TargetSpeed = initialSpeed;

        // AIM 4-3-2: VFR departures must maintain runway heading until past the DER
        // and within 300ft of pattern altitude. Defer nav route and heading setup.
        bool deferVfrTurn = IsVfr && DepartureRequiresTurn() && ctx.Runway is not null;
        if (deferVfrTurn)
        {
            _vfrTurnApplied = false;
            _runwayDerLat = ctx.Runway!.EndLatitude;
            _runwayDerLon = ctx.Runway.EndLongitude;
            _runwayHeading = ctx.Runway.TrueHeading;
            _vfrTurnAltitude = _fieldElevation + CategoryPerformance.PatternAltitudeAgl(ctx.Category) - VfrPatternAltMarginFt;
        }
        else
        {
            _vfrTurnApplied = true;
            SetupDepartureNavigation(ctx);
        }

        // Activate SID procedure state (via mode ON by default for departures)
        if (DepartureSidId is not null)
        {
            ctx.Aircraft.ActiveSidId = DepartureSidId;
            ctx.Aircraft.SidViaMode = true;
        }

        ctx.Logger.LogDebug(
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
        // VFR departure turn deferral: apply heading/nav route once both conditions met
        if (!_vfrTurnApplied)
        {
            bool pastDer =
                GeoMath.AlongTrackDistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, _runwayDerLat, _runwayDerLon, _runwayHeading)
                >= VfrDerMinDistanceNm;
            bool altReached = ctx.Aircraft.Altitude >= _vfrTurnAltitude;
            if (pastDer && altReached)
            {
                _vfrTurnApplied = true;
                ApplyDeferredVfrTurn(ctx);
            }
        }

        // Update speed target based on current altitude band
        double appropriateSpeed = AircraftPerformance.DefaultSpeed(ctx.AircraftType, ctx.Category, ctx.Aircraft.Altitude, ctx.Targets.TargetAltitude);
        if (ctx.Targets.TargetSpeed is null && Math.Abs(ctx.Aircraft.IndicatedAirspeed - appropriateSpeed) > 5)
        {
            ctx.Targets.TargetSpeed = appropriateSpeed;
        }

        bool headingDone = _departureHeading is null || ctx.Aircraft.TrueHeading.AbsAngleTo(_departureHeading.Value) < HeadingToleranceDeg;

        bool altitudeDone = _phaseCompletionAltitude is null || ctx.Aircraft.Altitude >= _phaseCompletionAltitude.Value;

        // If heading or altitude was explicitly specified, complete when those are met.
        // Otherwise fall back to self-clear at 1500 AGL.
        bool complete =
            (_departureHeading is not null || _phaseCompletionAltitude is not null)
                ? (headingDone && altitudeDone)
                : ctx.Aircraft.Altitude >= _selfClearAltitude;

        if (complete)
        {
            ctx.Logger.LogDebug(
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
        // All standard RPO commands exit the phase
        return CommandAcceptance.ClearsPhase;
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
                double bearing = GeoMath.BearingTo(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, first.Latitude, first.Longitude);
                ctx.Targets.TargetTrueHeading = new TrueHeading(bearing);
                ctx.Targets.PreferredTurnDirection = dfd.Direction;
            }
        }
    }

    /// <summary>
    /// Apply heading and navigation that was deferred for VFR departures.
    /// </summary>
    private void ApplyDeferredVfrTurn(PhaseContext ctx)
    {
        // Apply heading target
        if (_departureHeading is not null)
        {
            ctx.Targets.TargetTrueHeading = _departureHeading.Value;
        }

        // Apply turn direction
        switch (Departure)
        {
            case RelativeTurnDeparture rel:
                ctx.Targets.PreferredTurnDirection = rel.Direction;
                break;
            case FlyHeadingDeparture fh:
                ctx.Targets.PreferredTurnDirection = fh.Direction;
                break;
        }

        // Load navigation route (OnCourse, DirectFix)
        SetupDepartureNavigation(ctx);

        ctx.Logger.LogDebug(
            "[InitialClimb] {Callsign}: VFR turn applied (alt={Alt:F0}ft, hdg={Hdg})",
            ctx.Aircraft.Callsign,
            ctx.Aircraft.Altitude,
            _departureHeading?.Degrees.ToString("F0") ?? "nav"
        );
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
