using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Ground;
using Yaat.Client.Views.Radar;
using Yaat.Sim;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Mva;
using Yaat.Sim.Situation;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The views' point-menu wiring: what the radar map, a ground taxi node and a runway threshold hand the shared builder,
/// and when they open their own point section alone.
/// </summary>
public class PointMenuViewTests
{
    private const string Callsign = "AAL202";
    private const string Separator = "-";

    // --- Ground node ------------------------------------------------------------------------

    [AvaloniaFact]
    public void GroundNode_AirborneSelected_OffersNoDrawTaxiRoute()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (GroundView view, MainViewModel main) = GroundHarness(Airborne(new LatLon(37.70, -122.20)));
        GroundNodeDto node = OakNode("Spot", "1");

        ContextMenu? menu = view.BuildNodePointMenu(main.Ground, node.Id, default);

        Assert.NotNull(menu);
        List<string> labels = Labels(menu.Items);
        Assert.Equal([$"{Callsign} · B738 → spot 1", Separator], labels[..2]);
        Assert.StartsWith("Fly heading ", labels[2]);
        Assert.DoesNotContain("Draw taxi route…", labels);
    }

    // --- Push route gate --------------------------------------------------------------------

    [AvaloniaFact]
    public void GroundNode_TugReachable_OffersPushRouteAndPushTo()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (GroundView view, MainViewModel main) = GroundHarness(ParkedAtStand25());
        Show(Parent(view));

        ContextMenu? menu = view.BuildNodePointMenu(main.Ground, OakNode("Spot", ReachableSpot).Id, default);

        Assert.NotNull(menu);
        List<string> labels = Labels(menu.Items);
        Assert.Contains("Push route…", labels);
        Assert.Contains($"Push to {ReachableSpot}", labels);
        Assert.Contains(MenuIds.PointPushTo, StripTags((ContextMenu)menu));
    }

    // The tug planner refuses a move over 2,000 ft, so the named spot farthest from the aircraft is out of reach: neither
    // Push route… nor Push to is offered there, as text or as an icon.
    [AvaloniaFact]
    public void GroundNode_TugUnreachable_OffersNeitherPushRouteNorPushTo()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel ac = ParkedAtStand25();
        (GroundView view, MainViewModel main) = GroundHarness(ac);
        Show(Parent(view));
        GroundNodeDto far = MenuGoldenFixtures
            .OakLayoutForClient.Nodes.Where(n => (n.Type == "Spot") && !string.IsNullOrEmpty(n.Name))
            .MaxBy(n => FeetFrom(ac, n))!;
        Assert.True(FeetFrom(ac, far) > 2000);

        ContextMenu? menu = view.BuildNodePointMenu(main.Ground, far.Id, default);

        Assert.NotNull(menu);
        List<string> labels = Labels(menu.Items);
        Assert.Contains("Draw taxi route…", labels);
        Assert.DoesNotContain("Push route…", labels);
        Assert.DoesNotContain($"Push to {far.Name}", labels);
        Assert.DoesNotContain(MenuIds.PointPushTo, StripTags((ContextMenu)menu));
    }

    // The server's stand departure decides the push gate, not the stand's geometry the client sees: the stand-25 aircraft
    // that is offered both above loses both, as text and as an icon, once the server says its stand is taxied out of.
    [AvaloniaFact]
    public void GroundNode_TaxiOutStand_OffersNeitherPushRouteNorPushTo()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel ac = ParkedAtStand25();
        ac.StandDeparture = StandDeparture.TaxiOut;
        (GroundView view, MainViewModel main) = GroundHarness(ac);
        Show(Parent(view));

        ContextMenu? menu = view.BuildNodePointMenu(main.Ground, OakNode("Spot", ReachableSpot).Id, default);

        Assert.NotNull(menu);
        List<string> labels = Labels(menu.Items);
        Assert.Contains("Draw taxi route…", labels);
        Assert.DoesNotContain("Push route…", labels);
        Assert.DoesNotContain($"Push to {ReachableSpot}", labels);
        Assert.DoesNotContain(MenuIds.PointPushTo, StripTags((ContextMenu)menu));
    }

    [AvaloniaFact]
    public void GroundNode_NothingSelected_OpensOnlyTheViewSection()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (GroundView view, MainViewModel main) = GroundHarness(null);
        Show(Parent(view));
        GroundNodeDto node = OakNode("Spot", "1");

        ContextMenu? menu = view.BuildNodePointMenu(main.Ground, node.Id, default);

        Assert.NotNull(menu);
        Assert.Equal(["Measure from here"], Labels(menu.Items));
    }

    [AvaloniaFact]
    public void GroundNode_ParkedSelected_ShowsPointStripWithTaxiHere()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        GroundNodeDto start = OakNode("Spot", "I30");
        (GroundView view, MainViewModel main) = GroundHarness(GroundAircraft(new LatLon(start.Latitude, start.Longitude)));
        Show(Parent(view));

        ContextMenu? menu = view.BuildNodePointMenu(main.Ground, OakNode("Spot", "1").Id, default);

        Assert.NotNull(menu);
        Assert.Equal($"{Callsign} · B738 → spot 1", Assert.IsType<MenuItem>(menu.Items[0]).Header);
        Assert.IsType<Avalonia.Controls.Separator>(menu.Items[1]);
        MenuItem strip = Assert.IsType<MenuItem>(menu.Items[2]);
        Assert.True(QuickCommandStrip.IsStrip(strip));
        Assert.Contains(MenuIds.PointTaxiHere, QuickCommandStrip.Buttons(strip).Select(b => (string)b.Tag!));
        Assert.IsType<Avalonia.Controls.Separator>(menu.Items[3]);
    }

    [AvaloniaFact]
    public void GroundNode_NothingSelected_ShowsNoStrip()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (GroundView view, MainViewModel main) = GroundHarness(null);
        Show(Parent(view));

        ContextMenu? menu = view.BuildNodePointMenu(main.Ground, OakNode("Spot", "1").Id, default);

        Assert.NotNull(menu);
        Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), QuickCommandStrip.IsStrip);
    }

    [AvaloniaFact]
    public void GroundNode_StripHoldsOnlyPointItems()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (GroundView view, MainViewModel main) = GroundHarness(ParkedAtStand25());
        Show(Parent(view));

        ContextMenu? menu = view.BuildNodePointMenu(main.Ground, OakNode("Spot", ReachableSpot).Id, default);

        Assert.NotNull(menu);
        MenuItem strip = Assert.Single(menu.Items.OfType<MenuItem>(), QuickCommandStrip.IsStrip);
        List<string> tags = [.. QuickCommandStrip.Buttons(strip).Select(b => (string)b.Tag!)];
        string[] pointIds = [MenuIds.PointTaxiHere, MenuIds.PointTaxiToRunway, MenuIds.PointPushTo, MenuIds.PointCustomTaxi];
        Assert.All(tags, tag => Assert.Contains(tag, pointIds));
        Assert.Contains(MenuIds.PointPushTo, tags);
        Assert.Contains(MenuIds.PointCustomTaxi, tags);
    }

    [AvaloniaFact]
    public void GroundNode_NoMainViewModel_OpensOnlyTheViewSection()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        var main = new MainViewModel(new FakeFilePickerService());
        main.Ground.SetLayoutForTesting(MenuGoldenFixtures.OakLayoutForClient);
        AircraftModel ac = ParkedAtStand25();
        main.Aircraft.Clear();
        main.Aircraft.Add(ac);
        main.Ground.SelectedAircraft = ac;
        var view = new GroundView { DataContext = main.Ground };

        ContextMenu? menu = view.BuildNodePointMenu(main.Ground, OakNode("Spot", ReachableSpot).Id, default);

        Assert.NotNull(menu);
        Assert.Equal(["Draw taxi route…", "Push route…"], Labels(menu.Items));
    }

    /// <summary>
    /// A KOAK spot a tug reaches from stand 25 (about 1,000 ft). Spot 1, nearer some stands, sits on taxiway U, where the
    /// planner refuses to leave an aircraft.
    /// </summary>
    internal const string ReachableSpot = "E";

    /// <summary>A parked aircraft at KOAK stand 25, at the stand's heading: <see cref="ReachableSpot"/> is within a tug's reach.</summary>
    internal static AircraftModel ParkedAtStand25()
    {
        GroundNodeDto stand = OakNode("Parking", "25");
        AircraftModel ac = GroundAircraft(new LatLon(stand.Latitude, stand.Longitude));
        ac.Heading = new TrueHeading(stand.Heading!.Value);
        Assert.InRange(FeetFrom(ac, OakNode("Spot", ReachableSpot)), 150, 2000);
        return ac;
    }

    private static double FeetFrom(AircraftModel ac, GroundNodeDto node) =>
        GeoMath.DistanceNm(ac.Position, new LatLon(node.Latitude, node.Longitude)) * GeoMath.FeetPerNm;

    /// <summary>The catalog ids of the menu's strip buttons, in strip order; none without a strip.</summary>
    private static List<string> StripTags(ContextMenu menu) =>
        menu.Items.OfType<MenuItem>().FirstOrDefault(QuickCommandStrip.IsStrip) is { } strip
            ? [.. QuickCommandStrip.Buttons(strip).Select(b => (string)b.Tag!)]
            : [];

    // --- Runway threshold -------------------------------------------------------------------

    // The clicked end is chosen as the End2 of the hold-short node the view resolves for it, so a view that dropped the
    // clicked end would seed that node's End1 instead.
    [AvaloniaFact]
    public void Threshold_CustomTaxiSeed_NamesTheClickedEnd()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        GroundNodeDto start = OakNode("Spot", "I30");
        (GroundView view, MainViewModel main) = GroundHarness(GroundAircraft(new LatLon(start.Latitude, start.Longitude)));
        Show(Parent(view));
        string clickedEnd = ResolvedEnd2(main);

        ContextMenu? menu = view.BuildRunwayThresholdMenu(main.Ground, clickedEnd);

        Assert.NotNull(menu);
        Click(Item(menu.Items, "Custom taxi…"));
        HeadlessWindowExtensions.PumpDispatcher();
        string expected = $"RWY {RunwayIdentifier.ToDisplayDesignator(clickedEnd)} TAXI ";
        Assert.Equal(expected, FindTextBox(view).Text);
    }

    [AvaloniaFact]
    public void Threshold_HoldShortMissingFromTheLayout_OpensNoMenu()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        GroundNodeDto start = OakNode("Spot", "I30");
        AircraftModel ac = GroundAircraft(new LatLon(start.Latitude, start.Longitude));
        (GroundView view, MainViewModel main) = GroundHarness(ac);
        int missing = MenuGoldenFixtures.OakLayoutForClient.Nodes.Max(n => n.Id) + 1;

        Assert.Null(view.BuildThresholdPointMenu(main.Ground, ac, "30", missing));
    }

    // --- Runway surface ---------------------------------------------------------------------

    [AvaloniaFact]
    public void GroundView_RunwaySurfaceClick_BuildsTheTaxiToRunwayMenu()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        GroundNodeDto start = OakNode("Spot", "I30");
        (GroundView view, MainViewModel main) = GroundHarness(GroundAircraft(new LatLon(start.Latitude, start.Longitude)));
        Show(Parent(view));
        var id = RunwayIdentifier.Parse("28R/10L");
        GroundRunwayDto runway = Assert.Single(MenuGoldenFixtures.OakLayoutForClient.Runways!, r => RunwayIdentifier.Parse(r.Name) == id);
        var click = new LatLon(
            (runway.Coordinates[0][0] + runway.Coordinates[^1][0]) / 2.0,
            (runway.Coordinates[0][1] + runway.Coordinates[^1][1]) / 2.0
        );
        GroundNodeDto nearest = main.Ground.GetNode(main.Ground.DomainLayout!.FindNearestNode(click)!.Id)!;

        ContextMenu? menu = view.BuildRunwaySurfaceMenu(main.Ground, [runway.Name], click, nearest, default);

        Assert.NotNull(menu);
        List<string> labels = Labels(menu.Items);
        Assert.Contains("Taxi to 28R", labels);
        Assert.Contains("Taxi to 10L", labels);
        Assert.DoesNotContain(labels, l => (l is "Taxi here" or "Custom taxi…") || l.StartsWith("Push to", StringComparison.Ordinal));
        // Warp here, then the ground's section: the measuring items and Draw taxi route… from the node nearest the click.
        int warp = labels.IndexOf("Warp here");
        int measure = labels.IndexOf("Measure from here");
        Assert.InRange(warp, labels.IndexOf("Taxi to 10L") + 1, measure - 1);
        Assert.Equal("Draw taxi route…", labels[^1]);
    }

    // --- Radar map --------------------------------------------------------------------------

    [AvaloniaFact]
    public void Map_AirborneSelected_PointItemsThenTheViewSection()
    {
        (RadarView view, MainViewModel main) = RadarHarness();
        AircraftModel ac = Airborne(new LatLon(37.50, -121.70));
        main.Aircraft.Add(ac);
        main.Radar.SelectedAircraft = ac;

        ContextMenu menu = view.BuildMapPointMenu(main.Radar, new LatLon(37.60, -121.90), default);

        List<string> labels = Labels(menu.Items);
        Assert.Equal($"{Callsign} · B738 → this point", labels[0]);
        Assert.EndsWith(" from the aircraft", labels[1]);
        Assert.Equal(Separator, labels[2]);
        Assert.Contains(labels, l => l.StartsWith("Fly heading ", StringComparison.Ordinal));
        Assert.StartsWith("MVA", labels[^1]);
    }

    // The header's second row names the point by its FRD, so the radar section leaves out its own FRD row.
    [AvaloniaFact]
    public void Map_SelectedWithFrd_HeaderCarriesTheFrdAndTheSectionDropsItsRow()
    {
        (RadarView view, MainViewModel main) = RadarHarness();
        main.Radar.SetFixes([("OAK", 37.7213, -122.2208)]);
        AircraftModel ac = Airborne(new LatLon(37.50, -121.70));
        main.Aircraft.Add(ac);
        main.Radar.SelectedAircraft = ac;
        var position = new LatLon(37.80, -122.20);
        string frd = FrdResolver.ToFrd(position.Lat, position.Lon, main.Radar.Fixes!)!;

        ContextMenu menu = view.BuildMapPointMenu(main.Radar, position, default);

        List<string> labels = Labels(menu.Items);
        Assert.StartsWith($"{frd} · ", labels[1]);
        Assert.DoesNotContain(frd, labels);
        Assert.Contains("Copy FRD", labels);
    }

    [AvaloniaFact]
    public void Map_NothingSelectedWithFrd_KeepsTheFrdRow()
    {
        (RadarView view, MainViewModel main) = RadarHarness();
        main.Radar.SetFixes([("OAK", 37.7213, -122.2208)]);
        var position = new LatLon(37.80, -122.20);
        string frd = FrdResolver.ToFrd(position.Lat, position.Lon, main.Radar.Fixes!)!;

        ContextMenu menu = view.BuildMapPointMenu(main.Radar, position, default);

        List<string> labels = Labels(menu.Items);
        Assert.Equal([frd, "Copy FRD"], labels[..2]);
    }

    // --- MVA row ------------------------------------------------------------------------------

    [AvaloniaFact]
    public void Map_MvaRow_NamesTheFloorAndSector()
    {
        (RadarView view, MainViewModel main) = RadarHarness();
        var koak = new LatLon(37.7213, -122.2208);
        MvaSector sector = MvaDatabase.Default.FindSector(koak)!;
        Assert.Equal(2000, sector.FloorFtMsl);

        ContextMenu menu = view.BuildMapPointMenu(main.Radar, koak, default);

        MenuItem row = Assert.IsType<MenuItem>(menu.Items[^1]);
        Assert.Equal($"MVA 2,000 ft (sector {sector.Sector})", row.Header);
        Assert.False(row.IsEnabled);
        Assert.Equal(0.8, row.Opacity);
    }

    [AvaloniaFact]
    public void Map_NoMvaData_HasNoMvaRow()
    {
        (RadarView view, MainViewModel main) = RadarHarness();
        var midAtlantic = new LatLon(40.0, -70.0);
        Assert.Null(MvaDatabase.Default.FindSector(midAtlantic));

        ContextMenu menu = view.BuildMapPointMenu(main.Radar, midAtlantic, default);

        Assert.DoesNotContain(Labels(menu.Items), l => l.StartsWith("MVA", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void Map_NothingSelected_OpensOnlyTheViewSection()
    {
        (RadarView view, MainViewModel main) = RadarHarness();

        ContextMenu menu = view.BuildMapPointMenu(main.Radar, new LatLon(37.60, -121.90), default);

        AssertOnlyMapSection(Labels(menu.Items));
    }

    [AvaloniaFact]
    public void Map_NoMainViewModel_OpensOnlyTheViewSection()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        AircraftModel ac = Airborne(new LatLon(37.50, -121.70));
        main.Aircraft.Add(ac);
        main.Radar.SelectedAircraft = ac;
        var view = new RadarView { DataContext = main.Radar };

        ContextMenu menu = view.BuildMapPointMenu(main.Radar, new LatLon(37.60, -121.90), default);

        AssertOnlyMapSection(Labels(menu.Items));
    }

    // --- Fixtures ---------------------------------------------------------------------------

    /// <summary>The radar's own point section with no fixes loaded: the measuring items, then the MVA row, and no point item.</summary>
    private static void AssertOnlyMapSection(List<string> labels)
    {
        Assert.StartsWith("MVA", labels[^1]);
        Assert.All(labels[..^1], label => Assert.True((label == Separator) || label.StartsWith("Measure", StringComparison.Ordinal), label));
    }

    /// <summary>
    /// A ground view over the main view model's primary ground view model with the OAK layout, parented to a host carrying
    /// the main view model.
    /// </summary>
    private static (GroundView View, MainViewModel Main) GroundHarness(AircraftModel? selected)
    {
        var main = new MainViewModel(new FakeFilePickerService());
        main.Ground.SetLayoutForTesting(MenuGoldenFixtures.OakLayoutForClient);
        main.Aircraft.Clear();
        if (selected is not null)
        {
            main.Aircraft.Add(selected);
            main.Ground.SelectedAircraft = selected;
        }

        var view = new GroundView { DataContext = main.Ground };
        var host = new Grid { DataContext = main };
        host.Children.Add(view);
        return (view, main);
    }

    private static (RadarView View, MainViewModel Main) RadarHarness()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        main.Aircraft.Clear();
        var view = new RadarView { DataContext = main.Radar };
        var host = new Grid { DataContext = main };
        host.Children.Add(view);
        return (view, main);
    }

    private static Control Parent(Control view) => (Control)view.Parent!;

    private static void Show(Control content) => new Window { Content = content }.ShowAndRunLayout();

    /// <summary>
    /// A runway end whose nearest reachable hold-short node, as the ground view model resolves it for the selected
    /// aircraft, names it as its runway's End2.
    /// </summary>
    private static string ResolvedEnd2(MainViewModel main)
    {
        AircraftModel ac = main.Ground.SelectedAircraft!;
        IEnumerable<string> ends = MenuGoldenFixtures
            .OakLayoutForClient.Nodes.Where(n => (n.Type == "RunwayHoldShort") && (n.RunwayId is not null))
            .Select(n => RunwayIdentifier.Parse(n.RunwayId!).End2)
            .Distinct();
        foreach (string end in ends)
        {
            if (
                (main.Ground.FindNearestHoldShortNodeForRunwayEnd(ac, end) is { } id)
                && (main.Ground.GetNode(id) is { RunwayId: { } runwayId })
                && (RunwayIdentifier.Parse(runwayId).End2 == end)
            )
            {
                return end;
            }
        }

        throw new InvalidOperationException("No OAK runway end resolves to a hold-short node naming it as End2");
    }

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

    private static GroundNodeDto OakNode(string type, string name) =>
        MenuGoldenFixtures.OakLayoutForClient.Nodes.First(n => (n.Type == type) && (n.Name == name));

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

    private static TextBox FindTextBox(Visual anchor)
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
