using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;

namespace Yaat.Client.Views.Radar.Flyouts;

/// <summary>
/// Instructor-note text-entry popup. Focused TextBox pre-filled with the current note;
/// Enter submits, Esc cancels, the Clear button (and a bare submit) clears the note,
/// click-outside dismisses. No presets — notes are free-form. View-model-agnostic: the
/// caller supplies the command sink so both the radar and ground views can reuse it.
/// </summary>
internal static class NoteFlyout
{
    /// <summary>Builds the note popup. <paramref name="sendCommand"/> receives the full canonical command.</summary>
    public static Popup Build(Control anchor, string callsign, string currentNote, Func<string, Task> sendCommand)
    {
        var subtitle = string.IsNullOrEmpty(currentNote) ? null : $"Current: \"{currentNote}\"";

        return TextEntryPopup.Build(
            anchor,
            title: $"Note — {callsign}",
            subtitle: subtitle,
            initialText: currentNote,
            watermark: "Note (max 40)",
            presets: [],
            extraActions: [],
            onSubmit: async value =>
            {
                var trimmed = value.Trim();
                var command = trimmed.Length == 0 ? "NOTE" : $"NOTE {trimmed}";
                await sendCommand(command);
            }
        );
    }

    /// <summary>
    /// Opens the note popup attached to the anchor's overlay layer, deferring the open one
    /// message loop so a closing context menu doesn't immediately light-dismiss it.
    /// </summary>
    public static void Open(Control anchor, string callsign, string currentNote, Func<string, Task> sendCommand)
    {
        var popup = Build(anchor, callsign, currentNote, sendCommand);

        var overlay = OverlayLayer.GetOverlayLayer(anchor);
        if (overlay is null)
        {
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
