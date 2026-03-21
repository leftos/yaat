using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation.Snapshots;

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
        ctx.Targets.TargetTrueHeading = null;
        ctx.Targets.TargetAltitude = null;
        ctx.Aircraft.IndicatedAirspeed = 0;
        ctx.Aircraft.IsOnGround = true;

        ctx.Logger.LogDebug("[Parking] {Callsign}: at parking, spot={Spot}", ctx.Aircraft.Callsign, ctx.Aircraft.ParkingSpot ?? "unknown");
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
            CanonicalCommandType.Pushback => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Taxi => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.AirTaxi => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Land => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.ClearedTakeoffPresent => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected,
        };
    }

    public override PhaseDto ToSnapshot() =>
        new AtParkingPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = SnapshotRequirements(),
        };

    public static AtParkingPhase FromSnapshot(AtParkingPhaseDto dto)
    {
        var phase = new AtParkingPhase();
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        phase.RestoreRequirements(dto.Requirements);
        return phase;
    }
}
