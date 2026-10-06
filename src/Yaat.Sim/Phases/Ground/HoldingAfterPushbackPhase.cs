using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// Aircraft has completed pushback and is holding on the ramp, awaiting taxi instructions.
/// Speed=0, IsOnGround=true. Accepts Taxi, Hold, and Delete.
/// Never completes on its own — waits for an RPO command.
/// </summary>
public sealed class HoldingAfterPushbackPhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("HoldingAfterPushbackPhase");

    public override string Name => "Holding After Pushback";

    public override bool IsIdleAwaitingCommands => true;

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Targets.TargetSpeed = 0;
        ctx.Targets.TargetTrueHeading = null;
        ctx.Targets.TargetAltitude = null;
        ctx.Aircraft.IndicatedAirspeed = 0;
        ctx.Aircraft.IsOnGround = true;

        Log.LogDebug(
            "[Push] {Callsign}: holding after pushback at ({Lat:F6},{Lon:F6}), hdg={Hdg:F0}",
            ctx.Aircraft.Callsign,
            ctx.Aircraft.Position.Lat,
            ctx.Aircraft.Position.Lon,
            ctx.Aircraft.TrueHeading.Degrees
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        ctx.Aircraft.IndicatedAirspeed = 0;

        AircraftGroundOps ground = ctx.Aircraft.Ground;
        if (
            (ground.InitialCallup == InitialCallupPlan.AfterPush)
            && !ground.InitialCallupDecisionProcessed
            && (ElapsedSeconds >= InitialCallupCall.PostPushSetupDelaySeconds(ctx.Category))
        )
        {
            InitialCallupCall.TryMake(ctx, AfterPushLocation(ground));
        }

        return false;
    }

    /// <summary>
    /// What the after-push call names: the spot the tow ended on ("at spot 5"), else the stand the push left ("pushed back
    /// from gate F8"), else the ramp.
    /// </summary>
    private static ReadyToTaxiLocation AfterPushLocation(AircraftGroundOps ground)
    {
        if (ground.PushEndSpot is { Length: > 0 } spot)
        {
            return ReadyToTaxiLocation.Spot(spot);
        }

        return ground.PushedBackFrom is { Length: > 0 } stand ? ReadyToTaxiLocation.PushedBackFrom(stand) : ReadyToTaxiLocation.Ramp;
    }

    /// <summary>
    /// Leaving the post-push hold uncalled (a controller's TAXI before the call) ends the after-push call: wherever the
    /// aircraft goes next starts no new one. A further tow carries it over itself (<c>GroundCommandHandler.InstallTugMove</c>).
    /// </summary>
    public override void OnEnd(PhaseContext ctx, PhaseStatus endStatus)
    {
        AircraftGroundOps ground = ctx.Aircraft.Ground;
        if ((ground.InitialCallup == InitialCallupPlan.AfterPush) && !ground.InitialCallupDecisionProcessed)
        {
            Log.LogDebug("[PostPush] {Callsign}: left the post-push hold with its after-push call unmade; plan dropped", ctx.Aircraft.Callsign);
            ground.InitialCallup = InitialCallupPlan.None;
        }
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.Pushback or CanonicalCommandType.ForcedPushback => CommandAcceptance.ClearsPhase,
            // A tug move is offered from the alley as well as from the stand: an aircraft that has already been
            // pushed out is the one a ramp controller repositions. It clears this phase itself once its plan
            // holds, and its first leg turns on the aircraft not being on a stand, which clearing here would cost.
            CanonicalCommandType.PushbackMulti or CanonicalCommandType.ForcedPushbackMulti => CommandAcceptance.Allowed,
            CanonicalCommandType.Taxi or CanonicalCommandType.TaxiAuto => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.AirTaxi => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Land => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.HoldPosition => CommandAcceptance.Allowed,
            CanonicalCommandType.FollowGround => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected("aircraft is holding after pushback; only HOLD, FOLLOWG, or a new PUSH/PUSHM/TAXI/ATXI/LAND apply"),
        };
    }

    public override PhaseDto ToSnapshot() =>
        new HoldingAfterPushbackPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = SnapshotRequirements(),
        };

    public static HoldingAfterPushbackPhase FromSnapshot(HoldingAfterPushbackPhaseDto dto)
    {
        var phase = new HoldingAfterPushbackPhase { Status = (PhaseStatus)dto.Status, ElapsedSeconds = dto.ElapsedSeconds };
        phase.RestoreRequirements(dto.Requirements);
        return phase;
    }
}
