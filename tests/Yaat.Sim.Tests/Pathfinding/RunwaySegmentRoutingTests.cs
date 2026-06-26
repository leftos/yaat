using Xunit;
using Xunit.Abstractions;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pathfinding;

/// <summary>
/// A runway may appear as a traversable segment of an explicit taxi clearance — the aircraft taxis
/// ALONG the runway centerline, then turns off onto a taxiway (e.g. <c>TAXI 28R G D</c>). The clearance
/// authorizes the named runway, so the route flows straight onto it with no hold-short at its entry; a
/// DIFFERENT runway the path crosses still holds short. Bug: <c>TAXI 28R G D @NEW1</c> at OAK failed with
/// "Cannot find taxiway 28R in layout" because explicit pathfinding only matched taxiway-name edges, never
/// the runway centerline edges (named <c>RWY28R/10L</c>).
/// </summary>
public class RunwaySegmentRoutingTests
{
    private readonly ITestOutputHelper _output;

    public RunwaySegmentRoutingTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    private AirportGroundLayout? OakLayout()
    {
        var layout = new TestAirportGroundData(FilletMode.Standard).GetLayout("OAK");
        if (layout is null || TestVnasData.NavigationDb is null)
        {
            _output.WriteLine("oak layout / navdata unavailable — skipping");
            return null;
        }

        return layout;
    }

    /// <summary>Node on the 28R centerline nearest a lat/lon (geometry-keyed so it survives node-id churn).</summary>
    private static GroundNode NearestCenterlineNode(AirportGroundLayout layout, double lat, double lon) =>
        layout
            .Nodes.Values.Where(n => n.Edges.Any(e => e.IsRunwayCenterline && e.MatchesRunway("28R")))
            .OrderBy(n => GeoMath.DistanceNm(lat, lon, n.Position.Lat, n.Position.Lon))
            .First();

    private static GroundNode NearestNodeOnTaxiway(AirportGroundLayout layout, string taxiway, double lat, double lon) =>
        layout
            .Nodes.Values.Where(n => n.Edges.Any(e => e.MatchesTaxiway(taxiway)))
            .OrderBy(n => GeoMath.DistanceNm(lat, lon, n.Position.Lat, n.Position.Lon))
            .First();

    private void DumpRoute(TaxiRoute route)
    {
        _output.WriteLine($"Route: {route.Segments.Count} segments");
        foreach (var seg in route.Segments)
        {
            _output.WriteLine($"  {seg.TaxiwayName, -14} #{seg.FromNodeId} -> #{seg.ToNodeId}");
        }

        foreach (var hs in route.HoldShortPoints)
        {
            _output.WriteLine($"  HS {hs.Reason} {hs.TargetName} @#{hs.NodeId}");
        }
    }

    [Fact]
    public void Oak_Taxi28R_G_D_RoutesAlongCenterlineThenGThenD()
    {
        var layout = OakLayout();
        if (layout is null)
        {
            return;
        }

        // Start at the 28R landing-threshold (east) end of the centerline.
        var start = NearestCenterlineNode(layout, 37.724848, -122.204848);
        _output.WriteLine($"start node = {start.Id}");

        var route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            start.Id,
            ["28R", "G", "D"],
            out string? failReason,
            new ExplicitPathOptions { AirportId = "OAK", DiagnosticLog = msg => _output.WriteLine(msg) },
            AircraftCategory.Piston
        );

        Assert.NotNull(route);
        Assert.Null(failReason);
        DumpRoute(route);

        // The centerline of 28R is traversed.
        Assert.Contains(route.Segments, s => s.Edge.Edge.IsRunwayCenterline && s.Edge.Edge.MatchesRunway("28R"));

        // Segments appear in clearance order: runway 28R, then G, then D.
        int rwyIdx = route.Segments.FindIndex(s => s.Edge.Edge.IsRunwayCenterline && s.Edge.Edge.MatchesRunway("28R"));
        int gIdx = route.Segments.FindIndex(s => s.TaxiwayName == "G");
        int dIdx = route.Segments.FindIndex(s => s.TaxiwayName == "D");
        Assert.True(rwyIdx >= 0 && gIdx > rwyIdx && dIdx > gIdx, $"expected 28R(centerline)#{rwyIdx} < G#{gIdx} < D#{dIdx} in segment order");

        // The cleared runway is taxied straight onto — no hold-short at its entry.
        Assert.DoesNotContain(route.HoldShortPoints, h => (h.TargetName ?? "").Contains("28R", StringComparison.Ordinal));
    }

    [Fact]
    public void Oak_Taxi28R_NonIntersectingTaxiway_FailsCleanly()
    {
        var layout = OakLayout();
        if (layout is null)
        {
            return;
        }

        // W is on the west/north field and never touches runway 28R, so "28R W" cannot resolve.
        var start = NearestCenterlineNode(layout, 37.724848, -122.204848);

        var route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            start.Id,
            ["28R", "W"],
            out string? failReason,
            new ExplicitPathOptions { AirportId = "OAK", DiagnosticLog = msg => _output.WriteLine(msg) },
            AircraftCategory.Piston
        );

        Assert.Null(route);
        Assert.NotNull(failReason);
        _output.WriteLine($"failReason = {failReason}");
        // A clear "doesn't intersect" message — not a silent detour and not the old "Cannot find taxiway 28R".
        Assert.Contains("28R", failReason, StringComparison.Ordinal);
        Assert.Contains("intersect", failReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Oak_TaxiRunwayFirstToken_BridgesOntoCenterlineFromTaxiway()
    {
        var layout = OakLayout();
        if (layout is null)
        {
            return;
        }

        // Start on taxiway B just south of the 28R hold-short, cleared "28R G D" — the bridge must get
        // the aircraft from B onto the 28R centerline before the centerline walk begins.
        var start = NearestNodeOnTaxiway(layout, "B", 37.723668, -122.205446);
        _output.WriteLine($"start node = {start.Id}");

        var route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            start.Id,
            ["28R", "G", "D"],
            out string? failReason,
            new ExplicitPathOptions { AirportId = "OAK", DiagnosticLog = msg => _output.WriteLine(msg) },
            AircraftCategory.Piston
        );

        Assert.NotNull(route);
        Assert.Null(failReason);
        DumpRoute(route);

        Assert.Contains(route.Segments, s => s.Edge.Edge.IsRunwayCenterline && s.Edge.Edge.MatchesRunway("28R"));
        Assert.DoesNotContain(route.HoldShortPoints, h => (h.TargetName ?? "").Contains("28R", StringComparison.Ordinal));
    }
}
