using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
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
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The ground point menu's strip as designed flyouts: Taxi here opens a flyout of two-line rows (label over the whole
/// command, distance at the right), a lone route sends at once and no route leaves a faded, inert button; Taxi to runway
/// is one button whose flyout has a tab per runway end, each target and its routes a title line over their variants,
/// targets ordered by the click along the runway, a target with no route dimmed over a disabled "No route found". Push
/// to stays a plain sending button and Custom taxi… its seeded input popup. A strip button and its text row open the
/// same builder's flyout; the keyboard follows the Taxi to runway model and the route preview clears on tab change and
/// close.
/// </summary>
public class PointMenuFlyoutTests
{
    private const string Callsign = "AAL202";

    // --- Taxi here ---------------------------------------------------------------------------

    [AvaloniaFact]
    public void TaxiHere_SeveralRoutes_OpensAFlyoutOfTwoLineRows_InTheHostsOrder()
    {
        TaxiRoute routeA = NewRoute();
        var host = new RecordingMenuHost("");
        host.TaxiChoices.Add(new MenuCommandChoice("Taxi via A", "TAXI A #7", routeA, []));
        host.TaxiChoices.Add(new MenuCommandChoice("Taxi via B", "TAXI B #7", null, []));

        MenuFlyout flyout = OpenStripFlyout(host, NodePoint(), MenuIds.PointTaxiHere, out _);
        TaxiToRunwayPanel panel = Panel(flyout);

        Assert.False(panel.Strip.IsVisible);
        Assert.Equal(["Taxi via A — TAXI A #7 · ~0 ft", "Taxi via B — TAXI B #7"], panel.Lines);
        Assert.Equal(["TAXI A #7", "TAXI B #7"], panel.Rows.Select(MenuCommandText.GetCommand));
        Assert.Equal("TAXI A #7", FocusedCommand(flyout));
        Assert.Same(routeA, host.RoutePreviews[^1]);
        Assert.Empty(host.Sent);

        Press(flyout, Key.Down);
        Assert.Equal("TAXI B #7", FocusedCommand(flyout));
        Press(flyout, Key.Down);
        Assert.Equal("TAXI B #7", FocusedCommand(flyout));
        Press(flyout, Key.Up);
        Assert.Equal("TAXI A #7", FocusedCommand(flyout));
        Press(flyout, Key.Up);
        Assert.Equal("TAXI A #7", FocusedCommand(flyout));
        Press(flyout, Key.Tab);
        Assert.Equal("TAXI B #7", FocusedCommand(flyout));
        Press(flyout, Key.Tab, KeyModifiers.Shift);
        Assert.Equal("TAXI A #7", FocusedCommand(flyout));

        panel.Rows[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([(Callsign, "TAXI B #7", "AB")], host.Sent);
        Assert.False(flyout.IsOpen);
        Assert.Null(host.RoutePreviews[^1]);
    }

    /// <summary>On the KOAK layout from gate 25 to the V / W / W4 node (19), the host's routes become one row each, with their distance.</summary>
    [AvaloniaFact]
    public void TaxiHere_OnKoak_EachRouteIsARowWithItsDistance()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel ac = PointMenuViewTests.ParkedAtStand25();
        using OakHostScope scope = OakHosts(ac);
        GroundNodeDto node = MenuGoldenFixtures.OakLayoutForClient.Nodes.First(n => n.Id == 19);
        var point = new MenuPoint(new LatLon(node.Latitude, node.Longitude), node, null, [], null);
        MenuCommandChoice wrapper = Assert.Single(scope.Client.GetTaxiChoices(ac.Callsign, node, null));
        Assert.True(wrapper.Children.Count > 1, "Node 19 answers several routes from gate 25");

        MenuFlyout flyout = OpenStripFlyout(scope.Host, ac, point, MenuIds.PointTaxiHere, out _);

        Assert.Equal(wrapper.Children.Count, Panel(flyout).Lines.Count);
        foreach ((MenuCommandChoice route, string line) in wrapper.Children.Zip(Panel(flyout).Lines))
        {
            Assert.StartsWith($"{route.Label} — {route.Command} · ~", line);
            Assert.DoesNotContain("~0 ft", line);
        }
    }

    [AvaloniaFact]
    public void TaxiHere_RouteWithVariants_IsATitleOverItsVariants_EachPreviewingItsOwnRoute()
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

        MenuFlyout flyout = OpenStripFlyout(host, NodePoint(), MenuIds.PointTaxiHere, out ContextMenu menu);
        TaxiToRunwayPanel panel = Panel(flyout);

        Assert.Equal(
            [
                "Taxi via B · ~0 ft",
                "For Departure 30 — TAXI B 30",
                "---",
                "Unavailable [disabled]",
                "Crossings · ~0 ft",
                "Cross 28L — TAXI B CROSS 28L",
            ],
            panel.Lines
        );
        Assert.Same(leafRoute, host.RoutePreviews[^1]);
        Press(flyout, Key.Down);
        Assert.Equal("TAXI B CROSS 28L", FocusedCommand(flyout));
        Assert.Same(nestedLeafRoute, host.RoutePreviews[^1]);
        Assert.Contains(menu.Items.OfType<MenuItem>(), m => (m.Header as string) == "Taxi via B");

        panel.Rows[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([(Callsign, "TAXI B CROSS 28L", "AB")], host.Sent);
    }

    /// <summary>A route that crosses runways says so on its title, as the mock draws it; a route with no crossing says nothing.</summary>
    [AvaloniaFact]
    public void TaxiHere_RouteThatCrossesRunways_TitleSaysWhatItCrosses()
    {
        var host = new RecordingMenuHost("");
        host.TaxiChoices.Add(
            new MenuCommandChoice(
                "Taxi here",
                null,
                null,
                [
                    new MenuCommandChoice(
                        "Taxi T S C",
                        null,
                        CrossingRoute("28L/10R", "15/33"),
                        [
                            new MenuCommandChoice("HS 28L", "TAXI T S C #7 HS 28L", null, []),
                            new MenuCommandChoice("CROSS 28L 15", "TAXI T S C #7, CROSS 28L, CROSS 15", null, []),
                        ]
                    ),
                    new MenuCommandChoice("Taxi T V", null, NewRoute(), [new MenuCommandChoice("Via V", "TAXI T V #7", null, [])]),
                ]
            )
        );

        TaxiToRunwayPanel panel = Panel(OpenStripFlyout(host, NodePoint(), MenuIds.PointTaxiHere, out _));

        Assert.Equal(
            [
                "Taxi T S C · crosses 28L, 15 · ~0 ft",
                "HS 28L — TAXI T S C #7 HS 28L",
                "CROSS 28L 15 — TAXI T S C #7, CROSS 28L, CROSS 15",
                "Taxi T V · ~0 ft",
                "Via V — TAXI T V #7",
            ],
            panel.Lines
        );
    }

    [AvaloniaFact]
    public void TaxiHere_OneRoute_TheButtonSendsAtOnce()
    {
        var host = new RecordingMenuHost("");
        host.TaxiChoices.Add(new MenuCommandChoice("Taxi via B", "TAXI B", null, []));
        ContextMenu menu = OpenMenu(host, GroundAircraft(), NodePoint());

        Button button = StripButton(menu, MenuIds.PointTaxiHere);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Null(FlyoutBase.GetAttachedFlyout(button));
        Assert.Equal([(Callsign, "TAXI B", "AB")], host.Sent);
    }

    [AvaloniaFact]
    public void TaxiHere_NoRoute_IsAFadedInertButtonNamingTheReason()
    {
        var host = new RecordingMenuHost("");
        host.TaxiChoices.Add(new MenuCommandChoice("No route found", null, null, []));
        ContextMenu menu = OpenMenu(host, GroundAircraft(), NodePoint());

        Button button = StripButton(menu, MenuIds.PointTaxiHere);
        Assert.Equal(MenuGlyphRowTemplate.DimmedOpacity, button.Opacity);
        RaisePointer(button, InputElement.PointerEnteredEvent);
        Assert.Equal("Taxi here — No route found", QuickCommandLabelLine.In(menu)!.Label);

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Null(FlyoutBase.GetAttachedFlyout(button));
        Assert.Empty(host.Sent);
        MenuItem row = Assert.Single(menu.Items.OfType<MenuItem>(), m => (m.Header as string) == "No route found");
        Assert.False(row.IsEnabled);
        menu.Close();
    }

    [AvaloniaFact]
    public void TaxiHere_TextRow_OpensTheSameFlyout()
    {
        var host = new RecordingMenuHost("");
        host.TaxiChoices.Add(new MenuCommandChoice("Taxi via A", "TAXI A", null, []));
        host.TaxiChoices.Add(new MenuCommandChoice("Taxi via B", "TAXI B", null, []));
        MenuFlyout flyout = OpenStripFlyout(host, NodePoint(), MenuIds.PointTaxiHere, out ContextMenu menu);
        IReadOnlyList<string> stripLines = Panel(flyout).Lines;
        flyout.Hide();

        MenuItem text = Assert.Single(menu.Items.OfType<MenuItem>(), m => (m.Header as string) == "Taxi here");
        text.IsSubMenuOpen = true;

        Assert.Equal(stripLines, Panel(text).Lines);
        Assert.Equal(["Taxi via A — TAXI A", "Taxi via B — TAXI B"], Panel(text).Lines);
        menu.Close();
    }

    // --- Taxi to runway at a point -----------------------------------------------------------

    [AvaloniaFact]
    public void TaxiToRunway_OneButton_ATabPerEnd_TargetTitlesOverTheirVariants()
    {
        RecordingMenuHost host = HostWithTargets(RouteToRunway("B"));
        var ids = RunwayIdentifier.Parse("28R/10L");

        MenuFlyout flyout = OpenStripFlyout(host, GroundAircraft(), SurfacePoint(), MenuIds.PointTaxiToRunway, out ContextMenu menu);
        TaxiToRunwayPanel panel = Panel(flyout);

        Assert.Single(StripTags(menu), MenuIds.PointTaxiToRunway);
        Assert.Equal(
            [RunwayIdentifier.ToDisplayDesignator(ids.End1), RunwayIdentifier.ToDisplayDesignator(ids.End2)],
            panel.Strip.Tabs.Select(tab => tab.Label)
        );
        Assert.Equal(["RW At B · nearest · via B · ~0 ft [bold]", "For Departure — TAXI B RWY", "---", "Hold short — TAXI B HS RWY"], panel.Lines);
        Assert.Contains(menu.Items.OfType<MenuItem>(), m => (m.Header as string) == "Taxi to runway");
        Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), m => (m.Header as string)?.StartsWith("Taxi to 2", StringComparison.Ordinal) == true);

        panel.Rows[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([(Callsign, "TAXI B HS RWY", "AB")], host.Sent);
    }

    [AvaloniaFact]
    public void TaxiToRunway_TargetWithNoRoute_IsADimmedTitleOverADisabledNoRouteFound()
    {
        RecordingMenuHost host = HostWithTargets(new MenuCommandChoice("No route found", null, null, []));

        MenuFlyout flyout = OpenStripFlyout(host, GroundAircraft(), SurfacePoint(), MenuIds.PointTaxiToRunway, out _);
        TaxiToRunwayPanel panel = Panel(flyout);

        Assert.Equal(["RW At B · nearest [dim]", "No route found [disabled]"], panel.Lines);
        Assert.Empty(panel.Rows);
        Assert.All(panel.Strip.Tabs, tab => Assert.Equal(FlyoutTabState.Empty, tab.State));
    }

    /// <summary>
    /// The host's group of several routes to a target is unwrapped: one RW title per route, the first bold, each with its
    /// own via, crossings and distance, over its own variants.
    /// </summary>
    [AvaloniaFact]
    public void TaxiToRunway_TargetWithSeveralRoutes_IsATitlePerRoute()
    {
        MenuCommandChoice viaC = RouteToRunway("C") with { Preview = CrossingRoute("28L/10R") };
        RecordingMenuHost host = HostWithTargets(new MenuCommandChoice("Taxi here", null, null, [RouteToRunway("B"), viaC]));

        TaxiToRunwayPanel panel = Panel(OpenStripFlyout(host, GroundAircraft(), SurfacePoint(), MenuIds.PointTaxiToRunway, out _));

        Assert.Equal(
            [
                "RW At B · nearest · via B · ~0 ft [bold]",
                "For Departure — TAXI B RWY",
                "---",
                "Hold short — TAXI B HS RWY",
                "RW At B · nearest · via C · crosses 28L · ~0 ft",
                "For Departure — TAXI C RWY",
                "---",
                "Hold short — TAXI C HS RWY",
            ],
            panel.Lines
        );
    }

    [AvaloniaFact]
    public void TaxiToRunway_Keyboard_AndThePreviewClearingOnTabChangeAndClose()
    {
        RecordingMenuHost host = HostWithTargets(RouteToRunway("B"));
        var ids = RunwayIdentifier.Parse("28R/10L");

        MenuFlyout flyout = OpenStripFlyout(host, GroundAircraft(), SurfacePoint(), MenuIds.PointTaxiToRunway, out ContextMenu menu);
        TaxiToRunwayPanel panel = Panel(flyout);

        Assert.Equal("TAXI B RWY", FocusedCommand(flyout));
        Press(flyout, Key.Down);
        Assert.Equal("TAXI B HS RWY", FocusedCommand(flyout));
        Press(flyout, Key.Left);
        Assert.Equal(0, panel.Strip.SelectedIndex);

        int before = host.RoutePreviews.Count;
        Press(flyout, Key.Right);
        Assert.Equal(1, panel.Strip.SelectedIndex);
        Assert.Equal(RunwayIdentifier.ToDisplayDesignator(ids.End2), panel.Strip.Tabs[panel.Strip.SelectedIndex].Label);
        Assert.Null(host.RoutePreviews[before]);
        Assert.Equal("TAXI B RWY", FocusedCommand(flyout));

        Press(flyout, Key.Escape);
        Assert.False(flyout.IsOpen);
        Assert.True(menu.IsOpen);
        Assert.True(Assert.Single(menu.Items.OfType<MenuItem>(), QuickCommandStrip.IsStrip).IsKeyboardFocusWithin, "Focus is not back on the strip.");
        Assert.Null(host.RoutePreviews[^1]);
        Assert.Empty(host.Sent);
        menu.Close();
    }

    /// <summary>
    /// On KOAK runway 30/12, the aircraft just behind the W3 hold short of 30, clicked at the hold short nearest the
    /// runway's middle: the target near the click comes first, though the aircraft is nearer another.
    /// </summary>
    [AvaloniaFact]
    public void TaxiToRunway_OnKoak_TargetsRunFromTheClickAlongTheRunway()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AirportGroundLayout domain = MenuGoldenFixtures.OakDomainLayout;
        GroundNode w3 = MenuGoldenFixtures.HoldShort30AtW3(domain);
        GroundRunwayDto runway = Assert.Single(
            MenuGoldenFixtures.OakLayoutForClient.Runways!,
            r => RunwayIdentifier.Parse(r.Name) == RunwayIdentifier.Parse(w3.RunwayId!.Value.ToString())
        );
        var middle = new LatLon(
            (runway.Coordinates[0][0] + runway.Coordinates[^1][0]) / 2.0,
            (runway.Coordinates[0][1] + runway.Coordinates[^1][1]) / 2.0
        );
        GroundNodeDto nearMiddle = MenuGoldenFixtures
            .OakLayoutForClient.Nodes.Where(n =>
                (n.Type == "RunwayHoldShort") && (n.RunwayId is { } id) && (RunwayIdentifier.Parse(id) == RunwayIdentifier.Parse(runway.Name))
            )
            .MinBy(n => GeoMath.DistanceNm(middle.Lat, middle.Lon, n.Latitude, n.Longitude))!;
        var click = new LatLon(nearMiddle.Latitude, nearMiddle.Longitude);
        AircraftModel ac = GroundAircraft(MenuGoldenFixtures.W3NodeBeforeHoldShort30(domain).Position);
        ac.CurrentPhase = "Taxiing";
        using OakHostScope scope = OakHosts(ac);
        string end = RunwayIdentifier.Parse(runway.Name).End1;

        IReadOnlyList<RunwayHoldShortTarget> targets = scope.Client.GetRunwayHoldShortTargets(Callsign, runway.Name, end, click);

        Assert.Contains("near click", targets[0].Label);
        Assert.DoesNotContain("nearest", targets[0].Label);
        Assert.Contains(targets, t => t.Label.Contains("nearest", StringComparison.Ordinal));

        var surface = new MenuPoint(click, null, null, [runway.Name], null);
        TaxiToRunwayPanel panel = Panel(OpenStripFlyout(scope.Host, ac, surface, MenuIds.PointTaxiToRunway, out _));
        Assert.StartsWith($"RW {targets[0].Name} · {string.Join(", ", targets[0].Reasons)} · via ", panel.Lines[0]);
        List<string> titles = [.. panel.Lines.Where(line => line.StartsWith("RW ", StringComparison.Ordinal))];
        Assert.NotEmpty(titles);
        Assert.All(titles, title => Assert.DoesNotContain("via here", title));
        Assert.All(titles, title => Assert.DoesNotContain("~0 ft", title));
    }

    /// <summary>
    /// On KOAK runway 30/12, a click on the centerline halfway between two targets: every target runs by its along-runway
    /// distance from the click, ties nearer the threshold first.
    /// </summary>
    [AvaloniaFact]
    public void TaxiToRunway_OnKoak_AClickBetweenTwoTargets_OrdersEveryTarget()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AirportGroundLayout domain = MenuGoldenFixtures.OakDomainLayout;
        GroundNode w3 = MenuGoldenFixtures.HoldShort30AtW3(domain);
        string runwayName = Assert
            .Single(
                MenuGoldenFixtures.OakLayoutForClient.Runways!,
                r => RunwayIdentifier.Parse(r.Name) == RunwayIdentifier.Parse(w3.RunwayId!.Value.ToString())
            )
            .Name;
        string end = RunwayIdentifier.Parse(runwayName).End1;
        AircraftModel ac = GroundAircraft(MenuGoldenFixtures.W3NodeBeforeHoldShort30(domain).Position);
        ac.CurrentPhase = "Taxiing";
        using OakHostScope scope = OakHosts(ac);
        (LatLon threshold, TrueHeading heading) = scope.Main.Ground.RunwayEndThreshold(runwayName, end)!.Value;
        double AlongNm(LatLon p) => GeoMath.AlongTrackDistanceNm(p.Lat, p.Lon, threshold.Lat, threshold.Lon, heading);

        // The targets a click at the W3 hold short names; the click then moves halfway along the runway to the farthest one.
        IReadOnlyList<RunwayHoldShortTarget> first = scope.Client.GetRunwayHoldShortTargets(Callsign, runwayName, end, w3.Position);
        Assert.True(first.Count >= 2, "Runway 30/12 offers at least two targets from W3.");
        RunwayHoldShortTarget far = first.MaxBy(t => Math.Abs(AlongNm(NodePosition(t)) - AlongNm(w3.Position)))!;
        double halfwayNm = (AlongNm(w3.Position) + AlongNm(NodePosition(far))) / 2.0;
        LatLon click = GeoMath.ProjectPoint(threshold, heading, halfwayNm);

        IReadOnlyList<RunwayHoldShortTarget> targets = scope.Client.GetRunwayHoldShortTargets(Callsign, runwayName, end, click);

        double clickAlong = AlongNm(click);
        List<string> expected =
        [
            .. targets
                .OrderBy(t => Math.Abs(AlongNm(NodePosition(t)) - clickAlong))
                .ThenBy(t => Math.Abs(AlongNm(NodePosition(t))))
                .Select(t => t.Label),
        ];
        Assert.True(targets.Count >= 2, "A click between two targets names at least two.");
        Assert.Equal(expected, targets.Select(t => t.Label));
    }

    /// <summary>
    /// On KOAK from gate 25, clicked on runway 30/12 at its W3 hold short: no title names the destination runway as
    /// crossed, though each route ends at a bar of it.
    /// </summary>
    [AvaloniaFact]
    public void TaxiToRunway_OnKoak_FromAGate_TheDestinationRunwayIsNotACrossing()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        GroundNode w3 = MenuGoldenFixtures.HoldShort30AtW3(MenuGoldenFixtures.OakDomainLayout);
        GroundRunwayDto runway = Assert.Single(
            MenuGoldenFixtures.OakLayoutForClient.Runways!,
            r => RunwayIdentifier.Parse(r.Name) == RunwayIdentifier.Parse(w3.RunwayId!.Value.ToString())
        );
        AircraftModel ac = PointMenuViewTests.ParkedAtStand25();
        using OakHostScope scope = OakHosts(ac);

        var surface = new MenuPoint(w3.Position, null, null, [runway.Name], null);
        TaxiToRunwayPanel panel = Panel(OpenStripFlyout(scope.Host, ac, surface, MenuIds.PointTaxiToRunway, out _));

        Assert.Equal(
            ["RW At W3 · nearest, near click · via TE U W3 · ~3,000 ft [bold]", "RW At W1 · full length · via TE U W W1 · ~4,650 ft [bold]"],
            panel.Lines.Where(line => line.StartsWith("RW ", StringComparison.Ordinal))
        );
    }

    /// <summary>
    /// On KOAK from gate 25, Taxi here at runway 30/12's W3 hold short: the route ends at the bar of 30, and no title
    /// names 30 as crossed.
    /// </summary>
    [AvaloniaFact]
    public void TaxiHere_OnKoak_AtARunwayHoldShort_TheTitlesNameNoFalseCrossing()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        GroundNode w3 = MenuGoldenFixtures.HoldShort30AtW3(MenuGoldenFixtures.OakDomainLayout);
        GroundNodeDto node = MenuGoldenFixtures.OakLayoutForClient.Nodes.Single(n => n.Id == w3.Id);
        AircraftModel ac = PointMenuViewTests.ParkedAtStand25();
        using OakHostScope scope = OakHosts(ac);

        var point = new MenuPoint(w3.Position, node, null, [], null);
        TaxiToRunwayPanel panel = Panel(OpenStripFlyout(scope.Host, ac, point, MenuIds.PointTaxiHere, out _));

        Assert.Equal(["Taxi via TE U W3 · ~3,000 ft"], panel.Lines.Where(line => !line.Contains(" — ", StringComparison.Ordinal) && (line != "---")));
    }

    // --- Push to and Custom taxi… ------------------------------------------------------------

    [AvaloniaFact]
    public void PushTo_StaysAPlainSendingButton()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        GroundNodeDto spot = MenuGoldenFixtures.OakLayoutForClient.Nodes.First(n => (n.Type == "Spot") && (n.Name == "1"));
        var host = new RecordingMenuHost("");
        ContextMenu menu = OpenMenu(host, GroundAircraft(), new MenuPoint(new LatLon(spot.Latitude, spot.Longitude), spot, null, [], null));

        Button button = StripButton(menu, MenuIds.PointPushTo);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Null(FlyoutBase.GetAttachedFlyout(button));
        Assert.Equal([(Callsign, "PUSH $1", "AB")], host.Sent);
    }

    [AvaloniaFact]
    public void CustomTaxi_OpensItsSeededInputPopup()
    {
        var host = new RecordingMenuHost("") { CustomTaxiSeed = new MenuTextSeed("TAXI  E", 5), InputAnswer = " TAXI B E " };
        ContextMenu menu = OpenMenu(host, GroundAircraft(), NodePoint());

        Button button = StripButton(menu, MenuIds.PointCustomTaxi);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Null(FlyoutBase.GetAttachedFlyout(button));
        Assert.Equal(["Taxi command"], host.InputPlaceholders);
        Assert.Equal([("TAXI  E", 5)], host.InputSeeds);
        Assert.Equal([(Callsign, "TAXI B E", "AB")], host.Sent);
    }

    // --- Helpers ----------------------------------------------------------------------------

    /// <summary>An unnamed taxiway intersection; the recording host answers its choices for every node.</summary>
    private static GroundNodeDto TaxiNode { get; } = new(7, 37.7210, -122.2200, "TaxiwayIntersection", null, null, null);

    private static MenuPoint NodePoint() => new(new LatLon(TaxiNode.Latitude, TaxiNode.Longitude), TaxiNode, null, [], null);

    private static MenuPoint SurfacePoint() => new(new LatLon(37.7250, -122.2000), null, null, ["28R/10L"], null);

    /// <summary>
    /// A recording host answering one target, at <see cref="TaxiNode"/>, for each end of 28R/10L, with
    /// <paramref name="choice"/> there.
    /// </summary>
    private static RecordingMenuHost HostWithTargets(MenuCommandChoice choice)
    {
        var host = new RecordingMenuHost("");
        host.TaxiChoices.Add(choice);
        host.RunwayHoldShortTargets["28R"] = [new RunwayHoldShortTarget(TaxiNode, "At B", ["nearest"])];
        host.RunwayHoldShortTargets["10L"] = [new RunwayHoldShortTarget(TaxiNode, "At B", ["nearest"])];
        return host;
    }

    /// <summary>A route via <paramref name="taxiway"/> to a runway, as the host answers it: a departure and a hold-short variant.</summary>
    private static MenuCommandChoice RouteToRunway(string taxiway) =>
        new(
            $"Taxi via {taxiway}",
            null,
            NewRoute(),
            [
                new MenuCommandChoice("For Departure", $"TAXI {taxiway} RWY", NewRoute(), []),
                MenuCommandChoice.Separator,
                new MenuCommandChoice("Hold short", $"TAXI {taxiway} HS RWY", NewRoute(), []),
            ]
        );

    private static TaxiRoute NewRoute() => new() { Segments = [], HoldShortPoints = [] };

    /// <summary>An empty route holding short to cross each of <paramref name="runways"/>, in order.</summary>
    private static TaxiRoute CrossingRoute(params string[] runways) =>
        new()
        {
            Segments = [],
            HoldShortPoints =
            [
                .. runways.Select(
                    (runway, index) =>
                        new HoldShortPoint
                        {
                            NodeId = 100 + index,
                            Reason = HoldShortReason.RunwayCrossing,
                            TargetName = runway,
                        }
                ),
            ],
        };

    private static LatLon NodePosition(RunwayHoldShortTarget target) => new(target.Node.Latitude, target.Node.Longitude);

    private static AircraftModel GroundAircraft() => GroundAircraft(new LatLon(37.72, -122.22));

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

    /// <summary>The real client host over the KOAK layout with <paramref name="ac"/> selected, and a send-capturing host over it.</summary>
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

    private static ContextMenu OpenMenu(IMenuHost host, AircraftModel ac, MenuPoint point)
    {
        var window = new Window { Width = 900, Height = 900 };
        window.Show();
        ContextMenu menu = AircraftMenuBuilder.Build(ac, new MenuClick(ac.Callsign, null, point, []), host, _ => []);
        menu.Open(window);
        Dispatcher.UIThread.RunJobs();
        menu.UpdateLayout();
        return menu;
    }

    private static MenuFlyout OpenStripFlyout(RecordingMenuHost host, MenuPoint point, string id, out ContextMenu menu) =>
        OpenStripFlyout(host, GroundAircraft(), point, id, out menu);

    private static MenuFlyout OpenStripFlyout(IMenuHost host, AircraftModel ac, MenuPoint point, string id, out ContextMenu menu)
    {
        menu = OpenMenu(host, ac, point);
        Button button = StripButton(menu, id);
        QuickCommandStrip.OpenSubmenu(button);
        Dispatcher.UIThread.RunJobs();
        MenuFlyout flyout = Assert.IsType<MenuFlyout>(FlyoutBase.GetAttachedFlyout(button));
        Assert.True(flyout.IsOpen);
        Layout(flyout);
        return flyout;
    }

    private static List<string> StripTags(ContextMenu menu) =>
        [.. QuickCommandStrip.Buttons(Assert.Single(menu.Items.OfType<MenuItem>(), QuickCommandStrip.IsStrip)).Select(b => (string)b.Tag!)];

    private static Button StripButton(ContextMenu menu, string id) =>
        QuickCommandStrip.Buttons(Assert.Single(menu.Items.OfType<MenuItem>(), QuickCommandStrip.IsStrip)).Single(b => (string)b.Tag! == id);

    private static TaxiToRunwayPanel Panel(MenuFlyout flyout) =>
        Assert.IsType<TaxiToRunwayPanel>(Assert.Single(flyout.Items.OfType<MenuItem>()).Header);

    private static TaxiToRunwayPanel Panel(MenuItem entry) => Assert.IsType<TaxiToRunwayPanel>(Assert.Single(entry.Items.OfType<MenuItem>()).Header);

    private static void Layout(MenuFlyout flyout)
    {
        (flyout.Popup.Child as Control)?.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        (flyout.Popup.Child as Control)?.UpdateLayout();
    }

    /// <summary>Presses <paramref name="key"/> on whatever holds focus in the flyout.</summary>
    private static void Press(MenuFlyout flyout, Key key) => Press(flyout, key, KeyModifiers.None);

    /// <summary>Presses <paramref name="key"/> with <paramref name="modifiers"/> on whatever holds focus in the flyout.</summary>
    private static void Press(MenuFlyout flyout, Key key, KeyModifiers modifiers)
    {
        Control focused = Assert.IsAssignableFrom<Control>(Focused(flyout));
        focused.RaiseEvent(
            new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = key,
                KeyModifiers = modifiers,
            }
        );
        Layout(flyout);
    }

    private static IInputElement? Focused(MenuFlyout flyout) =>
        TopLevel.GetTopLevel(Assert.IsAssignableFrom<Avalonia.Visual>(flyout.Popup.Child))?.FocusManager?.GetFocusedElement();

    private static string? FocusedCommand(MenuFlyout flyout) =>
        ((Focused(flyout) as Avalonia.Visual)?.GetSelfAndVisualAncestors().OfType<Button>().FirstOrDefault() is { } row)
            ? MenuCommandText.GetCommand(row)
            : null;

    private static void RaisePointer(Control target, RoutedEvent routedEvent) =>
        target.RaiseEvent(
            new PointerEventArgs(
                routedEvent,
                target,
                new Pointer(0, PointerType.Mouse, isPrimary: true),
                rootVisual: null,
                rootVisualPosition: default,
                timestamp: 0,
                properties: default,
                modifiers: KeyModifiers.None
            )
        );
}
