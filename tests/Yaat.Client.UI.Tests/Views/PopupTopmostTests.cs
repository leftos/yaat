using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.UI.Tests.Helpers;

namespace Yaat.Client.UI.Tests.Views;

// A popup is hosted in its own native window on Windows, which is not topmost by default, so a
// menu opened from an unpinned YAAT window draws under a pinned one (GitHub #1349).
public class PopupTopmostTests
{
    [AvaloniaFact]
    public void ContextMenu_OpenedFromAWindow_ItsPopupIsTopmost()
    {
        var border = new Border { Width = 100, Height = 100 };
        var window = new Window
        {
            Width = 400,
            Height = 300,
            Content = border,
        };
        window.ShowAndRunLayout();

        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "One" });

        menu.Open(border);

        Popup popup = Assert.IsAssignableFrom<Popup>(menu.Parent);
        Assert.True(popup.Topmost, "a context menu's popup must be topmost while open");

        menu.Close();
        window.Close();
    }

    [AvaloniaFact]
    public void Popup_CodeBuilt_IsTopmostWhenOpened()
    {
        var border = new Border { Width = 100, Height = 100 };
        var panel = new StackPanel();
        panel.Children.Add(border);

        var popup = new Popup
        {
            PlacementTarget = border,
            Child = new TextBlock { Text = "popup" },
        };
        panel.Children.Add(popup);

        var window = new Window
        {
            Width = 400,
            Height = 300,
            Content = panel,
        };
        window.ShowAndRunLayout();

        popup.IsOpen = true;

        Assert.True(popup.Topmost, "a code-built popup must be topmost while open");

        popup.IsOpen = false;
        window.Close();
    }
}
