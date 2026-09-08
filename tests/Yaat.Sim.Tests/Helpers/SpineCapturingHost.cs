using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Simulation.Spine;
using Yaat.Sim.Simulation.Strips;
using Yaat.Sim.Simulation.Tdls;
using Yaat.Sim.Training;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// A whole-spine host for a bare engine: every step, slot and consumer is the engine's own bare host, so
/// <see cref="SimulationEngine.RunSecond"/> under this behaves exactly as <see cref="SimulationEngine.TickOneSecond"/>
/// does — except that the state changes the post-physics drain step hands over are recorded here. That is what it
/// is for: the action router drains into the host too, so only a second driven with this host can show that the
/// <see cref="StepId.StateChanges"/> entry delivers what the tick steps produced. The strip change sets and the
/// coordination notifications are recorded the same way.
/// </summary>
public sealed class SpineCapturingHost : ISimulationHost
{
    private readonly ISimulationHost _bare;

    public SpineCapturingHost(SimulationEngine engine)
    {
        _bare = engine.BareHost;
    }

    /// <summary>Every TDLS change set the spine's drain step handed over, in order.</summary>
    public List<TdlsChangeSet> TdlsChanges { get; } = [];

    /// <summary>Every strip change set the spine's drain step handed over, in order.</summary>
    public List<StripChangeSet> StripChanges { get; } = [];

    /// <summary>How many times a drain — the router's or the spine's — reported the coordination lists changed.</summary>
    public int CoordinationChangeCount { get; private set; }

    public void OnCoordinationChanged()
    {
        CoordinationChangeCount++;
        _bare.OnCoordinationChanged();
    }

    public void OnTdlsChanged(TdlsChangeSet changes)
    {
        TdlsChanges.Add(changes);
        _bare.OnTdlsChanged(changes);
    }

    public void OnStripsChanged(StripChangeSet changes)
    {
        StripChanges.Add(changes);
        _bare.OnStripsChanged(changes);
    }

    // --- IHostSteps ---

    public void ApplyPreTickRecordedActions(int second) => _bare.ApplyPreTickRecordedActions(second);

    public void LiveTrafficSync() => _bare.LiveTrafficSync();

    public void AsdexAlerts() => _bare.AsdexAlerts();

    public void SurfaceCoastExpiry() => _bare.SurfaceCoastExpiry();

    public void RundownBroadcast() => _bare.RundownBroadcast();

    public void LiveTrafficStatusBroadcast() => _bare.LiveTrafficStatusBroadcast();

    public void TimersBroadcast() => _bare.TimersBroadcast();

    public void IssueMetars() => _bare.IssueMetars();

    public void ApplyRecordedActions() => _bare.ApplyRecordedActions();

    // --- IHostConsumers ---

    public void OnPrePhysics(TickPrePhysicsResult result) => _bare.OnPrePhysics(result);

    public void OnTerminalEntries(List<TerminalEntry> entries) => _bare.OnTerminalEntries(entries);

    public void OnConflictAlerts(ConflictAlertChanges changes) => _bare.OnConflictAlerts(changes);

    public void OnEramConflictAlerts(EramConflictAlertChanges changes) => _bare.OnEramConflictAlerts(changes);

    public void OnSoloTrainingEvents(IReadOnlyList<SoloTrainingEvent> events) => _bare.OnSoloTrainingEvents(events);

    public void OnAutoDeleted(IReadOnlyList<AircraftState> removed) => _bare.OnAutoDeleted(removed);

    public void OnWeatherAdvanced(WeatherProfile profile) => _bare.OnWeatherAdvanced(profile);

    public void OnWarnings(List<(string Callsign, string Warning)> warnings) => _bare.OnWarnings(warnings);

    public void OnNotifications(List<(string Callsign, string Notification)> notifications) => _bare.OnNotifications(notifications);

    public void OnPilotSpeech(List<(string Callsign, string PilotSpeech)> speech) => _bare.OnPilotSpeech(speech);

    public void OnPilotReadbacks(List<(string Callsign, string Readback)> readbacks) => _bare.OnPilotReadbacks(readbacks);

    public void OnPilotTransmissions(List<PilotTransmission> transmissions) => _bare.OnPilotTransmissions(transmissions);

    public void OnApproachScores(List<ApproachScore> scores) => _bare.OnApproachScores(scores);

    // --- IActionHost ---

    public CommandResult ApplyAsdexEnableAllAlerts() => _bare.ApplyAsdexEnableAllAlerts();

    public CommandResult ApplyBookmark(BookmarkCommand command, string initials) => _bare.ApplyBookmark(command, initials);

    public CommandResult ApplyTransport(ParsedCommand command) => _bare.ApplyTransport(command);

    public void ApplyRecordedAsdexMutation(RecordedAsdexMutation mutation) => _bare.ApplyRecordedAsdexMutation(mutation);

    public void ApplyRecordedSaidMutation(RecordedSaidMutation mutation) => _bare.ApplyRecordedSaidMutation(mutation);

    public void ApplyRecordedEramCrrGroup(RecordedEramCrrGroup group) => _bare.ApplyRecordedEramCrrGroup(group);

    public void ApplyRecordedAsdexSafetyLogic(RecordedAsdexSafetyLogicChange change) => _bare.ApplyRecordedAsdexSafetyLogic(change);

    public void OnAircraftSpawned(AircraftState aircraft) => _bare.OnAircraftSpawned(aircraft);

    public void OnAircraftDeleted(string callsign, AircraftState? lastState) => _bare.OnAircraftDeleted(callsign, lastState);

    public void OnLiveTrafficHidden(string callsign) => _bare.OnLiveTrafficHidden(callsign);

    public void OnPositionSelected(string connectionId, TrackOwner owner, string tcpCode) => _bare.OnPositionSelected(connectionId, owner, tcpCode);

    public void OnGhostOverlayRemoved(string callsign) => _bare.OnGhostOverlayRemoved(callsign);

    public void OnAsdexTrackTerminated(string callsign) => _bare.OnAsdexTrackTerminated(callsign);

    public void OnTimersChanged() => _bare.OnTimersChanged();

    public void OnConsolidationChanged() => _bare.OnConsolidationChanged();

    public void OnHeldDeparturesChanged() => _bare.OnHeldDeparturesChanged();

    public void OnWeatherChanged() => _bare.OnWeatherChanged();

    public void OnQueuedCommandsShown(string connectionId, string callsign, IReadOnlyList<string> lines) =>
        _bare.OnQueuedCommandsShown(connectionId, callsign, lines);
}
