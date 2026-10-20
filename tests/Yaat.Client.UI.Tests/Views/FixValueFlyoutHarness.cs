using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>Opens the Cross fix and Depart fix flyouts over a routed aircraft at OAK, from the catalog or a strip button.</summary>
internal static class FixValueFlyoutHarness
{
    public const string Callsign = "UAL455";
    public const string Initials = "AB";

    public static MenuContext Context() => TestMenuContext.Create(Callsign, Initials, null, false, VfrCommandsForIfr.None);

    public static LatLon OakVor()
    {
        (double lat, double lon) = NavigationDatabase.Instance.GetFixPosition("OAK")!.Value;
        return new LatLon(lat, lon);
    }

    /// <summary>An aircraft at OAK navigating to SUNOL, its route SUNOL then ECA.</summary>
    public static FakeMenuAircraft Routed() =>
        new()
        {
            Callsign = Callsign,
            Position = OakVor(),
            NavigatingTo = "SUNOL",
            NavigationRoute = ["SUNOL", "ECA"],
            RouteFixes = ["SUNOL", "ECA"],
        };

    /// <summary>The same aircraft as a model whose navigation a test can move while the flyout is open.</summary>
    public static AircraftModel RoutedModel() =>
        new()
        {
            Callsign = Callsign,
            Position = OakVor(),
            NavigatingTo = "SUNOL",
            NavigationRoute = ["SUNOL", "ECA"],
        };

    /// <summary>The panel of the entry <paramref name="id"/> opened over <paramref name="aircraft"/>.</summary>
    public static FixValueFlyoutPanel Open(string id, IMenuAircraft aircraft, RecordingMenuHost host)
    {
        MenuItem entry = Assert.IsType<MenuItem>(MenuCatalog.Get(id).Build(aircraft, Context(), host));
        entry.IsSubMenuOpen = true;
        return Assert.IsAssignableFrom<FixValueFlyoutPanel>(
            Assert.IsAssignableFrom<FlyoutPanelHost>(Assert.Single(entry.Items.OfType<MenuItem>()).Header).Child
        );
    }

    public static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>The flyout opened from a strip button over a shown window. Disposing closes the menu, then the window.</summary>
    public sealed record OpenedFlyout(Window Window, ContextMenu Menu, MenuFlyout Flyout, FixValueFlyoutPanel Panel) : IDisposable
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

    public static OpenedFlyout OpenStrip(string id, IMenuAircraft aircraft, RecordingMenuHost host)
    {
        MenuCatalogEntry entry = MenuCatalog.Get(id);
        MenuItem built = entry.Build(aircraft, Context(), host) ?? throw new InvalidOperationException($"no {id} item");
        QuickCommandGlyph glyph = QuickCommandGlyphs.For(id) ?? throw new InvalidOperationException($"no {id} glyph");
        var menu = new ContextMenu();
        MenuItem? strip = QuickCommandStrip.FromBuilt(menu, [(new QuickCommandStripItem(entry, glyph), built)]);
        Assert.NotNull(strip);
        menu.Items.Add(strip);
        var window = new Window { Width = 900, Height = 900 };
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
            return new OpenedFlyout(window, menu, flyout, Assert.IsAssignableFrom<FixValueFlyoutPanel>(holder.Child));
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
