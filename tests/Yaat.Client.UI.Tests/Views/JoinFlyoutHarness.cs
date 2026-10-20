using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Sim;
using Yaat.Sim.Data;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>Opens the Join flyouts from the catalog or a strip button, in <see cref="FixValueFlyoutHarness"/>'s menu context.</summary>
internal static class JoinFlyoutHarness
{
    public static LatLon Position(string fix)
    {
        (double lat, double lon) = NavigationDatabase.Instance.GetFixPosition(fix)!.Value;
        return new LatLon(lat, lon);
    }

    /// <summary>The panel of the entry <paramref name="id"/> opened over <paramref name="aircraft"/>.</summary>
    public static TPanel Open<TPanel>(string id, IMenuAircraft? aircraft, RecordingMenuHost host)
        where TPanel : Control
    {
        MenuItem entry = Assert.IsType<MenuItem>(MenuCatalog.Get(id).Build(aircraft, FixValueFlyoutHarness.Context(), host));
        entry.IsSubMenuOpen = true;
        return Assert.IsType<TPanel>(Assert.IsAssignableFrom<FlyoutPanelHost>(Assert.Single(entry.Items.OfType<MenuItem>()).Header).Child);
    }

    /// <summary>Presses Enter in <paramref name="box"/>.</summary>
    public static void Enter(Control box) => box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });

    public static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>The flyout opened from a strip button over a shown window. Disposing closes the menu, then the window.</summary>
    public sealed record OpenedFlyout<TPanel>(Window Window, ContextMenu Menu, MenuFlyout Flyout, TPanel Panel) : IDisposable
    {
        public void Dispose()
        {
            Menu.Close();
            Dispatcher.UIThread.RunJobs();
            Window.Close();
        }

        /// <summary>Presses <paramref name="key"/> on whatever holds focus in the flyout.</summary>
        public void Press(Key key)
        {
            IInputElement? focused = TopLevel.GetTopLevel(Assert.IsAssignableFrom<Visual>(Flyout.Popup.Child))?.FocusManager?.GetFocusedElement();
            Assert.IsAssignableFrom<Control>(focused).RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
            Dispatcher.UIThread.RunJobs();
            Layout(Flyout);
        }
    }

    public static OpenedFlyout<TPanel> OpenStrip<TPanel>(string id, IMenuAircraft aircraft, RecordingMenuHost host)
        where TPanel : Control
    {
        MenuCatalogEntry entry = MenuCatalog.Get(id);
        MenuItem built = entry.Build(aircraft, FixValueFlyoutHarness.Context(), host) ?? throw new InvalidOperationException($"no {id} item");
        QuickCommandGlyph glyph = QuickCommandGlyphs.For(id) ?? throw new InvalidOperationException($"no {id} glyph");
        var menu = new ContextMenu();
        MenuItem? strip = QuickCommandStrip.FromBuilt(menu, [(new QuickCommandStripItem(entry, glyph), built)]);
        Assert.NotNull(strip);
        menu.Items.Add(strip);
        var window = new Window { Width = 900, Height = 1000 };
        try
        {
            window.Show();
            menu.Open(window);
            Dispatcher.UIThread.RunJobs();
            menu.UpdateLayout();
            Button button = Assert.Single(QuickCommandStrip.Buttons(strip));
            QuickCommandStrip.OpenSubmenu(button);
            MenuFlyout flyout = Assert.IsType<MenuFlyout>(FlyoutBase.GetAttachedFlyout(button));
            Layout(flyout);
            FlyoutPanelHost holder = Assert.IsAssignableFrom<FlyoutPanelHost>(Assert.Single(flyout.Items.OfType<MenuItem>()).Header);
            return new OpenedFlyout<TPanel>(window, menu, flyout, Assert.IsType<TPanel>(holder.Child));
        }
        catch
        {
            menu.Close();
            window.Close();
            throw;
        }
    }

    private static void Layout(MenuFlyout flyout)
    {
        (flyout.Popup.Child as Control)?.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        (flyout.Popup.Child as Control)?.UpdateLayout();
    }
}
