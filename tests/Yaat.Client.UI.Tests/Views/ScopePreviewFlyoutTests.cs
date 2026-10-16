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
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Situation;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The scope preview a Give way to… or Follow… row sends while it is pointed at or focused: the target ringed, this
/// aircraft's route and the target's route, and, for Give way to… only, the point where the two routes meet. A target
/// with no route is ringed alone; a Give way to… row whose routes meet nowhere says <c>no conflict predicted</c>. A gone
/// row draws nothing, and closing the flyout clears the preview.
/// </summary>
public class ScopePreviewFlyoutTests
{
    private const string Callsign = "SWA104";
    private const string Initials = "AB";
    private const string NoConflictText = "no conflict predicted";

    private static readonly LatLon Junction = new(37.7213, -122.2207);

    [AvaloniaFact]
    public void GiveWay_PointingARow_SendsOwnRouteTargetRouteAndMeetPoint()
    {
        RecordingMenuHost host = HostWith([Moving("AC01", 300) with { MeetPoint = Junction }, Moving("AC02", 600)]);
        TaxiRoute own = RouteFor(host, Callsign);
        TaxiRoute target = RouteFor(host, "AC01");
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundGiveWay);

        RaisePointer(GiveWayRow(flyout, "AC01"), InputElement.PointerEnteredEvent);

        Assert.Equal(new ScopePreview("AC01", own, target, null, Junction), host.ScopePreviews[^1]);
        Assert.False(RowOf(GiveWayRow(flyout, "AC01")).NoConflictPredicted);
        Assert.DoesNotContain(NoConflictText, Texts(GiveWayRow(flyout, "AC01")));
    }

    [AvaloniaFact]
    public void GiveWay_TargetWithNoMeetRow_SendsRoutesWithoutMeetPoint_AndRowSaysNoConflictPredicted()
    {
        RecordingMenuHost host = HostWith([Moving("AC01", 300), Moving("AC02", 600)]);
        TaxiRoute own = RouteFor(host, Callsign);
        TaxiRoute target = RouteFor(host, "AC01");
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundGiveWay);

        RaisePointer(GiveWayRow(flyout, "AC01"), InputElement.PointerEnteredEvent);

        Assert.Equal(new ScopePreview("AC01", own, target, null, null), host.ScopePreviews[^1]);
        Assert.True(RowOf(GiveWayRow(flyout, "AC01")).NoConflictPredicted);
        Assert.Contains(NoConflictText, Texts(GiveWayRow(flyout, "AC01")));
    }

    /// <summary>With no route of its own, this aircraft has nothing for a conflict note to be about: no row says one.</summary>
    [AvaloniaFact]
    public void GiveWay_CommandedAircraftWithNoRoute_NoRowSaysNoConflictPredicted()
    {
        RecordingMenuHost host = HostWith([Moving("AC01", 300), Moving("AC02", 600)]);
        RouteFor(host, "AC01");
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundGiveWay);

        Assert.False(RowOf(GiveWayRow(flyout, "AC01")).NoConflictPredicted);
        Assert.DoesNotContain(NoConflictText, Texts(GiveWayRow(flyout, "AC01")));
    }

    /// <summary>The pointed row's preview is sent again at each refill, so its meet point follows the traffic.</summary>
    [AvaloniaFact]
    public void GiveWay_PointedRowsPreview_FollowsTheRefill()
    {
        var moved = new LatLon(37.7221, -122.2199);
        RecordingMenuHost host = HostWith([Moving("AC01", 300) with { MeetPoint = Junction }, Moving("AC02", 600)]);
        RouteFor(host, Callsign);
        RouteFor(host, "AC01");
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundGiveWay);
        RaisePointer(GiveWayRow(flyout, "AC01"), InputElement.PointerEnteredEvent);
        Assert.Equal(Junction, host.ScopePreviews[^1]?.MeetPoint);
        int highlights = host.Highlights.Count;

        SetTraffic(host, [Moving("AC01", 280) with { MeetPoint = moved }, Moving("AC02", 600)]);
        Tick(host);

        Assert.Equal(moved, host.ScopePreviews[^1]?.MeetPoint);
        Assert.Equal(highlights, host.Highlights.Count);
    }

    /// <summary>
    /// A pointed Give way row whose aircraft goes stops previewing it: the preview follows the row the focus is handed to,
    /// the same row the highlight follows, and names no route that row's aircraft does not have.
    /// </summary>
    [AvaloniaFact]
    public void GiveWay_PointedRowGoesGone_PreviewFollowsTheFocus()
    {
        RecordingMenuHost host = HostWith([Moving("AC01", 300), Moving("AC02", 600), Moving("AC03", 900)]);
        RouteFor(host, Callsign);
        RouteFor(host, "AC02");
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundGiveWay);
        PointerIn(flyout);
        RaisePointer(GiveWayRow(flyout, "AC02"), InputElement.PointerEnteredEvent);
        Assert.Equal("AC02", host.ScopePreviews[^1]?.RingCallsign);

        SetTraffic(host, [Moving("AC01", 300), Moving("AC03", 900)]);
        Tick(host);
        Layout(flyout);

        Assert.False(GiveWayRow(flyout, "AC02").IsEnabled);
        ScopePreview handedBack = Assert.IsType<ScopePreview>(host.ScopePreviews[^1]);
        Assert.NotEqual("AC02", handedBack.RingCallsign);
        Assert.Equal(host.Highlights[^1], handedBack.RingCallsign);
        Assert.Equal(new ScopePreview(handedBack.RingCallsign, null, null, null, null), handedBack);
    }

    [AvaloniaFact]
    public void GiveWay_ParkedTargetWithNoRoute_SendsRingOnly()
    {
        RecordingMenuHost host = HostWith([Parked("AC01", 300), Moving("AC02", 600)]);
        RouteFor(host, Callsign);
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundGiveWay);

        RaisePointer(GiveWayRow(flyout, "AC01"), InputElement.PointerEnteredEvent);

        Assert.Equal(new ScopePreview("AC01", null, null, null, null), host.ScopePreviews[^1]);
    }

    [AvaloniaFact]
    public void Follow_PointingARow_SendsRoutesAndNoMeetPoint()
    {
        RecordingMenuHost host = HostWith([Moving("AC01", 300) with { MeetPoint = Junction }, Moving("AC02", 600)]);
        TaxiRoute own = RouteFor(host, Callsign);
        TaxiRoute target = RouteFor(host, "AC01");
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundFollow);
        Button row = FollowRow(flyout, "AC01");

        RaisePointer(row, InputElement.PointerEnteredEvent);

        Assert.Equal(new ScopePreview("AC01", own, target, null, null), host.ScopePreviews[^1]);
        Assert.False(RowOf(row).NoConflictPredicted);
        Assert.DoesNotContain(NoConflictText, Texts(row));
    }

    [AvaloniaFact]
    public void PointingAGoneRow_ClearsTheFeedback()
    {
        RecordingMenuHost host = HostWith([Moving("AC01", 300), Moving("AC02", 600)]);
        RouteFor(host, Callsign);
        RouteFor(host, "AC01");
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundFollow);
        Button ac01 = FollowRow(flyout, "AC01");
        PointerIn(flyout);
        RaisePointer(ac01, InputElement.PointerEnteredEvent);
        Assert.NotNull(host.ScopePreviews[^1]);

        SetTraffic(host, [Moving("AC02", 600)]);
        Tick(host);
        Layout(flyout);

        Assert.Null(host.ScopePreviews[^1]);

        RaisePointer(ac01, InputElement.PointerEnteredEvent);

        Assert.Null(host.ScopePreviews[^1]);
    }

    [AvaloniaFact]
    public void ClosingTheFlyout_ClearsTheFeedback()
    {
        RecordingMenuHost host = HostWith([Moving("AC01", 300), Moving("AC02", 600)]);
        RouteFor(host, Callsign);
        RouteFor(host, "AC02");
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundGiveWay);
        RaisePointer(GiveWayRow(flyout, "AC02"), InputElement.PointerEnteredEvent);
        Assert.NotNull(host.ScopePreviews[^1]);

        flyout.Hide();

        Assert.Null(host.ScopePreviews[^1]);
    }

    /// <summary>
    /// A Give way… submenu closed while a row is still pointed sends no preview again: the one the menu itself cleared as
    /// it closed stays cleared. The preview follows the list only while the strip's live flyout refills it.
    /// </summary>
    [AvaloniaFact]
    public void GiveWay_SubmenuClosingWithARowPointed_SendsNoPreviewAgain()
    {
        RecordingMenuHost host = HostWith([Moving("AC01", 300), Moving("AC02", 600)]);
        RouteFor(host, Callsign);
        RouteFor(host, "AC01");
        MenuItem giveWay = GiveWay(BuildMenu(host));
        giveWay.IsSubMenuOpen = true;
        RaisePointer(RowItem(giveWay.Items, "AC01"), InputElement.PointerEnteredEvent);
        Assert.NotNull(host.ScopePreviews[^1]);

        // The whole menu closing clears the preview, as AircraftMenuBuilder's own Closed handler does.
        host.SetScopePreview(null);

        giveWay.IsSubMenuOpen = false;

        Assert.Null(host.ScopePreviews[^1]);
    }

    /// <summary>A stop at the bar the route starts at has no route to draw: the bar is boxed alone, and no aircraft is ringed.</summary>
    [AvaloniaFact]
    public void ZeroSegmentStop_Hover_BarOnly()
    {
        RecordingMenuHost host = LadderHost();
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundHoldShort);

        RaisePointer(LadderStop(flyout, "HS 28R"), InputElement.PointerEnteredEvent);

        Assert.Equal(new ScopePreview(null, null, null, Bar28R, null), host.ScopePreviews[^1]);
    }

    /// <summary>A ladder stop pointed at or focused highlights no aircraft: its key is a command, not a callsign.</summary>
    [AvaloniaFact]
    public void LadderStop_Hover_RingsNoAircraft()
    {
        RecordingMenuHost host = LadderHost();
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundHoldShort);
        int highlights = host.Highlights.Count;
        MenuItem stop = LadderStop(flyout, "HS 28R");

        RaisePointer(stop, InputElement.PointerEnteredEvent);
        stop.RaiseEvent(new FocusChangedEventArgs(InputElement.GotFocusEvent));

        Assert.Equal(highlights, host.Highlights.Count);
        Assert.Null(host.ScopePreviews[^1]?.RingCallsign);
    }

    [AvaloniaFact]
    public void FlyoutClose_ClearsPreview()
    {
        RecordingMenuHost host = LadderHost();
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundHoldShort);
        RaisePointer(LadderStop(flyout, "HS 28R"), InputElement.PointerEnteredEvent);
        Assert.NotNull(host.ScopePreviews[^1]);

        flyout.Hide();

        Assert.Null(host.ScopePreviews[^1]);
    }

    // --- Harness ----------------------------------------------------------------------------

    private static readonly LatLon Bar28R = new(37.7221, -122.2199);

    /// <summary>A host whose Hold short of… ladder has one stop, the 28R bar the route starts at, 50 ft ahead.</summary>
    private static RecordingMenuHost LadderHost()
    {
        var host = new RecordingMenuHost("")
        {
            Session = new(Initials, false, VfrCommandsForIfr.None, _ => [new CatalogQuickCommandEntry(MenuIds.GroundHoldShort, null)]),
        };
        host.HoldShortChoices.Add(
            new HoldShortChoice(
                new HoldShortRowLabel(HoldShortChoice.RunwayBadge, "28R", "crossing on B", 50),
                "HS 28R",
                new TaxiRoute { Segments = [], HoldShortPoints = [] },
                Bar28R
            )
        );
        return host;
    }

    private static MenuItem LadderStop(MenuFlyout flyout, string command) =>
        flyout.Items.OfType<MenuItem>().Single(item => (item.Header as HoldShortLadderStop)?.Command == command);

    private static MenuGroundTrafficRow Moving(string callsign, double feet) =>
        new(callsign, "B738", feet, "taxiing on W")
        {
            IsMoving = true,
            IsSurfaceShadow = false,
            MeetPoint = null,
        };

    private static MenuGroundTrafficRow Parked(string callsign, double feet) =>
        new(callsign, "B738", feet, "at parking · gate 1")
        {
            IsMoving = false,
            IsSurfaceShadow = false,
            MeetPoint = null,
        };

    /// <summary>Gives <paramref name="callsign"/> a route of its own on <paramref name="host"/>, and returns it.</summary>
    private static TaxiRoute RouteFor(RecordingMenuHost host, string callsign)
    {
        var route = new TaxiRoute { Segments = [], HoldShortPoints = [] };
        host.RemainingRoutes[callsign] = route;
        return route;
    }

    private static RecordingMenuHost HostWith(IEnumerable<MenuGroundTrafficRow> traffic)
    {
        var host = new RecordingMenuHost("")
        {
            Session = new(
                Initials,
                false,
                VfrCommandsForIfr.None,
                _ => [new CatalogQuickCommandEntry(MenuIds.GroundGiveWay, null), new CatalogQuickCommandEntry(MenuIds.GroundFollow, null)]
            ),
        };
        host.GroundTraffic.AddRange(traffic);
        return host;
    }

    private static void SetTraffic(RecordingMenuHost host, IEnumerable<MenuGroundTrafficRow> traffic)
    {
        host.GroundTraffic.Clear();
        host.GroundTraffic.AddRange(traffic);
    }

    /// <summary>One second passes: the flyout's tick fires and the refill it posts runs.</summary>
    private static void Tick(RecordingMenuHost host)
    {
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        Dispatcher.UIThread.RunJobs();
    }

    private static void PointerIn(MenuFlyout flyout) => RaisePointer((Control)flyout.Popup.Child!, InputElement.PointerEnteredEvent);

    private static MenuFlyout OpenStripFlyout(RecordingMenuHost host, string entryId)
    {
        var window = new Window { Width = 600, Height = 900 };
        window.Show();
        ContextMenu menu = BuildMenu(host);
        menu.Open(window);
        Dispatcher.UIThread.RunJobs();
        menu.UpdateLayout();
        Button button = QuickCommandStrip
            .Buttons(Assert.Single(menu.Items.OfType<MenuItem>(), QuickCommandStrip.IsStrip))
            .Single(b => (string)b.Tag! == entryId);
        QuickCommandStrip.OpenSubmenu(button);
        Dispatcher.UIThread.RunJobs();
        MenuFlyout flyout = Assert.IsType<MenuFlyout>(FlyoutBase.GetAttachedFlyout(button));
        Assert.True(flyout.IsOpen);
        Layout(flyout);
        return flyout;
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

    /// <summary>The All Commands submenu's Give way to… entry.</summary>
    private static MenuItem GiveWay(ContextMenu menu) =>
        Item(Item(menu.Items, AircraftMenuBuilder.AllCommandsHeader).Items, MenuCatalog.Get(MenuIds.GroundGiveWay).Label);

    private static MenuItem Item(ItemCollection items, string header) =>
        Assert.Single(items.OfType<MenuItem>(), item => (item.Header as string) == header);

    private static MenuItem RowItem(IEnumerable<object?> items, string callsign) =>
        items.OfType<MenuItem>().Single(item => (item.Header as GroundTrafficFlyoutRow)?.Traffic.Callsign == callsign);

    private static void Layout(MenuFlyout flyout)
    {
        (flyout.Popup.Child as Control)?.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static MenuItem GiveWayRow(MenuFlyout flyout, string callsign) =>
        flyout.Items.OfType<MenuItem>().Single(item => (item.Header as GroundTrafficFlyoutRow)?.Traffic.Callsign == callsign);

    private static Button FollowRow(MenuFlyout flyout, string callsign) =>
        flyout
            .Items.OfType<MenuItem>()
            .SelectMany(item => item.GetVisualDescendants())
            .OfType<Button>()
            .Single(button => (button.Content as GroundTrafficFlyoutRow)?.Traffic.Callsign == callsign);

    private static GroundTrafficFlyoutRow RowOf(MenuItem row) => Assert.IsType<GroundTrafficFlyoutRow>(row.Header);

    private static GroundTrafficFlyoutRow RowOf(Button row) => Assert.IsType<GroundTrafficFlyoutRow>(row.Content);

    /// <summary>Every text the row draws.</summary>
    private static List<string> Texts(Visual row) => [.. row.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text ?? "")];

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
