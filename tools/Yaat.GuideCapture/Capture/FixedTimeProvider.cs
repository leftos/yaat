namespace Yaat.GuideCapture.Capture;

// A clock that never moves, so every terminal timestamp in the captures reads
// the same instant on every run. Only GetUtcNow is pinned: timers and
// GetTimestamp stay on the system clock, and the local zone stays the
// machine's, matching how the client converts server stamps for display.
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    // The instant every capture's terminal shows.
    public static FixedTimeProvider CaptureInstant { get; } = new(new DateTimeOffset(2026, 1, 15, 18, 30, 0, TimeSpan.Zero));

    public override DateTimeOffset GetUtcNow() => now;
}
