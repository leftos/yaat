using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.VStrips;
using Yaat.Client.Views.VTdls;

namespace Yaat.Client.Views;

/// <summary>
/// Centralizes app-wide window-level hotkeys so they fire from any YAAT working window — MainWindow,
/// the main pop-outs (Radar, Ground, Aircraft List, Controllers, METAR, Terminal, Favorites) and the
/// per-facility Flight Strips / TDLS windows — instead of each window wiring them in its own
/// <c>OnKeyDown</c>. A single Avalonia class handler on <see cref="InputElement.KeyDownEvent"/> covers
/// every <see cref="Window"/>, so new pop-out windows are covered without per-window code.
///
/// Handles:
/// <list type="bullet">
/// <item><b>Focus command input</b> — focuses whichever command input is visible (docked or popped-out
/// terminal). Scoped to <see cref="MainViewModel"/>-backed windows plus Strips/TDLS.</item>
/// <item><b>Always on top</b> — toggles the focused window's topmost state via
/// <see cref="IAlwaysOnTopToggle"/>.</item>
/// <item><b>Toggle DCB</b> — Ctrl+F8 shows/hides the radar Display Control Bar, mirroring CRC's
/// <c>StarsSpecialKey.Dcb</c>. Fixed binding, scoped to YAAT working windows.</item>
/// </list>
///
/// These are in-app shortcuts (they only fire while a YAAT window has focus), deliberately unlike the
/// system-wide PTT hook. Modal dialogs (Settings and friends) are out of scope so the hotkeys can't
/// interfere with the Settings keybind-capture.
/// </summary>
internal static class WindowHotkeys
{
    private static bool _registered;

    /// <summary>
    /// Registers the global class handler once per process. Idempotent — safe to call from both
    /// <c>App.OnFrameworkInitializationCompleted</c> and test setup.
    /// </summary>
    public static void EnsureRegistered()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        InputElement.KeyDownEvent.AddClassHandler<Window>(OnWindowKeyDown, RoutingStrategies.Bubble);
    }

    private static void OnWindowKeyDown(Window window, KeyEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        // Both hotkeys read their keybind from the single application preferences, resolved via the
        // one MainViewModel. Strips/TDLS windows carry their own VM, so resolution falls back to the
        // app MainWindow.
        var vm = ResolveMainViewModel(window);
        if (vm is null)
        {
            return;
        }

        if (IsFocusInputScope(window) && Matches(vm.Preferences.FocusInputKey, e))
        {
            vm.FocusCommandInput();
            e.Handled = true;
            return;
        }

        if (window is IAlwaysOnTopToggle toggle && Matches(vm.Preferences.AlwaysOnTopKey, e))
        {
            toggle.ToggleAlwaysOnTop();
            e.Handled = true;
            return;
        }

        // Ctrl+F8 toggles the radar Display Control Bar (DCB), mirroring CRC's StarsSpecialKey.Dcb.
        // Fixed (non-configurable) binding; the focus-input scope guard keeps it off modal dialogs.
        if (IsFocusInputScope(window) && (e.Key == Key.F8) && (e.KeyModifiers == KeyModifiers.Control))
        {
            vm.Radar.ToggleDcbVisibleCommand.Execute(null);
            e.Handled = true;
        }
    }

    private static bool Matches(string keybind, KeyEventArgs e) =>
        KeybindHelper.ParseKeybind(keybind, out var key, out var mods) && e.Key == key && e.KeyModifiers == mods;

    /// <summary>
    /// True for the windows the focus-input hotkey should reach. MainWindow and the main pop-outs
    /// share the single <see cref="MainViewModel"/> as their DataContext; the Strips/TDLS windows
    /// carry a per-facility view-model, so they're named explicitly. Everything else (modal dialogs)
    /// is excluded.
    /// </summary>
    private static bool IsFocusInputScope(Window window) => window.DataContext is MainViewModel || window is VStripsViewWindow or VTdlsViewWindow;

    /// <summary>
    /// Resolves the single application <see cref="MainViewModel"/>. The focused window's DataContext
    /// is used directly for MainViewModel-backed windows; other windows fall back to the app's
    /// MainWindow, which always carries the one VM.
    /// </summary>
    private static MainViewModel? ResolveMainViewModel(Window window) =>
        window.DataContext as MainViewModel
        ?? (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.DataContext as MainViewModel;
}
