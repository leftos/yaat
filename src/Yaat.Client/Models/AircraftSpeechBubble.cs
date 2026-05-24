namespace Yaat.Client.Models;

/// <summary>
/// Transient per-aircraft overlay shown on Radar and Ground views when the user has opted into
/// speech bubbles (Settings → Display → Overlays) and a SAY-family verb (controller-issued) or
/// RPO pilot transmission arrives via <c>TerminalEntryKind.Say</c> / <c>TerminalEntryKind.PilotSpeech</c>.
/// The bubble carries the literal terminal-channel message text and an absolute UTC expiry; the
/// renderer drops bubbles whose <see cref="ExpiresAt"/> has passed. A subsequent message for the
/// same aircraft replaces the previous bubble (single slot, no queue).
/// </summary>
public sealed record AircraftSpeechBubble(string Text, DateTime ExpiresAt)
{
    /// <summary>
    /// Content-length-derived display duration. Floor: 4 s (short calls don't blink). Ceiling: 12 s
    /// (long position reports don't loiter forever). Linear between: ~12 chars/s reading speed
    /// plus a 2 s buffer so the eye has time to land on the bubble before it starts counting down.
    /// </summary>
    public static TimeSpan ComputeDuration(string text)
    {
        var length = text?.Length ?? 0;
        var seconds = Math.Clamp(2.0 + length / 12.0, 4.0, 12.0);
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Pure gating predicate: returns a fully-formed bubble when all opt-in conditions are met,
    /// otherwise null. Exposed so the gating logic can be exercised by unit tests without
    /// fabricating a full <c>MainViewModel</c> + preferences + aircraft collection. Callers
    /// remain responsible for locating the target aircraft and attaching the bubble.
    /// </summary>
    public static AircraftSpeechBubble? TryBuild(bool showSpeechBubbles, bool soloMode, TerminalEntryKind kind, string message, DateTime nowUtc)
    {
        if (!showSpeechBubbles || soloMode)
        {
            return null;
        }
        if (kind != TerminalEntryKind.Say && kind != TerminalEntryKind.PilotSpeech)
        {
            return null;
        }
        if (string.IsNullOrEmpty(message))
        {
            return null;
        }
        return new AircraftSpeechBubble(message, nowUtc + ComputeDuration(message));
    }
}
