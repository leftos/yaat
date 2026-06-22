namespace Yaat.Sim.Pilot;

/// <summary>
/// Pair of forms produced by a pilot-speech builder: the terminal-readable form (controller's
/// view, with identifiers and digits, callsign carried by the SAY column) and the TTS spoken form
/// (NATO-spelled identifiers, digit-by-digit numbers, AIM-compliant phraseology). Each builder
/// produces both forms independently from the canonical command — terminal text is NEVER derived
/// by regex-stripping the TTS string.
/// </summary>
public sealed record PilotSpeechText(string Terminal, string Tts)
{
    /// <summary>
    /// Terminal text for the RPO/instructor channel when it must carry a diagnostic the solo
    /// student must not see — chiefly the lead/target callsign in follow transmissions. A real
    /// controller (and therefore a solo student) identifies that traffic by position, never by
    /// callsign, so <see cref="Terminal"/> stays callsign-free for the solo view while the RPO
    /// gets the extra identifier as an aid. Null when the two views are identical;
    /// <see cref="TerminalForRpo"/> resolves the fallback.
    /// </summary>
    public string? RpoTerminal { get; init; }

    /// <summary>Terminal text for the RPO channel: <see cref="RpoTerminal"/> if set, else <see cref="Terminal"/>.</summary>
    public string TerminalForRpo => RpoTerminal ?? Terminal;
}
