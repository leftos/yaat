using Microsoft.Extensions.Logging;
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
        bool changed = EramCrrGroupsChanged;
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
        string label = group.Label.ToUpperInvariant();
        if (group.Lat is not { } lat || group.Lon is not { } lon)
        {
            CrrGroups.Remove(label);
            MarkEramCrrGroupsChanged();
            return;
        }

        EramCrrColor color = Enum.TryParse<EramCrrColor>(group.Color, ignoreCase: true, out EramCrrColor parsed) ? parsed : EramCrrColor.White;
        CrrGroups[label] = new EramCrrGroup(label, color, lat, lon);
        MarkEramCrrGroupsChanged();
    }

    /// <summary>
    /// The room's per-facility ERAM conflict-alert settings (the <c>CA</c> entry). Engine state on every run kind,
    /// snapshotted beside the CRR groups and reset with them. Detection never reads it — <see cref="EramConflicts"/> and
    /// what the AI controllers see stay whole — so it only decides what a host shows each sector.
    /// </summary>
    public EramRoomSettings EramRoomSettings { get; } = new();

    /// <summary>
    /// True when <see cref="ApplyEramRoomEntry"/> has applied an entry since the last drain. Payload-less: a host
    /// re-evaluates what each sector is shown from <see cref="EramRoomSettings"/> itself.
    /// </summary>
    internal bool EramConflictSettingsChanged { get; private set; }

    /// <summary>Marks the settings dirty; the next drain hands the host one <c>OnEramConflictSettingsChanged</c>.</summary>
    internal void MarkEramConflictSettingsChanged() => EramConflictSettingsChanged = true;

    /// <summary>
    /// Takes the flag and clears it, for a path that mutates the engine outside both the action router's drain and
    /// the post-physics one — the same escape the CRR-group flag has.
    /// </summary>
    internal bool DrainEramConflictSettingsChanged()
    {
        bool changed = EramConflictSettingsChanged;
        EramConflictSettingsChanged = false;
        return changed;
    }

    /// <summary>
    /// The room's ERAM sector messages (the <c>SM</c> entry), one per (facility, sector). Engine state on every run kind,
    /// snapshotted and reset beside <see cref="EramRoomSettings"/>. Storing one notifies no host: the push to the addressed
    /// sectors is the recorder's, and it is recorded as chat.
    /// </summary>
    public EramSectorMessages EramSectorMessages { get; } = new();

    /// <summary>
    /// The room's entered weather reports (the <c>WX</c> entry), one per station. Engine state on every run kind,
    /// snapshotted and reset beside <see cref="EramSectorMessages"/>, and kept inside ERAM: no host is told of an entry,
    /// and no client broadcast carries the reports.
    /// </summary>
    public EramWeatherReports EramWeatherReports { get; } = new();

    /// <summary>
    /// Removes the entered weather reports whose expiry instant the session clock has reached. Part of the end-of-second
    /// weather advance (<see cref="AdvanceWeatherTimeline"/>), so a report leaves the store on the second it expires on
    /// every run kind, and a snapshot taken after it never carries it.
    /// </summary>
    private void ExpireEramWeatherReports()
    {
        if (Scenario is { } scenario)
        {
            EramWeatherReports.RemoveExpired(scenario.SimTimeUtc);
        }
    }

    /// <summary>
    /// Applies one recorded ERAM room entry, dispatched on its verb: <c>CA </c> to <see cref="EramRoomSettings"/> (which
    /// tells the host), <c>WX </c> to <see cref="EramWeatherReports"/>, <c>SM </c> and
    /// <c>SMDE </c> to <see cref="EramSectorMessages"/>. A weather report is stamped with the session-clock instant of the
    /// entry's own elapsed time, so a replay stamps it identically. An entry outside the recorded shapes is a recording bug
    /// rather than user input: it is logged, changes nothing and returns false.
    /// </summary>
    public bool ApplyEramRoomEntry(RecordedEramRoomEntry entry)
    {
        bool isConflictSetting = entry.Entry.StartsWith("CA ", StringComparison.Ordinal);
        bool isWeatherReport = entry.Entry.StartsWith("WX ", StringComparison.Ordinal);
        DateTime enteredAtUtc = (Scenario?.SessionStartUtc ?? SimScenarioState.ProcessDayUtc).AddSeconds(entry.ElapsedSeconds);
        bool applied =
            isConflictSetting ? EramRoomSettings.TryApply(entry.FacilityId, entry.Entry)
            : isWeatherReport ? EramWeatherReports.TryApply(entry.Entry, enteredAtUtc)
            : EramSectorMessages.TryApply(entry.FacilityId, entry.Entry);
        if (!applied)
        {
            _logger.LogWarning(
                "Ignoring malformed ERAM room entry {Entry} for facility {Facility} at {Elapsed}s",
                entry.Entry,
                entry.FacilityId,
                entry.ElapsedSeconds
            );
            return false;
        }

        if (isConflictSetting)
        {
            MarkEramConflictSettingsChanged();
        }

        return true;
    }
}
