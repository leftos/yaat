using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Yaat.Client.ContextMenus;
using Yaat.Client.Logging;
using Yaat.Client.Views.Radar.Flyouts;

namespace Yaat.Client.Views;

/// <summary>
/// The popups every view's menu host opens its pickers through: free-text input, and the Command… and Note… flyouts. Each
/// popup is code-built into its anchor's overlay layer with <see cref="PlacementMode.Pointer"/>, so it opens at the pointer on
/// the radar, the ground view and the aircraft list alike. Every popup light-dismisses on a click outside it; the input popup
/// also closes on Escape. A popup opens on the next dispatcher turn so the context menu closing in the same message does not
/// dismiss it at once.
/// </summary>
internal static class MenuPopups
{
    private static readonly ILogger Log = AppLog.CreateLogger("MenuPopups");

    private static readonly IBrush InputBackground = new SolidColorBrush(Color.FromArgb(240, 24, 24, 24));
    private static readonly IBrush InputBorderBrush = new SolidColorBrush(Color.FromArgb(255, 80, 80, 80));

    /// <summary>
    /// Opens a focused free-text box showing <paramref name="placeholder"/> as its prompt, holding
    /// <paramref name="initialText"/> with the caret at <paramref name="caretIndex"/> (clamped to the text). Enter or OK
    /// hands the trimmed text to <paramref name="onSubmit"/> and closes the popup once it has run; a blank submit, Clear
    /// and Escape close it without calling it. While a submit is in flight, another Enter or OK does nothing, so a slow
    /// send is never fired twice.
    /// </summary>
    public static void ShowInput(Control anchor, string placeholder, string initialText, int caretIndex, Func<string, Task> onSubmit)
    {
        Popup popup = NewPopup(anchor);
        var textBox = new TextBox
        {
            Text = initialText,
            PlaceholderText = placeholder,
            Width = 160,
            VerticalAlignment = VerticalAlignment.Center,
        };

        bool submitting = false;

        async Task Submit()
        {
            if (submitting)
            {
                return;
            }

            string text = textBox.Text?.Trim() ?? "";
            if (text.Length > 0)
            {
                submitting = true;
                try
                {
                    await onSubmit(text);
                }
                finally
                {
                    submitting = false;
                }
            }

            popup.Close();
        }

        textBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await Submit();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                popup.Close();
            }
        };

        var okButton = new Button { Content = "OK" };
        okButton.Click += async (_, _) => await Submit();
        var clearButton = new Button { Content = "Clear" };
        clearButton.Click += (_, _) => popup.Close();

        popup.Child = new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = InputBorderBrush,
            Background = InputBackground,
            Padding = new Thickness(8),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children = { textBox, okButton, clearButton },
            },
        };

        int caret = Math.Clamp(caretIndex, 0, initialText.Length);
        popup.Opened += (_, _) =>
        {
            textBox.Focus();
            textBox.SelectionStart = caret;
            textBox.SelectionEnd = caret;
            textBox.CaretIndex = caret;
        };

        Open(anchor, popup, "input");
    }

    /// <summary>Opens the Command… flyout for <paramref name="callsign"/> on <paramref name="anchor"/>.</summary>
    public static void ShowCommand(Control anchor, string callsign, Func<string, Task> onSubmit) =>
        Open(anchor, CommandFlyout.Build(anchor, callsign, onSubmit), "command");

    /// <summary>Opens the Note… flyout for <paramref name="callsign"/> on <paramref name="anchor"/>.</summary>
    public static void ShowNote(Control anchor, string callsign, string currentNote, Func<string, Task> sendCommand) =>
        Open(anchor, NoteFlyout.Build(anchor, callsign, currentNote, sendCommand), "note");

    private static Popup NewPopup(Control anchor) =>
        new()
        {
            Placement = PlacementMode.Pointer,
            PlacementTarget = anchor,
            IsLightDismissEnabled = true,
            OverlayDismissEventPassThrough = false,
        };

    private static void Open(Control anchor, Popup popup, string kind)
    {
        var overlay = OverlayLayer.GetOverlayLayer(anchor);
        if (overlay is null)
        {
            Log.LogWarning("MenuPopups: no overlay layer for anchor {Anchor}; {Kind} popup not shown", anchor.GetType().Name, kind);
            return;
        }

        overlay.Children.Add(popup);
        popup.Closed += (s, _) =>
        {
            if (s is Popup p)
            {
                overlay.Children.Remove(p);
            }
        };
        Dispatcher.UIThread.Post(() => popup.IsOpen = true);
    }
}
