using Xunit;
using Yaat.Client.Services;
using Yaat.Client.Views.Scope;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// The right-click menu of one RUNDOWN entry: the coordination verbs, each enabled only for the statuses
/// CoordinationCommandHandler accepts it in, each sent as the position it acts as.
/// </summary>
public class RundownEntryMenuTests
{
    private static readonly string[] Headers = ["Acknowledge", "Release", "Recall", "Hold…", "Modify…", "Delete", "Move up", "Move down"];

    /// <summary>
    /// Each item's enabled state for an entry at index 2 of 3 with an origin and a receiver, as a mask in header order:
    /// Acknowledge, Release, Recall, Hold…, Modify…, Delete, Move up, Move down.
    /// </summary>
    [Theory]
    [InlineData("Unsent", "01101111")]
    [InlineData("Unacknowledged", "10100111")]
    [InlineData("Acknowledged", "00100111")]
    [InlineData("Recalled", "01010111")]
    [InlineData("DepartureExpirationWarning", "00100111")]
    [InlineData("VoidUnacknowledged", "00100111")]
    [InlineData("Bogus", "00000100")]
    public void Status_EnablesTheVerbsItAllows(string status, string mask)
    {
        (CoordinationListHubDto list, CoordinationItemHubDto item) = Middle(status);

        IReadOnlyList<RundownMenuItem> items = RundownEntryMenu.Items(list, item);

        Assert.Equal(Headers, items.Select(i => i.Header));
        Assert.Equal(mask, string.Concat(items.Select(i => i.IsEnabled ? '1' : '0')));
    }

    [Fact]
    public void Commands_NameTheActingTcpAndTheList()
    {
        (CoordinationListHubDto list, CoordinationItemHubDto item) = Middle("Unsent");

        IReadOnlyList<RundownMenuItem> items = RundownEntryMenu.Items(list, item);

        Assert.Equal(
            [
                "AS 2B RDACK PMRY",
                "AS 1Z RDH PMRY",
                "AS 1Z RDR PMRY",
                "AS 1Z RDH PMRY",
                "AS 1Z RDTXT /PMRY",
                "AS 1Z RDDEL PMRY",
                "AS 1Z RDPOS PMRY 1",
                "AS 1Z RDPOS PMRY 3",
            ],
            items.Select(i => i.Command)
        );
        Assert.Equal([false, false, false, true, true, false, false, false], items.Select(i => i.AsksForText));
        Assert.Equal([false, false, false, true, false, false, false, false], items.Select(i => i.SendsWithoutText));

        (CoordinationListHubDto recalledList, CoordinationItemHubDto recalled) = Middle("Recalled");
        RundownMenuItem release = RundownEntryMenu.Items(recalledList, recalled)[1];
        Assert.Equal("AS 1Z RD PMRY", release.Command);

        RundownMenuItem hold = items[3];
        RundownMenuItem modify = items[4];
        Assert.Equal("AS 1Z RDH PMRY RWY 30 ONLY", RundownEntryMenu.Compose(hold, "RWY 30 ONLY"));
        Assert.Equal("AS 1Z RDH PMRY", RundownEntryMenu.Compose(hold, "  "));
        Assert.Equal("AS 1Z RDTXT /PMRY RWY 30 ONLY", RundownEntryMenu.Compose(modify, "RWY 30 ONLY"));
        Assert.Null(RundownEntryMenu.Compose(modify, ""));
    }

    /// <summary>Modify… opens with the entry's message; every other item, Hold… among them, opens empty.</summary>
    [Fact]
    public void Modify_OpensWithTheEntrysMessage_OtherItemsOpenEmpty()
    {
        (CoordinationListHubDto list, CoordinationItemHubDto item) = Middle("Unsent");

        IReadOnlyList<RundownMenuItem> items = RundownEntryMenu.Items(list, item);

        Assert.Equal(["", "", "", "", "RWY 30", "", "", ""], items.Select(i => i.InitialText));
    }

    [Fact]
    public void NoReceiver_DisablesAcknowledge()
    {
        (CoordinationListHubDto list, CoordinationItemHubDto item) = Middle("Unacknowledged");
        list = list with { ReceiverTcps = [] };

        RundownMenuItem acknowledge = RundownEntryMenu.Items(list, item)[0];

        Assert.False(acknowledge.IsEnabled);
    }

    [Fact]
    public void NoOrigin_DisablesEverySenderItem()
    {
        (CoordinationListHubDto list, CoordinationItemHubDto item) = Middle("Unacknowledged");
        item = item with { OriginTcp = "" };
        list = list with { Items = [list.Items[0], item, list.Items[2]] };

        IReadOnlyList<RundownMenuItem> items = RundownEntryMenu.Items(list, item);

        Assert.True(items[0].IsEnabled);
        Assert.All(items.Skip(1), i => Assert.False(i.IsEnabled));
    }

    [Fact]
    public void FirstItem_DisablesMoveUp()
    {
        (CoordinationListHubDto list, _) = Middle("Unsent");

        IReadOnlyList<RundownMenuItem> items = RundownEntryMenu.Items(list, list.Items[0]);

        Assert.False(items[6].IsEnabled);
        Assert.True(items[7].IsEnabled);
        Assert.Equal("AS 1Z RDPOS PMRY 2", items[7].Command);
    }

    [Fact]
    public void LastItem_DisablesMoveDown()
    {
        (CoordinationListHubDto list, _) = Middle("Unsent");

        IReadOnlyList<RundownMenuItem> items = RundownEntryMenu.Items(list, list.Items[2]);

        Assert.True(items[6].IsEnabled);
        Assert.False(items[7].IsEnabled);
        Assert.Equal("AS 1Z RDPOS PMRY 2", items[6].Command);
    }

    /// <summary>A send that throws is logged by the click, never thrown out of it (the click does not await it).</summary>
    [Fact]
    public async Task Click_WhenTheSendThrows_DoesNotThrow()
    {
        (CoordinationListHubDto list, CoordinationItemHubDto item) = Middle("Unsent");
        RundownMenuItem recall = RundownEntryMenu.Items(list, item)[2];

        Task click = RundownEntryMenu.ClickAsync(
            recall,
            _ => throw new InvalidOperationException("the server is gone"),
            (_, _) => Task.FromResult<string?>(null)
        );
        await click;

        Assert.True(click.IsCompletedSuccessfully);
    }

    // List PMRY sent by 1Z to 2B, with the entry under test second of three.
    private static (CoordinationListHubDto List, CoordinationItemHubDto Item) Middle(string status)
    {
        CoordinationItemHubDto item = Item("PMRY-2", "SWA456", status);
        var list = new CoordinationListHubDto(
            ListId: "PMRY",
            Title: "Montgomery releases",
            AutoAcknowledge: false,
            SenderTcps: ["1Z"],
            ReceiverTcps: ["2B"],
            Items: [Item("PMRY-1", "SWA123", "Unsent"), item, Item("PMRY-3", "SWA789", "Unsent")]
        );
        return (list, item);
    }

    private static CoordinationItemHubDto Item(string id, string callsign, string status) =>
        new(id, callsign, status, 7, "B738", "0425", "F", "SJC", "RWY 30", "1Z", null);
}
