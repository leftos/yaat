namespace Yaat.Sim.Phases;

public sealed class PhaseList
{
    public RunwayInfo? AssignedRunway { get; set; }

    /// <summary>
    /// Landing clearance that persists across phases.
    /// Set by CTL command (possibly on downwind/base), consumed by FinalApproachPhase.
    /// Cleared by CTOC or supplanted by a new clearance for a different runway.
    /// </summary>
    public ClearanceType? LandingClearance { get; set; }

    /// <summary>
    /// Runway ID the landing clearance applies to (null if no clearance active).
    /// </summary>
    public string? ClearedRunwayId { get; set; }

    /// <summary>
    /// When set, the aircraft is doing pattern work (MLT/MRT/CTOMLT/CTOMRT).
    /// Each approach defaults to touch-and-go and re-enters the pattern.
    /// Null means the aircraft will full-stop on landing.
    /// </summary>
    public PatternDirection? TrafficDirection { get; set; }

    public int CurrentIndex { get; private set; }

    /// <summary>
    /// The full phase list. Mutable — commands and clearances may insert,
    /// remove, or replace future phases at any time.
    /// </summary>
    public List<Phase> Phases { get; } = [];

    public Phase? CurrentPhase =>
        CurrentIndex >= 0 && CurrentIndex < Phases.Count
            ? Phases[CurrentIndex]
            : null;

    public bool IsComplete => CurrentIndex >= Phases.Count;

    public void Add(Phase phase)
    {
        Phases.Add(phase);
    }

    public void Start(PhaseContext ctx)
    {
        CurrentIndex = 0;
        if (CurrentPhase is { } phase)
        {
            phase.Status = PhaseStatus.Active;
            phase.OnStart(ctx);
        }
    }

    public void AdvanceToNext(PhaseContext ctx)
    {
        var current = CurrentPhase;
        if (current is not null)
        {
            current.Status = PhaseStatus.Completed;
            current.OnEnd(ctx, PhaseStatus.Completed);
        }

        CurrentIndex++;

        if (CurrentPhase is { } next)
        {
            next.Status = PhaseStatus.Active;
            next.OnStart(ctx);
        }
    }

    public void InsertAfterCurrent(Phase phase)
    {
        int insertAt = CurrentIndex + 1;
        if (insertAt > Phases.Count)
        {
            insertAt = Phases.Count;
        }
        Phases.Insert(insertAt, phase);
    }

    /// <summary>
    /// Insert a sequence of phases immediately after the current phase,
    /// preserving the order of the provided list.
    /// </summary>
    public void InsertAfterCurrent(IEnumerable<Phase> phases)
    {
        int insertAt = CurrentIndex + 1;
        if (insertAt > Phases.Count)
        {
            insertAt = Phases.Count;
        }
        Phases.InsertRange(insertAt, phases);
    }

    /// <summary>
    /// Remove all pending phases after the current one and replace them.
    /// The current (active) phase is not affected.
    /// </summary>
    public void ReplaceUpcoming(IEnumerable<Phase> phases)
    {
        int removeFrom = CurrentIndex + 1;
        if (removeFrom < Phases.Count)
        {
            Phases.RemoveRange(removeFrom, Phases.Count - removeFrom);
        }
        Phases.AddRange(phases);
    }

    /// <summary>
    /// Skip forward to the first pending phase of type T.
    /// All intermediate phases get Skipped status.
    /// </summary>
    public void SkipTo<T>(PhaseContext ctx) where T : Phase
    {
        while (CurrentPhase is not null && CurrentPhase is not T)
        {
            var skipped = CurrentPhase;
            skipped.Status = PhaseStatus.Skipped;
            skipped.OnEnd(ctx, PhaseStatus.Skipped);
            CurrentIndex++;
        }

        if (CurrentPhase is { Status: PhaseStatus.Pending } next)
        {
            next.Status = PhaseStatus.Active;
            next.OnStart(ctx);
        }
    }

    /// <summary>
    /// End the active phase with Skipped status and mark list complete.
    /// </summary>
    public void Clear(PhaseContext ctx)
    {
        if (CurrentPhase is { } current && current.Status == PhaseStatus.Active)
        {
            current.Status = PhaseStatus.Skipped;
            current.OnEnd(ctx, PhaseStatus.Skipped);
        }

        // Skip all remaining phases
        for (int i = CurrentIndex + 1; i < Phases.Count; i++)
        {
            Phases[i].Status = PhaseStatus.Skipped;
        }

        CurrentIndex = Phases.Count;
    }
}
