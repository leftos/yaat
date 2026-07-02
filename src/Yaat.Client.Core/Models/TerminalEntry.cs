namespace Yaat.Client.Models;

public enum TerminalEntryKind
{
    Command,
    Response,
    System,
    Say,
    Warning,
    Error,
    Chat,

    /// <summary>
    /// Sim-initiated pilot transmission emitted in RPO mode when the user has enabled
    /// <c>RpoShowPilotSpeech</c>. Visually identical to <see cref="Say"/> (green) — the
    /// distinct kind exists so the audible-alert preference can fire only on
    /// sim-initiated speech, not on the controller's own AS-prefix SAY-class verbs.
    /// </summary>
    PilotSpeech,

    /// <summary>
    /// vTDLS PDC delivery. Mirrors the ACARS message format the pilot's client receives so
    /// instructors see exactly what the student sent. Separate from System so it can be
    /// colored, filtered, and (eventually) routed to a dedicated channel toggle.
    /// </summary>
    Tdls,

    /// <summary>
    /// Flight-strip command echo and its feedback (STRIP, HSC, SEP, BLANK, …). Routine strip
    /// traffic is high-volume, so it carries its own kind to get a dedicated channel toggle
    /// that hides both the command echo and the response in one click.
    /// </summary>
    Strip,
}

public sealed class TerminalEntry
{
    public required DateTime Timestamp { get; init; }
    public required string Initials { get; init; }
    public required TerminalEntryKind Kind { get; init; }
    public required string Callsign { get; init; }
    public required string Message { get; init; }
}
