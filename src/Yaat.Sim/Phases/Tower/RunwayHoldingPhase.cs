using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Aircraft has stopped on the runway after a LAHSO landing, holding short
/// of the intersecting runway. Waits for CROSS, TAXI, or similar command
/// to release. Similar to HoldingShortPhase but on a runway surface.
/// </summary>
public sealed class RunwayHoldingPhase : Phase
{
    private readonly string _crossingRunwayId;

    public RunwayHoldingPhase(string crossingRunwayId)
    {
        _crossingRunwayId = crossingRunwayId;
    }

    public override string Name => $"Holding Short RWY {_crossingRunwayId}";

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Aircraft.IndicatedAirspeed = 0;
        ctx.Targets.TargetSpeed = 0;
        ctx.Targets.TargetHeading = null;

        ctx.Logger.LogDebug("[LAHSO] {Callsign}: holding short of runway {CrossRwy}", ctx.Aircraft.Callsign, _crossingRunwayId);

        ctx.Aircraft.PendingWarnings.Add($"{ctx.Aircraft.Callsign} holding short runway {_crossingRunwayId}");
    }

    public override bool OnTick(PhaseContext ctx)
    {
        ctx.Aircraft.IndicatedAirspeed = 0;

        foreach (var req in Requirements)
        {
            if (req.IsSatisfied)
            {
                ctx.Logger.LogDebug("[LAHSO] {Callsign}: cleared past runway {CrossRwy}", ctx.Aircraft.Callsign, _crossingRunwayId);
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
            CanonicalCommandType.Taxi => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.ExitLeft => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.ExitRight => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.ExitTaxiway => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.GoAround => CommandAcceptance.Rejected,
            _ => CommandAcceptance.Rejected,
        };
    }

    protected override List<ClearanceRequirement> CreateRequirements()
    {
        return [new ClearanceRequirement { Type = ClearanceType.RunwayCrossing }];
    }
}
