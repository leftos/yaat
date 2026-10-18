using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Data;

/// <summary>
/// Where <see cref="HoldShortAnnotator.ComputeHoldShortPositions"/> puts an explicit taxiway hold-short at a shallow merge, on
/// routes built along the arriving taxiway's straight edges into the merge node (YAAT-800). Nodes are found by shape and
/// taxiway names, never by id.
/// </summary>
public sealed class ShallowMergeHoldShortPlacementTests
{
    /// <summary>How far back along the arriving taxiway the route starts (ft).</summary>
    private const double RouteBackFt = 1500.0;

    private readonly ITestOutputHelper _output;

    public ShallowMergeHoldShortPlacementTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    [Theory]
    [InlineData("AY1")]
    [InlineData("B5")]
    public void SfoMergeIntoA_B738WhoseLengthSetbackStandsBehindTheIntersection_KeepsTheCappedStop(string arriving)
    {
        if (new TestAirportGroundData().GetLayout("SFO") is not { } layout)
        {
            return;
        }

        double lengthFt = LengthFt("B738");
        (TaxiRoute route, GroundNode merge) = PlaceAtMerge(layout, arriving, "A", lengthFt);
        HoldShortPoint bar = route.HoldShortPoints[0];
        double noseBackFt = NoseBackFt(route, bar, merge, lengthFt);
        _output.WriteLine($"{arriving}→A B738: nose {noseBackFt:F1} ft back of the merge; warnings: {string.Join(" | ", route.Warnings)}");

        Assert.Null(bar.MergeHold);
        Assert.True(Math.Abs(noseBackFt - ((lengthFt / 2.0) + 30.0)) <= 1.0, $"the nose is {noseBackFt:F1} ft back, not at the length setback");
        Assert.DoesNotContain(route.Warnings, w => w.StartsWith("holding short of TWY A before TWY ", StringComparison.Ordinal));
        Assert.Single(route.Warnings, w => w.StartsWith("holding short of TWY A — wingtip clearance from A not assured (", StringComparison.Ordinal));
    }

    [Fact]
    public void SfoB5IntoA_C172_HoldsBeforeOneNamedTaxiway()
    {
        if (new TestAirportGroundData().GetLayout("SFO") is not { } layout)
        {
            return;
        }

        (TaxiRoute route, _) = PlaceAtMerge(layout, "B5", "A", LengthFt("C172"));
        HoldShortPoint bar = route.HoldShortPoints[0];
        _output.WriteLine($"B5→A C172: {bar.MergeHold}; warnings: {string.Join(" | ", route.Warnings)}");

        Assert.Equal(new MergeHold("B5", "B4"), bar.MergeHold);
        Assert.Single(route.Warnings, w => w.StartsWith("holding short of TWY A before TWY B4 (B5 merges into A)", StringComparison.Ordinal));
    }

    [Fact]
    public void OakW5IntoW_B738_UncappedFloorStopBehindTheLengthSetback_CarriesTheSetbackAdvisory()
    {
        if (new TestAirportGroundData().GetLayout("OAK") is not { } layout)
        {
            return;
        }

        double lengthFt = LengthFt("B738");
        (TaxiRoute route, GroundNode merge) = PlaceAtMerge(layout, "W5", "W", lengthFt);
        HoldShortPoint bar = route.HoldShortPoints[0];
        _output.WriteLine($"W5→W B738: nose {NoseBackFt(route, bar, merge, lengthFt):F1} ft back; warnings: {string.Join(" | ", route.Warnings)}");

        Assert.Null(bar.MergeHold);
        Assert.Equal(["holding short of TWY W 203 ft back (W5 merges into W)"], route.Warnings);

        HoldShortAnnotator.ComputeHoldShortPositions(layout, route, lengthFt);
        Assert.Equal(["holding short of TWY W 203 ft back (W5 merges into W)"], route.Warnings);
    }

    /// <summary>
    /// The same merge for a body long enough that the plain <c>length + 30</c> setback already stands behind the point where
    /// the floor is met: the stop is the plain setback, which needs no explaining, so the route carries no advisory. No real
    /// type is that long (the floor is met 203 ft back), so the length is a stand-in for one.
    /// </summary>
    [Fact]
    public void OakW5IntoW_FloorStopWithinTheLengthSetback_CarriesNoAdvisory()
    {
        if (new TestAirportGroundData().GetLayout("OAK") is not { } layout)
        {
            return;
        }

        const double LongerThanTheFloorPointFt = 500.0;
        (TaxiRoute route, GroundNode merge) = PlaceAtMerge(layout, "W5", "W", LongerThanTheFloorPointFt);
        HoldShortPoint bar = route.HoldShortPoints[0];
        double noseBackFt = NoseBackFt(route, bar, merge, LongerThanTheFloorPointFt);
        _output.WriteLine(
            $"W5→W {LongerThanTheFloorPointFt:F0} ft body: nose {noseBackFt:F1} ft back; warnings: {string.Join(" | ", route.Warnings)}"
        );

        Assert.True(Math.Abs(noseBackFt - ((LongerThanTheFloorPointFt / 2.0) + 30.0)) <= 1.0, $"the nose is {noseBackFt:F1} ft back");
        Assert.Empty(route.Warnings);
    }

    /// <summary>
    /// Across every shallow merge in the committed layouts that have them, a merge hold names a taxiway with a straight edge
    /// at a route node between the bar's stop and the bar itself, and each airport here places at least one merge hold.
    /// </summary>
    [Theory]
    [InlineData("ATL")]
    [InlineData("FLL")]
    [InlineData("IAH")]
    [InlineData("LAX")]
    [InlineData("MIA")]
    [InlineData("OAK")]
    [InlineData("SEA")]
    [InlineData("SFO")]
    public void EveryMergeHold_NamesATaxiwayThatRunsThroughTheIntersection(string airport)
    {
        if (new TestAirportGroundData().GetLayout(airport) is not { } layout)
        {
            return;
        }

        int named = 0;
        foreach ((GroundNode node, GroundEdge arriving, GroundEdge ahead) in FindJunctions(layout, behindOffDeg: (160.0, 179.0)))
        {
            foreach (string type in new[] { "C172", "B738" })
            {
                TaxiRoute route = PlaceAt(layout, node, arriving, ahead, LengthFt(type));
                if (route.HoldShortPoints[0].MergeHold is not { } merge)
                {
                    continue;
                }

                named++;
                Assert.True(
                    NamesATaxiwayBetweenStopAndBar(route, route.HoldShortPoints[0], merge.BeforeTaxiway),
                    $"{airport} node {node.Id} {arriving.TaxiwayName}→{ahead.TaxiwayName} {type}: holds before {merge.BeforeTaxiway}, "
                        + "which is not carried by any edge at a route node from the stop to the bar"
                );
            }
        }

        _output.WriteLine($"{airport}: {named} merge holds checked");
        Assert.True(named > 0, $"{airport}: the sweep found no merge hold to check");
    }

    /// <summary>
    /// FLL draws A and A1 on one line: where the route arrives on one of them and the other continues straight ahead, the
    /// other's edge straight behind fans off the arriving line by under 1°. That is one line under two names, not a merge,
    /// so the placement stays what it was before shallow merges were measured: no merge hold, and no floor held against the
    /// target's own centreline (which lies under the aircraft and would read about 0 ft).
    /// </summary>
    [Fact]
    public void FllCoincidentAAndA1_IsNoShallowMerge()
    {
        if (new TestAirportGroundData().GetLayout("FLL") is not { } layout)
        {
            return;
        }

        (GroundNode node, GroundEdge arrivingEdge, GroundEdge ahead) = FindJunctions(layout, behindOffDeg: (179.0, 180.0))
            .First(j => (j.Arriving.TaxiwayName, j.Ahead.TaxiwayName) is ("A", "A1") or ("A1", "A"));
        string target = ahead.TaxiwayName;
        TaxiRoute route = PlaceAt(layout, node, arrivingEdge, ahead, LengthFt("C172"));
        HoldShortPoint bar = route.HoldShortPoints[0];
        _output.WriteLine($"FLL node {node.Id}: {arrivingEdge.TaxiwayName}→{target}; warnings: {string.Join(" | ", route.Warnings)}");

        Assert.Null(bar.MergeHold);
        Assert.DoesNotContain(route.Warnings, w => w.StartsWith($"holding short of TWY {target} before TWY ", StringComparison.Ordinal));
        Assert.DoesNotContain(
            route.Warnings,
            w => w.StartsWith($"holding short of TWY {target} — wingtip clearance from {target} not assured", StringComparison.Ordinal)
        );
    }

    private static double LengthFt(string type) => FaaAircraftDatabase.Get(type)!.LengthFt!.Value;

    private static (TaxiRoute Route, GroundNode Merge) PlaceAtMerge(AirportGroundLayout layout, string arriving, string target, double lengthFt)
    {
        (GroundNode node, GroundEdge arrivingEdge, GroundEdge ahead) = FindJunctions(layout, behindOffDeg: (160.0, 179.0))
            .Single(j => (j.Arriving.TaxiwayName == arriving) && (j.Ahead.TaxiwayName == target));
        return (PlaceAt(layout, node, arrivingEdge, ahead, lengthFt), node);
    }

    private static TaxiRoute PlaceAt(AirportGroundLayout layout, GroundNode node, GroundEdge arriving, GroundEdge ahead, double lengthFt)
    {
        var route = new TaxiRoute
        {
            Segments =
            [
                .. WalkBack(node, arriving),
                new TaxiRouteSegment { TaxiwayName = ahead.TaxiwayName, Edge = ahead.Directed(node, ahead.OtherNode(node)) },
            ],
            HoldShortPoints =
            [
                new HoldShortPoint
                {
                    NodeId = node.Id,
                    Reason = HoldShortReason.ExplicitHoldShort,
                    TargetName = ahead.TaxiwayName,
                },
            ],
        };
        HoldShortAnnotator.ComputeHoldShortPositions(layout, route, lengthFt);
        return route;
    }

    /// <summary>
    /// Every node where a straight edge arrives and another taxiway leaves within 20° of straight ahead while a second edge
    /// of that taxiway leaves at an angle off the arriving heading inside <paramref name="behindOffDeg"/> (degrees, low
    /// inclusive, high inclusive), by node id.
    /// </summary>
    private static List<(GroundNode Node, GroundEdge Arriving, GroundEdge Ahead)> FindJunctions(
        AirportGroundLayout layout,
        (double Low, double High) behindOffDeg
    )
    {
        var found = new List<(GroundNode, GroundEdge, GroundEdge)>();
        foreach (GroundNode n in layout.Nodes.Values.OrderBy(n => n.Id))
        {
            var straight = n.Edges.OfType<GroundEdge>().Where(e => !e.IsRamp && !e.IsRunwayCenterline).ToList();
            foreach (GroundEdge arr in straight)
            {
                double arrivingDeg = arr.Directed(arr.OtherNode(n), n).ArrivalBearing;
                var offs = straight.Select(e => (Edge: e, Off: GeoMath.AbsBearingDifference(arrivingDeg, LeavingDeg(e, n)))).ToList();
                foreach ((GroundEdge ahead, double off) in offs.Where(b => (b.Off <= 20.0) && (b.Edge.TaxiwayName != arr.TaxiwayName)))
                {
                    bool behind = offs.Any(b =>
                        !ReferenceEquals(b.Edge, arr)
                        && (b.Edge.TaxiwayName == ahead.TaxiwayName)
                        && (b.Off >= behindOffDeg.Low)
                        && (b.Off <= behindOffDeg.High)
                    );
                    if (behind)
                    {
                        found.Add((n, arr, ahead));
                    }
                }
            }
        }

        return found;
    }

    private static double LeavingDeg(GroundEdge e, GroundNode node)
    {
        bool fromStart = e.Nodes[0].Id == node.Id;
        LatLon next =
            e.IntermediatePoints.Count == 0 ? e.OtherNode(node).Position
            : fromStart ? new LatLon(e.IntermediatePoints[0].Lat, e.IntermediatePoints[0].Lon)
            : new LatLon(e.IntermediatePoints[^1].Lat, e.IntermediatePoints[^1].Lon);
        return GeoMath.BearingTo(node.Position, next);
    }

    /// <summary>The arriving taxiway's straight edges back from <paramref name="node"/> for <see cref="RouteBackFt"/>, in route order.</summary>
    private static List<TaxiRouteSegment> WalkBack(GroundNode node, GroundEdge arriving)
    {
        var reversed = new List<TaxiRouteSegment>();
        GroundNode to = node;
        GroundEdge edge = arriving;
        double walkedFt = 0.0;
        while (true)
        {
            GroundNode from = edge.OtherNode(to);
            reversed.Add(new TaxiRouteSegment { TaxiwayName = edge.TaxiwayName, Edge = edge.Directed(from, to) });
            walkedFt += GeoMath.DistanceNm(from.Position, to.Position) * GeoMath.FeetPerNm;
            double inDeg = GeoMath.BearingTo(to.Position, from.Position);
            GroundEdge current = edge;
            GroundEdge? next = from
                .Edges.OfType<GroundEdge>()
                .Where(e => !ReferenceEquals(e, current) && (e.TaxiwayName == current.TaxiwayName))
                .Select(e => (Edge: e, Off: GeoMath.AbsBearingDifference(inDeg, GeoMath.BearingTo(from.Position, e.OtherNode(from).Position))))
                .Where(x => x.Off < 45.0)
                .OrderBy(x => x.Off)
                .Select(x => x.Edge)
                .FirstOrDefault();
            if ((walkedFt >= RouteBackFt) || (next is null))
            {
                break;
            }

            to = from;
            edge = next;
        }

        reversed.Reverse();
        return reversed;
    }

    /// <summary>How far back of <paramref name="merge"/> (ft, along the route's chords) the nose of the aircraft at the bar's stop is.</summary>
    private static double NoseBackFt(TaxiRoute route, HoldShortPoint bar, GroundNode merge, double lengthFt)
    {
        List<LatLon> chords = [route.Segments[0].Edge.FromNode.Position, .. route.Segments.Select(s => s.Edge.ToNode.Position)];
        var stop = new LatLon(bar.Latitude!.Value, bar.Longitude!.Value);
        return AlongFt(chords, merge.Position) - AlongFt(chords, stop) - (lengthFt / 2.0);
    }

    /// <summary>
    /// Whether <paramref name="taxiway"/> is carried by an edge at a route node from the bar's stop forward to the bar's node,
    /// the stretch the aircraft holding there occupies: a straight edge, or a fillet arc whose joined names include it.
    /// </summary>
    private static bool NamesATaxiwayBetweenStopAndBar(TaxiRoute route, HoldShortPoint bar, string taxiway) =>
        NodesFromStopToBar(route, bar).Any(n => n.Edges.SelectMany(EdgeNames).Any(name => name == taxiway));

    private static IEnumerable<string> EdgeNames(IGroundEdge edge) => edge is GroundArc arc ? arc.TaxiwayNames : [edge.TaxiwayName];

    /// <summary>The route's nodes from the bar's stop forward to the bar's node, both included.</summary>
    private static List<GroundNode> NodesFromStopToBar(TaxiRoute route, HoldShortPoint bar)
    {
        List<LatLon> chords = [route.Segments[0].Edge.FromNode.Position, .. route.Segments.Select(s => s.Edge.ToNode.Position)];
        double stopFt = AlongFt(chords, new LatLon(bar.Latitude!.Value, bar.Longitude!.Value));
        var nodes = new List<GroundNode>();
        double accFt = 0.0;
        for (int i = 0; i < route.Segments.Count; i++)
        {
            accFt += GeoMath.DistanceNm(chords[i], chords[i + 1]) * GeoMath.FeetPerNm;
            if (accFt >= stopFt - 0.01)
            {
                nodes.Add(route.Segments[i].Edge.FromNode);
            }
        }

        nodes.AddRange(route.Segments.Select(s => s.Edge.ToNode).Where(n => n.Id == bar.NodeId));
        return nodes;
    }

    private static double AlongFt(List<LatLon> chords, LatLon point)
    {
        double bestOffFt = double.MaxValue;
        double bestAlongFt = 0.0;
        double accFt = 0.0;
        for (int i = 0; i + 1 < chords.Count; i++)
        {
            double offFt = GeoMath.DistanceToSegmentFt(point, chords[i], chords[i + 1]);
            if (offFt < bestOffFt - 0.01)
            {
                bestOffFt = offFt;
                double fromStartFt = GeoMath.DistanceNm(chords[i], point) * GeoMath.FeetPerNm;
                bestAlongFt = accFt + Math.Sqrt(Math.Max(0.0, (fromStartFt * fromStartFt) - (offFt * offFt)));
            }

            accFt += GeoMath.DistanceNm(chords[i], chords[i + 1]) * GeoMath.FeetPerNm;
        }

        return bestAlongFt;
    }
}
