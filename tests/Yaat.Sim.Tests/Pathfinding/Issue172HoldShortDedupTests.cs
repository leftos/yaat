using Xunit;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pathfinding;

/// <summary>
/// Issue #172 sub-bug #1: a taxi clearance whose explicit hold-short target is a taxiway the route
/// runs ALONG (e.g. <c>TAXI G B HS B</c>) echoed the hold-short once per B-adjacent node —
/// <c>"G B HS B HS B HS B …"</c> repeated 18-120 times. The route summary must list a taxiway
/// hold-short at most once.
///
/// Two layers are asserted: the source fix in <see cref="RouteMaterialiser"/> (exactly one
/// <c>HoldShortPoint</c> per distinct taxiway target) on the real SFO layout, and the defensive
/// collapse in <see cref="TaxiRoute.ToSummary"/>.
/// </summary>
public class Issue172HoldShortDedupTests
{
    // SFO node 867 is the 01L/19R hold-short on taxiway G (where arrivals off 19L/19R hold) —
    // exactly where KLM605/EJA512/N984DC sat when issued "TAXI G B HS B" in the recording.
    private const int GHoldShortNode = 867;

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }

        return count;
    }

    [Fact]
    public void TaxiGB_HoldShortB_AnnotatesTaxiwayHoldShortOnce()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var layout = new TestAirportGroundData().GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

        Assert.True(layout.Nodes.ContainsKey(GHoldShortNode), $"Node {GHoldShortNode} missing from current sfo layout");

        var route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            fromNodeId: GHoldShortNode,
            taxiwayNames: ["G", "B"],
            out string? failReason,
            new ExplicitPathOptions { AirportId = "SFO", ExplicitHoldShorts = ["B"] },
            AircraftCategory.Jet
        );

        Assert.Null(failReason);
        Assert.NotNull(route);

        int bHoldShorts = route.HoldShortPoints.Count(hs =>
            hs.Reason == HoldShortReason.ExplicitHoldShort && string.Equals(hs.TargetName, "B", StringComparison.OrdinalIgnoreCase)
        );
        Assert.Equal(1, bHoldShorts);

        // The operator echo must contain a single "HS B".
        Assert.Equal(1, CountOccurrences(route.ToSummary(), "HS B"));
    }

    [Fact]
    public void ToSummary_CollapsesDuplicateTaxiwayHoldShorts()
    {
        var n0 = new GroundNode
        {
            Id = 0,
            Position = new LatLon(37.700, -122.200),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var n1 = new GroundNode
        {
            Id = 1,
            Position = new LatLon(37.701, -122.200),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var edge = new GroundEdge
        {
            Nodes = [n0, n1],
            TaxiwayName = "B",
            DistanceNm = GeoMath.DistanceNm(n0.Position, n1.Position),
        };

        var route = new TaxiRoute
        {
            Segments = [new TaxiRouteSegment { Edge = edge.Directed(n0, n1), TaxiwayName = "B" }],
            HoldShortPoints =
            [
                new HoldShortPoint
                {
                    NodeId = 1,
                    Reason = HoldShortReason.ExplicitHoldShort,
                    TargetName = "B",
                },
                new HoldShortPoint
                {
                    NodeId = 2,
                    Reason = HoldShortReason.ExplicitHoldShort,
                    TargetName = "B",
                },
                new HoldShortPoint
                {
                    NodeId = 3,
                    Reason = HoldShortReason.ExplicitHoldShort,
                    TargetName = "B",
                },
            ],
        };

        Assert.Equal("B HS B", route.ToSummary());
    }
}
