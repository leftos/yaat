using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Fillet;
using Yaat.Sim.Data.Airport.Fillet.V2;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Fillet;

/// <summary>Round-9 synthetic verification: stable intermediate on a filleted arm walk is chained and reachable.</summary>
public class FilletStableWalkChainTests
{
    [Fact]
    public void SharedArmWithStableIntermediate_PlanChainsThroughMid_AndMidReachableFromHoldShort()
    {
        var layout = BuildSharedArmWithIntermediate();
        layout.RebuildAdjacencyLists();

        int midId = 5;
        int j1Id = 1;

        var jp1 = JunctionClassifier.Classify(layout.Nodes[j1Id], preserveNode: false, manualArcNodes: []);
        int nextCutId = 1;
        var r1 = ArmCutResolver.Resolve(jp1, ref nextCutId);
        var plan = FilletPlanBuilder.Build(layout, [jp1], [r1]);

        bool chainsThroughMid = plan.ArmChainEdges.Any(op =>
            (op.FromStableNodeId == midId)
            || (op.TerminalNodeId == midId)
            || (
                op.JunctionNodeId == j1Id
                && jp1.Arms.Any(a =>
                    a.Walk.Steps.Any(s => s.FarNode.Id == midId)
                    && plan.ArmChainEdges.Any(c => c.ArmId == a.Id && (c.FromStableNodeId == midId || c.TerminalNodeId == midId))
                )
            )
        );
        Assert.True(
            chainsThroughMid,
            $"Expected ArmChainEdge through stable node {midId}; ops: {string.Join("; ", plan.ArmChainEdges.Select(FormatOp))}"
        );

        var pre = LayoutCloner.DeepClone(layout);
        var v2 = LayoutCloner.DeepClone(layout);
        _ = new FilletArcGeneratorV2().Apply(v2);

        var reachable = FilletComparisonGates.ReachableStableIdsFromHoldShorts(pre, v2);
        Assert.Contains(midId, reachable);
        Assert.True(v2.Nodes.ContainsKey(midId), $"Node {midId} should survive V2 apply");
        Assert.True(v2.Nodes[midId].Edges.Count >= 2, $"Node {midId} should retain connectivity");
    }

    private static string FormatOp(ArmChainEdgeOp op) =>
        $"J{op.JunctionNodeId}/arm{op.ArmId} fromCut={op.FromCutId} fromStable={op.FromStableNodeId} toCut={op.ToCutId} term={op.TerminalNodeId}";

    private static AirportGroundLayout BuildSharedArmWithIntermediate()
    {
        var layout = new AirportGroundLayout { AirportId = "TEST" };
        double sharedNm = 300.0 / GeoMath.FeetPerNm;
        double midNm = 150.0 / GeoMath.FeetPerNm;
        double legNm = 400.0 / GeoMath.FeetPerNm;

        var j1 = new GroundNode
        {
            Id = 1,
            Position = LatLon.Zero,
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var mid = new GroundNode
        {
            Id = 5,
            Position = GeoMath.ProjectPoint(LatLon.Zero, new TrueHeading(0), midNm),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var j2 = new GroundNode
        {
            Id = 2,
            Position = GeoMath.ProjectPoint(LatLon.Zero, new TrueHeading(0), sharedNm),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var north = new GroundNode
        {
            Id = 11,
            Position = GeoMath.ProjectPoint(j1.Position, new TrueHeading(90), legNm),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var south = new GroundNode
        {
            Id = 12,
            Position = GeoMath.ProjectPoint(j2.Position, new TrueHeading(270), legNm),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var holdShort = new GroundNode
        {
            Id = 99,
            Position = GeoMath.ProjectPoint(south.Position, new TrueHeading(270), 80.0 / GeoMath.FeetPerNm),
            Type = GroundNodeType.RunwayHoldShort,
        };

        foreach (var n in new[] { j1, mid, j2, north, south, holdShort })
        {
            layout.Nodes[n.Id] = n;
        }

        void Add(GroundNode a, GroundNode b, string twy, string? origin = null)
        {
            layout.Edges.Add(
                new GroundEdge
                {
                    Nodes = [a, b],
                    TaxiwayName = twy,
                    DistanceNm = GeoMath.DistanceNm(a.Position, b.Position),
                    Origin = origin,
                }
            );
        }

        Add(j1, mid, "SHARED");
        Add(mid, j2, "SHARED");
        Add(j1, north, "N");
        Add(j2, south, "S");
        Add(south, holdShort, "S");

        layout.RebuildAdjacencyLists();
        return layout;
    }
}
