using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.Tests;

/// <summary>
/// The ground context menu's "Hold short of..." entries for a route that meets the same target on
/// more than one route taxiway. One bare `HS X` can only bind the first crossing, so the menu emits
/// located entries (<c>X@A</c>, <c>X@B</c>) — one per crossing — and the hover preview for a located
/// target ends at that crossing, using the same node-incidence rule the server binds with.
///
/// Synthetic graph: route A (n0→n1→n2), B (n2→n3→n4), then C; taxiway X crosses A at n1 and B at n3.
/// </summary>
public class GroundViewModelHoldShortMenuLocatedTests
{
    private static GroundNode Node(int id, double lat, double lon) =>
        new()
        {
            Id = id,
            Position = new LatLon(lat, lon),
            Type = GroundNodeType.TaxiwayIntersection,
        };

    private static void Edge(AirportGroundLayout layout, GroundNode a, GroundNode b, string twy)
    {
        layout.Edges.Add(
            new GroundEdge
            {
                Nodes = [a, b],
                TaxiwayName = twy,
                DistanceNm = GeoMath.DistanceNm(a.Position, b.Position),
            }
        );
    }

    private static (GroundViewModel Vm, AircraftModel Ac, AirportGroundLayout Layout) MakeDoubleCrossingFixture()
    {
        var n0 = Node(0, 37.700, -122.200);
        var n1 = Node(1, 37.702, -122.200);
        var n2 = Node(2, 37.704, -122.200);
        var n3 = Node(3, 37.704, -122.203);
        var n4 = Node(4, 37.704, -122.206);
        var x1 = Node(5, 37.702, -122.198);
        var x2 = Node(6, 37.706, -122.203);
        var c1 = Node(7, 37.702, -122.206);

        var layout = new AirportGroundLayout { AirportId = "TEST" };
        foreach (var n in new[] { n0, n1, n2, n3, n4, x1, x2, c1 })
        {
            layout.Nodes[n.Id] = n;
        }

        Edge(layout, n0, n1, "A");
        Edge(layout, n1, n2, "A");
        Edge(layout, n2, n3, "B");
        Edge(layout, n3, n4, "B");
        Edge(layout, n4, c1, "C");
        Edge(layout, n1, x1, "X");
        Edge(layout, n3, x2, "X");
        layout.RebuildAdjacencyLists();

        var connection = new ServerConnection();
        var vm = new GroundViewModel(connection, sendCommand: (_, _, _) => Task.CompletedTask);
        vm.SetDomainLayoutForTesting(layout);

        var ac = new AircraftModel
        {
            Callsign = "N358HS",
            Position = n0.Position,
            CurrentTaxiway = "A",
            TaxiRoute = "A B C",
            AssignedRunway = "",
        };
        return (vm, ac, layout);
    }

    [Fact]
    public void Fixture_RouteReconstructionResolves()
    {
        var (vm, ac, layout) = MakeDoubleCrossingFixture();
        var direct = Yaat.Sim.Data.Airport.TaxiPathfinder.ResolveExplicitPath(
            layout,
            0,
            ["A", "B", "C"],
            out string? failReason,
            new ExplicitPathOptions(),
            AircraftCategory.Jet
        );
        Assert.True(direct is not null, $"direct resolve failed: {failReason}");

        var route = vm.ResolveRemainingRoute(ac);
        Assert.NotNull(route);
        Assert.NotEmpty(route.Segments);
    }

    [Fact]
    public void DoubleCrossedTaxiway_OffersOneLocatedEntryPerCrossing()
    {
        var (vm, ac, _) = MakeDoubleCrossingFixture();

        var targets = vm.GetHoldShortTargets(ac);

        Assert.Contains(targets, t => t.Target == "X@A");
        Assert.Contains(targets, t => t.Target == "X@B");
        Assert.DoesNotContain(targets, t => t.Target == "X");

        var atA = targets.Single(t => t.Target == "X@A");
        Assert.Equal("Taxiway X at A", atA.DisplayName);
    }

    [Fact]
    public void SingleCrossedTaxiway_KeepsBareEntry()
    {
        var (vm, ac, _) = MakeDoubleCrossingFixture();
        ac.TaxiRoute = "A";

        var targets = vm.GetHoldShortTargets(ac);

        // Route A only: X is crossed once (at n1) and B is an adjacent turn-off — both bare.
        Assert.Contains(targets, t => (t.Target == "X") && (t.DisplayName == "Taxiway X"));
        Assert.DoesNotContain(targets, t => t.Target.Contains('@'));
    }

    [Fact]
    public void LocatedPreview_EndsAtTheNamedCrossing()
    {
        var (vm, ac, layout) = MakeDoubleCrossingFixture();

        var previewAtB = vm.FindHoldShortPreviewRoute(ac, "X@B");
        Assert.NotNull(previewAtB);
        Assert.Equal(3, previewAtB.Segments[^1].ToNodeId);

        var previewAtA = vm.FindHoldShortPreviewRoute(ac, "X@A");
        Assert.NotNull(previewAtA);
        Assert.Equal(1, previewAtA.Segments[^1].ToNodeId);

        // Bare form keeps today's first-crossing preview.
        var bare = vm.FindHoldShortPreviewRoute(ac, "X");
        Assert.NotNull(bare);
        Assert.Equal(1, bare.Segments[^1].ToNodeId);
        Assert.NotNull(layout);
    }
}
