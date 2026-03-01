using Yaat.Sim.Commands;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// Aircraft is at a parking spot, engines off. Speed=0, IsOnGround=true.
/// Accepts Pushback, Taxi, and Delete only.
/// Never completes on its own — waits for an RPO command.
/// </summary>
public sealed class AtParkingPhase : Phase
{
    public override string Name => "At Parking";

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
            CanonicalCommandType.Pushback => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Taxi => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected,
        };
    }
}
