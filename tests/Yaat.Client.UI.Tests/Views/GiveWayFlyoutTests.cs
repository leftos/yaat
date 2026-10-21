using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
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
/// The Give way to… flyout, one builder for All Commands and the quick-command strip: one list of the other ground
/// aircraft nearest first, a moving or parked mark on each row, the first eight then "Show n more", which shows the rest
/// in place; a row sends <c>GW</c> with its callsign and highlights its aircraft while pointed at or focused. With no
/// other aircraft on the ground the strip button stays, faded, and opens nothing.
/// </summary>
public class GiveWayFlyoutTests
{
    private const string Callsign = "SWA104";
    private const string Initials = "AB";

    /// <summary>Thirteen aircraft, 300 ft apart, the odd ones moving, handed over out of distance order.</summary>
    private static readonly MenuGroundTrafficRow[] Thirteen =
    [
        .. Enumerable
            .Range(1, 13)
            .Select(i => new MenuGroundTrafficRow($"AC{i:00}", "B738", i * 300, (i % 2 == 1) ? "taxiing on W" : "at parking · gate 1")
            {
                IsMoving = i % 2 == 1,
                IsSurfaceShadow = false,
                MeetPoint = null,
            })
            .OrderByDescending(row => row.DistanceFeet),
    ];

    [AvaloniaFact]
    public void AllCommands_ListsNearestFirstWithAMark_TheFirstEightThenShowMore()
    {
        RecordingMenuHost host = HostWith(Thirteen);

        List<object?> items = [.. GiveWay(BuildMenu(host)).Items];

        GroundTrafficFlyoutTitle title = Assert.IsType<GroundTrafficFlyoutTitle>(Assert.IsType<MenuItem>(items[0]).Header);
        Assert.Equal((GroundTrafficFlyout.GiveWayTitle, GroundTrafficFlyout.GiveWayLegend), (title.Title, title.Note));
        Assert.False(((MenuItem)items[0]!).IsEnabled);
        List<GroundTrafficFlyoutRow> rows = Rows(items);
        Assert.Equal([.. Enumerable.Range(1, 8).Select(i => $"AC{i:00}")], rows.Select(row => row.Traffic.Callsign));
        Assert.All(rows, row => Assert.True(row.ShowsMark));
        Assert.Equal([true, false, true, false, true, false, true, false], rows.Select(row => row.Traffic.IsMoving));
        Assert.Equal("AC01 · B738 · taxiing on W · ~300 ft — GW AC01", rows[0].ToString());
        GroundTrafficMoreRow more = Assert.IsType<GroundTrafficMoreRow>(Assert.IsType<MenuItem>(items[^1]).Header);
        Assert.Equal("Show 5 more · up to ~3,900 ft", more.ToString());
        Assert.Equal(10, items.Count);
    }

    [AvaloniaFact]
    public void ShowMore_ShowsTheRestInPlace_WithoutANestedSubmenu_UntilCollapsed()
    {
        RecordingMenuHost host = HostWith(Thirteen);
        MenuItem giveWay = GiveWay(BuildMenu(host));
        MenuItem more = MoreRow(giveWay.Items);
        Assert.True(more.StaysOpenOnClick);

        Click(more);

        Assert.Equal([.. Enumerable.Range(1, 13).Select(i => $"AC{i:00}")], Rows(giveWay.Items).Select(row => row.Traffic.Callsign));
        Assert.All(giveWay.Items.OfType<MenuItem>(), item => Assert.Empty(item.Items));
        Assert.Equal("Show fewer", MoreRow(giveWay.Items).Header!.ToString());
        Assert.Empty(host.Sent);

        Click(MoreRow(giveWay.Items));

        Assert.Equal(8, Rows(giveWay.Items).Count);
        Assert.Equal("Show 5 more · up to ~3,900 ft", MoreRow(giveWay.Items).Header!.ToString());
    }

    [AvaloniaFact]
    public void EightOrFewer_ShowsNoShowMoreRow()
    {
        RecordingMenuHost host = HostWith([.. Thirteen.Where(row => row.DistanceFeet <= 2400)]);

        MenuItem giveWay = GiveWay(BuildMenu(host));

        Assert.Equal(8, Rows(giveWay.Items).Count);
        Assert.DoesNotContain(giveWay.Items.OfType<MenuItem>(), item => item.Header is GroundTrafficMoreRow);
    }

    [AvaloniaFact]
    public void AllCommands_RowClick_SendsGWWithItsCallsign()
    {
        RecordingMenuHost host = HostWith(Thirteen);

        Click(RowItem(GiveWay(BuildMenu(host)).Items, "AC03"));

        Assert.Equal([(Callsign, "GW AC03", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void GiveWay_LeavesOutSurfaceShadows()
    {
        MenuGroundTrafficRow shadow = new("SHD1", "B738", 100, "taxiing on P · live feed")
        {
            IsMoving = true,
            IsSurfaceShadow = true,
            MeetPoint = null,
        };
        RecordingMenuHost host = HostWith([shadow, .. Thirteen.Take(2)]);

        Assert.DoesNotContain("SHD1", Rows(GiveWay(BuildMenu(host)).Items).Select(row => row.Traffic.Callsign));
    }

    /// <summary>
    /// A row highlights its aircraft while pointed at or focused. With the pointer on one row and focus on another, the
    /// pointed row's aircraft is highlighted and, once the pointer leaves, the focused row's again; focus passing through
    /// another row leaves the pointed row's highlight in place.
    /// </summary>
    [AvaloniaFact]
    public void RowPointedAtOrFocused_HighlightsItsAircraft_AndLeavingClearsIt()
    {
        RecordingMenuHost host = HostWith(Thirteen);
        List<object?> items = [.. GiveWay(BuildMenu(host)).Items];
        MenuItem row = RowItem(items, "AC02");

        RaisePointer(row, InputElement.PointerEnteredEvent);
        RaisePointer(row, InputElement.PointerExitedEvent);
        row.RaiseEvent(new FocusChangedEventArgs(InputElement.GotFocusEvent));
        row.RaiseEvent(new FocusChangedEventArgs(InputElement.LostFocusEvent));

        Assert.Equal(["AC02", null, "AC02", null], host.Highlights);

        host.Highlights.Clear();
        MenuItem focusedRow = row;
        MenuItem pointedRow = RowItem(items, "AC03");
        focusedRow.RaiseEvent(new FocusChangedEventArgs(InputElement.GotFocusEvent));
        RaisePointer(pointedRow, InputElement.PointerEnteredEvent);
        RaisePointer(pointedRow, InputElement.PointerExitedEvent);
        Assert.Equal(["AC02", "AC03", "AC02"], host.Highlights);
        focusedRow.RaiseEvent(new FocusChangedEventArgs(InputElement.LostFocusEvent));
        Assert.Null(host.Highlights[^1]);

        host.Highlights.Clear();
        RaisePointer(focusedRow, InputElement.PointerEnteredEvent);
        pointedRow.RaiseEvent(new FocusChangedEventArgs(InputElement.GotFocusEvent));
        pointedRow.RaiseEvent(new FocusChangedEventArgs(InputElement.LostFocusEvent));
        Assert.Equal(["AC02", "AC02", "AC02"], host.Highlights);
    }

    /// <summary>A row "Show fewer" took away sends nothing, even when a click reaches it after.</summary>
    [AvaloniaFact]
    public void AllCommands_RowFoldedAway_SendsNothingWhenClicked()
    {
        RecordingMenuHost host = HostWith(Thirteen);
        MenuItem giveWay = GiveWay(BuildMenu(host));
        Click(MoreRow(giveWay.Items));
        MenuItem folded = RowItem(giveWay.Items, "AC12");

        Click(MoreRow(giveWay.Items));
        Click(folded);

        Assert.DoesNotContain(folded, giveWay.Items);
        Assert.Empty(host.Sent);
    }

    /// <summary>Traffic that left after the menu was built: the strip's flyout opens on the muted no-traffic line.</summary>
    [AvaloniaFact]
    public void StripFlyout_TrafficGoneByTheTimeItOpens_ShowsTheNoTrafficLine()
    {
        RecordingMenuHost host = HostWith(Thirteen);
        ContextMenu menu = OpenInWindow(BuildMenu(host));
        host.GroundTraffic.Clear();
        Button button = GiveWayButton(menu);

        QuickCommandStrip.OpenSubmenu(button);
        Dispatcher.UIThread.RunJobs();

        MenuFlyout flyout = Assert.IsType<MenuFlyout>(FlyoutBase.GetAttachedFlyout(button));
        MenuItem line = Assert.IsType<MenuItem>(Assert.Single(flyout.Items));
        Assert.Equal("No other aircraft on the ground.", line.Header);
        Assert.False(line.IsEnabled);
    }

    [AvaloniaFact]
    public void ShowMore_FoldsBackToEight_WhenTheStripFlyoutCloses()
    {
        RecordingMenuHost host = HostWith(Thirteen);
        (_, MenuFlyout flyout) = OpenStripFlyout(host);
        Click(MoreRow(flyout.Items));
        Assert.Equal(13, Rows(flyout.Items).Count);

        flyout.Hide();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(8, Rows(flyout.Items).Count);
        Assert.Equal("Show 5 more · up to ~3,900 ft", MoreRow(flyout.Items).Header!.ToString());
    }

    [AvaloniaFact]
    public void ShowMore_FoldsBackToEight_WhenTheSubmenuCloses()
    {
        RecordingMenuHost host = HostWith(Thirteen);
        MenuItem giveWay = GiveWay(BuildMenu(host));
        giveWay.IsSubMenuOpen = true;
        Click(MoreRow(giveWay.Items));
        Assert.Equal(13, Rows(giveWay.Items).Count);

        giveWay.IsSubMenuOpen = false;

        Assert.Equal(8, Rows(giveWay.Items).Count);
    }

    [AvaloniaFact]
    public void StripFlyout_RowClick_EvenAfterShowMore_SendsGWAndClosesTheFlyoutAndTheMenu()
    {
        RecordingMenuHost host = HostWith(Thirteen);
        (ContextMenu menu, MenuFlyout flyout) = OpenStripFlyout(host);
        Click(MoreRow(flyout.Items));

        Click(RowItem(flyout.Items, "AC12"));

        Assert.Equal([(Callsign, "GW AC12", Initials)], host.Sent);
        Assert.False(flyout.IsOpen);
        Assert.False(menu.IsOpen);
    }

    [AvaloniaFact]
    public void StripFlyout_ShowMoreClick_KeepsTheFlyoutOpen()
    {
        RecordingMenuHost host = HostWith(Thirteen);
        (ContextMenu menu, MenuFlyout flyout) = OpenStripFlyout(host);

        Click(MoreRow(flyout.Items));

        Assert.True(flyout.IsOpen);
        Assert.True(menu.IsOpen);
        Assert.Equal(13, Rows(flyout.Items).Count);
    }

    [AvaloniaFact]
    public void StripFlyout_OpensWithTheFirstRowFocused()
    {
        RecordingMenuHost host = HostWith(Thirteen);

        (_, MenuFlyout flyout) = OpenStripFlyout(host);

        IInputElement? focused = TopLevel.GetTopLevel(flyout.Popup.Child)?.FocusManager?.GetFocusedElement();
        MenuItem? row = (focused as Visual)?.GetSelfAndVisualAncestors().OfType<MenuItem>().FirstOrDefault();
        Assert.Equal("AC01", (row?.Header as GroundTrafficFlyoutRow)?.Traffic.Callsign);
    }

    [AvaloniaFact]
    public void StripFlyout_Closing_ClearsTheHighlight()
    {
        RecordingMenuHost host = HostWith(Thirteen);
        (_, MenuFlyout flyout) = OpenStripFlyout(host);
        RaisePointer(RowItem(flyout.Items, "AC02"), InputElement.PointerEnteredEvent);

        flyout.Hide();

        Assert.Null(host.Highlights[^1]);
    }

    [AvaloniaFact]
    public void StripButton_NoOtherGroundTraffic_StaysFaded_OpensNothing_AndNamesTheReason()
    {
        RecordingMenuHost host = HostWith([]);
        ContextMenu menu = OpenInWindow(BuildMenu(host));
        Button giveWay = GiveWayButton(menu);

        Assert.Equal(MenuGlyphRowTemplate.DimmedOpacity, giveWay.Opacity);
        Assert.Null(FlyoutBase.GetAttachedFlyout(giveWay));
        giveWay.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Empty(host.Sent);
        Assert.True(menu.IsOpen);
        QuickCommandLabelLine line = Assert.IsType<QuickCommandLabelLine>(menu.Items[1]);

        RaisePointer(giveWay, InputElement.PointerEnteredEvent);

        Assert.Equal("Give way to · no other aircraft on the ground", line.Label);
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
                _ => [new CatalogQuickCommandEntry(MenuIds.GroundGiveWay, null)],
                SoloAdvisoryPrefill.TrueValues,
                false
            ),
        };
        host.GroundTraffic.AddRange(traffic);
        return host;
    }

    private static AircraftModel Taxiing() =>
        new()
        {
            Callsign = Callsign,
            AircraftType = "B738",
            FlightRules = "IFR",
            IsOnGround = true,
            CurrentPhase = "Taxiing",
            Situation = AircraftSituation.Taxiing,
            HasActiveTaxiRoute = true,
        };

    private static ContextMenu BuildMenu(RecordingMenuHost host) =>
        AircraftMenuBuilder.Build(Taxiing(), new MenuClick(Callsign, null, null, []), host, _ => []);

    private static MenuItem GiveWay(ContextMenu menu) =>
        Item(Item(menu.Items, AircraftMenuBuilder.AllCommandsHeader).Items, MenuCatalog.Get(MenuIds.GroundGiveWay).Label);

    private static MenuItem Item(ItemCollection items, string header) =>
        Assert.Single(items.OfType<MenuItem>(), item => (item.Header as string) == header);

    private static List<GroundTrafficFlyoutRow> Rows(IEnumerable<object?> items) =>
        [.. items.OfType<MenuItem>().Select(item => item.Header).OfType<GroundTrafficFlyoutRow>()];

    private static MenuItem RowItem(IEnumerable<object?> items, string callsign) =>
        items.OfType<MenuItem>().Single(item => (item.Header as GroundTrafficFlyoutRow)?.Traffic.Callsign == callsign);

    private static MenuItem MoreRow(IEnumerable<object?> items) => items.OfType<MenuItem>().Single(item => item.Header is GroundTrafficMoreRow);

    private static Button GiveWayButton(ContextMenu menu) =>
        QuickCommandStrip
            .Buttons(Assert.Single(menu.Items.OfType<MenuItem>(), QuickCommandStrip.IsStrip))
            .Single(button => (string)button.Tag! == MenuIds.GroundGiveWay);

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
        Button button = GiveWayButton(menu);
        QuickCommandStrip.OpenSubmenu(button);
        Dispatcher.UIThread.RunJobs();
        MenuFlyout flyout = Assert.IsType<MenuFlyout>(FlyoutBase.GetAttachedFlyout(button));
        Assert.True(flyout.IsOpen);
        return (menu, flyout);
    }

    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

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
