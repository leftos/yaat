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
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Situation;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Report field in sight… opens the field advisory flyout in both modes: the clock and miles box, D5's clock dial (captioned
/// <c>field</c>) and a Miles stepper that write it, the spoken line and the destination field's row. The true clock and miles are
/// pre-filled in RPO mode and in solo with true values, and follow the aircraft until the user edits them; in solo with values
/// left for the student the box starts empty and the row says to find the field on the scope. The sim does not grade the field
/// advisory, so the check line says so whatever the live-check setting. Real navdata: the destination is KOAK.
/// </summary>
public class FieldAdvisoryFlyoutTests
{
    private const string Callsign = "N52TW";
    private const string Initials = "AB";
    private const string Field = "KOAK";
    private const string Runways = "runways 10L 10R 12 15 28L 28R 30 33";
    private const string NotChecked = "not checked: the sim does not grade the field advisory";

    public FieldAdvisoryFlyoutTests() => MenuGoldenFixtures.EnsureNavData();

    [AvaloniaFact]
    public void Rpo_OpensPrefilledWithTrueClockAndMiles_EnterSendsRfis()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        var host = new RecordingMenuHost("");
        AircraftModel aircraft = EastOfField(5);
        (MenuItem entry, FieldAdvisoryPanel panel) = OpenEntry(aircraft, Rpo(), host);

        Assert.Equal("Report field in sight…", entry.Header);
        Assert.Equal(FieldAdvisoryFlyout.Title, panel.Title);
        Assert.Equal(AirborneTrafficFlyout.TitleNow(aircraft, Callsign), panel.TitleNow);
        Assert.False(panel.HasSoloBadge);
        Assert.Equal("Field advisory for KOAK", panel.Lead);
        Assert.Equal("KOAK · destination", panel.BoxSub);
        Assert.Equal("12 5", panel.Box.Text);
        Assert.Equal("12", panel.Dial.Value);
        Assert.Equal("field", panel.DialCaption);
        Assert.Equal(5, panel.Miles.Value);
        Assert.Equal("RFIS 12 5", panel.BoxCommandText.Text);
        Assert.Equal("complete · Enter sends", panel.GhostText.Text);
        Assert.True(panel.SpokenText.IsVisible);
        Assert.Equal("“Field's at your 12 o'clock, 5 miles, report it in sight.”", panel.SpokenText.Text);
        Assert.Equal("● KOAK · Oakland San Francisco Bay · destination", panel.FieldRowName);
        Assert.Equal($"12 o'clock · 5 nm · {Runways}", panel.FieldRowWhere);
        Assert.True(panel.SendButton.IsEnabled);

        PressKey(panel.Box, Key.Enter);

        Assert.Equal([(Callsign, "RFIS 12 5", Initials)], host.Sent);
        Assert.False(entry.IsSubMenuOpen);
    }

    [AvaloniaFact]
    public void Solo_TrueValues_Prefilled()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        FieldAdvisoryPanel panel = Open(SouthOfField(8), Solo(SoloAdvisoryPrefill.TrueValues, false), new RecordingMenuHost(""));

        Assert.True(panel.HasSoloBadge);
        Assert.Equal("3 8", panel.Box.Text);
        Assert.Equal("3", panel.Dial.Value);
        Assert.Equal(8, panel.Miles.Value);
        Assert.Equal("RFIS 3 8", panel.BoxCommandText.Text);
        Assert.Equal($"3 o'clock · 8 nm · {Runways}", panel.FieldRowWhere);
    }

    [AvaloniaFact]
    public void Solo_LeftForStudent_BoxEmpty_RowSaysFindItOnTheScope()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        FieldAdvisoryPanel panel = Open(EastOfField(5), Solo(SoloAdvisoryPrefill.LeftForStudent, false), new RecordingMenuHost(""));

        Assert.Equal("", panel.Box.Text ?? "");
        Assert.Equal("clock miles: 12 5", panel.Box.PlaceholderText);
        Assert.Null(panel.Dial.Value);
        Assert.Null(panel.Miles.Value);
        Assert.Equal("RFIS ···", panel.BoxCommandText.Text);
        Assert.Equal("next: ‹clock›", panel.GhostText.Text);
        Assert.False(panel.SpokenText.IsVisible);
        Assert.Equal("find it on the scope", panel.FieldRowWhere);
        Assert.True(panel.IsFieldRowWhereItalic);
        Assert.False(panel.SendButton.IsEnabled);
    }

    [AvaloniaFact]
    public void DialAndStepper_WriteTheBox()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        FieldAdvisoryPanel panel = Open(EastOfField(5), Solo(SoloAdvisoryPrefill.LeftForStudent, false), new RecordingMenuHost(""));

        Click(panel.Dial.Buttons["3"]);

        Assert.Equal("3", panel.Box.Text);
        Assert.Equal("next: ‹miles›", panel.GhostText.Text);

        Click(panel.Miles.More);
        Click(panel.Miles.More);

        Assert.Equal("3 2", panel.Box.Text);
        Assert.Equal("RFIS 3 2", panel.BoxCommandText.Text);
        Assert.Equal("complete · Enter sends", panel.GhostText.Text);

        Click(panel.Dial.Buttons["11"]);

        Assert.Equal("11 2", panel.Box.Text);
        Assert.Equal("11", panel.Dial.Value);
        Assert.Equal("“Field's at your 11 o'clock, 2 miles, report it in sight.”", panel.SpokenText.Text);
    }

    [AvaloniaFact]
    public void TypingMovesDialAndStepper()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        FieldAdvisoryPanel panel = Open(EastOfField(5), Rpo(), new RecordingMenuHost(""));

        panel.Box.Text = "7 9";

        Assert.Equal("7", panel.Dial.Value);
        Assert.Equal(9, panel.Miles.Value);
        Assert.Equal("RFIS 7 9", panel.BoxCommandText.Text);

        panel.Box.Text = "2 14";

        Assert.Equal("2", panel.Dial.Value);
        Assert.Equal(14, panel.Miles.Value);
    }

    [AvaloniaFact]
    public void IncompleteEnter_SendsNothing_SaysWhatIsMissing()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        var host = new RecordingMenuHost("");
        (MenuItem entry, FieldAdvisoryPanel panel) = OpenEntry(EastOfField(5), Solo(SoloAdvisoryPrefill.LeftForStudent, false), host);

        PressKey(panel.Box, Key.Enter);

        Assert.Empty(host.Sent);
        Assert.True(panel.NoteText.IsVisible);
        Assert.Equal("Set the clock, miles first; Enter sends nothing.", panel.NoteText.Text);

        panel.Box.Text = "7";
        PressKey(panel.Box, Key.Enter);

        Assert.Empty(host.Sent);
        Assert.Equal("Set the miles first; Enter sends nothing.", panel.NoteText.Text);
        Assert.Equal("RFIS ···", panel.BoxCommandText.Text);
        Assert.True(entry.IsSubMenuOpen);
    }

    [AvaloniaFact]
    public void Refusal_KeepsFlyoutOpenWithSimReply()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        const string Reply = "Unable, KOAK not in nav database";
        var host = new RecordingMenuHost("") { SendResult = new CommandResultDto(false, Reply) };
        AircraftModel aircraft = EastOfField(5);
        (MenuItem entry, FieldAdvisoryPanel panel) = OpenEntry(aircraft, Solo(SoloAdvisoryPrefill.TrueValues, false), host);

        PressKey(panel.Box, Key.Enter);

        Assert.Equal([(Callsign, "RFIS 12 5", Initials)], host.Sent);
        Assert.True(entry.IsSubMenuOpen);
        Assert.True(panel.NoteText.IsVisible);
        Assert.Equal($"Refused: “{Reply}”. The values are kept; fix a part and press Enter again.", panel.NoteText.Text);
        Assert.Equal("12 5", panel.Box.Text);

        aircraft.Position = EastOfField(9).Position;
        host.RaiseAircraftUpdate();

        Assert.Equal("12 5", panel.Box.Text);
    }

    [AvaloniaFact]
    public void CheckLine_AlwaysNotChecked_EvenWithLiveCheckOn()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        FieldAdvisoryPanel live = Open(EastOfField(5), Solo(SoloAdvisoryPrefill.TrueValues, true), new RecordingMenuHost(""));
        FieldAdvisoryPanel blank = Open(EastOfField(5), Solo(SoloAdvisoryPrefill.LeftForStudent, false), new RecordingMenuHost(""));
        FieldAdvisoryPanel rpo = Open(EastOfField(5), Rpo(), new RecordingMenuHost(""));

        Assert.All([live, blank, rpo], panel => Assert.True(panel.CheckText.IsVisible));
        Assert.All([live, blank, rpo], panel => Assert.Equal(NotChecked, panel.CheckText.Text));

        live.Box.Text = "6 30";

        Assert.Equal(NotChecked, live.CheckText.Text);
    }

    [AvaloniaTheory]
    [InlineData("")]
    [InlineData("ZZZZ")]
    public void NoDestination_StripInert_AllCommandsDisabledWithReason(string destination)
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        const string Inert = "Report field in sight… · no destination field";
        var host = new RecordingMenuHost("");
        AircraftModel aircraft = EastOfField(5);
        aircraft.Destination = destination;

        MenuItem entry = Build(aircraft, Rpo(), host);

        Assert.False(entry.IsEnabled);
        Assert.Equal(Inert, entry.Header?.ToString());
        Assert.Empty(entry.Items);

        ContextMenu menu = OpenInWindow(AircraftMenuBuilder.Build(aircraft, new MenuClick(Callsign, null, null, []), host, _ => []));
        Button button = QuickCommandStrip
            .Buttons(Assert.Single(menu.Items.OfType<MenuItem>(), QuickCommandStrip.IsStrip))
            .Single(each => (string)each.Tag! == MenuIds.ApproachReportFieldInSight);
        Assert.Equal(MenuGlyphRowTemplate.DimmedOpacity, button.Opacity);
        Assert.Null(FlyoutBase.GetAttachedFlyout(button));
        MenuItem allCommands = Assert.Single(
            menu.GetLogicalDescendants().OfType<MenuItem>(),
            item => !QuickCommandStrip.IsStrip(item) && (item.Header?.ToString() == Inert)
        );
        Assert.False(allCommands.IsEnabled);
        menu.Close();
    }

    [AvaloniaFact]
    public void PrefillFollowsUpdates_UntilTheUserEdits()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        var host = new RecordingMenuHost("");
        AircraftModel aircraft = EastOfField(5);
        FieldAdvisoryPanel panel = Open(aircraft, Rpo(), host);

        aircraft.Position = EastOfField(8).Position;
        host.RaiseAircraftUpdate();

        Assert.Equal("12 8", panel.Box.Text);
        Assert.Equal(8, panel.Miles.Value);

        Click(panel.Dial.Buttons["1"]);
        aircraft.Position = EastOfField(10).Position;
        host.RaiseAircraftUpdate();

        Assert.Equal("1 8", panel.Box.Text);
        Assert.Equal("1", panel.Dial.Value);
        Assert.Equal($"12 o'clock · 10 nm · {Runways}", panel.FieldRowWhere);

        FieldAdvisoryPanel typed = Open(aircraft, Rpo(), host);
        typed.Box.Text = "12 4";
        aircraft.Position = EastOfField(6).Position;
        host.RaiseAircraftUpdate();

        Assert.Equal("12 4", typed.Box.Text);
    }

    [AvaloniaFact]
    public void FieldRow_NamesTheRoomsArrivalEnds_ElseEveryEnd()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        var host = new RecordingMenuHost("")
        {
            RoomActiveRunways = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["OAK"] = ["30", "A28R", "A28L"] },
        };

        FieldAdvisoryPanel active = Open(EastOfField(5), Rpo(), host);
        FieldAdvisoryPanel none = Open(EastOfField(5), Rpo(), new RecordingMenuHost(""));

        Assert.Equal("12 o'clock · 5 nm · runways 28L 28R 30", active.FieldRowWhere);
        Assert.Equal($"12 o'clock · 5 nm · {Runways}", none.FieldRowWhere);
    }

    [AvaloniaFact]
    public void MilesStepper_StepsFromAFarPrefill_WithoutClamping()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        FieldAdvisoryPanel panel = Open(EastOfField(150), Rpo(), new RecordingMenuHost(""));

        Assert.Equal("12 150", panel.Box.Text);

        Click(panel.Miles.More);

        Assert.Equal("12 151", panel.Box.Text);

        Click(panel.Miles.Less);
        Click(panel.Miles.Less);

        Assert.Equal("12 149", panel.Box.Text);
        Assert.Equal(149, panel.Miles.Value);
    }

    [AvaloniaFact]
    public void EmptyingTheBox_ClearsTheDialAndTheStepper()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        FieldAdvisoryPanel panel = Open(EastOfField(5), Rpo(), new RecordingMenuHost(""));

        panel.Box.Text = "";

        Assert.Null(panel.Dial.Value);
        Assert.Null(panel.Miles.Value);
        Assert.Equal("next: ‹clock›", panel.GhostText.Text);

        panel.Box.Text = "4";

        Assert.Equal("4", panel.Dial.Value);
        Assert.Null(panel.Miles.Value);
    }

    [AvaloniaFact]
    public void ASendThatNeverReachedTheSim_KeepsFlyoutOpenWithTheNote()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        var host = new RecordingMenuHost("") { SendResult = null };
        (MenuItem entry, FieldAdvisoryPanel panel) = OpenEntry(EastOfField(5), Rpo(), host);

        PressKey(panel.Box, Key.Enter);

        Assert.Single(host.Sent);
        Assert.True(panel.NoteText.IsVisible);
        Assert.Equal(SoloAdvisoryPanel.NotSentNote, panel.NoteText.Text);
        Assert.Equal("12 5", panel.Box.Text);
        Assert.True(entry.IsSubMenuOpen);
    }

    [AvaloniaFact]
    public void ClosingTheSubmenu_LeavesNoObserver()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        var host = new RecordingMenuHost("");
        (MenuItem entry, _) = OpenEntry(EastOfField(5), Rpo(), host);

        Assert.Equal(1, host.AircraftUpdateObserverCount);

        entry.IsSubMenuOpen = false;

        Assert.Equal(0, host.AircraftUpdateObserverCount);
    }

    [AvaloniaFact]
    public void ClosingTheStripFlyout_LeavesNoObserverAndNoTimer()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        var host = new RecordingMenuHost("");
        ContextMenu menu = OpenInWindow(AircraftMenuBuilder.Build(EastOfField(5), new MenuClick(Callsign, null, null, []), host, _ => []));
        Button button = QuickCommandStrip
            .Buttons(Assert.Single(menu.Items.OfType<MenuItem>(), QuickCommandStrip.IsStrip))
            .Single(each => (string)each.Tag! == MenuIds.ApproachReportFieldInSight);
        QuickCommandStrip.OpenSubmenu(button);
        Dispatcher.UIThread.RunJobs();
        MenuFlyout flyout = Assert.IsType<MenuFlyout>(FlyoutBase.GetAttachedFlyout(button));
        FieldAdvisoryPanel panel = Assert.Single(flyout.Items.OfType<MenuItem>().Select(item => item.Header).OfType<FieldAdvisoryFlyoutHost>()).Panel;

        Assert.Equal("12 5", panel.Box.Text);
        Assert.Equal(1, host.AircraftUpdateObserverCount);

        flyout.Hide();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, host.AircraftUpdateObserverCount);
        Assert.Equal(0, host.Clock.LiveTimerCount);
        menu.Close();
    }

    [AvaloniaFact]
    public void ASecondEnterWhileSending_SendsNothing_AndAnAnswerAfterClosingIsDropped()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        var inFlight = new TaskCompletionSource<CommandResultDto?>();
        var host = new RecordingMenuHost("") { PendingSend = inFlight };
        (MenuItem entry, FieldAdvisoryPanel panel) = OpenEntry(EastOfField(5), Rpo(), host);

        PressKey(panel.Box, Key.Enter);
        PressKey(panel.Box, Key.Enter);

        Assert.Single(host.Sent);

        entry.IsSubMenuOpen = false;
        inFlight.SetResult(new CommandResultDto(false, "Unable"));
        Dispatcher.UIThread.RunJobs();

        Assert.False(panel.NoteText.IsVisible);
        Assert.Equal("", panel.Box.Text ?? "");

        entry.IsSubMenuOpen = true;

        Assert.False(panel.NoteText.IsVisible);
        Assert.Equal("12 5", panel.Box.Text);
    }

    [AvaloniaFact]
    public void ATakeArrivingAfterClosing_DoesNotCloseTheReopenedFlyout()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        var inFlight = new TaskCompletionSource<CommandResultDto?>();
        var host = new RecordingMenuHost("") { PendingSend = inFlight };
        (MenuItem entry, FieldAdvisoryPanel panel) = OpenEntry(EastOfField(5), Rpo(), host);

        PressKey(panel.Box, Key.Enter);
        entry.IsSubMenuOpen = false;
        entry.IsSubMenuOpen = true;
        inFlight.SetResult(new CommandResultDto(true, ""));
        Dispatcher.UIThread.RunJobs();

        Assert.Single(host.Sent);
        Assert.True(entry.IsSubMenuOpen);
        Assert.Equal("12 5", panel.Box.Text);
    }

    // --- Harness ----------------------------------------------------------------------------

    private static LatLon FieldPosition()
    {
        (double lat, double lon) =
            NavigationDatabase.Instance.GetAirportPosition(Field) ?? throw new InvalidOperationException("KOAK not in navdata");
        return new LatLon(lat, lon);
    }

    /// <summary>An IFR arrival to KOAK <paramref name="nm"/> NM east of the field, heading west: the field at its 12 o'clock.</summary>
    private static AircraftModel EastOfField(double nm) => Arrival(GeoMath.ProjectPoint(FieldPosition(), new TrueHeading(90), nm), 270);

    /// <summary>An IFR arrival to KOAK <paramref name="nm"/> NM south of the field, heading west: the field at its 3 o'clock.</summary>
    private static AircraftModel SouthOfField(double nm) => Arrival(GeoMath.ProjectPoint(FieldPosition(), new TrueHeading(180), nm), 270);

    private static AircraftModel Arrival(LatLon position, double trueHeading) =>
        new()
        {
            Callsign = Callsign,
            AircraftType = "B738",
            FlightRules = "IFR",
            IsOnGround = false,
            Altitude = 4000,
            Position = position,
            Heading = new TrueHeading(trueHeading),
            Destination = Field,
            Situation = AircraftSituation.Approach,
        };

    private static MenuContext Rpo() =>
        new(
            new MenuClick(Callsign, null, null, []),
            new MenuSession(Initials, false, VfrCommandsForIfr.None, QuickCommandDefaults.For, SoloAdvisoryPrefill.TrueValues, false)
        );

    private static MenuContext Solo(SoloAdvisoryPrefill prefill, bool liveCheck) =>
        new(
            new MenuClick(Callsign, null, null, []),
            new MenuSession(Initials, true, VfrCommandsForIfr.None, QuickCommandDefaults.For, prefill, liveCheck)
        );

    private static MenuItem Build(AircraftModel aircraft, MenuContext context, RecordingMenuHost host) =>
        Assert.IsType<MenuItem>(MenuCatalog.Get(MenuIds.ApproachReportFieldInSight).Build(aircraft, context, host));

    private static FieldAdvisoryPanel Open(AircraftModel aircraft, MenuContext context, RecordingMenuHost host) =>
        OpenEntry(aircraft, context, host).Panel;

    private static (MenuItem Entry, FieldAdvisoryPanel Panel) OpenEntry(AircraftModel aircraft, MenuContext context, RecordingMenuHost host)
    {
        MenuItem entry = Build(aircraft, context, host);
        FieldAdvisoryPanel panel = Assert.IsType<FieldAdvisoryFlyoutHost>(Assert.IsType<MenuItem>(Assert.Single(entry.Items)).Header).Panel;
        entry.IsSubMenuOpen = true;
        return (entry, panel);
    }

    private static ContextMenu OpenInWindow(ContextMenu menu)
    {
        var window = new Window { Width = 600, Height = 900 };
        window.Show();
        menu.Open(window);
        Dispatcher.UIThread.RunJobs();
        menu.UpdateLayout();
        return menu;
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void PressKey(Control target, Key key) =>
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
}
