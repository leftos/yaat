using Yaat.Sim.Commands;
using Yaat.Sim.Simulation;
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

    public int WeatherChanges { get; private set; }

    public int TransportApplies { get; private set; }

    public List<RecordedAsdexMutation> AsdexMutations { get; } = [];

    public List<RecordedSaidMutation> SaidMutations { get; } = [];

    public List<string> SpawnedCallsigns { get; } = [];

    public List<string> DeletedCallsigns { get; } = [];

    public List<string> HiddenLiveTraffic { get; } = [];

    public List<string> AcquiredCallsigns { get; } = [];

    public List<string> OverlaysRemoved { get; } = [];

    public List<string> AmendedCallsigns { get; } = [];

    public List<(string ConnectionId, string Callsign, List<string> Lines)> ShownQueues { get; } = [];

    public bool IsPositionAttended(Tcp tcp) => AttendedTcpIds.Contains(tcp.Id);

    public void OnConsolidationChanged() => ConsolidationChanges++;

    public CommandResult ApplyStrip(string callsign, ParsedCommand command, TrackOwner? identity) => ActionRefusals.HostOnly(command);

    public CommandResult ApplyTdls(AircraftState aircraft, ParsedCommand command) => ActionRefusals.HostOnly(command);

    public CommandResult ApplyTdlsOpsConfig(TdlsOpsConfigCommand command) => ActionRefusals.HostOnly(command);

    public CommandResult ApplyCoordination(AircraftState aircraft, ParsedCommand command, TrackOwner? identity) => ActionRefusals.HostOnly(command);

    public CommandResult ApplyGlobalCoordination(CoordinationAutoAckCommand command, TrackOwner? identity) => ActionRefusals.HostOnly(command);

    public CommandResult ApplyAsdexEnableAllAlerts() => ActionRefusals.HostOnly("ASDXALERTS");

    public CommandResult ApplyBookmark(BookmarkCommand command, string initials) => ActionRefusals.HostOnly(command);

    public CommandResult ApplyTransport(ParsedCommand command)
    {
        TransportApplies++;
        return ActionRefusals.HostOnly(command);
    }

    public void ApplyRecordedAsdexMutation(RecordedAsdexMutation mutation) => AsdexMutations.Add(mutation);

    public void ApplyRecordedSaidMutation(RecordedSaidMutation mutation) => SaidMutations.Add(mutation);

    public List<RecordedEramCrrGroup> CrrGroups { get; } = [];

    public void ApplyRecordedEramCrrGroup(RecordedEramCrrGroup group) => CrrGroups.Add(group);

    public void OnAircraftSpawned(AircraftState aircraft) => SpawnedCallsigns.Add(aircraft.Callsign);

    public void OnAircraftDeleted(string callsign, AircraftState? lastState) => DeletedCallsigns.Add(callsign);

    public void OnLiveTrafficHidden(string callsign) => HiddenLiveTraffic.Add(callsign);

    public List<(string ConnectionId, TrackOwner Owner, string TcpCode)> SelectedPositions { get; } = [];

    public void OnPositionSelected(string connectionId, TrackOwner owner, string tcpCode) => SelectedPositions.Add((connectionId, owner, tcpCode));

    public void OnTrackAcquired(string callsign) => AcquiredCallsigns.Add(callsign);

    public void OnGhostOverlayRemoved(string callsign) => OverlaysRemoved.Add(callsign);

    public void OnAsdexTrackTerminated(string callsign) { }

    public void OnQueuedCommandsShown(string connectionId, string callsign, IReadOnlyList<string> lines) =>
        ShownQueues.Add((connectionId, callsign, lines.ToList()));

    public void OnTimersChanged() { }

    public void OnHeldDeparturesChanged() { }

    public void OnFlightPlanAmended(string callsign) => AmendedCallsigns.Add(callsign);

    public void OnWeatherChanged() => WeatherChanges++;
}
