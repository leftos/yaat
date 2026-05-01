namespace Yaat.Sim.Simulation;

/// <summary>
/// Terminal message emitted during tick processing (presets, spawns, triggers, generators)
/// or during command dispatch (pilot transmissions from SAY-class verbs). Drained by the
/// server for broadcasting; discarded by client's convenience wrapper.
/// </summary>
public record TerminalEntry(string Kind, string Callsign, string Message);
