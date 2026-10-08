using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
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

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The client menu host's ground members — ground traffic, hold short, route preview, pushback and preset taxi — which
/// answer from the primary ground view model on the real KOAK layout, and the builder's hold-short and follow items built
/// over them. The display items the menu shows are the view's own (see <c>CanvasMenuItemsTests</c>), so the host serves
/// none of them.
/// </summary>
public class ClientMenuHostGroundTests
{
    private const string Callsign = "UAL100";
    private const string OtherCallsign = "SWA200";

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
        Assert.Empty(host.GetHoldShortChoices(Callsign));
        Assert.Empty(host.GetPushbackFaceChoices(Callsign));
        Assert.Empty(host.GetPushbackToChoices(Callsign));
        Assert.Empty(host.GetPresetTaxiChoices(Callsign));

        host.EnterPushRoute(Callsign);
        Assert.False(main.Ground.IsDrawingRoute);
    }

    [AvaloniaFact]
    public void GetHoldShortChoices_SendHSWithThePreviewRoute()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = TaxiingOnW3();
        MainViewModel main = OakMain(target, []);
        var host = new ClientMenuHost(main, target, new Border());

        IReadOnlyList<MenuCommandChoice> choices = host.GetHoldShortChoices(Callsign);

        Assert.Equal([("Runway 12", "HS 12"), ("Runway 30", "HS 30")], choices.Select(c => (c.Label, c.Command)));
        string[] runways = ["12", "30"];
        foreach ((MenuCommandChoice choice, string runway) in choices.Zip(runways))
        {
            TaxiRoute? expected = main.Ground.FindHoldShortPreviewRoute(target, runway);
            Assert.NotNull(expected);
            Assert.NotNull(choice.Preview);
            Assert.Equal(SegmentsOf(expected), SegmentsOf(choice.Preview));
        }
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

    [AvaloniaFact]
    public void GetPushbackToChoices_AtSpotI30_ThirtyNearestStands_SpotUsesDollarParkingUsesAt()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(PushbackFaceNode));
        var host = new ClientMenuHost(OakMain(target, []), target, new Border());

        IReadOnlyList<MenuCommandChoice> choices = host.GetPushbackToChoices(Callsign);

        Assert.Equal(30, choices.Count);
        Assert.DoesNotContain(choices, c => c.Label == "I30");
        Assert.Contains(choices, c => (c.Label == "1") && (c.Command == "PUSH $1"));
        Assert.Contains(choices, c => (c.Label == "32") && (c.Command == "PUSH @32"));
        Assert.All(choices, c => Assert.Null(c.Preview));

        List<double> distances = [.. choices.Select(c => DistanceToStand(target, c.Label))];
        Assert.Equal(distances.Order(), distances);
    }

    [AvaloniaFact]
    public void GetPresetTaxiChoices_FromTheOakSidecar_KeepTheWalkableRouteAndDropTheOther()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "Taxiing", PositionOf(PresetTaxiNode));
        target.AssignedRunway = "30";
        var host = new ClientMenuHost(OakMain(target, []), target, new Border());

        IReadOnlyList<MenuCommandChoice> choices = host.GetPresetTaxiChoices(Callsign);

        MenuCommandChoice terminal = Assert.Single(choices, c => c.Label == "TERMINAL to 30");
        Assert.Equal("TAXI T U W RWY 30", terminal.Command);
        Assert.Null(terminal.Preview);
        Assert.DoesNotContain(choices, c => c.Label == "30 to TERMINAL");
    }

    [AvaloniaFact]
    public void PushbackAndPresetChoices_NoLayout_AreEmpty()
    {
        AircraftModel target = GroundAircraft(Callsign, "At Parking", new LatLon(37.72, -122.22));
        var host = new ClientMenuHost(MainWith(target, []), target, new Border());

        Assert.Empty(host.GetPushbackFaceChoices(Callsign));
        Assert.Empty(host.GetPushbackToChoices(Callsign));
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
        AircraftModel target = TaxiingOnW3();
        AircraftModel candidate = GroundAircraft(OtherCallsign, "Taxiing", new LatLon(target.Position.Lat + 0.002, target.Position.Lon));
        MainViewModel main = OakMain(target, [candidate]);
        var host = new SendCapturingHost(new ClientMenuHost(main, target, new Border()), "AB");

        ContextMenu menu = AircraftMenuBuilder.Build(target, new MenuClick(Callsign, null, null, []), host, _ => []);

        string[] groundItems = ["Hold short of…", "Follow…", "Give way to…"];
        Assert.Equal(groundItems, Headers(CommandTree(menu)).Where(groundItems.Contains));
        Assert.Equal(["Runway 12", "Runway 30"], Headers(Item(CommandTree(menu), "Hold short of…").Items));
        MenuItem followRow = TrafficRow(Item(CommandTree(menu), "Follow…").Items, OtherCallsign);
        MenuItem giveWayRow = TrafficRow(Item(CommandTree(menu), "Give way to…").Items, OtherCallsign);
        Assert.Equal(["Moving"], Headers(Item(CommandTree(menu), "Follow…").Items));
        Assert.Equal(["Moving"], Headers(Item(CommandTree(menu), "Give way to…").Items));

        MenuItem holdShort30 = Item(Item(CommandTree(menu), "Hold short of…").Items, "Runway 30");
        RaisePointerEntered(holdShort30);
        Assert.NotNull(main.Ground.PreviewRoute);
        Assert.Equal(SegmentsOf(main.Ground.FindHoldShortPreviewRoute(target, "30")!), SegmentsOf(main.Ground.PreviewRoute));

        Click(holdShort30);
        Click(followRow);
        Click(giveWayRow);

        Assert.Equal([(Callsign, "HS 30", "AB"), (Callsign, $"FOLLOWG {OtherCallsign}", "AB"), (Callsign, $"GW {OtherCallsign}", "AB")], host.Sent);
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

    /// <summary>The distance from <paramref name="aircraft"/> to the named stand <paramref name="name"/>.</summary>
    private static double DistanceToStand(AircraftModel aircraft, string name)
    {
        GroundNodeDto stand = Oak.Nodes.First(n => (n.Name == name) && (n.Type is "Spot" or "Parking" or "Helipad"));
        return GeoMath.DistanceNm(aircraft.Position.Lat, aircraft.Position.Lon, stand.Latitude, stand.Longitude);
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
                .Single(n => !LinkNamesOf(n.Id).Any(name => name.Contains("RWY", StringComparison.OrdinalIgnoreCase)));
        }
    }

    /// <summary>The taxiway names on every link at <paramref name="nodeId"/>, plain edges and fillet arcs alike.</summary>
    private static IEnumerable<string> LinkNamesOf(int nodeId) =>
        Oak
            .Edges.Where(e => (e.FromNodeId == nodeId) || (e.ToNodeId == nodeId))
            .Select(e => e.TaxiwayName)
            .Concat((Oak.Arcs ?? []).Where(a => (a.FromNodeId == nodeId) || (a.ToNodeId == nodeId)).SelectMany(a => a.TaxiwayNames));

    private static IEnumerable<(int From, int To)> SegmentsOf(TaxiRoute route) => route.Segments.Select(s => (s.FromNodeId, s.ToNodeId));

    private static List<string> Headers(ItemCollection items) =>
        [.. items.OfType<MenuItem>().Where(m => m.Header is string).Select(m => (string)m.Header!)];

    /// <summary>The items of the menu's All Commands submenu, where the ground block lives.</summary>
    private static ItemCollection CommandTree(ContextMenu menu) => Item(menu.Items, AircraftMenuBuilder.AllCommandsHeader).Items;

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
