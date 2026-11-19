using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Situation;
using Yaat.Sim.Testing;

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
    private const string Strip = "[strip]";

    // --- Airborne -------------------------------------------------------------------------

    // The icons carry Fly heading, Direct to, the two holds and Warp here; the text rows keep only Fly heading (with its
    // turn row) and Append direct to.
    [AvaloniaFact]
    public void AirbornePoint_OffersHeadingDirectHoldWarp()
    {
        TestVnasData.EnsureInitialized();
        AircraftModel ac = Airborne(new LatLon(37.5000, -121.7000));
        var point = new MenuPoint(new LatLon(37.6000, -121.9000), null, null, [], null);
        var host = new RecordingMenuHost("") { PointDescription = Frd };

        ContextMenu menu = Build(ac, point, host, _ => []);

        int heading = ExpectedMagneticHeading(ac.Position, point.Position);
        string fly = $"Fly heading {new MagneticHeading(heading).ToDisplayString()}";
        List<string> body = Labels(Body(menu));
        Assert.Equal([Strip, Separator, fly], body[..3]);
        Assert.StartsWith($"FH {new MagneticHeading(heading).ToDisplayString()} · ", body[3]);
        Assert.Equal([$"Append direct to {Frd}"], body[4..]);
        Assert.Equal(
            [MenuIds.PointFlyHeading, MenuIds.PointDirectTo, MenuIds.PointHoldLeft, MenuIds.PointHoldRight, MenuIds.PointWarpHere],
            StripTags(menu)
        );

        foreach (string id in (string[])[MenuIds.PointFlyHeading, MenuIds.PointDirectTo, MenuIds.PointHoldLeft, MenuIds.PointHoldRight])
        {
            StripButton(menu, id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }

        Click(Item(menu.Items, $"Append direct to {Frd}"));

        Assert.Equal(
            [
                (Callsign, $"FH {heading}", "AB"),
                (Callsign, $"DCT {Frd}", "AB"),
                (Callsign, $"HFIXL {Frd}", "AB"),
                (Callsign, $"HFIXR {Frd}", "AB"),
                (Callsign, $"ADCT {Frd}", "AB"),
            ],
            host.Sent
        );

        MenuContext context = TestMenuContext.Create(Callsign, "AB", null, false, VfrCommandsForIfr.EnterFinalOnly) with
        {
            Click = new MenuClick(Callsign, null, point, []),
        };
        MenuItem warpHere = Assert.IsType<MenuItem>(MenuCatalog.Get(MenuIds.PointWarpHere).Build(ac, context, host));
        InlineBoxRow[] warpRows = [.. warpHere.Items.OfType<MenuItem>().Select(m => m.Header).OfType<InlineBoxRow>()];
        int warpHeading = int.Parse(ac.HeadingDisplay, CultureInfo.InvariantCulture);
        Assert.Equal(Frd, warpRows[0].Text);
        warpRows[3].Text = "250";
        Assert.Equal(warpHeading.ToString("D3", CultureInfo.InvariantCulture), warpRows[1].CommandColumn.Text);
        warpRows[3].Input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Assert.Equal((Callsign, $"WARP {Frd} {warpHeading:D3} 330 250", "AB"), host.Sent[^1]);
        Assert.NotEmpty(host.DescribedPoints);
        Assert.All(host.DescribedPoints, described => Assert.Equal(point.Position, described));
    }

    // The radar strip's Warp here button opens the Warp flyout over the strip, its FRD box taking the focus with the clicked
    // point's FRD selected, so the first keystroke replaces it.
    [AvaloniaFact]
    public void PointStrip_WarpHereButton_OpensTheWarpFlyout_FrdBoxFocusedWithTheSeedSelected()
    {
        AircraftModel ac = Airborne(new LatLon(37.5000, -121.7000));
        var point = new MenuPoint(new LatLon(37.6000, -121.9000), null, null, [], null);
        var host = new RecordingMenuHost("") { PointDescription = Frd };
        ContextMenu menu = Build(ac, point, host, _ => []);
        var window = new Window { Width = 600, Height = 900 };
        window.Show();
        try
        {
            menu.Open(window);
            HeadlessWindowExtensions.PumpDispatcher();
            menu.UpdateLayout();
            Button button = StripButton(menu, MenuIds.PointWarpHere);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            HeadlessWindowExtensions.PumpDispatcher();
            MenuFlyout flyout = Assert.IsType<MenuFlyout>(FlyoutBase.GetAttachedFlyout(button));
            Assert.True(flyout.IsOpen);
            Assert.IsAssignableFrom<Control>(flyout.Popup.Child).UpdateLayout();
            HeadlessWindowExtensions.PumpDispatcher();

            InlineBoxRow frd = flyout.Items.OfType<MenuItem>().Select(m => m.Header).OfType<InlineBoxRow>().First();
            TextBox inner = frd.Input.GetVisualDescendants().OfType<TextBox>().First();
            Assert.Equal(Frd, frd.Text);
            Assert.True(inner.IsFocused, "the FRD box did not take focus when the strip opened Warp here");
            Assert.Equal((0, Frd.Length), (Math.Min(inner.SelectionStart, inner.SelectionEnd), Math.Max(inner.SelectionStart, inner.SelectionEnd)));
            Assert.Empty(host.Sent);
        }
        finally
        {
            menu.Close();
            window.Close();
        }
    }

    // Enter in the FRD box of the Warp flyout the strip's Warp here button opened sends the seeded warp and closes the menu.
    [AvaloniaFact]
    public void PointStrip_WarpHere_EnterInTheFrdBox_SendsAndClosesTheMenu()
    {
        TestVnasData.EnsureInitialized();
        AircraftModel ac = Airborne(new LatLon(37.5000, -121.7000));
        var point = new MenuPoint(new LatLon(37.6000, -121.9000), null, null, [], null);
        var host = new RecordingMenuHost("") { PointDescription = Frd };
        ContextMenu menu = Build(ac, point, host, _ => []);
        var window = new Window { Width = 600, Height = 900 };
        window.Show();
        try
        {
            menu.Open(window);
            HeadlessWindowExtensions.PumpDispatcher();
            menu.UpdateLayout();
            Button button = StripButton(menu, MenuIds.PointWarpHere);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            HeadlessWindowExtensions.PumpDispatcher();
            MenuFlyout flyout = Assert.IsType<MenuFlyout>(FlyoutBase.GetAttachedFlyout(button));
            Assert.IsAssignableFrom<Control>(flyout.Popup.Child).UpdateLayout();
            HeadlessWindowExtensions.PumpDispatcher();

            InlineBoxRow frd = flyout.Items.OfType<MenuItem>().Select(m => m.Header).OfType<InlineBoxRow>().First();
            frd.Input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            HeadlessWindowExtensions.PumpDispatcher();

            Assert.Equal([(Callsign, $"WARP {Frd}", "AB")], host.Sent);
            Assert.False(menu.IsOpen, "Enter in the strip's Warp flyout should close the menu");
            Assert.False(flyout.IsOpen, "Enter in the strip's Warp flyout should close the flyout too");
        }
        finally
        {
            menu.Close();
            window.Close();
        }
    }

    [AvaloniaFact]
    public void AirbornePoint_NotNavigatingToAFix_HasNoAppendDirectTo()
    {
        AircraftModel ac = Airborne(new LatLon(37.5000, -121.7000));
        ac.NavigatingTo = "";
        var point = new MenuPoint(new LatLon(37.6000, -121.9000), null, null, [], null);
        var host = new RecordingMenuHost("") { PointDescription = Frd };

        ContextMenu menu = Build(ac, point, host, _ => []);

        Assert.Contains(MenuIds.PointDirectTo, StripTags(menu));
        Assert.DoesNotContain($"Append direct to {Frd}", Labels(menu.Items));
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

        List<string> body = Labels(Body(menu));
        Assert.Equal([Strip, Separator], body[..2]);
        Assert.StartsWith("Fly heading ", body[2]);
        Assert.StartsWith("FH ", body[3]);
        Assert.Equal(4, body.Count);
        Assert.Equal([MenuIds.PointFlyHeading], StripTags(menu));
    }

    // WARP leaves the aircraft airborne, so a ground aircraft gets no Warp here at a map point (its ground warp is Warp here
    // at a taxi node, WARPG), and no radar icons.
    [AvaloniaFact]
    public void RadarPoint_GroundAircraft_OffersNoWarpHere()
    {
        var point = new MenuPoint(new LatLon(37.6000, -121.9000), null, null, [], null);
        var host = new RecordingMenuHost("") { PointDescription = Frd };

        ContextMenu menu = Build(GroundAircraft(new LatLon(37.72, -122.22)), point, host, _ => []);

        Assert.DoesNotContain(Labels(menu.Items), l => l.StartsWith("Warp here", StringComparison.Ordinal));
        Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), QuickCommandStrip.IsStrip);
    }

    // --- Fly heading turn row ---------------------------------------------------------------

    [AvaloniaFact]
    public void FlyHeadingTurnRow_NamesALeftTurn()
    {
        (ContextMenu menu, int heading) = FlyHeadingMenu(currentOffsetDeg: 36);

        MenuItem row = Assert.IsType<MenuItem>(menu.Items[menu.Items.IndexOf(Item(menu.Items, FlyLabel(heading))) + 1]);
        Assert.Equal($"FH {heading:D3} · left turn, 36°", row.Header);
        Assert.False(row.IsEnabled);
        Assert.Equal(0.8, row.Opacity);
    }

    [AvaloniaFact]
    public void FlyHeadingTurnRow_NamesARightTurn()
    {
        (ContextMenu menu, int heading) = FlyHeadingMenu(currentOffsetDeg: -20);

        Assert.Contains($"FH {heading:D3} · right turn, 20°", Labels(menu.Items));
    }

    [AvaloniaFact]
    public void FlyHeadingTurnRow_NoneUnderOneDegree()
    {
        (ContextMenu menu, _) = FlyHeadingMenu(currentOffsetDeg: 0.4);

        Assert.DoesNotContain(Labels(menu.Items), l => l.StartsWith("FH ", StringComparison.Ordinal));
    }

    // --- Header ------------------------------------------------------------------------------

    [AvaloniaFact]
    public void Header_RadarPoint_NamesTheAircraftThePointAndItsFrdDistanceBearing()
    {
        var koak = new LatLon(37.7213, -122.2208);
        AircraftModel ac = Airborne(koak);
        var point = new MenuPoint(new LatLon(koak.Lat + 0.15, koak.Lon), null, null, [], null);

        ContextMenu menu = Build(ac, point, new RecordingMenuHost("") { PointDescription = Frd }, _ => []);

        MenuItem title = Assert.IsType<MenuItem>(menu.Items[0]);
        Assert.Equal($"{Callsign} · B738 → this point", title.Header);
        Assert.False(title.IsEnabled);
        Assert.Equal(Avalonia.Media.FontWeight.Bold, title.FontWeight);
        Assert.Equal("", Assert.IsType<QuickCommandLabelLine>(menu.Items[1]).Header);
        MenuItem detail = Assert.IsType<MenuItem>(menu.Items[2]);
        Assert.Equal($"{Frd} · 9 nm, bearing {NorthBearing(koak)} from the aircraft", detail.Header);
        Assert.False(detail.IsEnabled);
        Assert.IsType<Avalonia.Controls.Separator>(menu.Items[3]);
    }

    [AvaloniaFact]
    public void Header_RadarPointUnderOneMile_ShowsTenths()
    {
        var koak = new LatLon(37.7213, -122.2208);
        var point = new MenuPoint(new LatLon(koak.Lat + 0.005, koak.Lon), null, null, [], null);

        ContextMenu menu = Build(Airborne(koak), point, new RecordingMenuHost("") { PointDescription = Frd }, _ => []);

        Assert.Equal($"{Frd} · 0.3 nm, bearing {NorthBearing(koak)} from the aircraft", Labels(menu.Items)[2]);
    }

    [AvaloniaFact]
    public void Header_RadarPointWithoutFrd_ShowsDistanceAndBearingAlone()
    {
        var koak = new LatLon(37.7213, -122.2208);
        var point = new MenuPoint(new LatLon(koak.Lat + 0.15, koak.Lon), null, null, [], null);

        ContextMenu menu = Build(Airborne(koak), point, new RecordingMenuHost(""), _ => []);

        Assert.Equal($"9 nm, bearing {NorthBearing(koak)} from the aircraft", Labels(menu.Items)[2]);
    }

    [AvaloniaFact]
    public void Header_HoldShortClick_NamesRunwayAndTaxiway()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        GroundNode holdShort = MenuGoldenFixtures.HoldShort30AtW3(MenuGoldenFixtures.OakDomainLayout);
        GroundNodeDto node = MenuGoldenFixtures.OakLayoutForClient.Nodes.Single(n => n.Id == holdShort.Id);
        AircraftModel ac = GroundAircraft(SpotI30());
        using OakHostScope scope = OakHosts(ac);

        ContextMenu menu = Build(ac, new MenuPoint(new LatLon(node.Latitude, node.Longitude), node, "30", [], null), scope.Host, _ => []);

        Assert.Equal([$"{Callsign} · B738 → HS 30 at W3", "", Separator], Labels(menu.Items)[..3]);
    }

    [AvaloniaFact]
    public void Header_ParkingNode_NamesTheStand()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        GroundNodeDto stand = MenuGoldenFixtures.OakLayoutForClient.Nodes.First(n => (n.Type == "Parking") && !string.IsNullOrEmpty(n.Name));
        AircraftModel ac = GroundAircraft(SpotI30());
        using OakHostScope scope = OakHosts(ac);

        ContextMenu menu = Build(ac, new MenuPoint(new LatLon(stand.Latitude, stand.Longitude), stand, null, [], null), scope.Host, _ => []);

        Assert.Equal($"{Callsign} · B738 → parking {stand.Name}", Labels(menu.Items)[0]);
    }

    [AvaloniaFact]
    public void Header_SpotNode_NamesTheSpot()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        GroundNodeDto spot = OakNode("Spot", "1");
        AircraftModel ac = GroundAircraft(SpotI30());
        using OakHostScope scope = OakHosts(ac);

        ContextMenu menu = Build(ac, new MenuPoint(new LatLon(spot.Latitude, spot.Longitude), spot, null, [], null), scope.Host, _ => []);

        Assert.Equal($"{Callsign} · B738 → spot 1", Labels(menu.Items)[0]);
    }

    // KOAK's V/W/W4 intersection, where straight edges meet; the fillet tangent node beside it on W4, where straight W4
    // edges meet the W–W4 fillet arcs; and a T node where the T–RAMP fillet arc starts. Each names every taxiway once,
    // an arc's names separately, never the arc's joined "W - W4", and never the ramp.
    [AvaloniaFact]
    public void Header_TaxiwayNodes_NameEachTaxiwayOnce_FilletArcsSplit()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AirportGroundLayout domain = MenuGoldenFixtures.OakDomainLayout;
        GroundNode intersection = domain.Nodes.Values.First(n =>
            (n.Type == GroundNodeType.TaxiwayIntersection) && TaxiwaysAre(n, ["V", "W", "W4"], [])
        );
        GroundNode tangent = domain.Nodes.Values.First(n => TaxiwaysAre(n, ["W4"], ["W", "W4"]));
        GroundNode rampArc = domain.Nodes.Values.First(n => TaxiwaysAre(n, ["T", "RAMP"], ["T", "RAMP"]));
        AircraftModel ac = GroundAircraft(SpotI30());
        using OakHostScope scope = OakHosts(ac);

        Assert.Equal($"{Callsign} · B738 → V / W / W4", Title(intersection));
        Assert.Equal($"{Callsign} · B738 → W / W4", Title(tangent));
        Assert.Equal($"{Callsign} · B738 → T", Title(rampArc));

        string Title(GroundNode node)
        {
            GroundNodeDto dto = MenuGoldenFixtures.OakLayoutForClient.Nodes.Single(n => n.Id == node.Id);
            var point = new MenuPoint(new LatLon(dto.Latitude, dto.Longitude), dto, null, [], null);
            return Labels(Build(ac, point, scope.Host, _ => []).Items)[0];
        }
    }

    /// <summary>
    /// Whether the straight edges at <paramref name="node"/> carry exactly <paramref name="straight"/> and its arcs exactly
    /// <paramref name="arcs"/>.
    /// </summary>
    private static bool TaxiwaysAre(GroundNode node, string[] straight, string[] arcs) =>
        StraightTaxiways(node).SetEquals(straight) && node.Edges.OfType<GroundArc>().SelectMany(a => a.TaxiwayNames).ToHashSet().SetEquals(arcs);

    /// <summary>The taxiway names of the straight (non-arc) edges at <paramref name="node"/>.</summary>
    private static HashSet<string> StraightTaxiways(GroundNode node) => [.. node.Edges.Where(e => e is not GroundArc).Select(e => e.TaxiwayName)];

    [AvaloniaFact]
    public void Header_RunwaySurface_NamesTheRunwaysFirstEnd()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        GroundRunwayDto runway = OakRunway("28R/10L");
        string end = RunwayIdentifier.ToDisplayDesignator(RunwayIdentifier.Parse(runway.Name).End1);
        Assert.Contains(end, (string[])["28R", "10L"]);
        AircraftModel ac = GroundAircraft(SpotI30());
        using OakHostScope scope = OakHosts(ac);

        ContextMenu menu = Build(ac, SurfacePoint(Midpoint(runway), runway.Name, null), scope.Host, _ => []);

        Assert.Equal([$"{Callsign} · B738 → runway {end}", "", Separator], Labels(menu.Items)[..3]);
    }

    // --- Ground ---------------------------------------------------------------------------

    [AvaloniaFact]
    public void GroundNode_OffersTaxiHerePushToCustomTaxiWarpG()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        GroundNodeDto spot = OakNode("Spot", PointMenuViewTests.ReachableSpot);
        AircraftModel ac = PointMenuViewTests.ParkedAtStand25();
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
        Assert.Equal([Strip, Separator, taxiChoice.Label, "Push to E", "Custom taxi…", Separator, "Warp here"], Labels(Body(menu)));
        Assert.DoesNotContain(Labels(menu.Items), l => l.StartsWith("Fly heading", StringComparison.Ordinal));

        Click(Item(menu.Items, "Push to E"));
        Click(Item(menu.Items, "Warp here"));
        Assert.Equal([(Callsign, "PUSH $E", "AB"), (Callsign, $"WARPG #{spot.Id}", "AB")], host.Sent);

        Click(Item(menu.Items, "Custom taxi…"));
        HeadlessWindowExtensions.PumpDispatcher();
        TextBox input = FindTextBox(anchor);
        Assert.Equal("TAXI  $E", input.Text);
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
                    new MenuCommandChoice("Plain", "TAXI B", null, []),
                    MenuCommandChoice.Separator,
                    new MenuCommandChoice("Unavailable", null, null, []),
                    new MenuCommandChoice(
                        "Crossings",
                        null,
                        nestedRoute,
                        [
                            new MenuCommandChoice("Cross 28L", "TAXI B CROSS 28L", nestedLeafRoute, []),
                            new MenuCommandChoice("Cross both", "TAXI B CROSS 28L CROSS 15", null, []),
                        ]
                    ),
                ]
            )
        );
        AircraftModel ac = GroundAircraft(new LatLon(37.72, -122.22));

        ContextMenu menu = Build(ac, NodePoint("28R"), host, _ => []);

        Assert.Equal([(Callsign, TaxiNode.Id, (string?)"28R")], host.TaxiChoiceRequests);
        // The route is a title over its variants, a separator line, a disabled row, and the nested choice a title over its own.
        TaxiToRunwayPanel panel = Panel(Item(menu.Items, "Taxi via B"));
        Assert.Equal(
            [
                "Taxi via B · ~0 ft",
                "For Departure 30 — TAXI B 30",
                "Plain — TAXI B",
                "---",
                "Unavailable [disabled]",
                "Crossings · ~0 ft",
                "Cross 28L — TAXI B CROSS 28L",
                "Cross both — TAXI B CROSS 28L CROSS 15",
            ],
            panel.Lines
        );
        Assert.Equal(4, panel.Rows.Count);

        // Each row previews its own route, else the route of the choice it sits under.
        foreach (Button row in panel.Rows)
        {
            RaisePointerEntered(row);
        }

        Assert.Equal([leafRoute, parentRoute, nestedLeafRoute, nestedRoute], host.RoutePreviews.Skip(host.RoutePreviews.Count - 4));

        panel.Rows[2].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal([(Callsign, "TAXI B CROSS 28L", "AB")], host.Sent);
    }

    [AvaloniaFact]
    public void TaxiHere_ZeroChoices_AddsNothing()
    {
        var host = new RecordingMenuHost("");

        ContextMenu menu = Build(GroundAircraft(new LatLon(37.72, -122.22)), NodePoint(null), host, _ => []);

        Assert.Equal([Strip, Separator, "Custom taxi…", Separator, "Warp here"], Labels(Body(menu)));
        Assert.Equal([(Callsign, TaxiNode.Id, (string?)null)], host.TaxiChoiceRequests);
    }

    [AvaloniaFact]
    public void TaxiHere_OneChoice_IsUnwrapped()
    {
        var host = new RecordingMenuHost("");
        host.TaxiChoices.Add(new MenuCommandChoice("Taxi via B", "TAXI B", null, []));

        ContextMenu menu = Build(GroundAircraft(new LatLon(37.72, -122.22)), NodePoint(null), host, _ => []);

        Assert.Equal([Strip, Separator, "Taxi via B", "Custom taxi…", Separator, "Warp here"], Labels(Body(menu)));
        Click(Item(menu.Items, "Taxi via B"));
        Assert.Equal([(Callsign, "TAXI B", "AB")], host.Sent);
    }

    // With the quick command icons off the ground point menu drops its strip; its text rows already carry every strip item.
    [AvaloniaFact]
    public void GroundPoint_IconsOff_DropsTheStrip_KeepsTheTextRows()
    {
        var host = new RecordingMenuHost("");
        host = new RecordingMenuHost("") { Session = host.Session with { ShowQuickCommandIcons = false } };
        host.TaxiChoices.Add(new MenuCommandChoice("Taxi via A", "TAXI A", null, []));
        host.TaxiChoices.Add(new MenuCommandChoice("Taxi via B", "TAXI B", null, []));

        ContextMenu menu = Build(GroundAircraft(new LatLon(37.72, -122.22)), NodePoint(null), host, _ => []);

        Assert.Equal(["Taxi here", "Custom taxi…", Separator, "Warp here"], Labels(Body(menu)));
        Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), QuickCommandStrip.IsStrip);
    }

    [AvaloniaFact]
    public void TaxiHere_SeveralChoices_WrapUnderTaxiHere()
    {
        var host = new RecordingMenuHost("");
        host.TaxiChoices.Add(new MenuCommandChoice("Taxi via A", "TAXI A", null, []));
        host.TaxiChoices.Add(new MenuCommandChoice("Taxi via B", "TAXI B", null, []));

        ContextMenu menu = Build(GroundAircraft(new LatLon(37.72, -122.22)), NodePoint(null), host, _ => []);

        Assert.Equal([Strip, Separator, "Taxi here", "Custom taxi…", Separator, "Warp here"], Labels(Body(menu)));
        Assert.Equal(["Taxi via A — TAXI A", "Taxi via B — TAXI B"], Panel(Item(menu.Items, "Taxi here")).Lines);
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
    public void TaxiHere_NoRouteFound_ShowsTheRowAndAFadedStripIcon()
    {
        var host = new RecordingMenuHost("");
        host.TaxiChoices.Add(new MenuCommandChoice("No route found", null, null, []));

        ContextMenu menu = Build(GroundAircraft(new LatLon(37.72, -122.22)), NodePoint(null), host, _ => []);

        Assert.Equal([Strip, Separator, "No route found", "Custom taxi…", Separator, "Warp here"], Labels(Body(menu)));
        Assert.False(Item(menu.Items, "No route found").IsEnabled);
        Assert.Equal([MenuIds.PointTaxiHere, MenuIds.PointCustomTaxi], StripTags(menu));
        Assert.Equal(MenuGlyphRowTemplate.DimmedOpacity, StripButton(menu, MenuIds.PointTaxiHere).Opacity);
    }

    [AvaloniaFact]
    public void PointStrip_PushToButton_SendsPushToTheSpot()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        GroundNodeDto spot = OakNode("Spot", "1");
        var host = new RecordingMenuHost("");
        var point = new MenuPoint(new LatLon(spot.Latitude, spot.Longitude), spot, null, [], null);

        ContextMenu menu = Build(GroundAircraft(SpotI30()), point, host, _ => []);
        StripButton(menu, MenuIds.PointPushTo).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal([(Callsign, "PUSH $1", "AB")], host.Sent);
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
        Assert.Equal(["Taxi to runway"], labels.Where(l => l.StartsWith("Taxi to ", StringComparison.Ordinal)).ToList());
        TaxiToRunwayPanel panel = Panel(Item(menu.Items, "Taxi to runway"));
        string end1 = RunwayIdentifier.ToDisplayDesignator(ids.End1);
        string end2 = RunwayIdentifier.ToDisplayDesignator(ids.End2);
        Assert.Equal([end1, end2], panel.Strip.Tabs.Select(tab => tab.Label));
        Assert.True(new HashSet<string> { end1, end2 }.SetEquals(["28R", "10L"]), $"Ends offered: {end1}, {end2}");
        Assert.NotEmpty(panel.Rows);
        panel.Strip.Select(1);
        Assert.NotEmpty(panel.Rows);
        Assert.DoesNotContain(labels, l => (l is "Taxi here" or "Custom taxi…") || l.StartsWith("Push to", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void PointStrip_RunwaySurface_OneButton_OpensATabPerEnd()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        GroundRunwayDto runway = OakRunway("28R/10L");
        AircraftModel ac = GroundAircraft(SpotI30());
        using OakHostScope scope = OakHosts(ac);

        ContextMenu menu = Build(ac, SurfacePoint(Midpoint(runway), runway.Name, null), scope.Host, _ => []);
        menu.Open(scope.Window);

        Assert.Equal([MenuIds.PointTaxiToRunway], StripTags(menu));
        Button end = QuickCommandStrip.Buttons(StripOf(menu))[0];
        end.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        MenuFlyout flyout = Assert.IsType<MenuFlyout>(FlyoutBase.GetAttachedFlyout(end));
        Assert.True(flyout.IsOpen);
        TaxiToRunwayPanel panel = Assert.IsType<TaxiToRunwayPanel>(Assert.Single(flyout.Items.OfType<MenuItem>()).Header);
        Assert.Equal(2, panel.Strip.Tabs.Count);
        Assert.NotEmpty(panel.Rows);
        Assert.Empty(scope.Host.Sent);
        menu.Close();
    }

    [AvaloniaFact]
    public void RunwaySurface_EachEnd_OffersNearestNearClickAndFullLength()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        GroundRunwayDto runway = OakRunway("28R/10L");
        var ids = RunwayIdentifier.Parse(runway.Name);
        AircraftModel ac = GroundAircraft(SpotI30());
        using OakHostScope scope = OakHosts(ac);

        LatLon click = Midpoint(runway);
        ContextMenu menu = Build(ac, SurfacePoint(click, runway.Name, null), scope.Host, _ => []);
        TaxiToRunwayPanel panel = Panel(Item(menu.Items, "Taxi to runway"));

        string[] ends = [ids.End1, ids.End2];
        for (int tab = 0; tab < ends.Length; tab++)
        {
            panel.Strip.Select(tab);
            List<string> targets = TargetsIn(panel);
            Assert.All(targets, label => Assert.Matches(TargetLabel, label));
            AssertEachReasonOnce(targets);
            Assert.Equal(scope.Client.GetRunwayHoldShortTargets(Callsign, runway.Name, ends[tab], click).Select(t => t.Label), targets);
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
        AircraftModel ac = GroundAircraft(MenuGoldenFixtures.W3NodeBeforeHoldShort30(domain).Position);
        ac.CurrentPhase = "Taxiing";
        using OakHostScope scope = OakHosts(ac);

        ContextMenu menu = Build(ac, SurfacePoint(holdShort.Position, runway.Name, null), scope.Host, _ => []);
        TaxiToRunwayPanel panel = Panel(Item(menu.Items, "Taxi to runway"));

        Assert.Equal(2, panel.Strip.Tabs.Count);
        for (int tab = 0; tab < panel.Strip.Tabs.Count; tab++)
        {
            panel.Strip.Select(tab);
            List<string> targets = TargetsIn(panel);
            Assert.StartsWith("At W3 (nearest, near click", targets[0]);
            AssertEachReasonOnce(targets);
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
        TaxiToRunwayPanel panel = Panel(Item(menu.Items, "Taxi to runway"));

        string[] ends = [ids.End1, ids.End2];
        string[][] titles =
        [
            [
                "RW At G · near click · via W1 W B A C G · crosses 28L · ~15,900 ft [bold]",
                "RW At G · near click · via W1 W U T B C G · crosses 28L · ~15,200 ft",
                "RW At G · near click · via W1 W B C G · crosses 28L · ~15,300 ft",
                "RW At B · nearest, full length · via W1 W B · crosses 28L · ~12,100 ft [bold]",
                "RW At B · nearest, full length · via W1 W U T B · crosses 28L · ~12,000 ft",
            ],
            [
                "RW At G · near click · via W1 W B A C G · crosses 28L · ~15,900 ft [bold]",
                "RW At G · near click · via W1 W U T B C G · crosses 28L · ~15,200 ft",
                "RW At G · near click · via W1 W B C G · crosses 28L · ~15,300 ft",
                "RW At C1 · full length · via W1 W B A C C1 · crosses 28L, 15 · ~18,700 ft [bold]",
                "RW At C1 · full length · via W1 W U T B C C1 · crosses 28L, 15 · ~18,000 ft",
                "RW At C1 · full length · via W1 W B C C1 · crosses 28L, 15 · ~18,050 ft",
                "RW At B · nearest · via W1 W B · crosses 28L · ~12,100 ft [bold]",
                "RW At B · nearest · via W1 W U T B · crosses 28L · ~12,000 ft",
            ],
        ];
        for (int tab = 0; tab < ends.Length; tab++)
        {
            panel.Strip.Select(tab);
            IReadOnlyList<RunwayHoldShortTarget> targets = scope.Client.GetRunwayHoldShortTargets(Callsign, runway.Name, ends[tab], click);
            Assert.Equal(targets.Select(t => t.Label), TargetsIn(panel));
            // Each route's title as the layout gives it; a route crosses 28L/10R on the way, never its own runway 28R/10L.
            Assert.Equal(titles[tab], panel.Lines.Where(IsTitle));
            // Under the titles, the host's answer for each target, line for line: variant rows and separators.
            string end = ends[tab];
            Assert.Equal(VariantLines(targets, t => scope.Client.GetTaxiChoices(Callsign, t.Node, end)), panel.Lines.Where(line => !IsTitle(line)));
            // Every command the Taxi here choices for each target's node and that end send is a row, in order.
            List<string> commands = [.. targets.SelectMany(t => LeafCommands(scope.Client.GetTaxiChoices(Callsign, t.Node, ends[tab])))];
            Assert.Equal(commands, panel.Rows.Select(MenuCommandText.GetCommand));
        }

        // The first row of the first target sends exactly the first Taxi here command for that node and end.
        panel.Strip.Select(0);
        RunwayHoldShortTarget first = scope.Client.GetRunwayHoldShortTargets(Callsign, runway.Name, ids.End1, click)[0];
        string firstCommand = LeafCommands(scope.Client.GetTaxiChoices(Callsign, first.Node, ids.End1)).First();
        panel.Rows[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal((Callsign, firstCommand, "AB"), scope.Host.Sent[^1]);
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
        TaxiToRunwayPanel panel = Panel(Item(surface.Items, "Taxi to runway"));
        panel.Strip.Select(panel.Strip.Tabs.ToList().FindIndex(tab => tab.Label == "28R"));
        Assert.Equal(["At B (nearest)"], TargetsIn(panel));
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

    /// <summary>
    /// The point menu at a radar point due north of KOAK with an airborne aircraft there whose current heading is
    /// <paramref name="currentOffsetDeg"/> right of (negative: left of) the heading Fly heading sends, and that heading.
    /// </summary>
    private static (ContextMenu Menu, int Heading) FlyHeadingMenu(double currentOffsetDeg)
    {
        var koak = new LatLon(37.7213, -122.2208);
        AircraftModel ac = Airborne(koak);
        var point = new MenuPoint(new LatLon(koak.Lat + 0.15, koak.Lon), null, null, [], null);
        int heading = ExpectedMagneticHeading(ac.Position, point.Position);
        ac.Heading = new MagneticHeading(heading + currentOffsetDeg).ToTrue(MagneticDeclination.GetDeclination(koak));
        return (Build(ac, point, new RecordingMenuHost(""), _ => []), heading);
    }

    private static string FlyLabel(int heading) => $"Fly heading {new MagneticHeading(heading).ToDisplayString()}";

    /// <summary>True north from <paramref name="from"/> as a magnetic bearing at its variation, three digits.</summary>
    private static string NorthBearing(LatLon from) => new TrueHeading(0).ToMagnetic(MagneticDeclination.GetDeclination(from)).ToDisplayString();

    /// <summary>
    /// The point menu's items after its header: the title, the strip's header line when there is a strip, the radar detail
    /// row when there is one, and the separator.
    /// </summary>
    private static List<object?> Body(ContextMenu menu)
    {
        List<object?> items = [.. menu.Items];
        int separator = items.FindIndex(i => i is Avalonia.Controls.Separator);
        Assert.InRange(separator, 1, 3);
        Assert.False(Assert.IsType<MenuItem>(items[0]).IsEnabled);
        return items[(separator + 1)..];
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

    /// <summary>Each of the three reasons, nearest, near click and full length, is named by exactly one target.</summary>
    private static void AssertEachReasonOnce(List<string> targets) =>
        Assert.Equal(new[] { "nearest", "near click", "full length" }.Order(), targets.SelectMany(TagsOf).Order());

    /// <summary>The Taxi to runway panel's targets in order, once each, read from its title lines (<c>RW {target} · …</c>).</summary>
    private static List<string> TargetsIn(TaxiToRunwayPanel panel) =>
        [
            .. panel
                .Lines.Where(line => line.StartsWith("RW ", StringComparison.Ordinal))
                .Select(line => line["RW ".Length..].Replace(" [dim]", "", StringComparison.Ordinal).Split(" · "))
                .Select(parts => ((parts.Length > 1) && IsReason(parts[1])) ? $"{parts[0]} ({parts[1]})" : parts[0])
                .Distinct(),
        ];

    /// <summary>Whether a title's second part names the target's reasons rather than the route's via or distance.</summary>
    private static bool IsReason(string part) => !part.StartsWith("via ", StringComparison.Ordinal) && !part.StartsWith('~');

    /// <summary>Whether a Taxi to runway panel line is a target's title (<c>RW At W · nearest · …</c>).</summary>
    private static bool IsTitle(string line) => line.StartsWith("RW ", StringComparison.Ordinal);

    /// <summary>
    /// The lines the Taxi to runway panel shows under its titles for <paramref name="targets"/>, built from the host's
    /// Taxi here answer for each (<paramref name="choicesFor"/>): per route its variants; a target with no route
    /// <c>No route found</c>.
    /// </summary>
    private static List<string> VariantLines(
        IReadOnlyList<RunwayHoldShortTarget> targets,
        Func<RunwayHoldShortTarget, IReadOnlyList<MenuCommandChoice>> choicesFor
    )
    {
        List<string> lines = [];
        foreach (RunwayHoldShortTarget target in targets)
        {
            IReadOnlyList<MenuCommandChoice> answer = choicesFor(target);
            if ((answer.Count == 1) && (answer[0].Children.Count > 0) && (answer[0].Preview is null))
            {
                answer = answer[0].Children;
            }

            List<MenuCommandChoice> routes =
            [
                .. answer.Where(c => !ReferenceEquals(c, MenuCommandChoice.Separator) && ((c.Command is not null) || (c.Children.Count > 0))),
            ];
            if (routes.Count == 0)
            {
                lines.Add("No route found [disabled]");
                continue;
            }

            foreach (MenuCommandChoice route in routes)
            {
                AddVariantLines(lines, (route.Children.Count > 0) ? route.Children : [route]);
            }
        }

        return lines;
    }

    /// <summary>A runway route's variants as rows: the host answers them one level deep, a separator between the two kinds.</summary>
    private static void AddVariantLines(List<string> lines, IReadOnlyList<MenuCommandChoice> choices)
    {
        foreach (MenuCommandChoice choice in choices)
        {
            Assert.Empty(choice.Children);
            if (ReferenceEquals(choice, MenuCommandChoice.Separator))
            {
                lines.Add("---");
            }
            else
            {
                lines.Add((choice.Command is { } command) ? $"{choice.Label} — {command}" : $"{choice.Label} [disabled]");
            }
        }
    }

    /// <summary>The commands <paramref name="choices"/> send, depth first, in order.</summary>
    private static IEnumerable<string> LeafCommands(IReadOnlyList<MenuCommandChoice> choices)
    {
        foreach (MenuCommandChoice choice in choices)
        {
            if (choice.Children.Count > 0)
            {
                foreach (string command in LeafCommands(choice.Children))
                {
                    yield return command;
                }
            }
            else if (choice.Command is { } command)
            {
                yield return command;
            }
        }
    }

    /// <summary>The panel of a point taxi entry's submenu, opened.</summary>
    private static TaxiToRunwayPanel Panel(MenuItem entry)
    {
        entry.IsSubMenuOpen = true;
        return Assert.IsType<TaxiToRunwayPanel>(Assert.Single(entry.Items.OfType<MenuItem>()).Header);
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
        public required MainViewModel Main { get; init; }
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
            Main = main,
            Client = client,
            Host = new SendCapturingHost(client, "AB"),
            Window = window,
        };
    }

    /// <summary>A recording host answering one target, at <see cref="TaxiNode"/>, for each end of 28R/10L, with one taxi choice there.</summary>
    private static RecordingMenuHost HostWithTargets()
    {
        var host = new RecordingMenuHost("");
        host.TaxiChoices.Add(new MenuCommandChoice("Taxi via B", "TAXI B", NewRoute(), []));
        host.RunwayHoldShortTargets["28R"] = [new RunwayHoldShortTarget(TaxiNode, "At B", ["nearest"])];
        host.RunwayHoldShortTargets["10L"] = [new RunwayHoldShortTarget(TaxiNode, "At B", ["nearest"])];
        return host;
    }

    private static TaxiRoute NewRoute() => new() { Segments = [], HoldShortPoints = [] };

    /// <summary>
    /// Raises a pointer enter on <paramref name="item"/> with a real <see cref="PointerEventArgs"/>, which the hover
    /// handler requires.
    /// </summary>
    private static void RaisePointerEntered(Control item) =>
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
                    MenuItem m when QuickCommandStrip.IsStrip(m) => Strip,
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

    /// <summary>The catalog ids the point menu's strip buttons stand for, in strip order.</summary>
    private static List<string> StripTags(ContextMenu menu) => [.. QuickCommandStrip.Buttons(StripOf(menu)).Select(b => (string)b.Tag!)];

    private static Button StripButton(ContextMenu menu, string id) => QuickCommandStrip.Buttons(StripOf(menu)).Single(b => (string)b.Tag! == id);

    private static MenuItem StripOf(ContextMenu menu) => Assert.Single(menu.Items.OfType<MenuItem>(), QuickCommandStrip.IsStrip);

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
