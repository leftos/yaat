using Yaat.Sim.Commands;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Simulation.Strips;
using Yaat.Sim.Simulation.Tdls;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// An action host with no room: every slot refused, every consumer counted or ignored. Attendance is engine state
/// (<see cref="AttendanceTestSupport.Attend"/>), not something the host answers.
/// </summary>
public sealed class AttendanceActionHost : IActionHost
{
    public int ConsolidationChanges { get; private set; }

    public int WeatherChanges { get; private set; }

    public int TransportApplies { get; private set; }

    public List<RecordedAsdexMutation> AsdexMutations { get; } = [];

    public List<RecordedSaidMutation> SaidMutations { get; } = [];

    public List<string> SpawnedCallsigns { get; } = [];

    public List<string> DeletedCallsigns { get; } = [];

    public List<string> HiddenLiveTraffic { get; } = [];

    public List<string> OverlaysRemoved { get; } = [];

    public List<(string ConnectionId, string Callsign, List<string> Lines)> ShownQueues { get; } = [];

    public void OnConsolidationChanged() => ConsolidationChanges++;

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

    public List<RecordedAsdexSafetyLogicChange> AsdexSafetyLogicChanges { get; } = [];

    public void ApplyRecordedAsdexSafetyLogic(RecordedAsdexSafetyLogicChange change) => AsdexSafetyLogicChanges.Add(change);

    public void OnAircraftSpawned(AircraftState aircraft) => SpawnedCallsigns.Add(aircraft.Callsign);

    public void OnAircraftDeleted(string callsign, AircraftState? lastState) => DeletedCallsigns.Add(callsign);

    public void OnLiveTrafficHidden(string callsign) => HiddenLiveTraffic.Add(callsign);

    public List<(string ConnectionId, TrackOwner Owner, string TcpCode)> SelectedPositions { get; } = [];

    public void OnPositionSelected(string connectionId, TrackOwner owner, string tcpCode) => SelectedPositions.Add((connectionId, owner, tcpCode));

    public void OnGhostOverlayRemoved(string callsign) => OverlaysRemoved.Add(callsign);

    public void OnAsdexTrackTerminated(string callsign) { }

    public void OnQueuedCommandsShown(string connectionId, string callsign, IReadOnlyList<string> lines) =>
        ShownQueues.Add((connectionId, callsign, lines.ToList()));

    /// <summary>Every TDLS change set the router or the spine drained into this host, in order.</summary>
    public List<TdlsChangeSet> TdlsChanges { get; } = [];

    public void OnTdlsChanged(TdlsChangeSet changes) => TdlsChanges.Add(changes);

    /// <summary>Every strip change set the router or the spine drained into this host, in order.</summary>
    public List<StripChangeSet> StripChanges { get; } = [];

    public void OnStripsChanged(StripChangeSet changes) => StripChanges.Add(changes);

    /// <summary>How many times a drain reported the coordination lists changed.</summary>
    public int CoordinationChanges { get; private set; }

    public void OnCoordinationChanged() => CoordinationChanges++;

    public void OnTimersChanged() { }

    public void OnHeldDeparturesChanged() { }

    public void OnWeatherChanged() => WeatherChanges++;
}
