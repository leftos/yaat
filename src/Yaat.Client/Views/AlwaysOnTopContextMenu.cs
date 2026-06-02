using Avalonia.Controls;

namespace Yaat.Client.Views;

/// <summary>
/// Attaches a right-click "Always On Top" toggle to a pop-out window, complementing the
/// Settings keybind. Reachable wherever inner controls don't handle the right-click (empty
/// areas, list rows, scrollbars). Shared by every pop-out host so the behaviour is consistent.
/// </summary>
internal static class AlwaysOnTopContextMenu
{
    public static void Attach(Window window, WindowGeometryHelper geometry)
    {
        var item = new MenuItem
        {
            Header = "Always On Top",
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = window.Topmost,
        };
        item.Click += (_, _) =>
        {
            geometry.ToggleTopmost();
            item.IsChecked = window.Topmost;
        };

        var menu = new ContextMenu();
        menu.Items.Add(item);
        menu.Opening += (_, _) => item.IsChecked = window.Topmost;

        window.ContextMenu = menu;
    }
}
