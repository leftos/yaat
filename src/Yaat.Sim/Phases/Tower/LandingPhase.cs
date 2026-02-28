using Yaat.Sim.Commands;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Flare, touchdown, and rollout deceleration.
/// Flare begins at category-specific altitude AGL.
/// Touchdown sets IsOnGround=true. Rollout completes at 20 kts.
/// </summary>
public sealed class LandingPhase : Phase
{
    private const double RolloutCompleteSpeed = 20.0;

    private double _fieldElevation;
    private bool _touchedDown;

    public override string Name => "Landing";

    public override void OnStart(PhaseContext ctx)
    {
        _fieldElevation = ctx.FieldElevation;

        // Continue approach descent toward field elevation
        ctx.Targets.TargetAltitude = _fieldElevation;
    }

    public override bool OnTick(PhaseContext ctx)
    {
        double agl = ctx.Aircraft.Altitude - _fieldElevation;

        if (!_touchedDown)
        {
            return TickAirborne(ctx, agl);
        }

        return TickRollout(ctx);
    }

    private bool TickAirborne(PhaseContext ctx, double agl)
    {
        double flareAlt = CategoryPerformance.FlareAltitude(ctx.Category);

        if (agl <= flareAlt)
        {
            // Flare: reduce descent rate
            double flareRate = CategoryPerformance.FlareDescentRate(ctx.Category);
            ctx.Targets.DesiredVerticalRate = -flareRate;
        }

        // Touchdown
        if (agl <= 0)
        {
            _touchedDown = true;
            ctx.Aircraft.IsOnGround = true;
            ctx.Aircraft.Altitude = _fieldElevation;
            ctx.Aircraft.VerticalSpeed = 0;
            ctx.Targets.TargetAltitude = null;
            ctx.Targets.DesiredVerticalRate = null;

            // Set touchdown speed and begin deceleration
            double tdSpeed = CategoryPerformance.TouchdownSpeed(ctx.Category);
            if (ctx.Aircraft.GroundSpeed > tdSpeed)
            {
                ctx.Aircraft.GroundSpeed = tdSpeed;
            }
        }

        return false;
    }

    private bool TickRollout(PhaseContext ctx)
    {
        // Decelerate on the ground
        double decelRate = CategoryPerformance.RolloutDecelRate(ctx.Category);
        double newSpeed = ctx.Aircraft.GroundSpeed - decelRate * ctx.DeltaSeconds;
        if (newSpeed < 0)
        {
            newSpeed = 0;
        }
        ctx.Aircraft.GroundSpeed = newSpeed;
        ctx.Targets.TargetSpeed = null;

        return ctx.Aircraft.GroundSpeed <= RolloutCompleteSpeed;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        if (!_touchedDown)
        {
            // During flare, reject most commands
            return cmd switch
            {
                CanonicalCommandType.GoAround => CommandAcceptance.Allowed,
                CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
                _ => CommandAcceptance.Rejected,
            };
        }

        // During rollout, reject speed/heading changes
        return cmd switch
        {
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.GoAround => CommandAcceptance.Rejected,
            _ => CommandAcceptance.Rejected,
        };
    }
}
