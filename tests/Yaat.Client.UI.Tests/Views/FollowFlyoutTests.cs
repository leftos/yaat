using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Sim.Commands;
using Yaat.Sim.Situation;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Follow… flyout, one builder for All Commands and the quick-command strip: a filter box, a hint line and one
/// scrolling list about eight rows tall under the sticky Moving and Parked or holding titles, nearest first in each,
/// live-traffic shadows included. Typing narrows the list by callsign; Enter sends the top match, or <c>FOLLOWG</c> with
/// the typed callsign when nothing matches.
/// </summary>
public class FollowFlyoutTests
{
    private const string Callsign = "SWA104";
    private const string Initials = "AB";

    /// <summary>Seven moving and four parked aircraft, 300 ft apart, AC01 nearest, handed over farthest first; AC03 a live-feed shadow.</summary>
    private static readonly MenuGroundTrafficRow[] Eleven =
    [
        .. Enumerable
            .Range(1, 11)
            .Select(i => new MenuGroundTrafficRow($"AC{i:00}", "B738", i * 300, (i % 2 == 1) ? "taxiing on W" : "at parking · gate 1")
            {
                IsMoving = (i % 2 == 1) || (i == 10),
                IsSurfaceShadow = i == 3,
                MeetPoint = null,
            })
            .OrderByDescending(row => row.DistanceFeet),
    ];

    private static readonly string[] MovingOrder = ["AC01", "AC03", "AC05", "AC07", "AC09", "AC10", "AC11"];
    private static readonly string[] ParkedOrder = ["AC02", "AC04", "AC06", "AC08"];

    [AvaloniaFact]
    public void AllCommands_HoldsTheBoxTheHintAndOneListUnderMovingThenParked_NearestFirst()
    {
        MenuItem follow = Follow(BuildMenu(HostWith(Eleven)));

        Assert.Equal(3, follow.Items.Count);
        TextBox box = Box(follow);
        Assert.Equal(GroundTrafficFlyout.FollowFilterPlaceholder, box.PlaceholderText);
        Assert.Equal(GroundTrafficFlyout.FollowTopMatchHint, Hint(follow).Text);
        Assert.Equal(
            [GroundTrafficFlyout.FollowMovingTitle, .. MovingOrder, GroundTrafficFlyout.FollowParkedTitle, .. ParkedOrder],
            ListEntries(follow, visibleOnly: true)
        );
        Assert.Equal("AC01 · B738 · taxiing on W · ~300 ft — FOLLOWG AC01", RowButtons(follow)[0].Content!.ToString());
        Assert.Equal(GroundTrafficFlyout.FollowShownRows * GroundTrafficFlyout.FollowRowHeight, Scroll(follow).MaxHeight);
    }

    [AvaloniaFact]
    public void Typing_NarrowsByCallsignIgnoringCase_AndHidesASectionWithNoRowLeft()
    {
        MenuItem follow = Follow(BuildMenu(HostWith(Eleven)));

        Box(follow).Text = "c1";

        Assert.Equal([GroundTrafficFlyout.FollowMovingTitle, "AC10", "AC11"], ListEntries(follow, visibleOnly: true));
        Assert.Equal(GroundTrafficFlyout.FollowTopMatchHint, Hint(follow).Text);
    }

    [AvaloniaFact]
    public void MatchingNothing_HidesEveryRowAndTitle_AndTheHintSaysEnterSendsWhatWasTyped()
    {
        MenuItem follow = Follow(BuildMenu(HostWith(Eleven)));

        Box(follow).Text = "n123";

        Assert.Empty(ListEntries(follow, visibleOnly: true));
        Assert.Equal("No aircraft matches; Enter sends FOLLOWG N123 as typed.", Hint(follow).Text);
    }

    [AvaloniaFact]
    public void Enter_SendsTheTopMatch()
    {
        RecordingMenuHost host = HostWith(Eleven);
        MenuItem follow = Follow(BuildMenu(host));
        Box(follow).Text = "0";

        PressEnter(Box(follow));

        Assert.Equal([(Callsign, "FOLLOWG AC01", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void Enter_WithNoMatch_SendsFOLLOWGWithTheTypedCallsign()
    {
        RecordingMenuHost host = HostWith(Eleven);
        MenuItem follow = Follow(BuildMenu(host));
        Box(follow).Text = " n123 ";

        PressEnter(Box(follow));

        Assert.Equal([(Callsign, "FOLLOWG N123", Initials)], host.Sent);
    }

    /// <summary>A callsign has no spaces: what follows the first typed word is left out of the hint and the command.</summary>
    [AvaloniaFact]
    public void Enter_WithNoMatch_SendsOnlyTheFirstTypedWord()
    {
        RecordingMenuHost host = HostWith(Eleven);
        MenuItem follow = Follow(BuildMenu(host));
        Box(follow).Text = "n1 23";

        Assert.Equal("No aircraft matches; Enter sends FOLLOWG N1 as typed.", Hint(follow).Text);
        PressEnter(Box(follow));

        Assert.Equal([(Callsign, "FOLLOWG N1", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void RowClick_SendsFOLLOWG_AndPointingHighlightsItsAircraft()
    {
        RecordingMenuHost host = HostWith(Eleven);
        Button row = RowButtons(Follow(BuildMenu(host))).Single(button => ((GroundTrafficFlyoutRow)button.Content!).Traffic.Callsign == "AC04");

        RaisePointer(row, InputElement.PointerEnteredEvent);
        RaisePointer(row, InputElement.PointerExitedEvent);
        row.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(["AC04", null], host.Highlights);
        Assert.Equal([(Callsign, "FOLLOWG AC04", Initials)], host.Sent);
    }

    /// <summary>The strip's flyout opens with the caret in the filter box: typed text lands there and narrows the list.</summary>
    [AvaloniaFact]
    public void StripFlyout_OpensWithFocusInTheFilterBox_SoTypingFilters()
    {
        (_, MenuFlyout flyout) = OpenStripFlyout(HostWith(Eleven));
        TextBox box = FlyoutBox(flyout);

        Assert.True(box.IsFocused, "the filter box did not take focus when the flyout opened");
        TopLevel.GetTopLevel(box)!.KeyTextInput("c1");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("c1", box.Text);
        Assert.Equal(
            ["AC10", "AC11"],
            FlyoutRowButtons(flyout).Where(button => button.IsVisible).Select(button => ((GroundTrafficFlyoutRow)button.Content!).Traffic.Callsign)
        );
    }

    [AvaloniaFact]
    public void StripFlyout_DownFromTheBoxReachesTheFirstRow_UpReturnsToTheBox()
    {
        (_, MenuFlyout flyout) = OpenStripFlyout(HostWith(Eleven));
        TextBox box = FlyoutBox(flyout);
        Assert.True(box.IsFocused, "the filter box did not take focus when the flyout opened");
        Button first = FlyoutRowButtons(flyout)[0];

        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down });
        Dispatcher.UIThread.RunJobs();
        Assert.True(first.IsFocused, "Down from the box did not reach the first row");

        first.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Up });
        Dispatcher.UIThread.RunJobs();
        Assert.True(box.IsFocused, "Up from the first row did not return to the box");
    }

    [AvaloniaFact]
    public void StripFlyout_ScrolledPastTheMovingTitle_ShowsItStuckAtTheTop()
    {
        (_, MenuFlyout flyout) = OpenStripFlyout(HostWith(Eleven));
        ScrollViewer scroll = FlyoutScroll(flyout);
        Border sticky = FlyoutSticky(flyout);
        Assert.False(sticky.IsVisible);

        scroll.Offset = new Vector(0, 2 * GroundTrafficFlyout.FollowRowHeight);
        Layout(flyout);

        Assert.True(sticky.IsVisible, $"offset {scroll.Offset.Y}, extent {scroll.Extent.Height}, viewport {scroll.Viewport.Height}");
        Assert.Equal(GroundTrafficFlyout.FollowMovingTitle, ((TextBlock)sticky.Child!).Text);
    }

    /// <summary>
    /// Scrolled into Moving, a filter that hides every moving row lifts the Parked or holding title above the list's top
    /// while the scroll offset stays put: the stuck title then names Parked or holding.
    /// </summary>
    [AvaloniaFact]
    public void StripFlyout_FilterLiftsTheParkedTitlePastTheTop_TheStuckTitleNamesIt()
    {
        MenuGroundTrafficRow[] traffic =
        [
            .. Enumerable
                .Range(1, 10)
                .SelectMany(i =>
                    new[]
                    {
                        new MenuGroundTrafficRow($"MOV{i:00}", "B738", i * 100, "taxiing on W")
                        {
                            IsMoving = true,
                            IsSurfaceShadow = false,
                            MeetPoint = null,
                        },
                        new MenuGroundTrafficRow($"PRK{i:00}", "B738", 2000 + (i * 100), "at parking · gate 1")
                        {
                            IsMoving = false,
                            IsSurfaceShadow = false,
                            MeetPoint = null,
                        },
                    }
                ),
        ];
        (_, MenuFlyout flyout) = OpenStripFlyout(HostWith(traffic));
        ScrollViewer scroll = FlyoutScroll(flyout);
        Border sticky = FlyoutSticky(flyout);
        double offset = 2 * GroundTrafficFlyout.FollowRowHeight;
        scroll.Offset = new Vector(0, offset);
        Layout(flyout);
        Assert.Equal(GroundTrafficFlyout.FollowMovingTitle, ((TextBlock)sticky.Child!).Text);

        FlyoutBox(flyout).Text = "prk";
        Layout(flyout);

        Assert.Equal(offset, scroll.Offset.Y);
        Assert.True(sticky.IsVisible, $"offset {scroll.Offset.Y}, extent {scroll.Extent.Height}, viewport {scroll.Viewport.Height}");
        Assert.Equal(GroundTrafficFlyout.FollowParkedTitle, ((TextBlock)sticky.Child!).Text);
    }

    /// <summary>
    /// Down from the box nine times walks to the ninth row, scrolling it into view; Up back to the first row scrolls each
    /// row into view below the stuck section title, never under it.
    /// </summary>
    [AvaloniaFact]
    public void StripFlyout_ArrowKeys_ScrollTheFocusedRowIntoView_BelowTheStuckTitle()
    {
        (_, MenuFlyout flyout) = OpenStripFlyout(HostWith(Eleven));
        ScrollViewer scroll = FlyoutScroll(flyout);
        Border sticky = FlyoutSticky(flyout);
        Assert.True(FlyoutBox(flyout).IsFocused, "the filter box did not take focus when the flyout opened");

        for (int i = 0; i < 9; i++)
        {
            AssertInViewBelowTheStuckTitle(Press(flyout, Key.Down), scroll, sticky);
        }

        Assert.Equal("AC04", CallsignOf(Focused(flyout)));
        for (int i = 0; i < 8; i++)
        {
            AssertInViewBelowTheStuckTitle(Press(flyout, Key.Up), scroll, sticky);
        }

        Assert.Equal("AC01", CallsignOf(Focused(flyout)));
    }

    [AvaloniaFact]
    public void StripFlyout_DownAndUp_SkipTheRowsTheFilterHides_AndEnterSendsTheFocusedRow()
    {
        RecordingMenuHost host = HostWith(Eleven);
        (_, MenuFlyout flyout) = OpenStripFlyout(host);
        FlyoutBox(flyout).Text = "ac0";
        Layout(flyout);

        List<string> down = [.. Enumerable.Range(0, 6).Select(_ => CallsignOf(Press(flyout, Key.Down)))];
        string up = CallsignOf(Press(flyout, Key.Up));
        Press(flyout, Key.Enter);

        Assert.Equal(["AC01", "AC03", "AC05", "AC07", "AC09", "AC02"], down);
        Assert.Equal("AC09", up);
        Assert.Equal([(Callsign, "FOLLOWG AC09", Initials)], host.Sent);
    }

    /// <summary>
    /// The pointer on a row inside the list leaves the menu row hosting the list as it was: the whole list does not take
    /// the menu's hover or selected background.
    /// </summary>
    [AvaloniaFact]
    public void StripFlyout_PointerOnARow_LeavesTheListsMenuRowBackgroundAsAtRest()
    {
        (_, MenuFlyout flyout) = OpenStripFlyout(HostWith(Eleven));
        MenuItem listRow = flyout
            .Items.OfType<MenuItem>()
            .Single(item => item.GetVisualDescendants().OfType<ScrollViewer>().Any(viewer => viewer.Name == GroundTrafficFlyout.FollowListName));
        Border layoutRoot = Assert.Single(listRow.GetVisualDescendants().OfType<Border>(), border => border.Name == "PART_LayoutRoot");
        IBrush? atRest = layoutRoot.Background;
        Button row = FlyoutRowButtons(flyout)[1];
        TopLevel top = TopLevel.GetTopLevel(row)!;

        top.MouseMove(row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), top)!.Value);
        Layout(flyout);

        Assert.True(row.IsPointerOver, "the pointer did not reach the row");
        Assert.True(
            Assert.IsAssignableFrom<ISolidColorBrush>(atRest).Color == (layoutRoot.Background as ISolidColorBrush)?.Color,
            $"the list's menu row went from {atRest} to {layoutRoot.Background}; classes {string.Join(' ', listRow.Classes)}"
        );
    }

    [AvaloniaFact]
    public void StripFlyout_RowClick_SendsAndClosesTheFlyoutAndTheMenu()
    {
        RecordingMenuHost host = HostWith(Eleven);
        (ContextMenu menu, MenuFlyout flyout) = OpenStripFlyout(host);
        Button first = flyout
            .Items.OfType<MenuItem>()
            .SelectMany(item => item.GetVisualDescendants())
            .OfType<Button>()
            .First(button => button.Content is GroundTrafficFlyoutRow);

        first.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal([(Callsign, "FOLLOWG AC01", Initials)], host.Sent);
        Assert.False(flyout.IsOpen);
        Assert.False(menu.IsOpen);
    }

    [AvaloniaFact]
    public void StripButton_NoOtherGroundTraffic_StaysFaded_OpensNothing_AndNamesTheReason()
    {
        RecordingMenuHost host = HostWith([]);
        ContextMenu menu = OpenInWindow(BuildMenu(host));
        Button follow = FollowButton(menu);

        Assert.Equal(MenuGlyphRowTemplate.DimmedOpacity, follow.Opacity);
        Assert.Null(FlyoutBase.GetAttachedFlyout(follow));
        follow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Empty(host.Sent);
        Assert.True(menu.IsOpen);
        QuickCommandLabelLine line = Assert.IsType<QuickCommandLabelLine>(menu.Items[1]);

        RaisePointer(follow, InputElement.PointerEnteredEvent);

        Assert.Equal("Follow · no other aircraft on the ground", line.Label);
        menu.Close();
    }

    // --- Harness ----------------------------------------------------------------------------

    private static RecordingMenuHost HostWith(IEnumerable<MenuGroundTrafficRow> traffic)
    {
        var host = new RecordingMenuHost("")
        {
            Session = new(
                Initials,
                false,
                VfrCommandsForIfr.None,
                _ => [new CatalogQuickCommandEntry(MenuIds.GroundFollow, null)],
                SoloAdvisoryPrefill.TrueValues,
                false
            ),
        };
        host.GroundTraffic.AddRange(traffic);
        return host;
    }

    private static ContextMenu BuildMenu(RecordingMenuHost host) =>
        AircraftMenuBuilder.Build(
            new AircraftModel
            {
                Callsign = Callsign,
                AircraftType = "B738",
                FlightRules = "IFR",
                IsOnGround = true,
                CurrentPhase = "Taxiing",
                Situation = AircraftSituation.Taxiing,
                HasActiveTaxiRoute = true,
            },
            new MenuClick(Callsign, null, null, []),
            host,
            _ => []
        );

    private static MenuItem Follow(ContextMenu menu) =>
        Item(AllCommandsPath.Group(menu, MenuCategory.Taxi).Items, MenuCatalog.Get(MenuIds.GroundFollow).Label);

    private static MenuItem Item(ItemCollection items, string header) =>
        Assert.Single(items.OfType<MenuItem>(), item => (item.Header as string) == header);

    /// <summary>The filter box, from the first row's header (the box sits beside the glyph).</summary>
    private static TextBox Box(MenuItem follow) => Assert.Single(((Panel)((MenuItem)follow.Items[0]!).Header!).Children.OfType<TextBox>());

    private static TextBlock Hint(MenuItem follow) => Assert.IsType<TextBlock>(((MenuItem)follow.Items[1]!).Header);

    private static ScrollViewer Scroll(MenuItem follow) => FollowListItems.Scroll(follow);

    private static List<Control> ListChildren(MenuItem follow) => [.. ((Panel)Scroll(follow).Content!).Children];

    private static List<Button> RowButtons(MenuItem follow) => [.. ListChildren(follow).OfType<Button>()];

    /// <summary>The list's section titles and row callsigns in order, only those showing when <paramref name="visibleOnly"/>.</summary>
    private static List<string> ListEntries(MenuItem follow, bool visibleOnly) =>
        [
            .. ListChildren(follow)
                .Where(child => !visibleOnly || child.IsVisible)
                .Select(child =>
                    child switch
                    {
                        TextBlock title => title.Text!,
                        Button { Content: GroundTrafficFlyoutRow row } => row.Traffic.Callsign,
                        _ => throw new Xunit.Sdk.XunitException($"unexpected list child {child.GetType().Name}"),
                    }
                ),
        ];

    private static Button FollowButton(ContextMenu menu) =>
        QuickCommandStrip
            .Buttons(Assert.Single(menu.Items.OfType<MenuItem>(), QuickCommandStrip.IsStrip))
            .Single(button => (string)button.Tag! == MenuIds.GroundFollow);

    private static ContextMenu OpenInWindow(ContextMenu menu)
    {
        var window = new Window { Width = 600, Height = 900 };
        window.Show();
        menu.Open(window);
        Dispatcher.UIThread.RunJobs();
        menu.UpdateLayout();
        return menu;
    }

    private static (ContextMenu Menu, MenuFlyout Flyout) OpenStripFlyout(RecordingMenuHost host)
    {
        ContextMenu menu = OpenInWindow(BuildMenu(host));
        Button button = FollowButton(menu);
        QuickCommandStrip.OpenSubmenu(button);
        Dispatcher.UIThread.RunJobs();
        MenuFlyout flyout = Assert.IsType<MenuFlyout>(FlyoutBase.GetAttachedFlyout(button));
        Assert.True(flyout.IsOpen);
        (flyout.Popup.Child as Control)?.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return (menu, flyout);
    }

    /// <summary>The filter box in the strip's open flyout.</summary>
    private static TextBox FlyoutBox(MenuFlyout flyout) =>
        Assert.Single(flyout.Items.OfType<MenuItem>().SelectMany(item => item.GetVisualDescendants()).OfType<TextBox>());

    /// <summary>The aircraft rows in the strip's open flyout, in list order.</summary>
    private static List<Button> FlyoutRowButtons(MenuFlyout flyout) =>
        [
            .. flyout
                .Items.OfType<MenuItem>()
                .SelectMany(item => item.GetVisualDescendants())
                .OfType<Button>()
                .Where(button => button.Content is GroundTrafficFlyoutRow),
        ];

    private static IEnumerable<Visual> FlyoutVisuals(MenuFlyout flyout) =>
        flyout.Items.OfType<MenuItem>().SelectMany(item => item.GetVisualDescendants());

    /// <summary>The list's scroll viewer in the strip's open flyout.</summary>
    private static ScrollViewer FlyoutScroll(MenuFlyout flyout) =>
        Assert.Single(FlyoutVisuals(flyout).OfType<ScrollViewer>(), viewer => viewer.Name == GroundTrafficFlyout.FollowListName);

    /// <summary>The border at the list's top showing the section title scrolled past, in the strip's open flyout.</summary>
    private static Border FlyoutSticky(MenuFlyout flyout) =>
        Assert.Single(FlyoutVisuals(flyout).OfType<Border>(), border => border.Name == GroundTrafficFlyout.FollowStickyTitleName);

    private static void Layout(MenuFlyout flyout)
    {
        (flyout.Popup.Child as Control)?.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static IInputElement? Focused(MenuFlyout flyout) => TopLevel.GetTopLevel(flyout.Popup.Child)?.FocusManager?.GetFocusedElement();

    /// <summary>Presses <paramref name="key"/> on whatever holds focus in the flyout and returns what holds it after.</summary>
    private static IInputElement? Press(MenuFlyout flyout, Key key)
    {
        Control focused = Assert.IsAssignableFrom<Control>(Focused(flyout));
        focused.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
        Layout(flyout);
        return Focused(flyout);
    }

    private static string CallsignOf(IInputElement? row) => ((GroundTrafficFlyoutRow)Assert.IsType<Button>(row).Content!).Traffic.Callsign;

    /// <summary>Asserts <paramref name="focused"/>, a row, lies inside the list's viewport and wholly below the stuck title.</summary>
    private static void AssertInViewBelowTheStuckTitle(IInputElement? focused, ScrollViewer scroll, Border sticky)
    {
        Button row = Assert.IsType<Button>(focused);
        double top = row.TranslatePoint(default, scroll)!.Value.Y;
        double stuckBottom = sticky.IsVisible ? sticky.Bounds.Height : 0;
        string where =
            $"{CallsignOf(row)} spans {top:F1}..{top + row.Bounds.Height:F1} in a {scroll.Viewport.Height:F1} px viewport, "
            + $"the stuck title {stuckBottom:F1} px tall, offset {scroll.Offset.Y:F1}";
        Assert.True(top >= stuckBottom - 0.5, where);
        Assert.True(top + row.Bounds.Height <= scroll.Viewport.Height + 0.5, where);
    }

    private static void PressEnter(TextBox box) => box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });

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
}
