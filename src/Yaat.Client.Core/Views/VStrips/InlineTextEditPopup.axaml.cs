using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;

namespace Yaat.Client.Views.VStrips;

/// <summary>
/// Reusable single-line inline editor. Opens a small Popup anchored to a
/// target control, pre-fills it with initial text, and invokes a callback when
/// the user commits with Enter. Esc cancels. Used by Round 3 annotation
/// edits, half-strip line edits, and separator label edits.
///
/// Usage:
///     var editor = new InlineTextEditPopup();
///     editor.Open(anchorControl, initial: "RV", onCommit: text => AnnotateAsync(strip, 5, text));
/// </summary>
public partial class InlineTextEditPopup : UserControl
{
    private Action<string>? _onCommit;

    public InlineTextEditPopup()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Opens the popup anchored to <paramref name="anchor"/>. Focuses the text
    /// box, selects its contents so users can overwrite without clearing, and
    /// invokes <paramref name="onCommit"/> with the final text when the user
    /// presses Enter.
    /// </summary>
    public void Open(Control anchor, string initial, Action<string> onCommit)
    {
        _onCommit = onCommit;
        EditTextBox.Text = initial;
        EditPopup.PlacementTarget = anchor;
        EditPopup.IsOpen = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                EditTextBox.Focus();
                EditTextBox.SelectAll();
            },
            DispatcherPriority.Loaded
        );
    }

    private void OnEditKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Commit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            EditPopup.IsOpen = false;
            e.Handled = true;
        }
    }

    private void Commit()
    {
        var value = EditTextBox.Text ?? "";
        EditPopup.IsOpen = false;
        _onCommit?.Invoke(value);
        _onCommit = null;
    }
}
