using Xunit;
using Yaat.Client.Services;
using Yaat.Client.Views.Scope;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// The Radar view's merged RUNDOWN rows: a channel column, then CRC's STARS coordination line
/// (<c>[time][line][+][callsign][type][beacon][category][exit fix][message]</c>), with the status's colour and blink.
/// </summary>
public class RundownRowsTests
{
    /// <summary>
    /// A row's time column: the channel column (<c>"PFAT "</c>) is the same width on every row, so the next five
    /// characters are the time.
    /// </summary>
    private static string TimeOf(RundownRow row) => row.Text[5..10];

    [Fact]
    public void Unsent_DrawsSteadyListText()
    {
        RundownRow row = Assert.Single(RundownRows.Build([List("PFAT", Item("Unsent", null))], 100));

        Assert.Equal("PFAT       7   SWA123 B738 0425 F SJC RWY 30", row.Text);
        Assert.Equal(RundownRowColor.ListText, row.Color);
        Assert.False(row.Blinks);
    }

    [Fact]
    public void Unacknowledged_DrawsBlinkingListText()
    {
        RundownRow row = Assert.Single(RundownRows.Build([List("PFAT", Item("Unacknowledged", null))], 100));

        Assert.Equal("PFAT       7   SWA123 B738 0425 F SJC RWY 30", row.Text);
        Assert.Equal(RundownRowColor.ListText, row.Color);
        Assert.True(row.Blinks);
    }

    [Fact]
    public void Acknowledged_DrawsSteadyCautionWithPlus()
    {
        RundownRow row = Assert.Single(RundownRows.Build([List("PFAT", Item("Acknowledged", 225))], 100));

        Assert.Equal("PFAT 2:05  7+  SWA123 B738 0425 F SJC RWY 30", row.Text);
        Assert.Equal(RundownRowColor.Caution, row.Color);
        Assert.False(row.Blinks);
    }

    [Fact]
    public void Recalled_DrawsBlinkingListTextWithRecall()
    {
        RundownRow row = Assert.Single(RundownRows.Build([List("PFAT", Item("Recalled", null))], 100));

        Assert.Equal("PFAT       7   SWA123 B738 0425 F SJC RECALL", row.Text);
        Assert.Equal(RundownRowColor.ListText, row.Color);
        Assert.True(row.Blinks);
    }

    [Fact]
    public void DepartureExpirationWarning_DrawsBlinkingCautionWithPlus()
    {
        RundownRow row = Assert.Single(RundownRows.Build([List("PFAT", Item("DepartureExpirationWarning", 225))], 100));

        Assert.Equal("PFAT 2:05  7+  SWA123 B738 0425 F SJC RWY 30", row.Text);
        Assert.Equal(RundownRowColor.Caution, row.Color);
        Assert.True(row.Blinks);
    }

    [Fact]
    public void VoidUnacknowledged_DrawsBlinkingListTextWithItsCountdown()
    {
        RundownRow row = Assert.Single(RundownRows.Build([List("PFAT", Item("VoidUnacknowledged", 225))], 100));

        Assert.Equal("PFAT 2:05  7   SWA123 B738 0425 F SJC RWY 30", row.Text);
        Assert.Equal(RundownRowColor.ListText, row.Color);
        Assert.True(row.Blinks);
    }

    [Fact]
    public void UnknownStatus_DrawsSteadyListText()
    {
        RundownRow row = Assert.Single(RundownRows.Build([List("PFAT", Item("Bogus", null))], 100));

        Assert.Equal("PFAT       7   SWA123 B738 0425 F SJC RWY 30", row.Text);
        Assert.Equal(RundownRowColor.ListText, row.Color);
        Assert.False(row.Blinks);
    }

    [Fact]
    public void Countdown_TruncatesAFractionalSecond()
    {
        RundownRow row = Assert.Single(RundownRows.Build([List("PFAT", Item("Acknowledged", 224.6))], 100));

        Assert.Equal("2:04 ", TimeOf(row));
    }

    [Fact]
    public void Countdown_ReadsZeroAtTheExpiry()
    {
        RundownRow row = Assert.Single(RundownRows.Build([List("PFAT", Item("Acknowledged", 100))], 100));

        Assert.Equal("0:00 ", TimeOf(row));
    }

    [Fact]
    public void Countdown_ReadsZeroPastTheExpiry()
    {
        RundownRow row = Assert.Single(RundownRows.Build([List("PFAT", Item("Acknowledged", 50))], 100));

        Assert.Equal("0:00 ", TimeOf(row));
    }

    [Fact]
    public void CountdownWithNoExpiry_ReadsZero()
    {
        RundownRow row = Assert.Single(RundownRows.Build([List("PFAT", Item("Acknowledged", null))], 100));

        Assert.Equal("0:00 ", TimeOf(row));
    }

    [Fact]
    public void ANullLineNumber_LeavesTwoSpaces()
    {
        var item = new CoordinationItemHubDto(
            Id: "POAK-1",
            Callsign: "SWA123",
            Status: "Unsent",
            LineNumber: null,
            AircraftType: "B738",
            Beacon: "0425",
            Category: "F",
            ExitFix: "SJC",
            Message: "RWY 30",
            OriginTcp: "3O",
            ExpireElapsedSeconds: null
        );

        RundownRow row = Assert.Single(RundownRows.Build([List("PFAT", item)], 100));

        Assert.Equal("PFAT           SWA123 B738 0425 F SJC RWY 30", row.Text);
    }

    [Fact]
    public void EmptyTypeBeaconCategoryAndExitFix_DrawOnlyTheirSeparators()
    {
        var item = new CoordinationItemHubDto(
            Id: "POAK-1",
            Callsign: "SWA123",
            Status: "Unsent",
            LineNumber: 7,
            AircraftType: "",
            Beacon: "",
            Category: "",
            ExitFix: "",
            Message: "RWY 30",
            OriginTcp: "3O",
            ExpireElapsedSeconds: null
        );

        RundownRow row = Assert.Single(RundownRows.Build([List("PFAT", item)], 100));

        Assert.Equal("PFAT       7   SWA123     RWY 30", row.Text);
    }

    [Fact]
    public void TwoLists_KeepTheDtoOrderAndPadTheChannelColumn()
    {
        IReadOnlyList<RundownRow> rows = RundownRows.Build(
            [List("PFAT", Item("Unsent", null, "POAK-1"), Item("Unsent", null, "POAK-2")), List("S", Item("Unsent", null, "POAK-3"))],
            100
        );

        Assert.Equal(["PFAT", "PFAT", "S"], rows.Select(r => r.ListId));
        Assert.Equal(["POAK-1", "POAK-2", "POAK-3"], rows.Select(r => r.ItemId));
        Assert.Equal("PFAT       7   SWA123 B738 0425 F SJC RWY 30", rows[0].Text);
        Assert.Equal("S          7   SWA123 B738 0425 F SJC RWY 30", rows[2].Text);
    }

    private static CoordinationItemHubDto Item(string status, double? expiresAt, string id = "POAK-1") =>
        new(
            Id: id,
            Callsign: "SWA123",
            Status: status,
            LineNumber: 7,
            AircraftType: "B738",
            Beacon: "0425",
            Category: "F",
            ExitFix: "SJC",
            Message: "RWY 30",
            OriginTcp: "3O",
            ExpireElapsedSeconds: expiresAt
        );

    private static CoordinationListHubDto List(string listId, params CoordinationItemHubDto[] items) =>
        new(ListId: listId, Title: "Oakland departure releases", AutoAcknowledge: false, SenderTcps: ["3O"], ReceiverTcps: ["4O"], Items: [.. items]);
}
