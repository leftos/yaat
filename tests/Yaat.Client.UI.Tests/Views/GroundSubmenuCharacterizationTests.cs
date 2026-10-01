using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Client.Views.Ground;
using Yaat.Sim;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.UI.Tests.Views;

// Characterization of the ground map's host-answered submenus on today's builders: the pushback face
// items and "Push back to...", "Preset taxi route", "Hold short of...", "Follow..." and "Give way to...".
// The menu goldens are single-aircraft fixtures and show none of them, so nothing else pins their
// headers, their children and order, their caps and gates, or the exact command a click sends.
//
// Every test here must pass against the current code unchanged; they exist so the move of these submenus
// into the Core catalog can prove "behaviour unchanged" through them. Nothing under src/ changes.
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
    public void GroundMenu_AtParking_FollowListsNearestGroundTrafficCappedAtTwelve()
    {
        LatLon at = PositionOf(PushbackFaceNode);
        AircraftModel target = GroundAircraft("SWA100", "At Parking", at);
        AircraftModel[] others =
        [
            .. Enumerable.Range(1, 14).Select(i => GroundAircraft($"SWA{i:000}", "Taxiing", new LatLon(at.Lat + (i * 0.001), at.Lon))),
        ];

        Built built = BuildMenu(target, prevSelected: null, others);

        List<string> follow = Children(built.Menu, "Follow...");
        Assert.Equal(12, follow.Count);
        Assert.Equal(Enumerable.Range(1, 12).Select(i => $"SWA{i:000}"), follow);
    }

    [AvaloniaFact]
    public void GroundMenu_AtParking_FollowClickSendsFOLLOWG()
    {
        Built built = BuildMenu(ParkedAircraft(), prevSelected: null, Candidate());

        Click(Child(built.Menu, "Follow...", CandidateCallsign));

        Assert.Equal([(ParkedCallsign, $"FOLLOWG {CandidateCallsign}", Initials)], built.Sent);
    }

    [AvaloniaFact]
    public void GroundMenu_AtParking_OffersNoGiveWay()
    {
        Built built = BuildMenu(ParkedAircraft(), prevSelected: null, Candidate());

        List<string> headers = Headers(built.Menu.Items);
        Assert.Contains("Follow...", headers);
        Assert.DoesNotContain("Give way to...", headers);
    }

    [AvaloniaFact]
    public void GroundMenu_Parked_PushbackFaceItemsSendPushFace()
    {
        AircraftModel target = ParkedAircraft();
        Built built = BuildMenu(target, prevSelected: null);

        List<(string Label, string Cardinal)> directions = built.Vm.GetPushbackDirections(target);
        Assert.NotEmpty(directions);

        List<string> faceItems = [.. Headers(built.Menu.Items).Where(h => h.StartsWith("Push back, ", StringComparison.Ordinal))];
        Assert.Equal(directions.Select(d => $"Push back, {d.Label}"), faceItems);
        Assert.All(directions, d => Assert.StartsWith("face ", d.Label));

        Click(Item(built.Menu.Items, $"Push back, {directions[0].Label}"));

        Assert.Equal([(ParkedCallsign, $"PUSH FACE {directions[0].Cardinal}", Initials)], built.Sent);
        Assert.Contains(directions[0].Cardinal, new[] { "N", "NE", "E", "SE", "S", "SW", "W", "NW" });
    }

    [AvaloniaFact]
    public void GroundMenu_PushbackTo_SpotUsesDollarParkingUsesAt()
    {
        Built built = BuildMenu(ParkedAircraft(), prevSelected: null);

        Assert.NotEqual(NodeTypeOf("1"), NodeTypeOf("32"));

        Click(Child(built.Menu, "Push back to...", "1"));
        Assert.Equal([(ParkedCallsign, "PUSH $1", Initials)], built.Sent);

        built.Sent.Clear();
        Click(Child(built.Menu, "Push back to...", "32"));
        Assert.Equal([(ParkedCallsign, "PUSH @32", Initials)], built.Sent);
    }

    [AvaloniaFact]
    public void GroundMenu_PushRoute_StartsPushRouteDrawing()
    {
        Built built = BuildMenu(ParkedAircraft(), prevSelected: null);
        Assert.False(built.Vm.IsDrawingRoute);

        Click(Item(built.Menu.Items, "Push route..."));

        Assert.True(built.Vm.IsDrawingRoute);
    }

    // --- Taxiing and the holds: follow, give way, hold short ---------------------------------

    [AvaloniaFact]
    public void GroundMenu_Taxiing_GiveWayClickSendsGW()
    {
        Built built = BuildMenu(TaxiingOnW3(), prevSelected: null, Candidate());

        Click(Child(built.Menu, "Give way to...", CandidateCallsign));

        Assert.Equal([(TaxiingCallsign, $"GW {CandidateCallsign}", Initials)], built.Sent);
    }

    [AvaloniaFact]
    public void GroundMenu_HoldingAfterExit_GiveWayOnlyWithActiveTaxiRoute()
    {
        AircraftModel withoutRoute = HoldingAfterExit(hasActiveTaxiRoute: false);
        Built noRoute = BuildMenu(withoutRoute, prevSelected: null, Candidate());
        Assert.Contains("Follow...", Headers(noRoute.Menu.Items));
        Assert.DoesNotContain("Give way to...", Headers(noRoute.Menu.Items));

        AircraftModel withRoute = HoldingAfterExit(hasActiveTaxiRoute: true);
        Built routed = BuildMenu(withRoute, prevSelected: null, Candidate());
        Assert.Contains("Follow...", Headers(routed.Menu.Items));
        Assert.Contains("Give way to...", Headers(routed.Menu.Items));
    }

    [AvaloniaFact]
    public void GroundMenu_HoldingAfterPushback_FollowAppearsOnceFromHoldBranch()
    {
        AircraftModel target = GroundAircraft("SWA104", "Holding After Pushback", PositionOf(PushbackFaceNode));

        Built built = BuildMenu(target, prevSelected: null, Candidate());

        Assert.Equal(1, Headers(built.Menu.Items).Count(h => h == "Follow..."));
    }

    [AvaloniaFact]
    public void GroundMenu_RelativeSelection_ReplacesFollowAndGiveWay()
    {
        AircraftModel target = TaxiingOnW3();
        AircraftModel selected = GroundAircraft("SWA602", "Taxiing", PositionOf(PushbackFaceNode));

        Built built = BuildMenu(target, prevSelected: selected);

        List<string> headers = Headers(built.Menu.Items);
        Assert.DoesNotContain("Follow...", headers);
        Assert.DoesNotContain("Give way to...", headers);
        Assert.Contains("SWA602: follow SWA104", headers);
        Assert.Contains("SWA602: give way to SWA104", headers);
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

        Assert.Equal([CandidateCallsign], Children(built.Menu, "Follow..."));
    }

    [AvaloniaFact]
    public void GroundMenu_NoGroundTraffic_NoFollowOrGiveWaySubmenu()
    {
        Built built = BuildMenu(TaxiingOnW3(), prevSelected: null);

        List<string> headers = Headers(built.Menu.Items);
        Assert.DoesNotContain("Follow...", headers);
        Assert.DoesNotContain("Give way to...", headers);
    }

    [AvaloniaFact]
    public void GroundMenu_Taxiing_HoldShortListsRouteTargetsAndSendsHS()
    {
        Built built = BuildMenu(TaxiingOnW3(), prevSelected: null);

        Assert.Equal(["Runway 12", "Runway 30"], Children(built.Menu, "Hold short of..."));

        Click(Child(built.Menu, "Hold short of...", "Runway 30"));

        Assert.Equal([(TaxiingCallsign, "HS 30", Initials)], built.Sent);
    }

    [AvaloniaFact]
    public void GroundMenu_HoldShortHover_SetsPreviewRoute_AndPointerExitDoesNotClearIt()
    {
        AircraftModel target = TaxiingOnW3();
        Built built = BuildMenu(target, prevSelected: null);

        MenuItem item = Child(built.Menu, "Hold short of...", "Runway 30");
        Assert.Null(built.Vm.PreviewRoute);

        RaisePointer(item, InputElement.PointerEnteredEvent);

        TaxiRoute? preview = built.Vm.PreviewRoute;
        TaxiRoute? expected = built.Vm.FindHoldShortPreviewRoute(target, "30");
        Assert.NotNull(expected);
        Assert.NotNull(preview);
        Assert.Equal(expected.Segments.Select(s => (s.FromNodeId, s.ToNodeId)), preview.Segments.Select(s => (s.FromNodeId, s.ToNodeId)));

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

        Assert.DoesNotContain("Hold short of...", Headers(built.Menu.Items));
    }

    // --- Preset taxi routes ------------------------------------------------------------------

    [AvaloniaFact]
    public void GroundMenu_PresetTaxi_SendsCanonicalCommand()
    {
        Built built = BuildMenu(PresetTaxiAircraft(), prevSelected: null);

        Click(Child(built.Menu, "Preset taxi route", "TERMINAL to 30"));

        Assert.Equal([(TaxiingCallsign, "TAXI T U W RWY 30", Initials)], built.Sent);
    }

    [AvaloniaFact]
    public void GroundMenu_PresetTaxi_DropsUnresolvableRoutes()
    {
        Built built = BuildMenu(PresetTaxiAircraft(), prevSelected: null);

        List<string> routes = Children(built.Menu, "Preset taxi route");
        Assert.Contains("TERMINAL to 30", routes);
        Assert.DoesNotContain("30 to TERMINAL", routes);
    }

    // --- Order ------------------------------------------------------------------------------

    [AvaloniaFact]
    public void GroundMenu_Taxiing_SubmenuOrder()
    {
        Built built = BuildMenu(TaxiingOnW3(), prevSelected: null, Candidate());

        string[] covered = ["Hold position", "Hold short of...", "Follow...", "Give way to...", "Break conflict"];
        List<string> present = [.. Headers(built.Menu.Items).Where(covered.Contains)];

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

    private static string NodeTypeOf(string name) => Oak.Nodes.First(n => (n.Name == name) && (n.Type is "Spot" or "Parking" or "Helipad")).Type;

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
            GroundNodeDto holdShort = Oak.Nodes.First(n =>
                (n.Type == "RunwayHoldShort") && (n.RunwayId is { } rwy) && rwy.Contains("30") && LinkNamesOf(n.Id).Contains("W3")
            );
            return Oak
                .Edges.Where(e => (e.TaxiwayName == "W3") && ((e.FromNodeId == holdShort.Id) || (e.ToNodeId == holdShort.Id)))
                .Select(e => NodeById((e.FromNodeId == holdShort.Id) ? e.ToNodeId : e.FromNodeId))
                .Single(n => !LinkNamesOf(n.Id).Any(name => name.Contains("RWY", StringComparison.OrdinalIgnoreCase)));
        }
    }

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
    /// Builds the ground view's simulated-aircraft items for <paramref name="target"/>, with the KOAK layout installed
    /// on the ground view model and every other aircraft in the main view model's list so the follow candidates resolve.
    /// The view is parented to a host carrying the main view model, which is what <c>GroundView.FindMainViewModel</c>
    /// walks; commands land in <see cref="Built.Sent"/> through the view model's send delegate. Built under a scoped
    /// navigation database so the main view model's background navdata load cannot replace it mid-build.
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

        var sent = new List<(string Callsign, string Command, string Initials)>();
        var vm = new GroundViewModel(
            new ServerConnection(),
            sendCommand: (callsign, command, initials) =>
            {
                sent.Add((callsign, command, initials));
                return Task.CompletedTask;
            }
        );
        vm.SetLayoutForTesting(MenuGoldenFixtures.OakLayoutForClient);

        var view = new GroundView { DataContext = vm };
        var host = new Grid { DataContext = main };
        host.Children.Add(view);

        var menu = new ContextMenu();
        view.AddSimulatedAircraftItems(menu, vm, new GroundMenuTarget(target, prevSelected, target.Callsign, Initials));
        return new Built(vm, sent, menu);
    }

    private static List<string> Headers(ItemCollection items) =>
        [.. items.OfType<MenuItem>().Where(m => m.Header is string).Select(m => (string)m.Header!)];

    private static List<string> Children(ContextMenu menu, string submenuHeader) => Headers(Item(menu.Items, submenuHeader).Items);

    private static MenuItem Child(ContextMenu menu, string submenuHeader, string childHeader) =>
        Item(Item(menu.Items, submenuHeader).Items, childHeader);

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
