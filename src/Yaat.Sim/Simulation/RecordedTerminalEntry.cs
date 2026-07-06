namespace Yaat.Sim.Simulation;

/// <summary>
/// A single broadcast terminal-panel line (command echo, response, SAY, warning, chat, …) captured
/// with both its wall-clock timestamp and the scenario-elapsed second it occurred. Persisted in the
/// recording archive (<c>terminal-log.json.br</c>) so a loaded recording repopulates the full terminal
/// and each line can scrub the replay to the moment it happened. This is a display log, not a
/// <see cref="RecordedAction"/> — it has no simulation-state effect and is never replayed.
/// </summary>
public sealed record RecordedTerminalEntry(double ElapsedSeconds, DateTime Timestamp, string Initials, string Kind, string Callsign, string Message);
