namespace Yaat.Sim.Simulation;

/// <summary>
/// Thread-safe storage for manual consolidation overrides within a room.
/// Room-scoped: mutated on RPO/CRC consolidation commands, read on broadcast.
/// </summary>
public sealed class ConsolidationState
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, ManualOverride> _overrides = [];

    public record ManualOverride(string ReceivingTcpId, bool IsBasic);

    /// <summary>
    /// Records a manual override consolidating <paramref name="sending"/> into
    /// <paramref name="receiving"/>, replacing any existing override for the sender.
    /// Returns false without mutating anything when the override would consolidate a
    /// TCP into itself or close a loop (1F → 1G → 1X → 1F). A circular combine has no
    /// operational meaning, and owner resolution bails out on one — which would read as
    /// "no attended owner" and silently disable auto-accept suppression for a sector a
    /// live controller is working. Rejecting here keeps every writer safe, since this is
    /// the only path that adds an override.
    /// </summary>
    public bool Consolidate(Tcp receiving, Tcp sending, bool basic)
    {
        lock (_lock)
        {
            if (WouldCreateCycle(receiving.Id, sending.Id))
            {
                return false;
            }

            _overrides[sending.Id] = new ManualOverride(receiving.Id, basic);
            return true;
        }
    }

    /// <summary>
    /// Walks the override chain up from the prospective receiver. Reaching the sender
    /// means the new edge would close a loop. The sender's own (about to be replaced)
    /// entry is never consulted, so re-pointing an already-consolidated TCP elsewhere is
    /// not mistaken for a cycle. Caller must hold the lock.
    /// </summary>
    private bool WouldCreateCycle(string receivingId, string sendingId)
    {
        var current = receivingId;
        var visited = new HashSet<string>();
        while (visited.Add(current))
        {
            if (current == sendingId)
            {
                return true;
            }

            if (!_overrides.TryGetValue(current, out var ov))
            {
                return false;
            }

            current = ov.ReceivingTcpId;
        }

        // Already-cyclic chain (only reachable via Restore) — refuse to extend it.
        return true;
    }

    public void Deconsolidate(Tcp tcp)
    {
        lock (_lock)
        {
            _overrides.Remove(tcp.Id);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _overrides.Clear();
        }
    }

    public ManualOverride? GetOverride(string tcpId)
    {
        lock (_lock)
        {
            return _overrides.TryGetValue(tcpId, out var ov) ? ov : null;
        }
    }

    /// <summary>
    /// Removes all overrides where the given TCP is the receiver or the sender.
    /// </summary>
    public void RemoveOverridesInvolving(string tcpId)
    {
        lock (_lock)
        {
            _overrides.Remove(tcpId);

            var toRemove = new List<string>();
            foreach (var (key, value) in _overrides)
            {
                if (value.ReceivingTcpId == tcpId)
                {
                    toRemove.Add(key);
                }
            }

            foreach (var key in toRemove)
            {
                _overrides.Remove(key);
            }
        }
    }

    /// <summary>
    /// Replaces all overrides with the provided set. Used during snapshot restore.
    /// </summary>
    public void Restore(Dictionary<string, ManualOverride> overrides)
    {
        lock (_lock)
        {
            _overrides.Clear();
            foreach (var (key, value) in overrides)
            {
                _overrides[key] = value;
            }
        }
    }

    /// <summary>
    /// Returns a snapshot of all current overrides (for testing/inspection).
    /// </summary>
    public Dictionary<string, ManualOverride> GetSnapshot()
    {
        lock (_lock)
        {
            return new Dictionary<string, ManualOverride>(_overrides);
        }
    }
}
