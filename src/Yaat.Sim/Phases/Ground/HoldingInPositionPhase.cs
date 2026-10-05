using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation.Snapshots;

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
    private static readonly ILogger Log = SimLog.CreateLogger("HoldingInPositionPhase");

    public override string Name => "Holding In Position";

    public override bool IsIdleAwaitingCommands => true;

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Targets.TargetSpeed = 0;
        ctx.Targets.TargetTrueHeading = null;
        ctx.Targets.TargetAltitude = null;
        ctx.Aircraft.IndicatedAirspeed = 0;
        ctx.Aircraft.IsOnGround = true;

        Log.LogDebug(
            "[Hold] {Callsign}: holding in position at ({Lat:F6},{Lon:F6}), hdg={Hdg:F0}",
            ctx.Aircraft.Callsign,
            ctx.Aircraft.Position.Lat,
            ctx.Aircraft.Position.Lon,
            ctx.Aircraft.TrueHeading.Degrees
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        ctx.Aircraft.IndicatedAirspeed = 0;
        Pilot.TaxiInRequest.TryAnnounce(ctx, ElapsedSeconds, ctx.Aircraft.Phases?.AssignedRunway?.Designator, ctx.Aircraft.Ground.CurrentTaxiway);
        if (
            (PresetTaxiStopLocation(ctx.Aircraft.Ground) is { } location)
            && (ElapsedSeconds >= InitialCallupCall.AfterTaxiArrivalDelaySeconds(ctx.Aircraft.Callsign))
        )
        {
            InitialCallupCall.TryMake(ctx, location);
        }

        return false;
    }

    /// <summary>
    /// Where the after-taxi-arrival call is made from when the aircraft has come to rest at the stop its spawn's preset taxi
    /// ends at (<see cref="AircraftGroundOps.PresetTaxiStop"/>) and the call is still owed: "at spot 9" when its route ended
    /// at that spot, "on taxiway K" when a route with no destination ended on that taxiway. Null anywhere else, so a
    /// controller's TAXI that took the aircraft elsewhere never triggers the call.
    /// </summary>
    private static ReadyToTaxiLocation? PresetTaxiStopLocation(AircraftGroundOps ground)
    {
        if ((ground.InitialCallup != InitialCallupPlan.AfterTaxiArrival) || ground.InitialCallupDecisionProcessed)
        {
            return null;
        }

        return (ground.PresetTaxiStop is { } stop) && (ground.AssignedTaxiRoute is { } route) ? MatchPresetTaxiStop(ground, stop, route) : null;
    }

    private static ReadyToTaxiLocation? MatchPresetTaxiStop(AircraftGroundOps ground, PresetTaxiStop stop, TaxiRoute route)
    {
        if (stop.Kind == PresetTaxiStopKind.Spot)
        {
            return string.Equals(route.DestinationSpot, stop.Name, StringComparison.OrdinalIgnoreCase) ? ReadyToTaxiLocation.Spot(stop.Name) : null;
        }

        bool endedOnTheTaxiway =
            (stop.Kind == PresetTaxiStopKind.RouteEnd)
            && (route.DestinationSpot is null)
            && (route.DestinationParking is null)
            && string.Equals(ground.CurrentTaxiway, stop.Name, StringComparison.OrdinalIgnoreCase);
        return endedOnTheTaxiway ? ReadyToTaxiLocation.OnTaxiway(stop.Name) : null;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.Taxi or CanonicalCommandType.TaxiAuto => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.AirTaxi => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Pushback or CanonicalCommandType.ForcedPushback => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.FollowGround => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Land => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.LineUpAndWait => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.ClearedTakeoffPresent => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.HoldPosition => CommandAcceptance.Allowed,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected("aircraft is holding position on the taxiway; issue RES, a new TAXI/PUSH/ATXI/LAND/LUAW, or DEL"),
        };
    }

    public override PhaseDto ToSnapshot() =>
        new HoldingInPositionPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = SnapshotRequirements(),
        };

    public static HoldingInPositionPhase FromSnapshot(HoldingInPositionPhaseDto dto)
    {
        var phase = new HoldingInPositionPhase { Status = (PhaseStatus)dto.Status, ElapsedSeconds = dto.ElapsedSeconds };
        phase.RestoreRequirements(dto.Requirements);
        return phase;
    }
}
