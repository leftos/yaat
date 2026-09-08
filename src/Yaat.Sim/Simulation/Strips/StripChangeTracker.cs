namespace Yaat.Sim.Simulation.Strips;

/// <summary>
/// What the strip mutations touched since the last drain: the ids whose records changed (in the order they were first
/// touched) and whether a full-state push is owed — a move, a delete or a printer-queue change, which no per-item
/// message describes. The host turns this into broadcasts; a run kind that does not broadcast drops it.
/// </summary>
public sealed record StripChangeSet(IReadOnlyList<string> ChangedItemIds, bool FullState)
{
    public static StripChangeSet Empty { get; } = new([], FullState: false);
}

/// <summary>
/// The broadcast seam for <see cref="FlightStripState"/>: the mutations record what they touched here and the host
/// drains it (the action router after every routed action, one post-physics spine step for what the tick steps
/// produced). Transient: never snapshotted, cleared by <see cref="FlightStripState.ClearSession"/>. What serialises it
/// is what serialises every mutation of a run — the room's tick gate on the server, a single thread in a bare engine —
/// not <see cref="FlightStripState.Gate"/>, which the marks made from inside a mutation hold but the full-state mark
/// and the drains do not.
/// </summary>
public sealed class StripChangeTracker
{
    private readonly List<string> _changed = [];
    private bool _fullState;

    public bool HasAny => (_changed.Count > 0) || _fullState;

    /// <summary>Records an item whose record changed. Recorded once per drain, in the order the ids were first touched.</summary>
    public void MarkChanged(string itemId)
    {
        if (!_changed.Contains(itemId))
        {
            _changed.Add(itemId);
        }
    }

    /// <summary>Records that the whole state has to be re-pushed — a rack, printer-queue or deletion change no per-item message describes.</summary>
    public void MarkFullState() => _fullState = true;

    /// <summary>Takes everything accumulated and resets, so the next drain reports only what happened after this one.</summary>
    public StripChangeSet Drain()
    {
        if (!HasAny)
        {
            return StripChangeSet.Empty;
        }

        var set = new StripChangeSet([.. _changed], _fullState);
        Clear();
        return set;
    }

    public void Clear()
    {
        _changed.Clear();
        _fullState = false;
    }
}
