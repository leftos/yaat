using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// Aircraft is at a parking spot, engines off. Speed=0, IsOnGround=true.
/// Accepts Pushback, Taxi, and Delete only.
/// Never completes on its own — waits for an RPO command.
/// </summary>
public sealed class AtParkingPhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("AtParkingPhase");

    public override string Name => "At Parking";

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Targets.TargetSpeed = 0;
        ctx.Targets.TargetTrueHeading = null;
        ctx.Targets.TargetAltitude = null;
        ctx.Aircraft.IndicatedAirspeed = 0;
        ctx.Aircraft.IsOnGround = true;

        Log.LogDebug("[Parking] {Callsign}: at parking, spot={Spot}", ctx.Aircraft.Callsign, ctx.Aircraft.Ground.ParkingSpot ?? "unknown");
    }

    /// <summary>
    /// Delay before the spawn check-in fires. Avoids announcing on the same tick the aircraft
    /// appears (gives the world a moment to settle) and gives a barely-perceptible pause that
    /// reads as the pilot reaching for the radio.
    /// </summary>
    public const double ReadyToTaxiDelaySeconds = 5.0;

    public override bool OnTick(PhaseContext ctx)
    {
        ctx.Aircraft.IndicatedAirspeed = 0;

        if (ctx.SoloTrainingMode && !ctx.Aircraft.Ground.HasAnnouncedReady && ElapsedSeconds >= ReadyToTaxiDelaySeconds)
        {
            var facilityCallName = PilotResponder.ResolveContextFacilityCallName(ctx.StudentPositionType, ctx.StudentRadioName, "GND", "ground");
            PilotResponder.QueueSoloPilotTransmission(
                ctx.Aircraft,
                PilotResponder.BuildReadyToTaxi(ctx.Aircraft, facilityCallName),
                PilotTransmissionKind.Proactive,
                PilotResponder.SourceResponse
            );
            ctx.Aircraft.Ground.HasAnnouncedReady = true;
            ctx.Aircraft.HasMadeInitialContact = true;
        }

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
            _ => CommandAcceptance.Rejected("aircraft is parked with engines off; only PUSH/TAXI/ATXI/LAND/CTOPP/DEL apply"),
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
