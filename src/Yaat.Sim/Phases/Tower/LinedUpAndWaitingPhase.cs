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

    public override bool IsIdleAwaitingCommands => true;

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

    /// <summary>
    /// True once the takeoff clearance has been issued (the phase's single requirement is
    /// satisfied) — the aircraft is waiting out its reaction delay, not waiting on the
    /// controller. Read by <see cref="Commands.RunwaySafetyAdvisor"/>: a lined-up aircraft with
    /// clearance in hand is anticipated separation, not a 3-9-4 "holding in position" conflict.
    /// </summary>
    public bool HasTakeoffClearance => Requirements[0].IsSatisfied;

    /// <summary>Departure instruction from CTO command.</summary>
    public DepartureInstruction? Departure { get; set; }

    /// <summary>Altitude override from CTO command.</summary>
    public int? AssignedAltitude { get; set; }

    /// <summary>
    /// Delay before the "ready, waiting on takeoff clearance" reminder fires once. Anchored at
    /// 90 seconds — the FAA's recommended interval after which the controller should reassert
    /// to a holding pilot why they are still waiting. A real pilot held silent through that
    /// window typically pings the tower right around the same threshold.
    /// </summary>
    public const double LinedUpReadyDelaySeconds = 90.0;

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Aircraft.IsOnGround = true;
        ctx.Targets.TargetSpeed = 0;
        // Cross-runway closed traffic holds aligned with the DEPARTURE runway.
        var rwy = ctx.Aircraft.Phases?.DepartureRunway ?? ctx.Runway;
        if (rwy is not null)
        {
            ctx.Targets.TargetTrueHeading = rwy.TrueHeading;
        }

        Log.LogDebug(
            "[LineUp] {Callsign}: lined up and waiting, rwy={Rwy}, pos=({Lat:F6},{Lon:F6})",
            ctx.Aircraft.Callsign,
            rwy?.Designator ?? "?",
            ctx.Aircraft.Position.Lat,
            ctx.Aircraft.Position.Lon
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        // Hold position until ClearedForTakeoff is satisfied
        ctx.Targets.TargetSpeed = 0;

        if (
            !ctx.Aircraft.HasAnnouncedLinedUpReady
            && ctx.Aircraft.Phases?.DepartureClearance is null
            && ElapsedSeconds >= LinedUpReadyDelaySeconds
            && (ctx.Aircraft.Phases?.DepartureRunway ?? ctx.Runway) is { } rwy
            && ctx.PilotContacts.ResolveFor(ctx.Aircraft, "TWR", rwy.AirportId, ctx.ToEligibilityContext(), false) is { } answering
        )
        {
            var facilityCallName = PilotResponder.ResolveAnsweringCallName(answering, "TWR", "tower");
            var line = PilotResponder.BuildLinedUpReady(ctx.Aircraft, rwy.Designator, facilityCallName);
            PilotResponder.QueueSoloPilotTransmission(ctx.Aircraft, line, PilotTransmissionKind.Proactive, PilotResponder.SourceResponse);
            PilotRequestTracker.RecordRequest(
                ctx.Aircraft,
                PilotPendingRequestKind.Takeoff,
                ctx.ScenarioElapsedSeconds,
                line,
                PilotRequestContext.Runway(rwy.Designator, facilityCallName)
            );
            ctx.Aircraft.HasAnnouncedLinedUpReady = true;
            answering.MarkInitialContact(ctx.Aircraft);
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
