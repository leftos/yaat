using Avalonia.Threading;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The once-a-second beat a live flyout refills on while it is open: <see cref="Start"/> as the flyout opens,
/// <see cref="Stop"/> as it closes. Each beat runs <paramref name="refill"/> on the UI thread; a beat already queued
/// there when the flyout closes, or from an earlier opening, does nothing.
/// </summary>
/// <param name="clock">The clock the beat runs on (<see cref="IMenuHost.Clock"/>).</param>
/// <param name="refill">What runs on each beat.</param>
internal sealed class FlyoutLiveTick(TimeProvider clock, Action refill)
{
    /// <summary>How often an open live flyout refills.</summary>
    public static readonly TimeSpan Period = TimeSpan.FromSeconds(1);

    private ITimer? _timer;
    private int _opening;

    /// <summary>Whether the flyout is open and the beat running.</summary>
    public bool IsRunning => _timer is not null;

    /// <summary>Starts the beat, the first one a <see cref="Period"/> from now; a beat already running carries on.</summary>
    public void Start()
    {
        if (_timer is not null)
        {
            return;
        }

        int opening = ++_opening;
        _timer = clock.CreateTimer(_ => Dispatcher.UIThread.Post(() => Beat(opening)), null, Period, Period);
    }

    /// <summary>Stops the beat and lets go of its timer; a beat already queued does nothing when it runs.</summary>
    public void Stop()
    {
        _opening++;
        _timer?.Dispose();
        _timer = null;
    }

    private void Beat(int opening)
    {
        if ((_timer is null) || (opening != _opening))
        {
            return;
        }

        refill();
    }
}
