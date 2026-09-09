using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Simulation.Spine;
using Yaat.Sim.Simulation.Strips;
using Yaat.Sim.Simulation.Tdls;
using Yaat.Sim.Training;

namespace Yaat.Sim.Simulation.Replay;

/// <summary>
/// The host of a <see cref="RunKind.Replay"/> run. It differs from the bare host in exactly two steps: the recorded
/// pre-tick actions (spawns, live-traffic samples) land after the clock increment and before physics, and the
/// remaining recorded actions at or before the completed second are applied after it. Everything else — every other
/// spine step, every consumer, every action-host slot — delegates to the bare host, so a replay produces the same
/// events a test tick does. The log is walked by a <see cref="RecordedActionPump"/> (one per traversal — the driver
/// keeps one for its stepping entry points, a range replay builds its own, so the two never interfere); recorded
/// actions go through <see cref="ActionRouter.ApplyRecorded(RecordedAction, IActionHost)"/> with this host unless the
/// caller supplies its own applier.
/// </summary>
internal sealed class ReplayHost : ISimulationHost
{
    private readonly SimulationEngine _engine;
    private readonly RecordedActionPump _pump;
    private readonly Action<RecordedAction> _applier;
    private readonly BareHost _bare;

    public ReplayHost(SimulationEngine engine, RecordedActionPump pump, Action<RecordedAction>? applier)
    {
        _engine = engine;
        _pump = pump;
        _bare = engine.BareHost;
        _applier = applier ?? (action => _engine.Actions.ApplyRecorded(action, this));
    }

    public RecordedActionPump Pump => _pump;

    public void ApplyPreTickRecordedActions(int second) => _pump.ApplyPreTick(second, _applier);

    public void ApplyRecordedActions()
    {
        if (_engine.Scenario is not { } scenario)
        {
            return;
        }

        ApplyRecordedActionsThrough((int)scenario.ElapsedSeconds);
    }

    /// <summary>Applies every action at or before <paramref name="second"/> the pre-tick pass did not, advancing the cursor past them.</summary>
    public void ApplyRecordedActionsThrough(int second) => _pump.ApplyThrough(second, _applier);

    public void LiveTrafficSync() => _bare.LiveTrafficSync();

    public void AsdexAlerts() => _bare.AsdexAlerts();

    public void SurfaceCoastExpiry() => _bare.SurfaceCoastExpiry();

    public void RundownBroadcast() => _bare.RundownBroadcast();

    public void LiveTrafficStatusBroadcast() => _bare.LiveTrafficStatusBroadcast();

    public void TimersBroadcast() => _bare.TimersBroadcast();

    public void IssueMetars() => _bare.IssueMetars();

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

    // --- IActionHost: a replay has no room, so every slot is the bare host's refusal and every consumer its no-op ---

    public void ApplyRecordedAsdexSafetyLogic(RecordedAsdexSafetyLogicChange change) => _bare.ApplyRecordedAsdexSafetyLogic(change);

    public void OnAircraftSpawned(AircraftState aircraft) => _bare.OnAircraftSpawned(aircraft);

    public void OnAircraftDeleted(string callsign, AircraftState? lastState) => _bare.OnAircraftDeleted(callsign, lastState);

    public void OnLiveTrafficHidden(string callsign) => _bare.OnLiveTrafficHidden(callsign);

    public void OnPositionSelected(string connectionId, TrackOwner owner, string tcpCode) => _bare.OnPositionSelected(connectionId, owner, tcpCode);

    public void OnGhostOverlayRemoved(string callsign) => _bare.OnGhostOverlayRemoved(callsign);

    public void OnAsdexTrackTerminated(string callsign) => _bare.OnAsdexTrackTerminated(callsign);

    public void OnSaidTrackTerminated(string callsign) => _bare.OnSaidTrackTerminated(callsign);

    public void OnStripsChanged(StripChangeSet changes) => _bare.OnStripsChanged(changes);

    public void OnTdlsChanged(TdlsChangeSet changes) => _bare.OnTdlsChanged(changes);

    public void OnCoordinationChanged() => _bare.OnCoordinationChanged();

    public void OnBookmarksChanged() => _bare.OnBookmarksChanged();

    public void OnSimStateChanged() => _bare.OnSimStateChanged();

    public void OnEramCrrGroupsChanged() => _bare.OnEramCrrGroupsChanged();

    public void OnTimersChanged() => _bare.OnTimersChanged();

    public void OnConsolidationChanged() => _bare.OnConsolidationChanged();

    public void OnHeldDeparturesChanged() => _bare.OnHeldDeparturesChanged();

    public void OnWeatherChanged() => _bare.OnWeatherChanged();

    public void OnQueuedCommandsShown(string connectionId, string callsign, IReadOnlyList<string> lines) =>
        _bare.OnQueuedCommandsShown(connectionId, callsign, lines);
}
