using Yaat.Sim.Commands;
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
public interface IHostConsumers
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
    void OnStripDispatches(List<(string Callsign, ParsedCommand Command)> dispatches);

    /// <summary>The profile <see cref="SimulationEngine.AdvanceWeatherTimeline"/> just installed; not called when the scenario has no timeline.</summary>
    void OnWeatherAdvanced(WeatherProfile profile);

    /// <summary>
    /// The aircraft <see cref="SimulationEngine.TickAutoDelete"/> removed this second — already gone from the world;
    /// each state still carries its last position for a surface-track coast or drop.
    /// </summary>
    void OnAutoDeleted(IReadOnlyList<AircraftState> removed);
}
