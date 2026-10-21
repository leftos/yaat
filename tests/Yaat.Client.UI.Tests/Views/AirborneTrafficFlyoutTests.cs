using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Situation;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Report traffic in sight… and Follow traffic… flyouts outside solo training: one layout, the title line, the Traffic box
/// with the command Enter sends, then the eight nearest other airborne aircraft within 10 NM, two lines each, the box's filter
/// reaching every other airborne aircraft. Follow lists the aircraft reported in sight first and greys the rest until Force
/// turns them into <c>FOLLOWF</c>. In solo, Force is hidden (Report traffic in sight… there is SoloAdvisoryFlyoutTests').
/// </summary>
public class AirborneTrafficFlyoutTests
{
    private const string Callsign = "N52TW";
    private const string Initials = "AB";

    [AvaloniaFact]
    public void ReportFlyout_ListsEightNearestWithinTenNm()
    {
        RecordingMenuHost host = HostWith([.. Enumerable.Range(1, 10).Reverse().Select(i => Row($"AC{i:00}", i)), Row("FAR1", 10.5)]);

        AirborneTrafficPanel panel = PanelOf(Report(Pilot(), Rpo(), host));

        Assert.Equal(["AC01", "AC02", "AC03", "AC04", "AC05", "AC06", "AC07", "AC08"], Callsigns(panel));
        Assert.Equal(AirborneTrafficFlyout.ReportTitle, panel.Title);
        Assert.Equal("N52TW · 2,000 · hdg 300", panel.TitleNow);
        Assert.Null(panel.ForceToggle);
        Assert.Equal($"{AirborneTrafficFlyout.GroupTitle} · 8", panel.GroupLine);
        Assert.Equal(GroundTrafficFlyout.FollowFilterPlaceholder, panel.Box.PlaceholderText);
        Assert.Equal("RTIS ···", panel.BoxCommandText.Text);
        Assert.Equal("AC01 · C172 — 12 o'clock · 1 nm · same altitude · eastbound — RTIS AC01", panel.Rows[0].Entry.ToString());

        RecordingMenuHost sparse = HostWith([Row("NEAR1", 4), Row("FAR1", 10.5)]);
        Assert.Equal(["NEAR1"], Callsigns(PanelOf(Report(Pilot(), Rpo(), sparse))));
    }

    [AvaloniaFact]
    public void Filter_ReachesAircraftBeyondTheList()
    {
        RecordingMenuHost host = HostWith([.. Enumerable.Range(1, 10).Select(i => Row($"AC{i:00}", i)), Row("N77FAR", 25)]);
        AirborneTrafficPanel panel = PanelOf(Report(Pilot(), Rpo(), host));

        panel.Box.Text = "77f";

        Assert.Equal(["N77FAR"], Callsigns(panel));
        Assert.Equal("RTIS N77FAR", panel.BoxCommandText.Text);

        panel.Box.Text = "";

        Assert.DoesNotContain("N77FAR", Callsigns(panel));
    }

    [AvaloniaFact]
    public void FollowFlyout_InSightRowFirstOthersGreyed()
    {
        RecordingMenuHost host = HostWith([Row("AC01", 1), Row("AC02", 2), Row("AC03", 3), Row("AC04", 4)]);
        AirborneTrafficPanel panel = PanelOf(
            Follow(Pilot() with { SituationFlags = SituationFlags.HasReportedTrafficInSight, LastReportedTrafficCallsign = "AC03" }, Rpo(), host)
        );

        Assert.Equal(AirborneTrafficFlyout.FollowTitle, panel.Title);
        Assert.Equal(["AC03", "AC01", "AC02", "AC04"], Callsigns(panel));
        AirborneTrafficRowButton inSight = panel.Rows[0];
        Assert.False(inSight.Entry.IsGreyed);
        Assert.Equal("◆ AC03 · C172 · in sight — 12 o'clock · 3 nm · same altitude · eastbound — FOLLOW AC03", inSight.Entry.ToString());
        foreach (AirborneTrafficRowButton greyed in panel.Rows.Skip(1))
        {
            Assert.True(greyed.Entry.IsGreyed);
            Assert.Contains(AirborneTrafficFlyout.NotInSightNote, greyed.Entry.ToString());
            Assert.Equal(AirborneTrafficFlyout.NotInSightTip, ToolTip.GetTip(greyed));
            Assert.False(greyed.Focusable);
        }

        Click(panel.Rows[1]);

        Assert.Empty(host.Sent);
        Assert.NotNull(panel.ForceToggle);
        Assert.Equal(AirborneTrafficFlyout.ForceTip, ToolTip.GetTip(panel.ForceToggle!));
    }

    [AvaloniaFact]
    public void Follow_ForceOn_RowsSendFollowf()
    {
        RecordingMenuHost host = HostWith([Row("AC01", 1), Row("AC03", 3)]);
        AirborneTrafficPanel panel = PanelOf(
            Follow(Pilot() with { SituationFlags = SituationFlags.HasReportedTrafficInSight, LastReportedTrafficCallsign = "AC03" }, Rpo(), host)
        );

        panel.ForceToggle!.IsChecked = true;

        Assert.Equal(["FOLLOW AC03", "FOLLOWF AC01"], panel.Rows.Select(row => row.Entry.Command));
        Assert.False(panel.Rows[1].Entry.IsGreyed);
        Click(panel.Rows[1]);
        Assert.Equal([(Callsign, "FOLLOWF AC01", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void Force_HiddenInSolo()
    {
        RecordingMenuHost host = HostWith([Row("AC01", 1)]);

        AirborneTrafficPanel follow = PanelOf(Follow(Pilot(), Solo(), host));

        Assert.Null(follow.ForceToggle);
        Assert.True(follow.Rows[0].Entry.IsGreyed);
        Assert.NotNull(PanelOf(Follow(Pilot(), Rpo(), host)).ForceToggle);
    }

    [AvaloniaFact]
    public void InSightMark_NeedsFlagAndCallsign()
    {
        RecordingMenuHost host = HostWith([Row("AC01", 1), Row("AC02", 2)]);
        FakeMenuAircraft reported = Pilot() with { SituationFlags = SituationFlags.HasReportedTrafficInSight, LastReportedTrafficCallsign = "AC02" };

        Assert.Equal([false, true], PanelOf(Report(reported, Rpo(), host)).Rows.Select(row => row.Entry.InSight));
        Assert.StartsWith("◆ AC02", PanelOf(Report(reported, Rpo(), host)).Rows[1].Entry.ToString());
        Assert.Equal(
            [false, false],
            PanelOf(Report(reported with { SituationFlags = SituationFlags.None }, Rpo(), host)).Rows.Select(row => row.Entry.InSight)
        );
        Assert.Equal(
            [false, false],
            PanelOf(Report(reported with { LastReportedTrafficCallsign = "AC99" }, Rpo(), host)).Rows.Select(row => row.Entry.InSight)
        );
    }

    [AvaloniaFact]
    public void FollowingMark_ReadsFollowingCallsign()
    {
        RecordingMenuHost host = HostWith([Row("AC01", 1), Row("AC02", 2)]);

        AirborneTrafficPanel panel = PanelOf(Report(Pilot() with { FollowingCallsign = "AC02" }, Rpo(), host));

        Assert.Equal([false, true], panel.Rows.Select(row => row.Entry.Following));
        Assert.Equal("AC02 · C172 · following — 12 o'clock · 2 nm · same altitude · eastbound — RTIS AC02", panel.Rows[1].Entry.ToString());
    }

    [AvaloniaFact]
    public void Rows_FrozenWhilePointerInside_GoneRowGrey()
    {
        RecordingMenuHost host = HostWith([Row("AC01", 1), Row("AC02", 2), Row("AC03", 3)]);
        (_, MenuFlyout flyout) = OpenStripFlyout(host, MenuIds.ApproachReportTrafficInSight);
        AirborneTrafficPanel panel = FlyoutPanel(flyout);
        RaisePointer((Control)flyout.Popup.Child!, InputElement.PointerEnteredEvent);

        SetTraffic(host, [Row("AC02", 0.5), Row("AC01", 1.5)]);
        Tick(host);

        Assert.Equal(["AC01", "AC02", "AC03"], Callsigns(panel));
        Assert.Equal("AC01 · C172 — 12 o'clock · 2 nm · same altitude · eastbound — RTIS AC01", panel.Rows[0].Entry.ToString());
        AirborneTrafficRowButton gone = panel.Rows[2];
        Assert.True(gone.Entry.IsGone);
        Assert.False(gone.IsEnabled);
        Click(gone);
        Assert.Empty(host.Sent);

        RaisePointer((Control)flyout.Popup.Child!, InputElement.PointerExitedEvent);

        Assert.Equal(["AC02", "AC01"], Callsigns(panel));
    }

    [AvaloniaFact]
    public void HeldList_NewcomerJoinsTheBottom_Marked()
    {
        RecordingMenuHost host = HostWith([Row("AC01", 1), Row("AC02", 2)]);
        (_, MenuFlyout flyout) = OpenStripFlyout(host, MenuIds.ApproachReportTrafficInSight);
        AirborneTrafficPanel panel = FlyoutPanel(flyout);
        RaisePointer((Control)flyout.Popup.Child!, InputElement.PointerEnteredEvent);

        SetTraffic(host, [Row("NEW1", 0.5), Row("AC01", 1), Row("AC02", 2)]);
        Tick(host);

        Assert.Equal(["AC01", "AC02", "NEW1"], Callsigns(panel));
        Assert.Equal([false, false, true], panel.Rows.Select(row => row.Classes.Contains(AirborneTrafficFlyout.NewcomerClass)));

        RaisePointer((Control)flyout.Popup.Child!, InputElement.PointerExitedEvent);

        Assert.Equal(["NEW1", "AC01", "AC02"], Callsigns(panel));
        Assert.DoesNotContain(panel.Rows, row => row.Classes.Contains(AirborneTrafficFlyout.NewcomerClass));
    }

    /// <summary>Tab from the box reaches the Force toggle, and Tab from Force the first row that sends; Shift+Tab walks back.</summary>
    [AvaloniaFact]
    public void Force_IsKeyboardReachable_AfterTheBox()
    {
        RecordingMenuHost host = HostWith([Row("AC01", 1)]);
        (_, MenuFlyout flyout) = OpenStripFlyout(host, MenuIds.PatternFollow);
        AirborneTrafficPanel panel = FlyoutPanel(flyout);
        panel.ForceToggle!.IsChecked = true;
        panel.Box.Focus();
        Dispatcher.UIThread.RunJobs();

        Assert.True(panel.ForceToggle.Focusable);
        Press(panel.Box, Key.Tab, KeyModifiers.None);
        Assert.True(panel.ForceToggle.IsFocused, "Tab from the box did not reach Force");
        Press(panel.ForceToggle, Key.Tab, KeyModifiers.None);
        Assert.True(panel.Rows[0].IsFocused, "Tab from Force did not reach the first row");
        Press(panel.Rows[0], Key.Tab, KeyModifiers.Shift);
        Assert.True(panel.ForceToggle.IsFocused, "Shift+Tab from the first row did not return to Force");
        Press(panel.ForceToggle, Key.Tab, KeyModifiers.Shift);
        Assert.True(panel.Box.IsFocused, "Shift+Tab from Force did not return to the box");
    }

    [AvaloniaFact]
    public void NoTraffic_StripEntryInert_AllCommandsDisabledWithReason()
    {
        RecordingMenuHost host = HostWith([]);

        MenuItem report = Report(Pilot(), Rpo(), host);
        MenuItem follow = Follow(Pilot(), Rpo(), host);

        Assert.False(report.IsEnabled);
        Assert.Equal("Report traffic in sight… · no other aircraft airborne", report.Header?.ToString());
        Assert.False(follow.IsEnabled);
        Assert.Equal("Follow traffic… · no other aircraft airborne", follow.Header?.ToString());

        ContextMenu menu = OpenInWindow(BuildMenu(host));
        Button button = StripButton(menu, MenuIds.ApproachReportTrafficInSight);
        Assert.Equal(MenuGlyphRowTemplate.DimmedOpacity, button.Opacity);
        Assert.Null(FlyoutBase.GetAttachedFlyout(button));
        QuickCommandLabelLine line = Assert.IsType<QuickCommandLabelLine>(menu.Items[1]);
        RaisePointer(button, InputElement.PointerEnteredEvent);
        Assert.Equal("Report traffic in sight… · no other aircraft airborne", line.Label);
        menu.Close();
    }

    [AvaloniaFact]
    public void NoMatch_EnterSendsVerbAsTyped()
    {
        RecordingMenuHost host = HostWith([Row("AC01", 1)]);
        AirborneTrafficPanel report = PanelOf(Report(Pilot(), Rpo(), host));

        report.Box.Text = " zz9 ";

        Assert.Empty(Callsigns(report));
        Assert.True(report.NoMatchText.IsVisible);
        Assert.Equal("No aircraft matches; Enter sends RTIS ZZ9 as typed.", report.NoMatchText.Text);
        Assert.True(report.BoxNote.IsVisible);
        Assert.Equal(AirborneTrafficFlyout.NotListed, report.BoxNote.Text);
        PressEnter(report.Box);
        Assert.Equal([(Callsign, "RTIS ZZ9", Initials)], host.Sent);

        AirborneTrafficPanel follow = PanelOf(Follow(Pilot(), Rpo(), host));
        follow.Box.Text = "zz9";
        Assert.Equal("No aircraft matches; Enter sends FOLLOW ZZ9 as typed.", follow.NoMatchText.Text);
        PressEnter(follow.Box);
        Assert.Equal((Callsign, "FOLLOW ZZ9", Initials), host.Sent[^1]);
    }

    /// <summary>RTIS takes a descriptive form, so a no-match Enter sends every typed word, not just the first.</summary>
    [AvaloniaFact]
    public void NoMatch_EnterSendsEveryTypedWord()
    {
        RecordingMenuHost host = HostWith([Row("AC01", 1)]);
        AirborneTrafficPanel report = PanelOf(Report(Pilot(), Rpo(), host));

        report.Box.Text = "2 4  w sr22 35";

        Assert.Equal("No aircraft matches; Enter sends RTIS 2 4 W SR22 35 as typed.", report.NoMatchText.Text);
        Assert.Equal("RTIS 2 4 W SR22 35", report.BoxCommandText.Text);
        PressEnter(report.Box);
        Assert.Equal([(Callsign, "RTIS 2 4 W SR22 35", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void NoMatch_ForceOn_EnterSendsFollowf()
    {
        RecordingMenuHost host = HostWith([Row("AC01", 1)]);
        AirborneTrafficPanel follow = PanelOf(Follow(Pilot(), Rpo(), host));
        follow.ForceToggle!.IsChecked = true;

        follow.Box.Text = "zz9";

        Assert.Equal("No aircraft matches; Enter sends FOLLOWF ZZ9 as typed.", follow.NoMatchText.Text);
        Assert.Equal("FOLLOWF ZZ9", follow.BoxCommandText.Text);
        PressEnter(follow.Box);
        Assert.Equal([(Callsign, "FOLLOWF ZZ9", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void EmptyEnter_SendsNothing()
    {
        RecordingMenuHost host = HostWith([Row("AC01", 1)]);
        AirborneTrafficPanel panel = PanelOf(Report(Pilot(), Rpo(), host));

        panel.Box.Text = "   ";
        PressEnter(panel.Box);

        Assert.Empty(host.Sent);
        Assert.Equal("RTIS ···", panel.BoxCommandText.Text);
        Assert.False(panel.NoMatchText.IsVisible);
    }

    [AvaloniaFact]
    public void HoverRow_HighlightsAndCuesTarget()
    {
        RecordingMenuHost host = HostWith([Row("AC01", 1), Row("AC02", 2)]);
        AirborneTrafficPanel panel = PanelOf(Report(Pilot(), Rpo(), host));

        RaisePointer(panel.Rows[1], InputElement.PointerEnteredEvent);

        Assert.Equal(new RadarCue(Callsign, "AC02", null, true), host.RadarCues[^1]);
        Assert.Equal("AC02", host.Highlights[^1]);

        RaisePointer(panel.Rows[1], InputElement.PointerExitedEvent);

        Assert.Null(host.RadarCues[^1]);
        Assert.Null(host.Highlights[^1]);
    }

    [AvaloniaFact]
    public void FocusRow_CuesTarget_BlurClears()
    {
        RecordingMenuHost host = HostWith([Row("AC01", 1), Row("AC02", 2)]);
        AirborneTrafficPanel panel = PanelOf(Report(Pilot(), Rpo(), host));

        panel.Rows[0].RaiseEvent(new FocusChangedEventArgs(InputElement.GotFocusEvent));

        Assert.Equal((new RadarCue(Callsign, "AC01", null, true), "AC01"), (host.RadarCues[^1], host.Highlights[^1]));

        panel.Rows[0].RaiseEvent(new FocusChangedEventArgs(InputElement.LostFocusEvent));

        Assert.Equal(((RadarCue?)null, (string?)null), (host.RadarCues[^1], host.Highlights[^1]));
    }

    /// <summary>A row the filter takes away under the pointer raises no PointerExited; its cue still goes.</summary>
    [AvaloniaFact]
    public void HoveredRowFilteredAway_ClearsTheCue()
    {
        RecordingMenuHost host = HostWith([Row("AC01", 1), Row("BB02", 2)]);
        AirborneTrafficPanel panel = PanelOf(Report(Pilot(), Rpo(), host));
        RaisePointer(panel.Rows[1], InputElement.PointerEnteredEvent);
        Assert.Equal("BB02", host.RadarCues[^1]?.TargetCallsign);

        panel.Box.Text = "ac";

        Assert.Equal(["AC01"], Callsigns(panel));
        Assert.Null(host.RadarCues[^1]);
        Assert.Null(host.Highlights[^1]);
    }

    [AvaloniaFact]
    public void ClosingTheStripFlyout_WithARowHovered_ClearsCueAndHighlight()
    {
        RecordingMenuHost host = HostWith([Row("AC01", 1), Row("AC02", 2)]);
        (ContextMenu menu, MenuFlyout flyout) = OpenStripFlyout(host, MenuIds.ApproachReportTrafficInSight);
        AirborneTrafficPanel panel = FlyoutPanel(flyout);
        RaisePointer(panel.Rows[1], InputElement.PointerEnteredEvent);
        Assert.Equal("AC02", host.RadarCues[^1]?.TargetCallsign);

        flyout.Hide();
        Dispatcher.UIThread.RunJobs();

        Assert.Null(host.RadarCues[^1]);
        Assert.Null(host.Highlights[^1]);
        menu.Close();
    }

    [AvaloniaFact]
    public void ReportRow_SendsRtisCallsign()
    {
        RecordingMenuHost host = HostWith([Row("AC01", 1), Row("AC02", 2)]);
        AirborneTrafficPanel panel = PanelOf(Report(Pilot(), Rpo(), host));

        Click(panel.Rows[1]);
        panel.Box.Text = "ac0";
        PressEnter(panel.Box);

        Assert.Equal([(Callsign, "RTIS AC02", Initials), (Callsign, "RTIS AC01", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void FollowRow_SendsFollowCallsign()
    {
        RecordingMenuHost host = HostWith([Row("AC01", 1), Row("AC02", 2)]);
        FakeMenuAircraft reported = Pilot() with { SituationFlags = SituationFlags.HasReportedTrafficInSight, LastReportedTrafficCallsign = "AC02" };
        AirborneTrafficPanel panel = PanelOf(Follow(reported, Rpo(), host));

        Click(panel.Rows[0]);

        Assert.Equal([(Callsign, "FOLLOW AC02", Initials)], host.Sent);
    }

    // --- Harness ----------------------------------------------------------------------------

    private static MenuAirborneTrafficRow Row(string callsign, double nm) => new(new MenuTrafficRow(callsign, "C172", nm, 12, 0), "eastbound", null);

    private static FakeMenuAircraft Pilot() =>
        new()
        {
            Callsign = Callsign,
            FlightRules = "VFR",
            IsOnGround = false,
            AltitudeFeet = 2000,
            HeadingDegrees = 300,
            FiledAircraftType = "PA28",
        };

    private static MenuContext Rpo() => TestMenuContext.Create(Callsign, Initials, null, false, VfrCommandsForIfr.None);

    private static MenuContext Solo() => TestMenuContext.Create(Callsign, Initials, null, true, VfrCommandsForIfr.None);

    private static RecordingMenuHost HostWith(IEnumerable<MenuAirborneTrafficRow> traffic)
    {
        var host = new RecordingMenuHost("")
        {
            Session = new(
                Initials,
                false,
                VfrCommandsForIfr.None,
                _ =>
                    [
                        new CatalogQuickCommandEntry(MenuIds.ApproachReportTrafficInSight, null),
                        new CatalogQuickCommandEntry(MenuIds.PatternFollow, null),
                    ],
                SoloAdvisoryPrefill.TrueValues,
                false
            ),
        };
        host.AirborneTraffic.AddRange(traffic);
        return host;
    }

    private static void SetTraffic(RecordingMenuHost host, IEnumerable<MenuAirborneTrafficRow> traffic)
    {
        host.AirborneTraffic.Clear();
        host.AirborneTraffic.AddRange(traffic);
    }

    private static MenuItem Report(IMenuAircraft aircraft, MenuContext context, RecordingMenuHost host) =>
        Assert.IsType<MenuItem>(MenuCatalog.Get(MenuIds.ApproachReportTrafficInSight).Build(aircraft, context, host));

    private static MenuItem Follow(IMenuAircraft aircraft, MenuContext context, RecordingMenuHost host) =>
        Assert.IsType<MenuItem>(MenuCatalog.Get(MenuIds.PatternFollow).Build(aircraft, context, host));

    private static AirborneTrafficPanel PanelOf(MenuItem entry) =>
        Assert.IsType<AirborneTrafficFlyoutHost>(Assert.IsType<MenuItem>(Assert.Single(entry.Items)).Header).Panel;

    private static AirborneTrafficPanel FlyoutPanel(MenuFlyout flyout) =>
        Assert.Single(flyout.Items.OfType<MenuItem>().Select(item => item.Header).OfType<AirborneTrafficFlyoutHost>()).Panel;

    private static List<string> Callsigns(AirborneTrafficPanel panel) => [.. panel.Rows.Select(row => row.Entry.Callsign)];

    private static void Click(Button row) => row.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void PressEnter(TextBox box) => box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });

    private static void Press(Control target, Key key, KeyModifiers modifiers)
    {
        target.RaiseEvent(
            new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = key,
                KeyModifiers = modifiers,
            }
        );
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>One second passes: the flyout's tick fires and the refill it posts runs.</summary>
    private static void Tick(RecordingMenuHost host)
    {
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        Dispatcher.UIThread.RunJobs();
    }

    private static ContextMenu BuildMenu(RecordingMenuHost host) =>
        AircraftMenuBuilder.Build(
            new AircraftModel
            {
                Callsign = Callsign,
                AircraftType = "PA28",
                FlightRules = "VFR",
                IsOnGround = false,
                Altitude = 2000,
                Position = new LatLon(37.7, -122.2),
                Situation = AircraftSituation.VfrFlightFollowing,
            },
            new MenuClick(Callsign, null, null, []),
            host,
            _ => []
        );

    private static Button StripButton(ContextMenu menu, string id) =>
        QuickCommandStrip
            .Buttons(Assert.Single(menu.Items.OfType<MenuItem>(), QuickCommandStrip.IsStrip))
            .Single(button => (string)button.Tag! == id);

    private static ContextMenu OpenInWindow(ContextMenu menu)
    {
        var window = new Window { Width = 600, Height = 900 };
        window.Show();
        menu.Open(window);
        Dispatcher.UIThread.RunJobs();
        menu.UpdateLayout();
        return menu;
    }

    private static (ContextMenu Menu, MenuFlyout Flyout) OpenStripFlyout(RecordingMenuHost host, string id)
    {
        ContextMenu menu = OpenInWindow(BuildMenu(host));
        Button button = StripButton(menu, id);
        QuickCommandStrip.OpenSubmenu(button);
        Dispatcher.UIThread.RunJobs();
        MenuFlyout flyout = Assert.IsType<MenuFlyout>(FlyoutBase.GetAttachedFlyout(button));
        Assert.True(flyout.IsOpen);
        (flyout.Popup.Child as Control)?.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return (menu, flyout);
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
}
