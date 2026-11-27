using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The text a RUNDOWN entry's Hold… or Modify… asks for: a flyout with one monospace box at a point on the canvas, opened with
/// the text it is given and, when that is not empty, all of it selected. Enter answers the box's text, Esc or a click
/// outside answers null.
/// </summary>
internal static class RundownTextPrompt
{
    /// <summary>
    /// Opens the prompt on <paramref name="anchor"/> with its top-left corner at <paramref name="at"/> (in the anchor's coordinates),
    /// <paramref name="placeholder"/> in the empty box and the box filled with <paramref name="initialText"/> already selected
    /// (Modify… opens with the entry's message selected); completes with the text Enter submitted, or null once it closes without one.
    /// </summary>
    public static Task<string?> AskAsync(Control anchor, Point at, string placeholder, string initialText)
    {
        var answer = new TaskCompletionSource<string?>();
        var box = new TextBox
        {
            Text = initialText,
            PlaceholderText = placeholder,
            MinWidth = 240,
            FontSize = 12,
        };
        if ((Application.Current?.TryGetResource("MonoFont", null, out object? font) == true) && (font is FontFamily family))
        {
            box.FontFamily = family;
        }

        var flyout = new Flyout
        {
            Content = box,
            Placement = PlacementMode.AnchorAndGravity,
            PlacementAnchor = PopupAnchor.TopLeft,
            PlacementGravity = PopupGravity.BottomRight,
            HorizontalOffset = at.X,
            VerticalOffset = at.Y,
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                answer.TrySetResult(box.Text ?? "");
                flyout.Hide();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                flyout.Hide();
                e.Handled = true;
            }
        };
        flyout.Opened += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        flyout.Closed += (_, _) => answer.TrySetResult(null);
        // On the next dispatcher turn, so the context menu whose item asked closes first and does not take the focus back.
        // An anchor in no window by then has no top level, where the flyout would never open nor close: answer null instead.
        Dispatcher.UIThread.Post(() =>
        {
            if (TopLevel.GetTopLevel(anchor) is null)
            {
                answer.TrySetResult(null);
                return;
            }

            flyout.ShowAt(anchor);
        });
        return answer.Task;
    }
}
