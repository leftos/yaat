using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Aircraft holds at runway threshold, speed=0, heading=runway heading.
/// Requires ClearedForTakeoff clearance to advance to TakeoffPhase.
/// Stores departure instruction from CTO for TakeoffPhase.
/// </summary>
public sealed class LinedUpAndWaitingPhase : Phase
{
    public override string Name => "LinedUpAndWaiting";

    /// <summary>Departure instruction from CTO command.</summary>
    public DepartureInstruction? Departure { get; set; }

    /// <summary>Altitude override from CTO command.</summary>
    public int? AssignedAltitude { get; set; }

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Aircraft.IsOnGround = true;
        ctx.Targets.TargetSpeed = 0;
        if (ctx.Runway is not null)
        {
            ctx.Targets.TargetTrueHeading = ctx.Runway.TrueHeading;
        }

        ctx.Logger.LogDebug(
            "[LineUp] {Callsign}: lined up and waiting, rwy={Rwy}, pos=({Lat:F6},{Lon:F6})",
            ctx.Aircraft.Callsign,
            ctx.Runway?.Designator ?? "?",
            ctx.Aircraft.Latitude,
            ctx.Aircraft.Longitude
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        // Hold position until ClearedForTakeoff is satisfied
        ctx.Targets.TargetSpeed = 0;
        return Requirements[0].IsSatisfied;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.ClearedForTakeoff => CommandAcceptance.Allowed,
            CanonicalCommandType.CancelTakeoffClearance => CommandAcceptance.Allowed,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected,
        };
    }

    protected override List<ClearanceRequirement> CreateRequirements()
    {
        return [new ClearanceRequirement { Type = ClearanceType.ClearedForTakeoff }];
    }
}
