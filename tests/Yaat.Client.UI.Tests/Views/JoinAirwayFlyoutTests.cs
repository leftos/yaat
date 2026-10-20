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
/// The Join airway flyout (<see cref="JoinAirwayFlyout"/>, <c>JAWY</c>): the Airway box, the filed airways marked ◆ first, then the
/// airways through the route and the fixes within 30 NM, nearest first. The aircraft sits on the OAK VOR filing V25 and J80.
/// </summary>
public class JoinAirwayFlyoutTests
{
    public JoinAirwayFlyoutTests() => TestVnasData.EnsureInitialized();

    private static FakeMenuAircraft Filed() =>
        new()
        {
            Callsign = FixValueFlyoutHarness.Callsign,
            Position = FixValueFlyoutHarness.OakVor(),
            Route = "OAK V25 SUNOL J80 ECA V25",
            RouteFixes = ["SUNOL", "ECA"],
        };

    private static JoinAirwayFlyoutPanel Open(IMenuAircraft? aircraft, RecordingMenuHost host) =>
        JoinFlyoutHarness.Open<JoinAirwayFlyoutPanel>(MenuIds.ProceduresJoinAirway, aircraft, host);

    [AvaloniaFact]
    public void FiledAirways_ComeFirst_MarkedFiled_EachOnce()
    {
        JoinAirwayFlyoutPanel panel = Open(Filed(), new RecordingMenuHost(""));

        Assert.Equal(["V25", "J80"], panel.Rows.Where(row => row.IsFiled).Select(row => row.Airway));
        Assert.Equal(["V25", "J80"], panel.Rows.Take(2).Select(row => row.Airway));
        Assert.DoesNotContain(panel.Rows.Skip(2), row => row.Airway is "V25" or "J80");
        Assert.Equal("Filed", panel.Lines[3]);
        Assert.StartsWith("◆ V25 · filed · ", panel.Lines[4]);
        Assert.EndsWith(" — JAWY V25", panel.Lines[4]);
    }

    /// <summary>After the filed ones, the airways through the route and the fixes within 30 NM, nearest first, at most 12.</summary>
    [AvaloniaFact]
    public void NearbyAirways_FollowTheFiled_NearestFirst()
    {
        FakeMenuAircraft aircraft = Filed();
        JoinAirwayFlyoutPanel panel = Open(aircraft, new RecordingMenuHost(""));

        NearbyAirway[] expected =
        [
            .. NavigationDatabase
                .Instance.AirwaysNear(aircraft.RouteFixes, aircraft.Position, JoinAirwayFlyout.NearbyRadiusNm)
                .Where(a => a.Id is not "V25" and not "J80")
                .Take(JoinAirwayFlyout.MaxNearby),
        ];
        Assert.Equal(JoinAirwayFlyout.MaxNearby, expected.Length);
        JoinAirwayRow[] nearby = [.. panel.Rows.Where(row => !row.IsFiled)];
        Assert.Equal(expected.Select(a => a.Id), nearby.Select(row => row.Airway));
    }

    /// <summary>
    /// Each row names the fix the sim proceeds to after intercepting the airway on the present heading (the sim's own
    /// <see cref="AirwayBracketing.AheadFix"/>), with its distance and bearing; where the sim finds no segment ahead the row
    /// stays, noting so.
    /// </summary>
    [AvaloniaFact]
    public void Rows_NameTheFixAheadAfterTheIntercept_OrNoSegmentAhead()
    {
        LatLon oak = FixValueFlyoutHarness.OakVor();
        IReadOnlyList<NearbyAirway> near = NavigationDatabase.Instance.AirwaysNear(["SUNOL", "ECA"], oak, JoinAirwayFlyout.NearbyRadiusNm);
        (string airway, double heading) = near.Take(JoinAirwayFlyout.MaxNearby)
            .SelectMany(a => Enumerable.Range(0, 8).Select(i => (a.Id, Heading: i * 45.0)))
            .First(pair => AirwayBracketing.AheadFix(NavigationDatabase.Instance, pair.Id, oak, new TrueHeading(pair.Heading)) is null);
        FakeMenuAircraft aircraft = Filed() with { Route = "", HeadingDegrees = heading };

        JoinAirwayFlyoutPanel panel = Open(aircraft, new RecordingMenuHost(""));

        JoinAirwayRow none = Assert.Single(panel.Rows, row => row.Airway == airway);
        Assert.Null(none.AheadFix);
        Assert.Equal(JoinAirwayFlyout.NoSegmentAhead, none.Note);
        Assert.All(
            panel.Rows,
            row => Assert.Equal(AirwayBracketing.AheadFix(NavigationDatabase.Instance, row.Airway, oak, new TrueHeading(heading)), row.AheadFix)
        );
        JoinAirwayRow some = panel.Rows.First(row => row.AheadFix is not null);
        Assert.StartsWith($"{some.AheadFix} · ", some.Note);
        Assert.EndsWith("°", some.Note);
    }

    /// <summary>A military training route is refused in the box: it is cleared with CMTR, never joined with JAWY (7110.65 9-2-6).</summary>
    [AvaloniaFact]
    public void MilitaryRoute_IsRefused_UseCmtr()
    {
        Assert.True(NavigationDatabase.Instance.IsMilitaryRoute("IR149"));
        var host = new RecordingMenuHost("");
        JoinAirwayFlyoutPanel panel = Open(Filed(), host);

        panel.Box.Text = "ir149";
        JoinFlyoutHarness.Enter(panel.Box);

        Assert.True(panel.BoxError.IsVisible);
        Assert.Equal("military route: use CMTR", panel.BoxError.Text);
        Assert.Null(panel.BoxCommand);
        Assert.False(panel.Caption.IsVisible);
        Assert.Empty(host.Sent);
    }

    [AvaloniaFact]
    public void RowClick_SendsJawy_WhichTheParserReads()
    {
        var host = new RecordingMenuHost("");
        JoinAirwayFlyoutPanel panel = Open(Filed(), host);

        JoinFlyoutHarness.Click(panel.Rows[1]);

        Assert.Equal([(FixValueFlyoutHarness.Callsign, "JAWY J80", FixValueFlyoutHarness.Initials)], host.Sent);
        Assert.Equal("J80", Assert.IsType<JoinAirwayCommand>(CommandParser.Parse("JAWY J80").Value).AirwayId);
    }

    /// <summary>A typed airway the navigation data knows sends from the box; the caption says it as a controller does (7110.65 4-4-1.a).</summary>
    [AvaloniaFact]
    public void TypedAirway_EnterSends_CaptionJoinsTheAirway()
    {
        var host = new RecordingMenuHost("");
        JoinAirwayFlyoutPanel panel = Open(Filed(), host);

        panel.Box.Text = "v25";

        Assert.False(panel.BoxError.IsVisible);
        Assert.Equal("JAWY V25", panel.BoxCommand);
        Assert.Equal("Join V25", panel.Caption.Text);
        JoinFlyoutHarness.Enter(panel.Box);
        Assert.Equal([(FixValueFlyoutHarness.Callsign, "JAWY V25", FixValueFlyoutHarness.Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void UnknownAirway_ShowsTheRefusal_AndSendsNothing()
    {
        Assert.False(NavigationDatabase.Instance.IsAirway("V9999"));
        var host = new RecordingMenuHost("");
        JoinAirwayFlyoutPanel panel = Open(Filed(), host);

        panel.Box.Text = "v9999";
        JoinFlyoutHarness.Enter(panel.Box);

        Assert.True(panel.BoxError.IsVisible);
        Assert.Equal(JoinAirwayFlyout.UnknownAirway, panel.BoxError.Text);
        Assert.Contains("unknown airway", panel.Lines);
        Assert.Null(panel.BoxCommand);
        Assert.False(panel.Caption.IsVisible);
        Assert.Empty(host.Sent);
    }

    [AvaloniaFact]
    public void Typing_FiltersTheRowsByPrefix()
    {
        JoinAirwayFlyoutPanel panel = Open(Filed(), new RecordingMenuHost(""));
        Assert.Contains(panel.Rows, row => !row.Airway.StartsWith('J'));

        panel.Box.Text = "j";

        Assert.All(panel.Rows.Where(row => row.IsVisible), row => Assert.StartsWith("J", row.Airway, StringComparison.Ordinal));
        Assert.All(panel.Rows.Where(row => !row.IsVisible), row => Assert.False(row.Airway.StartsWith('J')));
        Assert.Equal(["J80"], panel.Rows.Where(row => row.IsFiled && row.IsVisible).Select(row => row.Airway));
    }

    /// <summary>Without an aircraft or a route the flyout is the box alone; it still sends what is typed.</summary>
    [AvaloniaFact]
    public void NoAircraft_IsTheBoxAlone()
    {
        var host = new RecordingMenuHost("");
        JoinAirwayFlyoutPanel panel = Open(null, host);

        Assert.Empty(panel.Rows);
        panel.Box.Text = "J80";
        Assert.Equal("Join J80", panel.Caption.Text);
        JoinFlyoutHarness.Enter(panel.Box);
        Assert.Equal([(FixValueFlyoutHarness.Callsign, "JAWY J80", FixValueFlyoutHarness.Initials)], host.Sent);
    }

    /// <summary>Each beat redraws the distances in place; the rows keep their order.</summary>
    [AvaloniaFact]
    public void LiveTick_RedrawsTheDistances_RowsStayInPlace()
    {
        var aircraft = new AircraftModel
        {
            Callsign = FixValueFlyoutHarness.Callsign,
            Position = FixValueFlyoutHarness.OakVor(),
            Route = "OAK V25 SUNOL",
        };
        JoinAirwayFlyoutPanel panel = Open(aircraft, new RecordingMenuHost(""));
        string[] order = [.. panel.Rows.Select(row => row.Airway)];
        string before = panel.Rows[0].Note;

        aircraft.Position = JoinFlyoutHarness.Position("SFO");
        panel.Refresh();

        Assert.Equal(order, panel.Rows.Select(row => row.Airway));
        Assert.NotEqual(before, panel.Rows[0].Note);
    }

    /// <summary>The box holds focus at open; Down focuses the first row, Down walks, Up returns to the box; Enter sends the focused row.</summary>
    [AvaloniaFact]
    public void Keyboard_WalksTheRows_EnterSendsTheFocusedRow()
    {
        var host = new RecordingMenuHost("");
        using JoinFlyoutHarness.OpenedFlyout<JoinAirwayFlyoutPanel> open = JoinFlyoutHarness.OpenStrip<JoinAirwayFlyoutPanel>(
            MenuIds.ProceduresJoinAirway,
            Filed(),
            host
        );
        Assert.True(open.Panel.Box.IsKeyboardFocusWithin, "the box does not hold focus at open");

        open.Press(Key.Down);
        Assert.True(open.Panel.Rows[0].IsKeyboardFocusWithin, "Down from the box does not focus the first row");
        open.Press(Key.Down);
        Assert.True(open.Panel.Rows[1].IsKeyboardFocusWithin, "Down does not walk the rows");
        open.Press(Key.Up);
        open.Press(Key.Up);
        Assert.True(open.Panel.Box.IsKeyboardFocusWithin, "Up from the top row does not return to the box");

        open.Press(Key.Down);
        open.Press(Key.Down);
        open.Press(Key.Enter);

        Assert.Equal([(FixValueFlyoutHarness.Callsign, "JAWY J80", FixValueFlyoutHarness.Initials)], host.Sent);
        Assert.False(open.Menu.IsOpen);
    }
}
