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
        var point = new MenuPoint(new LatLon(37.6000, -121.9000), null, null);
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
        var point = new MenuPoint(new LatLon(37.6000, -121.9000), null, null);
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
        var point = new MenuPoint(new LatLon(koak.Lat + 0.5, koak.Lon), null, null);
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
        var point = new MenuPoint(new LatLon(37.6000, -121.9000), null, null);
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
        var point = new MenuPoint(new LatLon(spot.Latitude, spot.Longitude), spot, null);

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
        var point = new MenuPoint(new LatLon(holdShort.Latitude, holdShort.Longitude), holdShort, clickedEnd);

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
        var point = new MenuPoint(new LatLon(37.6000, -121.9000), null, null);

        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            AircraftMenuBuilder.Build(null, new MenuClick(Callsign, null, point, []), new RecordingMenuHost(""), _ => [])
        );
        Assert.Equal("aircraft", ex.ParamName);
    }

    // --- Same point, two views -------------------------------------------------------------

    [AvaloniaFact]
    public void SamePoint_SameItemsFromRadarAndGround()
    {
        AircraftModel ac = Airborne(new LatLon(37.5000, -121.7000));
        var point = new MenuPoint(new LatLon(37.6000, -121.9000), null, null);
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

    private static MenuPoint NodePoint(string? runwayEnd) => new(new LatLon(TaxiNode.Latitude, TaxiNode.Longitude), TaxiNode, runwayEnd);

    private static TaxiRoute NewRoute() => new() { Segments = [], HoldShortPoints = [] };

    /// <summary>Raises a pointer enter on <paramref name="item"/> with a real <see cref="PointerEventArgs"/>, which the hover handler requires.</summary>
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
