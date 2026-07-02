namespace Yaat.Client.Models;

/// <summary>
/// One up-arrow recall entry. <see cref="Command"/> is the callsign-less command text (so it
/// re-runs on the implicit target); <see cref="Callsign"/> records which aircraft the command
/// was originally sent to, so recall can be filtered to the selected aircraft. An empty
/// <see cref="Callsign"/> marks an untargeted/global command (PAUSE, ADD, global half-strip, …),
/// which surfaces in recall regardless of the current selection.
/// </summary>
public sealed record CommandHistoryEntry(string Callsign, string Command);
