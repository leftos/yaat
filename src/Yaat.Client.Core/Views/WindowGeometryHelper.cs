using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Yaat.Client.Services;

namespace Yaat.Client.Views;

/// <summary>
/// Saves and restores window position, size, and maximized
/// state via <see cref="UserPreferences"/>.
/// </summary>
public sealed class WindowGeometryHelper
{
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
    private string _baseTitle = string.Empty;
    private bool _applyingTitle;
    private bool _isRegistered;

    public WindowGeometryHelper(Window window, UserPreferences preferences, string windowName, double defaultWidth, double defaultHeight)
    {
        _window = window;
        _preferences = preferences;
        _windowName = windowName;
        _defaultWidth = defaultWidth;
        _defaultHeight = defaultHeight;
        _systemMenuHelper = new WindowSystemMenuHelper(window, this, preferences, windowName);
        _nativeMenuHelper = new WindowNativeMenuHelper(window, this, preferences, windowName);
    }

    public void Restore()
    {
        var geo = _preferences.GetWindowGeometry(_windowName);

        if (geo is not null && geo.Width > 0 && geo.Height > 0)
        {
            var screens = _window.Screens.All;
            if (screens.Count > 0)
            {
                var targetScreen = GetTargetScreen(screens, geo);
                var workArea = targetScreen.WorkingArea;

                var width = Math.Min(geo.Width, workArea.Width);
                var height = Math.Min(geo.Height, workArea.Height);

                var x = Clamp(geo.X, workArea.X, workArea.Right - (int)width);
                var y = Clamp(geo.Y, workArea.Y, workArea.Bottom - (int)height);

                _window.WindowStartupLocation = WindowStartupLocation.Manual;
                _window.Width = width;
                _window.Height = height;
                _window.Position = new PixelPoint(x, y);

                if (geo.IsMaximized)
                {
                    _window.WindowState = WindowState.Maximized;
                }
            }
            else
            {
                _window.Width = geo.Width;
                _window.Height = geo.Height;
            }

            _window.Topmost = geo.IsTopmost;
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

        _window.PropertyChanged += OnWindowPropertyChanged;
        _window.PositionChanged += OnPositionChanged;
        _window.Closing += OnClosing;
        _preferences.WindowTopmostChanged += OnPreferencesWindowTopmostChanged;
        _systemMenuHelper.Attach();
        _nativeMenuHelper.Attach();

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

    private static Screen GetTargetScreen(IReadOnlyList<Screen> screens, SavedWindowGeometry geo)
    {
        // Prefer the saved screen index if it still exists
        if (geo.ScreenIndex >= 0 && geo.ScreenIndex < screens.Count)
        {
            return screens[geo.ScreenIndex];
        }

        // Fall back to whichever screen contains the saved center point
        var centerX = geo.X + (int)(geo.Width / 2);
        var centerY = geo.Y + (int)(geo.Height / 2);
        var center = new PixelPoint(centerX, centerY);

        foreach (var screen in screens)
        {
            if (screen.WorkingArea.Contains(center))
            {
                return screen;
            }
        }

        // No screen contains the saved position — use primary
        return screens[0];
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
            ApplyTitle();
        }
    }

    private void OnPositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (_window.WindowState == WindowState.Normal)
        {
            _previousNormalGeometry = _lastNormalGeometry;
            _lastNormalGeometry = CaptureCurrentGeometry();
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        _preferences.WindowTopmostChanged -= OnPreferencesWindowTopmostChanged;
        SaveCurrentGeometry();
        Unregister();
    }

    private void SaveCurrentGeometry()
    {
        var isNotNormal = _window.WindowState != WindowState.Normal;
        var isMax = _window.WindowState == WindowState.Maximized;

        var normalGeometry = GetNormalGeometryForPersistence(isNotNormal);
        var geo = CreateSavedGeometry(normalGeometry, isMax);

        _preferences.SetWindowGeometry(_windowName, geo);
    }

    public void ToggleTopmost()
    {
        _window.Topmost = !_window.Topmost;

        var isNotNormal = _window.WindowState != WindowState.Normal;
        var isMax = _window.WindowState == WindowState.Maximized;
        var normalGeometry = GetNormalGeometryForPersistence(isNotNormal);
        var geo = CreateSavedGeometry(normalGeometry, isMax);

        _preferences.SetWindowGeometry(_windowName, geo);
    }

    private SavedWindowGeometry CreateSavedGeometry(NormalWindowGeometry geometry, bool isMaximized) =>
        new()
        {
            X = geometry.Position.X,
            Y = geometry.Position.Y,
            Width = geometry.Width,
            Height = geometry.Height,
            IsMaximized = isMaximized,
            ScreenIndex = GetCurrentScreenIndex(geometry),
            IsTopmost = _window.Topmost,
        };

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

    private static bool IsIconicOrigin(NormalWindowGeometry geometry) => geometry.Position is { X: 0, Y: 0 };

    private int GetCurrentScreenIndex(NormalWindowGeometry geometry)
    {
        var screens = _window.Screens.All;
        var centerX = geometry.Position.X + (int)(geometry.Width / 2);
        var centerY = geometry.Position.Y + (int)(geometry.Height / 2);
        var center = new PixelPoint(centerX, centerY);

        for (var i = 0; i < screens.Count; i++)
        {
            if (screens[i].WorkingArea.Contains(center))
            {
                return i;
            }
        }

        return 0;
    }

    private readonly record struct NormalWindowGeometry(PixelPoint Position, double Width, double Height);
}
