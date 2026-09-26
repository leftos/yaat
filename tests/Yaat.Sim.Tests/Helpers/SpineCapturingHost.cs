using Yaat.Sim.Asdex;
using Yaat.Sim.Commands;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Phases;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Simulation.Coast;
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
public sealed class SpineCapturingHost(ISimulationHost inner) : ISimulationHost
{
    public SpineCapturingHost(SimulationEngine engine)
        : this(engine.BareHost) { }

    /// <summary>
    /// The host every step, slot and consumer is forwarded to after it is recorded: the engine's bare host, or — for
    /// a test that captures what a replayed run hands over — a replay host, whose slots apply the recording.
    /// </summary>
    private readonly ISimulationHost _inner = inner;

    /// <summary>Every TDLS change set the spine's drain step handed over, in order.</summary>
    public List<TdlsChangeSet> TdlsChanges { get; } = [];

    /// <summary>Every strip change set the spine's drain step handed over, in order.</summary>
    public List<StripChangeSet> StripChanges { get; } = [];

    /// <summary>How many times a drain — the router's or the spine's — reported the coordination lists changed.</summary>
    public int CoordinationChangeCount { get; private set; }

    public void OnCoordinationChanged()
    {
        CoordinationChangeCount++;
        _inner.OnCoordinationChanged();
    }

    /// <summary>How many times a drain — the router's or the spine's — reported the timeline bookmarks changed.</summary>
    public int BookmarkChangeCount { get; private set; }

    public void OnBookmarksChanged()
    {
        BookmarkChangeCount++;
        _inner.OnBookmarksChanged();
    }

    /// <summary>How many times a drain — the router's or the spine's — reported the session clock changed.</summary>
    public int SimStateChangeCount { get; private set; }

    public void OnSimStateChanged()
    {
        SimStateChangeCount++;
        _inner.OnSimStateChanged();
    }

    /// <summary>How many times a drain — the router's or the spine's — reported the ERAM CRR groups changed.</summary>
    public int EramCrrGroupChangeCount { get; private set; }

    public void OnEramCrrGroupsChanged()
    {
        EramCrrGroupChangeCount++;
        _inner.OnEramCrrGroupsChanged();
    }

    /// <summary>Every ASDE-X Safety Logic diff the post-physics detector step handed over, in order.</summary>
    public List<(IReadOnlyList<AsdexSafetyAlert> NewAlerts, IReadOnlyList<string> ClearedAlertIds)> AsdexAlertChanges { get; } = [];

    public void OnAsdexAlertsChanged(IReadOnlyList<AsdexSafetyAlert> newAlerts, IReadOnlyList<string> clearedAlertIds)
    {
        AsdexAlertChanges.Add((newAlerts, clearedAlertIds));
        _inner.OnAsdexAlertsChanged(newAlerts, clearedAlertIds);
    }

    /// <summary>Every batch of disconnect-coast facets the post-physics expiry step retired, in order.</summary>
    public List<IReadOnlyList<ExpiredDisconnectCoastFacet>> DisconnectCoastExpiries { get; } = [];

    public void OnDisconnectCoastExpired(IReadOnlyList<ExpiredDisconnectCoastFacet> expired)
    {
        DisconnectCoastExpiries.Add(expired);
        _inner.OnDisconnectCoastExpired(expired);
    }

    /// <summary>Every batch of callsigns whose disconnect coast a spawn cleared, as a drain handed them over, in order.</summary>
    public List<IReadOnlyList<string>> DisconnectCoastClears { get; } = [];

    public void OnDisconnectCoastsCleared(IReadOnlyList<string> callsigns)
    {
        DisconnectCoastClears.Add(callsigns);
        _inner.OnDisconnectCoastsCleared(callsigns);
    }

    public void OnTdlsChanged(TdlsChangeSet changes)
    {
        TdlsChanges.Add(changes);
        _inner.OnTdlsChanged(changes);
    }

    public void OnStripsChanged(StripChangeSet changes)
    {
        StripChanges.Add(changes);
        _inner.OnStripsChanged(changes);
    }

    // --- IHostSteps ---

    public void ApplyPreTickRecordedActions(int second) => _inner.ApplyPreTickRecordedActions(second);

    public void SurfaceCoastExpiry() => _inner.SurfaceCoastExpiry();

    public void RundownBroadcast() => _inner.RundownBroadcast();

    public void LiveTrafficStatusBroadcast() => _inner.LiveTrafficStatusBroadcast();

    public void TimersBroadcast() => _inner.TimersBroadcast();

    public void IssueMetars() => _inner.IssueMetars();

    public void ApplyRecordedActions() => _inner.ApplyRecordedActions();

    // --- IHostConsumers ---

    public void OnPrePhysics(TickPrePhysicsResult result) => _inner.OnPrePhysics(result);

    public void OnTerminalEntries(List<TerminalEntry> entries) => _inner.OnTerminalEntries(entries);

    public void OnConflictAlerts(ConflictAlertChanges changes) => _inner.OnConflictAlerts(changes);

    public void OnEramConflictAlerts(EramConflictAlertChanges changes) => _inner.OnEramConflictAlerts(changes);

    public void OnSoloTrainingEvents(IReadOnlyList<SoloTrainingEvent> events) => _inner.OnSoloTrainingEvents(events);

    public void OnAutoDeleted(IReadOnlyList<AircraftState> removed) => _inner.OnAutoDeleted(removed);

    public void OnWeatherAdvanced(WeatherProfile profile) => _inner.OnWeatherAdvanced(profile);

    public void OnWarnings(List<(string Callsign, string Warning)> warnings) => _inner.OnWarnings(warnings);

    public void OnNotifications(List<(string Callsign, string Notification)> notifications) => _inner.OnNotifications(notifications);

    public void OnPilotSpeech(List<(string Callsign, string PilotSpeech)> speech) => _inner.OnPilotSpeech(speech);

    public void OnPilotReadbacks(List<(string Callsign, string Readback)> readbacks) => _inner.OnPilotReadbacks(readbacks);

    public void OnPilotTransmissions(List<PilotTransmission> transmissions) => _inner.OnPilotTransmissions(transmissions);

    public void OnApproachScores(List<ApproachScore> scores) => _inner.OnApproachScores(scores);

    public ILiveTrafficFeedPort LiveTrafficFeed => _inner.LiveTrafficFeed;

    /// <summary>Every shadow the live-traffic sync spawned, with the source of its first track, in order.</summary>
    public List<(AircraftState Shadow, LiveTrafficSource Source)> LiveTrafficSpawns { get; } = [];

    /// <summary>Every shadow the live-traffic sync removed, with the reason, in order.</summary>
    public List<(AircraftState Shadow, LiveTrafficRemovalReason Reason)> LiveTrafficRemovals { get; } = [];

    /// <summary>Every callsign the live-traffic sync reported as held by a simulated aircraft, in order.</summary>
    public List<string> LiveTrafficCallsignsInUse { get; } = [];

    /// <summary>Every filter sweep's count the live-traffic sync reported, in order.</summary>
    public List<int> LiveTrafficFilteredOut { get; } = [];

    public void OnLiveTrafficSpawned(AircraftState shadow, LiveTrafficSource source)
    {
        LiveTrafficSpawns.Add((shadow, source));
        _inner.OnLiveTrafficSpawned(shadow, source);
    }

    public void OnLiveTrafficRemoved(AircraftState shadow, LiveTrafficRemovalReason reason)
    {
        LiveTrafficRemovals.Add((shadow, reason));
        _inner.OnLiveTrafficRemoved(shadow, reason);
    }

    public void OnLiveTrafficCallsignInUse(string callsign)
    {
        LiveTrafficCallsignsInUse.Add(callsign);
        _inner.OnLiveTrafficCallsignInUse(callsign);
    }

    public void OnLiveTrafficFilteredOut(int count)
    {
        LiveTrafficFilteredOut.Add(count);
        _inner.OnLiveTrafficFilteredOut(count);
    }

    /// <summary>Every re-acquire count the live-traffic sync reported, in order.</summary>
    public List<int> LiveTrafficReacquired { get; } = [];

    public void OnLiveTrafficReacquired(int shadows)
    {
        LiveTrafficReacquired.Add(shadows);
        _inner.OnLiveTrafficReacquired(shadows);
    }

    // --- IActionHost ---

    public void OnAircraftSpawned(AircraftState aircraft) => _inner.OnAircraftSpawned(aircraft);

    public void OnAircraftDeleted(string callsign, AircraftState? lastState) => _inner.OnAircraftDeleted(callsign, lastState);

    public void OnPositionSelected(string connectionId, TrackOwner owner, string tcpCode) => _inner.OnPositionSelected(connectionId, owner, tcpCode);

    public void OnGhostOverlayRemoved(string callsign) => _inner.OnGhostOverlayRemoved(callsign);

    public void OnAsdexTrackTerminated(string callsign) => _inner.OnAsdexTrackTerminated(callsign);

    public void OnSaidTrackTerminated(string callsign) => _inner.OnSaidTrackTerminated(callsign);

    public void OnTimersChanged() => _inner.OnTimersChanged();

    public void OnConsolidationChanged() => _inner.OnConsolidationChanged();

    public void OnHeldDeparturesChanged() => _inner.OnHeldDeparturesChanged();

    public void OnWeatherChanged() => _inner.OnWeatherChanged();

    public void OnQueuedCommandsShown(string connectionId, string callsign, IReadOnlyList<string> lines) =>
        _inner.OnQueuedCommandsShown(connectionId, callsign, lines);
}
