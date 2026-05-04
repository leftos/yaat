using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Xunit;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

// Regression coverage for the right-click "Command" textbox swallowing every keystroke
// (docs/plans/open-issues/context-menu-textbox-swallows-keys.md). The helper used to
// set e.Handled = true for every non-Enter/non-Escape KeyDown, which prevented the
// TextBox from receiving text input. This test asserts the helper no longer flags
// letter keys as handled, and that Enter still invokes the submit callback.
public class ContextMenuExtensionsTests
{
    [AvaloniaFact]
    public async Task LetterKeysAreNotMarkedHandled_AndEnterInvokesSubmit()
    {
        var submitted = new TaskCompletionSource<string>();
        var menu = new ContextMenu();
        menu.AddCommandTextBox(cmd =>
        {
            submitted.TrySetResult(cmd);
            return Task.CompletedTask;
        });

        // Pull the TextBox out of the menu's Items so we can host it in a live visual
        // tree without driving the ContextMenu open lifecycle. The helper's KeyDown
        // handler is wired on the TextBox itself, so it fires regardless of whether
        // the menu is currently displayed in a popup.
        var textBox = (TextBox)menu.Items[0]!;
        menu.Items.Clear();

        // Spy handler runs after the helper's KeyDown handler and can observe whether
        // the helper marked the event handled. handledEventsToo: true ensures we see
        // the event even if the helper swallowed it.
        bool letterFlaggedHandled = true;
        textBox.AddHandler(
            InputElement.KeyDownEvent,
            (_, e) =>
            {
                if (e.Key == Key.L)
                {
                    letterFlaggedHandled = e.Handled;
                }
            },
            RoutingStrategies.Bubble,
            handledEventsToo: true
        );

        var window = new Window
        {
            Width = 300,
            Height = 100,
            Content = textBox,
        };
        window.ShowAndRunLayout();
        textBox.Focus();
        HeadlessWindowExtensions.PumpDispatcher();

        window.DispatchKey(Key.L);

        Assert.False(letterFlaggedHandled, "Helper must not set e.Handled = true for letter keys (would block text input).");

        // Enter still submits — set the text directly since headless KeyPressQwerty
        // doesn't drive TextInput on its own.
        textBox.Text = "RES";
        window.DispatchKey(Key.Enter);

        var result = await submitted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("RES", result);
    }
}
