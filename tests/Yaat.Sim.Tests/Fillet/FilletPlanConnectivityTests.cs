using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Fillet;
using Yaat.Sim.Data.Airport.Fillet.V2;

namespace Yaat.Sim.Tests.Fillet;

/// <summary>Pass-6 plan-shape tests for connectivity ops (not executed layout parity).</summary>
public class FilletPlanConnectivityTests
{
    [Fact]
    public void Simple90Degree_PlanHasCornerArc_ArmChainEdges_NoStraightConnector()
    {
        var layout = BuildSimple90Layout();
        var plan = BuildPlanForJunction(layout, junctionNodeId: 0);

        Assert.Single(plan.CornerArcs);
        Assert.Empty(plan.StraightConnectors);
        Assert.Equal(2, plan.Cuts.Count);
        Assert.NotEmpty(plan.ArmChainEdges);
    }

    [Fact]
    public void ParkingSpur_MatchingTaxiway_ProducesReconnectEdgeOp()
    {
        var layout = BuildJunctionWithParkingSpur(taxiway: "T1");
        var junction = JunctionClassifier.Classify(layout.Nodes[0], preserveNode: false, manualArcNodes: []);
        var t1Arm = junction.Arms.Single(a => a.TaxiwayName == "T1" && a.TerminalNode.Id == 1);
        var cuts = new Dictionary<int, ResolvedArmCut> { [1] = new ResolvedArmCut(1, 0, t1Arm.Id, 50, layout.Nodes[1].Position, 180, [t1Arm.Id]) };
        var edgesToRemove = new HashSet<GroundEdge>();
        var reconnects = new List<ReconnectEdgeOp>();
        var warnings = new List<PlanWarning>();

        FilletConnectivityPlanner.AppendReconnectEdges(layout, junction, cuts, edgesToRemove, reconnects, warnings);

        var reconnect = Assert.Single(reconnects);
        Assert.Equal(99, reconnect.OtherNodeId);
        Assert.Equal(1, reconnect.TargetCutId);
        Assert.Equal("T1", reconnect.TaxiwayName);
    }

    [Fact]
    public void ParkingSpur_NoTaxiwayMatch_EmitsNoOwningCut_LeavesEdge()
    {
        var layout = BuildJunctionWithParkingSpur(taxiway: "RAMP");
        var junction = JunctionClassifier.Classify(layout.Nodes[0], preserveNode: false, manualArcNodes: []);
        var t1Arm = junction.Arms.Single(a => a.TaxiwayName == "T1" && a.TerminalNode.Id == 1);
        var cuts = new Dictionary<int, ResolvedArmCut> { [1] = new ResolvedArmCut(1, 0, t1Arm.Id, 50, layout.Nodes[1].Position, 180, [t1Arm.Id]) };
        var edgesToRemove = new HashSet<GroundEdge>();
        var reconnects = new List<ReconnectEdgeOp>();
        var warnings = new List<PlanWarning>();

        FilletConnectivityPlanner.AppendReconnectEdges(layout, junction, cuts, edgesToRemove, reconnects, warnings);

        Assert.Empty(reconnects);
        Assert.Contains(warnings, w => w.Code == PlanWarning.NoOwningCut);
        var spur = layout.Edges.Single(e => e.Nodes[0].Id == 99 || e.Nodes[1].Id == 99);
        Assert.DoesNotContain(spur, edgesToRemove);
    }

    [Fact]
    public void CutlessArmAtRemovedJunction_ProducesArmBypassOp()
    {
        var layout = BuildSimple90Layout();
        var junction = JunctionClassifier.Classify(layout.Nodes[0], preserveNode: false, manualArcNodes: []);
        var cutOnNorthOnly = new ArmCutResolver.JunctionCutResult(
            Cuts: new Dictionary<int, ResolvedArmCut> { [1] = new ResolvedArmCut(1, 0, 0, 50, layout.Nodes[1].Position, 180, [1]) },
            ArmCuts: [new ArmCutOp(1)],
            TangentMerges: [],
            CornerArcs: [new CornerArcOp(1, 1, 1)],
            StraightConnectors: [],
            Warnings: [],
            SurvivingCorners: []
        );

        var armBypasses = new List<ArmBypassOp>();
        FilletConnectivityPlanner.AppendArmBypasses(junction, cutOnNorthOnly, new HashSet<int>(), armBypasses);

        var bypass = Assert.Single(armBypasses);
        Assert.Equal(1, bypass.ArmId);
    }

    [Theory]
    [InlineData("fll")]
    [InlineData("oak")]
    public void RealLayout_PlanCutReferences_AndJunctionCoverage(string shortId)
    {
        string path = Path.Combine("TestData", $"{shortId}.geojson");
        if (!File.Exists(path))
        {
            return;
        }

        var layout = GeoJsonParser.Parse(shortId, File.ReadAllText(path), null, FilletMode.None);
        layout.RebuildAdjacencyLists();
        var manualArcNodes = ManualArcDetector.Detect(layout);
        var junctionPlans = new List<JunctionPlan>();
        var cutResults = new List<ArmCutResolver.JunctionCutResult>();
        int nextCutId = 1;

        foreach (var node in layout.Nodes.Values.OrderBy(n => n.Id))
        {
            if (manualArcNodes.Contains(node.Id))
            {
                continue;
            }

            if (!FilletEligibility.IsEligible(node, out bool preserve))
            {
                continue;
            }

            if (!layout.Nodes.TryGetValue(node.Id, out var current) || (current.Edges.Count < 2))
            {
                continue;
            }

            var junction = JunctionClassifier.Classify(current, preserve, manualArcNodes);
            if (junction.Kind == JunctionKind.Skip)
            {
                continue;
            }

            var cutResult = ArmCutResolver.Resolve(junction, ref nextCutId);
            if ((cutResult.CornerArcs.Count == 0) && (junction.CollinearPairs.Count == 0))
            {
                continue;
            }

            junctionPlans.Add(junction);
            cutResults.Add(cutResult);
        }

        var plan = FilletPlanBuilder.Build(layout, junctionPlans, cutResults);
        foreach (var cut in plan.Cuts.Values)
        {
            Assert.Contains(junctionPlans, j => j.JunctionNodeId == cut.JunctionNodeId);
        }
    }

    [Fact]
    public void CrossJunctionMerge_ArmChainEdgesReferenceSurvivorCutOnly()
    {
        var layout = BuildSharedArmLayout(sharedLengthFt: 80);
        var jp1 = PlanAt(layout, 1);
        var jp2 = PlanAt(layout, 2);
        int nextCutId = 1;
        var r1 = ArmCutResolver.Resolve(jp1, ref nextCutId);
        var r2 = ArmCutResolver.Resolve(jp2, ref nextCutId);
        var plan = FilletPlanBuilder.Build(layout, [jp1, jp2], [r1, r2]);

        Assert.NotEmpty(plan.TangentMerges);
        var merge = plan.TangentMerges[0];
        int survivor = Math.Min(merge.CutIdA, merge.CutIdB);
        int child = Math.Max(merge.CutIdA, merge.CutIdB);

        Assert.Contains(plan.Cuts.Keys, k => k == survivor);
        Assert.DoesNotContain(plan.Cuts.Keys, k => k == child);
        Assert.DoesNotContain(plan.ArmChainEdges, e => e.FromCutId == child || e.ToCutId == child);
        Assert.DoesNotContain(plan.CornerArcs, a => a.CutIdAtArmA == child || a.CutIdAtArmB == child);
        Assert.DoesNotContain(plan.ArmChainEdges, e => e.TerminalNodeId == 2);
    }

    [Fact]
    public void SharedArm_NoCrossMerge_EmitsCrossJunctionChainWhenBothEndsHaveCuts()
    {
        var layout = BuildSharedArmLayout(sharedLengthFt: 200);
        var jp1 = PlanAt(layout, 1);
        var jp2 = PlanAt(layout, 2);
        int nextCutId = 1;
        var r1 = ArmCutResolver.Resolve(jp1, ref nextCutId);
        var r2 = ArmCutResolver.Resolve(jp2, ref nextCutId);
        var plan = FilletPlanBuilder.Build(layout, [jp1, jp2], [r1, r2]);

        Assert.Empty(plan.TangentMerges);
        Assert.Contains(plan.ArmChainEdges, e => (e.JunctionNodeId == 1) && (e.FromCutId is int) && (e.ToCutId is int) && (e.TerminalNodeId is null));
    }

    private static JunctionPlan PlanAt(AirportGroundLayout layout, int junctionNodeId)
    {
        var node = layout.Nodes[junctionNodeId];
        return JunctionClassifier.Classify(node, preserveNode: false, manualArcNodes: []);
    }

    private static FilletPlan BuildPlanForJunction(AirportGroundLayout layout, int junctionNodeId)
    {
        var junctionPlans = new List<JunctionPlan>();
        var cutResults = new List<ArmCutResolver.JunctionCutResult>();
        int nextCutId = 1;

        foreach (var node in layout.Nodes.Values.Where(n => n.Id == junctionNodeId))
        {
            if (node.Edges.Count < 2)
            {
                continue;
            }

            var junction = JunctionClassifier.Classify(node, preserveNode: false, manualArcNodes: []);
            if (junction.Kind == JunctionKind.Skip)
            {
                continue;
            }

            var cutResult = ArmCutResolver.Resolve(junction, ref nextCutId);
            if ((cutResult.CornerArcs.Count == 0) && (junction.CollinearPairs.Count == 0))
            {
                continue;
            }

            junctionPlans.Add(junction);
            cutResults.Add(cutResult);
        }

        return junctionPlans.Count == 0 ? FilletPlan.Empty : FilletPlanBuilder.Build(layout, junctionPlans, cutResults);
    }

    private static AirportGroundLayout BuildSimple90Layout()
    {
        var layout = new AirportGroundLayout { AirportId = "TEST" };
        var intersection = new GroundNode
        {
            Id = 0,
            Position = LatLon.Zero,
            Type = GroundNodeType.TaxiwayIntersection,
        };
        layout.Nodes[0] = intersection;

        for (int i = 0; i < 2; i++)
        {
            int id = i + 1;
            var node = new GroundNode
            {
                Id = id,
                Position = new LatLon(i == 0 ? 0.01 : 0, i == 0 ? 0 : 0.01),
                Type = GroundNodeType.TaxiwayIntersection,
            };
            layout.Nodes[id] = node;
            layout.Edges.Add(
                new GroundEdge
                {
                    Nodes = [intersection, node],
                    TaxiwayName = $"T{id}",
                    DistanceNm = GeoMath.DistanceNm(LatLon.Zero, node.Position),
                }
            );
        }

        layout.RebuildAdjacencyLists();
        return layout;
    }

    private static AirportGroundLayout BuildJunctionWithParkingSpur(string taxiway)
    {
        var layout = BuildSimple90Layout();
        var parking = new GroundNode
        {
            Id = 99,
            Position = new LatLon(0.005, 0.005),
            Type = GroundNodeType.Parking,
            Name = "P1",
        };
        layout.Nodes[99] = parking;
        layout.Edges.Add(
            new GroundEdge
            {
                Nodes = [parking, layout.Nodes[0]],
                TaxiwayName = taxiway,
                DistanceNm = GeoMath.DistanceNm(parking.Position, layout.Nodes[0].Position),
            }
        );
        layout.RebuildAdjacencyLists();
        return layout;
    }

    private static AirportGroundLayout BuildSharedArmLayout(double sharedLengthFt)
    {
        var layout = new AirportGroundLayout { AirportId = "TEST" };
        double sharedNm = sharedLengthFt / GeoMath.FeetPerNm;
        double legNm = 400.0 / GeoMath.FeetPerNm;

        var j1 = new GroundNode
        {
            Id = 1,
            Position = LatLon.Zero,
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

        foreach (var n in new[] { j1, j2, north, south })
        {
            layout.Nodes[n.Id] = n;
        }

        void Add(GroundNode a, GroundNode b, string twy)
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

        Add(j1, j2, "SHARED");
        Add(j1, north, "N");
        Add(j2, south, "S");

        layout.RebuildAdjacencyLists();
        return layout;
    }
}
