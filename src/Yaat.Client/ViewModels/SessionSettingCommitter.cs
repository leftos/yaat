using Microsoft.Extensions.Logging;

namespace Yaat.Client.ViewModels;

/// <summary>
/// One live session setting the flyout sends to the server. Each key is one wire call, not one control: the auto-accept
/// checkbox and its delay share a key, and so do the two command-run-delay bounds.
/// </summary>
internal enum SessionSettingKey
{
    DepartureDistance,
    AutoAccept,
    RunDelay,
    LiveTrafficCeiling,
}

/// <summary>
/// Sits between the session-settings flyout and the hub: a typed value is sent once the user stops typing, a spinner
/// step after a short trailing pause (so holding the arrow key sends one value), and Enter, focus loss or the flyout
/// closing send everything still waiting at once. The view model property stays the only home of each value; this class
/// remembers only which keys are waiting to be sent and when.
/// </summary>
/// <remarks>
/// Every key and every commit lives on the UI thread: the timers fire on the thread pool, so a timer only posts its
/// take-and-commit through <paramref name="postToUi"/> — the pending check, the property reads and the hub calls then
/// run where the view model's state is safe.
/// </remarks>
internal sealed class SessionSettingCommitter(
    TimeProvider timeProvider,
    ILogger log,
    IReadOnlyDictionary<SessionSettingKey, Action> commits,
    Action<Action> postToUi
)
{
    /// <summary>How long after the last typed character a value is sent.</summary>
    internal static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(1000);

    /// <summary>How long after the last spinner step a value is sent: shorter, so a step answers promptly.</summary>
    internal static readonly TimeSpan StepDelay = TimeSpan.FromMilliseconds(200);

    private readonly Dictionary<SessionSettingKey, ITimer> _timers = [];
    private readonly HashSet<SessionSettingKey> _pending = [];

    // A timer fires on the pool thread and posts its commit, so a job can still be queued when the user changes the box
    // again. Each change bumps the key's generation and a commit runs only if the generation its timer armed with is
    // still current, so a queued job never sends a value typed after it fired.
    private readonly Dictionary<SessionSettingKey, int> _generation = [];
    private readonly Dictionary<SessionSettingKey, int> _armedGeneration = [];

    /// <summary>Test seam: raised just before a key's commit action runs, so a test can count sends without a hub.</summary>
    internal event Action<SessionSettingKey>? Committed;

    /// <summary>True while the key holds a value the user changed but the server has not received yet.</summary>
    internal bool IsPending(SessionSettingKey key) => _pending.Contains(key);

    /// <summary>Waits for <see cref="IdleDelay"/> without another change, then commits the key.</summary>
    internal void Schedule(SessionSettingKey key) => Arm(key, IdleDelay);

    /// <summary>Waits for <see cref="StepDelay"/> without another step, then commits the key.</summary>
    internal void ScheduleStep(SessionSettingKey key) => Arm(key, StepDelay);

    /// <summary>
    /// Marks the key as waiting without arming a commit: an emptied box sends only on Enter, focus loss or close, never
    /// on idle. Any commit already armed for the key is disarmed, so a value typed before the box went blank is not sent.
    /// </summary>
    internal void MarkPending(SessionSettingKey key)
    {
        _pending.Add(key);
        StopTimer(key);
        Bump(key);
    }

    /// <summary>Commits the key now if it is waiting.</summary>
    internal void Flush(SessionSettingKey key)
    {
        if (TryTakePending(key))
        {
            Commit(key);
        }
    }

    /// <summary>Commits every key still waiting, each once.</summary>
    internal void FlushAll()
    {
        SessionSettingKey[] waiting = [.. _pending];
        foreach (SessionSettingKey key in waiting)
        {
            Flush(key);
        }
    }

    /// <summary>Commits the key now whether or not it is waiting, and disarms anything it had armed.</summary>
    internal void CommitNow(SessionSettingKey key)
    {
        _pending.Remove(key);
        StopTimer(key);
        Bump(key);
        Commit(key);
    }

    /// <summary>Drops every waiting value without sending it: the scenario or room it belonged to is gone.</summary>
    internal void DiscardAll(string reason)
    {
        int discarded = _pending.Count;
        foreach (ITimer timer in _timers.Values)
        {
            timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        _pending.Clear();
        foreach (SessionSettingKey key in _timers.Keys)
        {
            Bump(key);
        }

        if (discarded > 0)
        {
            log.LogInformation("Discarded {Count} pending session setting(s): {Reason}", discarded, reason);
        }
    }

    private void Arm(SessionSettingKey key, TimeSpan delay)
    {
        _pending.Add(key);
        _armedGeneration[key] = Bump(key);
        if (!_timers.TryGetValue(key, out ITimer? timer))
        {
            timer = timeProvider.CreateTimer(_ => OnTimer(key), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _timers[key] = timer;
        }

        timer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private void OnTimer(SessionSettingKey key) =>
        postToUi(() =>
        {
            if ((_armedGeneration.GetValueOrDefault(key) == _generation.GetValueOrDefault(key)) && TryTakePending(key))
            {
                Commit(key);
            }
        });

    private bool TryTakePending(SessionSettingKey key)
    {
        if (!_pending.Remove(key))
        {
            return false;
        }

        StopTimer(key);
        Bump(key);
        return true;
    }

    private int Bump(SessionSettingKey key) => _generation[key] = _generation.GetValueOrDefault(key) + 1;

    private void StopTimer(SessionSettingKey key)
    {
        if (_timers.TryGetValue(key, out ITimer? timer))
        {
            timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    private void Commit(SessionSettingKey key)
    {
        if (!commits.TryGetValue(key, out Action? commit))
        {
            log.LogError("No commit action is registered for session setting {Key}", key);
            return;
        }

        try
        {
            Committed?.Invoke(key);
            commit();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to commit session setting {Key}", key);
        }
    }
}
