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
/// and TDLS (room-owned state, no snapshot coverage), coordination channels, the ASDE-X alert inhibits, bookmarks
/// and the room clock, and the flight-plan verbs' input normalization plus the unsupported-track spawn they make for
/// an unknown callsign. As each body crosses, its slot is deleted and the arm becomes a Sim body; the interface
/// shrinks the way <see cref="Spine.IHostSteps"/> does.
/// </para>
/// </summary>
public interface IActionHost
{
    // --- Slots: bodies the host owns ---

    /// <summary>A strip verb (<c>STRIP</c>, <c>AN</c>, <c>HSC</c>, …); the callsign may name no aircraft (half strips, separators, blanks).</summary>
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

    /// <summary>A mutating <c>BM</c> verb. Never recorded.</summary>
    CommandResult ApplyBookmark(BookmarkCommand command);

    /// <summary><c>PAUSE</c> / <c>UNPAUSE</c> / <c>SIMRATE</c> — the room's clock. Never recorded.</summary>
    CommandResult ApplyTransport(ParsedCommand command);

    /// <summary>
    /// A fresh <c>DA</c> / <c>FP</c> / <c>RMK</c>: normalise the typed fields into a flight-plan amendment (and spawn an
    /// unsupported track for an unknown callsign). A recorded one is never applied through here — the
    /// <see cref="RecordedAmendFlightPlan"/> recorded beside it carries the state.
    /// </summary>
    CommandResult ApplyFlightPlanCommand(string callsign, ParsedCommand command, TrackOwner? identity);

    // --- Consumers: what a Sim arm hands over ---

    /// <summary>An aircraft a command put into the world (<c>SPAWN</c>, <c>ADD</c>, <c>GHOST</c>).</summary>
    void OnAircraftSpawned(AircraftState aircraft);

    /// <summary>A <c>DEL</c> removed the aircraft (or its still-queued spawn).</summary>
    void OnAircraftDeleted(string callsign);

    /// <summary>A bare <c>AS</c> selected the connection's acting position.</summary>
    void OnPositionSelected(string connectionId, TrackOwner owner);

    /// <summary>A <c>TIMER</c> set or cancelled a scenario timer.</summary>
    void OnTimersChanged();

    /// <summary><c>HFR</c> / <c>HFROFF</c> / <c>REL</c> changed the held-departure picture.</summary>
    void OnHeldDeparturesChanged();

    /// <summary>A recorded flight-plan amendment was applied to the aircraft.</summary>
    void OnFlightPlanAmended(string callsign);
}
