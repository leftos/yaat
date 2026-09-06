using Yaat.Sim.Commands;

namespace Yaat.Sim.Simulation.Actions;

/// <summary>
/// The action-path view of a host: the arm bodies the host still owns and the consumers a Sim arm notifies. The
/// <see cref="ActionRouter"/> resolves an action's scope and identity, then either runs a Sim body or calls one of the
/// slots here; a host that has nothing to do in a slot refuses it with a result rather than silently succeeding,
/// so a replay records the same verdict live produced once the host that produced it answers the slot.
/// There are no default implementations — a new slot fails the build in every host until each has answered.
///
/// <para>
/// <b>Step-4 debt.</b> Every <c>Apply*</c> member here is a body whose state has not crossed into Yaat.Sim: strips
/// and TDLS (room-owned state, no snapshot coverage), coordination channels, the ASDE-X and SAID display state (the
/// recorded mutations included), bookmarks and the room clock. <see cref="IsPositionAttended"/> is the one query: CRC
/// attendance is the first recorded input ADR 0003 names, and until a recording carries it the consolidate arm asks
/// the host. As each body crosses, its slot is deleted and the arm becomes a Sim body; the interface shrinks the way
/// <see cref="Spine.IHostSteps"/> does.
/// </para>
///
/// <para>
/// The consumers are where a Sim arm's result leaves the simulation: the live room broadcasts, prints strips and
/// pushes display config from them; a replay or reconstruction keeps its change tracker honest and nothing more.
/// </para>
/// </summary>
public interface IActionHost
{
    // --- Slots: bodies the host owns ---

    /// <summary>
    /// A strip verb (<c>STRIP</c>, <c>AN</c>, <c>HSC</c>, …); the callsign may name no aircraft (half strips, separators, blanks).
    /// </summary>
    CommandResult ApplyStrip(string callsign, ParsedCommand command, TrackOwner? identity);

    /// <summary><c>TDLSQ</c> / <c>TDLSS</c> / <c>TDLSW</c> / <c>TDLSD</c> against an aircraft that exists.</summary>
    CommandResult ApplyTdls(AircraftState aircraft, ParsedCommand command);

    /// <summary><c>TDLSOPS</c> — a facility's active operational configuration.</summary>
    CommandResult ApplyTdlsOpsConfig(TdlsOpsConfigCommand command);

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

    // --- Queries: answers only the host has ---

    /// <summary>
    /// Whether a CRC session is signed on to the TCP — the input a full <c>CON+</c> reads to decide which of the sender's
    /// descendants move with it (an attended subsector keeps its own tracks). Attendance is room state that no recording
    /// carries, so a bare or replay run answers false for every TCP.
    /// </summary>
    bool IsPositionAttended(Tcp tcp);

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
