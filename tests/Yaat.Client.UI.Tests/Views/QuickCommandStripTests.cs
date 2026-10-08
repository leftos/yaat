using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;

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
    public void SubmenuButton_FlyoutClosesAndSuspendsItsTooltip_UntilTheFlyoutCloses()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        ContextMenu menu = OpenInWindow(Build(Fixture("taxiing"), Host()));
        Button follow = QuickCommandStrip.Buttons(Strip(menu)).Single(b => (string)b.Tag! == MenuIds.GroundFollow);
        ToolTip.SetIsOpen(follow, true);

        follow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.False(ToolTip.GetIsOpen(follow));
        Assert.False(ToolTip.GetServiceEnabled(follow));

        SubmenuFlyout(follow).Hide();

        Assert.True(ToolTip.GetServiceEnabled(follow));
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
    public void ApproachButton_WithoutADefault_OpensTheGroupedPickerAsAFlyout()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        RecordingMenuHost host = Host();
        AircraftModel ac = Fixture("approach-ifr");
        ac.AssignedRunway = "";
        ContextMenu menu = OpenInWindow(Build(ac, host));
        Button approach = QuickCommandStrip.Buttons(Strip(menu)).Single(b => (string)b.Tag! == MenuIds.ApproachCleared);

        approach.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        MenuFlyout flyout = SubmenuFlyout(approach);
        Assert.True(flyout.IsOpen);
        Assert.Empty(host.Sent);
        Assert.Equal(MenuCatalog.Get(MenuIds.ApproachCleared).Label, ToolTip.GetTip(approach));
        MenuItem runway = flyout.Items.OfType<MenuItem>().Single(item => item.Header as string == "28R · ILS, LOC, RNAV Y, RNP Z");
        MenuItem ils = runway.Items.OfType<MenuItem>().First(item => item.IsEnabled);

        ils.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        Assert.Equal("CAPP I28R", Assert.Single(host.Sent).Command);
        Assert.False(flyout.IsOpen);
        Assert.False(menu.IsOpen);
    }

    [AvaloniaFact]
    public void ApproachButton_WithADefault_SendsTheDefaultApproach()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        RecordingMenuHost host = Host();
        Button approach = QuickCommandStrip
            .Buttons(Strip(Build(Fixture("approach-ifr"), host)))
            .Single(b => (string)b.Tag! == MenuIds.ApproachCleared);

        Assert.Equal("Cleared ILS 30 — CAPP I30", ToolTip.GetTip(approach));
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

        var rows = (StackPanel)((StackPanel)strip.Header!).Children[1];
        Assert.InRange(QuickCommandStrip.Buttons(strip).Count, 1, QuickCommandStrip.RowLength);
        Assert.False(rows.Children[1].IsVisible);
    }

    [AvaloniaFact]
    public void HoverLeaf_LabelNamesItAndItsCommand()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        MenuItem strip = Strip(Build(Fixture("taxiing"), Host()));
        Button holdPosition = QuickCommandStrip.Buttons(strip).Single(b => (string)b.Tag! == MenuIds.GroundHoldPosition);

        RaisePointer(holdPosition, InputElement.PointerEnteredEvent);

        Assert.Equal((MenuCatalog.Get(MenuIds.GroundHoldPosition).Label, "HOLD"), QuickCommandStrip.Label(strip));
    }

    [AvaloniaFact]
    public void HoverSubmenu_LabelEndsWithChevronAndSaysItOpensASubmenu()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        MenuItem strip = Strip(Build(Fixture("taxiing"), Host()));
        Button follow = QuickCommandStrip.Buttons(strip).Single(b => (string)b.Tag! == MenuIds.GroundFollow);

        RaisePointer(follow, InputElement.PointerEnteredEvent);

        Assert.Equal(($"{MenuCatalog.Get(MenuIds.GroundFollow).Label} ›", "opens a submenu"), QuickCommandStrip.Label(strip));
    }

    [AvaloniaFact]
    public void PointerLeavesStrip_LabelResetsToPrompt()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        MenuItem strip = Strip(Build(Fixture("taxiing"), Host()));
        Assert.Equal(("Quick commands", "point at an icon"), QuickCommandStrip.Label(strip));
        Button holdPosition = QuickCommandStrip.Buttons(strip).Single(b => (string)b.Tag! == MenuIds.GroundHoldPosition);
        RaisePointer(holdPosition, InputElement.PointerEnteredEvent);

        RaisePointer(strip, InputElement.PointerExitedEvent);

        Assert.Equal(("Quick commands", "point at an icon"), QuickCommandStrip.Label(strip));
    }

    [AvaloniaFact]
    public void FocusButton_LabelFollowsFocus()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        MenuItem strip = Strip(Build(Fixture("taxiing"), Host()));
        IReadOnlyList<Button> buttons = QuickCommandStrip.Buttons(strip);
        Button holdPosition = buttons.Single(b => (string)b.Tag! == MenuIds.GroundHoldPosition);
        Button follow = buttons.Single(b => (string)b.Tag! == MenuIds.GroundFollow);

        // Focus() on a button inside a context-menu popup returns false under the headless platform, so the focus
        // change is raised as the focus manager raises it.
        holdPosition.RaiseEvent(new FocusChangedEventArgs(InputElement.GotFocusEvent));
        Assert.Equal((MenuCatalog.Get(MenuIds.GroundHoldPosition).Label, "HOLD"), QuickCommandStrip.Label(strip));
        follow.RaiseEvent(new FocusChangedEventArgs(InputElement.GotFocusEvent));
        Assert.Equal(($"{MenuCatalog.Get(MenuIds.GroundFollow).Label} ›", "opens a submenu"), QuickCommandStrip.Label(strip));
    }

    [AvaloniaFact]
    public void HoverFinalApproachSpeed_LabelWrapsOntoTwoLinesAndKeepsTheRowHeight()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel ac = Fixture("final-ifr");
        ac.FiledAircraftType = "B738";
        MenuItem strip = Strip(Build(ac, Host()));
        var labelRow = (StackPanel)((StackPanel)strip.Header!).Children[0];
        var title = (TextBlock)labelRow.Children[0];
        labelRow.Measure(new Size(labelRow.Width, double.PositiveInfinity));
        double promptHeight = labelRow.DesiredSize.Height;
        Button fas = QuickCommandStrip.Buttons(strip).Single(b => (string)b.Tag! == MenuIds.SpeedFinalApproach);

        RaisePointer(fas, InputElement.PointerEnteredEvent);
        labelRow.Measure(new Size(labelRow.Width, double.PositiveInfinity));

        Assert.Matches(@"^Reduce to final approach speed - \d+ kt$", title.Text);
        Assert.Equal(TextWrapping.Wrap, title.TextWrapping);
        // The headless platform measures every glyph at a stub width far wider than the real font's, so this pins the
        // wrap to two lines, not where the real font breaks the text.
        Assert.Equal(2, title.MaxLines);
        Assert.Equal(2, title.TextLayout.TextLines.Count);
        Assert.True(title.TextLayout.TextLines[^1].HasCollapsed);
        Assert.Equal(promptHeight, labelRow.DesiredSize.Height);
    }

    private static void RaisePointer(Control target, RoutedEvent routedEvent) =>
        target.RaiseEvent(
            new PointerEventArgs(
                routedEvent,
                target,
                new Pointer(0, PointerType.Mouse, isPrimary: true),
                rootVisual: null,
                rootVisualPosition: default,
                timestamp: 0,
                properties: default,
                modifiers: KeyModifiers.None
            )
        );

    private static RecordingMenuHost Host()
    {
        var host = new RecordingMenuHost("");
        host.GroundTraffic.Add(RecordingMenuHost.ParkedRow("SWA602"));
        host.HoldShortChoices.Add(
            new HoldShortChoice(
                new HoldShortRowLabel(HoldShortChoice.TaxiwayBadge, "B", "crossing on T", 300),
                "HS B",
                new TaxiRoute { Segments = [], HoldShortPoints = [] }
            )
        );
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
        foreach (MenuItem item in items.OfType<MenuItem>().Where(item => item.IsEnabled))
        {
            return (item.Items.Count == 0) ? item : FirstLeaf(item.Items);
        }

        throw new Xunit.Sdk.XunitException("the submenu has no command in it");
    }
}
