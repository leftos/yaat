using Avalonia.Controls;
using Yaat.Client.Services;

namespace Yaat.Client.Views;

/// <summary>
/// Adds a "Window → Always on Top" toggle to a window's <see cref="NativeMenu"/>,
/// which Avalonia renders in the macOS menu bar when the window is focused.
///
/// Cross-platform behavior:
/// <list type="bullet">
///   <item><description><b>macOS:</b> the toggle appears in the menu bar under "Window"
///     while this window is focused. Avalonia auto-generates the standard application
///     menu (About, Hide, Quit) so users still get the rest of the macOS menu bar.</description></item>
///   <item><description><b>Windows:</b> not attached. The Windows backend doesn't render
///     per-window <see cref="NativeMenu"/>; <see cref="WindowSystemMenuHelper"/> covers
///     discoverability via the title-bar system menu instead.</description></item>
///   <item><description><b>Linux:</b> not attached. Most window managers (Mutter/KWin/XFWM)
///     already render a native "Always on Top" item in the title-bar context menu via
///     <c>_NET_WM_STATE_ABOVE</c>, which Avalonia sets when <see cref="Window.Topmost"/>
///     changes.</description></item>
/// </list>
/// </summary>
public sealed class WindowNativeMenuHelper
{
    private readonly Window _window;
    private readonly WindowGeometryHelper _geometryHelper;
    private readonly UserPreferences _preferences;
    private readonly string _windowName;

    private NativeMenuItem? _alwaysOnTopItem;

    public WindowNativeMenuHelper(Window window, WindowGeometryHelper geometryHelper, UserPreferences preferences, string windowName)
    {
        _window = window;
        _geometryHelper = geometryHelper;
        _preferences = preferences;
        _windowName = windowName;
    }

    public void Attach()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        _alwaysOnTopItem = new NativeMenuItem("Always on Top") { ToggleType = MenuItemToggleType.CheckBox, IsChecked = _window.Topmost };
        _alwaysOnTopItem.Click += OnAlwaysOnTopClicked;

        var windowSubmenu = new NativeMenu();
        windowSubmenu.Items.Add(_alwaysOnTopItem);

        var windowRoot = new NativeMenuItem("Window") { Menu = windowSubmenu };

        var menu = new NativeMenu();
        menu.Items.Add(windowRoot);

        NativeMenu.SetMenu(_window, menu);

        _preferences.WindowTopmostChanged += OnWindowTopmostChanged;
        _window.Closed += OnWindowClosed;
    }

    private void OnAlwaysOnTopClicked(object? sender, EventArgs e)
    {
        _geometryHelper.ToggleTopmost();
    }

    private void OnWindowTopmostChanged(string windowName, bool isTopmost)
    {
        if (windowName != _windowName || _alwaysOnTopItem is null)
        {
            return;
        }

        _alwaysOnTopItem.IsChecked = isTopmost;
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _preferences.WindowTopmostChanged -= OnWindowTopmostChanged;
        _window.Closed -= OnWindowClosed;

        if (_alwaysOnTopItem is not null)
        {
            _alwaysOnTopItem.Click -= OnAlwaysOnTopClicked;
        }
    }
}
