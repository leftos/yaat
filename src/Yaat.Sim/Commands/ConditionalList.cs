namespace Yaat.Sim.Commands;

/// <summary>
/// What an entry in the unified conditional list refers to.
/// </summary>
public enum ConditionalEntryKind
{
    /// <summary>The current applied-but-incomplete queue block. Shown as [Active], not deletable.</summary>
    ActiveBlock,

    /// <summary>A pending precondition-gated queue block (AT / ONHO / ATFN / LV / …).</summary>
    QueueBlock,

    /// <summary>A pending deferred dispatch (WAIT / WAITD / BEHIND).</summary>
    Deferred,
}

/// <summary>
/// One entry in an aircraft's unified conditional list.
/// </summary>
/// <param name="Kind">Whether this is the active block, a pending queue block, or a deferred dispatch.</param>
/// <param name="Number">1-based delete index; 0 for the (non-deletable) active block.</param>
/// <param name="Description">Human-readable description for display.</param>
/// <param name="QueueBlockIndex">Absolute index into <c>aircraft.Queue.Blocks</c> when this is a queue/active entry; otherwise null.</param>
/// <param name="DeferredIndex">Index into <c>aircraft.DeferredDispatches</c> when this is a deferred entry; otherwise null.</param>
public readonly record struct ConditionalEntry(ConditionalEntryKind Kind, int Number, string Description, int? QueueBlockIndex, int? DeferredIndex);

/// <summary>
/// Single source of truth for an aircraft's "conditional list" — the pending,
/// precondition-gated work it will execute later. This unifies two storage mechanisms:
/// precondition-gated <see cref="CommandQueue"/> blocks (AT / ONHO / ATFN / LV / …) and
/// <see cref="DeferredDispatch"/>es (WAIT / WAITD / BEHIND). It feeds the SHOWAT/SHOWCOND
/// listing, the "Pending Cmds" client column, and DELAT/DELCOND deletion so all three
/// agree on numbering and contents.
///
/// Numbering: the active block (if any) is shown but not numbered; pending queue blocks are
/// numbered 1..k in queue order; deferred dispatches continue k+1..m in insertion order.
/// Automatic pilot-reaction-delay deferrals (<see cref="DeferredDispatch.IsReactionDelay"/>)
/// are internal timers, not controller-authored conditionals, and are omitted.
/// </summary>
public static class ConditionalList
{
    /// <summary>
    /// Enumerate the aircraft's conditional list. When <paramref name="liveCountdown"/> is
    /// true, time/distance deferrals include their live remaining ETA (for on-demand SHOWAT);
    /// when false they use a stable description (for the broadcast "Pending Cmds" column, whose
    /// fingerprint must not churn every second).
    /// </summary>
    public static List<ConditionalEntry> Enumerate(AircraftState aircraft, bool liveCountdown)
    {
        var entries = new List<ConditionalEntry>();
        var queue = aircraft.Queue;

        int number = 1;
        for (int i = Math.Max(0, queue.CurrentBlockIndex); i < queue.Blocks.Count; i++)
        {
            var block = queue.Blocks[i];
            if (block.IsApplied && block.AllComplete)
            {
                continue; // finished — nothing pending
            }

            var desc = Describe(block);
            if (string.IsNullOrEmpty(desc))
            {
                continue;
            }

            // The applied-but-incomplete current block is executing now: shown as [Active],
            // not deletable. Everything else (an unapplied trigger at the head, or any block
            // behind it) is a pending conditional, numbered and deletable.
            if (i == queue.CurrentBlockIndex && block.IsApplied)
            {
                entries.Add(new ConditionalEntry(ConditionalEntryKind.ActiveBlock, 0, desc, i, null));
            }
            else
            {
                entries.Add(new ConditionalEntry(ConditionalEntryKind.QueueBlock, number, desc, i, null));
                number++;
            }
        }

        for (int j = 0; j < aircraft.DeferredDispatches.Count; j++)
        {
            var d = aircraft.DeferredDispatches[j];
            if (d.IsReactionDelay)
            {
                continue;
            }

            entries.Add(new ConditionalEntry(ConditionalEntryKind.Deferred, number, DescribeDeferred(d, liveCountdown), null, j));
            number++;
        }

        return entries;
    }

    /// <summary>
    /// Render the conditional list as display lines: "[Active] …" for the current block and
    /// "[n] …" for each numbered entry. Empty when there is nothing pending.
    /// </summary>
    public static List<string> ToLines(AircraftState aircraft, bool liveCountdown)
    {
        var lines = new List<string>();
        foreach (var e in Enumerate(aircraft, liveCountdown))
        {
            lines.Add(e.Kind == ConditionalEntryKind.ActiveBlock ? $"[Active] {e.Description}" : $"[{e.Number}] {e.Description}");
        }

        return lines;
    }

    /// <summary>
    /// Outcome of a <see cref="Delete"/> call. <see cref="DeletableCount"/> is the number of
    /// deletable entries that were present (for range/empty messaging by callers).
    /// </summary>
    public readonly record struct DeleteResult(bool Success, int DeletedCount, string? Description, int DeletableCount);

    /// <summary>
    /// Delete a conditional by its 1-based <paramref name="number"/>, or all deletable
    /// conditionals when null. Spans both pending queue blocks and deferred dispatches so
    /// "delete conditional #" reaches either kind. Reaction-delay deferrals are never deleted.
    /// Shared by the live handler and the replay applier so both stay deterministic.
    /// </summary>
    public static DeleteResult Delete(AircraftState aircraft, int? number)
    {
        var deletable = Enumerate(aircraft, liveCountdown: false).Where(e => e.Kind != ConditionalEntryKind.ActiveBlock).ToList();
        if (deletable.Count == 0)
        {
            return new DeleteResult(false, 0, null, 0);
        }

        if (number is null)
        {
            // Remove every deletable queue block (descending index so earlier removals don't
            // shift later ones) plus all controller-authored deferrals. The executing [Active]
            // block is excluded from `deletable`, so it is preserved.
            var queueIndices = deletable
                .Where(e => e.QueueBlockIndex is int)
                .Select(e => e.QueueBlockIndex!.Value)
                .OrderByDescending(x => x)
                .ToList();
            foreach (var qi in queueIndices)
            {
                aircraft.Queue.Blocks.RemoveAt(qi);
            }

            int beforeDeferred = aircraft.DeferredDispatches.Count;
            aircraft.DeferredDispatches.RemoveAll(d => !d.IsReactionDelay);
            int removedDeferred = beforeDeferred - aircraft.DeferredDispatches.Count;

            return new DeleteResult(true, queueIndices.Count + removedDeferred, null, deletable.Count);
        }

        ConditionalEntry? match = null;
        foreach (var e in deletable)
        {
            if (e.Number == number.Value)
            {
                match = e;
                break;
            }
        }

        if (match is not { } entry)
        {
            return new DeleteResult(false, 0, null, deletable.Count);
        }

        if (entry.Kind == ConditionalEntryKind.Deferred && entry.DeferredIndex is int di)
        {
            aircraft.DeferredDispatches.RemoveAt(di);
        }
        else if (entry.QueueBlockIndex is int qi)
        {
            aircraft.Queue.Blocks.RemoveAt(qi);
        }
        else
        {
            return new DeleteResult(false, 0, null, deletable.Count);
        }

        return new DeleteResult(true, 1, entry.Description, deletable.Count);
    }

    private static string Describe(CommandBlock block) =>
        !string.IsNullOrEmpty(block.NaturalDescription) ? block.NaturalDescription : block.Description;

    /// <summary>
    /// Describe a deferred dispatch's gating condition and payload, e.g.
    /// "in 42s: Proceed direct MUNCH" (live) or "waiting: Proceed direct MUNCH" (stable).
    /// </summary>
    public static string DescribeDeferred(DeferredDispatch d, bool liveCountdown)
    {
        var payload = string.Join("; then ", d.Payload.Blocks.Select(b => string.Join(", ", b.Commands.Select(CommandDescriber.DescribeNatural))));

        if (d.GiveWayTarget is not null)
        {
            return $"behind {d.GiveWayTarget}: {payload}";
        }

        if (!liveCountdown)
        {
            return $"waiting: {payload}";
        }

        if (d.IsDistanceBased)
        {
            return $"in {d.RemainingDistanceNm:0.#}nm: {payload}";
        }

        return $"in {(int)Math.Ceiling(d.RemainingSeconds)}s: {payload}";
    }
}
