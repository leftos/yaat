using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Views;
using Yaat.Client.Views.Ground;
using Yaat.Client.Views.Radar;

namespace Yaat.Client.UI.Tests.Views;

// Guards the fix for "right-clicking any window brings up an Always-on-Top context menu":
// pop-out windows must NOT set a window-level ContextMenu. Always-on-Top stays reachable
// via the Settings checkbox, the configurable hotkey, and the platform title-bar/menu-bar
// entries (WindowSystemMenuHelper / WindowNativeMenuHelper) — separate code paths that are
// untouched. A window-level ContextMenu would hijack every right-click on the window body.
public class PopOutContextMenuTests
{
    [AvaloniaFact]
    public void PopOutWindows_DoNotSetWindowLevelContextMenu()
    {
        var windows = new (string Name, Window Window)[]
        {
            ("Aircraft List", new DataGridWindow()),
            ("Ground View", new GroundViewWindow()),
            ("Radar View", new RadarViewWindow()),
            ("Controllers", new ControllersWindow()),
            ("METAR", new MetarWindow()),
            ("Terminal", new TerminalWindow()),
            ("Favorites", new FavoritesPanelWindow()),
        };

        foreach (var (name, window) in windows)
        {
            Assert.True(
                window.ContextMenu is null,
                $"{name} window must not set a window-level ContextMenu — the right-click Always-on-Top menu was removed."
            );
        }
    }
}
