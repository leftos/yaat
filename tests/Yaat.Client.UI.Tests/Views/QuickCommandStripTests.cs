using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Sim.Data;

namespace Yaat.Client.UI.Tests.Views;

// The quick-command icon strip at the top of the aircraft menu: its buttons follow the resolved strip, and each button
// goes through its catalog entry's own menu item.
public class QuickCommandStripTests
{
    [AvaloniaTheory]
    [InlineData("taxiing")]
    [InlineData("final-ifr")]
    public void Strip_ButtonsFollowTheResolvedStripInOrder(string fixture)
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        RecordingMenuHost host = Host();
        AircraftModel ac = Fixture(fixture);

        ContextMenu menu = Build(ac, host);
        MenuItem strip = Strip(menu);

        MenuContext context = Context(ac, host);
        QuickCommandResolution resolution = QuickCommandResolver.Resolve(ac, context, entry => entry.Build(ac, context, host) is not null);
        List<string> expected = [.. resolution.Strip.Select(item => item.Entry.Id)];
        Assert.NotEmpty(expected);
        Assert.Equal(expected, QuickCommandStrip.Buttons(strip).Select(button => (string)button.Tag!));
    }

    [AvaloniaTheory]
    [InlineData("taxiing")]
    [InlineData("final-ifr")]
    public void FirstButton_SendsWhatTheEntrysMenuItemSends(string fixture)
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        RecordingMenuHost host = Host();
        AircraftModel ac = Fixture(fixture);

        Button first = QuickCommandStrip.Buttons(Strip(Build(ac, host)))[0];
        first.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        (string Callsign, string Command, string Initials) fromStrip = Assert.Single(host.Sent);

        MenuItem item = MenuCatalog.Get((string)first.Tag!).Build(ac, Context(ac, host), host)!;
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(2, host.Sent.Count);
        Assert.Equal(host.Sent[1], fromStrip);
    }

    [AvaloniaFact]
    public void LeafButton_TooltipNamesTheEntryAndTheCommandItSends()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        RecordingMenuHost host = Host();
        AircraftModel ac = Fixture("taxiing");

        Button holdPosition = QuickCommandStrip.Buttons(Strip(Build(ac, host))).Single(b => (string)b.Tag! == MenuIds.GroundHoldPosition);

        Assert.Equal($"{MenuCatalog.Get(MenuIds.GroundHoldPosition).Label} — HOLD", ToolTip.GetTip(holdPosition));
    }

    [AvaloniaFact]
    public void SubmenuButton_TooltipIsTheLabelAloneAndAClickSendsNothing()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        RecordingMenuHost host = Host();
        AircraftModel ac = Fixture("taxiing");

        ContextMenu menu = OpenInWindow(Build(ac, host));
        Button follow = QuickCommandStrip.Buttons(Strip(menu)).Single(b => (string)b.Tag! == MenuIds.GroundFollow);

        follow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(MenuCatalog.Get(MenuIds.GroundFollow).Label, ToolTip.GetTip(follow));
        Assert.Empty(host.Sent);
        Assert.True(SubmenuFlyout(follow).IsOpen);
        Assert.True(menu.IsOpen);
    }

    [AvaloniaFact]
    public void SubmenuLeaf_SendsItsCommandAndClosesTheFlyoutAndTheMenu()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        RecordingMenuHost host = Host();
        AircraftModel ac = Fixture("taxiing");
        ContextMenu menu = OpenInWindow(Build(ac, host));
        Button follow = QuickCommandStrip.Buttons(Strip(menu)).Single(b => (string)b.Tag! == MenuIds.GroundFollow);
        follow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        MenuFlyout flyout = SubmenuFlyout(follow);
        MenuItem leaf = FirstLeaf(flyout.Items);

        leaf.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        Assert.Single(host.Sent);
        Assert.False(flyout.IsOpen);
        Assert.False(menu.IsOpen);
    }

    [AvaloniaFact]
    public void LeafButton_SendsItsCommandAndClosesTheMenu()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        RecordingMenuHost host = Host();
        AircraftModel ac = Fixture("taxiing");
        ContextMenu menu = OpenInWindow(Build(ac, host));
        Button holdPosition = QuickCommandStrip.Buttons(Strip(menu)).Single(b => (string)b.Tag! == MenuIds.GroundHoldPosition);

        holdPosition.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal("HOLD", Assert.Single(host.Sent).Command);
        Assert.False(menu.IsOpen);
    }

    [AvaloniaFact]
    public void FewerThanFiveButtons_HidesTheSecondRow()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        RecordingMenuHost host = Host();

        MenuItem strip = Strip(Build(Fixture("final-ifr"), host));

        var rows = (StackPanel)strip.Header!;
        Assert.InRange(QuickCommandStrip.Buttons(strip).Count, 1, QuickCommandStrip.RowLength);
        Assert.False(rows.Children[1].IsVisible);
    }

    private static RecordingMenuHost Host()
    {
        var host = new RecordingMenuHost("");
        host.GroundTraffic.Add("SWA602");
        host.HoldShortChoices.Add(new MenuCommandChoice("B", "HS B", null, []));
        return host;
    }

    private static ContextMenu Build(AircraftModel ac, RecordingMenuHost host) =>
        AircraftMenuBuilder.Build(ac, new MenuClick(ac.Callsign, null, null, []), host, _ => []);

    private static MenuContext Context(AircraftModel ac, RecordingMenuHost host) => new(new MenuClick(ac.Callsign, null, null, []), host.Session);

    /// <summary>The radar golden fixture of that name: its aircraft, the same on every view.</summary>
    private static AircraftModel Fixture(string name) => MenuGoldenFixtures.For(MenuView.Radar).Single(f => f.Name == name).Aircraft;

    private static MenuItem Strip(ContextMenu menu) => Assert.Single(menu.Items.OfType<MenuItem>(), QuickCommandStrip.IsStrip);

    /// <summary>Opens <paramref name="menu"/> over a shown window, so its strip buttons can show their flyouts.</summary>
    private static ContextMenu OpenInWindow(ContextMenu menu)
    {
        var window = new Window { Width = 400, Height = 300 };
        window.Show();
        menu.Open(window);
        Assert.True(menu.IsOpen);
        return menu;
    }

    private static MenuFlyout SubmenuFlyout(Button button) => Assert.IsType<MenuFlyout>(FlyoutBase.GetAttachedFlyout(button));

    /// <summary>The first menu item, depth first, that opens nothing: a command.</summary>
    private static MenuItem FirstLeaf(IEnumerable<object?> items)
    {
        foreach (MenuItem item in items.OfType<MenuItem>())
        {
            return (item.Items.Count == 0) ? item : FirstLeaf(item.Items);
        }

        throw new Xunit.Sdk.XunitException("the submenu has no command in it");
    }
}
