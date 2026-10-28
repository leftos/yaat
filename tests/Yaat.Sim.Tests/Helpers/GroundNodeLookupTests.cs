using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// <see cref="GroundNodeLookup"/> on the shared harness's KOAK layout: each lookup finds the node it names, and a query
/// that matches nothing, or several nodes without a tie-breaker, throws instead of returning one of them.
/// </summary>
public class GroundNodeLookupTests
{
    private static AirportGroundLayout Oak() => Assert.IsType<AirportGroundLayout>(new TestAirportGroundData().GetLayout("OAK"));

    private static string Ids(IEnumerable<GroundNode> nodes) => string.Join(", ", nodes.Select(node => $"#{node.Id}"));

    private static double EdgeFt(GroundNode from, GroundNode to) =>
        from.Edges.Single(edge => ReferenceEquals(edge.OtherNode(from), to)).DistanceNm * GeoMath.FeetPerNm;

    private static GroundNode Oak33BarOnCAtJ(AirportGroundLayout layout) =>
        GroundNodeLookup.HoldShortBar(layout, "33", "C", GroundNodeLookup.Junction(layout, "C", "J"));

    [Fact]
    public void HoldShortBar_EachSideOfACrossing_NearestToPicksThatSidesBar()
    {
        AirportGroundLayout layout = Oak();
        List<GroundNode> bars = TestLayoutNodes.RunwayHoldShortsOnTaxiway(layout, "33", "C");
        Assert.Equal(2, bars.Count);

        foreach (GroundNode bar in bars)
        {
            GroundNode found = GroundNodeLookup.HoldShortBar(layout, "33", "C", bar);
            Assert.Same(bar, found);
            Assert.Equal(GroundNodeType.RunwayHoldShort, found.Type);
            Assert.True(found.RunwayId?.Contains("33"), $"#{found.Id} is not a bar of 33");
        }
    }

    [Fact]
    public void HoldShortBar_TwoBarsWithoutNearestTo_ThrowsNamingEveryMatch()
    {
        AirportGroundLayout layout = Oak();
        List<GroundNode> bars = TestLayoutNodes.RunwayHoldShortsOnTaxiway(layout, "33", "C");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => GroundNodeLookup.HoldShortBar(layout, "33", "C"));
        Assert.Equal(
            $"OAK: 2 nodes match hold-short bar of runway 33 on taxiway C ({Ids(bars)}); pick one with the nearestTo overload",
            error.Message
        );
    }

    [Fact]
    public void HoldShortBar_TaxiwayThatNeverMeetsTheRunway_Throws()
    {
        AirportGroundLayout layout = Oak();

        InvalidOperationException single = Assert.Throws<InvalidOperationException>(() => GroundNodeLookup.HoldShortBar(layout, "33", "NOSUCH"));
        InvalidOperationException nearest = Assert.Throws<InvalidOperationException>(() =>
            GroundNodeLookup.HoldShortBar(layout, "33", "NOSUCH", GroundNodeLookup.Parking(layout, "GA20"))
        );
        Assert.Equal("OAK: no hold-short bar of runway 33 on taxiway NOSUCH", single.Message);
        Assert.Equal(single.Message, nearest.Message);
    }

    [Fact]
    public void Junction_TwoTaxiwaysThatMeetOnce_ReturnsTheNodeOnBoth()
    {
        GroundNode junction = GroundNodeLookup.Junction(Oak(), "C", "J");

        Assert.Contains(junction.Edges, edge => edge.TaxiwayName == "C");
        Assert.Contains(junction.Edges, edge => edge.TaxiwayName == "J");
    }

    [Fact]
    public void Junction_RunwayCentrelineByItsCombinedName_ReturnsTheNodeOnBoth()
    {
        GroundNode join = GroundNodeLookup.Junction(Oak(), "RWY15/33", "F");

        Assert.True(AirportGroundLayout.HasRunwayCenterlineEdge(join), $"#{join.Id} is not on a runway centreline");
        Assert.Contains(join.Edges, edge => edge.TaxiwayName == "F");
    }

    [Fact]
    public void Junction_TaxiwaysMeetingAtSeveralNodes_NearestToPicksEach_AndWithoutItThrows()
    {
        AirportGroundLayout layout = Oak();
        IGrouping<(string A, string B), GroundNode> multi = layout
            .Nodes.Values.SelectMany(node =>
            {
                string[] names = [.. node.Edges.Select(edge => edge.TaxiwayName).Distinct().Order(StringComparer.Ordinal)];
                return names.SelectMany((a, i) => names.Skip(i + 1).Select(b => (Pair: (a, b), Node: node)));
            })
            .GroupBy(entry => entry.Pair, entry => entry.Node)
            .First(group => group.Count() > 1);
        (string a, string b) = multi.Key;

        foreach (GroundNode node in multi)
        {
            Assert.Same(node, GroundNodeLookup.Junction(layout, a, b, node));
        }

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => GroundNodeLookup.Junction(layout, a, b));
        Assert.Contains($"OAK: {multi.Count()} nodes match junction of taxiways {a} and {b} (", error.Message);
        Assert.All(multi, node => Assert.Contains($"#{node.Id}", error.Message));
    }

    [Fact]
    public void Junction_TaxiwaysThatNeverMeet_Throws()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => GroundNodeLookup.Junction(Oak(), "C", "NOSUCH"));
        Assert.Equal("OAK: no junction of taxiways C and NOSUCH", error.Message);
    }

    [Fact]
    public void Parking_ByName_ReturnsThatStand()
    {
        GroundNode stand = GroundNodeLookup.Parking(Oak(), "GA20");

        Assert.Equal(GroundNodeType.Parking, stand.Type);
        Assert.Equal("GA20", stand.Name);
    }

    [Fact]
    public void Parking_UnknownName_Throws()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => GroundNodeLookup.Parking(Oak(), "NOSUCH"));
        Assert.Equal("OAK: no parking NOSUCH", error.Message);
    }

    [Fact]
    public void OnTaxiway_ZeroDistance_ReturnsTheStart()
    {
        AirportGroundLayout layout = Oak();
        GroundNode bar = Oak33BarOnCAtJ(layout);

        Assert.Same(bar, GroundNodeLookup.OnTaxiway(layout, "C", bar, 0.0, GroundNodeLookup.Junction(layout, "C", "J")));
    }

    [Fact]
    public void OnTaxiway_ReturnsTheFirstNodeAtOrPastTheDistance()
    {
        AirportGroundLayout layout = Oak();
        GroundNode bar = Oak33BarOnCAtJ(layout);
        GroundNode cj = GroundNodeLookup.Junction(layout, "C", "J");

        GroundNode first = GroundNodeLookup.OnTaxiway(layout, "C", bar, 1.0, cj);
        Assert.NotSame(bar, first);
        double firstFt = EdgeFt(bar, first);
        Assert.Same(first, GroundNodeLookup.OnTaxiway(layout, "C", bar, firstFt, cj));
        GroundNode second = GroundNodeLookup.OnTaxiway(layout, "C", bar, firstFt + 1.0, cj);
        Assert.NotSame(first, second);
        Assert.NotSame(bar, second);
        Assert.Contains(first.Edges, edge => ReferenceEquals(edge.OtherNode(first), second) && (edge.TaxiwayName == "C"));
    }

    [Fact]
    public void OnTaxiway_AtAFork_GoesStraightOn()
    {
        AirportGroundLayout layout = Oak();
        int checkedForks = 0;
        foreach (GroundNode fork in layout.Nodes.Values)
        {
            foreach (IGrouping<string, IGroundEdge> legs in fork.Edges.GroupBy(edge => edge.TaxiwayName).Where(group => group.Count() >= 3))
            {
                foreach (IGroundEdge inbound in legs)
                {
                    GroundNode from = inbound.OtherNode(fork);
                    double arriveDeg = GeoMath.BearingTo(from.Position, fork.Position);
                    IGroundEdge straightest = legs.Where(edge => !ReferenceEquals(edge, inbound))
                        .MinBy(edge => GeoMath.AbsBearingDifference(arriveDeg, GeoMath.BearingTo(fork.Position, edge.OtherNode(fork).Position)))!;
                    double offDeg = GeoMath.AbsBearingDifference(arriveDeg, GeoMath.BearingTo(fork.Position, straightest.OtherNode(fork).Position));
                    if ((offDeg >= 90.0) || (from.Edges.Count(edge => edge.TaxiwayName == legs.Key) > 1))
                    {
                        continue;
                    }

                    double pastForkFt = (inbound.DistanceNm * GeoMath.FeetPerNm) + 1.0;
                    Assert.Same(straightest.OtherNode(fork), GroundNodeLookup.OnTaxiway(layout, legs.Key, from, pastForkFt, fork));
                    checkedForks++;
                }
            }
        }

        Assert.True(checkedForks > 0, "the OAK layout has no taxiway fork to walk through");
    }

    [Fact]
    public void OnTaxiway_PastTheTaxiwaysEnd_Throws()
    {
        AirportGroundLayout layout = Oak();
        GroundNode bar = Oak33BarOnCAtJ(layout);
        GroundNode cj = GroundNodeLookup.Junction(layout, "C", "J");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => GroundNodeLookup.OnTaxiway(layout, "C", bar, 1.0e7, cj));
        Match match = Regex.Match(error.Message, $@"^OAK: taxiway C from #{bar.Id} toward #{cj.Id} ends (\d+) ft along, short of 10000000 ft$");
        Assert.True(match.Success, error.Message);
        Assert.InRange(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), 1, 9_999_999);
    }

    [Fact]
    public void OnTaxiway_StoppedByABendOf90DegreesOrMore_Throws()
    {
        AirportGroundLayout layout = Oak();
        (GroundNode From, GroundNode Bend, IGroundEdge Inbound)? found = BendOf90DegreesOrMore(layout);
        Assert.True(found.HasValue, "the OAK layout has no taxiway bend of 90° or more");
        (GroundNode from, GroundNode bend, IGroundEdge inbound) = found.Value;
        double inboundFt = inbound.DistanceNm * GeoMath.FeetPerNm;
        double askedFt = inboundFt + 1.0;

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            GroundNodeLookup.OnTaxiway(layout, inbound.TaxiwayName, from, askedFt, bend)
        );
        Assert.Equal(
            $"OAK: taxiway {inbound.TaxiwayName} from #{from.Id} toward #{bend.Id} ends {inboundFt:F0} ft along, short of {askedFt:F0} ft",
            error.Message
        );
    }

    /// <summary>
    /// A taxiway edge into a node where every onward edge on that taxiway turns 90° or more, from a node whose taxiway edge
    /// pointing most nearly at the bend is that edge, so a walk from it stops at the bend.
    /// </summary>
    private static (GroundNode From, GroundNode Bend, IGroundEdge Inbound)? BendOf90DegreesOrMore(AirportGroundLayout layout)
    {
        foreach (GroundNode bend in layout.Nodes.Values)
        {
            foreach (IGroundEdge inbound in bend.Edges)
            {
                GroundNode from = inbound.OtherNode(bend);
                double arriveDeg = GeoMath.BearingTo(from.Position, bend.Position);
                List<IGroundEdge> onward = [.. bend.Edges.Where(edge => !ReferenceEquals(edge, inbound) && IsOn(edge, inbound.TaxiwayName))];
                IGroundEdge firstStep = from.Edges.Where(edge => IsOn(edge, inbound.TaxiwayName)).MinBy(edge => OffDeg(from, arriveDeg, edge))!;
                bool turnsBack = (onward.Count > 0) && onward.All(edge => OffDeg(bend, arriveDeg, edge) >= 90.0);
                if (turnsBack && ReferenceEquals(firstStep, inbound) && ((inbound.DistanceNm * GeoMath.FeetPerNm) > 1.0))
                {
                    return (from, bend, inbound);
                }
            }
        }

        return null;
    }

    private static bool IsOn(IGroundEdge edge, string taxiway) => string.Equals(edge.TaxiwayName, taxiway, StringComparison.OrdinalIgnoreCase);

    private static double OffDeg(GroundNode at, double headingDeg, IGroundEdge edge) =>
        GeoMath.AbsBearingDifference(headingDeg, GeoMath.BearingTo(at.Position, edge.OtherNode(at).Position));

    [Fact]
    public void OnTaxiway_NoEdgeWithin90DegreesOfTheTarget_Throws()
    {
        AirportGroundLayout layout = Oak();
        GroundNode stand = GroundNodeLookup.Parking(layout, "GA20");
        IGroundEdge only = Assert.Single(stand.Edges);
        double backDeg = (GeoMath.BearingTo(stand.Position, only.OtherNode(stand).Position) + 180.0) % 360.0;
        GroundNode behind = layout
            .Nodes.Values.Where(node => GeoMath.AbsBearingDifference(backDeg, GeoMath.BearingTo(stand.Position, node.Position)) < 30.0)
            .MinBy(node => GeoMath.DistanceNm(stand.Position, node.Position))!;

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            GroundNodeLookup.OnTaxiway(layout, only.TaxiwayName, stand, 100.0, behind)
        );
        Assert.Contains($"leads toward #{behind.Id}", error.Message);
    }

    [Fact]
    public void OnTaxiway_FromANodeOffTheTaxiway_Throws()
    {
        AirportGroundLayout layout = Oak();
        GroundNode stand = GroundNodeLookup.Parking(layout, "GA20");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            GroundNodeLookup.OnTaxiway(layout, "NOSUCH", stand, 100.0, Oak33BarOnCAtJ(layout))
        );
        Assert.Equal($"OAK: node #{stand.Id} has no edge on taxiway NOSUCH", error.Message);
    }
}
