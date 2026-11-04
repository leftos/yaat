namespace Yaat.GuideCapture.Capture;

// The live-traffic clock of a capture run: FixedTimeProvider.CaptureInstant plus
// the elapsed sim seconds of the room a scene bound (ElapsedSeconds), so the
// in-process server's sample-to-sim-time mapping and the feed's message age
// depend on sim time only, never on how long the run took. Unbound, it reads
// CaptureInstant. Only GetUtcNow is locked; timers stay on the system clock.
internal sealed class SimLockedClock : TimeProvider
{
    public static SimLockedClock Instance { get; } = new();

    // The bound room's elapsed sim seconds; a scene that feeds live traffic binds
    // its room and unbinds in AfterCapture.
    public Func<double> ElapsedSeconds { get; set; } = static () => 0;

    public override DateTimeOffset GetUtcNow() => FixedTimeProvider.CaptureInstant.GetUtcNow().AddSeconds(ElapsedSeconds());
}
