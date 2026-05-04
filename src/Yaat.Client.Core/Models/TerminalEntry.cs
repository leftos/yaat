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
}

public sealed class TerminalEntry
{
    public required DateTime Timestamp { get; init; }
    public required string Initials { get; init; }
    public required TerminalEntryKind Kind { get; init; }
    public required string Callsign { get; init; }
    public required string Message { get; init; }
}
