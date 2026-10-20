using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Sim.Commands;
using Yaat.Sim.Testing;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Depart fix flyout (<see cref="FixValueFlyout"/>): the fix box and the Heading box over the route rows, each row showing
/// and sending the whole <c>DEPART</c>; a row with the heading missing asks for it rather than sending.
/// </summary>
public class DepartFixFlyoutTests
{
    public DepartFixFlyoutTests() => TestVnasData.EnsureInitialized();

    private static DepartFixFlyoutPanel Open(IMenuAircraft aircraft, RecordingMenuHost host) =>
        Assert.IsType<DepartFixFlyoutPanel>(FixValueFlyoutHarness.Open(MenuIds.ProceduresDepartFix, aircraft, host));

    [AvaloniaTheory]
    [InlineData("200", "DEPART SUNOL 200")]
    [InlineData("90", "DEPART SUNOL 090")]
    [InlineData("360", "DEPART SUNOL 360")]
    [InlineData("1", "DEPART SUNOL 001")]
    public void Heading_SendsThreeDigits_WhichTheParserAccepts(string heading, string expected)
    {
        var host = new RecordingMenuHost("");
        DepartFixFlyoutPanel panel = Open(FixValueFlyoutHarness.Routed(), host);
        Assert.Equal("DEPART SUNOL ···", panel.RouteRows[0].ShownCommand);

        panel.HeadingBox.Text = heading;
        Assert.Equal(expected, panel.RouteRows[0].ShownCommand);
        FixValueFlyoutHarness.Click(panel.RouteRows[0]);

        Assert.Equal([(FixValueFlyoutHarness.Callsign, expected, FixValueFlyoutHarness.Initials)], host.Sent);
        Assert.IsType<DepartFixCommand>(CommandParser.Parse(expected).Value);
    }

    [AvaloniaTheory]
    [InlineData("0")]
    [InlineData("361")]
    [InlineData("2000")]
    [InlineData("2O0")]
    public void Heading_OutsideOneTo360_IsRefused(string heading)
    {
        var host = new RecordingMenuHost("");
        DepartFixFlyoutPanel panel = Open(FixValueFlyoutHarness.Routed(), host);

        panel.HeadingBox.Text = heading;
        FixValueFlyoutHarness.Click(panel.RouteRows[0]);

        Assert.True(panel.HeadingError.IsVisible);
        Assert.Equal(FixValueFlyout.InvalidHeading, panel.HeadingError.Text);
        Assert.Empty(host.Sent);
    }

    [AvaloniaFact]
    public void Caption_SaysTheInstruction()
    {
        DepartFixFlyoutPanel panel = Open(FixValueFlyoutHarness.Routed(), new RecordingMenuHost(""));

        panel.Box.Text = "SUNOL";
        Assert.False(panel.Caption.IsVisible);
        panel.HeadingBox.Text = "200";

        Assert.Equal("Depart SUNOL heading 200", panel.Caption.Text);
        Assert.True(panel.Caption.IsVisible);
        Assert.Equal("DEPART SUNOL 200", panel.BoxCommandText.Text);
    }

    [AvaloniaFact]
    public void RowClick_WithoutHeading_SendsNothing_PicksTheFix_ShowsRequired()
    {
        var host = new RecordingMenuHost("");
        DepartFixFlyoutPanel panel = Open(FixValueFlyoutHarness.Routed(), host);
        Assert.False(panel.HeadingError.IsVisible);

        FixValueFlyoutHarness.Click(panel.RouteRows[1]);

        Assert.Empty(host.Sent);
        Assert.Equal("ECA", panel.Box.Text);
        Assert.True(panel.HeadingError.IsVisible);
        Assert.Equal(FixValueFlyout.HeadingRequired, panel.HeadingError.Text);
        Assert.Contains(FixValueFlyout.HeadingRequired, panel.Lines);
    }

    /// <summary>DEPART resolves no FRD, so an FRD in the fix box shows the box's refusal and Enter sends nothing.</summary>
    [AvaloniaFact]
    public void Box_RefusesAnFrd()
    {
        var host = new RecordingMenuHost("");
        DepartFixFlyoutPanel panel = Open(FixValueFlyoutHarness.Routed(), host);
        panel.HeadingBox.Text = "200";

        panel.Box.Text = "SUNOL090010";
        panel.Box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });

        Assert.Null(panel.BoxCommand);
        Assert.True(panel.BoxError.IsVisible);
        Assert.Empty(host.Sent);
    }

    [AvaloniaFact]
    public void PassedFix_GreysInPlace_AndStillSends()
    {
        AircraftModel aircraft = FixValueFlyoutHarness.RoutedModel();
        var host = new RecordingMenuHost("");
        DepartFixFlyoutPanel panel = Open(aircraft, host);

        aircraft.NavigatingTo = "ECA";
        aircraft.NavigationRoute = ["ECA"];
        panel.Refresh();

        Assert.True(panel.RouteRows[0].IsPassed);
        Assert.True(panel.RouteRows[1].IsNext);
        panel.HeadingBox.Text = "200";
        FixValueFlyoutHarness.Click(panel.RouteRows[0]);
        Assert.Equal([(FixValueFlyoutHarness.Callsign, "DEPART SUNOL 200", FixValueFlyoutHarness.Initials)], host.Sent);
    }

    /// <summary>With the heading typed, Down from the Heading box focuses the ● row and Enter sends it.</summary>
    [AvaloniaFact]
    public void Keyboard_DownFromTheHeadingBox_EnterSendsTheFocusedRow()
    {
        var host = new RecordingMenuHost("");
        using FixValueFlyoutHarness.OpenedFlyout open = FixValueFlyoutHarness.OpenStrip(
            MenuIds.ProceduresDepartFix,
            FixValueFlyoutHarness.Routed(),
            host
        );
        DepartFixFlyoutPanel panel = Assert.IsType<DepartFixFlyoutPanel>(open.Panel);
        panel.HeadingBox.Text = "200";
        panel.HeadingBox.Focus(NavigationMethod.Directional);

        open.Press(Key.Down);
        Assert.True(panel.RouteRows[0].IsKeyboardFocusWithin, "Down from the Heading box does not focus the ● row");
        open.Press(Key.Up);
        Assert.True(panel.Box.IsKeyboardFocusWithin, "Up from the top row does not return to the fix box");
        open.Press(Key.Down);
        open.Press(Key.Enter);

        Assert.Equal([(FixValueFlyoutHarness.Callsign, "DEPART SUNOL 200", FixValueFlyoutHarness.Initials)], host.Sent);
        Assert.False(open.Menu.IsOpen);
    }
}
