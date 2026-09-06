using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Simulation.Spine;
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

    public void DelayedHandoffs() => _bare.DelayedHandoffs();

    public void LiveTrafficSync() => _bare.LiveTrafficSync();

    public void AutoAccept() => _bare.AutoAccept();

    public void PointoutAutoAck() => _bare.PointoutAutoAck();

    public void FlightPlanCreatorAutoTrack() => _bare.FlightPlanCreatorAutoTrack();

    public void DeferredAutoTrack() => _bare.DeferredAutoTrack();

    public void CoordinationTimers() => _bare.CoordinationTimers();

    public void TowerLists() => _bare.TowerLists();

    public void AsdexAlerts() => _bare.AsdexAlerts();

    public void AutoArrivalStrips() => _bare.AutoArrivalStrips();

    public void AutoApproachDepartureStrips() => _bare.AutoApproachDepartureStrips();

    public void AutoTdlsQueue() => _bare.AutoTdlsQueue();

    public void TdlsAutoWilco() => _bare.TdlsAutoWilco();

    public void TdlsExpiry() => _bare.TdlsExpiry();

    public void TdlsTrackRemoval() => _bare.TdlsTrackRemoval();

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

    public void OnStripDispatches(List<(string Callsign, ParsedCommand Command)> dispatches) => _bare.OnStripDispatches(dispatches);

    // --- IActionHost: a replay has no room, so every slot is the bare host's refusal, its attendance answer and its no-op consumers ---

    public CommandResult ApplyStrip(string callsign, ParsedCommand command, TrackOwner? identity) => _bare.ApplyStrip(callsign, command, identity);

    public CommandResult ApplyTdls(AircraftState aircraft, ParsedCommand command) => _bare.ApplyTdls(aircraft, command);

    public CommandResult ApplyTdlsOpsConfig(TdlsOpsConfigCommand command) => _bare.ApplyTdlsOpsConfig(command);

    public CommandResult ApplyCoordination(AircraftState aircraft, ParsedCommand command, TrackOwner? identity) =>
        _bare.ApplyCoordination(aircraft, command, identity);

    public CommandResult ApplyGlobalCoordination(CoordinationAutoAckCommand command, TrackOwner? identity) =>
        _bare.ApplyGlobalCoordination(command, identity);

    public CommandResult ApplyAsdexEnableAllAlerts() => _bare.ApplyAsdexEnableAllAlerts();

    public CommandResult ApplyBookmark(BookmarkCommand command, string initials) => _bare.ApplyBookmark(command, initials);

    public CommandResult ApplyTransport(ParsedCommand command) => _bare.ApplyTransport(command);

    public void ApplyRecordedAsdexMutation(RecordedAsdexMutation mutation) => _bare.ApplyRecordedAsdexMutation(mutation);

    public void ApplyRecordedSaidMutation(RecordedSaidMutation mutation) => _bare.ApplyRecordedSaidMutation(mutation);

    public bool IsPositionAttended(Tcp tcp) => _bare.IsPositionAttended(tcp);

    public void OnAircraftSpawned(AircraftState aircraft) => _bare.OnAircraftSpawned(aircraft);

    public void OnAircraftDeleted(string callsign, AircraftState? lastState) => _bare.OnAircraftDeleted(callsign, lastState);

    public void OnLiveTrafficHidden(string callsign) => _bare.OnLiveTrafficHidden(callsign);

    public void OnPositionSelected(string connectionId, TrackOwner owner, string tcpCode) => _bare.OnPositionSelected(connectionId, owner, tcpCode);

    public void OnTrackAcquired(string callsign) => _bare.OnTrackAcquired(callsign);

    public void OnGhostOverlayRemoved(string callsign) => _bare.OnGhostOverlayRemoved(callsign);

    public void OnAsdexTrackTerminated(string callsign) => _bare.OnAsdexTrackTerminated(callsign);

    public void OnTimersChanged() => _bare.OnTimersChanged();

    public void OnConsolidationChanged() => _bare.OnConsolidationChanged();

    public void OnHeldDeparturesChanged() => _bare.OnHeldDeparturesChanged();

    public void OnFlightPlanAmended(string callsign) => _bare.OnFlightPlanAmended(callsign);

    public void OnWeatherChanged() => _bare.OnWeatherChanged();

    public void OnQueuedCommandsShown(string connectionId, string callsign, IReadOnlyList<string> lines) =>
        _bare.OnQueuedCommandsShown(connectionId, callsign, lines);
}
