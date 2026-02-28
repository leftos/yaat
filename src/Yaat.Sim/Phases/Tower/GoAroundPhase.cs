using Yaat.Sim.Commands;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Go-around: full power, climb on runway heading, accelerate.
/// Completes at 1500ft AGL (self-clear) or assigned altitude.
/// RPO commands clear the phase, allowing immediate re-vectoring.
/// </summary>
public sealed class GoAroundPhase : Phase
{
    private const double NoTurnAgl = 400.0;
    private const double SelfClearAgl = 1500.0;

    private double _fieldElevation;
    private double _runwayHeading;

    public override string Name => "GoAround";

    /// <summary>Heading to fly (null = runway heading).</summary>
    public int? AssignedHeading { get; init; }

    /// <summary>Altitude to climb to (null = self-clear at 1500 AGL).</summary>
    public int? TargetAltitude { get; init; }

    public override void OnStart(PhaseContext ctx)
    {
        _fieldElevation = ctx.FieldElevation;
        _runwayHeading = ctx.Runway?.TrueHeading ?? ctx.Aircraft.Heading;

        ctx.Aircraft.IsOnGround = false;

        // TOGA power climb
        double climbRate = CategoryPerformance.InitialClimbRate(ctx.Category);
        double climbSpeed = CategoryPerformance.InitialClimbSpeed(ctx.Category);
        double targetAlt = TargetAltitude ?? (_fieldElevation + SelfClearAgl);

        ctx.Targets.TargetAltitude = targetAlt;
        ctx.Targets.DesiredVerticalRate = climbRate;
        ctx.Targets.TargetSpeed = climbSpeed;
        ctx.Targets.TargetHeading = AssignedHeading ?? _runwayHeading;
        ctx.Targets.PreferredTurnDirection = null;
        ctx.Targets.NavigationRoute.Clear();
    }

    public override bool OnTick(PhaseContext ctx)
    {
        double agl = ctx.Aircraft.Altitude - _fieldElevation;
        double targetAgl = TargetAltitude.HasValue
            ? TargetAltitude.Value - _fieldElevation
            : SelfClearAgl;

        return agl >= targetAgl;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        // All standard commands clear the go-around phase
        return CommandAcceptance.ClearsPhase;
    }
}
