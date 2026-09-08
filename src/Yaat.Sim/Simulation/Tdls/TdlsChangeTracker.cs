namespace Yaat.Sim.Simulation.Tdls;

/// <summary>One TDLS item that left <see cref="TdlsState.Items"/>: dumped by a controller (lockout set) or removed by the TTL / track-removal sweeps.</summary>
public sealed record TdlsRemoval(string ItemId, string FacilityId, string Callsign, bool Dumped);

/// <summary>
/// What the TDLS mutations touched since the last drain: the ids whose records changed (in the order they were first
/// touched), the items that were removed, and whether a full-state push is owed. The host turns this into broadcasts;
/// a run kind that does not broadcast drops it.
/// </summary>
public sealed record TdlsChangeSet(IReadOnlyList<string> ChangedItemIds, IReadOnlyList<TdlsRemoval> Removed, bool FullState)
{
    public static TdlsChangeSet Empty { get; } = new([], [], FullState: false);
}

/// <summary>
/// The broadcast seam for <see cref="TdlsState"/>: the mutations record what they touched here and the host drains it
/// (the action router after every routed action, one post-physics spine step for what the tick steps produced).
/// Transient: never snapshotted, cleared by <see cref="TdlsState.ClearSession"/>. What serialises it is what
/// serialises every mutation of a run — the room's tick gate on the server, a single thread in a bare engine — not
/// <see cref="TdlsState.Gate"/>, which the marks made from inside a mutation hold but the full-state mark and the
/// drains do not.
/// </summary>
public sealed class TdlsChangeTracker
{
    private readonly List<string> _changed = [];
    private readonly List<TdlsRemoval> _removed = [];
    private bool _fullState;

    public bool HasAny => (_changed.Count > 0) || (_removed.Count > 0) || _fullState;

    /// <summary>Records an item whose record changed. Recorded once per drain, in the order the ids were first touched.</summary>
    public void MarkChanged(string itemId)
    {
        if (!_changed.Contains(itemId))
        {
            _changed.Add(itemId);
        }
    }

    public void MarkRemoved(TdlsRemoval removal) => _removed.Add(removal);

    /// <summary>Records that the whole state has to be re-pushed — a change no per-item message describes (the active ops configuration).</summary>
    public void MarkFullState() => _fullState = true;

    /// <summary>Takes everything accumulated and resets, so the next drain reports only what happened after this one.</summary>
    public TdlsChangeSet Drain()
    {
        if (!HasAny)
        {
            return TdlsChangeSet.Empty;
        }

        var set = new TdlsChangeSet([.. _changed], [.. _removed], _fullState);
        Clear();
        return set;
    }

    public void Clear()
    {
        _changed.Clear();
        _removed.Clear();
        _fullState = false;
    }
}
