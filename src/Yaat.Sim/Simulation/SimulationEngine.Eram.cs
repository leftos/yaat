using Yaat.Sim.Simulation.Eram;

namespace Yaat.Sim.Simulation;

// The ERAM Continuous Range Readout half of the engine: the group definitions the CRR view renders, the one body that
// creates, replaces, recolors and deletes them, and the dirty flag the drain turns into the host's one broadcast. The
// groups are engine state on every run kind, snapshotted beside the tower lists, so a replay, a rewind and a session
// restore all carry them; membership is already the aircraft's (AircraftEramState.CrrGroupLabel, written by the LF
// entries).
public sealed partial class SimulationEngine
{
    /// <summary>
    /// The room's CRR groups, keyed by label. Ordinal-ignore-case because a label is matched as typed and the
    /// entries are stored uppercased.
    ///
    /// <para>
    /// <b>Gate invariant.</b> Every write and every read runs under the room's tick gate — the mutations arrive
    /// through the action router, which the hub and the CRC handlers reach inside the gate, and the readers are the
    /// drain's broadcast and the snapshot. The one path that must be kept there deliberately is the initial-data
    /// build for a newly subscribing CRC client (<c>CrcBroadcastService.BuildEramCrrGroupsData</c>): it enumerates
    /// this dictionary from a connection's thread, and a plain <see cref="Dictionary{TKey, TValue}"/> tolerates no
    /// concurrent write.
    /// </para>
    /// </summary>
    public Dictionary<string, EramCrrGroup> CrrGroups { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when <see cref="ApplyCrrGroup"/> has touched the groups since the last drain. Payload-less like the
    /// coordination flag: the wire carries the whole <c>EramCrrGroups</c> topic anyway, so what a host needs to know
    /// is only "re-push it".
    /// </summary>
    internal bool EramCrrGroupsChanged { get; private set; }

    /// <summary>Marks the groups dirty; the next drain hands the host one <c>OnEramCrrGroupsChanged</c>.</summary>
    internal void MarkEramCrrGroupsChanged() => EramCrrGroupsChanged = true;

    /// <summary>
    /// Takes the flag and clears it, for a path that mutates the engine outside both the action router's drain and
    /// the post-physics one — the same escape the coordination and bookmark flags have.
    /// </summary>
    internal bool DrainEramCrrGroupsChanged()
    {
        var changed = EramCrrGroupsChanged;
        EramCrrGroupsChanged = false;
        return changed;
    }

    /// <summary>
    /// Applies one recorded CRR group write: a null latitude or longitude deletes the group, anything else creates or
    /// replaces it whole (which is also how a recolor arrives — the same location under a new colour). An unknown or
    /// missing colour name falls back to <see cref="EramCrrColor.White"/>, the sector default.
    /// </summary>
    public void ApplyCrrGroup(RecordedEramCrrGroup group)
    {
        var label = group.Label.ToUpperInvariant();
        if (group.Lat is not { } lat || group.Lon is not { } lon)
        {
            CrrGroups.Remove(label);
            MarkEramCrrGroupsChanged();
            return;
        }

        var color = Enum.TryParse<EramCrrColor>(group.Color, ignoreCase: true, out var parsed) ? parsed : EramCrrColor.White;
        CrrGroups[label] = new EramCrrGroup(label, color, lat, lon);
        MarkEramCrrGroupsChanged();
    }
}
