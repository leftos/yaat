using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// Aircraft is stopped on the ground (taxiway, runway, or ramp) awaiting instructions.
/// Catch-all idle state that prevents the aircraft from becoming phase-less after
/// taxi completion, runway crossing completion, air taxi arrival, or any other
/// ground operation that finishes without a specific successor phase.
/// Never completes on its own — waits for an RPO command.
/// </summary>
public sealed class HoldingInPositionPhase : Phase
{
    public override string Name => "Holding In Position";

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Targets.TargetSpeed = 0;
        ctx.Targets.TargetTrueHeading = null;
        ctx.Targets.TargetAltitude = null;
        ctx.Aircraft.IndicatedAirspeed = 0;
        ctx.Aircraft.IsOnGround = true;

        ctx.Logger.LogDebug(
            "[Hold] {Callsign}: holding in position at ({Lat:F6},{Lon:F6}), hdg={Hdg:F0}",
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
            CanonicalCommandType.Pushback => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Follow => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Land => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.LineUpAndWait => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.ClearedTakeoffPresent => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.HoldPosition => CommandAcceptance.Allowed,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected,
        };
    }
}
