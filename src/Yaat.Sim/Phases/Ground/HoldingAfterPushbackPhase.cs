using Microsoft.Extensions.Logging;
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
        ctx.Targets.TargetTrueHeading = null;
        ctx.Targets.TargetAltitude = null;
        ctx.Aircraft.IndicatedAirspeed = 0;
        ctx.Aircraft.IsOnGround = true;

        ctx.Logger.LogDebug(
            "[Push] {Callsign}: holding after pushback at ({Lat:F6},{Lon:F6}), hdg={Hdg:F0}",
            ctx.Aircraft.Callsign,
            ctx.Aircraft.Latitude,
            ctx.Aircraft.Longitude,
            ctx.Aircraft.TrueHeading.Degrees
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        ctx.Aircraft.IndicatedAirspeed = 0;
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
