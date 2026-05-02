using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Aircraft holds at runway threshold, speed=0, heading=runway heading.
/// Requires ClearedForTakeoff clearance to advance to TakeoffPhase.
/// Stores departure instruction from CTO for TakeoffPhase.
/// </summary>
public sealed class LinedUpAndWaitingPhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("LinedUpAndWaitingPhase");

    public override string Name => "LinedUpAndWaiting";

    public override PhaseDto ToSnapshot() =>
        new LinedUpAndWaitingPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = Requirements.Count > 0 ? Requirements.Select(r => r.ToSnapshot()).ToList() : null,
            Departure = Departure?.ToSnapshot(),
            AssignedAltitude = AssignedAltitude,
        };

    public static LinedUpAndWaitingPhase FromSnapshot(LinedUpAndWaitingPhaseDto dto)
    {
        var phase = new LinedUpAndWaitingPhase
        {
            Departure = dto.Departure is not null ? DepartureInstruction.FromSnapshot(dto.Departure) : null,
            AssignedAltitude = dto.AssignedAltitude,
        };
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        phase.RestoreRequirements(dto.Requirements);
        return phase;
    }

    /// <summary>Departure instruction from CTO command.</summary>
    public DepartureInstruction? Departure { get; set; }

    /// <summary>Altitude override from CTO command.</summary>
    public int? AssignedAltitude { get; set; }

    /// <summary>
    /// Delay before the "ready, waiting on takeoff clearance" reminder fires once. Mirrors
    /// <see cref="Ground.AtParkingPhase.ReadyToTaxiDelaySeconds"/>'s pattern but longer — the
    /// pilot pauses a beat after hitting the runway before nudging the controller.
    /// </summary>
    public const double LinedUpReadyDelaySeconds = 10.0;

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Aircraft.IsOnGround = true;
        ctx.Targets.TargetSpeed = 0;
        if (ctx.Runway is not null)
        {
            ctx.Targets.TargetTrueHeading = ctx.Runway.TrueHeading;
        }

        Log.LogDebug(
            "[LineUp] {Callsign}: lined up and waiting, rwy={Rwy}, pos=({Lat:F6},{Lon:F6})",
            ctx.Aircraft.Callsign,
            ctx.Runway?.Designator ?? "?",
            ctx.Aircraft.Position.Lat,
            ctx.Aircraft.Position.Lon
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        // Hold position until ClearedForTakeoff is satisfied
        ctx.Targets.TargetSpeed = 0;

        if (
            ctx.SoloTrainingMode
            && !ctx.Aircraft.HasAnnouncedLinedUpReady
            && ctx.Aircraft.Phases?.DepartureClearance is null
            && ElapsedSeconds >= LinedUpReadyDelaySeconds
            && ctx.Runway is { } rwy
        )
        {
            ctx.Aircraft.PendingNotifications.Add(PilotResponder.BuildLinedUpReady(ctx.Aircraft, rwy.Designator));
            ctx.Aircraft.HasAnnouncedLinedUpReady = true;
            ctx.Aircraft.HasMadeInitialContact = true;
        }

        return Requirements[0].IsSatisfied;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.ClimbMaintain => CommandAcceptance.Allowed,
            CanonicalCommandType.DescendMaintain => CommandAcceptance.Allowed,
            CanonicalCommandType.ClearedForTakeoff => CommandAcceptance.Allowed,
            CanonicalCommandType.CancelTakeoffClearance => CommandAcceptance.Allowed,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected("aircraft is lined up and waiting on the runway; only CTO/CTOC, CM/DM, or DEL apply"),
        };
    }

    protected override List<ClearanceRequirement> CreateRequirements()
    {
        return [new ClearanceRequirement { Type = ClearanceType.ClearedForTakeoff }];
    }
}
