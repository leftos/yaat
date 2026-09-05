using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Simulation.Actions;

/// <summary>
/// The per-connection position selections a bare <c>AS {tcp}</c> makes: connection id → the <see cref="TrackOwner"/>
/// that connection acts as until it selects another. One instance serves every run kind — the live server hands
/// each engine it creates the room's instance, so a selection outlives scenario reloads and rewinds the way it
/// always did, while a Sim-only host keeps the engine's own. Identity resolution reads it through
/// <see cref="Commands.TrackResolver.ResolveIdentity"/>, where an AI connection's own position takes precedence
/// over anything selected under its id.
///
/// Guarded by a lock: the server writes from SignalR command handlers, the CRC session hooks
/// (<c>SyncCrcPositionToRpo</c>) and session-persistence rebinds, and reads from the tick thread's reconstruction.
/// </summary>
public sealed class PositionSelections
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, TrackOwner> _byConnection = new(StringComparer.Ordinal);

    public void Select(string connectionId, TrackOwner owner)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionId);
        lock (_gate)
        {
            _byConnection[connectionId] = owner;
        }
    }

    public bool TryGet(string connectionId, out TrackOwner owner)
    {
        lock (_gate)
        {
            return _byConnection.TryGetValue(connectionId, out owner!);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _byConnection.Clear();
        }
    }

    /// <summary>A copy ordered by connection id, so a snapshot of the same selections is byte-stable.</summary>
    public SortedDictionary<string, TrackOwner> Snapshot()
    {
        lock (_gate)
        {
            return new SortedDictionary<string, TrackOwner>(_byConnection, StringComparer.Ordinal);
        }
    }

    /// <summary>Replaces every selection with the snapshot's; a null snapshot (pre-feature) leaves the map empty.</summary>
    public void Restore(IReadOnlyDictionary<string, TrackOwnerDto>? snapshot)
    {
        lock (_gate)
        {
            _byConnection.Clear();
            if (snapshot is null)
            {
                return;
            }

            foreach (var (connectionId, owner) in snapshot)
            {
                _byConnection[connectionId] = TrackOwner.FromSnapshot(owner);
            }
        }
    }
}
