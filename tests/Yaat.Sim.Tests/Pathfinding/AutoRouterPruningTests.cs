using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pathfinding;

/// <summary>
/// The A* closed set must key on the taxiway a state arrived on, not only on (node, bearing): the
/// taxiway-transition penalty is charged on the edge <em>after</em> an arrival, so two arrivals at the same
/// node with the same bearing but on different taxiways have different onward costs and neither may prune
/// the other. OAK SIG1 → runway 30: the A→B fillet reaches the A/B junction (node 805) at B's bearing a
/// hair cheaper than the straight-B arrival, then pays the A→B transition on the next edge. Pruning the B
/// arrival there returned D C A B (around the east end of 10L/28R) although D C B scores cheaper.
/// </summary>
public class AutoRouterPruningTests
{
    [Fact]
    public void Oak_Sig1_ToRunway30_KeepsTheStraightBArrivalTheFilletArrivalWouldPrune()
    {
        TestVnasData.EnsureInitialized();
        var layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        var parking = layout.FindParkingByName("SIG1");
        Assert.NotNull(parking);
        var holdShort = layout
            .Nodes.Values.Where(n => n.Type == GroundNodeType.RunwayHoldShort && n.RunwayId is { } r && r.Contains("30"))
            .OrderBy(n => GeoMath.DistanceNm(parking.Position, n.Position))
            .First();
        var destination = new DestinationDescriptor(holdShort.Id, "30", null, null, DestinationKind.Runway);
        var ctx = new SearchContext(
            layout,
            parking.Id,
            destination,
            [],
            null,
            new HashSet<HoldShortTarget>(),
            AircraftCategory.Piston,
            RoutePreference.FewestTurns,
            null
        );

        var (route, failure) = AutoRouter.Run(ctx);

        Assert.Null(failure);
        Assert.NotNull(route);
        Assert.StartsWith("D C B W", route.ToSummary());
    }
}
