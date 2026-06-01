using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
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
    public static void Open(Control anchor, string callsign, Func<string, Task> onSubmit)
    {
        var popup = TextEntryPopup.Build(
            anchor,
            title: $"Command — {callsign}",
            subtitle: null,
            initialText: "",
            watermark: "Command",
            presets: [],
            extraActions: [],
            onSubmit: async value =>
            {
                var trimmed = value.Trim();
                if (trimmed.Length > 0)
                {
                    await onSubmit(trimmed);
                }
            }
        );

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
        // Defer so the context menu closing in this same message doesn't immediately light-dismiss the
        // new popup (mirrors RadarView.CreateInputMenuItem's Dispatcher.UIThread.Post pattern).
        Dispatcher.UIThread.Post(() => popup.IsOpen = true);
    }
}
