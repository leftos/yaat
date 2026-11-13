using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// The held-short leg's reversal check (<c>GroundCommandHandler.ReversalUpTo</c>): a leg that turns back on itself by 150° or more
/// is rejected with the turn and the node it happens at; a leg that runs on is not. Built on real KOAK edges, since no aircraft pose
/// is needed to read a segment list's turns.
/// </summary>
public class HeldShortLegReversalTests
{
    private static AirportGroundLayout? OakLayout()
    {
        TestVnasData.EnsureInitialized();
        return new TestAirportGroundData().GetLayout("OAK");
    }

    /// <summary>The segment that drives <paramref name="edge"/> away from <paramref name="from"/>.</summary>
    private static TaxiRouteSegment Segment(IGroundEdge edge, GroundNode from)
    {
        GroundNode to = (edge.Nodes[0].Id == from.Id) ? edge.Nodes[1] : edge.Nodes[0];
        return new TaxiRouteSegment
        {
            Edge = new DirectionalEdge
            {
                Edge = edge,
                FromNode = from,
                ToNode = to,
            },
            TaxiwayName = edge.TaxiwayName,
        };
    }

    [Fact]
    public void AnEdgeDrivenForwardThenBack_Reverses_AtTheFarNode()
    {
        if (OakLayout() is not { } layout)
        {
            return;
        }

        GroundEdge edge = layout.Edges.OrderBy(e => e.Nodes[0].Id).ThenBy(e => e.Nodes[1].Id).First(e => !e.IsRunwayCenterline);
        GroundNode near = edge.Nodes[0];
        GroundNode far = edge.Nodes[1];
        TaxiRouteSegment forward = Segment(edge, near);
        TaxiRouteSegment back = Segment(edge, far);
        double turn = GeoMath.AbsBearingDifference(forward.Edge.ArrivalBearing, back.Edge.DepartureBearing);
        Assert.Equal("180", $"{turn:F0}");

        string? rejection = GroundCommandHandler.ReversalUpTo([forward, back], 1);

        Assert.Equal($"reverses (180°) at node {far.Id}", rejection);
    }

    [Fact]
    public void TwoEdgesRunningStraightOn_DoNotReverse()
    {
        if (OakLayout() is not { } layout)
        {
            return;
        }

        (TaxiRouteSegment first, TaxiRouteSegment second) = StraightPair(layout);

        Assert.Null(GroundCommandHandler.ReversalUpTo([first, second], 1));
    }

    /// <summary>The first two straight taxiway edges, by node id, that meet at a node and turn less than 10° there.</summary>
    private static (TaxiRouteSegment First, TaxiRouteSegment Second) StraightPair(AirportGroundLayout layout)
    {
        foreach (GroundNode middle in layout.Nodes.Values.OrderBy(n => n.Id))
        {
            List<GroundEdge> straight = [.. middle.Edges.OfType<GroundEdge>().Where(e => !e.IsRunwayCenterline)];
            foreach (GroundEdge inbound in straight)
            {
                GroundNode start = inbound.Nodes[0].Id == middle.Id ? inbound.Nodes[1] : inbound.Nodes[0];
                TaxiRouteSegment first = Segment(inbound, start);
                foreach (GroundEdge outbound in straight.Where(e => !ReferenceEquals(e, inbound)))
                {
                    TaxiRouteSegment second = Segment(outbound, middle);
                    if (GeoMath.AbsBearingDifference(first.Edge.ArrivalBearing, second.Edge.DepartureBearing) < 10.0)
                    {
                        return (first, second);
                    }
                }
            }
        }

        throw new InvalidOperationException("KOAK has no two straight taxiway edges running on through a node");
    }
}
