using Yaat.Sim.Commands;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Aircraft holds at runway threshold, speed=0, heading=runway heading.
/// Requires ClearedForTakeoff clearance to advance to TakeoffPhase.
/// Stores assigned heading and turn direction from CTO for TakeoffPhase.
/// </summary>
public sealed class LinedUpAndWaitingPhase : Phase
{
    public override string Name => "LinedUpAndWaiting";

    /// <summary>Heading assigned by CTO command (null = fly runway heading).</summary>
    public int? AssignedHeading { get; set; }

    /// <summary>Turn direction from CTOR/CTOL/CTOMLT/CTOMRT.</summary>
    public TurnDirection? AssignedTurn { get; set; }

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Aircraft.IsOnGround = true;
        ctx.Targets.TargetSpeed = 0;
        if (ctx.Runway is not null)
        {
            ctx.Targets.TargetHeading = ctx.Runway.TrueHeading;
        }
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
