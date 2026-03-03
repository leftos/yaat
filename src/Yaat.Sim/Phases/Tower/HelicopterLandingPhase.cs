using Yaat.Sim.Commands;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Helicopter landing: decelerate toward 0 KIAS while descending.
/// Below FlareAltitude (50ft AGL): slow descent at FlareDescentRate.
/// Touchdown at speed=0, altitude=field elevation. No rollout.
/// </summary>
public sealed class HelicopterLandingPhase : Phase
{
    private double _fieldElevation;
    private bool _touchedDown;

    public override string Name => "Landing-H";

    public override void OnStart(PhaseContext ctx)
    {
        _fieldElevation = ctx.FieldElevation;

        ctx.Targets.TargetAltitude = _fieldElevation;
        ctx.Targets.TargetSpeed = 0;
    }

    public override bool OnTick(PhaseContext ctx)
    {
        if (_touchedDown)
        {
            return true;
        }

        double agl = ctx.Aircraft.Altitude - _fieldElevation;
        double flareAlt = CategoryPerformance.FlareAltitude(ctx.Category);

        if (agl <= flareAlt)
        {
            double flareRate = CategoryPerformance.FlareDescentRate(ctx.Category);
            ctx.Targets.DesiredVerticalRate = -flareRate;
        }

        // Touchdown when at ground level
        if (agl <= 0)
        {
            _touchedDown = true;
            ctx.Aircraft.IsOnGround = true;
            ctx.Aircraft.Altitude = _fieldElevation;
            ctx.Aircraft.VerticalSpeed = 0;
            ctx.Aircraft.GroundSpeed = 0;
            ctx.Targets.TargetAltitude = null;
            ctx.Targets.DesiredVerticalRate = null;
            ctx.Targets.TargetSpeed = null;
            return true;
        }

        return false;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        if (_touchedDown)
        {
            return cmd switch
            {
                CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
                _ => CommandAcceptance.Rejected,
            };
        }

        // Before touchdown, go-around is always possible for helicopters
        return cmd switch
        {
            CanonicalCommandType.GoAround => CommandAcceptance.Allowed,
            CanonicalCommandType.ExitLeft => CommandAcceptance.Allowed,
            CanonicalCommandType.ExitRight => CommandAcceptance.Allowed,
            CanonicalCommandType.ExitTaxiway => CommandAcceptance.Allowed,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected,
        };
    }
}
