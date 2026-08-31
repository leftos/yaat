using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Services;

namespace Yaat.Client.Views;

/// <summary>
/// Saves and restores window position, size, and maximized
/// state via <see cref="UserPreferences"/>.
/// </summary>
public sealed class WindowGeometryHelper
{
    private static readonly ILogger Log = AppLog.CreateLogger("WindowGeometry");

    private const string TopmostTitlePrefix = "📌 ";

    // Process-wide registry of live helpers. Lets external callers (e.g. the
    // Velopack update flow) flush every tracked window's geometry before a
    // process restart that bypasses the Avalonia window-closing pipeline.
    private static readonly object RegistryLock = new();
    private static readonly List<WindowGeometryHelper> ActiveHelpers = new();

    private readonly Window _window;
    private readonly UserPreferences _preferences;
    private readonly string _windowName;
    private readonly double _defaultWidth;
    private readonly double _defaultHeight;
    private readonly WindowSystemMenuHelper _systemMenuHelper;
    private readonly WindowNativeMenuHelper _nativeMenuHelper;

    private NormalWindowGeometry _lastNormalGeometry;
    private NormalWindowGeometry? _previousNormalGeometry;
    private SavedWindowGeometry? _startupGeometry;
    private DateTime _openedAtUtc = DateTime.MaxValue;
    private bool _wasMaximizedBeforeMinimize;
    private string _baseTitle = string.Empty;
    private bool _applyingTitle;
    private bool _isRegistered;

    // Debounced auto-save so position/size/state changes are persisted while the window is
    // still alive, not only at close time. Closes the gap where a process kill (Ctrl+C in
    // a script that uses Stop-Process -Force, OS power loss, crash, Velopack restart) would
    // otherwise leave the saved geometry stale.
    private static readonly TimeSpan AutoSaveDebounce = TimeSpan.FromSeconds(1.5);
    private readonly DispatcherTimer _autoSaveTimer;

    public WindowGeometryHelper(Window window, UserPreferences preferences, string windowName, double defaultWidth, double defaultHeight)
    {
        _window = window;
        _preferences = preferences;
        _windowName = windowName;
        _defaultWidth = defaultWidth;
        _defaultHeight = defaultHeight;
        _systemMenuHelper = new WindowSystemMenuHelper(window, this, preferences, windowName);
        _nativeMenuHelper = new WindowNativeMenuHelper(window, this, preferences, windowName);
        _autoSaveTimer = new DispatcherTimer { Interval = AutoSaveDebounce };
        _autoSaveTimer.Tick += OnAutoSaveTick;
    }

    /// <summary>
    /// Identifier passed to <see cref="UserPreferences.GetWindowGeometry"/> when restoring this window
    /// and used as the key under which a window-layout profile records / matches this window's geometry.
    /// </summary>
    public string WindowName => _windowName;

    /// <summary>
    /// Snapshot of all currently-live helpers. Returned as an array so callers
    /// can iterate without holding the registry lock or risking a concurrent
    /// add/remove during enumeration.
    /// </summary>
    public static IReadOnlyList<WindowGeometryHelper> GetActiveHelpers()
    {
        lock (RegistryLock)
        {
            return ActiveHelpers.ToArray();
        }
    }

    public void Restore()
    {
        var geo = _preferences.GetWindowGeometry(_windowName);

        if (geo is not null && geo.Width > 0 && geo.Height > 0)
        {
            ApplyGeometryToWindow(geo, isStartupRestore: true);
            _startupGeometry = geo;
        }
        else
        {
            _window.Width = _defaultWidth;
            _window.Height = _defaultHeight;
            _window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        _lastNormalGeometry = CaptureCurrentGeometry();

        _baseTitle = _window.Title ?? string.Empty;
        ApplyTitle();

        _window.Opened += OnWindowOpened;
        _window.PropertyChanged += OnWindowPropertyChanged;
        _window.PositionChanged += OnPositionChanged;
        _window.SizeChanged += OnWindowSizeChanged;
        _window.Closing += OnClosing;
        _preferences.WindowTopmostChanged += OnPreferencesWindowTopmostChanged;
        _systemMenuHelper.Attach();
        _nativeMenuHelper.Attach();
        WindowGroupRaiser.Attach(_window, _preferences);

        Register();
    }

    /// <summary>
    /// Persists the current window geometry to <see cref="UserPreferences"/>
    /// without detaching event handlers. Call this when the process is about
    /// to terminate via a path that bypasses the window-closing pipeline
    /// (e.g. Velopack's <c>ApplyUpdatesAndRestart</c>).
    /// </summary>
    public void FlushSavedGeometry()
    {
        SaveCurrentGeometry();
    }

    /// <summary>
    /// Pushes a previously-saved geometry onto the live window (used when
    /// applying a window-layout profile). Differs from <see cref="Restore"/>
    /// in that it operates on a window that is already showing, so it must
    /// also un-minimize / un-maximize / re-maximize and reposition relative
    /// to the current screen layout, then bring the window to the front —
    /// setting <see cref="Window.WindowState"/> alone un-minimizes but does
    /// not raise the window (#360/#365). A geometry saved with
    /// <see cref="SavedWindowGeometry.IsMinimized"/> instead re-minimizes the
    /// window without activating it. The given geometry is clamped to a real
    /// screen so a profile captured on a multi-monitor box still lands
    /// on-screen when applied on a single-monitor box.
    /// </summary>
    public void ApplyGeometry(SavedWindowGeometry geometry)
    {
        if (geometry.Width <= 0 || geometry.Height <= 0)
        {
            return;
        }
        ApplyGeometryToWindow(geometry, isStartupRestore: false);
        // A minimized/maximized end state reports the iconic/maximized frame, which must
        // never be recorded as the window's normal geometry; the Width/Position setters
        // already refreshed _lastNormalGeometry while the window was still Normal.
        if (_window.WindowState == WindowState.Normal)
        {
            _lastNormalGeometry = CaptureCurrentGeometry();
        }
    }

    /// <summary>
    /// A screen's working area in device pixels plus its DPI scale factor,
    /// decoupled from Avalonia's <see cref="Screen"/> so geometry resolution
    /// is testable without a windowing platform.
    /// </summary>
    public readonly record struct ScreenInfo(PixelRect WorkingArea, double Scaling);

    /// <summary>
    /// Result of <see cref="ResolveGeometry"/>: position in device pixels,
    /// size in device-independent pixels (what <see cref="Window.Width"/> expects).
    /// </summary>
    public readonly record struct ResolvedGeometry(int X, int Y, double Width, double Height);

    // Minimum on-screen overlap (device px) for a saved position to be restored verbatim —
    // roughly enough visible title bar to grab the window with the mouse.
    private const int MinVisibleWidthPx = 50;
    private const int MinVisibleHeightPx = 30;

    /// <summary>
    /// Resolves where a saved geometry should land given the current screen layout.
    /// Saved positions are device pixels; saved sizes are DIPs. A position whose window
    /// still shows a usable sliver on any screen is restored verbatim — a window snapped
    /// to a screen edge legitimately has its frame origin slightly outside the working
    /// area (Windows' invisible resize borders), and clamping it inward makes it overlap
    /// its neighbors. Clamping into the target screen's working area is only a rescue
    /// for geometry that would be substantially off-screen (monitor unplugged,
    /// resolution shrank, or a poisoned iconic position).
    /// </summary>
    public static ResolvedGeometry ResolveGeometry(SavedWindowGeometry geo, IReadOnlyList<ScreenInfo> screens)
    {
        var target = ResolveTargetScreen(screens, geo);
        var workArea = target.WorkingArea;
        var scaling = SafeScaling(target.Scaling);

        var widthPx = (int)Math.Round(Math.Min(geo.Width * scaling, workArea.Width));
        var heightPx = (int)Math.Round(Math.Min(geo.Height * scaling, workArea.Height));
        var width = geo.Width * scaling > workArea.Width ? workArea.Width / scaling : geo.Width;
        var height = geo.Height * scaling > workArea.Height ? workArea.Height / scaling : geo.Height;

        if (IsSufficientlyVisible(new PixelRect(geo.X, geo.Y, widthPx, heightPx), screens))
        {
            return new ResolvedGeometry(geo.X, geo.Y, width, height);
        }

        var x = Clamp(geo.X, workArea.X, workArea.Right - widthPx);
        var y = Clamp(geo.Y, workArea.Y, workArea.Bottom - heightPx);

        return new ResolvedGeometry(x, y, width, height);
    }

    private static bool IsSufficientlyVisible(PixelRect windowRect, IReadOnlyList<ScreenInfo> screens)
    {
        foreach (var screen in screens)
        {
            var workArea = screen.WorkingArea;
            var overlapWidth = Math.Min(windowRect.Right, workArea.Right) - Math.Max(windowRect.X, workArea.X);
            var overlapHeight = Math.Min(windowRect.Bottom, workArea.Bottom) - Math.Max(windowRect.Y, workArea.Y);
            if ((overlapWidth >= MinVisibleWidthPx) && (overlapHeight >= MinVisibleHeightPx))
            {
                return true;
            }
        }

        return false;
    }

    private static ScreenInfo ResolveTargetScreen(IReadOnlyList<ScreenInfo> screens, SavedWindowGeometry geo)
    {
        // Prefer the saved screen index if it still exists
        if (geo.ScreenIndex >= 0 && geo.ScreenIndex < screens.Count)
        {
            return screens[geo.ScreenIndex];
        }

        // Fall back to whichever screen contains the saved center point
        foreach (var screen in screens)
        {
            var scaling = SafeScaling(screen.Scaling);
            var center = new PixelPoint(geo.X + (int)(geo.Width * scaling / 2), geo.Y + (int)(geo.Height * scaling / 2));
            if (screen.WorkingArea.Contains(center))
            {
                return screen;
            }
        }

        // No screen contains the saved position — use primary
        return screens[0];
    }

    private static double SafeScaling(double scaling) => scaling > 0 ? scaling : 1.0;

    private void ApplyGeometryToWindow(SavedWindowGeometry geo, bool isStartupRestore)
    {
        var screenInfos = GetScreenSnapshot();
        if (screenInfos.Count > 0)
        {
            var resolved = ResolveGeometry(geo, screenInfos);

            Log.LogDebug(
                "{Window} apply ({Mode}): saved={Saved} resolved={Resolved} screens=[{Screens}]",
                _windowName,
                isStartupRestore ? "startup" : "profile",
                Describe(geo),
                resolved,
                DescribeScreens(screenInfos)
            );

            if (isStartupRestore)
            {
                _window.WindowStartupLocation = WindowStartupLocation.Manual;
            }
            else if ((_window.WindowState == WindowState.Minimized) || ((_window.WindowState == WindowState.Maximized) && !geo.IsMaximized))
            {
                // Cannot resize/reposition while minimized or maximized — the geometry
                // must land on the restored frame, not the iconic one, so drop to
                // Normal first.
                _window.WindowState = WindowState.Normal;
            }

            _window.Width = resolved.Width;
            _window.Height = resolved.Height;
            _window.Position = new PixelPoint(resolved.X, resolved.Y);

            // Maximize before minimizing so a minimized geometry still restores to
            // the maximized frame when the user later un-minimizes it.
            _window.WindowState = geo.IsMaximized ? WindowState.Maximized : WindowState.Normal;
            if (geo.IsMinimized && !isStartupRestore)
            {
                _window.WindowState = WindowState.Minimized;
            }
        }
        else
        {
            Log.LogWarning("{Window} apply: no screens reported; setting size only (saved={Saved})", _windowName, Describe(geo));
            _window.Width = geo.Width;
            _window.Height = geo.Height;
        }

        _window.Topmost = geo.IsTopmost;

        // Profile apply brings every saved foreground window to the front; startup
        // restore runs while the window is being shown, which raises it anyway.
        if (!isStartupRestore && _window.WindowState != WindowState.Minimized)
        {
            _window.Activate();
        }
    }

    // Position drift tolerated between where a startup restore placed the window and where
    // it actually ended up once shown, before the post-open verify re-applies the geometry.
    // Covers sub-pixel rounding; anything larger means the OS or toolkit moved the window.
    private const int StartupDriftTolerancePx = 2;
    private const double StartupDriftToleranceDip = 2.0;

    /// <summary>
    /// Whether a shown window sits where <see cref="ResolveGeometry"/> placed it, within
    /// rounding tolerance. Position compares in device pixels, size in DIPs.
    /// </summary>
    public static bool IsAtResolvedGeometry(ResolvedGeometry resolved, PixelPoint actualPosition, double actualWidth, double actualHeight) =>
        (Math.Abs(actualPosition.X - resolved.X) <= StartupDriftTolerancePx)
        && (Math.Abs(actualPosition.Y - resolved.Y) <= StartupDriftTolerancePx)
        && (Math.Abs(actualWidth - resolved.Width) <= StartupDriftToleranceDip)
        && (Math.Abs(actualHeight - resolved.Height) <= StartupDriftToleranceDip);

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        _window.Opened -= OnWindowOpened;
        _openedAtUtc = DateTime.UtcNow;
        // Post at Background so the verify runs after the show-time position/size events
        // the windowing system queues while the frame is realized.
        Dispatcher.UIThread.Post(VerifyStartupGeometry, DispatcherPriority.Background);
    }

    /// <summary>
    /// One-shot check after the window first shows: if the OS or toolkit moved the window
    /// away from where the startup restore placed it (GitHub #408 — main window landing
    /// offset past the left screen edge on the first launch of the day), re-resolve the
    /// saved geometry against the current screens and re-apply it once. Re-applying the
    /// same geometry is exactly the reporter's manual workaround (loading a profile with
    /// identical coordinates), automated.
    /// </summary>
    private void VerifyStartupGeometry()
    {
        if (_startupGeometry is not { } saved)
        {
            return;
        }

        if (saved.IsMaximized || _window.WindowState != WindowState.Normal)
        {
            Log.LogDebug("{Window} post-open verify skipped: state={State}", _windowName, _window.WindowState);
            return;
        }

        var screens = GetScreenSnapshot();
        if (screens.Count == 0)
        {
            Log.LogWarning("{Window} post-open verify skipped: no screens reported", _windowName);
            return;
        }

        var resolved = ResolveGeometry(saved, screens);
        var actualPosition = _window.Position;
        var actualWidth = _window.Width;
        var actualHeight = _window.Height;

        if (IsAtResolvedGeometry(resolved, actualPosition, actualWidth, actualHeight))
        {
            Log.LogDebug("{Window} post-open verify OK at {Position} {Width}x{Height}", _windowName, actualPosition, actualWidth, actualHeight);
            return;
        }

        Log.LogWarning(
            "{Window} landed at {Position} {Width}x{Height} instead of {Resolved} (saved={Saved}, screens=[{Screens}]); re-applying",
            _windowName,
            actualPosition,
            actualWidth,
            actualHeight,
            resolved,
            Describe(saved),
            DescribeScreens(screens)
        );

        _window.Width = resolved.Width;
        _window.Height = resolved.Height;
        _window.Position = new PixelPoint(resolved.X, resolved.Y);
    }

    private IReadOnlyList<ScreenInfo> GetScreenSnapshot()
    {
        var screens = _window.Screens.All;
        var screenInfos = new ScreenInfo[screens.Count];
        for (var i = 0; i < screens.Count; i++)
        {
            screenInfos[i] = new ScreenInfo(screens[i].WorkingArea, screens[i].Scaling);
        }

        return screenInfos;
    }

    private static string Describe(SavedWindowGeometry geo) =>
        $"({geo.X},{geo.Y}) {geo.Width}x{geo.Height} screen={geo.ScreenIndex}" + (geo.IsMaximized ? " max" : "") + (geo.IsMinimized ? " min" : "");

    private static string DescribeScreens(IReadOnlyList<ScreenInfo> screens) =>
        string.Join("; ", screens.Select(s => $"{s.WorkingArea} @{s.Scaling:0.##}x"));

    /// <summary>
    /// Calls <see cref="FlushSavedGeometry"/> on every registered helper.
    /// Use before a process restart that won't trigger window-close events.
    /// </summary>
    public static void FlushAllSavedGeometries()
    {
        WindowGeometryHelper[] snapshot;
        lock (RegistryLock)
        {
            snapshot = ActiveHelpers.ToArray();
        }

        foreach (var helper in snapshot)
        {
            helper.FlushSavedGeometry();
        }
    }

    private void Register()
    {
        lock (RegistryLock)
        {
            if (_isRegistered)
            {
                return;
            }
            ActiveHelpers.Add(this);
            _isRegistered = true;
        }
    }

    private void Unregister()
    {
        lock (RegistryLock)
        {
            if (!_isRegistered)
            {
                return;
            }
            ActiveHelpers.Remove(this);
            _isRegistered = false;
        }
    }

    private void OnPreferencesWindowTopmostChanged(string windowName, bool isTopmost)
    {
        if (windowName != _windowName)
        {
            return;
        }

        if (_window.Topmost != isTopmost)
        {
            _window.Topmost = isTopmost;
        }
    }

    public void SetBaseTitle(string title)
    {
        _baseTitle = title ?? string.Empty;
        ApplyTitle();
    }

    private void ApplyTitle()
    {
        if (_applyingTitle)
        {
            return;
        }

        _applyingTitle = true;
        try
        {
            _window.Title = _window.Topmost ? TopmostTitlePrefix + _baseTitle : _baseTitle;
        }
        finally
        {
            _applyingTitle = false;
        }
    }

    private static int Clamp(int value, int min, int max)
    {
        if (min > max)
        {
            return min;
        }

        return Math.Max(min, Math.Min(value, max));
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.TopmostProperty)
        {
            // A group-raise pulses Topmost true→false without changing the
            // window's real pinned state — reacting would flash the 📌 title
            // prefix and schedule a pointless save.
            if (WindowGroupRaiser.IsRaising)
            {
                return;
            }
            ApplyTitle();
            ScheduleAutoSave();
        }
        else if (e.Property == Window.WindowStateProperty)
        {
            // Remember whether a minimize came from a maximized frame so the saved
            // geometry keeps IsMaximized while the window sits minimized (#365).
            if (e.GetNewValue<WindowState>() == WindowState.Minimized)
            {
                _wasMaximizedBeforeMinimize = e.GetOldValue<WindowState>() == WindowState.Maximized;
            }
            ScheduleAutoSave();
        }
    }

    // How long after first show a position change is still logged as suspect — moves this
    // early are the OS/toolkit settling (monitor wake, DPI change, off-screen rescue), not
    // the user dragging the window. Diagnostic breadcrumb for GitHub #408.
    private static readonly TimeSpan EarlyMoveLogWindow = TimeSpan.FromSeconds(10);

    private void OnPositionChanged(object? sender, PixelPointEventArgs e)
    {
        var sinceOpened = DateTime.UtcNow - _openedAtUtc;
        if ((sinceOpened >= TimeSpan.Zero) && (sinceOpened < EarlyMoveLogWindow))
        {
            Log.LogInformation("{Window} moved to {Position} within {Seconds:0}s of opening", _windowName, e.Point, EarlyMoveLogWindow.TotalSeconds);
        }

        // A minimize can report its iconic position before WindowState flips to
        // Minimized — never let that sentinel replace the last normal geometry.
        var current = CaptureCurrentGeometry();
        if (_window.WindowState == WindowState.Normal && !IsIconicOrigin(current))
        {
            _previousNormalGeometry = _lastNormalGeometry;
            _lastNormalGeometry = current;
        }
        ScheduleAutoSave();
    }

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_window.WindowState == WindowState.Normal)
        {
            _lastNormalGeometry = CaptureCurrentGeometry();
        }
        ScheduleAutoSave();
    }

    private void ScheduleAutoSave()
    {
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    private void OnAutoSaveTick(object? sender, System.EventArgs e)
    {
        _autoSaveTimer.Stop();
        SaveCurrentGeometry();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        _autoSaveTimer.Stop();
        _window.Opened -= OnWindowOpened;
        _preferences.WindowTopmostChanged -= OnPreferencesWindowTopmostChanged;
        SaveCurrentGeometry();
        Unregister();
    }

    private void SaveCurrentGeometry()
    {
        var geo = CreateSavedGeometry();
        Log.LogDebug("{Window} save: {Saved} (state={State})", _windowName, Describe(geo), _window.WindowState);
        _preferences.SetWindowGeometry(_windowName, geo);
    }

    public void ToggleTopmost()
    {
        _window.Topmost = !_window.Topmost;
        SaveCurrentGeometry();
    }

    private SavedWindowGeometry CreateSavedGeometry()
    {
        var state = _window.WindowState;
        var isMinimized = state == WindowState.Minimized;
        var geometry = GetNormalGeometryForPersistence(isNotNormal: state != WindowState.Normal);
        return new SavedWindowGeometry
        {
            X = geometry.Position.X,
            Y = geometry.Position.Y,
            Width = geometry.Width,
            Height = geometry.Height,
            IsMaximized = (state == WindowState.Maximized) || (isMinimized && _wasMaximizedBeforeMinimize),
            IsMinimized = isMinimized,
            ScreenIndex = GetCurrentScreenIndex(geometry),
            IsTopmost = _window.Topmost,
        };
    }

    private NormalWindowGeometry GetNormalGeometryForPersistence(bool isNotNormal)
    {
        if (!isNotNormal)
        {
            return CaptureCurrentGeometry();
        }

        if (IsIconicOrigin(_lastNormalGeometry) && _previousNormalGeometry is { } previousGeometry)
        {
            return previousGeometry;
        }

        return _lastNormalGeometry;
    }

    private NormalWindowGeometry CaptureCurrentGeometry() => new(_window.Position, _window.Width, _window.Height);

    // (0,0) is the headless/macOS iconic report; (-32000,-32000) is the Win32 sentinel
    // for a minimized window's position.
    private static bool IsIconicOrigin(NormalWindowGeometry geometry) => geometry.Position is { X: 0, Y: 0 } or { X: <= -32000, Y: <= -32000 };

    private int GetCurrentScreenIndex(NormalWindowGeometry geometry)
    {
        var screens = _window.Screens.All;

        for (var i = 0; i < screens.Count; i++)
        {
            var scaling = SafeScaling(screens[i].Scaling);
            var center = new PixelPoint(
                geometry.Position.X + (int)(geometry.Width * scaling / 2),
                geometry.Position.Y + (int)(geometry.Height * scaling / 2)
            );
            if (screens[i].WorkingArea.Contains(center))
            {
                return i;
            }
        }

        return 0;
    }

    private readonly record struct NormalWindowGeometry(PixelPoint Position, double Width, double Height);
}
