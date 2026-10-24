using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Situation;

namespace Yaat.Client.UI.Tests.Views;

// The quick-command icon strip at the top of the aircraft menu: its buttons follow the resolved strip, and each button
// goes through its catalog entry's own menu item.
public class QuickCommandStripTests
{
    private const string StickyClass = QuickCommandStrip.StickyClass;

    [AvaloniaTheory]
    [InlineData("taxiing")]
    [InlineData("final-ifr")]
    public void Strip_ButtonsFollowTheResolvedStrip_GroupedByCategory(string fixture)
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        RecordingMenuHost host = Host();
        AircraftModel ac = Fixture(fixture);

        ContextMenu menu = Build(ac, host);
        MenuItem strip = Strip(menu);

        MenuContext context = Context(ac, host);
        QuickCommandResolution resolution = QuickCommandResolver.Resolve(ac, context, entry => entry.Build(ac, context, host) is not null);
        List<string> expected =
        [
            .. QuickCommandStripLayout
                .Pack([.. resolution.Strip.Select(item => item.Entry.Id)], MenuCategories.Of)
                .SelectMany(row => row.Segments)
                .SelectMany(segment => segment.Items),
        ];
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
    public void SubmenuButton_AClickSendsNothingAndOpensTheFlyout()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        RecordingMenuHost host = Host();
        AircraftModel ac = Fixture("taxiing");

        ContextMenu menu = OpenInWindow(Build(ac, host));
        Button follow = QuickCommandStrip.Buttons(Strip(menu)).Single(b => (string)b.Tag! == MenuIds.GroundFollow);

        follow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Empty(host.Sent);
        Assert.True(SubmenuFlyout(follow).IsOpen);
        Assert.True(menu.IsOpen);
    }

    [AvaloniaTheory]
    [InlineData("taxiing")]
    [InlineData("final-ifr")]
    public void NoIcon_CarriesATooltip(string fixture)
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());

        IReadOnlyList<Button> buttons = QuickCommandStrip.Buttons(Strip(Build(Fixture(fixture), Host())));

        Assert.NotEmpty(buttons);
        Assert.All(buttons, button => Assert.Null(ToolTip.GetTip(button)));
    }

    /// <summary>
    /// An aircraft holding short whose route has a bar ahead gets Hold short of… on the strip from the default list
    /// alone (<see cref="QuickCommandDefaults"/>, the HoldingShort situation).
    /// </summary>
    [AvaloniaFact]
    public void HoldingShort_DefaultStrip_OffersHoldShortOf()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        RecordingMenuHost host = Host();
        AircraftModel ac = Fixture("holding-short");
        Assert.True(HoldShortMenuHelper.IsHoldingShort(ac.CurrentPhase), ac.CurrentPhase);

        ContextMenu menu = OpenInWindow(Build(ac, host));

        Assert.Contains(QuickCommandStrip.Buttons(Strip(menu)), b => (string)b.Tag! == MenuIds.GroundHoldShort);
    }

    [AvaloniaFact]
    public void SubmenuLeaf_SendsItsCommandAndClosesTheFlyoutAndTheMenu()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        // Preset taxi route opens its designed flyout of two-line route rows (PresetTaxiRouteFlyoutTests covers the rest);
        // a row's command still closes the flyout and the menu.
        var host = new RecordingMenuHost("")
        {
            Session = new(
                "AB",
                false,
                VfrCommandsForIfr.None,
                _ => [new CatalogQuickCommandEntry(MenuIds.GroundTaxiPreset, null)],
                SoloAdvisoryPrefill.TrueValues,
                false,
                CommandScheme.Default(),
                ShowQuickCommandIcons: true
            ),
        };
        host.PresetTaxiChoices.Add(RecordingMenuHost.PresetRow("TERMINAL to 30", "TAXI T U W RWY 30"));
        AircraftModel ac = Fixture("taxiing");
        ContextMenu menu = OpenInWindow(Build(ac, host));
        Button preset = QuickCommandStrip.Buttons(Strip(menu)).Single(b => (string)b.Tag! == MenuIds.GroundTaxiPreset);
        preset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        MenuFlyout flyout = SubmenuFlyout(preset);
        MenuItem leaf = FirstLeaf(flyout.Items);
        MenuCommandRow row = Assert.IsType<MenuCommandRow>(leaf.Header);
        Assert.Equal(MenuDetailPlacement.Route, row.DetailPlacement);
        Assert.Equal(QuickCommandGlyphs.For(MenuIds.GroundTaxiPreset), row.Glyph);

        leaf.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        Assert.Single(host.Sent);
        Assert.False(flyout.IsOpen);
        Assert.False(menu.IsOpen);
    }

    /// <summary>
    /// Taxi to runway asks the host for its runway ends only when its flyout first opens, and once however often it opens:
    /// the flyout shows a tab per end over the selected end's rows, and a row's command closes the flyout and the menu.
    /// </summary>
    [AvaloniaFact]
    public void TaxiToRunwayButton_FlyoutShowsTheTabsAndRows_AskingTheHostOnce_ARowClosesTheFlyout()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        RecordingMenuHost host = TaxiToRunwayHost(
            [
                new TaxiToRunwayEnd(
                    "30",
                    new RunwayIdentifier("12", "30"),
                    TaxiToRunwayEndRole.Assigned,
                    TaxiToRunwayEndState.Ready,
                    [RecordingMenuHost.PresetRow("TERMINAL to 30", "TAXI T U W RWY 30")]
                ),
            ],
            runway => throw new InvalidOperationException($"Runway {runway} was searched at open already.")
        );
        ContextMenu menu = OpenInWindow(Build(Fixture("taxiing"), host));
        Button taxiToRunway = QuickCommandStrip.Buttons(Strip(menu)).Single(b => (string)b.Tag! == MenuIds.GroundTaxiToRunway);
        Assert.Empty(host.TaxiToRunwayRequests);

        taxiToRunway.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        MenuFlyout flyout = SubmenuFlyout(taxiToRunway);
        flyout.Hide();
        taxiToRunway.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.True(flyout.IsOpen);
        TaxiToRunwayPanel panel = Assert.IsType<TaxiToRunwayPanel>(Assert.Single(flyout.Items.OfType<MenuItem>()).Header);
        Assert.Equal(["30 assigned"], panel.Strip.Tabs.Select(tab => tab.Label));
        Assert.Equal(["TERMINAL to 30 — TAXI T U W RWY 30"], panel.Lines);
        Assert.Equal(["SWA104"], host.TaxiToRunwayRequests);

        Assert.Single(panel.Rows).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal([("SWA104", "TAXI T U W RWY 30", "AB")], host.Sent);
        Assert.False(flyout.IsOpen);
        Assert.False(menu.IsOpen);
    }

    /// <summary>A row of a tab searched only once it is chosen still closes the strip's flyout and the menu when chosen.</summary>
    [AvaloniaFact]
    public void TaxiToRunwayButton_RowOfATabSearchedWhenChosen_ClosesTheFlyout()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        var runway28R = new RunwayIdentifier("10L", "28R");
        IReadOnlyList<(string Designator, IReadOnlyList<TaxiRouteRow> Rows)> found =
        [
            ("28R", [RecordingMenuHost.PresetRow("TERMINAL to 28R", "TAXI T B RWY 28R")]),
            ("10L", []),
        ];
        RecordingMenuHost host = TaxiToRunwayHost(
            [
                new TaxiToRunwayEnd("30", new RunwayIdentifier("12", "30"), TaxiToRunwayEndRole.Assigned, TaxiToRunwayEndState.Empty, []),
                new TaxiToRunwayEnd("28R", runway28R, TaxiToRunwayEndRole.Other, TaxiToRunwayEndState.NotSearched, []),
            ],
            runway => Task.FromResult<IReadOnlyList<(string Designator, IReadOnlyList<TaxiRouteRow> Rows)>?>((runway == runway28R) ? found : null)
        );
        ContextMenu menu = OpenInWindow(Build(Fixture("taxiing"), host));
        Button taxiToRunway = QuickCommandStrip.Buttons(Strip(menu)).Single(b => (string)b.Tag! == MenuIds.GroundTaxiToRunway);
        taxiToRunway.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        MenuFlyout flyout = SubmenuFlyout(taxiToRunway);
        TaxiToRunwayPanel panel = Assert.IsType<TaxiToRunwayPanel>(Assert.Single(flyout.Items.OfType<MenuItem>()).Header);

        panel.Strip.Select(1);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(panel.Rows).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal([("SWA104", "TAXI T B RWY 28R", "AB")], host.Sent);
        Assert.False(flyout.IsOpen);
        Assert.False(menu.IsOpen);
    }

    /// <summary>A host whose strip holds Taxi to runway alone, answering <paramref name="ends"/>, searched by <paramref name="search"/>.</summary>
    private static RecordingMenuHost TaxiToRunwayHost(
        List<TaxiToRunwayEnd> ends,
        Func<RunwayIdentifier, Task<IReadOnlyList<(string Designator, IReadOnlyList<TaxiRouteRow> Rows)>?>> search
    ) =>
        new("")
        {
            Session = new(
                "AB",
                false,
                VfrCommandsForIfr.None,
                _ => [new CatalogQuickCommandEntry(MenuIds.GroundTaxiToRunway, null)],
                SoloAdvisoryPrefill.TrueValues,
                false,
                CommandScheme.Default(),
                ShowQuickCommandIcons: true
            ),
            TaxiToRunway = new TaxiToRunwayMenu(new TaxiToRunwayEndList(ends, search)),
        };

    [AvaloniaFact]
    public void ApproachButton_WithoutADefault_OpensTheApproachFlyout()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        RecordingMenuHost host = Host();
        AircraftModel ac = Fixture("approach-ifr");
        ac.AssignedRunway = "";
        ac.ExpectedApproach = null;
        ContextMenu menu = OpenInWindow(Build(ac, host));
        Button approach = QuickCommandStrip.Buttons(Strip(menu)).Single(b => (string)b.Tag! == MenuIds.ApproachCleared);

        approach.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        MenuFlyout flyout = SubmenuFlyout(approach);
        Assert.True(flyout.IsOpen);
        Assert.Empty(host.Sent);
        ApproachFlyoutPanel panel = ApproachFlyoutTests.Panel(flyout);
        panel.Strip.Select(panel.Strip.Tabs.ToList().FindIndex(tab => tab.Label.StartsWith("28R ", StringComparison.Ordinal)));
        Button ils = panel.Rows.First(row => row.Content is ApproachRow { Kind: ApproachRowKind.Instrument });

        ils.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal("CAPP I28R", Assert.Single(host.Sent).Command);
        Assert.False(flyout.IsOpen);
        Assert.False(menu.IsOpen);
    }

    /// <summary>
    /// The flyout opens on the menu's right edge, flipping to its left near the screen edge as a submenu does, with its top
    /// edge on the strip's row; a click in the parent menu reaches the menu, as the flyout does not close on it by itself.
    /// </summary>
    [AvaloniaFact]
    public void SubmenuFlyout_OpensBesideTheMenu_AnchoredToItsContent()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        ContextMenu menu = OpenLaidOut(Build(Fixture("taxiing"), Host()));
        MenuItem strip = Strip(menu);
        Button follow = QuickCommandStrip.Buttons(strip).Single(b => (string)b.Tag! == MenuIds.GroundFollow);

        follow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        MenuFlyout flyout = SubmenuFlyout(follow);
        Assert.True(flyout.IsOpen);
        Assert.Same(menu, flyout.Target);
        Assert.Equal(PlacementMode.RightEdgeAlignedTop, flyout.Placement);
        Assert.True(flyout.PlacementConstraintAdjustment.HasFlag(PopupPositionerConstraintAdjustment.FlipX));
        Assert.True(flyout.PlacementConstraintAdjustment.HasFlag(PopupPositionerConstraintAdjustment.SlideY));
        Point stripTop = Assert.IsType<Point>(strip.TranslatePoint(default, menu));
        Assert.True(stripTop.Y > 0, $"the strip's top is {stripTop.Y}, the menu's own top");
        Assert.Equal(stripTop.Y, flyout.VerticalOffset);
        Assert.False(flyout.Popup.IsLightDismissEnabled);
    }

    [AvaloniaFact]
    public void SubmenuButton_SecondClick_ClosesItsFlyout()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        ContextMenu menu = OpenLaidOut(Build(Fixture("taxiing"), Host()));
        Button follow = QuickCommandStrip.Buttons(Strip(menu)).Single(b => (string)b.Tag! == MenuIds.GroundFollow);
        follow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(SubmenuFlyout(follow).IsOpen);

        follow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.False(SubmenuFlyout(follow).IsOpen);
        Assert.True(menu.IsOpen);
    }

    [AvaloniaFact]
    public void SubmenuButton_IsStickyWhileItsFlyoutIsOpen()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        ContextMenu menu = OpenLaidOut(Build(Fixture("taxiing"), Host()));
        IReadOnlyList<Button> buttons = QuickCommandStrip.Buttons(Strip(menu));
        Button follow = buttons.Single(b => (string)b.Tag! == MenuIds.GroundFollow);
        Button holdPosition = buttons.Single(b => (string)b.Tag! == MenuIds.GroundHoldPosition);
        Assert.Equal(BackgroundColor(holdPosition), BackgroundColor(follow));

        follow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Contains(StickyClass, follow.Classes);
        Assert.NotEqual(BackgroundColor(holdPosition), BackgroundColor(follow));

        SubmenuFlyout(follow).Hide();
        Assert.DoesNotContain(StickyClass, follow.Classes);
        Assert.Equal(BackgroundColor(holdPosition), BackgroundColor(follow));
    }

    [AvaloniaFact]
    public void Button_AtRest_DrawsItsGlyphLargeInADarkWell()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        ContextMenu menu = OpenLaidOut(Build(Fixture("taxiing"), Host()));
        Button holdPosition = QuickCommandStrip.Buttons(Strip(menu)).Single(b => (string)b.Tag! == MenuIds.GroundHoldPosition);

        Assert.Equal(QuickCommandStrip.WellBrush.Color, BackgroundColor(holdPosition));
        Viewbox icon = Assert.Single(holdPosition.GetVisualDescendants().OfType<Viewbox>());
        Assert.True(icon.Width > QuickCommandStrip.GlyphIcon(QuickCommandGlyphs.For(MenuIds.GroundHoldPosition)!).Width);
    }

    [AvaloniaFact]
    public void SubmenuButton_ClickingAnotherSubmenuIcon_ReplacesTheFlyout()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        ContextMenu menu = OpenLaidOut(Build(Fixture("taxiing"), Host()));
        List<Button> submenuButtons = [.. QuickCommandStrip.Buttons(Strip(menu)).Where(b => FlyoutBase.GetAttachedFlyout(b) is MenuFlyout)];
        Assert.True(submenuButtons.Count >= 2, $"the strip has {submenuButtons.Count} submenu icon(s), not two");
        (Button first, Button second) = (submenuButtons[0], submenuButtons[1]);
        first.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        second.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.False(SubmenuFlyout(first).IsOpen);
        Assert.DoesNotContain(StickyClass, first.Classes);
        Assert.True(SubmenuFlyout(second).IsOpen);
        Assert.Contains(StickyClass, second.Classes);
        Assert.True(menu.IsOpen);
    }

    [AvaloniaFact]
    public void SiblingSubmenuOpening_ClosesTheFlyout()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        ContextMenu menu = OpenLaidOut(Build(Fixture("taxiing"), Host()));
        Button follow = QuickCommandStrip.Buttons(Strip(menu)).Single(b => (string)b.Tag! == MenuIds.GroundFollow);
        follow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        MenuItem sibling = menu.Items.OfType<MenuItem>().First(m => (!QuickCommandStrip.IsStrip(m)) && (m.Items.Count > 0));

        sibling.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, sibling));

        Assert.False(SubmenuFlyout(follow).IsOpen);
        Assert.DoesNotContain(StickyClass, follow.Classes);
        Assert.True(menu.IsOpen);
    }

    /// <summary>A sibling row opening its submenu while the flyout holds focus leaves focus alone: only Escape hands it back to the icon.</summary>
    [AvaloniaFact]
    public void SiblingSubmenuOpening_WithFocusInTheFlyout_LeavesTheIconUnfocused()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        ContextMenu menu = OpenLaidOut(Build(Fixture("taxiing"), Host()));
        Button follow = QuickCommandStrip.Buttons(Strip(menu)).Single(b => (string)b.Tag! == MenuIds.GroundFollow);
        follow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        MenuFlyout flyout = SubmenuFlyout(follow);
        Dispatcher.UIThread.RunJobs();
        Assert.True(flyout.Popup.Child!.IsKeyboardFocusWithin, "the flyout took no keyboard focus when it opened");
        MenuItem sibling = menu.Items.OfType<MenuItem>().First(m => (!QuickCommandStrip.IsStrip(m)) && (m.Items.Count > 0));

        sibling.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, sibling));

        Assert.False(flyout.IsOpen);
        Dispatcher.UIThread.RunJobs();
        Assert.False(follow.IsFocused);
    }

    [AvaloniaFact]
    public void LeafRowHover_KeepsTheFlyoutOpen()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        ContextMenu menu = OpenLaidOut(Build(Fixture("taxiing"), Host()));
        Button follow = QuickCommandStrip.Buttons(Strip(menu)).Single(b => (string)b.Tag! == MenuIds.GroundFollow);
        follow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        MenuItem leaf = menu.Items.OfType<MenuItem>().First(m => (!QuickCommandStrip.IsStrip(m)) && (m.Items.Count == 0) && m.IsEnabled);

        RaisePointer(leaf, InputElement.PointerEnteredEvent);

        Assert.True(SubmenuFlyout(follow).IsOpen);
        Assert.Contains(StickyClass, follow.Classes);
    }

    [AvaloniaFact]
    public void MenuClosing_ClosesTheFlyout()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        ContextMenu menu = OpenLaidOut(Build(Fixture("taxiing"), Host()));
        Button follow = QuickCommandStrip.Buttons(Strip(menu)).Single(b => (string)b.Tag! == MenuIds.GroundFollow);
        follow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        menu.Close();

        Assert.False(SubmenuFlyout(follow).IsOpen);
        Assert.DoesNotContain(StickyClass, follow.Classes);
    }

    [AvaloniaFact]
    public void Escape_ClosesTheFlyoutFirst_ThenTheMenu()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        ContextMenu menu = OpenLaidOut(Build(Fixture("taxiing"), Host()));
        MenuItem strip = Strip(menu);
        Button follow = QuickCommandStrip.Buttons(strip).Single(b => (string)b.Tag! == MenuIds.GroundFollow);
        follow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        MenuFlyout flyout = SubmenuFlyout(follow);
        Dispatcher.UIThread.RunJobs();
        Assert.True(flyout.Popup.Child!.IsKeyboardFocusWithin, "the flyout took no keyboard focus when it opened");
        MenuItem row = flyout.Items.OfType<MenuItem>().First(item => item.IsEnabled);

        row.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });

        Assert.False(flyout.IsOpen);
        Assert.DoesNotContain(StickyClass, follow.Classes);
        Assert.True(menu.IsOpen);
        // Headless, the menu's focus manager reports the strip row, not the icon, once the flyout has closed, so the
        // second Escape is sent to the strip.
        strip.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });

        Assert.False(menu.IsOpen);
    }

    [AvaloniaFact]
    public void ApproachButton_WithADefault_OpensTheFlyoutWithTheDefaultRowFirst()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        ContextMenu menu = OpenInWindow(Build(Fixture("approach-ifr"), Host()));
        Button approach = QuickCommandStrip.Buttons(Strip(menu)).Single(b => (string)b.Tag! == MenuIds.ApproachCleared);

        RaisePointer(approach, InputElement.PointerEnteredEvent);
        Assert.Equal("Cleared approach ›", Line(menu).Label);
        approach.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        ApproachFlyoutPanel panel = ApproachFlyoutTests.Panel(SubmenuFlyout(approach));
        Assert.Equal("Cleared ILS 30 · default — CAPP I30", panel.PinnedRows[0].Content?.ToString());
        Assert.StartsWith("30 ", panel.Strip.Tabs[panel.Strip.SelectedIndex].Label, StringComparison.Ordinal);
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
    public void Strip_DrawsOneRowPerPackedRow_EachOfAtMostSixButtons()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        RecordingMenuHost host = Host();

        MenuItem strip = Strip(Build(Fixture("final-ifr"), host));

        var rows = (StackPanel)strip.Header!;
        IReadOnlyList<QuickCommandStripRow<Button>> layout = QuickCommandStrip.Layout(strip);
        Assert.NotEmpty(layout);
        Assert.Equal(layout.Count, rows.Children.Count);
        Assert.All(layout, row => Assert.InRange(row.Segments.Sum(segment => segment.Items.Count), 1, QuickCommandStripLayout.RowLength));
    }

    [AvaloniaFact]
    public void Rows_SixteenCells_HaveNoCap_AndLaySixToARow()
    {
        List<(Control Cell, MenuCategory? Category)> cells =
        [
            .. Enumerable.Range(0, 16).Select(i => ((Control)new Border { Tag = i }, (MenuCategory?)null)),
        ];

        StackPanel rows = QuickCommandStrip.Rows(cells);

        Assert.Equal(3, rows.Children.Count);
        Assert.Equal([.. Enumerable.Range(0, 6)], TaggedCells(rows.Children[0]));
        Assert.Equal([.. Enumerable.Range(6, 6)], TaggedCells(rows.Children[1]));
        Assert.Equal([12, 13, 14, 15], TaggedCells(rows.Children[2]));
        Assert.Empty(rows.GetLogicalDescendants().OfType<TextBlock>());
    }

    [AvaloniaFact]
    public void Rows_TwoCategoriesSharingARow_EachKeepItsLabel_WithARuleBetween()
    {
        List<(Control Cell, MenuCategory? Category)> cells =
        [
            (new Border { Tag = 0 }, MenuCategory.Runway),
            (new Border { Tag = 1 }, MenuCategory.Runway),
            (new Border { Tag = 2 }, MenuCategory.Taxi),
        ];

        StackPanel rows = QuickCommandStrip.Rows(cells);

        StackPanel row = Assert.IsType<StackPanel>(Assert.Single(rows.Children));
        Assert.Equal(3, row.Children.Count);
        Border rule = Assert.IsType<Border>(row.Children[1]);
        Assert.Equal(1, rule.Width);
        Assert.Equal([0, 1, 2], TaggedCells(row));
        Assert.Equal(["RUNWAY", "TAXI"], row.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text));
        Assert.All(row.GetLogicalDescendants().OfType<TextBlock>(), text => Assert.Equal(TextTrimming.CharacterEllipsis, text.TextTrimming));
    }

    [AvaloniaFact]
    public void Rows_AWrappedCategorysContinuation_ShowsNoLabelText()
    {
        List<(Control Cell, MenuCategory? Category)> cells =
        [
            .. Enumerable.Range(0, 8).Select(i => ((Control)new Border { Tag = i }, (MenuCategory?)MenuCategory.Taxi)),
            (new Border { Tag = 8 }, MenuCategory.Runway),
        ];

        StackPanel rows = QuickCommandStrip.Rows(cells);

        Assert.Equal(2, rows.Children.Count);
        Assert.Equal(["TAXI"], rows.Children[0].GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text));
        Assert.Equal(["RUNWAY"], rows.Children[1].GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text));
        Assert.Equal([6, 7, 8], TaggedCells(rows.Children[1]));
    }

    [AvaloniaFact]
    public void Rows_ALabelIsCutToItsSegmentsWidth_AndNeverWidensIt()
    {
        List<(Control Cell, MenuCategory? Category)> cells =
        [
            .. Enumerable.Range(0, 3).Select(i => (SizedCell(i), (MenuCategory?)MenuCategory.Runway)),
            (SizedCell(3), MenuCategory.TrackAndCoordination),
        ];

        StackPanel rows = LaidOut(QuickCommandStrip.Rows(cells));

        StackPanel row = Assert.IsType<StackPanel>(Assert.Single(rows.Children));
        Control runway = row.Children[0];
        Control track = row.Children[2];
        Assert.Equal((3 * 40) + (2 * 6), runway.Bounds.Width);
        Assert.Equal(40, track.Bounds.Width);
        foreach (Control segment in new[] { runway, track })
        {
            TextBlock label = Assert.Single(segment.GetLogicalDescendants().OfType<TextBlock>());
            Assert.True(
                label.Bounds.Width <= segment.Bounds.Width,
                $"'{label.Text}' is {label.Bounds.Width} px in a {segment.Bounds.Width} px segment"
            );
        }
    }

    [AvaloniaFact]
    public void Rows_AContinuationRowStandingAlone_HasNoLabelLine()
    {
        List<(Control Cell, MenuCategory? Category)> cells =
        [
            .. Enumerable.Range(0, 7).Select(i => (SizedCell(i), (MenuCategory?)MenuCategory.Taxi)),
            .. Enumerable.Range(7, 6).Select(i => (SizedCell(i), (MenuCategory?)MenuCategory.Runway)),
        ];

        StackPanel rows = LaidOut(QuickCommandStrip.Rows(cells));

        Assert.Equal(3, rows.Children.Count);
        Assert.Equal([6], TaggedCells(rows.Children[1]));
        Assert.Equal(40, rows.Children[1].Bounds.Height);
        Assert.True(rows.Children[0].Bounds.Height > 40);
        Assert.Empty(rows.Children[1].GetLogicalDescendants().OfType<DockPanel>());
    }

    private static Border SizedCell(int tag) =>
        new()
        {
            Tag = tag,
            Width = 40,
            Height = 40,
        };

    private static StackPanel LaidOut(StackPanel rows)
    {
        rows.Measure(Size.Infinity);
        rows.Arrange(new Rect(rows.DesiredSize));
        return rows;
    }

    private static List<int> TaggedCells(Control row) =>
        [.. row.GetLogicalDescendants().OfType<Border>().Where(b => b.Tag is int).Select(b => (int)b.Tag!)];

    /// <summary>
    /// Pointing at an icon that sends a command puts its name and command in the header's state-line row, in place of the
    /// state line, and the state line comes back the moment the pointer leaves the strip.
    /// </summary>
    [AvaloniaFact]
    public void HoverLeaf_HeaderLineNamesItAndItsCommand_UntilThePointerLeavesTheStrip()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        ContextMenu menu = Build(Fixture("taxiing"), Host());
        MenuItem strip = Strip(menu);
        QuickCommandLabelLine line = Line(menu);
        Assert.Equal("Taxiing", line.Header);
        Assert.Null(line.Label);
        Button holdPosition = QuickCommandStrip.Buttons(strip).Single(b => (string)b.Tag! == MenuIds.GroundHoldPosition);

        RaisePointer(holdPosition, InputElement.PointerEnteredEvent);

        Assert.Same(line, Line(menu));
        Assert.Equal($"{MenuCatalog.Get(MenuIds.GroundHoldPosition).Label} — HOLD", line.Label);
        Assert.NotEqual("Taxiing", line.Header);

        RaisePointer(strip, InputElement.PointerExitedEvent);

        Assert.Null(line.Label);
        Assert.Equal("Taxiing", line.Header);
    }

    /// <summary>The pointer crossing the gap between two icons leaves the first icon but not the strip: the state line stays away.</summary>
    [AvaloniaFact]
    public void PointerMovingToTheNextIcon_KeepsTheLabelUp()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        ContextMenu menu = Build(Fixture("taxiing"), Host());
        IReadOnlyList<Button> buttons = QuickCommandStrip.Buttons(Strip(menu));
        Button holdPosition = buttons.Single(b => (string)b.Tag! == MenuIds.GroundHoldPosition);
        Button follow = buttons.Single(b => (string)b.Tag! == MenuIds.GroundFollow);
        QuickCommandLabelLine line = Line(menu);
        string holdLabel = $"{MenuCatalog.Get(MenuIds.GroundHoldPosition).Label} — HOLD";

        RaisePointer(holdPosition, InputElement.PointerEnteredEvent);
        RaisePointer(holdPosition, InputElement.PointerExitedEvent);

        Assert.Equal(holdLabel, line.Label);

        RaisePointer(follow, InputElement.PointerEnteredEvent);

        Assert.Equal($"{MenuCatalog.Get(MenuIds.GroundFollow).Label} ›", line.Label);
    }

    [AvaloniaFact]
    public void HoverSubmenuIcon_LabelEndsWithAChevronAndShowsNoCommand()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        ContextMenu menu = Build(Fixture("taxiing"), Host());
        Button follow = QuickCommandStrip.Buttons(Strip(menu)).Single(b => (string)b.Tag! == MenuIds.GroundFollow);

        RaisePointer(follow, InputElement.PointerEnteredEvent);

        Assert.Equal($"{MenuCatalog.Get(MenuIds.GroundFollow).Label} ›", Line(menu).Label);
    }

    [AvaloniaFact]
    public void FocusOnAnIcon_ShowsItsLabel_AndFocusLeavingTheStripRestoresTheStateLine()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        ContextMenu menu = Build(Fixture("taxiing"), Host());
        IReadOnlyList<Button> buttons = QuickCommandStrip.Buttons(Strip(menu));
        Button holdPosition = buttons.Single(b => (string)b.Tag! == MenuIds.GroundHoldPosition);
        Button follow = buttons.Single(b => (string)b.Tag! == MenuIds.GroundFollow);
        QuickCommandLabelLine line = Line(menu);

        // Focus() on a button inside a context-menu popup returns false under the headless platform, so the focus
        // change is raised as the focus manager raises it: the old element loses focus, then the new one gets it.
        holdPosition.RaiseEvent(new FocusChangedEventArgs(InputElement.GotFocusEvent));
        Assert.Equal($"{MenuCatalog.Get(MenuIds.GroundHoldPosition).Label} — HOLD", line.Label);
        holdPosition.RaiseEvent(new FocusChangedEventArgs(InputElement.LostFocusEvent));
        follow.RaiseEvent(new FocusChangedEventArgs(InputElement.GotFocusEvent));
        Assert.Equal($"{MenuCatalog.Get(MenuIds.GroundFollow).Label} ›", line.Label);

        follow.RaiseEvent(new FocusChangedEventArgs(InputElement.LostFocusEvent));

        Assert.Null(line.Label);
        Assert.Equal("Taxiing", line.Header);
    }

    /// <summary>A label far longer than the state line neither widens nor heightens the menu: it scrolls inside the line instead.</summary>
    [AvaloniaFact]
    public void PointingAtAnIconWithALongLabel_LeavesTheMenusSizeAlone()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel ac = Fixture("final-ifr");
        ac.FiledAircraftType = "B738";
        ContextMenu menu = OpenLaidOut(Build(ac, Host()));
        Size atRest = menu.Bounds.Size;
        Button fas = QuickCommandStrip.Buttons(Strip(menu)).Single(b => (string)b.Tag! == MenuIds.SpeedFinalApproach);

        RaisePointer(fas, InputElement.PointerEnteredEvent);
        menu.UpdateLayout();

        Assert.StartsWith("Reduce to final approach speed - ", Line(menu).Label);
        Assert.Equal(atRest, menu.Bounds.Size);
        menu.Close();
    }

    /// <summary>An aircraft with nothing for the state line still gets a line of its height under the title, blank at rest.</summary>
    [AvaloniaFact]
    public void AircraftWithNoStateLine_KeepsABlankLineOfItsHeightThatTheLabelFills()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        // An en-route aircraft with no phase, altitude, speed or runway leaves every state segment out.
        AircraftModel bare = Fixture("ifr-enroute");
        bare.CurrentPhase = "";
        bare.Altitude = 0;
        bare.IndicatedAirspeed = 0;
        bare.AssignedRunway = "";
        ContextMenu withState = OpenLaidOut(Build(Fixture("ifr-enroute"), Host()));
        ContextMenu menu = OpenLaidOut(Build(bare, Host()));
        QuickCommandLabelLine line = Line(menu);
        Assert.Equal("", line.Header);
        Assert.Equal(Line(withState).Bounds.Height, line.Bounds.Height);
        MenuItem strip = Strip(menu);
        Button flyHeading = QuickCommandStrip.Buttons(strip).Single(b => (string)b.Tag! == MenuIds.HeadingFly);

        RaisePointer(flyHeading, InputElement.PointerEnteredEvent);

        Assert.StartsWith(MenuCatalog.Get(MenuIds.HeadingFly).Label, line.Label);

        RaisePointer(strip, InputElement.PointerExitedEvent);

        Assert.Null(line.Label);
        Assert.Equal("", line.Header);
        menu.Close();
        withState.Close();
    }

    [AvaloniaFact]
    public void TaxiNodePointMenu_KeepsALineUnderItsTitleThatTheLabelFills()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        var node = new GroundNodeDto(7, 37.7210, -122.2200, "TaxiwayIntersection", null, null, null);
        var ac = new AircraftModel
        {
            Callsign = "AAL202",
            AircraftType = "B738",
            FlightRules = "IFR",
            IsOnGround = true,
            CurrentPhase = "At Parking",
            Position = new LatLon(37.7200, -122.2190),
        };
        var point = new MenuPoint(new LatLon(node.Latitude, node.Longitude), node, null, [], null);

        ContextMenu menu = AircraftMenuBuilder.Build(ac, new MenuClick(ac.Callsign, null, point, []), new RecordingMenuHost(""), _ => []);

        QuickCommandLabelLine line = Line(menu);
        Assert.Equal("", line.Header);
        Assert.IsType<Separator>(menu.Items[2]);
        MenuItem strip = Strip(menu);
        RaisePointer(QuickCommandStrip.Buttons(strip)[0], InputElement.PointerEnteredEvent);
        Assert.False(string.IsNullOrEmpty(line.Label));
        RaisePointer(strip, InputElement.PointerExitedEvent);
        Assert.Null(line.Label);
    }

    [AvaloniaFact]
    public void RadarPointMenu_KeepsALineUnderItsTitle_AboveTheDetailRow()
    {
        var koak = new LatLon(37.7213, -122.2208);
        var ac = new AircraftModel
        {
            Callsign = "AAL202",
            AircraftType = "B738",
            FlightRules = "IFR",
            CurrentPhase = "",
            Situation = AircraftSituation.IfrEnroute,
            IsOnGround = false,
            Position = koak,
            Heading = new TrueHeading(120),
            Altitude = 33000,
            IndicatedAirspeed = 280,
            GroundSpeed = 280,
            NavigatingTo = "SUNOL",
        };
        var point = new MenuPoint(new LatLon(koak.Lat + 0.15, koak.Lon), null, null, [], null);
        var host = new RecordingMenuHost("") { PointDescription = "OAK090010" };

        ContextMenu menu = AircraftMenuBuilder.Build(ac, new MenuClick(ac.Callsign, null, point, []), host, _ => []);

        QuickCommandLabelLine line = Line(menu);
        Assert.Equal("", line.Header);
        Assert.StartsWith("OAK090010 · ", Assert.IsType<MenuItem>(menu.Items[2]).Header as string);
        Assert.IsType<Separator>(menu.Items[3]);
        Button flyHeading = QuickCommandStrip.Buttons(Strip(menu)).Single(b => (string)b.Tag! == MenuIds.PointFlyHeading);

        RaisePointer(flyHeading, InputElement.PointerEnteredEvent);

        Assert.StartsWith("Fly heading ", line.Label);
    }

    /// <summary>
    /// A label wider than the line holds still for 800 ms, then scrolls left at 60 px/s: 30 px half a second later.
    /// </summary>
    [AvaloniaFact]
    public void OverflowingLabel_WaitsThenScrollsLeftAtSixtyPixelsASecond()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel ac = Fixture("final-ifr");
        ac.FiledAircraftType = "B738";
        ContextMenu menu = OpenLaidOut(Build(ac, Host()));
        QuickCommandLabelLine line = Line(menu);
        var clock = new ManualClock();
        line.MarqueeClock = clock;
        Button fas = QuickCommandStrip.Buttons(Strip(menu)).Single(b => (string)b.Tag! == MenuIds.SpeedFinalApproach);

        RaisePointer(fas, InputElement.PointerEnteredEvent);
        RenderFrame(menu);
        clock.Advance(TimeSpan.FromMilliseconds(799));
        RenderFrame(menu);

        Assert.Equal(0, line.MarqueeOffset);

        clock.Advance(TimeSpan.FromMilliseconds(501));
        RenderFrame(menu);

        Assert.Equal(30, line.MarqueeOffset, 6);
        menu.Close();
    }

    /// <summary>
    /// The marquee's whole cycle for a label 60 px wider than its line, 1 s of travel in a 3.6 s cycle: still for 800 ms,
    /// left to 60 px, held there for 800 ms, back to 0 at the same speed, and the same again at each point of the next cycle.
    /// </summary>
    [Theory]
    [InlineData(500, 0)]
    [InlineData(1300, 30)]
    [InlineData(1800, 60)]
    [InlineData(2200, 60)]
    [InlineData(2590, 60)]
    [InlineData(2900, 42)]
    [InlineData(3500, 6)]
    public void MarqueeOffsetAt_HoldsAtTheFarEnd_ScrollsBack_AndRepeats(int elapsedMs, double expected)
    {
        const double overflow = 60;
        var cycle = TimeSpan.FromMilliseconds(3600);
        var elapsed = TimeSpan.FromMilliseconds(elapsedMs);

        Assert.Equal(expected, QuickCommandLabelLine.MarqueeOffsetAt(elapsed, overflow), 6);
        Assert.Equal(expected, QuickCommandLabelLine.MarqueeOffsetAt(elapsed + cycle, overflow), 6);
    }

    /// <summary>
    /// Pointing at another icon once a label has scrolled shows the new label from the left, and pointing back at the first
    /// starts its scroll again: still for the pause, not where it had got to.
    /// </summary>
    [AvaloniaFact]
    public void PointingAtAnotherIcon_AfterThePause_StartsTheLabelFromTheLeftAgain()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel ac = Fixture("final-ifr");
        ac.FiledAircraftType = "B738";
        ContextMenu menu = OpenLaidOut(Build(ac, Host()));
        QuickCommandLabelLine line = Line(menu);
        var clock = new ManualClock();
        line.MarqueeClock = clock;
        IReadOnlyList<Button> buttons = QuickCommandStrip.Buttons(Strip(menu));
        Button fas = buttons.Single(b => (string)b.Tag! == MenuIds.SpeedFinalApproach);
        Button goAround = buttons.Single(b => (string)b.Tag! == MenuIds.TowerGoAround);

        RaisePointer(fas, InputElement.PointerEnteredEvent);
        RenderFrame(menu);
        clock.Advance(TimeSpan.FromMilliseconds(1300));
        RenderFrame(menu);
        string? fasLabel = line.Label;
        Assert.Equal(30, line.MarqueeOffset, 6);

        RaisePointer(goAround, InputElement.PointerEnteredEvent);
        RenderFrame(menu);

        Assert.NotEqual(fasLabel, line.Label);
        Assert.Equal(0, line.MarqueeOffset);

        RaisePointer(fas, InputElement.PointerEnteredEvent);
        RenderFrame(menu);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        RenderFrame(menu);

        Assert.Equal(fasLabel, line.Label);
        Assert.Equal(0, line.MarqueeOffset);
        menu.Close();
    }

    [AvaloniaFact]
    public void FittingLabel_DoesNotMove()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        ContextMenu menu = OpenLaidOut(Build(Fixture("final-ifr"), Host()));
        QuickCommandLabelLine line = Line(menu);
        var clock = new ManualClock();
        line.MarqueeClock = clock;
        Button goAround = QuickCommandStrip.Buttons(Strip(menu)).Single(b => (string)b.Tag! == MenuIds.TowerGoAround);

        RaisePointer(goAround, InputElement.PointerEnteredEvent);
        RenderFrame(menu);
        clock.Advance(TimeSpan.FromSeconds(2));
        RenderFrame(menu);

        Assert.NotNull(line.Label);
        Assert.Equal(0, line.MarqueeOffset);
    }

    /// <summary>
    /// An overflowing label runs its frames on a UI-owned timer, not on the clock: the timer starts while the label shows
    /// and stops when the menu closes, and a frame after the close leaves the label where it was.
    /// </summary>
    [AvaloniaFact]
    public void OverflowingLabel_UsesOnlyUiOwnedFramesAndStopsAfterDetach()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel ac = Fixture("final-ifr");
        ac.FiledAircraftType = "B738";
        ContextMenu menu = OpenLaidOut(Build(ac, Host()));
        QuickCommandLabelLine line = Line(menu);
        var clock = new ManualClock();
        line.MarqueeClock = clock;
        Button fas = QuickCommandStrip.Buttons(Strip(menu)).Single(b => (string)b.Tag! == MenuIds.SpeedFinalApproach);

        RaisePointer(fas, InputElement.PointerEnteredEvent);
        RenderFrame(menu);
        Assert.True(line.MarqueeFramesRunning);
        Assert.Equal(0, clock.LiveTimerCount);

        clock.Advance(TimeSpan.FromMilliseconds(1300));
        RenderFrame(menu);
        Assert.Equal(30, line.MarqueeOffset, 6);

        menu.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.False(line.MarqueeFramesRunning);

        clock.Advance(TimeSpan.FromMilliseconds(500));
        line.RenderMarqueeFrame();

        Assert.Equal(30, line.MarqueeOffset, 6);
    }

    /// <summary>Lays the menu out and renders one deterministic marquee frame against the test clock.</summary>
    private static void RenderFrame(ContextMenu menu)
    {
        menu.UpdateLayout();
        Line(menu).RenderMarqueeFrame();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The header line under the title the strip writes its label into.</summary>
    private static QuickCommandLabelLine Line(ContextMenu menu) => Assert.IsType<QuickCommandLabelLine>(menu.Items[1]);

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
                new TaxiRoute { Segments = [], HoldShortPoints = [] },
                new LatLon(37.72, -122.22)
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

    /// <summary>
    /// Opens <paramref name="menu"/> as <see cref="OpenInWindow"/> does and lays it out, so its rows sit in the menu's visual tree.
    /// </summary>
    private static ContextMenu OpenLaidOut(ContextMenu menu)
    {
        OpenInWindow(menu);
        Dispatcher.UIThread.RunJobs();
        menu.UpdateLayout();
        return menu;
    }

    /// <summary>The colour <paramref name="button"/>'s template draws its background in, after every style has applied.</summary>
    private static Color BackgroundColor(Button button)
    {
        ContentPresenter presenter = button.GetVisualDescendants().OfType<ContentPresenter>().Single(p => p.Name == "PART_ContentPresenter");
        return Assert.IsType<ISolidColorBrush>(presenter.Background, exactMatch: false).Color;
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
