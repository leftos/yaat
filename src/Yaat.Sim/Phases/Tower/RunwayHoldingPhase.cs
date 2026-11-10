using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Follows a LAHSO stop: the aircraft has stopped on the runway after a LAHSO landing, holding short
/// of the intersecting runway. A CROSS of that runway (bare or naming it) releases it; an exit
/// instruction is stored for after the crossing; a TAXI whose route enters that runway is stored
/// (<see cref="PhaseList.TaxiRoute"/>) and starts on the CROSS, while one whose route never enters it
/// runs at once and ends the hold (the TAXI handler decides); DEL replaces it. Similar to
/// HoldingShortPhase but on a runway surface.
/// </summary>
public sealed class RunwayHoldingPhase(string crossingRunwayId) : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("RunwayHoldingPhase");

    private readonly string _crossingRunwayId = crossingRunwayId;

    /// <summary>The runway the aircraft holds short of.</summary>
    public string CrossingRunwayId => _crossingRunwayId;

    public override string Name => $"Holding Short RWY {RunwayIdentifier.ToDisplayDesignator(_crossingRunwayId)}";

    public override PhaseDto ToSnapshot() =>
        new RunwayHoldingPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = Requirements.Count > 0 ? [.. Requirements.Select(r => r.ToSnapshot())] : null,
            CrossingRunwayId = _crossingRunwayId,
        };

    public static RunwayHoldingPhase FromSnapshot(RunwayHoldingPhaseDto dto)
    {
        var phase = new RunwayHoldingPhase(dto.CrossingRunwayId) { Status = (PhaseStatus)dto.Status, ElapsedSeconds = dto.ElapsedSeconds };
        phase.RestoreRequirements(dto.Requirements);
        return phase;
    }

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Aircraft.IndicatedAirspeed = 0;
        ctx.Targets.TargetSpeed = 0;
        ctx.Targets.TargetTrueHeading = null;

        Log.LogDebug("[LAHSO] {Callsign}: holding short of runway {CrossRwy}", ctx.Aircraft.Callsign, _crossingRunwayId);

        Pilot.PilotResponder.RouteByTunedPosition(
            ctx.Aircraft,
            Pilot.PilotReportRouting.From(ctx),
            Pilot.PilotResponder.BuildHoldingShortCrossing(ctx.Aircraft, _crossingRunwayId)
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        // Physics owns ground speed, so a hold re-publishes its zero target every tick: a stale nonzero
        // TargetSpeed left by an earlier phase would have the integrator accelerating against the pin.
        ctx.Aircraft.IndicatedAirspeed = 0;
        ctx.Targets.TargetSpeed = 0;

        foreach (ClearanceRequirement req in Requirements)
        {
            if (req.IsSatisfied)
            {
                Log.LogDebug("[LAHSO] {Callsign}: cleared past runway {CrossRwy}", ctx.Aircraft.Callsign, _crossingRunwayId);
                return true;
            }
        }

        return false;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.CrossRunway => CommandAcceptance.ClearsPhase,
            // A TAXI is accepted in place: the handler stores one whose route enters the runway held short of (the hold stands)
            // and installs any other on a fresh phase list, which ends the hold.
            CanonicalCommandType.Taxi or CanonicalCommandType.TaxiAuto or CanonicalCommandType.ReRouteTaxi => CommandAcceptance.Allowed,
            // An exit is stored for after the crossing clearance, so the hold stands: accepted in place, not by clearing the phase.
            CanonicalCommandType.ExitLeft or CanonicalCommandType.ExitRight or CanonicalCommandType.ExitTaxiway => CommandAcceptance.Allowed,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.GoAround => CommandAcceptance.Rejected(
                $"aircraft is on the runway {HoldingAfterLahsoLanding()}; GA is not applicable on the ground"
            ),
            _ => CommandAcceptance.Rejected(
                $"aircraft is on the runway {HoldingAfterLahsoLanding()}; only CROSS, EL/ER/EXIT, a new TAXI, or DEL apply"
            ),
        };
    }

    /// <summary>How the aircraft's situation reads in a rejected command's reason.</summary>
    private string HoldingAfterLahsoLanding() =>
        $"holding short of runway {RunwayIdentifier.ToDisplayDesignator(_crossingRunwayId)} after a LAHSO landing";

    protected override List<ClearanceRequirement> CreateRequirements() => [new ClearanceRequirement { Type = ClearanceType.RunwayCrossing }];
}
