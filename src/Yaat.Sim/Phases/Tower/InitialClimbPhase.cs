using Yaat.Sim.Commands;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Continues climb after takeoff, maintains assigned heading,
/// accelerates to normal climb speed. Self-clears at 1500ft AGL
/// or when reaching assigned altitude.
/// </summary>
public sealed class InitialClimbPhase : Phase
{
    private const double SelfClearAgl = 1500.0;

    private double _fieldElevation;

    public override string Name => "InitialClimb";

    /// <summary>Target altitude assigned by RPO (null = self-clear at 1500 AGL).</summary>
    public int? AssignedAltitude { get; init; }

    public override void OnStart(PhaseContext ctx)
    {
        _fieldElevation = ctx.FieldElevation;

        double targetAlt = AssignedAltitude ?? (_fieldElevation + SelfClearAgl);
        ctx.Targets.TargetAltitude = targetAlt;
        ctx.Targets.DesiredVerticalRate = null;

        // Accelerate to normal climb speed
        double normalSpeed = CategoryPerformance.DefaultSpeed(ctx.Category, targetAlt);
        ctx.Targets.TargetSpeed = normalSpeed;
    }

    public override bool OnTick(PhaseContext ctx)
    {
        double agl = ctx.Aircraft.Altitude - _fieldElevation;
        double targetAgl = AssignedAltitude.HasValue
            ? AssignedAltitude.Value - _fieldElevation
            : SelfClearAgl;

        return agl >= targetAgl;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        // All standard RPO commands exit the phase
        return CommandAcceptance.ClearsPhase;
    }
}
