using Yaat.Sim.Commands;

namespace Yaat.Sim.Phases;

public abstract class Phase
{
    private List<ClearanceRequirement>? _requirements;

    public PhaseStatus Status { get; set; } = PhaseStatus.Pending;
    public double ElapsedSeconds { get; set; }

    public abstract string Name { get; }

    public IReadOnlyList<ClearanceRequirement> Requirements =>
        _requirements ??= CreateRequirements();

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
