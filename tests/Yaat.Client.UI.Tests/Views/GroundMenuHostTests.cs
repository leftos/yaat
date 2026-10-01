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
using Yaat.Client.Views.Ground;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using CatalogMenuView = Yaat.Client.ContextMenus.MenuView;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The ground menu host's taxi-route and hidden-datablock members: they act only on the right-clicked aircraft, and
/// still act by callsign when the menu was opened on an aircraft the main view model has no model for. Its ground
/// traffic, hold-short, route-preview, pushback and preset-taxi members, and the Core hold-short and follow groups built
/// over it on the real KOAK layout.
/// </summary>
public class GroundMenuHostTests
{
    private const string Callsign = "UAL100";
    private const string OtherCallsign = "SWA200";
    private const string Initials = "AB";

    private static (GroundView View, GroundViewModel Ground) GroundHarness()
    {
        var ground = new GroundViewModel(new ServerConnection(), sendCommand: (_, _, _) => Task.CompletedTask);
        var view = new GroundView { DataContext = ground };
        return (view, ground);
    }

    [AvaloniaFact]
    public void GroundMenuHost_MismatchedCallsign_Throws()
    {
        (GroundView view, GroundViewModel ground) = GroundHarness();
        var host = new GroundMenuHost(view, ground, null, new AircraftModel { Callsign = Callsign });

        Assert.Throws<InvalidOperationException>(() => host.GetTaxiRouteMode(OtherCallsign));
        Assert.Throws<InvalidOperationException>(() => host.SetTaxiRouteMode(OtherCallsign, TaxiRouteDisplayMode.AlwaysShow));
        Assert.Throws<InvalidOperationException>(() => host.IsDataBlockHidden(OtherCallsign));
        Assert.Throws<InvalidOperationException>(() => host.ToggleHiddenDataBlock(OtherCallsign));

        Assert.Equal(TaxiRouteDisplayMode.Follow, ground.GetTaxiRouteMode(OtherCallsign));
        Assert.False(view.Canvas.IsDataBlockHidden(OtherCallsign));
    }

    [AvaloniaFact]
    public void GroundMenuHost_NoAircraftModel_ReadsAndDrivesDisplayByCallsign()
    {
        (GroundView view, GroundViewModel ground) = GroundHarness();
        var host = new GroundMenuHost(view, ground, null, null);

        Assert.Equal(TaxiRouteDisplayMode.Follow, host.GetTaxiRouteMode(Callsign));
        host.SetTaxiRouteMode(Callsign, TaxiRouteDisplayMode.AlwaysHide);
        Assert.Equal(TaxiRouteDisplayMode.AlwaysHide, ground.GetTaxiRouteMode(Callsign));
        Assert.Equal(TaxiRouteDisplayMode.AlwaysHide, host.GetTaxiRouteMode(Callsign));

        Assert.False(host.IsDataBlockHidden(Callsign));
        host.ToggleHiddenDataBlock(Callsign);
        Assert.True(view.Canvas.IsDataBlockHidden(Callsign));
        Assert.True(host.IsDataBlockHidden(Callsign));
    }

    // --- Ground traffic, hold short and route preview ----------------------------------------

    [AvaloniaFact]
    public void GetGroundTrafficCallsigns_ExcludesSelfAndAirborne_NearestFirst_CappedAtTwelve()
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
        (GroundView view, GroundViewModel ground) = GroundHarness();
        var host = new GroundMenuHost(view, ground, main, target);

        Assert.Equal(Enumerable.Range(1, 12).Select(i => $"SWA{i:000}"), host.GetGroundTrafficCallsigns(Callsign));
    }

    [AvaloniaFact]
    public void GetGroundTrafficCallsigns_NoMainViewModel_IsEmpty()
    {
        (GroundView view, GroundViewModel ground) = GroundHarness();
        var host = new GroundMenuHost(view, ground, null, GroundAircraft(Callsign, "Taxiing", new LatLon(37.72, -122.22)));

        Assert.Empty(host.GetGroundTrafficCallsigns(Callsign));
    }

    [AvaloniaFact]
    public void GroundMenuHost_MovementMembers_ActOnlyOnTheRightClickedAircraft()
    {
        (GroundView view, GroundViewModel ground) = GroundHarness();
        var host = new GroundMenuHost(view, ground, null, new AircraftModel { Callsign = Callsign });
        var noModel = new GroundMenuHost(view, ground, null, null);

        Assert.Throws<InvalidOperationException>(() => host.GetGroundTrafficCallsigns(OtherCallsign));
        Assert.Throws<InvalidOperationException>(() => host.GetHoldShortChoices(OtherCallsign));
        Assert.Throws<InvalidOperationException>(() => noModel.GetGroundTrafficCallsigns(Callsign));
        Assert.Throws<InvalidOperationException>(() => noModel.GetHoldShortChoices(Callsign));
    }

    [AvaloniaFact]
    public void GetHoldShortChoices_SendHSWithThePreviewRoute()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = TaxiingOnW3();
        GroundViewModel ground = OakGround(_ => { });
        var host = new GroundMenuHost(new GroundView { DataContext = ground }, ground, null, target);

        IReadOnlyList<MenuCommandChoice> choices = host.GetHoldShortChoices(Callsign);

        Assert.Equal([("Runway 12", "HS 12"), ("Runway 30", "HS 30")], choices.Select(c => (c.Label, c.Command)));
        string[] runways = ["12", "30"];
        foreach ((MenuCommandChoice choice, string runway) in choices.Zip(runways))
        {
            TaxiRoute? expected = ground.FindHoldShortPreviewRoute(target, runway);
            Assert.NotNull(expected);
            Assert.NotNull(choice.Preview);
            Assert.Equal(SegmentsOf(expected), SegmentsOf(choice.Preview));
        }
    }

    [AvaloniaFact]
    public void SetRoutePreview_SetsAndClearsTheGroundPreview()
    {
        (GroundView view, GroundViewModel ground) = GroundHarness();
        var host = new GroundMenuHost(view, ground, null, null);
        var route = new TaxiRoute { Segments = [], HoldShortPoints = [] };

        host.SetRoutePreview(route);
        Assert.Same(route, ground.PreviewRoute);

        host.SetRoutePreview(null);
        Assert.Null(ground.PreviewRoute);
    }

    // --- Pushback faces, push back to, push route and preset taxi routes ---------------------

    [AvaloniaFact]
    public void GroundMenuHost_PushbackAndPresetMembers_ActOnlyOnTheRightClickedAircraft()
    {
        (GroundView view, GroundViewModel ground) = GroundHarness();
        var host = new GroundMenuHost(view, ground, null, new AircraftModel { Callsign = Callsign });
        var noModel = new GroundMenuHost(view, ground, null, null);

        (GroundMenuHost Host, string Asked)[] cases = [(host, OtherCallsign), (noModel, Callsign)];
        foreach ((GroundMenuHost h, string asked) in cases)
        {
            Assert.Throws<InvalidOperationException>(() => h.GetPushbackFaceChoices(asked));
            Assert.Throws<InvalidOperationException>(() => h.GetPushbackToChoices(asked));
            Assert.Throws<InvalidOperationException>(() => h.GetPresetTaxiChoices(asked));
            Assert.Throws<InvalidOperationException>(() => h.EnterPushRoute(asked));
        }

        Assert.False(ground.IsDrawingRoute);
    }

    [AvaloniaFact]
    public void GetPushbackFaceChoices_AtSpotI30_SendPushFaceForEachNonRampTaxiway()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(PushbackFaceNode));
        GroundViewModel ground = OakGround(_ => { });
        var host = new GroundMenuHost(new GroundView { DataContext = ground }, ground, null, target);

        IReadOnlyList<MenuCommandChoice> choices = host.GetPushbackFaceChoices(Callsign);

        List<(string Label, string Cardinal)> directions = ground.GetPushbackDirections(target);
        Assert.NotEmpty(directions);
        Assert.Equal(directions.Select(d => ($"Push back, {d.Label}", $"PUSH FACE {d.Cardinal}")), choices.Select(c => (c.Label, c.Command)));
        Assert.All(choices, c => Assert.StartsWith("Push back, face ", c.Label));
        Assert.All(choices, c => Assert.Null(c.Preview));
    }

    [AvaloniaFact]
    public void GetPushbackToChoices_AtSpotI30_ThirtyNearestStands_SpotUsesDollarParkingUsesAt()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(PushbackFaceNode));
        GroundViewModel ground = OakGround(_ => { });
        var host = new GroundMenuHost(new GroundView { DataContext = ground }, ground, null, target);

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
        GroundViewModel ground = OakGround(_ => { });
        var host = new GroundMenuHost(new GroundView { DataContext = ground }, ground, null, target);

        IReadOnlyList<MenuCommandChoice> choices = host.GetPresetTaxiChoices(Callsign);

        MenuCommandChoice terminal = Assert.Single(choices, c => c.Label == "TERMINAL to 30");
        Assert.Equal("TAXI T U W RWY 30", terminal.Command);
        Assert.Null(terminal.Preview);
        Assert.DoesNotContain(choices, c => c.Label == "30 to TERMINAL");
    }

    [AvaloniaFact]
    public void PushbackAndPresetChoices_NoLayout_AreEmpty()
    {
        (GroundView view, GroundViewModel ground) = GroundHarness();
        AircraftModel target = GroundAircraft(Callsign, "At Parking", new LatLon(37.72, -122.22));
        var host = new GroundMenuHost(view, ground, null, target);

        Assert.Empty(host.GetPushbackFaceChoices(Callsign));
        Assert.Empty(host.GetPushbackToChoices(Callsign));
        Assert.Empty(host.GetPresetTaxiChoices(Callsign));
    }

    [AvaloniaFact]
    public void EnterPushRoute_StartsPushRouteDrawingForTheRightClickedAircraft()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = GroundAircraft(Callsign, "At Parking", PositionOf(PushbackFaceNode));
        GroundViewModel ground = OakGround(_ => { });
        var host = new GroundMenuHost(new GroundView { DataContext = ground }, ground, null, target);

        host.EnterPushRoute(Callsign);

        Assert.True(ground.IsDrawingRoute);
        Assert.Equal(Callsign, ground.PushRouteCallsign);
    }

    // --- The Core groups over the ground host ------------------------------------------------

    [AvaloniaFact]
    public void CoreGroups_Taxiing_HoldShortFollowAndGiveWay_SendAndPreview()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel target = TaxiingOnW3();
        AircraftModel candidate = GroundAircraft(OtherCallsign, "Taxiing", new LatLon(target.Position.Lat + 0.002, target.Position.Lon));
        var sent = new List<(string Callsign, string Command, string Initials)>();
        GroundViewModel ground = OakGround(sent.Add);
        var host = new GroundMenuHost(new GroundView { DataContext = ground }, ground, MainWith(target, [candidate]), target);
        var context = new MenuContext(Callsign, Initials, null, false, VfrCommandsForIfr.None, CatalogMenuView.Ground);

        var menu = new ContextMenu();
        SharedMenuGroups.AddGroundFollowAndGiveWay(menu.Items, target, context, host, GroundFollowPosition.Parking);
        SharedMenuGroups.AddGroundFollowAndGiveWay(menu.Items, target, context, host, GroundFollowPosition.Hold);
        Assert.Empty(menu.Items);

        SharedMenuGroups.AddGroundHoldShort(menu.Items, target, context, host);
        SharedMenuGroups.AddGroundFollowAndGiveWay(menu.Items, target, context, host, GroundFollowPosition.Taxi);

        Assert.Equal(["Hold short of...", "Follow...", "Give way to..."], Headers(menu.Items));
        Assert.Equal(["Runway 12", "Runway 30"], Headers(Item(menu.Items, "Hold short of...").Items));
        Assert.Equal([OtherCallsign], Headers(Item(menu.Items, "Follow...").Items));
        Assert.Equal([OtherCallsign], Headers(Item(menu.Items, "Give way to...").Items));

        MenuItem holdShort30 = Item(Item(menu.Items, "Hold short of...").Items, "Runway 30");
        RaisePointerEntered(holdShort30);
        Assert.NotNull(ground.PreviewRoute);
        Assert.Equal(SegmentsOf(ground.FindHoldShortPreviewRoute(target, "30")!), SegmentsOf(ground.PreviewRoute));

        Click(holdShort30);
        Click(Item(Item(menu.Items, "Follow...").Items, OtherCallsign));
        Click(Item(Item(menu.Items, "Give way to...").Items, OtherCallsign));

        Assert.Equal(
            [(Callsign, "HS 30", Initials), (Callsign, $"FOLLOWG {OtherCallsign}", Initials), (Callsign, $"GW {OtherCallsign}", Initials)],
            sent
        );
    }

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

    /// <summary>A ground view model over the committed KOAK layout, handing every command it sends to <paramref name="onSend"/>.</summary>
    private static GroundViewModel OakGround(Action<(string Callsign, string Command, string Initials)> onSend)
    {
        var ground = new GroundViewModel(
            new ServerConnection(),
            sendCommand: (callsign, command, initials) =>
            {
                onSend((callsign, command, initials));
                return Task.CompletedTask;
            }
        );
        ground.SetLayoutForTesting(MenuGoldenFixtures.OakLayoutForClient);
        return ground;
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

    /// <summary>
    /// The W3 node just behind runway 30's hold-short at W3: of the hold-short's two W3 neighbours, the one with no link
    /// onto the runway (the hold-short fixture of <c>GroundSubmenuCharacterizationTests</c>).
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
