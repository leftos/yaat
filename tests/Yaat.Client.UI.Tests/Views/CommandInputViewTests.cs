using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Yaat.Client.Models;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Xunit;

namespace Yaat.Client.UI.Tests.Views;

// Widget-level coverage for CommandInputView.axaml.cs — specifically the KeyDown
// handler wired in OnLoaded (lines 85-199) that unit-level CommandInputController
// tests can't reach because the handler lives in code-behind.
public class CommandInputViewTests
{
    [AvaloniaFact]
    public void Escape_WithNoPopup_ClearsCommandTextAndSelection()
    {
        var (window, view, vm) = SetupInputView();
        vm.CommandText = "abc";

        var textBox = FindCommandInput(view);
        textBox.Focus();
        Helpers.HeadlessWindowExtensions.PumpDispatcher();

        window.DispatchKey(Key.Escape);

        Assert.Equal("", vm.CommandText);
        Assert.Null(vm.SelectedAircraft);
    }

    [AvaloniaFact]
    public void Up_WithNoPopup_WalksHistoryBackwards()
    {
        var (window, view, vm) = SetupInputView();
        // Populate history (CommandHistory is ObservableCollection<string>, newest at index 0)
        vm.CommandHistory.Insert(0, "OLDER");
        vm.CommandHistory.Insert(0, "NEWER");

        var textBox = FindCommandInput(view);
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
        var (window, view, vm) = SetupInputView();
        vm.Aircraft.Add(new AircraftModel { Callsign = "UAL123" });
        // Default aircraft-select keybind is Key.Add (NumPad +) with no modifiers.
        vm.CommandText = "UAL";

        var textBox = FindCommandInput(view);
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
        var (window, view, vm) = SetupInputView();
        vm.CommandText = "typed";
        // SignatureHelpState exposes IsVisible as an ObservableProperty, so we can
        // flip it directly without building a full CommandSignatureSet just to test
        // that the Escape branch in CommandInputView preserves CommandText while
        // SignatureHelp is open.
        vm.CommandInput.SignatureHelp.IsVisible = true;

        var textBox = FindCommandInput(view);
        textBox.Focus();
        Helpers.HeadlessWindowExtensions.PumpDispatcher();

        window.DispatchKey(Key.Escape);

        Assert.False(vm.CommandInput.SignatureHelp.IsVisible);
        Assert.Equal("typed", vm.CommandText);
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
        var textBox = view.FindControl<TextBox>("CommandInput");
        Assert.NotNull(textBox);
        return textBox!;
    }
}
