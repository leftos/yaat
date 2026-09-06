using Yaat.Sim.Commands;
using Yaat.Sim.Simulation.Actions;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// An action host with no room: every slot refused, every consumer counted or ignored — and a CRC attendance answer
/// the test controls, since attendance is the one input the consolidation and handoff-redirect bodies read from the host.
/// </summary>
public sealed class AttendanceActionHost : IActionHost
{
    public HashSet<string> AttendedTcpIds { get; } = [];

    public int ConsolidationChanges { get; private set; }

    public bool IsPositionAttended(Tcp tcp) => AttendedTcpIds.Contains(tcp.Id);

    public void OnConsolidationChanged() => ConsolidationChanges++;

    public CommandResult ApplyStrip(string callsign, ParsedCommand command, TrackOwner? identity) => ActionRefusals.HostOnly(command);

    public CommandResult ApplyTdls(AircraftState aircraft, ParsedCommand command) => ActionRefusals.HostOnly(command);

    public CommandResult ApplyTdlsOpsConfig(TdlsOpsConfigCommand command) => ActionRefusals.HostOnly(command);

    public CommandResult ApplyCoordination(AircraftState aircraft, ParsedCommand command, TrackOwner? identity) => ActionRefusals.HostOnly(command);

    public CommandResult ApplyGlobalCoordination(CoordinationAutoAckCommand command, TrackOwner? identity) => ActionRefusals.HostOnly(command);

    public CommandResult ApplyAsdexEnableAllAlerts() => ActionRefusals.HostOnly("ASDXALERTS");

    public CommandResult ApplyBookmark(BookmarkCommand command) => ActionRefusals.HostOnly(command);

    public CommandResult ApplyTransport(ParsedCommand command) => ActionRefusals.HostOnly(command);

    public CommandResult ApplyFlightPlanCommand(string callsign, ParsedCommand command, TrackOwner? identity) => ActionRefusals.HostOnly(command);

    public void OnAircraftSpawned(AircraftState aircraft) { }

    public void OnAircraftDeleted(string callsign) { }

    public void OnPositionSelected(string connectionId, TrackOwner owner) { }

    public void OnTimersChanged() { }

    public void OnHeldDeparturesChanged() { }

    public void OnFlightPlanAmended(string callsign) { }
}
