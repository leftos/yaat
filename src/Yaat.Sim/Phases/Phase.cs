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
    /// Whether this phase accepts the given RPO command.
    /// Default: ClearsPhase (most commands exit the phase system).
    /// Override to reject commands during critical sub-states.
    /// </summary>
    public virtual CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return CommandAcceptance.ClearsPhase;
    }

    /// <summary>
    /// Override to define clearance requirements for this phase.
    /// </summary>
    protected virtual List<ClearanceRequirement> CreateRequirements()
    {
        return [];
    }

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
        foreach (var dto in dtoRequirements)
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
        foreach (var req in _requirements)
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
        foreach (var req in Requirements)
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
