using Yaat.Sim.Commands;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases;

public abstract class Phase
{
    private List<ClearanceRequirement>? _requirements;

    public PhaseStatus Status { get; set; } = PhaseStatus.Pending;
    public double ElapsedSeconds { get; set; }

    public abstract string Name { get; }

    public IReadOnlyList<ClearanceRequirement> Requirements => _requirements ??= CreateRequirements();

    public abstract PhaseDto ToSnapshot();

    /// <summary>
    /// Called once when the phase becomes active.
    /// Set initial control targets here.
    /// </summary>
    public abstract void OnStart(PhaseContext ctx);

    /// <summary>
    /// Called each tick while the phase is active.
    /// Returns true when the phase is complete.
    /// </summary>
    public abstract bool OnTick(PhaseContext ctx);

    /// <summary>
    /// Called when the phase ends (completed or skipped).
    /// Override for cleanup.
    /// </summary>
    public virtual void OnEnd(PhaseContext ctx, PhaseStatus endStatus) { }

    /// <summary>
    /// When true, the auto speed schedule in FlightPhysics is suppressed.
    /// Override in phases that set their own speed targets (pattern phases).
    /// </summary>
    public virtual bool ManagesSpeed => false;

    /// <summary>
    /// True for terminal "parked/holding, awaiting a controller command" phases whose
    /// OnTick never returns true on its own (AtParking, HoldingAfterPushback, HoldingShort,
    /// LinedUpAndWaiting, ...). While such a phase is current, FlightPhysics.UpdateCommandQueue
    /// advances untriggered queued blocks — the aircraft has nothing else it will ever do, so
    /// the queue is its only source of progress (issue #407: `PUSH; SQ; SQNORM; TAXI ...`
    /// stranded forever once the pushback settled into HoldingAfterPushback). Airborne
    /// command-waiting maneuvers (holds, S-turns) stay false: there the maneuver itself is
    /// the commanded activity and queued blocks must not cancel it.
    /// </summary>
    public virtual bool IsIdleAwaitingCommands => false;

    /// <summary>
    /// Whether an uncleared VFR pilot about to enter Class B airspace may put an orbit hold in front of this phase
    /// (<see cref="PhaseList.InsertHoldInFront"/>) and resume it afterwards. This is the phase's own answer; the gates on
    /// height above the ground and on the final approach fix belong to the caller that inserts the hold. Default false: a
    /// phase that cannot be suspended keeps flying under the altitude cap alone.
    /// </summary>
    /// <param name="ctx">The phase context of the aircraft flying this phase.</param>
    /// <returns>True when this phase can be held behind a boundary hold.</returns>
    public virtual bool CanSuspendForBoundaryHold(PhaseContext ctx) => false;

    /// <summary>
    /// How this phase carries on when the boundary hold in front of it ends (<see cref="PhaseList.EndHold"/>):
    /// <see cref="HoldResume.InPlace"/> when its state is still valid from wherever the orbit left the aircraft, else
    /// <see cref="HoldResume.RestartWith"/> a new, not yet started copy that re-plans the leg from the present position.
    /// Only a phase whose <see cref="CanSuspendForBoundaryHold"/> answers true is ever held, so the default throws.
    /// </summary>
    /// <param name="ctx">The phase context of the aircraft, read where the orbit left it.</param>
    /// <returns>The resume the hold's end applies.</returns>
    /// <exception cref="InvalidOperationException">This phase cannot be suspended for a boundary hold.</exception>
    public virtual HoldResume ResumeAfterBoundaryHold(PhaseContext ctx) =>
        throw new InvalidOperationException($"{Name} cannot be suspended for a boundary hold, so it has no resume");

    /// <summary>
    /// Speed-axis commands: a pure speed instruction that adjusts the speed target
    /// without touching the lateral or vertical path. Single source of truth for
    /// phases whose <see cref="CanAcceptCommand"/> treats speed changes as additive.
    /// The forced speeds (SPEEDF, and SPDN / SPDN RFAS) are members: a forced speed retargets
    /// speed like any other, so it must not cancel a lateral maneuver or an approach.
    /// </summary>
    public static bool IsSpeedFamilyCommand(CanonicalCommandType cmd) =>
        cmd
            is CanonicalCommandType.Speed
                or CanonicalCommandType.ForceSpeedFinal
                or CanonicalCommandType.ForceSpeed
                or CanonicalCommandType.Mach
                or CanonicalCommandType.SpeedMax
                or CanonicalCommandType.ReduceToFinalApproachSpeed
                or CanonicalCommandType.ResumeNormalSpeed
                or CanonicalCommandType.ResumePublishedSpeed
                or CanonicalCommandType.DeleteSpeedRestrictions;

    /// <summary>
    /// Vertical-axis commands that adjust the altitude target without touching the
    /// lateral path.
    /// </summary>
    public static bool IsAltitudeFamilyCommand(CanonicalCommandType cmd) =>
        cmd is CanonicalCommandType.ClimbMaintain or CanonicalCommandType.DescendMaintain;

    /// <summary>
    /// Speed- or altitude-axis adjustment. A phase whose responsibility is lateral
    /// guidance (turns, holds, intercepts, pattern legs) treats these as additive —
    /// they retarget speed/altitude without cancelling the maneuver.
    /// </summary>
    public static bool IsAdditiveAirborneAdjustment(CanonicalCommandType cmd) => IsSpeedFamilyCommand(cmd) || IsAltitudeFamilyCommand(cmd);

    /// <summary>
    /// Landing clearances (CLAND, CLANDF, LAHSO), option clearances (COPT, TG, SG, LA), their cancellation (CLC) and
    /// the runway exits (EL, ER, EXIT). They set what the aircraft does at the runway, not the path flown to it, so
    /// every leg of an instrument approach keeps the approach on them.
    /// </summary>
    public static bool IsLandingClearanceOrExitCommand(CanonicalCommandType cmd) =>
        cmd
            is CanonicalCommandType.ClearedToLand
                or CanonicalCommandType.ForceLanding
                or CanonicalCommandType.LandAndHoldShort
                or CanonicalCommandType.ClearedForOption
                or CanonicalCommandType.TouchAndGo
                or CanonicalCommandType.StopAndGo
                or CanonicalCommandType.LowApproach
                or CanonicalCommandType.CancelLandingClearance
                or CanonicalCommandType.ExitLeft
                or CanonicalCommandType.ExitRight
                or CanonicalCommandType.ExitTaxiway;

    /// <summary>
    /// Whether this phase accepts the given RPO command.
    /// Default: ClearsPhase (most commands exit the phase system).
    /// Override to reject commands during critical sub-states.
    /// </summary>
    public virtual CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd) => CommandAcceptance.ClearsPhase;

    /// <summary>
    /// A proceed on course (<c>OC</c>) refused on an approach leg, a hold-in-lieu, or final: the aircraft keeps the
    /// approach until ATC cancels it (AIM 5-4-6.j). The dispatcher names the cleared approach first when it has one.
    /// </summary>
    protected static CommandAcceptance OnCourseOnApproach { get; } = CommandAcceptance.Rejected("Unable, on the approach");

    /// <summary>
    /// Called after the dispatcher accepts a command (CanAcceptCommand returned Allowed)
    /// and before the command handler runs. Override to release internal state machines
    /// that would otherwise fight a controller-issued change (e.g., a heading hold that
    /// re-applies every tick and would clobber an external FlyHeading).
    /// </summary>
    public virtual void OnCommandAccepted(CanonicalCommandType cmd, PhaseContext ctx) { }

    /// <summary>
    /// Override to define clearance requirements for this phase.
    /// </summary>
    protected virtual List<ClearanceRequirement> CreateRequirements() => [];

    /// <summary>
    /// Restores requirements from a DTO list. Call from FromSnapshot inside subclasses.
    /// </summary>
    protected void RestoreRequirements(List<ClearanceRequirementDto>? dtoRequirements)
    {
        if (dtoRequirements is null || dtoRequirements.Count == 0)
        {
            return;
        }

        _requirements = new List<ClearanceRequirement>(dtoRequirements.Count);
        foreach (ClearanceRequirementDto dto in dtoRequirements)
        {
            _requirements.Add(ClearanceRequirement.FromSnapshot(dto));
        }
    }

    /// <summary>
    /// Serializes requirements to DTO form. Only includes requirements if any have been created.
    /// </summary>
    protected List<ClearanceRequirementDto>? SnapshotRequirements()
    {
        if (_requirements is null || _requirements.Count == 0)
        {
            return null;
        }

        var result = new List<ClearanceRequirementDto>(_requirements.Count);
        foreach (ClearanceRequirement req in _requirements)
        {
            result.Add(req.ToSnapshot());
        }

        return result;
    }

    /// <summary>
    /// Satisfy a clearance requirement by type. Returns true if found.
    /// </summary>
    public bool SatisfyClearance(ClearanceType type)
    {
        foreach (ClearanceRequirement req in Requirements)
        {
            if (req.Type == type && !req.IsSatisfied)
            {
                req.IsSatisfied = true;
                return true;
            }
        }
        return false;
    }
}
