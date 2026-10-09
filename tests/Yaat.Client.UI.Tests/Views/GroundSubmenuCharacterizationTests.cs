using Avalonia.Automation;
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

// The aircraft menu's host-answered ground submenus, built through AircraftMenuBuilder over the client menu host: the
// pushback face items, "Preset taxi route", "Hold short of…", "Follow…" and "Give way to…" ("Push back to…" is
// ClientMenuHostGroundTests' and PushbackToMenuTests').
// The menu goldens are single-aircraft fixtures and show none of them, so nothing else pins their
// headers, their children and order, their caps and gates, or the exact command a click sends.
//
// Fixtures sit on the real committed KOAK layout (MenuGoldenFixtures.OakLayoutForClient), whose LatLons
// place each aircraft on a named node:
//   - the Spot node "I30" (a named Spot with two non-RAMP W1 edges), for the At Parking pushback items;
//   - the W3 node before runway 30's hold-short at W3 (of the hold-short's two W3 neighbours, the one
//     with no runway edge: the route W3 from it resolves to the runway-30 hold-short, and its two
//     crossings are runways 30 and 12), for the Taxiing hold-short items;
//   - the only taxiway intersection carrying both a T and a U edge, from which the committed sidecar's
//     "TERMINAL to 30" (T U W) resolves and its "30 to TERMINAL" (W V T) does not, for the preset routes.
public class GroundSubmenuCharacterizationTests
{
    private const string Initials = "AB";

    private const string TaxiingCallsign = "SWA104";
    private const string ParkedCallsign = "SWA101";
    private const string CandidateCallsign = "SWA200";

    private readonly NavigationDatabase _navDb;

    public GroundSubmenuCharacterizationTests() => _navDb = MenuGoldenFixtures.EnsureNavData();

    // --- At Parking: Follow… and the pushback items -----------------------------------------

    [AvaloniaFact]
    public void GroundMenu_AtParking_FollowListsMovingThenParked_FourEachThenMore()
    {
        LatLon at = PositionOf(PushbackFaceNode);
        AircraftModel target = GroundAircraft("SWA100", "At Parking", at);
        AircraftModel[] others =
        [
            .. Enumerable.Range(1, 6).Select(i => GroundAircraft($"SWA{i:000}", "Taxiing", new LatLon(at.Lat + (i * 0.001), at.Lon))),
            GroundAircraft("PRK1", "At Parking", new LatLon(at.Lat - 0.0005, at.Lon)),
            GroundAircraft("PRK2", "At Parking", new LatLon(at.Lat - 0.0015, at.Lon)),
        ];

        Built built = BuildMenu(target, prevSelected: null, others);

        ItemCollection follow = Item(CommandTree(built.Menu), "Follow…").Items;
        List<string> labels = Headers(follow);
        Assert.Equal(3, labels.Count);
        Assert.Equal("Moving", labels[0]);
        Assert.StartsWith("More (2, up to ~2,", labels[1]);
        Assert.Equal("Parked or holding", labels[2]);
        Assert.Equal(["SWA001", "SWA002", "SWA003", "SWA004", "PRK1", "PRK2"], RowCallsigns(follow));
        Assert.Equal(["SWA005", "SWA006"], RowCallsigns(Item(follow, labels[1]).Items));
        Assert.Equal(
            ["Moving", "SWA001", "SWA002", "SWA003", "SWA004", labels[1], "Parked or holding", "PRK1", "PRK2"],
            follow.OfType<MenuItem>().Select(item => (item.Header is string header) ? header : CallsignOf(item))
        );
    }

    [AvaloniaFact]
    public void GroundMenu_FollowRowsCarryTypeStateAndDistance()
    {
        LatLon at = PositionOf(PushbackFaceNode);
        AircraftModel target = GroundAircraft(ParkedCallsign, "At Parking", at);
        AircraftModel taxiing = GroundAircraft("SWA5286", "Taxiing", new LatLon(at.Lat + 0.003, at.Lon));
        taxiing.CurrentTaxiway = "W";
        taxiing.FiledAircraftType = "B738/L";
        AircraftModel parked = GroundAircraft("VTE3202", "At Parking", new LatLon(at.Lat - 0.0016, at.Lon));
        parked.AircraftType = "E145";
        parked.ParkingSpot = "1";

        Built built = BuildMenu(target, prevSelected: null, taxiing, parked);

        Assert.Equal(
            [
                "Moving",
                "SWA5286 · B738 · taxiing on W · ahead · ~1,100 ft — FOLLOWG SWA5286",
                "Parked or holding",
                "VTE3202 · E145 · at parking · gate 1 · ~600 ft — FOLLOWG VTE3202",
            ],
            Item(CommandTree(built.Menu), "Follow…").Items.OfType<MenuItem>().Select(item => item.Header?.ToString())
        );
    }

    [AvaloniaFact]
    public void GroundMenu_FollowHover_HighlightsTheAircraft_AndClosingTheMenuClearsIt()
    {
        Built built = BuildMenu(ParkedAircraft(), prevSelected: null, Candidate());
        var window = new Window();
        window.Show();
        built.Menu.Open(window);

        RaisePointer(Row(built.Menu, "Follow…", CandidateCallsign), InputElement.PointerEnteredEvent);
        Assert.Equal([CandidateCallsign], built.Vm.DataBlockState.HighlightedCallsigns);

        built.Menu.Close();
        window.Close();
        Assert.Empty(built.Vm.DataBlockState.HighlightedCallsigns);
    }

    [AvaloniaFact]
    public void GroundMenu_GiveWay_OmitsSurfaceShadows()
    {
        AircraftModel shadow = GroundAircraft(
            "SHD1",
            "Taxiing",
            new LatLon(PositionOf(PushbackFaceNode).Lat + 0.001, PositionOf(PushbackFaceNode).Lon)
        );
        shadow.IsLiveTraffic = true;

        Built built = BuildMenu(TaxiingOnW3(), prevSelected: null, shadow, Candidate());

        Assert.Equal(["SHD1", CandidateCallsign], RowCallsigns(Item(CommandTree(built.Menu), "Follow…").Items).Order(StringComparer.Ordinal));
        Assert.Equal([CandidateCallsign], RowCallsigns(Item(CommandTree(built.Menu), "Give way to…").Items));
    }

    [AvaloniaFact]
    public void GroundMenu_RelativeSelection_ForLineSaysWhereTheClickedAircraftIsOnTheSelectedRoute()
    {
        GroundNodeDto holdShort = HoldShort30AtW3;
        AircraftModel clicked = GroundAircraft("SWA601", $"Holding Short {holdShort.RunwayId}", PositionOf(holdShort));
        clicked.CurrentTaxiway = "W3";

        AircraftModel selected = TaxiingOnW3();

        Built built = BuildMenu(clicked, prevSelected: selected, selected);

        List<string> top = Headers(built.Menu.Items);
        int label = top.IndexOf("For SWA104 (selected)");
        Assert.True(label >= 0);
        Assert.Matches(@"^SWA601 is holding short of 30 at W3, [\d,]+ ft (ahead|behind) on SWA104's route$", top[label + 1]);
    }

    [AvaloniaFact]
    public void GroundMenu_AtParking_FollowClickSendsFOLLOWG()
    {
        Built built = BuildMenu(ParkedAircraft(), prevSelected: null, Candidate());

        Click(Row(built.Menu, "Follow…", CandidateCallsign));

        Assert.Equal([(ParkedCallsign, $"FOLLOWG {CandidateCallsign}", Initials)], built.Sent);
    }

    [AvaloniaFact]
    public void GroundMenu_AtParking_OffersNoGiveWay()
    {
        Built built = BuildMenu(ParkedAircraft(), prevSelected: null, Candidate());

        List<string> headers = Headers(CommandTree(built.Menu));
        Assert.Contains("Follow…", headers);
        Assert.DoesNotContain("Give way to…", headers);
    }

    [AvaloniaFact]
    public void GroundMenu_Parked_PushbackFaceItemsSendPushFace()
    {
        AircraftModel target = ParkedAircraft();
        Built built = BuildMenu(target, prevSelected: null);

        List<(string Label, string Cardinal)> directions = built.Vm.GetPushbackDirections(target);
        Assert.NotEmpty(directions);

        List<string> faceItems = [.. Headers(CommandTree(built.Menu)).Where(h => h.StartsWith("Push back, ", StringComparison.Ordinal))];
        Assert.Equal(directions.Select(d => $"Push back, {d.Label}"), faceItems);
        Assert.All(directions, d => Assert.StartsWith("face ", d.Label));

        Click(Item(CommandTree(built.Menu), $"Push back, {directions[0].Label}"));

        Assert.Equal([(ParkedCallsign, $"PUSH FACE {directions[0].Cardinal}", Initials)], built.Sent);
        Assert.Contains(directions[0].Cardinal, new[] { "N", "NE", "E", "SE", "S", "SW", "W", "NW" });
    }

    [AvaloniaFact]
    public void GroundMenu_PushRoute_StartsPushRouteDrawing()
    {
        Built built = BuildMenu(ParkedAircraft(), prevSelected: null);
        Assert.False(built.Vm.IsDrawingRoute);

        Click(Item(CommandTree(built.Menu), "Push route…"));

        Assert.True(built.Vm.IsDrawingRoute);
    }

    // --- Taxiing and the holds: follow, give way, hold short ---------------------------------

    [AvaloniaFact]
    public void GroundMenu_Taxiing_GiveWayClickSendsGW()
    {
        Built built = BuildMenu(TaxiingOnW3(), prevSelected: null, Candidate());

        Click(Row(built.Menu, "Give way to…", CandidateCallsign));

        Assert.Equal([(TaxiingCallsign, $"GW {CandidateCallsign}", Initials)], built.Sent);
    }

    [AvaloniaFact]
    public void GroundMenu_HoldingAfterExit_GiveWayOnlyWithActiveTaxiRoute()
    {
        AircraftModel withoutRoute = HoldingAfterExit(hasActiveTaxiRoute: false);
        Built noRoute = BuildMenu(withoutRoute, prevSelected: null, Candidate());
        Assert.Contains("Follow…", Headers(CommandTree(noRoute.Menu)));
        Assert.DoesNotContain("Give way to…", Headers(CommandTree(noRoute.Menu)));

        AircraftModel withRoute = HoldingAfterExit(hasActiveTaxiRoute: true);
        Built routed = BuildMenu(withRoute, prevSelected: null, Candidate());
        Assert.Contains("Follow…", Headers(CommandTree(routed.Menu)));
        Assert.Contains("Give way to…", Headers(CommandTree(routed.Menu)));
    }

    [AvaloniaFact]
    public void GroundMenu_HoldingAfterPushback_FollowAppearsOnce()
    {
        AircraftModel target = GroundAircraft("SWA104", "Holding After Pushback", PositionOf(PushbackFaceNode));

        Built built = BuildMenu(target, prevSelected: null, Candidate());

        Assert.Equal(1, Headers(CommandTree(built.Menu)).Count(h => h == "Follow…"));
    }

    [AvaloniaFact]
    public void GroundMenu_RelativeSelection_ReplacesFollowAndGiveWay()
    {
        AircraftModel target = TaxiingOnW3();
        AircraftModel selected = GroundAircraft("SWA602", "Taxiing", PositionOf(PushbackFaceNode));

        Built built = BuildMenu(target, prevSelected: selected);

        List<string> headers = Headers(CommandTree(built.Menu));
        Assert.DoesNotContain("Follow…", headers);
        Assert.DoesNotContain("Give way to…", headers);
        List<string> top = Headers(built.Menu.Items);
        Assert.Contains("For SWA602 (selected)", top);
        Assert.Contains("Follow SWA104", top);
        Assert.Contains("Give way to SWA104", top);
    }

    [AvaloniaFact]
    public void GroundMenu_FollowSkipsAirborneAndSelf()
    {
        LatLon at = PositionOf(PushbackFaceNode);
        AircraftModel target = GroundAircraft(ParkedCallsign, "At Parking", at);
        AircraftModel self = GroundAircraft(ParkedCallsign, "At Parking", at);
        AircraftModel airborne = GroundAircraft("AAL999", "ApproachNav", new LatLon(at.Lat + 0.01, at.Lon));
        airborne.IsOnGround = false;

        Built built = BuildMenu(target, prevSelected: null, self, airborne, Candidate());

        Assert.Equal([CandidateCallsign], RowCallsigns(Item(CommandTree(built.Menu), "Follow…").Items));
    }

    [AvaloniaFact]
    public void GroundMenu_NoGroundTraffic_NoFollowOrGiveWaySubmenu()
    {
        Built built = BuildMenu(TaxiingOnW3(), prevSelected: null);

        List<string> headers = Headers(CommandTree(built.Menu));
        Assert.DoesNotContain("Follow…", headers);
        Assert.DoesNotContain("Give way to…", headers);
    }

    // W3's bar is mid-way along 12/30 and the room names no active runway, so the one runway row names both ends and
    // sends the lower-numbered one, which binds the same bar.
    [AvaloniaFact]
    public void GroundMenu_Taxiing_HoldShortListsTheRouteLineAndOneRowPerBar_AndSendsHS()
    {
        Built built = BuildMenu(TaxiingOnW3(), prevSelected: null);

        Assert.Equal(["route W3 · RWY 30"], Children(built.Menu, "Hold short of…"));
        MenuItem runway = Assert.Single(HoldShortRows(built.Menu));
        Assert.Matches(@"^RW Runway 12/30 · at W3, end of route · ~\d[\d,]* ft — HS 12$", AutomationProperties.GetName(runway));

        Click(runway);

        Assert.Equal([(TaxiingCallsign, "HS 12", Initials)], built.Sent);
    }

    [AvaloniaFact]
    public void GroundMenu_HoldShortHover_SetsPreviewRoute_AndPointerExitDoesNotClearIt()
    {
        AircraftModel target = TaxiingOnW3();
        Built built = BuildMenu(target, prevSelected: null);

        MenuItem item = Assert.Single(HoldShortRows(built.Menu));
        Assert.Null(built.Vm.PreviewRoute);

        RaisePointer(item, InputElement.PointerEnteredEvent);

        TaxiRoute? preview = built.Vm.PreviewRoute;
        Assert.NotNull(preview);
        Assert.Equal(HoldShort30AtW3.Id, preview.Segments[^1].ToNodeId);

        RaisePointer(item, InputElement.PointerExitedEvent);

        Assert.NotNull(built.Vm.PreviewRoute);
    }

    [AvaloniaFact]
    public void GroundMenu_HoldShort_OnlyForTaxiing()
    {
        AircraftModel target = GroundAircraft("SWA104", "Following SWA200", PositionOf(TaxiingNode));
        target.TaxiRoute = "W3";
        target.CurrentTaxiway = "W3";
        target.AssignedRunway = "30";

        Built built = BuildMenu(target, prevSelected: null);

        Assert.DoesNotContain("Hold short of…", Headers(CommandTree(built.Menu)));
    }

    // --- Preset taxi routes ------------------------------------------------------------------

    [AvaloniaFact]
    public void GroundMenu_PresetTaxi_SendsCanonicalCommand()
    {
        Built built = BuildMenu(PresetTaxiAircraft(), prevSelected: null);

        Click(PresetRows(built.Menu).Single(row => ((MenuCommandRow)row.Header!).Name == "TERMINAL to 30"));

        Assert.Equal([(TaxiingCallsign, "TAXI T U W RWY 30", Initials)], built.Sent);
    }

    [AvaloniaFact]
    public void GroundMenu_PresetTaxi_DropsUnresolvableRoutes()
    {
        Built built = BuildMenu(PresetTaxiAircraft(), prevSelected: null);

        List<string> routes = [.. PresetRows(built.Menu).Select(row => ((MenuCommandRow)row.Header!).Name)];
        Assert.Contains("TERMINAL to 30", routes);
        Assert.DoesNotContain("30 to TERMINAL", routes);
    }

    // --- Order ------------------------------------------------------------------------------

    [AvaloniaFact]
    public void GroundMenu_Taxiing_SubmenuOrder()
    {
        Built built = BuildMenu(TaxiingOnW3(), prevSelected: null, Candidate());

        string[] covered = ["Hold position", "Hold short of…", "Follow…", "Give way to…", "Ignore ground conflicts (15 s)"];
        List<string> present = [.. Headers(CommandTree(built.Menu)).Where(covered.Contains)];

        Assert.Equal(covered, present);
    }

    // --- Fixtures ---------------------------------------------------------------------------

    private static AircraftModel ParkedAircraft() => GroundAircraft(ParkedCallsign, "At Parking", PositionOf(PushbackFaceNode));

    private static AircraftModel TaxiingOnW3()
    {
        AircraftModel ac = GroundAircraft(TaxiingCallsign, "Taxiing", PositionOf(TaxiingNode));
        ac.TaxiRoute = "W3";
        ac.CurrentTaxiway = "W3";
        ac.AssignedRunway = "30";
        ac.HasActiveTaxiRoute = true;
        return ac;
    }

    private static AircraftModel PresetTaxiAircraft()
    {
        AircraftModel ac = GroundAircraft(TaxiingCallsign, "Taxiing", PositionOf(PresetTaxiNode));
        ac.AssignedRunway = "30";
        return ac;
    }

    private static AircraftModel HoldingAfterExit(bool hasActiveTaxiRoute)
    {
        AircraftModel ac = GroundAircraft("SWA104", "Holding After Exit", PositionOf(PushbackFaceNode));
        ac.HasActiveTaxiRoute = hasActiveTaxiRoute;
        return ac;
    }

    private static AircraftModel Candidate() =>
        GroundAircraft(CandidateCallsign, "Taxiing", new LatLon(PositionOf(PushbackFaceNode).Lat + 0.002, PositionOf(PushbackFaceNode).Lon));

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

    private static LatLon PositionOf(GroundNodeDto node) => new(node.Latitude, node.Longitude);

    // --- OAK nodes --------------------------------------------------------------------------

    private static GroundLayoutDto Oak => MenuGoldenFixtures.OakLayoutForClient;

    /// <summary>The named Spot "I30": two non-RAMP W1 edges, so it has pushback facings and sits well inside the parking areas.</summary>
    private static GroundNodeDto PushbackFaceNode => Oak.Nodes.First(n => (n.Type == "Spot") && (n.Name == "I30"));

    /// <summary>
    /// The W3 node just behind runway 30's hold-short at W3 (OAK node 85): of the hold-short's two W3 neighbours,
    /// the one with no edge onto the runway.
    /// </summary>
    private static GroundNodeDto TaxiingNode
    {
        get
        {
            GroundNodeDto holdShort = HoldShort30AtW3;
            return Oak
                .Edges.Where(e => (e.TaxiwayName == "W3") && ((e.FromNodeId == holdShort.Id) || (e.ToNodeId == holdShort.Id)))
                .Select(e => NodeById((e.FromNodeId == holdShort.Id) ? e.ToNodeId : e.FromNodeId))
                .Single(n => !LinkNamesOf(n.Id).Any(name => name.Contains("RWY", StringComparison.OrdinalIgnoreCase)));
        }
    }

    /// <summary>Runway 30's hold-short on W3, where the route W3 to runway 30 ends.</summary>
    private static GroundNodeDto HoldShort30AtW3 =>
        Oak.Nodes.First(n => (n.Type == "RunwayHoldShort") && (n.RunwayId is { } rwy) && rwy.Contains("30") && LinkNamesOf(n.Id).Contains("W3"));

    /// <summary>The only intersection on both taxiway T and taxiway U, where the sidecar's "TERMINAL to 30" route begins.</summary>
    private static GroundNodeDto PresetTaxiNode =>
        Oak.Nodes.First(n => (n.Type == "TaxiwayIntersection") && LinkNamesOf(n.Id).Contains("T") && LinkNamesOf(n.Id).Contains("U"));

    /// <summary>
    /// The taxiway names on every link at <paramref name="nodeId"/>, plain edges and fillet arcs alike — the parsed
    /// layout's node adjacency carries both, and the runway a node joins is an arc at a junction like OAK's W3/RWY30.
    /// </summary>
    private static IEnumerable<string> LinkNamesOf(int nodeId) =>
        Oak
            .Edges.Where(e => (e.FromNodeId == nodeId) || (e.ToNodeId == nodeId))
            .Select(e => e.TaxiwayName)
            .Concat((Oak.Arcs ?? []).Where(a => (a.FromNodeId == nodeId) || (a.ToNodeId == nodeId)).SelectMany(a => a.TaxiwayNames));

    private static GroundNodeDto NodeById(int nodeId) => Oak.Nodes.First(n => n.Id == nodeId);

    // --- Harness ----------------------------------------------------------------------------

    private sealed record Built(GroundViewModel Vm, List<(string Callsign, string Command, string Initials)> Sent, ContextMenu Menu);

    /// <summary>
    /// Builds the aircraft menu for <paramref name="target"/> through <see cref="AircraftMenuBuilder"/> over the client
    /// menu host, with the KOAK layout installed on the main view model's primary ground view model and every other
    /// aircraft in its list so the follow candidates resolve; commands land in <see cref="Built.Sent"/>, sent with the
    /// test's initials. Built under a scoped navigation database so the main view model's background navdata load cannot
    /// replace it mid-build.
    /// </summary>
    private Built BuildMenu(AircraftModel target, AircraftModel? prevSelected, params AircraftModel[] others)
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(_navDb);

        var main = new MainViewModel(new FakeFilePickerService());
        main.Aircraft.Clear();
        main.Aircraft.Add(target);
        foreach (AircraftModel other in others)
        {
            main.Aircraft.Add(other);
        }

        main.Ground.SetLayoutForTesting(MenuGoldenFixtures.OakLayoutForClient);

        var host = new SendCapturingHost(new ClientMenuHost(main, target, new Border()), Initials);
        ContextMenu menu = AircraftMenuBuilder.Build(target, new MenuClick(target.Callsign, prevSelected, null, []), host, _ => []);
        return new Built(main.Ground, host.Sent, menu);
    }

    private static List<string> Headers(ItemCollection items) =>
        [.. items.OfType<MenuItem>().Where(m => m.Header is string).Select(m => (string)m.Header!)];

    /// <summary>The items of the menu's All Commands submenu, where the ground block lives.</summary>
    private static ItemCollection CommandTree(ContextMenu menu) => Item(menu.Items, AircraftMenuBuilder.AllCommandsHeader).Items;

    private static List<string> Children(ContextMenu menu, string submenuHeader) => Headers(Item(CommandTree(menu), submenuHeader).Items);

    /// <summary>The Hold short of… submenu's rows: every item but the route line.</summary>
    private static List<MenuItem> HoldShortRows(ContextMenu menu) =>
        [.. Item(CommandTree(menu), "Hold short of…").Items.OfType<MenuItem>().Where(m => m.Header is not string)];

    private static MenuItem Child(ContextMenu menu, string submenuHeader, string childHeader) =>
        Item(Item(CommandTree(menu), submenuHeader).Items, childHeader);

    /// <summary>The Preset taxi route submenu's rows, each headed by its command row.</summary>
    private static List<MenuItem> PresetRows(ContextMenu menu) =>
        [.. Item(CommandTree(menu), "Preset taxi route").Items.OfType<MenuItem>().Where(m => m.Header is MenuCommandRow)];

    /// <summary>The callsigns of the traffic rows in <paramref name="items"/>, in order, leaving out section labels and More.</summary>
    private static List<string> RowCallsigns(ItemCollection items) =>
        [.. items.OfType<MenuItem>().Where(m => m.Header is not string).Select(CallsignOf)];

    /// <summary>A traffic row's callsign, the first word of its one-line header.</summary>
    private static string CallsignOf(MenuItem row) => row.Header!.ToString()!.Split(' ')[0];

    /// <summary>The traffic row for <paramref name="callsign"/> in <paramref name="submenuHeader"/>'s list under All Commands.</summary>
    private static MenuItem Row(ContextMenu menu, string submenuHeader, string callsign)
    {
        MenuItem? row = Item(CommandTree(menu), submenuHeader)
            .Items.OfType<MenuItem>()
            .FirstOrDefault(m => (m.Header is not string) && (CallsignOf(m) == callsign));
        Assert.NotNull(row);
        return row;
    }

    private static MenuItem Item(ItemCollection items, string header)
    {
        MenuItem? item = items.OfType<MenuItem>().FirstOrDefault(m => m.Header is string s && s == header);
        Assert.NotNull(item);
        return item;
    }

    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    /// <summary>
    /// Raises a pointer enter or exit on <paramref name="item"/>. The hover handlers subscribe to
    /// <see cref="InputElement.PointerEntered"/> / <see cref="InputElement.PointerExited"/>, and Avalonia's dispatch
    /// skips a typed handler whose args are not that handler's args type, so a real <see cref="PointerEventArgs"/> is
    /// required — the constructor Avalonia marks unstable for exactly this headless-testing use.
    /// </summary>
    private static void RaisePointer(MenuItem item, RoutedEvent routedEvent) =>
        item.RaiseEvent(
            new PointerEventArgs(
                routedEvent,
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
