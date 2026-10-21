namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// A clock that moves only when the test advances it; an advance fires each live timer whose due time it passes, in due order,
/// a periodic timer once per period passed and a fire-once timer once, as a real timer would. It is the clock a live flyout's
/// once-a-second refill runs on (the test hosts' <c>Clock</c>), so no refill timer outlives a test, and the time source a strip
/// label's marquee reads; the marquee's frames run on a dispatcher timer, so a test draws one with
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
        var timer = new ManualTimer(this, () => callback(state));
        timer.Change(dueTime, period);
        _timers.Add(timer);
        return timer;
    }

    /// <summary>
    /// Moves the clock on by <paramref name="by"/>, firing every timer falling due on the way, earliest first; while a timer's
    /// callback runs, the clock reads the time it fell due.
    /// </summary>
    public void Advance(TimeSpan by)
    {
        long end = _ticks + by.Ticks;
        while (NextDue(end) is { } timer)
        {
            _ticks = timer.DueTicks!.Value;
            timer.Fire();
        }

        _ticks = end;
    }

    private ManualTimer? NextDue(long end) => _timers.Where(timer => timer.DueTicks <= end).MinBy(timer => timer.DueTicks);

    private sealed class ManualTimer(ManualClock clock, Action callback) : ITimer
    {
        private long? _periodTicks;

        /// <summary>When the timer fires next, in the clock's ticks; null while it is stopped.</summary>
        public long? DueTicks { get; private set; }

        public void Fire()
        {
            DueTicks += _periodTicks;
            callback();
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            DueTicks = (dueTime == Timeout.InfiniteTimeSpan) ? null : clock._ticks + Math.Max(0, dueTime.Ticks);
            _periodTicks = ((period == Timeout.InfiniteTimeSpan) || (period <= TimeSpan.Zero)) ? null : period.Ticks;
            return true;
        }

        public void Dispose()
        {
            DueTicks = null;
            clock._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
