using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Sim;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Situation;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The point menu: a right-click on a map point or a taxi node with an aircraft selected builds the point items by the
/// aircraft's predicates — the airborne heading, direct-to and hold items, the ground taxi, push and custom-taxi items,
/// and Warp here — then the view's own section, and nothing of the aircraft menu.
/// </summary>
public class PointMenuTests
{
    private const string Callsign = "AAL202";
    private const string Frd = "OAK090010";
    private const string Separator = "-";

    // --- Airborne -------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task AirbornePoint_OffersHeadingDirectHoldWarp()
    {
        AircraftModel ac = Airborne(new LatLon(37.5000, -121.7000));
        var point = new MenuPoint(new LatLon(37.6000, -121.9000), null, null, [], null);
        var host = new RecordingMenuHost("") { PointDescription = Frd };

        ContextMenu menu = Build(ac, point, host, _ => []);

        int heading = ExpectedMagneticHeading(ac.Position, point.Position);
        string fly = $"Fly heading {new MagneticHeading(heading).ToDisplayString()}";
        Assert.Equal(
            [fly, $"Direct to {Frd}", $"Append direct to {Frd}", $"Hold at {Frd} (left)", $"Hold at {Frd} (right)", Separator, $"Warp here ({Frd})"],
            Labels(menu.Items)
        );

        foreach (MenuItem item in menu.Items.OfType<MenuItem>().Take(5))
        {
            Click(item);
        }

        Assert.Equal(
            [
                (Callsign, $"FH {heading}", "AB"),
                (Callsign, $"DCT {Frd}", "AB"),
                (Callsign, $"ADCT {Frd}", "AB"),
                (Callsign, $"HFIXL {Frd}", "AB"),
                (Callsign, $"HFIXR {Frd}", "AB"),
            ],
            host.Sent
        );

        Click(Item(menu.Items, $"Warp here ({Frd})"));
        Assert.Equal([(Callsign, Frd, 120, 33000, 280)], host.WarpPopups);
        Func<string, int, int, int, Task>? submit = host.WarpSubmit;
        Assert.NotNull(submit);
        await submit(Frd, 90, 12000, 250);
        Assert.Equal((Callsign, $"WARP {Frd} 90 12000 250", "AB"), host.Sent[^1]);
        Assert.NotEmpty(host.DescribedPoints);
        Assert.All(host.DescribedPoints, described => Assert.Equal(point.Position, described));
    }

    [AvaloniaFact]
    public void AirbornePoint_NotNavigatingToAFix_HasNoAppendDirectTo()
    {
        AircraftModel ac = Airborne(new LatLon(37.5000, -121.7000));
        ac.NavigatingTo = "";
        var point = new MenuPoint(new LatLon(37.6000, -121.9000), null, null, [], null);
        var host = new RecordingMenuHost("") { PointDescription = Frd };

        ContextMenu menu = Build(ac, point, host, _ => []);

        List<string> labels = Labels(menu.Items);
        Assert.Contains($"Direct to {Frd}", labels);
        Assert.DoesNotContain($"Append direct to {Frd}", labels);
    }

    // The bearing from KOAK to a point due true north is 000 true; the item flies it as a magnetic heading at the
    // aircraft's position (about 13 degrees east variation), rounded to five degrees.
    [AvaloniaFact]
    public void AirbornePoint_FlyHeading_IsMagnetic()
    {
        var koak = new LatLon(37.7213, -122.2208);
        // The 345 pin holds while KOAK's variation stays in this band (000 true less 12.5-17.5 east rounds to 345); when
        // the declination model drifts out of it, this guard names the cause instead of the pin failing unexplained.
        Assert.InRange(MagneticDeclination.GetDeclination(koak), 12.5, 17.5);
        AircraftModel ac = Airborne(koak);
        var point = new MenuPoint(new LatLon(koak.Lat + 0.5, koak.Lon), null, null, [], null);
        var host = new RecordingMenuHost("");

        ContextMenu menu = Build(ac, point, host, _ => []);

        MenuItem fly = Item(menu.Items, "Fly heading 345");
        Click(fly);
        Assert.Equal([(Callsign, "FH 345", "AB")], host.Sent);
    }

    [AvaloniaFact]
    public void AirbornePoint_NoFrd_OffersOnlyFlyHeading()
    {
        AircraftModel ac = Airborne(new LatLon(37.5000, -121.7000));
        var point = new MenuPoint(new LatLon(37.6000, -121.9000), null, null, [], null);
        var host = new RecordingMenuHost("");

        ContextMenu menu = Build(ac, point, host, _ => []);

        string label = Assert.Single(Labels(menu.Items));
        Assert.StartsWith("Fly heading ", label);
    }

    // --- Ground ---------------------------------------------------------------------------

    [AvaloniaFact]
    public void GroundNode_OffersTaxiHerePushToCustomTaxiWarpG()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        GroundNodeDto start = OakNode("Spot", "I30");
        GroundNodeDto spot = OakNode("Spot", "1");
        AircraftModel ac = GroundAircraft(new LatLon(start.Latitude, start.Longitude));
        var main = new MainViewModel(new FakeFilePickerService());
        main.Aircraft.Clear();
        main.Aircraft.Add(ac);
        main.Ground.SetLayoutForTesting(MenuGoldenFixtures.OakLayoutForClient);
        var anchor = new Border();
        var window = new Window { Content = anchor };
        window.ShowAndRunLayout();
        var client = new ClientMenuHost(main, ac, anchor);
        var host = new SendCapturingHost(client, "AB");
        var point = new MenuPoint(new LatLon(spot.Latitude, spot.Longitude), spot, null, [], null);

        ContextMenu menu = Build(ac, point, host, _ => []);

        IReadOnlyList<MenuCommandChoice> taxi = client.GetTaxiChoices(Callsign, spot, null);
        MenuCommandChoice taxiChoice = Assert.Single(taxi);
        Assert.NotEqual("No route found", taxiChoice.Label);
        Assert.Equal([taxiChoice.Label, "Push to 1", "Custom taxi…", Separator, "Warp here"], Labels(menu.Items));
        Assert.DoesNotContain(Labels(menu.Items), l => l.StartsWith("Fly heading", StringComparison.Ordinal));

        Click(Item(menu.Items, "Push to 1"));
        Click(Item(menu.Items, "Warp here"));
        Assert.Equal([(Callsign, "PUSH $1", "AB"), (Callsign, $"WARPG #{spot.Id}", "AB")], host.Sent);

        Click(Item(menu.Items, "Custom taxi…"));
        HeadlessWindowExtensions.PumpDispatcher();
        TextBox input = FindTextBox(anchor);
        Assert.Equal("TAXI  $1", input.Text);
        Assert.Equal(5, input.CaretIndex);
    }

    // A threshold click names the runway end that was clicked; Custom taxi… seeds that end, as Taxi here routes to it,
    // not the hold-short node's runway End1.
    [AvaloniaFact]
    public void ThresholdClick_CustomTaxiSeed_UsesTheClickedEnd()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        GroundNodeDto start = OakNode("Spot", "I30");
        GroundNodeDto holdShort = MenuGoldenFixtures.OakLayoutForClient.Nodes.First(n => (n.Type == "RunwayHoldShort") && (n.RunwayId is not null));
        string clickedEnd = RunwayIdentifier.Parse(holdShort.RunwayId!).End2;
        AircraftModel ac = GroundAircraft(new LatLon(start.Latitude, start.Longitude));
        var main = new MainViewModel(new FakeFilePickerService());
        main.Aircraft.Clear();
        main.Aircraft.Add(ac);
        main.Ground.SetLayoutForTesting(MenuGoldenFixtures.OakLayoutForClient);
        var anchor = new Border();
        var window = new Window { Content = anchor };
        window.ShowAndRunLayout();
        var host = new SendCapturingHost(new ClientMenuHost(main, ac, anchor), "AB");
        var point = new MenuPoint(new LatLon(holdShort.Latitude, holdShort.Longitude), holdShort, clickedEnd, [], null);

        ContextMenu menu = Build(ac, point, host, _ => []);
        Click(Item(menu.Items, "Custom taxi…"));
        HeadlessWindowExtensions.PumpDispatcher();

        string expected = $"RWY {RunwayIdentifier.ToDisplayDesignator(clickedEnd)} TAXI ";
        TextBox input = FindTextBox(anchor);
        Assert.Equal(expected, input.Text);
        Assert.Equal(expected.Length, input.CaretIndex);
    }

    // --- Taxi here over the host's choices -------------------------------------------------

    [AvaloniaFact]
    public void TaxiHere_RendersTheHostsChoiceTree()
    {
        TaxiRoute parentRoute = NewRoute();
        TaxiRoute leafRoute = NewRoute();
        TaxiRoute nestedRoute = NewRoute();
        TaxiRoute nestedLeafRoute = NewRoute();
        var host = new RecordingMenuHost("");
        host.TaxiChoices.Add(
            new MenuCommandChoice(
                "Taxi via B",
                null,
                parentRoute,
                [
                    new MenuCommandChoice("For Departure 30", "TAXI B 30", leafRoute, []),
                    MenuCommandChoice.Separator,
                    new MenuCommandChoice("Unavailable", null, null, []),
                    new MenuCommandChoice(
                        "Crossings",
                        null,
                        nestedRoute,
                        [new MenuCommandChoice("Cross 28L", "TAXI B CROSS 28L", nestedLeafRoute, [])]
                    ),
                ]
            )
        );
        AircraftModel ac = GroundAircraft(new LatLon(37.72, -122.22));

        ContextMenu menu = Build(ac, NodePoint("28R"), host, _ => []);

        Assert.Equal([(Callsign, TaxiNode.Id, (string?)"28R")], host.TaxiChoiceRequests);
        MenuItem top = Item(menu.Items, "Taxi via B");
        Assert.Equal(["For Departure 30", Separator, "Unavailable", "Crossings"], Labels(top.Items));
        Assert.IsType<Avalonia.Controls.Separator>(top.Items[1]);
        MenuItem disabled = Item(top.Items, "Unavailable");
        Assert.False(disabled.IsEnabled);
        Assert.Empty(disabled.Items);
        MenuItem crossings = Item(top.Items, "Crossings");
        Assert.Equal(["Cross 28L"], Labels(crossings.Items));
        MenuItem nestedLeaf = Item(crossings.Items, "Cross 28L");

        RaisePointerEntered(top);
        RaisePointerEntered(Item(top.Items, "For Departure 30"));
        RaisePointerEntered(crossings);
        RaisePointerEntered(nestedLeaf);
        Assert.Equal(4, host.RoutePreviews.Count);
        Assert.Same(parentRoute, host.RoutePreviews[0]);
        Assert.Same(leafRoute, host.RoutePreviews[1]);
        Assert.Same(nestedRoute, host.RoutePreviews[2]);
        Assert.Same(nestedLeafRoute, host.RoutePreviews[3]);

        Click(nestedLeaf);
        Assert.Equal([(Callsign, "TAXI B CROSS 28L", "AB")], host.Sent);
    }

    [AvaloniaFact]
    public void TaxiHere_ZeroChoices_AddsNothing()
    {
        var host = new RecordingMenuHost("");

        ContextMenu menu = Build(GroundAircraft(new LatLon(37.72, -122.22)), NodePoint(null), host, _ => []);

        Assert.Equal(["Custom taxi…", Separator, "Warp here"], Labels(menu.Items));
        Assert.Equal([(Callsign, TaxiNode.Id, (string?)null)], host.TaxiChoiceRequests);
    }

    [AvaloniaFact]
    public void TaxiHere_OneChoice_IsUnwrapped()
    {
        var host = new RecordingMenuHost("");
        host.TaxiChoices.Add(new MenuCommandChoice("Taxi via B", "TAXI B", null, []));

        ContextMenu menu = Build(GroundAircraft(new LatLon(37.72, -122.22)), NodePoint(null), host, _ => []);

        Assert.Equal(["Taxi via B", "Custom taxi…", Separator, "Warp here"], Labels(menu.Items));
        Click(Item(menu.Items, "Taxi via B"));
        Assert.Equal([(Callsign, "TAXI B", "AB")], host.Sent);
    }

    [AvaloniaFact]
    public void TaxiHere_SeveralChoices_WrapUnderTaxiHere()
    {
        var host = new RecordingMenuHost("");
        host.TaxiChoices.Add(new MenuCommandChoice("Taxi via A", "TAXI A", null, []));
        host.TaxiChoices.Add(new MenuCommandChoice("Taxi via B", "TAXI B", null, []));

        ContextMenu menu = Build(GroundAircraft(new LatLon(37.72, -122.22)), NodePoint(null), host, _ => []);

        Assert.Equal(["Taxi here", "Custom taxi…", Separator, "Warp here"], Labels(menu.Items));
        Assert.Equal(["Taxi via A", "Taxi via B"], Labels(Item(menu.Items, "Taxi here").Items));
    }

    [AvaloniaFact]
    public void CustomTaxi_OpensWithTheHostSeed_AndSendsTheTrimmedText()
    {
        var host = new RecordingMenuHost("") { CustomTaxiSeed = new MenuTextSeed("TAXI  E", 5), InputAnswer = "  TAXI B E  " };

        ContextMenu menu = Build(GroundAircraft(new LatLon(37.72, -122.22)), NodePoint("28R"), host, _ => []);
        Click(Item(menu.Items, "Custom taxi…"));

        Assert.Equal([(TaxiNode.Id, (string?)"28R")], host.CustomTaxiSeedRequests);
        Assert.Equal([("TAXI  E", 5)], host.InputSeeds);
        Assert.Equal([(Callsign, "TAXI B E", "AB")], host.Sent);
    }

    [AvaloniaFact]
    public void Build_PointClickWithoutAircraft_Throws()
    {
        var point = new MenuPoint(new LatLon(37.6000, -121.9000), null, null, [], null);

        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            AircraftMenuBuilder.Build(null, new MenuClick(Callsign, null, point, []), new RecordingMenuHost(""), _ => [])
        );
        Assert.Equal("aircraft", ex.ParamName);
    }

    // --- Runway surface: Taxi to runway ----------------------------------------------------

    [AvaloniaFact]
    public void RunwaySurface_MidRunwayClick_OffersTaxiToBothEnds()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        GroundRunwayDto runway = OakRunway("28R/10L");
        var ids = RunwayIdentifier.Parse(runway.Name);
        AircraftModel ac = GroundAircraft(SpotI30());
        using OakHostScope scope = OakHosts(ac);

        ContextMenu menu = Build(ac, SurfacePoint(Midpoint(runway), runway.Name, null), scope.Host, _ => []);

        List<string> labels = Labels(menu.Items);
        string end1 = $"Taxi to {RunwayIdentifier.ToDisplayDesignator(ids.End1)}";
        string end2 = $"Taxi to {RunwayIdentifier.ToDisplayDesignator(ids.End2)}";
        Assert.Equal([end1, end2], labels.Where(l => l.StartsWith("Taxi to ", StringComparison.Ordinal)).ToList());
        HashSet<string> ends = [end1, end2];
        Assert.True(ends.SetEquals(["Taxi to 28R", "Taxi to 10L"]), $"Ends offered: {end1}, {end2}");
        Assert.NotEmpty(Item(menu.Items, end1).Items);
        Assert.NotEmpty(Item(menu.Items, end2).Items);
        Assert.DoesNotContain(labels, l => (l is "Taxi here" or "Custom taxi…") || l.StartsWith("Push to", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void RunwaySurface_EachEnd_OffersNearestNearClickAndFullLength()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        GroundRunwayDto runway = OakRunway("28R/10L");
        var ids = RunwayIdentifier.Parse(runway.Name);
        AircraftModel ac = GroundAircraft(SpotI30());
        using OakHostScope scope = OakHosts(ac);

        ContextMenu menu = Build(ac, SurfacePoint(Midpoint(runway), runway.Name, null), scope.Host, _ => []);

        foreach (string end in new[] { ids.End1, ids.End2 })
        {
            List<string> targets = Labels(Item(menu.Items, $"Taxi to {RunwayIdentifier.ToDisplayDesignator(end)}").Items);
            Assert.All(targets, label => Assert.Matches(TargetLabel, label));
            AssertEachReasonOnceInOrder(targets);
        }
    }

    // Runway 30/12 from the W3 node just behind its W3 hold short, clicked at that hold short: the route-nearest hold
    // short and the one nearest the click are the same node, shown once carrying both reasons.
    [AvaloniaFact]
    public void RunwaySurface_TargetsResolvingToOneNode_ShowOnceWithBothTags()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AirportGroundLayout domain = MenuGoldenFixtures.OakDomainLayout;
        GroundNode holdShort = MenuGoldenFixtures.HoldShort30AtW3(domain);
        GroundRunwayDto runway = OakRunway(holdShort.RunwayId!.Value.ToString());
        var ids = RunwayIdentifier.Parse(runway.Name);
        AircraftModel ac = GroundAircraft(MenuGoldenFixtures.W3NodeBeforeHoldShort30(domain).Position);
        ac.CurrentPhase = "Taxiing";
        using OakHostScope scope = OakHosts(ac);

        ContextMenu menu = Build(ac, SurfacePoint(holdShort.Position, runway.Name, null), scope.Host, _ => []);

        foreach (string end in new[] { ids.End1, ids.End2 })
        {
            List<string> targets = Labels(Item(menu.Items, $"Taxi to {RunwayIdentifier.ToDisplayDesignator(end)}").Items);
            Assert.StartsWith("At W3 (nearest, near click", targets[0]);
            AssertEachReasonOnceInOrder(targets);
        }
    }

    [AvaloniaFact]
    public void RunwaySurface_Target_OffersTaxiHereChoicesForThatEnd()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        GroundRunwayDto runway = OakRunway("28R/10L");
        var ids = RunwayIdentifier.Parse(runway.Name);
        AircraftModel ac = GroundAircraft(SpotI30());
        using OakHostScope scope = OakHosts(ac);
        LatLon click = Midpoint(runway);

        ContextMenu menu = Build(ac, SurfacePoint(click, runway.Name, null), scope.Host, _ => []);

        foreach (string end in new[] { ids.End1, ids.End2 })
        {
            MenuItem endItem = Item(menu.Items, $"Taxi to {RunwayIdentifier.ToDisplayDesignator(end)}");
            IReadOnlyList<RunwayHoldShortTarget> targets = scope.Client.GetRunwayHoldShortTargets(Callsign, runway.Name, end, click);
            Assert.Equal(targets.Select(t => t.Label), Labels(endItem.Items));
            foreach (RunwayHoldShortTarget target in targets)
            {
                IReadOnlyList<MenuCommandChoice> choices = scope.Client.GetTaxiChoices(Callsign, target.Node, end);
                MenuItem targetItem = Item(endItem.Items, target.Label);
                Assert.Equal(ChoiceTree(choices), ItemTree(targetItem.Items));
            }
        }

        // The first route of the first target sends exactly the Taxi here command for that node and end.
        RunwayHoldShortTarget first = scope.Client.GetRunwayHoldShortTargets(Callsign, runway.Name, ids.End1, click)[0];
        (MenuCommandChoice leaf, MenuItem leafItem) = FirstLeaf(
            scope.Client.GetTaxiChoices(Callsign, first.Node, ids.End1),
            Item(Item(menu.Items, $"Taxi to {RunwayIdentifier.ToDisplayDesignator(ids.End1)}").Items, first.Label).Items
        );
        Click(leafItem);
        Assert.Equal((Callsign, leaf.Command!, "AB"), scope.Host.Sent[^1]);
    }

    [AvaloniaFact]
    public void RunwaySurface_AirborneSelected_OffersNoTaxiToRunway()
    {
        RecordingMenuHost host = HostWithTargets();
        AircraftModel ac = Airborne(new LatLon(37.7250, -122.2000));

        ContextMenu menu = Build(ac, SurfacePoint(new LatLon(37.7250, -122.2000), "28R/10L", null), host, _ => []);

        Assert.DoesNotContain(Labels(menu.Items), l => l.StartsWith("Taxi to ", StringComparison.Ordinal));
        Assert.Empty(host.RunwayHoldShortTargetRequests);
    }

    [AvaloniaFact]
    public void RunwaySurface_NonSurfacePoint_OffersNoTaxiToRunway()
    {
        RecordingMenuHost host = HostWithTargets();
        AircraftModel ac = GroundAircraft(new LatLon(37.72, -122.22));
        var click = new LatLon(37.7250, -122.2000);

        ContextMenu plain = Build(ac, new MenuPoint(click, null, null, [], null), host, _ => []);

        Assert.DoesNotContain(Labels(plain.Items), l => l.StartsWith("Taxi to ", StringComparison.Ordinal));
        Assert.Empty(host.RunwayHoldShortTargetRequests);

        // The same host and click on the runway's surface does offer it, so the plain point is what withholds it.
        ContextMenu surface = Build(ac, SurfacePoint(click, "28R/10L", null), host, _ => []);
        Assert.Equal(["At B (nearest)"], Labels(Item(surface.Items, "Taxi to 28R").Items));
    }

    [AvaloniaFact]
    public void RunwaySurface_WarpHere_WarpsToTheNearestNode()
    {
        var host = new RecordingMenuHost("") { PointDescription = Frd };
        AircraftModel ac = GroundAircraft(new LatLon(37.72, -122.22));

        ContextMenu menu = Build(ac, SurfacePoint(new LatLon(37.7250, -122.2000), "28R/10L", TaxiNode), host, _ => []);

        List<string> labels = Labels(menu.Items);
        Assert.Contains("Warp here", labels);
        Assert.DoesNotContain(labels, l => l.StartsWith("Warp here (", StringComparison.Ordinal));
        Assert.DoesNotContain(labels, l => (l is "Taxi here" or "Custom taxi…") || l.StartsWith("Push to", StringComparison.Ordinal));
        Click(Item(menu.Items, "Warp here"));
        Assert.Equal([(Callsign, $"WARPG #{TaxiNode.Id}", "AB")], host.Sent);
        Assert.Empty(host.WarpPopups);
    }

    // --- Same point, two views -------------------------------------------------------------

    [AvaloniaFact]
    public void SamePoint_SameItemsFromRadarAndGround()
    {
        AircraftModel ac = Airborne(new LatLon(37.5000, -121.7000));
        var point = new MenuPoint(new LatLon(37.6000, -121.9000), null, null, [], null);
        var radarHost = new RecordingMenuHost("") { PointDescription = Frd };
        var groundHost = new RecordingMenuHost("") { PointDescription = Frd };

        ContextMenu radar = Build(ac, point, radarHost, _ => [new MenuItem { Header = "Copy FRD" }, new MenuItem { Header = "Pin marker here" }]);
        ContextMenu ground = Build(ac, point, groundHost, _ => [new MenuItem { Header = "Measure from here" }]);

        List<Control> radarItems = BeforeSection(radar.Items, "Copy FRD");
        List<Control> groundItems = BeforeSection(ground.Items, "Measure from here");
        Assert.NotEmpty(radarItems);
        Assert.Equal(Labels(radarItems), Labels(groundItems));

        foreach ((Control r, Control g) in radarItems.Zip(groundItems))
        {
            if ((r is MenuItem radarItem) && (g is MenuItem groundItem))
            {
                Click(radarItem);
                Click(groundItem);
            }
        }

        Assert.NotEmpty(radarHost.Sent);
        Assert.Equal(radarHost.Sent, groundHost.Sent);
        Assert.Equal(radarHost.WarpPopups, groundHost.WarpPopups);
    }

    // --- Fixtures ---------------------------------------------------------------------------

    private static ContextMenu Build(AircraftModel ac, MenuPoint point, IMenuHost host, Func<MenuContext, IReadOnlyList<Control>> section) =>
        AircraftMenuBuilder.Build(ac, new MenuClick(ac.Callsign, null, point, []), host, section);

    /// <summary>An airborne IFR B738 navigating to SUNOL, heading 120 true at FL330 and 280 knots.</summary>
    private static AircraftModel Airborne(LatLon position) =>
        new()
        {
            Callsign = Callsign,
            AircraftType = "B738",
            FlightRules = "IFR",
            CurrentPhase = "",
            Situation = AircraftSituation.IfrEnroute,
            IsOnGround = false,
            Position = position,
            Heading = new TrueHeading(120),
            Altitude = 33000,
            IndicatedAirspeed = 280,
            GroundSpeed = 280,
            NavigatingTo = "SUNOL",
        };

    private static AircraftModel GroundAircraft(LatLon position) =>
        new()
        {
            Callsign = Callsign,
            AircraftType = "B738",
            FlightRules = "IFR",
            IsOnGround = true,
            CurrentPhase = "At Parking",
            Position = position,
        };

    /// <summary>
    /// The true bearing from <paramref name="from"/> to <paramref name="to"/>, made magnetic at <paramref name="from"/>
    /// and rounded to five degrees.
    /// </summary>
    private static int ExpectedMagneticHeading(LatLon from, LatLon to)
    {
        double magnetic = new TrueHeading(GeoMath.BearingTo(from, to)).ToMagnetic(MagneticDeclination.GetDeclination(from)).Degrees;
        int heading = (int)(Math.Round(magnetic / 5.0) * 5);
        return heading <= 0 ? 360 : heading;
    }

    /// <summary>An unnamed taxiway intersection, the node the recording-host Taxi here tests click; its choices are the host's.</summary>
    private static GroundNodeDto TaxiNode { get; } = new(7, 37.7210, -122.2200, "TaxiwayIntersection", null, null, null);

    private static MenuPoint NodePoint(string? runwayEnd) => new(new LatLon(TaxiNode.Latitude, TaxiNode.Longitude), TaxiNode, runwayEnd, [], null);

    /// <summary>
    /// A Taxi to runway target label: <c>At {taxiway} ({reasons})</c>, or the bare reasons capitalised when the
    /// taxiway is unnamed.
    /// </summary>
    private const string TargetLabel =
        @"^(At \S+ \((nearest|near click|full length)(, (near click|full length))*\)|(Nearest|Near click|Full length)(, (near click|full length))*)$";

    /// <summary>
    /// Each of the three reasons is named by exactly one target, and the targets run in the order of their first reason:
    /// nearest, near click, full length.
    /// </summary>
    private static void AssertEachReasonOnceInOrder(List<string> targets)
    {
        string[] order = ["nearest", "near click", "full length"];
        Assert.Equal(order.Order(), targets.SelectMany(TagsOf).Order());
        List<int> firstRanks = [.. targets.Select(t => Array.IndexOf(order, TagsOf(t).First()))];
        Assert.Equal(firstRanks.Order(), firstRanks);
    }

    /// <summary>The reasons a Taxi to runway target label names, in order.</summary>
    private static IEnumerable<string> TagsOf(string label)
    {
        string tags = label.StartsWith("At ", StringComparison.Ordinal) ? label[(label.IndexOf('(') + 1)..^1] : label;
        return tags.Split(", ").Select(t => t.ToLowerInvariant());
    }

    private static GroundRunwayDto OakRunway(string name)
    {
        var id = RunwayIdentifier.Parse(name);
        return Assert.Single(MenuGoldenFixtures.OakLayoutForClient.Runways!, r => RunwayIdentifier.Parse(r.Name) == id);
    }

    private static LatLon Midpoint(GroundRunwayDto runway) =>
        new((runway.Coordinates[0][0] + runway.Coordinates[^1][0]) / 2.0, (runway.Coordinates[0][1] + runway.Coordinates[^1][1]) / 2.0);

    private static LatLon SpotI30()
    {
        GroundNodeDto spot = OakNode("Spot", "I30");
        return new LatLon(spot.Latitude, spot.Longitude);
    }

    private static MenuPoint SurfacePoint(LatLon click, string runwayName, GroundNodeDto? warpNode) => new(click, null, null, [runwayName], warpNode);

    /// <summary>
    /// The real client host over the KOAK layout with <paramref name="ac"/> selected, and a send-capturing host over
    /// it. Disposing closes the window the hosts' anchor lives in.
    /// </summary>
    private sealed class OakHostScope : IDisposable
    {
        public required ClientMenuHost Client { get; init; }
        public required SendCapturingHost Host { get; init; }
        public required Window Window { get; init; }

        public void Dispose()
        {
            Window.Close();
            HeadlessWindowExtensions.PumpDispatcher();
        }
    }

    private static OakHostScope OakHosts(AircraftModel ac)
    {
        var main = new MainViewModel(new FakeFilePickerService());
        main.Aircraft.Clear();
        main.Aircraft.Add(ac);
        main.Ground.SetLayoutForTesting(MenuGoldenFixtures.OakLayoutForClient);
        var anchor = new Border();
        var window = new Window { Content = anchor };
        window.ShowAndRunLayout();
        var client = new ClientMenuHost(main, ac, anchor);
        return new OakHostScope
        {
            Client = client,
            Host = new SendCapturingHost(client, "AB"),
            Window = window,
        };
    }

    /// <summary>A recording host answering one target, at <see cref="TaxiNode"/>, for each end of 28R/10L, with one taxi choice there.</summary>
    private static RecordingMenuHost HostWithTargets()
    {
        var host = new RecordingMenuHost("");
        host.TaxiChoices.Add(new MenuCommandChoice("Taxi via B", "TAXI B", null, []));
        host.RunwayHoldShortTargets["28R"] = [new RunwayHoldShortTarget(TaxiNode, "At B (nearest)")];
        host.RunwayHoldShortTargets["10L"] = [new RunwayHoldShortTarget(TaxiNode, "At B (nearest)")];
        return host;
    }

    /// <summary>The choice tree's labels as one string, each child list in brackets after its parent.</summary>
    private static string ChoiceTree(IReadOnlyList<MenuCommandChoice> choices) =>
        string.Join(
            "|",
            choices.Select(c =>
                ReferenceEquals(c, MenuCommandChoice.Separator) ? Separator
                : (c.Children.Count > 0) ? $"{c.Label}[{ChoiceTree(c.Children)}]"
                : c.Label
            )
        );

    /// <summary>The menu items' headers as one string, in <see cref="ChoiceTree"/>'s shape.</summary>
    private static string ItemTree(ItemCollection items) =>
        string.Join(
            "|",
            items.Select(i =>
                i switch
                {
                    Avalonia.Controls.Separator => Separator,
                    MenuItem { Items.Count: > 0 } m => $"{m.Header}[{ItemTree(m.Items)}]",
                    MenuItem m => m.Header as string ?? "",
                    _ => i?.GetType().Name ?? "null",
                }
            )
        );

    /// <summary>
    /// The first choice that sends a command, walking <paramref name="choices"/> and <paramref name="items"/> together,
    /// with its item.
    /// </summary>
    private static (MenuCommandChoice Choice, MenuItem Item) FirstLeaf(IReadOnlyList<MenuCommandChoice> choices, ItemCollection items)
    {
        foreach ((MenuCommandChoice choice, object? item) in choices.Zip(items))
        {
            if ((item is MenuItem menuItem) && (choice.Children.Count > 0))
            {
                return FirstLeaf(choice.Children, menuItem.Items);
            }

            if ((item is MenuItem leafItem) && (choice.Command is not null))
            {
                return (choice, leafItem);
            }
        }

        throw new InvalidOperationException("No taxi choice sends a command");
    }

    private static TaxiRoute NewRoute() => new() { Segments = [], HoldShortPoints = [] };

    /// <summary>
    /// Raises a pointer enter on <paramref name="item"/> with a real <see cref="PointerEventArgs"/>, which the hover
    /// handler requires.
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

    private static GroundNodeDto OakNode(string type, string name) =>
        MenuGoldenFixtures.OakLayoutForClient.Nodes.First(n => (n.Type == type) && (n.Name == name));

    /// <summary>The controls before the separator that heads the view section starting at <paramref name="sectionHeader"/>.</summary>
    private static List<Control> BeforeSection(ItemCollection items, string sectionHeader)
    {
        List<Control> controls = [.. items.OfType<Control>()];
        int section = controls.FindIndex(c => (c is MenuItem m) && (m.Header as string == sectionHeader));
        Assert.True(section > 0, $"No view section starting at '{sectionHeader}'");
        Assert.IsType<Avalonia.Controls.Separator>(controls[section - 1]);
        return controls[..(section - 1)];
    }

    private static List<string> Labels(IEnumerable<object?> items) =>
        [
            .. items.Select(i =>
                i switch
                {
                    Avalonia.Controls.Separator => Separator,
                    MenuItem m => m.Header as string ?? "",
                    _ => i?.GetType().Name ?? "null",
                }
            ),
        ];

    private static MenuItem Item(ItemCollection items, string header)
    {
        MenuItem? item = items.OfType<MenuItem>().FirstOrDefault(m => m.Header is string s && s == header);
        Assert.NotNull(item);
        return item;
    }

    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    private static TextBox FindTextBox(Control anchor)
    {
        var overlay = OverlayLayer.GetOverlayLayer(anchor);
        Assert.NotNull(overlay);
        Popup? popup = overlay.Children.OfType<Popup>().LastOrDefault();
        Assert.NotNull(popup);
        TextBox? textBox = popup.Child?.GetLogicalDescendants().OfType<TextBox>().FirstOrDefault();
        Assert.NotNull(textBox);
        return textBox;
    }
}
