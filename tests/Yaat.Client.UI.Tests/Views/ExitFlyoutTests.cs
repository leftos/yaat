using Avalonia;
using Avalonia.Automation;
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
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Situation;
using Yaat.Sim.Testing;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Exit left / Exit right flyouts in the quick-command strip and in All Commands. On the rollout (lane A): a title saying
/// the distances are from the aircraft, one row per exit on the side (glyph at the exit's turn-off angle, <c>Taxiway G</c>,
/// a planned note, the bare distance, <c>EL G</c>), a separator and Pilot's choice. On final (lane B): Pilot's choice, the
/// rows with every distance "from thr", then the "Exit at" box, whose typing lists the runway's matching exits, Enter taking
/// the top match and sending what was typed when nothing matches. The strip's flyout refills once a second: with the pointer
/// inside the order is frozen and an exit the list drops shows "passed", greyed and disabled; it catches up when the pointer
/// leaves. Long rows stay one line, the note cut with an ellipsis and the full text in the tooltip.
/// </summary>
public class ExitFlyoutTests
{
    private const string Callsign = "N12KJ";
    private const string Initials = "AB";

    // --- Lanes -----------------------------------------------------------------------------

    [AvaloniaFact]
    public void Rollout_LaneA_TitleRowsSeparatorThenPilotsChoice()
    {
        AircraftModel aircraft = RollingOut([Exit("G", ExitSide.Left, 600, false, 90), Exit("J", ExitSide.Left, 2800, true, 39)]);
        MenuFlyout flyout = OpenStripFlyout(HostFor(), aircraft, MenuIds.TowerExitLeft);

        Assert.Equal(
            [
                "[Exits ahead, left side · from the aircraft]",
                "Taxiway G · ~600 ft — EL G",
                "Taxiway J · planned · ~2,800 ft — EL J",
                "---",
                "Pilot's choice — EL",
            ],
            Rows(flyout.Items)
        );
        Assert.Null(BoxItem(flyout.Items));
    }

    [AvaloniaFact]
    public void Final_LaneB_PilotsChoiceFirst_RowsThenTheBox()
    {
        AircraftModel aircraft = OnFinal([Exit("G", ExitSide.Right, 2700, false, 53), Exit("P", ExitSide.Right, 4600, true, 53)]);
        MenuFlyout flyout = OpenStripFlyout(HostFor(), aircraft, MenuIds.TowerExitRight);

        Assert.Equal(
            [
                "Pilot's choice — ER",
                "---",
                "Taxiway G · ~2,700 ft from thr — ER G",
                "Taxiway P · planned · ~4,600 ft from thr — ER P",
                "---",
                "Exit at — ER ···",
            ],
            Rows(flyout.Items)
        );
    }

    /// <summary>Exit left on final mirrors Exit right: Pilot's choice first, rows from the threshold, a box sending <c>EL</c>.</summary>
    [AvaloniaFact]
    public void Final_ExitLeft_MirrorsLaneB_WithAnElBox()
    {
        AircraftModel aircraft = OnFinal([Exit("H", ExitSide.Left, 3100, false, 90)]);
        MenuFlyout flyout = OpenStripFlyout(HostFor(), aircraft, MenuIds.TowerExitLeft);

        Assert.Equal(["Pilot's choice — EL", "---", "Taxiway H · ~3,100 ft from thr — EL H", "---", "Exit at — EL ···"], Rows(flyout.Items));
    }

    [AvaloniaFact]
    public void FromThr_OnFinalOnly()
    {
        ExitAheadDto[] exits = [Exit("G", ExitSide.Right, 2700, false, 53)];
        MenuFlyout final = OpenStripFlyout(HostFor(), OnFinal(exits), MenuIds.TowerExitRight);
        MenuFlyout rollout = OpenStripFlyout(HostFor(), RollingOut(exits), MenuIds.TowerExitRight);

        Assert.All(ExitRows(final.Items), row => Assert.EndsWith(" from thr", row.Distance, StringComparison.Ordinal));
        Assert.All(ExitRows(rollout.Items), row => Assert.DoesNotContain("thr", row.Distance, StringComparison.Ordinal));
        Assert.NotEmpty(ExitRows(final.Items));
        Assert.NotEmpty(ExitRows(rollout.Items));
    }

    // --- The "Exit at" box ------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("W 2")]
    public void EmptyOrUnreadableBox_ShowsDots_AndSendsNothing(string typed)
    {
        RecordingMenuHost host = HostFor();
        MenuFlyout flyout = OpenStripFlyout(host, OnFinal([Exit("G", ExitSide.Right, 2700, false, 53)]), MenuIds.TowerExitRight);
        MenuItem box = Assert.IsType<MenuItem>(BoxItem(flyout.Items));
        Type(flyout, typed);

        Assert.Equal("ER ···", Assert.IsType<ExitAtBox>(box.Header).ShownCommand);
        Assert.True(box.StaysOpenOnClick);
        box.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        Assert.Empty(host.Sent);
        Assert.True(flyout.IsOpen);
    }

    /// <summary>
    /// Typing <c>c</c> lists the side's matches nearest the threshold first, and Enter sends the top match, <c>ER C2</c>, not
    /// the text typed.
    /// </summary>
    [AvaloniaFact]
    public void TypedPrefix_ListsMatchesWithDistances_EnterSendsTheTopMatch()
    {
        RecordingMenuHost host = HostFor();
        host.RunwayExits.AddRange([
            Exit("C1", ExitSide.Right, 5500, false, 89),
            Exit("C2", ExitSide.Right, 3000, false, 90),
            Exit("C3", ExitSide.Left, 900, false, 90),
        ]);
        MenuFlyout flyout = OpenStripFlyout(host, OnFinal([Exit("G", ExitSide.Right, 2700, false, 53)]), MenuIds.TowerExitRight);

        Type(flyout, "c");

        Assert.Equal(["Exit at — ER C2", "C2 · ~3,000 ft from thr — ER C2", "C1 · ~5,500 ft from thr — ER C1"], Rows(flyout.Items).TakeLast(3));
        Press(flyout, Key.Enter);

        Assert.Equal([(Callsign, "ER C2", Initials)], host.Sent);
    }

    /// <summary>A match the aircraft's exits-ahead list holds outranks a nearer one it does not: Enter sends the forecast exit.</summary>
    [AvaloniaFact]
    public void Box_RanksTheListedExitsBeforeNearerOnes()
    {
        RecordingMenuHost host = HostFor();
        host.RunwayExits.AddRange([Exit("C2", ExitSide.Right, 3000, false, 90), Exit("C1", ExitSide.Right, 5500, false, 89)]);
        AircraftModel aircraft = OnFinal([Exit("G", ExitSide.Right, 2700, false, 53), Exit("C1", ExitSide.Right, 5500, false, 89)]);
        MenuFlyout flyout = OpenStripFlyout(host, aircraft, MenuIds.TowerExitRight);

        Type(flyout, "c");

        Assert.Equal(["Exit at — ER C1", "C1 · ~5,500 ft from thr — ER C1", "C2 · ~3,000 ft from thr — ER C2"], Rows(flyout.Items).TakeLast(3));
        Press(flyout, Key.Enter);

        Assert.Equal([(Callsign, "ER C1", Initials)], host.Sent);
    }

    /// <summary>
    /// The exit the text names exactly comes first, ahead of a listed exit and a nearer one; then the listed exit, then the rest
    /// nearest the threshold first.
    /// </summary>
    [AvaloniaFact]
    public void Box_RanksAnExactNameFirst()
    {
        RecordingMenuHost host = HostFor();
        host.RunwayExits.AddRange([
            Exit("C2", ExitSide.Right, 3000, false, 90),
            Exit("C", ExitSide.Right, 4000, false, 90),
            Exit("C1", ExitSide.Right, 5500, false, 89),
        ]);
        AircraftModel aircraft = OnFinal([Exit("G", ExitSide.Right, 2700, false, 53), Exit("C1", ExitSide.Right, 5500, false, 89)]);
        MenuFlyout flyout = OpenStripFlyout(host, aircraft, MenuIds.TowerExitRight);

        Type(flyout, "c");

        Assert.Equal(
            ["Exit at — ER C", "C · ~4,000 ft from thr — ER C", "C1 · ~5,500 ft from thr — ER C1", "C2 · ~3,000 ft from thr — ER C2"],
            Rows(flyout.Items).TakeLast(4)
        );
        Press(flyout, Key.Enter);

        Assert.Equal([(Callsign, "ER C", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void Match_Clicked_SendsItsCommand()
    {
        RecordingMenuHost host = HostFor();
        host.RunwayExits.AddRange([Exit("C", ExitSide.Right, 3000, false, 90), Exit("C1", ExitSide.Right, 5500, false, 89)]);
        MenuFlyout flyout = OpenStripFlyout(host, OnFinal([Exit("G", ExitSide.Right, 2700, false, 53)]), MenuIds.TowerExitRight);
        Type(flyout, "C");

        Assert
            .Single(flyout.Items.OfType<MenuItem>(), item => AutomationProperties.GetName(item) == "C1 · ~5,500 ft from thr — ER C1")
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        Assert.Equal([(Callsign, "ER C1", Initials)], host.Sent);
        Assert.False(flyout.IsOpen);
    }

    [AvaloniaFact]
    public void NoMatch_SendsWhatWasTyped()
    {
        RecordingMenuHost host = HostFor();
        host.RunwayExits.Add(Exit("E", ExitSide.Right, 1700, false, 45));
        MenuFlyout flyout = OpenStripFlyout(host, OnFinal([Exit("G", ExitSide.Right, 2700, false, 53)]), MenuIds.TowerExitRight);

        Type(flyout, "h");

        Assert.Equal("Exit at — ER H", Rows(flyout.Items)[^1]);
        Assert.IsType<MenuItem>(BoxItem(flyout.Items)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal([(Callsign, "ER H", Initials)], host.Sent);
    }

    // --- Live refill --------------------------------------------------------------------------

    [AvaloniaFact]
    public void Tick_DistancesCountDownInPlace()
    {
        AircraftModel aircraft = RollingOut([Exit("G", ExitSide.Left, 300, false, 90), Exit("H", ExitSide.Left, 800, false, 90)]);
        RecordingMenuHost host = HostFor();
        MenuFlyout flyout = OpenStripFlyout(host, aircraft, MenuIds.TowerExitLeft);
        MenuItem h = RowItem(flyout, "EL H");

        aircraft.ExitsAhead = [Exit("G", ExitSide.Left, 100, false, 90), Exit("H", ExitSide.Left, 600, false, 90)];
        Tick(host);

        Assert.Same(h, RowItem(flyout, "EL H"));
        Assert.Equal(["Taxiway G · ~100 ft — EL G", "Taxiway H · ~600 ft — EL H"], Rows(flyout.Items).Skip(1).Take(2));
    }

    /// <summary>The live tick keeps the box's match rows: the same row survives a tick, and a match row holding focus keeps it.</summary>
    [AvaloniaFact]
    public void Tick_KeepsTheBoxsMatchRows_AndTheirFocus()
    {
        RecordingMenuHost host = HostFor();
        host.RunwayExits.AddRange([Exit("C2", ExitSide.Right, 3000, false, 90), Exit("C1", ExitSide.Right, 5500, false, 89)]);
        AircraftModel aircraft = OnFinal([Exit("G", ExitSide.Right, 2700, false, 53)]);
        MenuFlyout flyout = OpenStripFlyout(host, aircraft, MenuIds.TowerExitRight);
        Type(flyout, "c");
        MenuItem match = NamedItem(flyout, "C1 · ~5,500 ft from thr — ER C1");
        match.Focus();
        Assert.True(match.IsKeyboardFocusWithin);

        aircraft.ExitsAhead = [Exit("G", ExitSide.Right, 2600, false, 53)];
        Tick(host);

        Assert.Same(match, NamedItem(flyout, "C1 · ~5,500 ft from thr — ER C1"));
        Assert.True(match.IsKeyboardFocusWithin);
    }

    [AvaloniaFact]
    public void PointerInside_OrderFrozen_CatchesUpOnLeave()
    {
        AircraftModel aircraft = RollingOut([Exit("G", ExitSide.Left, 700, false, 90), Exit("H", ExitSide.Left, 800, false, 90)]);
        RecordingMenuHost host = HostFor();
        MenuFlyout flyout = OpenStripFlyout(host, aircraft, MenuIds.TowerExitLeft);
        PointerIn(flyout);

        aircraft.ExitsAhead =
        [
            Exit("H", ExitSide.Left, 600, false, 90),
            Exit("G", ExitSide.Left, 650, false, 90),
            Exit("J", ExitSide.Left, 2000, true, 39),
        ];
        Tick(host);
        Assert.Equal(["EL G", "EL H", "EL J"], Commands(flyout.Items));

        PointerOut(flyout);
        Layout(flyout);
        Assert.Equal(["EL H", "EL G", "EL J"], Commands(flyout.Items));
    }

    /// <summary>
    /// An exit the list drops once the aircraft has rolled past it reads "passed", greyed and disabled while frozen; a click on
    /// it sends nothing and leaves the flyout open; it drops out when the pointer leaves.
    /// </summary>
    [AvaloniaFact]
    public void VanishedExit_ShowsPassed_DisabledWhileFrozen_DropsOnLeave()
    {
        AircraftModel aircraft = RollingOut([Exit("G", ExitSide.Left, 300, false, 90), Exit("H", ExitSide.Left, 800, false, 90)]);
        aircraft.GroundSpeed = 200;
        RecordingMenuHost host = HostFor();
        MenuFlyout flyout = OpenStripFlyout(host, aircraft, MenuIds.TowerExitLeft);
        PointerIn(flyout);

        aircraft.ExitsAhead = [Exit("H", ExitSide.Left, 300, false, 90)];
        Tick(host);

        MenuItem passed = RowItem(flyout, "EL G");
        Assert.Equal("Taxiway G · passed — EL G", AutomationProperties.GetName(passed));
        Assert.False(passed.IsEnabled);
        Assert.True(passed.StaysOpenOnClick);
        Assert.Equal(MenuGlyphRowTemplate.DimmedOpacity, passed.Opacity);
        passed.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Empty(host.Sent);
        Assert.True(flyout.IsOpen);

        PointerOut(flyout);
        Layout(flyout);
        Assert.Equal(["EL H"], Commands(flyout.Items));
    }

    /// <summary>
    /// On the rollout at 80 kt (135 ft a second), an exit the list drops 300 ft ahead reads "can't make" while the aircraft is
    /// still short of it, and "passed" once it has rolled past it.
    /// </summary>
    [AvaloniaFact]
    public void Rollout_DroppedRow_ReadsCantMake_ThenPassedOnceRolledPast()
    {
        AircraftModel aircraft = RollingOut([Exit("H", ExitSide.Left, 300, false, 90), Exit("J", ExitSide.Left, 2000, true, 39)]);
        aircraft.GroundSpeed = 80;
        RecordingMenuHost host = HostFor();
        MenuFlyout flyout = OpenStripFlyout(host, aircraft, MenuIds.TowerExitLeft);
        PointerIn(flyout);

        aircraft.ExitsAhead = [Exit("J", ExitSide.Left, 1900, true, 39)];
        Tick(host);
        Assert.Equal("Taxiway H · can't make — EL H", AutomationProperties.GetName(RowItem(flyout, "EL H")));
        Tick(host);
        Assert.Equal("Taxiway H · can't make — EL H", AutomationProperties.GetName(RowItem(flyout, "EL H")));
        Tick(host);
        Assert.Equal("Taxiway H · passed — EL H", AutomationProperties.GetName(RowItem(flyout, "EL H")));
    }

    /// <summary>On final an exit the forecast drops reads "can't make", and never "passed" however far the aircraft flies.</summary>
    [AvaloniaFact]
    public void Final_DroppedRow_ReadsCantMake_NeverPassed()
    {
        AircraftModel aircraft = OnFinal([Exit("G", ExitSide.Right, 2700, false, 53), Exit("H", ExitSide.Right, 3100, false, 90)]);
        aircraft.GroundSpeed = 140;
        RecordingMenuHost host = HostFor();
        MenuFlyout flyout = OpenStripFlyout(host, aircraft, MenuIds.TowerExitRight);
        PointerIn(flyout);

        aircraft.ExitsAhead = [Exit("H", ExitSide.Right, 3100, false, 90)];
        for (int second = 0; second < 30; second++)
        {
            Tick(host);
        }

        MenuItem dropped = RowItem(flyout, "ER G");
        Assert.Equal("Taxiway G · can't make — ER G", AutomationProperties.GetName(dropped));
        Assert.False(dropped.IsEnabled);
    }

    // --- Final with no exit forecast on the side ------------------------------------------------

    /// <summary>
    /// On final with no right exit forecast but right exits on the runway, Exit right stays: a dimmed "none forecast" line, no
    /// Pilot's choice, and the "Exit at" box listing the runway's right exits nearest first while empty, narrowed by typing.
    /// </summary>
    [AvaloniaFact]
    public void Final_NoneForecastOnTheSide_ShowsNoneForecast_AndTheBoxOverTheRunwaysExits()
    {
        RecordingMenuHost host = HostFor();
        host.RunwayExits.AddRange([
            Exit("G", ExitSide.Right, 2700, false, 53),
            Exit("E", ExitSide.Right, 1700, false, 45),
            Exit("J", ExitSide.Left, 4700, false, 39),
        ]);
        AircraftModel aircraft = OnFinal([Exit("J", ExitSide.Left, 4700, true, 39)]);
        MenuFlyout flyout = OpenStripFlyout(host, aircraft, MenuIds.TowerExitRight);

        Assert.Equal(
            ["none forecast", "---", "Exit at — ER ···", "E · ~1,700 ft from thr — ER E", "G · ~2,700 ft from thr — ER G"],
            Rows(flyout.Items)
        );
        Assert.False(NamedItem(flyout, "none forecast").IsEnabled);

        Type(flyout, "g");

        Assert.Equal(["none forecast", "---", "Exit at — ER G", "G · ~2,700 ft from thr — ER G"], Rows(flyout.Items));
    }

    /// <summary>
    /// On final, when the last forecast row on the side drops with the pointer outside, the row that held focus hands it to the
    /// "Exit at" box, since the none-forecast layout shows no Pilot's choice.
    /// </summary>
    [AvaloniaFact]
    public void Final_LastRowDrops_FocusGoesToTheBox()
    {
        RecordingMenuHost host = HostFor();
        host.RunwayExits.Add(Exit("E", ExitSide.Right, 1700, false, 45));
        AircraftModel aircraft = OnFinal([Exit("G", ExitSide.Right, 2700, false, 53)]);
        MenuFlyout flyout = OpenStripFlyout(host, aircraft, MenuIds.TowerExitRight);
        MenuItem row = RowItem(flyout, "ER G");
        row.Focus();
        Assert.True(row.IsKeyboardFocusWithin);

        aircraft.ExitsAhead = [];
        Tick(host);

        Assert.True(Assert.IsType<MenuItem>(BoxItem(flyout.Items)).IsKeyboardFocusWithin);
    }

    /// <summary>On final with no exit forecast on the side and none on the runway there, the entry is not offered.</summary>
    [AvaloniaFact]
    public void Final_NoExitOnTheSide_NorOnTheRunway_HasNoEntry()
    {
        RecordingMenuHost host = HostFor();
        host.RunwayExits.Add(Exit("J", ExitSide.Left, 4700, false, 39));
        AircraftModel aircraft = OnFinal([Exit("J", ExitSide.Left, 4700, true, 39)]);

        Assert.Null(MenuCatalog.Get(MenuIds.TowerExitRight).Build(aircraft, Context(), host));
    }

    /// <summary>On the rollout the side rule stands: no listed exit on the side, no entry, whatever the runway has there.</summary>
    [AvaloniaFact]
    public void Rollout_NoListedExitOnTheSide_HasNoEntry_EvenWithRunwayExits()
    {
        RecordingMenuHost host = HostFor();
        host.RunwayExits.Add(Exit("E", ExitSide.Right, 1700, false, 45));
        AircraftModel aircraft = RollingOut([Exit("G", ExitSide.Left, 600, false, 90)]);

        Assert.False(MenuCatalog.Get(MenuIds.TowerExitRight).IsApplicable(aircraft, Context()));
        Assert.Null(MenuCatalog.Get(MenuIds.TowerExitRight).Build(aircraft, Context(), host));
    }

    [AvaloniaFact]
    public void AllCommands_SameRows_NoLiveTick()
    {
        RecordingMenuHost host = HostFor();
        AircraftModel aircraft = RollingOut([Exit("G", ExitSide.Left, 300, false, 90)]);
        MenuItem exit = Assert.IsType<MenuItem>(MenuCatalog.Get(MenuIds.TowerExitLeft).Build(aircraft, Context(), host));
        int timers = host.Clock.LiveTimerCount;

        exit.IsSubMenuOpen = true;
        Assert.Equal(timers, host.Clock.LiveTimerCount);
        Assert.Equal(["[Exits ahead, left side · from the aircraft]", "Taxiway G · ~300 ft — EL G", "---", "Pilot's choice — EL"], Rows(exit.Items));

        aircraft.ExitsAhead = [Exit("H", ExitSide.Left, 500, false, 90)];
        exit.IsSubMenuOpen = false;
        exit.IsSubMenuOpen = true;
        Assert.Equal(["EL H"], Commands(exit.Items));
    }

    // --- Rows ------------------------------------------------------------------------------------

    [AvaloniaFact]
    public void LongNote_EllipsisedOnOneLine_FullTextInTheTooltip()
    {
        ExitAheadDto longRow = Exit("CARGO WEST NORTH APRON", ExitSide.Right, 10800, true, 128);
        MenuFlyout flyout = OpenStripFlyout(HostFor(), OnFinal([longRow, Exit("S3", ExitSide.Right, 11300, false, 90)]), MenuIds.TowerExitRight);

        MenuItem item = RowItem(flyout, "ER CARGO WEST NORTH APRON");
        TextBlock note = item.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Text == " · planned");
        Grid grid = note.GetVisualAncestors().OfType<Grid>().First(candidate => candidate.ColumnDefinitions.Count == 4);

        Assert.True(grid.Bounds.Width <= MenuCommandRow.RowMaxWidth + 0.5, $"The row is {grid.Bounds.Width:F1}px wide.");
        Assert.True((note.Bounds.Width < 1) || note.TextLayout.TextLines.Any(line => line.HasCollapsed), "The note should be cut.");
        Assert.All(grid.GetVisualDescendants().OfType<TextBlock>(), block => Assert.Single(block.TextLayout.TextLines));
        Assert.Equal("Taxiway CARGO WEST NORTH APRON · planned · ~10,800 ft from thr · turn-off 128°", ToolTip.GetTip(grid));
    }

    [AvaloniaFact]
    public void Glyph_IsDrawnAtTheDtosTurnOffAngle()
    {
        AircraftModel aircraft = RollingOut([Exit("G", ExitSide.Left, 600, false, 90), Exit("J", ExitSide.Left, 2800, true, 39)]);
        MenuFlyout flyout = OpenStripFlyout(HostFor(), aircraft, MenuIds.TowerExitLeft);

        QuickCommandGlyph square = Assert.IsType<ExitRow>(RowItem(flyout, "EL G").Header).Glyph;
        QuickCommandGlyph highSpeed = Assert.IsType<ExitRow>(RowItem(flyout, "EL J").Header).Glyph;

        Assert.Equal(ExitFlyout.TurnOffGlyph(ExitSide.Left, 90), square);
        Assert.Equal(ExitFlyout.TurnOffGlyph(ExitSide.Left, 39), highSpeed);
        Assert.Contains("M18 22V13 L6 13", square.PathData, StringComparison.Ordinal);
        Assert.Contains("M18 22V13 L10.4 3.7", highSpeed.PathData, StringComparison.Ordinal);
        Assert.Contains("M6 22V13 L18 13", ExitFlyout.TurnOffGlyph(ExitSide.Right, 90).PathData, StringComparison.Ordinal);
        Assert.Equal(QuickCommandGlyphFamily.Tower, highSpeed.Family);
    }

    // --- The host's runway exits, on the real KOAK layout ----------------------------------------

    [AvaloniaFact]
    public void ClientHost_RunwayExits_OnOak28R_RightSide_NearestTheThresholdFirst()
    {
        (ClientMenuHost host, _) = OakHost(OnFinal([]));

        IReadOnlyList<ExitAheadDto> right = host.GetRunwayExits(Callsign, ExitSide.Right);

        Assert.NotEmpty(right);
        Assert.Contains(right, exit => exit.Taxiway == "E");
        Assert.All(
            right,
            exit =>
            {
                Assert.Equal(ExitSide.Right, exit.Side);
                Assert.False(exit.Planned);
                Assert.True(exit.DistanceFt > 0);
                Assert.Equal(0, exit.DistanceFt % 100);
            }
        );
        Assert.Equal(right.OrderBy(exit => exit.DistanceFt).Select(exit => exit.DistanceFt), right.Select(exit => exit.DistanceFt));
        Assert.Equal(right.Count, right.Select(exit => exit.Taxiway).Distinct().Count());
    }

    [AvaloniaFact]
    public void ClientHost_RunwayExits_EmptyWithoutALayoutOrARunway()
    {
        AircraftModel noRunway = OnFinal([]);
        noRunway.AssignedRunway = "";
        (ClientMenuHost oak, _) = OakHost(noRunway);
        var bare = new ClientMenuHost(MainWith(OnFinal([])), null, new Border());

        Assert.Empty(oak.GetRunwayExits(Callsign, ExitSide.Right));
        Assert.Empty(bare.GetRunwayExits(Callsign, ExitSide.Right));
        Assert.Empty(oak.GetRunwayExits("NOBODY", ExitSide.Right));
    }

    /// <summary>
    /// The destination gate matches FAA and ICAO forms through the navigation data, outside CONUS too, and tells another
    /// airport apart.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("PHNL", "HNL", true)]
    [InlineData("HNL", "PHNL", true)]
    [InlineData("KOAK", "OAK", true)]
    [InlineData("OAK", "KOAK", true)]
    [InlineData("KSFO", "OAK", false)]
    [InlineData("", "OAK", false)]
    public void LandsAt_MatchesTheAirportInEitherForm(string destination, string airportId, bool expected)
    {
        TestVnasData.EnsureInitialized();

        Assert.Equal(expected, GroundViewModel.LandsAt(destination, airportId));
    }

    /// <summary>
    /// An SFO arrival on its 28R while the ground view shows KOAK, which has a 28R too, gets none of OAK's exits: the host lists
    /// none and the box suggests none.
    /// </summary>
    [AvaloniaFact]
    public void ClientHost_RunwayExits_EmptyForAnotherAirportsArrival()
    {
        AircraftModel sfoArrival = OnFinal([Exit("G", ExitSide.Right, 2700, false, 53)]);
        sfoArrival.Destination = "KSFO";
        (ClientMenuHost host, _) = OakHost(sfoArrival);
        MenuItem exit = Assert.IsType<MenuItem>(MenuCatalog.Get(MenuIds.TowerExitRight).Build(sfoArrival, Context(), host));
        exit.IsSubMenuOpen = true;
        ExitAtBox box = Assert.IsType<ExitAtBox>(Assert.IsType<MenuItem>(BoxItem(exit.Items)).Header);

        box.Text = "e";

        Assert.Empty(host.GetRunwayExits(Callsign, ExitSide.Right));
        Assert.Empty(host.GetRunwayExits(Callsign, ExitSide.Left));
        Assert.Empty(box.Matches);
    }

    /// <summary>
    /// On a real KOAK 28R final whose exits-ahead rows are G and P, typing <c>E</c> (a right exit of 28R that the list does not
    /// hold) matches it from the runway's exits and sends <c>ER E</c>.
    /// </summary>
    [AvaloniaFact]
    public void Box_OnOak28R_MatchesARunwayExitTheListDoesNotHold()
    {
        AircraftModel aircraft = OnFinal([Exit("G", ExitSide.Right, 2700, false, 53), Exit("P", ExitSide.Right, 4600, false, 53)]);
        (ClientMenuHost inner, AircraftModel _) = OakHost(aircraft);
        var host = new SendCapturingHost(inner, Initials);
        MenuItem exit = Assert.IsType<MenuItem>(MenuCatalog.Get(MenuIds.TowerExitRight).Build(aircraft, Context(), host));
        exit.IsSubMenuOpen = true;
        ExitAtBox box = Assert.IsType<ExitAtBox>(Assert.IsType<MenuItem>(BoxItem(exit.Items)).Header);

        box.Text = "e";

        ExitAheadDto e = Assert.Single(box.Matches, match => match.Taxiway == "E");
        Assert.Contains($"E · {HoldShortLadder.FeetText(e.DistanceFt)} from thr — ER E", Rows(exit.Items));
        Assert.IsType<MenuItem>(BoxItem(exit.Items)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal([(Callsign, "ER E", Initials)], host.Sent);
    }

    // --- Harness ----------------------------------------------------------------------------------

    private static ExitAheadDto Exit(string taxiway, ExitSide side, int feet, bool planned, int degrees) =>
        new(taxiway, side, feet, planned, degrees);

    private static AircraftModel RollingOut(IReadOnlyList<ExitAheadDto> exits) =>
        new()
        {
            Callsign = Callsign,
            AircraftType = "C56X",
            FlightRules = "IFR",
            IsOnGround = true,
            CurrentPhase = "Landing",
            Situation = AircraftSituation.RolloutExit,
            SituationFlags = SituationFlags.RolloutDecelerating,
            AssignedRunway = "28R",
            ExitsAhead = exits,
        };

    private static AircraftModel OnFinal(IReadOnlyList<ExitAheadDto> exits) =>
        new()
        {
            Callsign = Callsign,
            AircraftType = "C680",
            FlightRules = "IFR",
            IsOnGround = false,
            CurrentPhase = "FinalApproach",
            Situation = AircraftSituation.Final,
            Destination = "KOAK",
            AssignedRunway = "28R",
            ExitsAhead = exits,
        };

    private static MenuContext Context() => TestMenuContext.Create(Callsign, Initials, null, false, VfrCommandsForIfr.None);

    private static RecordingMenuHost HostFor() =>
        new("")
        {
            Session = new(
                Initials,
                false,
                VfrCommandsForIfr.None,
                _ => [new CatalogQuickCommandEntry(MenuIds.TowerExitLeft, null), new CatalogQuickCommandEntry(MenuIds.TowerExitRight, null)]
            ),
        };

    private static MainViewModel MainWith(AircraftModel target)
    {
        var main = new MainViewModel(new FakeFilePickerService());
        main.Aircraft.Clear();
        main.Aircraft.Add(target);
        return main;
    }

    /// <summary>A client host over a main view model holding <paramref name="aircraft"/>, its ground view on the real KOAK layout.</summary>
    private static (ClientMenuHost Host, AircraftModel Aircraft) OakHost(AircraftModel aircraft)
    {
        TestVnasData.EnsureInitialized();
        MainViewModel main = MainWith(aircraft);
        main.Ground.SetDomainLayoutForTesting(MenuGoldenFixtures.OakDomainLayout);
        return (new ClientMenuHost(main, aircraft, new Border()), aircraft);
    }

    /// <summary>One second passes: the flyout's tick fires and the refill it posts runs.</summary>
    private static void Tick(RecordingMenuHost host)
    {
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        Dispatcher.UIThread.RunJobs();
    }

    private static MenuFlyout OpenStripFlyout(RecordingMenuHost host, AircraftModel aircraft, string id)
    {
        var window = new Window { Width = 900, Height = 900 };
        window.Show();
        ContextMenu menu = AircraftMenuBuilder.Build(aircraft, new MenuClick(Callsign, null, null, []), host, _ => []);
        menu.Open(window);
        Dispatcher.UIThread.RunJobs();
        menu.UpdateLayout();
        MenuItem strip = Assert.Single(menu.Items.OfType<MenuItem>(), QuickCommandStrip.IsStrip);
        Button button = QuickCommandStrip.Buttons(strip).Single(b => (string)b.Tag! == id);
        QuickCommandStrip.OpenSubmenu(button);
        Dispatcher.UIThread.RunJobs();
        MenuFlyout flyout = Assert.IsType<MenuFlyout>(FlyoutBase.GetAttachedFlyout(button));
        Assert.True(flyout.IsOpen);
        Layout(flyout);
        return flyout;
    }

    /// <summary>Types <paramref name="text"/> into the open flyout's "Exit at" box, focused as a user would.</summary>
    private static void Type(MenuFlyout flyout, string text)
    {
        TextBox box = ((Control)flyout.Popup.Child!)
            .GetVisualDescendants()
            .OfType<TextBox>()
            .Single(candidate => candidate.Name == ExitFlyout.BoxName);
        box.Focus();
        box.Text = text;
        Layout(flyout);
    }

    /// <summary>Presses <paramref name="key"/> on whatever holds focus in the flyout.</summary>
    private static void Press(MenuFlyout flyout, Key key)
    {
        Control focused = Assert.IsAssignableFrom<Control>(TopLevel.GetTopLevel(flyout.Popup.Child)?.FocusManager?.GetFocusedElement());
        focused.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
        Layout(flyout);
    }

    private static void PointerIn(MenuFlyout flyout) => RaisePointer((Control)flyout.Popup.Child!, InputElement.PointerEnteredEvent);

    private static void PointerOut(MenuFlyout flyout) => RaisePointer((Control)flyout.Popup.Child!, InputElement.PointerExitedEvent);

    private static void Layout(MenuFlyout flyout)
    {
        (flyout.Popup.Child as Control)?.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The items as shown: a disabled title's text in brackets, <c>---</c> for a separator, else the accessible name.</summary>
    private static List<string> Rows(ItemCollection items) =>
        [
            .. items.Select(child =>
                child switch
                {
                    Separator => "---",
                    MenuItem { Header: ExitFlyoutTitle title } => $"[{title}]",
                    MenuItem row => AutomationProperties.GetName(row) ?? "",
                    _ => throw new Xunit.Sdk.XunitException($"unexpected flyout child {child}"),
                }
            ),
        ];

    private static List<ExitRow> ExitRows(ItemCollection items) => [.. items.OfType<MenuItem>().Select(item => item.Header).OfType<ExitRow>()];

    private static List<string> Commands(ItemCollection items) => [.. ExitRows(items).Select(row => row.Command)];

    private static MenuItem? BoxItem(ItemCollection items) => items.OfType<MenuItem>().SingleOrDefault(item => item.Header is ExitAtBox);

    private static MenuItem RowItem(MenuFlyout flyout, string command) =>
        Assert.Single(flyout.Items.OfType<MenuItem>(), item => (item.Header as ExitRow)?.Command == command);

    private static MenuItem NamedItem(MenuFlyout flyout, string name) =>
        Assert.Single(flyout.Items.OfType<MenuItem>(), item => AutomationProperties.GetName(item) == name);

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
