using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// Aircraft holds short of a runway or explicit hold-short point.
/// Speed=0. Generates a notification on start.
/// Clearance-gated: completes when RunwayCrossing clearance is satisfied
/// (via CROSS, LUAW, or CTO command).
/// </summary>
public sealed class HoldingShortPhase : Phase
{
    private readonly HoldShortPoint _holdShort;

    public HoldingShortPhase(HoldShortPoint holdShort)
    {
        _holdShort = holdShort;
    }

    public HoldShortPoint HoldShort => _holdShort;

    public override string Name => _holdShort.RunwayId is not null
        ? $"Holding Short {_holdShort.RunwayId}"
        : "Holding Short";

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Aircraft.GroundSpeed = 0;
        ctx.Targets.TargetSpeed = 0;

        // Generate notification
        string rwy = _holdShort.RunwayId ?? "unknown";
        string taxiway = ctx.Aircraft.CurrentTaxiway ?? "taxiway";
        ctx.Aircraft.PendingWarnings.Add(
            $"{ctx.Aircraft.Callsign} holding short runway {rwy} at {taxiway}");
    }

    public override bool OnTick(PhaseContext ctx)
    {
        ctx.Aircraft.GroundSpeed = 0;

        // Check if clearance has been satisfied
        foreach (var req in Requirements)
        {
            if (req.IsSatisfied)
            {
                _holdShort.IsCleared = true;
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
            CanonicalCommandType.LineUpAndWait => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.ClearedForTakeoff => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Taxi => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected,
        };
    }

    protected override List<ClearanceRequirement> CreateRequirements()
    {
        return [new ClearanceRequirement { Type = ClearanceType.RunwayCrossing }];
    }
}
