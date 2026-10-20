using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
/// The PTAC flyout (<see cref="PtacFlyout"/>, <c>PTAC</c> / <c>PTACF</c>): the PTAC / Forced toggle, the Heading and Altitude
/// boxes (empty sends <c>PH</c> / <c>PA</c>, the placeholders naming what those are now) and the approach rows, the expected
/// approach first. The aircraft flies to KOAK, whose CIFP holds the ILS 28R (<c>I28R</c>).
/// </summary>
public class PtacFlyoutTests
{
    private const string Destination = "KOAK";

    public PtacFlyoutTests() => TestVnasData.EnsureInitialized();

    private static FakeMenuAircraft Inbound() =>
        new()
        {
            Callsign = FixValueFlyoutHarness.Callsign,
            Position = FixValueFlyoutHarness.OakVor(),
            Destination = Destination,
            ExpectedApproach = "I28R",
            HeadingDegrees = 250,
            AltitudeFeet = 3000,
        };

    private static PtacFlyoutPanel Open(IMenuAircraft? aircraft, RecordingMenuHost host) =>
        JoinFlyoutHarness.Open<PtacFlyoutPanel>(MenuIds.ProceduresPtac, aircraft, host);

    private static PtacRow Row(PtacFlyoutPanel panel, string id) => Assert.Single(panel.Rows, row => row.ApproachId == id);

    /// <summary>The one command <paramref name="host"/> recorded, run through the sim's parser.</summary>
    private static PositionTurnAltitudeClearanceCommand ParsedSend(RecordingMenuHost host)
    {
        (string _, string command, string _) = Assert.Single(host.Sent);
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(command);
        Assert.True(parsed.IsSuccess, $"{command}: {parsed.Reason}");
        return Assert.IsType<PositionTurnAltitudeClearanceCommand>(parsed.Value);
    }

    /// <summary>
    /// A fresh open, as text: the title and its right side, the toggle, each box with its placeholder and token, the group line,
    /// the rows.
    /// </summary>
    [AvaloniaFact]
    public void FreshOpen_Lines_LayOutTheFlyout()
    {
        PtacFlyoutPanel panel = Open(Inbound(), new RecordingMenuHost(""));

        Assert.Equal(
            [
                "PTAC · hdg 250 · 3,000",
                "[PTAC PTAC] | Forced PTACF",
                "Heading [] PH (present 250) — PH",
                "Altitude [] PA (present 3,000) — PA",
                "---",
                "Approach",
                "● The expected approach · I28R expected — PTAC PH PA",
            ],
            panel.Lines.Take(7)
        );
        Assert.Equal(panel.Rows.Skip(1).Select(row => row.Line), panel.Lines.Skip(7));
        Assert.Contains("  ILS RWY 28R · ILS — PTAC PH PA I28R", panel.Lines);

        panel.HeadingBox.Text = "280";
        panel.AltitudeBox.Text = "025";
        Assert.Equal("Heading [280] PH (present 250) — 280", panel.Lines[2]);
        Assert.Equal("Altitude [025] PA (present 3,000) — 025", panel.Lines[3]);
    }

    /// <summary>The title shows the present heading and altitude, never the assigned ones.</summary>
    [AvaloniaFact]
    public void Title_ShowsThePresentHeadingAndAltitude_WithAssignedOnes()
    {
        PtacFlyoutPanel panel = Open(
            Inbound() with
            {
                AssignedAltitude = 4000,
                AssignedHeading = new MagneticHeading(270),
            },
            new RecordingMenuHost("")
        );

        Assert.Equal("hdg 250 · 3,000", panel.Now);
        Assert.Equal("PA (assigned 4,000)", panel.AltitudeBox.PlaceholderText);
    }

    [AvaloniaFact]
    public void ExpectedRow_IsFirst_SendsPtacPhPa()
    {
        var host = new RecordingMenuHost("");
        PtacFlyoutPanel panel = Open(Inbound(), host);

        PtacRow first = panel.Rows[0];
        Assert.Null(first.ApproachId);
        Assert.True(first.IsExpected);
        Assert.Equal(PtacFlyout.ExpectedRowName, first.ApproachName);
        Assert.Equal("I28R expected", first.Note);
        Assert.Equal("PTAC PH PA", first.SentCommand);
        Assert.Equal("PTAC PH PA I28R", Row(panel, "I28R").SentCommand);
        Assert.False(Row(panel, "I28R").IsExpected);
        Assert.Equal(1, panel.Rows.Count(row => row.IsExpected));

        JoinFlyoutHarness.Click(first);

        Assert.Equal([(FixValueFlyoutHarness.Callsign, "PTAC PH PA", FixValueFlyoutHarness.Initials)], host.Sent);
        PositionTurnAltitudeClearanceCommand parsed = ParsedSend(host);
        Assert.Null(parsed.MagneticHeading);
        Assert.Null(parsed.Altitude);
        Assert.Null(parsed.ApproachId);
        Assert.False(parsed.Forced);
    }

    /// <summary>Without an expected approach the list starts at the destination's first approach and no row is marked.</summary>
    [AvaloniaFact]
    public void NoExpectedApproach_ListStartsAtTheChoices_NoRowMarked()
    {
        PtacFlyoutPanel panel = Open(Inbound() with { ExpectedApproach = null }, new RecordingMenuHost(""));

        Assert.NotEmpty(panel.Rows);
        Assert.NotNull(panel.Rows[0].ApproachId);
        Assert.DoesNotContain(panel.Rows, row => row.IsExpected);
    }

    [AvaloniaFact]
    public void TypedHeadingAndAltitude_RowSendsThem_TheParserReads()
    {
        var host = new RecordingMenuHost("");
        PtacFlyoutPanel panel = Open(Inbound(), host);

        panel.HeadingBox.Text = "280";
        panel.AltitudeBox.Text = "025";
        Assert.Equal("PTAC 280 025 I28R", Row(panel, "I28R").ShownCommand);
        JoinFlyoutHarness.Click(Row(panel, "I28R"));

        Assert.Equal([(FixValueFlyoutHarness.Callsign, "PTAC 280 025 I28R", FixValueFlyoutHarness.Initials)], host.Sent);
        PositionTurnAltitudeClearanceCommand parsed = ParsedSend(host);
        Assert.Equal(280, parsed.MagneticHeading?.Degrees);
        Assert.Equal(2500, parsed.Altitude);
        Assert.Equal("I28R", parsed.ApproachId);
        Assert.False(parsed.Forced);
    }

    [AvaloniaFact]
    public void Forced_SendsPtacf_ShowsTheCaption_ColoursTheCommands()
    {
        var host = new RecordingMenuHost("");
        PtacFlyoutPanel panel = Open(Inbound(), host);
        Assert.False(panel.ForcedCaption.IsVisible);
        Assert.True(panel.PtacToggle.IsChecked);

        JoinFlyoutHarness.Click(panel.ForcedToggle);

        Assert.True(panel.IsForced);
        Assert.True(panel.ForcedToggle.IsChecked);
        Assert.False(panel.PtacToggle.IsChecked);
        Assert.True(panel.ForcedCaption.IsVisible);
        Assert.Equal("skip the intercept-angle check", panel.ForcedCaption.Text);
        Assert.All(panel.Rows, row => Assert.Same(ApproachRowView.ForcedCommand, row.CommandForeground));
        JoinFlyoutHarness.Click(Row(panel, "I28R"));
        Assert.Equal([(FixValueFlyoutHarness.Callsign, "PTACF PH PA I28R", FixValueFlyoutHarness.Initials)], host.Sent);
        PositionTurnAltitudeClearanceCommand parsed = ParsedSend(host);
        Assert.True(parsed.Forced);
        Assert.Equal("I28R", parsed.ApproachId);

        JoinFlyoutHarness.Click(panel.PtacToggle);
        Assert.False(panel.ForcedCaption.IsVisible);
        Assert.All(panel.Rows, row => Assert.NotSame(ApproachRowView.ForcedCommand, row.CommandForeground));
    }

    /// <summary>
    /// The Heading placeholder reads the assigned heading while the aircraft flies one (its navigation route empty), else the
    /// present heading, an aircraft with an assigned heading that navigates to a fix included.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(true, "", "PH (assigned 270)")]
    [InlineData(false, "", "PH (present 250)")]
    [InlineData(true, "SUNOL", "PH (present 250)")]
    public void HeadingPlaceholder_AssignedOrPresent(bool assigned, string navigatingTo, string placeholder)
    {
        FakeMenuAircraft aircraft = Inbound() with
        {
            AssignedHeading = assigned ? new MagneticHeading(270) : null,
            NavigatingTo = navigatingTo,
            NavigationRoute = (navigatingTo.Length > 0) ? [navigatingTo] : [],
        };

        PtacFlyoutPanel panel = Open(aircraft, new RecordingMenuHost(""));

        Assert.Equal(placeholder, panel.HeadingBox.PlaceholderText);
    }

    [AvaloniaTheory]
    [InlineData(4000.0, "PA (assigned 4,000)")]
    [InlineData(null, "PA (present 3,000)")]
    [InlineData(21000.0, "PA (assigned FL210)")]
    public void AltitudePlaceholder_AssignedOrPresent(double? assigned, string placeholder)
    {
        PtacFlyoutPanel panel = Open(Inbound() with { AssignedAltitude = assigned }, new RecordingMenuHost(""));

        Assert.Equal(placeholder, panel.AltitudeBox.PlaceholderText);
    }

    [AvaloniaTheory]
    [InlineData("000")]
    [InlineData("361")]
    [InlineData("abc")]
    public void InvalidHeading_SendsNothing_ShowsTheRefusal(string typed)
    {
        var host = new RecordingMenuHost("");
        PtacFlyoutPanel panel = Open(Inbound(), host);

        panel.HeadingBox.Text = typed;
        JoinFlyoutHarness.Enter(panel.HeadingBox);
        JoinFlyoutHarness.Click(Row(panel, "I28R"));

        Assert.True(panel.HeadingError.IsVisible);
        Assert.Equal(FixValueFlyout.InvalidHeading, panel.HeadingError.Text);
        Assert.Null(panel.BoxCommand);
        Assert.All(panel.Rows, row => Assert.Equal("PTAC ···", row.ShownCommand));
        Assert.All(panel.Rows, row => Assert.Null(row.SentCommand));
        Assert.Empty(host.Sent);
    }

    [AvaloniaFact]
    public void InvalidAltitude_SendsNothing_ShowsTheRefusal()
    {
        var host = new RecordingMenuHost("");
        PtacFlyoutPanel panel = Open(Inbound(), host);

        panel.AltitudeBox.Text = "x5";
        JoinFlyoutHarness.Enter(panel.AltitudeBox);
        JoinFlyoutHarness.Click(Row(panel, "I28R"));

        Assert.True(panel.AltitudeError.IsVisible);
        Assert.Equal(AltitudeFlyout.InvalidAltitude, panel.AltitudeError.Text);
        Assert.Empty(host.Sent);

        panel.AltitudeBox.Text = "";
        Assert.False(panel.AltitudeError.IsVisible);
    }

    [AvaloniaFact]
    public void EnterInABox_SendsTheExpectedRow()
    {
        var host = new RecordingMenuHost("");
        PtacFlyoutPanel panel = Open(Inbound(), host);

        panel.HeadingBox.Text = "280";
        panel.AltitudeBox.Text = "30";
        JoinFlyoutHarness.Enter(panel.AltitudeBox);

        Assert.Equal([(FixValueFlyoutHarness.Callsign, "PTAC 280 030", FixValueFlyoutHarness.Initials)], host.Sent);
        Assert.Equal("PTAC 280 030", panel.Rows[0].SentCommand);
        PositionTurnAltitudeClearanceCommand parsed = ParsedSend(host);
        Assert.Equal(280, parsed.MagneticHeading?.Degrees);
        Assert.Equal(3000, parsed.Altitude);
        Assert.Null(parsed.ApproachId);
    }

    /// <summary>With no expected approach Enter still sends no approach id, so the sim resolves it; it never picks the first row.</summary>
    [AvaloniaFact]
    public void EnterInABox_WithoutAnExpectedApproach_SendsNoApproachId()
    {
        var host = new RecordingMenuHost("");
        PtacFlyoutPanel panel = Open(Inbound() with { ExpectedApproach = null }, host);

        JoinFlyoutHarness.Enter(panel.HeadingBox);

        Assert.Equal([(FixValueFlyoutHarness.Callsign, "PTAC PH PA", FixValueFlyoutHarness.Initials)], host.Sent);
        Assert.Null(ParsedSend(host).ApproachId);
    }

    [AvaloniaTheory]
    [InlineData("", "No destination filed")]
    [InlineData("ZZZZ", "No approaches at ZZZZ")]
    public void NoApproaches_ShowsTheNote_EnterSendsNothing(string destination, string note)
    {
        var host = new RecordingMenuHost("");
        PtacFlyoutPanel panel = Open(Inbound() with { Destination = destination }, host);

        Assert.Empty(panel.Rows);
        Assert.True(panel.EmptyNote.IsVisible);
        Assert.Equal(note, panel.EmptyNote.Text);
        Assert.Contains(note, panel.Lines);
        JoinFlyoutHarness.Enter(panel.HeadingBox);

        Assert.Null(panel.BoxCommand);
        Assert.Empty(host.Sent);
    }

    [AvaloniaFact]
    public void Refresh_FollowsAHeadingChange()
    {
        var aircraft = new AircraftModel
        {
            Callsign = FixValueFlyoutHarness.Callsign,
            Position = FixValueFlyoutHarness.OakVor(),
            Destination = Destination,
            Heading = new TrueHeading(100),
        };
        PtacFlyoutPanel panel = Open(aircraft, new RecordingMenuHost(""));
        string before = panel.HeadingBox.PlaceholderText ?? "";
        Assert.Equal($"PH (present {HoldFlyout.Three(HeadingFlyout.WholeHeading(aircraft.MagneticHeadingDegrees))})", before);

        aircraft.Heading = new TrueHeading(200);
        panel.Refresh();

        Assert.NotEqual(before, panel.HeadingBox.PlaceholderText);
        Assert.Equal(
            $"PH (present {HoldFlyout.Three(HeadingFlyout.WholeHeading(aircraft.MagneticHeadingDegrees))})",
            panel.HeadingBox.PlaceholderText
        );
        Assert.StartsWith($"hdg {HoldFlyout.Three(HeadingFlyout.WholeHeading(aircraft.MagneticHeadingDegrees))} · ", panel.Now);
    }

    /// <summary>
    /// The Heading box holds focus at open; Down walks onto the rows, Up from the top row returns to the Altitude box, Enter sends
    /// the focused row.
    /// </summary>
    [AvaloniaFact]
    public void Keyboard_WalksTheRows_EnterSendsTheFocusedRow()
    {
        var host = new RecordingMenuHost("");
        using JoinFlyoutHarness.OpenedFlyout<PtacFlyoutPanel> open = JoinFlyoutHarness.OpenStrip<PtacFlyoutPanel>(
            MenuIds.ProceduresPtac,
            Inbound(),
            host
        );
        PtacFlyoutPanel panel = open.Panel;
        Assert.True(panel.HeadingBox.IsKeyboardFocusWithin, "the Heading box does not hold focus at open");

        open.Press(Key.Down);
        Assert.True(panel.Rows[0].IsKeyboardFocusWithin, "Down from the box does not focus the first row");
        open.Press(Key.Down);
        Assert.True(panel.Rows[1].IsKeyboardFocusWithin, "Down does not walk the rows");
        open.Press(Key.Up);
        open.Press(Key.Up);
        Assert.True(panel.AltitudeBox.IsKeyboardFocusWithin, "Up from the top row does not return to the Altitude box");

        open.Press(Key.Down);
        open.Press(Key.Down);
        string sent = Assert.IsType<string>(panel.Rows[1].SentCommand);
        open.Press(Key.Enter);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([(FixValueFlyoutHarness.Callsign, sent, FixValueFlyoutHarness.Initials)], host.Sent);
        Assert.Equal(panel.Rows[1].ApproachId, ParsedSend(host).ApproachId);
        Assert.False(open.Menu.IsOpen);
    }
}
