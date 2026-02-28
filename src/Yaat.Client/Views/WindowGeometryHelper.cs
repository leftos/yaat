using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
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
        var geo = _windowName switch
        {
            "Main" => _preferences.MainWindowGeometry,
            "Settings" => _preferences.SettingsWindowGeometry,
            _ => null,
        };

        if (geo is not null && IsVisibleOnAnyScreen(geo.X, geo.Y, geo.Width, geo.Height))
        {
            _window.Width = geo.Width;
            _window.Height = geo.Height;
            _window.Position = new PixelPoint(geo.X, geo.Y);

            if (geo.IsMaximized)
            {
                _window.WindowState = WindowState.Maximized;
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
        };

        _preferences.SetWindowGeometry(_windowName, geo);
    }

    private bool IsVisibleOnAnyScreen(int x, int y, double w, double h)
    {
        var screens = _window.Screens.All;
        if (screens.Count == 0)
        {
            return false;
        }

        foreach (var screen in screens)
        {
            var bounds = screen.WorkingArea;
            var overlapX = x + w > bounds.X && x < bounds.Right;
            var overlapY = y + h > bounds.Y && y < bounds.Bottom;
            if (overlapX && overlapY)
            {
                return true;
            }
        }

        return false;
    }
}
