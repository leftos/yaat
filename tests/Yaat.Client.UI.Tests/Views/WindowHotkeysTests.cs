using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Client.Views.Radar;

namespace Yaat.Client.UI.Tests.Views;

// Coverage for the centralized window hotkeys (WindowHotkeys): the focus-command-input hotkey must
// fire from any working window — not just MainWindow — and route to whichever CommandInputView is
// visible; the always-on-top hotkey must toggle the focused window's topmost via IAlwaysOnTopToggle.
public class WindowHotkeysTests
{
    [AvaloniaFact]
    public void FocusKey_FromPopOutWindow_RaisesRequestCommandInputFocus()
    {
        WindowHotkeys.EnsureRegistered();
        var vm = new MainViewModel(new FakeFilePickerService());
        // A main-view-model-backed pop-out (Radar) — the default focus key is OemTilde.
        var window = new RadarViewWindow(vm.Preferences) { DataContext = vm };
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
        var window = new RadarViewWindow(vm.Preferences) { DataContext = vm };
        window.ShowAndRunLayout();

        Assert.False(window.Topmost);

        window.DispatchKey(Key.T, RawInputModifiers.Control | RawInputModifiers.Shift);

        Assert.True(window.Topmost);
    }

    [AvaloniaFact]
    public void DockedTerminal_FocusRequest_FocusesEmbeddedCommandInput()
    {
        var (main, vm) = BootMainWindow();
        vm.IsTerminalDocked = true;
        Dispatcher.UIThread.RunJobs();

        vm.FocusCommandInput();
        Dispatcher.UIThread.RunJobs();

        var box = FindCommandInput(main);
        Assert.NotNull(box);
        Assert.True(box!.IsFocused);
    }

    [AvaloniaFact]
    public void PoppedTerminal_FocusRequest_FocusesTerminalWindowCommandInput()
    {
        var (main, vm) = BootMainWindow();
        vm.IsTerminalDocked = false;
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(main.TerminalWindow);
        main.TerminalWindow!.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        vm.FocusCommandInput();
        Dispatcher.UIThread.RunJobs();

        var termBox = FindCommandInput(main.TerminalWindow!);
        Assert.NotNull(termBox);
        Assert.True(termBox!.IsFocused);

        // The hidden embedded input must not have stolen focus.
        var embedded = FindCommandInput(main);
        Assert.False(embedded?.IsFocused ?? false);
    }

    private static (MainWindow main, MainViewModel vm) BootMainWindow()
    {
        var main = new MainWindow();
        main.Show();
        Dispatcher.UIThread.RunJobs();
        main.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return (main, (MainViewModel)main.DataContext!);
    }

    private static TextBox? FindCommandInput(Window window) =>
        window.FindControl<CommandInputView>("CommandInputView")?.FindControl<TextBox>("CommandInput");
}
