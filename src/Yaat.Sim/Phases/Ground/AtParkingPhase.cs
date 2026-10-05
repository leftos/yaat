using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// Aircraft is at a parking spot, engines off. Speed=0, IsOnGround=true.
/// Accepts Pushback, Taxi, AirTaxi, Land, ClearedTakeoffPresent, FollowGround, and Delete.
/// Never completes on its own — waits for an RPO command.
/// </summary>
public sealed class AtParkingPhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("AtParkingPhase");

    public override string Name => "At Parking";

    public override bool IsIdleAwaitingCommands => true;

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Targets.TargetSpeed = 0;
        ctx.Targets.TargetTrueHeading = null;
        ctx.Targets.TargetAltitude = null;
        ctx.Aircraft.IndicatedAirspeed = 0;
        ctx.Aircraft.IsOnGround = true;

        // An aircraft the loader did not arm (an arrival that taxied to this stand, a warp, a generated aircraft, or a
        // spawn whose presets script its ground sequence) makes no initial call. Marking the decision processed up front
        // keeps OnTick's pacing path from ever firing on it, however long it sits here.
        if (ctx.Aircraft.Ground.InitialCallup == InitialCallupPlan.None)
        {
            ctx.Aircraft.Ground.InitialCallupDecisionProcessed = true;
        }

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

        if (!ctx.Aircraft.Ground.InitialCallupDecisionProcessed && (CallDelaySeconds(ctx) is { } delay) && (ElapsedSeconds >= delay))
        {
            InitialCallupCall.TryMake(ctx, ReadyToTaxiLocation.ForStandCall(ctx.Aircraft));
        }

        return false;
    }

    /// <summary>
    /// How long after this phase starts the aircraft calls, or null when it makes no call from here: the stand call's
    /// delay, or the post-push setup delay on a stand a tow ended on. An after-push aircraft still at its spawn stand
    /// (not yet pushed) calls only after its push.
    /// </summary>
    private static double? CallDelaySeconds(PhaseContext ctx) =>
        ctx.Aircraft.Ground.InitialCallup switch
        {
            InitialCallupPlan.StandCall => ReadyToTaxiDelaySeconds,
            InitialCallupPlan.AfterPush when ctx.Aircraft.Ground.PushedBackFrom is not null => InitialCallupCall.PostPushSetupDelaySeconds(
                ctx.Category
            ),
            _ => null,
        };

    /// <summary>
    /// Leaving the stand uncalled ends the call this stand owed: an aircraft that leaves before its stand call (a
    /// controller's TAXI or PUSH) never makes it later, and a stand it taxis to starts no new one; likewise for the
    /// after-push call owed on a stand a tow ended on. A tow off the stand carries an uncalled call over itself
    /// (<c>GroundCommandHandler.InstallTugMove</c>), and an after-push aircraft still at its spawn stand and an
    /// after-arrival one keep theirs, since their call comes after the move this phase ends for.
    /// </summary>
    public override void OnEnd(PhaseContext ctx, PhaseStatus endStatus)
    {
        AircraftGroundOps ground = ctx.Aircraft.Ground;
        bool owedHere =
            (ground.InitialCallup == InitialCallupPlan.StandCall)
            || (
                (ground.InitialCallup == InitialCallupPlan.AfterPush) && (ground.PushedBackFrom is not null) && !ground.InitialCallupDecisionProcessed
            );
        if (owedHere)
        {
            Log.LogDebug(
                "[Parking] {Callsign}: left the stand with its {Plan} call unmade; plan dropped",
                ctx.Aircraft.Callsign,
                ground.InitialCallup
            );
            ground.InitialCallup = InitialCallupPlan.None;
        }
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.Pushback or CanonicalCommandType.ForcedPushback => CommandAcceptance.ClearsPhase,
            // A tug move plans its legs off the pose the aircraft is in and clears this phase itself once the
            // plan holds, so clearing it here would cost the handler the "is it on a stand" its first leg turns on.
            CanonicalCommandType.PushbackMulti or CanonicalCommandType.ForcedPushbackMulti => CommandAcceptance.Allowed,
            CanonicalCommandType.Taxi or CanonicalCommandType.TaxiAuto => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.AirTaxi => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Land => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.ClearedTakeoffPresent => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.FollowGround => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected("aircraft is parked with engines off; only PUSH/PUSHM/TAXI/ATXI/LAND/CTOPP/FOLLOWG/DEL apply"),
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
        var phase = new AtParkingPhase { Status = (PhaseStatus)dto.Status, ElapsedSeconds = dto.ElapsedSeconds };
        phase.RestoreRequirements(dto.Requirements);
        return phase;
    }
}
