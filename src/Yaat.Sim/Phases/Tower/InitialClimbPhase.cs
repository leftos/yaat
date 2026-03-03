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

    private double _fieldElevation;
    private double _targetAltitude;

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

    public override void OnStart(PhaseContext ctx)
    {
        _fieldElevation = ctx.FieldElevation;
        _targetAltitude = ResolveTargetAltitude(ctx);

        ctx.Targets.TargetAltitude = _targetAltitude;
        ctx.Targets.DesiredVerticalRate = null;
        ctx.Targets.TurnRateOverride = null;

        // Accelerate to normal climb speed
        double normalSpeed = CategoryPerformance.DefaultSpeed(ctx.Category, _targetAltitude);
        ctx.Targets.TargetSpeed = normalSpeed;

        // Set up navigation for route-based departures
        if (DepartureRoute is { Count: > 0 })
        {
            ctx.Targets.NavigationRoute.Clear();
            foreach (var target in DepartureRoute)
            {
                ctx.Targets.NavigationRoute.Add(
                    new NavigationTarget
                    {
                        Name = target.Name,
                        Latitude = target.Latitude,
                        Longitude = target.Longitude,
                    }
                );
            }
        }
    }

    public override bool OnTick(PhaseContext ctx)
    {
        return ctx.Aircraft.Altitude >= _targetAltitude;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        // All standard RPO commands exit the phase
        return CommandAcceptance.ClearsPhase;
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

        // 5. IFR → self-clear at 1500 AGL
        return _fieldElevation + DefaultSelfClearAgl;
    }
}
