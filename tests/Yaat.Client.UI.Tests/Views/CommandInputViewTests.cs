using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

// Widget-level coverage for CommandInputView.axaml.cs — specifically the KeyDown
// handler wired in OnLoaded (lines 85-199) that unit-level CommandInputController
// tests can't reach because the handler lives in code-behind.
public class CommandInputViewTests
{
    [AvaloniaFact]
    public void Escape_WithNoPopup_ClearsCommandTextAndSelection()
    {
        (Window? window, CommandInputView? view, MainViewModel? vm) = SetupInputView();
        vm.CommandText = "abc";

        TextBox textBox = FindCommandInput(view);
        textBox.Focus();
        Helpers.HeadlessWindowExtensions.PumpDispatcher();

        window.DispatchKey(Key.Escape);

        Assert.Equal("", vm.CommandText);
        Assert.Null(vm.SelectedAircraft);
    }

    [AvaloniaFact]
    public void Up_WithNoPopup_WalksHistoryBackwards()
    {
        (Window? window, CommandInputView? view, MainViewModel? vm) = SetupInputView();
        // Populate history (CommandHistory is ObservableCollection<CommandHistoryEntry>, newest at
        // index 0). No aircraft is selected, so untargeted (empty-callsign) entries recall for all.
        vm.CommandHistory.Insert(0, new CommandHistoryEntry("", "OLDER"));
        vm.CommandHistory.Insert(0, new CommandHistoryEntry("", "NEWER"));

        TextBox textBox = FindCommandInput(view);
        textBox.Focus();
        Helpers.HeadlessWindowExtensions.PumpDispatcher();

        window.DispatchKey(Key.Up);
        Assert.Equal("NEWER", vm.CommandText);

        window.DispatchKey(Key.Up);
        Assert.Equal("OLDER", vm.CommandText);
    }

    [AvaloniaFact]
    public void AircraftSelectKey_ResolvesCallsignAndClearsInput()
    {
        (Window? window, CommandInputView? view, MainViewModel? vm) = SetupInputView();
        vm.Aircraft.Add(new AircraftModel { Callsign = "UAL123" });
        // Default aircraft-select keybind is Key.Add (NumPad +) with no modifiers.
        vm.CommandText = "UAL";

        TextBox textBox = FindCommandInput(view);
        textBox.Focus();
        Helpers.HeadlessWindowExtensions.PumpDispatcher();

        window.DispatchKey(Key.Add);

        Assert.NotNull(vm.SelectedAircraft);
        Assert.Equal("UAL123", vm.SelectedAircraft!.Callsign);
        Assert.Equal("", vm.CommandText);
    }

    [AvaloniaFact]
    public void Escape_WithSignatureHelpVisible_OnlyDismissesSignatureHelp()
    {
        (Window? window, CommandInputView? view, MainViewModel? vm) = SetupInputView();
        vm.CommandText = "typed";
        // SignatureHelpState exposes IsVisible as an ObservableProperty, so we can
        // flip it directly without building a full CommandSignatureSet just to test
        // that the Escape branch in CommandInputView preserves CommandText while
        // SignatureHelp is open.
        vm.CommandInput.SignatureHelp.IsVisible = true;

        TextBox textBox = FindCommandInput(view);
        textBox.Focus();
        Helpers.HeadlessWindowExtensions.PumpDispatcher();

        window.DispatchKey(Key.Escape);

        Assert.False(vm.CommandInput.SignatureHelp.IsVisible);
        Assert.Equal("typed", vm.CommandText);
    }

    [AvaloniaFact]
    public void SessionFlyoutClosed_FlushesPendingValue()
    {
        // The fake clock keeps the debounce from firing on its own while the flyout is opening and closing.
        var vm = new MainViewModel(new FakeFilePickerService(), new ManualTimeProvider()) { ActiveScenarioName = "Test" };
        var view = new CommandInputView { DataContext = vm };
        var window = new Window
        {
            Width = 600,
            Height = 400,
            Content = view,
        };
        window.ShowAndRunLayout();
        var commits = new List<SessionSettingKey>();
        vm.SessionCommitter.Committed += commits.Add;

        vm.SessionCommandRunDelayMinSeconds = 15;
        Assert.True(vm.SessionCommitter.IsPending(SessionSettingKey.RunDelay));
        Assert.Empty(commits);

        Button button = view.FindControl<Button>("SessionSettingsButton")!;
        Flyout flyout = Assert.IsType<Flyout>(button.Flyout);
        flyout.ShowAt(button);
        HeadlessWindowExtensions.PumpDispatcher();
        flyout.Hide();
        HeadlessWindowExtensions.PumpDispatcher();

        Assert.Equal<SessionSettingKey>([SessionSettingKey.RunDelay], commits);
        Assert.False(vm.SessionCommitter.IsPending(SessionSettingKey.RunDelay));
    }

    [AvaloniaFact]
    public void EnterInSessionBox_FlushesItsKey()
    {
        var clock = new ManualTimeProvider();
        (Window window, CommandInputView view, MainViewModel vm, Flyout flyout) = OpenSessionFlyout(clock);
        var commits = new List<SessionSettingKey>();
        vm.SessionCommitter.Committed += commits.Add;
        vm.SessionCommandRunDelayMinSeconds = 15;

        FocusSessionBox(view, "SessionCommandRunDelayMinBox");
        window.DispatchKey(Key.Enter);

        // The flyout must still be open: an Enter that closed the flyout, and so flushed on close, would not prove the box's own handler.
        Assert.True(flyout.IsOpen);
        Assert.Equal<SessionSettingKey>([SessionSettingKey.RunDelay], commits);
    }

    [AvaloniaFact]
    public void LeavingSessionBox_FlushesItsKey()
    {
        var clock = new ManualTimeProvider();
        (_, CommandInputView view, MainViewModel vm, Flyout flyout) = OpenSessionFlyout(clock);
        var commits = new List<SessionSettingKey>();
        vm.SessionCommitter.Committed += commits.Add;
        vm.SessionCommandRunDelayMinSeconds = 15;

        FocusSessionBox(view, "SessionCommandRunDelayMinBox");
        TextBox commandInput = FindCommandInput(view);
        commandInput.Focus();
        HeadlessWindowExtensions.PumpDispatcher();

        // The flyout must still be open: a close-time flush would not prove the box's own focus-loss handler.
        Assert.True(flyout.IsOpen);
        Assert.Equal<SessionSettingKey>([SessionSettingKey.RunDelay], commits);
    }

    [AvaloniaFact]
    public void SpinnerStepInSessionBox_CommitsAfterTheStepDelay()
    {
        var clock = new ManualTimeProvider();
        (Window window, CommandInputView view, MainViewModel vm, _) = OpenSessionFlyout(clock);
        var commits = new List<SessionSettingKey>();
        vm.SessionCommitter.Committed += commits.Add;
        vm.SessionCommandRunDelayMaxSeconds = 3;

        FocusSessionBox(view, "SessionCommandRunDelayMaxBox");
        window.DispatchKey(Key.Up);

        Assert.Equal(4, vm.SessionCommandRunDelayMaxSeconds);
        Assert.Empty(commits);

        clock.Advance(TimeSpan.FromMilliseconds(199));
        HeadlessWindowExtensions.PumpDispatcher();
        Assert.Empty(commits);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        HeadlessWindowExtensions.PumpDispatcher();
        Assert.Equal<SessionSettingKey>([SessionSettingKey.RunDelay], commits);
    }

    private static (Window Window, CommandInputView View, MainViewModel Vm, Flyout Flyout) OpenSessionFlyout(ManualTimeProvider clock)
    {
        var vm = new MainViewModel(new FakeFilePickerService(), clock) { ActiveScenarioName = "Test" };
        var view = new CommandInputView { DataContext = vm };
        var window = new Window
        {
            Width = 600,
            Height = 400,
            Content = view,
        };
        window.ShowAndRunLayout();

        Button button = view.FindControl<Button>("SessionSettingsButton")!;
        Flyout flyout = Assert.IsType<Flyout>(button.Flyout);
        flyout.ShowAt(button);
        HeadlessWindowExtensions.PumpDispatcher();
        window.UpdateLayout();
        return (window, view, vm, flyout);
    }

    private static void FocusSessionBox(CommandInputView view, string name)
    {
        NumericUpDown box = Assert.IsType<NumericUpDown>(view.FindControl<NumericUpDown>(name));
        box.Focus();
        HeadlessWindowExtensions.PumpDispatcher();
        if (box.IsKeyboardFocusWithin)
        {
            return;
        }

        // The box itself is not focusable: its inner text field is where the keystrokes land.
        TextBox textBox = Assert.IsType<TextBox>(box.GetVisualDescendants().OfType<TextBox>().FirstOrDefault());
        textBox.Focus();
        HeadlessWindowExtensions.PumpDispatcher();
    }

    private static (Window window, CommandInputView view, MainViewModel vm) SetupInputView()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        var view = new CommandInputView { DataContext = vm };
        var window = new Window
        {
            Width = 600,
            Height = 200,
            Content = view,
        };
        window.ShowAndRunLayout();
        return (window, view, vm);
    }

    private static TextBox FindCommandInput(CommandInputView view)
    {
        TextBox? textBox = view.FindControl<TextBox>("CommandInput");
        Assert.NotNull(textBox);
        return textBox!;
    }
}
