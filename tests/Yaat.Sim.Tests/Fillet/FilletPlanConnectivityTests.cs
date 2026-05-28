using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Fillet.V2;

namespace Yaat.Sim.Tests.Fillet;

/// <summary>Pass-6 plan-shape tests for connectivity ops (not executed layout parity).</summary>
public class FilletPlanConnectivityTests
{
    [Fact]
    public void Simple90Degree_PlanHasCornerArc_NoStraightConnector()
    {
        var layout = BuildSimple90Layout();
        var plan = BuildPlanForJunction(layout, junctionNodeId: 0);

        Assert.Single(plan.CornerArcs);
        Assert.Empty(plan.StraightConnectors);
        Assert.Equal(2, plan.Cuts.Count);
    }

    [Fact]
    public void JunctionWithParkingSpur_ProducesReconnectEdgeOp()
    {
        var layout = BuildJunctionWithParkingSpur();
        var junction = JunctionClassifier.Classify(layout.Nodes[0], preserveNode: false, manualArcNodes: []);
        var rampArm = junction.Arms.First(a => a.TaxiwayName == "T1");
        var cutResult = new ArmCutResolver.JunctionCutResult(
            Cuts: new Dictionary<int, ResolvedArmCut> { [1] = new ResolvedArmCut(1, 0, rampArm.Id, 50, layout.Nodes[1].Position, 180, [1]) },
            ArmCuts: [new ArmCutOp(1)],
            TangentMerges: [],
            CornerArcs: [new CornerArcOp(1, 1, 1)],
            StraightConnectors: [],
            Warnings: [],
            SurvivingCorners: []
        );

        var reconnects = new List<ReconnectEdgeOp>();
        var warnings = new List<PlanWarning>();
        var edgesToRemove = new HashSet<GroundEdge>(junction.Arms.Where(a => a.TaxiwayName.StartsWith('T')).Select(a => a.RootEdge));

        FilletConnectivityPlanner.AppendForJunction(layout, junction, cutResult, edgesToRemove, [], reconnects, warnings);

        var reconnect = Assert.Single(reconnects);
        Assert.Equal(99, reconnect.OtherNodeId);
        Assert.Equal(1, reconnect.TargetCutId);
        Assert.Equal("RAMP", reconnect.TaxiwayName);
        Assert.Contains(layout.Edges.Single(e => e.Nodes[0].Id == 99 || e.Nodes[1].Id == 99), edgesToRemove);
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
        var reconnects = new List<ReconnectEdgeOp>();
        var warnings = new List<PlanWarning>();
        var edgesToRemove = new HashSet<GroundEdge>(junction.Arms.Where(a => a.TaxiwayName.StartsWith('T')).Select(a => a.RootEdge));

        FilletConnectivityPlanner.AppendForJunction(layout, junction, cutOnNorthOnly, edgesToRemove, armBypasses, reconnects, warnings);

        var bypass = Assert.Single(armBypasses);
        Assert.Equal(1, bypass.ArmId);
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

    private static AirportGroundLayout BuildJunctionWithParkingSpur()
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
                TaxiwayName = "RAMP",
                DistanceNm = GeoMath.DistanceNm(parking.Position, layout.Nodes[0].Position),
            }
        );
        layout.RebuildAdjacencyLists();
        return layout;
    }

    private static AirportGroundLayout BuildYPatternLayout()
    {
        var layout = new AirportGroundLayout { AirportId = "TEST" };
        var junction = new GroundNode
        {
            Id = 0,
            Position = LatLon.Zero,
            Type = GroundNodeType.TaxiwayIntersection,
        };
        layout.Nodes[0] = junction;

        double distNm = 800.0 / GeoMath.FeetPerNm;
        void AddArm(int nodeId, double bearingDeg, string taxiway)
        {
            var pos = GeoMath.ProjectPoint(LatLon.Zero, new TrueHeading(bearingDeg), distNm);
            var node = new GroundNode
            {
                Id = nodeId,
                Position = pos,
                Type = GroundNodeType.TaxiwayIntersection,
            };
            layout.Nodes[nodeId] = node;
            layout.Edges.Add(
                new GroundEdge
                {
                    Nodes = [junction, node],
                    TaxiwayName = taxiway,
                    DistanceNm = distNm,
                }
            );
        }

        AddArm(10, 0.0, "STRAIGHT");
        AddArm(11, 89.0, "BRANCH");
        AddArm(12, 179.0, "STRAIGHT");

        layout.RebuildAdjacencyLists();
        return layout;
    }
}
