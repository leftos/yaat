using Yaat.Sim.Asdex;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Simulation.Coast;
using Yaat.Sim.Simulation.Strips;
using Yaat.Sim.Simulation.Tdls;

namespace Yaat.Sim.Simulation.Spine;

/// <summary>
/// Where the drained state changes go — the strip and TDLS change sets, the coordination dirty flag the
/// channels and the tower lists share, the bookmark one, the session clock's and the ERAM CRR groups' — and the
/// hand-over of a live-traffic shadow's spawn, which the feed's sync and a replayed sample both make. Declared on
/// its own because both halves of a host reach it: the action router holds the action view (<c>IActionHost</c>) and
/// the post-physics drain step and the live-traffic sync the consumer view (<see cref="IHostConsumers"/>), and one
/// implementation on a host answers both.
/// </summary>
public interface IStateChangeConsumer
{
    /// <summary>
    /// <paramref name="shadow"/> (in the world) was spawned from a <paramref name="source"/> track: by the live-traffic
    /// sync, which recorded the sample, or by a recorded sample applied back on replay. A shadow's only spawn
    /// hand-over: it never reaches <c>IActionHost.OnAircraftSpawned</c> or <see cref="SimulationEngine.AfterAircraftSpawned"/>.
    /// </summary>
    void OnLiveTrafficSpawned(AircraftState shadow, LiveTrafficSource source);

    /// <summary>
    /// What the strip bodies touched since the last drain: the items whose records changed and whether a full state is
    /// owed. The host broadcasts them unless it is suppressed; a reconstruction drops them and the room re-syncs
    /// afterwards.
    /// </summary>
    void OnStripsChanged(StripChangeSet changes);

    /// <summary>
    /// What the TDLS bodies touched since the last drain: the items whose records changed, the items removed, and
    /// whether a full state is owed. Same suppression rule as <see cref="OnStripsChanged"/>.
    /// </summary>
    void OnTdlsChanged(TdlsChangeSet changes);

    /// <summary>
    /// The coordination lists changed — a channel's release rundown, or a tower list the proximity step touched.
    /// Payload-less: the host re-pushes the whole <c>StarsCoordination</c> topic, which is what the wire carries
    /// anyway (every channel and every tower list in one payload). Same suppression rule as
    /// <see cref="OnStripsChanged"/> — a reconstruction stays silent and the room re-syncs when it lands.
    /// </summary>
    void OnCoordinationChanged();

    /// <summary>
    /// The shared timeline bookmarks changed — a <c>BM</c> verb, or one of the room's bookmark RPCs. Payload-less:
    /// the host re-sends the whole list, which is what the wire carries. Same suppression rule as
    /// <see cref="OnStripsChanged"/>.
    /// </summary>
    void OnBookmarksChanged();

    /// <summary>
    /// The session clock changed — <c>PAUSE</c>, <c>UNPAUSE</c> or <c>SIMRATE</c>, or the host's own pause and
    /// resume, which run the same bodies. Payload-less: the host re-sends the whole sim state. A host answering this
    /// needs no suppression check of its own, because the sim-state broadcast applies it. These are not the only
    /// writers of the clock: a host that pauses a room on its own — an unattended room, or a rewind — does so outside
    /// these bodies and answers for the broadcast itself.
    /// </summary>
    void OnSimStateChanged();

    /// <summary>
    /// The ERAM Continuous Range Readout groups changed — one was created, replaced, recolored or deleted.
    /// Payload-less: the host re-pushes the whole <c>EramCrrGroups</c> topic, which is what the wire carries anyway.
    /// Same suppression rule as <see cref="OnStripsChanged"/>. A deletion is the exception the topic's additive shape
    /// forces: re-pushing the remaining groups cannot unsay one, so the CRC handler that deletes it still sends the
    /// explicit <c>DeleteEramCrrGroups</c> itself.
    /// </summary>
    void OnEramCrrGroupsChanged();

    /// <summary>
    /// The ERAM conflict-alert settings changed — a <c>CA</c> entry turned a function or a sector display on or off.
    /// Payload-less: the host re-evaluates what each sector is shown from <c>SimulationEngine.EramRoomSettings</c>.
    /// Same suppression rule as <see cref="OnStripsChanged"/>.
    /// </summary>
    void OnEramConflictSettingsChanged();

    /// <summary>
    /// The ASDE-X Safety Logic alerts the post-physics detector pass raised and cleared this second at one ASDE-X
    /// facility, called once per facility that moved. The diff rather than the whole set, because CRC's alert topic is
    /// additive with an explicit delete: the new alerts go out as <c>ReceiveAsdexAlerts</c> and the cleared ids as
    /// <c>DeleteAsdexAlerts</c>, on <paramref name="facilityId"/>'s alert topic only. Never called with both sides empty.
    /// Same suppression rule as <see cref="OnStripsChanged"/> — a reconstruction stays silent; the room's resync re-sends
    /// the standing set, and deletes every id the rebuilt state lacks, when the reconstruction lands.
    /// </summary>
    void OnAsdexAlertsChanged(string facilityId, IReadOnlyList<AsdexSafetyAlert> newAlerts, IReadOnlyList<string> clearedAlertIds);

    /// <summary>
    /// The ASDE-X hold bars at one facility whose status changed this second, each with its new status: Active (drawn) or
    /// Suppressed (hidden). CRC's hold-bar topic is an upsert by id that never removes a bar, so a change is the whole bar with
    /// its new status, and a bar leaving the set (a scenario load replacing it) is reported Suppressed. Called once per
    /// facility that changed, never empty. Same suppression rule as <see cref="OnStripsChanged"/>.
    /// </summary>
    void OnAsdexHoldBarsChanged(string facilityId, IReadOnlyList<AsdexHoldBarState> changed);

    /// <summary>
    /// The ASDE-X facilities whose stored safety-logic configuration changed since the last drain, in ordinal order: a CRC
    /// push applied, an <c>ARWY</c> re-pick, or an arrival-alert inhibit's lapse. Payload-less beyond the ids: the host sends
    /// each named facility's stored configuration (<see cref="SimScenarioState.AsdexSafetyLogicConfigs"/>), the one channel
    /// every such change reaches the displays through. Never called empty. Same suppression rule as
    /// <see cref="OnStripsChanged"/>.
    /// </summary>
    void OnAsdexSafetyLogicConfigsChanged(IReadOnlyList<string> facilityIds);

    /// <summary>
    /// The disconnect-coast facets the post-physics expiry step retired this second, ordered by callsign then facet
    /// order. Never called empty. The facets are already gone from <see cref="SimScenarioState.DisconnectCoasts"/>.
    /// </summary>
    void OnDisconnectCoastExpired(IReadOnlyList<ExpiredDisconnectCoastFacet> expired);

    /// <summary>
    /// The callsigns whose disconnect coast a spawn under the same callsign cleared since the last drain, in the order
    /// they were cleared. Never called empty. The entries are already gone from
    /// <see cref="SimScenarioState.DisconnectCoasts"/>.
    /// </summary>
    void OnDisconnectCoastsCleared(IReadOnlyList<string> callsigns);
}
