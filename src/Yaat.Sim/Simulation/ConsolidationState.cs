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

    public void Consolidate(Tcp receiving, Tcp sending, bool basic)
    {
        lock (_lock)
        {
            _overrides[sending.Id] = new ManualOverride(receiving.Id, basic);
        }
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
