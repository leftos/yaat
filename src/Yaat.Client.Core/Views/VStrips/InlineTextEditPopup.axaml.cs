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
    private bool _substituteCheckmark;

    public InlineTextEditPopup()
    {
        InitializeComponent();
        EditTextBox.TextChanged += OnTextChanged;
    }

    /// <summary>
    /// Opens the popup anchored to <paramref name="anchor"/>. Focuses the text
    /// box, selects its contents so users can overwrite without clearing, and
    /// invokes <paramref name="onCommit"/> with the final text when the user
    /// presses Enter.
    ///
    /// <paramref name="substituteCheckmark"/> enables CRC's annotation-cell
    /// shortcut (<see href="docs/crc/vstrips.md">docs/crc/vstrips.md:130</see>):
    /// every <c>?</c> typed is replaced live with a checkmark <c>✓</c> (U+2713).
    /// Pass <c>true</c> only when editing a strip annotation box — label /
    /// half-strip editors keep <c>?</c> literal.
    /// </summary>
    public void Open(Control anchor, string initial, Action<string> onCommit, bool substituteCheckmark = false)
    {
        _onCommit = onCommit;
        _substituteCheckmark = substituteCheckmark;
        EditTextBox.Text = substituteCheckmark ? ApplyCheckmarkSubstitution(initial) : initial;
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

    private void OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_substituteCheckmark || EditTextBox.Text is not { } text || text.IndexOf('?') < 0)
        {
            return;
        }
        var replaced = text.Replace('?', '✓');
        // Preserve caret position across the replacement so the user keeps
        // typing at the same logical spot. '?' and '✓' are both single UTF-16
        // code units, so offsets are stable.
        var caret = EditTextBox.CaretIndex;
        EditTextBox.Text = replaced;
        EditTextBox.CaretIndex = Math.Min(caret, replaced.Length);
    }

    private static string ApplyCheckmarkSubstitution(string text) => text.Replace('?', '✓');

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
