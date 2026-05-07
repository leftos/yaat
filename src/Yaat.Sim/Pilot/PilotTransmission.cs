namespace Yaat.Sim.Pilot;

/// <summary>
/// Typed metadata for solo-training pilot audio. Terminal queues remain the
/// visual source of truth; this side queue lets clients synthesize the same
/// text without reclassifying terminal entries.
/// </summary>
public sealed record PilotTransmission(string Callsign, string Text, string SpeechText, string SourceKind);
