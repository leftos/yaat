using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Situation;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Report traffic in sight… in a solo session: the student types the advisory, Tab takes each part from the pointed row as the
/// pre-fill setting allows, and, with the live check on, the check line grades the call 300 ms after typing stops and again after
/// each burst of aircraft updates while no typing check is pending, flashing when an update changes its text. The call resolving
/// to other traffic rings that aircraft dashed while the pointed one keeps the solid ring; with none pointed it gets the dashed
/// ring alone. A refusal or a send that never reached the sim keeps the flyout open; no row sends <c>RTIS {callsign}</c>; every
/// way the flyout closes leaves no observer and no timer behind. Time runs on the host's manual clock, never a real wait.
/// </summary>
public class SoloAdvisoryFlyoutTests
{
    private const string Callsign = "N52TW";
    private const string Initials = "AB";
    private const string N22RxResolved = "→ N22RX clock ✓ miles ✓ direction ✓ altitude ✓ type ✓ · scores clean";
    private static readonly LatLon Here = new(37.72, -122.22);
    private static readonly TrafficAdvisoryCandidate Me = new(Callsign, Here, 300, 300, 2000, "PA28", "PA28", false, null, null);
    private static readonly Traffic N22Rx = new("N22RX", "SR22")
    {
        Clock = 2,
        Nm = 4,
        Track = 270,
        AltitudeFt = 3500,
        Direction = "westbound",
    };

    [AvaloniaFact]
    public void LiveCheck_RunsThreeHundredMsAfterTyping()
    {
        RecordingMenuHost host = HostWith(N22Rx);
        SoloAdvisoryPanel panel = Open(host, SoloAdvisoryPrefill.TrueValues, liveCheck: true);

        panel.Box.Text = "2";
        host.Clock.Advance(TimeSpan.FromMilliseconds(200));
        panel.Box.Text = "2 4 w sr22 035";
        host.Clock.Advance(TimeSpan.FromMilliseconds(200));

        Assert.Equal(SoloAdvisoryModel.EmptyHint, panel.CheckText);

        host.Clock.Advance(TimeSpan.FromMilliseconds(100));

        Assert.Equal(N22RxResolved, panel.CheckText);
        Assert.Equal(SoloAdvisoryPanel.YourCall, RowOf(panel, "N22RX").CommandColumn);
    }

    [AvaloniaFact]
    public void LiveCheck_EverySegmentOfAResolvedCall_HasABrush()
    {
        RecordingMenuHost host = HostWith(N22Rx);
        SoloAdvisoryPanel panel = Open(host, SoloAdvisoryPrefill.TrueValues, liveCheck: true);

        panel.Box.Text = "2 4 w sr22 035";
        host.Clock.Advance(SoloAdvisoryPanel.CheckDelay);
        panel.Measure(Size.Infinity);
        panel.Arrange(new Rect(panel.DesiredSize));

        WrapPanel line = Assert.Single(
            panel.GetLogicalDescendants().OfType<WrapPanel>(),
            each => AutomationProperties.GetName(each) == N22RxResolved
        );
        List<TextBlock> segments = [.. line.Children.OfType<TextBlock>()];
        Assert.Equal(N22RxResolved, string.Join(' ', segments.Select(segment => segment.Text)));
        Assert.All(segments, segment => Assert.True(segment.Foreground is not null, $"'{segment.Text}' draws with no brush"));
    }

    [AvaloniaFact]
    public void LiveCheck_RerunsOnAircraftUpdate_FlashesOnTextChange()
    {
        RecordingMenuHost host = HostWith(N22Rx);
        SoloAdvisoryPanel panel = Open(host, SoloAdvisoryPrefill.TrueValues, liveCheck: true);
        panel.Box.Text = "2 4 w sr22 035";
        host.Clock.Advance(SoloAdvisoryPanel.CheckDelay);
        int reads = host.AdvisoryCandidateReads;

        host.RaiseAircraftUpdate();

        Assert.Equal(reads + 1, host.AdvisoryCandidateReads);
        Assert.Equal(N22RxResolved, panel.CheckText);
        Assert.False(panel.IsCheckFlashing);

        host.AdvisoryCandidates[1] = (N22Rx with { Clock = 3 }).Candidate;
        host.RaiseAircraftUpdate();

        Assert.Equal("→ N22RX clock ✓ ±1 h miles ✓ direction ✓ altitude ✓ type ✓ · scores clean", panel.CheckText);
        Assert.True(panel.IsCheckFlashing);

        host.Clock.Advance(SoloAdvisoryPanel.FlashTime - TimeSpan.FromMilliseconds(1));

        Assert.True(panel.IsCheckFlashing);

        host.Clock.Advance(TimeSpan.FromMilliseconds(1));

        Assert.False(panel.IsCheckFlashing);
    }

    [AvaloniaFact]
    public void AnUpdateMidTyping_WaitsForTheKeysToRest_AndDoesNotFlash()
    {
        RecordingMenuHost host = HostWith(N22Rx);
        SoloAdvisoryPanel panel = Open(host, SoloAdvisoryPrefill.TrueValues, liveCheck: true);
        panel.Box.Text = "2 4 w sr22 035";
        host.Clock.Advance(SoloAdvisoryPanel.CheckDelay);

        panel.Box.Text = "3 4 w sr22 035";
        host.Clock.Advance(TimeSpan.FromMilliseconds(100));
        host.AdvisoryCandidates[1] = (N22Rx with { Clock = 3 }).Candidate;
        host.RaiseAircraftUpdate();

        Assert.Equal(N22RxResolved, panel.CheckText);
        Assert.False(panel.IsCheckFlashing);

        host.Clock.Advance(TimeSpan.FromMilliseconds(200));

        Assert.Equal(N22RxResolved, panel.CheckText);
        Assert.False(panel.IsCheckFlashing);
    }

    [AvaloniaFact]
    public void LiveCheck_Off_ShowsNothing()
    {
        RecordingMenuHost host = HostWith(N22Rx);
        SoloAdvisoryPanel panel = Open(host, SoloAdvisoryPrefill.TrueValues, liveCheck: false);

        panel.Box.Text = "2 4 w sr22 035";
        host.Clock.Advance(SoloAdvisoryPanel.CheckDelay);
        host.RaiseAircraftUpdate();

        Assert.False(panel.IsCheckVisible);
        Assert.Equal("", panel.CheckText);
        Assert.All(panel.Rows, row => Assert.Equal("", row.CommandColumn));
        Assert.All(host.RadarCues, cue => Assert.Null(cue?.ResolvedCallsign));
    }

    [AvaloniaFact]
    public void ResolvedOther_GetsDashedRing_PointedKeepsSolid()
    {
        Traffic pointed = new("N811CP", "C182")
        {
            Clock = 4,
            Nm = 7,
            Track = 180,
            AltitudeFt = 2500,
            Direction = "southbound",
        };
        Traffic other = new("N4417Q", "C182")
        {
            Clock = 6,
            Nm = 5,
            Track = 315,
            AltitudeFt = 2000,
            Direction = "northwestbound",
        };
        RecordingMenuHost host = HostWith(pointed, other);
        SoloAdvisoryPanel panel = Open(host, SoloAdvisoryPrefill.TrueValues, liveCheck: true);

        Click(RowOf(panel, "N811CP"));
        panel.Box.Text = "5 6 nw c182 020";
        host.Clock.Advance(SoloAdvisoryPanel.CheckDelay);

        Assert.Equal(new RadarCue(Callsign, "N811CP", "N4417Q", true), host.RadarCues[^1]);
        Assert.Equal("N811CP", host.Highlights[^1]);
        Assert.True(RowOf(panel, "N811CP").IsPointed);
        Assert.Equal(SoloAdvisoryPanel.YourCall, RowOf(panel, "N4417Q").CommandColumn);
        Assert.Equal("", RowOf(panel, "N811CP").CommandColumn);
    }

    [AvaloniaFact]
    public void ResolvedWithNonePointed_GetsTheDashedRingAlone()
    {
        RecordingMenuHost host = HostWith(N22Rx);
        SoloAdvisoryPanel panel = Open(host, SoloAdvisoryPrefill.TrueValues, liveCheck: true);

        panel.Box.Text = "2 4 w sr22 035";
        host.Clock.Advance(SoloAdvisoryPanel.CheckDelay);

        Assert.Equal(new RadarCue(Callsign, null, "N22RX", true), host.RadarCues[^1]);
        Assert.Null(host.Highlights[^1]);
    }

    [AvaloniaFact]
    public void Refusal_KeepsFlyoutOpenWithSimReply()
    {
        RecordingMenuHost host = HostWith(N22Rx);
        const string Reply = "Unable, no aircraft matches traffic 8 o'clock, 4 mile(s), westbound, SR22";
        host.SendResult = new CommandResultDto(false, Reply);
        (MenuItem entry, SoloAdvisoryPanel panel) = OpenEntry(host, SoloAdvisoryPrefill.TrueValues, liveCheck: true);

        panel.Box.Text = "8 4 w sr22";
        PressKey(panel.Box, Key.Enter);

        Assert.Equal([(Callsign, "RTIS 8 4 W SR22", Initials)], host.Sent);
        Assert.True(panel.NoteText.IsVisible);
        Assert.Equal($"Refused: “{Reply}”. The values are kept; fix a part and press Enter again.", panel.NoteText.Text);
        Assert.Equal("8 4 w sr22", panel.Box.Text);
        Assert.True(entry.IsSubMenuOpen);

        panel.Box.Text = "8 4";
        PressKey(panel.Box, Key.Enter);

        Assert.Single(host.Sent);
        Assert.Equal("Set the direction, type first; Enter sends nothing.", panel.NoteText.Text);
    }

    [AvaloniaFact]
    public void ASendThatNeverReachedTheSim_KeepsFlyoutOpenWithTheNote()
    {
        RecordingMenuHost host = HostWith(N22Rx);
        host.SendResult = null;
        (MenuItem entry, SoloAdvisoryPanel panel) = OpenEntry(host, SoloAdvisoryPrefill.TrueValues, liveCheck: false);

        panel.Box.Text = "2 4 w sr22";
        PressKey(panel.Box, Key.Enter);

        Assert.Single(host.Sent);
        Assert.Equal("Not sent: connection error (see status line)", panel.NoteText.Text);
        Assert.True(panel.NoteText.IsVisible);
        Assert.Equal("2 4 w sr22", panel.Box.Text);
        Assert.True(entry.IsSubMenuOpen);
    }

    [AvaloniaFact]
    public void ASecondEnterWhileSending_SendsNothing_AndAnAnswerAfterClosingIsDropped()
    {
        RecordingMenuHost host = HostWith(N22Rx);
        var inFlight = new TaskCompletionSource<CommandResultDto?>();
        host.PendingSend = inFlight;
        (MenuItem entry, SoloAdvisoryPanel panel) = OpenEntry(host, SoloAdvisoryPrefill.TrueValues, liveCheck: false);

        panel.Box.Text = "2 4 w sr22";
        PressKey(panel.Box, Key.Enter);
        PressKey(panel.Box, Key.Enter);

        Assert.Single(host.Sent);

        entry.IsSubMenuOpen = false;
        inFlight.SetResult(new CommandResultDto(false, "Unable"));
        Dispatcher.UIThread.RunJobs();
        entry.IsSubMenuOpen = true;

        Assert.False(panel.NoteText.IsVisible);

        host.PendingSend = null;
        host.SendResult = new CommandResultDto(false, "Unable");
        panel.Box.Text = "2 4 w sr22";
        PressKey(panel.Box, Key.Enter);

        Assert.Equal(2, host.Sent.Count);
        Assert.Equal("Refused: “Unable”. The values are kept; fix a part and press Enter again.", panel.NoteText.Text);
    }

    [AvaloniaFact]
    public void AFilter_PicksAnAircraftBeyondTheNearestListed()
    {
        Traffic far = new("N999FA", "C172")
        {
            Clock = 9,
            Nm = 15,
            Track = 90,
            AltitudeFt = 3000,
            Direction = "eastbound",
        };
        RecordingMenuHost host = HostWith(N22Rx, far);
        SoloAdvisoryPanel panel = Open(host, SoloAdvisoryPrefill.TrueValues, liveCheck: false);

        panel.Box.Text = "n999";

        Assert.Equal("Tab picks N999FA", panel.GhostText.Text);

        PressKey(panel.Box, Key.Enter);

        Assert.Equal("N999FA", panel.Pointed);
        Assert.Equal("Tab 9 clock", panel.GhostText.Text);
        Assert.Empty(host.Sent);
    }

    [AvaloniaFact]
    public void FourForms_GreyedWithReason()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        RecordingMenuHost host = HostWith(N22Rx, OnBase());
        SoloAdvisoryPanel panel = Open(host, SoloAdvisoryPrefill.TrueValues, liveCheck: false);

        Click(RowOf(panel, "N22RX"));
        Click(panel.BuilderToggle);

        Assert.Equal(["Radar clock", "Nose NR", "Pattern DW", "Over fix"], panel.FormTabs.Select(tab => tab.Content as string));
        Assert.Equal([true, true, false, false], panel.FormTabs.Select(tab => tab.IsEnabled));
        Assert.Equal("N22RX is in no pattern leg", ToolTip.GetTip(panel.FormTabs[2]));
        Assert.Equal("N22RX is not within 2 NM of a reporting point", ToolTip.GetTip(panel.FormTabs[3]));

        host.ReportingPoints.Add("VPCOL");
        Click(RowOf(panel, "N9152M"));

        Assert.All(panel.FormTabs, tab => Assert.True(tab.IsEnabled));

        Click(panel.FormTabs[2]);

        Assert.Equal("BASE L 2 28L C152", panel.Box.Text);

        Click(panel.FormTabs[3]);

        Assert.Equal("OVER VPCOL C152", panel.Box.Text);
    }

    [AvaloniaFact]
    public void BlankPrefill_RowsAndBearingLabelDropClockAndMiles()
    {
        RecordingMenuHost blankHost = HostWith(N22Rx);
        SoloAdvisoryPanel blank = Open(blankHost, SoloAdvisoryPrefill.LeftForStudent, liveCheck: false);

        Click(RowOf(blank, "N22RX"));

        Assert.Equal("1,500 above · find it on the scope", RowOf(blank, "N22RX").Where);
        Assert.False(blankHost.RadarCues[^1]!.ShowBearingLabel);
        Assert.Equal("next: ‹clock›", blank.GhostText.Text);

        RecordingMenuHost trueHost = HostWith(N22Rx);
        SoloAdvisoryPanel shown = Open(trueHost, SoloAdvisoryPrefill.TrueValues, liveCheck: false);

        Click(RowOf(shown, "N22RX"));

        Assert.Equal("2 o'clock · 4 nm · 1,500 above · westbound", RowOf(shown, "N22RX").Where);
        Assert.True(trueHost.RadarCues[^1]!.ShowBearingLabel);
    }

    [AvaloniaFact]
    public void BlankPrefill_GhostFollowsTheBuilderForm_AndSeededPartsSurviveTyping()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        RecordingMenuHost host = HostWith(N22Rx, OnBase());
        SoloAdvisoryPanel panel = Open(host, SoloAdvisoryPrefill.LeftForStudent, liveCheck: false);

        Click(RowOf(panel, "N9152M"));
        Click(panel.BuilderToggle);
        Click(panel.FormTabs[2]);

        Assert.Equal("", panel.Box.Text);
        Assert.Equal("next: ‹leg›", panel.GhostText.Text);

        Click(RowOf(panel, "N22RX"));
        Click(panel.FormTabs[0]);

        Assert.Equal(35, panel.Builder!.Altitude.Value);

        panel.Box.Text = "2";

        Assert.Equal(35, panel.Builder!.Altitude.Value);
        Assert.Equal("Tab SR22 type", GhostAfter(panel, "2 4 w"));
    }

    [AvaloniaFact]
    public void UncheckingUnknownAltitude_GivesTheAircraftsOwnAltitude()
    {
        RecordingMenuHost host = HostWith(N22Rx);
        SoloAdvisoryPanel panel = Open(host, SoloAdvisoryPrefill.TrueValues, liveCheck: false);
        Click(RowOf(panel, "N22RX"));
        Click(panel.BuilderToggle);
        panel.Box.Text = "2 4 w sr22";

        Assert.True(panel.Builder!.AltitudeUnknown.IsChecked);
        Assert.True(panel.Builder.AltitudeUnknown.IsEnabled);

        panel.Builder.AltitudeUnknown.IsChecked = false;

        Assert.Equal("2 4 W SR22 020", panel.Box.Text);
        Assert.False(panel.Builder!.AltitudeUnknown.IsChecked);
        Assert.Equal(20, panel.Builder.Altitude.Value);
    }

    [AvaloniaFact]
    public void WithNoCommandedAircraft_TheUnknownAltitudeBoxIsDisabled_AndSeedsNoAltitude()
    {
        RecordingMenuHost host = HostWith(N22Rx);
        MenuItem entry = Assert.IsType<MenuItem>(
            MenuCatalog.Get(MenuIds.ApproachReportTrafficInSight).Build(null, SoloContext(SoloAdvisoryPrefill.TrueValues, false), host)
        );
        SoloAdvisoryPanel panel = Assert.IsType<SoloAdvisoryFlyoutHost>(Assert.IsType<MenuItem>(Assert.Single(entry.Items)).Header).Panel;
        entry.IsSubMenuOpen = true;
        Click(RowOf(panel, "N22RX"));
        Click(panel.BuilderToggle);
        panel.Box.Text = "2 4 w sr22";

        Assert.True(panel.Builder!.AltitudeUnknown.IsChecked);
        Assert.False(panel.Builder.AltitudeUnknown.IsEnabled);
        Assert.Equal(AdvisoryBuilderView.NoAircraftAltitudeTip, ToolTip.GetTip(panel.Builder.AltitudeUnknown));

        panel.Builder.AltitudeUnknown.IsChecked = false;

        Assert.Equal("2 4 w sr22", panel.Box.Text);
    }

    [AvaloniaFact]
    public void Tab_FillsNextPartFromPointedRow()
    {
        RecordingMenuHost host = HostWith(N22Rx);
        SoloAdvisoryPanel panel = Open(host, SoloAdvisoryPrefill.TrueValues, liveCheck: false);

        panel.Box.Text = "n22";

        Assert.Equal("Tab picks N22RX", panel.GhostText.Text);

        PressKey(panel.Box, Key.Tab);

        Assert.Equal("", panel.Box.Text);
        Assert.True(RowOf(panel, "N22RX").IsPointed);
        Assert.Equal("Tab 2 clock", panel.GhostText.Text);

        List<string> typed = [];
        for (int i = 0; i < 5; i++)
        {
            PressKey(panel.Box, Key.Tab);
            typed.Add(panel.Box.Text ?? "");
        }

        Assert.Equal(["2 ", "2 4 ", "2 4 W ", "2 4 W SR22 ", "2 4 W SR22 035 "], typed);
        Assert.Equal(SoloAdvisoryModel.CompleteGhost, panel.GhostText.Text);
        Assert.Equal("RTIS 2 4 W SR22 035", panel.BoxCommandText.Text);
    }

    [AvaloniaFact]
    public void SoloReportRow_NeverSendsCallsignForm()
    {
        Traffic n738Sp = new("N738SP", "C172")
        {
            Clock = 12,
            Nm = 3,
            Track = 90,
            AltitudeFt = 1000,
            Direction = "eastbound",
        };
        RecordingMenuHost host = HostWith(N22Rx, n738Sp);
        SoloAdvisoryPanel panel = Open(host, SoloAdvisoryPrefill.TrueValues, liveCheck: true);

        foreach (SoloTrafficRowButton row in panel.Rows.ToList())
        {
            Click(row);
            PressKey(panel.Box, Key.Enter);
        }

        panel.Box.Text = "N738SP";
        PressKey(panel.Box, Key.Enter);

        Assert.Empty(host.Sent);
        Assert.True(RowOf(panel, "N738SP").IsPointed);
        Assert.Equal("", panel.Box.Text);
        Assert.All(panel.Rows, row => Assert.DoesNotContain("RTIS", row.ToString()));
    }

    [AvaloniaFact]
    public void ClosingTheSubmenu_LeavesNoObserverAndNoTimer()
    {
        RecordingMenuHost host = HostWith(N22Rx);
        (MenuItem entry, SoloAdvisoryPanel panel) = OpenEntry(host, SoloAdvisoryPrefill.TrueValues, liveCheck: true);
        panel.Box.Text = "2 4";

        Assert.Equal(1, host.AircraftUpdateObserverCount);
        Assert.Equal(1, host.Clock.LiveTimerCount);

        entry.IsSubMenuOpen = false;

        Assert.Equal(0, host.AircraftUpdateObserverCount);
        Assert.Equal(0, host.Clock.LiveTimerCount);
    }

    [AvaloniaFact]
    public void ASuccessfulSend_ClosesTheSubmenu_LeavingNoObserverAndNoTimer()
    {
        RecordingMenuHost host = HostWith(N22Rx);
        (MenuItem entry, SoloAdvisoryPanel panel) = OpenEntry(host, SoloAdvisoryPrefill.TrueValues, liveCheck: true);

        panel.Box.Text = "2 4 w sr22 035";
        PressKey(panel.Box, Key.Enter);

        Assert.Single(host.Sent);
        Assert.False(entry.IsSubMenuOpen);
        Assert.Equal(0, host.AircraftUpdateObserverCount);
        Assert.Equal(0, host.Clock.LiveTimerCount);
    }

    [AvaloniaFact]
    public void ClosingTheStripFlyout_LeavesNoObserverAndNoTimer()
    {
        RecordingMenuHost host = HostWith(N22Rx);
        (ContextMenu menu, MenuFlyout flyout) = OpenStripFlyout(host);
        SoloAdvisoryPanel panel = Assert.Single(flyout.Items.OfType<MenuItem>().Select(item => item.Header).OfType<SoloAdvisoryFlyoutHost>()).Panel;
        panel.Box.Text = "2 4";

        Assert.Equal(1, host.AircraftUpdateObserverCount);

        flyout.Hide();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, host.AircraftUpdateObserverCount);
        Assert.Equal(0, host.Clock.LiveTimerCount);
        menu.Close();
    }

    // --- Harness ----------------------------------------------------------------------------

    /// <summary>
    /// One other aircraft, placed by clock and miles off N52TW (heading 300): the matcher's candidate and the flyout's row for
    /// it.
    /// </summary>
    private sealed record Traffic(string Callsign, string Type)
    {
        public required double Clock { get; init; }

        public required double Nm { get; init; }

        public required double Track { get; init; }

        public required double AltitudeFt { get; init; }

        public required string Direction { get; init; }

        public string? Runway { get; init; }

        public PatternPosition? Pattern { get; init; }

        public TrafficAdvisoryCandidate Candidate =>
            new TrafficAdvisoryCandidate(
                Callsign,
                GeoMath.ProjectPoint(Here, new TrueHeading((300 + (Clock * 30)) % 360), Nm),
                0,
                Track,
                AltitudeFt,
                Type,
                Type,
                false,
                null,
                null
            ) with
            {
                AssignedRunwayId = Runway,
                PatternPosition = Pattern,
            };

        public MenuAirborneTrafficRow Row => new(new MenuTrafficRow(Callsign, Type, Nm, (int)Clock, (int)(AltitudeFt - 2000)), Direction, null);
    }

    private static Traffic OnBase() =>
        new("N9152M", "C152")
        {
            Clock = 11,
            Nm = 4,
            Track = 0,
            AltitudeFt = 900,
            Direction = "northbound",
            Runway = "28L",
            Pattern = new PatternPosition(PatternEntryLeg.Base, PatternDirection.Left, 2.2),
        };

    private static RecordingMenuHost HostWith(params Traffic[] traffic)
    {
        var host = new RecordingMenuHost("") { Session = SoloContext(SoloAdvisoryPrefill.TrueValues, true).Session };
        host.AdvisoryCandidates.Add(Me);
        foreach (Traffic each in traffic)
        {
            host.AdvisoryCandidates.Add(each.Candidate);
            host.AirborneTraffic.Add(each.Row);
        }

        return host;
    }

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

    private static MenuContext SoloContext(SoloAdvisoryPrefill prefill, bool liveCheck) =>
        new(
            new MenuClick(Callsign, null, null, []),
            new MenuSession(Initials, true, VfrCommandsForIfr.None, QuickCommandDefaults.For, prefill, liveCheck, CommandScheme.Default())
        );

    private static SoloAdvisoryPanel Open(RecordingMenuHost host, SoloAdvisoryPrefill prefill, bool liveCheck) =>
        OpenEntry(host, prefill, liveCheck).Panel;

    private static (MenuItem Entry, SoloAdvisoryPanel Panel) OpenEntry(RecordingMenuHost host, SoloAdvisoryPrefill prefill, bool liveCheck)
    {
        MenuItem entry = Assert.IsType<MenuItem>(
            MenuCatalog.Get(MenuIds.ApproachReportTrafficInSight).Build(Pilot(), SoloContext(prefill, liveCheck), host)
        );
        SoloAdvisoryPanel panel = Assert.IsType<SoloAdvisoryFlyoutHost>(Assert.IsType<MenuItem>(Assert.Single(entry.Items)).Header).Panel;
        entry.IsSubMenuOpen = true;
        return (entry, panel);
    }

    /// <summary>Opens the aircraft menu in a window and the strip's Report traffic in sight… flyout, on the host's solo session.</summary>
    private static (ContextMenu Menu, MenuFlyout Flyout) OpenStripFlyout(RecordingMenuHost host)
    {
        ContextMenu menu = AircraftMenuBuilder.Build(
            new AircraftModel
            {
                Callsign = Callsign,
                AircraftType = "PA28",
                FlightRules = "VFR",
                IsOnGround = false,
                Altitude = 2000,
                Position = Here,
                Situation = AircraftSituation.VfrFlightFollowing,
            },
            new MenuClick(Callsign, null, null, []),
            host,
            _ => []
        );
        var window = new Window { Width = 600, Height = 900 };
        window.Show();
        menu.Open(window);
        Dispatcher.UIThread.RunJobs();
        menu.UpdateLayout();
        Button button = QuickCommandStrip
            .Buttons(Assert.Single(menu.Items.OfType<MenuItem>(), QuickCommandStrip.IsStrip))
            .Single(each => (string)each.Tag! == MenuIds.ApproachReportTrafficInSight);
        QuickCommandStrip.OpenSubmenu(button);
        Dispatcher.UIThread.RunJobs();
        MenuFlyout flyout = Assert.IsType<MenuFlyout>(FlyoutBase.GetAttachedFlyout(button));
        Assert.True(flyout.IsOpen);
        return (menu, flyout);
    }

    /// <summary>The ghost once <paramref name="typed"/> is in the box.</summary>
    private static string GhostAfter(SoloAdvisoryPanel panel, string typed)
    {
        panel.Box.Text = typed;
        return panel.GhostText.Text ?? "";
    }

    private static SoloTrafficRowButton RowOf(SoloAdvisoryPanel panel, string callsign) => Assert.Single(panel.Rows, row => row.Callsign == callsign);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void PressKey(Control target, Key key) =>
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
}
