namespace Yaat.Sim.Pilot;

/// <summary>
/// Pair of forms produced by a pilot-speech builder: the terminal-readable form (controller's
/// view, with identifiers and digits, callsign carried by the SAY column) and the TTS spoken form
/// (NATO-spelled identifiers, digit-by-digit numbers, AIM-compliant phraseology). Each builder
/// produces both forms independently from the canonical command — terminal text is NEVER derived
/// by regex-stripping the TTS string.
/// </summary>
public sealed record PilotSpeechText(string Terminal, string Tts);
