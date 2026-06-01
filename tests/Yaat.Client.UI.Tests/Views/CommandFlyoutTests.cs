using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

// Coverage for CommandFlyout, the floating command-entry popup that replaced the in-menu
// "Command" TextBox. The old TextBox lived inside an Avalonia ContextMenu and could not retain
// keyboard focus (the menu's interaction handler stole it on click). These tests assert the
// replacement popup focuses its TextBox on open, submits trimmed text on Enter, and treats
// blank input as a no-op.
public class CommandFlyoutTests
{
    [AvaloniaFact]
    public void Open_FocusesCommandTextBox()
    {
        var (_, anchor) = ShowAnchorWindow();

        CommandFlyout.Open(anchor, "UAL123", _ => Task.CompletedTask);
        HeadlessWindowExtensions.PumpDispatcher();

        var textBox = FindCommandTextBox(anchor);
        Assert.True(textBox.IsFocused, "Command popup TextBox should receive focus when the popup opens.");
    }

    [AvaloniaFact]
    public async Task Enter_SubmitsTrimmedCommand()
    {
        var (_, anchor) = ShowAnchorWindow();
        var submitted = new TaskCompletionSource<string>();

        CommandFlyout.Open(
            anchor,
            "UAL123",
            cmd =>
            {
                submitted.TrySetResult(cmd);
                return Task.CompletedTask;
            }
        );
        HeadlessWindowExtensions.PumpDispatcher();

        var popup = FindPopup(anchor);
        var textBox = FindCommandTextBox(anchor);
        textBox.Text = "  C 250  ";
        RaiseEnter(textBox);

        var result = await submitted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        HeadlessWindowExtensions.PumpDispatcher();

        Assert.Equal("C 250", result);
        Assert.False(popup.IsOpen, "Submitting a command should close the popup.");
    }

    [AvaloniaFact]
    public void Enter_WithBlankText_DoesNotSubmit()
    {
        var (_, anchor) = ShowAnchorWindow();
        bool submitted = false;

        CommandFlyout.Open(
            anchor,
            "UAL123",
            _ =>
            {
                submitted = true;
                return Task.CompletedTask;
            }
        );
        HeadlessWindowExtensions.PumpDispatcher();

        var popup = FindPopup(anchor);
        var textBox = FindCommandTextBox(anchor);
        textBox.Text = "   ";
        RaiseEnter(textBox);
        HeadlessWindowExtensions.PumpDispatcher();

        Assert.False(submitted, "Blank/whitespace input must not invoke the submit callback.");
        Assert.False(popup.IsOpen, "Pressing Enter should still dismiss the popup.");
    }

    private static (Window window, Control anchor) ShowAnchorWindow()
    {
        var anchor = new Border();
        var window = new Window
        {
            Width = 400,
            Height = 200,
            Content = anchor,
        };
        window.ShowAndRunLayout();
        return (window, anchor);
    }

    private static Popup FindPopup(Control anchor)
    {
        var overlay = OverlayLayer.GetOverlayLayer(anchor);
        Assert.NotNull(overlay);
        var popup = overlay!.Children.OfType<Popup>().LastOrDefault();
        Assert.NotNull(popup);
        return popup!;
    }

    private static TextBox FindCommandTextBox(Control anchor)
    {
        var popup = FindPopup(anchor);
        Assert.NotNull(popup.Child);
        var textBox = popup.Child!.GetLogicalDescendants().OfType<TextBox>().FirstOrDefault();
        Assert.NotNull(textBox);
        return textBox!;
    }

    private static void RaiseEnter(TextBox textBox) =>
        textBox.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
}
