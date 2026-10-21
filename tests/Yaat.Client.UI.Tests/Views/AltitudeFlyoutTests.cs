using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Sim.Commands;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The altitude flyouts (<see cref="AltitudeFlyout"/>): Maintain altitude, one builder for the strip's button and All Commands'
/// entry, holding the title line, the Altitude box and the rich list of altitudes (marks, the MVA line, greyed rows, a command
/// on every row) in a 300 px scrolling panel centred on the assigned row; and the one-box Temporary altitude and Cruise flyouts,
/// each showing the command it will send. Each refreshes once a second while open, and no row moves.
/// </summary>
public class AltitudeFlyoutTests
{
    private const string Callsign = "UAL455";
    private const string Initials = "AB";

    private static MenuContext Context() => TestMenuContext.Create(Callsign, Initials, null, false, VfrCommandsForIfr.None);

    private static FakeMenuAircraft Aircraft(double altitude, double? assigned) =>
        new()
        {
            Callsign = Callsign,
            AltitudeFeet = altitude,
            AssignedAltitude = assigned,
        };

    // --- The strip's Maintain altitude ------------------------------------------------------------

    /// <summary>
    /// The strip's Maintain altitude opens the flyout, no popup: the title line, the box holding focus, today's rows with their
    /// marks and commands, the MVA line between 2,600 and 2,500, the rows under it greyed; the list is 300 px tall and opens with
    /// the assigned row centred.
    /// </summary>
    [AvaloniaFact]
    public void StripMaintain_OpensTheFlyout_RowsMarked_AssignedRowCentred()
    {
        var host = new RecordingMenuHost("") { Mva = ("12", 2600) };
        using StripFlyout open = OpenStripMaintain(Aircraft(3040, 5000), host);
        MaintainAltitudePanel panel = open.Panel;

        Assert.True(open.Flyout.IsOpen);
        Assert.Equal("Maintain altitude", panel.List.Title);
        Assert.Equal("now 3,000 · assigned 5,000", panel.List.Subtitle);
        Assert.Equal(AltitudeFlyout.BoxPlaceholder, panel.Box.PlaceholderText);
        Assert.True(panel.Box.IsKeyboardFocusWithin, "the altitude box does not hold focus at open");
        Assert.Equal(new MenuRichRow("●", "3,000", "now", MenuRichRowKind.Now, "CM 3000"), Row(panel, "3,000"));
        Assert.Equal(new MenuRichRow("◆", "5,000", "assigned", MenuRichRowKind.Assigned, "CM 5000"), Row(panel, "5,000"));
        Assert.Equal(new MenuRichRow("↑", "4,000", "CM 4000", MenuRichRowKind.Climb, "CM 4000"), Row(panel, "4,000"));
        Assert.Equal(new MenuRichRow("↓", "2,600", "DM 2600", MenuRichRowKind.Descend, "DM 2600"), Row(panel, "2,600"));
        Assert.Equal(new MenuRichRow("↓", "2,500", "below MVA", MenuRichRowKind.BelowMva, "DM 2500"), Row(panel, "2,500"));
        AssertMvaLineBetween(panel, "MVA 2,600 here (sector 12)", 2600, 2500);
        Assert.Equal("5,000", panel.List.Rows[panel.List.SelectedIndex].Label);

        Assert.Equal(AltitudeFlyout.ListHeight, panel.Scroll.Bounds.Height, 1);
        Button assignedRow = panel.RowFor(5000);
        Point top = assignedRow.TranslatePoint(default, panel.Scroll) ?? throw new InvalidOperationException("the row is not in the list");
        double middle = top.Y + (assignedRow.Bounds.Height / 2);
        double centre = AltitudeFlyout.ListHeight / 2;
        Assert.InRange(middle, centre - assignedRow.Bounds.Height, centre + assignedRow.Bounds.Height);
    }

    /// <summary>Unassigned, the flyout opens on the ● row, the current altitude's, and the title line names no assignment.</summary>
    [AvaloniaFact]
    public void StripMaintain_Unassigned_OpensOnTheCurrentRow()
    {
        using StripFlyout open = OpenStripMaintain(Aircraft(24960, null), new RecordingMenuHost(""));

        Assert.Equal("now FL250", open.Panel.List.Subtitle);
        Assert.Equal("FL250", open.Panel.List.Rows[open.Panel.List.SelectedIndex].Label);
    }

    // --- Row clicks ---------------------------------------------------------------------------------

    /// <summary>
    /// A row click sends today's command for it and closes the flyout and the menu: a climb row <c>CM</c>, a descent row
    /// <c>DM</c>, and a greyed row under the MVA its command too, the grey its only warning.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(4000, "CM 4000")]
    [InlineData(2600, "DM 2600")]
    [InlineData(2500, "DM 2500")]
    public void RowClick_SendsTheRowsCommand(int altitude, string command)
    {
        var host = new RecordingMenuHost("") { Mva = ("12", 2600) };
        using StripFlyout open = OpenStripMaintain(Aircraft(3000, null), host);

        Click(open.Panel.RowFor(altitude));

        Assert.Equal([(Callsign, command, Initials)], host.Sent);
        Assert.False(open.Menu.IsOpen);
    }

    /// <summary>The MVA line is a rule, not a row: nothing a click or the keys can land on.</summary>
    [AvaloniaFact]
    public void MvaLine_IsNoRow()
    {
        var host = new RecordingMenuHost("") { Mva = ("12", 2600) };
        using StripFlyout open = OpenStripMaintain(Aircraft(3000, null), host);

        Assert.True(open.Panel.MvaLine.IsVisible);
        Assert.IsNotAssignableFrom<Button>(open.Panel.MvaLine);
        Assert.False(open.Panel.MvaLine.Focusable);
        Assert.DoesNotContain(open.Panel.Rows, row => ReferenceEquals(row, open.Panel.MvaLine));
    }

    // --- The box ------------------------------------------------------------------------------------

    /// <summary>
    /// <c>230</c> and <c>23000</c> are both 23,000 ft: the box outlines the 23,000 row and previews today's verb for it, a descent
    /// from FL240; Enter sends it and closes the menu.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("230")]
    [InlineData("23000")]
    [InlineData("FL230")]
    public void Box_OutlinesTheRow_PreviewsTheCommand_EnterSends(string typed)
    {
        var host = new RecordingMenuHost("");
        using StripFlyout open = OpenStripMaintain(Aircraft(24000, null), host);
        List<Button> rowsBefore = [.. open.Panel.Rows];

        open.Panel.Box.Text = typed;
        Layout(open.Flyout);

        Assert.Equal(23000, open.Panel.OutlinedAltitude);
        Assert.Equal("DM 23000", open.Panel.BoxCommand);
        Assert.Equal("DM 23000", open.Panel.BoxCommandText.Text);
        Assert.Equal(rowsBefore, open.Panel.Rows);
        Press(open.Flyout, Key.Enter);

        Assert.Equal([(Callsign, "DM 23000", Initials)], host.Sent);
        Assert.False(open.Menu.IsOpen);
    }

    /// <summary>An altitude between the rows is sent as typed, the nearest row outlined.</summary>
    [AvaloniaFact]
    public void Box_AltitudeBetweenRows_IsSentAsTyped_NearestRowOutlined()
    {
        var host = new RecordingMenuHost("");
        using StripFlyout open = OpenStripMaintain(Aircraft(3000, null), host);

        open.Panel.Box.Text = "123";
        Layout(open.Flyout);

        Assert.Equal(12500, open.Panel.OutlinedAltitude);
        Assert.Equal("CM 12300", open.Panel.BoxCommand);
    }

    /// <summary>Text that is no altitude greys the send, outlines nothing, and Enter sends nothing.</summary>
    [AvaloniaFact]
    public void Box_Unreadable_GreysTheSend_SendsNothing()
    {
        var host = new RecordingMenuHost("");
        using StripFlyout open = OpenStripMaintain(Aircraft(24000, null), host);

        open.Panel.Box.Text = "abc";
        Layout(open.Flyout);

        Assert.Null(open.Panel.BoxCommand);
        Assert.Null(open.Panel.OutlinedAltitude);
        Assert.Equal($"CM {TakeoffFlyout.Unfilled}", open.Panel.BoxCommandText.Text);
        Assert.True(open.Panel.BoxCommandText.Opacity < 0.7);
        Press(open.Flyout, Key.Enter);

        Assert.Empty(host.Sent);
        Assert.True(open.Flyout.IsOpen);
    }

    // --- Keyboard -----------------------------------------------------------------------------------

    /// <summary>
    /// Down from the box focuses the ● row; Down walks the rows, onto the greyed rows too, stepping over the MVA line; Enter sends
    /// the focused row.
    /// </summary>
    [AvaloniaFact]
    public void Keyboard_DownFromTheBox_FocusesTheNowRow_WalksOverTheMvaLine_EnterSends()
    {
        var host = new RecordingMenuHost("") { Mva = ("12", 2600) };
        using StripFlyout open = OpenStripMaintain(Aircraft(2700, null), host);

        Press(open.Flyout, Key.Down);
        Assert.True(open.Panel.RowFor(2700).IsKeyboardFocusWithin, "Down from the box does not focus the ● row");
        Press(open.Flyout, Key.Down);
        Assert.True(open.Panel.RowFor(2600).IsKeyboardFocusWithin, "Down does not reach the next row");
        Press(open.Flyout, Key.Down);
        Assert.True(open.Panel.RowFor(2500).IsKeyboardFocusWithin, "Down does not step over the MVA line onto the greyed row");
        Assert.Empty(host.Sent);

        Press(open.Flyout, Key.Enter);

        Assert.Equal([(Callsign, "DM 2500", Initials)], host.Sent);
        Assert.False(open.Menu.IsOpen);
    }

    /// <summary>Up from the top row returns to the box; Up from a row walks back up the list.</summary>
    [AvaloniaFact]
    public void Keyboard_UpFromTheTopRow_ReturnsToTheBox()
    {
        using StripFlyout open = OpenStripMaintain(Aircraft(3000, null), new RecordingMenuHost(""));
        open.Panel.Rows[1].Focus(NavigationMethod.Directional);
        Layout(open.Flyout);

        Press(open.Flyout, Key.Up);
        Assert.True(open.Panel.Rows[0].IsKeyboardFocusWithin, "Up does not reach the row above");
        Press(open.Flyout, Key.Up);

        Assert.True(open.Panel.Box.IsKeyboardFocusWithin, "Up from the top row does not return to the box");
    }

    /// <summary>Escape closes the flyout back to the strip button and leaves the menu open, sending nothing.</summary>
    [AvaloniaFact]
    public void Keyboard_Escape_ClosesTheFlyout_MenuStaysOpen()
    {
        var host = new RecordingMenuHost("");
        using StripFlyout open = OpenStripMaintain(Aircraft(3000, null), host);

        Press(open.Flyout, Key.Escape);

        Assert.False(open.Flyout.IsOpen);
        Assert.True(open.Menu.IsOpen);
        Assert.Empty(host.Sent);
    }

    // --- Live ---------------------------------------------------------------------------------------

    /// <summary>
    /// Each second the marks and the verbs follow the aircraft: climbing from 3,000 to 3,600 moves the ● mark to 3,600 and turns
    /// the rows it passed from climbs into descents, on the row controls and in the box's preview, while no row moves and the box
    /// keeps its text and focus.
    /// </summary>
    [AvaloniaFact]
    public void OneSecondRefresh_MovesTheNowMark_UpdatesTheVerbs_RowsStay_BoxKept()
    {
        var host = new RecordingMenuHost("");
        var aircraft = new AircraftModel { Callsign = Callsign, Altitude = 3000 };
        using StripFlyout open = OpenStripMaintain(aircraft, host);
        MaintainAltitudePanel panel = open.Panel;
        List<Button> rowsBefore = [.. panel.Rows];
        List<int> altitudesBefore = [.. panel.Altitudes];
        panel.Box.Text = "34";
        Assert.Equal("CM 3400", panel.BoxCommandText.Text);
        Assert.Equal("CM 3300", MenuCommandText.GetCommand(panel.RowFor(3300)));

        aircraft.Altitude = 3600;
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("now 3,600", panel.List.Subtitle);
        Assert.Equal(MenuRichRowKind.Now, Row(panel, "3,600").Kind);
        Assert.Equal(MenuRichRowKind.Descend, Row(panel, "3,000").Kind);
        Assert.Equal("DM 3300", MenuCommandText.GetCommand(panel.RowFor(3300)));
        Assert.Equal("3,300 — DM 3300", AutomationProperties.GetName(panel.RowFor(3300)));
        Assert.Equal("3,600 · now — CM 3600", AutomationProperties.GetName(panel.RowFor(3600)));
        Assert.Equal("DM 3400", panel.BoxCommandText.Text);
        Assert.Equal(rowsBefore, panel.Rows);
        Assert.Equal(altitudesBefore, panel.Altitudes);
        Assert.Equal("34", panel.Box.Text);
        Assert.True(panel.Box.IsKeyboardFocusWithin, "the refresh took focus from the box");
        Assert.Empty(host.Sent);
    }

    /// <summary>
    /// The MVA line follows the sector each second: from 2,600 to 3,000 it moves between 3,000 and 2,900 with the same row
    /// controls in place, and once the MVA is gone it hides. Removed above the shown rows, it leaves them where they were on
    /// screen. A click then sends the row's command as it reads now.
    /// </summary>
    [AvaloniaFact]
    public void OneSecondRefresh_MovesTheMvaLine_RowsStayOnScreen()
    {
        var host = new RecordingMenuHost("") { Mva = ("12", 2600) };
        using StripFlyout open = OpenStripMaintain(Aircraft(3000, null), host);
        MaintainAltitudePanel panel = open.Panel;
        List<Button> rowsBefore = [.. panel.Rows];
        AssertMvaLineBetween(panel, "MVA 2,600 here (sector 12)", 2600, 2500);

        host.Mva = ("13", 3000);
        Tick(host, open.Flyout);

        AssertMvaLineBetween(panel, "MVA 3,000 here (sector 13)", 3000, 2900);
        Assert.Equal(rowsBefore, panel.Rows);
        Assert.Equal(MenuRichRowKind.BelowMva, Row(panel, "2,900").Kind);

        panel.Scroll.Offset = new Vector(0, panel.RowFor(2000).Bounds.Y);
        Layout(open.Flyout);
        double shownAt = OnScreen(panel, 2000);
        host.Mva = null;
        Tick(host, open.Flyout);

        Assert.False(panel.MvaLine.IsVisible);
        Assert.DoesNotContain(panel.List.Rows, row => row.Kind is MenuRichRowKind.MvaLine or MenuRichRowKind.BelowMva);
        Assert.Equal(shownAt, OnScreen(panel, 2000), 0.5);
        Assert.Equal(rowsBefore, panel.Rows);

        Click(panel.RowFor(2900));

        Assert.Equal([(Callsign, "DM 2900", Initials)], host.Sent);
    }

    /// <summary>How a test closes an open altitude flyout.</summary>
    public enum CloseBy
    {
        HidingTheStripFlyout,
        ClosingTheMenuOverTheStrip,
        ClosingTheAllCommandsSubmenu,
        ClosingTheMenuOverAllCommands,
    }

    /// <summary>Closing any of the three flyouts, on the strip or in All Commands, lets go of its tick's timer, and it refreshes no more.</summary>
    [AvaloniaTheory]
    [InlineData(MenuIds.AltitudeMaintain, CloseBy.HidingTheStripFlyout)]
    [InlineData(MenuIds.AltitudeMaintain, CloseBy.ClosingTheMenuOverTheStrip)]
    [InlineData(MenuIds.AltitudeMaintain, CloseBy.ClosingTheAllCommandsSubmenu)]
    [InlineData(MenuIds.AltitudeMaintain, CloseBy.ClosingTheMenuOverAllCommands)]
    [InlineData(MenuIds.DataBlockTempAltitude, CloseBy.HidingTheStripFlyout)]
    [InlineData(MenuIds.DataBlockTempAltitude, CloseBy.ClosingTheMenuOverTheStrip)]
    [InlineData(MenuIds.DataBlockTempAltitude, CloseBy.ClosingTheAllCommandsSubmenu)]
    [InlineData(MenuIds.DataBlockTempAltitude, CloseBy.ClosingTheMenuOverAllCommands)]
    [InlineData(MenuIds.DataBlockCruise, CloseBy.HidingTheStripFlyout)]
    [InlineData(MenuIds.DataBlockCruise, CloseBy.ClosingTheMenuOverTheStrip)]
    [InlineData(MenuIds.DataBlockCruise, CloseBy.ClosingTheAllCommandsSubmenu)]
    [InlineData(MenuIds.DataBlockCruise, CloseBy.ClosingTheMenuOverAllCommands)]
    public void Closing_StopsTheTick(string id, CloseBy close)
    {
        var host = new RecordingMenuHost("");
        var aircraft = new AircraftModel { Callsign = Callsign, Altitude = 3000 };
        int timersBeforeOpen = host.Clock.LiveTimerCount;
        bool onTheStrip = close is CloseBy.HidingTheStripFlyout or CloseBy.ClosingTheMenuOverTheStrip;
        using OpenedFlyout open = onTheStrip ? OpenStrip(id, aircraft, host) : OpenAllCommands(id, aircraft, host);
        Assert.Equal(timersBeforeOpen + 1, host.Clock.LiveTimerCount);
        switch (close)
        {
            case CloseBy.HidingTheStripFlyout:
                Assert.IsType<MenuFlyout>(open.Flyout).Hide();
                break;
            case CloseBy.ClosingTheAllCommandsSubmenu:
                open.Entry.IsSubMenuOpen = false;
                break;
            default:
                open.Menu.Close();
                break;
        }

        Dispatcher.UIThread.RunJobs();
        Assert.Equal(timersBeforeOpen, host.Clock.LiveTimerCount);
        string shown = TitleOf(open.Holder);
        aircraft.Altitude = 9000;
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(shown, TitleOf(open.Holder));
    }

    // --- Temporary altitude and Cruise ----------------------------------------------------------------

    /// <summary>The one-box flyouts send their command in hundreds, as the input popups did, and preview it as typed.</summary>
    [AvaloniaTheory]
    [InlineData(MenuIds.DataBlockTempAltitude, "120", "TEMPALT 120")]
    [InlineData(MenuIds.DataBlockTempAltitude, "12000", "TEMPALT 120")]
    [InlineData(MenuIds.DataBlockCruise, "240", "CRUISE 240")]
    [InlineData(MenuIds.DataBlockCruise, "FL240", "CRUISE 240")]
    public void AltitudeBox_PreviewsAndSendsTheCommand(string id, string typed, string command)
    {
        var host = new RecordingMenuHost("");
        using OpenedFlyout open = OpenAllCommands(id, Aircraft(24000, null), host);
        AltitudeBoxPanel panel = Assert.IsType<AltitudeBoxPanel>(open.Holder.Child);
        Assert.True(panel.Box.IsKeyboardFocusWithin, "the box does not hold focus at open");

        panel.Box.Text = typed;
        Assert.Equal(command, panel.BoxCommand);
        Assert.Equal(command, panel.BoxCommandText.Text);
        PressOn(panel.Box, Key.Enter);

        Assert.Equal([(Callsign, command, Initials)], host.Sent);
        Assert.False(open.Menu.IsOpen);
    }

    /// <summary>The title line names the box's entry and the aircraft's present altitude.</summary>
    [AvaloniaTheory]
    [InlineData(MenuIds.DataBlockTempAltitude, "Temporary altitude · now FL240")]
    [InlineData(MenuIds.DataBlockCruise, "Cruise · now FL240")]
    public void AltitudeBox_TitleNamesTheEntryAndThePresentAltitude(string id, string title)
    {
        using OpenedFlyout open = OpenAllCommands(id, Aircraft(24000, null), new RecordingMenuHost(""));

        Assert.Equal(title, Assert.IsType<AltitudeBoxPanel>(open.Holder.Child).Title);
    }

    /// <summary>An unreadable altitude shows today's refusal in the command's place; Enter sends nothing and the flyout stays open.</summary>
    [AvaloniaTheory]
    [InlineData(MenuIds.DataBlockTempAltitude, "abc")]
    [InlineData(MenuIds.DataBlockCruise, "FL1200")]
    [InlineData(MenuIds.DataBlockCruise, "100000")]
    public void AltitudeBox_Unreadable_ShowsTheRefusal_SendsNothing(string id, string typed)
    {
        var host = new RecordingMenuHost("");
        using OpenedFlyout open = OpenAllCommands(id, Aircraft(24000, null), host);
        AltitudeBoxPanel panel = Assert.IsType<AltitudeBoxPanel>(open.Holder.Child);

        panel.Box.Text = typed;
        Assert.Null(panel.BoxCommand);
        Assert.Equal(AltitudeFlyout.InvalidAltitude, panel.BoxCommandText.Text);
        PressOn(panel.Box, Key.Enter);

        Assert.Empty(host.Sent);
        Assert.True(open.Menu.IsOpen);
        Assert.Equal(typed, panel.Box.Text);
    }

    /// <summary>Enter in the empty box closes the menu and sends nothing, as the input popup's blank submit did.</summary>
    [AvaloniaFact]
    public void AltitudeBox_Blank_EnterClosesSendingNothing()
    {
        var host = new RecordingMenuHost("");
        using OpenedFlyout open = OpenAllCommands(MenuIds.DataBlockTempAltitude, Aircraft(24000, null), host);
        AltitudeBoxPanel panel = Assert.IsType<AltitudeBoxPanel>(open.Holder.Child);
        Assert.Equal($"TEMPALT {TakeoffFlyout.Unfilled}", panel.BoxCommandText.Text);

        PressOn(panel.Box, Key.Enter);

        Assert.Empty(host.Sent);
        Assert.False(open.Menu.IsOpen);
    }

    // --- All Commands -------------------------------------------------------------------------------

    /// <summary>All Commands' three entries keep their labels and open the same flyouts, built when they first open.</summary>
    [AvaloniaTheory]
    [InlineData(MenuIds.AltitudeMaintain, "Maintain altitude")]
    [InlineData(MenuIds.DataBlockTempAltitude, "Temporary altitude…")]
    [InlineData(MenuIds.DataBlockCruise, "Cruise…")]
    public void AllCommandsEntries_KeepTheirLabels_AndOpenTheFlyouts(string id, string label)
    {
        MenuItem entry = Assert.IsType<MenuItem>(MenuCatalog.Get(id).Build(Aircraft(24000, null), Context(), new RecordingMenuHost("")));

        Assert.Equal(label, entry.Header as string);
        Assert.Null(entry.Tag);
        Assert.Null(HolderOf(entry.Items).Child);
    }

    /// <summary>All Commands' Maintain altitude opens the Maintain flyout with focus in its box, and a row click sends.</summary>
    [AvaloniaFact]
    public void AllCommandsMaintain_OpensTheMaintainFlyout_RowClickSends()
    {
        var host = new RecordingMenuHost("");
        using OpenedFlyout open = OpenAllCommands(MenuIds.AltitudeMaintain, Aircraft(3000, null), host);
        MaintainAltitudePanel panel = Assert.IsType<MaintainAltitudePanel>(open.Holder.Child);

        Assert.True(panel.Box.IsKeyboardFocusWithin, "the altitude box does not hold focus at open");
        Click(panel.RowFor(5000));

        Assert.Equal([(Callsign, "CM 5000", Initials)], host.Sent);
        Assert.False(open.Menu.IsOpen);
    }

    // --- Helpers ------------------------------------------------------------------------------------

    /// <summary>
    /// An altitude flyout opened over a shown window: its menu, the strip's flyout when opened from the strip (null in All
    /// Commands), the catalog entry's item and the holder of its panel. Disposing closes the window.
    /// </summary>
    private sealed record OpenedFlyout(Window Window, ContextMenu Menu, MenuFlyout? Flyout, MenuItem Entry, AltitudeFlyoutHost Holder) : IDisposable
    {
        /// <summary>Closes the menu first (a window closed under an open submenu fails to detach it), then the window.</summary>
        public void Dispose()
        {
            Menu.Close();
            Dispatcher.UIThread.RunJobs();
            Window.Close();
        }
    }

    /// <summary>The Maintain flyout on the strip and its panel. Disposing closes the window.</summary>
    private sealed record StripFlyout(OpenedFlyout Opened, MaintainAltitudePanel Panel) : IDisposable
    {
        public ContextMenu Menu => Opened.Menu;

        public MenuFlyout Flyout => Opened.Flyout ?? throw new InvalidOperationException("the flyout was not opened from the strip");

        public void Dispose() => Opened.Dispose();
    }

    private static MenuRichRow Row(MaintainAltitudePanel panel, string label) => Assert.Single(panel.List.Rows, row => row.Label == label);

    /// <summary>
    /// The MVA line reads <paramref name="label"/> and sits between the rows of <paramref name="above"/> and
    /// <paramref name="below"/>, in the list and on screen.
    /// </summary>
    private static void AssertMvaLineBetween(MaintainAltitudePanel panel, string label, int above, int below)
    {
        int line = Assert.Single(Enumerable.Range(0, panel.List.Rows.Count), i => panel.List.Rows[i].Kind == MenuRichRowKind.MvaLine);
        Assert.Equal(label, panel.List.Rows[line].Label);
        Assert.Equal(MenuCatalog.FormatAltitude(above), panel.List.Rows[line - 1].Label);
        Assert.Equal(MenuCatalog.FormatAltitude(below), panel.List.Rows[line + 1].Label);
        Assert.True(panel.MvaLine.IsVisible);
        Assert.InRange(panel.MvaLine.Bounds.Y, panel.RowFor(above).Bounds.Bottom - 0.5, panel.RowFor(below).Bounds.Y + 0.5);
    }

    /// <summary>Where the row of <paramref name="altitude"/> sits within the list's viewport.</summary>
    private static double OnScreen(MaintainAltitudePanel panel, int altitude) =>
        (panel.RowFor(altitude).TranslatePoint(default, panel.Scroll) ?? throw new InvalidOperationException("the row is not in the list")).Y;

    private static StripFlyout OpenStripMaintain(IMenuAircraft aircraft, RecordingMenuHost host)
    {
        OpenedFlyout open = OpenStrip(MenuIds.AltitudeMaintain, aircraft, host);
        return new StripFlyout(open, Assert.IsType<MaintainAltitudePanel>(open.Holder.Child));
    }

    /// <summary>An altitude entry's flyout on a strip button (Maintain's glyph stands in for the data-block entries, which have none).</summary>
    private static OpenedFlyout OpenStrip(string id, IMenuAircraft aircraft, RecordingMenuHost host)
    {
        MenuCatalogEntry entry = MenuCatalog.Get(id);
        MenuItem built = entry.Build(aircraft, Context(), host) ?? throw new InvalidOperationException($"no {id} item");
        QuickCommandGlyph glyph = QuickCommandGlyphs.For(MenuIds.AltitudeMaintain) ?? throw new InvalidOperationException("no Maintain glyph");
        var menu = new ContextMenu();
        MenuItem? strip = QuickCommandStrip.FromBuilt(menu, [(new QuickCommandStripItem(entry, glyph), built)]);
        Assert.NotNull(strip);
        menu.Items.Add(strip);
        var window = new Window { Width = 900, Height = 900 };
        try
        {
            window.Show();
            menu.Open(window);
            Dispatcher.UIThread.RunJobs();
            menu.UpdateLayout();
            Button button = Assert.Single(QuickCommandStrip.Buttons(strip));
            QuickCommandStrip.OpenSubmenu(button);
            MenuFlyout flyout = Assert.IsType<MenuFlyout>(FlyoutBase.GetAttachedFlyout(button));
            Layout(flyout);
            return new OpenedFlyout(window, menu, flyout, built, HolderOf(flyout.Items));
        }
        catch
        {
            menu.Close();
            window.Close();
            throw;
        }
    }

    private static OpenedFlyout OpenAllCommands(string id, IMenuAircraft aircraft, RecordingMenuHost host)
    {
        MenuItem entry = Assert.IsType<MenuItem>(MenuCatalog.Get(id).Build(aircraft, Context(), host));
        var menu = new ContextMenu { Items = { entry } };
        var window = new Window { Width = 900, Height = 900 };
        try
        {
            window.Show();
            menu.Open(window);
            Dispatcher.UIThread.RunJobs();
            entry.IsSubMenuOpen = true;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AltitudeFlyoutHost holder = HolderOf(entry.Items);
            Control panel = Assert.IsAssignableFrom<Control>(holder.Child);
            (TopLevel.GetTopLevel(panel) as Control)?.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            return new OpenedFlyout(window, menu, null, entry, holder);
        }
        catch
        {
            menu.Close();
            window.Close();
            throw;
        }
    }

    private static AltitudeFlyoutHost HolderOf(ItemCollection items) =>
        Assert.IsType<AltitudeFlyoutHost>(Assert.Single(items.OfType<MenuItem>()).Header);

    /// <summary>The flyout's title line as text.</summary>
    private static string TitleOf(AltitudeFlyoutHost holder) =>
        holder.Child switch
        {
            MaintainAltitudePanel maintain => $"{maintain.List.Title} · {maintain.List.Subtitle}",
            AltitudeBoxPanel box => box.Title,
            _ => throw new InvalidOperationException($"unexpected panel {holder.Child?.GetType().Name ?? "none"}"),
        };

    private static void Click(Button row) => row.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>Advances the host's clock one beat and lays the flyout out again.</summary>
    private static void Tick(RecordingMenuHost host, MenuFlyout flyout)
    {
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        Dispatcher.UIThread.RunJobs();
        Layout(flyout);
    }

    private static void Layout(MenuFlyout flyout)
    {
        (flyout.Popup.Child as Control)?.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        (flyout.Popup.Child as Control)?.UpdateLayout();
    }

    /// <summary>Presses <paramref name="key"/> on whatever holds focus in the flyout.</summary>
    private static void Press(MenuFlyout flyout, Key key)
    {
        IInputElement? focused = TopLevel.GetTopLevel(Assert.IsAssignableFrom<Visual>(flyout.Popup.Child))?.FocusManager?.GetFocusedElement();
        PressOn(Assert.IsAssignableFrom<Control>(focused), key);
        Layout(flyout);
    }

    private static void PressOn(Control control, Key key)
    {
        control.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
        Dispatcher.UIThread.RunJobs();
    }
}
