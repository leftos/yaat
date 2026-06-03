using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;

namespace Yaat.Sim.Tests;

/// <summary>
/// Tests for one-way taxiway enforcement in the pathfinder, on a synthetic layout for determinism.
/// A short "C" corridor (S-X-D) and a longer "B" detour (S-Y1-Y2-D) both reach D. A one-way on C allows
/// only D→S, so the reverse moves (S→X, X→D) are forbidden. Auto routes hard-exclude the wrong way;
/// explicit/warn routes traverse it but are flagged.
/// </summary>
public class OneWayTaxiwayPathfinderTests
{
    private static GroundNode Node(int id, double lat, double lon) =>
        new()
        {
            Id = id,
            Position = new LatLon(lat, lon),
            Type = GroundNodeType.TaxiwayIntersection,
        };

    private static void Edge(GroundNode a, GroundNode b, string twy)
    {
        var edge = new GroundEdge
        {
            Nodes = [a, b],
            TaxiwayName = twy,
            DistanceNm = GeoMath.DistanceNm(a.Position, b.Position),
        };
        a.Edges.Add(edge);
        b.Edges.Add(edge);
    }

    private static SearchContext Ctx(AirportGroundLayout layout, int from, int to, IReadOnlySet<(int, int)> forbidden, OneWayMode mode) =>
        new SearchContext(
            layout,
            from,
            new DestinationDescriptor(to, null, null, null, DestinationKind.Node),
            [],
            null,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            AircraftCategory.Jet,
            RoutePreference.FewestTurns,
            null
        )
        {
            ForbiddenOneWayMoves = forbidden,
            OneWayMode = mode,
        };

    private static bool UsesTaxiway(TaxiRoute route, string name) =>
        route.Segments.Any(s => string.Equals(s.TaxiwayName, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>S-X-D short "C" corridor + S-Y1-Y2-D long "B" detour. Returns node ids and the C-reverse forbidden set.</summary>
    private static AirportGroundLayout Layout(out int start, out int dest, out HashSet<(int, int)> forbidCReverse)
    {
        var s = Node(0, 37.000, -122.000);
        var x = Node(1, 37.000, -122.010);
        var d = Node(2, 37.000, -122.020);
        var y1 = Node(3, 37.030, -122.000);
        var y2 = Node(4, 37.030, -122.020);

        Edge(s, x, "C");
        Edge(x, d, "C");
        Edge(s, y1, "B");
        Edge(y1, y2, "B");
        Edge(y2, d, "B");

        var layout = new AirportGroundLayout { AirportId = "TST" };
        foreach (var n in new[] { s, x, d, y1, y2 })
        {
            layout.Nodes[n.Id] = n;
        }

        start = s.Id;
        dest = d.Id;
        // C is one-way D->S, so the S->X and X->D moves are forbidden.
        forbidCReverse = [(s.Id, x.Id), (x.Id, d.Id)];
        return layout;
    }

    [Fact]
    public void Auto_HardExclude_TakesDetour_AvoidingWrongWayCorridor()
    {
        var layout = Layout(out int start, out int dest, out var forbidden);

        var (route, failure) = AutoRouter.Run(Ctx(layout, start, dest, forbidden, OneWayMode.HardExclude));

        Assert.Null(failure);
        Assert.NotNull(route);
        Assert.False(UsesTaxiway(route!, "C"), "Auto route must not travel C the wrong way.");
        Assert.True(UsesTaxiway(route, "B"), "Auto route must take the legal B detour.");
    }

    [Fact]
    public void Off_UsesShortCorridor_WhenNoConstraint()
    {
        var layout = Layout(out int start, out int dest, out _);

        var (route, failure) = AutoRouter.Run(Ctx(layout, start, dest, new HashSet<(int, int)>(), OneWayMode.Off));

        Assert.Null(failure);
        Assert.NotNull(route);
        Assert.True(UsesTaxiway(route!, "C"), "With no constraint the shorter C corridor is used.");
    }

    [Fact]
    public void Explicit_Warn_TraversesWrongWayButFlagsIt()
    {
        var layout = Layout(out int start, out int dest, out var forbidden);

        // Warn mode does not hard-block, so A* still picks the shorter C corridor — the wrong way — and
        // the route surfaces a warning.
        var (route, failure) = AutoRouter.Run(Ctx(layout, start, dest, forbidden, OneWayMode.Warn));

        Assert.Null(failure);
        Assert.NotNull(route);
        Assert.True(UsesTaxiway(route!, "C"));
        Assert.Contains(route.Warnings, w => w.Contains("one-way", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WithFlowDirection_NoWarning()
    {
        var layout = Layout(out int start, out int dest, out var forbidden);

        // Travelling the allowed direction D->S must not be flagged.
        var (route, failure) = AutoRouter.Run(Ctx(layout, dest, start, forbidden, OneWayMode.Warn));

        Assert.Null(failure);
        Assert.NotNull(route);
        Assert.True(UsesTaxiway(route!, "C"));
        Assert.DoesNotContain(route.Warnings, w => w.Contains("one-way", StringComparison.OrdinalIgnoreCase));
    }
}
