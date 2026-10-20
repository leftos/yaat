using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Testing;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Hold at fix flyout (<see cref="HoldFlyout"/>, <c>HOLDP</c>): the Inbound course box (empty sends the default course), the
/// Legs chips opening on the altitude standard, the Right / Left turns toggle, the direction of holding and the caption; and the
/// 360s flyout (<c>HFIXL</c> / <c>HFIXR</c>) with its Left / Right toggle. The aircraft sits on the OAK VOR, navigating to SUNOL
/// (an intersection) with ECA (a name the navigation data has no position for) after it; SFO is the VOR whose station
/// declination the courses must read.
/// </summary>
public class HoldFlyoutTests
{
    public HoldFlyoutTests() => TestVnasData.EnsureInitialized();

    private static HoldPatternFlyoutPanel Open(IMenuAircraft aircraft, RecordingMenuHost host) =>
        Assert.IsType<HoldPatternFlyoutPanel>(FixValueFlyoutHarness.Open(MenuIds.HoldPattern, aircraft, host));

    private static TPanel OpenEntry<TPanel>(string id, IMenuAircraft? aircraft, RecordingMenuHost host)
    {
        MenuItem entry = Assert.IsType<MenuItem>(MenuCatalog.Get(id).Build(aircraft, FixValueFlyoutHarness.Context(), host));
        entry.IsSubMenuOpen = true;
        return Assert.IsType<TPanel>(Assert.IsAssignableFrom<FlyoutPanelHost>(Assert.Single(entry.Items.OfType<MenuItem>()).Header).Child);
    }

    private static LatLon Position(string fix)
    {
        (double lat, double lon) = NavigationDatabase.Instance.GetFixPosition(fix)!.Value;
        return new LatLon(lat, lon);
    }

    /// <summary>The magnetic course from <paramref name="from"/> to <paramref name="fix"/> under <paramref name="declination"/>.</summary>
    /// <remarks>The course arriving at the fix: the reciprocal of the bearing from the fix back to <paramref name="from"/>.</remarks>
    private static int Course(LatLon from, string fix, double declination) =>
        HeadingFlyout.WholeHeading(GeoMath.BearingTo(Position(fix), from) + 180 - declination);

    private static double Published(string fix) => NavigationDatabase.Instance.GetPublishedCourseDeclination(fix, string.Empty, Position(fix));

    private static int SunolCourse() => Course(FixValueFlyoutHarness.OakVor(), "SUNOL", MagneticDeclination.GetDeclination(Position("SUNOL")));

    private static FakeMenuAircraft Routed(double? assigned, double present) =>
        FixValueFlyoutHarness.Routed() with
        {
            AssignedAltitude = assigned,
            AltitudeFeet = present,
        };

    [AvaloniaTheory]
    [InlineData(HoldLeg.OneMinute, "1M", 1.0, true)]
    [InlineData(HoldLeg.OneAndHalfMinutes, "1.5M", 1.5, true)]
    [InlineData(HoldLeg.FiveMiles, "5", 5.0, false)]
    [InlineData(HoldLeg.TenMiles, "10", 10.0, false)]
    public void EachLegChip_SendsItsToken_WhichTheParserReads(HoldLeg leg, string token, double length, bool minutes)
    {
        var host = new RecordingMenuHost("");
        HoldPatternFlyoutPanel panel = Open(FixValueFlyoutHarness.Routed(), host);

        FixValueFlyoutHarness.Click(panel.LegChip(leg));
        FixValueFlyoutHarness.Click(panel.RouteRows[0]);

        string expected = $"HOLDP SUNOL {HoldFlyout.Three(SunolCourse())} {token} R";
        Assert.Equal([(FixValueFlyoutHarness.Callsign, expected, FixValueFlyoutHarness.Initials)], host.Sent);
        HoldingPatternCommand parsed = Assert.IsType<HoldingPatternCommand>(CommandParser.Parse(expected).Value);
        Assert.Equal(length, parsed.LegLength);
        Assert.Equal(minutes, parsed.IsMinuteBased);
        Assert.Equal(TurnDirection.Right, parsed.Direction);
    }

    /// <summary>The leg opens on the altitude standard (AIM 5-3-8): 1 minute up to 14,000 ft MSL, 1½ above, assigned altitude first.</summary>
    [AvaloniaTheory]
    [InlineData(14000.0, 20000.0, HoldLeg.OneMinute)]
    [InlineData(14100.0, 5000.0, HoldLeg.OneAndHalfMinutes)]
    [InlineData(null, 14000.0, HoldLeg.OneMinute)]
    [InlineData(null, 14100.0, HoldLeg.OneAndHalfMinutes)]
    public void Leg_OpensOnTheAltitudeStandard(double? assigned, double present, HoldLeg expected)
    {
        HoldPatternFlyoutPanel panel = Open(Routed(assigned, present), new RecordingMenuHost(""));

        Assert.Equal(expected, panel.Leg);
        Assert.False(panel.LegPicked);
        Assert.True(panel.LegChip(expected).IsChecked);
        Assert.EndsWith($" {HoldFlyout.LegToken(expected)} R", panel.RouteRows[0].SentCommand);
    }

    /// <summary>The leg follows the altitude on each live beat until a chip is picked; then it stays.</summary>
    [AvaloniaFact]
    public void Leg_FollowsTheAltitude_UntilAChipIsPicked()
    {
        AircraftModel aircraft = FixValueFlyoutHarness.RoutedModel();
        aircraft.Altitude = 10000;
        HoldPatternFlyoutPanel panel = Open(aircraft, new RecordingMenuHost(""));
        Assert.Equal(HoldLeg.OneMinute, panel.Leg);

        aircraft.Altitude = 16000;
        panel.Refresh();
        Assert.Equal(HoldLeg.OneAndHalfMinutes, panel.Leg);
        Assert.EndsWith(" 1.5M R", panel.RouteRows[0].SentCommand);

        FixValueFlyoutHarness.Click(panel.LegChip(HoldLeg.FiveMiles));
        aircraft.Altitude = 10000;
        panel.Refresh();

        Assert.Equal(HoldLeg.FiveMiles, panel.Leg);
        Assert.True(panel.LegPicked);
        Assert.EndsWith(" 5 R", panel.RouteRows[0].SentCommand);
    }

    /// <summary>
    /// With the box empty, the next route fix's course is the bearing from the aircraft, made magnetic by the modelled declination
    /// at the fix (SUNOL is no navaid): the non-navaid control.
    /// </summary>
    [AvaloniaFact]
    public void DefaultCourse_NextFix_IsFromTheAircraft_ModelledDeclinationAtTheFix()
    {
        Assert.Null(NavigationDatabase.Instance.GetNavaidType("SUNOL"));
        HoldPatternFlyoutPanel panel = Open(FixValueFlyoutHarness.Routed(), new RecordingMenuHost(""));

        Assert.Equal($"HOLDP SUNOL {HoldFlyout.Three(SunolCourse())} 1M R", panel.RouteRows[0].SentCommand);
    }

    /// <summary>
    /// A fix further down the route takes the course of the route leg into it, from the previous route fix; at a VOR it is made
    /// magnetic by the station's declination of record, not the modelled one (they differ by more than 3° at SFO).
    /// </summary>
    [AvaloniaFact]
    public void DefaultCourse_DownRouteVor_IsTheRouteLeg_StationDeclination()
    {
        Assert.Equal("SFO", StationOffModel());
        double station = NavigationDatabase.Instance.GetStationDeclination("SFO")!.Value;
        int expected = Course(Position("SUNOL"), "SFO", station);
        Assert.NotEqual(Course(Position("SUNOL"), "SFO", MagneticDeclination.GetDeclination(Position("SFO"))), expected);
        Assert.NotEqual(Course(FixValueFlyoutHarness.OakVor(), "SFO", station), expected);

        HoldPatternFlyoutPanel panel = Open(
            FixValueFlyoutHarness.Routed() with
            {
                NavigationRoute = ["SUNOL", "SFO"],
                RouteFixes = ["SUNOL", "SFO"],
            },
            new RecordingMenuHost("")
        );

        Assert.Equal($"HOLDP SFO {HoldFlyout.Three(expected)} 1M R", panel.RouteRows[1].SentCommand);
    }

    /// <summary>SFO, a VOR whose station declination of record differs from the modelled one by more than 3°; asserted, then named.</summary>
    private static string StationOffModel()
    {
        double station = Assert.NotNull(NavigationDatabase.Instance.GetStationDeclination("SFO"));
        double modelled = MagneticDeclination.GetDeclination(Position("SFO"));
        Assert.True(Math.Abs(station - modelled) > 3, $"SFO station {station} vs modelled {modelled}");
        Assert.True(HoldFlyout.IsRadialNavaid("SFO"), $"SFO is a {NavigationDatabase.Instance.GetNavaidType("SFO")}");
        return "SFO";
    }

    /// <summary>A fix off the route takes the bearing from the aircraft, at a VOR made magnetic by the station's declination.</summary>
    [AvaloniaFact]
    public void DefaultCourse_OffRouteVor_IsFromTheAircraft_StationDeclination()
    {
        Assert.Equal("SFO", StationOffModel());
        double station = NavigationDatabase.Instance.GetStationDeclination("SFO")!.Value;
        int expected = Course(FixValueFlyoutHarness.OakVor(), "SFO", station);
        Assert.NotEqual(Course(FixValueFlyoutHarness.OakVor(), "SFO", MagneticDeclination.GetDeclination(Position("SFO"))), expected);
        HoldPatternFlyoutPanel panel = Open(FixValueFlyoutHarness.Routed(), new RecordingMenuHost(""));

        panel.Box.Text = "sfo";

        Assert.Equal("SFO", panel.OutlinedRow?.Fix);
        Assert.Equal($"HOLDP SFO {HoldFlyout.Three(expected)} 1M R", panel.BoxCommand);
        Assert.Equal(HoldFlyout.Three(expected), panel.CourseBox.PlaceholderText);
    }

    /// <summary>A typed course overrides the default; north reads 360, and the parser reads it so.</summary>
    [AvaloniaFact]
    public void TypedCourse_Overrides_NorthIs360()
    {
        HoldPatternFlyoutPanel panel = Open(FixValueFlyoutHarness.Routed(), new RecordingMenuHost(""));

        panel.CourseBox.Text = "360";

        Assert.Equal("HOLDP SUNOL 360 1M R", panel.RouteRows[0].SentCommand);
        Assert.Equal("HOLDP ECA 360 1M R", panel.RouteRows[1].SentCommand);
        Assert.Equal(360, Assert.IsType<HoldingPatternCommand>(CommandParser.Parse("HOLDP SUNOL 360 1M R").Value).InboundCourse);
        Assert.Equal("HOLDP SUNOL 004 1M R", HoldFlyout.PatternCommand("SUNOL", 4, HoldLeg.OneMinute, HoldTurns.Right));
    }

    [AvaloniaTheory]
    [InlineData(184, "north")]
    [InlineData(200, "north")]
    [InlineData(360, "south")]
    [InlineData(270, "east")]
    [InlineData(90, "west")]
    [InlineData(45, "southwest")]
    [InlineData(225, "northeast")]
    [InlineData(135, "northwest")]
    [InlineData(315, "southeast")]
    public void HoldingSide_IsTheOutboundCourseOnEightPoints(int inbound, string side) => Assert.Equal(side, HoldFlyout.HoldingSide(inbound));

    /// <summary>Beside the course box: the direction of holding, and at a VOR also the radial, the reciprocal of the inbound course.</summary>
    [AvaloniaTheory]
    [InlineData("SFO", "north · 004 radial", "Hold north of SFO on the 004 radial")]
    [InlineData("SUNOL", "north", "Hold north of SUNOL on the 184 course inbound")]
    public void SideText_AndCaption_NameTheRadialAtAVor(string fix, string side, string caption)
    {
        HoldPatternFlyoutPanel panel = Open(FixValueFlyoutHarness.Routed(), new RecordingMenuHost(""));

        panel.Box.Text = fix;
        panel.CourseBox.Text = "184";

        Assert.Equal(side, panel.SideText);
        Assert.Contains($"Inbound course [184] — {side}", panel.Lines);
        Assert.True(panel.Caption.IsVisible);
        Assert.Equal(caption, panel.Caption.Text);
    }

    /// <summary>
    /// The caption leaves the standard leg and right turns unsaid; it says a picked leg (even the standard one), a leg that differs
    /// from the altitude standard, and left turns.
    /// </summary>
    [AvaloniaFact]
    public void Caption_SaysOnlyWhatDiffersFromTheStandard()
    {
        HoldPatternFlyoutPanel panel = Open(FixValueFlyoutHarness.Routed(), new RecordingMenuHost(""));
        panel.Box.Text = "sunol";
        panel.CourseBox.Text = "184";
        Assert.Equal("Hold north of SUNOL on the 184 course inbound", panel.Caption.Text);

        FixValueFlyoutHarness.Click(panel.LegChip(HoldLeg.OneMinute));
        Assert.Equal("Hold north of SUNOL on the 184 course inbound, 1-minute leg", panel.Caption.Text);

        FixValueFlyoutHarness.Click(panel.LegChip(HoldLeg.OneAndHalfMinutes));
        FixValueFlyoutHarness.Click(panel.TurnsToggle(HoldTurns.Left));
        Assert.Equal("Hold north of SUNOL on the 184 course inbound, 1½-minute leg, left turns", panel.Caption.Text);
        Assert.Equal("HOLDP SUNOL 184 1.5M L", panel.BoxCommand);

        FixValueFlyoutHarness.Click(panel.LegChip(HoldLeg.FiveMiles));
        Assert.Equal("Hold north of SUNOL on the 184 course inbound, 5-mile leg, left turns", panel.Caption.Text);
        FixValueFlyoutHarness.Click(panel.TurnsToggle(HoldTurns.Right));
        Assert.Equal("Hold north of SUNOL on the 184 course inbound, 5-mile leg", panel.Caption.Text);
    }

    /// <summary>Above 14,000 ft the 1½-minute standard leg goes unsaid until picked.</summary>
    [AvaloniaFact]
    public void Caption_StandardLegAboveFourteenThousand_IsSilent()
    {
        HoldPatternFlyoutPanel panel = Open(Routed(16000, 16000), new RecordingMenuHost(""));
        panel.Box.Text = "sunol";
        panel.CourseBox.Text = "184";

        Assert.Equal("HOLDP SUNOL 184 1.5M R", panel.BoxCommand);
        Assert.Equal("Hold north of SUNOL on the 184 course inbound", panel.Caption.Text);
    }

    /// <summary>HOLDP resolves no fix-radial-distance, so the fix box refuses one as it does an unknown fix.</summary>
    [AvaloniaFact]
    public void Pattern_BoxRefusesAnFrd()
    {
        var host = new RecordingMenuHost("");
        HoldPatternFlyoutPanel panel = Open(FixValueFlyoutHarness.Routed(), host);

        panel.Box.Text = "sunol090010";
        panel.Box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });

        Assert.Null(panel.BoxCommand);
        Assert.True(panel.BoxError.IsVisible);
        Assert.Equal(DirectToFlyout.UnknownFix, panel.BoxError.Text);
        Assert.Empty(host.Sent);
    }

    /// <summary>HFIXL / HFIXR resolve a fix-radial-distance, so the 360s fix box lists and sends one.</summary>
    [AvaloniaFact]
    public void Turns_BoxAcceptsAnFrd()
    {
        var host = new RecordingMenuHost("");
        HoldTurnsFlyoutPanel panel = OpenEntry<HoldTurnsFlyoutPanel>(MenuIds.HoldFixLeft, FixValueFlyoutHarness.Routed(), host);

        panel.Box.Text = "sunol090010";
        Assert.Equal("SUNOL090010", panel.MatchRows[0].Fix);
        Assert.False(panel.BoxError.IsVisible);
        panel.Box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });

        Assert.Equal([(FixValueFlyoutHarness.Callsign, "HFIXL SUNOL090010", FixValueFlyoutHarness.Initials)], host.Sent);
        Assert.True(CommandParser.Parse("HFIXL SUNOL090010").IsSuccess);
    }

    /// <summary>
    /// Without a default course (no aircraft to measure from) the course box is required: Enter sends nothing and shows the red
    /// required state; a course that is none is refused; a typed course sends.
    /// </summary>
    [AvaloniaFact]
    public void CourseBox_RequiredWithoutADefault_RefusesWhatIsNoCourse()
    {
        var host = new RecordingMenuHost("");
        HoldPatternFlyoutPanel panel = OpenEntry<HoldPatternFlyoutPanel>(MenuIds.HoldPattern, null, host);
        panel.Box.Text = "sunol";
        Assert.False(panel.CourseError.IsVisible);
        Assert.Equal(HoldFlyout.CoursePlaceholder, panel.CourseBox.PlaceholderText);
        Assert.Equal("HOLDP SUNOL ···", panel.BoxCommandText.Text);

        panel.Box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Assert.Empty(host.Sent);
        Assert.True(panel.CourseError.IsVisible);
        Assert.Equal(HoldFlyout.CourseRequired, panel.CourseError.Text);

        panel.CourseBox.Text = "abc";
        Assert.Equal(HoldFlyout.InvalidCourse, panel.CourseError.Text);
        Assert.Null(panel.BoxCommand);

        panel.CourseBox.Text = "184";
        Assert.False(panel.CourseError.IsVisible);
        panel.Box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Assert.Equal([(FixValueFlyoutHarness.Callsign, "HOLDP SUNOL 184 1M R", FixValueFlyoutHarness.Initials)], host.Sent);
    }

    /// <summary>The fix box holds focus at open; Down focuses the ● row, Down walks, Up returns to the box; Enter sends the focused row.</summary>
    [AvaloniaFact]
    public void Keyboard_WalksTheRows_EnterSendsTheFocusedRow()
    {
        var host = new RecordingMenuHost("");
        using FixValueFlyoutHarness.OpenedFlyout open = FixValueFlyoutHarness.OpenStrip(MenuIds.HoldPattern, FixValueFlyoutHarness.Routed(), host);
        Assert.True(open.Panel.Box.IsKeyboardFocusWithin, "the fix box does not hold focus at open");

        open.Press(Key.Down);
        Assert.True(open.Panel.RouteRows[0].IsKeyboardFocusWithin, "Down from the box does not focus the ● row");
        open.Press(Key.Down);
        Assert.True(open.Panel.RouteRows[1].IsKeyboardFocusWithin, "Down does not walk the rows");
        open.Press(Key.Up);
        open.Press(Key.Up);
        Assert.True(open.Panel.Box.IsKeyboardFocusWithin, "Up from the top row does not return to the box");

        open.Press(Key.Down);
        open.Press(Key.Enter);

        Assert.Equal(
            [(FixValueFlyoutHarness.Callsign, $"HOLDP SUNOL {HoldFlyout.Three(SunolCourse())} 1M R", FixValueFlyoutHarness.Initials)],
            host.Sent
        );
        Assert.False(open.Menu.IsOpen);
    }

    /// <summary>
    /// Only the live navigation route has legs: the rows the filed route adds after it (a fix already flown, the destination, the
    /// departure) take the course from the aircraft, never a leg from the row above.
    /// </summary>
    [AvaloniaFact]
    public void DefaultCourse_RowsPastTheNavigationRoute_AreFromTheAircraft()
    {
        FakeMenuAircraft aircraft = FixValueFlyoutHarness.Routed() with
        {
            NavigationRoute = ["SUNOL"],
            RouteFixes = ["SUNOL", "SJC", "KSFO", "KOAK"],
        };
        HoldPatternFlyoutPanel panel = Open(aircraft, new RecordingMenuHost(""));

        Assert.Equal(["SUNOL", "SJC", "KSFO", "KOAK"], panel.RouteRows.Select(row => row.Fix));
        foreach (FixRow row in panel.RouteRows)
        {
            int expected = Course(FixValueFlyoutHarness.OakVor(), row.Fix, Published(row.Fix));
            Assert.Equal($"HOLDP {row.Fix} {HoldFlyout.Three(expected)} 1M R", row.SentCommand);
        }
    }

    /// <summary>A re-route while the flyout is open moves a down-route fix's default leg onto the new route's previous fix.</summary>
    [AvaloniaFact]
    public void DefaultCourse_FollowsAReRouteWhileOpen()
    {
        AircraftModel aircraft = FixValueFlyoutHarness.RoutedModel();
        aircraft.NavigationRoute = ["SUNOL", "SFO"];
        HoldPatternFlyoutPanel panel = Open(aircraft, new RecordingMenuHost(""));
        Assert.Equal(["SUNOL", "SFO"], panel.RouteRows.Select(row => row.Fix));
        int viaSunol = Course(Position("SUNOL"), "SFO", Published("SFO"));
        Assert.Equal($"HOLDP SFO {HoldFlyout.Three(viaSunol)} 1M R", panel.RouteRows[1].SentCommand);

        aircraft.NavigatingTo = "SJC";
        aircraft.NavigationRoute = ["SJC", "SFO"];
        panel.Refresh();

        int viaSjc = Course(Position("SJC"), "SFO", Published("SFO"));
        Assert.NotEqual(viaSunol, viaSjc);
        Assert.Equal($"HOLDP SFO {HoldFlyout.Three(viaSjc)} 1M R", panel.RouteRows[1].SentCommand);
    }

    /// <summary>Each live beat redraws the default course from where the aircraft is now: the row's command and the box's placeholder.</summary>
    [AvaloniaFact]
    public void LiveTick_MovesTheShownDefaultCourse()
    {
        AircraftModel aircraft = FixValueFlyoutHarness.RoutedModel();
        HoldPatternFlyoutPanel panel = Open(aircraft, new RecordingMenuHost(""));
        panel.Box.Text = "sunol";
        string before = HoldFlyout.Three(SunolCourse());
        Assert.Equal(before, panel.CourseBox.PlaceholderText);

        aircraft.Position = Position("SFO");
        panel.Refresh();

        string after = HoldFlyout.Three(Course(Position("SFO"), "SUNOL", Published("SUNOL")));
        Assert.NotEqual(before, after);
        Assert.Equal($"HOLDP SUNOL {after} 1M R", panel.RouteRows[0].SentCommand);
        Assert.Equal(after, panel.CourseBox.PlaceholderText);
    }

    [AvaloniaFact]
    public void PassedFix_GreysInPlace_AndStillSends()
    {
        AircraftModel aircraft = FixValueFlyoutHarness.RoutedModel();
        var host = new RecordingMenuHost("");
        HoldPatternFlyoutPanel panel = Open(aircraft, host);

        aircraft.NavigatingTo = "ECA";
        aircraft.NavigationRoute = ["ECA"];
        panel.Refresh();

        Assert.Equal(["SUNOL", "ECA"], panel.RouteRows.Select(row => row.Fix));
        Assert.True(panel.RouteRows[0].IsPassed);
        FixValueFlyoutHarness.Click(panel.RouteRows[0]);
        (string _, string command, string _) = Assert.Single(host.Sent);
        Assert.Equal($"HOLDP SUNOL {HoldFlyout.Three(SunolCourse())} 1M R", command);
        Assert.True(CommandParser.Parse(command).IsSuccess);
    }

    /// <summary>The 360s flyout opens on its entry's side; the toggle switches the title and the verb every row and the box send.</summary>
    [AvaloniaTheory]
    [InlineData(MenuIds.HoldFixLeft, HoldTurns.Left, "Left 360s at fix", "HFIXL", HoldTurns.Right, "Right 360s at fix", "HFIXR")]
    [InlineData(MenuIds.HoldFixRight, HoldTurns.Right, "Right 360s at fix", "HFIXR", HoldTurns.Left, "Left 360s at fix", "HFIXL")]
    public void Turns_ToggleSwitchesTitleAndVerb(
        string id,
        HoldTurns preset,
        string title,
        string verb,
        HoldTurns other,
        string otherTitle,
        string otherVerb
    )
    {
        var host = new RecordingMenuHost("");
        HoldTurnsFlyoutPanel panel = OpenEntry<HoldTurnsFlyoutPanel>(id, FixValueFlyoutHarness.Routed(), host);

        Assert.Equal(preset, panel.Side);
        Assert.True(panel.Toggle(preset).IsChecked);
        Assert.Equal($"{title} · → SUNOL", panel.Lines[0]);
        Assert.Equal($"{verb} SUNOL", panel.RouteRows[0].SentCommand);

        FixValueFlyoutHarness.Click(panel.Toggle(other));
        panel.Box.Text = "eca";

        Assert.Equal(otherTitle, panel.Title);
        Assert.Same(QuickCommandGlyphs.ForPoint((other == HoldTurns.Left) ? MenuIds.PointHoldLeft : MenuIds.PointHoldRight), panel.BoxGlyph);
        Assert.Equal($"{otherVerb} SUNOL", panel.RouteRows[0].SentCommand);
        Assert.Equal($"{otherVerb} ECA", panel.BoxCommand);
        FixValueFlyoutHarness.Click(panel.RouteRows[1]);
        Assert.Equal([(FixValueFlyoutHarness.Callsign, $"{otherVerb} ECA", FixValueFlyoutHarness.Initials)], host.Sent);
    }
}
