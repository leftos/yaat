namespace Yaat.Sim.Pilot;

/// <summary>
/// Pair of forms produced by a pilot-speech builder: the terminal-readable form (controller's
/// view, with identifiers and digits) and the TTS spoken form (NATO-spelled identifiers,
/// digit-by-digit numbers, AIM-compliant phraseology). Each builder produces both forms
/// independently from the canonical command — terminal text is NEVER derived by regex-stripping
/// the TTS string. New builders should return this record; the legacy single-string builders
/// that rely on <c>CompactForTerminal</c> are migrating to this paradigm.
/// </summary>
public sealed record PilotSpeechText(string Terminal, string Tts);
