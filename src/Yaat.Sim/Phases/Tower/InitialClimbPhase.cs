using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;

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

    private double _fieldElevation;
    private double _targetAltitude;
    private double? _departureHeading;
    private double? _phaseCompletionAltitude;
    private double _selfClearAltitude;

    public override string Name => "InitialClimb";

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

        // Set up navigation for route-based departures
        if (DepartureRoute is { Count: > 0 })
        {
            ctx.Targets.NavigationRoute.Clear();
            foreach (var target in DepartureRoute)
            {
                ctx.Targets.NavigationRoute.Add(target);
            }
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
        // Update speed target based on current altitude band
        double appropriateSpeed = AircraftPerformance.DefaultSpeed(ctx.AircraftType, ctx.Category, ctx.Aircraft.Altitude, ctx.Targets.TargetAltitude);
        if (ctx.Targets.TargetSpeed is null && Math.Abs(ctx.Aircraft.IndicatedAirspeed - appropriateSpeed) > 5)
        {
            ctx.Targets.TargetSpeed = appropriateSpeed;
        }

        bool headingDone =
            _departureHeading is null || Math.Abs(FlightPhysics.NormalizeAngle(ctx.Aircraft.Heading - _departureHeading.Value)) < HeadingToleranceDeg;

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
                _departureHeading?.ToString("F0") ?? "n/a",
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

    private double? ResolveDepartureHeading(PhaseContext ctx)
    {
        double runwayHeading = ctx.Runway?.TrueHeading ?? ctx.Aircraft.Heading;
        return Departure switch
        {
            RelativeTurnDeparture rel => rel.Direction == TurnDirection.Right
                ? FlightPhysics.NormalizeHeadingInt(runwayHeading + rel.Degrees)
                : FlightPhysics.NormalizeHeadingInt(runwayHeading - rel.Degrees),
            FlyHeadingDeparture fh => fh.Heading,
            _ => null,
        };
    }

    private double ResolveTargetAltitude(PhaseContext ctx)
    {
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
