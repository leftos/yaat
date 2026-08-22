using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Services;

namespace Yaat.Client.Views;

/// <summary>
/// Raises every YAAT window to the top of the Z-order when the app regains focus,
/// so clicking one YAAT window uncovers all of them — the same feel as CRC, whose
/// windows share a Win32 ownership group. YAAT pop-outs are deliberately unowned
/// top-level windows (arbitrary stacking among them must stay possible), so the
/// group-raise is explicit: each window raised via a <c>Topmost</c> pulse, which
/// maps to a no-activate <c>SetWindowPos</c> and therefore never steals focus.
///
/// Windows are tracked in most-recently-activated order and re-raised in current
/// Z-order (via <see cref="Window.SortWindowsByZOrder"/>), so relative stacking is
/// preserved and the clicked window ends on top. A raise only fires when focus
/// returns from another application — focus handoff between two YAAT windows is
/// detected via the posted deactivation check and leaves the Z-order alone.
///
/// UI-thread only: every entry point runs on the Avalonia UI thread, so the
/// static state needs no locking.
/// </summary>
public static class WindowGroupRaiser
{
    private static readonly ILogger Log = AppLog.CreateLogger("WindowGroupRaiser");

    // Most-recently-activated last. Approximates Z-order on platforms where
    // SortWindowsByZOrder cannot resolve it.
    private static readonly List<Window> Tracked = new();
    private static readonly Dictionary<Window, UserPreferences> Preferences = new();

    private static bool _groupActive;
    private static bool _deactivationCheckPending;

    /// <summary>
    /// True while the raiser is pulsing <see cref="Window.Topmost"/>.
    /// <see cref="WindowGeometryHelper"/> checks this to ignore the transient
    /// property changes (📌 title prefix, debounced geometry auto-save).
    /// </summary>
    public static bool IsRaising { get; private set; }

    /// <summary>
    /// Suspends group-raising while a window-layout profile is being applied —
    /// profile apply activates windows in a deliberate order (#360/#365) that a
    /// concurrent group-raise would scramble.
    /// </summary>
    public static bool IsSuspended { get; set; }

    /// <summary>Fired after a group raise completes, with the window whose activation triggered it.</summary>
    internal static event Action<Window>? GroupRaised;

    /// <summary>
    /// Starts tracking a window. Called once per window from
    /// <see cref="WindowGeometryHelper.Restore"/>; the raiser detaches itself when
    /// the window closes.
    /// </summary>
    public static void Attach(Window window, UserPreferences preferences)
    {
        if (Tracked.Contains(window))
        {
            return;
        }

        Tracked.Add(window);
        Preferences[window] = preferences;
        window.Activated += OnWindowActivated;
        window.Deactivated += OnWindowDeactivated;
        window.Closed += OnWindowClosed;
    }

    private static void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not Window window)
        {
            return;
        }

        window.Activated -= OnWindowActivated;
        window.Deactivated -= OnWindowDeactivated;
        window.Closed -= OnWindowClosed;
        Tracked.Remove(window);
        Preferences.Remove(window);
    }

    private static void OnWindowActivated(object? sender, EventArgs e)
    {
        if (sender is not Window window)
        {
            return;
        }

        // Move to MRU end. NOTE: Avalonia raises Activated before setting
        // IsActive = true on the activating window, so never consult
        // window.IsActive here.
        Tracked.Remove(window);
        Tracked.Add(window);

        var raise = !_groupActive && !IsSuspended && !IsRaising;
        _groupActive = true;
        if (raise && (Preferences.GetValueOrDefault(window)?.RaiseWindowsTogether ?? false))
        {
            RaiseAll(window);
        }
    }

    private static void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (_deactivationCheckPending)
        {
            return;
        }

        // On an intra-app focus handoff the next window's Activated has already
        // fired (and set IsActive) by the time this posted check runs; only when
        // focus truly left the app is no tracked window active.
        _deactivationCheckPending = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _deactivationCheckPending = false;
                if (!Tracked.Any(w => w.IsActive))
                {
                    _groupActive = false;
                }
            },
            DispatcherPriority.Background
        );
    }

    private static void RaiseAll(Window activatedWindow)
    {
        var order = ComputeRaiseOrder(Tracked, activatedWindow);

        IsRaising = true;
        try
        {
            foreach (var window in order)
            {
                // Raises without activating: HWND_TOPMOST then HWND_NOTOPMOST,
                // both with SWP_NOACTIVATE, leaving the window at the top of the
                // normal band with focus untouched.
                window.Topmost = true;
                window.Topmost = false;
            }
        }
        finally
        {
            IsRaising = false;
        }

        GroupRaised?.Invoke(activatedWindow);
    }

    /// <summary>
    /// Computes which windows to pulse and in what order: visible, non-minimized,
    /// non-pinned, unowned windows (owned dialogs already ride with their owner;
    /// pinned windows already sit above everything), bottom of the current Z-order
    /// first, the just-activated window last so it finishes on top.
    /// </summary>
    internal static List<Window> ComputeRaiseOrder(IReadOnlyList<Window> mruWindows, Window activatedWindow)
    {
        var candidates = new List<Window>();
        foreach (var window in mruWindows)
        {
            var include =
                (window == activatedWindow)
                || ((window.IsVisible) && (window.WindowState != WindowState.Minimized) && (!window.Topmost) && (window.Owner is null));
            if (include)
            {
                candidates.Add(window);
            }
        }

        var ordered = candidates.ToArray();
        try
        {
            // Ascending Z-order, topmost last. Platforms answer via
            // IWindowingPlatform.GetWindowsZOrder (Win32/macOS/X11/Headless).
            Window.SortWindowsByZOrder(ordered);
        }
        catch (Exception ex)
        {
            // A window mid-close (null PlatformImpl) makes the sort throw; the MRU
            // order already approximates Z-order, so fall back to it.
            Log.LogDebug(ex, "SortWindowsByZOrder failed; falling back to activation order");
        }

        var result = new List<Window>(ordered.Length);
        foreach (var window in ordered)
        {
            if (window != activatedWindow)
            {
                result.Add(window);
            }
        }

        // The activated window is pulsed last so it ends on top — unless it is
        // pinned, in which case it already floats above the normal band and
        // pulsing would be a no-op that still churns the platform window state.
        if (!activatedWindow.Topmost)
        {
            result.Add(activatedWindow);
        }

        return result;
    }

    /// <summary>Clears all static state. Test-only — headless tests share the process-wide statics.</summary>
    internal static void ResetForTest()
    {
        foreach (var window in Tracked.ToArray())
        {
            window.Activated -= OnWindowActivated;
            window.Deactivated -= OnWindowDeactivated;
            window.Closed -= OnWindowClosed;
        }

        Tracked.Clear();
        Preferences.Clear();
        _groupActive = false;
        _deactivationCheckPending = false;
        IsRaising = false;
        IsSuspended = false;
        GroupRaised = null;
    }
}
