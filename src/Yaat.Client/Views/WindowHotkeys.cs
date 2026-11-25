using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.VStrips;
using Yaat.Client.Views.VTdls;

namespace Yaat.Client.Views;

/// <summary>
/// Centralizes app-wide window-level hotkeys so they fire from any YAAT working window — MainWindow,
/// the main pop-outs (the Scope, Ground, Aircraft List, Controllers, METAR, Terminal, Favorites) and the
/// per-facility Flight Strips / TDLS windows — instead of each window wiring them in its own
/// <c>OnKeyDown</c>. A single Avalonia class handler on <see cref="InputElement.KeyDownEvent"/> covers
/// every <see cref="Window"/>, so new pop-out windows are covered without per-window code.
///
/// Handles:
/// <list type="bullet">
/// <item><b>Focus command input</b> — focuses whichever command input is visible (docked or popped-out
/// terminal). Scoped to <see cref="MainViewModel"/>-backed windows plus Strips/TDLS and the Scope window.</item>
/// <item><b>Always on top</b> — toggles the focused window's topmost state via
/// <see cref="IAlwaysOnTopToggle"/>.</item>
/// <item><b>Panel toggles</b> — the pop-out keys (aircraft list, ground, Scope, terminal, controllers, METAR)
/// and the favorites-bar key flip their flag on the <see cref="MainViewModel"/>, the same flag the View menu's
/// checkable item binds. Scoped to YAAT working windows.</item>
/// <item><b>Open Settings</b> — Ctrl+, by default asks the <see cref="MainViewModel"/> for Settings with no section of its
/// own: an open Settings window comes to the front on the section it is on, and a closed one opens at General. Scoped to
/// YAAT working windows, so it does nothing inside Settings itself.</item>
/// </list>
///
/// These are in-app shortcuts (they only fire while a YAAT window has focus), deliberately unlike the
/// system-wide PTT hook. Modal dialogs (Settings and friends) are out of scope so the hotkeys can't
/// interfere with the Settings keybind-capture.
/// </summary>
internal static class WindowHotkeys
{
    private const string MeasureChord = "Ctrl+M";

    /// <summary>The Notes window's own key for the stamp, handled by that window alone.</summary>
    internal const string NotesStampChord = "Ctrl+Shift+N";

    /// <summary>
    /// The chords bound in code rather than in Settings (here, and Find in the Strips and vTDLS views, the ground view's
    /// debug overlay and the Notes window's stamp), by the action name Settings shows when a configured key would take one
    /// of them.
    /// </summary>
    internal static readonly IReadOnlyList<(string Name, string Chord)> FixedChords =
    [
        ("Measure tool", MeasureChord),
        ("Strips and vTDLS find", "Ctrl+F"),
        ("Notes find", "Ctrl+F"),
        ("Notes replace", "Ctrl+H"),
        ("Ground view debug overlay", "Ctrl+D"),
        ("Notes stamp", NotesStampChord),
    ];

    // Each configurable key scoped to YAAT working windows and what it does to the main view model: the panel keys flip the
    // flag the View menu binds, and the Open Settings key asks for Settings with no section of its own.
    private static readonly (Func<UserPreferences, string> Keybind, Action<MainViewModel> Action)[] ScopedKeyActions =
    [
        (p => p.PopOutAircraftListKey, vm => vm.IsDataGridPoppedOut = !vm.IsDataGridPoppedOut),
        (p => p.PopOutGroundViewKey, vm => vm.IsGroundViewPoppedOut = !vm.IsGroundViewPoppedOut),
        (p => p.PopOutRadarViewKey, vm => vm.IsScopePoppedOut = !vm.IsScopePoppedOut),
        (p => p.PopOutTerminalKey, vm => vm.IsTerminalPoppedOut = !vm.IsTerminalPoppedOut),
        (p => p.PopOutControllersKey, vm => vm.IsControllersPoppedOut = !vm.IsControllersPoppedOut),
        (p => p.PopOutMetarKey, vm => vm.IsMetarPoppedOut = !vm.IsMetarPoppedOut),
        (p => p.FavoritesBarKey, vm => vm.ShowFavoritesBar = !vm.ShowFavoritesBar),
        (p => p.OpenSettingsKey, vm => vm.RequestSettings(section: null)),
    ];

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
        MainViewModel? vm = ResolveMainViewModel(window);
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

        // Ctrl+M arms the distance measuring tool. Fixed binding, and deliberately global: the tool is
        // shared by the ground and Scope views, so it arms wherever the instructor clicks next. The
        // Scope arms it from its own key handler (ScopeView), which takes the key first where the Scope has the focus.
        if (IsFocusInputScope(window) && Matches(MeasureChord, e))
        {
            vm.Measure.Arm();
            e.Handled = true;
            return;
        }

        if (IsFocusInputScope(window))
        {
            RunScopedKeyAction(vm, e);
        }
    }

    private static void RunScopedKeyAction(MainViewModel vm, KeyEventArgs e)
    {
        foreach ((Func<UserPreferences, string> keybind, Action<MainViewModel> action) in ScopedKeyActions)
        {
            if (Matches(keybind(vm.Preferences), e))
            {
                action(vm);
                e.Handled = true;
                return;
            }
        }
    }

    private static bool Matches(string keybind, KeyEventArgs e) =>
        KeybindHelper.ParseKeybind(keybind, out Key key, out KeyModifiers mods) && e.Key == key && e.KeyModifiers == mods;

    /// <summary>
    /// True for the windows the focus-input hotkey should reach. MainWindow and the main pop-outs
    /// share the single <see cref="MainViewModel"/> as their DataContext; the Strips/TDLS windows
    /// carry a per-facility view-model and the Scope window its Scope's, so they're named explicitly.
    /// Everything else (modal dialogs) is excluded.
    /// </summary>
    private static bool IsFocusInputScope(Window window) =>
        (window.DataContext is MainViewModel) || (window is VStripsViewWindow or VTdlsViewWindow or ScopeWindow);

    /// <summary>
    /// Resolves the single application <see cref="MainViewModel"/>. The focused window's DataContext
    /// is used directly for MainViewModel-backed windows; other windows fall back to the app's
    /// MainWindow, which always carries the one VM.
    /// </summary>
    internal static MainViewModel? ResolveMainViewModel(TopLevel window) =>
        window.DataContext as MainViewModel
        ?? (window.DataContext as ScopeViewModel)?.Main
        ?? (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.DataContext as MainViewModel;
}
