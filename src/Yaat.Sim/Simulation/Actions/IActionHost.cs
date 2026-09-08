using Yaat.Sim.Commands;
using Yaat.Sim.Simulation.Tdls;

namespace Yaat.Sim.Simulation.Actions;

/// <summary>
/// What a strip verb answers: the verdict, and — for a verb that creates an item under a minted id (<c>SEP</c>,
/// <c>HSC</c>, <c>SCAN</c>, <c>BLANK</c>) — the id it minted or reused. Null for every other strip verb and for a refusal; the
/// router bakes a non-null one onto the record so replay creates the item under the same id.
/// </summary>
public sealed record StripApplyResult(CommandResult Result, string? StripId);

/// <summary>
/// The action-path view of a host: the arm bodies the host still owns and the consumers a Sim arm notifies. The
/// <see cref="ActionRouter"/> resolves an action's scope and identity, then either runs a Sim body or calls one of the
/// slots here; a host that has nothing to do in a slot refuses it with a result rather than silently succeeding,
/// so a replay records the same verdict live produced once the host that produced it answers the slot.
/// There are no default implementations — a new slot fails the build in every host until each has answered.
///
/// <para>
/// <b>Step-4 debt.</b> Every <c>Apply*</c> member here is a body whose state has not crossed into Yaat.Sim:
/// coordination channels, the ASDE-X and SAID display state (the recorded mutations included), bookmarks and the
/// room clock. Strips are the halfway case — the <i>state</i> has crossed
/// (<see cref="SimulationEngine.Strips"/>: engine-owned, snapshotted, so every run kind carries it), but
/// <see cref="ApplyStrip"/> and <see cref="ApplyRecordedStripRequest"/> stay because the mutation bodies are still the
/// host's. TDLS has crossed whole: its bodies are the engine's and what they touched reaches the host through
/// <see cref="OnTdlsChanged"/>. The host answers no
/// questions, because CRC attendance, the last one it was asked, is now engine state every run kind carries
/// (<see cref="SimulationEngine.Attendance"/>, fed by <see cref="RecordedAttendanceChange"/>). As each body crosses,
/// its slot is deleted and the arm becomes a Sim body; the interface shrinks the way <see cref="Spine.IHostSteps"/> does.
/// </para>
///
/// <para>
/// The consumers are where a Sim arm's result leaves the simulation: the live room broadcasts and pushes display
/// config from them. What a replay or reconstruction takes from them is the state a run kind must carry — a spawn's
/// strip and PDC are printed and queued on every run kind, since they are engine state a reconstruction has to
/// reproduce — and the broadcasts are what it leaves out.
/// </para>
/// </summary>
public interface IActionHost : ITdlsChangeConsumer
{
    // --- Slots: bodies the host owns ---

    /// <summary>
    /// A strip verb (<c>STRIP</c>, <c>AN</c>, <c>HSC</c>, …); the callsign may name no aircraft (half strips, separators, blanks).
    /// <paramref name="bakedStripId"/> is the id the live run minted for a creating verb, replayed from the record —
    /// null on a fresh action, where the host mints and reports the id it used.
    /// </summary>
    StripApplyResult ApplyStrip(string callsign, ParsedCommand command, TrackOwner? identity, string? bakedStripId);

    /// <summary><c>RD</c> / <c>RDH</c> / <c>RDR</c> / <c>RDACK</c> / <c>RDDEL</c> / … against an aircraft that exists.</summary>
    CommandResult ApplyCoordination(AircraftState aircraft, ParsedCommand command, TrackOwner? identity);

    /// <summary><c>RDAUTO</c> — coordination auto-acknowledge for the acting position.</summary>
    CommandResult ApplyGlobalCoordination(CoordinationAutoAckCommand command, TrackOwner? identity);

    /// <summary><c>ASDXALERTS</c> — clear every ASDE-X alert inhibit in the room.</summary>
    CommandResult ApplyAsdexEnableAllAlerts();

    /// <summary>A mutating <c>BM</c> verb, with the issuing controller's initials for the bookmark's author. Never recorded.</summary>
    CommandResult ApplyBookmark(BookmarkCommand command, string initials);

    /// <summary><c>PAUSE</c> / <c>UNPAUSE</c> / <c>SIMRATE</c> — the room's clock. Never recorded.</summary>
    CommandResult ApplyTransport(ParsedCommand command);

    /// <summary>A recorded CRC ASDE-X mutation (tag / terminate / suspend / inhibit / edit); ASDE-X display state is the room's.</summary>
    void ApplyRecordedAsdexMutation(RecordedAsdexMutation mutation);

    /// <summary>A recorded CRC SAID mutation; SAID state is the room's.</summary>
    void ApplyRecordedSaidMutation(RecordedSaidMutation mutation);

    /// <summary>
    /// A recorded ERAM CRR group created, replaced, recolored or (null latitude) deleted; the groups are the room's,
    /// while membership rides each aircraft's ERAM state through the Sim's <c>LF</c> entries.
    /// </summary>
    void ApplyRecordedEramCrrGroup(RecordedEramCrrGroup group);

    /// <summary>
    /// A recorded flight-strip request; strips are the room's. The host answers the verdict: the aircraft is gone is a
    /// refusal, a room that already holds the record's strip id is a success that printed nothing, otherwise the strip
    /// is printed under the recorded id.
    /// </summary>
    CommandResult ApplyRecordedStripRequest(RecordedStripRequest request);

    /// <summary>A recorded CRC ASDE-X safety-logic configuration push; the facility's runway configuration is the room's.</summary>
    void ApplyRecordedAsdexSafetyLogic(RecordedAsdexSafetyLogicChange change);

    // --- Consumers: what a Sim arm hands over ---

    /// <summary>An aircraft a command put into the world (<c>SPAWN</c>, <c>ADD</c>, <c>GHOST</c>).</summary>
    void OnAircraftSpawned(AircraftState aircraft);

    /// <summary>
    /// A <c>DEL</c> removed the aircraft (or its still-queued spawn), a <c>DROP</c> removed a pure ghost, or a recorded
    /// live-traffic removal took a shadow out. <paramref name="lastState"/> is the aircraft as it was just before — the
    /// state a display coasts from — null when only a queued spawn was removed, the removal is a recorded one, or the
    /// aircraft was a pure ghost (an operator-maintained block leaves outright; it never coasts).
    /// </summary>
    void OnAircraftDeleted(string callsign, AircraftState? lastState);

    /// <summary>A <c>DEL</c> on a live-traffic shadow: the feed is to ignore the callsign from now on; the live run records the removal.</summary>
    void OnLiveTrafficHidden(string callsign);

    /// <summary>A bare <c>AS</c> selected the connection's acting position; <paramref name="tcpCode"/> is the code as typed.</summary>
    void OnPositionSelected(string connectionId, TrackOwner owner, string tcpCode);

    /// <summary>A <c>TRACK</c> acquired the aircraft: coordination items on it are moot.</summary>
    void OnTrackAcquired(string callsign);

    /// <summary>A <c>DROP</c> lifted a ghost overlay off a real aircraft, which stays in the world as itself.</summary>
    void OnGhostOverlayRemoved(string callsign);

    /// <summary>An ASDE-X <c>TERM</c> verb terminated the aircraft's surface track this tick.</summary>
    void OnAsdexTrackTerminated(string callsign);

    /// <summary>A <c>TIMER</c> set or cancelled a scenario timer.</summary>
    void OnTimersChanged();

    /// <summary><c>CON</c> / <c>CON+</c> / <c>DECON</c> changed the manual consolidation overrides.</summary>
    void OnConsolidationChanged();

    /// <summary><c>HFR</c> / <c>HFROFF</c> / <c>REL</c> changed the held-departure picture.</summary>
    void OnHeldDeparturesChanged();

    /// <summary>
    /// A flight-plan verb (<c>FP</c> / <c>DA</c> / <c>RMK</c> / <c>APT</c>) or a recorded amendment changed the aircraft's
    /// flight plan.
    /// </summary>
    void OnFlightPlanAmended(string callsign);

    /// <summary>A recorded weather load or clear was applied: <c>World.Weather</c> and the scenario's timeline changed.</summary>
    void OnWeatherChanged();

    /// <summary>
    /// <c>SHOWAT</c> / <c>SHOWCOND</c> listed the aircraft's pending conditionals (or "No pending commands") — a
    /// read-back for the issuing connection alone, never the room.
    /// </summary>
    void OnQueuedCommandsShown(string connectionId, string callsign, IReadOnlyList<string> lines);
}
