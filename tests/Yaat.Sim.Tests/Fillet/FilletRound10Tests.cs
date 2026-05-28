using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Fillet.V2;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Fillet;

public class FilletRound10Tests
{
    private readonly ITestOutputHelper _output;

    public FilletRound10Tests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Sfo_J224_PreservedJunctionSurvivesCoincidentMerge()
    {
        const string shortId = "sfo";
        if (!File.Exists(Path.Combine("TestData", $"{shortId}.geojson")))
        {
            return;
        }

        var pre = GeoJsonParser.Parse(shortId, File.ReadAllText(Path.Combine("TestData", $"{shortId}.geojson")), null, FilletMode.None);
        var v2 = LayoutCloner.DeepClone(pre);
        _ = new FilletArcGeneratorV2().Apply(v2);

        _output.WriteLine(FilletPlanDumpDiagnostics.FormatPreservedJunctionPostExecuteCheck(shortId, junctionNodeId: 224));

        Assert.True(v2.Nodes.ContainsKey(224), "Preserved junction J224 must remain in V2 layout after coincident-merge guard");
        Assert.True(v2.Nodes[224].Edges.Count > 0, "J224 must retain at least one incident edge");
    }

    [Fact]
    public void Oak_PlanIncludesSideBranchChain58To57()
    {
        var artifacts = FilletPlanDumpDiagnostics.TryBuild("oak");
        if (artifacts is null)
        {
            return;
        }

        bool hasOp = artifacts.Plan.ArmChainEdges.Any(o => (o.FromStableNodeId == 58) && (o.TerminalNodeId == 57));
        _output.WriteLine($"plan ops 58->57: {hasOp}; J28 remove 57: {artifacts.JunctionNodesToRemove.Contains(57)}");
        Assert.True(hasOp, "J28 W6 arm should plan a stable side-branch chain 58->57");
    }

    [Fact]
    public void Oak_Node57_Reachable_AndOnlyV2RegressionGone()
    {
        const string shortId = "oak";
        string path = Path.Combine("TestData", $"{shortId}.geojson");
        if (!File.Exists(path))
        {
            return;
        }

        var pre = GeoJsonParser.Parse(shortId, File.ReadAllText(path), null, FilletMode.None);
        var legacy = LayoutCloner.DeepClone(pre);
        var v2 = LayoutCloner.DeepClone(pre);
        _ = new LegacyFilletArcGenerator().Apply(legacy);
        _ = new FilletArcGeneratorV2().Apply(v2);

        _output.WriteLine(FilletPlanDumpDiagnostics.FormatNodeEdgesAfterV2(shortId, 58, 57));

        var legacyReachable = FilletComparisonGates.ReachableStableIdsFromHoldShorts(pre, legacy);
        var v2Reachable = FilletComparisonGates.ReachableStableIdsFromHoldShorts(pre, v2);
        var onlyV2 = v2Reachable.Except(legacyReachable).ToList();

        _output.WriteLine($"only-v2 count={onlyV2.Count} ids={string.Join(",", onlyV2)}");
        _output.WriteLine($"node 57 in v2={v2.Nodes.ContainsKey(57)} reachable={v2Reachable.Contains(57)} edge 58->57={HasEdge(v2, 58, 57)}");

        Assert.True(v2.Nodes.ContainsKey(57), "Node 57 must not be coincident-merged away from 58");
        Assert.Contains(57, v2Reachable);
        Assert.True(HasEdge(v2, 58, 57), "W6 side branch 58->57 must survive V2");
        Assert.True(onlyV2.Count <= 2, $"only-v2 should not grow vs round 8 (was 2): {string.Join(",", onlyV2)}");
    }

    [Fact]
    public void RunwayCenterlineArm_DoesNotChainThroughStableWalkSteps()
    {
        var layout = BuildRunwayArmWithMidIntersection();
        var jp = JunctionClassifier.Classify(layout.Nodes[1], preserveNode: false, manualArcNodes: []);
        int nextCutId = 1;
        var cuts = ArmCutResolver.Resolve(jp, ref nextCutId);
        var plan = FilletPlanBuilder.Build(layout, [jp], [cuts]);

        var rwyArm = jp.Arms.First(a => a.IsRunwayCenterline);
        bool chainsMid = plan.ArmChainEdges.Any(op => (op.ArmId == rwyArm.Id) && ((op.FromStableNodeId == 5) || (op.TerminalNodeId == 5)));
        Assert.False(chainsMid, "Runway centerline arms must not emit chain-through-stable walk-step hops");
    }

    private static bool HasEdge(AirportGroundLayout layout, int a, int b) =>
        layout.Nodes.TryGetValue(a, out var na) && na.Edges.Any(e => e is GroundEdge ge && ge.OtherNodeId(a) == b);

    private static AirportGroundLayout BuildRunwayArmWithMidIntersection()
    {
        var layout = new AirportGroundLayout { AirportId = "TEST" };
        double legNm = 500.0 / GeoMath.FeetPerNm;

        var j1 = new GroundNode
        {
            Id = 1,
            Position = LatLon.Zero,
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var mid = new GroundNode
        {
            Id = 5,
            Position = GeoMath.ProjectPoint(LatLon.Zero, new TrueHeading(0), legNm / 2),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var end = new GroundNode
        {
            Id = 2,
            Position = GeoMath.ProjectPoint(LatLon.Zero, new TrueHeading(0), legNm),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var branch = new GroundNode
        {
            Id = 11,
            Position = GeoMath.ProjectPoint(j1.Position, new TrueHeading(90), legNm),
            Type = GroundNodeType.TaxiwayIntersection,
        };

        foreach (var n in new[] { j1, mid, end, branch })
        {
            layout.Nodes[n.Id] = n;
        }

        void Add(GroundNode a, GroundNode b, string twy) =>
            layout.Edges.Add(
                new GroundEdge
                {
                    Nodes = [a, b],
                    TaxiwayName = twy,
                    DistanceNm = GeoMath.DistanceNm(a.Position, b.Position),
                }
            );

        Add(j1, mid, "RWY:RWY10");
        Add(mid, end, "RWY:RWY10");
        Add(j1, branch, "T");

        layout.RebuildAdjacencyLists();
        return layout;
    }
}
