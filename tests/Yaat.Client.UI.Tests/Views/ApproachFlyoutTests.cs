using Avalonia;
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
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Situation;
using Yaat.Sim.Testing;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Cleared approach / Expect approach flyout's model (<see cref="ApproachFlyoutModel"/>) on real navigation data: tabs in
/// the room's runway order then Circling, a visual row heading each runway tab, the default row pinned above the tabs, short
/// runways dimmed, visual rows greyed until the field is reported unless Force approach is on, the airport-level visual row at
/// an untowered field, and Cancel IFR for an IFR aircraft airborne below FL180.
/// </summary>
public class ApproachFlyoutTests
{
    private const string Callsign = "UAL455";
    private const string Initials = "AB";

    /// <summary>KOAK 30's tab: first, as the room's arrival ends go first in the room's order (30, then 28R).</summary>
    private const int Thirty = 0;

    /// <summary>KOAK with the room landing 30 (both ways) and 28R (arrivals), towered.</summary>
    private static RecordingMenuHost KoakHost(params string[] towered) =>
        new("")
        {
            RoomActiveRunways = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["OAK"] = ["30", "A28R"] },
            ToweredAirports = towered,
        };

    private static ApproachFlyoutModel Model(string entryId, IMenuAircraft aircraft, IMenuHost host) => Model(entryId, aircraft, host, false);

    private static ApproachFlyoutModel Model(string entryId, IMenuAircraft aircraft, IMenuHost host, bool solo) =>
        ApproachFlyoutModel.Build(
            MenuCatalog.ApproachSpec(entryId),
            aircraft,
            TestMenuContext.Create(Callsign, Initials, null, solo, VfrCommandsForIfr.None),
            host
        );

    private static int TabIndex(ApproachFlyoutModel model, string runway) => model.Tabs.ToList().FindIndex(tab => tab.Runway == runway);

    // --- Tabs ----------------------------------------------------------------------------------

    /// <summary>
    /// An A320 to KOAK with the room's runways set <c>30, A28R</c>: its arrival ends first in the room's order (30, then 28R),
    /// then the rest as the pattern-entry list orders them (landable first, short last); each tab counts its rows, the visual
    /// row included, and 15 and 33 hold only their visual row.
    /// </summary>
    [Fact]
    public void Koak_TabsFollowTheRoomsArrivalEndsInTheRoomsOrder_ThenTheRest()
    {
        TestVnasData.EnsureInitialized();

        ApproachFlyoutModel model = Model(MenuIds.ApproachCleared, Arrival("KOAK"), KoakHost("OAK"));

        Assert.Equal(["30 5", "28R 5", "10R 3", "12 5", "28L 3", "10L 2", "15 1", "33 1"], model.Tabs.Select(tab => tab.Label));
        Assert.Equal(0, model.SelectedTab);
        Assert.Empty(model.Pinned(force: false));
        Assert.Equal(
            [
                "Visual RWY 30 · field not in sight — CVA 30",
                "ILS RWY 30 — CAPP I30",
                "LOC RWY 30 — CAPP L30",
                "RNAV (GPS) Y RWY 30 — CAPP R30-Y",
                "RNAV (RNP) Z RWY 30 — CAPP H30-Z",
            ],
            model.Rows(Thirty, force: false).Select(row => row.ToString())
        );
    }

    /// <summary>Told to expect the ILS 30 with 30 assigned, the default row is pinned above the tabs and its tab is selected.</summary>
    [Fact]
    public void DefaultApproach_IsPinnedFirst_ItsTabSelected_TheExpectedRowBadgedInPlace()
    {
        TestVnasData.EnsureInitialized();
        FakeMenuAircraft aircraft = Arrival("KOAK") with { AssignedRunway = "28R", ExpectedApproach = "I30" };

        ApproachFlyoutModel model = Model(MenuIds.ApproachCleared, aircraft, KoakHost("OAK"));

        ApproachRow pinned = Assert.Single(model.Pinned(force: false));
        Assert.Equal("Cleared ILS 30 · default — CAPP I30", pinned.ToString());
        Assert.Equal(TabIndex(model, "30"), model.SelectedTab);
        Assert.Equal("ILS RWY 30 · expected — CAPP I30", model.Rows(model.SelectedTab, force: false)[1].ToString());
    }

    /// <summary>With no expected approach, the default is the assigned runway's first instrument approach (28R's ILS).</summary>
    [Fact]
    public void DefaultApproach_WithoutAnExpectation_IsTheAssignedRunwaysFirstApproach()
    {
        TestVnasData.EnsureInitialized();

        ApproachFlyoutModel model = Model(MenuIds.ApproachCleared, Arrival("KOAK") with { AssignedRunway = "28R" }, KoakHost("OAK"));

        Assert.Equal("Cleared ILS 28R · default — CAPP I28R", Assert.Single(model.Pinned(force: false)).ToString());
        Assert.Equal(TabIndex(model, "28R"), model.SelectedTab);
    }

    /// <summary>An expected visual (<c>VIS28R</c>) makes that runway's visual row the default.</summary>
    [Fact]
    public void DefaultApproach_ExpectedVisual_IsThatRunwaysVisualRow()
    {
        TestVnasData.EnsureInitialized();
        FakeMenuAircraft aircraft = Arrival("KOAK") with { ExpectedApproach = "VIS28R", SituationFlags = SituationFlags.HasReportedFieldInSight };

        ApproachFlyoutModel model = Model(MenuIds.ApproachCleared, aircraft, KoakHost("OAK"));

        Assert.Equal("Cleared visual 28R · default — CVA 28R", Assert.Single(model.Pinned(force: false)).ToString());
        Assert.Equal(TabIndex(model, "28R"), model.SelectedTab);
        Assert.Equal("Visual RWY 28R · expected — CVA 28R", model.Rows(model.SelectedTab, force: false)[0].ToString());
    }

    /// <summary>KTRK's circling RNAV (GPS)-A gets the last tab, labelled Circling with its count, and no visual row.</summary>
    [Fact]
    public void CirclingApproaches_GetTheLastTab()
    {
        TestVnasData.EnsureInitialized();

        ApproachFlyoutModel model = Model(
            MenuIds.ApproachCleared,
            Arrival("KTRK") with
            {
                BaseAircraftType = "C68A",
                FiledAircraftType = "C68A",
            },
            KoakHost("TRK")
        );

        ApproachFlyoutTab last = model.Tabs[^1];
        Assert.Equal("Circling 1", last.Label);
        Assert.Equal(["RNAV (GPS)-A — CAPP RNV-A"], model.Rows(model.Tabs.Count - 1, force: false).Select(row => row.ToString()));
        Assert.All(model.Tabs.SkipLast(1), tab => Assert.Equal(ApproachRowKind.Visual, model.Rows(model.Tabs.ToList().IndexOf(tab), false)[0].Kind));
    }

    // --- Row states ----------------------------------------------------------------------------

    /// <summary>KOAK 10L (5,336 ft) is short for an A320 (5,433 ft needed): its visual and its RNAV row are both dimmed "short".</summary>
    [Fact]
    public void ShortRunway_DimsEveryRowOnItsTab_InstrumentRowsIncluded()
    {
        TestVnasData.EnsureInitialized();
        FakeMenuAircraft aircraft = Arrival("KOAK") with { SituationFlags = SituationFlags.HasReportedFieldInSight };

        ApproachFlyoutModel model = Model(MenuIds.ApproachCleared, aircraft, KoakHost("OAK"));

        IReadOnlyList<ApproachRow> rows = model.Rows(TabIndex(model, "10L"), force: false);
        Assert.Equal(["Visual RWY 10L · short — CVA 10L", "RNAV (GPS) RWY 10L · short — CAPP R10L"], rows.Select(row => row.ToString()));
        Assert.All(rows, row => Assert.True(row.IsShort && row.IsEnabled));
        Assert.All(model.Rows(TabIndex(model, "30"), force: false), row => Assert.False(row.IsShort));
    }

    [Fact]
    public void VisualRow_GreyedUntilTheFieldIsReported_LiveOnceItIs()
    {
        TestVnasData.EnsureInitialized();

        ApproachRow before = Model(MenuIds.ApproachCleared, Arrival("KOAK"), KoakHost("OAK")).Rows(Thirty, force: false)[0];
        FakeMenuAircraft reported = Arrival("KOAK") with { SituationFlags = SituationFlags.HasReportedFieldInSight };
        ApproachRow after = Model(MenuIds.ApproachCleared, reported, KoakHost("OAK")).Rows(Thirty, force: false)[0];

        Assert.False(before.IsEnabled);
        Assert.Equal("field not in sight", before.NotInSight);
        Assert.Equal("Field not in sight — issue RFIS first", before.Tooltip);
        Assert.True(after.IsEnabled);
        Assert.Null(after.NotInSight);
        Assert.Equal("CVA 30", after.Command);
    }

    [Fact]
    public void ForceOn_TurnsEveryRowForced_AndEnablesTheGreyedVisualRow()
    {
        TestVnasData.EnsureInitialized();

        ApproachFlyoutModel model = Model(MenuIds.ApproachCleared, Arrival("KOAK") with { ExpectedApproach = "I30" }, KoakHost("OAK"));

        Assert.True(model.OffersForce);
        IReadOnlyList<ApproachRow> rows = model.Rows(Thirty, force: true);
        Assert.Equal("Visual RWY 30 · force — CVAF 30", rows[0].ToString());
        Assert.True(rows[0].IsEnabled);
        Assert.Equal("ILS RWY 30 · expected · force — CAPPF I30", rows[1].ToString());
        Assert.All(rows, row => Assert.True(row.IsForced));
        Assert.Equal("Cleared ILS 30 · default · force — CAPPF I30", Assert.Single(model.Pinned(force: true)).ToString());
        Assert.Equal("CAPPF L30", model.BoxCommand(Thirty, "l3", force: true));
        Assert.Equal("CAPPF ZZ9", model.BoxCommand(Thirty, "zz9", force: true));
    }

    [Fact]
    public void SoloTraining_OffersNoForceToggle()
    {
        TestVnasData.EnsureInitialized();

        Assert.False(Model(MenuIds.ApproachCleared, Arrival("KOAK"), KoakHost("OAK"), true).OffersForce);
    }

    // --- The Approach box ----------------------------------------------------------------------

    [Fact]
    public void Box_SendsTheTopMatchOfTheTabsRows_ElseTheVerbWithTheText_EmptySendsNothing()
    {
        TestVnasData.EnsureInitialized();
        ApproachFlyoutModel model = Model(MenuIds.ApproachCleared, Arrival("KOAK"), KoakHost("OAK"));

        Assert.Null(model.BoxCommand(Thirty, "  ", force: false));
        Assert.Equal("CAPP R30-Y", model.BoxCommand(Thirty, "r", force: false));
        Assert.Equal("CAPP ILS28R", model.BoxCommand(Thirty, "ils28r", force: false));
        // The greyed visual row is never the match.
        Assert.Equal("CAPP VIS", model.BoxCommand(Thirty, "vis", force: false));
        Assert.Equal(["ILS RWY 30 — CAPP I30"], model.Rows(Thirty, force: false).Where(row => row.Matches("I")).Select(row => row.ToString()));
    }

    // --- Untowered fields ----------------------------------------------------------------------

    /// <summary>KHES has no instrument approach: an untowered KHES gives the airport-level visual row and Cancel IFR, nothing else.</summary>
    [Fact]
    public void Untowered_NoInstrumentApproaches_GivesTheAirportRowAndCancelIfrOnly()
    {
        TestVnasData.EnsureInitialized();
        FakeMenuAircraft aircraft = Arrival("KHES") with { BaseAircraftType = "C172", FiledAircraftType = "C172" };

        ApproachFlyoutModel model = Model(MenuIds.ApproachCleared, aircraft, KoakHost("OAK"));

        Assert.Equal(
            ["Visual approach to KHES · pilot picks the runway · field not in sight — CVA KHES"],
            model.Pinned(false).Select(row => row.ToString())
        );
        Assert.Empty(model.Tabs);
        Assert.False(model.HasBox);
        Assert.Equal(CancelIfrPlacement.Bottom, model.CancelIfr?.Placement);
        Assert.Equal(
            "Cancel IFR for UAL455? The aircraft becomes VFR: no further approach clearances, and VFR pattern entry applies.",
            model.CancelIfr?.Question
        );
        Assert.Equal("Visual approach to KHES · force · pilot picks the runway — CVAF KHES", Assert.Single(model.Pinned(force: true)).ToString());
    }

    [Fact]
    public void Untowered_WithApproaches_PinsTheAirportRow_ToweredHasNone()
    {
        TestVnasData.EnsureInitialized();

        ApproachFlyoutModel untowered = Model(MenuIds.ApproachCleared, Arrival("KOAK"), KoakHost());
        ApproachFlyoutModel towered = Model(MenuIds.ApproachCleared, Arrival("KOAK"), KoakHost("KOAK"));

        Assert.Equal(ApproachRowKind.Airport, Assert.Single(untowered.Pinned(false)).Kind);
        Assert.Equal("CVA KOAK", untowered.Pinned(false)[0].Command);
        Assert.NotEmpty(untowered.Tabs);
        Assert.Empty(towered.Pinned(false));
        Assert.Equal(CancelIfrPlacement.Bottom, towered.CancelIfr?.Placement);
    }

    // --- Expect approach -----------------------------------------------------------------------

    [Fact]
    public void Expect_RowsSendEapp_VisualRowsNeverGreyed_NoForce_NoAirportRow()
    {
        TestVnasData.EnsureInitialized();

        ApproachFlyoutModel model = Model(MenuIds.ApproachExpect, Arrival("KOAK"), KoakHost());

        IReadOnlyList<ApproachRow> rows = model.Rows(Thirty, force: false);
        Assert.Equal("Visual RWY 30 — EAPP VIS30", rows[0].ToString());
        Assert.True(rows[0].IsEnabled);
        Assert.Equal("ILS RWY 30 — EAPP I30", rows[1].ToString());
        Assert.False(model.OffersForce);
        Assert.Empty(model.Pinned(false));
        Assert.Equal("EAPP ZZ9", model.BoxCommand(Thirty, "zz9", force: false));
    }

    // --- The six other verbs -------------------------------------------------------------------

    /// <summary>
    /// Each of the six other approach verbs: its title, the default row's label and the command its rows and box send, and
    /// whether its rows are forced.
    /// </summary>
    public static TheoryData<string, string, string, string, bool> OtherVerbs() =>
        new()
        {
            { MenuIds.ApproachJoin, "Join approach", "JAPP", "Join ILS 30", false },
            { MenuIds.ApproachClearedStraightIn, "Cleared straight-in", "CAPPSI", "Cleared straight-in ILS 30", false },
            { MenuIds.ApproachJoinStraightIn, "Join straight-in", "JAPPSI", "Join straight-in ILS 30", false },
            { MenuIds.ApproachClearedForce, "Cleared approach (force)", "CAPPF", "Cleared ILS 30 (force)", true },
            { MenuIds.ApproachJoinForce, "Join approach (force)", "JAPPF", "Join ILS 30 (force)", true },
            { MenuIds.ApproachJoinFinalCourse, "Join final approach course", "JFAC", "Join final course ILS 30", false },
        };

    /// <summary>Each of the six other approach verbs and the command its rows and box send.</summary>
    public static TheoryData<string, string> OtherVerbCommands() =>
        new()
        {
            { MenuIds.ApproachJoin, "JAPP" },
            { MenuIds.ApproachClearedStraightIn, "CAPPSI" },
            { MenuIds.ApproachJoinStraightIn, "JAPPSI" },
            { MenuIds.ApproachClearedForce, "CAPPF" },
            { MenuIds.ApproachJoinForce, "JAPPF" },
            { MenuIds.ApproachJoinFinalCourse, "JFAC" },
        };

    /// <summary>
    /// Told to expect the ILS 30 with 30 assigned at an untowered KOAK: the verb's default row above the tabs, one tab per runway
    /// with an instrument approach in the room's order (no visual rows, so 15 and 33 get none), 10L dimmed short, no airport
    /// row, no Force toggle and no Cancel IFR; the force pair's rows are all forced.
    /// </summary>
    [Theory]
    [MemberData(nameof(OtherVerbs))]
    public void OtherVerb_DefaultRowAndInstrumentTabsOnly_ForcedRowsForTheForcePair(
        string id,
        string title,
        string verb,
        string defaultLabel,
        bool forced
    )
    {
        TestVnasData.EnsureInitialized();
        FakeMenuAircraft aircraft = Arrival("KOAK") with { AssignedRunway = "30", ExpectedApproach = "I30" };

        ApproachFlyoutModel model = Model(id, aircraft, KoakHost());

        string mark = forced ? " · force" : "";
        Assert.Equal(title, model.Title);
        Assert.Equal($"{defaultLabel} · default{mark} — {verb} I30", Assert.Single(model.Pinned(force: false)).ToString());
        Assert.Equal(["30 4", "28R 4", "10R 2", "12 4", "28L 2", "10L 1"], model.Tabs.Select(tab => tab.Label));
        Assert.Equal(Thirty, model.SelectedTab);
        Assert.Equal(
            [$"ILS RWY 30 · expected{mark} — {verb} I30", $"LOC RWY 30{mark} — {verb} L30"],
            model.Rows(Thirty, force: false).Take(2).Select(row => row.ToString())
        );
        IEnumerable<ApproachRow> rows = Enumerable.Range(0, model.Tabs.Count).SelectMany(tab => model.Rows(tab, force: false));
        Assert.All(rows, row => Assert.Equal(ApproachRowKind.Instrument, row.Kind));
        Assert.All(rows, row => Assert.Equal(forced, row.IsForced));
        Assert.All(model.Rows(TabIndex(model, "10L"), force: false), row => Assert.True(row.IsShort));
        Assert.False(model.OffersForce);
        Assert.Null(model.CancelIfr);
        Assert.True(model.HasBox);
    }

    /// <summary>KTRK's circling RNAV (GPS)-A is the last tab of every verb's flyout too.</summary>
    [Theory]
    [MemberData(nameof(OtherVerbCommands))]
    public void OtherVerb_CirclingApproaches_GetTheLastTab(string id, string verb)
    {
        TestVnasData.EnsureInitialized();
        FakeMenuAircraft aircraft = Arrival("KTRK") with { BaseAircraftType = "C68A", FiledAircraftType = "C68A" };

        ApproachFlyoutModel model = Model(id, aircraft, KoakHost("TRK"));

        Assert.Equal("Circling 1", model.Tabs[^1].Label);
        Assert.Equal([$"{verb} RNV-A"], model.Rows(model.Tabs.Count - 1, force: false).Select(row => row.Command));
    }

    /// <summary>The box sends the tab's top match, else the verb with the typed id, with or without Force; empty sends nothing.</summary>
    [Theory]
    [MemberData(nameof(OtherVerbCommands))]
    public void OtherVerb_Box_SendsTheVerbWithTheTopMatch(string id, string verb)
    {
        TestVnasData.EnsureInitialized();

        ApproachFlyoutModel model = Model(id, Arrival("KOAK"), KoakHost());

        Assert.Null(model.BoxCommand(Thirty, "  ", force: false));
        Assert.Equal($"{verb} L30", model.BoxCommand(Thirty, "l3", force: false));
        Assert.Equal($"{verb} ZZ9", model.BoxCommand(Thirty, "zz9", force: false));
        Assert.Equal($"{verb} ZZ9", model.BoxCommand(Thirty, "zz9", force: true));
        Assert.Equal(verb, model.BoxVerb(force: true));
    }

    /// <summary>A destination with no approaches gives the six the box alone, with no Cancel IFR.</summary>
    [Theory]
    [MemberData(nameof(OtherVerbCommands))]
    public void OtherVerb_UnknownDestination_GivesTheBoxOnly(string id, string verb)
    {
        TestVnasData.EnsureInitialized();

        ApproachFlyoutModel model = Model(id, Arrival("ZZZZ"), KoakHost());

        Assert.Empty(model.Pinned(false));
        Assert.Empty(model.Tabs);
        Assert.True(model.HasBox);
        Assert.Null(model.CancelIfr);
        Assert.Equal($"{verb} I30", model.BoxCommand(model.SelectedTab, "i30", force: false));
    }

    /// <summary>
    /// All Commands › Approach › each of the six opens its flyout: the title line names the verb (the force pair's with the
    /// force mark), no Force toggle, the default row sends the verb's command and the box shows it.
    /// </summary>
    [AvaloniaTheory]
    [MemberData(nameof(OtherVerbs))]
    public void OtherVerb_Entry_OpensItsFlyoutUnderItsTitleLine(string id, string title, string verb, string defaultLabel, bool forced)
    {
        TestVnasData.EnsureInitialized();
        RecordingMenuHost host = KoakHost("OAK");

        ApproachFlyoutPanel panel = OpenEntry(id, Arrival("KOAK") with { ExpectedApproach = "I30" }, host);

        Assert.Equal(forced ? $"{title} · force" : title, panel.Lines[0]);
        Assert.Null(panel.ForceToggle);
        Assert.Null(panel.CancelIfrRow);
        Assert.StartsWith(defaultLabel, panel.PinnedRows[0].Content?.ToString(), StringComparison.Ordinal);
        TextBox box = Assert.IsType<TextBox>(panel.Box);
        box.Text = "l3";
        Assert.Equal($"{verb} L30", MenuCommandText.GetCommand(box));
        panel.PinnedRows[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal([(Callsign, $"{verb} I30", Initials)], host.Sent);
    }

    /// <summary>Cleared approach's and Expect approach's flyouts open under a title line naming the verb, Cleared's with Force.</summary>
    [AvaloniaTheory]
    [InlineData(MenuIds.ApproachCleared, "Cleared approach · Force")]
    [InlineData(MenuIds.ApproachExpect, "Expect approach")]
    public void ClearedAndExpect_OpenUnderATitleLine(string id, string titleLine)
    {
        TestVnasData.EnsureInitialized();

        ApproachFlyoutPanel panel = OpenEntry(id, Arrival("KOAK"), KoakHost("OAK"));

        Assert.Equal(titleLine, panel.Lines[0]);
    }

    // --- Cancel IFR ----------------------------------------------------------------------------

    [Theory]
    [InlineData("IFR", 5000, false, true)]
    [InlineData("VFR", 5000, false, false)]
    [InlineData("IFR", 18000, false, false)]
    [InlineData("IFR", 0, true, false)]
    public void CancelIfr_OnlyForAnIfrAircraftAirborneBelowFl180(string rules, double altitude, bool onGround, bool offered)
    {
        TestVnasData.EnsureInitialized();
        FakeMenuAircraft aircraft = Arrival("KOAK") with { FlightRules = rules, AltitudeFeet = altitude, IsOnGround = onGround };

        Assert.Equal(offered, Model(MenuIds.ApproachCleared, aircraft, KoakHost("OAK")).CancelIfr is not null);
    }

    /// <summary>A towered destination with no instrument approach puts Cancel IFR first, above the runway tabs.</summary>
    [Fact]
    public void CancelIfr_IsFirst_WhenTheDestinationHasNoInstrumentApproach()
    {
        TestVnasData.EnsureInitialized();

        ApproachFlyoutModel model = Model(
            MenuIds.ApproachCleared,
            Arrival("KHES") with
            {
                BaseAircraftType = "C172",
                FiledAircraftType = "C172",
            },
            KoakHost("HES")
        );

        Assert.Equal(CancelIfrPlacement.Top, model.CancelIfr?.Placement);
        Assert.Empty(model.Pinned(false));
        Assert.Equal(["13 1", "31 1"], model.Tabs.Select(tab => tab.Label));
        Assert.True(model.HasBox);
    }

    // --- Expected approaches, odd destinations ------------------------------------------------

    /// <summary>A shorthand or lower-case expected id resolves to its approach: the default row, its tab and the in-tab badge.</summary>
    [Theory]
    [InlineData("R28L", "Cleared RNAV Y 28L · default — CAPP R28LY", "28L", "RNAV (GPS) Y RWY 28L · expected — CAPP R28LY")]
    [InlineData("r28ry", "Cleared RNAV Y 28R · default — CAPP R28RY", "28R", "RNAV (GPS) Y RWY 28R · expected — CAPP R28RY")]
    public void ExpectedShorthand_FeedsTheDefaultRow(string expected, string pinned, string runway, string row)
    {
        TestVnasData.EnsureInitialized();

        ApproachFlyoutModel model = Model(MenuIds.ApproachCleared, Arrival("KOAK") with { ExpectedApproach = expected }, KoakHost("OAK"));

        Assert.Equal(pinned, Assert.Single(model.Pinned(force: false)).ToString());
        Assert.Equal(TabIndex(model, runway), model.SelectedTab);
        Assert.Contains(row, model.Rows(model.SelectedTab, force: false).Select(r => r.ToString()));
    }

    /// <summary>An expected approach KOAK does not publish (I33) badges nothing; the default falls through to the assigned runway.</summary>
    [Fact]
    public void ExpectedApproachNotTheDestinations_FallsThroughToTheAssignedRunway()
    {
        TestVnasData.EnsureInitialized();
        Assert.DoesNotContain(NavigationDatabase.Instance.GetApproaches("KOAK"), a => a.ApproachId == "I33");

        ApproachFlyoutModel model = Model(
            MenuIds.ApproachCleared,
            Arrival("KOAK") with
            {
                AssignedRunway = "28R",
                ExpectedApproach = "I33",
            },
            KoakHost("OAK")
        );

        Assert.Equal("Cleared ILS 28R · default — CAPP I28R", Assert.Single(model.Pinned(force: false)).ToString());
        Assert.All(Enumerable.Range(0, model.Tabs.Count), tab => Assert.All(model.Rows(tab, false), row => Assert.NotEqual("expected", row.Badge)));
    }

    /// <summary>A destination the navigation data has no runways or approaches for gets the box (and Cancel IFR) only, untowered or not.</summary>
    [Fact]
    public void UnknownDestination_GivesTheBoxOnly()
    {
        TestVnasData.EnsureInitialized();

        ApproachFlyoutModel model = Model(MenuIds.ApproachCleared, Arrival("ZZZZ"), KoakHost());

        Assert.Empty(model.Pinned(false));
        Assert.Empty(model.Tabs);
        Assert.True(model.HasBox);
        Assert.Equal("CAPP I30", model.BoxCommand(model.SelectedTab, "i30", force: false));
        Assert.Equal(CancelIfrPlacement.Top, model.CancelIfr?.Placement);
    }

    [Theory]
    [InlineData("KVNY", "Circling 3", "CAPP LDA-C")]
    [InlineData("KAVX", "Circling 2", "CAPP VDM-B")]
    [InlineData("KAAS", "Circling 1", "CAPP VDM-A")]
    [InlineData("KMFR", "Circling 3", "CAPP LBC-B")]
    public void CirclingApproaches_GetTheLastTab_AtOtherAirports(string airport, string label, string command)
    {
        TestVnasData.EnsureInitialized();

        ApproachFlyoutModel model = Model(MenuIds.ApproachCleared, Arrival(airport), KoakHost(airport));

        Assert.Equal(label, model.Tabs[^1].Label);
        Assert.Contains(command, model.Rows(model.Tabs.Count - 1, force: false).Select(row => row.Command));
    }

    // --- VFR aircraft --------------------------------------------------------------------------

    /// <summary>
    /// The sim clears only an IFR aircraft for a visual approach: a VFR one's Cleared approach has no visual or airport row,
    /// forced or not.
    /// </summary>
    [Fact]
    public void Vfr_ClearedApproach_HasNoVisualOrAirportRows()
    {
        TestVnasData.EnsureInitialized();

        ApproachFlyoutModel model = Model(MenuIds.ApproachCleared, Arrival("KOAK") with { FlightRules = "VFR" }, KoakHost());

        Assert.Empty(model.Pinned(false));
        Assert.NotEmpty(model.Tabs);
        Assert.DoesNotContain(model.Tabs, tab => tab.Runway is "15" or "33");
        foreach (bool force in new[] { false, true })
        {
            IEnumerable<ApproachRow> rows = Enumerable.Range(0, model.Tabs.Count).SelectMany(tab => model.Rows(tab, force));
            Assert.All(rows, row => Assert.Equal(ApproachRowKind.Instrument, row.Kind));
        }
    }

    [Fact]
    public void Vfr_ExpectApproach_KeepsItsVisualRows()
    {
        TestVnasData.EnsureInitialized();

        ApproachFlyoutModel model = Model(MenuIds.ApproachExpect, Arrival("KOAK") with { FlightRules = "VFR" }, KoakHost());

        Assert.Equal("Visual RWY 30 — EAPP VIS30", model.Rows(Thirty, force: false)[0].ToString());
    }

    // --- The grey rule -------------------------------------------------------------------------

    /// <summary>A row waits for its own report: the field for a visual row, the traffic for a FOLLOW row; none for the rest.</summary>
    [Theory]
    [InlineData(ApproachRowSight.Field, SituationFlags.None, "field not in sight", ApproachFlyoutModel.FieldNotInSightRefusal)]
    [InlineData(ApproachRowSight.Field, SituationFlags.HasReportedTrafficInSight, "field not in sight", ApproachFlyoutModel.FieldNotInSightRefusal)]
    [InlineData(ApproachRowSight.Field, SituationFlags.HasReportedFieldInSight, null, null)]
    [InlineData(
        ApproachRowSight.Traffic,
        SituationFlags.HasReportedFieldInSight,
        "traffic not in sight",
        ApproachFlyoutModel.TrafficNotInSightRefusal
    )]
    [InlineData(ApproachRowSight.Traffic, SituationFlags.HasReportedTrafficInSight, null, null)]
    [InlineData(ApproachRowSight.None, SituationFlags.None, null, null)]
    public void Waiting_GreysARowUntilItsOwnReportIsIn(ApproachRowSight needs, SituationFlags flags, string? note, string? refusal) =>
        Assert.Equal((note, refusal), ApproachFlyoutModel.Waiting(needs, flags));

    /// <summary>The greyed rows' tooltips quote the sim's refusals word for word.</summary>
    [Fact]
    public void Refusals_AreTheSimsWording()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while ((root is not null) && !File.Exists(Path.Combine(root.FullName, "yaat.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        string handler = File.ReadAllText(Path.Combine(root.FullName, "src", "Yaat.Sim", "Commands", "ApproachCommandHandler.cs"));
        Assert.Contains($"\"{ApproachFlyoutModel.FieldNotInSightRefusal}\"", handler, StringComparison.Ordinal);
        Assert.Contains($"\"{ApproachFlyoutModel.TrafficNotInSightRefusal}\"", handler, StringComparison.Ordinal);
    }

    /// <summary>A reopened flyout looks as it first opened: Force approach off, the box empty, the opening tab selected.</summary>
    [AvaloniaFact]
    public void Reopen_ResetsForceTheBoxAndTheTab()
    {
        TestVnasData.EnsureInitialized();
        MenuContext context = TestMenuContext.Create(Callsign, Initials, null, false, VfrCommandsForIfr.None);
        MenuItem entry = Assert.IsType<MenuItem>(MenuCatalog.Get(MenuIds.ApproachCleared).Build(Arrival("KOAK"), context, KoakHost("OAK")));
        entry.IsSubMenuOpen = true;
        ApproachFlyoutPanel panel = Assert.IsType<ApproachFlyoutPanel>(
            Assert.IsType<ApproachFlyoutHost>(Assert.Single(entry.Items.OfType<MenuItem>()).Header).Child
        );
        int opening = panel.Strip.SelectedIndex;
        panel.ForceToggle!.IsChecked = true;
        panel.Strip.Select(opening + 1);
        panel.Box!.Text = "zz";

        entry.IsSubMenuOpen = false;
        entry.IsSubMenuOpen = true;

        Assert.False(panel.IsForced);
        Assert.Equal("", panel.Box.Text);
        Assert.Equal(opening, panel.Strip.SelectedIndex);
        Assert.All(panel.Rows, row => Assert.True(row.IsVisible));
    }

    // --- The panel ----------------------------------------------------------------------------

    /// <summary>All Commands › Approach › Cleared approach opens the same flyout: the default row pinned, its tab selected; a row sends.</summary>
    [AvaloniaFact]
    public void AllCommandsEntry_OpensTheFlyout_ARowSendsItsCommand()
    {
        TestVnasData.EnsureInitialized();
        RecordingMenuHost host = KoakHost("OAK");

        ApproachFlyoutPanel panel = OpenEntry(MenuIds.ApproachCleared, Arrival("KOAK") with { ExpectedApproach = "I30" }, host);

        Assert.Equal("Cleared ILS 30 · default — CAPP I30", panel.PinnedRows[0].Content?.ToString());
        Assert.Equal("30 5", panel.Strip.Tabs[panel.Strip.SelectedIndex].Label);
        Assert.False(panel.Rows[0].IsEnabled);
        Assert.Equal(
            "Visual RWY 30 · field not in sight — CVA 30 [disabled]",
            panel.Lines.First(line => line.StartsWith("Visual RWY 30", StringComparison.Ordinal))
        );
        panel.Rows[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal([(Callsign, "CAPP I30", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void ForceToggle_RerendersTheRowsInPlace_KeepingTheTab()
    {
        TestVnasData.EnsureInitialized();
        RecordingMenuHost host = KoakHost("OAK");
        ApproachFlyoutPanel panel = OpenEntry(MenuIds.ApproachCleared, Arrival("KOAK") with { ExpectedApproach = "I30" }, host);
        int tab = panel.Strip.Tabs.ToList().FindIndex(t => t.Label.StartsWith("28R ", StringComparison.Ordinal));
        panel.Strip.Select(tab);
        Button visual = panel.Rows[0];

        Assert.NotNull(panel.ForceToggle);
        panel.ForceToggle.IsChecked = true;

        Assert.Same(visual, panel.Rows[0]);
        Assert.True(visual.IsEnabled);
        Assert.Equal("CVAF 28R", MenuCommandText.GetCommand(visual));
        Assert.Equal("CAPPF I30", MenuCommandText.GetCommand(panel.PinnedRows[0]));
        Assert.Equal(tab, panel.Strip.SelectedIndex);
        visual.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal([(Callsign, "CVAF 28R", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void Box_FiltersTheTabsRows_AndSendsItsCommand()
    {
        TestVnasData.EnsureInitialized();
        RecordingMenuHost host = KoakHost("OAK");
        ApproachFlyoutPanel panel = OpenEntry(MenuIds.ApproachExpect, Arrival("KOAK") with { AssignedRunway = "30" }, host);
        TextBox box = Assert.IsType<TextBox>(panel.Box);

        Assert.Null(panel.ForceToggle);
        box.Text = "l3";

        Assert.Equal(["LOC RWY 30 — EAPP L30"], panel.Rows.Where(row => row.IsVisible).Select(row => row.Content?.ToString()));
        Assert.Equal("EAPP L30", MenuCommandText.GetCommand(box));
    }

    [AvaloniaFact]
    public void CancelIfr_OpensTheConfirmStrip_BackFoldsIt_ConfirmSendsCifr()
    {
        TestVnasData.EnsureInitialized();
        RecordingMenuHost host = KoakHost("OAK");
        ApproachFlyoutPanel panel = OpenEntry(MenuIds.ApproachCleared, Arrival("KOAK"), host);
        Button row = Assert.IsType<Button>(panel.CancelIfrRow);
        Border strip = Assert.IsType<Border>(panel.CancelIfrStrip);

        row.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(strip.IsVisible);
        panel.CancelIfrBack!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(strip.IsVisible);
        Assert.Empty(host.Sent);

        row.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        panel.CancelIfrConfirm!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal([(Callsign, "CIFR", Initials)], host.Sent);
    }

    /// <summary>
    /// The strip's Cleared approach flyout opens with focus on the pinned default row; Down goes into the selected tab's first
    /// live row (past its greyed visual row), Right selects the next tab and focuses its first live row, Down past the last row
    /// reaches the Approach box, and Escape closes the flyout, leaving the menu open.
    /// </summary>
    [AvaloniaFact]
    public void Keyboard_DefaultRowFocused_DownIntoTheTab_RightSwitchesTabs_DownReachesTheBox_EscapeCloses()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel ac = MenuGoldenFixtures.For(MenuView.Radar).Single(f => f.Name == "approach-ifr").Aircraft;
        RecordingMenuHost host = KoakHost("OAK");
        var window = new Window { Width = 900, Height = 900 };
        window.Show();
        ContextMenu menu = AircraftMenuBuilder.Build(ac, new MenuClick(ac.Callsign, null, null, []), host, _ => []);
        menu.Open(window);
        Dispatcher.UIThread.RunJobs();
        menu.UpdateLayout();
        Button button = QuickCommandStrip
            .Buttons(Assert.Single(menu.Items.OfType<MenuItem>(), QuickCommandStrip.IsStrip))
            .Single(b => (string)b.Tag! == MenuIds.ApproachCleared);
        QuickCommandStrip.OpenSubmenu(button);
        Dispatcher.UIThread.RunJobs();
        MenuFlyout flyout = Assert.IsType<MenuFlyout>(FlyoutBase.GetAttachedFlyout(button));
        Layout(flyout);
        ApproachFlyoutPanel panel = Panel(flyout);

        Assert.Same(panel.PinnedRows[0], FocusedButton(flyout));
        Assert.Equal("Cleared ILS 30 · default — CAPP I30", panel.PinnedRows[0].Content?.ToString());

        Press(flyout, Key.Down);
        Assert.False(panel.Rows[0].IsEnabled);
        Assert.Same(panel.Rows[1], FocusedButton(flyout));

        int opening = panel.Strip.SelectedIndex;
        Press(flyout, Key.Right);
        Assert.Equal(opening + 1, panel.Strip.SelectedIndex);
        List<Button> live = [.. panel.Rows.Where(row => row.IsEnabled)];
        Assert.Same(live[0], FocusedButton(flyout));

        for (int step = 1; step < live.Count; step++)
        {
            Press(flyout, Key.Down);
        }

        Assert.Same(live[^1], FocusedButton(flyout));
        Press(flyout, Key.Down);
        Assert.True(panel.Box?.IsKeyboardFocusWithin, "Down past the last row does not reach the Approach box.");

        // A tab with no live row (15: only its greyed visual row) keeps the arrows working through the scroller.
        panel.Strip.Select(panel.Strip.Tabs.ToList().FindIndex(tab => tab.Label == "15 1"));
        Layout(flyout);
        Assert.Same(panel.Scroll, Focused(flyout));
        Press(flyout, Key.Down);
        Assert.True(panel.Box?.IsKeyboardFocusWithin, "Down from a tab with no live row does not reach the Approach box.");
        Press(flyout, Key.Up);
        Assert.Same(panel.Scroll, Focused(flyout));
        Press(flyout, Key.Up);
        Assert.Same(panel.PinnedRows[0], FocusedButton(flyout));
        Press(flyout, Key.Escape);
        Assert.False(flyout.IsOpen);
        Assert.True(menu.IsOpen);
        Assert.Empty(host.Sent);
    }

    private static void Layout(MenuFlyout flyout)
    {
        (flyout.Popup.Child as Control)?.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        (flyout.Popup.Child as Control)?.UpdateLayout();
    }

    /// <summary>Presses <paramref name="key"/> on whatever holds focus in the flyout.</summary>
    private static void Press(MenuFlyout flyout, Key key)
    {
        Control focused = Assert.IsAssignableFrom<Control>(Focused(flyout));
        focused.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
        Layout(flyout);
    }

    private static IInputElement? Focused(MenuFlyout flyout) =>
        TopLevel.GetTopLevel(Assert.IsAssignableFrom<Visual>(flyout.Popup.Child))?.FocusManager?.GetFocusedElement();

    /// <summary>The row button holding the flyout's focus, or null when focus is on no row.</summary>
    private static Button? FocusedButton(MenuFlyout flyout) =>
        (Focused(flyout) as Visual)?.GetSelfAndVisualAncestors().OfType<Button>().FirstOrDefault();

    /// <summary>The flyout panel a strip button's flyout holds.</summary>
    internal static ApproachFlyoutPanel Panel(MenuFlyout flyout) =>
        Assert.IsType<ApproachFlyoutPanel>(Assert.IsType<ApproachFlyoutHost>(Assert.Single(flyout.Items.OfType<MenuItem>()).Header).Child);

    private static ApproachFlyoutPanel OpenEntry(string id, IMenuAircraft aircraft, IMenuHost host)
    {
        MenuContext context = TestMenuContext.Create(Callsign, Initials, null, false, VfrCommandsForIfr.None);
        MenuItem entry = Assert.IsType<MenuItem>(MenuCatalog.Get(id).Build(aircraft, context, host));
        entry.IsSubMenuOpen = true;
        MenuItem row = Assert.Single(entry.Items.OfType<MenuItem>());
        return Assert.IsType<ApproachFlyoutPanel>(Assert.IsType<ApproachFlyoutHost>(row.Header).Child);
    }

    /// <summary>An airborne IFR arrival to <paramref name="destination"/> at 6,000 ft, an A320 unless told otherwise.</summary>
    private static FakeMenuAircraft Arrival(string destination) =>
        new()
        {
            Callsign = Callsign,
            Destination = destination,
            FiledAircraftType = "A320",
            BaseAircraftType = "A320",
            AltitudeFeet = 6000,
            IndicatedAirspeedKnots = 210,
            GroundSpeedKnots = 210,
        };
}
