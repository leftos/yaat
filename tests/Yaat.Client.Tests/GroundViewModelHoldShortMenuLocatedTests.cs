using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.Tests;

/// <summary>
/// The ground context menu's "Hold short of..." rows for a route that meets the same target on
/// more than one route taxiway. One bare `HS X` can only bind the first crossing, so the menu emits
/// located rows (<c>X@A</c>, <c>X@B</c>) — one per crossing, in route order — and the hover preview for a located
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
        GroundNode n0 = Node(0, 37.700, -122.200);
        GroundNode n1 = Node(1, 37.702, -122.200);
        GroundNode n2 = Node(2, 37.704, -122.200);
        GroundNode n3 = Node(3, 37.704, -122.203);
        GroundNode n4 = Node(4, 37.704, -122.206);
        GroundNode x1 = Node(5, 37.702, -122.198);
        GroundNode x2 = Node(6, 37.706, -122.203);
        GroundNode c1 = Node(7, 37.702, -122.206);

        var layout = new AirportGroundLayout { AirportId = "TEST" };
        foreach (GroundNode? n in new[] { n0, n1, n2, n3, n4, x1, x2, c1 })
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
        var vm = new GroundViewModel(connection, sendCommand: (_, _, _) => Task.CompletedTask)
        {
            RoomActiveRunways = () => new Dictionary<string, IReadOnlyList<string>>(),
        };
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
        (GroundViewModel? vm, AircraftModel? ac, AirportGroundLayout? layout) = MakeDoubleCrossingFixture();
        TaxiRoute? direct = Yaat.Sim.Data.Airport.TaxiPathfinder.ResolveExplicitPath(
            layout,
            0,
            ["A", "B", "C"],
            out string? failReason,
            new ExplicitPathOptions { OccupiedTaxiway = null },
            AircraftCategory.Jet,
            WakeTurbulenceData.WakeClass.Large
        );
        Assert.True(direct is not null, $"direct resolve failed: {failReason}");

        TaxiRoute? route = vm.ResolveRemainingRoute(ac);
        Assert.NotNull(route);
        Assert.NotEmpty(route.Segments);
    }

    [Fact]
    public void DoubleCrossedTaxiway_OffersOneLocatedRowPerCrossing_InPositionOrder()
    {
        (GroundViewModel? vm, AircraftModel? ac, AirportGroundLayout _) = MakeDoubleCrossingFixture();

        IReadOnlyList<HoldShortChoice> rows = vm.GetHoldShortTargets(ac);

        Assert.Equal(["HS X@A", "HS X@B"], rows.Where(r => r.Label.Name == "X").Select(r => r.Command));
        Assert.DoesNotContain(rows, r => r.Command == "HS X");

        HoldShortChoice atA = rows.Single(r => r.Command == "HS X@A");
        HoldShortChoice atB = rows.Single(r => r.Command == "HS X@B");
        Assert.Equal((HoldShortChoice.TaxiwayBadge, "crossing on A"), (atA.Label.Badge, atA.Label.Where));
        Assert.Equal(1, atA.Preview.Segments[^1].ToNodeId);
        Assert.Equal(3, atB.Preview.Segments[^1].ToNodeId);
        Assert.True(atA.Label.DistanceFt < atB.Label.DistanceFt, $"X@A ~{atA.Label.DistanceFt} ft, X@B ~{atB.Label.DistanceFt} ft");
    }

    [Fact]
    public void SingleCrossedTaxiway_KeepsBareRow()
    {
        (GroundViewModel? vm, AircraftModel? ac, AirportGroundLayout _) = MakeDoubleCrossingFixture();
        ac.TaxiRoute = "A";

        IReadOnlyList<HoldShortChoice> rows = vm.GetHoldShortTargets(ac);

        // Route A only: X is crossed once (at n1) and B meets the end of A (at n2) — both bare, X first.
        Assert.Equal(["HS X", "HS B"], rows.Select(r => r.Command));
        Assert.Equal(("X", "crossing on A"), (rows[0].Label.Name, rows[0].Label.Where));
        Assert.Equal(("B", "at A, end of route"), (rows[1].Label.Name, rows[1].Label.Where));
        Assert.Equal(1, rows[0].Preview.Segments[^1].ToNodeId);
    }
}
