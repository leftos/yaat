using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.Find;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Client.Views.Find;
using Yaat.Client.Views.Radar;
using Yaat.Client.Views.Settings;
using Yaat.Client.Views.VStrips;

namespace Yaat.Client.UI.Tests.Views;

// Coverage for the centralized window hotkeys (WindowHotkeys): the focus-command-input hotkey must
// fire from any working window — not just MainWindow — and route to whichever CommandInputView is
// visible; the always-on-top hotkey must toggle the focused window's topmost via IAlwaysOnTopToggle; each
// pop-out and favorites-bar hotkey toggles its flag on the main view model from the main window or a pop-out.
public class WindowHotkeysTests
{
    [AvaloniaFact]
    public void FocusKey_FromPopOutWindow_RaisesRequestCommandInputFocus()
    {
        WindowHotkeys.EnsureRegistered();
        var vm = new MainViewModel(new FakeFilePickerService());
        // A main-view-model-backed pop-out (Radar) — the default focus key is OemTilde.
        var window = new RadarViewWindow(vm.Preferences, "RadarView", "Radar View") { DataContext = vm };
        window.ShowAndRunLayout();

        bool raised = false;
        vm.RequestCommandInputFocus += () => raised = true;

        window.DispatchKey(Key.OemTilde);

        Assert.True(raised);
    }

    [AvaloniaFact]
    public void FocusKey_FromOutOfScopeWindow_DoesNotRaise()
    {
        WindowHotkeys.EnsureRegistered();
        var vm = new MainViewModel(new FakeFilePickerService());
        bool raised = false;
        vm.RequestCommandInputFocus += () => raised = true;

        // DataContext is not a MainViewModel and the window is not Strips/TDLS — out of scope.
        var window = new Window
        {
            DataContext = new object(),
            Width = 200,
            Height = 100,
        };
        window.ShowAndRunLayout();

        window.DispatchKey(Key.OemTilde);

        Assert.False(raised);
    }

    [AvaloniaFact]
    public void AlwaysOnTopKey_TogglesFocusedPopOutWindowTopmost()
    {
        WindowHotkeys.EnsureRegistered();
        var vm = new MainViewModel(new FakeFilePickerService());
        // Default always-on-top keybind is Ctrl+Shift+T.
        var window = new RadarViewWindow(vm.Preferences, "RadarView", "Radar View") { DataContext = vm };
        window.ShowAndRunLayout();

        Assert.False(window.Topmost);

        window.DispatchKey(Key.T, RawInputModifiers.Control | RawInputModifiers.Shift);

        Assert.True(window.Topmost);
    }

    [AvaloniaFact]
    public void CtrlF8_InExtraRadarWindow_TogglesThatWindowsDcb()
    {
        WindowHotkeys.EnsureRegistered();
        var vm = new MainViewModel(new FakeFilePickerService());
        try
        {
            vm.OpenExtraRadarView("KOAK");
            RadarViewInstance instance = vm.ExtraRadarViews.Single();
            var window = new RadarViewWindow(vm.Preferences, instance.GeometryKey, instance.Title) { DataContext = vm };
            window.SetViewModel(instance.Vm);
            window.ShowAndRunLayout();

            bool dockedBefore = vm.Radar.IsDcbVisible;
            bool extraBefore = instance.Vm.IsDcbVisible;

            window.DispatchKey(Key.F8, RawInputModifiers.Control);

            // The hotkey acts on the focused window's own view-model, never on the docked view.
            Assert.Equal(!extraBefore, instance.Vm.IsDcbVisible);
            Assert.Equal(dockedBefore, vm.Radar.IsDcbVisible);
        }
        finally
        {
            vm.ReconcileExtraViews([], []);
        }
    }

    // One panel hotkey: the keybind it reads and writes, and the main-view-model flag it toggles.
    private sealed record PanelHotkey(
        Func<UserPreferences, string> Keybind,
        Action<UserPreferences, string> Bind,
        Func<MainViewModel, bool> Get,
        Action<MainViewModel, bool> Set
    );

    private static readonly Dictionary<string, PanelHotkey> Panels = new()
    {
        ["AircraftList"] = new(
            p => p.PopOutAircraftListKey,
            (p, k) => p.SetPopOutAircraftListKey(k),
            vm => vm.IsDataGridPoppedOut,
            (vm, v) => vm.IsDataGridPoppedOut = v
        ),
        ["GroundView"] = new(
            p => p.PopOutGroundViewKey,
            (p, k) => p.SetPopOutGroundViewKey(k),
            vm => vm.IsGroundViewPoppedOut,
            (vm, v) => vm.IsGroundViewPoppedOut = v
        ),
        ["RadarView"] = new(
            p => p.PopOutRadarViewKey,
            (p, k) => p.SetPopOutRadarViewKey(k),
            vm => vm.IsRadarViewPoppedOut,
            (vm, v) => vm.IsRadarViewPoppedOut = v
        ),
        ["Terminal"] = new(
            p => p.PopOutTerminalKey,
            (p, k) => p.SetPopOutTerminalKey(k),
            vm => vm.IsTerminalPoppedOut,
            (vm, v) => vm.IsTerminalPoppedOut = v
        ),
        ["Controllers"] = new(
            p => p.PopOutControllersKey,
            (p, k) => p.SetPopOutControllersKey(k),
            vm => vm.IsControllersPoppedOut,
            (vm, v) => vm.IsControllersPoppedOut = v
        ),
        ["Metar"] = new(p => p.PopOutMetarKey, (p, k) => p.SetPopOutMetarKey(k), vm => vm.IsMetarPoppedOut, (vm, v) => vm.IsMetarPoppedOut = v),
        ["FavoritesBar"] = new(
            p => p.FavoritesBarKey,
            (p, k) => p.SetFavoritesBarKey(k),
            vm => vm.ShowFavoritesBar,
            (vm, v) => vm.ShowFavoritesBar = v
        ),
    };

    [AvaloniaTheory]
    [InlineData("AircraftList", "Ctrl+Shift+L")]
    [InlineData("GroundView", "Ctrl+Shift+G")]
    [InlineData("RadarView", "Ctrl+Shift+R")]
    [InlineData("Terminal", "Ctrl+Shift+E")]
    [InlineData("Controllers", "Ctrl+Shift+C")]
    [InlineData("Metar", "Ctrl+Shift+M")]
    [InlineData("FavoritesBar", "Ctrl+Shift+F")]
    public void PanelHotkey_DefaultKey_FromAPopOutWindow_TogglesItsFlag(string panel, string defaultKey)
    {
        using var scope = new PreferencesFileScope();
        WindowHotkeys.EnsureRegistered();
        var vm = new MainViewModel(new FakeFilePickerService());
        PanelHotkey hotkey = Panels[panel];
        Assert.Equal(defaultKey, hotkey.Keybind(UserPreferences.CreateDefaults()));
        hotkey.Bind(vm.Preferences, defaultKey);
        bool before = hotkey.Get(vm);
        var window = new RadarViewWindow(vm.Preferences, "RadarView", "Radar View") { DataContext = vm };
        window.ShowAndRunLayout();

        try
        {
            PressChord(window, defaultKey);
            Assert.Equal(!before, hotkey.Get(vm));

            PressChord(window, defaultKey);
            Assert.Equal(before, hotkey.Get(vm));
        }
        finally
        {
            hotkey.Set(vm, before);
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData("AircraftList")]
    [InlineData("GroundView")]
    [InlineData("RadarView")]
    [InlineData("Terminal")]
    [InlineData("Controllers")]
    [InlineData("Metar")]
    [InlineData("FavoritesBar")]
    public void PanelHotkey_FromTheMainWindow_TogglesItsFlag(string panel)
    {
        using var scope = new PreferencesFileScope();
        WindowHotkeys.EnsureRegistered();
        (MainWindow? main, MainViewModel? vm) = MainWindowHost.Boot();
        PanelHotkey hotkey = Panels[panel];
        bool before = hotkey.Get(vm);

        try
        {
            PressChord(main, hotkey.Keybind(vm.Preferences));
            Assert.Equal(!before, hotkey.Get(vm));

            PressChord(main, hotkey.Keybind(vm.Preferences));
            Assert.Equal(before, hotkey.Get(vm));
        }
        finally
        {
            hotkey.Set(vm, before);
            Dispatcher.UIThread.RunJobs();
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact]
    public void PanelHotkey_Rebound_FiresOnTheNewKeyOnly()
    {
        using var scope = new PreferencesFileScope();
        WindowHotkeys.EnsureRegistered();
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.Preferences.SetPopOutRadarViewKey("Ctrl+Alt+R");
        bool before = vm.IsRadarViewPoppedOut;
        var window = new RadarViewWindow(vm.Preferences, "RadarView", "Radar View") { DataContext = vm };
        window.ShowAndRunLayout();

        try
        {
            PressChord(window, "Ctrl+Shift+R");
            Assert.Equal(before, vm.IsRadarViewPoppedOut);

            PressChord(window, "Ctrl+Alt+R");
            Assert.Equal(!before, vm.IsRadarViewPoppedOut);
        }
        finally
        {
            vm.IsRadarViewPoppedOut = before;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void PanelHotkey_WithTheCommandInputFocused_FiresAndTypesNothing()
    {
        using var scope = new PreferencesFileScope();
        WindowHotkeys.EnsureRegistered();
        (MainWindow? main, MainViewModel? vm) = MainWindowHost.Boot();
        DockTerminal(main, vm);
        vm.Preferences.SetPopOutAircraftListKey("Ctrl+Shift+L");
        bool before = vm.IsDataGridPoppedOut;
        TextBox box = FindCommandInput(main)!;
        box.Text = "";
        box.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.True(box.IsFocused);

        try
        {
            main.DispatchKey(Key.L, RawInputModifiers.Control | RawInputModifiers.Shift);

            Assert.Equal(!before, vm.IsDataGridPoppedOut);
            Assert.True(string.IsNullOrEmpty(box.Text));
        }
        finally
        {
            vm.IsDataGridPoppedOut = before;
            Dispatcher.UIThread.RunJobs();
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact]
    public void FavoritesBarHotkey_InsideAStripsWindow_TogglesTheBar_AndDoesNotOpenFind()
    {
        using var scope = new PreferencesFileScope();
        WindowHotkeys.EnsureRegistered();
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.Preferences.SetFavoritesBarKey("Ctrl+Shift+F");
        bool before = vm.ShowFavoritesBar;
        (VStripsViewModel stripsVm, _) = VStripsViewInteractionTests.MakeVm();
        var view = new VStripsView { DataContext = stripsVm };
        // A real Strips window has no DataContext and resolves the app's MainWindow view model; the headless app has
        // no desktop lifetime, so the test hands the window the view model directly.
        var window = new VStripsViewWindow(vm.Preferences) { DataContext = vm, Content = view };
        window.ShowAndRunLayout();
        InputElement focusTarget = view.GetVisualDescendants().OfType<InputElement>().First(c => c.Focusable && c.IsEffectivelyVisible);
        focusTarget.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.True(view.IsKeyboardFocusWithin);
        var find = (FindController)view.GetVisualDescendants().OfType<FindBarView>().Single().DataContext!;
        Assert.False(find.IsVisible, "Find starts closed");

        try
        {
            window.DispatchKey(Key.F, RawInputModifiers.Control | RawInputModifiers.Shift);

            Assert.Equal(!before, vm.ShowFavoritesBar);
            Assert.False(find.IsVisible, "Ctrl+Shift+F opened Find");

            // Plain Ctrl+F still opens Find.
            focusTarget.Focus();
            window.DispatchKey(Key.F, RawInputModifiers.Control);

            Assert.True(find.IsVisible);
            Assert.Equal(!before, vm.ShowFavoritesBar);
        }
        finally
        {
            vm.ShowFavoritesBar = before;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void OpenSettingsKey_DefaultsToCtrlComma_AndFromAPopOutWindow_RequestsNoSection()
    {
        using var scope = new PreferencesFileScope();
        WindowHotkeys.EnsureRegistered();
        Assert.Equal("Ctrl+OemComma", UserPreferences.CreateDefaults().OpenSettingsKey);
        var vm = new MainViewModel(new FakeFilePickerService());
        List<SettingsSectionId?> requests = [];
        vm.SettingsRequested += requests.Add;
        var window = new RadarViewWindow(vm.Preferences, "RadarView", "Radar View") { DataContext = vm };
        window.ShowAndRunLayout();

        try
        {
            PressChord(window, "Ctrl+OemComma");

            Assert.Equal([null], requests);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void OpenSettingsKey_FromTheMainWindow_RequestsNoSection()
    {
        using var scope = new PreferencesFileScope();
        WindowHotkeys.EnsureRegistered();
        (MainWindow? main, MainViewModel? vm) = MainWindowHost.Boot();
        List<SettingsSectionId?> requests = [];
        vm.SettingsRequested += requests.Add;

        try
        {
            PressChord(main, vm.Preferences.OpenSettingsKey);

            Assert.Equal([null], requests);
        }
        finally
        {
            // The main window answers the request by opening Settings; the host closes it and hides the main window
            // rather than closing it, which would latch the process-wide shutdown flag for later tests.
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact]
    public void OpenSettingsKey_WithTheCommandInputFocused_RequestsNoSectionAndTypesNothing()
    {
        using var scope = new PreferencesFileScope();
        WindowHotkeys.EnsureRegistered();
        (MainWindow? main, MainViewModel? vm) = MainWindowHost.Boot();
        DockTerminal(main, vm);
        List<SettingsSectionId?> requests = [];
        vm.SettingsRequested += requests.Add;
        TextBox box = FindCommandInput(main)!;
        box.Text = "";
        box.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.True(box.IsFocused);

        try
        {
            main.DispatchKey(Key.OemComma, RawInputModifiers.Control);

            Assert.Equal([null], requests);
            Assert.True(string.IsNullOrEmpty(box.Text));
        }
        finally
        {
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact]
    public void OpenSettingsKey_IsARebindableWindowHotkey_ThatReportsAClash()
    {
        using var scope = new PreferencesFileScope();
        KeybindDescriptor descriptor = SettingsViewModel.KeybindDescriptors.Single(d => d.Id == "OpenSettings");
        Assert.Equal("Open Settings", descriptor.Name);
        Assert.Equal(KeybindKind.WindowHotkey, descriptor.Kind);
        Assert.Equal("Ctrl+OemComma", descriptor.Read(UserPreferences.CreateDefaults()));

        var settings = new SettingsViewModel();
        KeybindRow takeControl = settings.KeybindRows.Single(r => r.Id == "TakeControl");
        takeControl.StartCaptureCommand.Execute(null);
        settings.CaptureKey(Key.OemComma, KeyModifiers.Control);

        Assert.True(settings.HasKeybindClash);
        Assert.Equal("Also used by Open Settings", takeControl.ClashMessage);
        Assert.Equal("Also used by Take control", settings.KeybindRows.Single(r => r.Id == "OpenSettings").ClashMessage);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void OpenSettingsKey_WithSettingsClosed_OpensAtGeneral()
    {
        using var scope = new PreferencesFileScope();
        WindowHotkeys.EnsureRegistered();
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();

        try
        {
            PressChord(main, vm.Preferences.OpenSettingsKey);

            SettingsWindow dialog = Assert.Single(main.OwnedWindows.OfType<SettingsWindow>());
            Assert.IsType<GeneralSection>(ShownSection(dialog));
        }
        finally
        {
            MainWindowHost.CloseAll(main);
        }
    }

    private static object? ShownSection(SettingsWindow dialog) => dialog.FindControl<ContentControl>("SectionHost")!.Content;

    // Raises the chord's KeyDown on the window, where the WindowHotkeys class handler listens.
    private static void PressChord(Window window, string keybind)
    {
        Assert.True(KeybindHelper.ParseKeybind(keybind, out Key key, out KeyModifiers modifiers), keybind);
        window.RaiseEvent(
            new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Source = window,
                Key = key,
                KeyModifiers = modifiers,
            }
        );
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void DockedTerminal_FocusRequest_FocusesEmbeddedCommandInput()
    {
        using var scope = new PreferencesFileScope();
        (MainWindow? main, MainViewModel? vm) = MainWindowHost.Boot();
        DockTerminal(main, vm);

        try
        {
            vm.FocusCommandInput();
            Dispatcher.UIThread.RunJobs();

            TextBox? box = FindCommandInput(main);
            Assert.NotNull(box);
            Assert.True(box!.IsFocused);
        }
        finally
        {
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact]
    public void PoppedTerminal_FocusRequest_FocusesTerminalWindowCommandInput()
    {
        using var scope = new PreferencesFileScope();
        (MainWindow? main, MainViewModel? vm) = MainWindowHost.Boot();

        try
        {
            vm.IsTerminalPoppedOut = true;
            Dispatcher.UIThread.RunJobs();

            Assert.NotNull(main.TerminalWindow);
            main.TerminalWindow!.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            vm.FocusCommandInput();
            Dispatcher.UIThread.RunJobs();

            TextBox? termBox = FindCommandInput(main.TerminalWindow!);
            Assert.NotNull(termBox);
            Assert.True(termBox!.IsFocused);

            // The hidden embedded input must not have stolen focus.
            TextBox? embedded = FindCommandInput(main);
            Assert.False(embedded?.IsFocused ?? false);
        }
        finally
        {
            // Re-dock in memory as well as in the scoped file: WindowGeometryHelper.FlushAllSavedGeometries writes every
            // live window's preferences back on teardown, which would leave the terminal popped out for later tests.
            vm.IsTerminalPoppedOut = false;
            Dispatcher.UIThread.RunJobs();
            MainWindowHost.CloseAll(main);
        }
    }

    // Docks the terminal and settles the layout: the command-input paths below open a flyout or focus the input that is
    // showing, and the window boots with whatever pop-out state the shared preferences.json holds.
    private static void DockTerminal(MainWindow main, MainViewModel vm)
    {
        vm.IsTerminalPoppedOut = false;
        Dispatcher.UIThread.RunJobs();
        main.UpdateLayout();
        MainWindowHost.Pump();
    }

    private static TextBox? FindCommandInput(Window window) =>
        window.FindControl<CommandInputView>("CommandInputView")?.FindControl<TextBox>("CommandInput");
}
