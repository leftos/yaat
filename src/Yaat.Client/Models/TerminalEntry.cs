namespace Yaat.Client.Models;

public enum TerminalEntryKind { Command, Response, System, Say }

public sealed class TerminalEntry
{
    public required DateTime Timestamp { get; init; }
    public required string Initials { get; init; }
    public required TerminalEntryKind Kind { get; init; }
    public required string Callsign { get; init; }
    public required string Message { get; init; }
}
