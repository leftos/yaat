using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Sim;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The client menu host's ground members — ground traffic, hold short, route preview, pushback and preset taxi — which
/// answer from the primary ground view model on the real KOAK layout, and the builder's hold-short and follow items built
/// over them. The display items the menu shows are the view's own (see <c>CanvasMenuItemsTests</c>), so the host serves
/// none of them.
/// </summary>
public class ClientMenuHostGroundTests(OakPushTargetSeedCopy seedCopy) : IClassFixture<OakPushTargetSeedCopy>
{
    internal const string Callsign = "UAL100";
    private const string OtherCallsign = "SWA200";

    /// <summary>The OAK stand the push-target tests park on.</summary>
    internal const string Gate26 = "26";

    /// <summary>The B738 parked on gate 27, beside gate 26's pushes onto TE and TC.</summary>
    internal const string Gate27Neighbour = "SWA27";

    /// <summary>How long a test waits for a background live plan to land: a hang guard a passing run never reaches.</summary>
    internal static readonly TimeSpan SettleHangGuard = TimeSpan.FromMinutes(5);

    private readonly OakPushTargetSeedCopy _seedCopy = seedCopy;

    // --- Ground traffic, hold short and route preview ----------------------------------------

    [AvaloniaFact]
    public void GetGroundTrafficRows_ExcludesSelfAndAirborne_NearestFirst_Uncapped()
    {
        var at = new LatLon(37.72, -122.22);
        AircraftModel target = GroundAircraft(Callsign, "Taxiing", at);
        AircraftModel self = GroundAircraft(Callsign, "At Parking", at);
        AircraftModel airborne = GroundAircraft("AAL999", "ApproachNav", new LatLon(at.Lat + 0.0005, at.Lon));
        airborne.IsOnGround = false;
        // Added farthest first, so the order must come from the distance, not the list.
        AircraftModel[] traffic =
        [
            .. Enumerable.Range(1, 14).Reverse().Select(i => GroundAircraft($"SWA{i:000}", "Taxiing", new LatLon(at.Lat + (i * 0.001), at.Lon))),
        ];
        MainViewModel main = MainWith(target, [self, airborne, .. traffic]);
        var host = new ClientMenuHost(main, target, new Border());

        Assert.Equal(Enumerable.Range(1, 14).Select(i => $"SWA{i:000}"), host.GetGroundTrafficRows(Callsign).Select(row => row.Callsign));
    }

    /// <summary>
    /// YAAT-447: delayed spawns parked nearer than a real aircraft pushing back are not in the sim yet, so the list leaves
    /// them out and the pushing-back aircraft is not cut.
    /// </summary>
    [AvaloniaFact]
    public void GetGroundTrafficRows_ExcludesDelayedSpawns()
    {
        var at = new LatLon(37.72, -122.22);
        AircraftModel target = GroundAircraft(Callsign, "Taxiing", at);
        AircraftModel[] delayed = [.. Enumerable.Range(1, 12).Select(i => DelayedSpawn($"DLY{i:00}", new LatLon(at.Lat + (i * 0.0001), at.Lon)))];
        AircraftModel pushingBack = GroundAircraft("SWA1182", "Pushback", new LatLon(at.Lat + 0.002, at.Lon));
        MainViewModel main = MainWith(target, [.. delayed, pushingBack]);
        var host = new ClientMenuHost(main, target, new Border());

        Assert.Equal(["SWA1182"], host.GetGroundTrafficRows(Callsign).Select(row => row.Callsign));
    }

    [AvaloniaFact]
    public void GetGroundTrafficRows_SplitsMovingFromParkedByPhaseAndSpeed()
    {
        var at = new LatLon(37.72, -122.22);
        AircraftModel target = GroundAircraft(Callsign, "Taxiing", at);
        AircraftModel pushingBack = GroundAircraft("PSH1", "Pushback", new LatLon(at.Lat + 0.001, at.Lon));
        AircraftModel taxiing = GroundAircraft("TXI1", "Taxiing", new LatLon(at.Lat + 0.002, at.Lon));
        AircraftModel rolling = GroundAircraft("ROL1", "Runway Exit", new LatLon(at.Lat + 0.003, at.Lon));
        rolling.GroundSpeed = 12;
        AircraftModel parked = GroundAircraft("PRK1", "At Parking", new LatLon(at.Lat + 0.004, at.Lon));
        AircraftModel creeping = GroundAircraft("HLD1", "Holding In Position", new LatLon(at.Lat + 0.005, at.Lon));
        creeping.GroundSpeed = 1;
        MainViewModel main = MainWith(target, [creeping, parked, rolling, taxiing, pushingBack]);
        var host = new ClientMenuHost(main, target, new Border());

        Assert.Equal(
            [("PSH1", true), ("TXI1", true), ("ROL1", true), ("PRK1", false), ("HLD1", false)],
            host.GetGroundTrafficRows(Callsign).Select(row => (row.Callsign, row.IsMoving))
        );
    }

    [AvaloniaFact]
    public void GetGroundTrafficRows_FlagsSurfaceShadows()
    {
        var at = new LatLon(37.72, -122.22);
        AircraftModel target = GroundAircraft(Callsign, "Taxiing", at);
        AircraftModel shadow = GroundAircraft("SHD1", "Taxiing", new LatLon(at.Lat + 0.001, at.Lon));
        shadow.IsLiveTraffic = true;
        AircraftModel inSim = GroundAircraft(OtherCallsign, "Taxiing", new LatLon(at.Lat + 0.002, at.Lon));
        MainViewModel main = MainWith(target, [shadow, inSim]);
        var host = new ClientMenuHost(main, target, new Border());

        Assert.Equal(
            [("SHD1", true), (OtherCallsign, false)],
            host.GetGroundTrafficRows(Callsign).Select(row => (row.Callsign, row.IsSurfaceShadow))
        );
    }

    [AvaloniaFact]
    public void IsOnTaxiRoute_TheHoldShortAheadIsOnTheRoute_AStandIsNot()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = TaxiingOnW3();
        AircraftModel holding = GroundAircraft(OtherCallsign, $"Holding Short {Runway30HoldShortNode.RunwayId}", PositionOf(Runway30HoldShortNode));
        AircraftModel parked = GroundAircraft("SWA300", "At Parking", PositionOf(PushbackFaceNode));
        MainViewModel main = OakMain(target, [holding, parked]);
        var host = new ClientMenuHost(main, target, new Border());

        Assert.True(host.IsOnTaxiRoute(Callsign, OtherCallsign));
        Assert.False(host.IsOnTaxiRoute(Callsign, "SWA300"));
        Assert.False(host.IsOnTaxiRoute(OtherCallsign, Callsign));
        Assert.False(host.IsOnTaxiRoute("NOPE", OtherCallsign));
    }

    [AvaloniaFact]
    public void HighlightAircraft_SetsReplacesAndClears_KeepingAHandHighlight()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var host = new ClientMenuHost(main, null, new Border());
        GroundDataBlockViewState state = main.Ground.DataBlockState;
        state.ToggleHighlight("KEEP1");
        int changes = 0;
        state.HighlightsChanged += () => changes++;

        host.HighlightAircraft("SWA1");
        Assert.Equal(["KEEP1", "SWA1"], state.HighlightedCallsigns.Order());
        host.HighlightAircraft("SWA2");
        Assert.Equal(["KEEP1", "SWA2"], state.HighlightedCallsigns.Order());
        host.HighlightAircraft("KEEP1");
        Assert.Equal(["KEEP1"], state.HighlightedCallsigns.Order());
        host.HighlightAircraft(null);
        Assert.Equal(["KEEP1"], state.HighlightedCallsigns.Order());

        // Four calls, three changes: the clear removes nothing (the menu never set KEEP1 as its own highlight), so it raises no event.
        Assert.Equal(3, changes);
    }

    private static AircraftModel DelayedSpawn(string callsign, LatLon position)
    {
        AircraftModel ac = GroundAircraft(callsign, "At Parking", position);
        ac.Status = "Delayed (40:21)";
        return ac;
    }

    [AvaloniaFact]
    public void GroundMembers_AircraftNotInTheList_AnswerNothing()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var host = new ClientMenuHost(main, null, new Border());

        Assert.Empty(host.GetGroundTrafficRows(Callsign));
        Assert.Equal(HoldShortMenu.Empty, host.GetHoldShortChoices(Callsign));
        Assert.Empty(host.GetPushbackFaceChoices(Callsign));
        AssertSettledEmpty(host.GetPushbackTargets(Callsign));
        Assert.Empty(host.GetPresetTaxiChoices(Callsign));

        host.EnterPushRoute(Callsign);
        Assert.False(main.Ground.IsDrawingRoute);
    }

    [AvaloniaFact]
    public void GetHoldShortChoices_SendHSWithThePreviewRoute()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = TaxiingAcrossOnG();
        MainViewModel main = OakMain(target, []);
        var host = new ClientMenuHost(main, target, new Border());

        HoldShortMenu holdShort = host.GetHoldShortChoices(Callsign);

        // G crosses 10L/28R mid-runway on its way to 28L (the destination, the ladder's dimmed last row rather than a stop),
        // and the room names no active runway: the runway stop names both ends and sends the lower-numbered one, which binds
        // the same bar. D is where the G/D fillet leaves G, C where its fillets meet the bar.
        Assert.Equal("route G · RWY 28L", holdShort.RouteLine);
        Assert.Equal(
            [("TW", "D", "HS D"), ("RW", "10L/28R", "HS 10L"), ("TW", "C", "HS C")],
            holdShort.Rows.Select(r => (r.Label.Badge, r.Label.Name, r.Command))
        );
        HoldShortChoice runway = Assert.Single(holdShort.Rows, r => r.Label.Badge == HoldShortChoice.RunwayBadge);
        Assert.Equal((HoldShortChoice.RunwayBadge, "10L/28R", "HS 10L"), (runway.Label.Badge, runway.Label.Name, runway.Command));
        Assert.Equal(Bar28ROnG.Id, runway.Preview.Segments[^1].ToNodeId);
        Assert.Equal("Runway 28L", holdShort.Destination?.Name);
    }

    [AvaloniaFact]
    public void GetHoldShortChoices_ActiveEnd_NamesTheRunwayRow()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = TaxiingAcrossOnG();
        MainViewModel main = OakMain(target, []);
        main.ApplyActiveRunways(new Dictionary<string, List<string>> { ["OAK"] = ["28R"] });
        var host = new ClientMenuHost(main, target, new Border());

        HoldShortChoice runway = Assert.Single(host.GetHoldShortChoices(Callsign).Rows, r => r.Label.Badge == HoldShortChoice.RunwayBadge);

        Assert.Equal(("28R", "HS 28R"), (runway.Label.Name, runway.Command));
    }

    [AvaloniaFact]
    public void SetRoutePreview_SetsAndClearsTheGroundPreview()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var host = new ClientMenuHost(main, null, new Border());
        var route = new TaxiRoute { Segments = [], HoldShortPoints = [] };

        host.SetRoutePreview(route);
        Assert.Same(route, main.Ground.PreviewRoute);

        host.SetRoutePreview(null);
        Assert.Null(main.Ground.PreviewRoute);
    }

    // --- Pushback faces, push back to, push route and preset taxi routes ---------------------

    [AvaloniaFact]
    public void GetPushbackFaceChoices_AtSpotI30_SendPushFaceForEachNonRampTaxiway()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(PushbackFaceNode));
        MainViewModel main = OakMain(target, []);
        var host = new ClientMenuHost(main, target, new Border());

        IReadOnlyList<MenuCommandChoice> choices = host.GetPushbackFaceChoices(Callsign);

        List<(string Label, string Cardinal)> directions = main.Ground.GetPushbackDirections(target);
        Assert.NotEmpty(directions);
        Assert.Equal(
            directions.Select(d => ($"Push back, {d.Label}", (string?)$"PUSH FACE {d.Cardinal}")),
            choices.Select(c => (c.Label, c.Command))
        );
        Assert.All(choices, c => Assert.StartsWith("Push back, face ", c.Label));
        Assert.All(choices, c => Assert.Null(c.Preview));
    }

    /// <summary>
    /// With no usable seed the list is computing at once; once the background live plan lands, its rows are gate 26's
    /// group-III targets as <see cref="PushTargetPlanner.ComputeStand"/> plans them, each through the live check.
    /// </summary>
    [AvaloniaFact]
    public async Task PushbackTargets_Gate26_NoSeed_ShowsComputingThenTheLivePlan()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (AircraftModel target, MainViewModel main, ClientMenuHost host) = AtGate26(OakPushTargetSeedCopy.NoSeed());

        PushTargetList list = host.GetPushbackTargets(Callsign);

        Assert.Empty(list.Targets);
        Assert.False(list.Settled.IsCompleted);

        await list.Settled.WaitAsync(SettleHangGuard, TestContext.Current.CancellationToken);

        Assert.True(list.Settled.IsCompleted);
        List<PushTargetRowKey> expected = LivePlanRows(main, target, Gate26);
        Assert.NotEmpty(expected);
        Assert.Equal(expected, RowKeys(list));
        Assert.All(list.Targets.Where(t => t.Kind == MenuPushTargetKind.Spot), t => Assert.Empty(t.Facings));
    }

    /// <summary>
    /// <paramref name="stand"/>'s group-III targets as <see cref="PushTargetPlanner.ComputeStand"/> plans them for
    /// <paramref name="target"/>, alone at the airport, each through the live check: the rows a settled list should hold.
    /// </summary>
    internal static List<PushTargetRowKey> LivePlanRows(MainViewModel main, AircraftModel target, string stand)
    {
        PushTargetEntry? live = PushTargetPlanner.ComputeStand(
            main.Ground.DomainLayout!,
            DesignGroupEnvelopes.LoadShipped(),
            NavigationDatabase.Instance.AirportSidecars,
            stand,
            AirplaneDesignGroup.III
        );
        Assert.NotNull(live);
        Assert.NotEmpty(live.Targets);
        var request = new PushTargetLiveCheckRequest
        {
            Subject = GroundViewModel.TugCandidateOf(target),
            Actual = AircraftFootprint.FromType(target.AircraftType),
            Others = [GroundViewModel.TugCandidateOf(target)],
        };
        return
        [
            .. live
                .Targets.Select(t => (Target: t, Verdict: PushTargetLiveCheck.Check(t, request)))
                .Where(v => v.Verdict.Outcome != PushTargetLiveOutcome.Unflyable)
                .Select(v => new PushTargetRowKey(v.Target.Kind.ToString(), v.Target.Command, v.Target.PathLengthFt, v.Verdict.BlockerCallsign)),
        ];
    }

    /// <summary>The list's targets as <see cref="PushTargetRowKey"/>s, in list order.</summary>
    internal static List<PushTargetRowKey> RowKeys(PushTargetList list) =>
        [.. list.Targets.Select(t => new PushTargetRowKey(t.Kind.ToString(), t.Command, t.PathLengthFt, t.BlockedBy))];

    /// <summary>What a push-target row is compared by: its kind, command, planned length and blocker.</summary>
    internal sealed record PushTargetRowKey(string Kind, string Command, double PathLengthFt, string? BlockedBy);

    /// <summary>A seed keyed current fills the list at once, not computing; the live plan still follows.</summary>
    [AvaloniaFact]
    public async Task PushbackTargets_Gate26_Seed_ShowsSeedAtOnce()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (_, _, ClientMenuHost host) = AtGate26(_seedCopy.NewSeed());

        PushTargetList list = host.GetPushbackTargets(Callsign);

        Assert.NotEmpty(list.Targets);
        Assert.False(list.Settled.IsCompleted);
        Assert.Contains(list.Targets, t => t.Command == "PUSH TE");
        Assert.All(list.Targets, t => Assert.Null(t.BlockedBy));

        await list.Settled.WaitAsync(SettleHangGuard, TestContext.Current.CancellationToken);
        Assert.Contains(list.Targets, t => t.Command == "PUSH TE");
    }

    /// <summary>
    /// A B738 parked on gate 27 sits beside gate 26's pushes onto TE and TC: the live check blocks both on it, from the
    /// seed at once and from the live plan once it lands.
    /// </summary>
    [AvaloniaFact]
    public async Task PushbackTargets_ParkedNeighbour_GreysTheTargetAndNamesIt()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (_, _, ClientMenuHost host) = AtGate26(_seedCopy.NewSeed(), ParkedAt("27", Gate27Neighbour));

        PushTargetList list = host.GetPushbackTargets(Callsign);

        Assert.Equal(Gate27Neighbour, Assert.Single(list.Targets, t => t.Command == "PUSH TE").BlockedBy);
        Assert.Equal(Gate27Neighbour, Assert.Single(list.Targets, t => t.Command == "PUSH TC").BlockedBy);

        await list.Settled.WaitAsync(SettleHangGuard, TestContext.Current.CancellationToken);
        Assert.Equal(Gate27Neighbour, Assert.Single(list.Targets, t => t.Command == "PUSH TE").BlockedBy);
    }

    [AvaloniaFact]
    public void PushbackTargets_NoStandName_IsEmpty()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (AircraftModel target, _, ClientMenuHost host) = AtGate26(_seedCopy.NewSeed());
        target.ParkingSpot = "";

        AssertSettledEmpty(host.GetPushbackTargets(Callsign));
    }

    [AvaloniaFact]
    public void GetPresetTaxiChoices_FromTheOakSidecar_KeepTheWalkableRouteWithItsViaDistanceAndPreview_AndDropTheOther()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "Taxiing", PositionOf(PresetTaxiNode));
        target.AssignedRunway = "30";
        var host = new ClientMenuHost(OakMain(target, []), target, new Border());

        IReadOnlyList<TaxiRouteRow> rows = host.GetPresetTaxiChoices(Callsign);

        TaxiRouteRow terminal = Assert.Single(rows, r => r.Name == "TERMINAL to 30");
        Assert.Equal(
            (TaxiRouteRow.PresetBadge, (string?)null, "via T U W", "TAXI T U W RWY 30"),
            (terminal.Badge, terminal.Reason, terminal.Via, terminal.Command)
        );
        Assert.Empty(terminal.Variants);

        // The preview is the resolved path, ending at a runway 30 hold short; the aircraft stands on the node it starts
        // at, so the distance is the path's own length, rounded to 50 ft.
        Assert.NotEmpty(terminal.Preview.Segments);
        GroundNodeDto end = Oak.Nodes.First(n => n.Id == terminal.Preview.Segments[^1].ToNodeId);
        Assert.Equal("RunwayHoldShort", end.Type);
        Assert.Contains("30", end.RunwayId);
        Assert.True(terminal.DistanceFt > 0, $"distance {terminal.DistanceFt}");
        Assert.Equal(TugMovePlanner.NoteDistanceFt(terminal.Preview.PrefixDistanceFt(terminal.Preview.Segments.Count)), terminal.DistanceFt);

        Assert.DoesNotContain(rows, r => r.Name == "30 to TERMINAL");
    }

    // --- Taxi to runway --------------------------------------------------------------------

    [AvaloniaFact]
    public async Task GetTaxiToRunwayChoices_AtAStand_DepartureEndsSearchedAtOpen_NearestAndFullLength_RestWhenAsked()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(Gate25Node));
        MainViewModel main = OakMain(target, []);
        main.ApplyActiveRunways(new Dictionary<string, List<string>> { ["OAK"] = ["D30", "28L"] });
        var host = new ClientMenuHost(main, target, new Border());

        TaxiToRunwayEndList list = host.GetTaxiToRunwayChoices(Callsign).RunwayEnds;
        IReadOnlyList<TaxiToRunwayEnd> ends = list.Ends;

        Assert.Equal([("30", TaxiToRunwayEndRole.Departure), ("28L", TaxiToRunwayEndRole.Departure)], Offered(ends));
        // The other ends are searched only when first asked for.
        Assert.Equal(TaxiToRunwayEndState.NotSearched, EndOf(ends, "28R").State);
        int searchesBeforeOther = main.Ground.TaxiToRunwayRouteSearches;
        await list.SearchRunwayOf("28R").WaitAsync(RunwaySearchHangGuard, TestContext.Current.CancellationToken);
        Assert.True(main.Ground.TaxiToRunwayRouteSearches > searchesBeforeOther, "Asking for 28R searched nothing");
        Assert.Equal(TaxiToRunwayEndState.Ready, EndOf(list.Ends, "28R").State);

        // Runway 30 from gate 25: the full-length entry at W1 first, then the intersections, one of them the nearest, each
        // opening For departure and Hold short of runway 30 and previewing its route to a runway 30 hold short.
        List<TaxiRouteRow> entries = EntryRows(EndOf(ends, "30"));
        Assert.Equal(("At W1", "full length"), (entries[0].Name, entries[0].Reason));
        TaxiRouteRow nearest = Assert.Single(entries, r => r.Reason == "nearest");
        Assert.True(nearest.DistanceFt < entries[0].DistanceFt, $"{nearest.DistanceFt} vs {entries[0].DistanceFt}");
        Assert.All(entries, AssertEntryTo("30"));
    }

    [AvaloniaFact]
    public void GetTaxiToRunwayChoices_NearestEntryIsTheFullLengthOne_OneRowNamesBoth()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "Taxiing", PositionOf(NodeBehind(HoldShort30On("W1"), "W1")));
        MainViewModel main = OakMain(target, []);
        main.ApplyActiveRunways(new Dictionary<string, List<string>> { ["OAK"] = ["30"] });
        var host = new ClientMenuHost(main, target, new Border());

        TaxiToRunwayEnd runway30 = EndOf(Ends(host, Callsign), "30");

        Assert.Equal(TaxiToRunwayEndRole.Departure, runway30.Role);
        List<TaxiRouteRow> entries = EntryRows(runway30);
        Assert.Equal(("At W1", "nearest, full length"), (entries[0].Name, entries[0].Reason));
        Assert.True(entries.Count > 1, "no intersection entry after the full-length one");
        Assert.All(entries.Skip(1), r => Assert.Null(r.Reason));
    }

    /// <summary>
    /// A B738 (7,545 ft takeoff distance) at a KOAK gate: W3 joins 12/30 about 2,900 ft from the 30 threshold and W2 about
    /// 500 ft from it, too little runway ahead for a departure on 12, so neither is offered for 12; both are for 30.
    /// </summary>
    [AvaloniaFact]
    public void GetTaxiToRunwayChoices_KoakTwelve_DropsShortIntersectionEntries()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(Gate25Node));
        MainViewModel main = OakMain(target, []);
        main.ApplyActiveRunways(new Dictionary<string, List<string>> { ["OAK"] = ["30", "12"] });
        var host = new ClientMenuHost(main, target, new Border());

        IReadOnlyList<TaxiToRunwayEnd> ends = Ends(host, Callsign);

        Assert.Equal([("30", TaxiToRunwayEndRole.Departure), ("12", TaxiToRunwayEndRole.Departure)], Offered(ends));
        List<string> twelve = [.. EntryRows(EndOf(ends, "12")).Select(r => r.Name)];
        Assert.NotEmpty(twelve);
        Assert.DoesNotContain("At W3", twelve);
        Assert.DoesNotContain("At W2", twelve);
        Assert.Contains("At W2", EntryRows(EndOf(ends, "30")).Select(r => r.Name));
        TaxiRouteRow w3 = Assert.Single(EntryRows(EndOf(ends, "30")), r => r.Name == "At W3");
        Assert.True(w3.AvailableFt >= 7545, $"W3 leaves {w3.AvailableFt} ft of runway 30");
    }

    [AvaloniaFact]
    public void GetTaxiToRunwayChoices_EntriesOrderedFromTheThreshold_FullLengthFirst()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(Gate25Node));
        MainViewModel main = OakMain(target, []);
        main.ApplyActiveRunways(new Dictionary<string, List<string>> { ["OAK"] = ["30"] });
        var host = new ClientMenuHost(main, target, new Border());

        List<TaxiRouteRow> entries = EntryRows(EndOf(Ends(host, Callsign), "30"));

        Assert.True(entries.Count >= 3, $"{entries.Count} entries");
        Assert.Equal(("At W1", "full length"), (entries[0].Name, entries[0].Reason));
        Assert.DoesNotContain(entries.Skip(1), r => r.Reason?.Contains("full length", StringComparison.Ordinal) == true);
        Assert.Null(entries[0].AvailableFt);
        Assert.All(entries.Skip(1), r => Assert.NotNull(r.AvailableFt));
        List<double> available = [.. entries.Skip(1).Select(r => r.AvailableFt!.Value)];
        Assert.Equal(available.OrderDescending(), available);
        // The full-length row and the nearest one are highlighted, and only they.
        Assert.Equal(2, entries.Count(r => r.IsHighlighted));
        Assert.Equal(entries.Where(r => r.Reason is not null).Select(r => r.Name), entries.Where(r => r.IsHighlighted).Select(r => r.Name));
    }

    [AvaloniaFact]
    public void GetTaxiToRunwayChoices_RemainingLengthRoundsDownTo50Ft()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(Gate25Node));
        MainViewModel main = OakMain(target, []);
        main.ApplyActiveRunways(new Dictionary<string, List<string>> { ["OAK"] = ["30"] });
        var host = new ClientMenuHost(main, target, new Border());

        TaxiRouteRow w3 = Assert.Single(EntryRows(EndOf(Ends(host, Callsign), "30")), r => r.Name == "At W3");

        double? remainingFt = main.Ground.RunwayRemainingFt("30", w3.Preview.Segments[^1].ToNodeId);
        Assert.NotNull(remainingFt);
        Assert.Equal(Math.Floor(remainingFt.Value / 50) * 50, w3.AvailableFt);
        Assert.Equal(7600, GroundViewModel.AvailableRunwayFt(7649.9));
        Assert.Equal(7650, GroundViewModel.AvailableRunwayFt(7650));
        Assert.Equal(7600, GroundViewModel.AvailableRunwayFt(7600.1));
    }

    /// <summary>
    /// From a KOAK gate, W3 leaves about 2,900 ft of runway 12 ahead: enough for a C172 (984 ft takeoff distance), not for a
    /// B738 (7,545 ft).
    /// </summary>
    [AvaloniaFact]
    public void GetTaxiToRunwayChoices_C172_KeepsAnIntersectionTheJetCannotUse()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel jet = GroundAircraft(Callsign, "At Parking", PositionOf(Gate25Node));
        jet.AssignedRunway = "12";
        AircraftModel cessna = GroundAircraft(OtherCallsign, "At Parking", PositionOf(Gate25Node));
        cessna.AircraftType = "C172";
        cessna.AssignedRunway = "12";

        TaxiToRunwayEnd jet12 = EndOf(Ends(new ClientMenuHost(OakMain(jet, []), jet, new Border()), Callsign), "12");
        TaxiToRunwayEnd cessna12 = EndOf(Ends(new ClientMenuHost(OakMain(cessna, []), cessna, new Border()), OtherCallsign), "12");

        Assert.Equal((TaxiToRunwayEndRole.Assigned, TaxiToRunwayEndRole.Assigned), (jet12.Role, cessna12.Role));
        Assert.DoesNotContain("At W3", EntryRows(jet12).Select(r => r.Name));
        TaxiRouteRow w3 = Assert.Single(EntryRows(cessna12), r => r.Name == "At W3");
        Assert.InRange(w3.AvailableFt!.Value, 984, 7545);
    }

    /// <summary>
    /// KOAK 15/33 is about 3,400 ft long: under a B738's 7,545 ft takeoff distance, so its full-length rows are still offered
    /// but tagged <c>short for type</c>; runway 30 (about 10,500 ft) is not, and neither is 15/33 for a C172 (984 ft).
    /// </summary>
    [AvaloniaFact]
    public async Task GetTaxiToRunwayChoices_RunwayShorterThanTheType_TagsFullLengthShortForType()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel jet = GroundAircraft(Callsign, "At Parking", PositionOf(Gate25Node));
        AircraftModel cessna = GroundAircraft(OtherCallsign, "At Parking", PositionOf(Gate25Node));
        cessna.AircraftType = "C172";

        IReadOnlyList<TaxiToRunwayEnd> jetEnds = await AllRunwayEnds(jet, Callsign);
        IReadOnlyList<TaxiToRunwayEnd> cessnaEnds = await AllRunwayEnds(cessna, OtherCallsign);

        List<TaxiRouteRow> jetShort = FullLengthRows(jetEnds, "15", "33");
        Assert.NotEmpty(jetShort);
        Assert.All(jetShort, r => Assert.Contains("full length · short for type", r.Reason, StringComparison.Ordinal));
        List<TaxiRouteRow> jet30 = FullLengthRows(jetEnds, "30");
        Assert.NotEmpty(jet30);
        Assert.All(jet30, r => Assert.DoesNotContain("short for type", r.Reason, StringComparison.Ordinal));
        List<TaxiRouteRow> cessnaShort = FullLengthRows(cessnaEnds, "15", "33");
        Assert.NotEmpty(cessnaShort);
        Assert.All(cessnaShort, r => Assert.DoesNotContain("short for type", r.Reason, StringComparison.Ordinal));
    }

    /// <summary>Every runway end <paramref name="target"/>'s Taxi to runway menu offers at KOAK, each one searched.</summary>
    private static async Task<IReadOnlyList<TaxiToRunwayEnd>> AllRunwayEnds(AircraftModel target, string callsign)
    {
        MainViewModel main = OakMain(target, []);
        main.ApplyActiveRunways(new Dictionary<string, List<string>> { ["OAK"] = ["30", "15", "33"] });
        return await SearchedEnds(new ClientMenuHost(main, target, new Border()).GetTaxiToRunwayChoices(callsign).RunwayEnds);
    }

    /// <summary>The full-length entry rows of the ends among <paramref name="ends"/> named <paramref name="designators"/>.</summary>
    private static List<TaxiRouteRow> FullLengthRows(IReadOnlyList<TaxiToRunwayEnd> ends, params string[] designators) =>
        [
            .. ends.Where(e => designators.Contains(e.Designator))
                .SelectMany(EntryRows)
                .Where(r => r.Reason?.Contains("full length", StringComparison.Ordinal) == true),
        ];

    /// <summary>
    /// An MD81 carries no takeoff distance in its profile, so an intersection needs half the runway ahead: from a KOAK gate
    /// W3 (about 2,900 ft of runway 12 ahead) and W2 (about 500 ft) are dropped for 12, an intersection in 12's departure
    /// half is kept.
    /// </summary>
    [AvaloniaFact]
    public void GetTaxiToRunwayChoices_TypeWithoutATakeoffDistance_KeepsIntersectionsWithHalfTheRunwayAhead()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(Gate25Node));
        target.AircraftType = "MD81";
        target.AssignedRunway = "12";
        var host = new ClientMenuHost(OakMain(target, []), target, new Border());

        TaxiToRunwayEnd runway12 = EndOf(Ends(host, Callsign), "12");

        Assert.Equal(0, AircraftProfileDatabase.Get("MD81")!.TakeoffDistance);
        Assert.Equal(TaxiToRunwayEndRole.Assigned, runway12.Role);
        List<TaxiRouteRow> entries = EntryRows(runway12);
        Assert.DoesNotContain("At W3", entries.Select(r => r.Name));
        Assert.DoesNotContain("At W2", entries.Select(r => r.Name));
        // 12/30 is about 10,500 ft long: every intersection kept leaves at least half of it (less up to 50 ft of rounding).
        List<TaxiRouteRow> intersections = [.. entries.Where(r => r.AvailableFt is not null)];
        Assert.NotEmpty(intersections);
        Assert.All(intersections, r => Assert.True(r.AvailableFt >= 5200, $"{r.Name} leaves {r.AvailableFt} ft"));
    }

    /// <summary>The runway entry rows of <paramref name="end"/>, in order, without its presets.</summary>
    private static List<TaxiRouteRow> EntryRows(TaxiToRunwayEnd end) => [.. end.Rows.Where(r => r.Badge == TaxiRouteRow.RunwayEntryBadge)];

    /// <summary>The runway ends <paramref name="host"/>'s Taxi to runway menu names for <paramref name="callsign"/>.</summary>
    private static IReadOnlyList<TaxiToRunwayEnd> Ends(ClientMenuHost host, string callsign) => host.GetTaxiToRunwayChoices(callsign).RunwayEnds.Ends;

    /// <summary>The assigned and departure ends of <paramref name="ends"/>, in order, each with its role.</summary>
    private static List<(string Designator, TaxiToRunwayEndRole Role)> Offered(IReadOnlyList<TaxiToRunwayEnd> ends) =>
        [.. ends.Where(e => e.Role != TaxiToRunwayEndRole.Other).Select(e => (e.Designator, e.Role))];

    /// <summary><paramref name="list"/>'s ends once every runway not searched at open has been searched.</summary>
    private static async Task<IReadOnlyList<TaxiToRunwayEnd>> SearchedEnds(TaxiToRunwayEndList list)
    {
        foreach (TaxiToRunwayEnd end in list.Ends.ToList())
        {
            if (end.State == TaxiToRunwayEndState.NotSearched)
            {
                await list.SearchRunwayOf(end.Designator).WaitAsync(RunwaySearchHangGuard, TestContext.Current.CancellationToken);
            }
        }

        return list.Ends;
    }

    [AvaloniaFact]
    public void GetTaxiToRunwayChoices_NoActiveOrAssignedRunway_SearchesNothingAtOpen_EndsByRunwayNumber()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(Gate25Node));
        MainViewModel main = OakMain(target, []);
        var host = new ClientMenuHost(main, target, new Border());
        int before = main.Ground.TaxiToRunwayRouteSearches;

        IReadOnlyList<TaxiToRunwayEnd> ends = Ends(host, Callsign);

        Assert.Equal(before, main.Ground.TaxiToRunwayRouteSearches);
        Assert.Equal(["10L", "10R", "12", "15", "28L", "28R", "30", "33"], ends.Select(e => e.Designator));
        Assert.Empty(Offered(ends));
        Assert.All(ends, e => Assert.Equal(TaxiToRunwayEndState.NotSearched, e.State));
    }

    [AvaloniaFact]
    public void GetTaxiToRunwayChoices_AssignedRunwayNotActive_ComesFirst_WithItsPresets_AndArrivalEndsAreNotDepartureEnds()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "Taxiing", PositionOf(PresetTaxiNode));
        target.AssignedRunway = "30";
        MainViewModel main = OakMain(target, []);
        main.ApplyActiveRunways(new Dictionary<string, List<string>> { ["OAK"] = ["28L", "A28R"] });
        var host = new ClientMenuHost(main, target, new Border());

        IReadOnlyList<TaxiToRunwayEnd> ends = Ends(host, Callsign);

        Assert.Equal([("30", TaxiToRunwayEndRole.Assigned), ("28L", TaxiToRunwayEndRole.Departure)], Offered(ends));
        Assert.Equal(TaxiToRunwayEndRole.Other, EndOf(ends, "28R").Role);
        TaxiRouteRow preset = EndOf(ends, "30").Rows[^1];
        Assert.Equal((TaxiRouteRow.PresetBadge, "TERMINAL to 30", "TAXI T U W RWY 30"), (preset.Badge, preset.Name, preset.Command));
    }

    [AvaloniaFact]
    public async Task SearchRunwayOf_AfterClearLayout_FindsNoRow_AndLeavesTheEndsToSearchAgain()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(Gate25Node));
        MainViewModel main = OakMain(target, []);
        main.ApplyActiveRunways(new Dictionary<string, List<string>> { ["OAK"] = ["D30", "28L"] });
        TaxiToRunwayEndList list = new ClientMenuHost(main, target, new Border()).GetTaxiToRunwayChoices(Callsign).RunwayEnds;

        main.Ground.ClearLayout();
        await list.SearchRunwayOf("28R").WaitAsync(RunwaySearchHangGuard, TestContext.Current.CancellationToken);

        Assert.Equal((TaxiToRunwayEndState.NotSearched, 0), (EndOf(list.Ends, "28R").State, EndOf(list.Ends, "28R").Rows.Count));
    }

    [AvaloniaFact]
    public async Task GetTaxiToRunwayChoices_HoldingShortAtABar_HidesThatBarsRow()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        GroundNodeDto bar = Runway30HoldShortNode;
        AircraftModel target = GroundAircraft(Callsign, $"Holding Short {bar.RunwayId}", PositionOf(bar));
        target.AssignedRunway = "30";
        var host = new ClientMenuHost(OakMain(target, []), target, new Border());

        IReadOnlyList<TaxiToRunwayEnd> ends = await SearchedEnds(host.GetTaxiToRunwayChoices(Callsign).RunwayEnds);

        Assert.Equal([("30", TaxiToRunwayEndRole.Assigned)], Offered(ends));
        Assert.Contains(EndOf(ends, "30").Rows, r => r.Badge == TaxiRouteRow.RunwayEntryBadge);
        List<TaxiRouteRow> rows = [.. ends.SelectMany(e => e.Rows)];
        Assert.DoesNotContain(rows, r => r.Preview.Segments[^1].ToNodeId == bar.Id);
    }

    [AvaloniaFact]
    public void GetTaxiToRunwayChoices_SecondOpenFromTheSameNode_ReusesTheRoutesItFound()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(Gate25Node));
        target.AssignedRunway = "30";
        MainViewModel main = OakMain(target, []);

        TaxiToRunwayMenu first = new ClientMenuHost(main, target, new Border()).GetTaxiToRunwayChoices(Callsign);
        int searches = main.Ground.TaxiToRunwayRouteSearches;
        TaxiToRunwayMenu second = new ClientMenuHost(main, target, new Border()).GetTaxiToRunwayChoices(Callsign);

        Assert.True(searches > 0, "the first open searched no route");
        Assert.Equal(searches, main.Ground.TaxiToRunwayRouteSearches);
        Assert.Equal(Summary(first), Summary(second));
    }

    [AvaloniaFact]
    public void GetTaxiToRunwayChoices_ArrivalsOnlyActiveList_NamesNoDepartureEnd_AsNoList()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(Gate25Node));
        MainViewModel main = OakMain(target, []);
        TaxiToRunwayMenu noList = new ClientMenuHost(main, target, new Border()).GetTaxiToRunwayChoices(Callsign);
        main.ApplyActiveRunways(new Dictionary<string, List<string>> { ["OAK"] = ["A30", "A28R"] });

        TaxiToRunwayMenu menu = new ClientMenuHost(main, target, new Border()).GetTaxiToRunwayChoices(Callsign);

        Assert.Empty(Offered(menu.RunwayEnds.Ends));
        Assert.Equal(Summary(noList), Summary(menu));
    }

    [AvaloniaFact]
    public void GetTaxiToRunwayChoices_NewLayout_SearchesTheRoutesAgain()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(Gate25Node));
        target.AssignedRunway = "30";
        MainViewModel main = OakMain(target, []);
        _ = new ClientMenuHost(main, target, new Border()).GetTaxiToRunwayChoices(Callsign);
        int firstSearches = main.Ground.TaxiToRunwayRouteSearches;

        main.Ground.SetLayoutForTesting(Oak);
        _ = new ClientMenuHost(main, target, new Border()).GetTaxiToRunwayChoices(Callsign);

        Assert.True(firstSearches > 0, "the first open searched no route");
        Assert.Equal(2 * firstSearches, main.Ground.TaxiToRunwayRouteSearches);
    }

    [AvaloniaFact]
    public async Task GetTaxiToRunwayChoices_HoldingShortWhereAPresetEnds_HidesThatPreset()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel atStart = GroundAircraft(Callsign, "Taxiing", PositionOf(PresetTaxiNode));
        TaxiRouteRow terminal = Assert.Single(
            new ClientMenuHost(OakMain(atStart, []), atStart, new Border()).GetPresetTaxiChoices(Callsign),
            r => r.Name == "TERMINAL to 30"
        );
        GroundNodeDto bar = Oak.Nodes.First(n => n.Id == terminal.Preview.Segments[^1].ToNodeId);
        AircraftModel holding = GroundAircraft(OtherCallsign, $"Holding Short {bar.RunwayId}", PositionOf(bar));
        holding.AssignedRunway = "30";
        var host = new ClientMenuHost(OakMain(holding, []), holding, new Border());

        IReadOnlyList<TaxiToRunwayEnd> ends = await SearchedEnds(host.GetTaxiToRunwayChoices(OtherCallsign).RunwayEnds);

        Assert.DoesNotContain(host.GetPresetTaxiChoices(OtherCallsign), r => r.Preview.Segments[^1].ToNodeId == bar.Id);
        Assert.DoesNotContain(ends.SelectMany(e => e.Rows), r => r.Preview.Segments[^1].ToNodeId == bar.Id);
    }

    // --- Taxi to runway: every runway end, searched one runway at a time --------------------

    /// <summary>How long a test waits for a background runway search to land: a hang guard a passing run never reaches.</summary>
    private static readonly TimeSpan RunwaySearchHangGuard = TimeSpan.FromMinutes(5);

    [AvaloniaFact]
    public void GetTaxiToRunwayChoices_Koak_NamesEveryRunwayEnd_AssignedThenDepartureThenRunwayNumber()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (AircraftModel target, MainViewModel main) = AtGate25("30", ["28L"]);
        var host = new ClientMenuHost(main, target, new Border());

        IReadOnlyList<TaxiToRunwayEnd> ends = host.GetTaxiToRunwayChoices(Callsign).RunwayEnds.Ends;

        Assert.Equal(["30", "28L", "10L", "10R", "12", "15", "28R", "33"], ends.Select(e => e.Designator));
        Assert.Equal(
            [TaxiToRunwayEndRole.Assigned, TaxiToRunwayEndRole.Departure, .. Enumerable.Repeat(TaxiToRunwayEndRole.Other, 6)],
            ends.Select(e => e.Role)
        );
        // The assigned and departure runways (12/30, 10R/28L) are searched at open, both ends each; the rest wait.
        Assert.All(ends.Where(e => e.Designator is "30" or "12" or "28L" or "10R"), e => Assert.NotEqual(TaxiToRunwayEndState.NotSearched, e.State));
        Assert.All(ends.Where(e => e.Designator is "10L" or "28R" or "15" or "33"), e => Assert.Equal(TaxiToRunwayEndState.NotSearched, e.State));
        Assert.Equal(TaxiToRunwayEndState.Ready, EndOf(ends, "30").State);
        Assert.All(ends.Where(e => e.State != TaxiToRunwayEndState.Ready), e => Assert.Empty(e.Rows));
    }

    [AvaloniaFact]
    public void GetTaxiToRunwayChoices_NoDepartureRunway_SearchesOnlyTheAssignedRunwayAtOpen()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (AircraftModel target, MainViewModel main) = AtGate25("30", []);
        int before = main.Ground.TaxiToRunwayRouteSearches;

        IReadOnlyList<TaxiToRunwayEnd> ends = main.Ground.GetTaxiToRunwayChoices(target).RunwayEnds.Ends;

        Assert.Equal(HoldShortsSearched(main, "30"), main.Ground.TaxiToRunwayRouteSearches - before);
        Assert.Equal(["30", "10L", "10R", "12", "15", "28L", "28R", "33"], ends.Select(e => e.Designator));
        Assert.Equal(TaxiToRunwayEndState.Ready, EndOf(ends, "30").State);
        Assert.All(ends.Where(e => e.Designator is not ("30" or "12")), e => Assert.Equal(TaxiToRunwayEndState.NotSearched, e.State));
    }

    [AvaloniaFact]
    public async Task SearchRunwayOf_AnOtherRunway_SearchesThatRunwayOnce_AndSettlesBothEnds()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (AircraftModel target, MainViewModel main) = AtGate25("30", []);
        TaxiToRunwayEndList list = main.Ground.GetTaxiToRunwayChoices(target).RunwayEnds;
        int before = main.Ground.TaxiToRunwayRouteSearches;

        Task search = list.SearchRunwayOf("28R");

        bool completedOnUiThread = false;
        Task observed = search.ContinueWith(
            _ => completedOnUiThread = Dispatcher.UIThread.CheckAccess(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );

        Assert.False(search.IsCompleted);
        Assert.Equal(TaxiToRunwayEndState.Searching, EndOf(list.Ends, "28R").State);
        Assert.Equal(TaxiToRunwayEndState.Searching, EndOf(list.Ends, "10L").State);
        Assert.Equal(TaxiToRunwayEndState.NotSearched, EndOf(list.Ends, "15").State);

        await observed.WaitAsync(RunwaySearchHangGuard, TestContext.Current.CancellationToken);

        Assert.True(completedOnUiThread, "the search completed off the UI thread");
        int searches = main.Ground.TaxiToRunwayRouteSearches - before;
        Assert.Equal(HoldShortsSearched(main, "28R"), searches);
        Assert.Equal(TaxiToRunwayEndState.Ready, EndOf(list.Ends, "28R").State);
        Assert.True(
            EndOf(list.Ends, "10L").State is TaxiToRunwayEndState.Ready or TaxiToRunwayEndState.Empty,
            $"10L is {EndOf(list.Ends, "10L").State}"
        );
        Assert.Equal(TaxiToRunwayEndState.NotSearched, EndOf(list.Ends, "15").State);

        // Asking for the other end of a searched runway searches nothing more.
        Assert.True(list.SearchRunwayOf("10L").IsCompletedSuccessfully);
        Assert.Equal(searches, main.Ground.TaxiToRunwayRouteSearches - before);

        // The rows are the ones an open with 28R a departure runway searches at once.
        main.ApplyActiveRunways(new Dictionary<string, List<string>> { ["OAK"] = ["28R"] });
        TaxiToRunwayEnd departure28R = EndOf(main.Ground.GetTaxiToRunwayChoices(target).RunwayEnds.Ends, "28R");
        Assert.Equal(TaxiToRunwayEndRole.Departure, departure28R.Role);
        Assert.Equal(TaxiRowKeys(departure28R.Rows), TaxiRowKeys(EndOf(list.Ends, "28R").Rows));
    }

    /// <summary>Each row's name, reason, distance and command, in order.</summary>
    private static List<string> TaxiRowKeys(IReadOnlyList<TaxiRouteRow> rows) =>
        [.. rows.Select(r => $"{r.Name} {r.Reason} {r.DistanceFt} {r.Command}")];

    [AvaloniaFact]
    public async Task SearchRunwayOf_BothEndsOfOneRunwayAskedAtOnce_RunOneSearch()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (AircraftModel target, MainViewModel main) = AtGate25("30", []);
        TaxiToRunwayEndList list = main.Ground.GetTaxiToRunwayChoices(target).RunwayEnds;
        int before = main.Ground.TaxiToRunwayRouteSearches;

        Task first = list.SearchRunwayOf("28R");
        Task second = list.SearchRunwayOf("10L");

        Assert.Same(first, second);
        await first.WaitAsync(RunwaySearchHangGuard, TestContext.Current.CancellationToken);
        Assert.Equal(HoldShortsSearched(main, "28R"), main.Ground.TaxiToRunwayRouteSearches - before);
    }

    /// <summary>From gate 25 every hold short of 10R/28L is at or toward the 10R threshold: none is an entry for 10R.</summary>
    [AvaloniaFact]
    public async Task SearchRunwayOf_Koak10RFromGate25_IsEmpty_And28LIsReady()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (AircraftModel target, MainViewModel main) = AtGate25("30", []);
        TaxiToRunwayEndList list = main.Ground.GetTaxiToRunwayChoices(target).RunwayEnds;

        await list.SearchRunwayOf("10R").WaitAsync(RunwaySearchHangGuard, TestContext.Current.CancellationToken);

        TaxiToRunwayEnd tenRight = EndOf(list.Ends, "10R");
        Assert.Equal((TaxiToRunwayEndState.Empty, 0), (tenRight.State, tenRight.Rows.Count));
        Assert.Equal(TaxiToRunwayEndState.Ready, EndOf(list.Ends, "28L").State);
    }

    /// <summary>
    /// A background search of the same runway that an open call searches synchronously: the open call waits for the running
    /// search rather than running its own, and both lists settle on the same rows.
    /// </summary>
    [AvaloniaFact]
    public async Task SearchRunwayOf_BackgroundSearchOfTheSameRunwayRunning_TheOpenCallWaitsForIt()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (AircraftModel target, MainViewModel main) = AtGate25("30", []);
        TaxiToRunwayEndList list = main.Ground.GetTaxiToRunwayChoices(target).RunwayEnds;
        int before = main.Ground.TaxiToRunwayRouteSearches;
        Task search = list.SearchRunwayOf("28R");

        AircraftModel assigned28R = GroundAircraft(OtherCallsign, "At Parking", PositionOf(Gate25Node));
        assigned28R.AssignedRunway = "28R";
        TaxiToRunwayEndList opened = main.Ground.GetTaxiToRunwayChoices(assigned28R).RunwayEnds;

        Assert.Equal(TaxiToRunwayEndState.Ready, EndOf(opened.Ends, "28R").State);
        Assert.Equal(HoldShortsSearched(main, "28R"), main.Ground.TaxiToRunwayRouteSearches - before);

        await search.WaitAsync(RunwaySearchHangGuard, TestContext.Current.CancellationToken);

        Assert.Equal(HoldShortsSearched(main, "28R"), main.Ground.TaxiToRunwayRouteSearches - before);
        Assert.Equal(TaxiRowKeys(EndOf(opened.Ends, "28R").Rows), TaxiRowKeys(EndOf(list.Ends, "28R").Rows));
    }

    /// <summary>
    /// Two end lists of one aircraft both ask for 28R before either is awaited: the route cache runs one search, and both
    /// lists settle on the same ends.
    /// </summary>
    [AvaloniaFact]
    public async Task SearchRunwayOf_TwoListsOfOneAircraftAskTheSameRunway_RunOneSearch()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (AircraftModel target, MainViewModel main) = AtGate25("30", []);
        TaxiToRunwayEndList first = main.Ground.GetTaxiToRunwayChoices(target).RunwayEnds;
        TaxiToRunwayEndList second = main.Ground.GetTaxiToRunwayChoices(target).RunwayEnds;
        int before = main.Ground.TaxiToRunwayRouteSearches;

        Task askedFirst = first.SearchRunwayOf("28R");
        Task askedSecond = second.SearchRunwayOf("28R");

        await Task.WhenAll(askedFirst, askedSecond).WaitAsync(RunwaySearchHangGuard, TestContext.Current.CancellationToken);

        Assert.Equal(HoldShortsSearched(main, "28R"), main.Ground.TaxiToRunwayRouteSearches - before);
        Assert.Equal(EndKeys(first), EndKeys(second));
    }

    /// <summary>
    /// A result for a layout that has since changed is dropped, and 28R's ends go back to NotSearched: a later select
    /// searches again rather than leaving them Searching forever.
    /// </summary>
    [AvaloniaFact]
    public async Task SearchRunwayOf_LayoutChangesBeforeTheSearchLands_LeavesTheEndsToSearchAgain()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (AircraftModel target, MainViewModel main) = AtGate25("30", []);
        TaxiToRunwayEndList list = main.Ground.GetTaxiToRunwayChoices(target).RunwayEnds;
        Task search = list.SearchRunwayOf("28R");

        main.Ground.SetLayoutForTesting(Oak);
        await search.WaitAsync(RunwaySearchHangGuard, TestContext.Current.CancellationToken);

        Assert.Equal(TaxiToRunwayEndState.NotSearched, EndOf(list.Ends, "28R").State);
        Assert.Equal(TaxiToRunwayEndState.NotSearched, EndOf(list.Ends, "10L").State);
        Assert.Empty(EndOf(list.Ends, "28R").Rows);
    }

    /// <summary>
    /// Six background searches at once, three runways each for a jet and a C172 (different cache keys on one layout): each
    /// runway's ends come out as a one-at-a-time run on a fresh view model finds them, with the same number of route searches.
    /// </summary>
    [AvaloniaFact]
    public async Task SearchRunwayOf_ManyRunwaysAtOnce_MatchOneAtATime()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        string[] asked = ["28R", "10R", "15"];
        (MainViewModel concurrentMain, TaxiToRunwayEndList[] concurrent) = JetAndCessnaEnds();
        int concurrentBefore = concurrentMain.Ground.TaxiToRunwayRouteSearches;

        Task[] searches = [.. concurrent.SelectMany(list => asked.Select(list.SearchRunwayOf))];
        await Task.WhenAll(searches).WaitAsync(RunwaySearchHangGuard, TestContext.Current.CancellationToken);

        (MainViewModel sequentialMain, TaxiToRunwayEndList[] sequential) = JetAndCessnaEnds();
        int sequentialBefore = sequentialMain.Ground.TaxiToRunwayRouteSearches;
        foreach (TaxiToRunwayEndList list in sequential)
        {
            foreach (string end in asked)
            {
                await list.SearchRunwayOf(end).WaitAsync(RunwaySearchHangGuard, TestContext.Current.CancellationToken);
            }
        }

        Assert.All(concurrent.Concat(sequential).SelectMany(list => list.Ends), e => Assert.NotEqual(TaxiToRunwayEndState.Searching, e.State));
        Assert.Equal(sequential.Select(EndKeys), concurrent.Select(EndKeys));
        Assert.NotEqual(EndKeys(sequential[0]), EndKeys(sequential[1]));
        Assert.Equal(
            sequentialMain.Ground.TaxiToRunwayRouteSearches - sequentialBefore,
            concurrentMain.Ground.TaxiToRunwayRouteSearches - concurrentBefore
        );
    }

    /// <summary>A B738 and a C172 parked at gate 25 on one fresh KOAK view model, assigned 30, and each one's runway ends.</summary>
    private static (MainViewModel Main, TaxiToRunwayEndList[] Ends) JetAndCessnaEnds()
    {
        (AircraftModel jet, MainViewModel main) = AtGate25("30", []);
        AircraftModel cessna = GroundAircraft(OtherCallsign, "At Parking", PositionOf(Gate25Node));
        cessna.AircraftType = "C172";
        cessna.AssignedRunway = "30";
        return (main, [main.Ground.GetTaxiToRunwayChoices(jet).RunwayEnds, main.Ground.GetTaxiToRunwayChoices(cessna).RunwayEnds]);
    }

    /// <summary>A B738 parked at KOAK gate 25 assigned <paramref name="assignedRunway"/>, the room naming <paramref name="activeRunways"/>.</summary>
    private static (AircraftModel Target, MainViewModel Main) AtGate25(string assignedRunway, string[] activeRunways)
    {
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(Gate25Node));
        target.AssignedRunway = assignedRunway;
        MainViewModel main = OakMain(target, []);
        if (activeRunways.Length > 0)
        {
            main.ApplyActiveRunways(new Dictionary<string, List<string>> { ["OAK"] = [.. activeRunways] });
        }

        return (target, main);
    }

    /// <summary>
    /// The hold shorts one search of <paramref name="end"/>'s runway runs a route search to: every hold short of the runway
    /// that is not on another runway's pavement only (the start, a stand, is none of them).
    /// </summary>
    private static int HoldShortsSearched(MainViewModel main, string end) =>
        main.Ground.DomainLayout!.Nodes.Values.Count(n =>
            (n.Type == GroundNodeType.RunwayHoldShort)
            && (n.RunwayId is { } id)
            && (id.Contains(end))
            && (!GroundViewModel.LiesOnRunwayPavementOnly(n))
        );

    private static TaxiToRunwayEnd EndOf(IReadOnlyList<TaxiToRunwayEnd> ends, string designator) =>
        Assert.Single(ends, e => e.Designator == designator);

    /// <summary>Every end's designator, state and rows' name, reason, distance and command, in order.</summary>
    private static List<string> EndKeys(TaxiToRunwayEndList list) =>
        [.. list.Ends.Select(e => $"{e.Designator} {e.State}: {string.Join(" | ", TaxiRowKeys(e.Rows))}")];

    // --- Taxi to runway: the runway's own bars, its centreline and bars without a name ------

    [AvaloniaFact]
    public void GetTaxiToRunwayChoices_KoakSecondNamedEnd_FirstVariantIsForDeparture()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(Gate25Node));
        target.AssignedRunway = "12";
        var host = new ClientMenuHost(OakMain(target, []), target, new Border());

        TaxiToRunwayEnd runway12 = EndOf(Ends(host, Callsign), "12");

        Assert.Equal(TaxiToRunwayEndRole.Assigned, runway12.Role);
        List<TaxiRouteRow> entries = [.. runway12.Rows.Where(r => r.Badge == TaxiRouteRow.RunwayEntryBadge)];
        Assert.NotEmpty(entries);
        Assert.All(entries, row => AssertNeverCrossesItsOwnRunway(row.Variants, "12", ["12", "30"]));
        Assert.All(entries, row => Assert.Equal(row.Variants[0].Command, row.Command));
    }

    [AvaloniaFact]
    public void GetTaxiToRunwayChoices_KsfoOneLeft_FirstVariantIsForDeparture()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(Sfo.Nodes.First(n => (n.Type == "Parking") && (n.Name == "A10"))));
        target.AssignedRunway = "1L";
        MainViewModel main = MainWith(target, []);
        main.Ground.SetLayoutForTesting(Sfo);
        var host = new ClientMenuHost(main, target, new Border());

        TaxiToRunwayEnd runway1L = EndOf(Ends(host, Callsign), "1L");

        Assert.Equal(TaxiToRunwayEndRole.Assigned, runway1L.Role);
        List<TaxiRouteRow> entries = [.. runway1L.Rows.Where(r => r.Badge == TaxiRouteRow.RunwayEntryBadge)];
        Assert.NotEmpty(entries);
        Assert.All(entries, row => AssertNeverCrossesItsOwnRunway(row.Variants, "1L", ["1L", "01L", "19R"]));
    }

    /// <summary>The point menu's taxi to a threshold click on the runway's second-named end goes through the same variants.</summary>
    [AvaloniaFact]
    public void TaxiChoices_ThresholdClickOnTheSecondNamedEnd_NeverCrossesItsOwnRunway()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(PushbackFaceNode));
        var host = new ClientMenuHost(OakMain(target, []), target, new Border());
        var runway = RunwayIdentifier.Parse(Runway30HoldShortNode.RunwayId!);

        IReadOnlyList<MenuCommandChoice> routes = RouteChoices(host.GetTaxiChoices(Callsign, Runway30HoldShortNode, runway.End2));

        Assert.Equal("12", runway.End2);
        Assert.NotEmpty(routes);
        Assert.All(routes, route => AssertNeverCrossesItsOwnRunway(route.Children, runway.End2, [runway.End1, runway.End2]));
    }

    [AvaloniaFact]
    public async Task GetTaxiToRunwayChoices_HoldingShortWithBarAcrossTheRunway_OffersNoRouteOverThatRunway()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (GroundNodeDto bar, GroundNodeDto across) = BarsFacingAcrossTheRunway();
        var runway = RunwayIdentifier.Parse(bar.RunwayId!);
        AircraftModel target = GroundAircraft(Callsign, $"Holding Short {bar.RunwayId}", PositionOf(bar));
        var host = new ClientMenuHost(OakMain(target, []), target, new Border());

        IReadOnlyList<TaxiToRunwayEnd> ends = await SearchedEnds(host.GetTaxiToRunwayChoices(Callsign).RunwayEnds);

        List<TaxiRouteRow> ownRows = [.. ends.Where(e => runway.Contains(e.Designator)).SelectMany(e => e.Rows)];
        Assert.DoesNotContain(ownRows, r => r.Preview.Segments[^1].ToNodeId == across.Id);
        Assert.All(
            ownRows,
            r =>
                Assert.DoesNotContain(r.Preview.Segments, s => s.Edge.ToNode.Edges.Any(e => (e.IsRunwayCenterline) && (e.MatchesRunway(runway.End1))))
        );

        // Holding at W4, back along W to the full-length bar at W1 is a taxi, not a crossing: still offered.
        GroundNodeDto w4 = HoldShort30On("W4");
        AircraftModel atW4 = GroundAircraft(OtherCallsign, $"Holding Short {w4.RunwayId}", PositionOf(w4));
        atW4.AssignedRunway = "30";
        TaxiToRunwayEnd runway30 = EndOf(Ends(new ClientMenuHost(OakMain(atW4, []), atW4, new Border()), OtherCallsign), "30");
        Assert.Equal(TaxiToRunwayEndRole.Assigned, runway30.Role);
        Assert.Contains(runway30.Rows, r => r.Preview.Segments[^1].ToNodeId == HoldShort30On("W1").Id);
    }

    /// <summary>
    /// No hold short of KOAK or KSFO lies on runway pavement only, and no KOAK node has only centreline edges, so the rule
    /// is shown on real KOAK data: every KOAK hold short is an entry, and a hold short at a runway 30 centreline junction
    /// holding only that junction's two real centreline edges is not.
    /// </summary>
    [AvaloniaFact]
    public void LiesOnRunwayPavementOnly_OnlyABarWhoseEveryEdgeIsCentreline()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "oak.geojson");
        AirportGroundLayout layout = GeoJsonParser.Parse(
            "OAK",
            File.ReadAllText(path),
            null,
            new LayoutInputs([], VideoMap: null),
            FilletMode.Standard
        );
        GroundNode junction = layout.Nodes.Values.First(n => n.Edges.Count(e => (e.IsRunwayCenterline) && (e.MatchesRunway("30"))) >= 2);
        var onPavement = new GroundNode
        {
            Id = junction.Id,
            Position = junction.Position,
            Type = GroundNodeType.RunwayHoldShort,
            Edges = [.. junction.Edges.Where(e => (e.IsRunwayCenterline) && (e.MatchesRunway("30")))],
        };

        Assert.True(GroundViewModel.LiesOnRunwayPavementOnly(onPavement));
        Assert.All(
            layout.Nodes.Values.Where(n => n.Type == GroundNodeType.RunwayHoldShort),
            bar => Assert.False(GroundViewModel.LiesOnRunwayPavementOnly(bar))
        );
    }

    /// <summary>
    /// No hold short of KOAK or KSFO sits on an unnamed taxiway, so the name fallback is shown on a real KOAK route: without
    /// the bar's taxiway the entry is named by the route's last taxiway, and a route naming none gives no name.
    /// </summary>
    [AvaloniaFact]
    public void EntryName_WithoutTheBarsTaxiway_IsTheRoutesLastTaxiway_AndNoneWithoutOne()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(Gate25Node));
        MainViewModel main = OakMain(target, []);
        int from = main.Ground.GetAircraftNearestNodeId(target)!.Value;
        TaxiRoute route = main.Ground.FindRoutesToNode(
            from,
            HoldShort30On("W1").Id,
            GroundViewModel.CategoryFor(target),
            GroundViewModel.WakeClassFor(target)
        )[0];
        string lastTaxiway = main.Ground.GetTaxiwayDisplayName(route).Split(' ')[^1];

        Assert.Equal("At W1", GroundViewModel.EntryName("W1", route));
        Assert.Equal($"At {lastTaxiway}", GroundViewModel.EntryName(null, route));
        Assert.Null(GroundViewModel.EntryName(null, new TaxiRoute { Segments = [], HoldShortPoints = [] }));
    }

    /// <summary>
    /// <paramref name="variants"/> taxi to <paramref name="end"/>: the first is For departure to it, and none holds short of
    /// or crosses the runway itself under any spelling of its ends (<paramref name="ownSpellings"/>), but the hold short of
    /// <paramref name="end"/> the Hold short variants end at.
    /// </summary>
    private static void AssertNeverCrossesItsOwnRunway(IReadOnlyList<MenuCommandChoice> variants, string end, string[] ownSpellings)
    {
        Assert.StartsWith($"For Departure {end}", variants[0].Label);
        foreach (MenuCommandChoice variant in variants.Where(v => !ReferenceEquals(v, MenuCommandChoice.Separator)))
        {
            string command = variant.Command ?? "";
            foreach (string spelling in ownSpellings)
            {
                Assert.DoesNotContain($"CROSS {spelling}", variant.Label);
                Assert.DoesNotContain($", HS {spelling}", variant.Label);
                Assert.DoesNotContain($"CROSS {spelling}", command);
                Assert.True(
                    (spelling == end) || !command.Contains($" HS {spelling}", StringComparison.Ordinal),
                    $"'{command}' holds short of {spelling}"
                );
            }
        }
    }

    /// <summary>
    /// The first hold short of KOAK, in layout order, with a twin: another hold short of the same runway on a taxiway it
    /// also sits on, under 700 ft away, across the runway.
    /// </summary>
    private static (GroundNodeDto Bar, GroundNodeDto Across) BarsFacingAcrossTheRunway()
    {
        List<GroundNodeDto> bars = [.. Oak.Nodes.Where(n => (n.Type == "RunwayHoldShort") && (n.RunwayId is not null))];
        return bars.SelectMany(bar => bars.Where(across => IsTwin(bar, across)).Select(across => (bar, across))).First();
    }

    private static bool IsTwin(GroundNodeDto bar, GroundNodeDto across) =>
        (across.Id != bar.Id)
        && (RunwayIdentifier.Parse(across.RunwayId!) == RunwayIdentifier.Parse(bar.RunwayId!))
        && (GeoMath.DistanceNm(bar.Latitude, bar.Longitude, across.Latitude, across.Longitude) * GeoMath.FeetPerNm < 700)
        && (TaxiwaysAt(across.Id).Overlaps(TaxiwaysAt(bar.Id)));

    /// <summary>The non-runway taxiway names on the links at <paramref name="nodeId"/> of KOAK.</summary>
    private static HashSet<string> TaxiwaysAt(int nodeId) => [.. LinkNamesOf(nodeId).Where(name => (name.Length > 0) && !IsCentrelineName(name))];

    /// <summary>A runway centreline's name (<c>RWY28L/10R</c>), not a runway crossing link's.</summary>
    private static bool IsCentrelineName(string name) =>
        name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase) && !name.Contains(":link", StringComparison.OrdinalIgnoreCase);

    private static GroundLayoutDto Sfo => MenuGoldenFixtures.SfoLayoutForClient;

    /// <summary>Every end's designator, role, state and rows' name, reason, distance and command, in order.</summary>
    private static List<string> Summary(TaxiToRunwayMenu menu) =>
        [.. menu.RunwayEnds.Ends.Select(e => $"{e.Designator} {e.Role} {e.State}: {string.Join(" | ", TaxiRowKeys(e.Rows))}")];

    /// <summary>
    /// A runway entry to <paramref name="runway"/>: named by its hold short's taxiway, routed by a via, a distance away,
    /// showing its first variant's command, opening For departure and Hold short of the runway, and previewing its route
    /// to a hold short of the runway.
    /// </summary>
    private static Action<TaxiRouteRow> AssertEntryTo(string runway) =>
        row =>
        {
            Assert.StartsWith("At ", row.Name);
            Assert.StartsWith("via ", row.Via);
            Assert.True(row.DistanceFt > 0, $"{row.Name}: distance {row.DistanceFt}");
            Assert.Equal(row.Variants[0].Command, row.Command);
            Assert.Equal($"For Departure {runway}", row.Variants[0].Label);
            Assert.EndsWith($" {runway}", row.Command);
            Assert.Contains(row.Variants, v => ReferenceEquals(v, MenuCommandChoice.Separator));
            Assert.Contains(row.Variants, v => v.Command?.EndsWith($" HS {runway}", StringComparison.Ordinal) == true);
            GroundNodeDto end = Oak.Nodes.First(n => n.Id == row.Preview.Segments[^1].ToNodeId);
            Assert.Equal("RunwayHoldShort", end.Type);
            Assert.True(RunwayIdentifier.Parse(end.RunwayId!).Contains(runway), $"{row.Name} ends at a hold short of {end.RunwayId}");
        };

    [AvaloniaFact]
    public void PushbackAndPresetChoices_NoLayout_AreEmpty()
    {
        AircraftModel target = GroundAircraft(Callsign, "At Parking", new LatLon(37.72, -122.22));
        var host = new ClientMenuHost(MainWith(target, []), target, new Border());

        Assert.Empty(host.GetPushbackFaceChoices(Callsign));
        AssertSettledEmpty(host.GetPushbackTargets(Callsign));
        Assert.Empty(host.GetPresetTaxiChoices(Callsign));
    }

    [AvaloniaFact]
    public void EnterPushRoute_StartsPushRouteDrawingForTheRightClickedAircraft()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(PushbackFaceNode));
        MainViewModel main = OakMain(target, []);
        var host = new ClientMenuHost(main, target, new Border());

        host.EnterPushRoute(Callsign);

        Assert.True(main.Ground.IsDrawingRoute);
        Assert.Equal(Callsign, main.Ground.PushRouteCallsign);
    }

    // --- Point menu: taxi choices and the custom-taxi seed ----------------------------------

    [AvaloniaFact]
    public void TaxiChoices_NoRoute_IsOneDisabledRow()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(PushbackFaceNode));
        MainViewModel main = OakMain(target, []);
        var host = new ClientMenuHost(main, target, new Border());
        int from = main.Ground.GetAircraftNearestNodeId(target)!.Value;
        // The first node, helipads first, that the pathfinder cannot reach from I30 for a B738.
        GroundNodeDto? unreachable = Oak
            .Nodes.OrderBy(n => (n.Type == "Helipad") ? 0 : 1)
            .FirstOrDefault(n =>
                main.Ground.FindRoutesToNode(from, n.Id, GroundViewModel.CategoryFor(target), GroundViewModel.WakeClassFor(target)).Count == 0
            );
        Assert.NotNull(unreachable);

        IReadOnlyList<MenuCommandChoice> choices = host.GetTaxiChoices(Callsign, unreachable, null);

        MenuCommandChoice row = Assert.Single(choices);
        Assert.Equal("No route found", row.Label);
        Assert.Null(row.Command);
        Assert.Null(row.Preview);
        Assert.Empty(row.Children);
    }

    [AvaloniaFact]
    public void TaxiChoices_HoldShortNode_UsesItsRunwayEnd1()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(PushbackFaceNode));
        var host = new ClientMenuHost(OakMain(target, []), target, new Border());
        string end1 = RunwayIdentifier.Parse(Runway30HoldShortNode.RunwayId!).End1;

        IReadOnlyList<MenuCommandChoice> routes = RouteChoices(host.GetTaxiChoices(Callsign, Runway30HoldShortNode, null));

        Assert.NotEmpty(routes);
        Assert.All(routes, AssertRoutesToRunway(end1));
    }

    [AvaloniaFact]
    public void TaxiChoices_ThresholdClick_UsesTheClickedEnd()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(PushbackFaceNode));
        var host = new ClientMenuHost(OakMain(target, []), target, new Border());
        // The other end than the node's own End1, so the answer can only come from the click.
        string clickedEnd = RunwayIdentifier.Parse(Runway30HoldShortNode.RunwayId!).End2;

        IReadOnlyList<MenuCommandChoice> routes = RouteChoices(host.GetTaxiChoices(Callsign, Runway30HoldShortNode, clickedEnd));

        Assert.NotEmpty(routes);
        Assert.All(routes, AssertRoutesToRunway(clickedEnd));
    }

    // A 28L threshold click for an aircraft parked off taxiway R1, south of the runway: the choices taxi it to 28L for
    // departure and never hold it short of, or cross, the runway it was sent to.
    [AvaloniaFact]
    public void TaxiChoices_ThresholdClickOn28L_FromTheSouthRamp_OffersDepartureAndNoCrossingOrMidRouteHoldShortOf28L()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        List<GroundNodeDto> onR1 = [.. Oak.Nodes.Where(n => LinkNamesOf(n.Id).Contains("R1"))];
        GroundNodeDto stand = Oak
            .Nodes.Where(n => n.Type is "Parking" or "Spot")
            .MinBy(n => onR1.Min(r => GeoMath.DistanceNm(PositionOf(n), PositionOf(r))))!;
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(stand));
        MainViewModel main = OakMain(target, []);
        var host = new ClientMenuHost(main, target, new Border());
        int holdShortId = main.Ground.FindNearestHoldShortNodeForRunwayEnd(target, "28L")!.Value;
        GroundNodeDto holdShort = Oak.Nodes.Single(n => n.Id == holdShortId);

        IReadOnlyList<MenuCommandChoice> choices = host.GetTaxiChoices(Callsign, holdShort, "28L");

        IReadOnlyList<MenuCommandChoice> routes = RouteChoices(choices);
        Assert.NotEmpty(routes);
        Assert.All(routes, AssertRoutesToRunway("28L"));
        List<string> commands = LeafCommands(choices);
        Assert.NotEmpty(commands);
        // 28L's only hold short is the route's own end, the "Hold short RWY 28L" choice; never a crossing clause.
        string holdShortCommand = Assert.Single(commands, c => c.Contains("HS 28L", StringComparison.Ordinal));
        Assert.EndsWith(" HS 28L", holdShortCommand);
        Assert.All(commands, c => Assert.DoesNotContain("CROSS 28L", c, StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void TaxiChoices_TwoOrMoreRoutes_NestUnderTaxiHere()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = TaxiingOnW3();
        MainViewModel main = OakMain(target, []);
        var host = new ClientMenuHost(main, target, new Border());
        int from = main.Ground.GetAircraftNearestNodeId(target)!.Value;
        List<TaxiRoute> RoutesTo(GroundNodeDto node) =>
            main.Ground.FindRoutesToNode(from, node.Id, GroundViewModel.CategoryFor(target), GroundViewModel.WakeClassFor(target));
        // The first taxiway intersection, in layout order, the pathfinder reaches from W3 by two or more routes.
        GroundNodeDto destination = Oak.Nodes.Where(n => n.Type == "TaxiwayIntersection").First(n => RoutesTo(n).Count >= 2);
        List<TaxiRoute> found = RoutesTo(destination);

        IReadOnlyList<MenuCommandChoice> choices = host.GetTaxiChoices(Callsign, destination, null);

        MenuCommandChoice taxiHere = Assert.Single(choices);
        Assert.Equal("Taxi here", taxiHere.Label);
        Assert.Null(taxiHere.Command);
        Assert.Equal(found.Count, taxiHere.Children.Count);
        Assert.All(taxiHere.Children, c => Assert.StartsWith("Taxi ", c.Label));
        Assert.All(taxiHere.Children, c => Assert.True((c.Command is not null) || (c.Children.Count > 0), $"'{c.Label}' sends nothing"));
    }

    // A node with no named terminus — any taxiway intersection — is pinned with its node id, so the server stops at the
    // clicked node instead of at the junction where the last two taxiways meet.
    [AvaloniaFact]
    public void TaxiChoices_PlainNodeRouteWithoutCrossings_PinsTheNode()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = TaxiingOnW3();
        MainViewModel main = OakMain(target, []);
        var host = new ClientMenuHost(main, target, new Border());
        int from = main.Ground.GetAircraftNearestNodeId(target)!.Value;
        List<TaxiRoute> RoutesTo(GroundNodeDto node) =>
            main.Ground.FindRoutesToNode(from, node.Id, GroundViewModel.CategoryFor(target), GroundViewModel.WakeClassFor(target));
        GroundNodeDto destination = Oak
            .Nodes.Where(n => n.Type == "TaxiwayIntersection")
            .First(n =>
                (RoutesTo(n) is { Count: > 0 } routes) && routes.All(r => r.HoldShortPoints.All(h => h.Reason != HoldShortReason.RunwayCrossing))
            );

        List<string> commands = LeafCommands(host.GetTaxiChoices(Callsign, destination, null));

        Assert.NotEmpty(commands);
        Assert.All(commands, c => Assert.EndsWith($" #{destination.Id}", c));
    }

    [AvaloniaFact]
    public void TaxiChoices_PlainNodeRouteCrossingARunway_PinsTheNodeBeforeTheCrossingClauses()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = TaxiingOnW3();
        MainViewModel main = OakMain(target, []);
        var host = new ClientMenuHost(main, target, new Border());
        int from = main.Ground.GetAircraftNearestNodeId(target)!.Value;
        List<TaxiRoute> RoutesTo(GroundNodeDto node) =>
            main.Ground.FindRoutesToNode(from, node.Id, GroundViewModel.CategoryFor(target), GroundViewModel.WakeClassFor(target));
        GroundNodeDto destination = Oak
            .Nodes.Where(n => n.Type == "TaxiwayIntersection")
            .First(n =>
                (RoutesTo(n) is { Count: > 0 } routes) && routes.Any(r => r.HoldShortPoints.Any(h => h.Reason == HoldShortReason.RunwayCrossing))
            );

        List<string> commands = LeafCommands(host.GetTaxiChoices(Callsign, destination, null));

        Assert.NotEmpty(commands);
        Assert.Contains(commands, c => c.Contains(" HS ", StringComparison.Ordinal));
        foreach (string command in commands)
        {
            int pin = command.IndexOf($" #{destination.Id}", StringComparison.Ordinal);
            Assert.True(pin >= 0, command);
            int holdShort = command.IndexOf(" HS ", StringComparison.Ordinal);
            Assert.True((holdShort < 0) || (pin < holdShort), command);
            int cross = command.IndexOf(", CROSS ", StringComparison.Ordinal);
            Assert.True((cross < 0) || (pin < cross), command);
        }
    }

    // A named stand is the terminus; no node pin joins it.
    [AvaloniaFact]
    public void TaxiChoices_NamedSpotAndParking_CarryTheStandTokenAndNoNodePin()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = TaxiingOnW3();
        MainViewModel main = OakMain(target, []);
        var host = new ClientMenuHost(main, target, new Border());
        int from = main.Ground.GetAircraftNearestNodeId(target)!.Value;
        List<TaxiRoute> RoutesTo(GroundNodeDto node) =>
            main.Ground.FindRoutesToNode(from, node.Id, GroundViewModel.CategoryFor(target), GroundViewModel.WakeClassFor(target));
        GroundNodeDto spot = Oak.Nodes.First(n => (n.Type == "Spot") && (n.Name is not null) && (RoutesTo(n).Count > 0));
        GroundNodeDto parking = Oak.Nodes.First(n => (n.Type == "Parking") && (n.Name is not null) && (RoutesTo(n).Count > 0));

        AssertStandTokens(LeafCommands(host.GetTaxiChoices(Callsign, spot, null)), $"${spot.Name}");
        AssertStandTokens(LeafCommands(host.GetTaxiChoices(Callsign, parking, null)), $"@{parking.Name}");
    }

    [AvaloniaFact]
    public void TaxiChoices_RunwayHoldShortNode_LeafCommandsCarryNoNodePin()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(PushbackFaceNode));
        var host = new ClientMenuHost(OakMain(target, []), target, new Border());

        List<string> commands = LeafCommands(host.GetTaxiChoices(Callsign, Runway30HoldShortNode, null));

        Assert.NotEmpty(commands);
        Assert.All(commands, c => Assert.DoesNotContain("#", c));
    }

    [AvaloniaFact]
    public void CustomTaxiSeed_FollowsTheNodeType()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(PushbackFaceNode));
        MainViewModel main = OakMain(target, []);
        var host = new ClientMenuHost(main, target, new Border());
        GroundNodeDto parking = Oak.Nodes.First(n => (n.Type == "Parking") && (n.Name is not null));
        string rwy = RunwayIdentifier.ToDisplayDesignator(RunwayIdentifier.Parse(Runway30HoldShortNode.RunwayId!).End1);
        string taxiway = main.Ground.GetNodeTaxiwayNames(PresetTaxiNode.Id)[0];

        Assert.Equal(new MenuTextSeed("TAXI  $I30", 5), host.GetCustomTaxiSeed(PushbackFaceNode, null));
        Assert.Equal(new MenuTextSeed($"TAXI  @{parking.Name}", 5), host.GetCustomTaxiSeed(parking, null));
        Assert.Equal(new MenuTextSeed($"RWY {rwy} TAXI ", $"RWY {rwy} TAXI ".Length), host.GetCustomTaxiSeed(Runway30HoldShortNode, null));
        Assert.Equal(new MenuTextSeed($"TAXI  {taxiway}", 5), host.GetCustomTaxiSeed(PresetTaxiNode, null));
    }

    [AvaloniaFact]
    public void CustomTaxiSeed_ThresholdEnd_WinsOverTheHoldShortRunwayEnd1()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(PushbackFaceNode));
        MainViewModel main = OakMain(target, []);
        var host = new ClientMenuHost(main, target, new Border());
        string end2 = RunwayIdentifier.Parse(Runway30HoldShortNode.RunwayId!).End2;
        string seed = $"RWY {RunwayIdentifier.ToDisplayDesignator(end2)} TAXI ";

        Assert.Equal(new MenuTextSeed(seed, seed.Length), host.GetCustomTaxiSeed(Runway30HoldShortNode, end2));
    }

    /// <summary>The per-route choices: a "Taxi here" submenu's children, else the answer itself.</summary>
    private static IReadOnlyList<MenuCommandChoice> RouteChoices(IReadOnlyList<MenuCommandChoice> choices) =>
        (choices is [{ Label: "Taxi here" } parent]) ? parent.Children : choices;

    /// <summary>Every command the choice tree can send, walking "Taxi here" submenus and their per-route children.</summary>
    private static List<string> LeafCommands(IReadOnlyList<MenuCommandChoice> choices)
    {
        var commands = new List<string>();
        foreach (MenuCommandChoice choice in choices)
        {
            Walk(choice);
        }

        return commands;

        void Walk(MenuCommandChoice choice)
        {
            if (choice.Command is { } command)
            {
                commands.Add(command);
            }

            foreach (MenuCommandChoice child in choice.Children)
            {
                Walk(child);
            }
        }
    }

    /// <summary>Every command carries the stand token and none a node pin.</summary>
    private static void AssertStandTokens(List<string> commands, string token)
    {
        Assert.NotEmpty(commands);
        Assert.All(commands, c => Assert.Contains(token, c));
        Assert.All(commands, c => Assert.DoesNotContain("#", c));
    }

    /// <summary>
    /// A route to <paramref name="runway"/>: a "Taxi …" submenu previewing the route, whose first item is the departure
    /// taxi to that runway and whose separator divides it from the hold-short variants.
    /// </summary>
    private static Action<MenuCommandChoice> AssertRoutesToRunway(string runway) =>
        route =>
        {
            Assert.StartsWith("Taxi ", route.Label);
            Assert.NotNull(route.Preview);
            Assert.StartsWith($"For Departure {runway}", route.Children[0].Label);
            Assert.Contains(route.Children, c => ReferenceEquals(c, MenuCommandChoice.Separator));
        };

    // --- The builder's ground items over the host's answers ---------------------------------

    // The menu is built over the client host with only its sends captured, since the client host sends to a server a test
    // has none of; the choices and the hover preview are the client host's own.
    [AvaloniaFact]
    public void Builder_Taxiing_HoldShortFollowAndGiveWay_SendAndPreview()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = TaxiingAcrossOnG();
        AircraftModel candidate = GroundAircraft(OtherCallsign, "Taxiing", new LatLon(target.Position.Lat + 0.002, target.Position.Lon));
        MainViewModel main = OakMain(target, [candidate]);
        var host = new SendCapturingHost(new ClientMenuHost(main, target, new Border()), "AB");

        ContextMenu menu = AircraftMenuBuilder.Build(target, new MenuClick(Callsign, null, null, []), host, _ => []);

        string[] groundItems = ["Hold short of…", "Follow…", "Give way to…"];
        Assert.Equal(groundItems, Headers(CommandTree(menu)).Where(groundItems.Contains));
        Assert.Equal(
            new GroundTrafficFlyoutTitle(HoldShortLadder.Title, "route G · RWY 28L"),
            Item(CommandTree(menu), "Hold short of…").Items.OfType<MenuItem>().First().Header
        );
        Button followRow = FollowListItems.Row(Item(CommandTree(menu), "Follow…"), OtherCallsign);
        MenuItem giveWayRow = TrafficRow(Item(CommandTree(menu), "Give way to…").Items, OtherCallsign);
        Assert.Equal(["Moving"], FollowListItems.Titles(Item(CommandTree(menu), "Follow…")));
        Assert.IsType<GroundTrafficFlyoutTitle>(Item(CommandTree(menu), "Give way to…").Items.OfType<MenuItem>().First().Header);

        List<MenuItem> ladderStops =
        [
            .. Item(CommandTree(menu), "Hold short of…").Items.OfType<MenuItem>().Where(m => m.Header is HoldShortLadderStop),
        ];
        Assert.Equal(3, ladderStops.Count);
        MenuItem holdShortRunway = Assert.Single(ladderStops, m => m.Header is HoldShortLadderStop { Badge: HoldShortChoice.RunwayBadge });
        RaisePointerEntered(holdShortRunway);
        Assert.Null(main.Ground.PreviewRoute);
        ScopePreview preview = Assert.IsType<ScopePreview>(main.Ground.ScopePreview);
        Assert.Equal(Bar28ROnG.Id, Assert.IsType<TaxiRoute>(preview.OwnRoute).Segments[^1].ToNodeId);
        Assert.Equal(Bar28ROnG.Position, preview.BarPoint);

        Click(holdShortRunway);
        FollowListItems.Click(followRow);
        Click(giveWayRow);

        Assert.Equal([(Callsign, "HS 10L", "AB"), (Callsign, $"FOLLOWG {OtherCallsign}", "AB"), (Callsign, $"GW {OtherCallsign}", "AB")], host.Sent);
    }

    /// <summary>The traffic row in <paramref name="items"/> whose one-line header starts with <paramref name="callsign"/>.</summary>
    private static MenuItem TrafficRow(ItemCollection items, string callsign) =>
        items.OfType<MenuItem>().Single(item => item.Header?.ToString()?.StartsWith($"{callsign} ·", StringComparison.Ordinal) == true);

    // --- Fixtures ---------------------------------------------------------------------------

    private static AircraftModel GroundAircraft(string callsign, string phase, LatLon position) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "B738",
            FlightRules = "IFR",
            IsOnGround = true,
            CurrentPhase = phase,
            Position = position,
        };

    /// <summary>A taxiing aircraft on the W3 node behind runway 30's hold-short, routed along W3 to runway 30.</summary>
    private static AircraftModel TaxiingOnW3()
    {
        GroundNodeDto node = TaxiingNode;
        AircraftModel ac = GroundAircraft(Callsign, "Taxiing", new LatLon(node.Latitude, node.Longitude));
        ac.TaxiRoute = "W3";
        ac.CurrentTaxiway = "W3";
        ac.AssignedRunway = "30";
        ac.HasActiveTaxiRoute = true;
        return ac;
    }

    /// <summary>
    /// A taxiing aircraft two G nodes north of runway 28R's northern bar on G, facing it, cleared along G to 28L: the route
    /// crosses 10L/28R mid-runway on the way, so the ladder names that runway's bar, taxiway C met at the bar by its fillets
    /// and taxiway D where the G/D fillet leaves G (28L, its destination, is no stop).
    /// </summary>
    private static AircraftModel TaxiingAcrossOnG()
    {
        GroundNode bar = Bar28ROnG;
        GroundNode start = NextNorthOnG(NextNorthOnG(bar));
        AircraftModel ac = GroundAircraft(Callsign, "Taxiing", start.Position);
        ac.Heading = new TrueHeading(GeoMath.BearingTo(start.Position, bar.Position));
        ac.TaxiRoute = "G";
        ac.CurrentTaxiway = "G";
        ac.AssignedRunway = "28L";
        ac.HasActiveTaxiRoute = true;
        return ac;
    }

    /// <summary>Runway 28R's bar on G north of the runway: the first a southbound G taxi meets.</summary>
    private static GroundNode Bar28ROnG =>
        MenuGoldenFixtures
            .OakLayoutForSim.Nodes.Values.Where(n =>
                (n.Type == GroundNodeType.RunwayHoldShort) && (n.RunwayId is { } id) && id.Contains("28R") && n.Edges.Any(e => e.TaxiwayName == "G")
            )
            .MaxBy(n => n.Position.Lat)!;

    /// <summary>The northernmost node one straight G edge from <paramref name="node"/>.</summary>
    private static GroundNode NextNorthOnG(GroundNode node) =>
        node.Edges.OfType<GroundEdge>().Where(e => e.TaxiwayName == "G").Select(e => e.OtherNode(node)).MaxBy(n => n.Position.Lat)!;

    private static MainViewModel MainWith(AircraftModel target, AircraftModel[] others)
    {
        var main = new MainViewModel(new FakeFilePickerService());
        main.Aircraft.Clear();
        main.Aircraft.Add(target);
        foreach (AircraftModel other in others)
        {
            main.Aircraft.Add(other);
        }

        return main;
    }

    /// <summary>
    /// A main view model holding <paramref name="target"/> and <paramref name="others"/>, its primary ground view model
    /// over the committed KOAK layout.
    /// </summary>
    private static MainViewModel OakMain(AircraftModel target, AircraftModel[] others)
    {
        MainViewModel main = MainWith(target, others);
        main.Ground.SetLayoutForTesting(MenuGoldenFixtures.OakLayoutForClient);
        return main;
    }

    private static GroundLayoutDto Oak => MenuGoldenFixtures.OakLayoutForClient;

    private static LatLon PositionOf(GroundNodeDto node) => new(node.Latitude, node.Longitude);

    /// <summary>The named Spot "I30", the pushback fixture of <c>GroundSubmenuCharacterizationTests</c>: two non-RAMP W1 edges.</summary>
    private static GroundNodeDto PushbackFaceNode => Oak.Nodes.First(n => (n.Type == "Spot") && (n.Name == "I30"));

    /// <summary>The only intersection on both taxiway T and taxiway U, where the sidecar's "TERMINAL to 30" route begins.</summary>
    private static GroundNodeDto PresetTaxiNode =>
        Oak.Nodes.First(n => (n.Type == "TaxiwayIntersection") && LinkNamesOf(n.Id).Contains("T") && LinkNamesOf(n.Id).Contains("U"));

    /// <summary>Gate 25, a named parking node at the terminal.</summary>
    private static GroundNodeDto Gate25Node => Oak.Nodes.First(n => (n.Type == "Parking") && (n.Name == "25"));

    /// <summary>Runway 30's hold short on <paramref name="taxiway"/>.</summary>
    private static GroundNodeDto HoldShort30On(string taxiway) =>
        Oak.Nodes.First(n => (n.Type == "RunwayHoldShort") && (n.RunwayId is { } rwy) && rwy.Contains("30") && LinkNamesOf(n.Id).Contains(taxiway));

    /// <summary>The <paramref name="taxiway"/> node just behind <paramref name="holdShort"/>, away from the runway.</summary>
    private static GroundNodeDto NodeBehind(GroundNodeDto holdShort, string taxiway) =>
        Oak
            .Edges.Where(e => (e.TaxiwayName == taxiway) && ((e.FromNodeId == holdShort.Id) || (e.ToNodeId == holdShort.Id)))
            .Select(e => Oak.Nodes.First(n => n.Id == ((e.FromNodeId == holdShort.Id) ? e.ToNodeId : e.FromNodeId)))
            .Single(n => !LinkNamesOf(n.Id).Any(name => name.Contains("RWY", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// A B738 parked on OAK gate 26 on the stand's heading, its parking spot named, with <paramref name="others"/> about
    /// it, the ground view reading <paramref name="seed"/>, and the client host serving it.
    /// </summary>
    internal static (AircraftModel Target, MainViewModel Main, ClientMenuHost Host) AtGate26(PushTargetSeed seed, params AircraftModel[] others) =>
        AtStand(Gate26, seed, others);

    /// <summary>
    /// A B738 parked on the named OAK stand on its heading, its parking spot named, with <paramref name="others"/> about
    /// it, the ground view reading <paramref name="seed"/>, and the client host serving it.
    /// </summary>
    internal static (AircraftModel Target, MainViewModel Main, ClientMenuHost Host) AtStand(
        string standName,
        PushTargetSeed seed,
        params AircraftModel[] others
    )
    {
        AircraftModel target = ParkedAt(standName, Callsign);
        MainViewModel main = OakMain(target, others);
        main.Ground.SetPushTargetSeedForTesting(seed);
        return (target, main, new ClientMenuHost(main, target, new Border()));
    }

    /// <summary>A B738 parked on the named OAK stand on its heading, at parking with its parking spot named.</summary>
    internal static AircraftModel ParkedAt(string standName, string callsign)
    {
        GroundNodeDto gate = Oak.Nodes.Single(n => (n.Type == "Parking") && (n.Name == standName));
        AircraftModel ac = GroundAircraft(callsign, "At Parking", PositionOf(gate));
        GroundNode stand = MenuGoldenFixtures.OakLayoutForSim.Nodes[gate.Id];
        ac.Heading = stand.TrueHeading ?? throw new InvalidOperationException($"OAK gate {standName} has no stand heading");
        ac.ParkingSpot = standName;
        return ac;
    }

    private static void AssertSettledEmpty(PushTargetList list)
    {
        Assert.True(list.Settled.IsCompleted);
        Assert.Empty(list.Targets);
    }

    /// <summary>Runway 30's hold-short node on taxiway W3.</summary>
    private static GroundNodeDto Runway30HoldShortNode =>
        Oak.Nodes.First(n => (n.Type == "RunwayHoldShort") && (n.RunwayId is { } rwy) && rwy.Contains("30") && LinkNamesOf(n.Id).Contains("W3"));

    /// <summary>
    /// The W3 node just behind runway 30's hold-short at W3: of the hold-short's two W3 neighbours, the one with no link
    /// onto the runway (the hold-short fixture of <c>GroundSubmenuCharacterizationTests</c>).
    /// </summary>
    private static GroundNodeDto TaxiingNode
    {
        get
        {
            GroundNodeDto holdShort = Runway30HoldShortNode;
            return Oak
                .Edges.Where(e => (e.TaxiwayName == "W3") && ((e.FromNodeId == holdShort.Id) || (e.ToNodeId == holdShort.Id)))
                .Select(e => Oak.Nodes.First(n => n.Id == ((e.FromNodeId == holdShort.Id) ? e.ToNodeId : e.FromNodeId)))
                .MaxBy(n =>
                    Oak.Nodes.Where(r => LinkNamesOf(r.Id).Any(name => name.Contains("RWY", StringComparison.OrdinalIgnoreCase)))
                        .Min(r => Math.Pow(r.Latitude - n.Latitude, 2) + Math.Pow(r.Longitude - n.Longitude, 2))
                )!;
        }
    }

    /// <summary>The taxiway names on every link at <paramref name="nodeId"/>, plain edges and fillet arcs alike.</summary>
    private static IEnumerable<string> LinkNamesOf(int nodeId) =>
        Oak
            .Edges.Where(e => (e.FromNodeId == nodeId) || (e.ToNodeId == nodeId))
            .Select(e => e.TaxiwayName)
            .Concat((Oak.Arcs ?? []).Where(a => (a.FromNodeId == nodeId) || (a.ToNodeId == nodeId)).SelectMany(a => a.TaxiwayNames));

    private static List<string> Headers(ItemCollection items) =>
        [.. items.OfType<MenuItem>().Where(m => m.Header is string).Select(m => (string)m.Header!)];

    /// <summary>The items of the menu's All Commands submenu, where the ground block lives.</summary>
    private static ItemCollection CommandTree(ContextMenu menu) => AllCommandsPath.Group(menu, MenuCategory.Taxi).Items;

    private static MenuItem Item(ItemCollection items, string header)
    {
        MenuItem? item = items.OfType<MenuItem>().FirstOrDefault(m => m.Header is string s && s == header);
        Assert.NotNull(item);
        return item;
    }

    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    /// <summary>
    /// Raises a pointer enter on <paramref name="item"/> with a real <see cref="PointerEventArgs"/>, which the typed
    /// <see cref="InputElement.PointerEntered"/> handler requires (see <c>GroundSubmenuCharacterizationTests.RaisePointer</c>).
    /// </summary>
    private static void RaisePointerEntered(MenuItem item) =>
        item.RaiseEvent(
            new PointerEventArgs(
                InputElement.PointerEnteredEvent,
                item,
                new Pointer(0, PointerType.Mouse, isPrimary: true),
                rootVisual: null,
                rootVisualPosition: default,
                timestamp: 0,
                properties: default,
                modifiers: KeyModifiers.None
            )
        );
}
