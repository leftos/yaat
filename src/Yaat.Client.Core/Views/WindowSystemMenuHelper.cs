using System.Runtime.InteropServices;
using Avalonia.Controls;
using Yaat.Client.Services;

namespace Yaat.Client.Views;

/// <summary>
/// Adds an "Always on Top" item to the Windows system menu (the menu shown when
/// right-clicking the title bar, clicking the window icon, or pressing Alt+Space).
/// Toggling the item flips the same per-window topmost state used by the Settings
/// checkbox and the global Always-on-Top hotkey, via <see cref="WindowGeometryHelper.ToggleTopmost"/>.
///
/// Cross-platform behavior:
/// <list type="bullet">
///   <item><description><b>Windows:</b> a custom menu item with checkmark is injected.</description></item>
///   <item><description><b>Linux:</b> no-op. Most window managers (Mutter/KWin/XFWM) already
///     render a native "Always on Top" item that toggles <c>_NET_WM_STATE_ABOVE</c>, which
///     Avalonia sets when <see cref="Window.Topmost"/> changes — so the WM-native menu
///     reflects the same state without any application work.</description></item>
///   <item><description><b>macOS:</b> no-op. macOS has no equivalent per-window system menu.</description></item>
/// </list>
/// </summary>
public sealed class WindowSystemMenuHelper
{
    private const uint MF_BYCOMMAND = 0x0000_0000;
    private const uint MF_BYPOSITION = 0x0000_0400;
    private const uint MF_STRING = 0x0000_0000;
    private const uint MF_SEPARATOR = 0x0000_0800;
    private const uint MF_CHECKED = 0x0000_0008;
    private const uint MF_UNCHECKED = 0x0000_0000;
    private const uint WM_SYSCOMMAND = 0x0112;
    private const uint SC_CLOSE = 0xF060;

    // Custom system-menu command ID. Must be < 0xF000 to avoid colliding with SC_*
    // values (SC_CLOSE, SC_MINIMIZE, SC_MAXIMIZE, SC_RESTORE, SC_MOVE, SC_SIZE, etc.).
    private const uint SC_ALWAYS_ON_TOP = 0x1000;

    private static readonly UIntPtr SubclassId = new(0xA70_A70Au);

    private readonly Window _window;
    private readonly WindowGeometryHelper _geometryHelper;
    private readonly UserPreferences _preferences;
    private readonly string _windowName;

    private SubclassProc? _subclassProc;
    private IntPtr _hwnd;
    private IntPtr _systemMenu;
    private bool _menuInstalled;

    public WindowSystemMenuHelper(Window window, WindowGeometryHelper geometryHelper, UserPreferences preferences, string windowName)
    {
        _window = window;
        _geometryHelper = geometryHelper;
        _preferences = preferences;
        _windowName = windowName;
    }

    public void Attach()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (_window.IsVisible)
        {
            InstallMenu();
        }
        else
        {
            _window.Opened += OnWindowOpened;
        }

        _window.Closed += OnWindowClosed;
        _preferences.WindowTopmostChanged += OnWindowTopmostChanged;
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        _window.Opened -= OnWindowOpened;
        InstallMenu();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _preferences.WindowTopmostChanged -= OnWindowTopmostChanged;
        _window.Closed -= OnWindowClosed;
        _window.Opened -= OnWindowOpened;

        if (_menuInstalled && _hwnd != IntPtr.Zero && _subclassProc is not null)
        {
            RemoveWindowSubclass(_hwnd, _subclassProc, SubclassId);
        }

        _subclassProc = null;
        _menuInstalled = false;
    }

    private void OnWindowTopmostChanged(string windowName, bool isTopmost)
    {
        if (windowName != _windowName || !_menuInstalled || _systemMenu == IntPtr.Zero)
        {
            return;
        }

        CheckMenuItem(_systemMenu, SC_ALWAYS_ON_TOP, MF_BYCOMMAND | (isTopmost ? MF_CHECKED : MF_UNCHECKED));
    }

    private void InstallMenu()
    {
        var handle = _window.TryGetPlatformHandle();
        if (handle is null || handle.Handle == IntPtr.Zero)
        {
            return;
        }

        _hwnd = handle.Handle;
        _systemMenu = GetSystemMenu(_hwnd, false);
        if (_systemMenu == IntPtr.Zero)
        {
            return;
        }

        // Insert separator + "Always on Top" just before the existing Close item so the
        // new entry sits above Close (the conventional bottom of the system menu).
        InsertMenu(_systemMenu, SC_CLOSE, MF_BYCOMMAND | MF_SEPARATOR, UIntPtr.Zero, null);
        InsertMenu(_systemMenu, SC_CLOSE, MF_BYCOMMAND | MF_STRING, new UIntPtr(SC_ALWAYS_ON_TOP), "Always on Top");

        CheckMenuItem(_systemMenu, SC_ALWAYS_ON_TOP, MF_BYCOMMAND | (_window.Topmost ? MF_CHECKED : MF_UNCHECKED));

        // Keep the delegate as an instance field so the GC doesn't collect it while
        // unmanaged code holds a function pointer to it.
        _subclassProc = SubclassProcImpl;
        SetWindowSubclass(_hwnd, _subclassProc, SubclassId, UIntPtr.Zero);

        _menuInstalled = true;
    }

    private IntPtr SubclassProcImpl(IntPtr hWnd, uint uMsg, UIntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData)
    {
        if (uMsg == WM_SYSCOMMAND)
        {
            // Low 4 bits of wParam are reserved by the system; mask them off when comparing.
            var cmd = (uint)wParam.ToUInt64() & 0xFFF0;
            if (cmd == SC_ALWAYS_ON_TOP)
            {
                _geometryHelper.ToggleTopmost();
                return IntPtr.Zero;
            }
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, UIntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetSystemMenu(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool bRevert);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InsertMenu(IntPtr hMenu, uint uPosition, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint CheckMenuItem(IntPtr hMenu, uint uIDCheckItem, uint uCheck);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, UIntPtr wParam, IntPtr lParam);
}
