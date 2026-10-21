using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Initiate handoff… and Point out… flyouts (<see cref="CoordinationFlyout"/>, <c>HO</c> / <c>PO</c>): the staffed positions,
/// grouped by facility with the student's own first, the student's position on top unless it owns the track, the owner's row
/// greyed, the box's filter, Enter on the first match or on text no position matches, and the codes each verb sends.
/// </summary>
public class CoordinationFlyoutTests
{
    private const string StudentTcp = "4U";

    private static readonly OnlineControllerDto SfoDep = Controller("SFO_DEP", "Area D Sutro", "135.100", Nct(), "4U", "NorCal Approach", "4U");
    private static readonly OnlineControllerDto OakApp = Controller("OAK_APP", "Area B Woodside", "125.350", Nct(), "2B", "NorCal Approach", "2B");
    private static readonly OnlineControllerDto MryApp = Controller("MRY_APP", "Area A Seca", "133.000", Nct(), "1S", "NorCal Approach", "1S");
    private static readonly OnlineControllerDto OakTwr = Controller(
        "OAK_TWR",
        "LC1",
        "127.200",
        ("OAK", "Oakland ATCT", "Atct"),
        "3O",
        "Oakland Tower",
        "OAK_TWR"
    );
    private static readonly OnlineControllerDto MerTwr = Controller(
        "MER_TWR",
        "LC",
        "118.175",
        ("MER", "Castle ATCT", "Atct"),
        null,
        "Castle Tower",
        "MER_TWR"
    );
    private static readonly OnlineControllerDto Oak44 = Controller(
        "OAK_44_CTR",
        "Sector 44",
        "127.950",
        ("ZOA", "Oakland ARTCC", "Artcc"),
        null,
        "Oakland Center",
        "C44"
    );
    private static readonly OnlineControllerDto SuuSouth = Controller(
        "SUU_S_APP",
        "South",
        "119.900",
        ("SUU", "Travis RAPCON", "Tracon"),
        "1S",
        "Travis Approach",
        "SUU_S_APP"
    );
    private static readonly OnlineControllerDto SckTwrStandby = Controller(
        "SCK_TWR",
        "LC",
        "120.300",
        ("SCK", "Stockton ATCT", "Atct"),
        "3Z",
        "Stockton Tower",
        "3Z"
    ) with
    {
        IsActive = false,
    };

    /// <summary>The server's description of the student's position, SFO_DEP (NorCal 4U).</summary>
    private static readonly ActivePositionDto SfoDepActive = new(
        "SFO_DEP",
        "Area D Sutro",
        "NorCal Approach",
        "135.100",
        "NCT",
        "Northern California TRACON",
        "Tracon",
        "4U"
    );

    private static (string Id, string Name, string Type) Nct() => ("NCT", "Northern California TRACON", "Tracon");

    private static OnlineControllerDto Controller(
        string callsign,
        string name,
        string frequency,
        (string Id, string Name, string Type) facility,
        string? tcp,
        string radioName,
        string handoffCode
    ) => new(callsign, name, frequency, facility.Id, facility.Name, facility.Type, tcp, radioName, "YAAT", true, true, handoffCode);

    /// <summary>A track owned by Oakland Center sector 44.</summary>
    private static FakeMenuAircraft OwnedByCenter() =>
        new()
        {
            Callsign = "N123",
            Owner = "OAK_44_CTR",
            OwnerSectorCode = "C44",
        };

    /// <summary>A track owned by the student, NorCal 4U.</summary>
    private static FakeMenuAircraft OwnedByStudent() =>
        new()
        {
            Callsign = "N123",
            Owner = "SFO_DEP",
            OwnerSectorCode = StudentTcp,
        };

    private static RecordingMenuHost Host(string? activeTcp, params OnlineControllerDto[] controllers)
    {
        var host = new RecordingMenuHost("") { ActiveTcp = activeTcp };
        host.Controllers.AddRange(controllers);
        return host;
    }

    private static RecordingMenuHost Room() => Host(StudentTcp, SckTwrStandby, SuuSouth, Oak44, MerTwr, OakTwr, MryApp, OakApp, SfoDep);

    private static CoordinationFlyoutPanel Open(string id, IMenuAircraft aircraft, RecordingMenuHost host) =>
        JoinFlyoutHarness.Open<CoordinationFlyoutPanel>(id, aircraft, host);

    private static CoordinationFlyoutPanel OpenHandoff(IMenuAircraft aircraft, RecordingMenuHost host) =>
        Open(MenuIds.TrackInitiateHandoff, aircraft, host);

    private static CoordinationRow Row(CoordinationFlyoutPanel panel, string callsign) =>
        Assert.Single(panel.Rows, row => row.Position.Callsign == callsign);

    private static List<string> Shown(CoordinationFlyoutPanel panel) => [.. panel.Rows.Where(row => row.IsVisible).Select(row => row.Position.Code)];

    private static IEnumerable<string> Texts(object? content) =>
        content switch
        {
            TextBlock text => [text.Text ?? ""],
            Panel panel => panel.Children.SelectMany(child => Texts(child)),
            _ => [],
        };

    /// <summary>
    /// Rulings 1, 2 and 9, decision 2: the student's position on top (it does not own the track), then the staffed positions by
    /// facility, the student's facility first, the towers, the Center, the rest, each by code; a standby position is not listed.
    /// </summary>
    [AvaloniaFact]
    public void FreshOpen_StudentNotOwner_StudentOnTopThenFacilityGroups()
    {
        CoordinationFlyoutPanel panel = OpenHandoff(OwnedByCenter(), Room());

        Assert.Equal(
            [
                "Initiate handoff · N123 · owned by C44",
                "Hand off to — HO ···",
                "---",
                "  4U · Area D Sutro · 135.100 — HO 4U",
                "Northern California TRACON · NCT",
                "  1S · Area A Seca · 133.000 — HO 1S",
                "  2B · Area B Woodside · 125.350 — HO 2B",
                "Towers",
                "  3O · Oakland Tower · 127.200 — HO OAK_TWR",
                "  MER_TWR · Castle Tower · 118.175 — HO MER_TWR",
                "Oakland ARTCC · ZOA",
                "  C44 · Sector 44 · 127.950 — owns the track",
                "Travis RAPCON · SUU",
                "  1S · South · 119.900 — HO SUU_S_APP",
            ],
            panel.Lines
        );
    }

    /// <summary>Rulings 1 and 5: a student who owns the track keeps its row in its facility group, greyed, captioned as owner.</summary>
    [AvaloniaFact]
    public void FreshOpen_StudentOwnsTrack_StudentRowStaysInItsGroupGreyed()
    {
        CoordinationFlyoutPanel panel = OpenHandoff(OwnedByStudent(), Room());

        Assert.Equal(
            [
                "Northern California TRACON · NCT",
                "  1S · Area A Seca · 133.000 — HO 1S",
                "  2B · Area B Woodside · 125.350 — HO 2B",
                "  4U · Area D Sutro · 135.100 — owns the track",
                "Towers",
            ],
            panel.Lines.Skip(3).Take(5)
        );
        Assert.False(Row(panel, "SFO_DEP").IsEnabled);
    }

    /// <summary>
    /// Ruling 1 with the server's active position: a student the list does not hold still gets a named top row and its facility
    /// still comes first.
    /// </summary>
    [AvaloniaFact]
    public void FreshOpen_StudentNotListed_NamedTopRowAndItsFacilityFirst()
    {
        RecordingMenuHost host = Host(StudentTcp, SuuSouth, Oak44, OakApp);
        host.ActivePosition = SfoDepActive;

        CoordinationFlyoutPanel panel = OpenHandoff(OwnedByCenter(), host);

        Assert.Equal(
            [
                "  4U · Area D Sutro · 135.100 — HO 4U",
                "Northern California TRACON · NCT",
                "  2B · Area B Woodside · 125.350 — HO 2B",
                "Oakland ARTCC · ZOA",
                "  C44 · Sector 44 · 127.950 — owns the track",
                "Travis RAPCON · SUU",
                "  1S · South · 119.900 — HO SUU_S_APP",
            ],
            panel.Lines.Skip(3)
        );
    }

    /// <summary>Ruling 1 and fix: an unlisted student who owns the track gets no top row; its row sits greyed in its facility group.</summary>
    [AvaloniaFact]
    public void FreshOpen_StudentNotListedOwnsTrack_RowGreyedInItsGroup()
    {
        RecordingMenuHost host = Host(StudentTcp, OakApp);
        host.ActivePosition = SfoDepActive;

        CoordinationFlyoutPanel panel = OpenHandoff(OwnedByStudent(), host);

        Assert.Equal(
            ["Northern California TRACON · NCT", "  2B · Area B Woodside · 125.350 — HO 2B", "  4U · Area D Sutro · 135.100 — owns the track"],
            panel.Lines.Skip(3)
        );
    }

    /// <summary>
    /// A student on NCT's 1S while Travis's 1S is staffed: the student's row is NCT's, the one whose handoff code is the student's
    /// TCP, on top, and NCT's group comes first.
    /// </summary>
    [AvaloniaFact]
    public void FreshOpen_StudentOnSharedTcp_NctRowOnTopAndNctFirst()
    {
        CoordinationFlyoutPanel panel = OpenHandoff(OwnedByCenter(), Host("1S", SuuSouth, MryApp, OakApp));

        Assert.Equal(
            [
                "  1S · Area A Seca · 133.000 — HO 1S",
                "Northern California TRACON · NCT",
                "  2B · Area B Woodside · 125.350 — HO 2B",
                "Travis RAPCON · SUU",
                "  1S · South · 119.900 — HO SUU_S_APP",
            ],
            panel.Lines.Skip(3)
        );
    }

    /// <summary>
    /// A student switched to Travis's South on the shared 1S: the server's active position names it by its handoff code, so the
    /// student's row is Travis's, on top, and Travis's group comes first, ahead of NCT's 1S.
    /// </summary>
    [AvaloniaFact]
    public void FreshOpen_StudentSwitchedToTravisOnSharedTcp_TravisRowOnTopAndTravisFirst()
    {
        OnlineControllerDto suuNorth = Controller("SUU_N_APP", "North", "120.000", ("SUU", "Travis RAPCON", "Tracon"), "1N", "Travis Approach", "1N");
        RecordingMenuHost host = Host("1S", MryApp, SuuSouth, suuNorth, OakApp);
        host.ActivePosition = new ActivePositionDto(
            "SUU_S_APP",
            "South",
            "Travis Approach",
            "119.900",
            "SUU",
            "Travis RAPCON",
            "Tracon",
            "SUU_S_APP"
        );

        CoordinationFlyoutPanel panel = OpenHandoff(OwnedByCenter(), host);

        Assert.Equal(
            [
                "  1S · South · 119.900 — HO SUU_S_APP",
                "Travis RAPCON · SUU",
                "  1N · North · 120.000 — HO 1N",
                "Northern California TRACON · NCT",
                "  1S · Area A Seca · 133.000 — HO 1S",
                "  2B · Area B Woodside · 125.350 — HO 2B",
            ],
            panel.Lines.Skip(3)
        );
    }

    /// <summary>An ERAM owner whose sector code is not the row's handoff code (it was ambiguous) is greyed by its callsign.</summary>
    [AvaloniaFact]
    public void Owner_EramWithAmbiguousCode_GreyedByCallsign()
    {
        CoordinationFlyoutPanel panel = OpenHandoff(OwnedByCenter(), Host(StudentTcp, SfoDep, Oak44 with { HandoffCode = "OAK_44_CTR" }));

        Assert.True(Row(panel, "OAK_44_CTR").Position.IsOwner);
    }

    /// <summary>A track owned by Travis's 1S greys only Travis's row, not NCT's 1S.</summary>
    [AvaloniaFact]
    public void Owner_SharedTcp_GreysOnlyTheOwnersRow()
    {
        var aircraft = new FakeMenuAircraft
        {
            Callsign = "N123",
            Owner = "SUU_S_APP",
            OwnerSectorCode = "1S",
        };

        CoordinationFlyoutPanel panel = OpenHandoff(aircraft, Room());

        Assert.True(Row(panel, "SUU_S_APP").Position.IsOwner);
        Assert.False(Row(panel, "MRY_APP").Position.IsOwner);
    }

    /// <summary>The keyboard: Down from the box focuses the first row, Up from it returns to the box, Enter on a row sends it.</summary>
    [AvaloniaFact]
    public void Keyboard_WalksTheRows_EnterSendsTheFocusedRow()
    {
        RecordingMenuHost host = Room();
        using JoinFlyoutHarness.OpenedFlyout<CoordinationFlyoutPanel> open = JoinFlyoutHarness.OpenStrip<CoordinationFlyoutPanel>(
            MenuIds.TrackInitiateHandoff,
            OwnedByCenter(),
            host
        );
        Assert.True(open.Panel.Box.IsKeyboardFocusWithin, "the box does not hold focus at open");

        open.Press(Key.Down);
        Assert.True(open.Panel.Rows[0].IsKeyboardFocusWithin, "Down from the box does not focus the first row");
        open.Press(Key.Up);
        Assert.True(open.Panel.Box.IsKeyboardFocusWithin, "Up from the top row does not return to the box");

        open.Press(Key.Down);
        open.Press(Key.Down);
        open.Press(Key.Enter);

        Assert.Equal([(FixValueFlyoutHarness.Callsign, "HO 1S", FixValueFlyoutHarness.Initials)], host.Sent);
    }

    /// <summary>Ruling 5: the owner's row is greyed and a click on it sends nothing.</summary>
    [AvaloniaFact]
    public void OwnerRow_IsGreyedAndNotClickable()
    {
        RecordingMenuHost host = Room();
        CoordinationFlyoutPanel panel = OpenHandoff(OwnedByCenter(), host);
        CoordinationRow owner = Row(panel, "OAK_44_CTR");

        JoinFlyoutHarness.Click(owner);

        Assert.True(owner.Position.IsOwner);
        Assert.False(owner.IsEnabled);
        Assert.Null(owner.SentCommand);
        Assert.Empty(host.Sent);
    }

    /// <summary>Decision 5: an owner the list does not hold greys no row.</summary>
    [AvaloniaFact]
    public void OwnerNotListed_NoRowGreyed()
    {
        var aircraft = new FakeMenuAircraft
        {
            Callsign = "N123",
            Owner = "SAC_APP",
            OwnerSectorCode = "2F",
        };

        CoordinationFlyoutPanel panel = OpenHandoff(aircraft, Room());

        Assert.DoesNotContain(panel.Rows, row => row.Position.IsOwner);
    }

    /// <summary>Ruling 8 and decision 4: Point out lists only positions with a TCP and sends the TCP.</summary>
    [AvaloniaFact]
    public void PointOut_OmitsRowsWithoutTcp_AndSendsTheTcp()
    {
        RecordingMenuHost host = Room();
        CoordinationFlyoutPanel panel = Open(MenuIds.TrackPointOut, OwnedByCenter(), host);

        Assert.Equal(
            [
                "Point out · N123 · owned by C44",
                "Point out to — PO ···",
                "---",
                "  4U · Area D Sutro · 135.100 — PO 4U",
                "Northern California TRACON · NCT",
                "  1S · Area A Seca · 133.000 — PO 1S",
                "  2B · Area B Woodside · 125.350 — PO 2B",
                "Towers",
                "  3O · Oakland Tower · 127.200 — PO 3O",
                "Travis RAPCON · SUU",
                "  1S · South · 119.900 — PO 1S",
            ],
            panel.Lines
        );

        JoinFlyoutHarness.Click(Row(panel, "OAK_TWR"));

        Assert.Equal("PO 3O", Assert.Single(host.Sent).Command);
    }

    /// <summary>Ruling 7 and decision 4: an Initiate handoff row shows the TCP and sends the server's handoff code.</summary>
    [AvaloniaFact]
    public void Handoff_RowClick_SendsTheHandoffCode()
    {
        RecordingMenuHost host = Room();
        CoordinationFlyoutPanel panel = OpenHandoff(OwnedByCenter(), host);

        JoinFlyoutHarness.Click(Row(panel, "SUU_S_APP"));

        Assert.Equal("1S", Row(panel, "SUU_S_APP").Position.Code);
        Assert.Equal("HO SUU_S_APP", Assert.Single(host.Sent).Command);
    }

    /// <summary>Decision 3: the box filters on the prefix of the code, the name or a word of it, the callsign and the frequency.</summary>
    [AvaloniaTheory]
    [InlineData("1s", new[] { "1S", "1S" })]
    [InlineData("castle", new[] { "MER_TWR" })]
    [InlineData("seca", new[] { "1S" })]
    [InlineData("oak", new[] { "2B", "3O", "C44" })]
    [InlineData("suu", new[] { "1S" })]
    [InlineData("127", new[] { "3O", "C44" })]
    public void Box_FiltersOnCodeNameCallsignAndFrequency(string typed, string[] shown)
    {
        CoordinationFlyoutPanel panel = OpenHandoff(OwnedByCenter(), Room());

        panel.Box.Text = typed;

        Assert.Equal(shown, Shown(panel));
    }

    /// <summary>Decision 3: the first match that can be sent is outlined and Enter in the box sends it.</summary>
    [AvaloniaFact]
    public void Enter_SendsTheFirstMatch()
    {
        RecordingMenuHost host = Room();
        CoordinationFlyoutPanel panel = OpenHandoff(OwnedByCenter(), host);

        panel.Box.Text = "oak";

        Assert.Equal("Hand off to — HO 2B", panel.Lines[1]);
        Assert.Contains("▸ 2B · Area B Woodside · 125.350 — HO 2B", panel.Lines);
        JoinFlyoutHarness.Enter(panel.Box);
        Assert.Equal("HO 2B", Assert.Single(host.Sent).Command);
    }

    /// <summary>Ruling 3: Enter on text no listed position matches sends it upper-cased as typed, with the box's note and the list's line.</summary>
    [AvaloniaFact]
    public void Enter_NoMatch_SendsTheTypedTextUpperCased()
    {
        RecordingMenuHost host = Room();
        CoordinationFlyoutPanel panel = OpenHandoff(OwnedByCenter(), host);

        panel.Box.Text = "3s";

        Assert.Equal(
            [
                "Initiate handoff · N123 · owned by C44",
                "Hand off to — HO 3S",
                CoordinationFlyout.NotStaffed,
                "---",
                "No staffed position matches 3S. Enter sends HO 3S as typed; the sim answers after the flyout closes.",
            ],
            panel.Lines
        );
        JoinFlyoutHarness.Enter(panel.Box);
        Assert.Equal("HO 3S", Assert.Single(host.Sent).Command);
    }

    /// <summary>Ruling 9: the list is read as the flyout opens; a change while it is open does not alter its rows.</summary>
    [AvaloniaFact]
    public void List_ReadOnceAtOpen_ChangeWhileOpenIgnored()
    {
        RecordingMenuHost host = Host(StudentTcp, SfoDep, OakApp);
        CoordinationFlyoutPanel panel = OpenHandoff(OwnedByCenter(), host);
        IReadOnlyList<string> opened = [.. panel.Lines];

        host.Controllers.Add(OakTwr);
        host.Clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(opened, panel.Lines);
    }

    /// <summary>Ruling 6: a row shows the code, the name (a tower's radio name) and the frequency, never the callsign.</summary>
    [AvaloniaFact]
    public void RowLabel_ShowsCodeNameAndFrequency_NoCallsign()
    {
        CoordinationFlyoutPanel panel = OpenHandoff(OwnedByCenter(), Room());

        Assert.Equal(["2B", "Area B Woodside", "125.350", "HO 2B"], Texts(Row(panel, "OAK_APP").Content));
        Assert.Equal(["3O", "Oakland Tower", "127.200", "HO OAK_TWR"], Texts(Row(panel, "OAK_TWR").Content));
    }
}
