using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Simulation.Strips;
using Yaat.Sim.Simulation.Tdls;
using Yaat.Sim.Training;

namespace Yaat.Sim.Simulation.Spine;

/// <summary>
/// The host of a bare engine — a <see cref="RunKind.Test"/> run stepped through <see cref="SimulationEngine.TickOneSecond"/>.
/// Every host step is empty: there is no room, no feed, no controller to broadcast to. The consumers fire the
/// engine's events (<see cref="SimulationEngine.WarningEmitted"/>, <see cref="SimulationEngine.TerminalEntryEmitted"/>,
/// <see cref="SimulationEngine.PilotSpeechEmitted"/>) so tests
/// and the solo client observe the same lines the RPO would see. As an <see cref="IActionHost"/> it refuses every slot
/// (the bodies are the live server's) and discards every consumer. The replay host delegates everything it does not
/// override here.
/// </summary>
internal sealed class BareHost(SimulationEngine engine) : ISimulationHost
{
    private readonly SimulationEngine _engine = engine;

    public void ApplyPreTickRecordedActions(int second) { }

    public void LiveTrafficSync() { }

    public void TowerLists() { }

    public void AsdexAlerts() { }

    public void SurfaceCoastExpiry() { }

    public void RundownBroadcast() { }

    public void LiveTrafficStatusBroadcast() { }

    public void TimersBroadcast() { }

    public void IssueMetars() { }

    public void ApplyRecordedActions() { }

    public void OnPrePhysics(TickPrePhysicsResult result) { }

    /// <summary>Discarded: a bare engine has no terminal. <see cref="SimulationEngine.TerminalEntryEmitted"/> already fired on add.</summary>
    public void OnTerminalEntries(List<TerminalEntry> entries) { }

    public void OnConflictAlerts(ConflictAlertChanges changes) { }

    public void OnEramConflictAlerts(EramConflictAlertChanges changes) { }

    /// <summary>Discarded: there is no controller to notify. The evaluator's own record of the findings is engine state.</summary>
    public void OnSoloTrainingEvents(IReadOnlyList<SoloTrainingEvent> events) { }

    /// <summary>Nothing to tear down: a bare engine has no room state keyed by callsign and nobody to broadcast to.</summary>
    public void OnAutoDeleted(IReadOnlyList<AircraftState> removed) { }

    /// <summary>Nothing to mirror: the world already holds the profile.</summary>
    public void OnWeatherAdvanced(WeatherProfile profile) { }

    public void OnWarnings(List<(string Callsign, string Warning)> warnings)
    {
        foreach (var (callsign, warning) in warnings)
        {
            _engine.FireWarningEmitted(callsign, warning);
        }
    }

    public void OnNotifications(List<(string Callsign, string Notification)> notifications)
    {
        foreach (var (callsign, notification) in notifications)
        {
            _engine.EmitTerminal("Response", callsign, notification);
        }
    }

    public void OnPilotSpeech(List<(string Callsign, string PilotSpeech)> speech)
    {
        foreach (var (callsign, line) in speech)
        {
            _engine.EmitTerminal("PilotSpeech", callsign, line);
            _engine.FirePilotSpeechEmitted(callsign, line);
        }
    }

    public void OnPilotReadbacks(List<(string Callsign, string Readback)> readbacks)
    {
        foreach (var (callsign, readback) in readbacks)
        {
            _engine.EmitTerminal("SayReadback", callsign, readback);
        }
    }

    public void OnPilotTransmissions(List<PilotTransmission> transmissions)
    {
        foreach (var transmission in transmissions)
        {
            _engine.EmitTerminal(SimulationEngine.ToSayKind(transmission), transmission.Callsign, transmission.Text);
        }
    }

    /// <summary>Discarded: the scores are consumed by the approach evaluator only where a controller can be debriefed.</summary>
    public void OnApproachScores(List<ApproachScore> scores) { }

    // --- IActionHost: no room, so every slot is refused and every consumer is a no-op ---

    public CommandResult ApplyAsdexEnableAllAlerts() => ActionRefusals.HostOnly("ASDXALERTS");

    public CommandResult ApplyBookmark(BookmarkCommand command, string initials) => ActionRefusals.HostOnly(command);

    public CommandResult ApplyTransport(ParsedCommand command) => ActionRefusals.HostOnly(command);

    public void ApplyRecordedAsdexMutation(RecordedAsdexMutation mutation) { }

    public void ApplyRecordedSaidMutation(RecordedSaidMutation mutation) { }

    public void ApplyRecordedEramCrrGroup(RecordedEramCrrGroup group) { }

    public void ApplyRecordedAsdexSafetyLogic(RecordedAsdexSafetyLogicChange change) { }

    public void OnAircraftSpawned(AircraftState aircraft) { }

    public void OnAircraftDeleted(string callsign, AircraftState? lastState) { }

    public void OnLiveTrafficHidden(string callsign) { }

    public void OnPositionSelected(string connectionId, TrackOwner owner, string tcpCode) { }

    public void OnGhostOverlayRemoved(string callsign) { }

    public void OnAsdexTrackTerminated(string callsign) { }

    /// <summary>Discarded: a bare engine has no vStrips client to push items to. The mutations themselves are engine state.</summary>
    public void OnStripsChanged(StripChangeSet changes) { }

    /// <summary>Discarded: a bare engine has no vTDLS client to push items to. The mutations themselves are engine state.</summary>
    public void OnTdlsChanged(TdlsChangeSet changes) { }

    /// <summary>Discarded: a bare engine has no CRC client to re-push the coordination topic to. The lists are engine state.</summary>
    public void OnCoordinationChanged() { }

    public void OnTimersChanged() { }

    public void OnConsolidationChanged() { }

    public void OnHeldDeparturesChanged() { }

    public void OnWeatherChanged() { }

    public void OnQueuedCommandsShown(string connectionId, string callsign, IReadOnlyList<string> lines) { }
}
