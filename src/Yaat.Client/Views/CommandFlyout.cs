using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Yaat.Client.Views.Radar.Flyouts;

namespace Yaat.Client.Views;

/// <summary>
/// Floating, focused command-entry popup opened from an aircraft right-click context menu. Replaces
/// the old in-menu TextBox, which could not retain keyboard focus inside an Avalonia ContextMenu (the
/// menu's interaction handler steals focus on click). Enter submits, Esc cancels, click-outside
/// dismisses. Empty/whitespace input is a no-op.
/// </summary>
internal static class CommandFlyout
{
    /// <summary>Builds the command popup anchored to <paramref name="anchor"/>; the caller attaches and opens it.</summary>
    public static Popup Build(Control anchor, string callsign, Func<string, Task> onSubmit)
    {
        return TextEntryPopup.Build(
            anchor,
            title: $"Command — {callsign}",
            subtitle: null,
            initialText: "",
            watermark: "Command",
            presets: [],
            extraActions: [],
            onSubmit: async value =>
            {
                string trimmed = value.Trim();
                if (trimmed.Length > 0)
                {
                    await onSubmit(trimmed);
                }
            }
        );
    }
}
