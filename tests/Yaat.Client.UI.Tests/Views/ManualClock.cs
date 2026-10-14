namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// A clock that moves only when the test advances it; each advance fires every live timer once. It is the clock a live
/// flyout's once-a-second refill runs on (the test hosts' <c>Clock</c>), so no refill timer outlives a test, and the time
/// source a strip label's marquee reads; the marquee's frames run on a dispatcher timer, so a test draws one with
/// <c>QuickCommandLabelLine.RenderMarqueeFrame</c>.
/// </summary>
internal sealed class ManualClock : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private long _ticks;

    /// <summary>How many timers made on this clock have not been disposed.</summary>
    public int LiveTimerCount => _timers.Count;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _ticks;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(() => callback(state), _timers);
        _timers.Add(timer);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        _ticks += by.Ticks;
        foreach (ManualTimer timer in _timers.ToList())
        {
            timer.Fire();
        }
    }

    private sealed class ManualTimer(Action fire, List<ManualTimer> timers) : ITimer
    {
        public void Fire() => fire();

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose() => timers.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
