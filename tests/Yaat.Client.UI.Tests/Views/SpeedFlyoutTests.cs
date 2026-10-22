using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Testing;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Assign speed flyout (<see cref="SpeedFlyout"/>), one builder for the strip's Assign speed and All Commands' Assign speed and
/// Speed…: the title line (the present speed, and the present Mach where the Mach chips show), the Speed box with focus on open
/// over a readout of the command it or the pointed-at chip sends, the Resume normal speed and Final approach speed rows, and the
/// knots chips eight to a row, with the Mach chips under them at or above FL240. The present speed's chip is filled, the assigned
/// speed's outlined; each refreshes once a second while open, and no chip moves.
/// </summary>
public class SpeedFlyoutTests
{
    private const string Callsign = "UAL455";
    private const string Initials = "AB";

    /// <summary>Real aircraft data, so each type's profile and category resolve as they do in the app.</summary>
    public SpeedFlyoutTests() => TestVnasData.EnsureInitialized();

    private static MenuContext Context() => TestMenuContext.Create(Callsign, Initials, null, false, VfrCommandsForIfr.None);

    private static FakeMenuAircraft Aircraft(string type, double altitude, double ias, double? assigned) =>
        new()
        {
            Callsign = Callsign,
            FiledAircraftType = type,
            AltitudeFeet = altitude,
            IndicatedAirspeedKnots = ias,
            AssignedSpeed = assigned,
        };

    private static FakeMenuAircraft Jet() => Aircraft("B738", 35_000, 250, null);

    private static string PresentMach(double ias, double altitude) =>
        $"M.{(int)Math.Round(WindInterpolator.IasToMach(ias, altitude) * 100, MidpointRounding.AwayFromZero):00}";

    // --- Opening ------------------------------------------------------------------------------------

    /// <summary>
    /// The strip's Assign speed opens the flyout, no popup: the title line, the Speed box holding focus, the two rows, and the knots
    /// chips holding today's values in order (B738 at FL350: 150 through 270).
    /// </summary>
    [AvaloniaFact]
    public void StripAssignSpeed_OpensTheFlyout_TitleBoxRowsAndKnotsChips()
    {
        var host = new RecordingMenuHost("");
        using OpenedFlyout open = OpenStrip(MenuIds.SpeedAssign, Jet(), host);
        SpeedFlyoutPanel panel = open.Panel;

        Assert.NotNull(open.Flyout);
        Assert.True(open.Flyout.IsOpen);
        Assert.Empty(host.InputPlaceholders);
        Assert.Equal($"Assign speed · now 250 kt · {PresentMach(250, 35_000)}", panel.Title);
        Assert.Equal(SpeedFlyout.BoxPlaceholder, panel.Box.PlaceholderText);
        Assert.True(panel.Box.IsKeyboardFocusWithin, "the speed box does not hold focus at open");
        Assert.Equal("Resume normal speed — RNS", AutomationProperties.GetName(panel.ResumeRow));
        Assert.Equal("Final approach speed · 144 kt — RFAS", AutomationProperties.GetName(panel.FinalApproachRow));
        Assert.Equal("144 kt", panel.FinalApproachNote.Text);
        Assert.Equal(Tens(150, 270), panel.KnotChips.Select(chip => chip.Content as string));
        Assert.True(panel.MachGroup.IsVisible);
        Assert.Equal(["M.74", "M.75", "M.76", "M.77", "M.78", "M.79", "M.80", "M.81"], panel.MachChips.Select(chip => chip.Content as string));
        Assert.True(panel.Bounds.Width <= MenuCommandRow.RowMaxWidth, $"the panel is {panel.Bounds.Width} px wide");
    }

    /// <summary>A piston at 3,500 ft gets its knots chips and no Mach group.</summary>
    [AvaloniaFact]
    public void Piston_At3500_HasNoMachGroup()
    {
        using OpenedFlyout open = OpenStrip(MenuIds.SpeedAssign, Aircraft("C172", 3_500, 100, null), new RecordingMenuHost(""));

        Assert.Equal(Tens(70, 130), open.Panel.KnotChips.Select(chip => chip.Content as string));
        Assert.Empty(open.Panel.MachChips);
        Assert.False(open.Panel.MachGroup.IsVisible);
        Assert.Equal("Assign speed · now 100 kt", open.Panel.Title);
    }

    // --- Sending ------------------------------------------------------------------------------------

    /// <summary>Each row and chip sends its command and closes the flyout and the menu.</summary>
    [AvaloniaTheory]
    [InlineData("RNS")]
    [InlineData("RFAS")]
    [InlineData("SPD 250")]
    [InlineData("MACH .78")]
    public void RowsAndChips_SendTheirCommand(string command)
    {
        var host = new RecordingMenuHost("");
        using OpenedFlyout open = OpenStrip(MenuIds.SpeedAssign, Jet(), host);
        SpeedFlyoutPanel panel = open.Panel;
        Button target = command switch
        {
            "RNS" => panel.ResumeRow,
            "RFAS" => panel.FinalApproachRow,
            "SPD 250" => panel.KnotChip(250),
            _ => panel.MachChip("M.78"),
        };

        Click(target);

        Assert.Equal([(Callsign, command, Initials)], host.Sent);
        Assert.False(open.Menu.IsOpen);
    }

    // --- The box ------------------------------------------------------------------------------------

    /// <summary><c>250</c> previews <c>SPD 250</c>, outlines the 250 chip, and Enter sends it.</summary>
    [AvaloniaFact]
    public void Box_PreviewsOutlinesAndSends()
    {
        var host = new RecordingMenuHost("");
        using OpenedFlyout open = OpenStrip(MenuIds.SpeedAssign, Jet(), host);
        SpeedFlyoutPanel panel = open.Panel;

        panel.Box.Text = "250";
        Layout(open);

        Assert.Equal("SPD 250", panel.BoxCommand);
        Assert.Equal("SPD 250", panel.Readout.Text);
        Assert.Equal(250, panel.OutlinedKnots);
        Assert.Contains(SpeedFlyout.TypedClass, panel.KnotChip(250).Classes);
        Assert.DoesNotContain(SpeedFlyout.TypedClass, panel.KnotChip(240).Classes);
        Press(open, Key.Enter);

        Assert.Equal([(Callsign, "SPD 250", Initials)], host.Sent);
        Assert.False(open.Menu.IsOpen);
    }

    /// <summary>Text that is no speed shows the refusal in red in the readout, and Enter sends nothing.</summary>
    [AvaloniaFact]
    public void Box_NotASpeed_ShowsTheRefusal_SendsNothing()
    {
        var host = new RecordingMenuHost("");
        using OpenedFlyout open = OpenStrip(MenuIds.SpeedAssign, Jet(), host);
        SpeedFlyoutPanel panel = open.Panel;

        panel.Box.Text = "abc";
        Layout(open);

        Assert.Null(panel.BoxCommand);
        Assert.Null(panel.OutlinedKnots);
        Assert.Equal(SpeedFlyout.InvalidSpeed, panel.Readout.Text);
        Press(open, Key.Enter);

        Assert.Empty(host.Sent);
        Assert.True(open.Menu.IsOpen);
        Assert.Equal("abc", panel.Box.Text);
    }

    /// <summary>Enter in the empty box closes the menu and sends nothing, as Speed…'s blank submit did.</summary>
    [AvaloniaFact]
    public void Box_Empty_EnterClosesTheMenu()
    {
        var host = new RecordingMenuHost("");
        using OpenedFlyout open = OpenAllCommands(MenuIds.SpeedCustom, Jet(), host);

        Assert.Equal($"SPD {TakeoffFlyout.Unfilled}", open.Panel.Readout.Text);
        PressOn(open.Panel.Box, Key.Enter);

        Assert.Empty(host.Sent);
        Assert.False(open.Menu.IsOpen);
    }

    // --- The readout --------------------------------------------------------------------------------

    /// <summary>
    /// Focusing a chip shows its command in the readout, and so does pointing at one; the box's command comes back when neither
    /// holds. Every chip's tooltip and automation name carry its command.
    /// </summary>
    [AvaloniaFact]
    public void Readout_ShowsTheFocusedOrPointedAtChipsCommand()
    {
        using OpenedFlyout open = OpenStrip(MenuIds.SpeedAssign, Jet(), new RecordingMenuHost(""));
        SpeedFlyoutPanel panel = open.Panel;
        panel.Box.Text = "230";
        Assert.Equal("SPD 230", panel.Readout.Text);

        panel.KnotChip(180).Focus(NavigationMethod.Directional);
        Layout(open);
        Assert.Equal("SPD 180", panel.Readout.Text);

        panel.Box.Focus(NavigationMethod.Directional);
        Layout(open);
        Assert.Equal("SPD 230", panel.Readout.Text);

        TopLevel top = TopLevel.GetTopLevel(panel) ?? throw new InvalidOperationException("the panel is in no top level");
        top.MouseMove(Centre(panel.MachChip("M.78")));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("MACH .78", panel.Readout.Text);

        top.MouseMove(Centre(panel.ResumeRow));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("RNS", panel.Readout.Text);

        Assert.Equal("SPD 180", ToolTip.GetTip(panel.KnotChip(180)));
        Assert.Equal("180 — SPD 180", AutomationProperties.GetName(panel.KnotChip(180)));
        Assert.Equal("MACH .78", ToolTip.GetTip(panel.MachChip("M.78")));
        Assert.Equal("M.78 — MACH .78", AutomationProperties.GetName(panel.MachChip("M.78")));
    }

    // --- All Commands -------------------------------------------------------------------------------

    /// <summary>All Commands' Assign speed and Speed… keep their labels and open the same flyout, built when it first opens.</summary>
    [AvaloniaTheory]
    [InlineData(MenuIds.SpeedAssign, "Assign speed")]
    [InlineData(MenuIds.SpeedCustom, "Speed…")]
    public void AllCommandsEntries_KeepTheirLabels_AndOpenTheFlyout(string id, string label)
    {
        MenuItem entry = Assert.IsType<MenuItem>(MenuCatalog.Get(id).Build(Jet(), Context(), new RecordingMenuHost("")));

        Assert.Equal(label, entry.Header as string);
        Assert.Null(entry.Tag);
        Assert.Null(HolderOf(entry.Items).Child);
    }

    /// <summary>Speed… and All Commands' Assign speed open the flyout with focus in the box, and a chip click sends.</summary>
    [AvaloniaTheory]
    [InlineData(MenuIds.SpeedCustom)]
    [InlineData(MenuIds.SpeedAssign)]
    public void AllCommands_OpensTheFlyout_FocusInTheBox_ChipSends(string id)
    {
        var host = new RecordingMenuHost("");
        using OpenedFlyout open = OpenAllCommands(id, Jet(), host);

        Assert.True(open.Panel.Box.IsKeyboardFocusWithin, "the speed box does not hold focus at open");
        Assert.Equal(Tens(150, 270), open.Panel.KnotChips.Select(chip => chip.Content as string));
        Click(open.Panel.KnotChip(210));

        Assert.Equal([(Callsign, "SPD 210", Initials)], host.Sent);
        Assert.False(open.Menu.IsOpen);
    }

    // --- Marks --------------------------------------------------------------------------------------

    /// <summary>
    /// The present speed's chip (IAS to the nearest 10) is filled, the present Mach's chip too, and the assigned speed's chip is
    /// outlined; no other chip is marked.
    /// </summary>
    [AvaloniaFact]
    public void Marks_PresentChipFilled_AssignedChipOutlined()
    {
        using OpenedFlyout open = OpenStrip(MenuIds.SpeedAssign, Aircraft("B738", 35_000, 265, 213), new RecordingMenuHost(""));
        SpeedFlyoutPanel panel = open.Panel;

        Assert.Equal([panel.KnotChip(270)], panel.KnotChips.Where(chip => chip.Classes.Contains(SpeedFlyout.NowClass)));
        Assert.Equal([panel.KnotChip(210)], panel.KnotChips.Where(chip => chip.Classes.Contains(SpeedFlyout.AssignedClass)));
        Assert.Equal([panel.MachChip(PresentMach(265, 35_000))], panel.MachChips.Where(chip => chip.Classes.Contains(SpeedFlyout.NowClass)));
        Assert.DoesNotContain(panel.MachChips, chip => chip.Classes.Contains(SpeedFlyout.AssignedClass));
    }

    // --- Keyboard -----------------------------------------------------------------------------------

    /// <summary>
    /// Down from the box reaches Resume normal speed, then Final approach speed, then the present speed's chip; Left and Right step
    /// one chip, Up and Down one row of eight; Up from the top knots row returns to Final approach speed; Down from the last knots
    /// row enters the Mach grid; Up from the rows returns to the box; Enter sends the focused chip.
    /// </summary>
    [AvaloniaFact]
    public void Keyboard_WalksTheRowsAndTheGrids_EnterSends()
    {
        var host = new RecordingMenuHost("");
        using OpenedFlyout open = OpenStrip(MenuIds.SpeedAssign, Jet(), host);
        SpeedFlyoutPanel panel = open.Panel;

        Press(open, Key.Down);
        AssertFocused(panel.ResumeRow, "Down from the box does not reach Resume normal speed");
        Press(open, Key.Down);
        AssertFocused(panel.FinalApproachRow, "Down does not reach Final approach speed");
        Press(open, Key.Down);
        AssertFocused(panel.KnotChip(250), "Down from Final approach speed does not land on the present speed's chip");
        Press(open, Key.Left);
        AssertFocused(panel.KnotChip(240), "Left does not step back one chip");
        Press(open, Key.Right);
        AssertFocused(panel.KnotChip(250), "Right does not step on one chip");
        Press(open, Key.Up);
        AssertFocused(panel.KnotChip(170), "Up does not step one row of eight");
        Press(open, Key.Up);
        AssertFocused(panel.FinalApproachRow, "Up from the top knots row does not return to Final approach speed");
        Press(open, Key.Down);
        Press(open, Key.Down);
        AssertFocused(panel.MachChip("M.76"), "Down from the last knots row does not enter the Mach grid");
        Press(open, Key.Up);
        AssertFocused(panel.KnotChip(250), "Up from the Mach grid does not return to the knots grid");
        Assert.Empty(host.Sent);

        Press(open, Key.Enter);

        Assert.Equal([(Callsign, "SPD 250", Initials)], host.Sent);
        Assert.False(open.Menu.IsOpen);
    }

    /// <summary>Up from Final approach speed reaches Resume normal speed, and Up from it returns to the box.</summary>
    [AvaloniaFact]
    public void Keyboard_UpFromTheRows_ReturnsToTheBox()
    {
        using OpenedFlyout open = OpenStrip(MenuIds.SpeedAssign, Jet(), new RecordingMenuHost(""));
        open.Panel.FinalApproachRow.Focus(NavigationMethod.Directional);
        Layout(open);

        Press(open, Key.Up);
        AssertFocused(open.Panel.ResumeRow, "Up from Final approach speed does not reach Resume normal speed");
        Press(open, Key.Up);

        AssertFocused(open.Panel.Box, "Up from Resume normal speed does not return to the box");
    }

    /// <summary>Enter on a focused row sends it.</summary>
    [AvaloniaFact]
    public void Keyboard_EnterOnARow_SendsIt()
    {
        var host = new RecordingMenuHost("");
        using OpenedFlyout open = OpenStrip(MenuIds.SpeedAssign, Jet(), host);
        Press(open, Key.Down);

        Press(open, Key.Enter);

        Assert.Equal([(Callsign, "RNS", Initials)], host.Sent);
    }

    /// <summary>Down from a piston's last knots row, with no Mach group under it, keeps focus on the chip and is not the menu's.</summary>
    [AvaloniaFact]
    public void Keyboard_DownFromTheLastKnotsRow_WithNoMach_KeepsFocus()
    {
        using OpenedFlyout open = OpenStrip(MenuIds.SpeedAssign, Aircraft("C172", 3_500, 100, null), new RecordingMenuHost(""));
        Button last = open.Panel.KnotChip(130);
        last.Focus(NavigationMethod.Directional);
        Layout(open);

        KeyEventArgs down = PressHandled(last, Key.Down);

        Assert.True(down.Handled, "Down off the last row leaks to the menu");
        AssertFocused(last, "Down from the last knots row moved focus");
        Assert.True(Assert.IsType<MenuFlyout>(open.Flyout).IsOpen);
    }

    /// <summary>Left on the Resume normal speed row in All Commands neither closes the submenu nor moves focus.</summary>
    [AvaloniaFact]
    public void Keyboard_LeftOnARow_InAllCommands_KeepsTheSubmenuAndFocus()
    {
        using OpenedFlyout open = OpenAllCommands(MenuIds.SpeedAssign, Jet(), new RecordingMenuHost(""));
        open.Panel.ResumeRow.Focus(NavigationMethod.Directional);
        Layout(open);

        KeyEventArgs left = PressHandled(open.Panel.ResumeRow, Key.Left);
        Layout(open);

        Assert.True(left.Handled, "Left on the row leaks to the menu");
        Assert.True(open.Entry.IsSubMenuOpen, "Left on the row closed the All Commands submenu");
        AssertFocused(open.Panel.ResumeRow, "Left on the row moved focus");
    }

    /// <summary>Escape closes the flyout back to the strip button and leaves the menu open, sending nothing.</summary>
    [AvaloniaFact]
    public void Keyboard_Escape_ClosesTheFlyout_MenuStaysOpen()
    {
        var host = new RecordingMenuHost("");
        using OpenedFlyout open = OpenStrip(MenuIds.SpeedAssign, Jet(), host);

        Press(open, Key.Escape);

        Assert.False(Assert.IsType<MenuFlyout>(open.Flyout).IsOpen);
        Assert.True(open.Menu.IsOpen);
        Assert.Empty(host.Sent);
    }

    // --- Live ---------------------------------------------------------------------------------------

    /// <summary>
    /// Each second the title and the marks follow the aircraft: speeding up from 250 to 270 kt moves the filled chip to 270, while
    /// no chip moves, the box keeps its text and focus, and climbing through FL240 adds no Mach group.
    /// </summary>
    [AvaloniaFact]
    public void OneSecondRefresh_MovesTheFilledChip_ChipsStay_BoxKept_NoMachAdded()
    {
        var host = new RecordingMenuHost("");
        var aircraft = new AircraftModel
        {
            Callsign = Callsign,
            FiledAircraftType = "B738",
            Altitude = 23_000,
            IndicatedAirspeed = 250,
        };
        using OpenedFlyout open = OpenStrip(MenuIds.SpeedAssign, aircraft, host);
        SpeedFlyoutPanel panel = open.Panel;
        List<Button> chipsBefore = [.. panel.KnotChips];
        Assert.Empty(panel.MachChips);
        panel.Box.Text = "26";
        Assert.Contains(SpeedFlyout.NowClass, panel.KnotChip(250).Classes);

        aircraft.IndicatedAirspeed = 270;
        aircraft.Altitude = 25_000;
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Assign speed · now 270 kt", panel.Title);
        Assert.Contains(SpeedFlyout.NowClass, panel.KnotChip(270).Classes);
        Assert.DoesNotContain(SpeedFlyout.NowClass, panel.KnotChip(250).Classes);
        Assert.Equal(chipsBefore, panel.KnotChips);
        Assert.Empty(panel.MachChips);
        Assert.False(panel.MachGroup.IsVisible);
        Assert.Equal("26", panel.Box.Text);
        Assert.True(panel.Box.IsKeyboardFocusWithin, "the refresh took focus from the box");
        Assert.Empty(host.Sent);
    }

    /// <summary>A typed value naming a chip keeps that chip outlined through a refresh.</summary>
    [AvaloniaFact]
    public void OneSecondRefresh_KeepsTheTypedOutline()
    {
        var host = new RecordingMenuHost("");
        using OpenedFlyout open = OpenStrip(MenuIds.SpeedAssign, Jet(), host);
        open.Panel.Box.Text = "250";
        Assert.Contains(SpeedFlyout.TypedClass, open.Panel.KnotChip(250).Classes);

        host.Clock.Advance(TimeSpan.FromSeconds(1));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(250, open.Panel.OutlinedKnots);
        Assert.Contains(SpeedFlyout.TypedClass, open.Panel.KnotChip(250).Classes);
        Assert.Equal("250", open.Panel.Box.Text);
    }

    /// <summary>A new assignment while the flyout is open moves the assigned outline on the next refresh.</summary>
    [AvaloniaFact]
    public void OneSecondRefresh_MovesTheAssignedOutline()
    {
        var host = new RecordingMenuHost("");
        var aircraft = new AircraftModel
        {
            Callsign = Callsign,
            FiledAircraftType = "B738",
            Altitude = 35_000,
            IndicatedAirspeed = 250,
            AssignedSpeed = 210,
        };
        using OpenedFlyout open = OpenStrip(MenuIds.SpeedAssign, aircraft, host);
        Assert.Equal([open.Panel.KnotChip(210)], open.Panel.KnotChips.Where(chip => chip.Classes.Contains(SpeedFlyout.AssignedClass)));

        aircraft.AssignedSpeed = 240;
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([open.Panel.KnotChip(240)], open.Panel.KnotChips.Where(chip => chip.Classes.Contains(SpeedFlyout.AssignedClass)));
    }

    /// <summary>
    /// Reopening the flyout clears the box and forgets the chip that was pointed at, so the readout shows the empty box; the pointer leaves
    /// the chip before the reopen, so the render frames that follow it hit empty window rather than the chip again.
    /// </summary>
    [AvaloniaFact]
    public void Reopen_ClearsTheBoxAndThePointedChip()
    {
        using OpenedFlyout open = OpenAllCommands(MenuIds.SpeedAssign, Jet(), new RecordingMenuHost(""));
        SpeedFlyoutPanel panel = open.Panel;
        panel.Box.Text = "230";
        TopLevel top = TopLevel.GetTopLevel(panel) ?? throw new InvalidOperationException("the panel is in no top level");
        top.MouseMove(Centre(panel.KnotChip(180)));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("SPD 180", panel.Readout.Text);

        open.Entry.IsSubMenuOpen = false;
        Dispatcher.UIThread.RunJobs();
        top.MouseMove(new Point(top.Bounds.Width - 2, top.Bounds.Height - 2));
        Dispatcher.UIThread.RunJobs();
        open.Entry.IsSubMenuOpen = true;
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(panel, Assert.IsType<SpeedFlyoutHost>(Assert.Single(open.Entry.Items.OfType<MenuItem>()).Header).Child);
        Assert.Equal("", panel.Box.Text);
        Assert.Equal($"SPD {TakeoffFlyout.Unfilled}", panel.Readout.Text);
    }

    /// <summary>How a test closes an open speed flyout.</summary>
    public enum CloseBy
    {
        HidingTheStripFlyout,
        ClosingTheMenuOverTheStrip,
        ClosingTheAllCommandsSubmenu,
        ClosingTheMenuOverAllCommands,
    }

    /// <summary>Closing the flyout, on the strip or in All Commands, lets go of its tick's timer, and it refreshes no more.</summary>
    [AvaloniaTheory]
    [InlineData(CloseBy.HidingTheStripFlyout)]
    [InlineData(CloseBy.ClosingTheMenuOverTheStrip)]
    [InlineData(CloseBy.ClosingTheAllCommandsSubmenu)]
    [InlineData(CloseBy.ClosingTheMenuOverAllCommands)]
    public void Closing_StopsTheTick(CloseBy close)
    {
        var host = new RecordingMenuHost("");
        var aircraft = new AircraftModel
        {
            Callsign = Callsign,
            FiledAircraftType = "B738",
            Altitude = 5_000,
            IndicatedAirspeed = 210,
        };
        int timersBeforeOpen = host.Clock.LiveTimerCount;
        bool onTheStrip = close is CloseBy.HidingTheStripFlyout or CloseBy.ClosingTheMenuOverTheStrip;
        using OpenedFlyout open = onTheStrip ? OpenStrip(MenuIds.SpeedAssign, aircraft, host) : OpenAllCommands(MenuIds.SpeedAssign, aircraft, host);
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
        string shown = open.Panel.Title;
        aircraft.IndicatedAirspeed = 240;
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(shown, open.Panel.Title);
    }

    // --- Helpers ------------------------------------------------------------------------------------

    /// <summary>
    /// The speed flyout opened over a shown window: its menu, the strip's flyout when opened from the strip (null in All Commands),
    /// the catalog entry's item and the panel. Disposing closes the window.
    /// </summary>
    private sealed record OpenedFlyout(Window Window, ContextMenu Menu, MenuFlyout? Flyout, MenuItem Entry, SpeedFlyoutPanel Panel) : IDisposable
    {
        /// <summary>Closes the menu first (a window closed under an open submenu fails to detach it), then the window.</summary>
        public void Dispose()
        {
            Menu.Close();
            Dispatcher.UIThread.RunJobs();
            Window.Close();
        }
    }

    private static string[] Tens(int first, int last) => [.. Enumerable.Range(first / 10, ((last - first) / 10) + 1).Select(tens => $"{tens * 10}")];

    private static OpenedFlyout OpenStrip(string id, IMenuAircraft aircraft, RecordingMenuHost host)
    {
        MenuCatalogEntry entry = MenuCatalog.Get(id);
        MenuItem built = entry.Build(aircraft, Context(), host) ?? throw new InvalidOperationException($"no {id} item");
        QuickCommandGlyph glyph = QuickCommandGlyphs.For(MenuIds.SpeedAssign) ?? throw new InvalidOperationException("no Assign speed glyph");
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
            LayoutFlyout(flyout);
            SpeedFlyoutPanel panel = Assert.IsType<SpeedFlyoutPanel>(HolderOf(flyout.Items).Child);
            return new OpenedFlyout(window, menu, flyout, built, panel);
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
            SpeedFlyoutPanel panel = Assert.IsType<SpeedFlyoutPanel>(HolderOf(entry.Items).Child);
            (TopLevel.GetTopLevel(panel) as Control)?.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            return new OpenedFlyout(window, menu, null, entry, panel);
        }
        catch
        {
            menu.Close();
            window.Close();
            throw;
        }
    }

    private static SpeedFlyoutHost HolderOf(ItemCollection items) => Assert.IsType<SpeedFlyoutHost>(Assert.Single(items.OfType<MenuItem>()).Header);

    private static void AssertFocused(Control control, string message) => Assert.True(control.IsKeyboardFocusWithin, message);

    private static Point Centre(Control control)
    {
        TopLevel top = TopLevel.GetTopLevel(control) ?? throw new InvalidOperationException("the control is in no top level");
        return control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top)
            ?? throw new InvalidOperationException("no position");
    }

    private static void Click(Button target) => target.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void Layout(OpenedFlyout open)
    {
        if (open.Flyout is { } flyout)
        {
            LayoutFlyout(flyout);
        }
        else
        {
            (TopLevel.GetTopLevel(open.Panel) as Control)?.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void LayoutFlyout(MenuFlyout flyout)
    {
        (flyout.Popup.Child as Control)?.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        (flyout.Popup.Child as Control)?.UpdateLayout();
    }

    /// <summary>Presses <paramref name="key"/> on whatever holds focus in the panel.</summary>
    private static void Press(OpenedFlyout open, Key key)
    {
        IInputElement? focused = TopLevel.GetTopLevel(open.Panel)?.FocusManager?.GetFocusedElement();
        PressOn(Assert.IsAssignableFrom<Control>(focused), key);
        Layout(open);
    }

    /// <summary>Presses <paramref name="key"/> on <paramref name="control"/> and returns the event, to read whether it was handled.</summary>
    private static KeyEventArgs PressHandled(Control control, Key key)
    {
        var args = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key };
        control.RaiseEvent(args);
        Dispatcher.UIThread.RunJobs();
        return args;
    }

    private static void PressOn(Control control, Key key)
    {
        control.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
        Dispatcher.UIThread.RunJobs();
    }
}
