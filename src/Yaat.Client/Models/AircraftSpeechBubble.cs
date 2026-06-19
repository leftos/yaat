namespace Yaat.Client.Models;

/// <summary>
/// Visual style of a speech bubble. <see cref="Speech"/> covers controller SAY-family verbs
/// and RPO pilot transmissions (green); <see cref="Warning"/> covers opt-in WARN-channel
/// messages (amber).
/// </summary>
public enum SpeechBubbleSeverity
{
    Speech,
    Warning,
}

/// <summary>
/// Transient per-aircraft overlay shown on Radar and Ground views when the user has opted into
/// speech bubbles (Settings → Display → Overlays) and a SAY-family verb (controller-issued), an
/// RPO pilot transmission, or — when separately opted in — a WARN-channel message arrives.
/// The bubble carries the literal terminal-channel message text, an absolute UTC expiry, and a
/// <see cref="SpeechBubbleSeverity"/> the renderer uses to pick its colour. The renderer drops
/// bubbles whose <see cref="ExpiresAt"/> has passed. A subsequent message for the same aircraft
/// replaces the previous bubble (single slot, no queue).
///
/// <para>When the user opts into "keep bubbles on screen until clicked", <see cref="ExpiresAt"/>
/// is set to <see cref="DateTime.MaxValue"/> so the bubble never times out; it is cleared only
/// by the click-to-dismiss path on either view.</para>
/// </summary>
public sealed record AircraftSpeechBubble(string Text, DateTime ExpiresAt, SpeechBubbleSeverity Severity)
{
    /// <summary>
    /// Content-length-derived display duration scaled by the user's
    /// <c>SpeechBubbleDurationMultiplier</c> preference. Base: floor 4 s (short calls don't
    /// blink), ceiling 12 s (long position reports don't loiter forever), linear between at
    /// ~12 chars/s reading speed plus a 2 s buffer so the eye has time to land on the bubble.
    /// The multiplier (1.0 = unchanged) scales the clamped base, so a higher value keeps long
    /// messages up proportionally longer. Non-positive multipliers fall back to 1.0.
    /// </summary>
    public static TimeSpan ComputeDuration(string text, double multiplier)
    {
        var length = text?.Length ?? 0;
        var seconds = Math.Clamp(2.0 + length / 12.0, 4.0, 12.0);
        var scale = multiplier > 0 ? multiplier : 1.0;
        return TimeSpan.FromSeconds(seconds * scale);
    }

    /// <summary>
    /// Pure gating predicate: returns a fully-formed bubble when all opt-in conditions are met,
    /// otherwise null. Exposed so the gating logic can be exercised by unit tests without
    /// fabricating a full <c>MainViewModel</c> + preferences + aircraft collection. Callers
    /// remain responsible for locating the target aircraft and attaching the bubble.
    ///
    /// <para>SAY / pilot-speech bubbles are suppressed in solo-training mode (TTS plays the pilot
    /// voice instead). WARN bubbles are controller-facing — not TTS'd — so they show in solo mode
    /// too, but only when both the master <paramref name="showSpeechBubbles"/> toggle and the
    /// <paramref name="showWarningBubbles"/> opt-in are on.</para>
    ///
    /// <para>When <paramref name="stayUntilClicked"/> is set the bubble is pinned: its expiry is
    /// <see cref="DateTime.MaxValue"/> instead of <paramref name="nowUtc"/> plus the computed
    /// duration, so it stays on screen until the user clicks it to dismiss. This applies to both
    /// SAY/pilot and WARN bubbles.</para>
    /// </summary>
    public static AircraftSpeechBubble? TryBuild(
        bool showSpeechBubbles,
        bool showWarningBubbles,
        bool soloMode,
        TerminalEntryKind kind,
        string message,
        double durationMultiplier,
        bool stayUntilClicked,
        DateTime nowUtc
    )
    {
        if (!showSpeechBubbles || string.IsNullOrEmpty(message))
        {
            return null;
        }

        SpeechBubbleSeverity severity;
        switch (kind)
        {
            case TerminalEntryKind.Say:
            case TerminalEntryKind.PilotSpeech:
                if (soloMode)
                {
                    return null;
                }
                severity = SpeechBubbleSeverity.Speech;
                break;
            case TerminalEntryKind.Warning:
                if (!showWarningBubbles)
                {
                    return null;
                }
                severity = SpeechBubbleSeverity.Warning;
                break;
            default:
                return null;
        }

        var expiresAt = stayUntilClicked ? DateTime.MaxValue : nowUtc + ComputeDuration(message, durationMultiplier);
        return new AircraftSpeechBubble(message, expiresAt, severity);
    }
}
