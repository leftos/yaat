using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Xunit;
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
        Assert.StartsWith("Fly heading ", labels[0]);
        Assert.DoesNotContain("Draw taxi route…", labels);
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
    public void GroundNode_NoMainViewModel_OpensOnlyTheViewSection()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        var main = new MainViewModel(new FakeFilePickerService());
        main.Ground.SetLayoutForTesting(MenuGoldenFixtures.OakLayoutForClient);
        GroundNodeDto start = OakNode("Spot", "I30");
        AircraftModel ac = GroundAircraft(new LatLon(start.Latitude, start.Longitude));
        main.Aircraft.Clear();
        main.Aircraft.Add(ac);
        main.Ground.SelectedAircraft = ac;
        var view = new GroundView { DataContext = main.Ground };

        ContextMenu? menu = view.BuildNodePointMenu(main.Ground, OakNode("Spot", "1").Id, default);

        Assert.NotNull(menu);
        Assert.Equal(["Draw taxi route…", "Push route…"], Labels(menu.Items));
    }

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
        Assert.StartsWith("Fly heading ", labels[0]);
        Assert.StartsWith("MVA", labels[^1]);
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

    /// <summary>A ground view over the main view model's primary ground view model with the OAK layout, parented to a host carrying the main view model.</summary>
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
