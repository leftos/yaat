using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
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
using Yaat.Sim.Commands;
using Yaat.Sim.Situation;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The quick-command strip's Follow… and Give way to… flyouts refill once a second while open. With the pointer outside
/// the flyout the list is fully live: re-sorted by distance, rows leaving and appearing. With the pointer inside the
/// order freezes: each row's distance and state update in place, an aircraft that left stays as a dimmed "gone" row that
/// sends nothing and the keyboard skips, and a newcomer joins the bottom of the full list; the list catches up at once
/// when the pointer leaves. Closing the flyout stops the tick, and a tick already queued then does nothing.
/// </summary>
public class LiveRefillFlyoutTests
{
    private const string Callsign = "SWA104";
    private const string Initials = "AB";

    /// <summary>Thirteen aircraft, 300 ft apart, AC01 nearest.</summary>
    private static readonly MenuGroundTrafficRow[] Thirteen = [.. Enumerable.Range(1, 13).Select(i => Moving($"AC{i:00}", i * 300))];

    private static readonly string[] FirstEight = [.. Enumerable.Range(1, 8).Select(i => $"AC{i:00}")];

    // --- Live with the pointer outside -----------------------------------------------------------

    [AvaloniaFact]
    public void GiveWay_PointerOutside_EachSecondReSortsByDistance_RowsLeaveAndAppear()
    {
        RecordingMenuHost host = HostWith([Moving("AC01", 300), Moving("AC02", 600), Parked("AC03", 900)]);
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundGiveWay).Flyout;
        MenuItem ac01 = GiveWayRow(flyout, "AC01");

        SetTraffic(host, [Moving("AC01", 1200), Parked("AC03", 900), Moving("AC04", 100)]);
        Tick(host);

        Assert.Equal(["AC04", "AC03", "AC01"], GiveWayCallsigns(flyout));
        Assert.Same(ac01, GiveWayRow(flyout, "AC01"));
        Assert.Equal("~1,200 ft", RowOf(ac01).Distance);
    }

    [AvaloniaFact]
    public void PointerLeaving_CatchesTheListUpAtOnce()
    {
        RecordingMenuHost host = HostWith([Moving("AC01", 300), Moving("AC02", 600), Parked("AC03", 900)]);
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundGiveWay).Flyout;
        PointerIn(flyout);
        SetTraffic(host, [Moving("AC01", 1200), Parked("AC03", 900)]);
        Tick(host);
        Assert.Equal(["AC01", "AC02", "AC03"], GiveWayCallsigns(flyout));

        PointerOut(flyout);

        Assert.Equal(["AC03", "AC01"], GiveWayCallsigns(flyout));
    }

    [AvaloniaFact]
    public void Follow_PointerLeaving_CatchesTheListUpAtOnce()
    {
        RecordingMenuHost host = HostWith([Moving("AC01", 300), Moving("AC02", 600), Moving("AC03", 900)]);
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundFollow).Flyout;
        PointerIn(flyout);
        SetTraffic(host, [Moving("AC01", 1200), Moving("AC03", 900)]);
        Tick(host);
        Assert.Equal([GroundTrafficFlyout.FollowMovingTitle, "AC01", "AC02", "AC03"], FollowEntries(flyout));

        PointerOut(flyout);

        Assert.Equal([GroundTrafficFlyout.FollowMovingTitle, "AC03", "AC01"], FollowEntries(flyout));
    }

    [AvaloniaFact]
    public void Follow_Closing_CatchesTheListUp()
    {
        RecordingMenuHost host = HostWith([Moving("AC01", 300), Moving("AC02", 600), Moving("AC03", 900)]);
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundFollow).Flyout;
        Panel list = FollowPanel(flyout);
        PointerIn(flyout);
        SetTraffic(host, [Moving("AC01", 1200), Moving("AC03", 900)]);
        Tick(host);

        flyout.Hide();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            ["AC03", "AC01"],
            list.Children.OfType<Button>().Select(button => RowOf(button)).Where(row => !row.IsGone).Select(row => row.Traffic.Callsign)
        );
        Assert.DoesNotContain(list.Children.OfType<Button>(), button => RowOf(button).IsGone);
    }

    // --- Frozen with the pointer inside -----------------------------------------------------------

    [AvaloniaFact]
    public void GiveWay_PointerInside_OrderFreezes_DistanceAndStateUpdateInPlace()
    {
        RecordingMenuHost host = HostWith([Moving("AC01", 300), Moving("AC02", 600), Parked("AC03", 900)]);
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundGiveWay).Flyout;
        MenuItem ac01 = GiveWayRow(flyout, "AC01");
        PointerIn(flyout);

        SetTraffic(host, [Holding("AC01", 1200), Moving("AC02", 600), Parked("AC03", 100)]);
        Tick(host);

        Assert.Equal(["AC01", "AC02", "AC03"], GiveWayCallsigns(flyout));
        Assert.Same(ac01, GiveWayRow(flyout, "AC01"));
        Assert.Equal("AC01 · B738 · holding short of 28R · ~1,200 ft — GW AC01", RowOf(ac01).ToString());
        Assert.Equal("~100 ft", RowOf(GiveWayRow(flyout, "AC03")).Distance);
    }

    [AvaloniaFact]
    public void GiveWay_AircraftLeavingWhileInside_StaysAsAGoneRow_DimmedAndSendingNothing()
    {
        RecordingMenuHost host = HostWith([Moving("AC01", 300), Moving("AC02", 600), Parked("AC03", 900)]);
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundGiveWay).Flyout;
        PointerIn(flyout);

        SetTraffic(host, [Moving("AC01", 300), Parked("AC03", 900)]);
        Tick(host);
        Tick(host);

        Assert.Equal(["AC01", "AC02", "AC03"], GiveWayCallsigns(flyout));
        MenuItem gone = GiveWayRow(flyout, "AC02");
        Assert.Equal("AC02 · B738 · gone — GW AC02", RowOf(gone).ToString());
        Assert.Equal("", RowOf(gone).Distance);
        Assert.Equal(MenuGlyphRowTemplate.DimmedOpacity, gone.Opacity);
        Assert.False(gone.IsEnabled);
        Click(gone);
        Assert.Empty(host.Sent);
        Assert.True(flyout.IsOpen);
    }

    [AvaloniaFact]
    public void Follow_AircraftLeavingWhileInside_StaysAsAGoneRowInItsSection_DimmedAndSendingNothing()
    {
        RecordingMenuHost host = HostWith([Moving("AC01", 300), Moving("AC02", 600), Parked("AC03", 900)]);
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundFollow).Flyout;
        PointerIn(flyout);

        SetTraffic(host, [Moving("AC01", 300), Parked("AC03", 900)]);
        Tick(host);

        Assert.Equal([GroundTrafficFlyout.FollowMovingTitle, "AC01", "AC02", GroundTrafficFlyout.FollowParkedTitle, "AC03"], FollowEntries(flyout));
        Button gone = FollowRow(flyout, "AC02");
        Assert.Equal("AC02 · B738 · gone — FOLLOWG AC02", RowOf(gone).ToString());
        Assert.Equal("", RowOf(gone).Distance);
        Assert.Equal(MenuGlyphRowTemplate.DimmedOpacity, gone.Opacity);
        Assert.False(gone.IsEnabled);
        gone.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Empty(host.Sent);
        Assert.True(flyout.IsOpen);
    }

    [AvaloniaFact]
    public void GiveWay_GoneRow_IsSkippedByDownAndUp()
    {
        RecordingMenuHost host = HostWith([Moving("AC01", 300), Moving("AC02", 600), Moving("AC03", 900)]);
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundGiveWay).Flyout;
        Assert.Equal("AC01", FocusedGiveWayCallsign(flyout));
        PointerIn(flyout);
        SetTraffic(host, [Moving("AC01", 300), Moving("AC03", 900)]);
        Tick(host);
        Layout(flyout);
        Assert.Equal(["AC01", "AC02", "AC03"], GiveWayCallsigns(flyout));

        Press(flyout, Key.Down);
        Assert.Equal("AC03", FocusedGiveWayCallsign(flyout));
        Press(flyout, Key.Up);
        Assert.Equal("AC01", FocusedGiveWayCallsign(flyout));
    }

    [AvaloniaFact]
    public void Follow_GoneRow_IsSkippedByDownAndUp()
    {
        RecordingMenuHost host = HostWith([Moving("AC01", 300), Moving("AC02", 600), Moving("AC03", 900)]);
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundFollow).Flyout;
        PointerIn(flyout);
        SetTraffic(host, [Moving("AC01", 300), Moving("AC03", 900)]);
        Tick(host);
        Layout(flyout);
        Assert.Equal([GroundTrafficFlyout.FollowMovingTitle, "AC01", "AC02", "AC03"], FollowEntries(flyout));

        Assert.Equal("AC01", CallsignOf(Press(flyout, Key.Down)));
        Assert.Equal("AC03", CallsignOf(Press(flyout, Key.Down)));
        Assert.Equal("AC01", CallsignOf(Press(flyout, Key.Up)));
    }

    /// <summary>
    /// The focused row's aircraft leaving while the pointer is inside hands keyboard focus, and the highlight with it, to
    /// the nearest live row below before the row goes gone, so the next Down moves on from there.
    /// </summary>
    [AvaloniaFact]
    public void GiveWay_FocusedRowGoneWhileInside_HandsFocusToTheNearestLiveRowBelow()
    {
        RecordingMenuHost host = HostWith([Moving("AC01", 300), Moving("AC02", 600), Moving("AC03", 900), Moving("AC04", 1200)]);
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundGiveWay).Flyout;
        Assert.Equal("AC01", FocusedGiveWayCallsign(flyout));
        PressKey(flyout, PhysicalKey.ArrowDown);
        Assert.Equal("AC02", FocusedGiveWayCallsign(flyout));
        PointerIn(flyout);

        SetTraffic(host, [Moving("AC01", 300), Moving("AC03", 900), Moving("AC04", 1200)]);
        Tick(host);
        Layout(flyout);

        Assert.Equal(["AC01", "AC02", "AC03", "AC04"], GiveWayCallsigns(flyout));
        Assert.False(GiveWayRow(flyout, "AC02").IsEnabled);
        Assert.Equal("AC03", FocusedGiveWayCallsign(flyout));
        Assert.Equal("AC03", host.Highlights[^1]);
        PressKey(flyout, PhysicalKey.ArrowDown);
        Assert.Equal("AC04", FocusedGiveWayCallsign(flyout));
    }

    [AvaloniaFact]
    public void Follow_FocusedRowGoneWhileInside_HandsFocusToTheNearestLiveRowBelow()
    {
        RecordingMenuHost host = HostWith([Moving("AC01", 300), Moving("AC02", 600), Moving("AC03", 900), Moving("AC04", 1200)]);
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundFollow).Flyout;
        PressKey(flyout, PhysicalKey.ArrowDown);
        PressKey(flyout, PhysicalKey.ArrowDown);
        Assert.Equal("AC02", CallsignOf(Focused(flyout)));
        PointerIn(flyout);

        SetTraffic(host, [Moving("AC01", 300), Moving("AC03", 900), Moving("AC04", 1200)]);
        Tick(host);
        Layout(flyout);

        Assert.Equal([GroundTrafficFlyout.FollowMovingTitle, "AC01", "AC02", "AC03", "AC04"], FollowEntries(flyout));
        Assert.False(FollowRow(flyout, "AC02").IsEnabled);
        Assert.Equal("AC03", CallsignOf(Focused(flyout)));
        Assert.Equal("AC03", host.Highlights[^1]);
        PressKey(flyout, PhysicalKey.ArrowDown);
        Assert.Equal("AC04", CallsignOf(Focused(flyout)));
    }

    /// <summary>With no live row below, focus goes to the nearest live row above; with none at all, to the filter box.</summary>
    [AvaloniaFact]
    public void Follow_FocusedRowGoneWhileInside_HandsFocusAbove_ElseToTheFilterBox()
    {
        RecordingMenuHost host = HostWith([Moving("AC01", 300), Moving("AC02", 600)]);
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundFollow).Flyout;
        PressKey(flyout, PhysicalKey.ArrowDown);
        PressKey(flyout, PhysicalKey.ArrowDown);
        Assert.Equal("AC02", CallsignOf(Focused(flyout)));
        PointerIn(flyout);

        SetTraffic(host, [Moving("AC01", 300)]);
        Tick(host);
        Layout(flyout);

        Assert.Equal("AC01", CallsignOf(Focused(flyout)));
        Assert.Equal("AC01", host.Highlights[^1]);

        SetTraffic(host, []);
        Tick(host);
        Layout(flyout);

        Assert.Same(FlyoutBox(flyout), Focused(flyout));
        Assert.Null(host.Highlights[^1]);
    }

    // --- Focus hand-off when a row leaves while the list is live -----------------------------------

    /// <summary>
    /// The focused row's aircraft leaving while the list is live (the pointer outside the flyout) hands keyboard focus, and
    /// the highlight with it, to the row now standing in its place, before its own row goes out of the list.
    /// </summary>
    [AvaloniaFact]
    public void GiveWay_FocusedRowsAircraftLeaves_WhileLive_FocusMovesToTheNearestRow()
    {
        RecordingMenuHost host = HostWith([Moving("AC01", 300), Moving("AC02", 600), Moving("AC03", 900), Moving("AC04", 1200)]);
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundGiveWay).Flyout;
        Assert.Equal("AC01", FocusedGiveWayCallsign(flyout));
        PressKey(flyout, PhysicalKey.ArrowDown);
        Assert.Equal("AC02", FocusedGiveWayCallsign(flyout));

        SetTraffic(host, [Moving("AC01", 300), Moving("AC03", 900), Moving("AC04", 1200)]);
        Tick(host);
        Layout(flyout);

        Assert.Equal(["AC01", "AC03", "AC04"], GiveWayCallsigns(flyout));
        Assert.Equal("AC03", FocusedGiveWayCallsign(flyout));
        Assert.Equal("AC03", host.Highlights[^1]);
        PressKey(flyout, PhysicalKey.ArrowDown);
        Assert.Equal("AC04", FocusedGiveWayCallsign(flyout));
    }

    [AvaloniaFact]
    public void Follow_FocusedRowsAircraftLeaves_WhileLive_FocusMovesToTheNearestRow()
    {
        RecordingMenuHost host = HostWith([Moving("AC01", 300), Moving("AC02", 600), Moving("AC03", 900), Moving("AC04", 1200)]);
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundFollow).Flyout;
        PressKey(flyout, PhysicalKey.ArrowDown);
        PressKey(flyout, PhysicalKey.ArrowDown);
        Assert.Equal("AC02", CallsignOf(Focused(flyout)));

        SetTraffic(host, [Moving("AC01", 300), Moving("AC03", 900), Moving("AC04", 1200)]);
        Tick(host);
        Layout(flyout);

        Assert.Equal([GroundTrafficFlyout.FollowMovingTitle, "AC01", "AC03", "AC04"], FollowEntries(flyout));
        Assert.Equal("AC03", CallsignOf(Focused(flyout)));
        Assert.Equal("AC03", host.Highlights[^1]);
        PressKey(flyout, PhysicalKey.ArrowDown);
        Assert.Equal("AC04", CallsignOf(Focused(flyout)));
    }

    /// <summary>The last row leaving while the list is live, nothing left to move to, hands focus to the filter box.</summary>
    [AvaloniaFact]
    public void Follow_LastRowLeaves_WhileLive_FocusMovesToTheFilterBox()
    {
        RecordingMenuHost host = HostWith([Moving("AC01", 300)]);
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundFollow).Flyout;
        PressKey(flyout, PhysicalKey.ArrowDown);
        Assert.Equal("AC01", CallsignOf(Focused(flyout)));

        SetTraffic(host, []);
        Tick(host);
        Layout(flyout);

        Assert.Equal(["No other aircraft on the ground."], FollowEntries(flyout));
        Assert.Same(FlyoutBox(flyout), Focused(flyout));
        Assert.Null(host.Highlights[^1]);
    }

    /// <summary>
    /// Under a filter, the focused row's aircraft leaving hands focus to the next row the filter shows below it, passing
    /// the rows it hides, rather than to the filter box.
    /// </summary>
    [AvaloniaFact]
    public void Follow_FocusedRowLeaves_WhileFiltered_FocusMovesToTheNextVisibleRow()
    {
        RecordingMenuHost host = HostWith([Moving("AC01", 100), Moving("AC05", 200), Moving("AC10", 300), Moving("AC15", 400)]);
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundFollow).Flyout;
        FlyoutBox(flyout).Text = "5";
        Layout(flyout);
        PressKey(flyout, PhysicalKey.ArrowDown);
        Assert.Equal("AC05", CallsignOf(Focused(flyout)));

        SetTraffic(host, [Moving("AC01", 100), Moving("AC10", 300), Moving("AC15", 400)]);
        Tick(host);
        Layout(flyout);

        Assert.Equal([GroundTrafficFlyout.FollowMovingTitle, "AC15"], FollowEntries(flyout));
        Assert.Equal("AC15", CallsignOf(Focused(flyout)));
        Assert.Equal("AC15", host.Highlights[^1]);
    }

    /// <summary>
    /// A row under the pointer is the pointed row, and goes out of reach rather than raising a PointerExited of its own once
    /// its aircraft is gone: disabling it drops the point first, so the highlight follows the focus to its new row.
    /// </summary>
    [AvaloniaFact]
    public void GoneRowUnderPointer_IsDisabled_StopsHighlightingItsAircraft()
    {
        RecordingMenuHost host = HostWith([Moving("AC01", 300), Moving("AC02", 600), Moving("AC03", 900)]);
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundGiveWay).Flyout;
        MenuItem ac01 = GiveWayRow(flyout, "AC01");
        PointerIn(flyout);
        RaisePointer(ac01, InputElement.PointerEnteredEvent);
        PressKey(flyout, PhysicalKey.ArrowDown);
        Assert.Equal("AC02", FocusedGiveWayCallsign(flyout));
        Assert.Equal("AC01", host.Highlights[^1]);
        PressKey(flyout, PhysicalKey.ArrowUp);
        Assert.Equal("AC01", FocusedGiveWayCallsign(flyout));

        SetTraffic(host, [Moving("AC02", 600), Moving("AC03", 900)]);
        Tick(host);
        Layout(flyout);

        Assert.False(ac01.IsEnabled);
        Assert.Equal("AC02", FocusedGiveWayCallsign(flyout));
        Assert.Equal("AC02", host.Highlights[^1]);
    }

    /// <summary>
    /// A row under the pointer whose aircraft leaves while another row holds focus goes gone without a PointerExited of its
    /// own: the highlight hands back to the focused row rather than staying on the departed aircraft.
    /// </summary>
    [AvaloniaFact]
    public void GiveWay_PointedUnfocusedRowGoesGone_StopsHighlightingItsAircraft()
    {
        RecordingMenuHost host = HostWith([Moving("AC01", 300), Moving("AC02", 600), Moving("AC03", 900)]);
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundGiveWay).Flyout;
        MenuItem ac01 = GiveWayRow(flyout, "AC01");
        PointerIn(flyout);
        RaisePointer(ac01, InputElement.PointerEnteredEvent);
        PressKey(flyout, PhysicalKey.ArrowDown);
        Assert.Equal("AC02", FocusedGiveWayCallsign(flyout));
        Assert.Equal("AC01", host.Highlights[^1]);

        SetTraffic(host, [Moving("AC02", 600), Moving("AC03", 900)]);
        Tick(host);
        Layout(flyout);

        Assert.False(ac01.IsEnabled);
        Assert.Equal("AC02", FocusedGiveWayCallsign(flyout));
        Assert.Equal("AC02", host.Highlights[^1]);
    }

    /// <summary>A pointed Follow row that nothing focuses releases its aircraft's highlight once that aircraft is gone.</summary>
    [AvaloniaFact]
    public void Follow_PointedRowGoesGone_StopsHighlightingItsAircraft()
    {
        RecordingMenuHost host = HostWith([Moving("AC01", 300), Moving("AC02", 600)]);
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundFollow).Flyout;
        Button ac01 = FollowRow(flyout, "AC01");
        PointerIn(flyout);
        RaisePointer(ac01, InputElement.PointerEnteredEvent);
        Assert.Equal("AC01", host.Highlights[^1]);

        SetTraffic(host, [Moving("AC02", 600)]);
        Tick(host);
        Layout(flyout);

        Assert.False(ac01.IsEnabled);
        Assert.Null(host.Highlights[^1]);
    }

    [AvaloniaFact]
    public void GiveWay_NewcomerWhileInside_IsAppendedAtTheBottom()
    {
        RecordingMenuHost host = HostWith([Moving("AC01", 300), Moving("AC02", 600), Parked("AC03", 900)]);
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundGiveWay).Flyout;
        PointerIn(flyout);

        SetTraffic(host, [Moving("AC09", 100), Moving("AC01", 300), Moving("AC02", 600), Parked("AC03", 900)]);
        Tick(host);

        Assert.Equal(["AC01", "AC02", "AC03", "AC09"], GiveWayCallsigns(flyout));
    }

    [AvaloniaFact]
    public void Follow_NewcomerWhileInside_JoinsTheBottomOfTheFullList_NotItsSection()
    {
        RecordingMenuHost host = HostWith([Moving("AC01", 300), Parked("AC02", 600)]);
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundFollow).Flyout;
        PointerIn(flyout);

        SetTraffic(host, [Moving("AC09", 100), Moving("AC01", 300), Parked("AC02", 600)]);
        Tick(host);

        Assert.Equal([GroundTrafficFlyout.FollowMovingTitle, "AC01", GroundTrafficFlyout.FollowParkedTitle, "AC02", "AC09"], FollowEntries(flyout));
    }

    [AvaloniaFact]
    public void GiveWay_Collapsed_NewcomerWhileInside_OnlyRaisesTheShowMoreCount()
    {
        RecordingMenuHost host = HostWith(Thirteen);
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundGiveWay).Flyout;
        PointerIn(flyout);

        SetTraffic(host, [Moving("AC00", 100), .. Thirteen]);
        Tick(host);

        Assert.Equal(FirstEight, GiveWayCallsigns(flyout));
        Assert.Equal("Show 6 more · up to ~3,900 ft", MoreRow(flyout).Header!.ToString());
    }

    /// <summary>Folded behind "Show n more", an aircraft leaving from the hidden rest only lowers the count.</summary>
    [AvaloniaFact]
    public void GiveWay_Collapsed_AircraftLeavingTheHiddenRestWhileInside_OnlyLowersTheShowMoreCount()
    {
        RecordingMenuHost host = HostWith(Thirteen);
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundGiveWay).Flyout;
        PointerIn(flyout);

        SetTraffic(host, [.. Thirteen.Where(row => row.Callsign != "AC12")]);
        Tick(host);

        Assert.Equal(FirstEight, GiveWayCallsigns(flyout));
        Assert.Equal("Show 4 more · up to ~3,900 ft", MoreRow(flyout).Header!.ToString());
    }

    /// <summary>
    /// Folded behind "Show n more", every shown row gone while the rows past the fold still stand: the focus moves to
    /// "Show n more", the only row left in reach.
    /// </summary>
    [AvaloniaFact]
    public void GiveWay_FocusedRowGoesGone_OnlyMoreLeft_FocusMovesToShowMore()
    {
        RecordingMenuHost host = HostWith(Thirteen);
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundGiveWay).Flyout;
        Assert.Equal("AC01", FocusedGiveWayCallsign(flyout));
        PointerIn(flyout);

        SetTraffic(host, [.. Thirteen.Skip(8)]);
        Tick(host);
        Layout(flyout);

        Assert.Equal(FirstEight, GiveWayCallsigns(flyout));
        Assert.Equal("Show 5 more · up to ~3,900 ft", MoreRow(flyout).Header!.ToString());
        Assert.Same(MoreRow(flyout), FocusedItem(flyout));
    }

    [AvaloniaFact]
    public void GiveWay_Expanded_StaysExpanded_NewcomersAppendBelow()
    {
        RecordingMenuHost host = HostWith(Thirteen);
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundGiveWay).Flyout;
        PointerIn(flyout);
        Click(MoreRow(flyout));

        SetTraffic(host, [Moving("AC00", 100), .. Thirteen]);
        Tick(host);
        Tick(host);

        Assert.Equal([.. Thirteen.Select(row => row.Callsign), "AC00"], GiveWayCallsigns(flyout));
        Assert.Equal("Show fewer", MoreRow(flyout).Header!.ToString());

        PointerOut(flyout);
        Tick(host);

        Assert.Equal(["AC00", .. Thirteen.Select(row => row.Callsign)], GiveWayCallsigns(flyout));
        Assert.Equal("Show fewer", MoreRow(flyout).Header!.ToString());
    }

    [AvaloniaFact]
    public void Follow_PointerInside_ARowKeepsItsSection_ItsStateUpdated_UntilThePointerLeaves()
    {
        RecordingMenuHost host = HostWith([Moving("AC01", 300), Parked("AC02", 600)]);
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundFollow).Flyout;
        PointerIn(flyout);

        SetTraffic(host, [Holding("AC01", 300), Parked("AC02", 600)]);
        Tick(host);

        Assert.Equal([GroundTrafficFlyout.FollowMovingTitle, "AC01", GroundTrafficFlyout.FollowParkedTitle, "AC02"], FollowEntries(flyout));
        Assert.Equal("AC01 · B738 · holding short of 28R · ~300 ft — FOLLOWG AC01", RowOf(FollowRow(flyout, "AC01")).ToString());

        PointerOut(flyout);

        Assert.Equal([GroundTrafficFlyout.FollowParkedTitle, "AC01", "AC02"], FollowEntries(flyout));
    }

    // --- Follow keeps the controller's place ---------------------------------------------------------

    [AvaloniaFact]
    public void Follow_Refill_KeepsTheFilterTextCaretAndScrollOffset()
    {
        RecordingMenuHost host = HostWith([.. Enumerable.Range(1, 20).Select(i => Moving($"AC{i:00}", i * 100))]);
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundFollow).Flyout;
        TextBox box = FlyoutBox(flyout);
        box.Text = "ac";
        box.CaretIndex = 1;
        ScrollViewer scroll = FlyoutScroll(flyout);
        double offset = 3 * GroundTrafficFlyout.FollowRowHeight;
        scroll.Offset = new Vector(0, offset);
        Layout(flyout);

        SetTraffic(host, [.. Enumerable.Range(1, 20).Select(i => Moving($"AC{i:00}", (21 - i) * 100))]);
        Tick(host);
        Layout(flyout);

        Assert.Equal("AC20", FollowEntries(flyout)[1]);
        Assert.Equal("ac", box.Text);
        Assert.Equal(1, box.CaretIndex);
        Assert.Equal(offset, scroll.Offset.Y);
    }

    [AvaloniaFact]
    public void Follow_Refill_KeepsFocusOnAFocusedRowItMoves()
    {
        RecordingMenuHost host = HostWith([Moving("AC01", 100), Moving("AC02", 200), Moving("AC03", 300)]);
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundFollow).Flyout;
        Press(flyout, Key.Down);
        Button focused = Assert.IsType<Button>(Press(flyout, Key.Down));
        Assert.Equal("AC02", CallsignOf(focused));

        SetTraffic(host, [Moving("AC02", 50), Moving("AC01", 100), Moving("AC03", 300)]);
        Tick(host);
        Layout(flyout);

        Assert.Equal([GroundTrafficFlyout.FollowMovingTitle, "AC02", "AC01", "AC03"], FollowEntries(flyout));
        Assert.Same(focused, Focused(flyout));
        Assert.True(focused.IsFocused, "the moved row lost keyboard focus");
    }

    [AvaloniaFact]
    public void GiveWay_Refill_KeepsFocusOnAFocusedRowItMoves()
    {
        RecordingMenuHost host = HostWith([Moving("AC01", 100), Moving("AC02", 200), Moving("AC03", 300)]);
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundGiveWay).Flyout;
        Assert.Equal("AC01", FocusedGiveWayCallsign(flyout));
        Press(flyout, Key.Down);
        Press(flyout, Key.Down);
        MenuItem focused = GiveWayRow(flyout, "AC03");
        Assert.True(focused.IsKeyboardFocusWithin, "Down twice did not reach AC03");

        SetTraffic(host, [Moving("AC03", 50), Moving("AC01", 100), Moving("AC02", 200)]);
        Tick(host);
        Layout(flyout);

        Assert.Equal(["AC03", "AC01", "AC02"], GiveWayCallsigns(flyout));
        Assert.Same(focused, GiveWayRow(flyout, "AC03"));
        Assert.True(focused.IsKeyboardFocusWithin, "the moved row lost keyboard focus");
    }

    // --- Newcomer mark -------------------------------------------------------------------------------

    /// <summary>
    /// A newcomer appended while the pointer is inside carries a thin teal edge at its left, drawn inside the row, until
    /// the pointer leaves and the list catches up.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(MenuIds.GroundGiveWay)]
    [InlineData(MenuIds.GroundFollow)]
    public void NewcomerWhileInside_CarriesATealLeftEdge_UntilThePointerLeaves(string entryId)
    {
        RecordingMenuHost host = HostWith([Moving("AC01", 300), Moving("AC02", 600)]);
        MenuFlyout flyout = OpenStripFlyout(host, entryId).Flyout;
        PointerIn(flyout);

        SetTraffic(host, [Moving("AC09", 100), Moving("AC01", 300), Moving("AC02", 600)]);
        Tick(host);
        Layout(flyout);

        var edge = new BoxShadow
        {
            IsInset = true,
            OffsetX = 2,
            Color = Color.FromArgb(217, 0x6E, 0xE0, 0xCE),
        };
        BoxShadows newcomer = EdgeOf(flyout, "AC09");
        Assert.Equal(1, newcomer.Count);
        Assert.Equal(edge, newcomer[0]);
        Assert.Equal(0, EdgeOf(flyout, "AC01").Count);

        PointerOut(flyout);
        Layout(flyout);

        Assert.Equal(0, EdgeOf(flyout, "AC09").Count);
    }

    // --- Empty while open ----------------------------------------------------------------------------

    [AvaloniaFact]
    public void GiveWay_ListEmptiesWhileOpen_ShowsOneMutedLine()
    {
        RecordingMenuHost host = HostWith([Moving("AC01", 300)]);
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundGiveWay).Flyout;

        SetTraffic(host, []);
        Tick(host);

        MenuItem line = Assert.IsType<MenuItem>(Assert.Single(flyout.Items));
        Assert.Equal("No other aircraft on the ground.", line.Header);
        Assert.False(line.IsEnabled);
    }

    [AvaloniaFact]
    public void Follow_ListEmptiesWhileOpen_ShowsOneMutedLine_ButAFilterMatchingNothingShowsTheHintAlone()
    {
        RecordingMenuHost host = HostWith([Moving("AC01", 300)]);
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundFollow).Flyout;

        SetTraffic(host, []);
        Tick(host);

        Assert.Equal(["No other aircraft on the ground."], FollowEntries(flyout));
        Assert.Equal(GroundTrafficFlyout.FollowTopMatchHint, FlyoutHint(flyout).Text);

        FlyoutBox(flyout).Text = "n1";

        Assert.Empty(FollowEntries(flyout));
        Assert.Equal("No aircraft matches; Enter sends FOLLOWG N1 as typed.", FlyoutHint(flyout).Text);
    }

    // --- The tick's lifetime -------------------------------------------------------------------------

    /// <summary>Closing the flyout, by itself or with the menu, lets go of the tick's timer and refills nothing after.</summary>
    [AvaloniaTheory]
    [InlineData(MenuIds.GroundGiveWay, false)]
    [InlineData(MenuIds.GroundFollow, false)]
    [InlineData(MenuIds.GroundGiveWay, true)]
    [InlineData(MenuIds.GroundFollow, true)]
    public void Closing_StopsTheTick(string entryId, bool closeTheMenu)
    {
        RecordingMenuHost host = HostWith([Moving("AC01", 300)]);
        int timersBeforeOpen = host.Clock.LiveTimerCount;
        (ContextMenu menu, MenuFlyout flyout) = OpenStripFlyout(host, entryId);
        Assert.Equal(timersBeforeOpen + 1, host.Clock.LiveTimerCount);
        int beforeTick = host.GroundTrafficRequests;
        Tick(host);
        Assert.Equal(beforeTick + 1, host.GroundTrafficRequests);

        if (closeTheMenu)
        {
            menu.Close();
        }
        else
        {
            flyout.Hide();
        }

        Dispatcher.UIThread.RunJobs();
        Assert.False(flyout.IsOpen);
        Assert.Equal(timersBeforeOpen, host.Clock.LiveTimerCount);
        int afterClose = host.GroundTrafficRequests;
        Tick(host);

        Assert.Equal(afterClose, host.GroundTrafficRequests);
    }

    [AvaloniaTheory]
    [InlineData(MenuIds.GroundGiveWay)]
    [InlineData(MenuIds.GroundFollow)]
    public void ATickQueuedBeforeClose_DoesNothingAfterIt(string entryId)
    {
        RecordingMenuHost host = HostWith([Moving("AC01", 300)]);
        MenuFlyout flyout = OpenStripFlyout(host, entryId).Flyout;
        int beforeTick = host.GroundTrafficRequests;
        Tick(host);
        Assert.Equal(beforeTick + 1, host.GroundTrafficRequests);

        host.Clock.Advance(TimeSpan.FromSeconds(1));
        flyout.Hide();
        int afterClose = host.GroundTrafficRequests;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(afterClose, host.GroundTrafficRequests);
    }

    /// <summary>A tick over traffic that has not changed, outside the flyout or inside it, moves, adds and replaces nothing.</summary>
    [AvaloniaTheory]
    [InlineData(MenuIds.GroundGiveWay)]
    [InlineData(MenuIds.GroundFollow)]
    public void StaticTraffic_ATickChurnsNoItem(string entryId)
    {
        RecordingMenuHost host = HostWith(Thirteen);
        MenuFlyout flyout = OpenStripFlyout(host, entryId).Flyout;
        int changes = 0;
        void Count(object? sender, NotifyCollectionChangedEventArgs e) => changes++;
        ((INotifyCollectionChanged)flyout.Items).CollectionChanged += Count;
        List<object?> rowsBefore = RowContents(flyout);
        if (entryId == MenuIds.GroundFollow)
        {
            FollowPanel(flyout).Children.CollectionChanged += Count;
        }

        int beforeTicks = host.GroundTrafficRequests;
        Tick(host);
        PointerIn(flyout);
        Tick(host);

        Assert.Equal(beforeTicks + 2, host.GroundTrafficRequests);
        Assert.Equal(0, changes);
        Assert.Equal(rowsBefore.Count, RowContents(flyout).Count);
        Assert.All(rowsBefore.Zip(RowContents(flyout)), pair => Assert.Same(pair.First, pair.Second));
    }

    /// <summary>
    /// Closing the flyout still rebuilds the list so its rows have caught up, but nothing the open list would say is posted
    /// after it: the stuck-title pass a closed list would queue never reaches it.
    /// </summary>
    [AvaloniaFact]
    public void Follow_Closed_PostsNothingAfterClose()
    {
        RecordingMenuHost host = HostWith([Moving("AC01", 300)]);
        MenuFlyout flyout = OpenStripFlyout(host, MenuIds.GroundFollow).Flyout;
        TextBlock sticky = StickyTitle(flyout);
        sticky.Text = "sentinel";

        flyout.Hide();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("sentinel", sticky.Text);
    }

    /// <summary>
    /// In the All Commands submenu, a filter that swaps the Moving rows for as many Parked rows of the same height leaves
    /// the scroll offset and extent alone, and the stuck title still follows the section now scrolled past.
    /// </summary>
    [AvaloniaFact]
    public void Follow_InAllCommandsSubmenu_FilterChange_UpdatesTheStickyTitle()
    {
        // The parked rows read the moving rows' state, so the two filters' rows are as wide as each other, not only as tall.
        RecordingMenuHost host = HostWith([
            .. Enumerable.Range(1, 10).Select(i => Moving($"AAL{i:00}", i * 100)),
            .. Enumerable.Range(1, 10).Select(i => Moving($"UAL{i:00}", i * 100) with { IsMoving = false }),
        ]);
        MenuItem follow = OpenAllCommandsFollow(host);
        List<Visual> visuals = [.. follow.Items.OfType<MenuItem>().SelectMany(item => item.GetVisualDescendants())];
        TextBox box = Assert.Single(visuals.OfType<TextBox>());
        ScrollViewer scroll = Assert.Single(visuals.OfType<ScrollViewer>(), viewer => viewer.Name == GroundTrafficFlyout.FollowListName);
        TextBlock sticky = Assert.IsType<TextBlock>(
            visuals.OfType<Border>().Single(border => border.Name == GroundTrafficFlyout.FollowStickyTitleName).Child
        );
        box.Text = "AAL";
        LayoutSubmenu(follow);
        scroll.Offset = new Vector(0, 2 * GroundTrafficFlyout.FollowRowHeight);
        LayoutSubmenu(follow);
        Assert.Equal(GroundTrafficFlyout.FollowMovingTitle, sticky.Text);
        (Vector offset, Size extent) = (scroll.Offset, scroll.Extent);

        box.Text = "UAL";
        LayoutSubmenu(follow);

        Assert.Equal((offset, extent), (scroll.Offset, scroll.Extent));
        Assert.Equal(GroundTrafficFlyout.FollowParkedTitle, sticky.Text);
    }

    // --- Harness ----------------------------------------------------------------------------

    private static MenuGroundTrafficRow Moving(string callsign, double feet) =>
        new(callsign, "B738", feet, "taxiing on W") { IsMoving = true, IsSurfaceShadow = false };

    private static MenuGroundTrafficRow Parked(string callsign, double feet) =>
        new(callsign, "B738", feet, "at parking · gate 1") { IsMoving = false, IsSurfaceShadow = false };

    private static MenuGroundTrafficRow Holding(string callsign, double feet) =>
        new(callsign, "B738", feet, "holding short of 28R") { IsMoving = false, IsSurfaceShadow = false };

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

    private static void PointerOut(MenuFlyout flyout) => RaisePointer((Control)flyout.Popup.Child!, InputElement.PointerExitedEvent);

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

    private static (ContextMenu Menu, MenuFlyout Flyout) OpenStripFlyout(RecordingMenuHost host, string entryId)
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
        return (menu, flyout);
    }

    /// <summary>Opens the menu in a window, then All Commands, then its Follow… submenu, and lays them out; returns Follow….</summary>
    private static MenuItem OpenAllCommandsFollow(RecordingMenuHost host)
    {
        var window = new Window { Width = 600, Height = 900 };
        window.Show();
        ContextMenu menu = BuildMenu(host);
        menu.Open(window);
        Dispatcher.UIThread.RunJobs();
        menu.UpdateLayout();
        MenuItem all = menu.Items.OfType<MenuItem>().Single(item => (item.Header as string) == AircraftMenuBuilder.AllCommandsHeader);
        all.IsSubMenuOpen = true;
        Dispatcher.UIThread.RunJobs();
        MenuItem follow = all.Items.OfType<MenuItem>().Single(item => (item.Header as string) == MenuCatalog.Get(MenuIds.GroundFollow).Label);
        follow.IsSubMenuOpen = true;
        Dispatcher.UIThread.RunJobs();
        LayoutSubmenu(follow);
        return follow;
    }

    private static void LayoutSubmenu(MenuItem entry)
    {
        Control first = entry.Items.OfType<MenuItem>().First();
        (TopLevel.GetTopLevel(first) ?? throw new InvalidOperationException("The Follow… submenu did not open")).UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Layout(MenuFlyout flyout)
    {
        (flyout.Popup.Child as Control)?.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static GroundTrafficFlyoutRow RowOf(MenuItem row) => Assert.IsType<GroundTrafficFlyoutRow>(row.Header);

    private static GroundTrafficFlyoutRow RowOf(Button row) => Assert.IsType<GroundTrafficFlyoutRow>(row.Content);

    private static List<string> GiveWayCallsigns(MenuFlyout flyout) =>
        [.. flyout.Items.OfType<MenuItem>().Select(item => item.Header).OfType<GroundTrafficFlyoutRow>().Select(row => row.Traffic.Callsign)];

    private static MenuItem GiveWayRow(MenuFlyout flyout, string callsign) =>
        flyout.Items.OfType<MenuItem>().Single(item => (item.Header as GroundTrafficFlyoutRow)?.Traffic.Callsign == callsign);

    private static MenuItem MoreRow(MenuFlyout flyout) => flyout.Items.OfType<MenuItem>().Single(item => item.Header is GroundTrafficMoreRow);

    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    private static IEnumerable<Visual> FlyoutVisuals(MenuFlyout flyout) =>
        flyout.Items.OfType<MenuItem>().SelectMany(item => item.GetVisualDescendants());

    private static TextBox FlyoutBox(MenuFlyout flyout) => Assert.Single(FlyoutVisuals(flyout).OfType<TextBox>());

    private static TextBlock FlyoutHint(MenuFlyout flyout) => Assert.IsType<TextBlock>(((MenuItem)flyout.Items[1]!).Header);

    private static ScrollViewer FlyoutScroll(MenuFlyout flyout) =>
        Assert.Single(FlyoutVisuals(flyout).OfType<ScrollViewer>(), viewer => viewer.Name == GroundTrafficFlyout.FollowListName);

    private static Panel FollowPanel(MenuFlyout flyout) => Assert.IsAssignableFrom<Panel>(FlyoutScroll(flyout).Content);

    private static Button FollowRow(MenuFlyout flyout, string callsign) =>
        FollowPanel(flyout).Children.OfType<Button>().Single(button => RowOf(button).Traffic.Callsign == callsign);

    /// <summary>The Follow list's showing section titles and other lines by their text, its showing rows by their callsign.</summary>
    private static List<string> FollowEntries(MenuFlyout flyout) =>
        [
            .. FollowPanel(flyout)
                .Children.Where(child => child.IsVisible)
                .Select(child =>
                    child switch
                    {
                        TextBlock line => line.Text!,
                        Button { Content: GroundTrafficFlyoutRow row } => row.Traffic.Callsign,
                        _ => throw new Xunit.Sdk.XunitException($"unexpected list child {child.GetType().Name}"),
                    }
                ),
        ];

    /// <summary>What each aircraft row of the open flyout shows, in order: a Give way row's header, a Follow row's content.</summary>
    private static List<object?> RowContents(MenuFlyout flyout) =>
        [
            .. flyout.Items.OfType<MenuItem>().Select(item => item.Header).OfType<GroundTrafficFlyoutRow>(),
            .. flyout.Items.OfType<MenuItem>().Select(item => item.Header).OfType<GroundTrafficMoreRow>(),
            .. FlyoutVisuals(flyout).OfType<Button>().Select(button => button.Content).OfType<GroundTrafficFlyoutRow>(),
        ];

    /// <summary>The box shadows drawn on the background of <paramref name="callsign"/>'s row: a Give way row's or a Follow row's.</summary>
    private static BoxShadows EdgeOf(MenuFlyout flyout, string callsign)
    {
        if (flyout.Items.OfType<MenuItem>().FirstOrDefault(item => (item.Header as GroundTrafficFlyoutRow)?.Traffic.Callsign == callsign) is { } item)
        {
            return item.GetVisualDescendants().OfType<Border>().First(border => border.Name == "PART_LayoutRoot").BoxShadow;
        }

        Button row = FollowRow(flyout, callsign);
        return row.GetVisualDescendants().OfType<ContentPresenter>().First(presenter => presenter.Name == "PART_ContentPresenter").BoxShadow;
    }

    private static IInputElement? Focused(MenuFlyout flyout) => TopLevel.GetTopLevel(flyout.Popup.Child)?.FocusManager?.GetFocusedElement();

    /// <summary>The text of the border showing the section title scrolled past the list's top.</summary>
    private static TextBlock StickyTitle(MenuFlyout flyout) =>
        Assert.IsType<TextBlock>(
            FlyoutVisuals(flyout).OfType<Border>().Single(border => border.Name == GroundTrafficFlyout.FollowStickyTitleName).Child
        );

    /// <summary>The menu row holding keyboard focus, itself or the row a control inside it belongs to.</summary>
    private static MenuItem? FocusedItem(MenuFlyout flyout) =>
        (Focused(flyout) as Visual)?.GetSelfAndVisualAncestors().OfType<MenuItem>().FirstOrDefault();

    private static string? FocusedGiveWayCallsign(MenuFlyout flyout) =>
        ((Focused(flyout) as Visual)?.GetSelfAndVisualAncestors().OfType<MenuItem>().FirstOrDefault()?.Header as GroundTrafficFlyoutRow)
            ?.Traffic
            .Callsign;

    /// <summary>Presses <paramref name="key"/> on whatever holds focus in the flyout and returns what holds it after.</summary>
    private static IInputElement? Press(MenuFlyout flyout, Key key)
    {
        Control focused = Assert.IsAssignableFrom<Control>(Focused(flyout));
        focused.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
        Layout(flyout);
        return Focused(flyout);
    }

    /// <summary>Presses <paramref name="key"/> on the flyout's keyboard, as the controller does, and lays the flyout out.</summary>
    private static void PressKey(MenuFlyout flyout, PhysicalKey key)
    {
        TopLevel.GetTopLevel(flyout.Popup.Child)!.KeyPressQwerty(key, RawInputModifiers.None);
        Layout(flyout);
    }

    private static string CallsignOf(IInputElement? row) => RowOf(Assert.IsType<Button>(row)).Traffic.Callsign;

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
