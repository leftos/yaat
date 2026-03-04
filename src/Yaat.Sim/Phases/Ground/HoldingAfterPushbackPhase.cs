using Yaat.Sim.Commands;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// Aircraft has completed pushback and is holding on the ramp, awaiting taxi instructions.
/// Speed=0, IsOnGround=true. Accepts Taxi, Hold, and Delete.
/// Never completes on its own — waits for an RPO command.
/// </summary>
public sealed class HoldingAfterPushbackPhase : Phase
{
    public override string Name => "Holding After Pushback";

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Targets.TargetSpeed = 0;
        ctx.Targets.TargetHeading = null;
        ctx.Targets.TargetAltitude = null;
        ctx.Aircraft.GroundSpeed = 0;
        ctx.Aircraft.IsOnGround = true;
    }

    public override bool OnTick(PhaseContext ctx)
    {
        ctx.Aircraft.GroundSpeed = 0;
        return false;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.Taxi => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.AirTaxi => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Land => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.HoldPosition => CommandAcceptance.Allowed,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected,
        };
    }
}
