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
/// Centralizes the "focus the command input" hotkey so it fires from any YAAT working window —
/// MainWindow, the main pop-outs (Radar, Ground, Aircraft List, Controllers, METAR, Terminal,
/// Favorites) and the per-facility Flight Strips / TDLS windows — not just MainWindow. Registers a
/// single Avalonia class handler on <see cref="InputElement.KeyDownEvent"/> for every
/// <see cref="Window"/>, so new pop-out windows are covered without per-window wiring.
///
/// This is an in-app shortcut (it only fires while a YAAT window has focus), deliberately unlike
/// the system-wide PTT hook. Modal dialogs (Settings and friends) are out of scope so the hotkey
/// can't interfere with the Settings keybind-capture.
/// </summary>
internal static class FocusInputHotkey
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
        if (e.Handled || !IsInScope(window))
        {
            return;
        }

        var vm = ResolveMainViewModel(window);
        if (vm is null)
        {
            return;
        }

        if (KeybindHelper.ParseKeybind(vm.Preferences.FocusInputKey, out var key, out var mods) && e.Key == key && e.KeyModifiers == mods)
        {
            vm.FocusCommandInput();
            e.Handled = true;
        }
    }

    /// <summary>
    /// True for the YAAT working windows the hotkey should reach. MainWindow and the main pop-outs
    /// share the single <see cref="MainViewModel"/> as their DataContext; the Strips/TDLS windows
    /// carry a per-facility view-model, so they're named explicitly. Everything else (modal dialogs)
    /// is excluded.
    /// </summary>
    private static bool IsInScope(Window window) => window.DataContext is MainViewModel || window is VStripsViewWindow or VTdlsViewWindow;

    /// <summary>
    /// Resolves the single application <see cref="MainViewModel"/>. The focused window's DataContext
    /// is used directly for MainViewModel-backed windows; Strips/TDLS windows fall back to the app's
    /// MainWindow, which always carries the one VM.
    /// </summary>
    private static MainViewModel? ResolveMainViewModel(Window window) =>
        window.DataContext as MainViewModel
        ?? (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.DataContext as MainViewModel;
}
