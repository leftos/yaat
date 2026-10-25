namespace Yaat.Client.ViewModels;

/// <summary>A command the command box sent to the server: the callsign, the canonical text, and the server's verdict and message.</summary>
public sealed record CommandSubmission(string Callsign, string Canonical, bool Success, string? Message);

/// <summary>
/// What one command-box submission did: the command it sent to the server (null when nothing reached the server), the status line
/// after it, the warnings it added to the terminal, and why it was refused before anything ran (null when it ran).
/// </summary>
public sealed record CommandBoxOutcome(CommandSubmission? Submission, string StatusText, IReadOnlyList<string> Warnings, string? Refused);
