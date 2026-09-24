using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Phases;
using Yaat.Sim.Pilot;
using Yaat.Sim.Training;

namespace Yaat.Sim.Simulation.Spine;

/// <summary>
/// The consumer view of a host: what a sim step hands over when it produces something the simulation itself does
/// not act on. A sim step in <see cref="SpineOrder"/> receives only this view, so it can deliver a result but never invoke
/// a host slot. Every drain the engine performs delivers here on every run kind; the bare test host turns them into
/// the engine's events, the live server into broadcasts.
/// </summary>
public interface IHostConsumers : IStateChangeConsumer
{
    /// <summary>The aircraft <see cref="SimulationEngine.TickPrePhysics"/> spawned this second.</summary>
    void OnPrePhysics(TickPrePhysicsResult result);

    /// <summary>The terminal entries accumulated since the last drain — command echoes, preset outcomes, spawn notes.</summary>
    void OnTerminalEntries(List<TerminalEntry> entries);

    void OnConflictAlerts(ConflictAlertChanges changes);
    void OnEramConflictAlerts(EramConflictAlertChanges changes);

    /// <summary>The findings <see cref="SimulationEngine.TickSoloTrainingEvaluation"/> raised this second; empty outside solo mode.</summary>
    void OnSoloTrainingEvents(IReadOnlyList<SoloTrainingEvent> events);
    void OnWarnings(List<(string Callsign, string Warning)> warnings);
    void OnNotifications(List<(string Callsign, string Notification)> notifications);
    void OnPilotSpeech(List<(string Callsign, string PilotSpeech)> speech);
    void OnPilotReadbacks(List<(string Callsign, string Readback)> readbacks);

    /// <summary>Pilot transmissions ready this second. Not called when nobody answers pilots — the engine discards them instead.</summary>
    void OnPilotTransmissions(List<PilotTransmission> transmissions);

    void OnApproachScores(List<ApproachScore> scores);

    /// <summary>The profile <see cref="SimulationEngine.AdvanceWeatherTimeline"/> just installed; not called when the scenario has no timeline.</summary>
    void OnWeatherAdvanced(WeatherProfile profile);

    /// <summary>
    /// The aircraft <see cref="SimulationEngine.TickAutoDelete"/> removed this second — already gone from the world;
    /// each state still carries its last position for a surface-track coast or drop.
    /// </summary>
    void OnAutoDeleted(IReadOnlyList<AircraftState> removed);

    /// <summary>
    /// The live-traffic feed <see cref="SimulationEngine.TickLiveTrafficSync"/> reads — the one input this view carries
    /// rather than receives. A host with no feed answers <see cref="EmptyLiveTrafficFeedPort.Instance"/>, so the step
    /// asks the port and never which run it is in (ADR 0005).
    /// </summary>
    ILiveTrafficFeedPort LiveTrafficFeed { get; }

    /// <summary>The live-traffic sync spawned <paramref name="shadow"/> (recorded, in the world) from a <paramref name="source"/> track.</summary>
    void OnLiveTrafficSpawned(AircraftState shadow, LiveTrafficSource source);

    /// <summary>
    /// The live-traffic sync removed <paramref name="shadow"/> — already out of the world with the removal recorded;
    /// the state is its last, for the room's per-callsign teardown.
    /// </summary>
    void OnLiveTrafficRemoved(AircraftState shadow, LiveTrafficRemovalReason reason);

    /// <summary>
    /// A feed track arrived under a callsign a (non-assumed) simulated aircraft holds; the feed is ignored for it. Called
    /// every second it recurs.
    /// </summary>
    void OnLiveTrafficCallsignInUse(string callsign);

    /// <summary>The room's live-traffic filter took <paramref name="count"/> shadows out this second (never called with zero).</summary>
    void OnLiveTrafficFilteredOut(int count);

    /// <summary>
    /// The room rejoined the feed after a gap and the sync removed <paramref name="shadows"/> shadows to re-acquire them
    /// this same second (never called with zero). The gap's length is wall-clock time, so the host's feed holds it.
    /// </summary>
    void OnLiveTrafficReacquired(int shadows);
}
