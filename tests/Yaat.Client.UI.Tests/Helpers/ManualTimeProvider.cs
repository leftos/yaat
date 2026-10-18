namespace Yaat.Client.UI.Tests.Helpers;

/// <summary>
/// A clock a test advances by hand, so the session-settings debounce can be proven without waiting out its timers. It
/// runs nothing else: a timer callback that posts to the dispatcher leaves the job queued, and the test decides when the
/// UI thread runs it.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

    public override DateTimeOffset GetUtcNow() => _now;

    public override long GetTimestamp() => _now.UtcTicks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        _timers.Add(timer);
        return timer;
    }

    /// <summary>Moves time forward, running every timer that comes due on the way, in due order.</summary>
    public void Advance(TimeSpan delta)
    {
        DateTimeOffset target = _now + delta;
        while (true)
        {
            ManualTimer? next = null;
            foreach (ManualTimer timer in _timers)
            {
                if (timer.DueAt is { } due && due <= target && (next?.DueAt is null || due < next.DueAt))
                {
                    next = timer;
                }
            }

            if (next?.DueAt is not { } nextDue)
            {
                break;
            }

            _now = nextDue;
            next.DueAt = null;
            next.Fire();
        }

        _now = target;
    }

    internal void Remove(ManualTimer timer) => _timers.Remove(timer);

    internal sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        internal DateTimeOffset? DueAt { get; set; }

        internal void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner.GetUtcNow() + dueTime;
            return true;
        }

        public void Dispose() => owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
