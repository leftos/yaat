using Avalonia;
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
/// The Fly heading flyout (<see cref="HeadingFlyout"/>), one builder for the strip's Fly heading and All Commands' five heading
/// entries: the Present heading row on top, the Fly heading / Turn left / Turn right toggle, the heading box, then the compass
/// rose between the left and right degree chips, and the readout. A rose pick or the box sends <c>FH</c>, <c>TL</c> or
/// <c>TR</c> with a three-digit heading by the toggle; the chips send <c>LT</c> / <c>RT</c>; the flyout follows the aircraft's
/// heading once a second and leaves the pick alone.
/// </summary>
public class HeadingFlyoutTests
{
    private const string Callsign = "UAL455";
    private const string Initials = "AB";

    private static MenuContext Context() => TestMenuContext.Create(Callsign, Initials, null, false, VfrCommandsForIfr.None);

    private static FakeMenuAircraft Aircraft(double heading) =>
        new()
        {
            Callsign = Callsign,
            HeadingDegrees = heading,
            AltitudeFeet = 24000,
            IndicatedAirspeedKnots = 280,
            FiledAircraftType = "A320",
            BaseAircraftType = "A320",
        };

    // --- Commands the sim reads ------------------------------------------------------------------

    /// <summary>The three-digit headings the flyout sends parse as the heading they name, <c>FH 360</c> as north.</summary>
    [Theory]
    [InlineData("FH 005", 5)]
    [InlineData("FH 360", 360)]
    [InlineData("TL 300", 300)]
    [InlineData("TR 090", 90)]
    public void ThreeDigitHeading_ParsesAsTheHeadingItNames(string text, int heading)
    {
        TestVnasData.EnsureInitialized();

        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);

        Assert.True(parsed.IsSuccess, parsed.Reason);
        ParsedCommand command = Assert.Single(Assert.Single(parsed.Value!.Blocks).Commands);
        MagneticHeading parsedHeading = command switch
        {
            FlyHeadingCommand fly => fly.MagneticHeading,
            TurnLeftCommand left => left.MagneticHeading,
            TurnRightCommand right => right.MagneticHeading,
            _ => throw new InvalidOperationException($"{text} parsed as {command.GetType().Name}"),
        };
        // MagneticHeading keeps north as 0.
        Assert.Equal(heading % 360, parsedHeading.Degrees, 6);
    }

    // --- The rose's angle-to-heading mapping -----------------------------------------------------

    /// <summary>A point on the rose, by its bearing from north (clockwise) at 100 px from the centre, screen y down.</summary>
    private static (double Dx, double Dy) At(double bearing) => (100 * Math.Sin(bearing * Math.PI / 180), -100 * Math.Cos(bearing * Math.PI / 180));

    [Theory]
    [InlineData(0, 360)]
    [InlineData(90, 90)]
    [InlineData(180, 180)]
    [InlineData(270, 270)]
    [InlineData(2.4, 360)]
    [InlineData(2.6, 5)]
    [InlineData(357.6, 360)]
    [InlineData(357.4, 355)]
    [InlineData(92.4, 90)]
    [InlineData(92.6, 95)]
    [InlineData(87.6, 90)]
    [InlineData(87.4, 85)]
    public void HeadingAt_SnapsToFive_NorthUp_Clockwise(double bearing, int heading)
    {
        (double dx, double dy) = At(bearing);

        Assert.Equal(heading, HeadingFlyout.HeadingAt(dx, dy));
    }

    [Fact]
    public void HeadingAt_NearTheCentre_IsNone() => Assert.Null(HeadingFlyout.HeadingAt(5, -10));

    [Theory]
    [InlineData(92.0, 90)]
    [InlineData(1.0, 360)]
    [InlineData(358.0, 360)]
    [InlineData(272.6, 275)]
    public void RoundedToFive_LandsOnARoseStep_NorthAs360(double heading, int rounded) => Assert.Equal(rounded, HeadingFlyout.RoundedToFive(heading));

    // --- The strip's Fly heading -----------------------------------------------------------------

    /// <summary>
    /// The strip's Fly heading opens the flyout, no popup: the title, the Present heading row on top, the toggle on Fly heading,
    /// the empty box, the rose between both chip groups and the readout's hint.
    /// </summary>
    [AvaloniaFact]
    public void StripFlyHeading_OpensTheFlyout_PresentHeadingOnTop()
    {
        var host = new RecordingMenuHost("");
        StripFlyout open = OpenStripFlyout(MenuIds.HeadingFly, Aircraft(270), host);

        Assert.True(open.Flyout.IsOpen);
        Assert.Empty(host.ListPopups);
        Assert.Equal(
            [
                "Fly heading · now 270",
                "Present heading · 270 now — FPH",
                "[Fly heading FH] | Turn left TL | Turn right TR",
                "Heading — FH ···",
                "---",
                "← Left by 5° 10° 15° 20° 30° 45° 60° 90°",
                "rose · now 270",
                "Right by → 5° 10° 15° 20° 30° 45° 60° 90°",
                HeadingFlyout.ReadoutHint,
            ],
            open.Panel.Lines
        );
        Assert.Equal(HeadingVerb.FlyHeading, open.Panel.Verb);
        Assert.True(open.Panel.Box.IsKeyboardFocusWithin, "the heading box does not hold focus at open");
    }

    [AvaloniaFact]
    public void PresentHeadingRow_SendsFph()
    {
        var host = new RecordingMenuHost("");
        StripFlyout open = OpenStripFlyout(MenuIds.HeadingFly, Aircraft(270), host);

        open.Panel.PresentRow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal([(Callsign, "FPH", Initials)], host.Sent);
        Assert.False(open.Menu.IsOpen);
    }

    // --- Rose picks ------------------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(HeadingVerb.FlyHeading, 300, "FH 300")]
    [InlineData(HeadingVerb.TurnLeft, 300, "TL 300")]
    [InlineData(HeadingVerb.TurnRight, 300, "TR 300")]
    [InlineData(HeadingVerb.FlyHeading, 5, "FH 005")]
    [InlineData(HeadingVerb.FlyHeading, 360, "FH 360")]
    public void RoseClick_SendsTheToggledVerbWithAThreeDigitHeading(HeadingVerb verb, int heading, string command)
    {
        var host = new RecordingMenuHost("");
        StripFlyout open = OpenStripFlyout(MenuIds.HeadingFly, Aircraft(270), host);
        Click(open.Panel.Toggle(verb));
        Assert.Equal(verb, open.Panel.Verb);

        ClickRose(open.Panel.Rose, heading);

        Assert.Equal([(Callsign, command, Initials)], host.Sent);
        Assert.False(open.Menu.IsOpen);
    }

    /// <summary>Pointing previews the heading under the pointer, its turn and command, and sends nothing.</summary>
    [AvaloniaFact]
    public void RosePointing_PreviewsTheHeadingAndCommand_SendsNothing()
    {
        var host = new RecordingMenuHost("");
        StripFlyout open = OpenStripFlyout(MenuIds.HeadingFly, Aircraft(270), host);

        TopLevel top = TopLevel.GetTopLevel(open.Panel.Rose)!;
        top.MouseMove(OnScreen(open.Panel.Rose, 300));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("300 · a right turn of 30° · FH 300", open.Panel.Readout.Text);
        Assert.Empty(host.Sent);
    }

    /// <summary>A drag released more than 30 px outside the rim sends nothing.</summary>
    [AvaloniaFact]
    public void RoseDrag_ReleasedFarOutsideTheRim_SendsNothing()
    {
        var host = new RecordingMenuHost("");
        StripFlyout open = OpenStripFlyout(MenuIds.HeadingFly, Aircraft(270), host);
        HeadingRose rose = open.Panel.Rose;
        TopLevel top = TopLevel.GetTopLevel(rose)!;

        top.MouseDown(OnScreen(rose, 300), MouseButton.Left);
        Point far = rose.TranslatePoint(new Point(HeadingRose.Centre, HeadingRose.Centre - HeadingRose.RimRadius - 40), top)!.Value;
        top.MouseMove(far, RawInputModifiers.LeftMouseButton);
        top.MouseUp(far, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(host.Sent);
        Assert.True(open.Flyout.IsOpen);
    }

    /// <summary>Pressing at 300 and dragging to 330 with the button held moves the pick; the release inside the rim sends it.</summary>
    [AvaloniaFact]
    public void RoseDrag_ReleasedInsideTheRim_SendsTheHeadingDraggedTo()
    {
        var host = new RecordingMenuHost("");
        StripFlyout open = OpenStripFlyout(MenuIds.HeadingFly, Aircraft(270), host);
        HeadingRose rose = open.Panel.Rose;
        TopLevel top = TopLevel.GetTopLevel(rose)!;

        top.MouseMove(OnScreen(rose, 300));
        top.MouseDown(OnScreen(rose, 300), MouseButton.Left);
        top.MouseMove(OnScreen(rose, 330), RawInputModifiers.LeftMouseButton);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(330, rose.Pick);
        Assert.Empty(host.Sent);
        top.MouseUp(OnScreen(rose, 330), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([(Callsign, "FH 330", Initials)], host.Sent);
    }

    /// <summary>
    /// A flyout closed while the pointer was on the rose reopens with nothing pointed at or picked. The panel is shown in a
    /// plain window, outside any popup, so closing raises no pointer exit that would clear the pointing on its own.
    /// </summary>
    [AvaloniaFact]
    public void Reopen_AfterPointing_ShowsNoPickAndTheHint()
    {
        var host = new RecordingMenuHost("");
        MenuItem entry = Assert.IsType<MenuItem>(MenuCatalog.Get(MenuIds.HeadingFly).Build(Aircraft(270), Context(), host));
        entry.IsSubMenuOpen = true;
        HeadingFlyoutPanel panel = PanelOf(entry);
        Assert.IsType<HeadingFlyoutHost>(Assert.Single(entry.Items.OfType<MenuItem>()).Header).Child = null;
        var window = new Window
        {
            Width = 900,
            Height = 900,
            Content = panel,
        };
        window.Show();
        window.UpdateLayout();
        window.MouseMove(OnScreen(panel.Rose, 300));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(300, panel.Rose.Shown);

        entry.IsSubMenuOpen = false;
        entry.IsSubMenuOpen = true;
        Dispatcher.UIThread.RunJobs();

        Assert.Null(panel.Rose.Shown);
        Assert.Null(panel.Rose.Pick);
        Assert.Equal(HeadingFlyout.ReadoutHint, panel.Readout.Text);
        Assert.Empty(host.Sent);
    }

    // --- The box ---------------------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData("250", "FH 250")]
    [InlineData("l180", "TL 180")]
    [InlineData("R090", "TR 090")]
    [InlineData("7", "FH 007")]
    [InlineData("abc", null)]
    [InlineData("400", null)]
    [InlineData("0", null)]
    public void Box_SendsWhatItShows_AndGreysTheSendOtherwise(string typed, string? command)
    {
        var host = new RecordingMenuHost("");
        StripFlyout open = OpenStripFlyout(MenuIds.HeadingFly, Aircraft(270), host);

        open.Panel.Box.Text = typed;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(command, open.Panel.BoxCommand);
        Assert.Equal(command ?? $"FH {TakeoffFlyout.Unfilled}", open.Panel.BoxCommandText.Text);
        Assert.Equal(command is null, open.Panel.BoxCommandText.Opacity < 0.7);
        open.Panel.Box.Focus();
        Press(open.Flyout, Key.Enter, KeyModifiers.None);
        if (command is null)
        {
            Assert.Empty(host.Sent);
            Assert.True(open.Flyout.IsOpen);
        }
        else
        {
            Assert.Equal([(Callsign, command, Initials)], host.Sent);
            Assert.False(open.Menu.IsOpen);
        }
    }

    // --- Chips -----------------------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(true, 20, "LT 20")]
    [InlineData(false, 90, "RT 90")]
    [InlineData(false, 5, "RT 5")]
    public void DegreeChip_SendsTheRelativeTurn(bool left, int degrees, string command)
    {
        var host = new RecordingMenuHost("");
        StripFlyout open = OpenStripFlyout(MenuIds.HeadingFly, Aircraft(270), host);
        IReadOnlyList<Button> chips = left ? open.Panel.LeftChips : open.Panel.RightChips;

        chips[HeadingFlyout.DegreeTurns.ToList().IndexOf(degrees)].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal([(Callsign, command, Initials)], host.Sent);
    }

    // --- All Commands ----------------------------------------------------------------------------

    /// <summary>All Commands' Turn left opens the same flyout, its toggle on Turn left, so the box and the rose send TL.</summary>
    [AvaloniaFact]
    public void AllCommandsTurnLeft_OpensTheFlyoutPresetToTurnLeft()
    {
        var host = new RecordingMenuHost("");
        HeadingFlyoutPanel panel = OpenAllCommands(MenuIds.HeadingTurnLeft, Aircraft(270), host).Panel;

        Assert.Equal(HeadingVerb.TurnLeft, panel.Verb);
        Assert.Equal("Turn left · now 270", panel.Lines[0]);
        Assert.Equal("Fly heading FH | [Turn left TL] | Turn right TR", panel.Lines[2]);
        panel.Box.Text = "250";
        Assert.Equal("TL 250", panel.BoxCommand);
    }

    [AvaloniaTheory]
    [InlineData(MenuIds.HeadingFly, "Fly heading", HeadingVerb.FlyHeading)]
    [InlineData(MenuIds.HeadingTurnLeft, "Turn left", HeadingVerb.TurnLeft)]
    [InlineData(MenuIds.HeadingTurnRight, "Turn right", HeadingVerb.TurnRight)]
    [InlineData(MenuIds.HeadingTurnLeftDegrees, "Turn left (degrees)", HeadingVerb.FlyHeading)]
    [InlineData(MenuIds.HeadingTurnRightDegrees, "Turn right (degrees)", HeadingVerb.FlyHeading)]
    public void EveryHeadingEntry_KeepsItsLabel_AndOpensTheFlyoutPreset(string id, string label, HeadingVerb verb)
    {
        MenuItem entry = Assert.IsType<MenuItem>(MenuCatalog.Get(id).Build(Aircraft(270), Context(), new RecordingMenuHost("")));

        Assert.Equal(label, entry.Header as string);
        Assert.Null(entry.Tag);
        HeadingFlyoutHost holder = Assert.IsType<HeadingFlyoutHost>(Assert.Single(entry.Items.OfType<MenuItem>()).Header);
        Assert.Null(holder.Child);
        entry.IsSubMenuOpen = true;
        Assert.Equal(verb, PanelOf(entry).Verb);
    }

    /// <summary>All Commands' Turn right (degrees) opens the flyout on Fly heading with focus on the right chips' 30°.</summary>
    [AvaloniaFact]
    public void AllCommandsTurnRightDegrees_FocusesTheRightChips()
    {
        var host = new RecordingMenuHost("");
        AllCommandsOpen open = OpenAllCommands(MenuIds.HeadingTurnRightDegrees, Aircraft(270), host);

        Assert.Equal(HeadingVerb.FlyHeading, open.Panel.Verb);
        Button chip = open.Panel.RightChips[HeadingFlyout.DegreeTurns.ToList().IndexOf(30)];
        Assert.True(chip.IsKeyboardFocusWithin, "the right chips' 30° does not hold focus at open");
    }

    // --- Keyboard --------------------------------------------------------------------------------

    /// <summary>
    /// From the box, Down focuses the rose at the present heading rounded to 5; Right steps 5° and Enter sends it; Shift+Right
    /// steps 30°.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(KeyModifiers.None, "FH 275")]
    [InlineData(KeyModifiers.Shift, "FH 300")]
    public void Keyboard_DownFromTheBox_FocusesTheRoseAtThePresentHeading_ArrowsStep_EnterSends(KeyModifiers modifiers, string command)
    {
        var host = new RecordingMenuHost("");
        StripFlyout open = OpenStripFlyout(MenuIds.HeadingFly, Aircraft(271), host);

        Press(open.Flyout, Key.Down, KeyModifiers.None);

        Assert.True(open.Panel.Rose.IsKeyboardFocusWithin, "Down from the box does not focus the rose");
        Assert.Equal(270, open.Panel.Rose.Pick);

        Press(open.Flyout, Key.Right, modifiers);
        Assert.Empty(host.Sent);
        Press(open.Flyout, Key.Enter, KeyModifiers.None);

        Assert.Equal([(Callsign, command, Initials)], host.Sent);
        Assert.False(open.Menu.IsOpen);
    }

    /// <summary>Tab from the box focuses the rose and Tab from the rose moves on to the chips.</summary>
    [AvaloniaFact]
    public void Keyboard_TabFromTheBoxReachesTheRose_ThenTheChips()
    {
        StripFlyout open = OpenStripFlyout(MenuIds.HeadingFly, Aircraft(270), new RecordingMenuHost(""));

        Press(open.Flyout, Key.Tab, KeyModifiers.None);
        Assert.True(open.Panel.Rose.IsKeyboardFocusWithin, "Tab from the box does not focus the rose");

        Press(open.Flyout, Key.Tab, KeyModifiers.None);
        Assert.True(open.Panel.LeftChips[0].IsKeyboardFocusWithin, "Tab from the rose does not reach the chips");
    }

    // --- Live ------------------------------------------------------------------------------------

    /// <summary>Each second the needle and the title follow the aircraft's heading; the pick stays where it was.</summary>
    [AvaloniaFact]
    public void OneSecondRefresh_MovesTheNeedle_AndLeavesThePickAlone()
    {
        var host = new RecordingMenuHost("");
        var aircraft = new AircraftModel { Callsign = Callsign, Heading = new TrueHeading(270) };
        StripFlyout open = OpenStripFlyout(MenuIds.HeadingFly, aircraft, host);
        Press(open.Flyout, Key.Down, KeyModifiers.None);
        int? pick = open.Panel.Rose.Pick;
        double before = ((IMenuAircraft)aircraft).MagneticHeadingDegrees;
        Assert.Equal(before, open.Panel.Rose.PresentHeading, 6);

        aircraft.Heading = new TrueHeading(300);
        double after = ((IMenuAircraft)aircraft).MagneticHeadingDegrees;
        Assert.Equal(before, open.Panel.Rose.PresentHeading, 6);
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(after, open.Panel.Rose.PresentHeading, 6);
        Assert.Equal($"Fly heading · now {HeadingFlyout.PresentHeading(aircraft):D3}", open.Panel.Lines[0]);
        Assert.Equal(pick, open.Panel.Rose.Pick);
        Assert.Empty(host.Sent);
    }

    /// <summary>How a test closes an open heading flyout.</summary>
    public enum CloseBy
    {
        HidingTheStripFlyout,
        ClosingTheMenu,
        ClosingTheAllCommandsSubmenu,
    }

    /// <summary>Closing the flyout, on the strip or in All Commands, lets go of the tick's timer and moves the needle no more.</summary>
    [AvaloniaTheory]
    [InlineData(CloseBy.HidingTheStripFlyout)]
    [InlineData(CloseBy.ClosingTheMenu)]
    [InlineData(CloseBy.ClosingTheAllCommandsSubmenu)]
    public void Closing_StopsTheTick(CloseBy close)
    {
        var host = new RecordingMenuHost("");
        var aircraft = new AircraftModel { Callsign = Callsign, Heading = new TrueHeading(270) };
        int timersBeforeOpen = host.Clock.LiveTimerCount;
        HeadingFlyoutPanel panel;
        if (close == CloseBy.ClosingTheAllCommandsSubmenu)
        {
            AllCommandsOpen open = OpenAllCommands(MenuIds.HeadingFly, aircraft, host);
            panel = open.Panel;
            Assert.Equal(timersBeforeOpen + 1, host.Clock.LiveTimerCount);
            open.Entry.IsSubMenuOpen = false;
        }
        else
        {
            StripFlyout open = OpenStripFlyout(MenuIds.HeadingFly, aircraft, host);
            panel = open.Panel;
            Assert.Equal(timersBeforeOpen + 1, host.Clock.LiveTimerCount);
            if (close == CloseBy.ClosingTheMenu)
            {
                open.Menu.Close();
            }
            else
            {
                open.Flyout.Hide();
            }
        }

        Dispatcher.UIThread.RunJobs();
        Assert.Equal(timersBeforeOpen, host.Clock.LiveTimerCount);
        double shown = panel.Rose.PresentHeading;
        aircraft.Heading = new TrueHeading(300);
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(shown, panel.Rose.PresentHeading, 6);
    }

    // --- Helpers ---------------------------------------------------------------------------------

    /// <summary>A heading flyout entry's flyout on the strip, opened over a shown window, and its panel.</summary>
    private sealed record StripFlyout(Window Window, ContextMenu Menu, MenuFlyout Flyout, HeadingFlyoutPanel Panel, Button Button);

    /// <summary>A heading entry as All Commands shows it: a submenu of an open context menu, and its panel.</summary>
    private sealed record AllCommandsOpen(Window Window, ContextMenu Menu, MenuItem Entry, HeadingFlyoutPanel Panel);

    private static StripFlyout OpenStripFlyout(string id, IMenuAircraft aircraft, RecordingMenuHost host)
    {
        MenuCatalogEntry entry = MenuCatalog.Get(id);
        MenuItem built = entry.Build(aircraft, Context(), host) ?? throw new InvalidOperationException($"no {id} item");
        var menu = new ContextMenu();
        MenuItem? strip = QuickCommandStrip.FromBuilt(menu, [(new QuickCommandStripItem(entry, QuickCommandGlyphs.For(MenuIds.HeadingFly)!), built)]);
        Assert.NotNull(strip);
        menu.Items.Add(strip);
        var window = new Window { Width = 900, Height = 900 };
        window.Show();
        menu.Open(window);
        Dispatcher.UIThread.RunJobs();
        menu.UpdateLayout();
        Button button = Assert.Single(QuickCommandStrip.Buttons(strip));
        QuickCommandStrip.OpenSubmenu(button);
        MenuFlyout flyout = Assert.IsType<MenuFlyout>(FlyoutBase.GetAttachedFlyout(button));
        Layout(flyout);
        return new StripFlyout(window, menu, flyout, PanelOf(flyout.Items), button);
    }

    /// <summary>The panel an opened heading entry's one menu row holds.</summary>
    internal static HeadingFlyoutPanel PanelOf(MenuItem entry) => PanelOf(entry.Items);

    private static HeadingFlyoutPanel PanelOf(ItemCollection items) =>
        Assert.IsType<HeadingFlyoutPanel>(Assert.IsType<HeadingFlyoutHost>(Assert.Single(items.OfType<MenuItem>()).Header).Child);

    private static AllCommandsOpen OpenAllCommands(string id, IMenuAircraft aircraft, RecordingMenuHost host)
    {
        MenuItem entry = Assert.IsType<MenuItem>(MenuCatalog.Get(id).Build(aircraft, Context(), host));
        var menu = new ContextMenu { Items = { entry } };
        var window = new Window { Width = 900, Height = 900 };
        window.Show();
        menu.Open(window);
        Dispatcher.UIThread.RunJobs();
        entry.IsSubMenuOpen = true;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        HeadingFlyoutPanel panel = PanelOf(entry);
        (TopLevel.GetTopLevel(panel) as Control)?.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return new AllCommandsOpen(window, menu, entry, panel);
    }

    private static void Layout(MenuFlyout flyout)
    {
        (flyout.Popup.Child as Control)?.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        (flyout.Popup.Child as Control)?.UpdateLayout();
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>Presses <paramref name="key"/> on whatever holds focus in the flyout.</summary>
    private static void Press(MenuFlyout flyout, Key key, KeyModifiers modifiers)
    {
        IInputElement? focused = TopLevel.GetTopLevel(Assert.IsAssignableFrom<Visual>(flyout.Popup.Child))?.FocusManager?.GetFocusedElement();
        Control control = Assert.IsAssignableFrom<Control>(focused);
        control.RaiseEvent(
            new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = key,
                KeyModifiers = modifiers,
            }
        );
        Layout(flyout);
    }

    /// <summary>Where <paramref name="heading"/> lies on <paramref name="rose"/>, 100 px from its centre, in its top level.</summary>
    private static Point OnScreen(HeadingRose rose, int heading)
    {
        TopLevel top = TopLevel.GetTopLevel(rose) ?? throw new InvalidOperationException("the rose is in no top level");
        double radians = heading * Math.PI / 180;
        var local = new Point(HeadingRose.Centre + (100 * Math.Sin(radians)), HeadingRose.Centre - (100 * Math.Cos(radians)));
        return rose.TranslatePoint(local, top) ?? throw new InvalidOperationException("no position");
    }

    /// <summary>Presses and releases the left button on <paramref name="rose"/> at <paramref name="heading"/>.</summary>
    private static void ClickRose(HeadingRose rose, int heading)
    {
        TopLevel top = TopLevel.GetTopLevel(rose)!;
        Point at = OnScreen(rose, heading);
        top.MouseMove(at);
        top.MouseDown(at, MouseButton.Left);
        top.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }
}
