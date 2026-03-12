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
    private readonly Window _window;
    private readonly UserPreferences _preferences;
    private readonly string _windowName;
    private readonly double _defaultWidth;
    private readonly double _defaultHeight;

    private PixelPoint _lastNormalPosition;
    private double _lastNormalWidth;
    private double _lastNormalHeight;

    public WindowGeometryHelper(Window window, UserPreferences preferences, string windowName, double defaultWidth, double defaultHeight)
    {
        _window = window;
        _preferences = preferences;
        _windowName = windowName;
        _defaultWidth = defaultWidth;
        _defaultHeight = defaultHeight;
    }

    public void Restore()
    {
        var geo = _preferences.GetWindowGeometry(_windowName);

        if (geo is not null)
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
        }
        else
        {
            _window.Width = _defaultWidth;
            _window.Height = _defaultHeight;
            _window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        _lastNormalWidth = _window.Width;
        _lastNormalHeight = _window.Height;
        _lastNormalPosition = _window.Position;

        _window.PositionChanged += OnPositionChanged;
        _window.Closing += OnClosing;
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

    private void OnPositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (_window.WindowState == WindowState.Normal)
        {
            _lastNormalPosition = _window.Position;
            _lastNormalWidth = _window.Width;
            _lastNormalHeight = _window.Height;
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        var isMax = _window.WindowState == WindowState.Maximized;

        var geo = new SavedWindowGeometry
        {
            X = isMax ? _lastNormalPosition.X : _window.Position.X,
            Y = isMax ? _lastNormalPosition.Y : _window.Position.Y,
            Width = isMax ? _lastNormalWidth : _window.Width,
            Height = isMax ? _lastNormalHeight : _window.Height,
            IsMaximized = isMax,
            ScreenIndex = GetCurrentScreenIndex(),
        };

        _preferences.SetWindowGeometry(_windowName, geo);
    }

    private int GetCurrentScreenIndex()
    {
        var screens = _window.Screens.All;
        var centerX = _window.Position.X + (int)(_window.Width / 2);
        var centerY = _window.Position.Y + (int)(_window.Height / 2);
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
}
