using Avalonia;
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
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Testing;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Cross fix flyout (<see cref="FixValueFlyout"/>): the fix box, the Cross at toggle (At / At or above / At or below,
/// opening on At), the Altitude box and the optional Speed box over the route rows, each row showing and sending the whole
/// <c>CFIX</c>; a row with the altitude missing asks for it rather than sending.
/// </summary>
public class CrossFixFlyoutTests
{
    public CrossFixFlyoutTests() => TestVnasData.EnsureInitialized();

    private static CrossFixFlyoutPanel Open(IMenuAircraft aircraft, RecordingMenuHost host) =>
        Assert.IsType<CrossFixFlyoutPanel>(FixValueFlyoutHarness.Open(MenuIds.ProceduresCrossFix, aircraft, host));

    [AvaloniaFact]
    public void OpensOnAt_RowsShowTheCommandWaitingForTheAltitude()
    {
        CrossFixFlyoutPanel panel = Open(FixValueFlyoutHarness.Routed(), new RecordingMenuHost(""));

        Assert.Equal(CrossFixAltitudeType.At, panel.At);
        Assert.True(panel.Toggle(CrossFixAltitudeType.At).IsChecked);
        Assert.Equal(["SUNOL", "ECA"], panel.RouteRows.Select(row => row.Fix));
        Assert.True(panel.RouteRows[0].IsNext);
        Assert.Null(panel.RouteRows[0].SentCommand);
        Assert.Equal("CFIX SUNOL ···", panel.RouteRows[0].ShownCommand);
        Assert.Equal(FixList.DistanceAndBearing(FixValueFlyoutHarness.OakVor(), "SUNOL"), panel.RouteRows[0].Note);
        Assert.Equal("Cross fix · → SUNOL", panel.Lines[0]);
        Assert.Contains("Cross at [At] | At or above | At or below", panel.Lines);
    }

    [AvaloniaTheory]
    [InlineData(CrossFixAltitudeType.At, "110", "CFIX SUNOL 110")]
    [InlineData(CrossFixAltitudeType.AtOrAbove, "110", "CFIX SUNOL A110")]
    [InlineData(CrossFixAltitudeType.AtOrBelow, "11000", "CFIX SUNOL B110")]
    [InlineData(CrossFixAltitudeType.AtOrAbove, "50", "CFIX SUNOL A050")]
    [InlineData(CrossFixAltitudeType.At, "FL240", "CFIX SUNOL 240")]
    public void EachToggleState_SendsItsCommand_WhichTheParserAccepts(CrossFixAltitudeType at, string altitude, string expected)
    {
        var host = new RecordingMenuHost("");
        CrossFixFlyoutPanel panel = Open(FixValueFlyoutHarness.Routed(), host);

        FixValueFlyoutHarness.Click(panel.Toggle(at));
        panel.AltitudeBox.Text = altitude;
        Assert.Equal(expected, panel.RouteRows[0].ShownCommand);
        FixValueFlyoutHarness.Click(panel.RouteRows[0]);

        Assert.Equal([(FixValueFlyoutHarness.Callsign, expected, FixValueFlyoutHarness.Initials)], host.Sent);
        CrossFixCommand parsed = Assert.IsType<CrossFixCommand>(CommandParser.Parse(expected).Value);
        Assert.Equal(at, parsed.AltType);
    }

    [AvaloniaFact]
    public void EmptySpeed_SendsNoSpeedToken_ATypedSpeedIsAppended()
    {
        var host = new RecordingMenuHost("");
        CrossFixFlyoutPanel panel = Open(FixValueFlyoutHarness.Routed(), host);
        panel.AltitudeBox.Text = "110";

        Assert.Equal("CFIX ECA 110", panel.RouteRows[1].SentCommand);
        FixValueFlyoutHarness.Click(panel.Toggle(CrossFixAltitudeType.AtOrAbove));
        panel.SpeedBox.Text = "250";
        FixValueFlyoutHarness.Click(panel.RouteRows[0]);

        Assert.Equal([(FixValueFlyoutHarness.Callsign, "CFIX SUNOL A110 250", FixValueFlyoutHarness.Initials)], host.Sent);
        ParseResult<ParsedCommand> parsed = CommandParser.Parse("CFIX SUNOL A110 250");
        Assert.True(parsed.IsSuccess, parsed.Reason);
        Assert.Equal(250, Assert.IsType<CrossFixCommand>(parsed.Value).Speed);
    }

    /// <summary>The speed box takes 5-knot steps: 252 is refused and blocks sending, 255 sends.</summary>
    [AvaloniaFact]
    public void Speed_TakesFiveKnotSteps()
    {
        var host = new RecordingMenuHost("");
        CrossFixFlyoutPanel panel = Open(FixValueFlyoutHarness.Routed(), host);
        panel.AltitudeBox.Text = "110";

        panel.SpeedBox.Text = "252";
        Assert.True(panel.SpeedError.IsVisible);
        Assert.Equal(FixValueFlyout.SpeedSteps, panel.SpeedError.Text);
        Assert.Null(panel.RouteRows[0].SentCommand);
        FixValueFlyoutHarness.Click(panel.RouteRows[0]);
        Assert.Empty(host.Sent);

        panel.SpeedBox.Text = "25O";
        Assert.Equal(SpeedFlyout.InvalidSpeed, panel.SpeedError.Text);

        panel.SpeedBox.Text = "255";
        Assert.False(panel.SpeedError.IsVisible);
        FixValueFlyoutHarness.Click(panel.RouteRows[0]);
        Assert.Equal([(FixValueFlyoutHarness.Callsign, "CFIX SUNOL 110 255", FixValueFlyoutHarness.Initials)], host.Sent);
    }

    [AvaloniaTheory]
    [InlineData(CrossFixAltitudeType.AtOrAbove, "110", "250", "Cross SUNOL at or above 11,000 at 250 knots")]
    [InlineData(CrossFixAltitudeType.At, "110", "", "Cross SUNOL at 11,000")]
    [InlineData(CrossFixAltitudeType.AtOrBelow, "240", "", "Cross SUNOL at or below FL240")]
    public void Caption_SaysTheRestriction(CrossFixAltitudeType at, string altitude, string speed, string caption)
    {
        CrossFixFlyoutPanel panel = Open(FixValueFlyoutHarness.Routed(), new RecordingMenuHost(""));
        Assert.False(panel.Caption.IsVisible);

        panel.Box.Text = "sunol";
        FixValueFlyoutHarness.Click(panel.Toggle(at));
        panel.AltitudeBox.Text = altitude;
        panel.SpeedBox.Text = speed;

        Assert.True(panel.Caption.IsVisible);
        Assert.Equal(caption, panel.Caption.Text);
        Assert.Contains(caption, panel.Lines);
    }

    /// <summary>A row clicked with the altitude empty sends nothing: it puts the fix in the box and shows the red required state.</summary>
    [AvaloniaFact]
    public void RowClick_WithoutAltitude_SendsNothing_PicksTheFix_ShowsRequired()
    {
        var host = new RecordingMenuHost("");
        CrossFixFlyoutPanel panel = Open(FixValueFlyoutHarness.Routed(), host);
        Assert.False(panel.AltitudeError.IsVisible);

        FixValueFlyoutHarness.Click(panel.RouteRows[1]);

        Assert.Empty(host.Sent);
        Assert.Equal("ECA", panel.Box.Text);
        Assert.Same(panel.RouteRows[1], panel.OutlinedRow);
        Assert.True(panel.AltitudeError.IsVisible);
        Assert.Equal(FixValueFlyout.AltitudeRequired, panel.AltitudeError.Text);

        panel.AltitudeBox.Text = "80";
        Assert.False(panel.AltitudeError.IsVisible);
        Assert.Equal("CFIX ECA 080", panel.BoxCommand);
    }

    [AvaloniaFact]
    public void Altitude_ThatIsNone_ShowsTheRefusal()
    {
        CrossFixFlyoutPanel panel = Open(FixValueFlyoutHarness.Routed(), new RecordingMenuHost(""));

        panel.AltitudeBox.Text = "abc";

        Assert.True(panel.AltitudeError.IsVisible);
        Assert.Equal(AltitudeFlyout.InvalidAltitude, panel.AltitudeError.Text);
        Assert.Null(panel.RouteRows[0].SentCommand);
    }

    /// <summary>CFIX resolves no FRD, so an FRD in the fix box shows the box's refusal and Enter sends nothing.</summary>
    [AvaloniaFact]
    public void Box_RefusesAnFrd()
    {
        var host = new RecordingMenuHost("");
        CrossFixFlyoutPanel panel = Open(FixValueFlyoutHarness.Routed(), host);
        panel.AltitudeBox.Text = "110";

        panel.Box.Text = "sunol090010";
        panel.Box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });

        Assert.Null(panel.BoxCommand);
        Assert.True(panel.BoxError.IsVisible);
        Assert.Equal(DirectToFlyout.UnknownFix, panel.BoxError.Text);
        Assert.Empty(panel.MatchRows);
        Assert.Empty(host.Sent);
    }

    [AvaloniaFact]
    public void Typing_ListsOtherMatchesNearestFirst_EachWithItsCommand()
    {
        CrossFixFlyoutPanel panel = Open(FixValueFlyoutHarness.Routed(), new RecordingMenuHost(""));
        panel.AltitudeBox.Text = "110";

        panel.Box.Text = "sa";

        Assert.NotEmpty(panel.MatchRows);
        Assert.All(panel.MatchRows, row => Assert.Equal($"CFIX {row.Fix} 110", row.SentCommand));
        Assert.Same(panel.MatchRows[0], panel.OutlinedRow);
        Assert.Equal($"CFIX {panel.MatchRows[0].Fix} 110", panel.BoxCommandText.Text);
    }

    [AvaloniaFact]
    public void PassedFix_GreysInPlace_StaysPassed_AndStillSends()
    {
        AircraftModel aircraft = FixValueFlyoutHarness.RoutedModel();
        var host = new RecordingMenuHost("");
        CrossFixFlyoutPanel panel = Open(aircraft, host);

        aircraft.NavigatingTo = "ECA";
        aircraft.NavigationRoute = ["ECA"];
        panel.Refresh();
        aircraft.NavigatingTo = "";
        aircraft.NavigationRoute = [];
        panel.Refresh();

        Assert.Equal(["SUNOL", "ECA"], panel.RouteRows.Select(row => row.Fix));
        Assert.True(panel.RouteRows[0].IsPassed);
        Assert.Equal("passed", panel.RouteRows[0].Note);
        panel.AltitudeBox.Text = "110";
        FixValueFlyoutHarness.Click(panel.RouteRows[0]);
        Assert.Equal([(FixValueFlyoutHarness.Callsign, "CFIX SUNOL 110", FixValueFlyoutHarness.Initials)], host.Sent);
    }

    /// <summary>The fix box holds focus at open; Down focuses the ● row, Up returns to the box.</summary>
    [AvaloniaFact]
    public void Keyboard_DownFromTheBoxFocusesTheMarkedRow_UpReturns()
    {
        using FixValueFlyoutHarness.OpenedFlyout open = FixValueFlyoutHarness.OpenStrip(
            MenuIds.ProceduresCrossFix,
            FixValueFlyoutHarness.Routed(),
            new RecordingMenuHost("")
        );
        Assert.True(open.Panel.Box.IsKeyboardFocusWithin, "the fix box does not hold focus at open");

        open.Press(Key.Down);
        Assert.True(open.Panel.RouteRows[0].IsKeyboardFocusWithin, "Down from the box does not focus the ● row");
        open.Press(Key.Down);
        Assert.True(open.Panel.RouteRows[1].IsKeyboardFocusWithin, "Down does not walk the rows");
        open.Press(Key.Up);
        open.Press(Key.Up);

        Assert.True(open.Panel.Box.IsKeyboardFocusWithin, "Up from the top row does not return to the box");
    }

    /// <summary>Enter on a row without the altitude focuses the Altitude box; Enter there with a value sends the picked fix.</summary>
    [AvaloniaFact]
    public void Keyboard_EnterOnARowWithoutAltitude_FocusesTheAltitudeBox_EnterThereSends()
    {
        var host = new RecordingMenuHost("");
        using FixValueFlyoutHarness.OpenedFlyout open = FixValueFlyoutHarness.OpenStrip(
            MenuIds.ProceduresCrossFix,
            FixValueFlyoutHarness.Routed(),
            host
        );
        CrossFixFlyoutPanel panel = Assert.IsType<CrossFixFlyoutPanel>(open.Panel);

        open.Press(Key.Down);
        open.Press(Key.Down);
        open.Press(Key.Enter);
        Assert.Empty(host.Sent);
        Assert.True(panel.AltitudeBox.IsKeyboardFocusWithin, "Enter on a row without the altitude does not focus the Altitude box");

        panel.AltitudeBox.Text = "110";
        open.Press(Key.Enter);

        Assert.Equal([(FixValueFlyoutHarness.Callsign, "CFIX ECA 110", FixValueFlyoutHarness.Initials)], host.Sent);
        Assert.False(open.Menu.IsOpen);
    }

    /// <summary>Enter in the fix box naming a fix, with the altitude typed, sends that fix's command.</summary>
    [AvaloniaFact]
    public void Enter_InACompleteFixBox_Sends()
    {
        var host = new RecordingMenuHost("");
        CrossFixFlyoutPanel panel = Open(FixValueFlyoutHarness.Routed(), host);
        panel.AltitudeBox.Text = "110";

        panel.Box.Text = "sunol";
        panel.Box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });

        Assert.Equal([(FixValueFlyoutHarness.Callsign, "CFIX SUNOL 110", FixValueFlyoutHarness.Initials)], host.Sent);
    }

    /// <summary>Enter with a valid altitude but a speed off the 5-knot steps sends nothing and focuses the Speed box, its refusal shown.</summary>
    [AvaloniaFact]
    public void Enter_WithASpeedOffTheSteps_FocusesTheSpeedBox()
    {
        var host = new RecordingMenuHost("");
        using FixValueFlyoutHarness.OpenedFlyout open = FixValueFlyoutHarness.OpenStrip(
            MenuIds.ProceduresCrossFix,
            FixValueFlyoutHarness.Routed(),
            host
        );
        CrossFixFlyoutPanel panel = Assert.IsType<CrossFixFlyoutPanel>(open.Panel);
        panel.Box.Text = "SUNOL";
        panel.AltitudeBox.Text = "110";
        panel.SpeedBox.Text = "252";

        open.Press(Key.Enter);

        Assert.Empty(host.Sent);
        Assert.True(panel.SpeedBox.IsKeyboardFocusWithin, "Enter with a speed off the steps does not focus the Speed box");
        Assert.True(panel.SpeedError.IsVisible);
        Assert.Equal(FixValueFlyout.SpeedSteps, panel.SpeedError.Text);
    }
}
