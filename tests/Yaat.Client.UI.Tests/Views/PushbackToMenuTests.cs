using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Sim;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Push back to… submenu (<see cref="PushbackToMenu"/>): over the real client host at OAK gate 26 with a seed keyed
/// current, its sections, badges, distance hints, facings and blocked rows; over a <see cref="RecordingMenuHost"/>, how
/// it follows a <see cref="PushTargetList"/> that is still computing — the placeholder, the in-place rebuild while
/// open, and the empty state once the plan lands with nothing.
/// </summary>
public class PushbackToMenuTests(OakPushTargetSeedCopy seedCopy) : IClassFixture<OakPushTargetSeedCopy>
{
    private const string Callsign = ClientMenuHostGroundTests.Callsign;
    private const string PushBackTo = "Push back to…";
    private static readonly string[] Cardinals = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];

    /// <summary>
    /// Gate 26's group-III seed holds taxilanes and taxiways only, so the submenu has one section: every row badged TL or
    /// TW, showing its distance hint and command, by planned length.
    /// </summary>
    [AvaloniaFact]
    public async Task PushbackTo_KoakGate26_TaxiwaySectionBadgesAndHints()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (AircraftModel target, _, ClientMenuHost host) = ClientMenuHostGroundTests.AtGate26(seedCopy.NewSeed());

        // The menu built in the same UI-thread job shares this list, so its rows are these targets.
        PushTargetList list = host.GetPushbackTargets(Callsign);
        MenuItem pushTo = Submenu(AircraftMenuBuilder.Build(target, new MenuClick(Callsign, null, null, []), host, _ => []));

        Assert.Equal([PushbackToMenu.TaxiwaysHeader], SectionHeaders(pushTo));
        List<PushbackToMenu.PushTargetRow> rows = SectionRows(pushTo, PushbackToMenu.TaxiwaysHeader);
        Assert.NotEmpty(rows);
        Assert.Equal(list.Targets.Count, rows.Count);
        Assert.All(rows, row => Assert.Contains(row.Badge, new[] { "TL", "TW" }));
        Assert.All(rows, row => Assert.StartsWith($"{row.Badge} ", row.ToString()));
        Assert.All(rows, row => Assert.Contains($" {PushbackToMenu.DistanceHint(row.Target.PathLengthFt)} {row.Target.Command}", row.ToString()));
        Assert.Equal(rows.Select(r => r.Target.PathLengthFt).Order(), rows.Select(r => r.Target.PathLengthFt));

        await Settle(list);
    }

    /// <summary>
    /// Gate 12's group-III seed holds taxilanes, a taxiway and spot C: the taxilane and taxiway section comes first, a
    /// separator, then the spot section with its S rows.
    /// </summary>
    [AvaloniaFact]
    public async Task PushbackTo_KoakGate12_SectionsInOrder()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (AircraftModel target, _, ClientMenuHost host) = ClientMenuHostGroundTests.AtStand("12", seedCopy.NewSeed());

        PushTargetList list = host.GetPushbackTargets(Callsign);
        MenuItem pushTo = Submenu(AircraftMenuBuilder.Build(target, new MenuClick(Callsign, null, null, []), host, _ => []));

        Assert.Equal([PushbackToMenu.TaxiwaysHeader, PushbackToMenu.SpotsHeader], SectionHeaders(pushTo));
        List<object?> items = [.. pushTo.Items];
        int spots = items.FindIndex(i => (i is MenuItem m) && ((m.Header as string) == PushbackToMenu.SpotsHeader));
        Assert.IsType<Separator>(items[spots - 1]);
        Assert.All(SectionRows(pushTo, PushbackToMenu.TaxiwaysHeader), row => Assert.Contains(row.Badge, new[] { "TL", "TW" }));
        Assert.Contains(SectionRows(pushTo, PushbackToMenu.SpotsHeader), row => row.Target.Command == "PUSH $C");
        Assert.All(SectionRows(pushTo, PushbackToMenu.SpotsHeader), row => Assert.Equal("S", row.Badge));

        await Settle(list);
    }

    [Fact]
    public void DistanceHint_RoundsToTheNearestFiveFeetWithAThousandsSeparator()
    {
        Assert.Equal("~250 ft", PushbackToMenu.DistanceHint(247.6));
        Assert.Equal("~1,355 ft", PushbackToMenu.DistanceHint(1357.4));
        Assert.Equal("~15 ft", PushbackToMenu.DistanceHint(12.5));
        Assert.Equal("~25 ft", PushbackToMenu.DistanceHint(22.5));
    }

    [AvaloniaFact]
    public async Task PushbackTo_TaxilaneArrow_OffersFacings()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (AircraftModel target, _, ClientMenuHost client) = ClientMenuHostGroundTests.AtGate26(seedCopy.NewSeed());
        var host = new SendCapturingHost(client, "AB");
        PushTargetList list = client.GetPushbackTargets(Callsign);
        MenuItem row = Row(Submenu(AircraftMenuBuilder.Build(target, new MenuClick(Callsign, null, null, []), host, _ => [])), "PUSH TE");

        IReadOnlyList<MenuItem> facings = PushbackToMenu.Facings(row);

        Assert.Equal(2, facings.Count);
        Assert.All(facings, f => Assert.StartsWith("Face ", (string)f.Header!));
        List<string> faced = [.. facings.Select(f => ((string)f.Header!)["Face ".Length..])];
        Assert.Equal(4, Math.Abs(Array.IndexOf(Cardinals, faced[0]) - Array.IndexOf(Cardinals, faced[1])));
        Assert.Equal(faced.Select(c => $"PUSH TE FACE {c}"), facings.Select(MenuCommandText.GetCommand));

        Click(row);
        Click(facings[1]);
        Assert.Equal([(Callsign, "PUSH TE", "AB"), (Callsign, $"PUSH TE FACE {faced[1]}", "AB")], host.Sent);

        await Settle(list);
    }

    [AvaloniaFact]
    public async Task PushbackTo_BlockedRow_IsDisabledAndNamesItsBlocker()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel neighbour = ClientMenuHostGroundTests.ParkedAt("27", ClientMenuHostGroundTests.Gate27Neighbour);
        (AircraftModel target, _, ClientMenuHost client) = ClientMenuHostGroundTests.AtGate26(seedCopy.NewSeed(), neighbour);
        var host = new SendCapturingHost(client, "AB");
        PushTargetList list = client.GetPushbackTargets(Callsign);
        MenuItem row = Row(Submenu(AircraftMenuBuilder.Build(target, new MenuClick(Callsign, null, null, []), host, _ => [])), "PUSH TE");

        Assert.False(row.IsEnabled);
        var content = (PushbackToMenu.PushTargetRow)row.Header!;
        Assert.EndsWith($" · blocked by {ClientMenuHostGroundTests.Gate27Neighbour}", content.ToString());
        Assert.NotNull(content.FacingsButton);
        Assert.False(content.FacingsButton.IsEnabled);

        Click(row);
        Assert.Empty(host.Sent);

        await Settle(list);
    }

    /// <summary>
    /// Push back to… has no quick-command glyph, so it lists as a text entry and never takes a strip button: its
    /// submenu follows a filling list through <c>IsSubMenuOpen</c>, which a strip button's flyout copy never raises.
    /// </summary>
    [Fact]
    public void PushbackTo_HasNoStripGlyph_SoItStaysOffTheQuickCommandStrip() => Assert.Null(QuickCommandGlyphs.For(MenuIds.GroundPushbackTo));

    /// <summary>
    /// A plan started for one stand is not handed to an open for another: moved from gate 26 to gate 25 before the first
    /// plan lands, the aircraft gets gate 25's targets.
    /// </summary>
    [AvaloniaFact]
    public async Task InFlightPlan_ForADifferentStand_IsNotReused()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (AircraftModel target, MainViewModel main, ClientMenuHost host) = ClientMenuHostGroundTests.AtGate26(OakPushTargetSeedCopy.NoSeed());
        PushTargetList atGate26 = host.GetPushbackTargets(Callsign);
        AircraftModel atGate25 = ClientMenuHostGroundTests.ParkedAt("25", Callsign);
        target.Position = atGate25.Position;
        target.Heading = atGate25.Heading;
        target.ParkingSpot = atGate25.ParkingSpot;

        PushTargetList second = host.GetPushbackTargets(Callsign);
        await Settle(atGate26);
        await Settle(second);

        List<ClientMenuHostGroundTests.PushTargetRowKey> expected = ClientMenuHostGroundTests.LivePlanRows(main, target, "25");
        Assert.NotEqual(expected, ClientMenuHostGroundTests.RowKeys(atGate26));
        Assert.Equal(expected, ClientMenuHostGroundTests.RowKeys(second));
    }

    /// <summary>
    /// A plan started for one held pose is not handed to an open from a pose fifty feet away: moved along its heading
    /// before the first plan lands, the held aircraft gets a fresh list, planned from where it now stands.
    /// </summary>
    [AvaloniaFact]
    public async Task InFlightPlan_ForAHeldPoseFiftyFeetAway_IsNotReused()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (AircraftModel target, MainViewModel main, ClientMenuHost host) = ClientMenuHostGroundTests.AtStand("12", seedCopy.NewSeed());
        HoldAtSpotC(main, target);
        PushTargetList first = host.GetPushbackTargets(Callsign);
        target.Position = GeoMath.ProjectPoint(target.Position, target.Heading, 50.0 / GeoMath.FeetPerNm);

        PushTargetList second = host.GetPushbackTargets(Callsign);
        await Settle(first);
        await Settle(second);

        Assert.NotSame(first, second);
        Assert.Equal(HeldPlanRows(main, target), ClientMenuHostGroundTests.RowKeys(second));
    }

    /// <summary>
    /// A plan started for one held pose is handed to an open from a pose half a foot and half a degree off it: the two
    /// opens share one list, wherever the pose falls; here the two headings straddle a whole degree's rounding edge.
    /// </summary>
    [AvaloniaFact]
    public async Task InFlightPlan_ForTheSameHeldPose_IsReused()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (AircraftModel target, MainViewModel main, ClientMenuHost host) = ClientMenuHostGroundTests.AtStand("12", seedCopy.NewSeed());
        HoldAtSpotC(main, target);
        target.Heading = new TrueHeading(Math.Floor(target.Heading.Degrees) + 0.3);
        PushTargetList first = host.GetPushbackTargets(Callsign);
        target.Position = GeoMath.ProjectPoint(target.Position, new TrueHeading(target.Heading.Degrees + 90.0), 0.5 / GeoMath.FeetPerNm);
        target.Heading = new TrueHeading(target.Heading.Degrees + 0.5);

        PushTargetList second = host.GetPushbackTargets(Callsign);
        await Settle(first);
        await Settle(second);

        Assert.Same(first, second);
    }

    /// <summary>
    /// The held-pose tolerance: on the same heading, an open at the edge of <see cref="GroundViewModel.SameHeldPoseFt"/> from
    /// the in-flight plan's pose shares its list, and one half a foot past it does not. The edge open stands a hundredth
    /// of the tolerance inside it, as <see cref="GeoMath.DistanceNm(LatLon, LatLon)"/> measures it:
    /// <see cref="GeoMath.ProjectPoint(LatLon, TrueHeading, double)"/> projects on a flat 60 nm-per-degree grid, so a point
    /// projected exactly the tolerance away measures about 0.07% past it.
    /// </summary>
    [AvaloniaFact]
    public async Task InFlightPlan_AtTheHeldPoseTolerance_IsReusedAndJustPastItIsNot()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (AircraftModel target, MainViewModel main, ClientMenuHost host) = ClientMenuHostGroundTests.AtStand("12", seedCopy.NewSeed());
        HoldAtSpotC(main, target);
        LatLon held = target.Position;
        var abeam = new TrueHeading(target.Heading.Degrees + 90.0);
        PushTargetList first = host.GetPushbackTargets(Callsign);

        target.Position = GeoMath.ProjectPoint(held, abeam, 0.99 * GroundViewModel.SameHeldPoseFt / GeoMath.FeetPerNm);
        double edgeFt = GeoMath.DistanceNm(held, target.Position) * GeoMath.FeetPerNm;
        PushTargetList atTolerance = host.GetPushbackTargets(Callsign);
        target.Position = GeoMath.ProjectPoint(held, abeam, (GroundViewModel.SameHeldPoseFt + 0.5) / GeoMath.FeetPerNm);
        PushTargetList pastTolerance = host.GetPushbackTargets(Callsign);
        await Settle(first);
        await Settle(pastTolerance);

        Assert.InRange(edgeFt, 0.99 * GroundViewModel.SameHeldPoseFt, GroundViewModel.SameHeldPoseFt);
        Assert.Same(first, atTolerance);
        Assert.NotSame(first, pastTolerance);
    }

    /// <summary>
    /// An aircraft pushed from gate 12 to spot C and held there has no stand but keeps Push back to…: no seed covers a
    /// held aircraft, so the entry computes at once, and the live plan is the one made from where it stands, without
    /// spot C.
    /// </summary>
    [AvaloniaFact]
    public async Task PushbackTo_HeldAfterPushToSpot_PlansFromWhereItStands()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (AircraftModel target, MainViewModel main, ClientMenuHost host) = ClientMenuHostGroundTests.AtStand("12", seedCopy.NewSeed());
        AirportGroundLayout layout = main.Ground.DomainLayout!;
        GroundNode spotC = layout.FindSpotNodeByName("C") ?? throw new InvalidOperationException("No spot C at OAK");
        HoldAfterPush(main, target, "12", TugGoal.Spot(spotC));
        target.ParkingSpot = "";

        PushTargetList list = host.GetPushbackTargets(Callsign);
        MenuItem pushTo = Submenu(AircraftMenuBuilder.Build(target, new MenuClick(Callsign, null, null, []), host, _ => []));

        AssertOnlyRow(pushTo, PushbackToMenu.ComputingText);
        await Settle(list);
        List<ClientMenuHostGroundTests.PushTargetRowKey> expected = HeldPlanRows(main, target);
        Assert.NotEmpty(expected);
        Assert.Equal(expected, ClientMenuHostGroundTests.RowKeys(list));
        Assert.DoesNotContain(list.Targets, t => t.Command == "PUSH $C");
    }

    /// <summary>
    /// Pushed from gate 26 onto TE and held there, the aircraft still carries stand 26, as the server leaves it after a push
    /// to a taxiway: the entry shows, computing rather than showing gate 26's seed, and the live plan is the one made from
    /// where it is held, not gate 26's.
    /// </summary>
    [AvaloniaFact]
    public async Task PushbackTo_HeldAfterPushOntoTaxiway_PlansHeldNotFromTheStand()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (AircraftModel target, MainViewModel main, ClientMenuHost host) = ClientMenuHostGroundTests.AtGate26(seedCopy.NewSeed());
        AirportGroundLayout layout = main.Ground.DomainLayout!;
        GroundNode exit = layout.FindExitByTaxiway(StandNode(layout, "26").Position, "TE") ?? throw new InvalidOperationException("No exit onto TE");
        HoldAfterPush(main, target, "26", TugGoal.StraightBackTo(exit, "TE"));
        Assert.Equal("26", target.ParkingSpot);

        PushTargetList list = host.GetPushbackTargets(Callsign);
        MenuItem pushTo = Submenu(AircraftMenuBuilder.Build(target, new MenuClick(Callsign, null, null, []), host, _ => []));

        AssertOnlyRow(pushTo, PushbackToMenu.ComputingText);
        await Settle(list);
        List<ClientMenuHostGroundTests.PushTargetRowKey> expected = HeldPlanRows(main, target);
        Assert.NotEmpty(expected);
        Assert.Equal(expected, ClientMenuHostGroundTests.RowKeys(list));
        Assert.NotEqual(ClientMenuHostGroundTests.LivePlanRows(main, target, "26"), ClientMenuHostGroundTests.RowKeys(list));
    }

    [AvaloniaFact]
    public void PushbackTo_Computing_ShowsThePlaceholder()
    {
        var host = new RecordingMenuHost("") { PushbackTargets = PushTargetList.Pending(null) };

        MenuItem pushTo = Submenu(Build(host));

        AssertOnlyRow(pushTo, PushbackToMenu.ComputingText);
    }

    /// <summary>
    /// An open submenu rebuilds its items in place when the plan lands; one closed when the plan lands keeps what it
    /// showed until it is opened again, and then shows the landed targets.
    /// </summary>
    [AvaloniaFact]
    public void PushbackTo_ListChangesWhileOpen_RebuildsInPlace()
    {
        var open = PushTargetList.Pending(null);
        MenuItem pushTo = Submenu(Build(new RecordingMenuHost("") { PushbackTargets = open }));
        var closed = PushTargetList.Pending(null);
        MenuItem later = Submenu(Build(new RecordingMenuHost("") { PushbackTargets = closed }));
        pushTo.IsSubMenuOpen = true;

        open.Apply(LandedTargets());
        closed.Apply(LandedTargets());

        string[] landed = [PushbackToMenu.TaxiwaysHeader, "PUSH TE", "PUSH W", "-", PushbackToMenu.SpotsHeader, "PUSH $1"];
        Assert.Equal(landed, Texts(pushTo));
        AssertOnlyRow(later, PushbackToMenu.ComputingText);

        later.IsSubMenuOpen = true;
        Assert.Equal(landed, Texts(later));
    }

    [AvaloniaFact]
    public void PushbackTo_EmptyAfterPlan_ShowsNoTargets()
    {
        var list = PushTargetList.Pending(null);
        MenuItem pushTo = Submenu(Build(new RecordingMenuHost("") { PushbackTargets = list }));
        pushTo.IsSubMenuOpen = true;

        list.Apply([]);

        AssertOnlyRow(pushTo, PushbackToMenu.NoTargetsText);
    }

    /// <summary>A taxilane, a taxiway and a spot, the spot listed first, so the sections must order them.</summary>
    private static List<MenuPushTarget> LandedTargets() =>
        [
            new(MenuPushTargetKind.Spot, "1", null, 300, "PUSH $1", [], null),
            new(MenuPushTargetKind.Taxiway, "W", "across", 420, "PUSH W", [], null),
            new(MenuPushTargetKind.Taxilane, "TE", "alongside", 140, "PUSH TE", [], null),
        ];

    private static GroundNode StandNode(AirportGroundLayout layout, string name) =>
        layout.Nodes.Values.Where(n => (n.Type == GroundNodeType.Parking) && (n.Name == name)).OrderBy(n => n.Id).First();

    /// <summary>Holds <paramref name="target"/> where a group-III push off gate 12 to spot C ends, its stand cleared.</summary>
    private static void HoldAtSpotC(MainViewModel main, AircraftModel target)
    {
        GroundNode spotC = main.Ground.DomainLayout!.FindSpotNodeByName("C") ?? throw new InvalidOperationException("No spot C at OAK");
        HoldAfterPush(main, target, "12", TugGoal.Spot(spotC));
        target.ParkingSpot = "";
    }

    /// <summary>
    /// Moves <paramref name="target"/> to where a group-III push off <paramref name="standName"/> to <paramref name="goal"/>
    /// ends, as the planner flies it, holding after the pushback there; its stand name is left as it was.
    /// </summary>
    private static void HoldAfterPush(MainViewModel main, AircraftModel target, string standName, TugGoal goal)
    {
        AirportGroundLayout layout = main.Ground.DomainLayout!;
        GroundNode stand = StandNode(layout, standName);
        TugRequest request = PushTargetPlanner.RequestFor(
            new PushbackPose(stand.Position, stand.TrueHeading!.Value.Degrees),
            DesignGroupEnvelopes.LoadShipped().FootprintOf(AirplaneDesignGroup.III),
            MovementAreaClassification.Build(layout, NavigationDatabase.Instance.AirportSidecars),
            goal,
            startsAtStand: true
        );
        TugPlan? plan = TugMovePlanner.Plan(layout, request, out string refusal);
        Assert.True(plan is not null, refusal);
        target.Position = plan.End.Position;
        target.Heading = new TrueHeading(plan.End.NoseTrueDeg);
        target.CurrentPhase = "Holding After Pushback";
    }

    /// <summary>
    /// The group-III targets <see cref="PushTargetPlanner.ComputeHeld"/> plans from <paramref name="target"/>'s pose, alone
    /// at the airport, each through the live check: the rows a settled held list should hold.
    /// </summary>
    private static List<ClientMenuHostGroundTests.PushTargetRowKey> HeldPlanRows(MainViewModel main, AircraftModel target)
    {
        IReadOnlyList<PrecomputedPushTarget>? held = PushTargetPlanner.ComputeHeld(
            main.Ground.DomainLayout!,
            DesignGroupEnvelopes.LoadShipped(),
            NavigationDatabase.Instance.AirportSidecars,
            new PushbackPose(target.Position, target.Heading.Degrees),
            AirplaneDesignGroup.III
        );
        Assert.NotNull(held);
        var request = new PushTargetLiveCheckRequest
        {
            Subject = GroundViewModel.TugCandidateOf(target),
            Actual = AircraftFootprint.FromType(target.AircraftType),
            Others = [GroundViewModel.TugCandidateOf(target)],
        };
        return
        [
            .. held.Select(t => (Target: t, Verdict: PushTargetLiveCheck.Check(t, request)))
                .Where(v => v.Verdict.Outcome != PushTargetLiveOutcome.Unflyable)
                .Select(v => new ClientMenuHostGroundTests.PushTargetRowKey(
                    v.Target.Kind.ToString(),
                    v.Target.Command,
                    v.Target.PathLengthFt,
                    v.Verdict.BlockerCallsign
                )),
        ];
    }

    private static ContextMenu Build(RecordingMenuHost host) =>
        AircraftMenuBuilder.Build(
            new AircraftModel
            {
                Callsign = Callsign,
                AircraftType = "B738",
                FlightRules = "IFR",
                IsOnGround = true,
                CurrentPhase = "At Parking",
            },
            new MenuClick(Callsign, null, null, []),
            host,
            _ => []
        );

    private static void AssertOnlyRow(MenuItem pushTo, string text)
    {
        MenuItem row = Assert.IsType<MenuItem>(Assert.Single(pushTo.Items));
        Assert.Equal(text, row.Header);
        Assert.False(row.IsEnabled);
    }

    /// <summary>Each item as its header text, a target row as its command, a separator as <c>-</c>.</summary>
    private static List<string> Texts(MenuItem pushTo) =>
        [.. pushTo.Items.Select(i => i is MenuItem { Header: string header } ? header : (i is MenuItem m ? MenuCommandText.GetCommand(m)! : "-"))];

    /// <summary>The Push back to… submenu under the menu's All Commands.</summary>
    private static MenuItem Submenu(ContextMenu menu)
    {
        MenuItem all = menu.Items.OfType<MenuItem>().Single(m => (m.Header as string) == AircraftMenuBuilder.AllCommandsHeader);
        return all.Items.OfType<MenuItem>().Single(m => (m.Header as string) == PushBackTo);
    }

    private static MenuItem Row(MenuItem pushTo, string command) =>
        pushTo.Items.OfType<MenuItem>().Single(m => (m.Header is PushbackToMenu.PushTargetRow) && (MenuCommandText.GetCommand(m) == command));

    private static List<string> SectionHeaders(MenuItem pushTo) =>
        [.. pushTo.Items.OfType<MenuItem>().Where(m => (m.Header is string) && !m.IsEnabled).Select(m => (string)m.Header!)];

    /// <summary>The rows under <paramref name="header"/> in <paramref name="pushTo"/>, up to the next separator.</summary>
    private static List<PushbackToMenu.PushTargetRow> SectionRows(MenuItem pushTo, string header)
    {
        List<object?> items = [.. pushTo.Items];
        int start = items.FindIndex(i => (i is MenuItem m) && ((m.Header as string) == header));
        Assert.True(start >= 0, $"no section {header}");
        return
        [
            .. items
                .Skip(start + 1)
                .TakeWhile(i => i is MenuItem { Header: PushbackToMenu.PushTargetRow })
                .Select(i => (PushbackToMenu.PushTargetRow)((MenuItem)i!).Header!),
        ];
    }

    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    /// <summary>Waits for the list's background live plan to land, so no plan outlives its test.</summary>
    private static Task Settle(PushTargetList list) =>
        list.Settled.WaitAsync(ClientMenuHostGroundTests.SettleTimeout, TestContext.Current.CancellationToken);
}
