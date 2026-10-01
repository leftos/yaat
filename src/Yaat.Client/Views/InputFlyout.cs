using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Views.Radar.Flyouts;

namespace Yaat.Client.Views;

/// <summary>
/// Floating, focused free-text popup for a catalog input item ("Custom...") on a surface that has no popup of its own
/// to host it — the ground view and the aircraft list. Built on the same <see cref="TextEntryPopup"/> as the
/// Command… and Note… flyouts: Enter submits, Esc cancels, click-outside dismisses. Empty/whitespace input is a no-op,
/// so the Clear button and Enter on a blank box close the popup without sending anything.
/// </summary>
internal static class InputFlyout
{
    private static readonly ILogger Log = AppLog.CreateLogger("InputFlyout");

    public static void Open(Control anchor, string placeholder, Func<string, Task> onSubmit)
    {
        Popup popup = TextEntryPopup.Build(
            anchor,
            title: "",
            subtitle: null,
            initialText: "",
            watermark: placeholder,
            presets: [],
            extraActions: [],
            onSubmit: async value =>
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    await onSubmit(value);
                }
            }
        );

        var overlay = OverlayLayer.GetOverlayLayer(anchor);
        if (overlay is null)
        {
            Log.LogWarning(
                "InputFlyout: no overlay layer for anchor {Anchor}; input popup '{Placeholder}' not shown",
                anchor.GetType().Name,
                placeholder
            );
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
        // Defer so the context menu closing in this same message doesn't immediately light-dismiss the new popup
        // (mirrors CommandFlyout).
        Dispatcher.UIThread.Post(() => popup.IsOpen = true);
    }
}
