using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
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
/// The Join radial flyout (<see cref="JoinRadialFlyout"/>, <c>JRADO</c> / <c>JRADI</c>): the Outbound / Inbound toggle, the
/// navaid-only Fix box, the navaid rows nearest first, the Radial box (empty sends the aircraft's present radial) and the rose.
/// The aircraft sits on the OAK VOR with SFO, a VOR whose station declination of record differs from the modelled one by more
/// than 3°, on its route.
/// </summary>
public class JoinRadialFlyoutTests
{
    public JoinRadialFlyoutTests() => TestVnasData.EnsureInitialized();

    private static FakeMenuAircraft Routed() =>
        new()
        {
            Callsign = FixValueFlyoutHarness.Callsign,
            Position = FixValueFlyoutHarness.OakVor(),
            RouteFixes = ["SUNOL", "SFO"],
        };

    private static JoinRadialFlyoutPanel Open(string id, IMenuAircraft? aircraft, RecordingMenuHost host) =>
        JoinFlyoutHarness.Open<JoinRadialFlyoutPanel>(id, aircraft, host);

    private static JoinRadialFlyoutPanel Open(IMenuAircraft? aircraft, RecordingMenuHost host) =>
        Open(MenuIds.ProceduresJoinRadialOutbound, aircraft, host);

    /// <summary>The magnetic radial from <paramref name="navaid"/> that <paramref name="from"/> lies on, by <paramref name="declination"/>.</summary>
    private static int Radial(string navaid, LatLon from, double declination) =>
        HeadingFlyout.WholeHeading(GeoMath.BearingTo(JoinFlyoutHarness.Position(navaid), from) - declination);

    /// <summary>SFO's station declination of record, asserted more than 3° off the modelled one at the station.</summary>
    private static double SfoStation()
    {
        double station = Assert.NotNull(NavigationDatabase.Instance.GetStationDeclination("SFO"));
        double modelled = MagneticDeclination.GetDeclination(JoinFlyoutHarness.Position("SFO"));
        Assert.True(Math.Abs(station - modelled) > 3, $"SFO station {station} vs modelled {modelled}");
        return station;
    }

    [AvaloniaFact]
    public void OpensOnTheNearestRouteNavaid_EvenWithANearerOneOff_TheRoute()
    {
        Assert.Equal("OAK", NavigationDatabase.Instance.NavaidsNear(FixValueFlyoutHarness.OakVor(), "")[0].Name);

        JoinRadialFlyoutPanel panel = Open(Routed(), new RecordingMenuHost(""));

        Assert.Equal("SFO", panel.Picked);
        Assert.Equal("SFO", panel.Rose.CentreCaption);
        Assert.True(Assert.Single(panel.Rows, row => row.Fix == "SFO").IsOutlined);
    }

    /// <summary>A route navaid beyond a VOR's high service volume (130 NM, AIM 1-1-8) is passed over for the nearest navaid.</summary>
    [AvaloniaFact]
    public void OpensOnTheNearestNavaid_WhenTheRouteNavaidIsBeyond130Nm()
    {
        Assert.True(NavigationDatabase.Instance.IsRadialNavaid("LKV"));
        double miles = GeoMath.DistanceNm(FixValueFlyoutHarness.OakVor(), JoinFlyoutHarness.Position("LKV"));
        Assert.True(miles > JoinRadialFlyout.RouteNavaidRangeNm, $"LKV is {miles:0} NM from OAK");

        JoinRadialFlyoutPanel panel = Open(Routed() with { RouteFixes = ["SUNOL", "LKV"] }, new RecordingMenuHost(""));

        Assert.Equal("OAK", panel.Picked);
        Assert.Equal(130, JoinRadialFlyout.RouteNavaidRangeNm);
    }

    [AvaloniaFact]
    public void OpensOnTheNearestNavaid_WithoutOneOnTheRoute()
    {
        JoinRadialFlyoutPanel panel = Open(Routed() with { RouteFixes = ["SUNOL"] }, new RecordingMenuHost(""));

        Assert.Equal("OAK", panel.Picked);
        Assert.Equal(["OAK"], panel.Rows.Take(1).Select(row => row.Fix));
        Assert.Equal(JoinRadialFlyout.MaxNavaids, panel.Rows.Count);
    }

    /// <summary>
    /// With the Radial box empty the command carries the radial the aircraft is on now, made magnetic by SFO's station
    /// declination (as the sim flies it), not the modelled one; the sim's own conversion maps it back onto the aircraft.
    /// </summary>
    [AvaloniaFact]
    public void EmptyRadialBox_SendsThePresentRadial_ByTheStationDeclination()
    {
        double station = SfoStation();
        LatLon oak = FixValueFlyoutHarness.OakVor();
        int expected = Radial("SFO", oak, station);
        Assert.NotEqual(Radial("SFO", oak, MagneticDeclination.GetDeclination(JoinFlyoutHarness.Position("SFO"))), expected);
        var host = new RecordingMenuHost("");
        JoinRadialFlyoutPanel panel = Open(Routed(), host);

        JoinFlyoutHarness.Enter(panel.FixBox);

        string command = $"JRADO SFO{HoldFlyout.Three(expected)}";
        Assert.Equal([(FixValueFlyoutHarness.Callsign, command, FixValueFlyoutHarness.Initials)], host.Sent);
        JoinRadialOutboundCommand parsed = Assert.IsType<JoinRadialOutboundCommand>(CommandParser.Parse(command).Value);
        TrueHeading back = RadialConversion.ToTrue(NavigationDatabase.Instance, "SFO", JoinFlyoutHarness.Position("SFO"), parsed.Radial);
        double off = Math.Abs(((back.Degrees - GeoMath.BearingTo(JoinFlyoutHarness.Position("SFO"), oak) + 540) % 360) - 180);
        Assert.True(off <= 0.5, $"the sim flies {back.Degrees:0.0}, {off:0.0}° off the aircraft's bearing from SFO");
        Assert.Contains($"{HoldFlyout.Three(expected)} radial · ", Assert.Single(panel.Rows, row => row.Fix == "SFO").Note);
    }

    /// <summary>The Fix box takes a VOR, VORTAC or TACAN only: a plain fix and a fix-radial-distance are refused.</summary>
    [AvaloniaTheory]
    [InlineData("sunol")]
    [InlineData("sfo090010")]
    public void FixBox_RefusesAnythingButARadialNavaid(string typed)
    {
        var host = new RecordingMenuHost("");
        JoinRadialFlyoutPanel panel = Open(Routed(), host);

        panel.FixBox.Text = typed;
        JoinFlyoutHarness.Enter(panel.FixBox);

        Assert.True(panel.FixError.IsVisible);
        Assert.Equal("not a VOR, VORTAC or TACAN", panel.FixError.Text);
        Assert.Null(panel.Picked);
        Assert.Null(panel.BoxCommand);
        Assert.Empty(host.Sent);

        panel.FixBox.Text = "oak";
        Assert.False(panel.FixError.IsVisible);
        Assert.Equal("OAK", panel.Picked);
    }

    /// <summary>A typed radial overrides the present one; north is 360, 000 is no radial; every command is one token the parser reads.</summary>
    [AvaloniaTheory]
    [InlineData(MenuIds.ProceduresJoinRadialOutbound, "184", "JRADO SFO184")]
    [InlineData(MenuIds.ProceduresJoinRadialInbound, "360", "JRADI SFO360")]
    [InlineData(MenuIds.ProceduresJoinRadialInbound, "7", "JRADI SFO007")]
    public void TypedRadial_SendsOneToken(string id, string radial, string command)
    {
        var host = new RecordingMenuHost("");
        JoinRadialFlyoutPanel panel = Open(id, Routed(), host);

        panel.RadialBox.Text = radial;
        JoinFlyoutHarness.Enter(panel.RadialBox);

        Assert.Equal([(FixValueFlyoutHarness.Callsign, command, FixValueFlyoutHarness.Initials)], host.Sent);
        Assert.True(CommandParser.Parse(command).IsSuccess);
    }

    [AvaloniaTheory]
    [InlineData("000")]
    [InlineData("361")]
    [InlineData("abc")]
    public void RadialBox_RefusesWhatIsNoRadial(string typed)
    {
        var host = new RecordingMenuHost("");
        JoinRadialFlyoutPanel panel = Open(Routed(), host);

        panel.RadialBox.Text = typed;
        JoinFlyoutHarness.Enter(panel.RadialBox);

        Assert.True(panel.RadialError.IsVisible);
        Assert.Equal(JoinRadialFlyout.InvalidRadial, panel.RadialError.Text);
        Assert.Null(panel.BoxCommand);
        Assert.Empty(host.Sent);
    }

    /// <summary>Each entry opens on its own side; the toggle switches the title and the verb the box and every row send.</summary>
    [AvaloniaTheory]
    [InlineData(
        MenuIds.ProceduresJoinRadialOutbound,
        RadialSide.Outbound,
        "Join radial outbound",
        RadialSide.Inbound,
        "Join radial inbound",
        "JRADI"
    )]
    [InlineData(MenuIds.ProceduresJoinRadialInbound, RadialSide.Inbound, "Join radial inbound", RadialSide.Outbound, "Join radial outbound", "JRADO")]
    public void Toggle_SwitchesTitleAndVerb(string id, RadialSide preset, string title, RadialSide other, string otherTitle, string otherVerb)
    {
        var host = new RecordingMenuHost("");
        JoinRadialFlyoutPanel panel = Open(id, Routed(), host);
        Assert.Equal(preset, panel.Side);
        Assert.True(panel.Toggle(preset).IsChecked);
        Assert.Equal(title, panel.Lines[0]);

        JoinFlyoutHarness.Click(panel.Toggle(other));
        panel.RadialBox.Text = "150";

        Assert.Equal(otherTitle, panel.Title);
        Assert.Equal($"{otherVerb} SFO150", panel.BoxCommand);
        Assert.All(panel.Rows, row => Assert.Equal($"{otherVerb} {row.Fix}150", row.SentCommand));
        JoinFlyoutHarness.Click(panel.Rows[1]);
        Assert.Equal([(FixValueFlyoutHarness.Callsign, $"{otherVerb} {panel.Rows[1].Fix}150", FixValueFlyoutHarness.Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void Caption_SaysTheRadialAndTheSide()
    {
        JoinRadialFlyoutPanel panel = Open(Routed(), new RecordingMenuHost(""));

        panel.RadialBox.Text = "184";
        Assert.Equal("Intercept the SFO 184 radial outbound", panel.Caption.Text);
        JoinFlyoutHarness.Click(panel.Toggle(RadialSide.Inbound));
        Assert.Equal("Intercept the SFO 184 radial inbound", panel.Caption.Text);
        Assert.Equal("Intercept the RBL 184 radial outbound", JoinRadialFlyout.Caption(RadialSide.Outbound, "RBL", 184));
    }

    /// <summary>
    /// A radial typed then cleared leaves no mark on the rose, so the arrow keys step from the aircraft's present radial, not
    /// from the radial once typed.
    /// </summary>
    [AvaloniaFact]
    public void ClearedRadialBox_LeavesNoMark_ArrowsStepFromThePresentRadial()
    {
        int present = Radial("SFO", FixValueFlyoutHarness.OakVor(), SfoStation());
        using JoinFlyoutHarness.OpenedFlyout<JoinRadialFlyoutPanel> open = JoinFlyoutHarness.OpenStrip<JoinRadialFlyoutPanel>(
            MenuIds.ProceduresJoinRadialOutbound,
            Routed(),
            new RecordingMenuHost("")
        );
        JoinRadialFlyoutPanel panel = open.Panel;

        panel.RadialBox.Text = "184";
        Assert.Equal(184, panel.Rose.Pick);
        panel.RadialBox.Text = "";
        Assert.Null(panel.Rose.Pick);
        panel.RadialBox.Text = "18x";
        Assert.Null(panel.Rose.Pick);
        panel.RadialBox.Text = "";

        panel.Rose.Focus(NavigationMethod.Directional);
        Dispatcher.UIThread.RunJobs();
        open.Press(Key.Right);

        int expected = HeadingFlyout.RoundedToFive(HeadingFlyout.RoundedToFive(present) + HeadingFlyout.Step);
        Assert.NotEqual(HeadingFlyout.RoundedToFive(184 + HeadingFlyout.Step), expected);
        Assert.Equal(expected, panel.Rose.Pick);
        Assert.Equal(HoldFlyout.Three(expected), panel.RadialBox.Text);
    }

    /// <summary>The rose draws no turn arc, centres the navaid, puts the needle on the aircraft's radial; a typed radial marks it.</summary>
    [AvaloniaFact]
    public void Rose_CentresTheNavaid_NeedleOnThePresentRadial_MarkOnTheTypedOne()
    {
        int present = Radial("SFO", FixValueFlyoutHarness.OakVor(), SfoStation());
        JoinRadialFlyoutPanel panel = Open(Routed(), new RecordingMenuHost(""));

        Assert.False(panel.Rose.ShowTurnArc);
        Assert.Equal("SFO", panel.Rose.CentreCaption);
        Assert.Equal(present, HeadingFlyout.WholeHeading(panel.Rose.PresentHeading));
        Assert.Null(panel.Rose.Pick);

        panel.RadialBox.Text = "184";
        Assert.Equal(184, panel.Rose.Pick);
        Assert.True(new HeadingRose().ShowTurnArc);
    }

    /// <summary>A click on the rose sets the Radial box to the radial clicked, and its release sends it.</summary>
    [AvaloniaFact]
    public void RoseClick_SetsTheRadialBox_AndSends()
    {
        var host = new RecordingMenuHost("");
        using JoinFlyoutHarness.OpenedFlyout<JoinRadialFlyoutPanel> open = JoinFlyoutHarness.OpenStrip<JoinRadialFlyoutPanel>(
            MenuIds.ProceduresJoinRadialOutbound,
            Routed(),
            host
        );
        HeadingRose rose = open.Panel.Rose;
        TopLevel top = TopLevel.GetTopLevel(rose)!;
        Point at = rose.TranslatePoint(new Point(HeadingRose.Centre + 100, HeadingRose.Centre), top)!.Value;

        top.MouseMove(at);
        top.MouseDown(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("090", open.Panel.RadialBox.Text);
        top.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([(FixValueFlyoutHarness.Callsign, "JRADO SFO090", FixValueFlyoutHarness.Initials)], host.Sent);
    }

    /// <summary>
    /// The Fix box holds focus at open; Down focuses the picked navaid's row, Down walks (picking each row's navaid), Up returns to
    /// the box from the top row; Enter sends the focused row.
    /// </summary>
    [AvaloniaFact]
    public void Keyboard_WalksTheRows_EnterSendsTheFocusedRow()
    {
        var host = new RecordingMenuHost("");
        FakeMenuAircraft aircraft = Routed() with { Position = JoinFlyoutHarness.Position("SUNOL"), RouteFixes = [] };
        using JoinFlyoutHarness.OpenedFlyout<JoinRadialFlyoutPanel> open = JoinFlyoutHarness.OpenStrip<JoinRadialFlyoutPanel>(
            MenuIds.ProceduresJoinRadialOutbound,
            aircraft,
            host
        );
        JoinRadialFlyoutPanel panel = open.Panel;
        Assert.True(panel.FixBox.IsKeyboardFocusWithin, "the Fix box does not hold focus at open");
        Assert.Equal(panel.Rows[0].Fix, panel.Picked);

        open.Press(Key.Down);
        Assert.True(panel.Rows[0].IsKeyboardFocusWithin, "Down from the box does not focus the picked row");
        open.Press(Key.Down);
        Assert.True(panel.Rows[1].IsKeyboardFocusWithin, "Down does not walk the rows");
        Assert.Equal(panel.Rows[1].Fix, panel.Picked);
        open.Press(Key.Up);
        open.Press(Key.Up);
        Assert.True(panel.FixBox.IsKeyboardFocusWithin, "Up from the top row does not return to the box");

        open.Press(Key.Down);
        string sent = Assert.IsType<string>(panel.Rows[0].SentCommand);
        open.Press(Key.Enter);

        Assert.Equal([(FixValueFlyoutHarness.Callsign, sent, FixValueFlyoutHarness.Initials)], host.Sent);
        Assert.False(open.Menu.IsOpen);
    }

    /// <summary>
    /// Each beat redraws the rows' radials and distances in place; a navaid picked by focusing its row (not the opening one) and a
    /// typed radial stay.
    /// </summary>
    [AvaloniaFact]
    public void LiveTick_RedrawsTheRows_KeepsThePickAndTheTypedRadial()
    {
        var aircraft = new AircraftModel { Callsign = FixValueFlyoutHarness.Callsign, Position = FixValueFlyoutHarness.OakVor() };
        using JoinFlyoutHarness.OpenedFlyout<JoinRadialFlyoutPanel> open = JoinFlyoutHarness.OpenStrip<JoinRadialFlyoutPanel>(
            MenuIds.ProceduresJoinRadialOutbound,
            aircraft,
            new RecordingMenuHost("")
        );
        JoinRadialFlyoutPanel panel = open.Panel;
        string opening = Assert.IsType<string>(panel.Picked);
        string[] order = [.. panel.Rows.Select(row => row.Fix)];
        string before = panel.Rows[0].Note;
        FixRow other = panel.Rows.First(row => row.Fix != opening);
        other.Focus(NavigationMethod.Directional);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(other.Fix, panel.Picked);
        panel.RadialBox.Text = "222";

        aircraft.Position = JoinFlyoutHarness.Position("SUNOL");
        panel.Refresh();

        Assert.Equal(order, panel.Rows.Select(row => row.Fix));
        Assert.NotEqual(before, panel.Rows[0].Note);
        Assert.Equal(other.Fix, panel.Picked);
        Assert.Equal("222", panel.RadialBox.Text);
    }
}
